// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Godot.Extensions;
using Lumora.Source.UI;
using Lumora.Source.Godot.UI;
using Lumora.Core.Components;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Input;

// Desktop camera modes: F5 = third-person orbit (mouse orbits camera around character; press again
// for first-person), F6 = free-cam fly (WASD+mouse, character frozen; press again for first-person).
// There is ONE rendering camera (the HeadOutput screen camera); each mode just feeds it a pose via
// HeadOutput's position/rotation override, it never spawns its own camera. That keeps a single
// source of truth for "the active camera", so the dashboard, cursor ray and laser (which all track
// the HeadOutput camera) follow the view in every mode. First-person = clear the override.
public partial class DesktopCameraController : Node
{
    public enum CameraMode { FirstPerson, ThirdPerson, FreeCam }

    // THIRD-PERSON SETTINGS
    private const float TpDefaultDistance = 3.5f;
    private const float TpMinDistance     = 1.0f;
    private const float TpMaxDistance     = 12.0f;
    private const float TpFallbackEyeHeight = 1.6f; // only until the rig's head body node resolves
    private const float TpDistanceLerpRate  = 10f;  // per second, toward the scrolled distance
    private const float TpMinUserScale      = 0.1f;
    private const float TpMaxUserScale      = 10f;
    private const float TpDefaultPitch    = 0.21f;  // ~12 degrees in radians
    private const float TpMinPitch        = -0.35f; // slightly below horizon
    private const float TpMaxPitch        =  1.40f; // nearly top-down

    // THIRD-PERSON CAMERA COLLISION
    // The probe starts this far off the head (user-scaled) so it cannot begin inside the avatar's own
    // head geometry; the source platform uses the same 5 cm. The standoff is the source's 1 cm, but
    // never less than the render camera's near plane: our NearClip is 0.25 m (HeadOutput.cs:34), and a
    // wall 1 cm from the lens sits entirely inside the near plane, is not drawn, and the view looks
    // straight through it - the pull-in would run and the wall would still vanish. -xlinka
    private const float TpCollisionRayStart    = 0.05f;
    private const float TpCollisionStandoff    = 0.01f;
    private const float TpCollisionReleaseRate = 8f;   // per second, easing back OUT once the way is clear

    // FREE-CAM SETTINGS
    private const float FreeCamBaseSpeed   = 5f;
    private const float FreeCamFastMult    = 4f;
    private const float LookReferenceHeight = 1080f;
    private const uint FreeCamIndicatorLayer = (uint)Lumora.Godot.Helpers.RenderHelper.FREECAM_INDICATOR_LAYER;

    private static float LookSensitivity => (Mathf.Pi / LookReferenceHeight) * InterfaceSettings.MouseSensitivity;

    // REFERENCES
    private Lumora.Core.Engine _engine = null!;
    private Lumora.Source.Godot.Bootstrap.HeadOutput _headOutput = null!;
    private Node3D    _freeCamIndicator = null!;
    private Label3D   _freeCamLabel = null!;

    // STATE
    public static CameraMode ActiveMode { get; private set; } = CameraMode.FirstPerson;

    private CameraMode _mode      = CameraMode.FirstPerson;

    // Third-person orbit (values in radians)
    private float   _tpDistance = TpDefaultDistance;
    private float   _tpSmoothDistance = TpDefaultDistance;
    private bool    _tpDistanceSeeded;
    private float   _tpOrbitYaw;
    private float   _tpOrbitPitch = TpDefaultPitch;
    private Vector2 _pendingTpMouse;

    // Orbit anchor, resolved from the focused world's local user root and re-resolved only when the
    // root changes or the rig is rebuilt. UserRoot.HeadSlot walks the registered-component table
    // behind a predicate closure, so calling it every frame at 144Hz is a per-frame allocation for a
    // slot that changes about once per world load. -xlinka
    private Lumora.Core.Components.UserRoot? _tpAnchorRoot;
    private Lumora.Core.Slot? _tpHeadSlot;

