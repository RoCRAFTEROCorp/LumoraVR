// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Source.Input;
using System.Numerics;
using System;
using System.Collections.Generic;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Input.Drivers;

public class GodotVRDriver : IVRDriver, IInputDriver
{
    public int UpdateOrder => 0;

    // IVRDriver interface properties
    public bool IsRuntimeInitialized => _xrInterface != null && _xrInterface.IsInitialized();
    public bool IsVRActive => _modeActive && IsRuntimeInitialized;
    public string VRSystemName => _xrInterface?.GetName() ?? "None";
    public VRPlatform Platform => DetectPlatform();
    public string RuntimeName => GetRuntimeName();
    public string LeftInteractionProfile  => GetTrackerInteractionProfile("left_hand");
    public string RightInteractionProfile => GetTrackerInteractionProfile("right_hand");

    private XRInterface _xrInterface = null!;
    private InputInterface _inputInterface = null!;
    private bool _modeActive;
    private bool _trackerSignalsConnected;

    // Godot XR nodes for tracking (proper Godot 4.x pattern)
    private XRCamera3D _xrCamera = null!;
    private XRController3D _leftController = null!;
    private XRController3D _rightController = null!;
    private XROrigin3D _xrOrigin = null!;

    // Tracked devices for body node system
    private TrackedObject _headTrackedObject = null!;
    private TrackedObject _leftControllerTrackedObject = null!;
    private TrackedObject _rightControllerTrackedObject = null!;
    private TrackedObject _leftHandTrackedObject = null!;
    private TrackedObject _rightHandTrackedObject = null!;
    private readonly Dictionary<BodyNode, TrackedObject> _handSkeletonTrackedObjects = new();

    // Body trackers (the pucks).
    //
    // Godot registers one XRPositionalTracker per top-level path in the action map when the OpenXR
    // session starts, puck or no puck, so "tracker exists" means nothing. A candidate becomes a device
    // only when the runtime binds an interaction profile to its path (profile_changed) or streams a pose
    // for it; it drops back to a candidate when the profile is unbound and to nothing on tracker_removed.
    // The frame loop walks the live list only, so a rig without pucks pays one Count compare.
    //
    // UniqueID is the tracker name, which is the OpenXR role path. Godot exposes no serial or persistent
    // path for a tracker and the runtime keeps role-per-serial itself, so the role path is the most
    // stable identity on offer. It still never leaves the machine as-is. -xlinka
    private sealed class TrackerEntry
    {
        public string Name = string.Empty;
        public XRPositionalTracker Tracker = null!;
        public XRPose? Pose;
        public TrackerDevice? Device;
        public bool Live;
        public bool WantsLive;
        public bool PoseSubscribed;
        public XRPositionalTracker.ProfileChangedEventHandler ProfileHandler = null!;
        public XRPositionalTracker.PoseChangedEventHandler PoseHandler = null!;
    }

    private const string TrackerRolePathPrefix = "/user/vive_tracker_htcx/role/";

    // Below the head and controllers (100) so a puck mapped onto a node one of those owns can never win
    // it, above the controller-derived hand estimate (50) because a puck is a measurement, not a guess.
    // The finger skeleton's 120 is irrelevant: nothing lets a puck map to a finger. -xlinka
    private const int TrackerPriority = 90;

    private static readonly StringName DefaultPoseName = new StringName("default");

    private readonly Dictionary<string, TrackerEntry> _trackerCandidates = new();
    private readonly List<TrackerEntry> _liveTrackers = new();

    // InputInterface cannot unregister a device, so a puck that leaves and returns gets its old device
    // back instead of a second one with the same identity.
    private readonly Dictionary<string, TrackerDevice> _trackerDevices = new();

    private static readonly FingerType[] FingerOrder = new[]
    {
        FingerType.Thumb,
        FingerType.Index,
        FingerType.Middle,
        FingerType.Ring,
        FingerType.Pinky
    };

    private static readonly FingerSegmentType[] ThumbSegments = new[]
    {
        FingerSegmentType.Metacarpal,
        FingerSegmentType.Proximal,
        FingerSegmentType.Distal,
        FingerSegmentType.Tip
    };

    private static readonly FingerSegmentType[] FingerSegments = new[]
    {
        FingerSegmentType.Metacarpal,
        FingerSegmentType.Proximal,
        FingerSegmentType.Intermediate,
        FingerSegmentType.Distal,
        FingerSegmentType.Tip
    };

    // Picks up the XR interface that PhaseXRDetection() already initialized, hooks tracker add/remove,
    // and walks the trackers the runtime registered before anyone was listening.
    public void InitializeVR()
    {
        _xrInterface = XRServer.PrimaryInterface;

        if (_xrInterface != null && _xrInterface.IsInitialized())
        {
            _modeActive = true;
            GD.Print($"GodotVRDriver: VR active - interface: {_xrInterface.GetName()}");

            // Log the resolved platform + runtime + active interaction profile
            // so it's obvious at boot whether we're on Quest, Pico, SteamVR PC,
            // etc. Useful for gameplay code that branches on Platform, and for
            // remote debugging on standalone where the only log surface is adb.
            // - xlinka
            GD.Print($"GodotVRDriver: Platform={Platform}, Runtime='{RuntimeName}', " +
                     $"LeftProfile='{LeftInteractionProfile}', RightProfile='{RightInteractionProfile}'");

            if (!_trackerSignalsConnected)
            {
                XRServer.TrackerAdded += OnTrackerAdded;
                XRServer.TrackerRemoved += OnTrackerRemoved;
                _trackerSignalsConnected = true;
            }

            DiscoverTrackers();
        }
        else
        {
            _modeActive = false;
            GD.Print("GodotVRDriver: No VR interface active - running in desktop mode");
        }
    }

    public void SetModeActive(bool active)
    {
        var newActive = active && IsRuntimeInitialized;
        if (_modeActive != newActive)
            GD.Print($"GodotVRDriver: Mode active = {newActive} (runtime initialized: {IsRuntimeInitialized})");

        _modeActive = newActive;

        if (!_modeActive)
            ClearTrackingState();
    }

    public void LogRuntimeDiagnostics()
    {
        global::Godot.GD.Print($"GodotVRDriver: Runtime='{RuntimeName}', System='{VRSystemName}', Platform={Platform}, Active={IsVRActive}");
        global::Godot.GD.Print($"GodotVRDriver: HMD='{GetTrackerDescription("head")}', Left='{GetTrackerDescription("left_hand")}', Right='{GetTrackerDescription("right_hand")}'");
        global::Godot.GD.Print($"GodotVRDriver: Profiles Left='{LeftInteractionProfile}', Right='{RightInteractionProfile}'");
    }

