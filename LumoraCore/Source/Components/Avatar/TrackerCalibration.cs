// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Avatar.IK;
using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraMeshes = Lumora.Core.Components.Meshes;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

// Body tracker calibration, the way players already know it from other social VR: the avatar freezes in
// a T-pose where you stand, every mappable joint gets a marker, every puck gets a marker, you put your
// pucks on the joints and pull both triggers. Nobody assigns a role to anything: at confirm each puck is
// paired with the nearest unclaimed joint, closest pair first, and the stored offset is what turns that
// puck's pose into that joint's pose from then on.
//
// A per-limb wizard was rejected on purpose. It asks the user to know what a "left lower leg" is and to
// hold a menu open with a foot in the air. Proximity needs neither. -xlinka
[ComponentCategory("Users/Avatar")]
public sealed class TrackerCalibration : Component
{
    [NonPersistent]
    public readonly Sync<bool> Active = new();

    [NonPersistent]
    public readonly Sync<string> Status = new();

    // Metres at tracking scale 1. A puck strapped to a shin or a belt sits well inside this of its joint
    // even on an avatar whose proportions are off; a puck a metre from everything was left on a desk.
    public readonly Sync<float> MaxPairDistance = new();

    // Both triggers have to stay down this long. A single twitch on the way to grabbing something is not
    // a confirm.
    public readonly Sync<float> ConfirmHoldSeconds = new();

    public struct PairResult
    {
        public string PublicID;
        public string Label;
        public BodyNode Node;
        public float Distance;
        public bool Mapped;
        public string Reason;
    }

    public IReadOnlyList<PairResult> LastResults => _lastResults;

    public const string SlotName = "TrackerCalibration";

    private const float PressPoint = 0.7f;
    private const float ReleasePoint = 0.3f;

    // When two candidate pairs are this close in distance, the one matching the runtime's role
    // suggestion wins. Small enough that it never overrides where the user actually put the puck.
    private const float RoleTieBreak = 0.01f;

    private const float NodeMarkerRadius = 0.03f;
    private const float LabelHeight = 0.07f;
    private static readonly float3 TrackerMarkerSize = new(0.06f, 0.025f, 0.04f);
    private static readonly float3 TrackerNoseSize = new(0.01f, 0.01f, 0.03f);

    private static readonly colorHDR LeftFill = new(0.2f, 0.7f, 1f);
    private static readonly colorHDR RightFill = new(1f, 0.3f, 0.3f);
    private static readonly colorHDR CentreFill = new(0.7f, 0.4f, 1f);
    private static readonly colorHDR TrackerFill = new(1f, 0.75f, 0.1f);
    private static readonly colorHDR TrackerLostFill = new(0.45f, 0.45f, 0.45f);

    private struct BoneState
    {
        public Slot Bone;
        public float3 LocalPosition;
        public floatQ LocalRotation;
    }

    private sealed class TrackerMarker
    {
        public Slot Slot = null!;
        public UnlitMaterial Material = null!;
        public bool Seen;
    }

    private readonly List<PairResult> _lastResults = new();
    private readonly List<BoneState> _boneStates = new();
    private readonly List<(BodyNode node, Slot bone)> _nodes = new();
    private readonly List<ITracker> _trackers = new();
    private readonly Dictionary<string, TrackerMarker> _trackerMarkers = new(StringComparer.Ordinal);

    private Slot? _avatarRoot;
    private HumanoidRig? _rig;
    private AvatarIK? _ik;
    private bool _ikWasEnabled;
    private bool _rootMoved;
    private float3 _rootLocalPosition;
    private floatQ _rootLocalRotation;
    private Slot? _markerRoot;
    private bool _armed;
    private float _hold;

    public override void OnInit()
    {
        base.OnInit();
        MaxPairDistance.Value = 0.4f;
        ConfirmHoldSeconds.Value = 0.6f;
        Status.Value = "Idle";
    }

    // Front door. Puts the component on a local, non-persistent slot under the local user's root so it
    // never replicates and never saves, then starts. The reason string is for whatever UI called this.
    public static bool TryBeginForLocalUser(World world, out string reason)
    {
        reason = string.Empty;
        var root = world?.LocalUser?.Root;
        if (root == null || root.IsDestroyed || root.Slot == null || root.Slot.IsDestroyed)
        {
            reason = "no local user root";
            return false;
        }

        var existing = root.Slot.GetComponentInChildren<TrackerCalibration>();
        if (existing != null && !existing.IsDestroyed)
        {
            if (existing.Active.Value)
            {
                reason = "calibration is already running";
                return false;
            }
            return existing.TryBegin(out reason);
        }

        var slot = root.Slot.AddLocalSlot(SlotName);
        slot.Persistent.Value = false;
        var calibration = slot.AttachComponent<TrackerCalibration>();
        if (calibration.TryBegin(out reason))
            return true;
        slot.Destroy();
        return false;
    }

