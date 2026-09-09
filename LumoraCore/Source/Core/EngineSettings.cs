// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core;

// Engine-owned user settings. The settings UI writes these; subsystems read
// them directly (mouse look) or the platform layer subscribes to Changed and
// applies what only it can (vsync, window mode, render scale, audio bus).
// Persisted as JSON in the user's application data folder.
public static class EngineSettings
{
    public static event Action? Changed;

    private static bool _dirty;
    private static bool _loaded;

    // INPUT

    private static float _mouseSensitivity = 1f;
    public static float MouseSensitivity
    {
        get => _mouseSensitivity;
        set => SetValue(ref _mouseSensitivity, SettingsCatalog.MouseSensitivity.Clamp(value));
    }

    private static float _mouseSmoothing;
    public static float MouseSmoothing
    {
        get => _mouseSmoothing;
        set => SetValue(ref _mouseSmoothing, SettingsCatalog.MouseSmoothing.Clamp(value));
    }

    // Noclip flight speed in m/s. Only affects the noclip locomotion module.
    private static float _noclipSpeed = 6f;
    public static float NoclipSpeed
    {
        get => _noclipSpeed;
        set => SetValue(ref _noclipSpeed, SettingsCatalog.NoclipSpeed.Clamp(value));
    }

    // Preferred locomotion module by DisplayName ("Walk", "Blink", "Noclip", ...), set from the
    // Settings screen's locomotion picker. Empty means no preference - spawn uses the ordinary first-usable
    // pick. Applied at spawn/world join; a name that no longer matches a usable module (renamed, or denied
    // by the current world's permission gate) just falls through to that default instead of failing.
    private static string _preferredLocomotion = string.Empty;
    public static string PreferredLocomotion
    {
        get => _preferredLocomotion;
        set => SetValue(ref _preferredLocomotion, value ?? string.Empty);
    }

    // AVATAR

    // Calibrated standing/eye height in metres. Drives the avatar auto-rescale (AvatarIK reads
    // InputInterface.UserHeight, which is kept in sync with this). Default 1.75 m. -xlinka
    private static float _userHeight = 1.75f;
    public static float UserHeight
    {
        get => _userHeight;
        set => SetValue(ref _userHeight, SettingsCatalog.UserHeight.Clamp(value));
    }

    // AUDIO

    private static float _masterVolume = 1f;
    public static float MasterVolume
    {
        get => _masterVolume;
        set => SetValue(ref _masterVolume, SettingsCatalog.MasterVolume.Clamp(value));
    }

    // VIDEO

    private static bool _vsync = true;
    public static bool VSync
    {
        get => _vsync;
        set => SetValue(ref _vsync, value);
    }

    private static int _maxFps;
    public static int MaxFps
    {
        get => _maxFps;
        set => SetValue(ref _maxFps, SettingsCatalog.MaxFps.ClampOptionalInt(value));
    }

    // Frame cap applied while the window is unfocused or minimized; 0 = no background throttle. Caps a
    // loop that would otherwise free-run when the compositor stops blocking the swap. Ignored in VR
    // (the headset compositor owns frame timing). Never raises the rate above MaxFps.
    private static int _backgroundFps = 30;
    public static int BackgroundFps
    {
        get => _backgroundFps;
        set => SetValue(ref _backgroundFps, SettingsCatalog.BackgroundFps.ClampOptionalInt(value));
    }

    private static bool _fullscreen;
    public static bool Fullscreen
    {
        get => _fullscreen;
        set => SetValue(ref _fullscreen, value);
    }

    private static float _renderScale = 1f;
    public static float RenderScale
    {
        get => _renderScale;
        set => SetValue(ref _renderScale, SettingsCatalog.RenderScale.Clamp(value));
    }

    // RenderScale becomes a CEILING when this is on: the platform layer lowers the 3D render scale
    // when frames run long and walks it back up when they do not. A 4k window is four times the
    // pixels of 1080p and nothing was adapting, which is the whole "30fps in fullscreen" report.
    // -xlinka
    private static bool _dynamicResolution = true;
    public static bool DynamicResolution
    {
        get => _dynamicResolution;
        set => SetValue(ref _dynamicResolution, value);
    }