    private void OnTrackerAdded(StringName trackerName, long type)
    {
        var tracker = XRServer.GetTracker(trackerName);
        if (tracker == null) return;

        string desc = tracker is XRPositionalTracker pt ? pt.Description : tracker.Name.ToString();
        global::Godot.GD.Print($"GodotVRDriver: Tracker connected [{(XRServer.TrackerType)type}] '{trackerName}' - {desc}");

        if ((XRServer.TrackerType)type == XRServer.TrackerType.Head)
            global::Godot.GD.Print($"GodotVRDriver: HMD device: {desc}");

        if (tracker is XRPositionalTracker positional)
            TrackCandidate(positional);
    }

    private void OnTrackerRemoved(StringName trackerName, long type)
    {
        if (!_trackerCandidates.Remove(trackerName.ToString(), out var entry))
            return;

        bool wasLive = entry.Live;
        DetachCandidate(entry);
        if (wasLive)
            LumoraLogger.Log($"GodotVRDriver: body tracker {entry.Device?.Name} removed");
    }

    // Vive pucks arrive typed Controller with the role path as their name; the type alone cannot tell
    // them from a hand controller, the name can. Everything Godot types as a head, hand, body, face,
    // anchor or base station is something else's job.
    private static bool IsBodyTracker(XRTracker tracker, string name)
    {
        if (tracker is not XRPositionalTracker)
            return false;

        switch (tracker.Type)
        {
            case XRServer.TrackerType.Head:
            case XRServer.TrackerType.Hand:
            case XRServer.TrackerType.Body:
            case XRServer.TrackerType.Face:
            case XRServer.TrackerType.Anchor:
            case XRServer.TrackerType.Basestation:
                return false;
        }

        return name.StartsWith(TrackerRolePathPrefix, StringComparison.Ordinal);
    }

    private void DiscoverTrackers()
    {
        if (!IsRuntimeInitialized)
            return;

        using var trackers = XRServer.GetTrackers((int)XRServer.TrackerType.Any);
        foreach (var pair in trackers)
        {
            if (pair.Value.AsGodotObject() is XRPositionalTracker tracker)
                TrackCandidate(tracker);
        }
    }

    private void TrackCandidate(XRPositionalTracker tracker)
    {
        string name = tracker.Name.ToString();
        if (!IsBodyTracker(tracker, name) || _trackerCandidates.ContainsKey(name))
            return;

        var entry = new TrackerEntry { Name = name, Tracker = tracker };
        entry.ProfileHandler = profile => OnTrackerProfileChanged(entry, profile);
        entry.PoseHandler = pose => OnTrackerPoseChanged(entry, pose);
        tracker.ProfileChanged += entry.ProfileHandler;
        tracker.PoseChanged += entry.PoseHandler;
        entry.PoseSubscribed = true;
        _trackerCandidates[name] = entry;

        LumoraLogger.Debug($"GodotVRDriver: tracker candidate '{name}' profile='{tracker.Profile}'");

        if (!string.IsNullOrEmpty(tracker.Profile))
            SetTrackerLive(entry, true);
    }

    private void OnTrackerProfileChanged(TrackerEntry entry, string profile)
    {
        SetTrackerLive(entry, !string.IsNullOrEmpty(profile));
    }

    // Runtimes that never bind a profile to the puck still stream poses; the first one is the connect.
    private void OnTrackerPoseChanged(TrackerEntry entry, XRPose pose)
    {
        if (entry.Live || pose == null || pose.Name != DefaultPoseName)
            return;

        entry.Pose = pose;
        SetTrackerLive(entry, true);
    }

    private void SetTrackerLive(TrackerEntry entry, bool live)
    {
        if (live)
        {
            if (entry.Live)
                return;

            if (_inputInterface == null)
            {
                entry.WantsLive = true;
                return;
            }

            entry.Device ??= AcquireTrackerDevice(entry.Name);
            entry.Device.IsDeviceActive = true;
            entry.Live = true;
            entry.WantsLive = false;
            _liveTrackers.Add(entry);

            // The frame loop reads the pose from here on; the signal would be a second copy per frame.
            if (entry.PoseSubscribed)
            {
                entry.Tracker.PoseChanged -= entry.PoseHandler;
                entry.PoseSubscribed = false;
            }

            LumoraLogger.Log($"GodotVRDriver: body tracker {entry.Device.Name} online, suggested role {entry.Device.SuggestedRole}");
            return;
        }

        entry.WantsLive = false;
        if (!entry.Live)
            return;

        entry.Live = false;
        entry.Pose = null;
        _liveTrackers.Remove(entry);
        entry.Device?.SetOffline();

        if (!entry.PoseSubscribed && GodotObject.IsInstanceValid(entry.Tracker))
        {
            entry.Tracker.PoseChanged += entry.PoseHandler;
            entry.PoseSubscribed = true;
        }

        LumoraLogger.Log($"GodotVRDriver: body tracker {entry.Device?.Name} offline");
    }

    private TrackerDevice AcquireTrackerDevice(string uniqueId)
    {
        if (_trackerDevices.TryGetValue(uniqueId, out var existing))
            return existing;

        var device = new TrackerDevice();
        device.SetIdentity(uniqueId);

        string publicId = device.PublicID;
        string shortId = publicId.Length > 12 ? publicId.Substring(0, 12) : publicId;
        _inputInterface.RegisterInputDevice(device, "VR_Tracker_" + shortId);

        var role = TrackerRoles.FromRolePath(uniqueId);
        device.SuggestedRole = role;

        // NOT auto-mapped from the role. A puck that has never been calibrated drives NOTHING: its
        // offset would be identity, so the body node would snap to the raw puck pose and the avatar
        // would look broken in a way that reads as a bug rather than as "you have not calibrated yet".
        // The role is a suggestion for the calibrator to break ties with, not an assignment. A saved
        // mapping is applied separately, by InputInterface at registration. -xlinka
        device.CorrespondingBodyNode = BodyNode.NONE;
        device.Priority = TrackerPriority;
        _trackerDevices[uniqueId] = device;
        return device;
    }

    // Candidates that went live before RegisterInputs handed us an InputInterface to put devices in.
    private void ActivatePendingTrackers()
    {
        if (_trackerCandidates.Count == 0)
            return;

        foreach (var entry in _trackerCandidates.Values)
        {
            if (entry.WantsLive)
                SetTrackerLive(entry, true);
        }
    }