    [SyncMethod]
    public void Begin()
    {
        if (!TryBegin(out var reason))
            LumoraLogger.Warn($"TrackerCalibration: refused: {reason}");
    }

    public bool TryBegin(out string reason)
    {
        reason = string.Empty;
        if (Active.Value)
        {
            reason = "already calibrating";
            return false;
        }

        var world = World;
        if (world == null || world.RootSlot == null)
        {
            reason = "no world";
            return false;
        }

        var input = Engine.Current?.InputInterface;
        if (input == null)
        {
            reason = "input is not initialised";
            return false;
        }

        var userRoot = world.LocalUser?.Root;
        if (userRoot == null || userRoot.IsDestroyed || userRoot.Slot == null)
        {
            reason = "no local user root";
            return false;
        }

        var avatar = userRoot.GetRegisteredComponent<AvatarEquipManager>()?.CurrentAvatar.Target;
        if (avatar == null || avatar.IsDestroyed)
        {
            reason = "no avatar equipped";
            return false;
        }

        var rig = avatar.GetComponentInChildren<HumanoidRig>();
        if (rig == null || rig.IsDestroyed || rig.Bones.Count == 0)
        {
            reason = "the avatar has no humanoid rig";
            return false;
        }

        _nodes.Clear();
        foreach (var node in TrackerRoles.Mappable)
        {
            var bone = rig.TryGetBone(node);
            if (bone != null && !bone.IsDestroyed)
                _nodes.Add((node, bone));
        }
        if (_nodes.Count == 0)
        {
            reason = "the rig has none of the mappable bones";
            return false;
        }

        _trackers.Clear();
        input.GetDevices(_trackers, t => t.IsDeviceActive);
        if (_trackers.Count == 0)
        {
            reason = "no tracker is connected";
            return false;
        }

        _avatarRoot = avatar;
        _rig = rig;
        _ik = avatar.GetComponent<AvatarIK>();
        _lastResults.Clear();
        _armed = false;
        _hold = 0f;

        LockAvatar(userRoot);
        BuildMarkers(world);

        Active.Value = true;
        SetStatus("Release both triggers, stand on the markers, then hold both triggers to confirm");
        LumoraLogger.Log($"TrackerCalibration: started with {_trackers.Count} tracker(s) and {_nodes.Count} mappable joint(s)");
        return true;
    }

    [SyncMethod]
    public void Cancel()
    {
        if (!Active.Value)
            return;
        Finish("Cancelled");
        LumoraLogger.Log("TrackerCalibration: cancelled, avatar restored");
    }

    // Immediate confirm, no trigger hold. For a button on a panel, and for driving this without controllers.
    [SyncMethod]
    public void Confirm()
    {
        if (!Active.Value)
            return;

        var input = Engine.Current?.InputInterface;
        if (input == null)
        {
            Finish("Cancelled: input went away");
            return;
        }

        Solve(input);
        Finish(Summary());
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (!Active.Value)
            return;

        if (_avatarRoot == null || _avatarRoot.IsDestroyed || _rig == null || _rig.IsDestroyed)
        {
            Finish("Cancelled: the avatar changed");
            LumoraLogger.Warn("TrackerCalibration: avatar went away mid-calibration, cancelled");
            return;
        }

        var input = Engine.Current?.InputInterface;
        if (input == null)
        {
            Finish("Cancelled: input went away");
            return;
        }

        RefreshTrackerMarkers(input);
        PollConfirm(input, delta);
    }

    public override void OnDestroy()
    {
        if (Active.Value)
            Finish("Cancelled");
        base.OnDestroy();
    }

    // ---- avatar lock / restore ----

