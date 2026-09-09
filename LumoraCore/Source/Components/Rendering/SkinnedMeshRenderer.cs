// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

[ComponentCategory("Rendering")]
public class SkinnedMeshRenderer : ImplementableComponent
{
    // BONE REFERENCES

    // bone index 0 in mesh data corresponds to Bones[0], etc.
    // Readonly fields, never properties: member discovery only sees init-only ISyncMember fields, and a
    // list it cannot see is never initialized before OnAwake, replicated, or saved. -xlinka
    public readonly SyncRefList<Slot> Bones = new();

    public readonly SyncFieldList<string> BoneNames = new();

    public readonly SyncRef<SkeletonBuilder> Skeleton = null!;

    // Phos mesh asset source (the universal pipeline). When set, the hook sources geometry, bone bindings AND
    // bind poses from this content-hashed asset and drives skinning through an explicit Skin - instead of the
    // inline Vertices/BoneIndices/BoneWeights lists below (which exist for the legacy Godot-glTF import path).
    // So a skinned avatar travels as one asset instead of thousands of synced elements.
    // MUST be an AssetRef, not a plain SyncRef: an AssetRef reference-counts the MeshProvider (so its underlying
    // MeshDataAsset is actually requested + decoded - a StaticAssetProvider only loads while AssetReferenceCount
    // > 0) AND re-fires ApplyChanges when the async decode finishes (AssetRef.AssetUpdated). A bare SyncRef did
    // NEITHER, so the provider never loaded and a Phos-imported avatar rendered as nothing. -xlinka
    public readonly AssetRef<MeshDataAsset> MeshAsset = null!;

    // MESH DATA

    public readonly SyncFieldList<float3> Vertices = new();

    public readonly SyncFieldList<float3> Normals = new();

    public readonly SyncFieldList<float2> UVs = new();

    public readonly SyncFieldList<int> Indices = new();

    // up to 4 bones per vertex; indices reference the Bones list
    public readonly SyncFieldList<int4> BoneIndices = new();

    public readonly SyncFieldList<float4> BoneWeights = new();

    // BLENDSHAPES (morph targets, e.g. facial expressions / visemes / blink)

    // mesh-level, shared across surfaces of a mesh
    public readonly SyncFieldList<string> BlendShapeNames = new();

    // flattened: shape k occupies [k*VertexCount, (k+1)*VertexCount); stored exactly as the source mesh
    // reported them so the round-trip honors BlendShapeMode
    public readonly SyncFieldList<float3> BlendShapeVertices = new();

    // Per-shape vertex NORMAL deltas, flattened like BlendShapeVertices. Optional - empty when the
    // source carried no normal morph data; when present the hook morphs normals too, so lighting follows the
    // expression (matches how the morph was authored, with per-frame normal deltas). -xlinka
    public readonly SyncFieldList<float3> BlendShapeNormals = new();

    // true when normal morph deltas are present for every shape vertex, so the hook morphs normals too
    public bool HasBlendShapeNormals => BlendShapeVertices.Count > 0 && BlendShapeNormals.Count == BlendShapeVertices.Count;

    // 0..1; driven by expression/viseme/blink drivers
    public readonly SyncFieldList<float> BlendShapeWeights = new();

    // 0 = Normalized, 1 = Relative (Godot Mesh.BlendShapeMode)
    public readonly Sync<int> BlendShapeMode = new();

    // set when only weights changed, so the hook reapplies weights without a full mesh rebuild
    public bool BlendWeightsChanged { get; set; }

    public int BlendShapeCount => BlendShapeNames.Count;

    public string BlendShapeName(int index)
        => (index >= 0 && index < BlendShapeNames.Count) ? BlendShapeNames[index] : null!;

