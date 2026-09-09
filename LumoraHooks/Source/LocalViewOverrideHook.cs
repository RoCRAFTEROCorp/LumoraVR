// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Applies a slot's per-view transform override while the matching context is the one rendering.
//
// Only the local client runs it, so the override is local by construction: everyone else holds their
// own copy of the same slot and IsLocalUser() is false there. Position, rotation and scale go through
// SlotHook.SetLocalTransformOverride so the slot stays the single writer of its own transform.
//
// A scale override of zero is the one case that does NOT go through the transform: it means "hide this
// from myself", and the whole point of hiding your own head is that its shadow stays on the floor. That
// is ShadowsOnly, not a zero scale, which would take the shadow with it.
[ImplementableHook(typeof(LocalViewOverride))]
public class LocalViewOverrideHook : ComponentHook<LocalViewOverride>
{
    private const float HideScaleEpsilon = 0.0001f;

    private bool _shadowsOnly;
    private bool _transformOverridden;

    public static IHook<LocalViewOverride> Constructor() => new LocalViewOverrideHook();

    public override void Initialize()
    {
        base.Initialize();
        LumoraLogger.Log($"LocalViewOverrideHook: Initialized on '{Owner.Slot.SlotName.Value}'");
    }

    public override void ApplyChanges()
    {
        // UserView hiding only applies while the first-person view renders:
        // third-person/free-cam must show the full avatar.
        bool shouldApply = Owner.Enabled
            && Owner.Context.Value == ViewContext.UserView
            && IsLocalUser()
            && !UserInputState.FocusedExternalCameraActive;

        if (!shouldApply)
        {
            ClearAll();
            return;
        }

        bool hasScale = Owner.HasScaleOverride.Value;
        float3 scale = Owner.ScaleOverride.Value;
        bool hide = hasScale && IsEffectivelyZero(scale);

        SetShadowsOnly(hide);

        float3? position = Owner.HasPositionOverride.Value ? Owner.PositionOverride.Value : null;
        floatQ? rotation = Owner.HasRotationOverride.Value ? Owner.RotationOverride.Value : null;
        // A zero scale is served by ShadowsOnly above, so it must not also reach the transform - the two
        // together would hide the mesh and its shadow.
        float3? scaleOverride = hasScale && !hide ? scale : null;

        PushTransform(position, rotation, scaleOverride);
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld)
            ClearAll();

        base.Destroy(destroyingWorld);
    }

    // Helpers

    private void ClearAll()
    {
        SetShadowsOnly(false);
        PushTransform(null, null, null);
    }

    private void PushTransform(float3? position, floatQ? rotation, float3? scale)
    {
        bool wanted = position.HasValue || rotation.HasValue || scale.HasValue;
        if (!wanted && !_transformOverridden)
            return;

        if (Owner.Slot?.Hook is not SlotHook slotHook)
            return;

        slotHook.SetLocalTransformOverride(position, rotation, scale);
        _transformOverridden = wanted;
    }

    private void SetShadowsOnly(bool shadowsOnly)
    {
        // Re-applied every frame while active to catch MeshInstance3Ds created after us; the restore
        // only runs on the edge so a slot that never hid never pays for the walk.
        if (!shadowsOnly && !_shadowsOnly)
            return;

        SetMeshCastShadow(shadowsOnly
            ? GeometryInstance3D.ShadowCastingSetting.ShadowsOnly
            : GeometryInstance3D.ShadowCastingSetting.On);
        _shadowsOnly = shadowsOnly;
    }

    private static bool IsEffectivelyZero(float3 v)
    {
        return System.Math.Abs(v.x) < HideScaleEpsilon
            && System.Math.Abs(v.y) < HideScaleEpsilon
            && System.Math.Abs(v.z) < HideScaleEpsilon;
    }

    private void SetMeshCastShadow(GeometryInstance3D.ShadowCastingSetting setting)
    {
        if (Owner.Slot?.Hook is not SlotHook slotHook)
            return;

        var slotNode = slotHook.GeneratedNode3D;
        if (slotNode == null || !GodotObject.IsInstanceValid(slotNode)) return;

        var nodes = slotNode.FindChildren("*", "MeshInstance3D", recursive: true, owned: false);
        foreach (var node in nodes)
        {
            if (node is MeshInstance3D mesh)
                mesh.CastShadow = setting;
        }
    }

    private bool IsLocalUser()
    {
        var slot = Owner.Slot;
        while (slot != null)
        {
            var userRoot = slot.GetComponent<UserRoot>();
            if (userRoot != null)
                return userRoot.ActiveUser?.IsLocal ?? false;
            slot = slot.Parent;
        }
        return false;
    }
}
