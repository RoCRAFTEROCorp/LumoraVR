// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.UI;

// The headset twin of LoadingScreen. OpenXR composites only the 3D viewport, so the 2D Control never
// reaches a Pico or Quest and the whole boot was a black void with no way to tell "loading" from "dead".
// This builds a dome around the head plus a progress panel out of plain Godot nodes, reads the same
// status/progress the 2D screen shows, and is freed with it.
//
// Rendering model: every piece lives in the transparent pass with the depth test off and a fixed
// render_priority ladder (dome lowest, labels highest). That makes it a true overlay: it draws over
// whatever the shared World3D already holds, in a deterministic order, without touching any camera's
// cull mask or the Environment. Nothing here needs Forward+; it is unshaded quads, one inverted
// sphere and Label3D. -xlinka
public partial class VrLoadingEnvironment : Node3D
{
	private const string DomeShaderPath = "res://Shaders/LoadingDome.gdshader";
	private const string FontPath = "res://Assets/Fonts/FiraCode/FiraCode-SemiBold.ttf";

	private const float DomeRadius = 30f;
	private const float PanelDistance = 1.4f;
	private const float PanelWidth = 0.8f;
	private const float PanelHeight = 0.4f;
	private const float PanelDrop = -0.06f;
	private const float BarWidth = 0.62f;
	private const float BarHeight = 0.028f;
	private const float SweepWidth = 0.07f;
	private const float SweepPeriodSec = 1.8f;
	private const float YawFollowRate = 2.5f;
	private const float ProgressSmoothSpeed = 2.0f;
	private const float LabelPixelSize = 0.0005f;

	// Transparent-pass ladder; higher draws later. Range is -128..127.
	private const int PriorityDome = -120;
	private const int PriorityBackdrop = -110;
	private const int PriorityBarBorder = -100;
	private const int PriorityBarTrack = -95;
	private const int PriorityBarFill = -90;
	private const int PrioritySweep = -85;
	private const int PriorityLabel = -80;

	private static readonly Color BackdropColor = new(0.05f, 0.04f, 0.10f, 0.94f);
	private static readonly Color AccentColor = new(0.47f, 0.37f, 0.94f);
	private static readonly Color BarBorderColor = new(0.227f, 0.185f, 0.421f);
	private static readonly Color BarTrackColor = new(0.10f, 0.08f, 0.20f);
	private static readonly Color SweepColor = new(1f, 1f, 1f, 0.28f);
	private static readonly Color StatusColor = new(0.8f, 0.8f, 0.9f);
	private static readonly Color DimColor = new(0.6f, 0.6f, 0.7f);
	private static readonly Color VersionColor = new(0.5f, 0.5f, 0.62f);

	private Camera3D? _camera;
	private Viewport? _xrViewport;

	private MeshInstance3D? _dome;
	private Node3D? _pivot;
	private MeshInstance3D? _barFill;
	private MeshInstance3D? _sweep;
	private Label3D? _statusLabel;
	private Label3D? _percentLabel;

	private string _status = "Initializing...";
	private float _targetProgress;
	private float _currentProgress;
	private float _yaw;
	private float _targetYaw;
	private bool _yawSeeded;
	private bool _built;
	private bool _dismissed;
	private bool _visibilityLogged;
	private double _clock;

	public void Bind(Camera3D camera, Viewport xrViewport)
	{
		_camera = camera;
		_xrViewport = xrViewport;
	}

	public void SetStatus(string status)
	{
		_status = status ?? string.Empty;
		if (_statusLabel != null)
			_statusLabel.Text = _status;
	}

	public void SetProgress(float percentage)
	{
		_targetProgress = Mathf.Clamp(percentage, 0f, 100f);
	}

	// Start the bar where the 2D one already is instead of sweeping up from zero when we attach
	// mid-boot.
	public void SnapProgress(float percentage)
	{
		_currentProgress = Mathf.Clamp(percentage, 0f, 100f);
		ApplyProgress(_currentProgress);
	}

	// Hide for good. _Process re-derives Visible from the XR state every frame, so a plain Visible =
	// false from the owner would be undone on the very next tick, before the queued free lands.
	public void Dismiss()
	{
		_dismissed = true;
		Visible = false;
		SetProcess(false);
	}