    // The avatar has to hold still while the user walks onto it, so the solver is switched off rather
    // than fought. The rig bones go back to their authored rest so the pose is the same every time
    // regardless of what the last solve left behind, then the limbs are straightened into a T. The root
    // is yawed and slid so the figure stands where the head is now: nobody should have to walk back to
    // the play-space origin to find their own avatar. All of it is snapshotted first and put back on
    // exit. -xlinka
    private void LockAvatar(UserRoot userRoot)
    {
        var avatar = _avatarRoot!;
        var rig = _rig!;

        _ikWasEnabled = _ik != null && !_ik.IsDestroyed && _ik.IKEnabled.Value;
        if (_ik != null && !_ik.IsDestroyed)
            _ik.IKEnabled.Value = false;

        _rootLocalPosition = avatar.LocalPosition.Value;
        _rootLocalRotation = avatar.LocalRotation.Value;
        _rootMoved = false;

        LumoraLogger.Log("POSEWRITE TrackerCalibration.LockAvatar: restoring rest and forcing a T-pose");
        SnapshotBones(avatar, rig);
        RestoreAuthoredRest(avatar, rig);
        rig.MakeTPose();
        PlaceUnderHead(userRoot, avatar, rig);
    }

    private void SnapshotBones(Slot avatar, HumanoidRig rig)
    {
        _boneStates.Clear();
        var seen = new HashSet<Slot>();

        foreach (var node in rig.Bones.Keys)
        {
            var bone = rig.TryGetBone(node);
            if (bone != null && !bone.IsDestroyed && seen.Add(bone))
                _boneStates.Add(new BoneState { Bone = bone, LocalPosition = bone.LocalPosition.Value, LocalRotation = bone.LocalRotation.Value });
        }

        var skeleton = avatar.GetComponentInChildren<SkeletonBuilder>();
        if (skeleton == null || skeleton.IsDestroyed)
            return;
        for (int i = 0; i < skeleton.BoneSlots.Count; i++)
        {
            var bone = skeleton.BoneSlots[i];
            if (bone != null && !bone.IsDestroyed && seen.Add(bone))
                _boneStates.Add(new BoneState { Bone = bone, LocalPosition = bone.LocalPosition.Value, LocalRotation = bone.LocalRotation.Value });
        }
    }

    // Only rig bones are reset, and only to what the skeleton recorded at build time. Bones above the hips
    // (the armature root) are left alone: equip re-frames them and a build-time value would undo that.
    private static void RestoreAuthoredRest(Slot avatar, HumanoidRig rig)
    {
        var skeleton = avatar.GetComponentInChildren<SkeletonBuilder>();
        if (skeleton == null || skeleton.IsDestroyed)
            return;

        var rigBones = new HashSet<Slot>();
        foreach (var node in rig.Bones.Keys)
        {
            var bone = rig.TryGetBone(node);
            if (bone != null && !bone.IsDestroyed)
                rigBones.Add(bone);
        }

        int count = System.Math.Min(skeleton.BoneSlots.Count, skeleton.RestPoseTransforms.Count);
        for (int i = 0; i < count; i++)
        {
            var bone = skeleton.BoneSlots[i];
            if (bone == null || bone.IsDestroyed || !rigBones.Contains(bone))
                continue;
            if (bone.LocalPosition.IsDriven || bone.LocalRotation.IsDriven)
                continue;
            if (!TryDecompose(skeleton.RestPoseTransforms[i], out var position, out var rotation))
                continue;
            bone.LocalPosition.Value = position;
            bone.LocalRotation.Value = rotation;
        }
    }

    private void PlaceUnderHead(UserRoot userRoot, Slot avatar, HumanoidRig rig)
    {
        var headBone = rig.TryGetBone(BodyNode.Head);
        var headSlot = userRoot.HeadSlot;
        if (headBone == null || headBone.IsDestroyed || headSlot == null || headSlot.IsDestroyed)
            return;
        if (avatar.LocalPosition.IsDriven || avatar.LocalRotation.IsDriven)
            return;

        float3 want = headSlot.GlobalRotation * float3.Backward;
        want.y = 0f;
        float3 have = AvatarFacing(avatar, rig);
        if (want.LengthSquared > 1e-6f && have.LengthSquared > 1e-6f)
        {
            float delta = MathF.Atan2(want.x, want.z) - MathF.Atan2(have.x, have.z);
            while (delta > MathF.PI) delta -= 2f * MathF.PI;
            while (delta < -MathF.PI) delta += 2f * MathF.PI;
            if (MathF.Abs(delta) > 0.005f)
                avatar.GlobalRotation = floatQ.AxisAngleRad(float3.Up, delta) * avatar.GlobalRotation;
        }

        float3 shift = headSlot.GlobalPosition - headBone.GlobalPosition;
        shift.y = 0f;
        if (shift.LengthSquared > 1e-8f)
            avatar.GlobalPosition = avatar.GlobalPosition + shift;

        _rootMoved = true;
    }

