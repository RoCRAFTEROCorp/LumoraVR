// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Phos;
using Lumora.Core.Logging;
using System;
using System.Runtime.CompilerServices;

namespace Lumora.Godot.Hooks;

// Godot hook for ProceduralMesh components.
// Converts PhosMesh to Godot ArrayMesh and uploads to GPU.
// Platform mesh hook for Godot.
#nullable enable
[ImplementableHook(
    typeof(ProceduralMesh),
    typeof(Lumora.Core.Components.Meshes.BoxMesh),
    typeof(Lumora.Core.Components.Meshes.QuadMesh),
    typeof(Lumora.Core.Components.Meshes.CylinderMesh),
    typeof(Lumora.Core.Components.Meshes.SphereMesh),
    typeof(Lumora.Core.Components.TextRenderer))]
public class MeshHook : ComponentHook<ProceduralMesh>
{
    private ArrayMesh? godotMesh;
    private MeshInstance3D? meshInstance;
    private SlotHook? _meshSlotHook;
    private Node3D? parentNode;
    private bool _loggedNoIndexWarning;
    private bool _loggedNoVertexWarning;
    private bool _loggedBadIndexError;
    private bool _loggedNaNVertexError;
    private bool _loggedSurfaceOverflow;

    // RenderingServer refuses surface 257 and up (MAX_MESH_SURFACES). Godot's own check is an ERR_FAIL
    // condition, so going past it prints one engine error PER SURFACE PER UPLOAD - a UI panel that rebuilds
    // on hover turns the log into a firehose and the extra surfaces are silently gone anyway. Stop at the
    // ceiling ourselves and say so once. -xlinka
    private const int MaxGodotMeshSurfaces = 256;

    // Reused surface-array scratch buffers. A deforming mesh (soft body) re-uploads every frame; allocating
    // fresh managed arrays each time churns the GC and causes frame hitches. Reuse and only grow. -xlinka
    private Vector3[]? _posBuf;
    private Vector3[]? _normBuf;
    private float[]? _tanBuf;
    private Color[]? _colBuf;
    private Vector2[]? _uvBuf;
    private Vector2[]? _uv1Buf;
    private int[]? _idxBuf;

    // The surface-array container handed to AddSurfaceFromArrays, reused across uploads. It is a native
    // container behind a finalizable wrapper, and a fresh one per submesh per upload was one more
    // finalizer-queue entry every frame for every deforming mesh. Cleared and re-sized per submesh so a
    // channel the previous upload carried cannot leak into one that does not. -xlinka
    private global::Godot.Collections.Array? _surfaceArrays;

    // Factory method for creating mesh hooks.
    public static IHook<ProceduralMesh> Constructor()
    {
        return new MeshHook();
    }

    public override void Initialize()
    {
        Lumora.Core.Logging.Logger.Debug($"MeshHook.Initialize: Starting for component on slot '{Owner?.Slot?.SlotName?.Value}'");

        godotMesh = new ArrayMesh();

        // Create Godot mesh instance (will hide if MeshRenderer is present)
        meshInstance = new MeshInstance3D();
        meshInstance.Name = "PhosMesh";
        meshInstance.Mesh = godotMesh;

        // Create default material so mesh is visible
        var material = new StandardMaterial3D();
        material.AlbedoColor = new Color(0.8f, 0.8f, 0.8f); // Light gray
        material.Roughness = 0.7f;
        material.ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel;

        var slotName = Owner?.Slot?.SlotName?.Value;
        if (!string.IsNullOrEmpty(slotName) && slotName.StartsWith("Default", System.StringComparison.Ordinal))
        {
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }
        meshInstance.MaterialOverride = material;

        // Get slot hook and request Node3D
        if (Owner!.Slot?.Hook is SlotHook hook)
        {
            _meshSlotHook = hook;
            parentNode = _meshSlotHook.RequestNode3D();
            parentNode.AddChild(meshInstance);
            Lumora.Core.Logging.Logger.Debug($"MeshHook: Successfully added mesh to slot '{Owner.Slot.SlotName.Value}'");
        }
        else
        {
            Lumora.Core.Logging.Logger.Warn($"MeshHook: Slot hook not found for '{Owner.Slot?.SlotName.Value}'");
        }

        // Initial upload if mesh data is ready
        if (Owner.PhosMesh != null)
        {
            UploadMesh(Owner.PhosMesh, Owner.UploadHint);
            NotifyRenderer();
        }
    }