    // 0 bilinear, 1 FSR 1, 2 FSR 2. DESKTOP ONLY and deliberately so: both are Forward+ features, our
    // Android/standalone build runs the mobile renderer where they do not exist, and FSR2 in particular
    // is a known open Godot bug in VR (godotengine/godot#89167 - it simply does not improve the image
    // through OpenXR). The XR path keeps bilinear; offering a headset toggle that changes nothing would
    // be a placebo. Only bites below 1.0 scale anyway, which is what dynamic resolution produces. -xlinka
    private static int _upscaler;
    public static int Upscaler
    {
        get => _upscaler;
        set => SetValue(ref _upscaler, SettingsCatalog.Upscaler.ClampInt(value));
    }

    public static string DescribeUpscaler(int value) => value switch
    {
        1 => "FSR 1",
        2 => "FSR 2",
        _ => "Bilinear"
    };

    // Vertical resolution to run at, 0 meaning the display's own. Anything at or above the display is
    // treated as native. What it does depends on the window mode, which is what players expect from
    // this control: WINDOWED resizes the window to that size, FULLSCREEN keeps the window native (a
    // Godot fullscreen window is borderless at desktop resolution) and renders the 3D buffer at that
    // height instead, so the interface stays sharp while the expensive part gets cheaper. -xlinka
    public static readonly int[] ResolutionHeightOptions = { 0, 2160, 1440, 1080, 900, 720 };

    private static int _resolutionHeight;
    public static int ResolutionHeight
    {
        get => _resolutionHeight;
        set => SetValue(ref _resolutionHeight, value < 0 ? 0 : value);
    }

    public static string DescribeResolutionHeight(int value) => value <= 0 ? "Native" : $"{value}p";

    public static readonly int[] TextureSizeOptions = { 0, 2048, 1024, 512, 256 };

    // Longest edge, in pixels, that a URL-loaded texture is allowed to reach; 0 loads it at source
    // resolution. Providers fold this into their variant descriptor, so changing it swaps every
    // texture over to a different variant on the next change pass - no world reload, and textures
    // already resident at the new cap are simply shared rather than reloaded.
    //
    // Snapped to a generated bucket rather than free-form: a cap nobody generated a blob for would
    // silently fall back to the source and quietly do nothing, which is the worst kind of setting.
    private static int _maxTextureSize;
    public static int MaxTextureSize
    {
        get => _maxTextureSize;
        set => SetValue(ref _maxTextureSize, SnapTextureSize(value));
    }

    public static int SnapTextureSize(int value)
    {
        if (value <= 0)
            return 0;
        int best = 0;
        foreach (int option in TextureSizeOptions)
        {
            if (option > 0 && option <= value && option > best)
                best = option;
        }
        // Below the smallest bucket, clamp up to it rather than silently meaning "no cap".
        return best == 0 ? 256 : best;
    }

    public static string DescribeTextureSize(int value) => value <= 0 ? "Original" : $"{value} px";

    // Whether reflection probes render at all. Off leaves every probe in the world alone as data and
    // simply stops the renderer node existing, so glossy surfaces fall back to the sky. This is a real
    // frame-time lever: a probe in Always mode re-renders the scene from six directions.
    private static bool _reflectionsEnabled = true;
    public static bool ReflectionsEnabled
    {
        get => _reflectionsEnabled;
        set => SetValue(ref _reflectionsEnabled, value);
    }

    // There is deliberately no reflection-resolution setting. A probe's face size comes from the
    // renderer's reflection atlas, which is sized once when the renderer starts and has no runtime
    // setter, so a slider for it would move a number that changes nothing until the next launch.
    // On/off is the lever that genuinely exists, and it is a big one. -xlinka

    // Multiplier on every LOD switching distance. Above 1 keeps detailed levels alive further out
    // (prettier, heavier); below 1 drops to cheaper levels sooner. Applied by the LOD hooks when the
    // setting changes, not per frame.
    private static float _lodBias = 1f;
    public static float LodBias
    {
        get => _lodBias;
        set => SetValue(ref _lodBias, SettingsCatalog.LodBias.Clamp(value));
    }

