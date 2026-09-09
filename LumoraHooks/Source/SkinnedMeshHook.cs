// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Phos;
using Lumora.Godot.Extensions;
using System.Collections.Generic;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

[ImplementableHook(typeof(SkinnedMeshRenderer))]
public class SkinnedMeshHook : ComponentHook<SkinnedMeshRenderer>, ILodRangeTarget
{
    // LOD plumbing. A LodGroup or LodDistanceCull decides the band; the single mesh instance this hook
    // owns is created once in Initialize and never replaced, so applying on receipt is enough. -xlinka
    private LodVisibilityRange _lodRange = LodVisibilityRange.Unbounded;

    public void SetLodVisibilityRange(in LodVisibilityRange range)
    {
        if (_lodRange.Equals(range))
            return;
        _lodRange = range;
        _lodRange.ApplyTo(_meshInstance);
    }

    private MeshInstance3D _meshInstance = null!;

    // Both the mesh instance and the skeleton are platform nodes that can be freed underneath us - by a
    // teardown, or by SkeletonHook rebuilding its skeleton - and a null check does NOT catch a freed
    // one. Every entry point revalidates instead of trusting the cached pointer. -xlinka
    private bool MeshInstanceAlive => _meshInstance != null && GodotObject.IsInstanceValid(_meshInstance);
    private bool SkeletonAlive => _skeleton != null && GodotObject.IsInstanceValid(_skeleton);
    private ArrayMesh _arrayMesh = null!;
    private SkeletonHook _skeletonHook = null!;
    private Skeleton3D _skeleton = null!;
    private bool _meshApplied;
    private bool _skeletonBound;
    private int _lastBindAttemptBoneCount = -1;
    // True once the real (component) material asset has been put on the surface. Stays false while we're showing
    // the neutral fallback, so the update loop keeps retrying until the PBS asset finishes loading. -xlinka
    private bool _realMaterialApplied;

    // Async mesh-build state: a worker-thread build is in flight (don't double-dispatch), plus the asset + vertex
    // count we last successfully applied so a lingering dirty flag can't trigger an endless rebuild loop. -xlinka
    private bool _meshBuildInFlight;
    private MeshDataAsset _appliedAsset = null!;
    private int _appliedMeshVcount = -1;

    // Maps mesh bone index -> Godot skeleton bone index
    private Dictionary<int, int> _boneIndexMap = new Dictionary<int, int>();

    public override void Initialize()
    {
        base.Initialize();

        _meshInstance = new MeshInstance3D();
        _meshInstance.Name = "SkinnedMeshInstance";

        // Added under the slot node first; TryBindToSkeleton reparents it under the Skeleton3D once that exists.
        attachedNode.AddChild(_meshInstance);
        LumoraLogger.Debug($"SkinnedMeshHook: Initialized and added mesh to '{Owner.Slot.SlotName.Value}'");

        TryBindToSkeleton();

        if (Owner.Vertices.Count > 0 || Owner.MeshAsset.Target != null)
        {
            ApplyMesh();
        }
    }

    public override void ApplyChanges()
    {
        if (!MeshInstanceAlive)
            return;

        // Zero-mapping rebind only retries when the bone list actually changed. Retrying every
        // ApplyChanges made a renderer whose bones never map (face/eye mesh with foreign bone names)
        // rebind and FULLY REBUILD its mesh on every blendshape weight change - every blink flashed
        // the whole mesh. -xlinka
        bool zeroMapRetry = _skeletonBound && _boneIndexMap.Count == 0 && Owner.BoneNames.Count > 0
            && Owner.BoneNames.Count != _lastBindAttemptBoneCount;

        // A bound skeleton can go STALE: SkeletonHook disposes and recreates its Skeleton3D whenever the
        // bone list changes, and our cached pointer then refers to a freed object. Touching it throws
        // ObjectDisposedException out of the hook update. Anything that builds a skeleton after the
        // renderers already exist - importing an avatar, for one - hits this every time. -xlinka
        bool skeletonStale = _skeletonBound && !SkeletonAlive;
        if (skeletonStale)
        {
            _skeletonBound = false;
            _skeleton = null!;
            _boneIndexMap.Clear();
            _meshApplied = false;
        }

        if (!_skeletonBound || zeroMapRetry)
        {
            TryBindToSkeleton();
        }

        // Consume the flag. Nothing cleared it before, so `shouldApplyMesh` was true on EVERY pass for
        // the rest of the session - harmless while ApplyChanges ran rarely, and a full mesh rebuild per
        // frame the moment anything started dirtying this hook regularly. -xlinka
        bool shouldApplyMesh = Owner.MeshDataChanged || !_meshApplied;
        Owner.MeshDataChanged = false;
        if (shouldApplyMesh && (Owner.Vertices.Count > 0 || Owner.MeshAsset.Target != null))
        {
            ApplyMesh();
        }

        if (Owner.SkeletonChanged)
        {
            _skeletonBound = false;
            _boneIndexMap.Clear();
            TryBindToSkeleton();
        }

        if (GodotObject.IsInstanceValid(_meshInstance))
        {
            _meshInstance.Visible = Owner.Enabled;
        }

        // Update material if changed (target reassigned), or keep retrying while the real asset is still loading
        // so the neutral fallback gets replaced the moment the PBS material is valid (MAT-3 race fix). -xlinka
        bool materialsChanged = Owner.MaterialsChanged;
        Owner.MaterialsChanged = false;
        if (materialsChanged | Owner.Material.GetWasChangedAndClear())
        {
            _realMaterialApplied = false;
            ApplyMaterial();
        }
        else if (_meshApplied && !_realMaterialApplied)
        {
            ApplyMaterial();
        }

        // Keep asking until the real material lands, WHICHEVER branch just painted the placeholder.
        //
        // ApplyChanges is QUEUE-driven, not per-frame: it runs when something dirties this hook and not
        // otherwise. A texture finishing its download dirties nothing here: the material re-binds itself
        // (its texture refs are direct members, so their arrival marks it dirty), but the renderer holds
        // that material in the Materials LIST, and a list element's change event has no listener - all
        // the arrival does on the renderer is set MaterialsChanged, which nobody reads until the hook
        // runs again. So every path that can leave a surface on the checker has to re-queue, not just the
        // retry branch above. It used to be only that branch: a surface that went on the placeholder
        // from the materials-changed branch, or from the mesh finalize, was stuck with no retry and no
        // "stuck" warning (the watch fires only on a repeat visit). On the dog import that was the
        // Fluff mane: its mesh finalized while its 4096-square albedo was still decoding, went on the
        // checker, and nothing ever came back for it. The body, same material type and same texture
        // format, escaped - most likely because its morph build (17k verts x 307 shapes against the
        // mane's 8k x 23) finalizes so much later that the texture is already there by then. It is a
        // race either way, and which surfaces lose it changes with the machine.
        //
        // Re-queueing while a surface is still on the placeholder costs one pass per hook per frame for
        // as long as the load takes, and stops the moment every surface is latched. -xlinka
        RequeueWhilePlaceholderShown();

        // Cheap path: blendshape weights changed (blink/viseme/expression) - reapply, no rebuild.
        if (Owner.BlendWeightsChanged)
        {
            ApplyBlendShapeWeights();
            Owner.BlendWeightsChanged = false;
        }
    }

