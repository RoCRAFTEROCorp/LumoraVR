// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// Retarget approach: a convention-robust direction-aim swing rather than a
// LookRotation-based coordinate compensation (floatQ.LookRotation returns the
// inverse here and Godot is right-handed, so that path would invert). Each
// finger segment is aimed at the next using a shortest-arc swing: we capture, in
// the wrist's local frame, each bone's rest rotation and its rest
// direction-to-next, then at runtime swing that rest direction onto the
// source-provided direction. The cost is no finger twist (fingers barely twist,
// so this reads fine).
//
// Bone drives are LocalValueOnly: every peer computes finger pose itself, so no
// finger data goes on the wire. An untracked hand takes the idle preset, or the
// tool grip while its HandTool holds something (both replicated as state, not as
// finger data), and the authored rest only when it has neither. - xlinka
[ComponentCategory("Users/Avatar/Hands")]
public class HandPoseDriver : UserRootComponent
{
    public readonly Sync<Chirality> Side = new();

    // falls back to the user-root UserHandPoseInfo when null
    public readonly SyncRef<IHandPoseSourceComponent> PoseSource = null!;

    // used only when nothing is tracking; live VR finger tracking still wins whenever it's actually tracking
    public readonly SyncRef<IHandPoseSourceComponent> IdlePose = null!;

    // resolved from the rig when unset
    public readonly SyncRef<Slot> HandRoot = null!;

    // The visibly-curling joints, proximal outward. Thumb has no intermediate.
    // Tip is included only as an aim target for the distal bone.
    private static readonly FingerType[] AllFingers =
    {
        FingerType.Thumb, FingerType.Index, FingerType.Middle, FingerType.Ring, FingerType.Pinky,
    };

    private static readonly FingerSegmentType[] ThumbSegments =
    {
        FingerSegmentType.Proximal, FingerSegmentType.Distal, FingerSegmentType.Tip,
    };

    private static readonly FingerSegmentType[] FingerSegments =
    {
        FingerSegmentType.Proximal, FingerSegmentType.Intermediate, FingerSegmentType.Distal, FingerSegmentType.Tip,
    };

    private sealed class SegmentDrive
    {
        public Slot Bone = null!;
        public BodyNode Node;
        public BodyNode NextNode;
        public FieldDrive<floatQ> Drive = null!;
        public floatQ RestLocalRotation;
        public floatQ RestRotWrist;
        public float3 RestDirWrist;        // (this -> next)
    }

    private readonly List<List<SegmentDrive>> _fingers = new();
    private Slot _wrist = null!;
    private bool _assigned;
    private bool _handReset;

    // TOOL GRIP
    //
    // Nothing tracks a desktop hand's fingers, so the only thing that can shape them is the tool: a
    // hand with a tool in it curls round the handle, and lets go when the tool goes away. The source
    // platform does not have this exact step - there, desktop fingers ride the locomotion animation's
    // relaxed preset and the TOOL is moved into the curl by its grip-pose alignment - but it has no
    // hand-shaped preset either, and an open idle hand with a tool floating in front of it is what the
    // screenshots showed. So: a handle grip, eased in on equip and out on dequip.
    //
    // Driven off the hand's HandTool.ActiveToolItem, which replicates, so every peer curls the same
    // hand with no extra sync. Ordered BELOW live finger tracking: a source that is actually tracking
    // this side always wins, so VR hands with finger data keep their real shape and only the untracked
    // case (desktop, or a controller with no finger tracking) takes the grip. -xlinka
    private HandTool? _handTool;
    private double _nextHandToolScan = double.NegativeInfinity;
    private float _gripWeight;
    private static readonly float[] GripCurl = { 0.45f, 0.42f, 0.55f, 0.58f, 0.60f };
    private static Dictionary<BodyNode, float3>? _gripLeft;
    private static Dictionary<BodyNode, float3>? _gripRight;
    private readonly GripPose _gripPose = new();
    private readonly BlendedPose _blendPose = new();

    // 1/s ease on the grip, matching HandTool's hold blend so the fingers close as the arm comes up.
    private const float GripBlendRate = 12f;

    // What the hand is doing right now, for HandTool's TOOLHOLD trace: which source shaped it, how far
    // into the grip it is, and whether it ever bound to finger bones at all.
    public enum PoseState
    {
        Unassigned,
        Rest,
        Idle,
        Grip,
        Tracked,
    }

