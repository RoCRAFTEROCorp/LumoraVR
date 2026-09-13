// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core;
using Lumora.Godot.Extensions;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Bootstrap;

// Camera rendering for VR and screen modes: position/rotation, FOV, and view overrides.
public partial class HeadOutput : Node
{
    // Decides how the camera is positioned and rendered.
    public enum OutputType
    {
        // Headset, stereo, tracked.
        VR,

        // Standard screen, mono, user-controlled or first-person.
        Screen,

        // 360-degree equirectangular.
        Screen360,

        // Static camera, no movement.
        Static
    }

    // CONFIGURATION
    [Export] public OutputType Type { get; set; } = OutputType.Screen;
    [Export] public float DefaultFOV { get; set; } = 90f;
    [Export] public float NearClip { get; set; } = 0.25f; // Clips through head sphere
    [Export] public float FarClip { get; set; } = 1000f;

    // CAMERA REFERENCES
    private Camera3D _camera = null!;
    private Camera3D _desktopCamera = null!;
    private XRCamera3D _vrCamera = null!;
    private XROrigin3D _xrOrigin = null!;

    // STATE
    private bool _isVRActive = false;
    private Vector3 _overridePosition = Vector3.Zero;
    private Quaternion _overrideRotation = Quaternion.Identity;
    private bool _hasPositionOverride = false;
    private bool _hasRotationOverride = false;

    // Which camera the clip planes and FOV were last written to, and with what. Every Camera3D
    // setter is a marshalled call that re-sends the projection to the rendering server, and these
    // three were rewritten every frame with values that never change. They are re-pushed when the
    // camera object changes hands (VR <-> screen), because the VR mirror overwrites the desktop
    // camera's planes and FOV while the headset is rendering. -xlinka
    private Camera3D? _settingsCamera;
    private float _appliedNear;
    private float _appliedFar;
    private float _appliedFov;

    public Vector3 CameraPosition => _camera?.GlobalPosition ?? Vector3.Zero;

    public Quaternion CameraRotation => _camera?.GlobalTransform.Basis.GetRotationQuaternion() ?? Quaternion.Identity;

    public void Initialize(Camera3D camera)
    {
        _desktopCamera = camera;
        _camera = _desktopCamera;
        ConfigureCamera(_desktopCamera, setFov: true);

        // Check if VR is active
        var xrInterface = XRServer.FindInterface("OpenXR");
        _isVRActive = xrInterface != null && xrInterface.IsInitialized();

        if (_isVRActive)
        {
            SetupVRCamera();
            UseVRCamera();
            Type = OutputType.VR;
        }
        else
        {
            UseDesktopCamera();
            Type = OutputType.Screen;
        }

        LumoraLogger.Debug($"HeadOutput: Initialized with type={Type}, FOV={DefaultFOV}, isVR={_isVRActive}");
    }

    // Locate the XROrigin3D / XRCamera3D defined in Bootstrap.tscn. They live inside the XR
    // SubViewport (see %XROrigin3D / %XRCamera3D) and are no longer created at runtime; the .tscn
    // ships them so the XR viewport stays valid for the whole process lifetime. Resolved via
    // CurrentScene rather than the calling node's owner-chain because HeadOutput is created at
    // runtime (Owner == null), which breaks the bare-percent unique-name lookup. - xlinka
    private void SetupVRCamera()
    {
        if (!IsInsideTree())
        {
            LumoraLogger.Log("HeadOutput: Deferring VR setup until added to scene tree");
            CallDeferred(MethodName.SetupVRCamera);
            return;
        }

        var sceneRoot = GetTree()?.CurrentScene;
        _xrOrigin = sceneRoot?.GetNodeOrNull<XROrigin3D>("%XROrigin3D")!;
        _vrCamera = sceneRoot?.GetNodeOrNull<XRCamera3D>("%XRCamera3D")!;

        if (_xrOrigin == null || _vrCamera == null)
        {
            LumoraLogger.Error("HeadOutput: %XROrigin3D / %XRCamera3D not found in scene. " +
                "Bootstrap.tscn must contain the XR SubViewport sub-tree.");
            return;
        }

        ConfigureCamera(_vrCamera, setFov: false);

        LumoraLogger.Log("HeadOutput: VR camera bound to %XRCamera3D");
    }

    private void UseDesktopCamera()
    {
        if (_desktopCamera == null || !GodotObject.IsInstanceValid(_desktopCamera))
        {
            LumoraLogger.Warn("HeadOutput: Cannot use desktop camera - camera is missing or invalid.");
            return;
        }

        _camera = _desktopCamera;

        if (_vrCamera != null && GodotObject.IsInstanceValid(_vrCamera) && CamerasShareViewport(_desktopCamera, _vrCamera))
            _vrCamera.Current = false;

        _desktopCamera.MakeCurrent();
    }

    private void UseVRCamera()
    {
        if (_vrCamera == null || !GodotObject.IsInstanceValid(_vrCamera))
            SetupVRCamera();

        if (_vrCamera == null || !GodotObject.IsInstanceValid(_vrCamera))
        {
            LumoraLogger.Warn("HeadOutput: Cannot use VR camera - XRCamera3D is missing or invalid.");
            return;
        }

        _camera = _vrCamera;

        if (_desktopCamera != null && GodotObject.IsInstanceValid(_desktopCamera) && CamerasShareViewport(_desktopCamera, _vrCamera))
            _desktopCamera.Current = false;

        _vrCamera.MakeCurrent();
    }

    private static bool CamerasShareViewport(Camera3D a, Camera3D b)
    {
        if (a == null || b == null || !GodotObject.IsInstanceValid(a) || !GodotObject.IsInstanceValid(b))
            return false;

        return a.GetViewport() == b.GetViewport();
    }