    // Screen-space error, in pixels, the renderer accepts before a mesh drops to a cheaper level of
    // itself. Imported meshes carry a continuous chain of index-reduced levels, so this is trading
    // triangles against a difference measured in fractions of a pixel: nothing vanishes, and the
    // coarsest level holds however far away it gets. 0 pins every mesh to full detail. Applied to the
    // live viewports on change; new viewports inherit the project default. -xlinka
    private static float _meshLodThreshold = 2f;
    public static float MeshLodThreshold
    {
        get => _meshLodThreshold;
        set => SetValue(ref _meshLodThreshold, SettingsCatalog.MeshLodThreshold.ClampOptional(value));
    }

    public static string DescribeMeshLodThreshold(float value) =>
        value <= 0f ? "Full" : $"{value:0.#} px";

    // There is deliberately no network tick rate here. The sync rate is a property of the SESSION, not
    // of the machine looking at it: a guest turning it down does not make the host send less, it just
    // desyncs that guest. It lives on WorldSettings, host-only, and the Session screen edits it. -xlinka

    // COMFORT

    public enum TurnStyle
    {
        Snap,
        Smooth,
    }

    // How the stick turns you. Read live by TurnSubmodule, which every smooth locomotion module owns.
    private static TurnStyle _turnMode = TurnStyle.Snap;
    public static TurnStyle TurnMode
    {
        get => _turnMode;
        set => SetEnum(ref _turnMode, value);
    }

    // Degrees per snap flick. Small angles are smoother but cost more flicks per turn; large ones are
    // the comfort option.
    private static float _snapTurnAngle = 45f;
    public static float SnapTurnAngle
    {
        get => _snapTurnAngle;
        set => SetValue(ref _snapTurnAngle, SettingsCatalog.SnapTurnAngle.Clamp(value));
    }

    // Degrees per second while the stick is held, in Smooth mode.
    private static float _smoothTurnSpeed = 90f;
    public static float SmoothTurnSpeed
    {
        get => _smoothTurnSpeed;
        set => SetValue(ref _smoothTurnSpeed, SettingsCatalog.SmoothTurnSpeed.Clamp(value));
    }

    // INTERFACE

    public enum ReticleShape
    {
        Ring,
        Dot,
        Crosshair,
        Off,
    }

    // Desktop cursor. The platform layer's cursor drawer reads these through InterfaceSettings, which
    // is a thin mirror over this so the Core settings UI can reach it at all (Core cannot see the
    // platform assembly). -xlinka
    private static ReticleShape _reticleStyle = ReticleShape.Ring;
    public static ReticleShape ReticleStyle
    {
        get => _reticleStyle;
        set => SetEnum(ref _reticleStyle, value);
    }

    private static float _reticleSize = 12f;
    public static float ReticleSize
    {
        get => _reticleSize;
        set => SetValue(ref _reticleSize, SettingsCatalog.ReticleSize.Clamp(value));
    }

    private static float _reticleThickness = 2f;
    public static float ReticleThickness
    {
        get => _reticleThickness;
        set => SetValue(ref _reticleThickness, SettingsCatalog.ReticleThickness.Clamp(value));
    }

    // POST PROCESSING
    //
    // These four had no representation at all, and the renderer was running the platform's untouched
    // defaults: a LINEAR tonemapper with a white point of 1.0. That clips every value above 1, which
    // is precisely the range colorHDR exists to carry - the material applicator deliberately keeps
    // emissives in HDR so neon keeps its punch, and then the tonemapper threw the punch away. There
    // was also no bloom, no exposure control and no anti-aliasing anywhere in the product. -xlinka

    public static readonly string[] TonemapOptions = { "Linear", "Reinhard", "Filmic", "ACES", "AgX" };

    // Index into TonemapOptions, which is ALSO the platform's own enum order, so the applier can cast
    // straight across. AgX by default: it holds saturated brights instead of skewing them toward white
    // the way ACES does, and saturated brights are exactly what an avatar's emissives are made of.
    private static int _tonemap = 4;
    public static int Tonemap
    {
        get => _tonemap;
        set => SetValue(ref _tonemap, System.Math.Clamp(value, 0, TonemapOptions.Length - 1));
    }

    public static string DescribeTonemap(int value)
        => value >= 0 && value < TonemapOptions.Length ? TonemapOptions[value] : TonemapOptions[4];

    // White point: the luminance that maps to full white. 1.0 is the platform default and is why
    // everything clips; higher keeps more highlight range before it saturates.
    private static float _exposure = 6f;
    public static float Exposure
    {
        get => _exposure;
        set => SetValue(ref _exposure, SettingsCatalog.Exposure.Clamp(value));
    }