	public override void _Ready()
	{
		try
		{
			Build();
			_built = true;
			// Place it on the head now rather than one frame late: the first XR frame is rendered before
			// our first _Process, and we attach in the same frame UseXR flips on.
			if (_camera != null && GodotObject.IsInstanceValid(_camera) && _camera.IsInsideTree())
				FollowHead(0f);
		}
		catch (Exception ex)
		{
			LumoraLogger.Warn($"VrLoadingEnvironment: build failed, headset stays on the bare world ({ex.Message})");
			Visible = false;
		}
	}

	public override void _Process(double delta)
	{
		if (!_built || _dismissed)
			return;

		var live = XrLive();
		if (!_visibilityLogged || Visible != live)
		{
			_visibilityLogged = true;
			LumoraLogger.Log(live
				? "VrLoadingEnvironment: showing in headset"
				: "VrLoadingEnvironment: hidden (XR viewport not live)");
		}
		Visible = live;

		if (!live)
			return;

		var dt = (float)delta;
		_clock += delta;

		FollowHead(dt);

		if (_currentProgress < _targetProgress)
		{
			_currentProgress = Mathf.MoveToward(_currentProgress, _targetProgress, dt * ProgressSmoothSpeed * 100f);
			ApplyProgress(_currentProgress);
		}

		// The sweep moves whether or not progress does. A stuck phase still shows a moving frame, a
		// hung process shows a frozen one; that distinction is what the person in the headset needs.
		if (_sweep != null)
		{
			var t = Mathf.PingPong((float)(_clock / SweepPeriodSec), 1f);
			t = Mathf.SmoothStep(0f, 1f, t);
			var travel = BarWidth - SweepWidth;
			var pos = _sweep.Position;
			pos.X = -travel * 0.5f + t * travel;
			_sweep.Position = pos;
		}
	}

	private bool XrLive()
	{
		if (_camera == null || !GodotObject.IsInstanceValid(_camera) || !_camera.IsInsideTree())
			return false;
		if (_xrViewport == null || !GodotObject.IsInstanceValid(_xrViewport) || !_xrViewport.UseXR)
			return false;
		var primary = XRServer.PrimaryInterface;
		return primary != null && primary.IsInitialized();
	}

	private void FollowHead(float dt)
	{
		if (_camera == null || _dome == null || _pivot == null)
			return;

		var head = _camera.GlobalTransform;

		// Dome: position-locked, rotation-free, so looking around actually looks around.
		_dome.GlobalPosition = head.Origin;

		// Panel: yaw-only lazy follow. Pitch and roll are dropped so the panel stays upright and level;
		// yaw eases so it trails a head turn and settles in front again instead of riding the eyes.
		var forward = -head.Basis.Z;
		forward.Y = 0f;
		if (forward.LengthSquared() > 1e-4f)
		{
			forward = forward.Normalized();
			_targetYaw = Mathf.Atan2(-forward.X, -forward.Z);
		}

		if (!_yawSeeded)
		{
			_yaw = _targetYaw;
			_yawSeeded = true;
		}
		else
		{
			var weight = 1f - Mathf.Exp(-dt * YawFollowRate);
			_yaw = Mathf.LerpAngle(_yaw, _targetYaw, weight);
		}

		_pivot.GlobalTransform = new Transform3D(new Basis(Vector3.Up, _yaw), head.Origin);
	}

	private void ApplyProgress(float percentage)
	{
		var fraction = Mathf.Clamp(percentage / 100f, 0f, 1f);
		if (_barFill != null)
		{
			var width = Mathf.Max(fraction * BarWidth, 0.0005f);
			_barFill.Visible = fraction > 0.001f;
			_barFill.Scale = new Vector3(width, BarHeight - 0.008f, 1f);
			var pos = _barFill.Position;
			pos.X = -BarWidth * 0.5f + width * 0.5f;
			_barFill.Position = pos;
		}

		if (_percentLabel != null)
			_percentLabel.Text = $"{Mathf.RoundToInt(percentage)}%";
	}

	private void Build()
	{
		// DOME
		Material domeMaterial;
		var domeShader = ResourceLoader.Exists(DomeShaderPath) ? GD.Load<Shader>(DomeShaderPath) : null;
		if (domeShader != null)
		{
			var shaderMaterial = new ShaderMaterial { Shader = domeShader, RenderPriority = PriorityDome };
			shaderMaterial.SetShaderParameter("accent_color", AccentColor);
			domeMaterial = shaderMaterial;
		}
		else
		{
			// Flat fallback so a packaging miss on the shader still leaves a lit space with a readable
			// panel instead of the black void this whole thing exists to replace.
			LumoraLogger.Warn($"VrLoadingEnvironment: '{DomeShaderPath}' not found, dome falls back to a flat colour.");
			domeMaterial = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				NoDepthTest = true,
				CullMode = BaseMaterial3D.CullModeEnum.Front,
				AlbedoColor = new Color(0.10f, 0.08f, 0.22f),
				RenderPriority = PriorityDome,
			};
		}