    private void RestoreAvatar()
    {
        var avatar = _avatarRoot;
        if (avatar != null && !avatar.IsDestroyed)
        {
            for (int i = 0; i < _boneStates.Count; i++)
            {
                var state = _boneStates[i];
                if (state.Bone == null || state.Bone.IsDestroyed)
                    continue;
                if (!state.Bone.LocalPosition.IsDriven)
                    state.Bone.LocalPosition.Value = state.LocalPosition;
                if (!state.Bone.LocalRotation.IsDriven)
                    state.Bone.LocalRotation.Value = state.LocalRotation;
            }

            if (_rootMoved)
            {
                if (!avatar.LocalPosition.IsDriven)
                    avatar.LocalPosition.Value = _rootLocalPosition;
                if (!avatar.LocalRotation.IsDriven)
                    avatar.LocalRotation.Value = _rootLocalRotation;
            }
        }

        if (_ik != null && !_ik.IsDestroyed && _ikWasEnabled)
            _ik.IKEnabled.Value = true;
    }

    private void Finish(string status)
    {
        DestroyMarkers();
        RestoreAvatar();

        Active.Value = false;
        SetStatus(status);

        _avatarRoot = null;
        _rig = null;
        _ik = null;
        _rootMoved = false;
        _boneStates.Clear();
        _nodes.Clear();
        _trackers.Clear();
        _armed = false;
        _hold = 0f;
    }

    // ---- markers ----

    private void BuildMarkers(World world)
    {
        DestroyMarkers();

        // Local and non-persistent: calibration chrome is for the person calibrating, and it must never
        // end up in a world save or on a peer's screen.
        _markerRoot = world.RootSlot.AddLocalSlot("TrackerCalibration Markers");
        _markerRoot.Persistent.Value = false;

        var facing = FacingRotation();
        float halo = MathF.Max(MaxPairDistance.Value, 0.05f) * RootScale();

        for (int i = 0; i < _nodes.Count; i++)
        {
            var (node, bone) = _nodes[i];
            if (bone == null || bone.IsDestroyed)
                continue;

            var slot = _markerRoot.AddSlot(node.ToString());
            slot.GlobalPosition = bone.GlobalPosition;
            slot.GlobalRotation = facing;

            var fill = FillFor(node);
            AddSphere(slot, NodeMarkerRadius, new colorHDR(fill.r, fill.g, fill.b, 1f), BlendMode.Opaque);

            var haloSlot = slot.AddSlot("Reach");
            AddSphere(haloSlot, halo, new colorHDR(fill.r, fill.g, fill.b, 0.06f), BlendMode.Transparent);

            AddLabel(slot, NodeLabel(node), fill, LabelHeight);
        }
    }

    private void RefreshTrackerMarkers(InputInterface input)
    {
        if (_markerRoot == null || _markerRoot.IsDestroyed)
            return;

        foreach (var marker in _trackerMarkers.Values)
            marker.Seen = false;

        _trackers.Clear();
        input.GetDevices(_trackers, t => t.IsDeviceActive);

        for (int i = 0; i < _trackers.Count; i++)
        {
            var tracker = _trackers[i];
            string key = tracker.UniqueID ?? string.Empty;
            if (key.Length == 0)
                continue;

            if (!_trackerMarkers.TryGetValue(key, out var marker) || marker.Slot == null || marker.Slot.IsDestroyed)
            {
                marker = CreateTrackerMarker(tracker);
                _trackerMarkers[key] = marker;
            }

            marker.Seen = true;
            marker.Slot.ActiveSelf.Value = true;
            if (tracker.IsTracking)
            {
                marker.Slot.GlobalPosition = tracker.Position;
                marker.Slot.GlobalRotation = tracker.Rotation;
                marker.Material.Color = TrackerFill;
            }
            else
            {
                marker.Material.Color = TrackerLostFill;
            }
        }

        foreach (var marker in _trackerMarkers.Values)
        {
            if (!marker.Seen && marker.Slot != null && !marker.Slot.IsDestroyed)
                marker.Slot.ActiveSelf.Value = false;
        }
    }

