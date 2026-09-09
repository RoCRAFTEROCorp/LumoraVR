// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using System.Collections.Generic;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

/// <summary>
/// Hook for SkeletonBuilder component -> Godot Skeleton3D.
/// Creates and manages a Skeleton3D node with bone hierarchy.
/// Platform skeleton hook for Godot.
/// </summary>
[ImplementableHook(typeof(SkeletonBuilder))]
public class SkeletonHook : ComponentHook<SkeletonBuilder>
{
    private Skeleton3D _skeleton = null!;
    private Dictionary<string, int> _boneNameToIndex = new Dictionary<string, int>();
    private Dictionary<int, Transform3D> _boneRestPoses = new Dictionary<int, Transform3D>();
    private Lumora.Core.Slot _rootBoneSlot = null!;

    // Each bone's parent IN THE SKELETON, which is not always its parent slot: the skeleton is built from
    // the nearest ancestor that is also a bone, so any non-bone slot in between drops out, and a bone with
    // no bone ancestor at all is parented at the skeleton root. Poses have to be expressed in whichever
    // frame the bone actually ended up in. -xlinka
    private readonly Dictionary<int, Lumora.Core.Slot> _boneParentSlot = new Dictionary<int, Lumora.Core.Slot>();

    public override void Initialize()
    {
        base.Initialize();

        // Create Skeleton3D node
        _skeleton = new Skeleton3D();
        _skeleton.Name = "Skeleton";
        attachedNode.AddChild(_skeleton);

        // Build initial skeleton if data is available
        if (Owner.IsBuilt.Value)
        {
            RebuildSkeleton();
        }

        LumoraLogger.Log($"SkeletonHook: Initialized for slot '{Owner.Slot.SlotName.Value}'");
    }

