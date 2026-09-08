// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;
using LumoraRigidBody = Lumora.Core.Components.RigidBody;

namespace Lumora.Godot.Hooks;

[ImplementableHook(typeof(LumoraRigidBody))]
public class RigidBodyHook : ComponentHook<LumoraRigidBody>
{
    private RigidBody3D _rigidBody = null!;
    private bool _hasCollisionShape;
    private bool _positionInitialized;
    private int _framesSinceInit;
    private bool _pendingWorldRootReparent;
    private readonly List<MeshInstance3D> _debugEdges = new();
    private const int DebugCircleSegments = 24;
    // Speed caps so a released grab / contact spike can't fling a prop across the room. ~a hard throw.
    private const float MaxLinearSpeed = 8f;    // m/s
    private const float MaxAngularSpeed = 16f;  // rad/s
    private static bool _showDebugEdges = true;
    private static StandardMaterial3D _debugLineMaterial = null!;

    public RigidBody3D GodotRigidBody => _rigidBody;

    public override void Initialize()
    {
        base.Initialize();

        _rigidBody = new RigidBody3D();
        _rigidBody.Name = "RigidBody_" + Owner.Slot.SlotName.Value;

        // This world's collision bit (BEFORE adding to tree): rigid bodies only interact with their
        // own world's geometry. Legacy bit kept in the mask so pre-bit bodies still collide.
        uint worldBit = WorldHook.GetCollisionBitFor(Owner?.World);
        _rigidBody.CollisionLayer = worldBit;
        _rigidBody.CollisionMask = worldBit | 1u;

        if (Owner?.Slot != null)
        {
            _rigidBody.SetMeta("LumoraSlotRef", Owner.Slot.ReferenceID.ToDecimalString());
        }

        _rigidBody.ContactMonitor = true;
        _rigidBody.MaxContactsReported = 4;
        _rigidBody.ContinuousCd = true;

        // Start frozen - we'll unfreeze after a few frames to let static colliders initialize first
        // This prevents rigid bodies from falling through the ground before it exists
        _rigidBody.Freeze = true;
        _framesSinceInit = 0;

        // Add collision shapes BEFORE adding to tree
        AddCollidersFromSlot();

        // Physics bodies must be siblings of attachedNode (not children) to avoid transform feedback loops.
        // When RigidBody syncs position to Slot, SlotHook updates attachedNode. If RigidBody were a child,
        // it would move WITH the parent, causing double-movement each frame.
        // Both RigidBody and attachedNode should be under worldRoot as siblings.
        Node3D worldRoot = (Owner?.World?.GodotSceneRoot as Node3D)!;
        if (worldRoot != null)
        {
            worldRoot.AddChild(_rigidBody);
            LumoraLogger.Debug($"RigidBodyHook: Added to worldRoot as sibling of slot node");
        }
        else
        {
            // Fallback: add to attachedNode's parent (making them siblings)
            var parent = attachedNode?.GetParent() as Node3D;
            if (parent != null)
            {
                parent.AddChild(_rigidBody);
                LumoraLogger.Debug($"RigidBodyHook: Added as sibling of attachedNode under '{parent.Name}'");
            }
            else
            {
                // Last resort - will cause issues but at least the body exists
                attachedNode!.AddChild(_rigidBody);
                LumoraLogger.Warn($"RigidBodyHook: Added as child of attachedNode (will have transform issues!)");
            }
            _pendingWorldRootReparent = true;
        }

        if (_rigidBody.IsInsideTree())
        {
            SetInitialTransform();
        }
        else
        {
            // Defer the transform setup until the node is in the tree
            _rigidBody.TreeEntered += OnTreeEntered;
        }

        LumoraLogger.Debug($"RigidBodyHook: Created RigidBody3D for '{Owner!.Slot.SlotName.Value}' with {_rigidBody.GetChildCount()} collision shapes (frozen until static colliders ready)");
    }

    private void OnTreeEntered()
    {
        _rigidBody.TreeEntered -= OnTreeEntered;

        // Just set the transform - don't try to reparent during tree entry
        // The RigidBody will be under worldRoot if it was available, or under attachedNode
        SetInitialTransform();
    }

    private void SetInitialTransform()
    {
        if (_positionInitialized) return;
        _positionInitialized = true;

        var slotPos = Owner.Slot.GlobalPosition;
        var slotRot = Owner.Slot.GlobalRotation;

        _rigidBody.GlobalPosition = new Vector3(slotPos.x, slotPos.y, slotPos.z);
        _rigidBody.Quaternion = new Quaternion(slotRot.x, slotRot.y, slotRot.z, slotRot.w);

        LumoraLogger.Debug($"RigidBodyHook: Position set for '{Owner.Slot.SlotName.Value}' at ({slotPos.x:F2}, {slotPos.y:F2}, {slotPos.z:F2})");
    }