		_dome = new MeshInstance3D
		{
			Name = "Dome",
			Mesh = new SphereMesh
			{
				Radius = DomeRadius,
				Height = DomeRadius * 2f,
				RadialSegments = 32,
				Rings = 16,
			},
			MaterialOverride = domeMaterial,
			// Bootstrap.tscn's DirectionalLight3D has shadows on. A 30 m shell casting a shadow would
			// darken everything inside it, and shadow casters are not exempt just because the material
			// never writes depth.
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_dome);

		// PANEL
		_pivot = new Node3D { Name = "PanelPivot" };
		AddChild(_pivot);

		var z = -PanelDistance;
		var y = PanelDrop;

		AddQuad(_pivot, "Backdrop", new Vector2(PanelWidth, PanelHeight), new Vector3(0f, y, z), BackdropColor, PriorityBackdrop);

		var barY = y - 0.07f;
		AddQuad(_pivot, "BarBorder", new Vector2(BarWidth + 0.012f, BarHeight + 0.012f), new Vector3(0f, barY, z + 0.003f), BarBorderColor, PriorityBarBorder);
		AddQuad(_pivot, "BarTrack", new Vector2(BarWidth, BarHeight), new Vector3(0f, barY, z + 0.005f), BarTrackColor, PriorityBarTrack);

		// Unit quad scaled per frame; the left edge stays put by shifting the centre with the width.
		_barFill = AddQuad(_pivot, "BarFill", Vector2.One, new Vector3(-BarWidth * 0.5f, barY, z + 0.007f), AccentColor, PriorityBarFill);
		_barFill.Scale = new Vector3(0.0005f, BarHeight - 0.008f, 1f);
		_barFill.Visible = false;

		_sweep = AddQuad(_pivot, "Sweep", new Vector2(SweepWidth, BarHeight - 0.008f), new Vector3(-(BarWidth - SweepWidth) * 0.5f, barY, z + 0.009f), SweepColor, PrioritySweep);

		var font = LoadFont();
		var labelZ = z + 0.011f;

		AddLabel(_pivot, "Title", "LUMORA", 96, AccentColor, new Vector3(0f, y + 0.10f, labelZ), font);
		_statusLabel = AddLabel(_pivot, "Status", _status, 56, StatusColor, new Vector3(0f, y + 0.015f, labelZ), font);
		_statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
		_statusLabel.Width = (PanelWidth - 0.08f) / LabelPixelSize;
		_percentLabel = AddLabel(_pivot, "Percent", "0%", 44, DimColor, new Vector3(0f, barY - 0.045f, labelZ), font);
		AddLabel(_pivot, "Version", $"Lumora v{BuildInfo.Version}", 34, VersionColor, new Vector3(0f, y - PanelHeight * 0.5f + 0.03f, labelZ), font);

		ApplyProgress(_currentProgress);
	}

	private static MeshInstance3D AddQuad(Node3D parent, string name, Vector2 size, Vector3 position, Color color, int priority)
	{
		var quad = new MeshInstance3D
		{
			Name = name,
			Mesh = new QuadMesh { Size = size },
			Position = position,
			MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				NoDepthTest = true,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				AlbedoColor = color,
				RenderPriority = priority,
			},
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		parent.AddChild(quad);
		return quad;
	}

	private static Label3D AddLabel(Node3D parent, string name, string text, int fontSize, Color color, Vector3 position, Font? font)
	{
		var label = new Label3D
		{
			Name = name,
			Text = text,
			FontSize = fontSize,
			PixelSize = LabelPixelSize,
			Modulate = color,
			OutlineSize = 0,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			NoDepthTest = true,
			RenderPriority = PriorityLabel,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Position = position,
		};
		if (font != null)
			label.Font = font;
		parent.AddChild(label);
		return label;
	}

	private static Font? LoadFont()
	{
		try
		{
			return ResourceLoader.Exists(FontPath) ? GD.Load<Font>(FontPath) : null;
		}
		catch (Exception ex)
		{
			LumoraLogger.Warn($"VrLoadingEnvironment: font load failed, using the engine default ({ex.Message})");
			return null;
		}
	}
}
