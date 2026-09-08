// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

[ComponentCategory("Animation")]
public class SkeletonBuilder : ImplementableComponent
{
    // SYNC FIELDS

    // all bones must be descendants of this slot
    public readonly SyncRef<Slot> RootBone = null!;

    // order must match what mesh bone weights expect
    // The bone lists are ordinary sync members: readonly fields are what member discovery sees, so they get
    // initialized before OnAwake, replicate to every peer, and save. A property with a setter never did. -xlinka
    public readonly SyncFieldList<string> BoneNames = new();

    public readonly SyncRefList<Slot> BoneSlots = new();

    // local space relative to parent bone; used to build inverse bind pose matrices
    public readonly SyncFieldList<float4x4> RestPoseTransforms = new();

    public readonly Sync<bool> IsBuilt = new();

    // CHANGE TRACKING

    public bool BoneHierarchyChanged { get; set; }

    // Raised by the rendering hook once the platform skeleton actually exists. A skinned mesh cannot
    // build until then, and the ref being ASSIGNED is not the same event as the skeleton being READY -
    // the builder's own hook may not have run yet. Without this a renderer that binds too early simply
    // never builds, and polling for it instead costs a full mesh rebuild every frame. -xlinka
    public event System.Action? SkeletonReady;

    public void NotifySkeletonReady() => SkeletonReady?.Invoke();

    // LIFECYCLE

    public override void OnAwake()
    {
        base.OnAwake();

        // IsBuilt = false (C# default, no OnInit needed)

        RootBone.OnChanged += (field) => BoneHierarchyChanged = true;
        BoneNames.OnChanged += (list) => BoneHierarchyChanged = true;
        BoneSlots.OnChanged += (list) => BoneHierarchyChanged = true;

        LumoraLogger.Debug($"SkeletonBuilder: Awake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnStart()
    {
        base.OnStart();
        LumoraLogger.Log($"SkeletonBuilder: Started on slot '{Slot.SlotName.Value}'");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // ensures bones sync to Godot's Skeleton3D; hook checks if rebuild is needed
        if (IsBuilt.Value && BoneCount > 0)
        {
            RunApplyChanges();
        }

        // Clear the flag AFTER registering for hook update
        BoneHierarchyChanged = false;
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        LumoraLogger.Debug($"SkeletonBuilder: Destroyed on slot '{Slot?.SlotName.Value}'");
    }

    // PUBLIC API

    public void BuildFromHierarchy(Slot rootBone)
    {
        if (rootBone == null)
        {
            LumoraLogger.Warn("SkeletonBuilder: Cannot build from null root bone");
            return;
        }

        RootBone.Target = rootBone;
        BoneNames.Clear();
        BoneSlots.Clear();
        RestPoseTransforms.Clear();

        CollectBonesRecursive(rootBone);

        IsBuilt.Value = true;
        BoneHierarchyChanged = true;

        LumoraLogger.Log($"SkeletonBuilder: Built skeleton with {BoneNames.Count} bones from root '{rootBone.SlotName.Value}'");
    }

    // bones must be added in hierarchical order, parent before children
    public void AddBone(string boneName, Slot boneSlot, float4x4? restPose = null)
    {
        if (string.IsNullOrEmpty(boneName) || boneSlot == null)
        {
            LumoraLogger.Warn("SkeletonBuilder: Cannot add bone with null name or slot");
            return;
        }

        BoneNames.Add(boneName);
        BoneSlots.Add(boneSlot);

        RestPoseTransforms.Add(restPose ?? float4x4.Identity);

        BoneHierarchyChanged = true;

        LumoraLogger.Debug($"SkeletonBuilder: Added bone '{boneName}' (total: {BoneNames.Count})");
    }

    public int GetBoneIndex(string boneName)
    {
        for (int i = 0; i < BoneNames.Count; i++)
        {
            if (BoneNames[i] == boneName)
                return i;
        }
        return -1;
    }

    public Slot GetBoneSlot(string boneName)
    {
        int index = GetBoneIndex(boneName);
        if (index >= 0 && index < BoneSlots.Count)
            return BoneSlots[index]!;
        return null!;
    }

    public int BoneCount => BoneNames.Count;

    public void ClearBones()
    {
        BoneNames.Clear();
        BoneSlots.Clear();
        RestPoseTransforms.Clear();
        IsBuilt.Value = false;
        BoneHierarchyChanged = true;

        LumoraLogger.Debug("SkeletonBuilder: Cleared all bones");
    }

    // PRIVATE METHODS

    private void CollectBonesRecursive(Slot bone)
    {
        if (bone == null)
            return;

        BoneNames.Add(bone.SlotName.Value);
        BoneSlots.Add(bone);

        var restPose = CalculateRestPoseTransform(bone);
        RestPoseTransforms.Add(restPose);

        foreach (var child in bone.Children)
        {
            CollectBonesRecursive(child);
        }
    }

    private float4x4 CalculateRestPoseTransform(Slot bone)
    {
        if (bone == null)
            return float4x4.Identity;

        var position = bone.LocalPosition.Value;
        var rotation = bone.LocalRotation.Value;
        var scale = bone.LocalScale.Value;

        var translationMatrix = float4x4.Translate(position);
        var rotationMatrix = float4x4.Rotate(rotation);
        var scaleMatrix = float4x4.Scale(scale);

        return translationMatrix * rotationMatrix * scaleMatrix;
    }
}

