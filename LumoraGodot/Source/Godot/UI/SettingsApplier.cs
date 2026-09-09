// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Source.Godot.Bootstrap;

namespace Lumora.Source.UI;

// Applies the engine-owned settings the platform layer is responsible for: vsync, frame cap, window
// mode, 3D render scale and the master audio bus. Changes apply live for preview; persistence is
// explicit (the settings screen's Save / the exit dialog commit via EngineSettings.Commit).
public partial class SettingsApplier : Node
{
	public override void _Ready()
	{
		base._Ready();
		EngineSettings.Load();
		EngineSettings.Changed += Apply;
		XRModeManager.ModeChanged += OnXrModeChanged;
		Apply();
	}

	public override void _ExitTree()
	{
		EngineSettings.Changed -= Apply;
		XRModeManager.ModeChanged -= OnXrModeChanged;
		base._ExitTree();
	}

	// Differential apply: Changed fires for every settings write (including each
	// slider tick), so only touch the subsystems whose value actually changed -
	// reassigning render scale or vsync per tick stalls the renderer.
	private int? _appliedMaxFps;
	private bool? _appliedFullscreen;
	private float? _appliedRenderScale;
	private float? _appliedMeshLodThreshold;
	private bool? _appliedDynamicResolution;
	private int? _appliedUpscaler;
	private int? _appliedResolutionHeight;
	// What the chosen resolution costs the 3D buffer, 1.0 at native. Multiplied into the render-scale
	// ceiling so it composes with the user's Render Scale and with dynamic resolution instead of
	// fighting them.
	private float _resolutionScale = 1f;
	private float? _appliedMasterVolume;
	private float? _appliedUserHeight;
	private int? _appliedBackgroundFps;
	private int? _appliedTonemap;
	private float? _appliedExposure;
	private float? _appliedBloom;
	private int? _appliedAntiAliasing;
	private int? _appliedShadowQuality;

	// When the window loses focus or is minimized the compositor stops blocking the swap, so vsync no longer throttles
	// us and the loop free-runs (the FPS graph spikes way past the vsync rate, burning GPU for a window nobody sees).
	// Engine.MaxFps is enforced regardless of focus or vsync, so while unfocused we clamp to the user's background-fps
	// setting and restore their normal limit when focus returns. -xlinka
	private bool _background;

	// --Lumora-Debug opens the console as a second window, and that is the window a person profiling
	// is looking at, so the game window sits unfocused for most of a capture. With the background cap
	// in force every such capture measured the frame limiter, not the frame: in the first clean
	// scratch-space profile 116 of 365 samples were exactly 33.33 ms, the cap's 30 fps, on a 74 Hz
	// display, and the console booked that sleep as time Godot spent. A profiling run keeps the
	// foreground rate whether or not the window has focus. Same for a frame-log run. -xlinka
	private static readonly bool ProfilingRun =
		LaunchArgs.HasFlag(LaunchArgs.DebugFlag) || LaunchArgs.ReadValue(FrameTimeLog.PathFlag) != null;
	private bool _backgroundThrottled;

	// VSYNC
	//
	// EngineSettings.VSync is the user's on/off. How the swap waits when it is on is a second choice,
	// and it decides the frame rate of every frame that cannot make the refresh interval. FIFO, Godot's
	// "Enabled", blocks the whole main loop until the next vertical blank, so a frame that misses one
	// interval pays for a full second one: 21 ms of work on a 74 Hz display becomes a 27 ms frame, on
	// 60 Hz a 33 ms one. Mailbox keeps vsync, no tearing, but lets the loop run at its real cost and
	// scans out the newest finished frame at each blank. Adaptive tears instead of waiting when a
	// frame is late. The typed settings live in the core, so the choice is read from the generic
	// store under Engine.Video.VSyncMode ("fifo", "mailbox", "adaptive"); a missing key is fifo,
	// exactly what shipped. --Lumora-VSync=<off|on|fifo|mailbox|adaptive> overrides the lot for one
	// run, for measurements. Vulkan falls back to FIFO on a surface that cannot do the mode asked for.
	//
	// A headset owns frame timing while it renders: the OpenXR frame wait paces the loop and a
	// blocking desktop swap on top of it would hold the headset to the monitor's refresh. The XR
	// bring-up disables vsync for that reason, and this used to put it straight back on at boot
	// because it only compared against its own cache, so a VR boot ran with the mirror window
	// vsynced. In VR the answer is always off; after F8 back to desktop the user's choice applies
	// again. -xlinka
	private const string VSyncModeKey = "Engine.Video.VSyncMode";
	private const string VSyncOverrideFlag = "--Lumora-VSync";
	private static readonly string? VSyncOverride = LaunchArgs.ReadValue(VSyncOverrideFlag);
	private DisplayServer.VSyncMode? _appliedVsyncMode;