    private void AddCollidersFromSlot()
    {
        if (_rigidBody == null) return;

        int shapeCount = 0;
        foreach (var component in Owner.Slot.Components)
        {
            if (component is BoxCollider box)
            {
                AddBoxShape(box);
                shapeCount++;
            }
            else if (component is SphereCollider sphere)
            {
                AddSphereShape(sphere);
                shapeCount++;
            }
            else if (component is CapsuleCollider capsule)
            {
                AddCapsuleShape(capsule);
                shapeCount++;
            }
            else if (component is CylinderCollider cylinder)
            {
                AddCylinderShape(cylinder);
                shapeCount++;
            }
        }
        _hasCollisionShape = shapeCount > 0;
        LumoraLogger.Debug($"RigidBodyHook: Added {shapeCount} collision shapes for '{Owner.Slot.SlotName.Value}'");
    }

    private void AddBoxShape(BoxCollider box)
    {
        var shape = new CollisionShape3D();
        shape.Name = "BoxShape";
        var boxShape = new BoxShape3D();
        var size = box.Size.Value;
        var sizeVec = new Vector3(size.x, size.y, size.z);
        boxShape.Size = sizeVec;
        shape.Shape = boxShape;
        var offset = new Vector3(box.Offset.Value.x, box.Offset.Value.y, box.Offset.Value.z);
        shape.Position = offset;
        shape.Disabled = false;
        _rigidBody.AddChild(shape);
        AddDebugEdgesForBox(sizeVec, offset);
        LumoraLogger.Debug($"RigidBodyHook.AddBoxShape: size=({size.x}, {size.y}, {size.z}), disabled={shape.Disabled}, shapeValid={shape.Shape != null}");
    }

    private void AddSphereShape(SphereCollider sphere)
    {
        var shape = new CollisionShape3D();
        shape.Name = "SphereShape";
        var sphereShape = new SphereShape3D();
        sphereShape.Radius = sphere.Radius.Value;
        shape.Shape = sphereShape;
        var offset = new Vector3(sphere.Offset.Value.x, sphere.Offset.Value.y, sphere.Offset.Value.z);
        shape.Position = offset;
        _rigidBody.AddChild(shape);
        AddDebugEdgesForSphere(sphereShape.Radius, offset);
    }

    private void AddCapsuleShape(CapsuleCollider capsule)
    {
        var shape = new CollisionShape3D();
        shape.Name = "CapsuleShape";
        var capsuleShape = new CapsuleShape3D();
        capsuleShape.Radius = capsule.Radius.Value;
        capsuleShape.Height = capsule.Height.Value;
        shape.Shape = capsuleShape;
        var offset = new Vector3(capsule.Offset.Value.x, capsule.Offset.Value.y, capsule.Offset.Value.z);
        shape.Position = offset;
        _rigidBody.AddChild(shape);
        AddDebugEdgesForCapsule(capsuleShape.Radius, capsuleShape.Height, offset);
    }

    private void AddCylinderShape(CylinderCollider cylinder)
    {
        var shape = new CollisionShape3D();
        shape.Name = "CylinderShape";
        var cylinderShape = new CylinderShape3D();
        cylinderShape.Radius = cylinder.Radius.Value;
        cylinderShape.Height = cylinder.Height.Value;
        shape.Shape = cylinderShape;
        var offset = new Vector3(cylinder.Offset.Value.x, cylinder.Offset.Value.y, cylinder.Offset.Value.z);
        shape.Position = offset;
        _rigidBody.AddChild(shape);
        AddDebugEdgesForCylinder(cylinderShape.Radius, cylinderShape.Height, offset);
    }