    private void ApplyBlendShapeWeights()
    {
        if (_meshInstance == null || !GodotObject.IsInstanceValid(_meshInstance) || _arrayMesh == null)
            return;

        // Bound by the count the RENDERING SERVER actually allocated for this mesh (mesh_get_blend_shape_count on
        // the RID) - which is exactly what the mesh instance's blend_weights is sized to - NOT the resource-level
        // GetBlendShapeCount(). They disagree when a surface registered blend-shape NAMES but failed to attach the
        // blend-shape DATA: the resource reports 33, the instance has 0. Writing a weight past the real count
        // spams "Index out of bounds" errors, each capturing a managed backtrace - and since animated weights
        // (blink/visemes) re-apply every frame, that flood froze GLB avatar imports for ~10s. -xlinka
        int n = (int)RenderingServer.MeshGetBlendShapeCount(_arrayMesh.GetRid());
        if (n <= 0)
            return;

        int weights = Owner.BlendShapeNames.Count;
        for (int i = 0; i < n; i++)
            _meshInstance.SetBlendShapeValue(i, i < weights ? Owner.GetEffectiveBlendShapeWeight(i) : 0f);
    }

    // Every surface gets its own material now. Painting only surface 0 left a multi-surface mesh -
    // a body with a separate face or eyes - showing one material where it should show several. -xlinka
    private void ApplyMaterial()
    {
        if (!MeshInstanceAlive || _arrayMesh == null) return;

        int surfaces = _arrayMesh.GetSurfaceCount();
        if (surfaces <= 0) return;

        bool allApplied = true;
        for (int surface = 0; surface < surfaces; surface++)
            allApplied &= ApplySurfaceMaterial(surface);

        _realMaterialApplied = allApplied;
    }

    // True once this surface is wearing its final material and needs no further retry.
    private bool ApplySurfaceMaterial(int surface)
    {
        var provider = Owner.MaterialFor(surface);

        // Nothing assigned is AUTHORED, not loading: no override, no loading skin, and latched so the
        // retry loop stops asking. It used to paint a flat tan guess here, which is a claim about a
        // material that does not exist. -xlinka
        if (provider == null)
        {
            _meshInstance!.SetSurfaceOverrideMaterial(surface, null);
            return true;
        }

        var materialAsset = provider.Asset;
        bool loading = Owner.IsSurfaceLoading(surface);
        if (!loading && materialAsset != null && materialAsset.GodotMaterial is Material godotMaterial)
        {
            _meshInstance!.SetSurfaceOverrideMaterial(surface, godotMaterial);
            _loadingSkin.Clear(surface);
            return true;
        }

        _loadingSkin.Note(surface, provider, Owner.Slot?.SlotName.Value);

        // Assigned but still arriving (the material asset, or its textures). Wear the shared loading
        // skin and DON'T latch it - leave the flag false so the arrival notification's re-drive swaps
        // in the real material. An avatar used to get stuck in the tan. -xlinka
        _meshInstance!.SetSurfaceOverrideMaterial(surface, LoadingPlaceholderMaterial.Get());
        return false;
    }

    private readonly LoadingSkinWatch _loadingSkin = new();

    // One more ApplyChanges next frame while any surface wears the loading skin. Safe from the deferred
    // mesh finalize too: the datamodel write goes through the world's synchronous queue rather than
    // straight into the change buckets from a Godot callback, the same route the blendshape-name mirror
    // in that finalize already takes. A no-op once every surface holds its real material. -xlinka
    private void RequeueWhilePlaceholderShown()
    {
        if (_realMaterialApplied || !_meshApplied)
            return;

        var owner = Owner;
        if (owner == null || owner.IsDestroyed)
            return;

        var world = owner.World;
        if (world == null)
        {
            owner.MarkChangeDirty();
            return;
        }

        world.RunSynchronously(() =>
        {
            if (!owner.IsDestroyed)
                owner.MarkChangeDirty();
        });
    }

    private void TryBindToSkeleton()
    {
        // Called from the stale-skeleton path too, by which point the instance itself may be gone.
        if (!MeshInstanceAlive)
            return;

        if (Owner.Skeleton.Target != null)
        {
            _skeletonHook = (Owner.Skeleton.Target.Hook as SkeletonHook)!;
            if (_skeletonHook != null)
            {
                _skeleton = _skeletonHook.GetSkeleton();
            }
        }

        // No explicit skeleton reference: walk up from the first bone slot's node to find the Skeleton3D.
        if (_skeleton == null && Owner.Bones.Count > 0 && Owner.Bones[0] != null)
        {
            var boneSlot = Owner.Bones[0]!;
            var slotHook = boneSlot.Hook as SlotHook;
            if (slotHook != null)
            {
                var node = slotHook.GeneratedNode3D;
                if (node != null)
                {
                    var parent = node.GetParent();
                    while (parent != null)
                    {
                        if (parent is Skeleton3D skel)
                        {
                            _skeleton = skel;
                            break;
                        }
                        parent = parent.GetParent();
                    }
                }
            }
        }

        if (!SkeletonAlive)
        {
            if (!_meshInstance.IsInsideTree())
            {
                attachedNode.AddChild(_meshInstance);
                LumoraLogger.Debug("SkinnedMeshHook: No skeleton found - added mesh as static");
            }
            return;
        }

        // SkeletonAlive already proved this non-null; the analyser cannot see through the property.
        if (_skeleton!.GetBoneCount() == 0)
        {
            if (!_meshInstance.IsInsideTree())
            {
                attachedNode.AddChild(_meshInstance);
                LumoraLogger.Debug("SkinnedMeshHook: Skeleton has no bones - added mesh as static");
            }
            return;
        }

        BuildBoneIndexMap();

        if (_meshInstance.IsInsideTree())
        {
            if (_meshInstance.GetParent() != _skeleton)
            {
                _meshInstance.GetParent().RemoveChild(_meshInstance);
                _skeleton.AddChild(_meshInstance);
                LumoraLogger.Debug("SkinnedMeshHook: Reparented mesh under skeleton");
            }
        }
        else
        {
            _skeleton.AddChild(_meshInstance);
            LumoraLogger.Debug("SkinnedMeshHook: Added mesh as child of skeleton");
        }

        // IDENTITY, and not the transform this instance carried under its own slot. A skinned vertex
        // lands at skeleton * skinMatrix * vertex, and the skin's inverse binds already carry mesh
        // space -> bone space, so anything left on the instance is applied a SECOND time and slides
        // the body off its armature. This used to preserve the global transform across the reparent,
        // which is exactly that bug: any model whose mesh node carries an offset - most Blender and
        // FBX exports, where the mesh hangs under an Armature node with its own transform - rendered
        // its mesh away from its bones. A skinned mesh node's own transform is ignored by definition;
        // the armature places the mesh, not the node it was authored under. -xlinka
        _meshInstance.Transform = Transform3D.Identity;

        // ".." because the mesh was just parented under the skeleton.
        _meshInstance.Skeleton = new NodePath("..");

        _skeletonBound = true;
        _lastBindAttemptBoneCount = Owner.BoneNames.Count;
        LumoraLogger.Debug($"SkinnedMeshHook: Bound to skeleton '{_skeleton.Name}' with {_skeleton.GetBoneCount()} bones, mapped {_boneIndexMap.Count} mesh bones");

        // Re-apply mesh now that we have skeleton with proper bone mapping / Skin. Skip when nothing
        // mapped (a rigid mesh renders fine without a Skin) - rebuilding then is churn for no gain.
        if ((_boneIndexMap.Count > 0 || Owner.BoneNames.Count == 0)
            && (Owner.Vertices.Count > 0 || Owner.MeshAsset.Target != null))
        {
            _meshApplied = false;
            ApplyMesh();
        }

        // Stops the component's update-loop re-drive polling.
        if (_boneIndexMap.Count > 0)
        {
            Owner.HookBindingComplete = true;
        }
    }

