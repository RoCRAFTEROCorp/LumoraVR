// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Draws a gradient sky with a procedural sun. Worlds are not unloaded when the user switches between
// them, so this hook outlives focus changes; it registers a claim with SkyEnvironment and that arbiter
// decides when the claim is showing. It claims at the lower priority, so a Skybox in the same world
// takes the sky over. - xlinka
[ImplementableHook(typeof(GradientSkybox))]
public sealed class GradientSkyboxHook : ComponentHook<GradientSkybox>
{
    private const string ShaderPath = "res://Shaders/GradientSkybox.gdshader";

    private global::Godot.Environment _environment = null!;
    private Sky _sky = null!;
    private ShaderMaterial _skyMaterial = null!;
    private bool _claimed;

    public static IHook<GradientSkybox> Constructor() => new GradientSkyboxHook();

    public override void Initialize()
    {
        base.Initialize();

        // Duplicate the bootstrap environment so the project's fog, tonemapping and post settings
        // survive; only the sky and ambient are ours to set.
        var bootstrap = SkyEnvironment.Bootstrap(attachedNode);
        _environment = bootstrap?.Duplicate() as global::Godot.Environment ?? new global::Godot.Environment();

        _skyMaterial = new ShaderMaterial();
        if (ResourceLoader.Exists(ShaderPath))
        {
            _skyMaterial.Shader = GD.Load<Shader>(ShaderPath);
        }
        else
        {
            LumoraLogger.Warn($"GradientSkyboxHook: Sky shader not found at {ShaderPath}");
        }

        _sky = new Sky { SkyMaterial = _skyMaterial };

        // The radiance map is rendered ONCE, at a size a mobile GPU can afford.
        //
        // Left on Automatic with a custom ShaderMaterial, Godot cannot prove the sky is static and
        // falls to real-time: six cubemap faces plus a full mip chain, every frame. On a Pico 4 that
        // showed up as 270 ms of GPU per frame with SIX visible draw calls in the scene, unchanged by
        // turning off glow and by halving the draw count. Quality mode re-renders only when a uniform
        // changes, which is when the world sets the sky up, and 64 is plenty for the ambient term
        // since reflections from this sky are killed below anyway. Desktop keeps the defaults. -xlinka
        if (OS.HasFeature("android"))
        {
            _sky.ProcessMode = Sky.ProcessModeEnum.Quality;
            _sky.RadianceSize = Sky.RadianceSizeEnum.Size64;
        }
        _environment.BackgroundMode = global::Godot.Environment.BGMode.Sky;
        _environment.Sky = _sky;
        // Sky still drives ambient color, but reflections are explicitly killed so the bright sun disc
        // inside the shader doesn't mirror onto glossy floors as a hard hotspot. Specular highlights
        // come only from real lights and reflection probes, not from this sky. - xlinka
        _environment.AmbientLightSource = global::Godot.Environment.AmbientSource.Sky;
        _environment.ReflectedLightSource = global::Godot.Environment.ReflectionSource.Disabled;

        ApplyChanges();
    }

    public override void ApplyChanges()
    {
        if (_environment == null || _skyMaterial == null)
        {
            return;
        }

        SetColor("top_color", Owner.TopColor.Value);
        SetColor("horizon_color", Owner.HorizonColor.Value);
        SetColor("bottom_color", Owner.BottomColor.Value);
        SetColor("sun_color", Owner.SunColor.Value);

        var sunDirection = Owner.SunDirection.Value;
        _skyMaterial.SetShaderParameter("sun_direction", new Vector3(sunDirection.x, sunDirection.y, sunDirection.z));
        _skyMaterial.SetShaderParameter("sun_size", Owner.SunSize.Value);
        _skyMaterial.SetShaderParameter("sun_intensity", Owner.SunIntensity.Value);
        _skyMaterial.SetShaderParameter("sun_glow_power", Owner.SunGlowPower.Value);

        _environment.AmbientLightEnergy = Owner.AmbientEnergy.Value;

        UpdateClaim();
    }

    private void UpdateClaim()
    {
        if (Owner.Enabled)
        {
            _claimed = true;
            SkyEnvironment.Claimed(this, Owner.World, SkyEnvironment.GradientPriority, _environment, attachedNode);
        }
        else if (_claimed)
        {
            _claimed = false;
            SkyEnvironment.Released(this);
        }
    }

    private void SetColor(string uniform, color value)
    {
        _skyMaterial.SetShaderParameter(uniform, new Color(value.r, value.g, value.b, value.a));
    }

    public override void Destroy(bool destroyingWorld)
    {
        SkyEnvironment.Released(this);
        _claimed = false;

        _environment?.Dispose();
        _sky?.Dispose();
        _skyMaterial?.Dispose();

        _environment = null!;
        _sky = null!;
        _skyMaterial = null!;

        SkyEnvironment.ReleaseNodeIfUnused();
        base.Destroy(destroyingWorld);
    }
}
