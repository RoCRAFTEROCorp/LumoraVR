// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.Avatar.IK;
using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

// IK for an avatar that stands on four legs.
//
// Occupies the slot AvatarIK occupies and REPLACES it there - the two must never coexist. Both run
// FixTransforms and then write the same bone LocalRotations, in the same phase, at the same update
// order, and the dispatch sort is not stable, so the winner changes frame to frame and the pose
// flickers. That is not a hypothetical: the codebase already hit it with two AvatarIKs on one avatar
// and fixed it by destroying the strays. The branch therefore lives at the three ATTACH sites, not in
// a defensive sweep in here, because a sweep would turn into an attach/destroy race once per equip.
//
// The head is the only part of a quadruped a person drives by default. The legs are procedural unless
// a tracked hand or foot is somewhere a paw could physically be, in which case that tracker owns the
// leg (see BuildPawTargets for the gate and why it exists). -xlinka
[ComponentCategory("Users/Avatar")]
[DefaultUpdateOrder(-5000)]
public class QuadrupedIK : Component, IAvatarEquipReceiver, IInputUpdateReceiver
{
    private readonly QuadrupedIKSolver _solver = new();
    private readonly QuadrupedGait _gait = new();

    public readonly SyncRef<SkeletonBuilder> Skeleton = new();
    public readonly SyncRef<QuadrupedRig> Rig = new();
    public readonly SyncRef<UserRoot> UserRoot = new();

    [Group("General")]
    public readonly Sync<bool> IKEnabled = new();
    public readonly Sync<bool> UseProceduralGait = new();
    public readonly Sync<float> PawIKWeight = new();
    public readonly Sync<float> BodyLength = new();

    // How much the wearer's crouch lowers the animal, and how far down it may go.
    public readonly Sync<float> CrouchFollow = new();

    // Distance from the lowest PAW BONE down to the bottom of the mesh. The solver plants bones, not
    // pads, so whatever fur or pad sits below the lowest bone floats above the floor by exactly this
    // much. Measured from the mesh at rest capture; set non-zero to override.
    public readonly Sync<float> SoleOffset = new();
    public readonly Sync<float> MinCrouchScale = new();
    public readonly Sync<float> HeightCompensation = new();

    [Group("Gait")]
    public readonly Sync<QuadGaitMode> GaitMode = new();
    public readonly Sync<float> StrideLength = new();
    public readonly Sync<float> DutyFactor = new();
    public readonly Sync<float> StepHeight = new();
    public readonly Sync<float> StepThreshold = new();
    public readonly Sync<float> StepAngleThreshold = new();
    // SECONDS of velocity lookahead, like the biped's. This was metres, applied unscaled by speed, so
    // every stance point sat 12 cm ahead of its girdle with the animal standing still - the front half
    // of the resting splay. Velocity times seconds is zero at rest by construction. -xlinka
    public readonly Sync<float> StepPrediction = new();
    // Longest a swing may take. A swing runs on its own clock so it always finishes, including when
    // the body stops under it; at speed the gait shortens it to keep up.
    public readonly Sync<float> StepDuration = new();
    // Root-to-paw distance, as a fraction of the leg's straight length, past which a planted leg must
    // step whatever the home distance says. The rig's own rest extension plus a margin overrides a
    // value below it, so a straight-legged rig does not step forever.
    public readonly Sync<float> MaxLegStretch = new();
    public readonly Sync<float> MaxStepVelocity = new();
    public readonly Sync<float> BodyBob = new();
    public readonly Sync<float> StanceWidth = new();
    public readonly Sync<float> GroundConformScale = new();

    [Group("Body")]
    public readonly Sync<float> RollGain = new();
    public readonly Sync<float> MaxBodyRoll = new();
    public readonly Sync<float> SpineStiffness = new();
    public readonly Sync<float> NeckStiffness = new();
    public readonly Sync<float> MaxHeadYaw = new();
    public readonly Sync<float> LimbStretch = new();
    public readonly Sync<float> BendGoalWeight = new();

    // The chest and pelvis proxies are plain slots the gait writes. The head and the four PAWS carry pose
    // drivers, because those are the five things a wearer can actually put a tracker on.
    //
    // The paws used to be plain too, on the reasoning that a quadruped has no tracker that maps to them.
    // That is only true of a quadruped's own anatomy - the PERSON wearing it still has two hands and two
    // feet, and the mapping is the obvious one: hands to the front pair, feet to the rear. Without it the
    // gait owned all four legs unconditionally, so the back legs could only ever follow the same
    // procedural cycle as the front and no tracker could say otherwise. -xlinka
    private readonly SyncRef<Slot> _headProxy = new();
    private readonly SyncRef<AvatarPoseDriver> _headNode = new();
    private readonly SyncRef<AvatarPoseDriver> _frontLeftPawNode = new();
    private readonly SyncRef<AvatarPoseDriver> _frontRightPawNode = new();
    private readonly SyncRef<AvatarPoseDriver> _rearLeftPawNode = new();
    private readonly SyncRef<AvatarPoseDriver> _rearRightPawNode = new();
    private readonly SyncRef<Slot> _chestProxy = new();
    private readonly SyncRef<Slot> _pelvisProxy = new();
    private readonly SyncRef<Slot> _frontLeftPawProxy = new();
    private readonly SyncRef<Slot> _frontRightPawProxy = new();
    private readonly SyncRef<Slot> _rearLeftPawProxy = new();
    private readonly SyncRef<Slot> _rearRightPawProxy = new();

    private bool _isInitialized;
    private bool _suspendSolve;
    private bool _registered;

    private readonly QuadrupedIKSolver.LimbTarget[] _pawTargets = new QuadrupedIKSolver.LimbTarget[QuadLimb.Count];
    private readonly float3[] _homePoints = new float3[QuadLimb.Count];
    private readonly float3[] _limbRoots = new float3[QuadLimb.Count];
    private readonly float[] _limbReach = new float[QuadLimb.Count];
    private readonly Slot?[] _pawProxies = new Slot?[QuadLimb.Count];
    private readonly AvatarPoseDriver?[] _pawNodes = new AvatarPoseDriver?[QuadLimb.Count];

    // The authored stance, captured with the rest pose and carried on the body.
    //
    // Each paw's home point is its REST position relative to a body-fixed reference: the chest for a
    // front leg, the pelvis for a rear one. Not the girdle. The girdle is the midpoint of the limb
    // roots, read from last frame's SOLVED pose, and the front root is the scapula, which the limb
    // solver swings freely - so a scapula pulled forward to reach a paw moved next frame's home
    // forward, and the paw chased its own shadow. The reference points are stored in the rig slot's
    // space and transformed by the live slot each frame, which is body-fixed by construction: the
    // solver never writes anything above the pelvis. The offset is a forward/lateral pair in metres at
    // capture, rescaled by the live body length so an equip rescale after capture does not shrink or
    // stretch the stance. -xlinka
    private readonly float2[] _restPawOffset = new float2[QuadLimb.Count];
    private readonly float[] _restReachRatio = new float[QuadLimb.Count];
    private readonly bool[] _hasRestStance = new bool[QuadLimb.Count];
    private float3 _restChestLocal;
    private float3 _restPelvisLocal;
    private float _restStanceBodyLength;
    private const float StretchMargin = 0.04f;

    // Which of the wearer's tracked nodes owns each leg.
    private static readonly BodyNode[] PawBodyNodes =
    {
        BodyNode.LeftHand,   // FrontLeft
        BodyNode.RightHand,  // FrontRight
        BodyNode.LeftFoot,   // RearLeft
        BodyNode.RightFoot,  // RearRight
    };

    private float3 _bodyBobOffset;
    private float _lastGroundY;

    // Per-limb: is a tracker driving this leg this frame? Kept only for the diagnostics line.
    private readonly bool[] _limbTracked = new bool[QuadLimb.Count];

    // Diagnostics are logged when the solve STATE changes (a leg flips between tracked and procedural,
    // the gait initialises, the chassis becomes valid), never per frame, and never faster than the
    // interval below even if something flaps. -xlinka
    private string? _lastDiagnosticSignature;
    private double _lastDiagnosticTime = double.NegativeInfinity;
    private bool _gaitBlockedLogged;
    private const double DiagnosticMinInterval = 2.0;

    public override void OnInit()
    {
        base.OnInit();

        IKEnabled.Value = true;
        UseProceduralGait.Value = true;
        PawIKWeight.Value = 1f;
        BodyLength.Value = 0f;
        CrouchFollow.Value = 1f;
        SoleOffset.Value = 0f;
        MinCrouchScale.Value = 0.35f;
        HeightCompensation.Value = 1f;

        GaitMode.Value = QuadGaitMode.Trot;
        StrideLength.Value = 0.55f;
        DutyFactor.Value = 0.42f;
        StepHeight.Value = 0.08f;
        StepThreshold.Value = 0.22f;
        StepAngleThreshold.Value = 28f;
        StepPrediction.Value = 0.12f;
        StepDuration.Value = 0.25f;
        MaxLegStretch.Value = 0.92f;
        MaxStepVelocity.Value = 6f;
        BodyBob.Value = 0.02f;
        StanceWidth.Value = 1f;
        GroundConformScale.Value = 1f;

        RollGain.Value = 0.7f;
        MaxBodyRoll.Value = 0.3f;
        SpineStiffness.Value = 0.6f;
        NeckStiffness.Value = 0.5f;
        MaxHeadYaw.Value = 1.4f;
        LimbStretch.Value = 1.06f;
        BendGoalWeight.Value = 1f;
    }

    public override void OnStart()
    {
        base.OnStart();

        var input = Engine.Current?.InputInterface;
        if (input != null)
        {
            input.RegisterInputEventReceiver(this);
            _registered = true;
        }
    }

    public override void OnDestroy()
    {
        if (_registered)
            Engine.Current?.InputInterface?.UnregisterInputEventReceiver(this);
        base.OnDestroy();
    }

    // SETUP

    public bool SetupFromAvatar(UserRoot userRoot)
    {
        var rig = Rig.Target ?? Slot.GetComponent<QuadrupedRig>() ?? Slot.GetComponentInChildren<QuadrupedRig>();
        var skeleton = Skeleton.Target
            ?? SkeletonHolding(rig)
            ?? Slot.GetComponent<SkeletonBuilder>()
            ?? Slot.GetComponentInChildren<SkeletonBuilder>();

        if (skeleton == null || rig == null)
        {
            LumoraLogger.Warn("QuadrupedIK: avatar tree has no skeleton or quadruped rig");
            return false;
        }

        Setup(skeleton, rig, userRoot);
        SetupTracking(userRoot);
        return true;
    }

    // An avatar can carry SEVERAL skeletons - a body, a tail, a swap mesh - and a fox very commonly
    // ships a separate tail rig. GetComponentInChildren answers with whichever it reaches first, and
    // driving a body off a tail's bone set fails silently and completely. Pick the one that actually
    // holds this rig's pelvis.
    private SkeletonBuilder? SkeletonHolding(QuadrupedRig? rig)
    {
        var pelvis = rig?.TryGetBone(QuadNode.Pelvis);
        if (pelvis == null)
            return null;

        foreach (var candidate in Slot.GetComponentsInChildren<SkeletonBuilder>())
        {
            for (int i = 0; i < candidate.BoneCount && i < candidate.BoneSlots.Count; i++)
            {
                if (ReferenceEquals(candidate.BoneSlots[i], pelvis))
                    return candidate;
            }
        }
        return null;
    }