	private void Apply()
	{
		ApplyVSync();

		if (_appliedMaxFps != EngineSettings.MaxFps || _appliedBackgroundFps != EngineSettings.BackgroundFps)
		{
			_appliedMaxFps = EngineSettings.MaxFps;
			_appliedBackgroundFps = EngineSettings.BackgroundFps;
			ApplyEffectiveFps();
			Lumora.Core.Logging.Logger.Log($"Settings: fps limit -> {(EngineSettings.MaxFps == 0 ? "off" : EngineSettings.MaxFps.ToString())} (background {(EngineSettings.BackgroundFps == 0 ? "off" : EngineSettings.BackgroundFps.ToString())})");
		}

		if (_appliedFullscreen != EngineSettings.Fullscreen)
		{
			_appliedFullscreen = EngineSettings.Fullscreen;
			DisplayServer.WindowSetMode(EngineSettings.Fullscreen
				? DisplayServer.WindowMode.Fullscreen
				: DisplayServer.WindowMode.Windowed);
			ApplyResolution();
		}

		if (_appliedResolutionHeight != EngineSettings.ResolutionHeight)
		{
			_appliedResolutionHeight = EngineSettings.ResolutionHeight;
			ApplyResolution();
			Lumora.Core.Logging.Logger.Log($"Settings: resolution -> {EngineSettings.DescribeResolutionHeight(EngineSettings.ResolutionHeight)}");
		}

		if (_appliedRenderScale != EngineSettings.RenderScale)
		{
			_appliedRenderScale = EngineSettings.RenderScale;
			ApplyRenderScaleCeiling();
		}

		if (_appliedDynamicResolution != EngineSettings.DynamicResolution)
		{
			_appliedDynamicResolution = EngineSettings.DynamicResolution;
			// Back to exactly what the user asked for the moment it is switched off.
			ApplyRenderScaleCeiling();
			Lumora.Core.Logging.Logger.Log($"Settings: dynamic resolution -> {(EngineSettings.DynamicResolution ? "on" : "off")}");
		}

		if (_appliedUpscaler != EngineSettings.Upscaler)
		{
			_appliedUpscaler = EngineSettings.Upscaler;
			ApplyUpscaler();
			Lumora.Core.Logging.Logger.Log($"Settings: upscaler -> {EngineSettings.DescribeUpscaler(EngineSettings.Upscaler)}");
		}

		// Tonemap, white point and glow all live on the same Environment, so one changed value rewrites
		// the set rather than three separate walks over every claimed environment.
		if (_appliedTonemap != EngineSettings.Tonemap
			|| _appliedExposure != EngineSettings.Exposure
			|| _appliedBloom != EngineSettings.Bloom)
		{
			_appliedTonemap = EngineSettings.Tonemap;
			_appliedExposure = EngineSettings.Exposure;
			_appliedBloom = EngineSettings.Bloom;
			Lumora.Godot.Hooks.SkyEnvironment.ApplyPostSettings(
				EngineSettings.Tonemap, EngineSettings.Exposure, EngineSettings.Bloom, this);
			Lumora.Core.Logging.Logger.Log(
				$"Settings: tonemap -> {EngineSettings.DescribeTonemap(EngineSettings.Tonemap)} "
				+ $"(white {EngineSettings.Exposure:0.#}, bloom {EngineSettings.Bloom:0.##})");
		}

		if (_appliedAntiAliasing != EngineSettings.AntiAliasing)
		{
			_appliedAntiAliasing = EngineSettings.AntiAliasing;
			var viewport = GetViewport();
			if (viewport != null)
			{
				viewport.Msaa3D = EngineSettings.AntiAliasing switch
				{
					>= 8 => Viewport.Msaa.Msaa8X,
					>= 4 => Viewport.Msaa.Msaa4X,
					>= 2 => Viewport.Msaa.Msaa2X,
					_ => Viewport.Msaa.Disabled,
				};
			}
			Lumora.Core.Logging.Logger.Log($"Settings: anti-aliasing -> {EngineSettings.DescribeAntiAliasing(EngineSettings.AntiAliasing)}");
		}

		if (_appliedShadowQuality != EngineSettings.ShadowQuality)
		{
			_appliedShadowQuality = EngineSettings.ShadowQuality;
			var quality = EngineSettings.ShadowQuality switch
			{
				0 => RenderingServer.ShadowQuality.Hard,
				2 => RenderingServer.ShadowQuality.SoftMedium,
				3 => RenderingServer.ShadowQuality.SoftHigh,
				_ => RenderingServer.ShadowQuality.SoftLow,
			};
			RenderingServer.PositionalSoftShadowFilterSetQuality(quality);
			RenderingServer.DirectionalSoftShadowFilterSetQuality(quality);
			Lumora.Core.Logging.Logger.Log($"Settings: shadow quality -> {EngineSettings.DescribeShadowQuality(EngineSettings.ShadowQuality)}");
		}

		if (_appliedMeshLodThreshold != EngineSettings.MeshLodThreshold)
		{
			_appliedMeshLodThreshold = EngineSettings.MeshLodThreshold;
			ApplyMeshLodThreshold(GetTree()?.Root, EngineSettings.MeshLodThreshold);
			Lumora.Core.Logging.Logger.Log($"Settings: mesh LOD threshold -> {EngineSettings.DescribeMeshLodThreshold(EngineSettings.MeshLodThreshold)}");
		}

		if (_appliedMasterVolume != EngineSettings.MasterVolume)
		{
			_appliedMasterVolume = EngineSettings.MasterVolume;
			int masterBus = AudioServer.GetBusIndex("Master");
			if (masterBus >= 0)
			{
				AudioServer.SetBusVolumeDb(masterBus, Mathf.LinearToDb(Mathf.Max(EngineSettings.MasterVolume, 0.0001f)));
				AudioServer.SetBusMute(masterBus, EngineSettings.MasterVolume <= 0f);
			}
		}

		if (_appliedUserHeight != EngineSettings.UserHeight)
		{
			_appliedUserHeight = EngineSettings.UserHeight;
			// Feed the calibrated height to the input layer; AvatarIK.MaybeRescaleAvatar reads it and re-scales the
			// avatar so its eye height matches (live). -xlinka
			var input = Lumora.Core.Engine.Current?.InputInterface;
			if (input != null)
				input.UserHeight = EngineSettings.UserHeight;
		}
	}