    // Slot names carry the PublicID only. The unique id is a serial and stays out of the scene graph.
    private TrackerMarker CreateTrackerMarker(ITracker tracker)
    {
        var slot = _markerRoot!.AddSlot("Tracker " + (tracker.PublicID ?? "?"));

        var mesh = slot.AttachComponent<LumoraMeshes.BoxMesh>();
        mesh.Size.Value = TrackerMarkerSize;
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        var material = slot.AttachComponent<UnlitMaterial>();
        material.Color = TrackerFill;
        renderer.Material.Target = material;

        // A stub along the puck's own -Z so its orientation can be read, not just its position.
        var nose = slot.AddSlot("Nose");
        nose.LocalPosition.Value = new float3(0f, 0f, -(TrackerMarkerSize.z * 0.5f + TrackerNoseSize.z * 0.5f));
        var noseMesh = nose.AttachComponent<LumoraMeshes.BoxMesh>();
        noseMesh.Size.Value = TrackerNoseSize;
        var noseRenderer = nose.AttachComponent<MeshRenderer>();
        noseRenderer.Mesh.Target = noseMesh;
        noseRenderer.Material.Target = material;

        AddLabel(slot, TrackerLabel(tracker), TrackerFill, LabelHeight);

        return new TrackerMarker { Slot = slot, Material = material, Seen = true };
    }

    private static void AddSphere(Slot slot, float radius, colorHDR fill, BlendMode blend)
    {
        var mesh = slot.AttachComponent<LumoraMeshes.SphereMesh>();
        mesh.Radius.Value = radius;
        mesh.Segments.Value = 20;
        mesh.Rings.Value = 14;
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        var material = slot.AttachComponent<UnlitMaterial>();
        material.Color = fill;
        material.BlendMode.Value = blend;
        renderer.Material.Target = material;
    }

    private static void AddLabel(Slot marker, string text, colorHDR fill, float height)
    {
        var label = marker.AddSlot("Label");
        label.LocalPosition.Value = new float3(0f, height, 0f);
        label.AttachComponent<FaceLocalUser>();
        var renderer = label.AttachComponent<TextRenderer>();
        renderer.Text.Value = text;
        renderer.Size.Value = 0.045f;
        renderer.Color.Value = new color(fill.r, fill.g, fill.b, 1f);
        renderer.HorizontalAlign.Value = TextHorizontalAlignment.Center;
        renderer.VerticalAlign.Value = TextVerticalAlignment.Middle;
        renderer.OutlineThickness.Value = 1.2f;
    }

    private void DestroyMarkers()
    {
        _trackerMarkers.Clear();
        if (_markerRoot != null && !_markerRoot.IsDestroyed)
            _markerRoot.Destroy();
        _markerRoot = null;
    }

    // ---- confirm ----

    // Armed only once BOTH triggers have been seen released after Begin, so a trigger that was already
    // down when calibration started cannot confirm it. Then both have to stay past the press point for
    // the hold time; a release on either side resets the hold.
    private void PollConfirm(InputInterface input, float delta)
    {
        var left = input.LeftController;
        var right = input.RightController;
        float l = left != null && left.IsDeviceActive ? left.TriggerValue : 0f;
        float r = right != null && right.IsDeviceActive ? right.TriggerValue : 0f;

        bool bothDown = l >= PressPoint && r >= PressPoint;
        bool bothUp = l <= ReleasePoint && r <= ReleasePoint;

        if (!_armed)
        {
            if (bothUp)
            {
                _armed = true;
                SetStatus("Stand on the markers, then hold both triggers to confirm");
            }
            return;
        }

        float hold = MathF.Max(ConfirmHoldSeconds.Value, 0.1f);
        if (bothDown)
        {
            _hold += delta;
            if (_hold >= hold)
            {
                left?.TriggerHaptic(0.6f, 0.12f);
                right?.TriggerHaptic(0.6f, 0.12f);
                Confirm();
                return;
            }
            SetStatus($"Hold both triggers... {System.Math.Clamp(_hold / hold, 0f, 1f):P0}");
        }
        else if (_hold > 0f)
        {
            _hold = 0f;
            SetStatus("Stand on the markers, then hold both triggers to confirm");
        }
    }