    // Glow intensity. 0 turns the whole glow pass off rather than running it at zero strength, so the
    // cost goes away instead of being paid for nothing.
    private static float _bloom = 0.5f;
    public static float Bloom
    {
        get => _bloom;
        set => SetValue(ref _bloom, SettingsCatalog.Bloom.Clamp(value));
    }

    public static readonly string[] ShadowQualityOptions = { "Hard", "Soft Low", "Soft Medium", "Soft High" };

    // The renderer's soft-shadow filter, which nothing in this engine has ever set - so we have been
    // running on the platform default for every world, on every machine. It is one of the largest
    // single levers on shadow cost: the same geometry measured a 2.1x swing across this range. -xlinka
    private static int _shadowQuality = 1;
    public static int ShadowQuality
    {
        get => _shadowQuality;
        set => SetValue(ref _shadowQuality, System.Math.Clamp(value, 0, ShadowQualityOptions.Length - 1));
    }

    public static string DescribeShadowQuality(int value)
        => value >= 0 && value < ShadowQualityOptions.Length ? ShadowQualityOptions[value] : ShadowQualityOptions[1];

    public static readonly int[] AntiAliasingOptions = { 0, 2, 4, 8 };

    // MSAA samples. Costs fill rate rather than frame logic, and in a headset the difference between
    // 0 and 2 is the difference between crawling edges and a clean image, so it defaults on.
    private static int _antiAliasing = 2;
    public static int AntiAliasing
    {
        get => _antiAliasing;
        set => SetValue(ref _antiAliasing, SnapAntiAliasing(value));
    }

    public static int SnapAntiAliasing(int value)
    {
        int best = 0;
        foreach (int option in AntiAliasingOptions)
        {
            if (option <= value && option > best)
                best = option;
        }
        return best;
    }

    public static string DescribeAntiAliasing(int value) => value <= 0 ? "Off" : $"{value}x MSAA";

    // Multiplier on every directional light's shadow distance. Below 1 shrinks the cascade range,
    // which is the cheapest real win on the shadow pass; above 1 buys distant shadows back. Applied
    // by the light hooks on change, same shape as LodBias.
    private static float _shadowDistanceScale = 1f;
    public static float ShadowDistanceScale
    {
        get => _shadowDistanceScale;
        set => SetValue(ref _shadowDistanceScale, SettingsCatalog.ShadowDistanceScale.Clamp(value));
    }

    // PERSISTENCE - values live-apply for preview; they are written to the shared binary config
    // store (Settings -> config.dat) only on Commit, which the exit screen's "Exit and Save" calls.

    private const string KeyMouseSensitivity = "Engine.Input.MouseSensitivity";
    private const string KeyMouseSmoothing = "Engine.Input.MouseSmoothing";
    private const string KeyNoclipSpeed = "Engine.Input.NoclipSpeed";
    private const string KeyPreferredLocomotion = "Engine.Input.PreferredLocomotion";
    private const string KeyUserHeight = "Engine.Avatar.UserHeight";
    private const string KeyMasterVolume = "Engine.Audio.MasterVolume";
    private const string KeyVSync = "Engine.Video.VSync";
    private const string KeyMaxFps = "Engine.Video.MaxFps";
    private const string KeyBackgroundFps = "Engine.Video.BackgroundFps";
    private const string KeyFullscreen = "Engine.Video.Fullscreen";
    private const string KeyRenderScale = "Engine.Video.RenderScale";
    private const string KeyDynamicResolution = "Engine.Video.DynamicResolution";
    private const string KeyUpscaler = "Engine.Video.Upscaler";
    private const string KeyResolutionHeight = "Engine.Video.ResolutionHeight";
    private const string KeyMaxTextureSize = "Engine.Video.MaxTextureSize";
    private const string KeyReflectionsEnabled = "Engine.Video.ReflectionsEnabled";
    private const string KeyLodBias = "Engine.Video.LodBias";
    private const string KeyMeshLodThreshold = "Engine.Video.MeshLodThreshold";
    private const string KeyShadowDistanceScale = "Engine.Video.ShadowDistanceScale";
    private const string KeyTonemap = "Engine.Video.Tonemap";
    private const string KeyExposure = "Engine.Video.Exposure";
    private const string KeyBloom = "Engine.Video.Bloom";
    private const string KeyAntiAliasing = "Engine.Video.AntiAliasing";
    private const string KeyShadowQuality = "Engine.Video.ShadowQuality";
    private const string KeyTurnMode = "Engine.Comfort.TurnMode";
    private const string KeySnapTurnAngle = "Engine.Comfort.SnapTurnAngle";
    private const string KeySmoothTurnSpeed = "Engine.Comfort.SmoothTurnSpeed";
    private const string KeyReticleStyle = "Engine.Interface.ReticleStyle";
    private const string KeyReticleSize = "Engine.Interface.ReticleSize";
    private const string KeyReticleThickness = "Engine.Interface.ReticleThickness";