    // Tell the MeshRenderer on our slot to re-apply now that our ArrayMesh actually has surfaces - the
    // surface count going 0->N is invisible to its SyncRef change path, so it needs an explicit poke. -xlinka
    private void NotifyRenderer()
    {
        Owner.Slot?.GetComponent<Lumora.Core.Components.MeshRenderer>()?.FlagSurfacesDirty();
    }

    public override void ApplyChanges()
    {
        // Upload changes if mesh is dirty
        if (Owner.IsDirty && Owner.PhosMesh != null)
        {
            UploadMesh(Owner.PhosMesh, Owner.UploadHint);
            Owner.ClearDirty();

            // Our ArrayMesh just went from empty -> populated. SurfaceCount changing 0->N is invisible to
            // the renderer's SyncRef change path, so on a joiner (where the MeshRenderer hook may have
            // drained before us) it would otherwise hold our empty mesh forever. Re-drive the co-located
            // renderer so it re-reads the now-populated ArrayMesh and re-applies materials. -xlinka
            NotifyRenderer();
        }

        // Hide our MeshInstance3D if something ELSE renders this mesh: a MeshRenderer (its own instance
        // + material), or a SquishyBody (renders the deformed mesh on a child). Both coincide with our
        // instance otherwise and z-fight. -xlinka
        if (meshInstance != null)
        {
            bool otherRenderer = Owner.Slot?.GetComponent<Lumora.Core.Components.MeshRenderer>() != null
                || Owner.Slot?.GetComponent<Lumora.Core.Components.SquishyBody>() != null
                || ChildRendersThisMesh(Owner.Slot)
                || ParticleSystemDrawsThisMesh()
                // An imported package parks every mesh it ships on one tagged library slot. Nothing there
                // is meant to be looked at, and the tests above all fail for a bare mesh component with
                // no renderer beside it, so the preview stayed up as a full-scale grey solid at the
                // import root. -xlinka
                || Owner.Slot?.Tag.Value == Lumora.Core.Assets.Interop.PackageObjectImporter.AssetLibraryTag;
            if (meshInstance.Visible != !otherRenderer)
            {
                meshInstance.Visible = !otherRenderer;
            }
        }
    }

    // A mesh drawn per-particle has no MeshRenderer anywhere, and its component does not have to live on
    // a slot with anything else on it at all.
    //
    // An imported package keeps meshes and materials in a flat top-level asset list rather than in the
    // object tree, and the importer lands that whole list on ONE holder slot. A procedural mesh in there
    // has no renderer beside it and no child below it, so every test above fails and this hook's default
    // instance stays up: a lit grey copy of the mesh sitting at the import root, at the object's full
    // scale. On a real avatar that was a two-and-a-half metre grey disc standing next to it - a circle
    // whose only real job was to be drawn a few centimetres wide, once per water particle. -xlinka
    private bool _drawnByParticles;

    private const double ParticleScanInterval = 0.5;

    private sealed class ParticleMeshScan
    {
        public double NextScan;
        public readonly System.Collections.Generic.HashSet<Lumora.Core.Component> Drawn = new();
    }

    // One scan per WORLD per window. The window used to live on each hook, which bounded each hook to one
    // walk per half second but bounded nothing about the frame: a hundred renderer-less meshes re-applying
    // together still walked the whole tree a hundred times, because none of them could see that a sibling
    // had just done it. The result is now shared through the world, so the first hook to ask pays for the
    // walk and the rest read the answer. Weak-keyed so a destroyed world takes its entry with it. -xlinka
    private static readonly ConditionalWeakTable<World, ParticleMeshScan> _particleScans = new();