    public override void ApplyChanges()
    {
        if (_rigidBody == null || !GodotObject.IsInstanceValid(_rigidBody))
            return;

        if (_pendingWorldRootReparent)
        {
            if (!TryReparentToWorldRoot())
            {
                return;
            }

            _pendingWorldRootReparent = false;
            _positionInitialized = false;
        }

        if (!_positionInitialized)
        {
            if (_rigidBody.IsInsideTree())
            {
                SetInitialTransform();
            }
            else
            {
                // Wait until the rigid body is in the scene tree before syncing transforms.
                return;
            }
        }

        // Wait a few frames before enabling physics to let static colliders (ground) initialize
        // This prevents rigid bodies from falling through the ground before it exists
        _framesSinceInit++;
        if (_framesSinceInit == 3)
        {
            // Reset position to slot's current position (in case it drifted during frozen frames)
            var slotPos = Owner.Slot.GlobalPosition;
            _rigidBody.GlobalPosition = new Vector3(slotPos.x, slotPos.y, slotPos.z);

            if (!Owner.IsKinematic.Value)
            {
                _rigidBody.Freeze = false;
                LumoraLogger.Debug($"RigidBodyHook: Unfroze '{Owner.Slot.SlotName.Value}' at ({slotPos.x:F1}, {slotPos.y:F1}, {slotPos.z:F1})");
            }
        }

        // Retry adding collision shapes if none were found during Initialize
        if (!_hasCollisionShape)
        {
            AddCollidersFromSlot();
        }

        // Only push a parameter the engine does not already hold. ApplyChanges runs every frame for the
        // simulation owner (RigidBody.OnUpdate queues it), and every one of these is a marshalled call
        // into the physics server that also dirties the body. A room of props was paying four of them
        // per body per frame to write the numbers back unchanged. -xlinka
        PushParameters();

        // Handle kinematic mode (only after initial delay). A HELD body counts as kinematic: the
        // grabber owns the transform - leaving physics live made it overwrite the slot every frame,
        // so grabs snapped back instantly and rigid bodies read as "not grabbable".
        if (_framesSinceInit >= 3)
        {
            if (Owner.IsKinematic.Value || IsHeld)
            {
                _rigidBody.Freeze = true;
                _rigidBody.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
            }
            else
            {
                _rigidBody.Freeze = false;
            }
        }

        if (Owner.PendingForce != float3.Zero)
        {
            _rigidBody.ApplyCentralForce(new Vector3(
                Owner.PendingForce.x,
                Owner.PendingForce.y,
                Owner.PendingForce.z));
        }

        if (Owner.PendingImpulse != float3.Zero)
        {
            _rigidBody.ApplyCentralImpulse(new Vector3(
                Owner.PendingImpulse.x,
                Owner.PendingImpulse.y,
                Owner.PendingImpulse.z));
        }

        if (Owner.PendingTorque != float3.Zero)
        {
            _rigidBody.ApplyTorque(new Vector3(
                Owner.PendingTorque.x,
                Owner.PendingTorque.y,
                Owner.PendingTorque.z));
        }

        // Throw hand-off: SET the velocity, never add. The body was kinematic a frame ago and whatever the
        // freeze/unfreeze left on it has nothing to do with the throw. A sleeping body swallows velocity
        // writes, so wake it first. Clamped below like any other live body.
        if (Owner.HasPendingVelocity)
        {
            _rigidBody.Sleeping = false;
            var throwLinear = Owner.PendingLinearVelocity;
            var throwAngular = Owner.PendingAngularVelocity;
            _rigidBody.LinearVelocity = new Vector3(throwLinear.x, throwLinear.y, throwLinear.z);
            _rigidBody.AngularVelocity = new Vector3(throwAngular.x, throwAngular.y, throwAngular.z);
        }

        Owner.ClearPendingForces();

        // SLEEPING BODIES COST ONE READ. Godot has parked this body: its transform cannot change, its
        // velocity is zero, and there is nothing to clamp. Everything below - two velocity reads, two
        // sync writes and a transform round trip through the slot - would be answering questions whose
        // answers cannot have changed. The one read that stays is Sleeping itself, because that is how
        // this peer learns the body was woken by a collision it did not initiate. -xlinka
        if (!Owner.IsKinematic.Value && !IsHeld && !_rigidBody.Freeze && _rigidBody.Sleeping)
        {
            Owner.IsSleeping = true;
            if (!_wasSleeping)
            {
                _wasSleeping = true;
                Owner.LinearVelocity.Value = float3.Zero;
                Owner.AngularVelocity.Value = float3.Zero;
                SyncTransformToSlot();
            }
            else
            {
                // A parked body still has to notice its slot being moved from outside it - a gizmo, an
                // inspector edit, a script. The check is two cached engine-side reads and a compare; only
                // a pose that actually changed costs a wake and a push. Without it the body would sit
                // where it fell and snap the slot back the moment something else woke it. -xlinka
                var slotPos = Owner.Slot.GlobalPosition;
                var slotRot = Owner.Slot.GlobalRotation;
                float dot = slotRot.x * _sleepSlotRot.x + slotRot.y * _sleepSlotRot.y
                          + slotRot.z * _sleepSlotRot.z + slotRot.w * _sleepSlotRot.w;
                if (float3.DistanceSquared(slotPos, _sleepSlotPos) > 1e-8f || System.MathF.Abs(dot) < 0.99999f)
                {
                    _rigidBody.Sleeping = false;
                    _rigidBody.GlobalPosition = new Vector3(slotPos.x, slotPos.y, slotPos.z);
                    _rigidBody.Quaternion = new Quaternion(slotRot.x, slotRot.y, slotRot.z, slotRot.w);
                }
            }
            _sleepSlotPos = Owner.Slot.GlobalPosition;
            _sleepSlotRot = Owner.Slot.GlobalRotation;
            return;
        }
        _wasSleeping = false;

        // Velocity clamp: a released grab (kinematic freeze-mode hands the body the velocity derived from
        // the last teleport-to-hand) or a spawn/contact depenetration spike can fling a prop across the
        // room. Cap the speed so nothing leaves faster than a hard throw - props settle instead of flying.
        // Only in live dynamic mode (a held/kinematic body is driven by transform, not velocity). -xlinka
        if (!Owner.IsKinematic.Value && !IsHeld && !_rigidBody.Freeze)
        {
            var lv = _rigidBody.LinearVelocity;
            if (lv.LengthSquared() > MaxLinearSpeed * MaxLinearSpeed)
                _rigidBody.LinearVelocity = lv.Normalized() * MaxLinearSpeed;
            var av = _rigidBody.AngularVelocity;
            if (av.LengthSquared() > MaxAngularSpeed * MaxAngularSpeed)
                _rigidBody.AngularVelocity = av.Normalized() * MaxAngularSpeed;
        }

        Owner.IsSleeping = _rigidBody.Sleeping;

        var linVel = _rigidBody.LinearVelocity;
        var angVel = _rigidBody.AngularVelocity;
        Owner.LinearVelocity.Value = new float3(linVel.X, linVel.Y, linVel.Z);
        Owner.AngularVelocity.Value = new float3(angVel.X, angVel.Y, angVel.Z);

        SyncTransformToSlot();
    }