    public static void Load()
    {
        if (_loaded)
            return;
        _loaded = true;

        try
        {
            _mouseSensitivity = SettingsCatalog.MouseSensitivity.Clamp(Settings.ReadValue(KeyMouseSensitivity, _mouseSensitivity));
            _mouseSmoothing = SettingsCatalog.MouseSmoothing.Clamp(Settings.ReadValue(KeyMouseSmoothing, _mouseSmoothing));
            _noclipSpeed = SettingsCatalog.NoclipSpeed.Clamp(Settings.ReadValue(KeyNoclipSpeed, _noclipSpeed));
            _preferredLocomotion = Settings.ReadValue(KeyPreferredLocomotion, _preferredLocomotion) ?? string.Empty;
            _userHeight = SettingsCatalog.UserHeight.Clamp(Settings.ReadValue(KeyUserHeight, _userHeight));
            _masterVolume = SettingsCatalog.MasterVolume.Clamp(Settings.ReadValue(KeyMasterVolume, _masterVolume));
            _vsync = Settings.ReadValue(KeyVSync, _vsync);
            int fps = Settings.ReadValue(KeyMaxFps, _maxFps);
            _maxFps = SettingsCatalog.MaxFps.ClampOptionalInt(fps);
            int bgFps = Settings.ReadValue(KeyBackgroundFps, _backgroundFps);
            _backgroundFps = SettingsCatalog.BackgroundFps.ClampOptionalInt(bgFps);
            _fullscreen = Settings.ReadValue(KeyFullscreen, _fullscreen);
            _renderScale = SettingsCatalog.RenderScale.Clamp(Settings.ReadValue(KeyRenderScale, _renderScale));
            _dynamicResolution = Settings.ReadValue(KeyDynamicResolution, _dynamicResolution);
            _upscaler = SettingsCatalog.Upscaler.ClampInt(Settings.ReadValue(KeyUpscaler, _upscaler));
            _resolutionHeight = System.Math.Max(0, Settings.ReadValue(KeyResolutionHeight, _resolutionHeight));
            _maxTextureSize = SnapTextureSize(Settings.ReadValue(KeyMaxTextureSize, _maxTextureSize));
            _reflectionsEnabled = Settings.ReadValue(KeyReflectionsEnabled, _reflectionsEnabled);
            _lodBias = SettingsCatalog.LodBias.Clamp(Settings.ReadValue(KeyLodBias, _lodBias));
            float meshLod = Settings.ReadValue(KeyMeshLodThreshold, _meshLodThreshold);
            _meshLodThreshold = SettingsCatalog.MeshLodThreshold.ClampOptional(meshLod);
            _shadowDistanceScale = SettingsCatalog.ShadowDistanceScale.Clamp(Settings.ReadValue(KeyShadowDistanceScale, _shadowDistanceScale));
            _tonemap = System.Math.Clamp(Settings.ReadValue(KeyTonemap, _tonemap), 0, TonemapOptions.Length - 1);
            _exposure = SettingsCatalog.Exposure.Clamp(Settings.ReadValue(KeyExposure, _exposure));
            _bloom = SettingsCatalog.Bloom.Clamp(Settings.ReadValue(KeyBloom, _bloom));
            _antiAliasing = SnapAntiAliasing(Settings.ReadValue(KeyAntiAliasing, _antiAliasing));
            _shadowQuality = System.Math.Clamp(Settings.ReadValue(KeyShadowQuality, _shadowQuality), 0, ShadowQualityOptions.Length - 1);
            _turnMode = ReadEnum(KeyTurnMode, _turnMode);
            _snapTurnAngle = SettingsCatalog.SnapTurnAngle.Clamp(Settings.ReadValue(KeySnapTurnAngle, _snapTurnAngle));
            _smoothTurnSpeed = SettingsCatalog.SmoothTurnSpeed.Clamp(Settings.ReadValue(KeySmoothTurnSpeed, _smoothTurnSpeed));
            _reticleStyle = ReadEnum(KeyReticleStyle, _reticleStyle);
            _reticleSize = SettingsCatalog.ReticleSize.Clamp(Settings.ReadValue(KeyReticleSize, _reticleSize));
            _reticleThickness = SettingsCatalog.ReticleThickness.Clamp(Settings.ReadValue(KeyReticleThickness, _reticleThickness));
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"EngineSettings: failed to load: {ex.Message}");
        }