    private void DetachCandidate(TrackerEntry entry)
    {
        if (entry.Live)
        {
            entry.Live = false;
            _liveTrackers.Remove(entry);
        }

        entry.WantsLive = false;
        entry.Pose = null;
        entry.Device?.SetOffline();

        if (GodotObject.IsInstanceValid(entry.Tracker))
        {
            entry.Tracker.ProfileChanged -= entry.ProfileHandler;
            if (entry.PoseSubscribed)
                entry.Tracker.PoseChanged -= entry.PoseHandler;
        }

        entry.PoseSubscribed = false;
        entry.Tracker = null!;
    }

    // Live pucks only. The cached XRPose is the tracker's own pose object, updated in place by the
    // runtime (set_pose reuses it, invalidate_pose flips has_tracking_data on it), so this reads a few
    // properties and one transform and allocates nothing. Confidence None means no valid pose this
    // frame and the device says so; Low is the runtime extrapolating and still counts as tracking,
    // with the level exposed for anyone who wants to be stricter. -xlinka
    private void UpdateTrackerPoses()
    {
        int count = _liveTrackers.Count;
        if (count == 0)
            return;

        var space = _inputInterface.GlobalTrackingSpace;
        for (int i = 0; i < count; i++)
        {
            var entry = _liveTrackers[i];
            var device = entry.Device!;
            device.TrackingSpace = space;
            device.IsDeviceActive = true;

            var pose = entry.Pose ??= entry.Tracker.GetPose(DefaultPoseName);
            if (pose == null || !pose.HasTrackingData)
            {
                device.SetUntracked();
                continue;
            }

            var confidence = pose.TrackingConfidence;
            if (confidence == XRPose.TrackingConfidenceEnum.None)
            {
                device.SetUntracked();
                continue;
            }

            var transform = pose.Transform;
            var origin = transform.Origin;
            var quat = transform.Basis.GetRotationQuaternion();
            device.SetPose(
                new float3(origin.X, origin.Y, origin.Z),
                new floatQ(quat.X, quat.Y, quat.Z, quat.W),
                confidence == XRPose.TrackingConfidenceEnum.High ? 1f : 0.5f);
        }
    }

    // Platform / interaction profile introspection
    //
    // OpenXRInterface exposes `runtime_name` as a registered Godot property,
    // and XRPositionalTracker exposes the active OpenXR interaction profile
    // path via `Profile`. We go through Get() so this stays compatible across
    // minor Godot versions that may not expose the same C# bindings.
    // - xlinka

    private string GetRuntimeName()
    {
        if (_xrInterface == null) return string.Empty;
        var v = _xrInterface.Get("runtime_name");
        return v.AsString() ?? string.Empty;
    }

    private static string GetTrackerInteractionProfile(string trackerName)
    {
        var tracker = XRServer.GetTracker(trackerName);
        if (tracker is not XRPositionalTracker pt) return string.Empty;
        var profile = pt.Profile;
        return string.IsNullOrEmpty(profile) ? string.Empty : profile;
    }

    private static string GetTrackerDescription(string trackerName)
    {
        var tracker = XRServer.GetTracker(trackerName);
        if (tracker == null) return "none";
        if (tracker is XRPositionalTracker positional && !string.IsNullOrWhiteSpace(positional.Description))
            return positional.Description;
        var name = tracker.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? trackerName : name;
    }

    private VRPlatform DetectPlatform()
    {
        if (!IsVRActive) return VRPlatform.None;

        // PC OpenXR runtimes (SteamVR, Oculus PC, WMR, Monado, Varjo, ...)
        // all share the same desktop bucket - mobile/standalone is the
        // distinction that actually changes rendering and feature defaults.
        if (!OS.HasFeature("android")) return VRPlatform.DesktopOpenXR;

        var runtime = GetRuntimeName().ToLowerInvariant();
        if (runtime.Contains("oculus") || runtime.Contains("meta"))     return VRPlatform.MetaQuestStandalone;
        if (runtime.Contains("pico")   || runtime.Contains("bytedance")) return VRPlatform.PicoStandalone;
        if (runtime.Contains("vive")   || runtime.Contains("htc")
                                       || runtime.Contains("focus"))    return VRPlatform.ViveFocusStandalone;

        // Fall back to interaction profile sniffing if the runtime name
        // didn't give us anything (some early standalone runtimes report
        // generic strings until the first controller binds).
        var profiles = (LeftInteractionProfile + "|" + RightInteractionProfile).ToLowerInvariant();
        if (profiles.Contains("oculus"))                                return VRPlatform.MetaQuestStandalone;
        if (profiles.Contains("bytedance") || profiles.Contains("pico")) return VRPlatform.PicoStandalone;
        if (profiles.Contains("htc")       || profiles.Contains("focus3")) return VRPlatform.ViveFocusStandalone;

        return VRPlatform.DesktopOpenXR;
    }

    // XRController3D nodes MUST exist for Godot to track controllers via OpenXR.
    public void FindXRNodes(Node? sceneRoot)
    {
        if (sceneRoot == null) return;

        _xrOrigin = FindNodeOfType<XROrigin3D>(sceneRoot);
        _xrCamera = FindNodeOfType<XRCamera3D>(sceneRoot);

        foreach (var node in GetAllNodes(sceneRoot))
        {
            if (node is XRController3D controller)
            {
                var trackerStr = controller.Tracker.ToString();
                global::Godot.GD.Print($"GodotVRDriver: Found XRController3D '{controller.Name}' with tracker '{trackerStr}'");

                if (trackerStr.Contains("left") || trackerStr == "left_hand")
                    _leftController = controller;
                else if (trackerStr.Contains("right") || trackerStr == "right_hand")
                    _rightController = controller;
            }
        }

        if (_xrOrigin == null || _leftController == null || _rightController == null)
        {
            global::Godot.GD.Print("GodotVRDriver: XR nodes not found, creating them...");
            CreateXRNodes(sceneRoot);
        }

        global::Godot.GD.Print($"GodotVRDriver: XR nodes ready - Origin:{_xrOrigin != null}, Camera:{_xrCamera != null}, Left:{_leftController != null}, Right:{_rightController != null}");
    }

