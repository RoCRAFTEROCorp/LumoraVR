// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Input.Actions;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Input;

public class InputInterface : IDisposable
{
    private class UpdateBucket
    {
        public readonly List<IInputDriver> InputDrivers = new List<IInputDriver>();
        public readonly int Order;

        public UpdateBucket(int order)
        {
            Order = order;
        }
    }

    public const float DEFAULT_USER_HEIGHT = 1.75f;
    public const float EYE_HEAD_OFFSET = 0.125f;

    private Lumora.Core.Engine _engine = null!;
    private List<IInputDriver> _inputDrivers = new List<IInputDriver>();
    private List<UpdateBucket> _inputDriverUpdateBuckets = new List<UpdateBucket>();
    private List<IInputDevice> _inputDevices = new List<IInputDevice>();
    private bool _initialized = false;

    private ITrackedDevice[] _bodyNodes = null!;

    private List<IInputUpdateReceiver> _inputReceivers = new List<IInputUpdateReceiver>();

    public TrackingSpace GlobalTrackingSpace { get; private set; } = new TrackingSpace();

    public Mouse Mouse { get; private set; } = null!;
    public Keyboard Keyboard { get; private set; } = null!;

    // Present whether or not a pad is plugged in; IsConnected says whether one actually is, so
    // bindings on it are inert rather than absent.
    public Gamepad Gamepad { get; private set; } = null!;

    // Everything above the driver layer reads THIS, never a key or a button: which control fires an action is a
    // user setting, not a fact about the code.
    public InputBindingMap Actions { get; private set; } = null!;

    // VR devices (legacy compatibility)
    public VRController LeftController { get; private set; } = null!;
    public VRController RightController { get; private set; } = null!;
    public HeadDevice HeadDevice { get; private set; } = null!;
    // Face/lip tracking. A hardware hook fills this; null-object semantics until then (untracked, all zero).
    public MouthDevice MouthDevice { get; private set; } = null!;

    // VR state - synced from VR drivers automatically
    public bool VR_Active => IsVRActive;
    public float UserHeight { get; set; } = DEFAULT_USER_HEIGHT;

    public bool IsDashboardOpen { get; set; }

    // The platform's clipboard text service, assigned by the runner alongside the other platform
    // services. Null on a platform that has no clipboard (or before the runner has wired one), and
    // every caller has to cope with that rather than assume - the VR keyboard greys its Paste key out
    // on exactly this check. -xlinka
    public IClipboardText? ClipboardText { get; set; }

    // The platform's "what colour is the view showing there" service, assigned by the runner alongside
    // the other platform services. Null on a platform with no view (headless), which is why the
    // eyedropper treats a failed read as "no answer" rather than a bug. -xlinka
    public IViewColorSampler? ViewColorSampler { get; set; }

    // The platform's "give me a picture of what the view is showing" service, assigned by the runner
    // alongside the other platform services. Null on a platform with no view (headless), so a session
    // thumbnail or a saved world's sidecar is simply not written rather than the save failing. -xlinka
    public IViewCapture? ViewCapture { get; set; }

    // The last picture taken with nothing drawn over the world. On desktop the dash composites onto
    // the same viewport the capture reads, so a capture taken while it is open is a picture of the
    // dash, and both pictures people actually want (the session thumbnail, the sidecar beside a save)
    // used to come out that way because saving happens FROM the dash. Callers go through this: a fresh
    // read while the dash is closed, otherwise the newest clean one. Size is whatever the last clean
    // read asked for; both callers ask for the same 256x144. -xlinka
    private byte[]? _lastWorldView;

    public bool TryCaptureWorldView(int width, int height, out byte[] jpeg)
    {
        jpeg = System.Array.Empty<byte>();
        var capture = ViewCapture;
        if (capture == null)
            return false;
        if (!IsDashboardOpen && capture.TryCapture(width, height, out var fresh) && fresh != null && fresh.Length > 0)
        {
            _lastWorldView = fresh;
            jpeg = fresh;
            return true;
        }
        if (_lastWorldView == null || _lastWorldView.Length == 0)
            return false;
        jpeg = _lastWorldView;
        return true;
    }

