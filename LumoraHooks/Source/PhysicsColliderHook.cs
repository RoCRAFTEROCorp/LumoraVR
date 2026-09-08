// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Core.Phos;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks
{
    // Creates a StaticBody3D or RigidBody3D with a CollisionShape3D and keeps it synced to the slot.
    // CharacterController colliders are handled separately by CharacterControllerHook.
    [ImplementableHook(typeof(BoxCollider), typeof(CapsuleCollider), typeof(SphereCollider), typeof(CylinderCollider), typeof(Lumora.Core.Components.MeshCollider), typeof(ConeCollider), typeof(TriangleCollider), typeof(ConvexHullCollider))]
    public class PhysicsColliderHook : ComponentHook<Collider>
    {
        private Node3D _bodyNode = null!;
        private CollisionShape3D _collisionShape = null!;
        private Shape3D _shape = null!;
        private bool _isDynamic;
        private PhysicsMaterial _material = null!;
        private MeshInstance3D _debugMesh = null!;
        private static bool _showDebugColliders = false;

        // Mesh-shape bake key: the slot's global scale is baked into the vertices (the body node stays
        // at scale 1 like every other collider here), so a rebuild is needed when the source geometry,
        // the scale, or the convex flag changes - and only then, cooking a trimesh isn't free. -xlinka
        // Last values actually pushed into the physics server, so a per-frame re-apply on a moving
        // collider stops writing the same numbers back.
        private bool _lastEnabled;
        private uint _lastLayer = uint.MaxValue;
        private uint _lastMask = uint.MaxValue;
        private Vector3 _lastBodyPosition = new Vector3(float.NaN, float.NaN, float.NaN);
        private Quaternion _lastBodyRotation = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);

        private float3[]? _meshBakeSource;
        private int _meshBakeIndexCount;
        private Vector3 _meshBakeScale;
        private bool _meshBakeConvex;

        // Hull-shape bake key. The hull itself is solved engine-side and cached there, so all this
        // side tracks is which version of it was uploaded and at what scale.
        private int _hullBakeVersion = -1;
        private int _hullBakePointCount = -1;
        private Vector3 _hullBakeScale;

        public override void Initialize()
        {
            base.Initialize();
            // Don't create body here - Type.Value may not be set yet
            // Body creation is deferred to ApplyChanges()
        }

        public override void ApplyChanges()
        {
            // CharacterController colliders are handled by CharacterControllerHook
            // NoCollision colliders should not create any physics body
            // If a RigidBody component exists on the same slot, RigidBodyHook handles the physics body
            bool hasRigidBody = Owner.Slot.GetComponent<Lumora.Core.Components.RigidBody>() != null;
            if (Owner.Type.Value == ColliderType.CharacterController ||
                Owner.Type.Value == ColliderType.NoCollision ||
                hasRigidBody)
            {
                // If we already created a body (Type changed after init), destroy it
                if (_bodyNode != null && GodotObject.IsInstanceValid(_bodyNode))
                {
                    LumoraLogger.Debug($"PhysicsColliderHook: Destroying body - hasRigidBody={hasRigidBody}, Type={Owner.Type.Value}");
                    DestroyBody(true);
                }
                return;
            }

            // Create body if needed (deferred from Initialize)
            if (_bodyNode == null || !GodotObject.IsInstanceValid(_bodyNode))
            {
                LumoraLogger.Debug($"PhysicsColliderHook: Creating body for {Owner.GetType().Name} on '{Owner.Slot.SlotName.Value}'");
                CreateBody();
                // BuildShape and UpdateTransform run below on every path; no need to do them twice.
            }

            bool shouldBeDynamic = Owner.Mass.Value > 0.0001f && Owner.Type.Value != ColliderType.Static;
            if (shouldBeDynamic != _isDynamic)
            {
                DestroyBody(true);
                CreateBody();
                BuildShape();
            }

            BuildShape();

            UpdateTransform();

            // Enable/disable collision, scoped to this world's collision bit so bodies in different
            // worlds can never touch or answer each other's queries.
            // A moving collider runs this every frame (its world transform event queues the apply), and
            // every write below is a marshalled call that dirties the body in the physics server. Push
            // only what actually changed. -xlinka
            uint worldBit = WorldHook.GetCollisionBitFor(Owner?.World);

            // A disabled slot turns off everything under it, and this body cannot rely on that the way a
            // renderer can: it is parented under the WORLD ROOT, not under the slot, so Godot's visibility
            // never reaches it. Asking only the component's own Enabled leaves a collider on a switched-off
            // subtree fully solid - an imported avatar's swap meshes ship exactly like that, disabled at
            // their root, and you walk into geometry that is not on screen. -xlinka
            bool enabled = (Owner?.Enabled ?? false) && (Owner?.Slot?.IsActive ?? false);
            uint layer = enabled ? worldBit : 0u;
            // sensor only (Area3D). on physics layer for engine-side interaction, mask=0 so nothing
            // pushes against it - xlinka
            uint mask = _bodyNode is Area3D ? 0u : (enabled ? (worldBit | 1u) : 0u);

            if (enabled != _lastEnabled)
            {
                _bodyNode!.Visible = enabled;
                _lastEnabled = enabled;
            }
            if (_bodyNode is CollisionObject3D collisionObject)
            {
                if (layer != _lastLayer)
                {
                    collisionObject.CollisionLayer = layer;
                    _lastLayer = layer;
                }
                if (mask != _lastMask)
                {
                    collisionObject.CollisionMask = mask;
                    _lastMask = mask;
                }
            }
        }

        private void CreateBody()
        {
            _isDynamic = Owner.Mass.Value > 0.0001f && Owner.Type.Value != ColliderType.Static;

            if (ShouldBeSensor())
            {
                // images and grabbable objects shouldn't act like walls. Area3D = sensor that raycasts still hit but physics never contacts - xlinka
                _isDynamic = false;
                _bodyNode = new Area3D
                {
                    Name = "GrabbableSensor",
                    Monitoring = false,
                    Monitorable = true,
                };
            }
            else if (_isDynamic)
            {
                _material = new PhysicsMaterial { Friction = 1f, Bounce = 0f };
                var rigid = new RigidBody3D
                {
                    Name = "RigidCollider",
                    PhysicsMaterialOverride = _material,
                    Mass = Owner.Mass.Value
                };
                _bodyNode = rigid;
            }
            else
            {
                _material = new PhysicsMaterial { Friction = 1f, Bounce = 0f };
                var staticBody = new StaticBody3D
                {
                    Name = "StaticCollider",
                    PhysicsMaterialOverride = _material
                };
                _bodyNode = staticBody;
            }

            _collisionShape = new CollisionShape3D { Name = "Shape" };
            _bodyNode.AddChild(_collisionShape);

            if (Owner?.Slot != null)
            {
                _bodyNode.SetMeta("LumoraSlotRef", Owner.Slot.ReferenceID.ToDecimalString());
            }

            // Parent under world root to avoid double transforms
            Node3D worldRoot = (Owner?.World?.GodotSceneRoot as Node3D)!;
            Node parentNode = (Node)worldRoot ?? attachedNode!;
            parentNode.AddChild(_bodyNode);
        }

        private void BuildShape()
        {
            if (_collisionShape == null || !GodotObject.IsInstanceValid(_collisionShape))
                return;

            // Update existing shape in-place if possible, otherwise create new
            switch (Owner)
            {
                case BoxCollider box:
                    float3 size = box.Size.Value;
                    if (_shape is BoxShape3D existingBox)
                    {
                        // Re-writing the same extents re-cooks the shape and wakes everything resting on
                        // it, and a moving collider lands here every frame. Only push a real change.
                        var boxSize = new Vector3(size.x, size.y, size.z);
                        if (existingBox.Size != boxSize)
                            existingBox.Size = boxSize;
                    }
                    else
                    {
                        _shape = new BoxShape3D { Size = new Vector3(size.x, size.y, size.z) };
                        _collisionShape.Shape = _shape;
                    }
                    break;
                case CapsuleCollider capsule:
                    if (_shape is CapsuleShape3D existingCap)
                    {
                        if (existingCap.Radius != capsule.Radius.Value)
                            existingCap.Radius = capsule.Radius.Value;
                        if (existingCap.Height != capsule.Height.Value)
                            existingCap.Height = capsule.Height.Value;
                    }
                    else
                    {
                        _shape = new CapsuleShape3D { Radius = capsule.Radius.Value, Height = capsule.Height.Value };
                        _collisionShape.Shape = _shape;
                    }
                    break;
                case SphereCollider sphere:
                    if (_shape is SphereShape3D existingSph)
                    {
                        if (existingSph.Radius != sphere.Radius.Value)
                            existingSph.Radius = sphere.Radius.Value;
                    }
                    else
                    {
                        _shape = new SphereShape3D { Radius = sphere.Radius.Value };
                        _collisionShape.Shape = _shape;
                    }
                    break;
                case CylinderCollider cylinder:
                    if (_shape is CylinderShape3D existingCyl)
                    {
                        if (existingCyl.Radius != cylinder.Radius.Value)
                            existingCyl.Radius = cylinder.Radius.Value;
                        if (existingCyl.Height != cylinder.Height.Value)
                            existingCyl.Height = cylinder.Height.Value;
                    }
                    else
                    {
                        _shape = new CylinderShape3D { Radius = cylinder.Radius.Value, Height = cylinder.Height.Value };
                        _collisionShape.Shape = _shape;
                    }
                    break;
                case Lumora.Core.Components.MeshCollider meshCollider:
                    BuildMeshShape(meshCollider);
                    break;
                case ConvexHullCollider hullCollider:
                    BuildConvexHullShape(hullCollider);
                    break;
                case ConeCollider cone:
                    // Godot has no cone primitive: convex hull of the base ring + apex.
                    _shape = BuildConeShape(cone.Radius.Value, cone.Height.Value);
                    _collisionShape.Shape = _shape;
                    break;
                case TriangleCollider triangle:
                    _shape = BuildTriangleShape(triangle.A.Value, triangle.B.Value, triangle.C.Value);
                    _collisionShape.Shape = _shape;
                    break;
                default:
                    LumoraLogger.Warn($"PhysicsColliderHook: Unknown collider type {Owner.GetType().Name}");
                    return;
            }

            var offset = Owner.Offset.Value;
            var offsetVector = new Vector3(offset.x, offset.y, offset.z);
            if (_collisionShape.Position != offsetVector)
                _collisionShape.Position = offsetVector;

            if (ShouldShowDebugForCollider())
            {
                UpdateDebugVisualization();
            }
            else
            {
                ClearDebugVisualization();
            }
        }

        private static ConvexPolygonShape3D BuildConeShape(float radius, float height)
        {
            const int segments = 16;
            var points = new Vector3[segments + 1];
            float halfH = height * 0.5f;
            for (int i = 0; i < segments; i++)
            {
                float a = System.MathF.Tau * i / segments;
                points[i] = new Vector3(System.MathF.Cos(a) * radius, -halfH, System.MathF.Sin(a) * radius);
            }
            points[segments] = new Vector3(0f, halfH, 0f);
            return new ConvexPolygonShape3D { Points = points };
        }

        private static ConcavePolygonShape3D BuildTriangleShape(float3 a, float3 b, float3 c)
        {
            var faces = new[]
            {
                new Vector3(a.x, a.y, a.z),
                new Vector3(b.x, b.y, b.z),
                new Vector3(c.x, c.y, c.z),
            };
            return new ConcavePolygonShape3D { Data = faces, BackfaceCollision = true };
        }

        // Trimesh (or convex hull) from the referenced mesh's geometry. The slot's global scale is
        // baked into the vertices because the physics body deliberately never inherits scale; the
        // Collider base re-applies on WorldTransformChanged, so a rescale lands here and the bake key
        // triggers the rebuild. Missing data is NOT an error: the provider decodes async and the
        // component polls until it lands.
        private void BuildMeshShape(Lumora.Core.Components.MeshCollider meshCollider)
        {
            PhosMesh? mesh = meshCollider.Mesh.Target switch
            {
                Lumora.Core.Components.Meshes.ProceduralMesh procedural => procedural.PhosMesh,
                MeshProvider provider => provider.Asset?.MeshData,
                _ => null
            };
            int vertexCount = mesh?.VertexCount ?? 0;
            if (mesh == null || vertexCount == 0)
                return;

            var gs = Owner.Slot.GlobalScale;
            var scale = new Vector3(gs.x, gs.y, gs.z);
            bool convex = meshCollider.Convex.Value;

            int totalIndexCount = 0;
            foreach (var submesh in mesh.Submeshes)
            {
                if (submesh.Topology == PhosTopology.Triangles)
                    totalIndexCount += submesh.IndexCount;
            }

            if (_shape != null && ReferenceEquals(_meshBakeSource, mesh.RawPositions)
                && _meshBakeIndexCount == totalIndexCount && _meshBakeScale == scale && _meshBakeConvex == convex)
                return;

            var positions = mesh.RawPositions;

            if (convex)
            {
                var points = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    var p = positions[i];
                    points[i] = new Vector3(p.x * scale.X, p.y * scale.Y, p.z * scale.Z);
                }
                _shape = new ConvexPolygonShape3D { Points = points };
            }
            else
            {
                if (totalIndexCount == 0)
                    return;

                var faces = new Vector3[totalIndexCount];
                int write = 0;
                foreach (var submesh in mesh.Submeshes)
                {
                    if (submesh.Topology != PhosTopology.Triangles)
                        continue;
                    var indices = submesh.RawIndices;
                    int count = submesh.IndexCount;
                    for (int i = 0; i + 2 < count; i += 3)
                    {
                        int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                        // An index past the vertex buffer would corrupt the cook; drop the triangle.
                        if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                            continue;
                        faces[write++] = new Vector3(positions[a].x * scale.X, positions[a].y * scale.Y, positions[a].z * scale.Z);
                        faces[write++] = new Vector3(positions[b].x * scale.X, positions[b].y * scale.Y, positions[b].z * scale.Z);
                        faces[write++] = new Vector3(positions[c].x * scale.X, positions[c].y * scale.Y, positions[c].z * scale.Z);
                    }
                }
                if (write == 0)
                    return;
                if (write != totalIndexCount)
                    System.Array.Resize(ref faces, write);

                // BackfaceCollision: imported world geometry has no winding guarantee; single-sided
                // trimeshes let users fall through inverted faces.
                _shape = new ConcavePolygonShape3D { Data = faces, BackfaceCollision = true };
            }

            _collisionShape.Shape = _shape;
            _meshBakeSource = positions;
            _meshBakeIndexCount = totalIndexCount;
            _meshBakeScale = scale;
            _meshBakeConvex = convex;
            LumoraLogger.Debug($"PhysicsColliderHook: Built {(convex ? "convex" : "trimesh")} collision for '{Owner.Slot.SlotName.Value}' ({vertexCount} verts)");
        }

        // Points only. The hull is solved in the component so every peer collides against the same
        // shape; this side just scales the result into the body's space and uploads it. The slot's
        // global scale is baked into the points because the physics body deliberately never inherits
        // scale, and the Collider base re-applies on WorldTransformChanged, so a rescale lands here and
        // the bake key catches it. Missing points are NOT an error: an asset-backed source decodes
        // async and the component polls until it lands.
        private void BuildConvexHullShape(ConvexHullCollider hullCollider)
        {
            var hull = hullCollider.GetHullPoints();
            if (hull.Count < 4)
            {
                // Under four points there is no volume to collide with. Leaving the previous shape in
                // place would be worse than having none, so drop it.
                if (_shape != null)
                {
                    _shape = null!;
                    _collisionShape.Shape = null;
                    _hullBakeVersion = -1;
                    _hullBakePointCount = -1;
                }
                return;
            }

            var gs = Owner.Slot.GlobalScale;
            var scale = new Vector3(gs.x, gs.y, gs.z);

            if (_shape != null
                && _hullBakeVersion == hullCollider.HullVersion
                && _hullBakePointCount == hull.Count
                && _hullBakeScale == scale)
                return;

            var points = new Vector3[hull.Count];
            for (int i = 0; i < hull.Count; i++)
            {
                var p = hull[i];
                points[i] = new Vector3(p.x * scale.X, p.y * scale.Y, p.z * scale.Z);
            }

            _shape = new ConvexPolygonShape3D { Points = points };
            _collisionShape.Shape = _shape;
            _hullBakeVersion = hullCollider.HullVersion;
            _hullBakePointCount = hull.Count;
            _hullBakeScale = scale;
            LumoraLogger.Debug($"PhysicsColliderHook: Built convex hull collision for '{Owner.Slot.SlotName.Value}' ({hull.Count} hull points, {hullCollider.LastResult})");
        }

        private void UpdateDebugVisualization()
        {
            if (_debugMesh != null && GodotObject.IsInstanceValid(_debugMesh))
            {
                _debugMesh.QueueFree();
                _debugMesh = null!;
            }

            if (_bodyNode == null || !GodotObject.IsInstanceValid(_bodyNode))
                return;

            bool isImageCollider = IsImageCollider();

            _debugMesh = new MeshInstance3D();
            _debugMesh.Name = "DebugCollider";

            var material = new StandardMaterial3D();
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            material.AlbedoColor = new Color(0.2f, 0.5f, 1.0f, 0.8f); // Blue
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;

            switch (Owner)
            {
                case BoxCollider box:
                    var boxSize = box.Size.Value;
                    if (isImageCollider)
                    {
                        _debugMesh.Mesh = BuildBoxWireMesh(new Vector3(boxSize.x, boxSize.y, boxSize.z));
                        material.AlbedoColor = new Color(0.75f, 0.2f, 1.0f, 1.0f);
                    }
                    else
                    {
                        var boxMesh = new BoxMesh();
                        boxMesh.Size = new Vector3(boxSize.x, boxSize.y, boxSize.z);
                        _debugMesh.Mesh = boxMesh;
                        material.AlbedoColor = new Color(0.2f, 0.5f, 1.0f, 0.3f);
                    }
                    break;

                case CapsuleCollider capsule:
                    var capsuleMesh = new CapsuleMesh();
                    capsuleMesh.Radius = capsule.Radius.Value;
                    capsuleMesh.Height = capsule.Height.Value;
                    _debugMesh.Mesh = capsuleMesh;
                    material.AlbedoColor = new Color(0.2f, 1.0f, 0.5f, 0.3f); // Green for capsule
                    break;

                case SphereCollider sphere:
                    var sphereMesh = new SphereMesh();
                    sphereMesh.Radius = sphere.Radius.Value;
                    _debugMesh.Mesh = sphereMesh;
                    material.AlbedoColor = new Color(1.0f, 0.5f, 0.2f, 0.3f); // Orange for sphere
                    break;

                case CylinderCollider cylinder:
                    var cylinderMesh = new CylinderMesh();
                    cylinderMesh.TopRadius = cylinder.Radius.Value;
                    cylinderMesh.BottomRadius = cylinder.Radius.Value;
                    cylinderMesh.Height = cylinder.Height.Value;
                    _debugMesh.Mesh = cylinderMesh;
                    material.AlbedoColor = new Color(0.8f, 0.2f, 0.8f, 0.3f); // Purple for cylinder
                    break;
            }

            _debugMesh.MaterialOverride = material;

            var offset = Owner.Offset.Value;
            _debugMesh.Position = new Vector3(offset.x, offset.y, offset.z);

            _bodyNode.AddChild(_debugMesh);
        }

        private void ClearDebugVisualization()
        {
            if (_debugMesh != null && GodotObject.IsInstanceValid(_debugMesh))
            {
                _debugMesh.QueueFree();
            }
            _debugMesh = null!;
        }

        private bool ShouldShowDebugForCollider()
        {
            return _showDebugColliders;
        }

        private bool IsImageCollider()
        {
            if (Owner?.Slot == null)
                return false;

            return Owner.Slot.GetComponent<ImageProvider>() != null;
        }

        // any grabbable slot without its own RigidBody should also act as a sensor so the player
        // can walk through it / grab it without getting bounced. covers shader orbs and similar - xlinka
        private bool ShouldBeSensor()
        {
            // Explicit query-only collider (e.g. a worn avatar's bone capsules): raycast-hittable for grab/laser but
            // never a solid body, so it can't push the wearer's own character controller around. -xlinka
            if (Owner != null && Owner.Type.Value == ColliderType.Trigger)
                return true;

            if (IsImageCollider())
                return true;

            var slot = Owner?.Slot;
            if (slot == null)
                return false;

            if (slot.GetComponent<Grabbable>() == null)
                return false;

            return slot.GetComponent<Lumora.Core.Components.RigidBody>() == null;
        }

        private static ArrayMesh? BuildBoxWireMesh(Vector3 size)
        {
            float hx = size.X * 0.5f;
            float hy = size.Y * 0.5f;
            float hz = size.Z * 0.5f;

            var corners = new[]
            {
                new Vector3(-hx, -hy, -hz),
                new Vector3(hx, -hy, -hz),
                new Vector3(hx, hy, -hz),
                new Vector3(-hx, hy, -hz),
                new Vector3(-hx, -hy, hz),
                new Vector3(hx, -hy, hz),
                new Vector3(hx, hy, hz),
                new Vector3(-hx, hy, hz)
            };

            var vertices = new System.Collections.Generic.List<Vector3>();
            var indices = new System.Collections.Generic.List<int>();

            AddLine(vertices, indices, corners[0], corners[1]);
            AddLine(vertices, indices, corners[1], corners[2]);
            AddLine(vertices, indices, corners[2], corners[3]);
            AddLine(vertices, indices, corners[3], corners[0]);

            AddLine(vertices, indices, corners[4], corners[5]);
            AddLine(vertices, indices, corners[5], corners[6]);
            AddLine(vertices, indices, corners[6], corners[7]);
            AddLine(vertices, indices, corners[7], corners[4]);

            AddLine(vertices, indices, corners[0], corners[4]);
            AddLine(vertices, indices, corners[1], corners[5]);
            AddLine(vertices, indices, corners[2], corners[6]);
            AddLine(vertices, indices, corners[3], corners[7]);

            return BuildLineMesh(vertices, indices);
        }

        private static ArrayMesh? BuildLineMesh(System.Collections.Generic.List<Vector3> vertices, System.Collections.Generic.List<int> indices)
        {
            if (vertices.Count == 0) return null;
            var mesh = new ArrayMesh();
            var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
            return mesh;
        }

        private static void AddLine(System.Collections.Generic.List<Vector3> vertices, System.Collections.Generic.List<int> indices, Vector3 from, Vector3 to)
        {
            int start = vertices.Count;
            vertices.Add(from);
            vertices.Add(to);
            indices.Add(start);
            indices.Add(start + 1);
        }

        private void UpdateTransform()
        {
            if (_bodyNode == null || !GodotObject.IsInstanceValid(_bodyNode))
                return;

            var slotNode = slotHook?.GeneratedNode3D;
            if (slotNode == null)
                return;

            // Only copy position and rotation - NOT scale. The shape already carries the real
            // dimensions, so scaling here would double-apply; scale stays at (1,1,1).
            //
            // Guarded on the value. A collider is re-applied for plenty of reasons other than moving (a
            // sync field, an enable, a shape edit), and a transform write on a static body re-inserts it
            // into the broadphase and wakes whatever was resting on it. Same pose in, nothing out.
            // -xlinka
            bool inTree = slotNode.IsInsideTree();
            Vector3 position;
            Quaternion rotation;
            if (inTree)
            {
                position = slotNode.GlobalPosition;
                rotation = slotNode.GlobalBasis.GetRotationQuaternion();
            }
            else
            {
                var globalPos = Owner.Slot.GlobalPosition;
                var globalRot = Owner.Slot.GlobalRotation;
                position = new Vector3(globalPos.x, globalPos.y, globalPos.z);
                rotation = new Quaternion(globalRot.x, globalRot.y, globalRot.z, globalRot.w);
            }

            if (position == _lastBodyPosition && rotation == _lastBodyRotation)
                return;
            _lastBodyPosition = position;
            _lastBodyRotation = rotation;

            if (inTree)
            {
                _bodyNode.GlobalPosition = position;
                _bodyNode.GlobalBasis = new Basis(rotation);
            }
            else
            {
                _bodyNode.Position = position;
                _bodyNode.Quaternion = rotation;
            }
        }

        public override void Destroy(bool destroyingWorld)
        {
            DestroyBody(!destroyingWorld);
            base.Destroy(destroyingWorld);
        }

        private void DestroyBody(bool queueFree)
        {
            if (_debugMesh != null && GodotObject.IsInstanceValid(_debugMesh) && queueFree)
            {
                _debugMesh.QueueFree();
            }

            if (_collisionShape != null && GodotObject.IsInstanceValid(_collisionShape) && queueFree)
            {
                _collisionShape.QueueFree();
            }

            if (_bodyNode != null && GodotObject.IsInstanceValid(_bodyNode) && queueFree)
            {
                _bodyNode.QueueFree();
            }

            _debugMesh = null!;
            _collisionShape = null!;
            _bodyNode = null!;
            _shape = null!;
        }
    }
}