	private void OnXrModeChanged(bool vrActive)
	{
		ApplyVSync();
		ApplyEffectiveFps();
	}

	private static DisplayServer.VSyncMode ResolveVSyncMode()
	{
		if (HeadsetOwnsFrameTiming())
			return DisplayServer.VSyncMode.Disabled;

		string choice = VSyncOverride
			?? (EngineSettings.VSync ? Settings.ReadValue<string>(VSyncModeKey, "fifo") : "off");

		return (choice ?? "fifo").Trim().ToLowerInvariant() switch
		{
			"off" or "disabled" or "0" or "false" => DisplayServer.VSyncMode.Disabled,
			"mailbox" => DisplayServer.VSyncMode.Mailbox,
			"adaptive" => DisplayServer.VSyncMode.Adaptive,
			_ => DisplayServer.VSyncMode.Enabled,
		};
	}

	// Compared against what was last asked for, not what the window reports: a mode the surface
	// cannot do falls back to FIFO inside Godot, and comparing against the window would then re-ask
	// for it (a swapchain rebuild) on every settings write.
	private void ApplyVSync()
	{
		var mode = ResolveVSyncMode();
		if (_appliedVsyncMode == mode)
			return;
		_appliedVsyncMode = mode;

		if (DisplayServer.WindowGetVsyncMode() != mode)
			DisplayServer.WindowSetVsyncMode(mode);

		Lumora.Core.Logging.Logger.Log($"Settings: vsync -> {DescribeVSync(mode)}"
			+ (VSyncOverride != null ? $" ({VSyncOverrideFlag}={VSyncOverride})" : string.Empty)
			+ (HeadsetOwnsFrameTiming() ? " (headset paces the frame)" : string.Empty));
	}

	private static string DescribeVSync(DisplayServer.VSyncMode mode) => mode switch
	{
		DisplayServer.VSyncMode.Disabled => "off",
		DisplayServer.VSyncMode.Mailbox => "on (mailbox)",
		DisplayServer.VSyncMode.Adaptive => "on (adaptive)",
		_ => "on",
	};

	// The headset is rendering (VR mode active), not merely holding a session: after F8 to desktop the
	// OpenXR session stays up but no viewport draws through it, so the monitor's swap is the only
	// pacing left and the user's vsync choice applies again.
	private static bool HeadsetOwnsFrameTiming()
	{
		var manager = XRModeManager.Instance;
		if (manager != null && GodotObject.IsInstanceValid(manager))
			return manager.IsVRActive;
		return IsVrActive();
	}

