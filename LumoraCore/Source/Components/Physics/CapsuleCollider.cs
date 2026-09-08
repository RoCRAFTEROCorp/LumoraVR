// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

[ComponentCategory("Physics/Colliders")]
public class CapsuleCollider : Collider
{
    // SYNC FIELDS

    public readonly Sync<float> Height;
    public readonly Sync<float> Radius;

    // Height = Length + (Radius * 2)
    public float Length
    {
        get => System.Math.Max(0f, Height.Value - Radius.Value * 2f);
        set => Height.Value = value + Radius.Value * 2f;
    }

    // INITIALIZATION

    public CapsuleCollider()
    {
        Height = new Sync<float>(this, 2.0f);
        Radius = new Sync<float>(this, 0.5f);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Height.OnChanged += _ => RunApplyChanges();
        Radius.OnChanged += _ => RunApplyChanges();
        LumoraLogger.Debug($"CapsuleCollider: Initialized with Height={Height.Value}, Radius={Radius.Value}");
    }

    // ABSTRACT METHOD IMPLEMENTATIONS

    public override BoundingBox GetLocalBounds()
    {
        // Y axis is the capsule axis; Height is the full tip-to-tip extent (caps included),
        // so half-height is at least the radius. X/Z extent is the radius.
        float r = Radius.Value;
        float halfHeight = System.Math.Max(Height.Value * 0.5f, r);
        var extent = new float3(r, halfHeight, r);
        return new BoundingBox(Offset.Value - extent, Offset.Value + extent);
    }

}