    private void CreateXRNodes(Node sceneRoot)
    {
        if (_xrOrigin == null)
        {
            _xrOrigin = new XROrigin3D();
            _xrOrigin.Name = "XROrigin3D";
            sceneRoot.AddChild(_xrOrigin);
            global::Godot.GD.Print("GodotVRDriver: Created XROrigin3D");
        }

        if (_xrCamera == null)
        {
            _xrCamera = new XRCamera3D();
            _xrCamera.Name = "XRCamera3D";
            _xrOrigin.AddChild(_xrCamera);
            global::Godot.GD.Print("GodotVRDriver: Created XRCamera3D");
        }

        if (_leftController == null)
        {
            _leftController = new XRController3D();
            _leftController.Name = "LeftController";
            _leftController.Tracker = new StringName("left_hand");
            _xrOrigin.AddChild(_leftController);
            global::Godot.GD.Print("GodotVRDriver: Created LeftController (left_hand)");
        }

        if (_rightController == null)
        {
            _rightController = new XRController3D();
            _rightController.Name = "RightController";
            _rightController.Tracker = new StringName("right_hand");
            _xrOrigin.AddChild(_rightController);
            global::Godot.GD.Print("GodotVRDriver: Created RightController (right_hand)");
        }
    }

    private T FindNodeOfType<T>(Node node) where T : Node
    {
        if (node is T result)
            return result;
        foreach (var child in node.GetChildren())
        {
            var found = FindNodeOfType<T>(child);
            if (found != null)
                return found;
        }
        return null!;
    }

    private System.Collections.Generic.IEnumerable<Node> GetAllNodes(Node node)
    {
        yield return node;
        foreach (var child in node.GetChildren())
        {
            foreach (var descendant in GetAllNodes(child))
                yield return descendant;
        }
    }

    public void RegisterInputs(InputInterface inputInterface)
    {
        _inputInterface = inputInterface;

        _headTrackedObject = inputInterface.CreateDevice<TrackedObject>("VR_Head");
        _headTrackedObject.CorrespondingBodyNode = BodyNode.Head;
        _headTrackedObject.Priority = 100; // High priority for actual VR tracking

        _leftControllerTrackedObject = inputInterface.CreateDevice<TrackedObject>("VR_LeftController");
        _leftControllerTrackedObject.CorrespondingBodyNode = BodyNode.LeftController;
        _leftControllerTrackedObject.Priority = 100;

        _rightControllerTrackedObject = inputInterface.CreateDevice<TrackedObject>("VR_RightController");
        _rightControllerTrackedObject.CorrespondingBodyNode = BodyNode.RightController;
        _rightControllerTrackedObject.Priority = 100;

        // Hand devices ride the controller tracking; there is no separate hand-tracking source.
        _leftHandTrackedObject = inputInterface.CreateDevice<TrackedObject>("VR_LeftHand");
        _leftHandTrackedObject.CorrespondingBodyNode = BodyNode.LeftHand;
        _leftHandTrackedObject.Priority = 50; // Lower priority than controller

        _rightHandTrackedObject = inputInterface.CreateDevice<TrackedObject>("VR_RightHand");
        _rightHandTrackedObject.CorrespondingBodyNode = BodyNode.RightHand;
        _rightHandTrackedObject.Priority = 50;

        RegisterHandSkeletonDevices(inputInterface);

        DiscoverTrackers();
        ActivatePendingTrackers();

        Lumora.Core.Logging.Logger.Log($"GodotVRDriver: Registered VR tracked devices - Head:{_headTrackedObject?.Name}, LeftController:{_leftControllerTrackedObject?.Name}, RightController:{_rightControllerTrackedObject?.Name}");
    }

    private int _debugLogCounter = 0;

    public void UpdateInputs(float deltaTime)
    {
        if (_inputInterface == null)
            return;

        if (!IsVRActive)
        {
            ClearTrackingState();
            return;
        }

        UpdateHeadTracking();

        UpdateControllerTracking(_leftControllerTrackedObject, _leftHandTrackedObject, Chirality.Left);
        UpdateControllerTracking(_rightControllerTrackedObject, _rightHandTrackedObject, Chirality.Right);

        UpdateTrackerPoses();

        // IMPORTANT: Update VRController inputs (thumbsticks, buttons, triggers)
        // This is separate from TrackedObject tracking
        if (_inputInterface.LeftController != null)
            UpdateControllerInputs(_inputInterface.LeftController);
        if (_inputInterface.RightController != null)
            UpdateControllerInputs(_inputInterface.RightController);

        UpdateHandSkeletonTracking(Chirality.Left);
        UpdateHandSkeletonTracking(Chirality.Right);

        _debugLogCounter++;
        if (_debugLogCounter >= 60)
        {
            _debugLogCounter = 0;
            var leftPos = _leftControllerTrackedObject?.RawPosition ?? Lumora.Core.Math.float3.Zero;
            var rightPos = _rightControllerTrackedObject?.RawPosition ?? Lumora.Core.Math.float3.Zero;
            var leftTrack = _leftControllerTrackedObject?.IsTracking ?? false;
            var rightTrack = _rightControllerTrackedObject?.IsTracking ?? false;
            var leftThumb = _inputInterface.LeftController?.ThumbstickPosition ?? default;
            // global::Godot.GD.Print($"[VR] L:{leftPos} track:{leftTrack} | R:{rightPos} track:{rightTrack} | Thumb:({leftThumb.X:F2},{leftThumb.Y:F2}) | XRNodes L:{_leftController != null} R:{_rightController != null}");
        }
    }

    private void RegisterHandSkeletonDevices(InputInterface inputInterface)
    {
        RegisterHandSkeletonDevice(inputInterface, BodyNode.LeftPalm);
        RegisterHandSkeletonDevice(inputInterface, BodyNode.RightPalm);

        foreach (var chirality in new[] { Chirality.Left, Chirality.Right })
        {
            foreach (var finger in FingerOrder)
            {
                var segments = finger == FingerType.Thumb ? ThumbSegments : FingerSegments;
                for (int i = 0; i < segments.Length; i++)
                {
                    RegisterHandSkeletonDevice(inputInterface, finger.ComposeFinger(segments[i], chirality));
                }
            }
        }
    }

    private void RegisterHandSkeletonDevice(InputInterface inputInterface, BodyNode node)
    {
        if (_handSkeletonTrackedObjects.ContainsKey(node))
            return;

        var tracked = inputInterface.CreateDevice<TrackedObject>($"VR_{node}");
        tracked.CorrespondingBodyNode = node;
        tracked.Priority = 120;
        _handSkeletonTrackedObjects[node] = tracked;
    }