    // Desktop free-cursor ray, pushed by the platform layer each frame while the
    // OS cursor is unlocked (dash open). World space. The interaction laser uses
    // it as its cast ray so the in-world laser cursor and the mouse are one
    // pointer. Only the local platform layer may write it.
    public bool DesktopCursorRayValid { get; private set; }
    public float3 DesktopCursorRayOrigin { get; private set; }
    public float3 DesktopCursorRayDirection { get; private set; }

    public void SetDesktopCursorRay(bool valid, float3 origin, float3 direction)
    {
        DesktopCursorRayValid = valid;
        DesktopCursorRayOrigin = origin;
        DesktopCursorRayDirection = direction;
    }

    // Userspace-laser arbitration, per controller side. The userspace dash pointer lives in
    // the overlay world and pushes its state here every frame. A game-world hand tool reads it
    // and stands down on that side while the userspace laser is live, so you never get two
    // cursors fighting over the dash and clicks never bleed through into the world behind it.
    // Indexed by (int)Chirality (None=0, Left=1, Right=2). -xlinka
    private readonly bool[] _userspaceLaserActive = new bool[3];
    private readonly bool[] _userspaceLaserHasTarget = new bool[3];

    public void SetUserspaceLaserActive(Chirality side, bool active, bool hasTarget)
    {
        int i = (int)side;
        if (i < 0 || i >= _userspaceLaserActive.Length) return;
        _userspaceLaserActive[i] = active;
        _userspaceLaserHasTarget[i] = hasTarget;
    }

    public bool IsUserspaceLaserActive(Chirality side)
    {
        int i = (int)side;
        return i >= 0 && i < _userspaceLaserActive.Length && _userspaceLaserActive[i];
    }

    public bool UserspaceLaserHasTarget(Chirality side)
    {
        int i = (int)side;
        return i >= 0 && i < _userspaceLaserHasTarget.Length && _userspaceLaserHasTarget[i];
    }

    // True when the dash owns the pointer on EITHER side. On desktop the single mouse cursor
    // drives the dash, so any game-world hand tool should stand down regardless of side. -xlinka
    public bool IsAnyUserspaceLaserActive => _userspaceLaserActive[1] || _userspaceLaserActive[2];

    // Desktop camera projection info (vertical FOV in degrees, viewport aspect,
    // world-space camera pose), pushed alongside the cursor ray. The dash uses
    // it to fit its projected surface to the window. The camera looks along its
    // local -Z.
    public float DesktopCameraFovY { get; private set; } = 70f;
    public float DesktopViewportAspect { get; private set; } = 16f / 9f;
    public float3 DesktopCameraPosition { get; private set; }
    public floatQ DesktopCameraRotation { get; private set; } = floatQ.Identity;
    public bool DesktopCameraPoseValid { get; private set; }

    public void SetDesktopViewInfo(float fovYDegrees, float aspect, float3 cameraPosition, floatQ cameraRotation)
    {
        if (fovYDegrees > 1f)
            DesktopCameraFovY = fovYDegrees;
        if (aspect > 0.1f)
            DesktopViewportAspect = aspect;
        DesktopCameraPosition = cameraPosition;
        DesktopCameraRotation = cameraRotation;
        DesktopCameraPoseValid = true;
    }

    // True when the desktop camera is driven by an OVERRIDE (3rd-person / free-cam) instead of following the
    // local user's head. The dashboard uses this to decide whether it may lock to the live head pose (no
    // override -> exact, zero-lag screen lock) or must fall back to the sampled camera pose. -xlinka
    public bool DesktopCameraHasOverride { get; private set; }

    public void SetDesktopCameraOverride(bool hasOverride) => DesktopCameraHasOverride = hasOverride;

