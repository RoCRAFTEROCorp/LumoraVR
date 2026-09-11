// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Input;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

[ComponentCategory("Users")]
public class LocomotionController : Component
{
    // PARAMETERS

    // DirectDelta is now normalized (1.0 = full screen height swipe).
    // Sensitivity of Pi means a full screen-height swipe = 180 degrees.
    public float MouseSensitivity { get; set; } = MathF.PI;
    public float MaxPitch { get; set; } = 89.0f;

    // Whether the user allows being scaled (grab-scale gestures, world zoom).
    // The context menu exposes it as a toggle while at default scale.
    public readonly Sync<bool> ScalingEnabled = new();

    public override void OnInit()
    {
        base.OnInit();
        ScalingEnabled.Value = true;
    }

    // STATE

    private CharacterController _characterController = null!;
    private UserRoot _userRoot = null!;
    private UserInputState _inputState = null!;
    private Mouse _mouse = null!;
    private IKeyboardDriver _keyboardDriver = null!;
    private InputInterface _inputInterface = null!;
    private readonly System.Collections.Generic.List<LocomotionModule> _modules = new();
    private LocomotionModule _activeModule = null!;
    private LocomotionPermissions _permissions = null!;

    private float _pitch = 0.0f;
    private float _yaw = 0.0f;
    private float _crouchBlend = 1.0f;   // smoothed eye-height fraction (1 standing .. CrouchHeight/StandingHeight)
    private bool _mouseCaptured = false;
    private bool _initialized = false;
    private bool _loggedMissingUserRoot = false;
    private bool _loggedActiveUserState = false;

    public UserInputState InputState => _inputState;
    public LocomotionModule ActiveModule => _activeModule;
    public System.Collections.Generic.IReadOnlyList<LocomotionModule> Modules => _modules;

    // Modules are kept in registration order (Walk, Noclip, ...); switching is
    // by list position, not priority (an ordered module list + active index).
    public void RegisterModule(LocomotionModule module)
    {
        if (module == null || _modules.Contains(module))
            return;
        _modules.Add(module);
    }

    public void UnregisterModule(LocomotionModule module)
    {
        if (module == null) return;
        if (_activeModule == module)
        {
            _activeModule.DeactivateInternal();
            _activeModule = null!;
        }
        _modules.Remove(module);
    }

    // INITIALIZATION

    public override void OnAwake()
    {
        base.OnAwake();

        // Don't run TryInitializeLocalUser() here: OnAwake executes inside the ReferenceController's
        // allocation soft-block, and the attaches it does (UserInputState + the locomotion modules) each
        // log a "RefID allocation during OnAwake" warning - dozens of lines per avatar. The first OnUpdate
        // calls it anyway (outside the block), one frame later, with no functional difference. Matches how
        // SessionJoinIndicator builds its children in OnInit rather than OnAwake. -xlinka
        LumoraLogger.Log($"LocomotionController: OnAwake started");
    }

    private void CaptureMouse()
    {
        _mouseCaptured = true;
        _inputState?.SetMouseCaptureRequested(true);
    }

    // UPDATE

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Late-bind InputInterface if it was not ready during Awake
        if (_inputInterface == null && Lumora.Core.Engine.Current?.InputInterface != null)
        {
            _inputInterface = Lumora.Core.Engine.Current.InputInterface;
            _mouse = _inputInterface.Mouse;
            _keyboardDriver = _inputInterface.GetKeyboardDriver();
        }

        if (!TryInitializeLocalUser())
            return;

        if (_characterController == null || !(_userRoot?.IsLocalUserRoot ?? false))
            return;

        // Everything below drives the LOCAL user's OWN avatar/root - head look, body turn, scale, and the
        // locomotion modules. That's engine movement of your own body, not a user edit, so bypass the data-model
        // permission gate: the root slot is host-allocated under the authority byte and ownership rests on the
        // User<->UserRoot link, which lags during join - without this the local writes are silently denied for a
        // window and you can't move/turn right after joining. -xlinka
        using var permBypass = World?.DataModelPermissions?.EnterSystemBypass();