    private void UpdateHandSkeletonTracking(Chirality side)
    {
        var controllerObj = side == Chirality.Left ? _leftControllerTrackedObject : _rightControllerTrackedObject;
        if (controllerObj == null)
            return;

        bool isTracking = controllerObj.IsTracking;
        if (!isTracking)
        {
            SetHandSkeletonTrackingState(side, false);
            return;
        }

        float3 controllerPos = controllerObj.RawPosition;
        floatQ controllerRot = controllerObj.RawRotation;
        var xrController = side == Chirality.Left ? _leftController : _rightController;
        var vrController = side == Chirality.Left ? _inputInterface?.LeftController : _inputInterface?.RightController;

        float trigger = Clamp01(vrController?.TriggerValue ?? 0f);
        float grip = Clamp01(vrController?.GripValue ?? 0f);
        float squeezeForce = 0f;
        float thumbAxis = 0f;
        bool triggerTouch = false;
        bool thumbTouch = false;

        if (xrController != null && GodotObject.IsInstanceValid(xrController))
        {
            trigger = MathF.Max(trigger, Clamp01(ReadFloatAction(xrController, "trigger")));
            grip = MathF.Max(grip, Clamp01(ReadFloatAction(xrController, "grip")));
            grip = MathF.Max(grip, Clamp01(ReadFloatAction(xrController, "grip_force")));
            grip = MathF.Max(grip, Clamp01(ReadFloatAction(xrController, "squeeze")));
            grip = MathF.Max(grip, Clamp01(ReadFloatAction(xrController, "squeeze_force")));
            squeezeForce = Clamp01(ReadFloatAction(xrController, "grip_force"));
            squeezeForce = MathF.Max(squeezeForce, Clamp01(ReadFloatAction(xrController, "squeeze_force")));

            var thumb = xrController.GetVector2("primary");
            thumbAxis = thumb.X;

            triggerTouch = ReadBoolAction(xrController, "trigger_touch");
            thumbTouch = ReadBoolAction(xrController, "primary_touch")
                || ReadBoolAction(xrController, "secondary_touch")
                || ReadBoolAction(xrController, "ax_touch")
                || ReadBoolAction(xrController, "by_touch")
                || ReadBoolAction(xrController, "thumbstick_touch")
                || ReadBoolAction(xrController, "trackpad_touch");
        }

        float indexCurl = Clamp01(trigger * 0.92f + (triggerTouch ? 0.18f : 0f));
        float baseCurl = Clamp01(MathF.Max(grip, squeezeForce));
        float middleCurl = Clamp01(baseCurl * 0.95f);
        float ringCurl = Clamp01(baseCurl * 1.02f + 0.03f);
        float pinkyCurl = Clamp01(baseCurl * 1.08f + 0.05f);
        float thumbCurl = Clamp01(baseCurl * 0.45f + trigger * 0.30f + (thumbTouch ? 0.20f : 0f));
        float splay = Clamp01(MathF.Abs(thumbAxis)) * MathF.Sign(thumbAxis) * 0.18f;
        float handScale = 1.12f;

        BodyNode palmNode = side == Chirality.Left ? BodyNode.LeftPalm : BodyNode.RightPalm;
        float3 palmLocal = MirrorForSide(new float3(0.0000f, -0.0150f, -0.0350f), side, handScale);
        SetHandNodePose(
            palmNode,
            Add(controllerPos, controllerRot * palmLocal),
            controllerRot,
            true);

        WriteFingerPose(side, FingerType.Thumb, thumbCurl, -splay * 0.50f, controllerPos, controllerRot, handScale);
        WriteFingerPose(side, FingerType.Index, indexCurl, splay * 0.70f, controllerPos, controllerRot, handScale);
        WriteFingerPose(side, FingerType.Middle, middleCurl, splay * 0.20f, controllerPos, controllerRot, handScale);
        WriteFingerPose(side, FingerType.Ring, ringCurl, -splay * 0.25f, controllerPos, controllerRot, handScale);
        WriteFingerPose(side, FingerType.Pinky, pinkyCurl, -splay * 0.75f, controllerPos, controllerRot, handScale);
    }

    private void WriteFingerPose(
        Chirality side,
        FingerType finger,
        float curl,
        float splay,
        float3 controllerPos,
        floatQ controllerRot,
        float handScale)
    {
        var openPoints = GetOpenPosePoints(finger);
        var closedPoints = GetClosedPosePoints(finger);
        var segments = finger == FingerType.Thumb ? ThumbSegments : FingerSegments;

        for (int i = 0; i < segments.Length; i++)
        {
            float3 open = openPoints[i];
            float3 closed = closedPoints[i];

            float3 local = new float3(
                Lerp(open.x, closed.x, curl),
                Lerp(open.y, closed.y, curl),
                Lerp(open.z, closed.z, curl));

            if (finger != FingerType.Thumb)
                local = new float3(local.x + splay * (0.4f + i * 0.1f), local.y, local.z);

            local = MirrorForSide(local, side, handScale);
            var world = Add(controllerPos, controllerRot * local);
            var node = finger.ComposeFinger(segments[i], side);
            SetHandNodePose(node, world, controllerRot, true);
        }
    }

    private void SetHandSkeletonTrackingState(Chirality side, bool isTracking)
    {
        BodyNode palmNode = side == Chirality.Left ? BodyNode.LeftPalm : BodyNode.RightPalm;
        SetHandNodeTrackingState(palmNode, isTracking);

        foreach (var finger in FingerOrder)
        {
            var segments = finger == FingerType.Thumb ? ThumbSegments : FingerSegments;
            for (int i = 0; i < segments.Length; i++)
                SetHandNodeTrackingState(finger.ComposeFinger(segments[i], side), isTracking);
        }
    }

    private void SetHandNodeTrackingState(BodyNode node, bool isTracking)
    {
        if (!_handSkeletonTrackedObjects.TryGetValue(node, out var tracked))
            return;

        tracked.IsTracking = isTracking;
        tracked.IsDeviceActive = isTracking;
        tracked.TrackingSpace = _inputInterface?.GlobalTrackingSpace!;
    }

    private void SetHandNodePose(BodyNode node, float3 position, floatQ rotation, bool isTracking)
    {
        if (!_handSkeletonTrackedObjects.TryGetValue(node, out var tracked))
            return;

        tracked.RawPosition = position;
        tracked.RawRotation = rotation;
        tracked.IsTracking = isTracking;
        tracked.IsDeviceActive = isTracking;
        tracked.TrackingSpace = _inputInterface?.GlobalTrackingSpace!;
    }