    // True while the desktop view is flown by a camera of its own (third-person orbit, free-cam) instead of
    // riding the local head. Anything that aims with the view has to come off the CAMERA in that state: the
    // head is parked where mouse look last left it and never moves again until first person comes back.
    // -xlinka
    public bool DesktopExternalCameraAim => !IsVRActive && DesktopCameraHasOverride && DesktopCameraPoseValid;

    // World-space aim ray through a point on the desktop view, built from the camera pose + projection the
    // platform pushes each frame - so it follows whichever camera is actually rendering.
    public bool TryGetDesktopViewRay(in float2 viewCoord, out float3 origin, out float3 direction)
    {
        origin = DesktopCameraPosition;
        direction = float3.Backward;
        if (!DesktopCameraPoseValid)
            return false;

        // Camera looks along its local -Z, so the image plane sits one unit down -Z and the half-extents are
        // the FOV tangent (vertical) and that times the aspect (horizontal).
        float tanHalfFov = MathF.Tan(DesktopCameraFovY * 0.5f * (MathF.PI / 180f));
        var local = new float3(
            viewCoord.x * tanHalfFov * DesktopViewportAspect,
            viewCoord.y * tanHalfFov,
            -1f);

        float3 world = DesktopCameraRotation * local;
        float length = world.Length;
        if (length <= 0.0001f)
            return false;

        direction = world / length;
        return true;
    }

    public float3 GlobalTrackingOffset { get; set; } = float3.Zero;
    public float3 CustomTrackingOffset { get; set; } = float3.Zero;

    private IKeyboardDriver _keyboardDriver = null!;
    private IMouseDriver _mouseDriver = null!;
    private IGamepadDriver _gamepadDriver = null!;
    private List<IVRDriver> _vrDrivers = new List<IVRDriver>();
    private HashSet<string> _loggedBodyNodeAssignments = new HashSet<string>();

    public int InputDeviceCount => _inputDevices.Count;

    public bool IsVRActive => _vrDrivers.Any(d => d.IsVRActive);

    public HeadOutputDevice CurrentHeadOutputDevice
    {
        get
        {
            if (_vrDrivers.Any(d => d.IsVRActive))
                return HeadOutputDevice.VR;
            return HeadOutputDevice.Screen;  // Desktop mode
        }
    }

    // Points the tracking space at the focused local user's root. It BINDS the slot rather than copying its
    // transform: the root moves during the frame (turn, walk, teleport) and every world-space device pose
    // read after that has to come out of where the rig actually is, not where it was at the frame boundary.
    // Still called at the frame edges so a world switch re-points it. -xlinka
    public bool SyncTrackingSpaceToFocusedLocalUser()
    {
        UserRoot root = _engine?.WorldManager?.FocusedWorld?.LocalUser?.Root!;
        if (root == null || root.IsDestroyed || root.Slot == null)
            return false;

        if (!ReferenceEquals(GlobalTrackingSpace.Space, root.Slot))
            GlobalTrackingSpace.Space = root.Slot;
        return true;
    }

    public InputInterface()
    {
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        _engine = Engine.Current;

        InitializeBodyNodes();

        Keyboard = new Keyboard();
        RegisterInputDevice(Keyboard, "Keyboard");

        LeftController = new VRController(VRControllerSide.Left);
        RightController = new VRController(VRControllerSide.Right);
        HeadDevice = new HeadDevice();
        MouthDevice = new MouthDevice();

        // The gamepad device exists from the start. A pad plugged in mid-session flips IsConnected
        // and its bindings come alive; nothing has to be built or rebound at that moment.
        Gamepad = new Gamepad();
        RegisterInputDevice(Gamepad, "Gamepad");

        Actions = new InputBindingMap(this);
        LoadBindingOverrides();

        _initialized = true;

        await Task.CompletedTask;
        Logger.Log("InputInterface: Initialized successfully");
    }