    private bool ParticleSystemDrawsThisMesh()
    {
        // Once true it stays true; a system does not normally stop drawing the mesh it was built around.
        if (_drawnByParticles)
            return true;

        // This walks the WHOLE WORLD, so it has to be bounded - but bounded by TIME, not by a count of
        // calls. It was a count of 120 ApplyChanges originally, which never elapsed because ApplyChanges
        // is drained from a deduped queue rather than called per frame, so the scan ran once, missed,
        // and never ran again. Removing the bound entirely fixed that and created a worse problem: a
        // world full of renderer-less procedural meshes then walked the whole tree once per mesh per
        // re-apply, which measured 211 ms in a single hook phase.
        //
        // A short time window gets both: the scan re-runs soon enough to catch a system wired a frame
        // later, and a hundred meshes re-applying in the same breath share one walk. The importer and
        // ParticleSystem also poke the mesh directly when they point at it, so this is the fallback
        // rather than the main path. -xlinka
        var world = Owner?.World;
        if (world == null || Owner == null)
            return false;

        var scan = _particleScans.GetValue(world, static _ => new ParticleMeshScan());
        double now = world.Time?.TotalTime ?? 0.0;
        if (now >= scan.NextScan)
        {
            scan.NextScan = now + ParticleScanInterval;
            scan.Drawn.Clear();
            var root = world.RootSlot;
            if (root != null)
            {
                foreach (var system in root.GetComponentsInChildren<Lumora.Core.Components.ParticleSystem>())
                {
                    if (system.IsDestroyed)
                        continue;
                    var target = system.ParticleMesh.Target;
                    if (target != null)
                        scan.Drawn.Add(target);
                }
            }
        }

        if (!scan.Drawn.Contains(Owner))
            return false;
        _drawnByParticles = true;
        return true;
    }

    // A renderer for this mesh does not have to sit on the mesh's own slot. TextRenderer keeps its
    // MeshRenderer on a LOCAL CHILD so the label's material pool and shadow settings are its own, and
    // the slot-level check above cannot see it. Missing it left this hook's default instance up, which
    // is a LIT GREY StandardMaterial3D over the same glyph quads: back-face culled, so it is invisible
    // from the readable side and a solid box behind every glyph from the other one, tinted by whatever
    // light the world has. That is the "back of the text is boxes" bug. Direct children only, and only
    // when the cheap same-slot test has already failed. -xlinka
    private bool ChildRendersThisMesh(Lumora.Core.Slot? slot)
    {
        if (slot == null || slot.IsDestroyed)
            return false;
        foreach (var child in slot.Children)
        {
            if (RendersOwner(child))
                return true;
        }
        foreach (var child in slot.LocalChildren)
        {
            if (RendersOwner(child))
                return true;
        }
        return false;
    }

    private bool RendersOwner(Lumora.Core.Slot child)
    {
        if (child == null || child.IsDestroyed)
            return false;
        // Indexed rather than GetComponents<T>(): that is a LINQ OfType and allocates its iterator on
        // every apply of every mesh that has no renderer on its own slot.
        var components = child.Components;
        for (int i = 0; i < components.Count; i++)
        {
            if (components[i] is Lumora.Core.Components.MeshRenderer renderer
                && ReferenceEquals(renderer.Mesh.Target, Owner))
                return true;
        }
        return false;
    }

    public override void Destroy(bool destroyingWorld)
    {
        // Clean up Godot resources
        if (!destroyingWorld)
        {
            // Free the Node3D request from slot hook
            _meshSlotHook?.FreeNode3D();

            if (meshInstance != null && GodotObject.IsInstanceValid(meshInstance))
            {
                meshInstance.QueueFree();
            }

            if (godotMesh != null)
            {
                godotMesh.Dispose();
            }
        }

        _surfaceArrays?.Dispose();
        _surfaceArrays = null;
        meshInstance = null;
        godotMesh = null;
        _meshSlotHook = null;
        parentNode = null;
        _loggedNoIndexWarning = false;
        _loggedNoVertexWarning = false;
        _loggedBadIndexError = false;
        _loggedNaNVertexError = false;
        _loggedSurfaceOverflow = false;
    }

    // Godot's surface arrays must be EXACTLY the vertex/index count, so reuse only when the size matches
    // (constant for a deforming mesh) and reallocate when it changes. -xlinka
    private static void EnsureExact<T>(ref T[]? buf, int n)
    {
        if (buf == null || buf.Length != n)
            buf = new T[n];
    }