    // Camera collision: the ABSOLUTE distance along the orbit ray the nearest obstruction allows
    // (+inf when the way is clear), kept apart from the orbit distance so scroll zoom is never
    // smoothed twice. It is a cap rather than an amount pulled in because an amount goes stale the
    // moment you scroll: zoom in while pinned to a wall and a stored "1.5 m in" would drag the camera
    // 1.5 m under a shorter free distance, dipping it closer than the wall asks and easing back out.
    // The exclude list is one entry (the local user root) reused across frames; the physics hook
    // walks ancestors so the whole avatar is skipped off that one slot. -xlinka
    private float _tpObstructionLimit = float.PositiveInfinity;
    private readonly List<Lumora.Core.Slot> _tpRayExclude = new(1);

    // The UserInputState the active external mode wrote its flags on. The flags live on a PER-WORLD
    // component (that world's local user root), and reading ForFocusedLocalUser again at exit time
    // clears whichever world is focused THEN: walk through a portal while in F5, press F5 again, and
    // the old world keeps MouseLookSuppressed + ExternalCameraActive forever (dead mouse look and a
    // permanently shown head when you go back), while the new world never had them (its
    // LocomotionController runs first-person mouse look against the orbit off the same motion, and
    // never takes its camera-relative path). Remember where the flags went and clear them there. -xlinka
    private UserInputState? _flaggedState;

    // Free-cam
    private Vector3 _freeCamPos;
    private float   _freeCamYaw;
    private float   _freeCamPitch;
    private Vector2 _pendingFreeCamMouse;

    // INIT

    public void Initialize(Lumora.Core.Engine engine, Lumora.Source.Godot.Bootstrap.HeadOutput headOutput)
    {
        _engine = engine;
        _headOutput = headOutput;
        // No camera is created here: third-person/free-cam drive the single HeadOutput screen camera
        // through its pose override (see SwitchMode/UpdateThirdPerson/UpdateFreeCam).
    }

    public override void _Ready()
    {
        CreateFreeCamIndicator();
    }

    // Without this, the camera could stay stuck in an override pose and every F8 cycle would leak
    // an indicator into the scene tree. - xlinka
    public override void _ExitTree()
    {
        _headOutput?.ClearPositionOverride();
        _headOutput?.ClearRotationOverride();

        if (_freeCamIndicator != null && GodotObject.IsInstanceValid(_freeCamIndicator))
            _freeCamIndicator.QueueFree();
        _freeCamIndicator = null!;
        _freeCamLabel = null!;

        base._ExitTree();
    }

