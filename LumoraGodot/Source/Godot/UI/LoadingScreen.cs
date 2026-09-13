// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using System;
using Lumora.Core;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.UI;

// Boot progress UI. The 2D Control covers the desktop window; once the XR viewport is live a
// VrLoadingEnvironment child mirrors the same status and progress into the headset, because OpenXR
// composites only the 3D viewport and a 2D Control never reaches it. Every state change goes through
// SetStatus/SetProgress so both views always agree. -xlinka
public partial class LoadingScreen : Control
{
	// UI NODE REFERENCES
	private Label _statusLabel = null!;
	private Label _percentageLabel = null!;
	private ProgressBar _progressBar = null!;
	private AnimationPlayer _animationPlayer = null!;
	private Control _loadingSpinner = null!;
	private Label _versionLabel = null!;

	private VrLoadingEnvironment? _vrEnvironment;

	// STATE
	private float _targetProgress = 0f;
	private float _currentProgress = 0f;
	private string _status = "Initializing...";
	private bool _isVisible = true;
	private bool _fadeOutQueued = false;

	// CONFIGURATION
	private const float PROGRESS_SMOOTH_SPEED = 2.0f; // How fast progress bar animates
	private const float SPINNER_ROTATION_SPEED = 2.0f; // Radians per second

	public override void _Ready()
	{
		// IMPORTANT: Make immediately visible - no fade in!
		// This prevents the "flash" where screen is empty before animation starts
		Visible = true;
		Modulate = Colors.White; // Full opacity immediately
		_isVisible = true;

		// Cache node references
		_statusLabel = GetNode<Label>("CenterContainer/VBoxContainer/ProgressContainer/StatusLabel");
		_percentageLabel = GetNode<Label>("CenterContainer/VBoxContainer/ProgressContainer/PercentageLabel");
		_progressBar = GetNode<ProgressBar>("CenterContainer/VBoxContainer/ProgressContainer/ProgressBarContainer/ProgressBar");
		_versionLabel = GetNode<Label>("VersionLabel");

		_versionLabel.Text = $"Lumora v{BuildInfo.Version}";
		// Initialize progress
		UpdateProgressDisplay(0f);
	}

	public override void _Process(double delta)
	{
		// Smoothly interpolate progress bar
		if (_currentProgress < _targetProgress)
		{
			_currentProgress = Mathf.MoveToward(_currentProgress, _targetProgress, (float)delta * PROGRESS_SMOOTH_SPEED * 100f);
			UpdateProgressDisplay(_currentProgress);
		}

		// Rotate spinner
		if (_loadingSpinner != null && _isVisible)
		{
			_loadingSpinner.Rotation += (float)delta * SPINNER_ROTATION_SPEED;
		}
	}

	// Grows the headset twin. Called by the runner the moment the XR viewport is configured, which is
	// mid-phase-2, so the twin is seeded with whatever the 2D screen already shows. Returns false and
	// leaves the 2D screen alone when XR is not actually presenting; both may coexist (PCVR shows the
	// Control on the monitor and the environment in the headset). -xlinka
	public bool AttachVrEnvironment(Camera3D? xrCamera, Viewport? xrViewport)
	{
		if (_vrEnvironment != null && GodotObject.IsInstanceValid(_vrEnvironment))
			return true;

		if (xrCamera == null || xrViewport == null)
		{
			LumoraLogger.Warn("LoadingScreen: VR environment skipped - no XR camera or viewport to follow.");
			return false;
		}

		var primary = XRServer.PrimaryInterface;
		if (!xrViewport.UseXR || primary == null || !primary.IsInitialized())
		{
			LumoraLogger.Log($"LoadingScreen: VR environment skipped - XR viewport not presenting (UseXR={xrViewport.UseXR}, primary={(primary == null ? "none" : primary.GetName())}).");
			return false;
		}

		try
		{
			_vrEnvironment = new VrLoadingEnvironment { Name = "VrLoadingEnvironment" };
			_vrEnvironment.Bind(xrCamera, xrViewport);
			_vrEnvironment.SetStatus(_status);
			_vrEnvironment.SetProgress(_targetProgress);
			_vrEnvironment.SnapProgress(_currentProgress);
			AddChild(_vrEnvironment);
			LumoraLogger.Log($"LoadingScreen: VR environment attached to '{xrCamera.Name}' in viewport '{xrViewport.Name}' (UseXR={xrViewport.UseXR}).");
			return true;
		}
		catch (Exception ex)
		{
			LumoraLogger.Warn($"LoadingScreen: VR environment failed to attach ({ex.Message}); headset stays on the bare world.");
			_vrEnvironment = null;
			return false;
		}
	}