    public void Setup(SkeletonBuilder skeleton, QuadrupedRig rig, UserRoot userRoot)
    {
        Skeleton.Target = skeleton;
        Rig.Target = rig;
        UserRoot.Target = userRoot;

        EnsureProxies();
        EnsureSolver();

        // BodyLength is NOT baked here any more. It used to be captured once at first setup, and the
        // avatar's scale is not final at that point: the import compensation for the armature scale
        // settles a frame after import and the equip rescales again. A stale value scales every gait
        // threshold with it, so the animal would either never step or step every frame. A zero value
        // means "measure live"; a non-zero value is an author override and is honoured as before.
        _isInitialized = _solver.IsReady;
        if (!_isInitialized)
            LumoraLogger.Warn("QuadrupedIK: solver did not capture a usable rig");
    }

    // The animal's real dimensions, for anything outside the solver that has to fit a shape to it.
    //
    // Stand height is the withers above the sole plane, width is the wider of the two girdle tracks.
    // Both come from the solver's rest capture and the rig's girdles, which are measured off the limb
    // roots and so do not care what the spine bones were named. False until the rest pose is captured,
    // because before that every number is a bind-pose guess. -xlinka
    public bool TryGetBodyMetrics(out float standHeight, out float bodyLength, out float width)
    {
        standHeight = 0f;
        bodyLength = 0f;
        width = 0f;

        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed || !_solver.IsReady)
            return false;

        standHeight = _solver.RestChestStandHeight;
        bodyLength = EffectiveBodyLength();

        if (rig.TryGetGirdle(front: true, out _, out float frontTrack, out _, out _))
            width = frontTrack;
        if (rig.TryGetGirdle(front: false, out _, out float rearTrack, out _, out _))
            width = MathF.Max(width, rearTrack);