    private void ConfigureCamera(Camera3D camera, bool setFov)
    {
        if (camera == null)
            return;

        camera.PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off;
        camera.Near = NearClip;
        camera.Far = FarClip;

        if (setFov)
            camera.Fov = DefaultFOV;
    }

    // Camera positioning from the focused world, once per frame after the engine update.
    public void UpdatePositioning(Lumora.Core.Engine? engine)
    {
        if (_camera == null || engine == null)
            return;

        var focusedWorld = engine.WorldManager?.FocusedWorld;
        if (focusedWorld == null)
            return;

        UpdateCameraSettings();

        if (Type == OutputType.VR)
        {
            UpdateVRPositioning(focusedWorld);
        }
        else if (Type == OutputType.Screen)
        {
            UpdateScreenPositioning(focusedWorld);
        }
        // Screen360 and Static have no positioning path yet.
    }

    // Clip planes and FOV. Defaults until a RenderSettings component exists to read from; written
    // only when the value or the camera object changed.
    private void UpdateCameraSettings()
    {
        bool sameCamera = ReferenceEquals(_settingsCamera, _camera);

        if (!sameCamera || _appliedNear != NearClip)
        {
            _camera.Near = NearClip;
            _appliedNear = NearClip;
        }

        if (!sameCamera || _appliedFar != FarClip)
        {
            _camera.Far = FarClip;
            _appliedFar = FarClip;
        }

        if (Type == OutputType.Screen && (!sameCamera || _appliedFov != DefaultFOV))
        {
            _camera.Fov = DefaultFOV;
            _appliedFov = DefaultFOV;
        }

        _settingsCamera = _camera;
    }

    // The VR camera is tracked by OpenXR; the XR origin follows the local user's root so HMD and
    // controllers land on the avatar's transforms.
    private void UpdateVRPositioning(World world)
    {
        if (world.LocalUser == null)
            return;

        var userRootSlot = world.LocalUser.Root?.Slot;
        if (_xrOrigin != null)
        {
            if (userRootSlot != null)
            {
                var originPosition = userRootSlot.GlobalPosition.ToGodot();
                var originRotation = new Basis(userRootSlot.GlobalRotation.ToGodot());
                _xrOrigin.GlobalTransform = new Transform3D(originRotation, originPosition);
            }
            else
            {
                _xrOrigin.GlobalPosition = Vector3.Zero;
                _xrOrigin.GlobalRotation = Vector3.Zero;
            }
        }

        Lumora.Core.Engine.Current?.InputInterface?.SyncTrackingSpaceToFocusedLocalUser();
    }

    // Screen cameras are user-controlled or follow the first-person view.
    private void UpdateScreenPositioning(World world)
    {
        // Publish whether the camera is overridden (3rd-person / free-cam) so the userspace dashboard knows it
        // can't simply lock to the local head pose this frame. -xlinka
        Lumora.Core.Engine.Current?.InputInterface?.SetDesktopCameraOverride(_hasPositionOverride || _hasRotationOverride);

        if (_hasPositionOverride)
        {
            _camera.GlobalPosition = _overridePosition;
        }
        else
        {
            if (world.LocalUser?.Root != null)
            {
                var userRoot = world.LocalUser.Root;
                _camera.GlobalPosition = userRoot.HeadPosition.ToGodot();
            }
            else
            {
                _camera.GlobalPosition = new Vector3(0, 1.6f, 0);
            }
        }

        if (_hasRotationOverride)
        {
            _camera.GlobalTransform = new Transform3D(new Basis(_overrideRotation), _camera.GlobalPosition);
        }
        else
        {
            if (world.LocalUser?.Root != null)
            {
                var userRoot = world.LocalUser.Root;
                var rotation = userRoot.HeadRotation;
                _camera.GlobalTransform = new Transform3D(new Basis(rotation.ToGodot()), _camera.GlobalPosition);
            }
        }
    }

    // Custom camera control (photo mode, cinematic cameras, third-person, free-cam).
    public void SetPositionOverride(Vector3 position)
    {
        _overridePosition = position;
        _hasPositionOverride = true;
    }

    public void ClearPositionOverride()
    {
        _hasPositionOverride = false;
    }

    public void SetRotationOverride(Quaternion rotation)
    {
        _overrideRotation = rotation;
        _hasRotationOverride = true;
    }

    public void ClearRotationOverride()
    {
        _hasRotationOverride = false;
    }

    // Called by XRModeManager when the VR active state changes at runtime.
    public void NotifyVRActiveChanged(bool isActive)
    {
        _isVRActive = isActive;

        if (isActive)
        {
            UseVRCamera();
        }
        else
        {
            UseDesktopCamera();
        }

        LumoraLogger.Log($"HeadOutput: VR active state -> {isActive}");
    }

    // Switch output type (VR <-> Screen). When switching to VR call NotifyVRActiveChanged first so
    // _isVRActive is already up to date by the time this runs.
    public void SwitchOutputType(OutputType newType)
    {
        if (Type == newType)
            return;

        LumoraLogger.Log($"HeadOutput: Switching from {Type} to {newType}");

        // Guard: only allow VR if the XR interface has been activated.
        if (newType == OutputType.VR && !_isVRActive)
        {
            LumoraLogger.Warn("HeadOutput: Cannot switch to VR - XR interface is not active. Staying in Screen mode.");
            return;
        }

        if (newType == OutputType.VR)
            UseVRCamera();
        else if (newType == OutputType.Screen)
            UseDesktopCamera();

        Type = newType;
    }

    public new void Dispose()
    {
        _camera = null!;
        _desktopCamera = null!;
        _vrCamera = null!;
        _xrOrigin = null!;
        _settingsCamera = null;
    }
}