        EnsurePlatformModule();

        // Always handle mouse look (updates head rotation)
        HandleMouseLook(delta);

        // Apply head/body rotation before movement so basis uses latest look
        UpdateHead();

        HandleDesktopScaling();

        _activeModule?.OnModuleUpdate(delta);
    }

    // Desktop user scaling: ctrl + scroll wheel. Crouch lives on C so ctrl
    // is free as the scale modifier.
    private const float MinUserScale = 0.05f;
    private const float MaxUserScale = 20f;

    // The engine's own sanity limits first, then the role's. Role bounds are only ever a NARROWING of
    // the engine range - a role configured wider than the engine allows does not get to widen it - so
    // the order matters: clamp to the engine range, hand that to the gate, and take what comes back.
    // An unbounded role is a no-op in there and gets the engine range unchanged. -xlinka
    public float ClampUserScale(float requested)
    {
        float scale = System.Math.Clamp(requested, MinUserScale, MaxUserScale);
        var permissions = World?.DataModelPermissions;
        return permissions == null ? scale : permissions.ClampScale(World!.LocalUser, scale);
    }

    private void HandleDesktopScaling()
    {
        if (_inputInterface == null || _inputInterface.IsVRActive)
            return;
        if (_inputInterface.IsDashboardOpen)
            return;
        if (!ScalingEnabled.Value)
            return;

        // The modifier is part of the binding now (stock: ctrl + wheel), so this is one read.
        float scroll = _inputInterface.Actions?.Locomotion.ScaleUser.Value ?? 0f;
        if (scroll == 0f)
            return;

        float factor = MathF.Pow(1.12f, scroll);
        _userRoot.GlobalScale = ClampUserScale(_userRoot.GlobalScale * factor);
    }

    private bool TryInitializeLocalUser()
    {
        if (_initialized)
            return true;

        if (_userRoot == null)
        {
            _userRoot = Slot.GetComponent<UserRoot>();
            if (_userRoot == null)
            {
                if (!_loggedMissingUserRoot)
                {
                    LumoraLogger.Warn("LocomotionController: No UserRoot found!");
                    _loggedMissingUserRoot = true;
                }
                return false;
            }
        }

        if (!_loggedActiveUserState)
        {
            LumoraLogger.Log($"LocomotionController: UserRoot.ActiveUser={_userRoot.ActiveUser?.UserName?.Value ?? "(null)"}, World.LocalUser={World?.LocalUser?.UserName?.Value ?? "(null)"}");
            _loggedActiveUserState = true;
        }

        if (!_userRoot.IsLocalUserRoot)
            return false;

        _characterController = Slot.GetComponent<CharacterController>();
        if (_characterController == null)
        {
            LumoraLogger.Warn("LocomotionController: No CharacterController found!");
            return false;
        }

        _inputState = Slot.GetComponent<UserInputState>() ?? Slot.AttachComponent<UserInputState>();

        _inputInterface = Lumora.Core.Engine.Current?.InputInterface!;
        if (_inputInterface != null)
        {
            _mouse = _inputInterface.Mouse;
            _keyboardDriver = _inputInterface.GetKeyboardDriver();
            LumoraLogger.Log($"LocomotionController: Input bound - Mouse={_mouse != null}, Keyboard={_keyboardDriver != null}");
        }
        else
        {
            LumoraLogger.Warn("[LocomotionController] No InputInterface found - will try late binding");
        }

        EnsureDefaultModules();
        ActivateDefaultModule();

        if (!IsVRActive())
            _inputState.SetMouseCaptureRequested(true);

        LumoraLogger.Log($"LocomotionController: Initialized for user '{_userRoot.ActiveUser?.UserName?.Value}' (VR={IsVRActive()}, Module={_activeModule?.DisplayName})");
        _initialized = true;
        return true;
    }

    // Make sure baseline modules are attached, then pull in anything else
    // already on the slot. PhysicalLocomotion is one module that handles both
    // stick and WASD via LocomotionInputHelper, so we no longer attach a
    // separate VR module or desktop module. - xlinka
    private void EnsureDefaultModules()
    {
        Slot.GetOrAttachComponent<PhysicalLocomotion>();
        // Attach order IS list order, and the default pick is the first usable entry - Walk has to stay
        // first or every user spawns into blink.
        Slot.GetOrAttachComponent<BlinkLocomotion>();
        Slot.GetOrAttachComponent<NoclipLocomotion>();
        Slot.GetOrAttachComponent<NullLocomotionModule>();

        _modules.Clear();
        foreach (var m in Slot.GetComponents<LocomotionModule>())
            _modules.Add(m);
    }

    // Whether a module is both internally eligible (CanActivate) and permitted right now (world-mode
    // gate). Public so UI (the Settings locomotion picker) can grey out a denied module without
    // duplicating the permission check ActivateModule itself enforces.
    public bool IsModuleUsable(LocomotionModule module) => module != null && module.CanActivate() && Permissions.CanUseLocomotion(module);

    // First module in list order that is usable. Mirrors the gate ActivateModule enforces, so a denied
    // module (e.g. noclip in a locked world) is never picked. -xlinka
    private LocomotionModule? FirstUsableModule()
    {
        for (int i = 0; i < _modules.Count; i++)
        {
            if (IsModuleUsable(_modules[i]))
                return _modules[i];
        }
        return null;
    }

    // A persisted preference (set from the Settings screen's locomotion picker) overrides the plain
    // first-usable pick when it still names a usable module by DisplayName - a stale name (renamed
    // module, or denied by the current world's permission gate) just falls through to the default so
    // spawn never strands the user on a mode it can't grant.
    private LocomotionModule? PreferredOrDefaultModule()
    {
        string preferred = EngineSettings.PreferredLocomotion;
        if (!string.IsNullOrEmpty(preferred))
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                var module = _modules[i];
                if (string.Equals(module.DisplayName, preferred, StringComparison.OrdinalIgnoreCase) && IsModuleUsable(module))
                    return module;
            }
        }
        return FirstUsableModule();
    }

    // Spawn in the preferred module if one is set and still usable, otherwise the default
    // (first usable) module.
    private void ActivateDefaultModule()
    {
        var pick = PreferredOrDefaultModule();
        if (pick != null && pick != _activeModule)
            ActivateModule(pick);
    }

    // Keeps a usable module active: the selection is sticky, but if the current
    // module becomes ineligible (permission revoked, VR dropped) fall back to
    // the first usable one. Does not override an eligible user choice.
    private void EnsurePlatformModule()
    {
        // The active module is a sticky choice (menu selection / persisted),
        // never recomputed by priority. Only re-pick when the current module
        // becomes unusable (e.g. permission revoked), advancing to the first
        // usable one.
        if (_activeModule == null || !_activeModule.CanActivate())
        {
            var pick = FirstUsableModule();
            if (pick != null && pick != _activeModule)
                ActivateModule(pick);
        }

        if (!IsVRActive() && !_mouseCaptured)
            CaptureMouse();
    }

    private bool IsVRActive()
    {
        if (_inputInterface == null)
            return false;

        // VR considered active when explicitly flagged and controllers/head tracked
        bool controllersTracked = _inputInterface.LeftController?.IsTracked == true || _inputInterface.RightController?.IsTracked == true;
        bool headTracked = _inputInterface.HeadDevice?.IsTracked == true;
        return _inputInterface.VR_Active && (controllersTracked || headTracked);
    }

    private void HandleMouseLook(float delta)
    {
        // Release/recapture the cursor. The action's own edge replaces the local was-pressed latch,
        // and because it lives in the locomotion set it stays quiet while the dashboard is up - the
        // same key closes the dash there instead of also unlocking the mouse behind it.
        if (_inputInterface?.Actions?.Locomotion.ToggleMouseCapture.Pressed == true)
        {
            _mouseCaptured = !_mouseCaptured;
            _inputState?.SetMouseCaptureRequested(_mouseCaptured);
            LumoraLogger.Log($"[LocomotionController] Mouse capture toggled: {_mouseCaptured}");
        }

        bool freeCam = _inputState?.FreeCamActive ?? false;
        bool lookSuppressed = _inputState?.MouseLookSuppressed ?? false;
        bool inputSuppressed = _inputState?.DesktopInputSuppressed ?? false;

        // Third person still turns the body, just not with raw mouse look: the camera orbits and the
        // avatar comes round to face where it is pointing once you start walking. Runs before the
        // early-out below, which exists to keep the mouse OFF the head in that mode.
        if (IsThirdPerson && !inputSuppressed)
            UpdateThirdPersonFacing();

        if (_mouse == null || !_mouseCaptured || freeCam || lookSuppressed || inputSuppressed)
            return;

        // Use Mouse.DirectDelta - now populated via GodotMouseDriver.HandleInputEvent
        float2 mouseDelta = _mouse.DirectDelta.Value;

        // Update yaw/pitch
        _yaw -= mouseDelta.x * MouseSensitivity;
        _pitch -= mouseDelta.y * MouseSensitivity;
        float maxPitchRad = MaxPitch * (float)System.Math.PI / 180f;
        _pitch = System.Math.Clamp(_pitch, -maxPitchRad, maxPitchRad);
    }

    // THIRD-PERSON BODY ALIGNMENT
    //
    // In third person the mouse drives the orbit, not the head, so nothing was turning the avatar at
    // all: you could spin the camera all the way round and the body stayed pointing wherever it was
    // when you pressed F5, and WASD kept pushing you along that stale direction. The other platform
    // aligns the body to the camera once you are MOVING, after a short dwell, rather than snapping the
    // instant the camera moves - so you can look around a stationary character freely, and the moment
    // you walk, the character comes round to where you are looking.
    //
    // The dwell is what stops a nudge of the mouse from yanking the body. Turn rate scales with how
    // hard you are pushing the stick, so a slow walk turns gently and a sprint turns sharply. -xlinka
    private const float AlignDwellSeconds = 0.15f;
    private const float AlignRate = 8f;
    // How far the head may turn away from the body before it stops following the camera.
    private const float HeadYawLimit = 1.4f;   // radians, ~80 degrees

    // Genuinely in third person, which is NOT the same as "the mouse is not driving the head".
    // MouseLookSuppressed is overloaded: F5 sets it, and so does opening a context menu in FIRST
    // person, because the menu takes the mouse for its own pointer. Keying the third-person body and
    // head work off that flag meant opening a menu while standing in first person made this code turn
    // the avatar to face the camera - which is exactly the "why does opening the context menu rotate
    // me" report. Ask the camera state instead: external camera, and not the free-cam. -xlinka
    private bool IsThirdPerson =>
        _inputState?.ExternalCameraActive == true && _inputState?.FreeCamActive != true;
    private float _alignDwell;
    private float _thirdPersonHeadYaw;

    private void UpdateThirdPersonFacing()
    {
        var input = _inputInterface;
        if (input == null || !input.DesktopCameraPoseValid)
            return;

        float dt = World?.Time.Delta ?? (1f / 60f);
        var axis = LocomotionInputHelper.ReadMovementAxis(input);
        float push = axis.Length;

        // The camera's own yaw and pitch, read in the SAME euler convention the head and body
        // rotations are built from, so no sign guessing.
        var camEuler = input.DesktopCameraRotation.ToEuler();
        float camYaw = camEuler.y;
        float camPitch = camEuler.x;

        // HEAD: tracks the camera every frame, yaw and pitch both. This is the half that makes the
        // avatar look like it answers the mouse, and the half we did not have - the head carries only
        // pitch normally and third person suppressed even that, so looking up and down in F5 moved
        // nothing at all. The yaw is stored relative to the body and CLAMPED, so the head turns to
        // follow the camera round to the side and then stops rather than the neck spinning.
        float maxPitchRad = MaxPitch * (float)System.Math.PI / 180f;
        _pitch = System.Math.Clamp(camPitch, -maxPitchRad, maxPitchRad);
        _thirdPersonHeadYaw = System.Math.Clamp(WrapAngle(camYaw - _yaw), -HeadYawLimit, HeadYawLimit);

        // BODY: only once you are actually walking. Turning it while you stand made the avatar rotate
        // to face the camera as you orbited it, which is not what the other platform does and is not
        // what anyone wants to look at. The dwell keeps a nudge of the stick from yanking it round.
        if (push <= 0.01f)
        {
            _alignDwell = 0f;
            return;
        }

        _alignDwell += dt;
        if (_alignDwell < AlignDwellSeconds)
            return;

        float delta = WrapAngle(camYaw - _yaw);
        _yaw += delta * System.Math.Clamp(AlignRate * dt * System.Math.Clamp(push, 0f, 1f), 0f, 1f);
    }

    // Shortest way round, so a turn across the +/-pi seam does not take the long way.
    private static float WrapAngle(float radians)
    {
        const float twoPi = MathF.PI * 2f;
        radians %= twoPi;
        if (radians > MathF.PI) radians -= twoPi;
        else if (radians < -MathF.PI) radians += twoPi;
        return radians;
    }

    private const float POS_THRESHOLD_SQ = 0.0001f * 0.0001f;
    // Tiny - only skips a truly-still frame. The old 0.0001 gate was ~1.6 deg of rotation: the camera reads the
    // head slot, so any frame under the gate froze the look until the mouse motion piled up past it, then jumped.
    // That stepping IS the mouse-look jitter. Keep it microscopic so the local view tracks the mouse every frame. -xlinka
    private const float ROT_THRESHOLD = 0.0000001f;

    private TransformStreamDriver _rootStreamDriver = null!;

    // When a TransformStreamDriver shares the root slot, the Root stream is the transport for the root rotation,
    // so turn writes must be SILENT or the root would replicate over BOTH the stream and the delta channel.
    // Re-checks until found (the driver is attached during avatar build), then caches. -xlinka
    private bool RootIsStreamed()
    {
        if (_rootStreamDriver == null || _rootStreamDriver.IsDestroyed)
            _rootStreamDriver = Slot?.GetComponent<TransformStreamDriver>()!;
        return _rootStreamDriver != null && !_rootStreamDriver.IsDestroyed;
    }

    private void SetRootRotation(floatQ rotation)
    {
        if (RootIsStreamed())
            Slot.SetGlobalRotationSilently(rotation); // Root stream is the transport - don't also delta it.
        else
            Slot.GlobalRotation = rotation;
    }

    // Desktop look yaws the ROOT, and it has to pivot on the head exactly like a stick turn does - same
    // rotate, same anchor - so the head stays put and the hands and body come round with it. Writing the
    // rotation straight onto the root spins the rig about the root ORIGIN instead, which is only the same
    // point for as long as the head sits directly above it. -xlinka
    private void SetRootRotationAroundHead(floatQ rotation)
    {
        if (_userRoot == null)
        {
            SetRootRotation(rotation);
            return;
        }
        _userRoot.RotateAroundHead((rotation * Slot.GlobalRotation.Inverse).Normalized);
    }

    private void UpdateHead()
    {
        if (_userRoot?.HeadSlot == null)
            return;

        bool headTracked = _inputInterface?.HeadDevice?.IsTracked == true;
        if (headTracked)
            return;

        float userHeight = _inputInterface?.UserHeight ?? InputInterface.DEFAULT_USER_HEIGHT;
        // Crouch lowers the EYE POINT in root space (the capsule no longer sinks the root), which is
        // what makes the avatar actually squat: the head target drops while the floor stays put. -xlinka
        float crouchTarget = _characterController != null && _characterController.IsCrouching
            ? _characterController.CrouchHeight / System.MathF.Max(_characterController.StandingHeight, 0.1f)
            : 1f;
        float dt = World?.Time.Delta ?? (1f / 60f);
        _crouchBlend += (crouchTarget - _crouchBlend) * (1f - System.MathF.Exp(-10f * System.MathF.Max(dt, 1e-4f)));
        float headHeight = (userHeight - InputInterface.EYE_HEAD_OFFSET) * _crouchBlend;
        var newHeadPos = new float3(0, headHeight, 0);

        var currentHeadPos = _userRoot.HeadSlot.LocalPosition.Value;
        if ((newHeadPos - currentHeadPos).LengthSquared > POS_THRESHOLD_SQ)
            _userRoot.HeadSlot.LocalPosition.Value = newHeadPos;

        var newBodyRot = floatQ.FromEuler(new float3(0, _yaw, 0));
        var currentBodyRot = Slot.GlobalRotation;
        float bodyDot = floatQ.Dot(newBodyRot, currentBodyRot);
        if (1.0f - (bodyDot < 0 ? -bodyDot : bodyDot) > ROT_THRESHOLD)
            SetRootRotationAroundHead(newBodyRot);

        // Yaw on the head is third person only: in first person the body IS the look direction, so a
        // head yaw there would double it.
        float headYaw = IsThirdPerson ? _thirdPersonHeadYaw : 0f;
        var newHeadRot = floatQ.FromEuler(new float3(_pitch, headYaw, 0));
        var currentHeadRot = _userRoot.HeadSlot.LocalRotation.Value;
        float headDot = floatQ.Dot(newHeadRot, currentHeadRot);
        if (1.0f - (headDot < 0 ? -headDot : headDot) > ROT_THRESHOLD)
            _userRoot.HeadSlot.LocalRotation.Value = newHeadRot;

        // Drive hand aim on the CONTROLLER slots (hands are grip-offset
        // children of controllers - writing the hand child would bypass the
        // tool/laser). The right hand pitches with the camera; both stay at
        // their resting height.
        //
        // The controller slot used to jump up to chest height whenever a context menu opened. Desktop arms
        // carry NO IK weight (there is no hand tracker to weigh), so the avatar's actual arm cannot follow
        // it - the hand and its laser slid ~30cm up the body on their own while the arm stayed hanging at
        // the side. That is the "hands moving up when laser selecting something" report. The hand stays
        // put now; aiming is TryAimHandAtLaserTarget's job, and it rotates in place.
        //
        // An equipped tool is the one case that DOES lift the hand, and it works because the missing half is
        // there now: HandTool drives the node to a hold pose in front of the head and AvatarIK weights the
        // arm to match, so the arm comes up with it instead of being left behind. That hand owns its node
        // outright while it holds, so hand the rest pose over rather than writing it - two components
        // writing one transform field alternate every frame and the hand jitters between them. -xlinka
        float restPitch = -MathF.PI / 2f;

        var rightController = ResolveControllerSlot(ref _rightControllerSlot, Input.BodyNode.RightController);
        if (rightController != null)
        {
            float aimPitch = MathF.Min(MathF.Max(restPitch + _pitch, -MathF.PI / 2f), 0f);
            var targetRot = floatQ.Euler(-MathF.PI / 2f, aimPitch, 0f);
            var restPos = new float3(0.25f, 1.0f, 0f);

            if (!OfferRestToHandTool(ref _rightHandTool, rightController, restPos, targetRot))
            {
                SetIfChanged(rightController.LocalPosition, restPos);
                if (!TryAimHandAtLaserTarget(rightController, dt))
                    SetIfChanged(rightController.LocalRotation, targetRot);
            }
        }

        var leftController = ResolveControllerSlot(ref _leftControllerSlot, Input.BodyNode.LeftController);
        if (leftController != null)
        {
            var restPos = new float3(-0.25f, 1.0f, 0f);
            var restRot = floatQ.Euler(MathF.PI / 2f, restPitch, 0f);

            if (!OfferRestToHandTool(ref _leftHandTool, leftController, restPos, restRot))
            {
                SetIfChanged(leftController.LocalPosition, restPos);
                SetIfChanged(leftController.LocalRotation, restRot);
            }
        }
    }

    private Interaction.HandTool? _leftHandTool;
    private Interaction.HandTool? _rightHandTool;

    // Give this controller's rest pose to the tool rig hanging off it. True means that rig is currently
    // driving the node (a tool held in view) and this component must keep its hands off it this frame.
    private static bool OfferRestToHandTool(ref Interaction.HandTool? cache, Slot controller,
        in float3 restPosition, in floatQ restRotation)
    {
        if (cache == null || cache.IsDestroyed || !ReferenceEquals(cache.Slot?.Parent, controller))
            cache = controller.GetComponentInChildren<Interaction.HandTool>(includeSelf: false);

        // A tool rig that has stopped updating cannot let go of the node on its own, and its last answer
        // would strand the hand wherever it was. Disabled means we take the node back. -xlinka
        if (cache == null || cache.IsDestroyed || !cache.Enabled.Value || cache.Slot?.IsActive != true)
            return false;

        return cache.OfferRestPose(restPosition, restRotation);
    }

    private Slot? _leftControllerSlot;
    private Slot? _rightControllerSlot;
    private Interaction.InteractionLaser? _rightLaserCache;

    // Rate the pointing hand eases onto the view's aim, in 1/s. Fast enough that the beam lands where you are
    // looking within a couple of frames, slow enough that a flick of the mouse doesn't snap the arm.
    private const float HandAimSmoothing = 10f;

    // Point the pointing hand at whatever the view is aimed at while an external camera is up (F5/F6). The
    // mouse drives the CAMERA in those modes and head look is frozen, so the mouse-pitch rest pose leaves the
    // hand aimed at wherever the head last looked while the beam has to leave it toward the crosshair's
    // target. Take the rotation that carries the laser's own forward onto the hand->target direction and apply
    // it to the controller: the laser rides the controller rigidly, so that is exact in one step rather than a
    // frame-by-frame chase. Work in the parent's frame so body turn and walking don't read as aim error, then
    // ease in. Left hand keeps its rest pose, and VR never reaches here (UpdateHead bails on a tracked head).
    // -xlinka
    private bool TryAimHandAtLaserTarget(Slot controller, float delta)
    {
        if (_inputState?.ExternalCameraActive != true)
            return false;

        var parent = controller.Parent;
        if (parent == null)
            return false;

        if (_rightLaserCache == null || _rightLaserCache.IsDestroyed)
            _rightLaserCache = controller.GetComponentInChildren<Interaction.InteractionLaser>();

        var laser = _rightLaserCache;
        var laserSlot = laser?.Slot;
        if (laser == null || laserSlot == null || !laser.HasAimPoint)
            return false;

        float3 toTarget = laser.AimPoint - laserSlot.GlobalPosition;
        if (toTarget.LengthSquared <= 1e-6f)
            return false;

        float3 wanted = parent.GlobalDirectionToLocal(toTarget.Normalized);
        float3 current = parent.GlobalDirectionToLocal(laserSlot.GlobalRotation * float3.Backward);
        if (wanted.LengthSquared <= 1e-6f || current.LengthSquared <= 1e-6f)
            return false;

        floatQ aimed = Avatar.IK.FabrikSolver.FromToRotation(current.Normalized, wanted.Normalized)
            * controller.LocalRotation.Value;
        float t = System.Math.Clamp(1f - MathF.Exp(-HandAimSmoothing * MathF.Max(delta, 0f)), 0f, 1f);
        SetIfChanged(controller.LocalRotation, floatQ.Slerp(controller.LocalRotation.Value, aimed.Normalized, t));
        return true;
    }

    private Slot? ResolveControllerSlot(ref Slot? cache, Input.BodyNode node)
    {
        if (cache == null || cache.IsDestroyed)
            cache = _userRoot?.GetRegisteredComponent<TrackedDevicePositioner>(p => p.AutoBodyNode.Value == node)?.Slot;
        return cache;
    }

    private static void SetIfChanged(Sync<float3> field, in float3 value)
    {
        if ((field.Value - value).LengthSquared > POS_THRESHOLD_SQ)
            field.Value = value;
    }

    private static void SetIfChanged(Sync<floatQ> field, in floatQ value)
    {
        float dot = floatQ.Dot(field.Value, value);
        if (1.0f - (dot < 0 ? -dot : dot) > ROT_THRESHOLD)
            field.Value = value;
    }

    public void ApplySnapTurn(float deltaYaw)
    {
        _yaw += deltaYaw;

        if (_userRoot != null)
        {
            _userRoot.RotateYawAroundHead(deltaYaw);
            return;
        }

        SetRootRotation((floatQ.AxisAngle(float3.Up, deltaYaw) * Slot.GlobalRotation).Normalized);
    }

    // Take the heading the root is ACTUALLY carrying as the new accumulator value.
    //
    // Desktop look owns the root's yaw: UpdateHead writes _yaw onto it every frame. Anything that poses
    // the rig from outside - a seat turning you to face the way the chair faces, a scripted placement -
    // therefore lasts exactly one frame unless it lands here too, because the next update writes the old
    // yaw straight back over it. Snap turns keep composing on top afterwards, since they add to the same
    // accumulator. -xlinka
    public void SyncYawFromRoot()
    {
        _yaw = Slot.GlobalRotation.ToEuler().y;
    }

    public void GetMovementBasis(out float3 forward, out float3 right)
    {
        // Third person walks relative to the CAMERA. The head does not turn in this mode, so taking the
        // basis off it left movement bound to whichever way you happened to be facing when the mode was
        // entered: you would orbit behind the character and W still walked the old way. -xlinka
        if (IsThirdPerson && _inputInterface?.DesktopCameraPoseValid == true)
        {
            forward = _inputInterface.DesktopCameraRotation * float3.Backward;
        }
        else if (_userRoot != null && _userRoot.HeadSlot != null)
        {
            forward = _userRoot.HeadFacingDirection;
        }
        else if (_inputInterface?.GetBodyNode(Input.BodyNode.Head) is ITrackedDevice headDevice && headDevice.IsTracking)
        {
            forward = headDevice.Rotation * float3.Backward;
        }
        else
        {
            // Desktop fallback: use yaw
            floatQ yawRotation = floatQ.AxisAngle(float3.Up, _yaw);
            forward = yawRotation * float3.Backward;
        }

        // Flatten to horizontal plane (remove vertical component)
        forward.y = 0;
        if (forward.LengthSquared < 1e-4f)
            forward = new float3(0, 0, -1); // Default forward is -Z
        forward = forward.Normalized;

        // Right as perpendicular on horizontal plane (Forward x Up = Right in right-handed system)
        right = float3.Cross(forward, float3.Up);
        if (right.LengthSquared < 1e-4f)
            right = float3.Right;
        right = right.Normalized;
    }

    // Locomotion permissions need the World (for the world's edit mode), which isn't set when the field
    // initializer runs - bind lazily the first time they're consulted. -xlinka
    private LocomotionPermissions Permissions => _permissions ??= new LocomotionPermissions(World);

    public void ActivateModule(LocomotionModule module)
    {
        if (module == null || !Permissions.CanUseLocomotion(module))
            return;
        if (ReferenceEquals(module, _activeModule))
            return;

        _activeModule?.DeactivateInternal();
        _activeModule = module;
        _activeModule.ActivateInternal(this);
    }

    public CharacterController CharacterController => _characterController;

    // noclip flight bypasses the character controller and moves the rig directly
    public UserRoot UserRoot => _userRoot;

    // CLEANUP

    public override void OnDestroy()
    {
        if (_mouseCaptured)
        {
            _inputState?.SetMouseCaptureRequested(false);
            _mouseCaptured = false;
        }

        _inputState?.SetDesktopInputSuppressed(this, false);

        base.OnDestroy();
    }
}