    private static float3[] GetOpenPosePoints(FingerType finger)
    {
        return finger switch
        {
            FingerType.Thumb => new[]
            {
                new float3(0.0240f, -0.0100f, -0.0320f),
                new float3(0.0360f, -0.0040f, -0.0430f),
                new float3(0.0440f,  0.0000f, -0.0560f),
                new float3(0.0500f,  0.0040f, -0.0680f),
            },
            FingerType.Index => new[]
            {
                new float3(0.0160f, -0.0060f, -0.0480f),
                new float3(0.0170f, -0.0030f, -0.0670f),
                new float3(0.0180f,  0.0010f, -0.0850f),
                new float3(0.0190f,  0.0040f, -0.1030f),
                new float3(0.0200f,  0.0070f, -0.1190f),
            },
            FingerType.Middle => new[]
            {
                new float3(0.0060f, -0.0060f, -0.0470f),
                new float3(0.0060f, -0.0020f, -0.0680f),
                new float3(0.0060f,  0.0020f, -0.0890f),
                new float3(0.0060f,  0.0060f, -0.1090f),
                new float3(0.0060f,  0.0100f, -0.1260f),
            },
            FingerType.Ring => new[]
            {
                new float3(-0.0040f, -0.0070f, -0.0450f),
                new float3(-0.0050f, -0.0030f, -0.0640f),
                new float3(-0.0060f,  0.0000f, -0.0820f),
                new float3(-0.0070f,  0.0030f, -0.0990f),
                new float3(-0.0080f,  0.0060f, -0.1130f),
            },
            _ => new[]
            {
                new float3(-0.0140f, -0.0090f, -0.0410f),
                new float3(-0.0160f, -0.0060f, -0.0570f),
                new float3(-0.0180f, -0.0030f, -0.0720f),
                new float3(-0.0200f,  0.0000f, -0.0860f),
                new float3(-0.0220f,  0.0030f, -0.0980f),
            },
        };
    }

    private static float3[] GetClosedPosePoints(FingerType finger)
    {
        return finger switch
        {
            FingerType.Thumb => new[]
            {
                new float3(0.0200f, -0.0080f, -0.0320f),
                new float3(0.0260f, -0.0100f, -0.0230f),
                new float3(0.0300f, -0.0120f, -0.0120f),
                new float3(0.0330f, -0.0130f, -0.0010f),
            },
            FingerType.Index => new[]
            {
                new float3(0.0160f, -0.0060f, -0.0470f),
                new float3(0.0200f, -0.0130f, -0.0320f),
                new float3(0.0220f, -0.0170f, -0.0180f),
                new float3(0.0240f, -0.0200f, -0.0060f),
                new float3(0.0260f, -0.0220f,  0.0060f),
            },
            FingerType.Middle => new[]
            {
                new float3(0.0060f, -0.0060f, -0.0460f),
                new float3(0.0090f, -0.0150f, -0.0310f),
                new float3(0.0100f, -0.0200f, -0.0180f),
                new float3(0.0110f, -0.0240f, -0.0070f),
                new float3(0.0120f, -0.0270f,  0.0040f),
            },
            FingerType.Ring => new[]
            {
                new float3(-0.0040f, -0.0070f, -0.0440f),
                new float3(-0.0030f, -0.0160f, -0.0300f),
                new float3(-0.0020f, -0.0210f, -0.0180f),
                new float3(-0.0010f, -0.0250f, -0.0080f),
                new float3( 0.0000f, -0.0280f,  0.0030f),
            },
            _ => new[]
            {
                new float3(-0.0130f, -0.0080f, -0.0410f),
                new float3(-0.0110f, -0.0160f, -0.0290f),
                new float3(-0.0100f, -0.0210f, -0.0180f),
                new float3(-0.0090f, -0.0250f, -0.0090f),
                new float3(-0.0080f, -0.0290f,  0.0010f),
            },
        };
    }

    private static float ReadFloatAction(XRController3D controller, string action)
    {
        if (controller == null || !GodotObject.IsInstanceValid(controller))
            return 0f;

        float value = controller.GetFloat(action);
        return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
    }

    // GetInput, not Get: Get() is GodotObject's PROPERTY lookup and XRController3D has no property
    // called "ax_button", so every button read came back Nil while the floats (GetFloat) worked. That
    // is why grip, trigger and the stick reached the engine and no face or menu button ever did. -xlinka
    private static bool ReadBoolAction(XRController3D controller, string action)
    {
        if (controller == null || !GodotObject.IsInstanceValid(controller))
            return false;

        Variant value = controller.GetInput(action);
        return value.VariantType switch
        {
            Variant.Type.Bool => value.AsBool(),
            Variant.Type.Float => value.AsSingle() > 0.5f,
            Variant.Type.Int => value.AsInt32() > 0,
            _ => controller.GetFloat(action) > 0.5f
        };
    }

    private static float Clamp01(float value)
    {
        return Math.Clamp(value, 0f, 1f);
    }

    private static float Lerp(float from, float to, float t)
    {
        return from + (to - from) * Clamp01(t);
    }

    private static float3 MirrorForSide(float3 rightHandLocal, Chirality side, float scale)
    {
        float3 scaled = new float3(
            rightHandLocal.x * scale,
            rightHandLocal.y * scale,
            rightHandLocal.z * scale);

        return side == Chirality.Left
            ? new float3(-scaled.x, scaled.y, scaled.z)
            : scaled;
    }

    private static float3 Add(float3 a, float3 b)
    {
        return new float3(a.x + b.x, a.y + b.y, a.z + b.z);
    }

    // Scene-node transforms (XRCamera3D/XRController3D.Position) are written
    // during those nodes' own internal process, in tree order - sampling them
    // from the engine runner can read a frame-stale pose while the headset
    // view is late-latched to the freshest one. The XR server pose data is
    // updated at frame begin, before any process callbacks, so hands/head
    // stay in lockstep with the rendered view. - xlinka
    private void UpdateHeadTracking()
    {
        if (_headTrackedObject == null)
            return;

        bool isTracking = false;
        float3 position = new float3(0, 1.7f, 0);
        floatQ rotation = floatQ.Identity;

        if (IsVRActive)
        {
            Transform3D headTransform = XRServer.GetHmdTransform();

            if (headTransform != Transform3D.Identity)
            {
                var pos = headTransform.Origin;
                position = new float3(pos.X, pos.Y, pos.Z);

                var quat = headTransform.Basis.GetRotationQuaternion();
                rotation = new floatQ(quat.X, quat.Y, quat.Z, quat.W);
                isTracking = true;
            }
            // Fall back to the camera node (playspace-relative, same space)
            else if (_xrCamera != null && GodotObject.IsInstanceValid(_xrCamera))
            {
                var pos = _xrCamera.Position;
                var quat = _xrCamera.Quaternion;

                position = new float3(pos.X, pos.Y, pos.Z);
                rotation = new floatQ(quat.X, quat.Y, quat.Z, quat.W);
                isTracking = true;
            }
        }

        _headTrackedObject.RawPosition = position;
        _headTrackedObject.RawRotation = rotation;
        _headTrackedObject.IsTracking = isTracking;
        _headTrackedObject.IsDeviceActive = isTracking;
        _headTrackedObject.TrackingSpace = _inputInterface?.GlobalTrackingSpace!;
    }