    public PoseState CurrentState { get; private set; }
    public float GripWeight => _gripWeight;
    public bool IsAssigned => _assigned;
    public float3 PoseBasisForward => _poseBasisZ;
    public float3 PoseBasisBack => _poseBasisY;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (!_assigned)
        {
            TryAssign();
            if (!_assigned)
            {
                CurrentState = PoseState.Unassigned;
                return;
            }
        }

        UpdateGripWeight(delta);

        var source = ResolveSource(Side.Value, out var state, out float restBlend);
        if (source == null || !source.IsHandTracked(Side.Value))
        {
            // Untracked (no live source and no fallback): park the hand in its authored pose once.
            CurrentState = PoseState.Rest;
            if (!_handReset)
            {
                ResetToRest();
                _handReset = true;
            }
            return;
        }

        CurrentState = state;
        _handReset = false;
        DrivePose(source, restBlend);
    }

    // Grip wanted = this side's HandTool has an item in it. Found by a throttled walk of the user root
    // (the same way AvatarIK finds its tools) and cached until it dies.
    private void UpdateGripWeight(float delta)
    {
        bool wanted = false;
        var tool = ResolveHandTool();
        if (tool != null)
            wanted = tool.ActiveToolItem.Target != null;

        float target = wanted ? 1f : 0f;
        float t = System.Math.Clamp(1f - MathF.Exp(-GripBlendRate * MathF.Max(delta, 0f)), 0f, 1f);
        _gripWeight += (target - _gripWeight) * t;
        if (_gripWeight < 0.001f)
            _gripWeight = 0f;
        else if (_gripWeight > 0.999f)
            _gripWeight = 1f;
    }

    private HandTool? ResolveHandTool()
    {
        if (_handTool != null && !_handTool.IsDestroyed && _handTool.Side.Value == Side.Value)
            return _handTool;

        double now = World?.Time.TotalTime ?? 0.0;
        if (now < _nextHandToolScan)
            return null;
        _nextHandToolScan = now + 1.0;

        var rootSlot = Slot?.ActiveUserRoot?.Slot;
        if (rootSlot == null || rootSlot.IsDestroyed)
            return null;

        var side = Side.Value;
        _handTool = rootSlot.GetComponentInChildren<HandTool>(t => t.Side.Value == side);
        return _handTool;
    }

    public override void OnDestroy()
    {
        ReleaseDrives();
        base.OnDestroy();
    }

    // Pick the source for one hand. Order: the explicit PoseSource (the equip assigner copies the user-root
    // VR stream here), else the user-root source directly - either, when actually tracking this side, beats
    // everything else so live VR finger tracking always wins. Then the tool grip, blended over the idle
    // fallback (or over the authored rest when there is no idle preset - restBlend says how far off rest
    // the caller should take the result). Then the idle preset alone, which gives the hand a real relaxed
    // shape instead of the authored bind pose.
    private IHandPoseSource ResolveSource(Chirality side, out PoseState state, out float restBlend)
    {
        restBlend = 1f;
        var primary = PoseSource?.Target
                      ?? Slot?.ActiveUserRoot?.GetRegisteredComponent<UserHandPoseInfo>()?.HandPoseSource?.Target;
        if (primary != null && primary.IsHandTracked(side))
        {
            state = PoseState.Tracked;
            return primary;
        }

        var idle = IdlePose?.Target;
        if (_gripWeight > 0f)
        {
            state = PoseState.Grip;
            _gripPose.Positions = GripPositions(side);
            if (idle != null && idle.IsHandTracked(side))
            {
                _blendPose.Set(idle, _gripPose, _gripWeight);
                return _blendPose;
            }
            // No idle shape to leave from: the caller eases from the authored rest instead.
            restBlend = _gripWeight;
            return _gripPose;
        }

        if (idle != null)
        {
            state = PoseState.Idle;
            return idle;
        }

        // No idle fallback: keep the prior behavior of returning the primary source directly (it may be
        // present but momentarily not tracking, or absent - the caller then rests the hand).
        state = PoseState.Rest;
        return primary!;
    }

    // A handle grip in the pose model's canonical frame, built once per side and shared by every hand
    // in the process: the model is deterministic and the drives are local, so there is nothing per-user
    // in it. Index a touch straighter than the rest (it is the trigger finger), thumb wrapped over.
    private static Dictionary<BodyNode, float3> GripPositions(Chirality side)
    {
        var cache = side == Chirality.Left ? _gripLeft : _gripRight;
        if (cache != null)
            return cache;

        var built = new Dictionary<BodyNode, float3>();
        for (int f = 0; f < HandPoseNodes.Fingers.Length; f++)
        {
            var finger = HandPoseNodes.Fingers[f];
            HandPoseModel.GenerateFinger(finger, side, GripCurl[f], 0f, (node, pos) => built[node] = pos);
        }
        if (side == Chirality.Left)
            _gripLeft = built;
        else
            _gripRight = built;
        return built;
    }

    private sealed class GripPose : IHandPoseSource
    {
        public Dictionary<BodyNode, float3>? Positions;
        public bool TracksMetacarpals => true;
        public bool IsHandTracked(Chirality side) => Positions != null;
        public bool TryGetFingerPosition(BodyNode node, out float3 wristLocalPosition)
        {
            wristLocalPosition = float3.Zero;
            return Positions != null && Positions.TryGetValue(node, out wristLocalPosition);
        }
    }

    // Position-space lerp between two sources. The consumer only reads directions between consecutive
    // nodes, so lerping node positions is a clean curl blend for the small shape change idle -> grip
    // covers (no segment passes through zero length on that path).
    private sealed class BlendedPose : IHandPoseSource
    {
        private IHandPoseSource _a = null!;
        private IHandPoseSource _b = null!;
        private float _t;

        public void Set(IHandPoseSource a, IHandPoseSource b, float t)
        {
            _a = a;
            _b = b;
            _t = t;
        }

        public bool TracksMetacarpals => _a.TracksMetacarpals && _b.TracksMetacarpals;
        public bool IsHandTracked(Chirality side) => _a.IsHandTracked(side) && _b.IsHandTracked(side);

        public bool TryGetFingerPosition(BodyNode node, out float3 wristLocalPosition)
        {
            wristLocalPosition = float3.Zero;
            bool hasA = _a.TryGetFingerPosition(node, out var pa);
            bool hasB = _b.TryGetFingerPosition(node, out var pb);
            if (!hasA && !hasB)
                return false;
            if (!hasA)
            {
                wristLocalPosition = pb;
                return true;
            }
            if (!hasB)
            {
                wristLocalPosition = pa;
                return true;
            }
            wristLocalPosition = float3.Lerp(pa, pb, _t);
            return true;
        }
    }

    // Bind to the rig's finger bones and capture the rest calibration. Retried
    // each frame until the avatar's rig is built and its hand has fingers.
    private void TryAssign()
    {
        var rig = Slot?.GetComponentInParent<HumanoidRig>() ?? Slot?.GetComponentInChildren<HumanoidRig>();
        if (rig == null)
            return;

        // Not on four legs.
        //
        // A quadruped keeps its HumanoidRig - that is how it reached the quadruped solver in the first
        // place - so this still finds finger bones and happily curls them to whatever the wearer's
        // controller fingers are doing. On a fox those "fingers" are the front paw digits, and the
        // quadruped solver is at the same time trying to plant that paw on the ground. Two things
        // writing the same bones in the same phase, which is the exact fight TryAttachFor documents
        // for AvatarIK. The equip path no longer ATTACHES one of these to a quadruped; this covers the
        // ones that arrive already attached inside an imported avatar. -xlinka
        var quadruped = Slot?.GetComponentInParent<QuadrupedRig>() ?? Slot?.GetComponentInChildren<QuadrupedRig>();
        if (quadruped != null && !quadruped.IsDestroyed)
            return;

        var side = Side.Value;
        bool hasFingers = side == Chirality.Left ? rig.HasLeftFingerBones : rig.HasRightFingerBones;
        if (!hasFingers)
            return;

        var wrist = HandRoot?.Target
                    ?? rig.TryGetBone(side == Chirality.Left ? BodyNode.LeftHand : BodyNode.RightHand);
        if (wrist == null)
            return;

        ReleaseDrives();
        _wrist = wrist;
        _fingers.Clear();
        MeasurePoseBasis(rig, side, wrist);

        foreach (var finger in AllFingers)
        {
            var chain = BuildChain(rig, finger, side, wrist);
            if (chain.Count > 0)
                _fingers.Add(chain);
        }

        _assigned = _fingers.Count > 0;
    }

    // The hand frame this rig actually uses, in wrist-local space.
    //
    // The pose model works in a CANONICAL frame - fingers along +Z, back of the hand toward +Y - and
    // hands back wrist-local positions in it. Nothing was checking whether the rig agreed, and a rig
    // that disagrees gets every curl delivered as a swing in the wrong plane: fingers splay open
    // instead of closing. Measured on a real avatar this one's fingers run along +Y with the back of
    // the hand toward +Z, a quarter turn out, which is exactly what a splayed paw looks like.
    //
    // A rig that already matches the canonical frame measures the identity here and nothing changes.
    // -xlinka
    private float3 _poseBasisX = float3.Right;
    private float3 _poseBasisY = float3.Up;
    private float3 _poseBasisZ = float3.Forward;

    private void MeasurePoseBasis(HumanoidRig rig, Chirality side, Slot wrist)
    {
        _poseBasisX = float3.Right;
        _poseBasisY = float3.Up;
        _poseBasisZ = float3.Forward;

        var middle = rig.TryGetBone(FingerType.Middle.ComposeFinger(FingerSegmentType.Proximal, side));
        var thumb = rig.TryGetBone(FingerType.Thumb.ComposeFinger(FingerSegmentType.Proximal, side));
        if (middle == null || middle.IsDestroyed || thumb == null || thumb.IsDestroyed)
            return;

        var fingers = wrist.GlobalPointToLocal(middle.GlobalPosition);
        var toThumb = wrist.GlobalPointToLocal(thumb.GlobalPosition);
        if (fingers.LengthSquared < 1e-8f || toThumb.LengthSquared < 1e-8f)
            return;

        var forward = fingers.Normalized;

        // The sign is PER SIDE, and it is the right hand that takes the plain cross.
        //
        // A cross product is a pseudovector: mirror both inputs and it flips. The two hands are mirror
        // images, so the same formula lands on opposite anatomical sides of each - and bone frames do
        // not cancel that, because a bone frame is a proper rotation and the cross commutes with it.
        // Checked on Chiki's rig from the AvatarIK[bones] dump: right hand hanging at the side, fingers
        // down, thumb forward, Cross(fingers, toThumb) came out +X = lateral = the BACK of that hand;
        // left hand pointing forward, thumb up, the same cross came out +X = medial = the PALM. The
        // source platform's hand poser encodes the same thing by picking its "right" vector from a
        // per-side finger order (index->pinky on one hand, pinky->index on the other) before crossing.
        //
        // Negating both, as this did, put the right hand's Y on the palm: every curl went toward the
        // back of the hand and the splay came out mirrored, which is the hyperextended, fanned-open
        // "stop" hand in the tool-hold screenshots. -xlinka
        var back = float3.Cross(forward, toThumb.Normalized);
        if (side == Chirality.Left)
            back = -back;
        if (back.LengthSquared < 1e-8f)
            return;
        back = back.Normalized;

        var across = float3.Cross(back, forward);
        if (across.LengthSquared < 1e-8f)
            return;

        _poseBasisZ = forward;
        _poseBasisY = back;
        _poseBasisX = across.Normalized;

        Logging.Logger.Log(
            $"HandPoseDriver: {side} basis in '{wrist.SlotName.Value}' space fingers={forward} back={back} "
            + $"thumbSide={_poseBasisX} (toThumb={toThumb.Normalized})");
    }

    private List<SegmentDrive> BuildChain(HumanoidRig rig, FingerType finger, Chirality side, Slot wrist)
    {
        var segTypes = finger == FingerType.Thumb ? ThumbSegments : FingerSegments;

        var bones = new List<(Slot bone, BodyNode node, float3 posWrist, floatQ rotWrist, floatQ local)>();
        foreach (var seg in segTypes)
        {
            var node = finger.ComposeFinger(seg, side);
            var bone = rig.TryGetBone(node);
            if (bone == null)
                continue;

            bones.Add((
                bone,
                node,
                wrist.GlobalPointToLocal(bone.GlobalPosition),
                wrist.GlobalRotationToLocal(bone.GlobalRotation),
                bone.LocalRotation.Value));
        }

        var chain = new List<SegmentDrive>();
        for (int i = 0; i < bones.Count - 1; i++)
        {
            var a = bones[i];
            var b = bones[i + 1];

            var dir = b.posWrist - a.posWrist;
            if (dir.LengthSquared < 1e-10f)
                continue;

            // Detached local drives, NOT members: how many finger segments exist and which bones they
            // land on is discovered from whatever rig this peer built, so replicating the links would put
            // every peer's rig discovery on the wire to fight over. Each peer drives its own copy of the
            // same bones from the same replicated hand-pose source instead. -xlinka
            var drive = FieldDrive<floatQ>.CreateLocal(this);
            drive.LocalValueOnly = true;
            drive.DriveTarget(a.bone.LocalRotation);

            chain.Add(new SegmentDrive
            {
                Bone = a.bone,
                Node = a.node,
                NextNode = b.node,
                Drive = drive,
                RestLocalRotation = a.local,
                RestRotWrist = a.rotWrist,
                RestDirWrist = dir.Normalized,
            });
        }

        return chain;
    }

    // restBlend < 1 eases the whole hand off its authored rest toward the source (the grip coming in on
    // a hand that has no idle preset to leave from); 1 is the plain source.
    private void DrivePose(IHandPoseSource source, float restBlend = 1f)
    {
        if (_wrist == null || _wrist.IsDestroyed)
            return;

        bool tracksMetacarpals = source.TracksMetacarpals;
        bool blendFromRest = restBlend < 0.999f;

        foreach (var chain in _fingers)
        {
            foreach (var seg in chain)
            {
                if (!seg.Drive.IsLinkValid)
                    continue;

                // When the source has no metacarpal data, leave any segment whose
                // aim involves a metacarpal node at its authored rest - driving it
                // from absent/stale data would splay the palm. (The default segment
                // chains start at Proximal, so this normally no-ops; it guards
                // sources that do publish metacarpals against this one not.)
                if (!tracksMetacarpals &&
                    (IsMetacarpal(seg.Node) || IsMetacarpal(seg.NextNode)))
                {
                    seg.Drive.SetValue(seg.RestLocalRotation);
                    continue;
                }

                if (!source.TryGetFingerPosition(seg.Node, out var pA) ||
                    !source.TryGetFingerPosition(seg.NextNode, out var pB))
                {
                    seg.Drive.SetValue(seg.RestLocalRotation);
                    continue;
                }

                // Canonical -> this rig's measured hand frame. Linear, so mapping the difference is the
                // same as mapping both endpoints.
                var raw = pB - pA;
                var desired = _poseBasisX * raw.x + _poseBasisY * raw.y + _poseBasisZ * raw.z;
                if (desired.LengthSquared < 1e-10f)
                {
                    seg.Drive.SetValue(seg.RestLocalRotation);
                    continue;
                }

                // Swing the bone's rest direction onto the source direction, both
                // in wrist space, then carry the result back through the (current)
                // wrist world rotation into the bone's parent-local space.
                var newRotWrist = FromTo(seg.RestDirWrist, desired.Normalized) * seg.RestRotWrist;
                var worldRot = _wrist.LocalRotationToGlobal(newRotWrist);
                var parent = seg.Bone.Parent;
                var local = parent != null ? parent.GlobalRotationToLocal(worldRot) : worldRot;
                if (blendFromRest)
                    local = floatQ.Slerp(seg.RestLocalRotation, local, restBlend).Normalized;
                seg.Drive.SetValue(local);
            }
        }
    }

    private void ResetToRest()
    {
        foreach (var chain in _fingers)
            foreach (var seg in chain)
                if (seg.Drive.IsLinkValid)
                    seg.Drive.SetValue(seg.RestLocalRotation);
    }

    private void ReleaseDrives()
    {
        foreach (var chain in _fingers)
            foreach (var seg in chain)
                seg.Drive?.Dispose();
        _fingers.Clear();
        _assigned = false;
        _handReset = false;
    }

    private static bool IsMetacarpal(BodyNode node)
        => node.IsFinger() && node.GetFingerSegmentType() == FingerSegmentType.Metacarpal;

    // Shortest-arc rotation taking unit vector `from` onto unit vector `to`.
    private static floatQ FromTo(float3 from, float3 to)
    {
        float d = float3.Dot(from, to);
        if (d >= 0.99999f)
            return floatQ.Identity;
        if (d <= -0.99999f)
        {
            var axis = float3.Cross(float3.Up, from);
            if (axis.LengthSquared < 1e-6f)
                axis = float3.Cross(float3.Right, from);
            return floatQ.AxisAngleRad(axis.Normalized, MathF.PI);
        }
        var c = float3.Cross(from, to).Normalized;
        float angle = MathF.Acos(System.Math.Clamp(d, -1f, 1f));
        return floatQ.AxisAngleRad(c, angle);
    }
}