    public int GetBlendShapeIndex(string name)
    {
        for (int i = 0; i < BlendShapeNames.Count; i++)
        {
            if (string.Equals(BlendShapeNames[i], name, System.StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    public float GetBlendShapeWeight(int index)
        => (index >= 0 && index < BlendShapeWeights.Count) ? BlendShapeWeights[index] : 0f;

    public void SetBlendShapeWeight(int index, float weight)
    {
        if (index < 0 || index >= BlendShapeWeights.Count)
            return;
        if (BlendShapeWeights[index] == weight)
            return;
        BlendShapeWeights[index] = weight;
        BlendWeightsChanged = true;
        RunApplyChanges();
    }

    public void SetBlendShapeWeight(string name, float weight)
        => SetBlendShapeWeight(GetBlendShapeIndex(name), weight);

    // Local (non-replicated) per-frame override buffer, written by blink/viseme/expression drivers.
    // Every peer runs those drivers from replicated inputs (voice, timers), so broadcasting per-frame
    // weights would just be churn - same reasoning as the IK bone writes.
    private float[]? _runtimeWeights;

    // Blendshape ownership: one owner per shape, like a per-element field drive. A face driver
    // claims the shapes it animates; a higher priority wins regardless of resolve order, so blink/viseme own
    // their shapes over the catch-all expression driver. Drivers re-check ownership at drive time, so a shape
    // that gets stolen stops being written by the loser - this is what stops drivers from fighting (flicker).
    // Local/runtime only (the drivers run on every peer from replicated inputs), not synced.
    public const int BlendShapePriorityBlink = 30;
    public const int BlendShapePriorityEye = 25;
    public const int BlendShapePriorityViseme = 20;
    public const int BlendShapePriorityExpression = 10;

    private object?[]? _blendShapeOwners;
    private int[]? _blendShapeOwnerPriority;

    // succeeds if unclaimed, already yours, or your priority beats the current owner's (a steal); call
    // from the driver's resolve pass
    public bool ClaimBlendShape(int index, object owner, int priority)
    {
        if (owner == null || index < 0 || index >= BlendShapeNames.Count)
            return false;
        EnsureClaimArrays();
        var current = _blendShapeOwners![index];
        if (current == null || ReferenceEquals(current, owner) || priority > _blendShapeOwnerPriority![index])
        {
            _blendShapeOwners[index] = owner;
            _blendShapeOwnerPriority![index] = priority;
            return true;
        }
        return false;
    }

    // check at drive time so a stolen shape isn't written by the previous owner
    public bool OwnsBlendShape(int index, object owner)
        => _blendShapeOwners != null && index >= 0 && index < _blendShapeOwners.Length
           && ReferenceEquals(_blendShapeOwners[index], owner);

    private void EnsureClaimArrays()
    {
        int n = BlendShapeNames.Count;
        if (_blendShapeOwners == null || _blendShapeOwners.Length != n)
        {
            _blendShapeOwners = new object?[n];
            _blendShapeOwnerPriority = new int[n];
        }
    }

    // local only, no network broadcast; for per-frame animation
    public void DriveBlendShapeWeight(int index, float weight)
    {
        if (index < 0 || index >= BlendShapeNames.Count)
            return;
        if (_runtimeWeights == null || _runtimeWeights.Length != BlendShapeNames.Count)
            System.Array.Resize(ref _runtimeWeights, BlendShapeNames.Count);
        if (_runtimeWeights[index] == weight)
            return;
        _runtimeWeights[index] = weight;
        BlendWeightsChanged = true;
        RunApplyChanges();
    }

    public void DriveBlendShapeWeight(string name, float weight)
        => DriveBlendShapeWeight(GetBlendShapeIndex(name), weight);

    // local driver override if set, else the synced value
    public float GetEffectiveBlendShapeWeight(int index)
    {
        if (_runtimeWeights != null && index >= 0 && index < _runtimeWeights.Length)
            return _runtimeWeights[index];
        return (index >= 0 && index < BlendShapeWeights.Count) ? BlendShapeWeights[index] : 0f;
    }

    // called at import; names.Length shapes, each with VertexCount verts
    public void SetBlendShapes(string[]? names, float3[]? flattenedVertices, int mode)
        => SetBlendShapes(names, flattenedVertices, null, mode);

    // Populate blendshapes with optional NORMAL morph deltas (carried when the source provided them, so normals
    // morph with the expression instead of staying frozen at the base). flattenedNormals must line up 1:1 with
    // flattenedVertices or it's ignored. -xlinka
    public void SetBlendShapes(string[]? names, float3[]? flattenedVertices, float3[]? flattenedNormals, int mode)
    {
        BlendShapeNames.Clear();
        BlendShapeVertices.Clear();
        BlendShapeNormals.Clear();
        BlendShapeWeights.Clear();
        // Shape set changed - drop ownership so drivers re-claim against the new indices.
        _blendShapeOwners = null;
        _blendShapeOwnerPriority = null;

        if (names == null || names.Length == 0 || flattenedVertices == null)
            return;

        BlendShapeMode.Value = mode;
        foreach (var n in names)
        {
            BlendShapeNames.Add(n);
            BlendShapeWeights.Add(0f);
        }
        foreach (var v in flattenedVertices)
            BlendShapeVertices.Add(v);

        if (flattenedNormals != null && flattenedNormals.Length == flattenedVertices.Length)
        {
            foreach (var n in flattenedNormals)
                BlendShapeNormals.Add(n);
        }

        MeshDataChanged = true;
        LumoraLogger.Log($"SkinnedMeshRenderer: Set {names.Length} blendshapes (normals: {(BlendShapeNormals.Count > 0 ? "yes" : "no")})");
    }

    // SETTINGS

    public readonly Sync<ShadowCastMode> ShadowCastMode = new();

    // Single material, kept as the whole-mesh assignment and as what every existing save and every
    // caller already uses.
    public readonly AssetRef<MaterialAsset> Material = new();

    // One material per submesh, for meshes that carry several. A skinned mesh routinely does - a body
    // with a separate face or eye surface is the ordinary case - and with only the single Material
    // above, every surface past the first was painted with the wrong material or none at all. When
    // this list is empty the single Material applies to everything, so nothing that worked before
    // changes. -xlinka
    public readonly SyncAssetList<MaterialAsset> Materials = new();

    // Same contract as MeshRenderer.MaterialsChanged: a list has no was-changed latch of its own, so
    // the hook reads and clears this instead.
    public bool MaterialsChanged { get; set; }

    private readonly LoadingSurfaceLatch _loadingSurfaces = new();

    // The material that governs a given surface: the per-surface entry when the list reaches that far,
    // otherwise the single Material.
    public IAssetProvider<MaterialAsset>? MaterialFor(int surfaceIndex)
    {
        if (surfaceIndex >= 0 && surfaceIndex < Materials.Count)
        {
            var perSurface = Materials[surfaceIndex];
            if (perSurface != null)
                return perSurface;
        }
        return Material.Target;
    }

    public int SurfaceMaterialCount => System.Math.Max(1, Materials.Count);

    // Same contract as MeshRenderer.IsSurfaceLoading: one latch slot per surface, so a mesh whose face
    // material arrives after its body material does not report the whole thing as ready. An avatar
    // arrives mesh-first and textures-later, which is exactly the case this covers. -xlinka
    public bool IsSurfaceLoading(int surfaceIndex = 0)
        => _loadingSurfaces.IsLoading(surfaceIndex, MaterialFor(surfaceIndex));

    public readonly Sync<bool> UpdateWhenOffscreen = new();

    public readonly Sync<SkinQuality> Quality = new();

    // CHANGE FLAGS

    public bool MeshDataChanged { get; set; }

    public bool SkeletonChanged { get; set; }

    public bool BonesReady => Bones.Count > 0 && Bones[0] != null;

    // set by the hook when binding is complete
    public bool HookBindingComplete { get; set; }

    private void OnMaterialsChanged(SyncAssetList<MaterialAsset> list) => MaterialsChanged = true;

    private SkeletonBuilder? _watchedSkeleton;

    // Assigning the ref and the skeleton being usable are two different moments: the builder's hook
    // creates the platform skeleton later, and a mesh that tried to bind before that simply never
    // builds. Listen for the ready signal instead of polling for it.
    private void SubscribeToSkeleton()
    {
        var target = Skeleton.Target;
        if (ReferenceEquals(target, _watchedSkeleton))
            return;

        if (_watchedSkeleton != null)
            _watchedSkeleton.SkeletonReady -= OnSkeletonReady;

        _watchedSkeleton = target;
        if (_watchedSkeleton != null)
            _watchedSkeleton.SkeletonReady += OnSkeletonReady;
    }

    private void OnSkeletonReady()
    {
        SkeletonChanged = true;
        RunApplyChanges();
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Materials.OnChanged += OnMaterialsChanged;

        // Subscribe to changes
        Skeleton.OnChanged += (field) => { SkeletonChanged = true; SubscribeToSkeleton(); RunApplyChanges(); };
        MeshAsset.OnChanged += (field) => { MeshDataChanged = true; RunApplyChanges(); };
        Bones.OnChanged += (list) => { SkeletonChanged = true; RunApplyChanges(); };
        Vertices.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        Normals.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        UVs.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        Indices.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        BoneIndices.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        BoneWeights.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };

        LumoraLogger.Debug($"SkinnedMeshRenderer: Awake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnInit()
    {
        base.OnInit();
        ShadowCastMode.Value    = Components.ShadowCastMode.On;
        UpdateWhenOffscreen.Value = true;
        Quality.Value           = SkinQuality.FourBones;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Keep re-driving the hook until it has actually applied the mesh (HookBindingComplete, set by the hook
        // once a real surface is built). Covers BOTH paths: the inline/legacy path waits for bones (BonesReady),
        // and the Phos asset path waits for the MeshProvider's async MeshDataAsset decode AND a ready skeleton.
        // The Phos path populates NO Bones, so the old BonesReady-only gate never fired - the asset would finish
        // decoding a few frames later with nothing to re-drive the build, and the mesh stayed invisible. -xlinka
        if (!HookBindingComplete && (BonesReady || MeshAsset.Target != null))
        {
            RunApplyChanges();
        }

        MeshDataChanged = false;
        SkeletonChanged = false;
    }

    public override void OnDestroy()
    {
        Materials.OnChanged -= OnMaterialsChanged;
        if (_watchedSkeleton != null)
        {
            _watchedSkeleton.SkeletonReady -= OnSkeletonReady;
            _watchedSkeleton = null;
        }
        base.OnDestroy();
        LumoraLogger.Debug($"SkinnedMeshRenderer: Destroyed on slot '{Slot?.SlotName.Value}'");
    }

    // BONE SETUP

    // call after mesh data is loaded and bone names are known
    public void SetupBones(Slot rootSlot)
    {
        if (rootSlot == null)
        {
            LumoraLogger.Warn("SkinnedMeshRenderer: Cannot setup bones with null root slot");
            return;
        }

        if (BoneNames.Count == 0)
        {
            LumoraLogger.Warn("SkinnedMeshRenderer: No bone names to setup");
            return;
        }

        Bones.Clear();

        for (int i = 0; i < BoneNames.Count; i++)
        {
            string boneName = BoneNames[i];
            Slot boneSlot = FindBoneInHierarchy(rootSlot, boneName);

            if (boneSlot != null)
            {
                Bones.Add(boneSlot);
            }
            else
            {
                // Add null reference to maintain bone index alignment
                Bones.Add(null);
                LumoraLogger.Warn($"SkinnedMeshRenderer: Bone '{boneName}' not found in hierarchy");
            }
        }

        SkeletonChanged = true;
        RunApplyChanges();
        LumoraLogger.Log($"SkinnedMeshRenderer: Setup {Bones.Count} bones from root '{rootSlot.SlotName.Value}'");
    }

    public void SetupBonesFromSkeleton(SkeletonBuilder skeleton)
    {
        if (skeleton == null || !skeleton.IsBuilt.Value)
        {
            LumoraLogger.Warn("SkinnedMeshRenderer: Cannot setup bones - skeleton not ready");
            return;
        }

        Skeleton.Target = skeleton;
        Bones.Clear();

        for (int i = 0; i < BoneNames.Count; i++)
        {
            string boneName = BoneNames[i];
            Slot boneSlot = skeleton.GetBoneSlot(boneName);

            if (boneSlot != null)
            {
                Bones.Add(boneSlot);
            }
            else
            {
                Bones.Add(null);
                LumoraLogger.Warn($"SkinnedMeshRenderer: Bone '{boneName}' not found in skeleton");
            }
        }

        SkeletonChanged = true;
        RunApplyChanges();
        LumoraLogger.Log($"SkinnedMeshRenderer: Setup {Bones.Count} bones from skeleton");
    }

    private static Slot FindBoneInHierarchy(Slot root, string boneName)
    {
        if (root == null || string.IsNullOrEmpty(boneName))
            return null!;

        if (root.SlotName.Value == boneName)
            return root;

        foreach (var child in root.Children)
        {
            var found = FindBoneInHierarchy(child, boneName);
            if (found != null)
                return found;
        }

        return null!;
    }

    public Slot GetBone(int index)
    {
        if (index < 0 || index >= Bones.Count)
            return null!;
        return Bones[index]!;
    }

    public Slot GetBone(string name)
    {
        int index = BoneNames.IndexOf(name);
        if (index < 0)
            return null!;
        return GetBone(index);
    }

    // MESH DATA METHODS

    public void SetMeshData(float3[]? vertices, float3[]? normals, float2[]? uvs, int[]? indices,
                            int4[]? boneIndices, float4[]? boneWeights, string[]? boneNames = null)
    {
        if (vertices == null || indices == null)
        {
            LumoraLogger.Warn("SkinnedMeshRenderer: Cannot set mesh data with null vertices or indices");
            return;
        }

        Vertices.Clear();
        Normals.Clear();
        UVs.Clear();
        Indices.Clear();
        BoneIndices.Clear();
        BoneWeights.Clear();
        BoneNames.Clear();

        foreach (var v in vertices)
            Vertices.Add(v);

        if (normals != null)
        {
            foreach (var n in normals)
                Normals.Add(n);
        }
        else
        {
            for (int i = 0; i < vertices.Length; i++)
                Normals.Add(new float3(0, 1, 0));
        }

        if (uvs != null)
        {
            foreach (var uv in uvs)
                UVs.Add(uv);
        }
        else
        {
            for (int i = 0; i < vertices.Length; i++)
                UVs.Add(new float2(0, 0));
        }

        foreach (var idx in indices)
            Indices.Add(idx);

        if (boneIndices != null)
        {
            foreach (var bi in boneIndices)
                BoneIndices.Add(bi);
        }
        else
        {
            for (int i = 0; i < vertices.Length; i++)
                BoneIndices.Add(new int4(0, 0, 0, 0));
        }

        if (boneWeights != null)
        {
            foreach (var bw in boneWeights)
                BoneWeights.Add(bw);
        }
        else
        {
            for (int i = 0; i < vertices.Length; i++)
                BoneWeights.Add(new float4(1, 0, 0, 0));
        }

        if (boneNames != null)
        {
            foreach (var name in boneNames)
                BoneNames.Add(name);
        }

        MeshDataChanged = true;
        LumoraLogger.Log($"SkinnedMeshRenderer: Set mesh data with {vertices.Length} vertices, {indices.Length / 3} triangles, {boneNames?.Length ?? 0} bones");
    }

    public void ClearMesh()
    {
        Vertices.Clear();
        Normals.Clear();
        UVs.Clear();
        Indices.Clear();
        BoneIndices.Clear();
        BoneWeights.Clear();
        Bones.Clear();
        BoneNames.Clear();
        MeshDataChanged = true;
        LumoraLogger.Log("SkinnedMeshRenderer: Cleared mesh data");
    }
}

public enum SkinQuality
{
    Auto = 0,
    OneBone = 1,
    TwoBones = 2,
    FourBones = 4
}