    // XRController3D node transform is a fallback; it can be a frame stale (see UpdateHeadTracking)
    private void UpdateControllerTracking(TrackedObject controllerObj, TrackedObject handObj, Chirality side)
    {
        if (controllerObj == null)
            return;

        bool isTracking = false;
        float3 position = float3.Zero;
        floatQ rotation = floatQ.Identity;

        var xrController = side == Chirality.Left ? _leftController : _rightController;

        if (IsVRActive)
        {
            // Godot 4 standard tracker names. (The old "/user/hand/left" form is
            // an OpenXR action path, not a Godot tracker name - it never resolved.)
            var trackerName = new StringName(side == Chirality.Left ? "left_hand" : "right_hand");
            if (XRServer.GetTracker(trackerName) is XRPositionalTracker positionalTracker)
            {
                // Query the same pose the controller node renders with so the
                // hand offset doesn't shift, then fall through grip/aim/default.
                XRPose? pose = null;
                if (xrController != null && GodotObject.IsInstanceValid(xrController) && !xrController.Pose.IsEmpty)
                    pose = positionalTracker.GetPose(xrController.Pose);
                if (pose == null || !pose.HasTrackingData)
                    pose = positionalTracker.GetPose(new StringName("grip"));
                if (pose == null || !pose.HasTrackingData)
                    pose = positionalTracker.GetPose(new StringName("aim"));
                if (pose == null || !pose.HasTrackingData)
                    pose = positionalTracker.GetPose(new StringName("default"));

                if (pose != null && pose.HasTrackingData && pose.Transform != Transform3D.Identity)
                {
                    var transform = pose.Transform;
                    position = new float3(transform.Origin.X, transform.Origin.Y, transform.Origin.Z);

                    var quat = transform.Basis.GetRotationQuaternion();
                    rotation = new floatQ(quat.X, quat.Y, quat.Z, quat.W);
                    isTracking = true;
                }
            }
        }

        // Fall back to the controller node transform (playspace-relative)
        if (!isTracking && xrController != null && GodotObject.IsInstanceValid(xrController) && xrController.GetIsActive())
        {
            var pos = xrController.Position;
            var quat = xrController.Quaternion;

            position = new float3(pos.X, pos.Y, pos.Z);
            rotation = new floatQ(quat.X, quat.Y, quat.Z, quat.W);
            isTracking = true;
        }

        controllerObj.RawPosition = position;
        controllerObj.RawRotation = rotation;
        controllerObj.IsTracking = isTracking;
        controllerObj.IsDeviceActive = isTracking;
        controllerObj.TrackingSpace = _inputInterface?.GlobalTrackingSpace!;

        // Hands take the controller pose; no separate hand-tracking source.
        if (handObj != null)
        {
            handObj.RawPosition = position;
            handObj.RawRotation = rotation;
            handObj.IsTracking = isTracking;
            handObj.IsDeviceActive = isTracking;
            handObj.TrackingSpace = _inputInterface?.GlobalTrackingSpace!;
        }
    }

    // legacy compatibility with old IVRDriver interface
    public void UpdateVRDevices(VRController leftController, VRController rightController, HeadDevice headDevice)
    {
        if (!IsVRActive)
        {
            ClearLegacyDevice(headDevice);
            ClearLegacyDevice(leftController);
            ClearLegacyDevice(rightController);
            ClearTrackingState();
            return;
        }

        UpdateHeadDevice(headDevice);
        UpdateController(leftController, Chirality.Left);
        UpdateController(rightController, Chirality.Right);
    }

    private void ClearTrackingState()
    {
        ClearTrackedObject(_headTrackedObject);
        ClearTrackedObject(_leftControllerTrackedObject);
        ClearTrackedObject(_rightControllerTrackedObject);
        ClearTrackedObject(_leftHandTrackedObject);
        ClearTrackedObject(_rightHandTrackedObject);

        foreach (var tracked in _handSkeletonTrackedObjects.Values)
            ClearTrackedObject(tracked);

        if (_trackerDevices.Count > 0)
        {
            foreach (var device in _trackerDevices.Values)
                device.SetOffline();
        }
    }

    private static void ClearTrackedObject(TrackedObject tracked)
    {
        if (tracked == null)
            return;

        tracked.RawPosition = float3.Zero;
        tracked.RawRotation = floatQ.Identity;
        tracked.IsTracking = false;
        tracked.IsDeviceActive = false;
        tracked.TrackingSpace = null!;
    }

    private static void ClearLegacyDevice(HeadDevice headDevice)
    {
        if (headDevice == null)
            return;

        headDevice.Position = Vector3.Zero;
        headDevice.Rotation = Quaternion.Identity;
        headDevice.IsTracked = false;
        headDevice.IsWorn = false;
        headDevice.TrackingConfidence = 0f;
        headDevice.IsDeviceActive = false;
    }

    private static void ClearLegacyDevice(VRController controller)
    {
        if (controller == null)
            return;

        controller.Position = Vector3.Zero;
        controller.Rotation = Quaternion.Identity;
        controller.IsTracked = false;
        controller.IsDeviceActive = false;
        controller.ThumbstickPosition = Vector2.Zero;
        controller.TriggerValue = 0f;
        controller.TriggerPressed = false;
        controller.GripValue = 0f;
        controller.GripPressed = false;
        controller.PrimaryButtonPressed = false;
        controller.SecondaryButtonPressed = false;
        controller.MenuButtonPressed = false;
        controller.ThumbstickPressed = false;
        controller.ThumbstickTouched = false;
    }

    private void UpdateHeadDevice(HeadDevice headDevice)
    {
        if (headDevice == null)
            return;

        if (_headTrackedObject != null)
        {
            headDevice.Position = new Vector3(_headTrackedObject.RawPosition.x, _headTrackedObject.RawPosition.y, _headTrackedObject.RawPosition.z);
            headDevice.Rotation = new Quaternion(_headTrackedObject.RawRotation.x, _headTrackedObject.RawRotation.y, _headTrackedObject.RawRotation.z, _headTrackedObject.RawRotation.w);
            headDevice.IsTracked = _headTrackedObject.IsTracking;
            headDevice.IsWorn = _headTrackedObject.IsTracking;
            headDevice.TrackingConfidence = _headTrackedObject.IsTracking ? 1.0f : 0f;
            headDevice.IsDeviceActive = _headTrackedObject.IsDeviceActive;
        }
    }