	// RESOLUTION
	//
	// Windowed, this resizes the window, which is what picking 1080p in any other game does. Fullscreen,
	// the window stays at the desktop resolution (Godot's fullscreen is borderless, it does not take the
	// display mode) and the 3D buffer renders at the chosen height instead. That is the half that costs
	// the framerate on a 4k screen, and it leaves the interface rendering at native so text stays sharp
	// rather than being upscaled with the world. -xlinka
	private void ApplyResolution()
	{
		int height = EngineSettings.ResolutionHeight;
		var screen = DisplayServer.ScreenGetSize(DisplayServer.WindowGetCurrentScreen());
		if (screen.Y <= 0)
			return;

		if (height <= 0 || height >= screen.Y)
		{
			_resolutionScale = 1f;
			ApplyRenderScaleCeiling();
			return;
		}

		if (EngineSettings.Fullscreen)
		{
			_resolutionScale = (float)height / screen.Y;
		}
		else
		{
			_resolutionScale = 1f;
			int width = (int)System.Math.Round(height * ((float)screen.X / screen.Y));
			DisplayServer.WindowSetSize(new Vector2I(System.Math.Max(width, 640), System.Math.Max(height, 480)));
		}

		ApplyRenderScaleCeiling();
	}

	// The ceiling everything else works under: the user's Render Scale, narrowed by whatever the chosen
	// resolution asks for. Dynamic resolution rides below this; with it off, this IS the scale.
	private float RenderScaleCeiling => EngineSettings.RenderScale * _resolutionScale;

	private void ApplyRenderScaleCeiling()
	{
		_dynamicScale = RenderScaleCeiling;
		var root = GetTree()?.Root;
		if (root != null && XRServer.PrimaryInterface?.IsInitialized() != true)
			root.Scaling3DScale = _dynamicScale;
	}

	// FSR is a Forward+ feature and it only does anything below 1.0 scale, which is exactly what the
	// controller below produces on a big window. XR stays on bilinear: the mobile renderer our
	// standalone build uses has neither mode, and FSR2 through OpenXR is a known Godot bug where the
	// image simply does not improve, so a headset toggle would be a lie. -xlinka
	private void ApplyUpscaler()
	{
		var root = GetTree()?.Root;
		if (root == null || XRServer.PrimaryInterface?.IsInitialized() == true)
			return;

		root.Scaling3DMode = EngineSettings.Upscaler switch
		{
			1 => Viewport.Scaling3DModeEnum.Fsr,
			2 => Viewport.Scaling3DModeEnum.Fsr2,
			_ => Viewport.Scaling3DModeEnum.Bilinear
		};
	}

	// ADAPTIVE RESOLUTION
	//
	// Nothing scaled with the window before this: Scaling3DScale was written once from the user's
	// setting, so a 4k fullscreen window rendered four times the pixels of 1080p and simply ran at
	// whatever framerate that cost. Now the user's RenderScale is the CEILING and this walks the
	// actual scale down when frames run long, back up when they do not.
	//
	// Falls fast and rises slow on purpose: dropping resolution the frame after a stall is what keeps
	// the number up, while creeping back adds pixels only once the headroom has held for a while, so
	// the picture does not pulse. The floor is deliberately not lower than half, past which the image
	// is worse than the framerate is worth. -xlinka
	private const float ScaleFloor = 0.5f;
	private const float ScaleFallStep = 0.05f;
	private const float ScaleRiseStep = 0.02f;
	private const double DecideInterval = 0.4;

	private float _dynamicScale = 1f;
	private double _smoothedFrame;
	private double _sinceDecision;
	private bool _measuring;

