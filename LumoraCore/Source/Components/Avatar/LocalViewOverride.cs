// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// Replaces part of a slot's rendered transform while a specific rendering context is the one drawing.
//
// It is applied on the LOCAL client only, so it never touches what anyone else sees: every peer holds
// its own copy of the slot and the context test fails there. Position, rotation and scale each have
// their own has-override flag; anything left off falls through to the slot's real transform.
//
// A scale override of zero is special-cased into shadows-only rendering rather than an actual zero
// scale, because the reason you hide your own head is to stop seeing it, not to lose its shadow.
// -xlinka
[ComponentCategory("Users/Avatar")]
public class LocalViewOverride : ImplementableComponent<IHook>
{
    // which rendering context activates this override
    public readonly Sync<ViewContext> Context = new();

    public readonly Sync<bool>   HasPositionOverride = new();
    public readonly Sync<float3> PositionOverride    = new();

    public readonly Sync<bool>   HasRotationOverride = new();
    public readonly Sync<floatQ> RotationOverride    = new();

    // float3.Zero here means "hide from this context" - the mesh stops drawing but keeps casting
    public readonly Sync<bool>   HasScaleOverride = new();
    public readonly Sync<float3> ScaleOverride    = new();

    private readonly UserRootRegistrationTracker _userRootReg;

    public LocalViewOverride()
    {
        _userRootReg = new UserRootRegistrationTracker(this);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        _userRootReg.Attach();
    }

    public override void OnInit()
    {
        base.OnInit();
        Context.Value          = ViewContext.UserView;
        // HasPositionOverride = false (C# default, skip)
        // PositionOverride = float3.Zero (C# default, skip)
        // HasRotationOverride = false (C# default, skip)
        RotationOverride.Value = floatQ.Identity;
        // HasScaleOverride = false (C# default, skip)
        ScaleOverride.Value    = float3.One;
    }

    // The hook also gates on the external-camera state (third-person/free-cam
    // must show the full avatar); poke it when that flips since no sync field
    // changes.
    private bool _lastExternalCamera;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        bool external = UserInputState.FocusedExternalCameraActive;
        if (external != _lastExternalCamera)
        {
            _lastExternalCamera = external;
            RunApplyChanges();
        }
    }

    public override void OnDestroy()
    {
        _userRootReg.Detach();
        base.OnDestroy();
    }
}