    private void Solve(InputInterface input)
    {
        _lastResults.Clear();

        _trackers.Clear();
        input.GetDevices(_trackers, t => t.IsDeviceActive);

        float scale = RootScale();
        float maxDistance = MathF.Max(MaxPairDistance.Value, 0.05f) * scale;
        floatQ facing = FacingRotation();

        // Joint frames, read live: position at the bone, rotation the upright facing frame the avatar is
        // locked in. That frame is what the pose nodes equip against, so it is the pose a tracker has to
        // reproduce, not the bone's authored axes.
        int nodeCount = _nodes.Count;
        var nodePosition = new float3[nodeCount];
        var nodeValid = new bool[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            var bone = _nodes[i].bone;
            nodeValid[i] = bone != null && !bone.IsDestroyed;
            if (nodeValid[i])
                nodePosition[i] = bone!.GlobalPosition;
        }

        var candidates = new List<(float key, float distance, int tracker, int node)>();
        for (int t = 0; t < _trackers.Count; t++)
        {
            var tracker = _trackers[t];
            if (!tracker.IsTracking)
                continue;
            var suggested = TrackerRoles.ToBodyNode(tracker.SuggestedRole);
            float3 position = tracker.Position;
            for (int n = 0; n < nodeCount; n++)
            {
                if (!nodeValid[n])
                    continue;
                float distance = float3.Distance(position, nodePosition[n]);
                if (distance > maxDistance)
                    continue;
                float key = distance - (suggested == _nodes[n].node ? RoleTieBreak * scale : 0f);
                candidates.Add((key, distance, t, n));
            }
        }
        candidates.Sort((a, b) => a.key.CompareTo(b.key));

        var trackerClaimed = new bool[_trackers.Count];
        var nodeClaimed = new bool[nodeCount];
        int mapped = 0;

        for (int i = 0; i < candidates.Count; i++)
        {
            var (_, distance, t, n) = candidates[i];
            if (trackerClaimed[t] || nodeClaimed[n])
                continue;
            trackerClaimed[t] = true;
            nodeClaimed[n] = true;

            var tracker = _trackers[t];
            var node = _nodes[n].node;
            WriteMapping(tracker, node, nodePosition[n], facing, scale, distance);
            mapped++;
        }

        for (int t = 0; t < _trackers.Count; t++)
        {
            if (trackerClaimed[t])
                continue;
            var tracker = _trackers[t];
            string label = TrackerLabel(tracker);
            if (!tracker.IsTracking)
            {
                Report(tracker, label, BodyNode.NONE, -1f, false, "not tracking");
                LumoraLogger.Warn($"TrackerCalibration: '{label}' ({tracker.PublicID}) is not tracking, left unmapped");
                continue;
            }

            float nearest = float.MaxValue;
            BodyNode nearestNode = BodyNode.NONE;
            float3 position = tracker.Position;
            for (int n = 0; n < nodeCount; n++)
            {
                if (!nodeValid[n])
                    continue;
                float distance = float3.Distance(position, nodePosition[n]);
                if (distance < nearest)
                {
                    nearest = distance;
                    nearestNode = _nodes[n].node;
                }
            }

            if (nearestNode == BodyNode.NONE)
            {
                Report(tracker, label, BodyNode.NONE, -1f, false, "no joint available");
                continue;
            }

            bool tooFar = nearest > maxDistance;
            string reason = tooFar
                ? $"nearest joint {NodeLabel(nearestNode)} is {nearest:F2} m away, over the {maxDistance:F2} m limit"
                : $"nearest joint {NodeLabel(nearestNode)} was already claimed by a closer tracker";
            Report(tracker, label, nearestNode, nearest, false, reason);
            LumoraLogger.Warn($"TrackerCalibration: '{label}' ({tracker.PublicID}) not mapped: {reason}. Existing mapping, if any, left untouched");
        }

        LumoraLogger.Log($"TrackerCalibration: mapped {mapped} of {_trackers.Count} tracker(s)");
    }