    private void UpdateController(VRController controller, Chirality side)
    {
        if (controller == null)
            return;

        var trackedObj = side == Chirality.Left ? _leftControllerTrackedObject : _rightControllerTrackedObject;
        if (trackedObj != null)
        {
            controller.Position = new Vector3(trackedObj.RawPosition.x, trackedObj.RawPosition.y, trackedObj.RawPosition.z);
            controller.Rotation = new Quaternion(trackedObj.RawRotation.x, trackedObj.RawRotation.y, trackedObj.RawRotation.z, trackedObj.RawRotation.w);
            controller.IsTracked = trackedObj.IsTracking;
            controller.IsDeviceActive = trackedObj.IsDeviceActive;
        }

        UpdateControllerInputs(controller);
    }

    private void UpdateControllerInputs(VRController controller)
    {
        var xrController = controller.Side == VRControllerSide.Left ? _leftController : _rightController;
        string sideName = controller.Side == VRControllerSide.Left ? "left" : "right";

        global::Godot.Vector2 thumbstick = global::Godot.Vector2.Zero;
        float trigger = 0f;
        float grip = 0f;
        bool primaryButton = false;
        bool secondaryButton = false;
        bool menuPressed = false;
        bool stickPressed = false;
        bool stickTouched = false;

        // Method 1: Try XRController3D if available (requires action map bindings to work)
        if (xrController != null && GodotObject.IsInstanceValid(xrController))
        {
            thumbstick = xrController.GetVector2("primary");
            trigger = xrController.GetFloat("trigger");
            grip = xrController.GetFloat("grip");

            primaryButton = ReadBoolAction(xrController, "ax_button");
            secondaryButton = ReadBoolAction(xrController, "by_button");
            // The dashboard is bound to the menu button in VR and this was never read, so no
            // controller on any headset could open it. -xlinka
            menuPressed = ReadBoolAction(xrController, "menu_button");
            stickPressed = ReadBoolAction(xrController, "primary_click");
            stickTouched = ReadBoolAction(xrController, "primary_touch");
        }

        // Method 2: Use Godot Input joypad mapping as fallback
        // OpenXR maps to joypad index 0 (left) and 1 (right)
        if (thumbstick == global::Godot.Vector2.Zero)
        {
            int joyId = controller.Side == VRControllerSide.Left ? 0 : 1;

            float joyX = global::Godot.Input.GetJoyAxis(joyId, JoyAxis.LeftX);
            float joyY = global::Godot.Input.GetJoyAxis(joyId, JoyAxis.LeftY);
            if (joyX != 0 || joyY != 0)
                thumbstick = new global::Godot.Vector2(joyX, joyY); // No inversion - raw values

            if (trigger == 0f)
                trigger = global::Godot.Input.GetJoyAxis(joyId, JoyAxis.TriggerRight);
            if (grip == 0f)
                grip = global::Godot.Input.GetJoyAxis(joyId, JoyAxis.TriggerLeft);

            if (!primaryButton)
                primaryButton = global::Godot.Input.IsJoyButtonPressed(joyId, JoyButton.A);
            if (!secondaryButton)
                secondaryButton = global::Godot.Input.IsJoyButtonPressed(joyId, JoyButton.B);
        }

        controller.ThumbstickPosition = new Vector2(thumbstick.X, thumbstick.Y);
        controller.TriggerValue = trigger;
        controller.TriggerPressed = trigger > 0.5f;
        controller.GripValue = grip;
        controller.GripPressed = grip > 0.5f;
        controller.PrimaryButtonPressed = primaryButton;
        controller.SecondaryButtonPressed = secondaryButton;
        controller.MenuButtonPressed = menuPressed;
        controller.ThumbstickPressed = stickPressed;
        controller.ThumbstickTouched = stickTouched;

        // First press of each control is logged once, so a headset log answers "does the app get my
        // buttons" without a debugger attached. -xlinka
        NoteFirstPress(controller.Side, "primary", primaryButton);
        NoteFirstPress(controller.Side, "secondary", secondaryButton);
        NoteFirstPress(controller.Side, "menu", menuPressed);
        NoteFirstPress(controller.Side, "trigger", trigger > 0.5f);
        NoteFirstPress(controller.Side, "grip", grip > 0.5f);
        NoteFirstPress(controller.Side, "stick", thumbstick.LengthSquared() > 0.25f);
        NoteFirstPress(controller.Side, "stickclick", stickPressed);

        DrainHaptics(controller, xrController);
    }

    private readonly HashSet<string> _seenPresses = new();

    private void NoteFirstPress(VRControllerSide side, string control, bool pressed)
    {
        if (!pressed)
            return;
        if (_seenPresses.Add($"{side}/{control}"))
            LumoraLogger.Log($"GodotVRDriver: first '{control}' press on the {side} controller");
    }

    // Hand any pulse the engine queued this frame to the runtime. Reading it CLEARS it, so a pulse
    // requested while VR is off or the node is gone is dropped rather than saved up and fired all at
    // once the moment a headset appears.
    //
    // Amplitude 0 means nothing was requested, which is the common case - this runs twice a frame all
    // session. Frequency 0 hands the choice to the runtime, which is what you want: controllers
    // disagree wildly about what a usable frequency is and picking one here gets you silence on
    // hardware that does not do it. -xlinka
    private static void DrainHaptics(VRController controller, XRController3D? xrController)
    {
        var (amplitude, duration, frequency) = controller.GetPendingHaptic();
        if (amplitude <= 0f || duration <= 0f)
            return;

        if (xrController == null || !GodotObject.IsInstanceValid(xrController))
            return;

        xrController.TriggerHapticPulse(
            HapticActionName,
            frequency > 0f ? frequency : 0.0,
            global::System.Math.Clamp(amplitude, 0f, 1f),
            duration,
            0.0);
    }

    // Standard OpenXR output action name; the action map ships it bound on every profile.
    private const string HapticActionName = "haptic";

    public void ShutdownVR()
    {
        if (_trackerSignalsConnected)
        {
            XRServer.TrackerAdded -= OnTrackerAdded;
            XRServer.TrackerRemoved -= OnTrackerRemoved;
            _trackerSignalsConnected = false;
        }

        foreach (var entry in _trackerCandidates.Values)
            DetachCandidate(entry);
        _trackerCandidates.Clear();
        _liveTrackers.Clear();

        _modeActive = false;
        ClearTrackingState();
        _xrInterface = null!;
        GD.Print("GodotVRDriver: VR shutdown");
    }
}