    private void BuildBoneIndexMap()
    {
        _boneIndexMap.Clear();

        if (_skeleton == null)
            return;

        // Mapping priority: BoneNames, then Bones slot refs, then the SkeletonBuilder's order, else 1:1.
        if (Owner.BoneNames.Count > 0)
        {
            for (int meshBoneIdx = 0; meshBoneIdx < Owner.BoneNames.Count; meshBoneIdx++)
            {
                string boneName = Owner.BoneNames[meshBoneIdx];
                int skelBoneIdx = _skeleton.FindBone(boneName);

                if (skelBoneIdx >= 0)
                {
                    _boneIndexMap[meshBoneIdx] = skelBoneIdx;
                }
                else
                {
                    _boneIndexMap[meshBoneIdx] = 0;
                    LumoraLogger.Warn($"SkinnedMeshHook: Bone '{boneName}' not found in skeleton, mapping to bone 0");
                }
            }
            LumoraLogger.Debug($"SkinnedMeshHook: Built bone map from names - {_boneIndexMap.Count} mappings");
            return;
        }

        if (Owner.Bones.Count > 0)
        {
            for (int meshBoneIdx = 0; meshBoneIdx < Owner.Bones.Count; meshBoneIdx++)
            {
                var boneSlot = Owner.Bones[meshBoneIdx];
                if (boneSlot != null)
                {
                    string boneName = boneSlot.SlotName.Value;
                    int skelBoneIdx = _skeleton.FindBone(boneName);

                    if (skelBoneIdx >= 0)
                    {
                        _boneIndexMap[meshBoneIdx] = skelBoneIdx;
                    }
                    else
                    {
                        _boneIndexMap[meshBoneIdx] = 0;
                    }
                }
                else
                {
                    _boneIndexMap[meshBoneIdx] = 0;
                }
            }
            LumoraLogger.Debug($"SkinnedMeshHook: Built bone map from slots - {_boneIndexMap.Count} mappings");
            return;
        }

        if (Owner.Skeleton.Target != null && Owner.Skeleton.Target.BoneNames.Count > 0)
        {
            var skelBuilder = Owner.Skeleton.Target;
            for (int meshBoneIdx = 0; meshBoneIdx < skelBuilder.BoneNames.Count; meshBoneIdx++)
            {
                string boneName = skelBuilder.BoneNames[meshBoneIdx];
                int skelBoneIdx = _skeleton.FindBone(boneName);

                if (skelBoneIdx >= 0)
                {
                    _boneIndexMap[meshBoneIdx] = skelBoneIdx;
                }
            }
            LumoraLogger.Debug($"SkinnedMeshHook: Built bone map from SkeletonBuilder - {_boneIndexMap.Count} mappings");
            return;
        }

        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _boneIndexMap[i] = i;
        }
        LumoraLogger.Debug($"SkinnedMeshHook: Using direct bone index mapping (fallback)");
    }

    // returns 0 for invalid indices to prevent Godot errors
    private int RemapBoneIndex(int meshBoneIndex)
    {
        if (meshBoneIndex < 0)
            return 0;

        if (_boneIndexMap.TryGetValue(meshBoneIndex, out int skelBoneIndex))
        {
            int boneCount = _skeleton?.GetBoneCount() ?? 0;
            if (skelBoneIndex < 0 || skelBoneIndex >= boneCount)
                return 0;
            return skelBoneIndex;
        }

        int maxBone = (_skeleton?.GetBoneCount() ?? 1) - 1;
        return System.Math.Clamp(meshBoneIndex, 0, System.Math.Max(0, maxBone));
    }

    private void ApplyMesh()
    {
        if (!MeshInstanceAlive)
            return;

        // A full mesh rebuild re-uploads the ArrayMesh and re-registers the instance with lights/shadows,
        // which reads as a visible shadow/light pop on the whole avatar. Rebuilds should be RARE (import,
        // mesh data change, skeleton rebind) - if this line spams in the log while an avatar blinks or
        // idles, something upstream is tripping MeshDataChanged per frame and that's the bug. - xlinka
        LumoraLogger.Debug($"SkinnedMeshHook: full mesh rebuild on '{Owner.Slot?.SlotName.Value}'");

        // Phos asset path (the universal pipeline): geometry + bone bindings + bind poses come from a
        // content-hashed MeshDataAsset, and skinning is driven by an explicit Skin. Takes precedence over the
        // inline lists (which serve the legacy Godot-glTF import path). -xlinka
        // AssetRef.Asset returns the provider's loaded MeshDataAsset (null until the async decode lands). The
        // AssetRef is what reference-counts the provider so this load actually happens at all. -xlinka
        var phosAsset = Owner.MeshAsset.Asset;
        if (phosAsset?.MeshData is { VertexCount: > 0 } phosMesh)
        {
            ApplyMeshFromAsset(phosMesh, phosAsset);
            return;
        }

        if (Owner.Vertices.Count == 0 || Owner.Indices.Count == 0)
        {
            LumoraLogger.Debug("SkinnedMeshHook: No mesh data");
            _meshInstance.Mesh = null;
            _meshApplied = false;
            return;
        }

        _arrayMesh?.Dispose();
        _arrayMesh = new ArrayMesh();

        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);

        // Vertices
        var vertices = new Vector3[Owner.Vertices.Count];
        for (int i = 0; i < Owner.Vertices.Count; i++)
        {
            var v = Owner.Vertices[i];
            vertices[i] = new Vector3(v.x, v.y, v.z);
        }
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;

        // Normals
        Vector3[]? baseNormals = null;
        if (Owner.Normals.Count == Owner.Vertices.Count)
        {
            baseNormals = new Vector3[Owner.Normals.Count];
            for (int i = 0; i < Owner.Normals.Count; i++)
            {
                var n = Owner.Normals[i];
                baseNormals[i] = new Vector3(n.x, n.y, n.z);
            }
            arrays[(int)Mesh.ArrayType.Normal] = baseNormals;
        }

        // UVs
        Vector2[]? baseUVs = null;
        if (Owner.UVs.Count == Owner.Vertices.Count)
        {
            baseUVs = new Vector2[Owner.UVs.Count];
            for (int i = 0; i < Owner.UVs.Count; i++)
            {
                var uv = Owner.UVs[i];
                baseUVs[i] = new Vector2(uv.x, uv.y);
            }
            arrays[(int)Mesh.ArrayType.TexUV] = baseUVs;
        }

        // Indices
        var indices = new int[Owner.Indices.Count];
        int minIndex = int.MaxValue;
        int maxIndex = -1;
        for (int i = 0; i < Owner.Indices.Count; i++)
        {
            int index = Owner.Indices[i];
            if (index > maxIndex) maxIndex = index;
            if (index < minIndex) minIndex = index;
            indices[i] = index;
        }

        // An index outside the vertex buffer is an out-of-bounds read at draw time -
        // that's a Vulkan device-lost, not a visual glitch. Same guard as MeshHook. - xlinka
        if (Owner.Indices.Count > 0 && (maxIndex >= Owner.Vertices.Count || minIndex < 0))
        {
            LumoraLogger.Error($"SkinnedMeshHook: Index range [{minIndex}, {maxIndex}] invalid for vertex count {Owner.Vertices.Count} on slot '{Owner.Slot?.SlotName?.Value}' - surface skipped");
            arrays.Dispose();
            return;
        }
        arrays[(int)Mesh.ArrayType.Index] = indices;

        // Bone data only goes on the surface once the skeleton is ready with real mappings - Godot
        // errors on bone indices that don't exist yet.
        bool hasBoneData = Owner.BoneIndices.Count == Owner.Vertices.Count &&
                           Owner.BoneWeights.Count == Owner.Vertices.Count;
        bool hasValidMappings = _boneIndexMap.Count > 0 && SkeletonAlive && _skeleton.GetBoneCount() > 0;

        if (hasBoneData && hasValidMappings)
        {
            // Godot wants 4 bone indices per vertex, in SKELETON bone space (hence the remap).
            var boneIndices = new int[Owner.Vertices.Count * 4];
            for (int i = 0; i < Owner.BoneIndices.Count; i++)
            {
                var bi = Owner.BoneIndices[i];
                boneIndices[i * 4 + 0] = RemapBoneIndex(bi.x);
                boneIndices[i * 4 + 1] = RemapBoneIndex(bi.y);
                boneIndices[i * 4 + 2] = RemapBoneIndex(bi.z);
                boneIndices[i * 4 + 3] = RemapBoneIndex(bi.w);
            }
            arrays[(int)Mesh.ArrayType.Bones] = boneIndices;

            var boneWeights = new float[Owner.Vertices.Count * 4];
            for (int i = 0; i < Owner.BoneWeights.Count; i++)
            {
                var bw = Owner.BoneWeights[i];
                boneWeights[i * 4 + 0] = bw.x;
                boneWeights[i * 4 + 1] = bw.y;
                boneWeights[i * 4 + 2] = bw.z;
                boneWeights[i * 4 + 3] = bw.w;
            }
            arrays[(int)Mesh.ArrayType.Weights] = boneWeights;

            LumoraLogger.Debug($"SkinnedMeshHook: Added bone data for {Owner.Vertices.Count} vertices with {_boneIndexMap.Count} bone mappings");
        }
        else if (hasBoneData)
        {
            LumoraLogger.Debug($"SkinnedMeshHook: Skipping bone data - skeleton not ready (mappings={_boneIndexMap.Count}, skelBones={_skeleton?.GetBoneCount() ?? 0})");
        }
        else
        {
            LumoraLogger.Warn($"SkinnedMeshHook: Missing bone data - indices:{Owner.BoneIndices.Count} weights:{Owner.BoneWeights.Count} verts:{Owner.Vertices.Count}");
        }

        // Blendshapes: declare them on the mesh first, then hand the per-shape vertex arrays to the
        // surface. Stored exactly as the source reported them, with the source's BlendShapeMode, so
        // the morph round-trips correctly. (Only vertex deltas are carried; normals aren't morphed.)
        int vertexCount = Owner.Vertices.Count;
        int shapeCount = Owner.BlendShapeNames.Count;
        bool hasBlendShapes = shapeCount > 0 && Owner.BlendShapeVertices.Count == shapeCount * vertexCount;
        global::Godot.Collections.Array<global::Godot.Collections.Array> blendShapes = null!;
        var shapeArrayPool = new List<global::Godot.Collections.Array>();
        if (hasBlendShapes)
        {
            _arrayMesh.ClearBlendShapes();
            _arrayMesh.BlendShapeMode = (Mesh.BlendShapeMode)Owner.BlendShapeMode.Value;
            for (int s = 0; s < shapeCount; s++)
                _arrayMesh.AddBlendShape(Owner.BlendShapeNames[s]);

            // Godot 4 requires every blend shape to carry the SAME morphable attributes as the base surface for
            // VERTEX/NORMAL/TANGENT. The base here has NORMAL, so each shape must include a NORMAL array too or
            // the whole surface is rejected (the mesh vanishes). "No change" = the BASE normal in EVERY mode,
            // never a zero vector - zeros octahedral-encode to NaN garbage that gets blended in by weight and
            // the whole mesh's lighting pulses with every blink. See the identical fix in BuildArrayMeshCore
            // for the full autopsy; that bug ate days. base + w*base renormalizes to the same direction, so
            // the base array is safe in Relative mode too. -xlinka
            bool baseHasNormals = Owner.Normals.Count == vertexCount;
            // Use the source's real NORMAL morph deltas when the import carried them (so lighting follows the
            // expression); otherwise fall back to "no normal morph" values just to satisfy Godot's format
            // requirement. -xlinka
            bool hasMorphNormals = Owner.HasBlendShapeNormals;

            blendShapes = new global::Godot.Collections.Array<global::Godot.Collections.Array>();

            // One staging buffer per stream, reused by every shape: the assignment into shapeArrays
            // marshals a copy, and both loops overwrite every element. -xlinka
            var sv = new Vector3[vertexCount];
            var sn = baseHasNormals ? new Vector3[vertexCount] : null;

            for (int s = 0; s < shapeCount; s++)
            {
                var shapeArrays = new global::Godot.Collections.Array();
                shapeArrays.Resize((int)Mesh.ArrayType.Max);
                shapeArrayPool.Add(shapeArrays);
                int baseIdx = s * vertexCount;
                for (int i = 0; i < vertexCount; i++)
                {
                    var v = Owner.BlendShapeVertices[baseIdx + i];
                    sv[i] = new Vector3(v.x, v.y, v.z);
                }
                shapeArrays[(int)Mesh.ArrayType.Vertex] = sv;

                if (sn != null)
                {
                    for (int i = 0; i < vertexCount; i++)
                    {
                        if (hasMorphNormals)
                        {
                            var nd = Owner.BlendShapeNormals[baseIdx + i];
                            sn[i] = new Vector3(nd.x, nd.y, nd.z);
                        }
                        else
                        {
                            var nrm = Owner.Normals[i];
                            sn[i] = new Vector3(nrm.x, nrm.y, nrm.z);
                        }
                    }
                    shapeArrays[(int)Mesh.ArrayType.Normal] = sn;
                }

                blendShapes.Add(shapeArrays);
            }
        }

        // Add surface to mesh. CRITICAL: if the blend-shape surface add is rejected (Godot 4 wants each blend
        // shape's arrays to match the base surface's VERTEX/NORMAL/TANGENT set, but we only carry vertex deltas),
        // it adds NO surface at all - the whole mesh vanishes (the "main mesh missing" import bug). So detect the
        // drop via the surface count and fall back to a plain surface, so the body always renders even if the
        // morphs can't be attached. -xlinka
        if (hasBlendShapes)
        {
            int beforeSurfaces = _arrayMesh.GetSurfaceCount();
            _arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, blendShapes);
            if (_arrayMesh.GetSurfaceCount() == beforeSurfaces)
            {
                LumoraLogger.Warn($"SkinnedMeshHook: blend-shape surface rejected on slot '{Owner.Slot?.SlotName?.Value}' ({shapeCount} shapes) - rendering without morphs so the mesh still shows.");
                _arrayMesh.ClearBlendShapes();
                _arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            }
        }
        else
        {
            _arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }

        // The surface holds its own copies; these only hand back their native storage when told to.
        for (int i = 0; i < shapeArrayPool.Count; i++)
            shapeArrayPool[i].Dispose();
        // Array<T> is a typed view over an untyped Array; the untyped one is what owns the storage.
        if (blendShapes != null)
            ((global::Godot.Collections.Array)blendShapes).Dispose();
        arrays.Dispose();

        _meshInstance.Mesh = _arrayMesh;
        ApplyBlendShapeWeights();

        // Apply material (real asset if loaded, else a non-latching fallback the update loop will replace).
        ApplyMaterial();

        _meshApplied = true;
        RequeueWhilePlaceholderShown();
        LumoraLogger.Debug($"SkinnedMeshHook: Applied mesh with {Owner.Vertices.Count} vertices, {Owner.Indices.Count / 3} triangles");
    }

    // Per-vertex tangents (xyz + handedness w) via the standard accumulate-per-triangle + Gram-Schmidt method.
    // Needed because the synced skinned-mesh path doesn't carry tangents, so normal-mapped materials otherwise
    // render with a garbage tangent basis (speckled artifacts on detailed areas). -xlinka
    private static float[] ComputeTangents(Vector3[] positions, Vector3[] normals, Vector2[] uvs, int[] indices)
    {
        int n = positions.Length;
        var tan = new Vector3[n];
        var bitan = new Vector3[n];

        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
            if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= n || i1 >= n || i2 >= n)
                continue;

            Vector3 e1 = positions[i1] - positions[i0];
            Vector3 e2 = positions[i2] - positions[i0];
            Vector2 d1 = uvs[i1] - uvs[i0];
            Vector2 d2 = uvs[i2] - uvs[i0];

            float denom = d1.X * d2.Y - d2.X * d1.Y;
            float r = System.Math.Abs(denom) < 1e-12f ? 0f : 1f / denom;

            Vector3 sdir = new Vector3(
                (d2.Y * e1.X - d1.Y * e2.X) * r,
                (d2.Y * e1.Y - d1.Y * e2.Y) * r,
                (d2.Y * e1.Z - d1.Y * e2.Z) * r);
            Vector3 tdir = new Vector3(
                (d1.X * e2.X - d2.X * e1.X) * r,
                (d1.X * e2.Y - d2.X * e1.Y) * r,
                (d1.X * e2.Z - d2.X * e1.Z) * r);

            tan[i0] += sdir; tan[i1] += sdir; tan[i2] += sdir;
            bitan[i0] += tdir; bitan[i1] += tdir; bitan[i2] += tdir;
        }

        var result = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            Vector3 nv = normals[i];
            Vector3 tv = tan[i];
            // Gram-Schmidt: make the tangent orthogonal to the normal.
            Vector3 ortho = tv - nv * nv.Dot(tv);
            ortho = ortho.LengthSquared() > 1e-12f ? ortho.Normalized() : new Vector3(1f, 0f, 0f);
            float w = nv.Cross(tv).Dot(bitan[i]) < 0f ? -1f : 1f;
            result[i * 4 + 0] = ortho.X;
            result[i * 4 + 1] = ortho.Y;
            result[i * 4 + 2] = ortho.Z;
            result[i * 4 + 3] = w;
        }
        return result;
    }

    // Build the Godot skinned mesh straight from the Phos asset: positions/normals/uvs/indices + the raw
    // per-vertex bone indices (kept in MESH-bone space) + weights, then an explicit Skin that maps each mesh
    // bone to its skeleton bone with the asset's bind pose. The Skin is why the mesh holds its shape when the
    // rig poses - without it Godot skins from rest poses and the mesh collapses. Needs the skeleton built first;
    // returns quietly (leaving _meshApplied false) until then so the update loop re-enters. -xlinka
    private void ApplyMeshFromAsset(PhosMesh mesh, MeshDataAsset asset)
    {
        // IsInstanceValid before ANY call on it - a null check alone does not cover a disposed node.
        if (!MeshInstanceAlive)
        {
            _meshApplied = false;
            return;
        }

        // A skeleton is only required if the MESH IS ACTUALLY SKINNED. Content routinely puts an
        // unskinned mesh on a skinned renderer - a swappable alternate body, a toggled accessory - and
        // demanding a skeleton for those meant they rendered nothing at all, forever, with no error.
        // With no bone data there is nothing to skin: build it and let it ride the slot transform.
        if (asset.BoneCount > 0 && (!SkeletonAlive || _skeleton.GetBoneCount() == 0))
        {
            _meshApplied = false;
            return;
        }
        bool anyIndices = false;
        foreach (var sm in mesh.Submeshes)
        {
            if (sm.IndexCount > 0) { anyIndices = true; break; }
        }
        if (!anyIndices)
        {
            _meshApplied = false;
            return;
        }
        if (_meshBuildInFlight)
            return; // a worker build is already running for this hook
        if (_meshApplied && ReferenceEquals(_appliedAsset, asset) && _appliedMeshVcount == mesh.VertexCount)
            return; // already built this exact mesh - don't rebuild on a lingering dirty flag

        // Build the heavy Godot ArrayMesh (geometry + per-blendshape arrays + AddSurfaceFromArrays) on a WORKER
        // thread so a dense blendshape mesh doesn't freeze the render thread. Godot 4's RenderingServer is
        // thread-safe, so mesh-resource creation off the main thread is supported; only the scene-tree assignment
        // and the Skin (which needs the Skeleton3D node) run back on the main thread via CallDeferred. We stay a
        // single process - this is just a worker thread - so it's fine on Quest/Pico. -xlinka
        _meshBuildInFlight = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            ArrayMesh built = null!;
            int sc = 0;
            try
            {
                built = BuildArrayMeshCore(mesh, out sc);
            }
            catch (System.Exception ex)
            {
                LumoraLogger.Error($"SkinnedMeshHook: off-thread mesh build failed ({ex.Message}); falling back to main thread.");
                built = null!;
            }
            int shapes = sc;
            global::Godot.Callable.From(() => FinalizeMeshFromAsset(built, shapes, mesh, asset)).CallDeferred();
        });
    }

    // PURE mesh-resource build - no scene tree, no Owner/data-model writes - so it is safe on a worker thread.
    // Produces the Godot ArrayMesh from the Phos asset. shapeCount returns the blendshape count actually applied
    // (0 if Godot rejected the morph surface). -xlinka
    private ArrayMesh BuildArrayMeshCore(PhosMesh mesh, out int shapeCount)
    {
        int vcount = mesh.VertexCount;

        var arrayMesh = new ArrayMesh();

        // Every Godot.Collections.Array below owns NATIVE storage, and AddSurfaceFromArrays copies out of
        // it rather than taking ownership. Left undisposed the native buffers only come back at
        // finalization, so an avatar with hundreds of morph targets sits on a second full copy of its
        // geometry for as long as the GC feels like it - on top of the peak that already put the machine
        // into swap. Collected here and released at the single exit below. -xlinka
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        var shapeArrayPool = new List<global::Godot.Collections.Array>();

        var rp = mesh.RawPositions;
        var positions = new Vector3[vcount];
        for (int i = 0; i < vcount; i++)
            positions[i] = new Vector3(rp[i].x, rp[i].y, rp[i].z);
        arrays[(int)Mesh.ArrayType.Vertex] = positions;

        var rn = mesh.RawNormals;
        Vector3[]? nrmArr = null;
        if (rn != null && rn.Length >= vcount)
        {
            nrmArr = new Vector3[vcount];
            for (int i = 0; i < vcount; i++)
                nrmArr[i] = new Vector3(rn[i].x, rn[i].y, rn[i].z);
            arrays[(int)Mesh.ArrayType.Normal] = nrmArr;
        }

        var ruv = mesh.RawUV0s;
        Vector2[]? uvArr = null;
        if (ruv != null && ruv.Length >= vcount)
        {
            uvArr = new Vector2[vcount];
            for (int i = 0; i < vcount; i++)
                uvArr[i] = new Vector2(ruv[i].x, ruv[i].y);
            arrays[(int)Mesh.ArrayType.TexUV] = uvArr;
        }

        // UV1 (lightmap/detail second channel) so it survives the skinned path, not just the static one. -xlinka
        var ruv1 = mesh.RawUV1s;
        if (ruv1 != null && ruv1.Length >= vcount)
        {
            var uvs2 = new Vector2[vcount];
            for (int i = 0; i < vcount; i++)
                uvs2[i] = new Vector2(ruv1[i].x, ruv1[i].y);
            arrays[(int)Mesh.ArrayType.TexUV2] = uvs2;
        }

        // Tangents (4 floats/vertex: xyz + handedness w) for normal maps, and vertex colors. -xlinka
        bool baseHasTangents = false;
        var rtan = mesh.RawTangents;
        if (mesh.HasTangents && rtan != null && rtan.Length >= vcount)
        {
            var tangents = new float[vcount * 4];
            for (int i = 0; i < vcount; i++)
            {
                var t = rtan[i];
                tangents[i * 4 + 0] = t.x;
                tangents[i * 4 + 1] = t.y;
                tangents[i * 4 + 2] = t.z;
                tangents[i * 4 + 3] = t.w;
            }
            arrays[(int)Mesh.ArrayType.Tangent] = tangents;
            baseHasTangents = true;
        }

        var rcol = mesh.RawColors;
        if (mesh.HasColors && rcol != null && rcol.Length >= vcount)
        {
            var colors = new Color[vcount];
            for (int i = 0; i < vcount; i++)
            {
                var c = rcol[i];
                colors[i] = new Color(c.r, c.g, c.b, c.a);
            }
            arrays[(int)Mesh.ArrayType.Color] = colors;
        }

        if (mesh.HasBoneBindings)
        {
            var bind = mesh.RawBoneBindings;
            var bones = new int[vcount * 4];
            var weights = new float[vcount * 4];
            for (int i = 0; i < vcount; i++)
            {
                var b = bind[i];
                bones[i * 4 + 0] = (int)b.boneIndices.x;
                bones[i * 4 + 1] = (int)b.boneIndices.y;
                bones[i * 4 + 2] = (int)b.boneIndices.z;
                bones[i * 4 + 3] = (int)b.boneIndices.w;
                weights[i * 4 + 0] = b.boneWeights.x;
                weights[i * 4 + 1] = b.boneWeights.y;
                weights[i * 4 + 2] = b.boneWeights.z;
                weights[i * 4 + 3] = b.boneWeights.w;
            }
            arrays[(int)Mesh.ArrayType.Bones] = bones;
            arrays[(int)Mesh.ArrayType.Weights] = weights;
        }

        // Every submesh's indices together, for the tangent pass - tangents are a per-VERTEX property and
        // a vertex can be used by any surface, so deriving them from one submesh leaves the rest with a
        // garbage basis.
        int totalIndices = 0;
        foreach (var sm in mesh.Submeshes)
            totalIndices += sm.IndexCount;

        var allIndices = new int[totalIndices];
        int writeAt = 0;
        foreach (var sm in mesh.Submeshes)
        {
            if (sm.IndexCount <= 0)
                continue;
            System.Array.Copy(sm.RawIndices, 0, allIndices, writeAt, sm.IndexCount);
            writeAt += sm.IndexCount;
        }

        // No source tangents but we have normals + UVs: GENERATE them. Without tangents a normal-mapped
        // material renders with a garbage tangent basis - the speckled "white cracks" on detailed areas like a
        // muzzle, and unstable/dark lighting when a blendshape (e.g. a blink) perturbs the mesh. -xlinka
        if (!baseHasTangents && nrmArr != null && uvArr != null && allIndices.Length >= 3)
        {
            arrays[(int)Mesh.ArrayType.Tangent] = ComputeTangents(positions, nrmArr, uvArr, allIndices);
            baseHasTangents = true;
        }

        // Blendshapes (morph targets) from the asset: declare them, then hand per-shape delta arrays to the
        // surface. glTF morphs are deltas -> Relative mode; Godot rejects the surface unless each shape carries
        // the SAME morphable attrs as the base (which has NORMAL), so feed zero normal deltas when absent. -xlinka
        shapeCount = mesh.BlendShapeCount;
        global::Godot.Collections.Array<global::Godot.Collections.Array> blendShapes = null!;
        if (shapeCount > 0)
        {
            arrayMesh.ClearBlendShapes();
            arrayMesh.BlendShapeMode = Mesh.BlendShapeMode.Relative;
            for (int s = 0; s < shapeCount; s++)
                arrayMesh.AddBlendShape(mesh.BlendShapes[s].Name);

            blendShapes = new global::Godot.Collections.Array<global::Godot.Collections.Array>();

            // ONE staging buffer for every shape. Assigning it into shapeArrays marshals it into a packed
            // Godot array, which is a copy, so the managed buffer is dead the moment the assignment returns
            // and a fresh Vector3[vcount] per shape was 756 * 58,030 * 12 bytes of pure garbage on the
            // avatar that started this. Every element is overwritten below, so no clear is needed. -xlinka
            var sv = new Vector3[vcount];

            for (int s = 0; s < shapeCount; s++)
            {
                var frame = mesh.BlendShapes[s].Frames.Length > 0 ? mesh.BlendShapes[s].Frames[0] : null;
                var shapeArrays = new global::Godot.Collections.Array();
                shapeArrays.Resize((int)Mesh.ArrayType.Max);
                shapeArrayPool.Add(shapeArrays);

                var sp = frame?.positions;
                for (int i = 0; i < vcount; i++)
                    sv[i] = (sp != null && i < sp.Length) ? new Vector3(sp[i].x, sp[i].y, sp[i].z) : Vector3.Zero;
                shapeArrays[(int)Mesh.ArrayType.Vertex] = sv;

                // "No normal/tangent morph" = feed the BASE normals/tangents. NOT zeros. NEVER FUCKING ZEROS.
                // This innocent-looking array cost DAYS: Godot packs mesh normals/tangents octahedral-encoded,
                // and octahedral can only represent UNIT vectors - a zero vector encodes as 0/0 = NaN garbage,
                // and the blend path then happily mixes that garbage in PROPORTIONALLY TO THE WEIGHT. Result:
                // the whole goddamn avatar's shading went dark every single blink, no rebuild in the log,
                // nothing wrong in the data model, just Godot quietly shitting NaNs into the normal buffer
                // where no debugger looks. Third attempt at this line: Assimp's real morph normals were noise
                // (whole mesh dark always), zeros "fixed" it into a weight-proportional skew (dark only WHILE
                // blinking, the absolute worst kind of subtle), base arrays are the correct end state - safe
                // under every blend convention: as a target the base IS "unchanged", as an added delta
                // base + w*base renormalizes to the same direction in the fragment shader. If you are reading
                // this because an avatar's lighting pulses with its face: it's this. It's always this. - xlinka
                shapeArrays[(int)Mesh.ArrayType.Normal] = arrays[(int)Mesh.ArrayType.Normal];
                if (baseHasTangents)
                    shapeArrays[(int)Mesh.ArrayType.Tangent] = arrays[(int)Mesh.ArrayType.Tangent];

                blendShapes.Add(shapeArrays);
            }
        }

        // ONE SURFACE PER SUBMESH. Only the first was ever built before, which on a character that
        // splits its body across several material groups means most of it simply is not there - the test
        // avatar draws 960 of its 82,540 triangles and reads as a shapeless blob. The vertex arrays are
        // shared; only the index array changes per surface, which is also exactly what the per-surface
        // materials on the renderer are for. -xlinka
        foreach (var sm in mesh.Submeshes)
        {
            if (sm == null || sm.IndexCount <= 0)
                continue;

            var indices = new int[sm.IndexCount];
            System.Array.Copy(sm.RawIndices, indices, sm.IndexCount);
            arrays[(int)Mesh.ArrayType.Index] = indices;

            if (shapeCount > 0)
            {
                int before = arrayMesh.GetSurfaceCount();
                arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, blendShapes);
                if (arrayMesh.GetSurfaceCount() == before)
                {
                    // Godot rejected the morph surface. Drop morphs for the WHOLE mesh and restart -
                    // blend shapes are declared per mesh, so a half-morphed mesh is not a valid state.
                    LumoraLogger.Warn($"SkinnedMeshHook: Phos blend-shape surface rejected ({shapeCount} shapes) - rendering without morphs so the mesh still shows.");
                    arrayMesh.ClearSurfaces();
                    arrayMesh.ClearBlendShapes();
                    shapeCount = 0;
                    // blendShapes stays referenced, unread, so the release below still frees it.
                    foreach (var retry in mesh.Submeshes)
                    {
                        if (retry == null || retry.IndexCount <= 0)
                            continue;
                        var retryIndices = new int[retry.IndexCount];
                        System.Array.Copy(retry.RawIndices, retryIndices, retry.IndexCount);
                        arrays[(int)Mesh.ArrayType.Index] = retryIndices;
                        arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                    }
                    break;
                }
            }
            else
            {
                arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            }
        }

        // Surfaces own their copies now. This is the only exit; a throw on the way here is caught by the
        // caller, which rebuilds from scratch, so the rare leak on that path costs nothing that survives.
        for (int i = 0; i < shapeArrayPool.Count; i++)
            shapeArrayPool[i].Dispose();
        // Array<T> is a typed view over an untyped Array; the untyped one is what owns the storage.
        if (blendShapes != null)
            ((global::Godot.Collections.Array)blendShapes).Dispose();
        arrays.Dispose();

        return arrayMesh;
    }

    // Main-thread finalize: assign the built mesh to the scene node, build the Skin (needs the Skeleton3D), mirror
    // blendshape names, apply material. Falls back to a synchronous on-main build if the worker build threw. -xlinka
    private void FinalizeMeshFromAsset(ArrayMesh built, int shapeCount, PhosMesh mesh, MeshDataAsset asset)
    {
        _meshBuildInFlight = false;

        if (_meshInstance == null || !GodotObject.IsInstanceValid(_meshInstance))
            return; // hook/instance was torn down while the worker built

        // Diagnostic: did the WORKER build succeed, or are we rebuilding on the main thread (= the freeze)? -xlinka
        bool offThreadOk = built != null;
        long _finalizeStart = System.Diagnostics.Stopwatch.GetTimestamp();

        if (built == null)
        {
            try
            {
                built = BuildArrayMeshCore(mesh, out shapeCount);
            }
            catch (System.Exception ex)
            {
                LumoraLogger.Error($"SkinnedMeshHook: main-thread fallback mesh build failed: {ex.Message}");
                return;
            }
        }

        var old = _arrayMesh;
        _arrayMesh = built;
        _meshInstance.Mesh = _arrayMesh;
        old?.Dispose();

        // Skinned + blendshape meshes deform past the REST-pose AABB Godot computes for culling; a generous extra
        // cull margin keeps the instance from being frustum-culled while animated. -xlinka
        var restAabb = _arrayMesh.GetAabb();
        _meshInstance.ExtraCullMargin = System.Math.Max(restAabb.Size.Length(), 4f);

        // A skinned avatar is a DYNAMIC mesh, but Godot defaults a MeshInstance3D to GI_MODE_STATIC, which bakes the
        // rest pose into global illumination. Once the avatar is equipped and posed/moving, that static GI no longer
        // matches the geometry and the mesh lights incorrectly (typically darker). Disable GI contribution - the
        // avatar still RECEIVES light, it just stops polluting/relying on static GI. -xlinka
        _meshInstance.GIMode = GeometryInstance3D.GIModeEnum.Disabled;

        // Mirror the asset's blendshape names onto the component so expression/viseme/blink drivers can find them.
        // These are SyncFieldList (data-model) writes that MUST run under the world lock. This finalize runs via a
        // Godot CallDeferred (off-lock), so writing them HERE trips HookManager.ThreadCheck and throws - which
        // aborted the finalize before the Skin + material were applied, leaving the mesh unskinned (rest-pose, so it
        // renders huge/distorted) and untextured (grey fallback). Queue the mirror onto the world's synchronous
        // action pass (runs under the Implementer lock), with a system bypass so it still applies once the avatar is
        // equipped/host-owned. -xlinka
        if (shapeCount > 0 && Owner.BlendShapeNames.Count != shapeCount)
        {
            var names = new string[shapeCount];
            for (int s = 0; s < shapeCount; s++)
                names[s] = mesh.BlendShapes[s].Name;
            var owner = Owner;
            owner.World?.RunSynchronously(() =>
            {
                if (owner == null || owner.IsDestroyed)
                    return;
                using (owner.World?.DataModelPermissions?.EnterSystemBypass())
                {
                    owner.BlendShapeNames.Clear();
                    owner.BlendShapeWeights.Clear();
                    for (int s = 0; s < names.Length; s++)
                    {
                        owner.BlendShapeNames.Add(names[s]);
                        owner.BlendShapeWeights.Add(0f);
                    }
                }
                ApplyBlendShapeWeights();
            });
        }

        BuildAndAssignSkinFromAsset(asset);
        ApplyBlendShapeWeights();

        // Material: real asset if loaded, else a non-latching fallback the update loop will replace once the PBS
        // asset finishes loading.
        ApplyMaterial();

        _appliedAsset = asset;
        _appliedMeshVcount = mesh.VertexCount;
        _meshApplied = true;
        Owner.HookBindingComplete = true;

        // HookBindingComplete above switches off the component's per-update re-drive, so from here on
        // nothing asks this hook to run again unless something else touches the renderer. If the
        // ApplyMaterial just above left a surface on the loading skin, this is the only thing that
        // brings the real material in when its textures land. -xlinka
        RequeueWhilePlaceholderShown();
        double _finalizeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _finalizeStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // One line per skinned mesh, at Log. A mesh can build, skin and pass every check and still not
        // appear - wrong visibility, an empty surface, or bounds the renderer culls against - and none
        // of that is visible from the outside. This is the line that says which. -xlinka
        var aabb = _meshInstance.GetAabb();
        int surfaces = _arrayMesh?.GetSurfaceCount() ?? 0;
        int skeletonBones = (_skeleton != null && GodotObject.IsInstanceValid(_skeleton)) ? _skeleton.GetBoneCount() : -1;
        LumoraLogger.Log($"SkinnedMeshHook '{Owner.Slot?.SlotName.Value}': {mesh.VertexCount} verts, {surfaces} surface(s), "
                       + $"{mesh.BlendShapeCount} shapes, {asset.BoneCount} bind bones vs {skeletonBones} skeleton bones | "
                       + $"visible={_meshInstance.Visible} enabled={Owner.Enabled} materials={Owner.Materials.Count} "
                       + $"aabb=({aabb.Size.X:F2},{aabb.Size.Y:F2},{aabb.Size.Z:F2}) at ({aabb.Position.X:F2},{aabb.Position.Y:F2},{aabb.Position.Z:F2}) "
                       // WHERE the Godot node actually is, and on which layers. A mesh reporting
                       // visible=True still photographs as nothing if its instance sits somewhere else
                       // or renders on a layer the viewing camera does not include. -xlinka
                       + $"node=({_meshInstance!.GlobalPosition.X:F2},{_meshInstance.GlobalPosition.Y:F2},{_meshInstance.GlobalPosition.Z:F2}) "
                       + $"scale=({_meshInstance.Scale.X:F2},{_meshInstance.Scale.Y:F2},{_meshInstance.Scale.Z:F2}) "
                       + $"layers={_meshInstance.Layers} | "
                       + $"offThreadBuild={offThreadOk}, finalize={_finalizeMs:F0}ms");
    }

    // Map every mesh bone (in the asset's bone table) to its skeleton bone BY NAME and stamp the asset's bind
    // pose, so the surface's mesh-space bone indices resolve to the right skeleton bone on the GPU. -xlinka
    private void BuildAndAssignSkinFromAsset(MeshDataAsset asset)
    {
        // Runs deferred, after an off-thread mesh build - the skeleton can have been rebuilt (and the
        // old node freed) in the meantime, so revalidate rather than trusting the cached pointer.
        if (!SkeletonAlive || !MeshInstanceAlive || asset.BoneCount == 0)
            return;

        // Names that differ only by decoration, a duplicate suffix or a typo still mean the same joint,
        // so fall back to a fuzzy match rather than collapsing the bone to the root. A real avatar binds
        // "Left_FirstToe" against a slot called "Left_FirstToey" - one stray character, and without this
        // the toe verts get yanked to the origin. Ambiguous matches are refused, not guessed.
        var matcher = new Lumora.Core.Assets.BoneNameMatcher();
        for (int b = 0; b < _skeleton.GetBoneCount(); b++)
            matcher.Add(_skeleton.GetBoneName(b), b);

        var skin = new Skin();
        skin.SetBindCount(asset.BoneCount);
        int unresolved = 0;
        int fuzzy = 0;
        for (int i = 0; i < asset.BoneCount; i++)
        {
            string boneName = asset.GetBoneName(i) ?? string.Empty;
            if (!matcher.TryResolve(boneName, out int skelBone, out var kind))
            {
                // Nothing plausible in the skeleton -> collapsing to bone 0 yanks its verts to the root
                // (spikes). Usually an FBX pivot ("_$AssimpFbx$") or a genuinely absent bone. Log it
                // instead of failing silently. -xlinka
                unresolved++;
                if (unresolved <= 8)
                    LumoraLogger.Warn($"SkinnedMeshHook: bind bone '{boneName}' not in skeleton - collapsing to bone 0 (will deform wrong).");
                skelBone = 0;
            }
            else if (kind != Lumora.Core.Assets.BoneNameMatcher.MatchKind.Exact)
            {
                fuzzy++;
                if (fuzzy <= 8)
                    LumoraLogger.Log($"SkinnedMeshHook: bind bone '{boneName}' matched '{_skeleton.GetBoneName(skelBone)}' by {kind} - names differ, joint is the same.");
            }
            skin.SetBindBone(i, skelBone);
            skin.SetBindPose(i, asset.GetBoneBindPose(i).ToGodot());
        }
        if (fuzzy > 8)
            LumoraLogger.Log($"SkinnedMeshHook: {fuzzy} bind bones matched by name normalization.");
        if (unresolved > 0)
            LumoraLogger.Warn($"SkinnedMeshHook: {unresolved}/{asset.BoneCount} bind bones unresolved against skeleton '{_skeleton.Name}' ({_skeleton.GetBoneCount()} bones).");

        // Skinning canary (one-time per build): at rest, GlobalBoneRest * BindPose must be ~identity (origin ~0,
        // basis det ~1) or that bone's verts deform. Scan ALL bones and flag the bad ones BY NAME - this catches a
        // SUBSET deforming (e.g. ears) while the body is fine, which a first-3-bones spot-check would miss. -xlinka
        int badBones = 0;
        for (int i = 0; i < asset.BoneCount; i++)
        {
            int skelBone = _skeleton.FindBone(asset.GetBoneName(i) ?? string.Empty);
            if (skelBone < 0) continue;
            var check = _skeleton.GetBoneGlobalRest(skelBone) * asset.GetBoneBindPose(i).ToGodot();
            float originErr = check.Origin.Length();
            float detErr = System.Math.Abs(check.Basis.Determinant() - 1f);
            if (originErr > 0.02f || detErr > 0.05f)
            {
                badBones++;
                if (badBones <= 16)
                    LumoraLogger.Warn($"SkinnedMeshHook[skin-check]: BONE '{asset.GetBoneName(i)}' rest*bind NOT identity - originErr={originErr:F3} detErr={detErr:F3} (its verts deform).");
            }
        }
        if (badBones > 0)
            LumoraLogger.Warn($"SkinnedMeshHook[skin-check]: {badBones}/{asset.BoneCount} bones FAIL rest*bind==identity on mesh '{Owner.Slot?.SlotName.Value}'.");
        else
            LumoraLogger.Debug($"SkinnedMeshHook[skin-check]: all {asset.BoneCount} bones OK at rest on mesh '{Owner.Slot?.SlotName.Value}'.");

        _meshInstance.Skin = skin;
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _meshInstance != null && GodotObject.IsInstanceValid(_meshInstance))
        {
            _meshInstance.QueueFree();
        }

        _arrayMesh?.Dispose();

        _meshInstance = null!;
        _arrayMesh = null!;
        _skeletonHook = null!;
        _skeleton = null!;
        _boneIndexMap.Clear();

        base.Destroy(destroyingWorld);
    }
}