    // The stored offset takes the TRACKER pose to the JOINT pose, expressed in the tracker's own frame:
    //
    //   rotationOffset = inverse(trackerRot) * jointRot
    //   positionOffset = inverse(trackerRot) * (jointPos - trackerPos) / trackingScale
    //
    // so that afterwards  jointPos = trackerPos + trackerRot * (positionOffset * trackingScale)  and
    // jointRot = trackerRot * rotationOffset. That is the child-transform composition
    // TrackedDevicePositioner applies (BodyNodeRoot.Local = offset under the device slot, whose world pose
    // is the tracker's), and the division by the tracking scale is there because that child's local units
    // are the user root's units, while the poses sampled here are world metres. The round-trip below is a
    // cheap guard that the maths agrees with itself; anything over a millimetre is logged. -xlinka
    private void WriteMapping(ITracker tracker, BodyNode node, float3 jointPosition, floatQ jointRotation, float rootScale, float distance)
    {
        float3 trackerPosition = tracker.Position;
        floatQ trackerRotation = tracker.Rotation.Normalized;
        floatQ inverse = trackerRotation.Inverse;

        float trackingScale = tracker.TrackingSpace?.Scale ?? rootScale;
        if (trackingScale < 1e-4f || float.IsNaN(trackingScale) || float.IsInfinity(trackingScale))
            trackingScale = 1f;

        float3 positionOffset = (inverse * (jointPosition - trackerPosition)) / trackingScale;
        floatQ rotationOffset = (inverse * jointRotation).Normalized;

        float3 check = trackerPosition + trackerRotation * (positionOffset * trackingScale);
        float error = float3.Distance(check, jointPosition);
        if (error > 0.001f)
            LumoraLogger.Warn($"TrackerCalibration: offset round-trip for {node} is off by {error * 1000f:F1} mm");

        string? customName = null;
        if (TrackerMappingStore.TryGet(tracker.UniqueID, out var existing))
            customName = existing.CustomName;

        var mapping = new TrackerMapping
        {
            UniqueID = tracker.UniqueID,
            Node = node,
            PositionOffset = positionOffset,
            RotationOffset = rotationOffset,
            Enabled = true,
            CustomName = customName,
            CalibratedRole = tracker.SuggestedRole,
            CalibratedUtcTicks = DateTime.UtcNow.Ticks,
        };
        TrackerMappingStore.Set(mapping);
        bool applied = TrackerMappingStore.Apply(tracker, mapping);

        // No slot work here any more. TrackerSlotManager gives every tracker a slot the moment the
        // hardware appears, whether or not it is mapped, so calibration only has to change where that
        // slot points. -xlinka

        string label = TrackerLabel(tracker);
        Report(tracker, label, node, distance, true, applied ? "mapped" : "mapped, stored only (device layer must apply)");
        LumoraLogger.Log(
            $"TrackerCalibration: '{label}' ({tracker.PublicID}) -> {node} at {distance:F3} m, " +
            $"posOffset={positionOffset} rotOffset={rotationOffset} live={(applied ? "applied" : "not applied")}");
    }

    private void Report(ITracker tracker, string label, BodyNode node, float distance, bool mapped, string reason)
    {
        _lastResults.Add(new PairResult
        {
            PublicID = tracker.PublicID ?? string.Empty,
            Label = label,
            Node = node,
            Distance = distance,
            Mapped = mapped,
            Reason = reason,
        });
    }

    private string Summary()
    {
        int mapped = 0;
        int skipped = 0;
        for (int i = 0; i < _lastResults.Count; i++)
        {
            if (_lastResults[i].Mapped) mapped++;
            else skipped++;
        }
        if (mapped == 0)
            return skipped == 0 ? "Nothing to map" : $"No tracker mapped ({skipped} rejected, see log)";
        return skipped == 0 ? $"Mapped {mapped} tracker(s)" : $"Mapped {mapped} tracker(s), {skipped} rejected (see log)";
    }

    // ---- frames and helpers ----

    private float3 AvatarFacing(Slot avatar, HumanoidRig rig)
    {
        float3 forward;
        if (!AvatarCalibration.TryComputeBodyAxes(rig, out _, out _, out forward))
            forward = avatar.GlobalRotation * float3.Backward;
        forward.y = 0f;
        return forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;
    }

    // Upright frame whose facing axis (-Z, the slot convention) points where the locked avatar faces.
    // Both inputs are horizontal so the swing axis is vertical and the result is yaw only.
    private floatQ FacingRotation()
    {
        if (_avatarRoot == null || _rig == null)
            return floatQ.Identity;
        float3 forward = AvatarFacing(_avatarRoot, _rig);
        return FabrikSolver.FromToRotation(float3.Backward, forward, float3.Up).Normalized;
    }

    private float RootScale()
    {
        var root = World?.LocalUser?.Root?.Slot;
        if (root == null || root.IsDestroyed)
            return 1f;
        float s = root.GlobalScale.x;
        return s > 1e-4f && !float.IsNaN(s) && !float.IsInfinity(s) ? s : 1f;
    }

    private void SetStatus(string text)
    {
        if (Status.Value != text)
            Status.Value = text;
    }

    private static colorHDR FillFor(BodyNode node)
    {
        string name = node.ToString();
        if (name.StartsWith("Left", StringComparison.Ordinal)) return LeftFill;
        if (name.StartsWith("Right", StringComparison.Ordinal)) return RightFill;
        return CentreFill;
    }

