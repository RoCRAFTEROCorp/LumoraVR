// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core;

// The allowed range of one setting, declared ONCE.
//
// A setting's bounds used to be written in three places: the property setter clamped, the load clamped
// again, and the settings screen passed its own min and max to the slider. Three copies of one fact,
// and they had already drifted: the engine accepted a render scale up to 2.0 while the slider stopped
// at 1.5, so a value the renderer supports could not be reached from the UI at all. Mouse sensitivity
// and smoothing were narrowed the same way.
//
// One declaration, three readers. A range cannot disagree with itself. -xlinka
public readonly struct SettingRange
{
    public readonly float Min;
    public readonly float Max;

    // The UI increment. Zero means the setting is not presented as a slider.
    public readonly float Step;

    // Some settings are "off, or a value in a range": an FPS cap of 0 means uncapped, and 1 would be
    // absurd, so anything above off snaps up to a floor. Zero means the setting has no off state.
    public readonly float Floor;

    public SettingRange(float min, float max, float step = 0f)
    {
        Min = min;
        Max = max;
        Step = step;
        Floor = 0f;
    }

    public SettingRange(float min, float max, float step, float floor)
    {
        Min = min;
        Max = max;
        Step = step;
        Floor = floor;
    }

    public float Clamp(float value) => System.Math.Clamp(value, Min, Max);

    public int ClampInt(int value) => System.Math.Clamp(value, (int)Min, (int)Max);

    // Off, or inside [Floor, Max]. The three settings using this each wrote the rule by hand and each
    // drifted from its own slider: the engine took an FPS cap to 480 while the UI stopped at 240, and a
    // background cap to 240 while the UI stopped at 120. -xlinka
    public float ClampOptional(float value)
        => value <= Min ? Min : System.Math.Clamp(value, Floor, Max);

    public int ClampOptionalInt(int value)
        => value <= (int)Min ? (int)Min : System.Math.Clamp(value, (int)Floor, (int)Max);
}

// Every bounded engine setting. Named rather than keyed by string so a typo is a compile error instead
// of a silently unbounded slider. -xlinka
public static class SettingsCatalog
{
    public static readonly SettingRange MouseSensitivity = new(0.05f, 10f, 0.01f);
    public static readonly SettingRange MouseSmoothing = new(0f, 0.95f, 0.01f);
    public static readonly SettingRange NoclipSpeed = new(1f, 30f, 0.5f);
    public static readonly SettingRange UserHeight = new(0.5f, 2.5f, 0.01f);
    public static readonly SettingRange MasterVolume = new(0f, 1f, 0.01f);
    public static readonly SettingRange RenderScale = new(0.5f, 2f, 0.05f);
    public static readonly SettingRange Upscaler = new(0f, 2f);
    public static readonly SettingRange LodBias = new(0.25f, 4f, 0.05f);
    public static readonly SettingRange ShadowDistanceScale = new(0.25f, 4f, 0.05f);
    public static readonly SettingRange Exposure = new(1f, 16f, 0.5f);
    public static readonly SettingRange Bloom = new(0f, 2f, 0.05f);
    public static readonly SettingRange SnapTurnAngle = new(10f, 90f, 5f);
    public static readonly SettingRange SmoothTurnSpeed = new(30f, 360f, 5f);
    public static readonly SettingRange ReticleSize = new(2f, 48f, 1f);
    // Off at 0, otherwise floored. Max is what the ENGINE accepts, which is what the slider must offer.
    public static readonly SettingRange MaxFps = new(0f, 480f, 10f, 30f);
    public static readonly SettingRange BackgroundFps = new(0f, 240f, 10f, 5f);
    public static readonly SettingRange MeshLodThreshold = new(0f, 8f, 0.5f, 0.5f);

    public static readonly SettingRange ReticleThickness = new(1f, 8f, 0.5f);
}