    // Upload PhosMesh to Godot ArrayMesh.
    // Only uploads channels marked dirty in the upload hint.
    private void UploadMesh(PhosMesh phosMesh, MeshUploadHint uploadHint)
    {
        if (godotMesh == null) return;

        godotMesh.ClearSurfaces();

        int dropped = 0;
        foreach (var submesh in phosMesh.Submeshes)
        {
            if (submesh is not PhosTriangleSubmesh triangleSubmesh)
            {
                continue;
            }

            if (godotMesh.GetSurfaceCount() >= MaxGodotMeshSurfaces)
            {
                dropped++;
                continue;
            }

            UploadTriangleSubmesh(phosMesh, triangleSubmesh, uploadHint);
        }

        if (dropped > 0)
        {
            if (!_loggedSurfaceOverflow)
            {
                _loggedSurfaceOverflow = true;
                Lumora.Core.Logging.Logger.Warn($"MeshHook.UploadMesh: '{Owner.Slot?.SlotName?.Value}' has {phosMesh.Submeshes.Count} submeshes but Godot caps a mesh at {MaxGodotMeshSurfaces} surfaces - {dropped} surface(s) past the cap were dropped and will not render. Split the mesh, or batch its materials.");
            }
        }
        else
        {
            // Fits again, so let the next overflow speak up.
            _loggedSurfaceOverflow = false;
        }
    }