        _dirty = false;
    }

    public static bool HasUnsavedChanges => _dirty;

    // Persist current values. Changes apply live for preview but are only saved here -
    // the exit screen's "Exit and Save" calls this.
    public static void Commit()
    {
        try
        {
            Settings.WriteValue(KeyMouseSensitivity, _mouseSensitivity);
            Settings.WriteValue(KeyMouseSmoothing, _mouseSmoothing);
            Settings.WriteValue(KeyNoclipSpeed, _noclipSpeed);
            Settings.WriteValue(KeyPreferredLocomotion, _preferredLocomotion);
            Settings.WriteValue(KeyUserHeight, _userHeight);
            Settings.WriteValue(KeyMasterVolume, _masterVolume);
            Settings.WriteValue(KeyVSync, _vsync);
            Settings.WriteValue(KeyMaxFps, _maxFps);
            Settings.WriteValue(KeyBackgroundFps, _backgroundFps);
            Settings.WriteValue(KeyFullscreen, _fullscreen);
            Settings.WriteValue(KeyRenderScale, _renderScale);
            Settings.WriteValue(KeyDynamicResolution, _dynamicResolution);
            Settings.WriteValue(KeyUpscaler, _upscaler);
            Settings.WriteValue(KeyResolutionHeight, _resolutionHeight);
            Settings.WriteValue(KeyMaxTextureSize, _maxTextureSize);
            Settings.WriteValue(KeyReflectionsEnabled, _reflectionsEnabled);
            Settings.WriteValue(KeyLodBias, _lodBias);
            Settings.WriteValue(KeyMeshLodThreshold, _meshLodThreshold);
            Settings.WriteValue(KeyShadowDistanceScale, _shadowDistanceScale);
            Settings.WriteValue(KeyTonemap, _tonemap);
            Settings.WriteValue(KeyExposure, _exposure);
            Settings.WriteValue(KeyBloom, _bloom);
            Settings.WriteValue(KeyAntiAliasing, _antiAliasing);
            Settings.WriteValue(KeyShadowQuality, _shadowQuality);
            Settings.WriteValue(KeyTurnMode, _turnMode.ToString());
            Settings.WriteValue(KeySnapTurnAngle, _snapTurnAngle);
            Settings.WriteValue(KeySmoothTurnSpeed, _smoothTurnSpeed);
            Settings.WriteValue(KeyReticleStyle, _reticleStyle.ToString());
            Settings.WriteValue(KeyReticleSize, _reticleSize);
            Settings.WriteValue(KeyReticleThickness, _reticleThickness);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"EngineSettings: failed to save: {ex.Message}");
        }

        _dirty = false;
    }

    // Enums persist by NAME: an int would silently re-point at a different option the day someone
    // inserts a value in the middle of the enum.
    private static T ReadEnum<T>(string key, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(Settings.ReadValue(key, fallback.ToString()), ignoreCase: true, out var parsed) ? parsed : fallback;

    private static void SetValue<T>(ref T field, T value) where T : IEquatable<T>
    {
        if (field.Equals(value))
            return;
        field = value;
        _dirty = true;
        Changed?.Invoke();
    }

    // Enums do not satisfy IEquatable<T>, so they need their own gate rather than the generic one.
    private static void SetEnum<T>(ref T field, T value) where T : struct, Enum
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        _dirty = true;
        Changed?.Invoke();
    }
}