    private static string NodeLabel(BodyNode node) => node switch
    {
        BodyNode.Hips => "Hips",
        BodyNode.Chest => "Chest",
        BodyNode.LeftFoot => "Left Foot",
        BodyNode.RightFoot => "Right Foot",
        BodyNode.LeftLowerLeg => "Left Knee",
        BodyNode.RightLowerLeg => "Right Knee",
        BodyNode.LeftLowerArm => "Left Elbow",
        BodyNode.RightLowerArm => "Right Elbow",
        BodyNode.LeftShoulder => "Left Shoulder",
        BodyNode.RightShoulder => "Right Shoulder",
        _ => node.ToString(),
    };

    private static string TrackerLabel(ITracker tracker)
    {
        if (TrackerMappingStore.TryGet(tracker.UniqueID, out var mapping) && !string.IsNullOrEmpty(mapping.CustomName))
            return mapping.CustomName!;
        if (tracker.SuggestedRole != TrackerRole.None)
            return tracker.SuggestedRole.ToString();
        string id = tracker.PublicID ?? string.Empty;
        return "Tracker " + (id.Length > 4 ? id[^4..] : id);
    }

    // Column-major TRS -> translation + rotation. Scale is stripped off the basis columns and dropped:
    // bone scale is never touched by the solver, so it never needs restoring. A mirrored or degenerate
    // matrix, or a rotation that does not rebuild the columns it came from, is refused rather than
    // written to a bone. -xlinka
    private static bool TryDecompose(in float4x4 m, out float3 position, out floatQ rotation)
    {
        position = new float3(m[0, 3], m[1, 3], m[2, 3]);
        rotation = floatQ.Identity;

        var x = new float3(m[0, 0], m[1, 0], m[2, 0]);
        var y = new float3(m[0, 1], m[1, 1], m[2, 1]);
        var z = new float3(m[0, 2], m[1, 2], m[2, 2]);
        if (x.LengthSquared < 1e-12f || y.LengthSquared < 1e-12f || z.LengthSquared < 1e-12f)
            return false;
        x = x.Normalized;
        y = y.Normalized;
        z = z.Normalized;
        if (float3.Dot(float3.Cross(x, y), z) < 0f)
            return false;

        float m00 = x.x, m10 = x.y, m20 = x.z;
        float m01 = y.x, m11 = y.y, m21 = y.z;
        float m02 = z.x, m12 = z.y, m22 = z.z;

        float trace = m00 + m11 + m22;
        float qx, qy, qz, qw;
        if (trace > 0f)
        {
            float s = MathF.Sqrt(trace + 1f) * 2f;
            qw = 0.25f * s;
            qx = (m21 - m12) / s;
            qy = (m02 - m20) / s;
            qz = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
            qw = (m21 - m12) / s;
            qx = 0.25f * s;
            qy = (m01 + m10) / s;
            qz = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
            qw = (m02 - m20) / s;
            qx = (m01 + m10) / s;
            qy = 0.25f * s;
            qz = (m12 + m21) / s;
        }
        else
        {
            float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
            qw = (m10 - m01) / s;
            qx = (m02 + m20) / s;
            qy = (m12 + m21) / s;
            qz = 0.25f * s;
        }

        var q = new floatQ(qx, qy, qz, qw);
        if (float.IsNaN(q.Length) || q.Length < 1e-6f)
            return false;
        q = q.Normalized;

        var rebuilt = float4x4.Rotate(q);
        float worst = 0f;
        worst = MathF.Max(worst, MathF.Abs(rebuilt[0, 0] - m00));
        worst = MathF.Max(worst, MathF.Abs(rebuilt[1, 0] - m10));
        worst = MathF.Max(worst, MathF.Abs(rebuilt[2, 0] - m20));
        worst = MathF.Max(worst, MathF.Abs(rebuilt[0, 1] - m01));
        worst = MathF.Max(worst, MathF.Abs(rebuilt[1, 1] - m11));
        worst = MathF.Max(worst, MathF.Abs(rebuilt[2, 1] - m21));
        worst = MathF.Max(worst, MathF.Abs(rebuilt[0, 2] - m02));
        worst = MathF.Max(worst, MathF.Abs(rebuilt[1, 2] - m12));
        worst = MathF.Max(worst, MathF.Abs(rebuilt[2, 2] - m22));
        if (worst > 1e-3f)
            return false;

        rotation = q;
        return true;
    }
}