    // Upload a triangle submesh to Godot.
    private void UploadTriangleSubmesh(PhosMesh phosMesh, PhosTriangleSubmesh submesh, MeshUploadHint uploadHint)
    {
        if (godotMesh == null) return;
        if (submesh.IndexCount <= 0) return;

        var arrays = _surfaceArrays ??= new global::Godot.Collections.Array();
        arrays.Clear();
        arrays.Resize((int)Mesh.ArrayType.Max);

        // Upload positions (ALWAYS required, regardless of hint)
        bool hasVertices = false;
        if (phosMesh.VertexCount > 0 && phosMesh.RawPositions != null)
        {
            hasVertices = true;
            EnsureExact(ref _posBuf, phosMesh.VertexCount);
            var positions = _posBuf!;
            bool hadBadPosition = false;
            for (int i = 0; i < phosMesh.VertexCount; i++)
            {
                var pos = phosMesh.RawPositions[i];
                // Non-finite positions poison the surface AABB and can fault the GPU
                // (device lost). Zero them and log instead of submitting garbage. - xlinka
                if (!float.IsFinite(pos.x) || !float.IsFinite(pos.y) || !float.IsFinite(pos.z))
                {
                    hadBadPosition = true;
                    positions[i] = Vector3.Zero;
                    continue;
                }
                positions[i] = new Vector3(pos.x, pos.y, pos.z);
            }
            if (hadBadPosition && !_loggedNaNVertexError)
            {
                _loggedNaNVertexError = true;
                Lumora.Core.Logging.Logger.Error($"MeshHook.UploadTriangleSubmesh: Non-finite vertex positions in mesh on slot '{Owner.Slot?.SlotName?.Value}' - zeroed before upload");
            }
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
        }

        // Upload normals
        if (phosMesh.HasNormals && uploadHint[MeshUploadHint.Flag.Normals])
        {
            EnsureExact(ref _normBuf, phosMesh.VertexCount);
            var normals = _normBuf!;
            for (int i = 0; i < phosMesh.VertexCount; i++)
            {
                var n = phosMesh.RawNormals[i];
                normals[i] = new Vector3(n.x, n.y, n.z);
            }
            arrays[(int)Mesh.ArrayType.Normal] = normals;
        }

        // Upload tangents
        if (phosMesh.HasTangents && uploadHint[MeshUploadHint.Flag.Tangents])
        {
            EnsureExact(ref _tanBuf, phosMesh.VertexCount * 4);
            var tangents = _tanBuf!;
            for (int i = 0; i < phosMesh.VertexCount; i++)
            {
                var t = phosMesh.RawTangents[i];
                tangents[i * 4 + 0] = t.x;
                tangents[i * 4 + 1] = t.y;
                tangents[i * 4 + 2] = t.z;
                tangents[i * 4 + 3] = t.w;
            }
            arrays[(int)Mesh.ArrayType.Tangent] = tangents;
        }

        // Upload colors
        if (phosMesh.HasColors && uploadHint[MeshUploadHint.Flag.Colors])
        {
            EnsureExact(ref _colBuf, phosMesh.VertexCount);
            var colors = _colBuf!;
            var rawColors = phosMesh.RawColors;
            for (int i = 0; i < phosMesh.VertexCount; i++)
            {
                // Safety: use white if color array is undersized
                if (i < rawColors.Length)
                {
                    var c = rawColors[i];
                    colors[i] = new Color(c.r, c.g, c.b, c.a);
                }
                else
                {
                    colors[i] = Colors.White;
                }
            }
            arrays[(int)Mesh.ArrayType.Color] = colors;
        }

        // Upload UV0
        if (phosMesh.HasUV0s && uploadHint[MeshUploadHint.Flag.UV0])
        {
            EnsureExact(ref _uvBuf, phosMesh.VertexCount);
            var uvs = _uvBuf!;
            for (int i = 0; i < phosMesh.VertexCount; i++)
            {
                var uv = phosMesh.RawUV0s[i];
                uvs[i] = new Vector2(uv.x, uv.y);
            }
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        }

        // UV1 rides to the GPU as TexUV2. Vertex-animated meshes (BubbleFieldMesh) keep their shape
        // parameters here and their instance index in UV0, so a shader can rebuild the surface from
        // two floats without a per-instance draw. Asset meshes already did this; procedural ones
        // dropped the channel on the floor. -xlinka
        if (phosMesh.HasUV1s && uploadHint[MeshUploadHint.Flag.UV1])
        {
            EnsureExact(ref _uv1Buf, phosMesh.VertexCount);
            var uvs = _uv1Buf!;
            var raw = phosMesh.RawUV1s;
            for (int i = 0; i < phosMesh.VertexCount; i++)
            {
                var uv = raw[i];
                uvs[i] = new Vector2(uv.x, uv.y);
            }
            arrays[(int)Mesh.ArrayType.TexUV2] = uvs;
        }

        // Upload indices (ALWAYS required for triangle meshes)
        bool hasIndices = submesh.IndexCount > 0 && submesh.RawIndices != null;
        if (hasIndices)
        {
            EnsureExact(ref _idxBuf, submesh.IndexCount);
            var indices = _idxBuf!;
            int maxIndex = -1;
            int minIndex = int.MaxValue;
            for (int i = 0; i < submesh.IndexCount; i++)
            {
                int index = submesh.RawIndices![i];
                if (index > maxIndex)
                {
                    maxIndex = index;
                }
                if (index < minIndex)
                {
                    minIndex = index;
                }
                indices[i] = index;
            }

            // An index past the vertex buffer is an out-of-bounds read at draw time -
            // that's a Vulkan device-lost, not a visual glitch. Negative indices wrap
            // to ~4 billion as unsigned on the GPU, same failure. Drop the surface and
            // log so the broken generator is findable. - xlinka
            if (maxIndex >= phosMesh.VertexCount || minIndex < 0)
            {
                if (!_loggedBadIndexError)
                {
                    _loggedBadIndexError = true;
                    Lumora.Core.Logging.Logger.Error($"MeshHook.UploadTriangleSubmesh: Index range [{minIndex}, {maxIndex}] invalid for vertex count {phosMesh.VertexCount} on slot '{Owner.Slot?.SlotName?.Value}' - surface skipped");
                }
                return;
            }

            arrays[(int)Mesh.ArrayType.Index] = indices;
        }

        // Only add surface if we have valid vertex data. Tracked on the managed side: reading the
        // entry back out of the container converts the whole packed vertex array into a fresh managed
        // copy just to compare it with null, which for a deforming mesh was a full vertex buffer of
        // garbage per submesh per frame. -xlinka
        if (hasVertices)
        {
            if (!hasIndices && (phosMesh.VertexCount % 3) != 0)
            {
                if (!_loggedNoIndexWarning)
                {
                    _loggedNoIndexWarning = true;
                    Lumora.Core.Logging.Logger.Warn($"MeshHook.UploadTriangleSubmesh: Skipping surface - no indices and vertex count {phosMesh.VertexCount} is not a multiple of 3");
                }
                return;
            }
            godotMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            // Lumora.Core.Logging.Logger.Debug($"MeshHook.UploadTriangleSubmesh: Uploaded {submesh.IndexCount / 3} triangles");
        }
        else
        {
            if (!_loggedNoVertexWarning)
            {
                _loggedNoVertexWarning = true;
                Lumora.Core.Logging.Logger.Warn($"MeshHook.UploadTriangleSubmesh: Skipping surface - no vertex data");
            }
        }
    }

    // Get the Godot MeshInstance3D node.
    public MeshInstance3D? GetMeshInstance() => meshInstance;
}