	public override void _Process(double delta)
	{
		base._Process(delta);

		if (!EngineSettings.DynamicResolution || _background)
			return;

		// XR owns its own render target scale (see ViewportQuality); do not fight it.
		if (XRServer.PrimaryInterface?.IsInitialized() == true)
			return;

		var root = GetTree()?.Root;
		if (root == null || delta <= 0.0)
			return;

		// MEASURED render time, not the frame delta. With vsync on, delta sits exactly on the refresh
		// interval whether we are coasting or barely keeping up, so a delta-driven controller can never
		// see headroom and would ratchet the resolution down and leave it there. The viewport reports
		// what the frame actually cost. -xlinka
		var viewportRid = root.GetViewportRid();
		if (!_measuring)
		{
			RenderingServer.ViewportSetMeasureRenderTime(viewportRid, true);
			_measuring = true;
			return;
		}

		double cost = System.Math.Max(
			RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewportRid),
			RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewportRid)) / 1000.0;
		if (cost <= 0.0)
			return;   // nothing measured yet, or the driver will not report it: leave the scale alone

		_smoothedFrame = _smoothedFrame <= 0.0 ? cost : _smoothedFrame + (cost - _smoothedFrame) * 0.1;

		_sinceDecision += delta;
		if (_sinceDecision < DecideInterval)
			return;
		_sinceDecision = 0.0;

		// What we are trying to hit: the user's frame cap if they set one, otherwise the display's own
		// refresh rate, which is the rate vsync is going to hold us to anyway.
		double targetFps = EngineSettings.MaxFps > 0 ? EngineSettings.MaxFps : ScreenRefreshRate();
		if (targetFps < 30.0)
			targetFps = 30.0;
		double target = 1.0 / targetFps;

		float ceiling = RenderScaleCeiling;
		float previous = _dynamicScale;

		// Rendering is not the only thing in a frame, so aim it at most of the budget rather than all of
		// it, and only add pixels back when it is comfortably under.
		if (_smoothedFrame > target * 0.85)
			_dynamicScale -= ScaleFallStep;
		else if (_smoothedFrame < target * 0.60)
			_dynamicScale += ScaleRiseStep;

		if (_dynamicScale < ScaleFloor) _dynamicScale = ScaleFloor;
		if (_dynamicScale > ceiling) _dynamicScale = ceiling;

		if (Mathf.Abs(_dynamicScale - previous) > 0.001f)
			root.Scaling3DScale = _dynamicScale;
	}

	private static double ScreenRefreshRate()
	{
		float hz = DisplayServer.ScreenGetRefreshRate(DisplayServer.WindowGetCurrentScreen());
		// The server answers -1 on displays it cannot read.
		return hz > 1f ? hz : 60.0;
	}

	public override void _Notification(int what)
	{
		base._Notification(what);
		switch (what)
		{
			// Both the application-level and per-window focus signals fire on desktop; the transition guard in
			// SetBackground makes the duplicate harmless and covers platforms that only emit one of them. -xlinka
			case (int)NotificationApplicationFocusOut:
			case (int)NotificationWMWindowFocusOut:
				SetBackground(true);
				break;
			case (int)NotificationApplicationFocusIn:
			case (int)NotificationWMWindowFocusIn:
				SetBackground(false);
				break;
		}
	}

	private void SetBackground(bool background)
	{
		if (_background == background)
		{
			return;
		}

		_background = background;
		ApplyEffectiveFps();
	}

	// Resolves Engine.MaxFps from the user's cap and the current focus state. While unfocused (and not in VR, where the
	// headset compositor owns frame timing) we clamp to the user's background-fps setting; a background setting of 0
	// disables the throttle. We never raise the rate above the user's own cap - a cap of 0 means "unlimited
	// (vsync-throttled)", which is exactly the case the background clamp rescues. -xlinka
	//
	// The transitions are logged: a capture that ran at the background rate should say so in the log
	// next to the settings it ran with, instead of leaving a 30 fps mystery for the profiler.
	private void ApplyEffectiveFps()
	{
		int userCap = EngineSettings.MaxFps;
		int effective = userCap;
		bool throttled = false;

		if (_background && !IsVrActive() && !ProfilingRun)
		{
			int backgroundCap = EngineSettings.BackgroundFps;
			if (backgroundCap > 0)
			{
				effective = userCap == 0 ? backgroundCap : System.Math.Min(userCap, backgroundCap);
				throttled = effective != userCap;
			}
		}

		global::Godot.Engine.MaxFps = effective;

		if (throttled != _backgroundThrottled)
		{
			_backgroundThrottled = throttled;
			Lumora.Core.Logging.Logger.Log(throttled
				? $"Settings: window unfocused, fps capped to {effective} until focus returns"
				: "Settings: window focused, background fps cap lifted");
		}
		else if (_background && !throttled && ProfilingRun && EngineSettings.BackgroundFps > 0 && !IsVrActive())
		{
			Lumora.Core.Logging.Logger.Log("Settings: window unfocused, background fps cap suspended for the profiling run");
		}
	}

	// The threshold is a per-viewport property, and the XR sub-viewport is a different one from the
	// window's. Walk what is in the tree rather than naming them, so a mirror or capture viewport that
	// renders 3D gets the same setting instead of quietly staying at the project default. Viewports
	// created after this runs inherit that project default until the next change. -xlinka
	private static void ApplyMeshLodThreshold(Node? node, float threshold)
	{
		if (node == null)
			return;

		if (node is Viewport viewport)
			viewport.MeshLodThreshold = threshold;

		foreach (var child in node.GetChildren())
			ApplyMeshLodThreshold(child, threshold);
	}

	private static bool IsVrActive()
		=> XRServer.FindInterface("OpenXR")?.IsInitialized() == true;
}