	public void SetProgress(float percentage)
	{
		_targetProgress = Mathf.Clamp(percentage, 0f, 100f);
		_vrEnvironment?.SetProgress(_targetProgress);
	}

	public void SetStatus(string status)
	{
		_status = status ?? string.Empty;
		if (_statusLabel != null)
		{
			_statusLabel.Text = _status;
		}
		_vrEnvironment?.SetStatus(_status);
	}

	public void SetPhase(int phaseNumber, int totalPhases, string phaseName)
	{
		// Calculate percentage (each phase is equal weight)
		float phaseProgress = (phaseNumber / (float)totalPhases) * 100f;
		SetProgress(phaseProgress);
		SetStatus($"[{phaseNumber}/{totalPhases}] {phaseName}");
	}

	public new void Hide()
	{
		if (!_isVisible)
			return;

		_isVisible = false;
		Visible = false;
		// Control visibility does not propagate to a Node3D child, and QueueFree lands a frame later;
		// dismiss the headset twin now so the world does not get one frame of dome over it.
		if (_vrEnvironment != null && GodotObject.IsInstanceValid(_vrEnvironment))
			_vrEnvironment.Dismiss();
		_vrEnvironment = null;
		QueueFree(); // Remove immediately since animation player was removed
	}

	public new void Show()
	{
		if (_isVisible)
			return;

		_isVisible = true;
		Visible = true;
	}

	private void UpdateProgressDisplay(float percentage)
	{
		if (_progressBar != null)
		{
			_progressBar.Value = percentage;
		}

		if (_percentageLabel != null)
		{
			_percentageLabel.Text = $"{Mathf.RoundToInt(percentage)}%";
		}
	}

	private void _on_animation_finished(StringName animName)
	{
		// AnimationPlayer removed; keep handler to avoid errors if signal still exists
	}

	// PHASE-SPECIFIC HELPER METHODS

	public static class PhaseMessages
	{
		public const string EnvironmentSetup = "Setting up environment...";
		public const string XRDetection = "Detecting VR hardware...";
		public const string HeadOutputCreation = "Initializing rendering system...";
		public const string EngineCoreInit = "Starting Lumora Engine...";
		public const string SystemIntegration = "Connecting input and audio systems...";
		public const string UserspaceSetup = "Loading user interface...";
		public const string Ready = "Ready!";

		// World synchronization phases
		public const string ConnectingToWorld = "Connecting to world...";
		public const string WaitingForJoinGrant = "Requesting access...";
		public const string DownloadingWorldState = "Downloading world data...";
		public const string InitializingWorld = "Initializing world...";
		public const string WorldReady = "World ready!";
	}

	public void UpdatePhase(int phaseIndex, string customMessage = null!)
	{
		string[] defaultMessages = new[]
		{
			PhaseMessages.EnvironmentSetup,
			PhaseMessages.XRDetection,
			PhaseMessages.HeadOutputCreation,
			PhaseMessages.EngineCoreInit,
			PhaseMessages.SystemIntegration,
			PhaseMessages.UserspaceSetup,
			PhaseMessages.Ready
		};

		int totalPhases = 6; // Not counting "Ready" as a phase
		string message = customMessage ?? (phaseIndex < defaultMessages.Length ? defaultMessages[phaseIndex] : "Initializing...");

		if (phaseIndex >= totalPhases)
		{
			// Final phase - set to 100%
			SetProgress(100f);
			SetStatus(message);
		}
		else
		{
			// Regular phase update
			SetPhase(phaseIndex + 1, totalPhases, message);
		}
	}
}
