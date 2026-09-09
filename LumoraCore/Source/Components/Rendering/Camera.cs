// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// A camera renders the world into a RenderTexture. That is the whole contract.
//
// It does NOT take over the screen, and the old hook doing exactly that was the reason this component
// was unsafe to attach: it set Camera3D.Current unconditionally, so dropping a Camera anywhere in a
// world stole the local player's viewport with no way back except deleting it. Nothing in the engine
// ever attached one, which is the only reason nobody hit it.
//
// With no TargetTexture a camera does nothing at all - it is a set of framing values with a gizmo, and
// that is a legitimate thing to want (a shot you are lining up, a marker for a photo spot). Point it at
// a RenderTextureProvider's asset and it starts filming into that texture, which is what mirrors,
// portals, security monitors and in-world photos are all built out of. -xlinka
[ComponentCategory("Rendering")]
public class Camera : ImplementableComponent
{
    [Group("Projection")]
    public readonly Sync<ProjectionType> Projection = new();

    // degrees; used in Perspective mode
    public readonly Sync<float> FieldOfView = new();

    // height in world units; used in Orthographic mode
    public readonly Sync<float> OrthographicSize = new();

    public readonly Sync<float> NearClip = new();

    public readonly Sync<float> FarClip = new();

    [Group("Clear")]
    public readonly Sync<ClearMode> Clear = new();

    // used when Clear = Color
    public readonly Sync<color> BackgroundColor = new();

    // The texture this camera films into. Null = the camera renders nothing.
    [Group("Output")]
    public readonly AssetRef<RenderTexture> TargetTexture = new();

    // Render layer bitmask, -1 = everything. Layers 1-5 are the engine's fixed bands (public, private,
    // temp, hidden, overlay); the rest are free.
    public readonly Sync<int> CullingMask = new();

    [Group("Rendering")]
    // Turns off shadows from point and spot lights for this camera's view only. The sun's shadow is a
    // property of the light rather than the viewport and cannot be switched off per camera, so a scene
    // lit by a directional light still gets that one. -xlinka
    public readonly Sync<bool> RenderShadows = new();

    public readonly Sync<bool> UseOcclusionCulling = new();

    public readonly Sync<bool> AllowMSAA = new();

    // Off gives a raw render with no tonemapping, glow or ambient occlusion, which is what you want when
    // the result is going to be composited rather than looked at.
    public readonly Sync<bool> RenderPostProcessing = new();

    // Render ONLY these slots and their children. Empty = render everything the culling mask allows.
    //
    // This is a layer trick underneath: the camera takes one free render layer, the listed subtrees get
    // that layer added on top of whatever they already had, and the camera renders that layer alone. So
    // the listed objects keep rendering normally for everyone else, and there is no per-object cost.
    [Group("Selective Rendering")]
    public readonly SyncRefList<Slot> SelectiveRender;

    // Hide these slots and their children from this camera. Empty = hide nothing.
    //
    // Exclusion cannot be additive the way inclusion is - to not see something you have to move it off
    // the layer you are looking at - so the excluded subtrees are swapped onto the camera's own layer,
    // which this camera then masks off. Consequence worth knowing: TWO cameras cannot exclude the same
    // object, because it only has one layer to be moved to. The second one wins.
    //
    // Ignored while SelectiveRender is non-empty, where excluding just means not being on the list.
    public readonly SyncRefList<Slot> ExcludeRender;

    public Camera()
    {
        SelectiveRender = new SyncRefList<Slot>(this);
        ExcludeRender = new SyncRefList<Slot>(this);
    }

    public override void OnInit()
    {
        base.OnInit();

        // Projection = ProjectionType.Perspective (enum 0, C# default, skip)
        FieldOfView.Value        = 60f;
        OrthographicSize.Value   = 5f;
        NearClip.Value           = 0.05f;
        FarClip.Value            = 1000f;
        // Clear = ClearMode.Skybox (enum 0, C# default, skip)
        BackgroundColor.Value    = new color(0.2f, 0.2f, 0.2f, 1f);
        // TargetTexture = default (C# default null, skip)
        CullingMask.Value        = -1; // All layers
        RenderShadows.Value      = true;
        UseOcclusionCulling.Value = true;
        AllowMSAA.Value          = true;
        RenderPostProcessing.Value = true;
    }

    // 0 when there is no render target, because without one there is no framing to have an aspect of.
    // The gizmo substitutes its own display aspect in that case.
    public float AspectRatio
    {
        get
        {
            var target = TargetTexture.Target?.Asset;
            if (target != null && target.RenderHeight > 0)
                return (float)target.RenderWidth / target.RenderHeight;

            return 0f;
        }
    }

    public bool IsRenderTexture => TargetTexture.Target != null;

    // The hook needs the slot's GLOBAL transform, and a slot moving does not dirty the components on it
    // - the slot dirties itself. Without this a camera on a moving mount would film one frame from where
    // it started and then never update again, which is exactly the class of bug ApplyChanges being
    // queue-driven keeps producing. -xlinka
    private float3 _lastPosition;
    private floatQ _lastRotation;
    private bool _hasLastTransform;
    private float _layerRescanTimer;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (TargetTexture.Target == null)
            return;

        var slot = Slot;
        if (slot == null)
            return;

        // A selective or excluded subtree that grows new children after the layers were applied has no
        // way to announce itself, and a camera that never moves would otherwise never look again. Poke
        // it on a timer so the rescan the hook already throttles actually gets a chance to run.
        if (SelectiveRender.Count > 0 || ExcludeRender.Count > 0)
        {
            _layerRescanTimer += delta;
            if (_layerRescanTimer >= 0.5f)
            {
                _layerRescanTimer = 0f;
                MarkChangeDirty();
                return;
            }
        }

        float3 position = slot.GlobalPosition;
        floatQ rotation = slot.GlobalRotation;

        if (_hasLastTransform && position == _lastPosition && rotation == _lastRotation)
            return;

        _lastPosition = position;
        _lastRotation = rotation;
        _hasLastTransform = true;
        MarkChangeDirty();
    }
}

public enum ProjectionType
{
    Perspective,
    Orthographic
}

public enum ClearMode
{
    Skybox,
    Color,
    DepthOnly,
    Nothing
}
