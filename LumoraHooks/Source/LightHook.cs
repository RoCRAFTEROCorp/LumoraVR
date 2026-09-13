// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Light component -> Godot Light3D. Light type (Directional/Point/Spot) maps
// to different Godot subclasses so the platform node is rebuilt via
// ReplacePlatformNode when Owner.Type changes. - xlinka
[ImplementableHook(typeof(Light))]
public class LightHook : NodeBackedComponentHook<Light, Light3D>
{
    private static readonly bool Standalone = OS.HasFeature("android");
    private const float StandaloneShadowDistance = 20f;

    public static IHook<Light> Constructor() => new LightHook();

    public Light3D GodotLight => PlatformNode;

    protected override Light3D CreatePlatformNode() => BuildLight(Owner.Type.Value);

    protected override void OnAfterAttach()
    {
        base.OnAfterAttach();
        EngineSettings.Changed += OnSettingsChanged;
    }

    public override void Destroy(bool destroyingWorld)
    {
        EngineSettings.Changed -= OnSettingsChanged;
        base.Destroy(destroyingWorld);
    }

    // Quality settings are global; re-dirty the owner so SyncProperties runs on the main thread instead
    // of poking the Godot node from wherever the setting was written.
    private void OnSettingsChanged()
    {
        var owner = Owner;
        var world = owner?.World;
        if (owner == null || world == null || owner.IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (!owner.IsDestroyed)
                owner.MarkChangeDirty();
        });
    }

    protected override void SyncProperties()
    {
        if (Owner.Type.GetWasChangedAndClear())
            ReplacePlatformNode(BuildLight(Owner.Type.Value));

        var light = PlatformNode;
        if (light == null) return;

        var c = Owner.LightColor.Value;
        light.LightColor = new Color(c.r, c.g, c.b, c.a);
        light.LightEnergy = Owner.Intensity.Value;

        if (light is DirectionalLight3D directional)
        {
            // The user's global scale rides on top of the light's authored distance: shrinking the
            // cascade range is the cheapest real cut on the shadow pass, and it has to work on worlds
            // whose lights were authored by someone else. -xlinka
            directional.DirectionalShadowMaxDistance =
                System.Math.Max(1f, Owner.ShadowMaxDistance.Value * EngineSettings.ShadowDistanceScale);
            directional.DirectionalShadowMode = Owner.ShadowSplits.Value switch
            {
                ShadowSplitMode.Orthogonal => DirectionalLight3D.ShadowMode.Orthogonal,
                ShadowSplitMode.Two => DirectionalLight3D.ShadowMode.Parallel2Splits,
                _ => DirectionalLight3D.ShadowMode.Parallel4Splits
            };

            // A standalone headset gets one cascade over a short range, whatever the world authored.
            // Four splits are four scene passes at 2048 each and the Pico 4 was spending most of its
            // 100 shadow draws there for six visible ones. -xlinka
            if (Standalone)
            {
                directional.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal;
                directional.DirectionalShadowMaxDistance =
                    System.Math.Min(directional.DirectionalShadowMaxDistance, StandaloneShadowDistance);
            }
        }
        else if (light is OmniLight3D omni)
        {
            omni.OmniRange = Owner.Range.Value;
            ApplyDistanceFade(omni);
        }
        else if (light is SpotLight3D spot)
        {
            spot.SpotRange = Owner.Range.Value;
            spot.SpotAngle = Owner.SpotAngle.Value;
            ApplyDistanceFade(spot);
        }

        ApplyShadowFade(light);
        ApplyCookie(light);

        // ShadowStrength was INVERTED, and the result was the worst of both worlds.
        //
        // The old line was ShadowOpacity = 1f - ShadowStrength. The field defaults to 1 and nothing in
        // the entire repo ever writes it, so every light in every world got opacity 0 - a shadow that
        // is fully transparent, i.e. invisible - while ShadowEnabled stayed true and the renderer kept
        // paying for the whole shadow map. Every light was buying a shadow nobody could see.
        //
        // Strength 1 means a strong shadow, so the mapping is direct. And a light whose shadow would be
        // invisible now stops rendering one at all: at opacity zero the two are pixel-identical, so the
        // pass is pure cost. -xlinka
        float shadowOpacity = System.Math.Clamp(Owner.ShadowStrength.Value, 0f, 1f);
        bool wantsShadow = Owner.Shadows.Value != ShadowType.None && shadowOpacity > 0.001f;

        // Positional shadows on standalone are six cubemap faces per omni, each a full scene pass. The
        // one directional light keeps its (single) cascade; everything else goes unshadowed. -xlinka
        if (Standalone && light is not DirectionalLight3D)
            wantsShadow = false;

        light.ShadowEnabled = wantsShadow;
        light.ShadowOpacity = shadowOpacity;

        // Soft was a no-op: both cases set the same flag and nothing ever wrote a blur. The softness of
        // a shadow in Godot is the light's own size (its angular diameter for a directional), so that
        // is what has to differ between the two.
        if (wantsShadow)
        {
            bool soft = Owner.Shadows.Value == ShadowType.Soft;
            if (light is DirectionalLight3D directionalSoft)
                directionalSoft.LightAngularDistance = soft ? 1.0f : 0f;
            else
                light.LightSize = soft ? 0.2f : 0f;
            light.ShadowBlur = soft ? 1.5f : 1f;
        }
        light.ShadowBias = Owner.ShadowBias.Value;
        light.ShadowNormalBias = Owner.ShadowNormalBias.Value;
        light.Visible = Owner.Enabled.Value;
    }

    // Zero length is the off switch, not a zero-metre dissolve: Godot reads Begin and Length independently
    // and a length of 0 with fade enabled would pop the light out at Begin with no transition at all. -xlinka
    private void ApplyDistanceFade(Light3D light)
    {
        float length = System.Math.Max(0f, Owner.DistanceFadeLength.Value);
        if (length <= 0f)
        {
            light.DistanceFadeEnabled = false;
            return;
        }

        light.DistanceFadeEnabled = true;
        light.DistanceFadeBegin = System.Math.Max(0f, Owner.DistanceFadeBegin.Value);
        light.DistanceFadeLength = length;
    }

    // The shadow's own fade distance, which Godot keeps separate from the light's. Written whenever a
    // value is set, independently of whether the LIGHT fades - a lamp can perfectly well shine at any
    // range while only casting nearby, and that is the point of the field.
    private void ApplyShadowFade(Light3D light)
    {
        float fade = System.Math.Max(0f, Owner.ShadowFadeDistance.Value);
        if (fade <= 0f)
            return;

        light.DistanceFadeEnabled = true;
        light.DistanceFadeShadow = fade;
        if (light.DistanceFadeLength <= 0f)
        {
            // Godot needs a non-zero fade length for the shadow cutoff to mean anything, and a light
            // with no fade of its own would otherwise vanish at DistanceFadeBegin. Push the light's own
            // fade far enough out that only the shadow is affected.
            light.DistanceFadeBegin = fade * 4f;
            light.DistanceFadeLength = fade;
        }
    }

    // The cookie is Godot's light projector: multiplied into the light before it reaches anything, so a
    // window frame or a caustic pattern costs one texture and no geometry.
    //
    // Godot has NO directional projector - the property exists on the shared Light3D base but the
    // directional renderer never reads it - so a cookie on a directional light is dropped. It says so
    // once per light rather than per frame, and rather than not at all: a cookie that silently does
    // nothing is the exact class of lying inspector row this pass exists to kill. -xlinka
    private bool _warnedDirectionalCookie;

    private void ApplyCookie(Light3D light)
    {
        Texture2D? projector = null;
        if (Owner.Cookie.Target?.Asset?.Hook is IGodotTexture textureHook && textureHook.IsValid)
            projector = textureHook.GodotTexture2D;

        if (light is DirectionalLight3D)
        {
            if (projector != null && !_warnedDirectionalCookie)
            {
                _warnedDirectionalCookie = true;
                LumoraLogger.Warn(
                    $"Light '{Owner.Slot.SlotName.Value}': cookies are point/spot only, the renderer has no " +
                    "directional projector. Ignoring it.");
            }
            if (light.LightProjector != null)
                light.LightProjector = null;
            return;
        }

        if (light.LightProjector != projector)
            light.LightProjector = projector;
    }

    private static Light3D BuildLight(LightType type)
    {
        return type switch
        {
            LightType.Directional => new DirectionalLight3D { Name = "DirectionalLight" },
            LightType.Point => new OmniLight3D { Name = "PointLight" },
            LightType.Spot => new SpotLight3D { Name = "SpotLight" },
            _ => throw new ArgumentException($"Unknown light type: {type}")
        };
    }
}