    private void CreateFreeCamIndicator()
    {
        try
        {
            _freeCamIndicator = new Node3D { Name = "FreeCamIndicator" };
            _freeCamIndicator.Visible = false;

            var mesh = new MeshInstance3D();
            mesh.Layers = FreeCamIndicatorLayer;
            mesh.Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f };
            var mat = new StandardMaterial3D
            {
                AlbedoColor     = new Color(0.55f, 0.55f, 1.0f),
                EmissionEnabled = true,
                Emission        = new Color(0.25f, 0.25f, 0.75f),
            };
            mesh.MaterialOverride = mat;
            _freeCamIndicator.AddChild(mesh);

            _freeCamLabel = new Label3D
            {
                Name        = "UsernameLabel",
                Text        = "freecam",
                Billboard   = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                PixelSize   = 0.004f,
                FontSize    = 28,
                Layers      = FreeCamIndicatorLayer,
                Position    = new Vector3(0f, 0.38f, 0f),
            };
            _freeCamIndicator.AddChild(_freeCamLabel);

            // Add directly to the scene-tree root so it has a world-space transform
            GetTree().Root.CallDeferred(Node.MethodName.AddChild, _freeCamIndicator);
        }
        catch (System.Exception ex)
        {
            LumoraLogger.Warn($"DesktopCameraController: FreeCam indicator disabled ({ex.Message})");
            _freeCamIndicator?.QueueFree();
            _freeCamIndicator = null!;
            _freeCamLabel = null!;
        }
    }

    // GODOT CALLBACKS

    public override void _Process(double delta)
    {
        var camera = Lumora.Core.Engine.Current?.InputInterface?.Actions?.Camera;
        if (camera != null)
        {
            if (camera.ThirdPerson.Pressed)
                SwitchMode(_mode == CameraMode.ThirdPerson ? CameraMode.FirstPerson : CameraMode.ThirdPerson);

            if (camera.FreeCam.Pressed)
                SwitchMode(_mode == CameraMode.FreeCam ? CameraMode.FirstPerson : CameraMode.FreeCam);
        }

        UpdateOrbitZoom(camera);

        switch (_mode)
        {
            case CameraMode.ThirdPerson: UpdateThirdPerson((float)delta); break;
            case CameraMode.FreeCam:     UpdateFreeCam((float)delta); break;
        }
    }

    // Orbit distance. The wheel is shared with the held-object distance control, so a hand carrying
    // something on its laser claims it and the camera leaves that notch alone - otherwise reeling an
    // object in would zoom the view at the same time. -xlinka
    private void UpdateOrbitZoom(Lumora.Core.Input.Actions.CameraActions? camera)
    {
        if (_mode != CameraMode.ThirdPerson || camera == null)
            return;
        if (DashboardToggle.IsDashboardVisible || UserInputState.FocusedScrollWheelCaptured)
            return;

        float zoom = camera.OrbitZoom.Value;
        if (zoom != 0f)
            _tpDistance = Mathf.Clamp(_tpDistance - zoom * 0.5f, TpMinDistance, TpMaxDistance);
    }

    public override void _Input(InputEvent @event)
    {
        if (DashboardToggle.IsDashboardVisible) return;
        if (UserInputState.FocusedDesktopInputSuppressed)
        {
            _pendingTpMouse = Vector2.Zero;
            _pendingFreeCamMouse = Vector2.Zero;
            return;
        }

        if (_mode == CameraMode.ThirdPerson && @event is InputEventMouseMotion tpMotion)
            _pendingTpMouse += tpMotion.Relative;

        if (_mode == CameraMode.FreeCam && @event is InputEventMouseMotion fcMotion)
            _pendingFreeCamMouse += fcMotion.Relative;

        // Raw motion stays on the event path: look is a continuous pixel delta accumulated between
        // frames, not a control anybody would rebind. Everything discrete goes through actions.
    }

    // MODE SWITCHING

    private void SwitchMode(CameraMode newMode)
    {
        var prev = _mode;
        _mode      = newMode;
        ActiveMode = newMode;

        // Leave the previous mode on the state it was written to (see _flaggedState), not on whatever
        // world happens to be focused now.
        var prevState = IsAlive(_flaggedState) ? _flaggedState : null;
        if (prev == CameraMode.FreeCam)
        {
            prevState?.SetFreeCamActive(false);
            _pendingFreeCamMouse = Vector2.Zero;
            if (_freeCamIndicator != null) _freeCamIndicator.Visible = false;
        }
        if (prev == CameraMode.ThirdPerson)
        {
            prevState?.SetMouseLookSuppressed(false);
            _pendingTpMouse = Vector2.Zero;
        }
        if (prev != CameraMode.FirstPerson)
            prevState?.SetExternalCameraActive(false);
        _flaggedState = null;

        var state = UserInputState.ForFocusedLocalUser;

        // First-person-only visual overrides (local head hiding) key off this:
        // any external camera must show the full avatar.
        if (newMode != CameraMode.FirstPerson && state != null)
        {
            state.SetExternalCameraActive(true);
            _flaggedState = state;
        }

        switch (newMode)
        {
            case CameraMode.FirstPerson:
                _headOutput?.ClearPositionOverride();
                _headOutput?.ClearRotationOverride();
                LumoraLogger.Log("[DesktopCameraController] First-person");
                break;

            case CameraMode.ThirdPerson:
                _tpOrbitYaw   = GetCharacterBodyYaw();
                _tpOrbitPitch = TpDefaultPitch;
                _pendingTpMouse = Vector2.Zero;
                // Snap the distance on the first frame instead of easing in from the last session's
                // value, and drop the cached rig slots so a world change can't pivot on a dead head.
                _tpDistanceSeeded = false;
                _tpAnchorRoot     = null;
                _tpHeadSlot       = null;
                _tpObstructionLimit = float.PositiveInfinity;
                state?.SetMouseLookSuppressed(true);
                LumoraLogger.Log("[DesktopCameraController] Third-person (mouse=orbit, scroll=distance)");
                break;

            case CameraMode.FreeCam:
                SeedFreeCamFromActiveCamera();
                RefreshFreeCamLabel();
                state?.SetFreeCamActive(true);
                if (_freeCamIndicator != null) _freeCamIndicator.Visible = true;
                LumoraLogger.Log("[DesktopCameraController] Free-cam (WASD+mouse, Shift=fast, Space/Ctrl=vertical)");
                break;
        }
    }

    // THIRD-PERSON

    // The orbit pivot is re-read from the FOCUSED world's local user root every frame, so walking
    // carries the camera with it. It used to pivot on a process-wide static of the local player's
    // character body (since removed from CharacterControllerHook) that EVERY world's local-user hook
    // wrote on init - local home, session world, anything else loaded - so the last world to build its
    // avatar owned it, and a body belonging to a background world is never re-based (Simulate bails
    // on WorldFocus.Background) and therefore never moves. Pivoting on a frozen body is a camera
    // that sits still while the avatar walks off. The null branch was worse: it fell back to the
    // render camera's own GlobalPosition, which in third person IS last frame's orbit result, so the
    // pivot chased itself away from the player. The user root is what first-person already reads
    // (HeadOutput.UpdateScreenPositioning), so it is known-live. -xlinka
    private void UpdateThirdPerson(float delta)
    {
        if (_headOutput == null) return;
        if (UserInputState.FocusedDesktopInputSuppressed)
        {
            _pendingTpMouse = Vector2.Zero;
        }

        var mouse       = _pendingTpMouse;
        _pendingTpMouse = Vector2.Zero;

        float sensitivity = LookSensitivity;
        _tpOrbitYaw   -= mouse.X * sensitivity;
        _tpOrbitPitch -= mouse.Y * sensitivity;
        _tpOrbitPitch  = Mathf.Clamp(_tpOrbitPitch, TpMinPitch, TpMaxPitch);

        // No rig yet (world still loading, avatar mid-rebuild): hold the pose we already published.
        // Writing a fallback here would snap the view to world origin for those frames.
        if (!TryGetOrbitPivot(out Vector3 pivot, out float userScale))
            return;

        EnsureThirdPersonFlagsOnAnchor();

        float targetDistance = _tpDistance * userScale;
        if (!_tpDistanceSeeded)
        {
            _tpSmoothDistance = targetDistance;
            _tpDistanceSeeded = true;
        }
        else
        {
            _tpSmoothDistance = Mathf.Lerp(_tpSmoothDistance, targetDistance,
                                           Mathf.Clamp(delta * TpDistanceLerpRate, 0f, 1f));
        }

        // Build orbit offset: +Z = behind character at yaw=charFacing
        var yawQ   = Quaternion.FromEuler(new Vector3(0f, _tpOrbitYaw,   0f));
        var pitchQ = Quaternion.FromEuler(new Vector3(_tpOrbitPitch, 0f, 0f));
        Vector3 rayDir = (yawQ * pitchQ) * Vector3.Back;
        float distance = ResolveCameraDistance(pivot, rayDir, _tpSmoothDistance, userScale, delta);
        Vector3 camPos = pivot + rayDir * distance;

        // Look along the orbit ray rather than at the pivot: the two are the same direction while the
        // camera sits on the ray, and this stays defined when a wall pulls it in to a few centimetres.
        Quaternion camRot = Basis.LookingAt(-rayDir, Vector3.Up).GetRotationQuaternion();

        _headOutput.SetPositionOverride(camPos);
        _headOutput.SetRotationOverride(camRot);
    }

    // Pull the camera in front of whatever the orbit ray crosses between the head and the wanted view
    // point, the way the source platform does: probe from the head toward the view against this
    // world's solid bodies with the local user's own avatar excluded, and park the camera at
    // hit.Point + normal * standoff when something is in the way (here projected back onto the orbit
    // ray, which differs from the source's off-ray point by under a centimetre and keeps the look
    // direction constant). Triggers are not probed, so grabbable sensors never tug the view, and the
    // physics hook already skips bodies it cannot resolve to a slot in this world, which is what keeps
    // every user's CharacterBody3D (parented to the world root with no slot tag) out of the result.
    //
    // The source snaps both ways. Snapping IN stays: a frame spent behind a wall is a frame of the
    // world's backface soup. Snapping OUT is what makes a camera jam and pop in doorways and at
    // corners, where the ray flickers between hit and miss on consecutive frames, so the release eases
    // back over a few frames instead. Worlds with no query hook (Raycast returns false) keep the free
    // distance, which is exactly what the camera did before the probe existed. -xlinka
    private float ResolveCameraDistance(Vector3 pivot, Vector3 rayDir, float freeDistance, float userScale, float delta)
    {
        float rayStart = TpCollisionRayStart * userScale;
        float limit = float.PositiveInfinity;

        var root = _tpAnchorRoot;
        var physics = root?.World?.Physics;
        var rootSlot = root?.Slot;
        float rayLength = freeDistance - rayStart;
        if (physics != null && rootSlot != null && rayLength > 0f)
        {
            if (_tpRayExclude.Count == 0) _tpRayExclude.Add(rootSlot);
            else _tpRayExclude[0] = rootSlot;

            var origin = (pivot + rayDir * rayStart).ToLumora();
            if (physics.Raycast(origin, rayDir.ToLumora(), rayLength, _tpRayExclude, out var hit))
            {
                float standoff = Mathf.Max(TpCollisionStandoff, _headOutput?.NearClip ?? 0f);
                Vector3 parked = hit.Point.ToGodot() + hit.Normal.ToGodot() * standoff;
                limit = Mathf.Max((parked - pivot).Dot(rayDir), rayStart);
            }
        }

        if (limit <= _tpObstructionLimit || _tpObstructionLimit >= freeDistance)
        {
            // Closer obstruction, or the old cap no longer binds at this zoom: take the new one as is.
            _tpObstructionLimit = limit;
        }
        else
        {
            // The obstruction receded or cleared: ease the cap out toward where it now sits (the free
            // distance if it is gone) and drop it the moment it stops binding.
            float target = Mathf.Min(limit, freeDistance);
            _tpObstructionLimit = Mathf.Lerp(_tpObstructionLimit, target, Mathf.Clamp(delta * TpCollisionReleaseRate, 0f, 1f));
            if (_tpObstructionLimit >= freeDistance - 0.001f)
                _tpObstructionLimit = limit;
        }

        return Mathf.Min(freeDistance, _tpObstructionLimit);
    }

    // Keep the third-person flags on the world the camera is actually orbiting in. TryGetOrbitPivot
    // already re-resolves the anchor root on a focus change; the flags have to move with it or the
    // new world's LocomotionController never sees IsThirdPerson (see _flaggedState). The state
    // component is attached by LocomotionController on its first update, so a fresh root can
    // legitimately have none yet: leave the old one in place and try again next frame. -xlinka
    private void EnsureThirdPersonFlagsOnAnchor()
    {
        var rootSlot = _tpAnchorRoot?.Slot;
        if (rootSlot == null)
            return;

        var current = _flaggedState;
        if (IsAlive(current) && ReferenceEquals(current.Slot, rootSlot))
            return;

        var next = rootSlot.GetComponent<UserInputState>();
        if (next == null)
            return;

        if (IsAlive(current))
        {
            current.SetMouseLookSuppressed(false);
            current.SetExternalCameraActive(false);
        }
        next.SetMouseLookSuppressed(true);
        next.SetExternalCameraActive(true);
        _flaggedState = next;
    }

    private static bool IsAlive([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] UserInputState? state)
        => state != null && !state.IsDestroyed;

    // Pivot on the head body node, not on the root: the root sits at the feet, and the source
    // platform orbits the actual head position so pitch 0 looks level with the player's eyes rather
    // than up at them. Distance rides the root's global scale for the same reason the source scales
    // its offset - a shrunk or grown user needs a proportional camera, not a fixed 3.5 m. -xlinka
    private bool TryGetOrbitPivot(out Vector3 pivot, out float userScale)
    {
        pivot     = Vector3.Zero;
        userScale = 1f;

        // Engine.Current as a backstop: an unset _engine would silently return "no rig" forever and
        // leave the view frozen with no error, which is the failure this method exists to end.
        var engine = _engine ?? Lumora.Core.Engine.Current;
        var userRoot = engine?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        if (userRoot == null || userRoot.IsDestroyed || userRoot.Slot == null)
        {
            _tpAnchorRoot = null;
            _tpHeadSlot   = null;
            return false;
        }

        if (!ReferenceEquals(userRoot, _tpAnchorRoot) || _tpHeadSlot == null || _tpHeadSlot.IsDestroyed)
        {
            _tpAnchorRoot = userRoot;
            _tpHeadSlot   = userRoot.HeadSlot;
        }

        var head = _tpHeadSlot;
        var pivotPos = (head != null && !head.IsDestroyed)
            ? head.GlobalPosition
            : userRoot.Slot.GlobalPosition + new Lumora.Core.Math.float3(0f, TpFallbackEyeHeight, 0f);

        pivot = pivotPos.ToGodot();

        float scale = userRoot.Slot.LocalScaleToGlobal(1f);
        if (float.IsFinite(scale))
            userScale = Mathf.Clamp(scale, TpMinUserScale, TpMaxUserScale);

        return true;
    }

    private float GetCharacterBodyYaw()
    {
        var slot = (_engine ?? Lumora.Core.Engine.Current)?.WorldManager?.FocusedWorld?.LocalUser?.Root?.Slot;
        if (slot == null) return 0f;
        var rot = slot.GlobalRotation;
        return new Quaternion(rot.x, rot.y, rot.z, rot.w).GetEuler().Y;
    }

    // FREE CAM

    private void SeedFreeCamFromActiveCamera()
    {
        var active = Lumora.Source.Godot.Bootstrap.XRModeManager.Instance?.CurrentCamera;
        if (active != null)
        {
            _freeCamPos   = active.GlobalPosition;
            var euler     = active.GlobalTransform.Basis.GetEuler();
            _freeCamPitch = euler.X;
            _freeCamYaw   = euler.Y;
        }
    }

    private void RefreshFreeCamLabel()
    {
        if (_freeCamLabel == null) return;
        var name = _engine?.WorldManager?.FocusedWorld?.LocalUser?.UserName?.Value;
        _freeCamLabel.Text = string.IsNullOrEmpty(name) ? "[freecam]" : $"{name}\n[freecam]";
    }

    private void UpdateFreeCam(float delta)
    {
        if (_headOutput == null) return;
        if (DashboardToggle.IsDashboardVisible) return;
        if (UserInputState.FocusedDesktopInputSuppressed)
        {
            _pendingFreeCamMouse = Vector2.Zero;
            return;
        }

        var mouse            = _pendingFreeCamMouse;
        _pendingFreeCamMouse = Vector2.Zero;

        float sensitivity = LookSensitivity;
        _freeCamYaw   -= mouse.X * sensitivity;
        _freeCamPitch -= mouse.Y * sensitivity;
        _freeCamPitch  = Mathf.Clamp(_freeCamPitch, -Mathf.Pi * 0.499f, Mathf.Pi * 0.499f);

        var yawQ   = Quaternion.FromEuler(new Vector3(0f, _freeCamYaw,   0f));
        var pitchQ = Quaternion.FromEuler(new Vector3(_freeCamPitch, 0f, 0f));
        Quaternion camRot = yawQ * pitchQ;

        var cameraActions = Lumora.Core.Engine.Current?.InputInterface?.Actions?.Camera;
        float speed = cameraActions?.FlyFast.Held == true
            ? FreeCamBaseSpeed * FreeCamFastMult
            : FreeCamBaseSpeed;

        var move = Vector3.Zero;
        if (cameraActions != null)
        {
            // The move action is +Y forward; the camera's local forward is -Z.
            var plane = cameraActions.FlyMove.Value;
            move.X = plane.x;
            move.Z = -plane.y;
            move.Y = cameraActions.FlyVertical.Value;
        }

        if (move.LengthSquared() > 0.001f)
            _freeCamPos += (camRot * move.Normalized()) * speed * delta;

        _headOutput.SetPositionOverride(_freeCamPos);
        _headOutput.SetRotationOverride(camRot);

        if (_freeCamIndicator != null && _freeCamIndicator.IsInsideTree())
            _freeCamIndicator.GlobalPosition = _freeCamPos;
    }
}