    private void InitializeBodyNodes()
    {
        _bodyNodes = new ITrackedDevice[(int)BodyNode.END];

        foreach (BodyNode bodyNode in Enum.GetValues(typeof(BodyNode)))
        {
            if (bodyNode == BodyNode.NONE || bodyNode == BodyNode.END)
                continue;

            int index = (int)bodyNode;
            if (index >= 0 && index < _bodyNodes.Length)
            {
                var trackedObject = new TrackedObject();
                trackedObject.Initialize(this, index, bodyNode.ToString());
                trackedObject.CorrespondingBodyNode = bodyNode;
                trackedObject.IsDeviceActive = false;
                _bodyNodes[index] = trackedObject;
            }
        }
    }

    #region Body Node Access

    public ITrackedDevice GetBodyNode(BodyNode node)
    {
        int index = (int)node;
        if (index < 0 || index >= _bodyNodes.Length)
            return null!;
        return _bodyNodes[index];
    }

    private void UpdateBodyNodeAssignments()
    {
        foreach (BodyNode bodyNode in Enum.GetValues(typeof(BodyNode)))
        {
            if (bodyNode == BodyNode.NONE || bodyNode == BodyNode.END)
                continue;

            int index = (int)bodyNode;
            if (index >= 0 && index < _bodyNodes.Length)
            {
                var current = _bodyNodes[index];
                if (current != null && current is TrackedObject defaultObj)
                {
                    defaultObj.IsDeviceActive = false;
                    defaultObj.IsTracking = false;
                }
            }
        }

        foreach (var device in _inputDevices)
        {
            if (device is ITrackedDevice trackedDevice &&
                trackedDevice.CorrespondingBodyNode != BodyNode.NONE &&
                trackedDevice.IsDeviceActive &&
                trackedDevice.IsTracking)
            {
                int index = (int)trackedDevice.CorrespondingBodyNode;
                if (index >= 0 && index < _bodyNodes.Length)
                {
                    var existing = _bodyNodes[index];
                    if (existing == null ||
                        !existing.IsDeviceActive ||
                        !existing.IsTracking ||
                        existing.Priority < trackedDevice.Priority)
                    {
                        _bodyNodes[index] = trackedDevice;
                        var logKey = $"{device.Name}_{trackedDevice.CorrespondingBodyNode}";
                        if (!_loggedBodyNodeAssignments.Contains(logKey))
                        {
                            _loggedBodyNodeAssignments.Add(logKey);
                            Logger.Log($"InputInterface: Assigned device '{device.Name}' to body node {trackedDevice.CorrespondingBodyNode}");
                        }
                    }
                }
            }
        }

        UpdateBodyNodeFromVRDevice(HeadDevice, BodyNode.Head);
        UpdateBodyNodeFromVRDevice(LeftController, BodyNode.LeftController);
        UpdateBodyNodeFromVRDevice(RightController, BodyNode.RightController);
    }

    private void UpdateBodyNodeFromVRDevice(IInputDevice device, BodyNode node)
    {
        if (device == null || !device.IsDeviceActive)
            return;

        int index = (int)node;
        if (index < 0 || index >= _bodyNodes.Length)
            return;

        if (_bodyNodes[index] is TrackedObject trackedObj)
        {
            if (device is HeadDevice head)
            {
                trackedObj.RawPosition = new float3(head.Position.X, head.Position.Y, head.Position.Z);
                trackedObj.RawRotation = new floatQ(head.Rotation.X, head.Rotation.Y, head.Rotation.Z, head.Rotation.W);
                trackedObj.IsTracking = head.IsTracked;
                trackedObj.IsDeviceActive = true;
                trackedObj.TrackingSpace = GlobalTrackingSpace;
            }
            else if (device is VRController controller)
            {
                trackedObj.RawPosition = new float3(controller.Position.X, controller.Position.Y, controller.Position.Z);
                trackedObj.RawRotation = new floatQ(controller.Rotation.X, controller.Rotation.Y, controller.Rotation.Z, controller.Rotation.W);
                trackedObj.IsTracking = controller.IsTracked;
                trackedObj.IsDeviceActive = true;
                trackedObj.TrackingSpace = GlobalTrackingSpace;

                var handNode = node == BodyNode.LeftController ? BodyNode.LeftHand : BodyNode.RightHand;
                int handIndex = (int)handNode;
                if (handIndex >= 0 && handIndex < _bodyNodes.Length && _bodyNodes[handIndex] is TrackedObject handObj)
                {
                    handObj.RawPosition = trackedObj.RawPosition;
                    handObj.RawRotation = trackedObj.RawRotation;
                    handObj.IsTracking = trackedObj.IsTracking;
                    handObj.IsDeviceActive = true;
                    handObj.TrackingSpace = GlobalTrackingSpace;
                }
            }
        }
    }