    public override void ApplyChanges()
    {
        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton))
            return;

        // Rebuild skeleton if:
        // 1. Hierarchy changed flag is set, OR
        // 2. Lumora has bones but Godot skeleton doesn't (late initialization case)
        bool needsRebuild = Owner.BoneHierarchyChanged ||
            (Owner.IsBuilt.Value && Owner.BoneCount > 0 && _skeleton.GetBoneCount() == 0);

        if (needsRebuild && Owner.IsBuilt.Value)
        {
            LumoraLogger.Log($"SkeletonHook: Triggering rebuild - hierarchyChanged={Owner.BoneHierarchyChanged}, lumoraBones={Owner.BoneCount}, godotBones={_skeleton.GetBoneCount()}");
            RebuildSkeleton();
        }

        // Update bone transforms from slots
        UpdateBoneTransforms();

        // Update enabled state
        bool enabled = Owner.Enabled;
        if (_skeleton.Visible != enabled)
        {
            _skeleton.Visible = enabled;
        }
    }

    /// <summary>
    /// Rebuild the entire Skeleton3D from component data.
    /// </summary>
    private void RebuildSkeleton()
    {
        if (_skeleton == null)
            return;

        LumoraLogger.Log($"SkeletonHook: Rebuilding skeleton with {Owner.BoneCount} bones");

        // Clear existing bones (Godot doesn't have RemoveBone, so recreate skeleton)
        if (_skeleton.GetBoneCount() > 0)
        {
            // Remove and recreate skeleton node
            _skeleton.QueueFree();
            _skeleton = new Skeleton3D();
            _skeleton.Name = "Skeleton";
            attachedNode.AddChild(_skeleton);
        }

        _boneNameToIndex.Clear();
        _boneRestPoses.Clear();
        _boneParentSlot.Clear();

        // Build parent hierarchy map
        var parentMap = BuildParentMap();

        // Godot requires a bone's parent to have a LOWER index, and SetBoneParent silently NO-OPS if the parent
        // hasn't been added yet. Assimp's bone list is NOT hierarchically ordered, so adding in raw order orphaned
        // any bone whose parent came later (e.g. Ear2/Ear3) to the skeleton ROOT - its global rest then lost the
        // ancestor translation (originErr ~2.57, basis fine) and its verts flew off as shards. Add bones in
        // TOPOLOGICAL order (every bone AFTER its parent bone) so parenting always resolves. -xlinka
        var slotToBoneIdx = new Dictionary<Lumora.Core.Slot, int>();
        for (int i = 0; i < Owner.BoneCount; i++)
        {
            var s = Owner.BoneSlots[i];
            if (s != null) slotToBoneIdx[s] = i;
        }
        var addOrder = new List<int>(Owner.BoneCount);
        var ordered = new HashSet<int>();
        void AddParentFirst(int i)
        {
            if (i < 0 || !ordered.Add(i)) return;
            var s = Owner.BoneSlots[i];
            if (s != null && parentMap.TryGetValue(s, out var ps) && slotToBoneIdx.TryGetValue(ps, out int pIdx))
                AddParentFirst(pIdx); // ensure the parent bone is added before this one
            addOrder.Add(i);
        }
        for (int i = 0; i < Owner.BoneCount; i++)
            AddParentFirst(i);

        // Add bones to the Godot skeleton in topological (parent-first) order.
        Lumora.Core.Slot rootBoneSlot = null!;
        var slotToGodotBone = new Dictionary<Lumora.Core.Slot, int>();
        foreach (int i in addOrder)
        {
            string boneName = Owner.BoneNames[i];
            var boneSlot = Owner.BoneSlots[i];

            if (boneSlot == null)
            {
                LumoraLogger.Warn($"SkeletonHook: Bone '{boneName}' has null slot, skipping");
                continue;
            }

            // A DUPLICATE NAME IS FATAL AND USED TO BE SILENT.
            //
            // Godot refuses a second bone with an existing name: AddBone returns -1 and logs its own
            // error. Nothing here checked that, so -1 went into the name map, every later child looked
            // its parent up by name and got -1, and SetBoneParent(-1, ...) did nothing - the skeleton
            // came out structurally wrong with no warning of ours anywhere. Downstream, the skinned
            // mesh then failed to resolve a quarter of its bind bones and collapsed them onto bone 0,
            // which reads as "the avatar deformed into a knot" rather than "two bones share a name".
            //
            // Rigs collide names legitimately: a deform bone and a control bone for the same joint, or
            // a naming convention that a name-cleaning pass has flattened. So make the name unique and
            // carry on, rather than dropping the bone.
            string uniqueName = boneName;
            if (_boneNameToIndex.ContainsKey(uniqueName))
            {
                int suffix = 1;
                do { uniqueName = $"{boneName}.{suffix++}"; }
                while (_boneNameToIndex.ContainsKey(uniqueName));
                LumoraLogger.Warn($"SkeletonHook: duplicate bone name '{boneName}' -> '{uniqueName}' (two bones share a slot name)");
            }

            int boneIndex = _skeleton.AddBone(uniqueName);
            if (boneIndex < 0)
            {
                LumoraLogger.Warn($"SkeletonHook: skeleton refused bone '{uniqueName}', skipping");
                continue;
            }
            _boneNameToIndex[uniqueName] = boneIndex;
            // Also key by the ORIGINAL name, so a bind list naming the pre-uniquified bone still
            // resolves to the first bone that claimed it.
            _boneNameToIndex.TryAdd(boneName, boneIndex);
            slotToGodotBone[boneSlot] = boneIndex;

            // Parent by SLOT, not by name. The slot is the real identity; a name is a label that two
            // bones can share, and looking a parent up by a shared label picks whichever won the race.
            bool parentedToBone = false;
            if (parentMap.TryGetValue(boneSlot, out var parentSlot))
            {
                if (slotToGodotBone.TryGetValue(parentSlot, out int parentIndex))
                {
                    _skeleton.SetBoneParent(boneIndex, parentIndex);
                    _boneParentSlot[boneIndex] = parentSlot;
                    parentedToBone = true;
                }
                else
                {
                    LumoraLogger.Warn($"SkeletonHook: bone '{uniqueName}' parent '{parentSlot.SlotName.Value}' not added yet - topological order failed.");
                }
            }
            // First skeleton-root bone (no PARENT BONE): remember its slot so we can re-introduce the transform of
            // any non-bone nodes between the model root and it (the orientation offset below).
            if (!parentedToBone)
                rootBoneSlot ??= boneSlot;

            // Set rest pose
            Transform3D restPose;
            if (i < Owner.RestPoseTransforms.Count)
            {
                restPose = ConvertToGodotTransform(Owner.RestPoseTransforms[i]);
            }
            else
            {
                // Use slot's local transform as fallback
                restPose = ConvertSlotLocalTransform(boneSlot);
            }

            _skeleton.SetBoneRest(boneIndex, restPose);
            _boneRestPoses[boneIndex] = restPose;
        }
        // Re-introduce dropped non-bone-node transforms (model-orientation fix). Bone rests are stored slot-local,
        // so any NON-bone node between the model root and the first bone - e.g. an FBX "Armature"/"RootNode" that
        // carries the up-axis (Z-up -> Y-up) rotation - is dropped from the skeleton, leaving the whole rig
        // mis-oriented (it imported lying on its side). The bone SLOTS still carry that transform (so IK + bone
        // visuals stay correct); only the Skeleton3D lost it. Offset the Skeleton3D node by the root bone's
        // slot-parent transform relative to the model root - without touching any bone rest or bind pose
        // (skinning math unchanged). -xlinka
        _rootBoneSlot = rootBoneSlot;
        RefreshOrientationOffset();
        LumoraLogger.Log($"SkeletonHook: rebuilt with {_skeleton.GetBoneCount()} bones, orientation offset origin={_skeleton.Transform.Origin} det={_skeleton.Transform.Basis.Determinant():F2}");

        // Tell anything skinned to this rig that it can build now.
        Owner.NotifySkeletonReady();
    }

    // The prefix nodes are LIVE transforms, not import-time constants: the forward normalization yaws the
    // Armature after the skeleton is built, and the studio can insert a wrapper above the hips. A baked
    // offset goes stale then and the skin renders somewhere the bones aren't (the engine-side bones,
    // markers and IK all follow the slots). Re-derive it every ApplyChanges; the parent is re-read from
    // the root bone each time so reparenting is picked up too. -xlinka
    private void RefreshOrientationOffset()
    {
        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton))
            return;

        var offset = Transform3D.Identity;
        var space = SkeletonSpaceSlot;
        if (space != null && Owner.Slot != null && space != Owner.Slot)
            offset = SlotGlobalTransform3D(Owner.Slot).AffineInverse() * SlotGlobalTransform3D(space);

        if (!_skeleton.Transform.IsEqualApprox(offset))
            _skeleton.Transform = offset;
    }

    // The frame the Skeleton3D node itself sits in. Root-bone poses are expressed relative to this, and the
    // offset above puts the node there, so both have to read it from the same place or the rig lands twice
    // as far off as it started. -xlinka
    private Lumora.Core.Slot? SkeletonSpaceSlot
    {
        get
        {
            var source = _rootBoneSlot != null && !_rootBoneSlot.IsDestroyed ? _rootBoneSlot.Parent : null;
            if (source != null && !source.IsDestroyed && Owner.Slot != null && source != Owner.Slot)
                return source;
            return Owner.Slot;
        }
    }

    /// <summary>
    /// Push the bone SLOT transforms onto the Godot Skeleton3D so the skinned mesh deforms. One-way: the
    /// engine owns the bones. Our own IK (AvatarIK / FullBodyIKSolver) and animation write the bone slots;
    /// the Skeleton3D is purely a skinning target. We do NOT read poses back from Godot - Godot runs no IK
    /// here, so a read-back would just round-trip our own values (adding drift and wasted work) and let the
    /// platform fight engine authority. -xlinka
    /// </summary>
    private void UpdateBoneTransforms()
    {
        if (_skeleton == null || Owner.BoneCount == 0)
            return;

        RefreshOrientationOffset();
        SyncSlotsToSkeleton();
    }

    /// <summary>
    /// Copy each bone slot's local transform onto its Skeleton3D bone pose, so the GPU skinning follows the
    /// engine-driven bones (IK, animation, manual posing alike).
    /// </summary>
    private void SyncSlotsToSkeleton()
    {
        var space = SkeletonSpaceSlot;

        for (int i = 0; i < Owner.BoneCount; i++)
        {
            var boneSlot = Owner.BoneSlots[i];
            if (boneSlot == null)
                continue;

            string boneName = Owner.BoneNames[i];
            if (!_boneNameToIndex.TryGetValue(boneName, out int boneIndex))
                continue;

            // A pose is relative to the bone's SKELETON parent. That is the same thing as its slot-local
            // transform only while the two hierarchies agree, and they stop agreeing the moment a non-bone
            // slot sits between two bones - or the bone set has no common chain at all, which is what a
            // pair of claws bound to fingertips on BOTH hands looks like. Every one of those bones is a
            // skeleton root, so pushing its finger-local transform put it in the frame of whichever
            // fingertip happened to be added first, and the mesh landed half a metre away. Derive the pose
            // from the global transforms, which is exact in every arrangement and reduces to the local one
            // when nothing is in between. -xlinka
            var parentSlot = _boneParentSlot.GetValueOrDefault(boneIndex);
            Transform3D pose;
            if (parentSlot != null && !parentSlot.IsDestroyed)
            {
                pose = ReferenceEquals(parentSlot, boneSlot.Parent)
                    ? ConvertSlotLocalTransform(boneSlot)
                    : ConvertToGodotTransform(parentSlot.WorldToLocal * boneSlot.LocalToWorld);
            }
            else if (space != null && !space.IsDestroyed && !ReferenceEquals(space, boneSlot.Parent))
            {
                pose = ConvertToGodotTransform(space.WorldToLocal * boneSlot.LocalToWorld);
            }
            else
            {
                pose = ConvertSlotLocalTransform(boneSlot);
            }

            _skeleton.SetBonePose(boneIndex, pose);
        }
    }

    /// <summary>
    /// Build a map of child slots to their parents.
    /// </summary>
    private Dictionary<Lumora.Core.Slot, Lumora.Core.Slot> BuildParentMap()
    {
        var parentMap = new Dictionary<Lumora.Core.Slot, Lumora.Core.Slot>();

        for (int i = 0; i < Owner.BoneCount; i++)
        {
            var boneSlot = Owner.BoneSlots[i];
            if (boneSlot == null)
                continue;

            // Find parent slot that is also a bone
            var parentSlot = boneSlot.Parent;
            while (parentSlot != null)
            {
                if (Owner.BoneSlots.Contains(parentSlot))
                {
                    parentMap[boneSlot] = parentSlot;
                    break;
                }
                parentSlot = parentSlot.Parent;
            }
        }

        return parentMap;
    }

    /// <summary>
    /// Convert Lumora float4x4 to Godot Transform3D.
    /// </summary>
    private Transform3D ConvertToGodotTransform(float4x4 matrix)
    {
        // float4x4 uses column-major storage: c0, c1, c2, c3
        // Each column is a float4 (x, y, z, w)

        // Extract translation from last column (c3)
        Vector3 origin = new Vector3(matrix.c3.x, matrix.c3.y, matrix.c3.z);

        // Extract basis (rotation + scale) from first 3 columns
        Basis basis = new Basis(
            new Vector3(matrix.c0.x, matrix.c0.y, matrix.c0.z), // Column 0
            new Vector3(matrix.c1.x, matrix.c1.y, matrix.c1.z), // Column 1
            new Vector3(matrix.c2.x, matrix.c2.y, matrix.c2.z)  // Column 2
        );

        return new Transform3D(basis, origin);
    }

    /// <summary>
    /// Convert Lumora slot local transform to Godot Transform3D.
    /// </summary>
    private Transform3D ConvertSlotLocalTransform(Lumora.Core.Slot slot)
    {
        if (slot == null)
            return Transform3D.Identity;

        // Get local transform components
        var position = slot.LocalPosition.Value;
        var rotation = slot.LocalRotation.Value;
        var scale = slot.LocalScale.Value;

        // Convert to Godot types
        Vector3 godotPosition = new Vector3(position.x, position.y, position.z);
        Quaternion godotRotation = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
        Vector3 godotScale = new Vector3(scale.x, scale.y, scale.z);

        // Build transform
        Transform3D transform = Transform3D.Identity;
        transform.Basis = new Basis(godotRotation).Scaled(godotScale);
        transform.Origin = godotPosition;

        return transform;
    }

    /// <summary>
    /// A slot's global transform as a Godot Transform3D (position + rotation + scale). Used to compute the
    /// skeleton's orientation offset relative to the model root. -xlinka
    /// </summary>
    private static Transform3D SlotGlobalTransform3D(Lumora.Core.Slot slot)
    {
        var p = slot.GlobalPosition;
        var r = slot.GlobalRotation;
        var s = slot.GlobalScale;
        var basis = new Basis(new Quaternion(r.x, r.y, r.z, r.w)).Scaled(new Vector3(s.x, s.y, s.z));
        return new Transform3D(basis, new Vector3(p.x, p.y, p.z));
    }

    /// <summary>
    /// Get the Godot Skeleton3D instance.
    /// Used by SkinnedMeshHook to attach meshes.
    /// </summary>
    public Skeleton3D GetSkeleton()
    {
        return _skeleton;
    }

    /// <summary>
    /// Get the bone index for a bone name.
    /// Returns -1 if bone not found.
    /// </summary>
    public int GetBoneIndex(string boneName)
    {
        if (_boneNameToIndex.TryGetValue(boneName, out int index))
            return index;
        return -1;
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _skeleton != null && GodotObject.IsInstanceValid(_skeleton))
        {
            _skeleton.QueueFree();
        }

        _rootBoneSlot = null!;
        _skeleton = null!;
        _boneNameToIndex.Clear();
        _boneRestPoses.Clear();

        base.Destroy(destroyingWorld);
    }
}

