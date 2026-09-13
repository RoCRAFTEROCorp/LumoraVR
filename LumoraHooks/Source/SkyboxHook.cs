// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Turns a Skybox into the scene's environment: a cubemap sky plus the ambient and reflection source
// flags.
//
// The hook never decides whether it is the world's sky - the component already resolved that from
// synced state - and it never writes the WorldEnvironment node directly. It builds an Environment
// and hands it to SkyEnvironment, which is the only thing allowed to install one. While this Skybox
// is not the active one it withdraws its claim, so the environment it built sits idle rather than
// fighting for the node. -xlinka
[ImplementableHook(typeof(Skybox))]
public sealed class SkyboxHook : ComponentHook<Skybox>
{
    private const string ShaderPath = "res://Shaders/Sky_Cubemap.gdshader";

    private global::Godot.Environment _environment = null!;
    private Sky _sky = null!;
    private ShaderMaterial _skyMaterial = null!;
    private bool _claimed;
    private bool _shaderMissingLogged;

    public static IHook<Skybox> Constructor() => new SkyboxHook();

    public override void Initialize()
    {
        base.Initialize();

        // Start from the bootstrap environment so the project's fog, tonemapping and post settings
        // survive; only the sky and the ambient/reflection sources are ours to set.
        var bootstrap = SkyEnvironment.Bootstrap(attachedNode);
        _environment = bootstrap?.Duplicate() as global::Godot.Environment ?? new global::Godot.Environment();

        _skyMaterial = new ShaderMaterial();
        if (ResourceLoader.Exists(ShaderPath))
            _skyMaterial.Shader = GD.Load<Shader>(ShaderPath);
        else
            LumoraLogger.Warn($"SkyboxHook: sky shader not found at {ShaderPath}");

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

        ApplyChanges();
    }

    public override void ApplyChanges()
    {
        if (_environment == null || _skyMaterial == null)
            return;

        ApplyCubemap();

        _skyMaterial.SetShaderParameter("sky_rotation", Owner.Rotation.Value * (System.MathF.PI / 180f));
        _skyMaterial.SetShaderParameter("sky_exposure", Owner.Exposure.Value);

        // Sky ambient reads the sky texture itself, so it follows the cubemap for free. The flat
        // fallback is a separate source in Godot, which is why this is a switch and not a blend.
        if (Owner.AmbientFromSky.Value)
        {
            _environment.AmbientLightSource = global::Godot.Environment.AmbientSource.Sky;
        }
        else
        {
            var ambient = Owner.AmbientColor.Value;
            _environment.AmbientLightSource = global::Godot.Environment.AmbientSource.Color;
            _environment.AmbientLightColor = new Color(ambient.r, ambient.g, ambient.b, ambient.a);
        }
        _environment.AmbientLightEnergy = Owner.AmbientEnergy.Value;

        _environment.ReflectedLightSource = Owner.ReflectionsFromSky.Value
            ? global::Godot.Environment.ReflectionSource.Sky
            : global::Godot.Environment.ReflectionSource.Disabled;

        UpdateClaim();
    }

    private void ApplyCubemap()
    {
        var asset = Owner.Cubemap.Asset;
        var texture = (asset?.Hook as CubemapAssetHook)?.GodotCubemap;

        // A null uniform is a black sky, which is exactly right while the asset is still loading: the
        // asset reports itself loaded only after its GPU cubemap exists, and that reference change
        // brings us straight back here.
        _skyMaterial.SetShaderParameter("sky_cubemap", texture!);

        if (_shaderMissingLogged || _skyMaterial.Shader != null)
            return;
        _shaderMissingLogged = true;
        LumoraLogger.Warn("SkyboxHook: no sky shader loaded; the sky will not render");
    }

    private void UpdateClaim()
    {
        bool wants = Owner.IsActiveSkybox && Owner.Enabled;
        if (wants)
        {
            _claimed = true;
            SkyEnvironment.Claimed(this, Owner.World, SkyEnvironment.SkyboxPriority, _environment, attachedNode);
        }
        else if (_claimed)
        {
            _claimed = false;
            SkyEnvironment.Released(this);
        }
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