    #endregion

    #region Input Update Receivers

    public void RegisterInputEventReceiver(IInputUpdateReceiver receiver)
    {
        if (!_inputReceivers.Contains(receiver))
        {
            _inputReceivers.Add(receiver);
        }
    }

    public void UnregisterInputEventReceiver(IInputUpdateReceiver receiver)
    {
        _inputReceivers.Remove(receiver);
    }

    #endregion

    #region Driver Registration

    public void RegisterInputDriver(IInputDriver driver)
    {
        _inputDrivers.Add(driver);
        driver.RegisterInputs(this);

        UpdateBucket bucket = _inputDriverUpdateBuckets.FirstOrDefault(b => b.Order == driver.UpdateOrder)!;
        if (bucket == null)
        {
            bucket = new UpdateBucket(driver.UpdateOrder);
            _inputDriverUpdateBuckets.Add(bucket);
            _inputDriverUpdateBuckets.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        bucket.InputDrivers.Add(driver);

        Logger.Log($"InputInterface: Registered driver with UpdateOrder {driver.UpdateOrder}");
    }

    public void RegisterKeyboardDriver(IKeyboardDriver keyboardDriver)
    {
        if (_keyboardDriver != null)
            throw new InvalidOperationException("Keyboard Driver is already registered");

        _keyboardDriver = keyboardDriver;
        Logger.Log("InputInterface: Keyboard driver registered");
    }

    public void RegisterMouseDriver(IMouseDriver mouseDriver)
    {
        if (_mouseDriver != null)
            throw new InvalidOperationException("Mouse Driver already registered!");

        Mouse = new Mouse();
        RegisterInputDevice(Mouse, "Mouse");
        _mouseDriver = mouseDriver;

        Logger.Log("InputInterface: Mouse driver registered");
    }

    // Optional: with no driver the Gamepad device never connects and every pad binding stays inert.
    public void RegisterGamepadDriver(IGamepadDriver gamepadDriver)
    {
        if (_gamepadDriver != null)
            throw new InvalidOperationException("Gamepad Driver already registered!");

        _gamepadDriver = gamepadDriver;
        Logger.Log("InputInterface: Gamepad driver registered");
    }

    public IGamepadDriver GetGamepadDriver() => _gamepadDriver;

    // Raised after a device is registered and initialized. A tracker arriving is the case this exists
    // for: its saved mapping lives in local settings, so something has to put the two together at the
    // moment the hardware shows up rather than only when somebody re-calibrates. -xlinka
    public event Action<IInputDevice>? InputDeviceAdded;

    public void RegisterInputDevice(IInputDevice device, string name)
    {
        int deviceIndex = _inputDevices.Count;
        _inputDevices.Add(device);
        device.Initialize(this, deviceIndex, name);

        Logger.Log($"InputInterface: Registered device '{name}' (index {deviceIndex})");

        // A tracker without its saved mapping is a puck that has forgotten it is a foot. Applied here so
        // it happens on EVERY connect, not just the one where the user happened to calibrate: the store
        // keeps mappings for absent trackers precisely so this can re-apply them.
        if (device is ITracker tracker)
        {
            try
            {
                if (TrackerMappingStore.TryApply(tracker))
                    Logger.Debug($"InputInterface: applied saved mapping to tracker '{name}'");
            }
            catch (Exception ex)
            {
                Logger.Error($"InputInterface: failed applying saved tracker mapping for '{name}': {ex.Message}");
            }
        }

        InputDeviceAdded?.Invoke(device);
    }

    public T CreateDevice<T>(string name) where T : IInputDevice, new()
    {
        var device = new T();
        RegisterInputDevice(device, name);
        return device;
    }

    public void RegisterVRDriver(IVRDriver vrDriver)
    {
        if (!_vrDrivers.Contains(vrDriver))
        {
            _vrDrivers.Add(vrDriver);

            if (!_inputDevices.Contains(LeftController))
            {
                RegisterInputDevice(LeftController, "LeftController");
                RegisterInputDevice(RightController, "RightController");
                RegisterInputDevice(HeadDevice, "HeadDevice");
            }

            // If the VR driver also implements IInputDriver, register it to create TrackedObjects
            if (vrDriver is IInputDriver inputDriver)
            {
                RegisterInputDriver(inputDriver);
                Logger.Log($"InputInterface: VR driver '{vrDriver.VRSystemName}' registered as IInputDriver");
            }
            else
            {
                Logger.Log($"InputInterface: VR driver '{vrDriver.VRSystemName}' registered (legacy only)");
            }
        }
    }

    #endregion

    #region Update Loop

    public void ProcessInput(double deltaTime)
    {
        if (!_initialized)
            return;

        UpdateInputs((float)deltaTime);
    }

    // Per-receiver cost, keyed by component type.
    //
    // Every IInputUpdateReceiver - the particle sim, cloth, dynamic bones, both IK solvers, the pose
    // drivers - is dispatched from here and then QUEUED onto the world via RunSynchronously, which never
    // runs inline. So all of it actually executes inside World.Update's synchronous drain, landing in the
    // phase labelled "sync (transport poll, queued actions)". A world full of particles therefore showed
    // up in the profiler as a networking cost, and nothing anywhere broke it down by type. That is the
    // single biggest thing the frame profiler could not see. -xlinka
    private readonly Dictionary<string, ReceiverCost> _receiverCosts = new(StringComparer.Ordinal);
    private readonly object _receiverCostLock = new();

    public static bool ProfileReceivers;

    private sealed class ReceiverCost
    {
        public double Ms;
        public int Count;
    }

    public readonly struct ReceiverProfileEntry
    {
        public readonly string Name;
        public readonly double Ms;
        public readonly int Count;
        public ReceiverProfileEntry(string name, double ms, int count) { Name = name; Ms = ms; Count = count; }
    }

    public void CollectReceiverProfile(List<ReceiverProfileEntry> into)
    {
        lock (_receiverCostLock)
        {
            foreach (var pair in _receiverCosts)
                into.Add(new ReceiverProfileEntry(pair.Key, pair.Value.Ms, pair.Value.Count));
        }
    }

    // Cleared when the callbacks are QUEUED and read after the world has drained them, which is the same
    // frame: Engine.Update runs ProcessInput before WorldManager.Update.
    private void ResetReceiverProfile()
    {
        lock (_receiverCostLock)
            _receiverCosts.Clear();
    }

    private void RecordReceiverCost(IInputUpdateReceiver receiver, long ticks)
    {
        string name = receiver.GetType().Name + " (input)";
        double ms = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        lock (_receiverCostLock)
        {
            if (!_receiverCosts.TryGetValue(name, out var bucket))
            {
                bucket = new ReceiverCost();
                _receiverCosts[name] = bucket;
            }
            bucket.Ms += ms;
            bucket.Count++;
        }
    }

    public void UpdateInputs(float deltaTime)
    {
        if (ProfileReceivers)
            ResetReceiverProfile();

        SyncTrackingSpaceToFocusedLocalUser();

        foreach (var bucket in _inputDriverUpdateBuckets)
        {
            foreach (var driver in bucket.InputDrivers)
            {
                driver.UpdateInputs(deltaTime);
            }
        }

        if (_mouseDriver != null && Mouse != null)
        {
            _mouseDriver.UpdateMouse(Mouse);
        }

        if (_keyboardDriver != null && Keyboard != null)
        {
            _keyboardDriver.UpdateKeyboard(Keyboard);
        }

        foreach (var vrDriver in _vrDrivers)
        {
            vrDriver.UpdateVRDevices(LeftController, RightController, HeadDevice);
        }

        if (_gamepadDriver != null && Gamepad != null)
        {
            _gamepadDriver.UpdateGamepad(Gamepad, deltaTime);
        }

        UpdateBodyNodeAssignments();

        // Actions resolve AFTER every device has this frame's hardware and BEFORE anything reads
        // them, so a receiver and a component update in the same frame agree on what was pressed.
        UpdateActionGates();
        Actions?.Evaluate(deltaTime);

        // Call BeforeInputUpdate on all receivers (TrackedDevicePositioner updates slots here)
        DispatchInputReceivers(before: true);

        DispatchInputReceivers(before: false);
    }

    // Decide which action sets are live this frame.
    // The dashboard asserting the menu set is the whole of "the dash stops you walking": locomotion
    // sits below it in priority, so it stops evaluating and every module underneath reads a resting
    // stick without knowing a panel is up.
    //
    // The per-user suppression requesters (free-cam, a tool holding the cursor, the radial menu)
    // stay where they are rather than becoming gates. They are finer-grained than a whole set -
    // the radial menu freezes MOUSE LOOK but deliberately leaves walking alive - and folding them
    // in here would flatten that distinction. -xlinka
    private void UpdateActionGates()
    {
        var actions = Actions;
        if (actions == null)
            return;

        // A focused text field owns the keyboard outright. Only the keyboard: a controller still
        // works while somebody types.
        actions.TextFocusHeld = Helio.UI.TextInput.Focused != null;

        actions.Menu.Set.Asserting = IsDashboardOpen;
    }

    private void LoadBindingOverrides()
    {
        try
        {
            InputBindingStore.Apply(Actions, Settings.ReadValue<string>(InputBindingStore.SettingsKey, string.Empty));
        }
        catch (Exception ex)
        {
            Logger.Warn($"InputInterface: failed to load control bindings, using defaults: {ex.Message}");
        }
    }

    // Write the current bindings to disk now.
    // Bindings save on change rather than on exit like the rest of the settings. A rebind you cannot
    // undo because you rebound the key that reaches the menu is a trap, and "preview until you quit"
    // is the wrong model for the thing you press to quit. -xlinka
    public void SaveBindingOverrides()
    {
        try
        {
            if (Actions != null)
                Settings.WriteValue(InputBindingStore.SettingsKey, InputBindingStore.Serialize(Actions));
        }
        catch (Exception ex)
        {
            Logger.Warn($"InputInterface: failed to save control bindings: {ex.Message}");
        }
    }

    private void DispatchInputReceivers(bool before)
    {
        // Receivers can unregister or be destroyed as part of world teardown.
        // Iterate a snapshot and prune stale world elements before invoking them.
        //
        // Dispatch in component update order, not registration order: device
        // positioners (-1000000) must write body-node slots before pose nodes
        // (-7500) consume them, and nested pose nodes rely on parent-before-
        // child ordering (EnsureCorrectUpdateOrder) - registration order made
        // both a matter of luck, costing a frame of lag per misordered link.
        var snapshot = _inputReceivers.ToArray();
        Array.Sort(snapshot, (a, b) =>
        {
            int orderA = (a as Component)?.UpdateOrder ?? 0;
            int orderB = (b as Component)?.UpdateOrder ?? 0;
            return orderA.CompareTo(orderB);
        });
        foreach (var receiver in snapshot)
        {
            if (!_inputReceivers.Contains(receiver))
                continue;

            if (ShouldPruneInputReceiver(receiver))
            {
                _inputReceivers.Remove(receiver);
                continue;
            }

            InvokeInputReceiver(receiver, before);

            if (ShouldPruneInputReceiver(receiver))
            {
                _inputReceivers.Remove(receiver);
            }
        }
    }

    private static bool ShouldPruneInputReceiver(IInputUpdateReceiver receiver)
    {
        if (receiver == null)
            return true;

        if (receiver is IWorldElement worldElement)
        {
            var world = worldElement.World;
            return worldElement.IsDestroyed ||
                   world == null ||
                   world.IsDisposed ||
                   world.IsDestroyed ||
                   world.ReferenceController == null;
        }

        return false;
    }

    private void InvokeInputReceiver(IInputUpdateReceiver receiver, bool before)
    {
        void InvokeNow()
        {
            // The engine sampling local hardware to pose the LOCAL user's OWN avatar/body is engine maintenance,
            // not a permission-gated edit - a user always controls their own body. On a freshly-joined client the
            // user's allocation + UserRoot ownership isn't wired the instant the body is built during state
            // replay, so without this the own-body writes (Head/hand transforms, body-node slots) get denied
            // every single frame and flood the log. Bypass perms for the duration of the local input callback;
            // remote users' bodies arrive over the network and are validated separately on the host. -xlinka
            var permissions = (receiver as IWorldElement)?.World?.DataModelPermissions;
            using var bypass = permissions?.EnterSystemBypass();
            try
            {
                if (ProfileReceivers)
                {
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (before)
                        receiver.BeforeInputUpdate();
                    else
                        receiver.AfterInputUpdate();
                    RecordReceiverCost(receiver, System.Diagnostics.Stopwatch.GetTimestamp() - start);
                }
                else if (before)
                {
                    receiver.BeforeInputUpdate();
                }
                else
                {
                    receiver.AfterInputUpdate();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"InputInterface: Error in {(before ? "BeforeInputUpdate" : "AfterInputUpdate")}: {ex.Message}");
            }
        }

        // World components modify Sync state in these callbacks.
        // Route through world sync queue while running so writes happen under the world's
        // implementer lock, avoiding cross-thread lock violations.
        if (receiver is IWorldElement worldElement &&
            worldElement.World != null &&
            !worldElement.World.IsDisposed &&
            !worldElement.World.IsDestroyed &&
            worldElement.World.State == World.WorldState.Running)
        {
            worldElement.World.RunSynchronously(InvokeNow);
            return;
        }

        InvokeNow();
    }

    #endregion

    #region Device Access

    public IInputDevice GetDevice(int index)
    {
        if (index < 0 || index >= _inputDevices.Count)
            return null!;
        return _inputDevices[index];
    }

    public T GetDevice<T>(string name) where T : class, IInputDevice
    {
        return (_inputDevices.FirstOrDefault(d => d.Name == name) as T) ?? null!;
    }

    public T GetDevice<T>(Predicate<T> predicate = null!) where T : class, IInputDevice
    {
        foreach (var device in _inputDevices)
        {
            if (device is T typedDevice && (predicate == null || predicate(typedDevice)))
            {
                return typedDevice;
            }
        }
        return null!;
    }

    public void GetDevices<T>(List<T> list, Predicate<T> predicate = null!) where T : class, IInputDevice
    {
        foreach (var device in _inputDevices)
        {
            if (device is T typedDevice && (predicate == null || predicate(typedDevice)))
            {
                list.Add(typedDevice);
            }
        }
    }

    public IKeyboardDriver GetKeyboardDriver() => _keyboardDriver;
    public IMouseDriver GetMouseDriver() => _mouseDriver;

    #endregion

    #region Disposal

    public void Dispose()
    {
        if (!_initialized)
            return;

        _inputReceivers.Clear();

        _inputDrivers.Clear();
        _inputDriverUpdateBuckets.Clear();
        _vrDrivers.Clear();

        _inputDevices.Clear();

        _bodyNodes = null!;

        _keyboardDriver = null!;
        _mouseDriver = null!;
        _gamepadDriver = null!;
        ClipboardText = null;
        ViewColorSampler = null;
        ViewCapture = null;
        Actions = null!;
        Mouse = null!;
        Keyboard = null!;
        Gamepad = null!;
        LeftController = null!;
        RightController = null!;
        HeadDevice = null!;
        MouthDevice = null!;

        _initialized = false;
        Logger.Log("InputInterface: Disposed");
    }

    #endregion
}