    private bool TryReparentToWorldRoot()
    {
        var worldRoot = Owner?.World?.GodotSceneRoot as Node3D;
        if (worldRoot == null || _rigidBody == null || !GodotObject.IsInstanceValid(_rigidBody))
        {
            return false;
        }

        if (_rigidBody.GetParent() != worldRoot)
        {
            var slotPos = Owner!.Slot.GlobalPosition;
            var slotRot = Owner.Slot.GlobalRotation;

            if (_rigidBody.GetParent() != null)
            {
                _rigidBody.Reparent(worldRoot, true);
            }
            else
            {
                worldRoot.AddChild(_rigidBody);
            }

            _rigidBody.GlobalPosition = new Vector3(slotPos.x, slotPos.y, slotPos.z);
            _rigidBody.Quaternion = new Quaternion(slotRot.x, slotRot.y, slotRot.z, slotRot.w);
        }

        return true;
    }

    private bool _wasSleeping;
    private float3 _sleepSlotPos;
    private floatQ _sleepSlotRot = floatQ.Identity;
    private float _lastMass = float.NaN;
    private float _lastGravityScale = float.NaN;
    private float _lastLinearDamp = float.NaN;
    private float _lastAngularDamp = float.NaN;

    // Six checkboxes that did nothing.
    //
    // FreezePositionX/Y/Z and FreezeRotationX/Y/Z were declared, replicated, saved and shown in the
    // inspector, and the hook read none of them - it only ever touched the body's whole-body Freeze,
    // which is a different feature entirely. Ticking "Freeze Rotation Y" left the object spinning. The
    // renderer has had the real per-axis constraints all along. -xlinka
    private bool _lastFreezeWritten;
    private bool _lastFpx, _lastFpy, _lastFpz, _lastFrx, _lastFry, _lastFrz;