        return standHeight > 0.01f;
    }

    // The animal's own head bone, for anything that has to sit ON the animal rather than on the wearer.
    public Slot? HeadBone => Rig.Target is { IsDestroyed: false } rig ? rig.TryGetBone(QuadNode.Head) : null;

    private float EffectiveBodyLength()
        => BodyLength.Value > 0f ? BodyLength.Value : MathF.Max(_solver.RestBodyLength, 0.1f);

    private void EnsureSolver()
    {
        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return;
        _solver.Initialize(rig);
        CaptureRestStance(rig);
        _gait.Bind(World, UserRoot.Target?.Slot);
        _gait.Reset();
    }

    // Runs right after the solver's Initialize, while the bones still sit in the pose it just stored
    // as rest, so the paw positions read here are exactly the solver's rest paws. Re-run on every
    // recapture attempt at equip, so the last capture is the settled one. -xlinka
    private void CaptureRestStance(QuadrupedRig rig)
    {
        _restStanceBodyLength = 0f;
        _rootAboveAnchor[0] = _rootAboveAnchor[1] = 0f;
        _rootAboveAnchorCount[0] = _rootAboveAnchorCount[1] = 0;
        for (int i = 0; i < QuadLimb.Count; i++)
            _hasRestStance[i] = false;

        var bodySlot = rig.Slot;
        if (bodySlot == null || bodySlot.IsDestroyed || !_solver.IsReady)
            return;

        float3 forward = rig.BodyForward;
        forward.y = 0f;
        if (forward.LengthSquared < 1e-8f)
            return;
        forward = forward.Normalized;
        float3 right = float3.Cross(float3.Up, forward);
        if (right.LengthSquared < 1e-8f)
            return;
        right = right.Normalized;

        _restChestLocal = bodySlot.GlobalPointToLocal(_solver.RestChestPosition);
        _restPelvisLocal = bodySlot.GlobalPointToLocal(_solver.RestPelvisPosition);
        _restStanceBodyLength = MathF.Max(_solver.RestBodyLength, 0.05f);

        int captured = 0;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            var id = (QuadLimbId)i;
            var paw = rig.TryGetLimbBone(id, QuadLimb.SegPaw);
            if (paw == null || paw.IsDestroyed)
                continue;

            float3 pawPos = paw.GlobalPosition;
            float3 reference = QuadLimb.IsFront(id) ? _solver.RestChestPosition : _solver.RestPelvisPosition;
            float3 offset = pawPos - reference;
            _restPawOffset[i] = new float2(float3.Dot(offset, forward), float3.Dot(offset, right));

            // How extended the leg is at rest, over its straight length. The stretch trigger must sit
            // above this or a rig authored with straight legs would want to step every frame.
            _restReachRatio[i] = 0f;
            if (_solver.TryGetLimbReach(id, out float3 root, out _)
                && _solver.TryGetLimbInfo(id, out _, out float length, out _)
                && length > 1e-4f)
            {
                _restReachRatio[i] = float3.Distance(root, pawPos) / length;
            }

            _hasRestStance[i] = true;
            captured++;

            // How far this leg's ROOT sits above the spine bone the chassis drives. The stand cap has
            // to limit the root height, because that is what the leg reaches from; capping the spine
            // bone left roots 6-10 cm higher than intended and every leg near straight.
            if (_solver.TryGetLimbReach(id, out float3 rootPos, out _))
            {
                int g = QuadLimb.IsFront(id) ? 0 : 1;
                _rootAboveAnchor[g] += (rootPos.y - reference.y);
                _rootAboveAnchorCount[g]++;
            }
        }
        for (int g = 0; g < 2; g++)
        {
            _rootAboveAnchor[g] = _rootAboveAnchorCount[g] > 0 ? _rootAboveAnchor[g] / _rootAboveAnchorCount[g] : 0f;
            _rootAboveAnchorCount[g] = 0;
        }

        if (captured < QuadLimb.Count)
        {
            LumoraLogger.Warn($"QuadrupedIK: rest stance captured for {captured}/{QuadLimb.Count} limbs; "
                            + "the rest fall back to girdle-relative home points");
        }

        SymmetriseRestStance();
    }

    // A neutral stance is SYMMETRIC, whatever pose the author happened to save.
    //
    // Home was captured straight from the authored rest and this fox is saved mid-stride: the paw
    // heights in its rig dump differ by 25 cm side to side. Measured after the gait rewrite, three home
    // points sat at 99-103% of their leg's reach (one physically unreachable, looping a settle step
    // forever) while the fourth sat at a relaxed 60%. The tested systems put home at the centre of the
    // leg's reachable area, which is a property of the animal, not of the frame it was exported on.
    // Average each front/rear pair's forward offset and mirror the lateral, so both sides of a pair get
    // the same neutral point. Then keep every home inside the leg's reach: a home the leg cannot reach
    // is a straight-locked leg at best and a settle loop at worst. -xlinka
    private void SymmetriseRestStance()
    {
        Pair(QuadLimbId.FrontLeft, QuadLimbId.FrontRight);
        Pair(QuadLimbId.RearLeft, QuadLimbId.RearRight);

        void Pair(QuadLimbId left, QuadLimbId right)
        {
            int l = (int)left, r = (int)right;
            if (!_hasRestStance[l] || !_hasRestStance[r])
                return;

            float forward = (_restPawOffset[l].x + _restPawOffset[r].x) * 0.5f;
            float lateral = (MathF.Abs(_restPawOffset[l].y) + MathF.Abs(_restPawOffset[r].y)) * 0.5f;
            float reach = MathF.Min(_restReachRatio[l], _restReachRatio[r]);
            if (reach <= 1e-4f)
                reach = MathF.Max(_restReachRatio[l], _restReachRatio[r]);

            _restPawOffset[l] = new float2(forward, -lateral);
            _restPawOffset[r] = new float2(forward, lateral);
            _restReachRatio[l] = _restReachRatio[r] = reach;
        }

        LumoraLogger.Log("QuadrupedIK: rest stance symmetrised - "
            + $"front fwd={_restPawOffset[0].x:F3} lat={MathF.Abs(_restPawOffset[0].y):F3} reach={_restReachRatio[0]:F2}, "
            + $"rear fwd={_restPawOffset[2].x:F3} lat={MathF.Abs(_restPawOffset[2].y):F3} reach={_restReachRatio[2]:F2}");
    }

    private void EnsureProxies()
    {
        EnsureProxy(_chestProxy, "Chest");
        EnsureProxy(_pelvisProxy, "Pelvis");
        EnsureProxy(_frontLeftPawProxy, "FrontLeftPaw");
        EnsureProxy(_frontRightPawProxy, "FrontRightPaw");
        EnsureProxy(_rearLeftPawProxy, "RearLeftPaw");
        EnsureProxy(_rearRightPawProxy, "RearRightPaw");

        _pawProxies[(int)QuadLimbId.FrontLeft] = _frontLeftPawProxy.Target;
        _pawProxies[(int)QuadLimbId.FrontRight] = _frontRightPawProxy.Target;
        _pawProxies[(int)QuadLimbId.RearLeft] = _rearLeftPawProxy.Target;
        _pawProxies[(int)QuadLimbId.RearRight] = _rearRightPawProxy.Target;

        EnsurePawNode(QuadLimbId.FrontLeft, _frontLeftPawProxy, _frontLeftPawNode);
        EnsurePawNode(QuadLimbId.FrontRight, _frontRightPawProxy, _frontRightPawNode);
        EnsurePawNode(QuadLimbId.RearLeft, _rearLeftPawProxy, _rearLeftPawNode);
        EnsurePawNode(QuadLimbId.RearRight, _rearRightPawProxy, _rearRightPawNode);

        if (_headProxy.Target == null)
            _headProxy.Target = Slot.AddSlot("HeadProxy");
        if (_headNode.Target == null)
            _headNode.Target = _headProxy.Target.AttachComponent<AvatarPoseDriver>();
        _headNode.Target.Node.Value = BodyNode.Head;
    }

    private void EnsurePawNode(QuadLimbId id, SyncRef<Slot> proxy, SyncRef<AvatarPoseDriver> node)
    {
        if (proxy.Target == null || proxy.Target.IsDestroyed)
            return;
        if (node.Target == null || node.Target.IsDestroyed)
            node.Target = proxy.Target.AttachComponent<AvatarPoseDriver>();
        node.Target.Node.Value = PawBodyNodes[(int)id];
        _pawNodes[(int)id] = node.Target;
    }

    private void EnsureProxy(SyncRef<Slot> proxy, string name)
    {
        if (proxy.Target == null)
            proxy.Target = Slot.AddSlot($"{name}Proxy");
    }

    public void SetupTracking(UserRoot userRoot)
    {
        UserRoot.Target = userRoot;
        if (userRoot == null)
            return;

        EnsureProxies();
        SweepForeignPoseDrivers();

        var headSlot = userRoot.HeadSlot;
        var node = _headNode.Target;
        if (headSlot != null && node != null)
            EquipPoseNodeToBodySlot(headSlot, node);

        // Hands drive the front pair and feet the rear, but ONLY in VR, where those nodes are real.
        //
        // Equipping them unconditionally was actively harmful on desktop. An equipped AvatarPoseDriver
        // DRIVES its proxy slot, and the gait refuses to write a driven proxy - so the four paw proxies
        // stopped being the gait's to place and became echoes of body nodes a desktop wearer does not
        // even have. The legs had nowhere correct to stand and bunched under the animal. A desktop
        // wearer has no hand or foot tracker, so the drivers bought nothing and cost the gait its
        // targets. -xlinka
        if (Engine.Current?.InputInterface?.IsVRActive == true)
        {
            EquipPoseNodeToBodySlot(userRoot.LeftHandSlot, _frontLeftPawNode.Target);
            EquipPoseNodeToBodySlot(userRoot.RightHandSlot, _frontRightPawNode.Target);
            EquipPoseNodeToBodySlot(userRoot.LeftFootSlot, _rearLeftPawNode.Target);
            EquipPoseNodeToBodySlot(userRoot.RightFootSlot, _rearRightPawNode.Target);
        }
        else
        {
            LumoraLogger.Log("QuadrupedIK: desktop wearer, paw proxies left to the gait");
        }

        DumpWearerFrame(userRoot);
    }

    private const char NL = (char)10;
    private const char QT = (char)39;

    // Where the WEARER is, next to where the animal is.
    //
    // The rig dump says what the avatar looks like on its own. This says what it is being asked to line
    // up with: the user root, every tracked body node and whether it is actually tracking, the solver's
    // own proxies, and the two scales. Nearly every wrong pose tonight came down to two frames being
    // compared that were not in the same space, and there was no single place to read both. -xlinka
    private void DumpWearerFrame(UserRoot userRoot)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("QuadrupedIK WEARER DUMP");

        void Line(string label, Slot? slot, string extra = "")
        {
            sb.Append(NL).Append("  ").Append(label.PadRight(20));
            if (slot == null || slot.IsDestroyed)
            {
                sb.Append("(none)");
                return;
            }
            var g = slot.GlobalPosition;
            var l = slot.LocalPosition.Value;
            sb.Append(QT).Append(slot.SlotName.Value).Append(QT)
              .Append(" world=(").Append(g.x.ToString("F3")).Append(", ").Append(g.y.ToString("F3")).Append(", ").Append(g.z.ToString("F3")).Append(')')
              .Append(" local=(").Append(l.x.ToString("F3")).Append(", ").Append(l.y.ToString("F3")).Append(", ").Append(l.z.ToString("F3")).Append(')');
            if (extra.Length > 0)
                sb.Append(' ').Append(extra);
        }

        var input = Engine.Current?.InputInterface;
        string Tracked(BodyNode node)
        {
            var device = input?.GetBodyNode(node);
            return device == null ? "[no device]" : (device.IsTracking ? "[TRACKING]" : "[not tracking]");
        }

        sb.Append(NL).Append(" WEARER");
        Line("UserRoot", userRoot.Slot, "scale=" + userRoot.GlobalScale.ToString("F3"));
        Line("Head", userRoot.HeadSlot, Tracked(BodyNode.Head));
        Line("LeftHand", userRoot.LeftHandSlot, Tracked(BodyNode.LeftHand));
        Line("RightHand", userRoot.RightHandSlot, Tracked(BodyNode.RightHand));
        Line("LeftFoot", userRoot.LeftFootSlot, Tracked(BodyNode.LeftFoot));
        Line("RightFoot", userRoot.RightFootSlot, Tracked(BodyNode.RightFoot));
        sb.Append(NL).Append("  ").Append("UserHeight".PadRight(20))
          .Append((input?.UserHeight ?? 0f).ToString("F3"))
          .Append("   VR=").Append(input?.IsVRActive == true);

        sb.Append(NL).Append(" SOLVER PROXIES");
        Line("HeadProxy", _headProxy.Target);
        Line("ChestProxy", _chestProxy.Target);
        Line("PelvisProxy", _pelvisProxy.Target);
        Line("FrontLeftPaw", _frontLeftPawProxy.Target);
        Line("FrontRightPaw", _frontRightPawProxy.Target);
        Line("RearLeftPaw", _rearLeftPawProxy.Target);
        Line("RearRightPaw", _rearRightPawProxy.Target);

        sb.Append(NL).Append(" CAMERA / PLATE / COLLIDER");
        var headOut = userRoot.Slot?.GetComponentInChildren<HeadOutput>();
        Line("HeadOutput slot", headOut?.Slot);

        // The camera rides the head slot's Godot node, so the head slot IS the eye position as far as
        // anything here can see; printed beside it is what the nameplate chose to follow, which is the
        // thing that tells you whether the plate is on the animal or on the wearer.
        var plateRoot = userRoot.Slot?.GetComponentInChildren<NameplateManager>()?.Slot;
        Line("Nameplate root", plateRoot);
        var positioner = plateRoot?.GetComponentInChildren<PositionAtUser>();
        if (positioner != null && !positioner.IsDestroyed)
        {
            Line("Plate slot", positioner.Slot);
            Line("Plate anchor", positioner.Anchor.Target,
                positioner.Anchor.Target == null ? "(falling back to the wearer's head node)" : "(avatar bone)");
        }
        else
        {
            sb.Append(NL).Append("  ").Append("Plate".PadRight(20)).Append("(no positioner)");
        }

        foreach (var capsule in userRoot.Slot?.GetComponents<CapsuleCollider>()
                                ?? System.Linq.Enumerable.Empty<CapsuleCollider>())
        {
            sb.Append(NL).Append("  ").Append("Capsule".PadRight(20))
              .Append("type=").Append(capsule.Type.Value)
              .Append(" height=").Append(capsule.Height.Value.ToString("F3"))
              .Append(" radius=").Append(capsule.Radius.Value.ToString("F3"))
              .Append(" offset=").Append(capsule.Offset.Value.ToString());
        }

        sb.Append(NL).Append(" AVATAR");
        Line("QuadrupedIK slot", Slot, Slot != null && !Slot.IsDestroyed
            ? "scale=" + Slot.GlobalScale.x.ToString("F3")
            : "");
        var rig = Rig.Target;
        Line("Rig slot", rig?.Slot);
        if (rig != null && !rig.IsDestroyed)
        {
            sb.Append(NL).Append("  ").Append("BodyForward".PadRight(20)).Append(rig.BodyForward.ToString());
            Line("Head bone", rig.TryGetBone(QuadNode.Head));
            Line("Chest bone", rig.TryGetBone(QuadNode.Chest));
            Line("Pelvis bone", rig.TryGetBone(QuadNode.Pelvis));
        }

        LumoraLogger.Log(sb.ToString());
    }

    // Take the body-node sockets off the avatar's own humanoid pose drivers.
    //
    // An avatar authored for a two-legged rig ships pose drivers on its head and hand BONES, and they
    // equip into the same Head/LeftHand/RightHand sockets this solver needs. They also equip LATER, so
    // they win: our head proxy gets dequipped and the animal's head bone is pinned straight to the
    // wearer's head at human height. The spine then stretches to reach it and the avatar comes out as a
    // vertical tube with a face on top, which is exactly what it looks like.
    //
    // The same reasoning as the AvatarIK sweep in TryAttachFor, one layer down: exactly one thing may
    // drive a body node, and on four legs that is this solver. Our own five drivers (head and the four
    // paws) are the keep set; everything else on the subtree goes. AvatarForm is not a pose driver and
    // is untouched, so the root equip still lands. Run before the equip manager collects equippables,
    // so the dropped drivers are never offered to a socket in the first place. -xlinka
    private void SweepForeignPoseDrivers()
    {
        var avatarSlot = Slot;
        if (avatarSlot == null || avatarSlot.IsDestroyed)
            return;

        var keep = new System.Collections.Generic.HashSet<AvatarPoseDriver>();
        if (_headNode.Target != null) keep.Add(_headNode.Target);
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (_pawNodes[i] != null)
                keep.Add(_pawNodes[i]!);
        }

        int swept = 0;
        foreach (var driver in new System.Collections.Generic.List<AvatarPoseDriver>(
                     avatarSlot.GetComponentsInChildren<AvatarPoseDriver>()))
        {
            if (driver == null || driver.IsDestroyed || keep.Contains(driver))
                continue;
            driver.Destroy();
            swept++;
        }

        if (swept > 0)
            LumoraLogger.Log($"QuadrupedIK: swept {swept} humanoid pose driver(s) off this avatar; "
                           + "the quadruped solver owns the body nodes");

        // Transform copiers on RIG BONES are the same kind of foreign writer, and a sneakier one.
        //
        // A package built for the source platform poses its animal bones by having the humanoid rig
        // solve and the <NOIK> bones COPY from it, one CopyGlobalTransform per bone. Under this solver
        // the animal bones are written directly, and the copier then overwrites them from the humanoid
        // legs, which nothing solves any more and which sit wherever the author left them. Measured:
        // both thigh bones carried a CopyGlobalTransform, the solver left them level at 0.663/0.660,
        // and by the next gait tick the right one had been copied down to 0.391. Only copiers that
        // target a bone this rig owns are removed; anything on a non-rig slot is not ours to touch.
        // -xlinka
        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return;

        int copiers = 0;
        foreach (var node in rig.Bones.Keys)
        {
            var bone = rig.TryGetBone(node);
            if (bone == null || bone.IsDestroyed)
                continue;
            foreach (var copier in new System.Collections.Generic.List<Lumora.Core.Components.Utility.CopyGlobalTransform>(
                         bone.GetComponents<Lumora.Core.Components.Utility.CopyGlobalTransform>()))
            {
                if (copier == null || copier.IsDestroyed)
                    continue;
                copier.Destroy();
                copiers++;
            }
        }

        if (copiers > 0)
            LumoraLogger.Log($"QuadrupedIK: removed {copiers} CopyGlobalTransform(s) from rig bones; "
                           + "they were re-posing bones the solver had just placed");
    }

    private static void EquipPoseNodeToBodySlot(Slot bodySlot, AvatarPoseDriver poseNode)
    {
        if (bodySlot == null || poseNode == null)
            return;

        var socket = bodySlot.GetComponent<AvatarSocket>()
                     ?? bodySlot.GetComponent<TrackedDevicePositioner>()?.ObjectSlot.Target
                     ?? bodySlot.GetComponentInChildren<AvatarSocket>();
        if (socket == null)
            return;

        // PreEquip first, ALWAYS. AvatarSocket.Equip only assigns; it is PreEquip that dequips whoever
        // is holding the socket.
        //
        // Calling Equip on its own overwrote the incumbent in place: OnDequip never fired, so the
        // default hand placeholder's DiscardOnDequip never ran and the controller hands stayed in the
        // world, still driven, still visible, for as long as the session lasted. That is why wearing a
        // quadruped left the wearer's human hands floating in front of them while wearing another
        // humanoid did not - the humanoid path goes through AvatarEquipManager.Equip, which calls
        // PreEquip on every socket and lets the placeholder tear itself down. -xlinka
        var dequipped = new System.Collections.Generic.HashSet<IAvatarEquippable>();
        if (!socket.PreEquip(poseNode, dequipped))
            return;

        socket.Equip(poseNode);
    }

    // PER-FRAME

    public BodyNode Node => BodyNode.Root;

    public void OnPreEquip(AvatarSocket slot) { }

    public void OnEquip(AvatarSocket slot)
    {
        // Equip reparents and rescales the avatar, so every captured length is stale. Suspend, then
        // re-capture once the transforms have settled.
        Rig.Target?.LogGirdleSpan("equip-entry");
        _suspendSolve = true;
        RecaptureUntilSane(0);
        LumoraLogger.Log($"QuadrupedIK: equipped to {slot.Node.Value}");
    }

    // Take the rig's bones off whatever was driving them.
    //
    // The imported avatar arrives with its bone transforms DRIVEN - the package ships drive links and
    // the components that drove them do not come across, which is what the import means by "103
    // dangling references". Two things follow, and both were invisible.
    //
    // First, a driven field REFUSES writes: Sync.InternalSetValue bails out of BeginModification and
    // the write vanishes with no error. Every rotation this solver computed for the legs was being
    // silently discarded, which is why the limbs never moved no matter what the targets said.
    //
    // Second, whatever the orphaned links last wrote is where the bones stay, which is how a 1.33 long
    // animal measured 0.47 with its forelegs spread wider than the author left them.
    //
    // Same rule the rest of this file already follows: exactly one thing may drive a bone, and on four
    // legs that is this solver. -xlinka
    private void ReleaseBoneDrives()
    {
        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return;

        int freed = 0;
        var bones = new System.Collections.Generic.List<Slot>();
        foreach (var node in rig.Bones.Keys)
        {
            var mapped = rig.TryGetBone(node);
            if (mapped != null && !mapped.IsDestroyed)
                bones.Add(mapped);
        }

        // The bone each limb HANGS FROM is not in the map when it is a hip rather than a scapula, and it
        // ships driven like everything else. Left driven, it keeps its orphaned-link pose forever, the
        // symmetrise pass refuses to write it, and one rear root sits 20 cm below its twin with both
        // paws flat on the floor. Release it too; it is the solver's to level even if not to solve.
        // -xlinka
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            var upper = rig.TryGetLimbBone((QuadLimbId)i, QuadLimb.SegUpper);
            var girdle = upper?.Parent;
            if (girdle != null && !girdle.IsDestroyed && !bones.Contains(girdle))
                bones.Add(girdle);
        }

        foreach (var bone in bones)
        {

            if (bone.LocalPosition.IsDriven && bone.LocalPosition.ActiveLink is { } localpositionLink)
            {
                bone.LocalPosition.ReleaseLink(localpositionLink);
                freed++;
            }
            if (bone.LocalRotation.IsDriven && bone.LocalRotation.ActiveLink is { } localrotationLink)
            {
                bone.LocalRotation.ReleaseLink(localrotationLink);
                freed++;
            }
            if (bone.LocalScale.IsDriven && bone.LocalScale.ActiveLink is { } localscaleLink)
            {
                bone.LocalScale.ReleaseLink(localscaleLink);
                freed++;
            }
        }

        if (freed > 0)
            LumoraLogger.Log($"QuadrupedIK: released {freed} drive link(s) off the rig's bones; "
                           + "the solver owns them now");

        // Releasing a link stops the next write; it does not undo the ones already made. The bones are
        // sitting wherever the orphaned drivers left them, so put the authored stance back before the
        // rest capture measures anything.
        int restored = rig.RestoreAuthoredPose();
        if (restored > 0)
            LumoraLogger.Log($"QuadrupedIK: restored the authored pose on {restored} bone(s)");

        SymmetriseGirdles(rig);
    }

    private float _measuredSoleOffset;
    private readonly float[] _rootAboveAnchor = new float[2];
    private readonly int[] _rootAboveAnchorCount = new int[2];
    private double _homeGeomLogAt = -10.0;
    private bool _constraintsLogged;

    // How far the MESH hangs below the lowest paw bone.
    //
    // The solver plants bones; the floor meets pads. Every quadruped rendered tonight sat a few
    // centimetres above the grid with plantedY=0 on all four limbs, because the sole plane was the
    // lowest bone and the fur below it was never counted. Walk every skinned vertex of the worn avatar
    // in world space at rest, take the lowest, and the difference to the lowest paw bone is the offset.
    // Bind pose equals rest pose at this moment, so mesh-local through the renderer's transform is the
    // posed vertex. Capped at 15% of stand height so a stray vertex on a tail cannot sink the whole
    // animal. -xlinka
    private void MeasureSoleOffset()
    {
        _measuredSoleOffset = 0f;
        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed || !_solver.IsReady)
            return;

        float lowestBone = float.MaxValue;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            var paw = rig.TryGetLimbBone((QuadLimbId)i, QuadLimb.SegPaw)
                      ?? rig.TryGetLimbBone((QuadLimbId)i, QuadLimb.SegToe);
            if (paw != null && !paw.IsDestroyed)
                lowestBone = MathF.Min(lowestBone, paw.GlobalPosition.y);
        }
        if (lowestBone == float.MaxValue)
            return;

        float lowestVertex = float.MaxValue;
        int scanned = 0;
        foreach (var renderer in Slot.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (renderer == null || renderer.IsDestroyed || renderer.Slot == null)
                continue;
            var verts = renderer.Vertices;
            int count = verts.Count;
            if (count == 0)
                continue;
            var slot = renderer.Slot;
            for (int v = 0; v < count; v++)
            {
                float y = slot.LocalPointToGlobal(verts[v]).y;
                if (y < lowestVertex)
                    lowestVertex = y;
            }
            scanned += count;
        }
        if (scanned == 0 || lowestVertex == float.MaxValue)
            return;

        float offset = lowestBone - lowestVertex;
        float cap = MathF.Max(_solver.RestPelvisStandHeight, _solver.RestChestStandHeight) * 0.15f;
        _measuredSoleOffset = System.Math.Clamp(offset, 0f, MathF.Max(cap, 0f));
        LumoraLogger.Log($"QuadrupedIK: sole offset measured {offset:F3} from {scanned} vertices "
                       + $"(lowest bone {lowestBone:F3}, lowest vertex {lowestVertex:F3}), using {_measuredSoleOffset:F3}");
    }

    private double _postSolveLogAt;

    // The pose the solver LEAVES, as opposed to the rest pose the frame starts from. The pre-solve line
    // prints after BeginFrame restores rest, so it always reads level and hid the fact that the solve
    // itself was dropping one thigh 27 cm. Measure the same three things on the other side of Solve.
    // -xlinka
    private void LogPostSolve(QuadrupedRig rig)
    {
        double now = World.Time.TotalTime;
        if (now - _postSolveLogAt < 2.0)
            return;
        _postSolveLogAt = now;

        var pelvis = rig.TryGetBone(QuadNode.Pelvis);
        var chest = rig.TryGetBone(QuadNode.Chest);
        var thighL = rig.TryGetLimbBone(QuadLimbId.RearLeft, QuadLimb.SegUpper);
        var thighR = rig.TryGetLimbBone(QuadLimbId.RearRight, QuadLimb.SegUpper);
        var hipL = thighL?.Parent;
        var hipR = thighR?.Parent;
        if (pelvis == null || chest == null || thighL == null || thighR == null)
            return;

        static float RollDeg(Slot b)
        {
            var r = b.GlobalRotation * float3.Right;
            return MathF.Asin(System.Math.Clamp(r.y, -1f, 1f)) * (180f / MathF.PI);
        }
        static float PitchDeg(Slot b)
        {
            var f = b.GlobalRotation * float3.Backward;
            return MathF.Asin(System.Math.Clamp(f.y, -1f, 1f)) * (180f / MathF.PI);
        }

        LumoraLogger.Log($"POSTSOLVE pelvisRoll={RollDeg(pelvis):F1} pelvisPitch={PitchDeg(pelvis):F1} "
                       + $"chestRoll={RollDeg(chest):F1} chestPitch={PitchDeg(chest):F1} "
                       + $"pelvisY={pelvis.GlobalPosition.y:F3} chestY={chest.GlobalPosition.y:F3} "
                       + $"hipY={hipL?.GlobalPosition.y ?? float.NaN:F3}/{hipR?.GlobalPosition.y ?? float.NaN:F3} "
                       + $"thighY={thighL.GlobalPosition.y:F3}/{thighR.GlobalPosition.y:F3}");
    }

    private void LogLimbConstraintsOnce()
    {
        if (_constraintsLogged)
            return;
        _constraintsLogged = true;

        // Who else has a hand on the rear legs. The right thigh reads 27 cm lower at gait time than
        // the solver left it the previous frame, so some component between the two writes it. List
        // every component on each rear-limb bone and on the bones above them up to the pelvis. -xlinka
        var rig = Rig.Target;
        if (rig != null && !rig.IsDestroyed)
        {
            foreach (var id in new[] { QuadLimbId.RearLeft, QuadLimbId.RearRight })
            {
                var upper = rig.TryGetLimbBone(id, QuadLimb.SegUpper);
                for (var s = upper; s != null && !s.IsDestroyed; s = s.Parent)
                {
                    var names = new System.Text.StringBuilder();
                    foreach (var c in s.Components)
                    {
                        if (c == null || c.IsDestroyed)
                            continue;
                        if (names.Length > 0) names.Append(", ");
                        names.Append(c.GetType().Name);
                    }
                    LumoraLogger.Log($"REARWRITERS {id} '{s.SlotName.Value}' drivenP={s.LocalPosition.IsDriven} drivenR={s.LocalRotation.IsDriven} components=[{names}]");
                    if (ReferenceEquals(s, rig.TryGetBone(QuadNode.Pelvis)))
                        break;
                }
            }
        }
        for (int i = 0; i < QuadLimb.Count; i++)
            LumoraLogger.Log($"QuadrupedIK: {QuadLimb.Name((QuadLimbId)i)} {_solver.DescribeLimbConstraints((QuadLimbId)i)}");
    }

    // Swing each girdle bone so its limb root sits at the SYMMETRIC lateral position.
    //
    // Mapping the hip bones into the chain levelled the two rear roots (0.589/0.386 became 0.556/0.569)
    // but left them laterally lopsided: the authored pose has one hip rotated outward, so one thigh
    // root sat 20 cm from its home and the other 3 cm, and the same leg read a comfortable stretch on
    // one side and the limit on the other. Rather than mirror quaternions across a body plane, which
    // depends on a handedness this codebase has already tripped over twice tonight, measure where each
    // pair's roots actually are along the left-right axis, average the magnitudes, and rotate each
    // girdle bone by the shortest arc that puts its root on the averaged offset. Nothing but the
    // lateral component changes; height and fore-aft stay as authored. Done after the authored pose is
    // restored so it acts on the same base every equip. -xlinka
    private void SymmetriseGirdles(QuadrupedRig rig)
    {
        Pair(QuadLimbId.FrontLeft, QuadLimbId.FrontRight, "front");
        Pair(QuadLimbId.RearLeft, QuadLimbId.RearRight, "rear");

        void Pair(QuadLimbId left, QuadLimbId right, string label)
        {
            var rl = rig.TryGetLimbBone(left, QuadLimb.SegUpper);
            var rr = rig.TryGetLimbBone(right, QuadLimb.SegUpper);
            // The girdle bone is the mapped scapula when there is one, else whatever the limb root hangs
            // from. Rear legs have no scapula segment on purpose; their hip is the thigh's parent.
            var gl = rig.TryGetLimbBone(left, QuadLimb.SegScapula) ?? rl?.Parent;
            var gr = rig.TryGetLimbBone(right, QuadLimb.SegScapula) ?? rr?.Parent;
            if (gl == null || gr == null || rl == null || rr == null
                || gl.IsDestroyed || gr.IsDestroyed || rl.IsDestroyed || rr.IsDestroyed)
                return;
            if (gl.LocalRotation.IsDriven || gr.LocalRotation.IsDriven)
                return;

            // Left-right axis from the two girdle bones themselves, midline at their midpoint. No
            // assumed body axis: that is what produced two false readings earlier tonight.
            float3 axis = gl.GlobalPosition - gr.GlobalPosition;
            axis.y = 0f;
            if (axis.LengthSquared < 1e-6f)
                return;
            axis = axis.Normalized;
            float3 mid = (gl.GlobalPosition + gr.GlobalPosition) * 0.5f;

            float sideL = float3.Dot(rl.GlobalPosition - mid, axis);
            float sideR = float3.Dot(rr.GlobalPosition - mid, axis);
            float target = (MathF.Abs(sideL) + MathF.Abs(sideR)) * 0.5f;
            if (target < 1e-4f)
                return;

            float before = MathF.Abs(MathF.Abs(sideL) - MathF.Abs(sideR));
            Swing(gl, rl, mid, axis, target);
            Swing(gr, rr, mid, axis, -target);

            float afterL = float3.Dot(rl.GlobalPosition - mid, axis);
            float afterR = float3.Dot(rr.GlobalPosition - mid, axis);
            LumoraLogger.Log($"QuadrupedIK: {label} girdles symmetrised - lateral roots were "
                           + $"{sideL:F3}/{sideR:F3} (asymmetry {before:F3}), now {afterL:F3}/{afterR:F3}");
        }

        static void Swing(Slot girdle, Slot root, float3 mid, float3 axis, float targetSide)
        {
            float3 current = root.GlobalPosition - girdle.GlobalPosition;
            if (current.LengthSquared < 1e-8f)
                return;

            // Desired root: same point but with its lateral component moved to the target offset.
            float3 rootPos = root.GlobalPosition;
            float side = float3.Dot(rootPos - mid, axis);
            float3 desiredRoot = rootPos + axis * (targetSide - side);
            float3 desired = desiredRoot - girdle.GlobalPosition;
            if (desired.LengthSquared < 1e-8f)
                return;

            var swing = FabrikSolver.FromToRotation(current.Normalized, desired.Normalized);
            girdle.GlobalRotation = (swing * girdle.GlobalRotation).Normalized;
        }
    }

    // Keep re-capturing until the rest pose stops looking folded.
    //
    // One update was not enough. The skeleton is still settling when equip fires - the mesh hook is
    // rebuilding, the skeleton hook is rebinding, the root is being rescaled - and a capture taken in
    // the middle of that measured an animal 0.209 long with 1.4 long legs. Because the spine beam then
    // drives the body to the captured rest positions, that bad measurement became the permanent pose:
    // a squashed body with all four legs bunched under it.
    //
    // Retry rather than wait a fixed longer time, because "settled" is not a duration; it is a
    // property, and RestLooksCollapsed can test for it directly. -xlinka
    private const int MaxRestCaptureAttempts = 90;

    private void RecaptureUntilSane(int attempt)
    {
        if (IsDestroyed)
            return;

        if (attempt == 0)
            ReleaseBoneDrives();
        if (attempt == 0 || attempt == 1 || attempt == 10)
            Rig.Target?.LogGirdleSpan($"capture-attempt-{attempt}");

        EnsureSolver();
        _isInitialized = _solver.IsReady;

        if (_solver.IsReady && !_solver.RestLooksCollapsed)
        {
            _suspendSolve = false;
            MeasureSoleOffset();
            LogLimbConstraintsOnce();
            if (attempt > 0)
                LumoraLogger.Log($"QuadrupedIK: rest pose settled after {attempt} extra capture(s), "
                               + $"bodyLength={_solver.RestBodyLength:F3}");
            return;
        }

        if (attempt >= MaxRestCaptureAttempts)
        {
            _suspendSolve = false;
            LumoraLogger.Warn($"QuadrupedIK: rest pose still looks collapsed after {attempt} captures "
                            + $"(bodyLength={_solver.RestBodyLength:F3}); solving on it anyway");
            return;
        }

        Slot.RunInUpdates(1, () => RecaptureUntilSane(attempt + 1));
    }

    public void OnDequip(AvatarSocket slot)
    {
        LumoraLogger.Log($"QuadrupedIK: dequipped from {slot.Node.Value}");
    }

    // The gate here is mandatory, not defensive. Input receivers live on the engine-global input
    // interface, NOT the world's update manager, and the dispatch checks neither Enabled nor whether
    // the world may run updates - so the background-world throttle does not cover this phase at all.
    // Without the gate a disabled component in a backgrounded world still solves every frame.
    // An avatar nobody is WEARING must keep the pose its author gave it.
    //
    // This gate had no idea whether the animal was worn, which did not matter while the only site that
    // attached this solver was the equip path. It matters now: the import path attaches it too, so a
    // quadruped sitting in the world was running a full gait and spine beam with no wearer to aim at,
    // and quietly folded itself up. Measured on a real avatar - 1.326 long at populate, 0.470 by the
    // time anyone equipped it, with no scale change and the forelegs spread wider than the author left
    // them. Everything downstream then captured rest from that heap and held the animal in it.
    //
    // UserRoot.Target is set by SetupTracking, which only runs on equip, so it IS the "am I worn" test.
    // -xlinka
    private bool CanRun()
        => IKEnabled.Value
           && Enabled.Value
           && _isInitialized
           && !IsDestroyed
           && World != null
           && World.IsFocused
           && UserRoot.Target != null
           && !UserRoot.Target.IsDestroyed;

    public void BeforeInputUpdate()
    {
        if (!CanRun() || !UseProceduralGait.Value)
            return;

        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return;

        TickGait(rig);
    }

    public void AfterInputUpdate()
    {
        if (!CanRun() || _suspendSolve)
            return;

        SolveBody();
    }

    private void TickGait(QuadrupedRig rig)
    {
        float dt = World!.Time.Delta;
        if (dt <= 0f)
            return;

        if (!rig.TryGetGirdle(front: true, out float3 frontCentre, out float frontTrack, out _, out _)
            || !rig.TryGetGirdle(front: false, out float3 rearCentre, out float rearTrack, out _, out _))
        {
            if (!_gaitBlockedLogged)
            {
                _gaitBlockedLogged = true;
                LumoraLogger.Warn("QuadrupedIK: gait cannot tick, a girdle has no limb root mapped; no paw will ever plant");
            }
            return;
        }

        float3 forward = rig.BodyForward;
        float3 right = float3.Cross(float3.Up, forward);
        if (right.LengthSquared < 1e-8f)
        {
            if (!_gaitBlockedLogged)
            {
                _gaitBlockedLogged = true;
                LumoraLogger.Warn($"QuadrupedIK: gait cannot tick, body forward {forward} is vertical; no paw will ever plant");
            }
            return;
        }
        right = right.Normalized;

        float bodyLength = EffectiveBodyLength();
        float groundY = ResolveGroundY(rig);
        _lastGroundY = groundY;

        // Home points. The authored rest paw offset hung off a body-fixed reference (see the capture
        // for why not the girdle), rotated by the live body yaw, lateral scaled by StanceWidth. No lead
        // here: the gait adds velocity * StepPrediction to the LANDING point only, so home is where the
        // paw belongs when the animal is not going anywhere.
        var bodySlot = rig.Slot;
        bool restStance = _restStanceBodyLength > 0f && bodySlot != null && !bodySlot.IsDestroyed;
        float3 chestRef = restStance ? bodySlot!.LocalPointToGlobal(_restChestLocal) : frontCentre;
        float3 pelvisRef = restStance ? bodySlot!.LocalPointToGlobal(_restPelvisLocal) : rearCentre;
        // Live measured length over captured, NOT EffectiveBodyLength: an author override of BodyLength
        // scales the gait thresholds and must not stretch the authored stance with it.
        float stanceScale = restStance ? MathF.Max(_solver.RestBodyLength, 0.05f) / _restStanceBodyLength : 1f;
        float width = MathF.Max(StanceWidth.Value, 0.1f);
        float stretchLimit = MathF.Max(MaxLegStretch.Value, 0.5f);

        for (int i = 0; i < QuadLimb.Count; i++)
        {
            var id = (QuadLimbId)i;
            bool front = QuadLimb.IsFront(id);

            float3 home;
            if (restStance && _hasRestStance[i])
            {
                var offset = _restPawOffset[i];
                home = (front ? chestRef : pelvisRef)
                       + forward * (offset.x * stanceScale)
                       + right * (offset.y * stanceScale * width);
            }
            else
            {
                // No authored stance for this leg: fall back to the girdle track, as it used to be.
                float track = front ? frontTrack : rearTrack;
                float side = QuadLimb.IsLeft(id) ? -1f : 1f;
                home = (front ? frontCentre : rearCentre) + right * (side * track * 0.5f * width);
            }
            home.y = groundY;

            // Stretch test off the LIVE limb root (last frame's solve, the honest extension) against the
            // leg's straight length. Off when the rig gave no chain.
            _limbReach[i] = 0f;
            _limbRoots[i] = home;
            if (_solver.TryGetLimbReach(id, out float3 root, out _)
                && _solver.TryGetLimbInfo(id, out _, out float length, out _)
                && length > 1e-4f)
            {
                float limit = MathF.Max(stretchLimit, _restReachRatio[i] + StretchMargin);
                _limbRoots[i] = root;
                _limbReach[i] = length * limit;

                // Pull home inside the leg's reach. Home is a point on the ground and the root is a
                // height above it, so the horizontal radius the leg can cover is what is left of a
                // comfortable reach after the vertical drop. A home outside that is not a stance, it is
                // a straight-locked leg, and on this rig it was a settle loop. Comfortable = 90% of the
                // straight length, which leaves the solver room to bend. -xlinka
                float comfortable = length * 0.90f;
                float drop = root.y - home.y;
                float radius = MathF.Sqrt(MathF.Max(comfortable * comfortable - drop * drop, 0f));
                float3 flat = home - root;
                flat.y = 0f;
                float flatLen = flat.Length;

                // Per-limb geometry, throttled. Three of four homes kept clamping to the reach limit and
                // the numbers upstream could not say whether it was height, fore-aft or lateral. -xlinka
                if (_homeGeomLogAt + 3.0 < World.Time.TotalTime && i == 0)
                    _homeGeomLogAt = World.Time.TotalTime;
                if (_homeGeomLogAt == World.Time.TotalTime)
                {
                    float3 fwdOff = forward * float3.Dot(flat, forward);
                    float3 latOff = right * float3.Dot(flat, right);
                    var upperNow = rig.TryGetLimbBone(id, QuadLimb.SegUpper);
                    string upperName = upperNow?.SlotName.Value ?? "none";
                    float upperY = upperNow?.GlobalPosition.y ?? float.NaN;
                    LumoraLogger.Log($"HOMEGEOM {id}: len={length:F3} rootY={root.y:F3} upper='{upperName}' upperY={upperY:F3} drop={drop:F3} "
                                   + $"radius={radius:F3} flat={flatLen:F3} fwd={float3.Dot(flat, forward):F3} "
                                   + $"lat={float3.Dot(flat, right):F3} restOff=({_restPawOffset[i].x:F3},{_restPawOffset[i].y:F3}) "
                                   + $"clamped={(flatLen > radius)}");
                }
                if (flatLen > radius && flatLen > 1e-4f)
                {
                    float3 pulled = root + flat * (radius / flatLen);
                    home = new float3(pulled.x, groundY, pulled.z);
                }
            }
            _homePoints[i] = home;
        }

        // Velocity is measured off the body-fixed references too, so a scapula swinging does not read
        // as the animal moving.
        float3 bodyCentre = restStance ? (chestRef + pelvisRef) * 0.5f : (frontCentre + rearCentre) * 0.5f;

        var input = new QuadGaitInput
        {
            Dt = dt,
            BodyCentre = bodyCentre,
            BodyForward = forward,
            BodyLength = bodyLength,
            StrideLength = MathF.Max(StrideLength.Value, 0.05f) * bodyLength,
            DutyFactor = DutyFactor.Value,
            StepHeight = StepHeight.Value * bodyLength,
            StepThreshold = StepThreshold.Value * bodyLength,
            StepAngleThresholdDeg = StepAngleThreshold.Value,
            StepPrediction = MathF.Max(StepPrediction.Value, 0f),
            StepDuration = StepDuration.Value > 0f ? StepDuration.Value : 0.25f,
            MaxStepVelocity = MathF.Max(MaxStepVelocity.Value, 0.1f),
            GroundY = groundY,
            Grounded = true,
            Mode = GaitMode.Value,
            Home = _homePoints,
            LimbRoots = _limbRoots,
            LimbReach = _limbReach,
            Proxies = _pawProxies,
            ConformScale = MathF.Max(GroundConformScale.Value, 0.01f),
        };

        _gait.Tick(in input);
    }

    // True only when a body node has a LIVE tracked source. A desktop wearer has no hand or foot tracker,
    // so this is false for all four legs and the gait keeps them. Same test the biped path uses.
    private static bool IsNodeTracked(Lumora.Core.Input.InputInterface? input, BodyNode node)
    {
        var device = input?.GetBodyNode(node);
        return device != null && device.IsTracking;
    }

    private float ResolveGroundY(QuadrupedRig rig)
    {
        var userSlot = UserRoot.Target?.Slot;
        if (userSlot != null && !userSlot.IsDestroyed)
            return userSlot.GlobalPosition.y;

        // No user root (a standalone prop or a preview): fall back to the lowest paw so the animal at
        // least stands on its own feet.
        float lowest = float.MaxValue;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            var paw = rig.TryGetLimbBone((QuadLimbId)i, QuadLimb.SegPaw);
            if (paw != null && !paw.IsDestroyed)
                lowest = MathF.Min(lowest, paw.GlobalPosition.y);
        }
        return lowest == float.MaxValue ? Slot.GlobalPosition.y : lowest;
    }

    private void SolveBody()
    {
        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed || !_solver.IsReady)
            return;

        float3 forward = rig.BodyForward;

        // Keep the joint cone and the crouch floor in step: a cone that cannot fold as far as the crouch
        // asks makes the legs straighten and stretch instead of bending.
        _solver.MinLimbFoldScale = System.Math.Clamp(MinCrouchScale.Value, 0.05f, 1f);
        _solver.SoleOffset = SoleOffset.Value > 1e-4f ? SoleOffset.Value : _measuredSoleOffset;
        _solver.MaxStretch = MathF.Max(LimbStretch.Value, 1f);
        _solver.BendGoalWeight = BendGoalWeight.Value;
        _solver.SpineStiffness = SpineStiffness.Value;
        _solver.NeckStiffness = NeckStiffness.Value;
        _solver.MaxHeadYaw = MaxHeadYaw.Value;
        _solver.MaxBodyRoll = MaxBodyRoll.Value;
        _solver.BodyForward = forward;
        _solver.GroundY = _lastGroundY;
        _solver.TimeSeconds = World != null ? (float)World.Time.TotalTime : 0f;

        // Rest pose first, targets second. The chassis needs this frame's stand heights and the paw
        // gate needs this frame's limb roots and reaches, and both come out of the rest measurement.
        // Building targets off last frame's solved pose fed the solve its own output.
        _solver.BeginFrame();

        BuildPawTargets(rig);
        var chassis = BuildChassisTarget(rig);
        var head = BuildHeadTarget();

        LogSolveDiagnostics(in chassis, in head);

        // Body bob rides the swing: a trotting body is highest mid-flight and settles as the pair
        // lands, so the offset follows the mean arc of the legs the gait is moving. BodyBob was a
        // knob with nothing behind it before this. -xlinka
        float lift = 0f;
        int swinging = 0;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (_limbTracked[i] || !_gait.Limbs[i].Init)
                continue;
            lift += _gait.Limbs[i].StepLift;
            swinging++;
        }
        _bodyBobOffset = swinging > 0
            ? new float3(0f, BodyBob.Value * EffectiveBodyLength() * (lift / swinging), 0f)
            : float3.Zero;

        _solver.LocomotionOffset = _bodyBobOffset;
        _solver.Solve(_pawTargets, in head, in chassis);
        LogPostSolve(rig);
    }

    private void BuildPawTargets(QuadrupedRig rig)
    {
        float weight = System.Math.Clamp(PawIKWeight.Value, 0f, 1f);
        bool procedural = UseProceduralGait.Value;
        var input = Engine.Current?.InputInterface;

        for (int i = 0; i < QuadLimb.Count; i++)
        {
            ref var target = ref _pawTargets[i];

            // A LIVE tracker on this leg's node wins over the gait, per leg, IF it is where a paw could be.
            //
            // The per-leg decision is the point of the paw drivers: one global bool meant the rear pair
            // could only ever copy the front pair's procedural cycle. The reach gate is what makes the
            // decision safe. InputInterface copies controller tracking straight onto the LeftHand and
            // RightHand nodes, so in VR both hands read as tracked the moment the controllers are on,
            // and a standing wearer holds them at chest height. Pinning the front paws to that put the
            // animal on its hind legs with its forelegs stretched up at the controllers, permanently,
            // which is one of the two poses the fox was actually seen in. A hand or foot is allowed to
            // own a leg only when it is within the limb's reach of the limb root and not above it: a
            // wearer on all fours passes, a wearer standing up does not, and desktop never reaches
            // this branch because nothing tracks its hands or feet. -xlinka
            bool tracked = false;
            if (IsNodeTracked(input, PawBodyNodes[i])
                && _pawProxies[i] is { IsDestroyed: false } proxySlot
                && _solver.TryGetLimbReach((QuadLimbId)i, out float3 limbRoot, out float reach))
            {
                float3 candidate = proxySlot.GlobalPosition;
                tracked = candidate.y <= limbRoot.y
                          && float3.Distance(limbRoot, candidate) <= reach;
            }
            _limbTracked[i] = tracked;

            if (tracked && _pawProxies[i] is { IsDestroyed: false } trackedProxy)
            {
                float3 trackedPos = trackedProxy.GlobalPosition;
                trackedPos.y += _solver.PawGroundClearance[i];

                target.Valid = true;
                target.Position = trackedPos;
                target.Rotation = trackedProxy.GlobalRotation;
                target.PositionWeight = weight;
                target.RotationWeight = weight;
                target.HasBendGoal = false;
                // A tracked paw is wherever the wearer put it, including off the floor. Conforming it to
                // the ground plane would drag a lifted paw back down and undo the tracking.
                target.GroundAlign = false;
                target.HasGroundRotation = false;
                target.StepLift = 0f;
                continue;
            }

            if (!procedural)
            {
                target.Valid = false;
                continue;
            }

            ref var limb = ref _gait.Limbs[i];

            // Before the gait has planted this limb once the target is invalid and the leg holds its
            // bind pose; the proxy is not consulted, because with a pose driver on it the proxy sits
            // wherever the wearer's hand is, which is nowhere a paw should go.
            float3 position = limb.CurrentPos;

            // Lift the target by the paw BONE's authored height above the sole plane, so a rig whose
            // paw bone sits inside the foot does not bury it in the floor.
            position.y += _solver.PawGroundClearance[i];

            target.Valid = limb.Init;
            target.Position = position;
            target.Rotation = limb.CurrentRot;
            target.PositionWeight = weight;
            target.RotationWeight = weight;
            target.HasBendGoal = false;
            target.GroundAlign = true;
            target.GroundNormal = limb.GroundNormal.LengthSquared > 1e-8f ? limb.GroundNormal : float3.Up;
            target.GroundForward = rig.BodyForward;
            target.HasGroundRotation = true;
            target.GroundRotation = limb.CurrentRot;
            target.StepLift = limb.StepLift;
        }
    }

    private QuadrupedIKSolver.ChassisTarget BuildChassisTarget(QuadrupedRig rig)
    {
        var chassis = default(QuadrupedIKSolver.ChassisTarget);
        if (!UseProceduralGait.Value)
            return chassis;

        if (!rig.TryGetGirdle(front: true, out float3 frontCentre, out float frontTrack, out _, out _)
            || !rig.TryGetGirdle(front: false, out float3 rearCentre, out float rearTrack, out _, out _))
            return chassis;

        if (_solver.SpineCount < 2)
            return chassis;

        // Each girdle rides its own pair of paws, so pitch falls out of the two pairs sitting at
        // different heights rather than being computed. Contact heights, not swing heights, and the
        // root ground plane until a pair has planted.
        float frontGround = _gait.TryGirdleGroundHeight(front: true, out float fg) ? fg : _lastGroundY;
        float rearGround = _gait.TryGirdleGroundHeight(front: false, out float rg) ? rg : _lastGroundY;

        // Stand height = the girdle bone's rest height above the SOLE PLANE, measured this frame at the
        // live scale. What was here before added the girdle's height above its own limb root to the
        // paw height, which is a few centimetres: it asked for the chest ten centimetres off the floor,
        // and the spine beam obliged by pointing the whole front of the animal at the ground. The
        // pelvis position is now actually written by the solver, so this target is the body's height
        // rather than a suggestion the rotations could not honour. -xlinka
        // CROUCH. The wearer dipping has to bring the animal down with them, on all four legs.
        //
        // Stand height was the animal's fixed rest height, so the body sat at full height no matter what
        // the person did and the legs never folded at all: you could squat on the floor and the fox
        // stayed bolt upright. Scaling both girdles by the same factor is what makes all four knees fold
        // together - the paws stay planted where the gait put them, the body comes down, and the limb
        // IK has no choice but to take up the slack in each leg's own authored bend direction. The front
        // pair folds forward at the carpus and the rear pair backward at the hock because that is the
        // plane CaptureLimbRest recorded, not because anything here tells them to.
        //
        // Measured against the CALIBRATED user height rather than a standing pose captured at equip,
        // which is the same reason the biped path gives: equipping while already crouched would
        // otherwise bake the crouch in as the new standing height. -xlinka
        float crouch = ResolveCrouchScale();
        // Stand no taller than the legs can stand WITH BEND.
        //
        // The rest stand heights are read from the authored pose, and this fox is authored on straight
        // legs. Holding the body at that height puts the limb root almost a full leg length above the
        // ground, and the horizontal radius a leg can then cover collapses toward zero: three homes
        // measured at 98% of reach with the fourth at 60%, purely because that one leg is 7% longer.
        // A standing animal keeps its legs bent. Cap each girdle's stand height at a fraction of its
        // pair's shorter leg so every home is reachable with slack to spare. -xlinka
        float chestStand = MathF.Min(_solver.RestChestStandHeight, StandCapFor(QuadLimbId.FrontLeft, QuadLimbId.FrontRight)) * crouch;
        float pelvisStand = MathF.Min(_solver.RestPelvisStandHeight, StandCapFor(QuadLimbId.RearLeft, QuadLimbId.RearRight)) * crouch;

        float comp = System.Math.Clamp(HeightCompensation.Value, 0f, 1f);
        // Horizontal position comes from the LIVE girdles; only the HEIGHT comes from the rest capture.
        //
        // These were both taken straight from the rest capture, which stores WORLD positions. So every
        // frame the solver drove the body back to wherever the animal happened to be standing when rest
        // was captured, and it could never translate. The gait reads its phase from body velocity
        // (AdvancePhase), so a body that cannot move reports zero speed, the phase never advances, and
        // no leg ever enters a swing - which is exactly the gaitPhase=0.00 in every log, walking or not.
        // The animal moved across the world only because its parent did, dragging a body that was
        // fighting to stay put. -xlinka
        var chestTarget = frontCentre;
        var pelvisTarget = rearCentre;
        chestTarget.y = _solver.RestChestPosition.y;
        pelvisTarget.y = _solver.RestPelvisPosition.y;
        chestTarget.y = chestTarget.y + (frontGround + chestStand - chestTarget.y) * comp;
        pelvisTarget.y = pelvisTarget.y + (rearGround + pelvisStand - pelvisTarget.y) * comp;

        float gain = RollGain.Value;
        chassis.Valid = true;
        chassis.ChestPosition = chestTarget;
        chassis.PelvisPosition = pelvisTarget;
        chassis.FrontRoll = _gait.GirdleRoll(front: true, frontTrack) * gain;
        chassis.RearRoll = _gait.GirdleRoll(front: false, rearTrack) * gain;
        return chassis;
    }

    // How far down the wearer has crouched, as a fraction of their standing height.
    //
    // 1 is standing, and the floor stops a deep squat from driving the body through its own legs. Live
    // head Y over CALIBRATED height: UserHeight does not move when the person dips, which is the whole
    // point of using it as the denominator. Returns 1 whenever there is no calibration to compare
    // against, so desktop and an uncalibrated headset simply stand. -xlinka
    private float ResolveCrouchScale()
    {
        if (CrouchFollow.Value <= 1e-3f)
            return 1f;

        var userRoot = UserRoot.Target;
        var headSlot = userRoot?.HeadSlot;
        if (userRoot?.Slot == null || headSlot == null || headSlot.IsDestroyed)
            return 1f;

        float userHeight = Engine.Current?.InputInterface?.UserHeight ?? 0f;
        if (userHeight < 0.5f)
            return 1f;

        float headAboveRoot = headSlot.GlobalPosition.y - userRoot.Slot.GlobalPosition.y;
        float scale = userRoot.GlobalScale;
        if (scale > 1e-4f)
            headAboveRoot /= scale;

        float raw = System.Math.Clamp(headAboveRoot / userHeight, 0f, 1f);
        float floor = System.Math.Clamp(MinCrouchScale.Value, 0.05f, 1f);
        float follow = System.Math.Clamp(CrouchFollow.Value, 0f, 1f);

        // Blend toward the crouch rather than snapping to it, so the knob is a real dial and 0 keeps
        // the old fixed-height behaviour exactly.
        return 1f + (System.Math.Clamp(raw, floor, 1f) - 1f) * follow;
    }

    // Tallest a girdle may stand while both of its legs keep a comfortable bend: the shorter leg's
    // straight length times StandExtension. Falls back to "no cap" when a leg is unmapped.
    // 0.82. Measured on a real avatar: 0.85 stood stiff-legged (h=0.87 front, 0.93 rear), 0.78 sank
    // into a stalking crouch (0.80/0.85). This sits between, on the standing side. -xlinka
    private const float StandExtension = 0.82f;

    private float StandCapFor(QuadLimbId a, QuadLimbId b)
    {
        float shortest = float.MaxValue;
        if (_solver.TryGetLimbInfo(a, out _, out float la, out _) && la > 1e-4f)
            shortest = MathF.Min(shortest, la);
        if (_solver.TryGetLimbInfo(b, out _, out float lb, out _) && lb > 1e-4f)
            shortest = MathF.Min(shortest, lb);
        if (shortest == float.MaxValue)
            return float.MaxValue;

        // The cap is on the ROOT, and the chassis drives the spine bone under it: subtract the rest
        // gap so that the root, not the anchor, lands at StandExtension of the leg.
        int g = QuadLimb.IsFront(a) ? 0 : 1;
        return MathF.Max(shortest * StandExtension - _rootAboveAnchor[g], 0.05f);
    }

    private QuadrupedIKSolver.HeadTarget BuildHeadTarget()
    {
        var head = default(QuadrupedIKSolver.HeadTarget);
        var proxy = _headProxy.Target;
        if (proxy == null || proxy.IsDestroyed)
            return head;

        // CLAMP THE HEAD TO THE NECK'S REACH.
        //
        // The head proxy is the WEARER'S head, which on a standing person is about 1.6 m up. A fox's
        // head lives around 0.6 m. Handed that target raw, the neck solve reaches for a point it cannot
        // physically get to, and since it cannot, it drags the front of the animal up after it: the
        // chassis asks for a level body at 0.6 and the render comes back with the thing rearing on its
        // hind legs. The wearer's head still STEERS the head - direction is preserved exactly - it just
        // cannot ask for a distance the neck does not have. -xlinka
        var target = proxy.GlobalPosition;
        var neckRoot = _solver.IsReady ? _solver.RestChestPosition : target;
        float reach = _solver.RestNeckReach;
        if (reach > 0.01f)
        {
            float3 toTarget = target - neckRoot;
            float span = toTarget.Length;
            if (span > reach)
                target = neckRoot + toTarget / span * reach;
        }

        head.Valid = true;
        head.Position = target;
        head.Rotation = proxy.GlobalRotation;
        head.PositionWeight = 1f;
        head.RotationWeight = 1f;
        return head;
    }

    // ATTACH BRANCH
    //
    // The ONE place that decides biped or quadruped, called from all three sites that attach a solver.
    // It has to be a shared decision rather than three independent checks, because the failure mode of
    // disagreeing is two solvers on one avatar writing the same bones in the same phase - and the
    // dispatch order between them is not stable, so it does not fail consistently, it flickers.
    //
    // Returns null when this is not a quadruped, and the caller runs its existing biped path untouched.
    // Detection is deliberately conservative: an anthro biped with a tail and digitigrade legs hits
    // nearly every quadruped signal except the horizontal spine, so a false positive here would put
    // every furry avatar on all fours. -xlinka
    public static QuadrupedIK? TryAttachFor(Slot avatarSlot, SkeletonBuilder skeleton, HumanoidRig? bipedRig)
    {
        if (avatarSlot == null || avatarSlot.IsDestroyed || skeleton == null || !skeleton.IsBuilt.Value)
            return null;

        // An existing rig means someone already decided - honour it rather than re-running a heuristic
        // that could answer differently after the avatar has been posed.
        var rig = avatarSlot.GetComponent<QuadrupedRig>() ?? avatarSlot.GetComponentInChildren<QuadrupedRig>();
        bool preexisting = rig != null;

        if (rig == null)
        {
            if (!QuadrupedRig.LooksQuadruped(skeleton, bipedRig))
                return null;
            rig = avatarSlot.AttachComponent<QuadrupedRig>();
            rig.PopulateFromSkeleton(skeleton);
        }

        // Scored as a quadruped but the bones did not actually resolve into four legs. Back out
        // completely rather than leaving a half-populated rig that would make every later site take the
        // quadruped branch and find nothing to solve.
        if (!rig.IsQuadruped)
        {
            LumoraLogger.Warn(
                $"QuadrupedIK: skeleton scored quadruped but only {rig.Bones.Count} bones mapped and the four "
                + "legs did not resolve - falling back to the biped path");
            if (!preexisting)
                rig.Destroy();
            return null;
        }

        // Exactly one solver. Sweep any AvatarIK off this subtree before attaching, for the reason the
        // studio path already documents about two AvatarIKs fighting.
        foreach (var stray in new System.Collections.Generic.List<AvatarIK>(avatarSlot.GetComponentsInChildren<AvatarIK>()))
            stray.Destroy();

        var ik = avatarSlot.GetComponent<QuadrupedIK>() ?? avatarSlot.AttachComponent<QuadrupedIK>();
        ik.Skeleton.Target = skeleton;
        ik.Rig.Target = rig;

        rig.AlignAvatarFacing();

        LumoraLogger.Log(
            $"QuadrupedIK: attached ({rig.Bones.Count} bones, {rig.TailBoneCount.Value} tail); AvatarIK suppressed on this avatar");
        return ik;
    }

    public static bool IsQuadrupedAvatar(Slot? avatarSlot)
        => avatarSlot != null
           && !avatarSlot.IsDestroyed
           && (avatarSlot.GetComponent<QuadrupedRig>() ?? avatarSlot.GetComponentInChildren<QuadrupedRig>()) != null;

    // COLLIDERS

    // Coarse grab/laser hitboxes down all four legs, along the spine beam and around the head.
    //
    // Trigger, never solid, for the reason the biped path records: a worn avatar's bone capsules
    // collided with the wearer's own character controller and flung the user around. A quadruped has
    // twice as many limbs to do it with.
    public void GenerateBodyColliders()
    {
        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return;

        var head = rig.TryGetBone(QuadNode.Head);
        if (head != null && !head.IsDestroyed)
        {
            RemoveBodyCollider(head);
            float radius = 0.08f;
            var neck = rig.TryGetBone(QuadNode.Neck0) ?? rig.TryGetBone(QuadNode.Chest);
            if (neck != null && !neck.IsDestroyed)
            {
                float len = float3.Distance(head.GlobalPosition, neck.GlobalPosition);
                if (len > 1e-4f)
                    radius = System.Math.Clamp(len * 0.5f, 0.04f, 0.1f);
            }
            // Dimensions are stored in the bone's LOCAL units, because the collider is a child of the
            // bone and physics multiplies by the bone's global scale to recover the world size. That
            // scale carries the armature scale, which settles a frame after import and changes again on
            // equip.
            float scale = head.GlobalScale.x;
            if (scale < 1e-4f) scale = 1f;
            var slot = head.AddSlot("BodyCollider");
            var sphere = slot.AttachComponent<SphereCollider>();
            sphere.Type.Value = Lumora.Core.Physics.ColliderType.Trigger;
            sphere.Radius.Value = radius / scale;
        }

        for (int i = 0; i < QuadLimb.Count; i++)
        {
            var id = (QuadLimbId)i;
            AddCapsule(rig.TryGetLimbBone(id, QuadLimb.SegUpper), rig.TryGetLimbBone(id, QuadLimb.SegLower), 0.14f);
            AddCapsule(rig.TryGetLimbBone(id, QuadLimb.SegLower), rig.TryGetLimbBone(id, QuadLimb.SegCannon)
                                                                  ?? rig.TryGetLimbBone(id, QuadLimb.SegPaw), 0.12f);
            var cannon = rig.TryGetLimbBone(id, QuadLimb.SegCannon);
            if (cannon != null)
                AddCapsule(cannon, rig.TryGetLimbBone(id, QuadLimb.SegPaw), 0.11f);
        }

        // Body: pelvis to chest, which on a quadruped is the long horizontal barrel rather than an
        // upright torso.
        AddCapsule(rig.TryGetBone(QuadNode.Pelvis), rig.TryGetBone(QuadNode.Chest), 0.22f);
        AddCapsule(rig.TryGetBone(QuadNode.Chest), rig.TryGetBone(QuadNode.Neck0) ?? rig.TryGetBone(QuadNode.Head), 0.18f);
    }

    private static void AddCapsule(Slot? proximal, Slot? distal, float radiusRatio)
    {
        if (proximal == null || proximal.IsDestroyed || distal == null || distal.IsDestroyed)
            return;
        RemoveBodyCollider(proximal);

        var pg = proximal.GlobalPosition;
        var cg = distal.GlobalPosition;
        var dir = cg - pg;
        float worldLen = dir.Length;
        if (worldLen < 1e-4f)
            return;

        float scale = proximal.GlobalScale.x;
        if (scale < 1e-4f) scale = 1f;

        var slot = proximal.AddSlot("BodyCollider");
        slot.GlobalPosition = (pg + cg) * 0.5f;
        slot.GlobalRotation = RotateUpTo(dir.Normalized);

        var capsule = slot.AttachComponent<CapsuleCollider>();
        capsule.Type.Value = Lumora.Core.Physics.ColliderType.Trigger;
        capsule.Radius.Value = System.Math.Clamp(worldLen * radiusRatio, 0.02f, 0.12f) / scale;
        capsule.Height.Value = worldLen / scale;
    }

    private static void RemoveBodyCollider(Slot bone)
    {
        System.Collections.Generic.List<Slot>? toRemove = null;
        foreach (var child in bone.Children)
        {
            if (child.SlotName.Value == "BodyCollider")
                (toRemove ??= new System.Collections.Generic.List<Slot>()).Add(child);
        }
        if (toRemove != null)
        {
            foreach (var c in toRemove)
                c.Destroy();
        }
    }

    private static floatQ RotateUpTo(float3 to)
    {
        float3 from = float3.Up;
        float d = float3.Dot(from, to);
        if (d >= 0.99999f) return floatQ.Identity;
        if (d <= -0.99999f) return floatQ.AxisAngle(float3.Right, MathF.PI);
        float3 axis = float3.Cross(from, to).Normalized;
        float ang = MathF.Acos(System.Math.Clamp(d, -1f, 1f));
        return floatQ.AxisAngleRad(axis, ang);
    }

    // DIAGNOSTICS

    // One line that answers, per leg, whether it is tracked or procedural and whether the gait has
    // planted it, plus the ground plane and whether the chassis is placing the body. Emitted when that
    // state changes, rate-limited, so a run's log holds the answer without holding a frame's worth of
    // spam per frame. -xlinka
    private void LogSolveDiagnostics(in QuadrupedIKSolver.ChassisTarget chassis, in QuadrupedIKSolver.HeadTarget head)
    {
        var sig = new System.Text.StringBuilder(64);
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            sig.Append(_limbTracked[i] ? 'T' : 'P');
            sig.Append(_gait.Limbs[i].Init ? 'i' : '-');
            sig.Append(_pawTargets[i].Valid ? 'v' : '-');
        }
        sig.Append(chassis.Valid ? 'C' : 'c');
        sig.Append(head.Valid ? 'H' : 'h');
        string signature = sig.ToString();

        if (signature == _lastDiagnosticSignature)
            return;
        double now = World?.Time.TotalTime ?? 0.0;
        if (now - _lastDiagnosticTime < DiagnosticMinInterval)
            return;
        _lastDiagnosticSignature = signature;
        _lastDiagnosticTime = now;

        var line = new System.Text.StringBuilder(384);
        line.Append("QuadrupedIK: solve state groundY=").Append(_lastGroundY.ToString("F3"));
        line.Append(" chassis=").Append(chassis.Valid ? "valid" : "INVALID");
        if (chassis.Valid)
        {
            line.Append(" pelvisY=").Append(chassis.PelvisPosition.y.ToString("F3"));
            line.Append(" chestY=").Append(chassis.ChestPosition.y.ToString("F3"));
        }
        line.Append(" restStand(pelvis=").Append(_solver.RestPelvisStandHeight.ToString("F3"));
        line.Append(" chest=").Append(_solver.RestChestStandHeight.ToString("F3"));
        line.Append(") soleY=").Append(_solver.RestSoleY.ToString("F3"));
        line.Append(" head=").Append(head.Valid ? "valid" : "INVALID");
        if (head.Valid)
            line.Append(" headY=").Append(head.Position.y.ToString("F3"));
        line.Append(" spine=").Append(_solver.SpineCount).Append(" neck=").Append(_solver.NeckCount);
        line.Append(" gaitPhase=").Append(_gait.Phase.ToString("F2"));
        line.Append(" bodyLength=").Append(EffectiveBodyLength().ToString("F3"));

        // Pelvis ROLL as actually posed versus what the chassis asked for, and the two rear roots'
        // heights. Rest capture shows the rear roots symmetric to a millimetre; by solve time one sits
        // 20 cm lower. Whatever rolls the pelvis between those two moments is the bug. -xlinka
        var rigForRoll = Rig.Target;
        var pelvisBone = rigForRoll?.TryGetBone(QuadNode.Pelvis);
        if (pelvisBone != null && !pelvisBone.IsDestroyed)
        {
            var pr = pelvisBone.GlobalRotation * float3.Right;
            float pelvisRollDeg = MathF.Asin(System.Math.Clamp(pr.y, -1f, 1f)) * (180f / MathF.PI);
            line.Append(" pelvisRoll=").Append(pelvisRollDeg.ToString("F1")).Append("deg");
            line.Append(" askedRearRoll=").Append((chassis.RearRoll * 180f / MathF.PI).ToString("F1")).Append("deg");
            line.Append(" askedFrontRoll=").Append((chassis.FrontRoll * 180f / MathF.PI).ToString("F1")).Append("deg");
        }
        var thighL = rigForRoll?.TryGetLimbBone(QuadLimbId.RearLeft, QuadLimb.SegUpper);
        var thighR = rigForRoll?.TryGetLimbBone(QuadLimbId.RearRight, QuadLimb.SegUpper);
        if (thighL != null && thighR != null)
            line.Append(" rearRootY=").Append(thighL.GlobalPosition.y.ToString("F3")).Append('/').Append(thighR.GlobalPosition.y.ToString("F3"));

        // The girdles AS THE SOLVER SEES THEM, this frame. The rig dump measures them at import, before
        // equip rescales and reparents anything, and those two have never been compared. Every stance
        // point is built from these, so if they have collapsed by solve time all four legs are being
        // asked to stand in the same place. -xlinka
        var rigNow = Rig.Target;
        if (rigNow != null && !rigNow.IsDestroyed
            && rigNow.TryGetGirdle(front: true, out float3 fCentre, out float fTrack, out _, out _)
            && rigNow.TryGetGirdle(front: false, out float3 rCentre, out float rTrack, out _, out _))
        {
            float3 gap = fCentre - rCentre;
            gap.y = 0f;
            line.Append(" girdleFront=").Append(fCentre.ToString())
                .Append(" girdleRear=").Append(rCentre.ToString())
                .Append(" girdleGap=").Append(gap.Length.ToString("F3"))
                .Append(" tracks=").Append(fTrack.ToString("F3")).Append('/').Append(rTrack.ToString("F3"));
        }
        else
        {
            line.Append(" girdles=UNRESOLVED");
        }
        line.Append(" procedural=").Append(UseProceduralGait.Value);

        int stanceCaptured = 0;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (_hasRestStance[i])
                stanceCaptured++;
        }
        line.Append(" restStance=").Append(stanceCaptured).Append('/').Append(QuadLimb.Count)
            .Append(" stanceBodyLen=").Append(_restStanceBodyLength.ToString("F3"));

        for (int i = 0; i < QuadLimb.Count; i++)
        {
            var id = (QuadLimbId)i;
            bool restValid = _solver.TryGetLimbInfo(id, out int bones, out float length, out _);
            ref var limb = ref _gait.Limbs[i];
            line.Append(" | ").Append(QuadLimb.Name(id));
            line.Append(_limbTracked[i] ? "=tracked" : "=procedural");
            line.Append(" init=").Append(limb.Init);
            line.Append(" target=").Append(_pawTargets[i].Valid ? "valid" : "INVALID");
            line.Append(" rest=").Append(restValid).Append(" bones=").Append(bones);
            line.Append(" len=").Append(length.ToString("F3"));
            if (limb.Init)
                line.Append(" plantedY=").Append(limb.Planted.y.ToString("F3"));
        }

        LumoraLogger.Log(line.ToString());
    }

    public void LogDiagnosticInfo()
    {
        var rig = Rig.Target;
        if (rig == null)
        {
            LumoraLogger.Log("QuadrupedIK: no rig");
            return;
        }

        LumoraLogger.Log(
            $"QuadrupedIK: init={_isInitialized} ready={_solver.IsReady} mode={GaitMode.Value} "
            + $"phase={_gait.Phase:F3} bodyLength={BodyLength.Value:F3} forward={rig.BodyForward}");

        for (int i = 0; i < QuadLimb.Count; i++)
        {
            var id = (QuadLimbId)i;
            _solver.TryGetLimbInfo(id, out int bones, out float length, out float bendDeg);
            ref var gait = ref _gait.Limbs[i];
            LumoraLogger.Log(
                $"  {QuadLimb.Name(id),-11} bones={bones} len={length:F3} maxBend={bendDeg:F0}deg "
                + $"phase={_gait.LocalPhase(id, GaitMode.Value):F2} stepping={gait.Stepping} swingT={gait.SwingT:F2} "
                + $"lift={gait.StepLift:F2} distFromHome={gait.DistFromHome:F3} stretch={gait.Stretch:F2} "
                + $"restOffset=({_restPawOffset[i].x:F3}, {_restPawOffset[i].y:F3}) pos={gait.CurrentPos}");
        }
    }
}