    private void PushAxisLocks()
    {
        bool fpx = Owner.FreezePositionX.Value, fpy = Owner.FreezePositionY.Value, fpz = Owner.FreezePositionZ.Value;
        bool frx = Owner.FreezeRotationX.Value, fry = Owner.FreezeRotationY.Value, frz = Owner.FreezeRotationZ.Value;

        if (_lastFreezeWritten
            && fpx == _lastFpx && fpy == _lastFpy && fpz == _lastFpz
            && frx == _lastFrx && fry == _lastFry && frz == _lastFrz)
            return;

        _rigidBody.AxisLockLinearX = fpx;
        _rigidBody.AxisLockLinearY = fpy;
        _rigidBody.AxisLockLinearZ = fpz;
        _rigidBody.AxisLockAngularX = frx;
        _rigidBody.AxisLockAngularY = fry;
        _rigidBody.AxisLockAngularZ = frz;

        _lastFpx = fpx; _lastFpy = fpy; _lastFpz = fpz;
        _lastFrx = frx; _lastFry = fry; _lastFrz = frz;
        _lastFreezeWritten = true;
    }

    private void PushParameters()
    {
        PushAxisLocks();

        float mass = Owner.Mass.Value;
        if (mass != _lastMass)
        {
            _rigidBody.Mass = mass;
            _lastMass = mass;
        }

        float gravity = Owner.UseGravity.Value ? 1f : 0f;
        if (gravity != _lastGravityScale)
        {
            _rigidBody.GravityScale = gravity;
            _lastGravityScale = gravity;
        }

        float linear = Owner.LinearDamping.Value;
        if (linear != _lastLinearDamp)
        {
            _rigidBody.LinearDamp = linear;
            _lastLinearDamp = linear;
        }

        float angular = Owner.AngularDamping.Value;
        if (angular != _lastAngularDamp)
        {
            _rigidBody.AngularDamp = angular;
            _lastAngularDamp = angular;
        }
    }

    // Cached lazily; a body without a Grabbable never allocates the lookup again.
    private Grabbable? _grabbable;
    private bool _grabbableChecked;

    private bool IsHeld
    {
        get
        {
            if (!_grabbableChecked || (_grabbable != null && _grabbable.IsDestroyed))
            {
                _grabbable = Owner?.Slot?.GetComponent<Grabbable>();
                _grabbableChecked = true;
            }
            return _grabbable != null && !_grabbable.IsDestroyed && _grabbable.IsGrabbed;
        }
    }

    private void SyncTransformToSlot()
    {
        if (_rigidBody == null || !_rigidBody.IsInsideTree())
            return;

        if (Owner.IsKinematic.Value || IsHeld)
        {
            // Kinematic or held: sync Slot -> RigidBody
            // The slot is being moved by the grabber, RigidBody needs to follow
            var slotPos = Owner.Slot.GlobalPosition;
            var slotRot = Owner.Slot.GlobalRotation;
            _rigidBody.GlobalPosition = new Vector3(slotPos.x, slotPos.y, slotPos.z);
            _rigidBody.Quaternion = new Quaternion(slotRot.x, slotRot.y, slotRot.z, slotRot.w);
        }
        else
        {
            // Dynamic mode: sync RigidBody -> Slot (physics simulation)
            var globalPos = _rigidBody.GlobalPosition;
            var globalRot = _rigidBody.GlobalBasis.GetRotationQuaternion();

            // Sync physics position to Lumora Slot (this is what gets networked)
            Owner.Slot.GlobalPosition = new float3(globalPos.X, globalPos.Y, globalPos.Z);
            Owner.Slot.GlobalRotation = new floatQ(globalRot.X, globalRot.Y, globalRot.Z, globalRot.W);
        }

        // SlotHook will sync attachedNode from Slot.GlobalPosition - don't do it here
        // to avoid transform conflicts (RigidBody is a sibling of attachedNode, not a child)
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _rigidBody != null && GodotObject.IsInstanceValid(_rigidBody))
        {
            _rigidBody.QueueFree();
        }

        ClearDebugEdges();
        _rigidBody = null!;
        base.Destroy(destroyingWorld);
    }

    private void AddDebugEdgesForBox(Vector3 size, Vector3 offset)
    {
        if (!_showDebugEdges || _rigidBody == null) return;
        var mesh = BuildBoxWireMesh(size);
        AddDebugEdgeMesh(mesh, offset, "DebugBoxEdges");
    }

    private void AddDebugEdgesForSphere(float radius, Vector3 offset)
    {
        if (!_showDebugEdges || _rigidBody == null) return;
        var mesh = BuildSphereWireMesh(radius);
        AddDebugEdgeMesh(mesh, offset, "DebugSphereEdges");
    }

    private void AddDebugEdgesForCapsule(float radius, float height, Vector3 offset)
    {
        if (!_showDebugEdges || _rigidBody == null) return;
        var mesh = BuildCapsuleWireMesh(radius, height);
        AddDebugEdgeMesh(mesh, offset, "DebugCapsuleEdges");
    }

    private void AddDebugEdgesForCylinder(float radius, float height, Vector3 offset)
    {
        if (!_showDebugEdges || _rigidBody == null) return;
        var mesh = BuildCylinderWireMesh(radius, height);
        AddDebugEdgeMesh(mesh, offset, "DebugCylinderEdges");
    }

    private void AddDebugEdgeMesh(ArrayMesh? mesh, Vector3 offset, string name)
    {
        if (mesh == null) return;
        var meshInstance = new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            MaterialOverride = GetDebugLineMaterial(),
            Position = offset,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _rigidBody.AddChild(meshInstance);
        _debugEdges.Add(meshInstance);
    }

    private void ClearDebugEdges()
    {
        for (int i = 0; i < _debugEdges.Count; i++)
        {
            var node = _debugEdges[i];
            if (node != null && GodotObject.IsInstanceValid(node))
            {
                node.QueueFree();
            }
        }
        _debugEdges.Clear();
    }

    private static StandardMaterial3D GetDebugLineMaterial()
    {
        if (_debugLineMaterial != null) return _debugLineMaterial;
        _debugLineMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(1f, 0.15f, 0.15f, 1f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        return _debugLineMaterial;
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

        var vertices = new List<Vector3>();
        var indices = new List<int>();

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

    private static ArrayMesh? BuildSphereWireMesh(float radius)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();

        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Up, radius, DebugCircleSegments, Vector3.Zero);
        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Forward, radius, DebugCircleSegments, Vector3.Zero);
        AddCircleLines(vertices, indices, Vector3.Up, Vector3.Forward, radius, DebugCircleSegments, Vector3.Zero);

        return BuildLineMesh(vertices, indices);
    }

    private static ArrayMesh? BuildCylinderWireMesh(float radius, float height)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();

        float half = height * 0.5f;
        var top = new Vector3(0f, half, 0f);
        var bottom = new Vector3(0f, -half, 0f);

        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Forward, radius, DebugCircleSegments, top);
        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Forward, radius, DebugCircleSegments, bottom);

        AddLine(vertices, indices, new Vector3(radius, -half, 0f), new Vector3(radius, half, 0f));
        AddLine(vertices, indices, new Vector3(-radius, -half, 0f), new Vector3(-radius, half, 0f));
        AddLine(vertices, indices, new Vector3(0f, -half, radius), new Vector3(0f, half, radius));
        AddLine(vertices, indices, new Vector3(0f, -half, -radius), new Vector3(0f, half, -radius));

        return BuildLineMesh(vertices, indices);
    }

    private static ArrayMesh? BuildCapsuleWireMesh(float radius, float height)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();

        float half = height * 0.5f;
        var top = new Vector3(0f, half, 0f);
        var bottom = new Vector3(0f, -half, 0f);

        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Forward, radius, DebugCircleSegments, top);
        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Forward, radius, DebugCircleSegments, bottom);

        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Up, radius, DebugCircleSegments, top);
        AddCircleLines(vertices, indices, Vector3.Right, Vector3.Up, radius, DebugCircleSegments, bottom);
        AddCircleLines(vertices, indices, Vector3.Up, Vector3.Forward, radius, DebugCircleSegments, top);
        AddCircleLines(vertices, indices, Vector3.Up, Vector3.Forward, radius, DebugCircleSegments, bottom);

        return BuildLineMesh(vertices, indices);
    }

    private static ArrayMesh? BuildLineMesh(List<Vector3> vertices, List<int> indices)
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

    private static void AddCircleLines(List<Vector3> vertices, List<int> indices, Vector3 axisA, Vector3 axisB, float radius, int segments, Vector3 center)
    {
        for (int i = 0; i < segments; i++)
        {
            float a0 = Mathf.Tau * i / segments;
            float a1 = Mathf.Tau * (i + 1) / segments;
            var p0 = center + axisA * (Mathf.Cos(a0) * radius) + axisB * (Mathf.Sin(a0) * radius);
            var p1 = center + axisA * (Mathf.Cos(a1) * radius) + axisB * (Mathf.Sin(a1) * radius);
            AddLine(vertices, indices, p0, p1);
        }
    }

    private static void AddLine(List<Vector3> vertices, List<int> indices, Vector3 from, Vector3 to)
    {
        int start = vertices.Count;
        vertices.Add(from);
        vertices.Add(to);
        indices.Add(start);
        indices.Add(start + 1);
    }
}

