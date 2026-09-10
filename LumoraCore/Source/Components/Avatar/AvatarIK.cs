// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components.Avatar.IK;
using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

// owns an engine-side FABRIK solver, no platform IK dependency; proxies mirror tracked body nodes,
// feed the solver each frame, apply authored reference offsets, and run procedural feet when tracking is absent
[ComponentCategory("Users/Avatar")]
[DefaultUpdateOrder(-5000)]
public class AvatarIK : Component, IAvatarEquipReceiver, IInputUpdateReceiver
{
    private readonly FullBodyIKSolver _solver = new();
    private struct ReferenceOffset
    {
        public bool Valid;
        public float3 LocalPosition;
        public floatQ LocalRotation;
    }

    [Group("References")]
    public readonly SyncRef<SkeletonBuilder> Skeleton = null!;
    public readonly SyncRef<HumanoidRig> Rig = null!;
    public readonly SyncRef<UserRoot> UserRoot = null!;

    [Group("General")]
    public readonly Sync<float> HeightCompensation = null!;
    public readonly Sync<float> AvatarHeight = null!;
    public readonly Sync<float> UserResizeThreshold = null!;
    public readonly Sync<bool> UseProceduralFeet = null!;
    public readonly Sync<bool> IKEnabled = null!;
    // When OFF (default) the solver captures the avatar's AUTHORED import pose as its rest - untracked limbs idle
    // in that pose (usually a relaxed A-pose). Don't force T-pose: the authored pose is what the artist intended.
    // Flip ON to normalize the rest to a hard T-pose (fallback for a badly-authored import).
    // Safe to capture the authored pose now that FixTransforms re-bases every frame and the IK weight gate
    // stops untracked limbs collapsing. -xlinka
    public readonly Sync<bool> ForceTpose = null!;

    // IK tunables - exposed so feel can be dialed in live in-world (no rebuild).
    [Group("Solver")]
    public readonly Sync<float> SpineStiffness = null!;
    public readonly Sync<float> PelvisDamping = null!;
    [Group("Shoulders")]
    public readonly Sync<float> ShoulderReach = null!;
    // Stronger shoulder/clavicle solve: yaw/pitch clamp half-ranges (degrees, asymmetric) + the overhead roll-up gain.
    public readonly Sync<float> ShoulderYawForward = null!; // deg the clavicle swings forward/across toward a reach
    public readonly Sync<float> ShoulderYawBack = null!;    // deg it swings back
    public readonly Sync<float> ShoulderPitchUp = null!;    // deg it lifts up on a high reach
    public readonly Sync<float> ShoulderPitchDown = null!;  // deg it drops down
    public readonly Sync<float> ArmLift = null!;            // deg the shoulder rolls up at a fully-overhead reach
    [Group("Arms")]
    public readonly Sync<float> ArmStretch = null!;
    public readonly Sync<float> BendGoalWeight = null!;
    public readonly Sync<float> TwistRelax = null!;
    public readonly Sync<float> WristBendInfluence = null!; // 0..1 tracked wrist roll steering the elbow pole
    public readonly Sync<float> WristBendDeadZone = null!;  // deg of wrist roll ignored before it steers the elbow
    public readonly Sync<float> ChestFollowHands = null!;   // 0..1 chest yaw toward tracked hands
    // Elbow swivel from the hand's position in the shoulder frame (degrees; see FullBodyIKSolver.SwivelBase).
    // Offset is the per-avatar trim: a rig whose elbows read too tucked or too flared gets a few degrees here.
    [Group("Elbow Swivel")]
    public readonly Sync<float> SwivelOffset = null!;       // deg added to every computed elbow swivel, default 0
    public readonly Sync<float> SwivelBase = null!;         // deg at shoulder height, arm half-extended
    public readonly Sync<float> SwivelHeightGain = null!;   // deg per arm-reach of hand height above the shoulder
    public readonly Sync<float> SwivelReachGain = null!;    // deg per arm-reach the hand is pulled in toward the chest
    public readonly Sync<float> SwivelOutwardGain = null!;  // deg per arm-reach of cross-body travel
    [Group("Idle Motion")]
    public readonly Sync<float> IdleBreathing = null!;      // 0..2 chest breathing amount
    public readonly Sync<float> IdleSway = null!;           // 0..2 standing weight-shift sway amount
    public readonly Sync<float> WalkArmSwing = null!;       // 0..1 walking counter-swing on untracked arms
    [Group("IK Weights")]
    public readonly Sync<float> HandIKWeight = null!;   // per-effector IK weight for both hands
    public readonly Sync<float> FootIKWeight = null!;   // per-effector IK weight for both feet
    [Group("Gait")]
    public readonly Sync<float> FootStanceWidth = null!;
    public readonly Sync<float> StepThreshold = null!;
    public readonly Sync<float> StepDuration = null!;
    public readonly Sync<float> StepHeight = null!;
    public readonly Sync<float> StepPrediction = null!; // seconds of velocity lookahead per step
    public readonly Sync<float> BodyBob = null!;        // vertical hip dip while a foot is airborne
    public readonly Sync<float> MaxRootAngle = null!;   // degrees the head can turn before the torso follows it
    public readonly Sync<float> HipFollowHead = null!;  // 0..1 torso follow within MaxRootAngle
    public readonly Sync<float> CrouchBackShift = null!;// 0..1 lean the hips back as the head dips (anti knees-forward)
    public readonly Sync<float> StepAngleThreshold = null!; // degrees the body turns before a planted foot re-steps
    public readonly Sync<float> MaxStepVelocity = null!;    // m/s cap on body velocity used for step prediction
    // Horizontal body settle: the torso eases toward the centroid of the planted feet (biased to the support foot)
    // so the hips don't hover dead-centre when the feet step out to a stance. The counterpart to the vertical BodyBob.
    [Group("Body Settle")]
    public readonly Sync<float> BodySettle = null!;         // 0..1 fraction of the foot-centroid offset the hips settle toward
    public readonly Sync<float> BodySettleSmoothTime = null!; // seconds; SmoothDamp time so the settle never pops on a step
    public readonly Sync<float> SupportLegBias = null!;     // 0..1 how far the settle leans from the foot midpoint toward the support foot

    [Group("Proxies")]
    protected readonly SyncRef<Slot> _headProxy = null!;
    protected readonly SyncRef<Slot> _pelvisProxy = null!;
    protected readonly SyncRef<Slot> _chestProxy = null!;
    protected readonly SyncRef<Slot> _leftHandProxy = null!;
    protected readonly SyncRef<Slot> _rightHandProxy = null!;
    protected readonly SyncRef<Slot> _leftElbowProxy = null!;
    protected readonly SyncRef<Slot> _rightElbowProxy = null!;
    protected readonly SyncRef<Slot> _leftFootProxy = null!;
    protected readonly SyncRef<Slot> _rightFootProxy = null!;
    protected readonly SyncRef<Slot> _leftKneeProxy = null!;
    protected readonly SyncRef<Slot> _rightKneeProxy = null!;

    [Group("Pose Nodes")]
    protected readonly SyncRef<AvatarPoseDriver> _headNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _pelvisNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _chestNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _leftHandNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _rightHandNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _leftElbowNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _rightElbowNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _leftFootNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _rightFootNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _leftKneeNode = null!;
    protected readonly SyncRef<AvatarPoseDriver> _rightKneeNode = null!;

    private bool _isInitialized;
    private bool _isRegistered;
    private bool _suspendSolve;
    private ReferenceOffset _viewOffset;
    private ReferenceOffset _leftHandOffset;
    private ReferenceOffset _rightHandOffset;
    private ReferenceOffset _leftFootOffset;
    private ReferenceOffset _rightFootOffset;

    // Procedural locomotion state for untracked feet: a foot plants, then steps with a constant-rate lifted arc to
    // a new target when the body strays past a distance
    // threshold OR turns past an angle threshold - one foot at a time, the landing tracking body motion mid-step. - xlinka
    private struct FootGait
    {
        public bool Init;
        public bool Stepping;
        public float Progress;     // 0..1 step progress
        public float Speed;        // progress/sec, randomized per step
        public float3 Planted;     // planted foot world position
        public float3 StepFrom;    // step start position
        public float3 StepTo;      // step target (retargeted toward the live desired mid-step)
        public float3 PlantedFwd;  // flattened body forward the foot is planted facing
        public float3 StepFromFwd;
        public float3 StepToFwd;
        public floatQ PlantedRot;
        public floatQ StepFromRot;
        public floatQ StepToRot;
    }

    private readonly struct FootStepIntent
    {
        public readonly bool WantsStep;
        public readonly float Priority;
        public readonly float Distance;
        public readonly float Angle;

        public FootStepIntent(bool wantsStep, float priority, float distance, float angle)
        {
            WantsStep = wantsStep;
            Priority = priority;
            Distance = distance;
            Angle = angle;
        }
    }
    private FootGait _leftGait;
    private FootGait _rightGait;
    private float3 _leftFootFwd = float3.Backward;   // current world facing per foot, fed to the solver
    private float3 _rightFootFwd = float3.Backward;
    private floatQ _leftFootRot = floatQ.Identity;
    private floatQ _rightFootRot = floatQ.Identity;
    private const float StepRetarget = 10f;          // how fast a mid-step landing tracks the live target
    private const float GroundProbeUp = 0.5f;      // foot ground ray starts this far above the plane
    private const float GroundProbeDown = 1.0f;    // ...and reaches this far below it
    private const float GlideThreshold = 0.4f;     // below this groundedness the feet glide instead of step
    private const float GlideFollow = 12f;         // 1/s follow rate of gliding feet toward their stance point
    private const float GaitVelocitySmoothing = 10f;  // 1/s smoothing of the locomotion velocity fed to the gait
    private const float SwingToeDip = 0.22f;           // rad the toe dangles down at peak swing
    private const float FootRelaxMinAngleDeg = 12f;    // planted feet tolerate this much facing error before relaxing
    private const float FootRelaxSpeedDeg = 160f;      // deg/s a planted (non-support) foot relaxes toward the facing
    private float3 _smoothedGaitVel;
    // Idle-life state: stillness weight (fades in slow, out fast), walking arm-swing phase + weight.
    private float _idleWeight;
    private float _armSwingPhase;
    private float _armSwingWeight;
    private readonly List<Slot> _footRayExclude = new(1);
    private float3 _leftFootGroundNormal = float3.Up;
    private float3 _rightFootGroundNormal = float3.Up;
    private float3 _prevHeadXZ;          // for step-prediction velocity
    private bool _hasPrevHead;
    private float _leftStepLift;         // 0..1 current step arc height per foot
    private float _rightStepLift;
    private float3 _locomotionOffset;    // hips bob (Y) + horizontal settle (XZ) fed to the solver
    // Horizontal body-settle state. _bodySettleXZ is the current smoothed offset; _bodySettleVel is the SmoothDamp
    // velocity carried between frames so the settle eases (no pop) across step transitions. -xlinka
    private float3 _bodySettleXZ;
    private float3 _bodySettleVel;
    private bool _leftIsSupport = true;  // last-elected support foot (the more-planted one), for the settle bias
    // Last gait-planted foot world position + whether procedural locomotion currently owns that foot. The
    // foot proxy is ALSO driven by its AvatarPoseDriver, so rather than rely on update-order to land last, we
    // re-assert the plant onto the proxy in SolveBody (AfterInputUpdate) right before the solver reads it. -xlinka
    private float3 _leftFootPlant;
    private float3 _rightFootPlant;
    private bool _hasLeftPlant;
    private bool _hasRightPlant;
    private float _proceduralGroundY;
    private bool _hasProceduralGroundY;
    private float _lastRescaleHeight = -1f;  // calibrated height the avatar was last scaled to
    private float _lastRescaleCompensation = -1f;
    private float _lastColliderScale = -1f;  // bone global scale the body colliders were last fitted to

    public bool IsEquipped => _headNode?.Target?.IsEquipped ?? false;
    public AvatarPoseDriver? HeadNode => _headNode?.Target;
    public AvatarPoseDriver? PelvisNode => _pelvisNode?.Target;
    public AvatarPoseDriver? LeftHandNode => _leftHandNode?.Target;
    public AvatarPoseDriver? RightHandNode => _rightHandNode?.Target;
    public AvatarPoseDriver? LeftFootNode => _leftFootNode?.Target;
    public AvatarPoseDriver? RightFootNode => _rightFootNode?.Target;
    public Slot? HeadProxy => _headProxy?.Target;
    public Slot? LeftHandProxy => _leftHandProxy?.Target;
    public Slot? RightHandProxy => _rightHandProxy?.Target;

    public bool TryGetHeadView(out float3 position, out float3 forward)
    {
        var target = ResolveHeadTarget(UserRoot.Target?.Slot, Engine.Current?.InputInterface);
        if (!target.Valid)
        {
            position = default;
            forward = default;
            return false;
        }

        position = target.Position;
        forward = target.Rotation * float3.Backward;
        forward = forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;
        return true;
    }

    // Decide once per equip whether the rig's geometric forward (HumanoidRig.GuessForwardAxis, which trusts the
    // left/right arm bone LABELS) points backward. A rig whose left/right arm bones sit on the swapped physical
    // side reads forward as backward, so the torso renders backward and drags the arms with it. Compare the
    // geometric forward against the LIVE head/view forward - a real tracker cue, NOT an authored bone rotation -
    // and flip only when they clearly oppose. No-op for correctly-built rigs (forward already agrees with the
    // view); only the local user (who has a view) ever decides, so remote avatars keep pure geometry. -xlinka
    private void CalibrateForwardSign(HumanoidRig rig)
    {
        if (rig == _forwardCalibratedRig)
            return;
        if (!TryGetHeadView(out _, out float3 viewFwd))
            return; // no calibrated view yet (remote user / before tracking) - retry next frame
        var geom = rig.GuessForwardAxis();
        if (!geom.HasValue)
            return;
        float3 f = geom.Value; f.y = 0f;
        float3 v = viewFwd; v.y = 0f;
        if (f.LengthSquared < 1e-6f || v.LengthSquared < 1e-6f)
            return;
        float dot = float3.Dot(f.Normalized, v.Normalized);
        // Looking ~sideways at the avatar gives an ambiguous sign; wait for a decisive frame before committing.
        if (dot > 0.25f)
            _forwardSignFlip = false;
        else if (dot < -0.25f)
            _forwardSignFlip = true;
        else
            return;
        _forwardCalibratedRig = rig;
        LumoraLogger.Log($"[IK-FWD-CAL] forwardSignFlip={_forwardSignFlip} dot(geomFwd,view)={dot:F3} geomFwd={f.Normalized} viewFwd={v.Normalized}");
    }

    public BodyNode Node => BodyNode.Root;

    public void OnPreEquip(AvatarSocket slot) { }

    public void OnEquip(AvatarSocket slot)
    {
        // Equip reparents and rescales the avatar, so the solver's captured
        // rest pose - world-space bone lengths and rest directions - is stale.
        // Arms solved against pre-equip lengths can't reach the real targets
        // (hands lag/never lift). Suspend solving, then re-capture once the
        // transforms have settled. -xlinka
        _suspendSolve = true;
        Slot.RunInUpdates(1, () =>
        {
            if (IsDestroyed) return;
            EnsureSolver();
            RecomputeReferenceOffsets();
            _suspendSolve = false;
        });
        LumoraLogger.Log($"AvatarIK: Equipped to {slot.Node.Value}");
    }

    public void OnDequip(AvatarSocket slot)
    {
        LumoraLogger.Log($"AvatarIK: Dequipped from {slot.Node.Value}");
    }

    private readonly UserRootRegistrationTracker _userRootReg;

    public AvatarIK()
    {
        _userRootReg = new UserRootRegistrationTracker(this);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        _userRootReg.Attach();
    }

    public override void OnInit()
    {
        base.OnInit();
        HeightCompensation.Value = 0.95f;
        AvatarHeight.Value = 1.7f;
        UserResizeThreshold.Value = 0.2f;
        UseProceduralFeet.Value = true;
        IKEnabled.Value = true;
        ForceTpose.Value = false;

        SpineStiffness.Value = 0.2f;
        PelvisDamping.Value = 0.65f;
        ShoulderReach.Value = 0.45f;
        ShoulderYawForward.Value = 40f;
        ShoulderYawBack.Value = 17f;
        ShoulderPitchUp.Value = 46f;
        ShoulderPitchDown.Value = 14f;
        ArmLift.Value = 26f;
        ArmStretch.Value = 1.08f;
        // 1: a bend goal is an elbow/knee tracker and decides the swivel. 0.5 only ever gave a tracker half a vote.
        BendGoalWeight.Value = 1f;
        TwistRelax.Value = 0.5f;
        WristBendInfluence.Value = 0.5f;
        WristBendDeadZone.Value = 40f;
        ChestFollowHands.Value = 0.2f;
        SwivelOffset.Value = 0f;
        SwivelBase.Value = 35f;
        SwivelHeightGain.Value = 75f;
        SwivelReachGain.Value = 60f;
        SwivelOutwardGain.Value = 50f;
        IdleBreathing.Value = 1f;
        // Off. The standing weight-shift read as a constant wiggle on a worn avatar rather than as life,
        // and the hand drift that went with it is gone entirely. Still here to turn back up per avatar.
        IdleSway.Value = 0f;
        WalkArmSwing.Value = 0.7f;
        HandIKWeight.Value = 1f;
        FootIKWeight.Value = 1f;
        FootStanceWidth.Value = 0.10f;
        StepThreshold.Value = 0.16f;
        StepDuration.Value = 0.28f;
        StepHeight.Value = 0.10f;
        StepPrediction.Value = 0.14f;
        BodyBob.Value = 0.035f;
        MaxRootAngle.Value = 25f;
        HipFollowHead.Value = 0f;   // OFF on desktop: the user root already yaws the body to the look. VR torso-follow > 0.
        CrouchBackShift.Value = 0.35f;
        StepAngleThreshold.Value = 35f;
        MaxStepVelocity.Value = 2.0f;
        BodySettle.Value = 0.45f;
        BodySettleSmoothTime.Value = 0.14f;
        SupportLegBias.Value = 0.55f;
    }

    public override void OnStart()
    {
        base.OnStart();

        var input = Engine.Current?.InputInterface;
        if (input != null)
        {
            input.RegisterInputEventReceiver(this);
            _isRegistered = true;
        }

        if (Skeleton.Target == null)
            Skeleton.Target = Slot.GetComponent<SkeletonBuilder>() ?? Slot.GetComponentInChildren<SkeletonBuilder>();

        if (Rig.Target == null)
            Rig.Target = Slot.GetComponent<HumanoidRig>() ?? Slot.GetComponentInChildren<HumanoidRig>();

        if (UserRoot.Target == null)
        {
            var userRoot = Slot.GetComponent<UserRoot>();
            if (userRoot == null && Slot.Parent != null)
                userRoot = Slot.Parent.GetComponent<UserRoot>();
            if (userRoot != null)
                UserRoot.Target = userRoot;
        }

        _isInitialized = Skeleton.Target != null && Skeleton.Target.IsBuilt.Value;
        RecomputeReferenceOffsets();

        if (_isInitialized)
            LumoraLogger.Log($"AvatarIK: Started on '{Slot.SlotName.Value}' with skeleton");
        else
            LumoraLogger.Warn($"AvatarIK: No valid skeleton found on '{Slot.SlotName.Value}'");
    }

    public override void OnDestroy()
    {
        _userRootReg.Detach();

        if (_isRegistered)
        {
            Engine.Current?.InputInterface?.UnregisterInputEventReceiver(this);
            _isRegistered = false;
        }

        _isInitialized = false;
        base.OnDestroy();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (!_isInitialized && Skeleton.Target != null && Skeleton.Target.IsBuilt.Value)
        {
            _isInitialized = true;
            EnsureSolver();
            RecomputeReferenceOffsets();
            LumoraLogger.Log("AvatarIK: Late-start with skeleton");
        }

        if (_isInitialized)
        {
            MaybeRescaleAvatar();
            MaybeRefitColliders();
        }
    }

    // Decided once per equip: does the rig's geometric forward point backward (swapped left/right arm labels)?
    // See CalibrateForwardSign. _forwardCalibratedRig tracks which rig the decision was made for so a re-equip
    // recalibrates. -xlinka
    private bool _forwardSignFlip;
    private HumanoidRig? _forwardCalibratedRig;

    // Re-fit the body colliders whenever the bone scale actually changes. The FBX armature scale lands on the
    // bones a frame after import and changes again on equip-rescale; the colliders are bone children, so a stale
    // fit would balloon or shrink the grab/point hitboxes. Cheap - only regenerates on a real scale change. -xlinka
    private void MaybeRefitColliders()
    {
        var rig = Rig.Target;
        if (rig == null)
            return;

        // ONLY refit the UNWORN avatar (in the world, owned by the local user). Once it's grabbed or equipped it
        // sits under a user root and is host-owned - destroying/modifying its collider slots from here throws every
        // frame ("destroying a held object requires ownership" + "Cannot modify disposed element: Height", ~20k log
        // spam). The equip path sizes the worn colliders itself. ActiveUserRoot alone wasn't catching the worn case
        // (it can read null right after equip), so also bail when our pose nodes are equipped. - xlinka
        if (Slot == null || Slot.IsDestroyed || Slot.ActiveUserRoot != null || IsEquipped)
            return;
        var refBone = rig.TryGetBone(BodyNode.Hips) ?? rig.TryGetBone(BodyNode.Spine);
        if (refBone == null || refBone.IsDestroyed)
            return;
        float scale = refBone.GlobalScale.x;
        if (scale < 1e-4f)
            return;
        if (_lastColliderScale < 0f ||
            System.Math.Abs(scale - _lastColliderScale) / System.Math.Max(scale, 1e-4f) > 0.02f)
        {
            GenerateBodyColliders();
            _lastColliderScale = scale;
        }
    }

    // Scale the avatar so its eye height matches the user's calibrated standing
    // height, so a tall/short imported model lines up with where the head is
    // actually tracked (feet reach the floor, hands reach naturally). Only the
    // owner writes; the scale replicates to everyone else. Driven off the
    // calibrated UserHeight (not the live head Y) so crouching never shrinks it.
    private void MaybeRescaleAvatar()
    {
        if (!IsUnderLocalUser)
            return;

        var input = Engine.Current?.InputInterface;
        if (input == null)
            return;

        float userHeight = input.UserHeight;
        if (userHeight < 0.8f)
            return;

        float comp = System.Math.Clamp(HeightCompensation.Value, 0.25f, 2f);
        float avatarHeight = AvatarHeight.Value;
        if (avatarHeight <= 0.01f)
            return;

        var userSpace = UserRoot.Target?.Slot ?? Slot.ActiveUserRoot?.Slot;
        float currentHeight = userSpace != null && !userSpace.IsDestroyed
            ? Slot.LocalScaleToSpace(avatarHeight, userSpace)
            : Slot.LocalScaleToGlobal(avatarHeight);
        if (currentHeight <= 0.01f)
            return;

        if (_lastRescaleHeight > 0f
            && System.Math.Abs(userHeight - _lastRescaleHeight) < 0.01f
            && System.Math.Abs(comp - _lastRescaleCompensation) < 0.001f)
            return;

        float scaleFactor = userHeight / currentHeight * comp;
        if (float.IsNaN(scaleFactor) || float.IsInfinity(scaleFactor) || scaleFactor <= 0f)
            return;
        var s = Slot.LocalScale.Value * scaleFactor;
        _lastRescaleHeight = userHeight;
        _lastRescaleCompensation = comp;

        var root = Slot.GetComponent<AvatarForm>();
        if (root != null)
            root.Scale.Value = s;          // canonical scale, re-applied on (re)equip
        Slot.LocalScale.Value = s;         // take effect now

        // Bone lengths are read fresh from the default local pose every solve, so a scale change does not need a
        // solver re-capture. Re-capturing here would be dangerous because this callback can run after a solved frame.
        // Only reference offsets are recomputed; height measurement reads setup references, not the solved pose. -xlinka
        _suspendSolve = true;
        Slot.RunInUpdates(1, () =>
        {
            if (IsDestroyed) return;
            if (!_solver.IsReady)
                EnsureSolver();
            RecomputeReferenceOffsets();
            _suspendSolve = false;
        });
    }

    public void BeforeInputUpdate()
    {
        if (!IKEnabled.Value || !_isInitialized)
            return;

        // Run every frame so the gait clock stays continuous; per-foot tracked checks live inside.
        if (UseProceduralFeet.Value)
            UpdateProceduralFeet();
    }

    public void AfterInputUpdate()
    {
        if (!IKEnabled.Value || !_isInitialized || _suspendSolve)
            return;

        SolveBody();
    }

    private void EnsureSolver()
    {
        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return;

        // The solver captures the current bone pose as rest. ForceTpose is the opt-in fallback for imports whose
        // authored pose is too cursed to use as an idle basis. - xlinka
        if (ForceTpose.Value)
            rig.MakeTPose();
        _solver.Initialize(rig);
    }

    // Read final proxy poses and run the engine FABRIK solve onto the rig bones.
    private void SolveBody()
    {
        if (!_solver.IsReady)
        {
            EnsureSolver();
            if (!_solver.IsReady)
                return;
        }

        // Push live tunables into the solver.
        _solver.SpineStiffness = SpineStiffness.Value;
        _solver.PelvisDamp = PelvisDamping.Value;
        _solver.ShoulderWeight = ShoulderReach.Value;
        const float deg2rad = MathF.PI / 180f;
        _solver.ShoulderYawForward = MathF.Max(ShoulderYawForward.Value, 0f) * deg2rad;
        _solver.ShoulderYawBack = MathF.Max(ShoulderYawBack.Value, 0f) * deg2rad;
        _solver.ShoulderPitchUp = MathF.Max(ShoulderPitchUp.Value, 0f) * deg2rad;
        _solver.ShoulderPitchDown = MathF.Max(ShoulderPitchDown.Value, 0f) * deg2rad;
        _solver.ArmLift = ShoulderReach.Value > 0f ? MathF.Max(ArmLift.Value, 0f) * deg2rad : 0f;
        _solver.MaxStretch = ArmStretch.Value;
        _solver.BendGoalWeight = BendGoalWeight.Value;
        _solver.TwistRelax = TwistRelax.Value;
        _solver.WristBendInfluence = System.Math.Clamp(WristBendInfluence.Value, 0f, 1f);
        _solver.WristBendDeadZone = System.Math.Clamp(WristBendDeadZone.Value, 0f, 120f) * deg2rad;
        _solver.ChestFollowHands = System.Math.Clamp(ChestFollowHands.Value, 0f, 1f);
        _solver.SwivelOffset = System.Math.Clamp(SwivelOffset.Value, -90f, 90f) * deg2rad;
        _solver.SwivelBase = SwivelBase.Value * deg2rad;
        _solver.SwivelHeightGain = SwivelHeightGain.Value * deg2rad;
        _solver.SwivelReachGain = SwivelReachGain.Value * deg2rad;
        _solver.SwivelOutwardGain = SwivelOutwardGain.Value * deg2rad;
        _solver.TimeSeconds = (float)(World?.Time.TotalTime ?? 0.0);
        _solver.IdleWeight = _idleWeight;
        _solver.BreathingWeight = System.Math.Clamp(IdleBreathing.Value, 0f, 2f);
        _solver.ArmSwingPhase = _armSwingPhase;
        _solver.ArmSwingWeight = _armSwingWeight * System.Math.Clamp(WalkArmSwing.Value, 0f, 1f);
        _solver.LocomotionOffset = _locomotionOffset;
        _solver.MaxRootAngle = MaxRootAngle.Value * (MathF.PI / 180f);
        _solver.HipFollowFraction = System.Math.Clamp(HipFollowHead.Value, 0f, 1f);
        _solver.MoveBodyBackWhenCrouching = System.Math.Clamp(CrouchBackShift.Value, 0f, 2f);

        var calRig = Rig.Target;
        if (calRig != null && !calRig.IsDestroyed)
            CalibrateForwardSign(calRig);
        _solver.ForwardSignFlip = _forwardSignFlip;

        var input = Engine.Current?.InputInterface;
        var userSlot = UserRoot.Target?.Slot;
        float3 locomotionBodyForward = ComputeBodyForward(userSlot, _headProxy.Target);
        var head = ResolveHeadTarget(userSlot, input);
        float3 visualBodyForward = UpdateBodyForwardReference(in head, locomotionBodyForward);

        // Pelvis target only counts when a waist/hip tracker is actually equipped; otherwise the
        // solver estimates the hips from the head (no calibration offset on the pelvis).
        var pelvis = (_pelvisNode.Target?.IsEquippedAndActive ?? false)
            ? ProxyTarget(_pelvisProxy.Target, default)
            : default;

        // Only pull a limb to its IK target when that body node is actually driven. On desktop an idle hand
        // proxy sits at a setup pose nothing is moving, so full weight would fold the avatar toward a stale
        // controller marker. - xlinka
        float handW = System.Math.Clamp(HandIKWeight.Value, 0f, 1f);
        // The proxy is only a valid source while OUR pose node owns the socket.
        //
        // An avatar that ships its own hand objects equips them into the hand sockets and displaces
        // this solver's AvatarPoseDriver, so the proxy never moves again and sits at the avatar root.
        // ProxyTarget still called that "valid" because the slot existed. In VR that meant the tracked
        // controllers moved the SOCKET every frame while the IK aimed the arms at a dead proxy on the
        // floor - hands did not follow the person at all. The socket's filtered pose is the same
        // controller pose with the grip offset applied, which is exactly what the pose driver would have
        // written into the proxy had it owned the socket. Read that instead whenever we do not. This
        // generalises the hold-only patch from earlier: it applies to tracked hands, held tools, and
        // everything else the same way. -xlinka
        var leftHandT = HandTargetFor(BodyNode.LeftHand, _leftHandProxy.Target, _leftHandNode.Target, _leftHandOffset);
        var rightHandT = HandTargetFor(BodyNode.RightHand, _rightHandProxy.Target, _rightHandNode.Target, _rightHandOffset);
        bool leftHandTracked = IsNodeTracked(input, BodyNode.LeftHand);
        bool rightHandTracked = IsNodeTracked(input, BodyNode.RightHand);
        // A desktop hand holding a tool IS driven even though no hardware tracks it: HandTool moves the
        // controller body node to a hold pose in front of the head, so the proxy hanging off it carries a
        // real target and the arm has somewhere to go. Weighting by the hand tool's own 0..1 blend rather
        // than a bool means the arm rises and falls WITH the hand instead of snapping to full IK the
        // instant a tool is equipped. Zero in VR, where the tracker gate above already answers. -xlinka
        RefreshHandTools();
        float leftHoldW = HoldWeightOf(_leftHandTool);
        float rightHoldW = HoldWeightOf(_rightHandTool);
        // A hold may only weight a hand whose PROXY IS ACTUALLY BEING DRIVEN.
        //
        // The hand target is the proxy slot, moved by an AvatarPoseDriver equipped into the hand socket.
        // When that node is not equipped - the avatar's own hand object can take the socket instead - the
        // proxy never moves and sits at the avatar root, on the FLOOR. That was harmless while an
        // untracked desktop hand carried PositionWeight 0. The tool hold raised it to 1, so the solver
        // began faithfully hauling the arm toward a target at Y=0: measured targetW=(0.805, 0.001, -0.936)
        // against a hand bone at Y=0.758, miss=0.762. The arm was not folding wrong, it was reaching for
        // the ground. Never weight a target nothing is writing. -xlinka
        // When our own pose node lost the socket, take the target from the SOCKET SLOT itself.
        //
        // An avatar that ships its own hand objects equips them into the hand sockets, which displaces
        // this solver's pose node - Chiki equips four objects, Root/Head/LeftHand/RightHand, and takes
        // both hands. The proxy then never moves and reports the avatar root, on the floor. Reading the
        // socket slot directly is not a workaround: the socket is a child of the controller body node
        // that HandTool writes the hold pose onto, so it is the same pose one link earlier, and it is
        // live whoever owns the socket. -xlinka
        // The hold no longer needs its own socket fallback: HandTargetFor above already sources from
        // the socket whenever our pose node does not own it, tracked or held alike.

        leftHandT.PositionWeight = leftHandT.RotationWeight = leftHandTracked ? handW : handW * leftHoldW;
        rightHandT.PositionWeight = rightHandT.RotationWeight = rightHandTracked ? handW : handW * rightHoldW;
        // Hand the far end of the chain back to the tool that asked for it, so its TOOLHOLD trace can say
        // whether the arm was actually weighted or the request died somewhere in between. -xlinka
        _leftHandTool?.ReportIKHandWeight(leftHandT.PositionWeight);
        _rightHandTool?.ReportIKHandWeight(rightHandT.PositionWeight);

        // TARGET vs BONE, both in world space, at the same instant.
        //
        // Three separate bugs have been indistinguishable from a screenshot: the target being wrong, the
        // bone failing to reach a correct target, and both being right while the avatar's scale makes it
        // look wrong. Three edits in a row failed to move this arm because I kept assuming the target was
        // correct and the solver was at fault without ever comparing them. Print both. -xlinka
        // BOTH hands, every time either is holding. Which arm actually moves is the open question, and a
        // one-sided log cannot answer it: if the right-hand tool drives a bone sitting on the avatar's
        // left, that is a mirrored rig and only a side-by-side comparison shows it. -xlinka
        // Equipped elbow trackers steer the arm bend. A desktop hand holding a tool gets NO synthetic goal any
        // more: the solver derives the elbow swivel from the hand's position in the shoulder frame, which is the
        // rule a held tool needed (elbow down and a little out for a hand at chest height). The old stopgap here
        // built a goal below-and-outside the shoulder and handed it to a 0.5-weight position lerp, so it only ever
        // moved the elbow halfway and was logged BEFORE it ran, which is why every ARMDIAG line said
        // bendGoal=False whether or not it fired. -xlinka
        ApplyBendGoal(ref leftHandT, _leftElbowProxy, _leftElbowNode);
        ApplyBendGoal(ref rightHandT, _rightElbowProxy, _rightElbowNode);

        // Logged AFTER the goals are attached so bendGoal= is what the solver will actually see. The swivel
        // fields are from the PREVIOUS solve (the bone positions in the same line are too). -xlinka
        float holdAny = MathF.Max(leftHoldW, rightHoldW);
        LogArmDiagnostic("Left", in leftHandT, BodyNode.LeftHand, holdAny, leftHoldW, _solver.LeftArmPole);
        LogArmDiagnostic("Right", in rightHandT, BodyNode.RightHand, holdAny, rightHoldW, _solver.RightArmPole);
        LogHandSeparation(holdAny);

        // Feet count as DRIVEN when a tracker is live OR procedural locomotion is on - the gait writes a
        // valid ground-planted target into the foot proxy every frame, so that target should be used.
        // Only TRULY undriven feet drop to rest weight 0; otherwise the avatar floats (the plants get
        // computed in UpdateProceduralFeet then thrown away by a hardware-only gate). The hands' equivalent
        // driver is the desktop tool hold handled just above. -xlinka
        float footW = System.Math.Clamp(FootIKWeight.Value, 0f, 1f);
        bool leftFootTrackedInput = IsNodeTracked(input, BodyNode.LeftFoot);
        bool rightFootTrackedInput = IsNodeTracked(input, BodyNode.RightFoot);
        bool leftFootDriven = leftFootTrackedInput || UseProceduralFeet.Value;
        bool rightFootDriven = rightFootTrackedInput || UseProceduralFeet.Value;
        // Re-assert the gait plant onto the foot proxy HERE (AfterInputUpdate), immediately before reading
        // it - so the procedural target wins over the foot's AvatarPoseDriver drive (which ran earlier in
        // BeforeInputUpdate) without depending on receiver order. -xlinka
        if (UseProceduralFeet.Value)
        {
            if (_hasLeftPlant && _leftFootProxy.Target != null && !_leftFootProxy.Target.IsDestroyed)
                WriteFoot(_leftFootProxy.Target, _leftFootPlant);
            if (_hasRightPlant && _rightFootProxy.Target != null && !_rightFootProxy.Target.IsDestroyed)
                WriteFoot(_rightFootProxy.Target, _rightFootPlant);
        }
        // Tracked feet convert the tracker pose through the calibration offset; PROCEDURAL feet take the
        // gait plant raw - it already places the foot BONE (sole clearance included), and the offset math
        // needs a trustworthy proxy ROTATION, which the position-only gait writes don't maintain. -xlinka
        var leftFootT = leftFootTrackedInput
            ? ProxyTarget(_leftFootProxy.Target, _leftFootOffset)
            : ProxyTarget(_leftFootProxy.Target, default);
        var rightFootT = rightFootTrackedInput
            ? ProxyTarget(_rightFootProxy.Target, _rightFootOffset)
            : ProxyTarget(_rightFootProxy.Target, default);
        leftFootT.PositionWeight = leftFootT.RotationWeight = leftFootDriven ? footW : 0f;
        rightFootT.PositionWeight = rightFootT.RotationWeight = rightFootDriven ? footW : 0f;
        // Equipped knee trackers steer the leg bend.
        ApplyBendGoal(ref leftFootT, _leftKneeProxy, _leftKneeNode);
        ApplyBendGoal(ref rightFootT, _rightKneeProxy, _rightKneeNode);

        // Untracked feet are ground-aligned (rest pose tilted to the surface normal the gait probed)
        // and carry the step lift so the solver can curl the toe; tracked feet keep tracker rotation.
        if (UseProceduralFeet.Value)
        {
            if (!(_leftFootNode.Target?.IsEquippedAndActive ?? false))
            {
                leftFootT.GroundAlign = true;
                leftFootT.GroundNormal = _leftFootGroundNormal;
                leftFootT.GroundForward = _leftFootFwd;
                leftFootT.HasGroundRotation = true;
                leftFootT.GroundRotation = _leftFootRot;
                leftFootT.StepLift = _leftStepLift;
            }
            if (!(_rightFootNode.Target?.IsEquippedAndActive ?? false))
            {
                rightFootT.GroundAlign = true;
                rightFootT.GroundNormal = _rightFootGroundNormal;
                rightFootT.GroundForward = _rightFootFwd;
                rightFootT.HasGroundRotation = true;
                rightFootT.GroundRotation = _rightFootRot;
                rightFootT.StepLift = _rightStepLift;
            }
        }

        // Floor-pin (ForceRootHeight equivalent): when neither foot is tracked, give the solver the
        // user-root ground plane so it can keep the pelvis within leg reach of the floor - the body can't
        // float above or sink below the ground as the head moves. Tracked feet rule their own height
        // (crouch/jump), so the pin is off then. -xlinka
        bool anyFootTracked = (_leftFootNode.Target?.IsEquippedAndActive ?? false)
                           || (_rightFootNode.Target?.IsEquippedAndActive ?? false);
        _solver.BodyForward = visualBodyForward;
        _solver.RootAnchor = userSlot != null && !userSlot.IsDestroyed ? userSlot.GlobalPosition : (float3?)null;
        _solver.GroundY = !anyFootTracked
            ? (_hasProceduralGroundY
                ? _proceduralGroundY
                : userSlot != null && !userSlot.IsDestroyed ? userSlot.GlobalPosition.y : (float?)null)
            : null;

        LogPoseDiagnostics(head, leftHandT, rightHandT, leftFootT, rightFootT);

        _solver.Solve(head, pelvis, leftHandT, rightHandT, leftFootT, rightFootT);
    }

    private bool _poseDiagLogged;

    // One block, once, the first time this avatar is actually solved.
    //
    // A body that comes out crumpled is a body whose targets are closer to it than its own limbs are
    // long: the chain has nowhere to go but fold. That is invisible from a screenshot and obvious from
    // two numbers, so print them - the reach of each chain against the distance its target actually
    // sits at. Anything where target is far below reach is what is doing the folding. -xlinka
    private void LogPoseDiagnostics(in FullBodyIKSolver.Target head, in FullBodyIKSolver.Target leftHand,
        in FullBodyIKSolver.Target rightHand, in FullBodyIKSolver.Target leftFoot, in FullBodyIKSolver.Target rightFoot)
    {
        if (_poseDiagLogged)
            return;
        _poseDiagLogged = true;

        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed || Slot == null)
            return;

        float scale = Slot.GlobalScale.x;
        float userHeight = Engine.Current?.InputInterface?.UserHeight ?? 0f;

        LumoraLogger.Log(
            $"AvatarIK[pose-diag] '{Slot.SlotName.Value}': avatarHeight={AvatarHeight.Value:F3} "
            + $"userHeight={userHeight:F3} rootScale={scale:F3} forceTpose={ForceTpose.Value}");

        // What the solver believes the rest pose is. A digitigrade leg is a DEEP zigzag: its chain is far
        // longer than the hips are high, and the foot bone sits well above the sole. If the clearances
        // come back at zero, or the stand height is close to the leg's full chain length, the pose it
        // captured as rest was not the bind pose and every limb derives from a straightened leg.
        var hips = rig.TryGetBone(BodyNode.Hips);
        float soleY = TryFootBoneY(rig, out float sy) ? sy : 0f;
        float hipsY = hips != null && !hips.IsDestroyed ? Slot.GlobalPointToLocal(hips.GlobalPosition).y : 0f;
        float footBoneY = FirstBoneLocalY(rig, BodyNode.LeftFoot);
        float toeBoneY = FirstBoneLocalY(rig, BodyNode.LeftToes);
        LumoraLogger.Log(
            $"AvatarIK[pose-diag]   rest   standHeight={_solver.RestStandHeight:F3} headHeight={_solver.RestHeadHeight:F3} "
            + $"clearance L={_solver.LeftFootGroundClearance:F3} R={_solver.RightFootGroundClearance:F3}");
        LumoraLogger.Log(
            $"AvatarIK[pose-diag]   local  hipsY={hipsY:F3} footY={footBoneY:F3} toeY={toeBoneY:F3} soleY={soleY:F3} "
            + $"(hips above sole = {hipsY - soleY:F3})");

        // Which of the three possible hand faults it is. An arm dragged somewhere silly, a wrist rolled
        // the wrong way, or fingers posed by a model that assumed a human hand frame - they look similar
        // in a screenshot and are three different fixes.
        LogHand("handL", BodyNode.LeftHand, _leftHandNode.Target, _leftHandProxy.Target, _leftHandOffset);
        LogHand("handR", BodyNode.RightHand, _rightHandNode.Target, _rightHandProxy.Target, _rightHandOffset);

        // Which way this body thinks it faces, from every source that has an opinion.
        //
        // Four of them, and they must agree: the axis their own rig file authored, the one we derive
        // from bone geometry, the avatar root's own forward, and what the solver is actually driving the
        // gait with. A body that walks backwards wrong is a body where one of these is opposite the
        // others, and the guess is the fragile one - it reads the LABELLED left-to-right arm line, so an
        // arm frozen mid-gesture skews it.
        var rigAxis = rig.ForwardAxis.Value;
        var guess = rig.GuessForwardAxis();
        var rootFwd = Slot.GlobalRotation * float3.Backward;

        // The angle AlignAvatarFacing measures. Near zero means the root already faces the body; near a
        // hundred and eighty means the frame is backwards and the gait drives every step the wrong way.
        float guessVsRoot = float.NaN;
        if (guess.HasValue)
        {
            var a = guess.Value; a.y = 0f;
            var b = rootFwd; b.y = 0f;
            if (a.LengthSquared > 1e-6f && b.LengthSquared > 1e-6f)
                guessVsRoot = MathF.Acos(System.Math.Clamp(float3.Dot(a.Normalized, b.Normalized), -1f, 1f)) * (180f / MathF.PI);
        }

        LumoraLogger.Log(
            $"AvatarIK[pose-diag]   fwd    rigAxis={(rigAxis.HasValue ? rigAxis.Value.ToString() : "<unset>")} "
            + $"guessLocal={(guess.HasValue ? Slot.GlobalDirectionToLocal(guess.Value).ToString() : "<null>")} "
            + $"guessVsRoot={guessVsRoot:F1}deg signFlip={_solver.ForwardSignFlip}");
        LumoraLogger.Log(
            $"AvatarIK[pose-diag]   fwd    solverRest={Slot.GlobalDirectionToLocal(_solver.RestBodyForward)} "
            + $"solverNow={Slot.GlobalDirectionToLocal(_solver.CurrentBodyForward)}");

        LogEveryBone(rig);

        LogChain("spine", BodyNode.Hips, head, new[] { BodyNode.Hips, BodyNode.Spine, BodyNode.Chest, BodyNode.Neck, BodyNode.Head });
        LogChain("armL", BodyNode.LeftUpperArm, leftHand, new[] { BodyNode.LeftUpperArm, BodyNode.LeftLowerArm, BodyNode.LeftHand });
        LogChain("armR", BodyNode.RightUpperArm, rightHand, new[] { BodyNode.RightUpperArm, BodyNode.RightLowerArm, BodyNode.RightHand });
        LogChain("legL", BodyNode.LeftUpperLeg, leftFoot, new[] { BodyNode.LeftUpperLeg, BodyNode.LeftLowerLeg, BodyNode.LeftFoot });
        LogChain("legR", BodyNode.RightUpperLeg, rightFoot, new[] { BodyNode.RightUpperLeg, BodyNode.RightLowerLeg, BodyNode.RightFoot });

        void LogHand(string label, BodyNode handNode, AvatarPoseDriver? node, Slot? proxy, in ReferenceOffset offset)
        {
            var bone = rig.TryGetBone(handNode);
            bool driven = node?.IsEquippedAndActive ?? false;
            string proxyText = proxy != null && !proxy.IsDestroyed
                ? $"{Slot.GlobalPointToLocal(proxy.GlobalPosition)}"
                : "<none>";

            // How far the grip frame we DERIVED sits from the hand bone's own frame. A few degrees is
            // an authored wrist; ninety is a grip built off a thumb that is not where the model expects.
            float twist = float.NaN;
            if (bone != null && !bone.IsDestroyed && offset.Valid)
            {
                var relative = offset.LocalRotation;
                twist = 2f * MathF.Acos(System.Math.Clamp(MathF.Abs(relative.w), 0f, 1f)) * (180f / MathF.PI);
            }

            LumoraLogger.Log(
                $"AvatarIK[pose-diag]   {label,-6} driven={driven} gripTwist={twist:F1}deg proxyLocal={proxyText}");

            var fingerDriver = bone?.GetComponent<HandPoseDriver>();
            if (fingerDriver != null)
                LumoraLogger.Log($"AvatarIK[pose-diag]   {label,-6} fingerDriver present, idlePose={fingerDriver.IdlePose.Target != null}");
        }

        void LogChain(string label, BodyNode rootNode, in FullBodyIKSolver.Target target, BodyNode[] chain)
        {
            var rootBone = rig.TryGetBone(rootNode);
            if (rootBone == null || rootBone.IsDestroyed)
            {
                LumoraLogger.Log($"AvatarIK[pose-diag]   {label,-6} no root bone");
                return;
            }

            float reach = 0f;
            Slot? previous = null;
            foreach (var node in chain)
            {
                var bone = rig.TryGetBone(node);
                if (bone == null || bone.IsDestroyed)
                    continue;
                if (previous != null)
                    reach += (bone.GlobalPosition - previous.GlobalPosition).Length;
                previous = bone;
            }

            float distance = (target.Position - rootBone.GlobalPosition).Length;
            LumoraLogger.Log(
                $"AvatarIK[pose-diag]   {label,-6} reach={reach:F3} targetAt={distance:F3} "
                + $"({(reach > 0.0001f ? distance / reach : 0f):P0} of reach) weight={target.PositionWeight:F2}");
        }
    }

    // BODY FORWARD REFERENCE
    //
    // The avatar's torso does not point where the head points, and pinning it there is the single
    // biggest reason our IK read as stiff next to other platforms: turn the view and the whole body
    // snapped round with it, so there was never any neck twist to see, and the shoulders swung on every
    // flick of the mouse. A real body TRAILS the head, up to about a shoulder's worth of twist, and only
    // gives up and turns once the head has gone far enough round that the neck runs out.
    //
    // Four parts, in order:
    //   1. A reference direction from the head. Straight up or straight down, the head's forward has no
    //      yaw left to flatten, so blend toward the head's UP axis by how vertical the head has got -
    //      otherwise looking at your feet spins the body on whatever numerical noise survives.
    //   2. Trail limit: the body may sit up to MaxNeckTwist away from that reference and no further.
    //   3. Hysteresis, which is what makes it read as a body rather than a spring. Below the START
    //      threshold nothing happens at all - small look-arounds leave the torso completely still. Once
    //      it commits it keeps turning until it is within the STOP threshold. Standing, the head can go
    //      a long way (60 degrees) before the body bothers; walking, it gives up almost at once (15).
    //   4. Under movement the reference is pulled toward the direction of travel by speed, so walking
    //      sideways or backwards turns the body to face the way you are going.
    // -xlinka
    private float3 _bodyForwardRef = float3.Backward;
    private bool _bodyForwardTurning;
    private bool _bodyForwardValid;

    private const float MaxNeckTwist = 80f * (MathF.PI / 180f);
    private const float TurnStartStanding = 60f * (MathF.PI / 180f);
    private const float TurnStartMoving = 15f * (MathF.PI / 180f);
    private const float TurnStopStanding = 2f * (MathF.PI / 180f);
    private const float TurnStopMoving = 0.5f * (MathF.PI / 180f);
    private const float BodyForwardRate = 6f;
    private const float MovingSpeed = 0.5f;
    private const float FullTurnSpeed = 1f;

    private float3 UpdateBodyForwardReference(in FullBodyIKSolver.Target head, float3 fallback)
    {
        if (!head.Valid)
            return _bodyForwardValid ? _bodyForwardRef : fallback;

        // 1. Reference from the head, vertical-look safe.
        float3 fwd = head.Rotation * float3.Backward;
        float3 up = head.Rotation * float3.Up;
        float vertical = MathF.Abs(float3.Dot(up, float3.Up));
        float3 reference = FlattenDir(float3.Lerp(float3.Dot(fwd, float3.Up) > 0f ? -up : up, fwd, vertical));
        if (reference.LengthSquared < 1e-6f)
            reference = _bodyForwardValid ? _bodyForwardRef : FlattenDir(fallback);
        if (reference.LengthSquared < 1e-6f)
            return fallback;
        reference = reference.Normalized;

        if (!_bodyForwardValid)
        {
            _bodyForwardRef = reference;
            _bodyForwardValid = true;
            return _bodyForwardRef;
        }

        // 4. Bias toward travel while actually travelling.
        // The gait's own smoothed body velocity, one frame old at worst. Reusing it keeps the torso, the
        // step prediction and the arm swing all agreeing about how fast the avatar is actually moving.
        float speed = _smoothedGaitVel.Length;
        float3 travel = FlattenDir(_smoothedGaitVel);
        if (speed > 1e-3f && travel.LengthSquared > 1e-6f)
        {
            // Only bias toward travel that agrees with where the body already faces.
            //
            // Walking backwards is not a reason to spin the torso: a person stepping back keeps facing
            // forward. Blending the reference onto a reversed travel vector turned the avatar a full
            // hundred and eighty degrees the moment it picked up speed, which reads exactly like the body
            // flipping round. Weighting by the alignment leaves a forward walk fully biased, gives a
            // strafe a partial turn - which is what a real body does - and leaves a backward step alone.
            // -xlinka
            float3 travelDir = travel.Normalized;
            float align = float3.Dot(travelDir, reference);
            if (align > 0f)
            {
                float blend = System.Math.Clamp(speed / FullTurnSpeed, 0f, 1f) * align;
                float3 mixed = FlattenDir(float3.Lerp(reference, travelDir, blend));
                if (mixed.LengthSquared > 1e-6f)
                    reference = mixed.Normalized;
            }
        }

        // 3. Hysteresis.
        bool moving = speed >= MovingSpeed;
        float offBy = float3.Angle(reference, _bodyForwardRef);
        if (offBy > (moving ? TurnStartMoving : TurnStartStanding))
            _bodyForwardTurning = true;
        else if (offBy < (moving ? TurnStopMoving : TurnStopStanding))
            _bodyForwardTurning = false;

        if (_bodyForwardTurning)
        {
            float dt = (float)(World?.Time.Delta ?? (1.0 / 60.0));
            float t = System.Math.Clamp(BodyForwardRate * dt, 0f, 1f);
            float3 stepped = FlattenDir(float3.Lerp(_bodyForwardRef, reference, t));
            if (stepped.LengthSquared > 1e-6f)
                _bodyForwardRef = stepped.Normalized;
        }

        // 2. Trail limit, applied last so it is never violated no matter what the steps above did.
        _bodyForwardRef = float3.LimitSwing(_bodyForwardRef, reference, MaxNeckTwist).Normalized;
        return _bodyForwardRef;
    }

    private static float3 ComputeBodyForward(Slot? userSlot, Slot? headSlot)
    {
        Slot? source = userSlot != null && !userSlot.IsDestroyed ? userSlot
            : headSlot != null && !headSlot.IsDestroyed ? headSlot
            : null;
        return source != null
            ? FlattenDir(source.GlobalRotation * float3.Backward)
            : float3.Zero;
    }

    private FullBodyIKSolver.Target ResolveHeadTarget(Slot? userSlot, InputInterface? input)
    {
        var directHeadSlot = UserRoot.Target?.HeadSlot;
        var direct = ViewProxyTarget(directHeadSlot, _viewOffset);
        bool hardwareHead = input?.HeadDevice?.IsTracked == true;

        // Same ownership rule as the hands. The desktop branch below already preferred the direct head
        // slot when the proxy read too low; VR trusted the proxy unconditionally, so a headset-tracked
        // person wearing an avatar with its own head object had the IK aim at a dead proxy while the
        // head slot moved with the headset. -xlinka
        bool headProxyOwned = _headNode.Target?.IsEquippedAndActive ?? false;
        var proxy = headProxyOwned ? ViewProxyTarget(_headProxy.Target, _viewOffset) : default;

        FullBodyIKSolver.Target chosen = proxy.Valid ? proxy : direct;

        if (!hardwareHead && userSlot != null && !userSlot.IsDestroyed)
        {
            float rootY = userSlot.GlobalPosition.y;
            bool proxyTooLow = !proxy.Valid || proxy.Position.y < rootY + 0.35f;
            bool directGood = direct.Valid && direct.Position.y >= rootY + 0.35f;
            if (proxyTooLow && directGood)
                chosen = direct;
            else if (proxyTooLow)
                chosen = DesktopHeadTarget(userSlot, directHeadSlot, input);
        }
        if (!hardwareHead && chosen.Valid)
            chosen = UprightHeadTarget(chosen);

        if (chosen.Valid && userSlot != null && !userSlot.IsDestroyed)
            chosen = CorrectInvertedView(chosen, userSlot);

        return chosen;
    }

    private FullBodyIKSolver.Target DesktopHeadTarget(Slot userSlot, Slot? directHeadSlot, InputInterface? input)
    {
        if (userSlot == null || userSlot.IsDestroyed)
            return default;

        float height = input?.UserHeight ?? InputInterface.DEFAULT_USER_HEIGHT;
        float headHeight = MathF.Max(height - InputInterface.EYE_HEAD_OFFSET, 0.5f);
        float3 position = userSlot.LocalPointToGlobal(new float3(0f, headHeight, 0f));
        floatQ rotation = directHeadSlot != null && !directHeadSlot.IsDestroyed
            ? directHeadSlot.GlobalRotation
            : userSlot.GlobalRotation;
        return ViewTargetFromPose(position, rotation, _viewOffset);
    }

    private static FullBodyIKSolver.Target UprightHeadTarget(in FullBodyIKSolver.Target target)
    {
        var upright = target;
        float3 forward = upright.Rotation * float3.Backward;
        upright.Rotation = BuildUprightRotation(forward);
        return upright;
    }

    // The avatar's View reference point can be authored ~180 from its head bone (a model authored facing +Z while
    // engine forward is -Z), baking a 180 yaw into _viewOffset. That sends the IK head target - and everything
    // derived from it (BodyForward, the arm _curBodyRight frame, and the forward-sign calibration anchor) - out the
    // BACK of the head, so the whole avatar solves backward. A real head turn never reaches 180 from the body, so
    // when the resolved target opposes the user-root forward we strip the spurious 180 yaw. No-op for correctly
    // authored avatars (the target already agrees with the body). -xlinka
    private static FullBodyIKSolver.Target CorrectInvertedView(FullBodyIKSolver.Target target, Slot userSlot)
    {
        float3 tFwd = target.Rotation * float3.Backward; tFwd.y = 0f;
        float3 rFwd = userSlot.GlobalRotation * float3.Backward; rFwd.y = 0f;
        if (tFwd.LengthSquared < 1e-6f || rFwd.LengthSquared < 1e-6f)
            return target;
        if (float3.Dot(tFwd.Normalized, rFwd.Normalized) < -0.25f)
            target.Rotation = floatQ.AxisAngleRad(float3.Up, MathF.PI) * target.Rotation;
        return target;
    }

    // True only when a body node has a LIVE tracked source (VR controller/tracker). Desktop hands/feet have no
    // tracker, so this is false. An undriven limb then takes IK weight 0 (rest pose) instead of folding the
    // avatar toward a marker nothing is moving; the callers decide what else counts as driven (procedural
    // feet, a desktop tool hold). -xlinka
    private static bool IsNodeTracked(Lumora.Core.Input.InputInterface? input, BodyNode node)
    {
        var device = input?.GetBodyNode(node);
        return device != null && device.IsTracking;
    }

    // The two hand tools on this avatar's user, so the solve can ask how far each hand is into a desktop
    // tool hold. Cached, and re-scanned at most once a second while unresolved - an avatar that has no
    // user (a prop standing in the world) would otherwise walk its whole tree every frame for nothing.
    // -xlinka
    private Interaction.HandTool? _leftHandTool;
    private Interaction.HandTool? _rightHandTool;
    private double _nextHandToolScan;

    private static float HoldWeightOf(Interaction.HandTool? tool)
        => tool != null && !tool.IsDestroyed ? System.Math.Clamp(tool.HoldWeight, 0f, 1f) : 0f;

    private void RefreshHandTools()
    {
        bool leftOk = _leftHandTool != null && !_leftHandTool.IsDestroyed;
        bool rightOk = _rightHandTool != null && !_rightHandTool.IsDestroyed;
        if (leftOk && rightOk)
            return;

        double now = World?.Time.TotalTime ?? 0.0;
        if (now < _nextHandToolScan)
            return;
        _nextHandToolScan = now + 1.0;

        var rootSlot = UserRoot.Target?.Slot;
        if (rootSlot == null || rootSlot.IsDestroyed)
            return;

        if (!leftOk)
            _leftHandTool = rootSlot.GetComponentInChildren<Interaction.HandTool>(
                t => t.Side.Value == Lumora.Core.Input.Chirality.Left);
        if (!rightOk)
            _rightHandTool = rootSlot.GetComponentInChildren<Interaction.HandTool>(
                t => t.Side.Value == Lumora.Core.Input.Chirality.Right);
    }

    // If the bend-goal's pose node is tracked (e.g. an elbow/knee tracker), feed its proxy position
    // to the solver so the limb bends toward it.
    private static bool TryTargetFromSocket(ref FullBodyIKSolver.Target target, Slot? socket)
    {
        if (socket == null || socket.IsDestroyed)
            return false;

        target.Position = socket.GlobalPosition;
        target.Rotation = socket.GlobalRotation;
        return true;
    }

    // The LEFT-TO-RIGHT axis measured directly, with no assumed body axis.
    //
    // A derived "right" vector is exactly what made the previous reading useless: projecting a
    // separation that lies along Z onto an axis pointing along X gave two nearly equal numbers and the
    // false conclusion that both hands sat on the same side. The vector between the two shoulder bones
    // IS the body's left-right axis, so measure against that and nothing else. Shoulders rather than
    // hands because a raised hand moves and a shoulder does not. -xlinka
    private double _sepLogAt;

    private void LogHandSeparation(float gateWeight)
    {
        if (gateWeight <= 1e-3f)
            return;
        double now = World != null ? World.Time.TotalTime : 0.0;
        if (now - _sepLogAt < 2.0)
            return;

        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return;

        var lUpper = rig.TryGetBone(BodyNode.LeftUpperArm);
        var rUpper = rig.TryGetBone(BodyNode.RightUpperArm);
        var lHand = rig.TryGetBone(BodyNode.LeftHand);
        var rHand = rig.TryGetBone(BodyNode.RightHand);
        if (lUpper == null || rUpper == null || lHand == null || rHand == null)
        {
            LumoraLogger.Log("HANDSEP: one or more arm bones unmapped "
                           + $"(lUpper={lUpper != null} rUpper={rUpper != null} lHand={lHand != null} rHand={rHand != null})");
            _sepLogAt = now;
            return;
        }

        // Shoulder axis: from the RIGHT shoulder to the LEFT one. On a correct rig the left hand lies on
        // the positive side of it and the right hand on the negative side.
        float3 axis = lUpper.GlobalPosition - rUpper.GlobalPosition;
        axis.y = 0f;
        float shoulderSpan = axis.Length;
        if (shoulderSpan < 1e-4f)
        {
            LumoraLogger.Log($"HANDSEP: shoulders coincide, span={shoulderSpan:F4} - the arms ARE mapped to one side");
            _sepLogAt = now;
            return;
        }
        axis = axis.Normalized;

        float3 mid = (lUpper.GlobalPosition + rUpper.GlobalPosition) * 0.5f;
        float lSide = float3.Dot(lHand.GlobalPosition - mid, axis);
        float rSide = float3.Dot(rHand.GlobalPosition - mid, axis);

        LumoraLogger.Log($"HANDSEP: shoulderSpan={shoulderSpan:F3} leftHandSide={lSide:F3} rightHandSide={rSide:F3} "
                       + $"verdict={(lSide > 0f && rSide < 0f ? "CORRECT" : "MIRRORED-OR-SAME-SIDE")}");
        _sepLogAt = now;
    }

    private string _armDiagLast = string.Empty;
    private double _armDiagAt;

    private void LogArmDiagnostic(string side, in FullBodyIKSolver.Target target, BodyNode handNode,
        float gateWeight, float holdWeight, FullBodyIKSolver.PoleDebug pole)
    {
        if (gateWeight <= 1e-3f)
            return;

        var rig = Rig.Target;
        if (rig == null || rig.IsDestroyed)
            return;
        var bone = rig.TryGetBone(handNode);
        if (bone == null || bone.IsDestroyed)
            return;

        var bonePos = bone.GlobalPosition;
        float miss = float3.Distance(bonePos, target.Position);
        float scale = Slot != null && !Slot.IsDestroyed ? Slot.GlobalScale.x : 1f;

        // Which SIDE of the body this bone is physically on, in the avatar's own frame. Positive is the
        // avatar's right. A bone the right-hand tool drives that reports a negative side is mirrored.
        // Measured from the HIPS, not the avatar root. The root is wherever equip parented the avatar and
        // is under no obligation to sit on the body's centre line, so projecting from it can report both
        // hands on the same side of a perfectly symmetric rig. The hips are the body's centre by
        // definition. Measuring from the wrong origin has already produced two wrong conclusions
        // tonight. -xlinka
        float bodySide = 0f;
        var hips = rig.TryGetBone(BodyNode.Hips) ?? rig.TryGetBone(BodyNode.Spine);
        var bodyRoot = Slot;
        if (hips != null && !hips.IsDestroyed && bodyRoot != null && !bodyRoot.IsDestroyed)
        {
            var r = bodyRoot.GlobalRotation * float3.Right;
            r.y = 0f;
            if (r.LengthSquared > 1e-6f)
                bodySide = float3.Dot(bonePos - hips.GlobalPosition, r.Normalized);
        }

        // swivelDeg is the final elbow angle about the shoulder->hand axis (0 = base direction: down for a reach
        // in front, back for a hang; positive = outward). handSwivelDeg is the hand-position term alone, so a gap
        // between the two is the rest bend, a goal or the wrist roll pulling. poleSource names the biggest voter.
        const float rad2deg = 180f / MathF.PI;
        string poleSource = pole.Source switch
        {
            FullBodyIKSolver.PoleSource.Rest => "rest",
            FullBodyIKSolver.PoleSource.Hand => "hand",
            FullBodyIKSolver.PoleSource.Goal => "goal",
            _ => "none",
        };
        string line = $"ARMDIAG {side}: hold={holdWeight:F2} bodySide={bodySide:F3} targetW=({target.Position.x:F3},{target.Position.y:F3},{target.Position.z:F3}) "
                    + $"boneW=({bonePos.x:F3},{bonePos.y:F3},{bonePos.z:F3}) miss={miss:F3} "
                    + $"posW={target.PositionWeight:F2} bendGoal={target.HasBendGoal} avatarScale={scale:F3} "
                    + $"swivelDeg={pole.SwivelRad * rad2deg:F1} handSwivelDeg={pole.HandSwivelRad * rad2deg:F1} "
                    + $"poleSource={poleSource} restW={pole.RestWeight:F2}";

        double now = World != null ? World.Time.TotalTime : 0.0;
        if (line == _armDiagLast || now - _armDiagAt < 2.0)
            return;
        _armDiagLast = line;
        _armDiagAt = now;
        LumoraLogger.Log(line);
    }

    private static void ApplyBendGoal(ref FullBodyIKSolver.Target target, SyncRef<Slot> proxy, SyncRef<AvatarPoseDriver> node)
    {
        if ((node.Target?.IsEquippedAndActive ?? false) && proxy.Target != null && !proxy.Target.IsDestroyed)
        {
            target.HasBendGoal = true;
            target.BendGoal = proxy.Target.GlobalPosition;
        }
    }

    // Calibration offsets are folded into the TARGET, never written back to
    // the proxy: the proxies are driven by pose nodes every frame, and a
    // second writer would both fight the drives and generate sync churn.
    private FullBodyIKSolver.Target HandTargetFor(BodyNode node, Slot? proxy, AvatarPoseDriver? poseNode, in ReferenceOffset offset)
    {
        if (poseNode?.IsEquippedAndActive ?? false)
            return ProxyTarget(proxy, offset);

        var socket = FindSocket(node);
        if (socket != null)
        {
            socket.GetFilteredPose(out float3 pos, out floatQ rot, out _);
            return TargetFromPose(pos, rot, offset);
        }

        // No socket either: fall back to the raw body-node slot, then to the proxy as a last resort.
        var slot = node == BodyNode.LeftHand ? UserRoot.Target?.LeftHandSlot : UserRoot.Target?.RightHandSlot;
        if (slot != null && !slot.IsDestroyed)
            return TargetFromPose(slot.GlobalPosition, slot.GlobalRotation, offset);
        return ProxyTarget(proxy, offset);
    }

    private AvatarSocket? _leftSocket, _rightSocket;
    private double _socketScanAt;

    private AvatarSocket? FindSocket(BodyNode node)
    {
        ref var cached = ref (node == BodyNode.LeftHand ? ref _leftSocket : ref _rightSocket);
        if (cached != null && !cached.IsDestroyed)
            return cached;

        double now = World?.Time.TotalTime ?? 0.0;
        if (now < _socketScanAt)
            return null;
        _socketScanAt = now + 1.0;

        var root = UserRoot.Target?.Slot;
        if (root == null || root.IsDestroyed)
            return null;
        cached = root.GetComponentInChildren<AvatarSocket>(s => s.Node.Value == node);
        return cached;
    }

    private static FullBodyIKSolver.Target ProxyTarget(Slot? proxy, in ReferenceOffset offset)
    {
        if (proxy == null || proxy.IsDestroyed)
            return default;

        return TargetFromPose(proxy.GlobalPosition, proxy.GlobalRotation, offset);
    }

    private static FullBodyIKSolver.Target ViewProxyTarget(Slot? proxy, in ReferenceOffset offset)
    {
        if (proxy == null || proxy.IsDestroyed)
            return default;

        return ViewTargetFromPose(proxy.GlobalPosition, proxy.GlobalRotation, offset);
    }

    // HEAD-target variant: keep the RAW tracked rotation - the tracked head pose IS the view/camera.
    // Rebasing it into the head BONE's authored frame (like TargetFromPose does for limb bones) put the
    // bone's arbitrary axes into the look: on a back-authored head bone the look came out the camera's
    // BACK, whose flattened yaw the downstream 180-patch fixes but whose PITCH stays mirrored (look up,
    // head down). The offset still places the bone position exactly like the limb path. -xlinka
    private static FullBodyIKSolver.Target ViewTargetFromPose(float3 position, floatQ rotation, in ReferenceOffset offset)
    {
        if (offset.Valid)
        {
            var boneRotation = rotation * offset.LocalRotation.Inverse;
            position -= boneRotation * offset.LocalPosition;
        }

        return new FullBodyIKSolver.Target
        {
            Valid = true,
            Position = position,
            Rotation = rotation,
            PositionWeight = 1f,
            RotationWeight = 1f,
        };
    }

    private static FullBodyIKSolver.Target TargetFromPose(float3 position, floatQ rotation, in ReferenceOffset offset)
    {
        if (offset.Valid)
        {
            var adjustedRotation = rotation * offset.LocalRotation.Inverse;
            position -= adjustedRotation * offset.LocalPosition;
            rotation = adjustedRotation;
        }

        return new FullBodyIKSolver.Target
        {
            Valid = true,
            Position = position,
            Rotation = rotation,
            PositionWeight = 1f,
            RotationWeight = 1f,
        };
    }

    private static floatQ BuildUprightRotation(float3 forward)
    {
        if (forward.LengthSquared < 1e-8f)
            forward = float3.Backward;
        forward = forward.Normalized;

        floatQ q1 = FabrikSolver.FromToRotation(float3.Backward, forward);
        float3 curUp = q1 * float3.Up;
        float3 upProj = float3.Up - forward * float3.Dot(float3.Up, forward);
        if (upProj.LengthSquared < 1e-6f)
            return q1.Normalized;
        floatQ q2 = FabrikSolver.FromToRotation(curUp, upProj.Normalized);
        return (q2 * q1).Normalized;
    }

    public void Setup(SkeletonBuilder skeleton, HumanoidRig rig, UserRoot userRoot)
    {
        Skeleton.Target = skeleton;
        Rig.Target = rig;
        UserRoot.Target = userRoot;

        EnsurePoseNodes();
        EnsureSolver();
        RecomputeReferenceOffsets();

        _isInitialized = true;
        LumoraLogger.Log($"AvatarIK: Setup complete ({rig?.Bones.Count ?? 0} bones)");
    }

    // an avatar is self-describing: its components ARE the metadata
    public bool SetupFromAvatar(UserRoot userRoot)
    {
        var rig = Rig.Target ?? Slot.GetComponent<HumanoidRig>() ?? Slot.GetComponentInChildren<HumanoidRig>();
        var skeleton = Skeleton.Target
            ?? SkeletonHolding(rig)
            ?? Slot.GetComponent<SkeletonBuilder>()
            ?? Slot.GetComponentInChildren<SkeletonBuilder>();
        if (skeleton == null || rig == null)
        {
            LumoraLogger.Warn("AvatarIK: Avatar tree has no skeleton or rig");
            return false;
        }

        Setup(skeleton, rig, userRoot);
        SetupTracking(userRoot);
        return true;
    }

    // An avatar can carry SEVERAL skeletons: a body, a tail, a swap mesh, anything skinned to its own
    // bone set. GetComponentInChildren answers with whichever it reaches first, and driving a biped off
    // a 35-bone tail rig fails silently and completely. Pick the one that actually holds the rig's hips.
    private SkeletonBuilder? SkeletonHolding(HumanoidRig? rig)
    {
        var hips = rig?.TryGetBone(BodyNode.Hips);
        if (hips == null)
            return null;

        foreach (var candidate in Slot.GetComponentsInChildren<SkeletonBuilder>())
        {
            for (int i = 0; i < candidate.BoneCount && i < candidate.BoneSlots.Count; i++)
            {
                if (ReferenceEquals(candidate.BoneSlots[i], hips))
                    return candidate;
            }
        }
        return null;
    }

    public void SetupTracking(UserRoot userRoot)
    {
        if (userRoot == null)
        {
            LumoraLogger.Warn("AvatarIK: Cannot setup tracking - null UserRoot");
            return;
        }

        UserRoot.Target = userRoot;
        EnsurePoseNodes();

        EquipPoseNodeToBodySlot(userRoot.HeadSlot, _headNode.Target);
        EquipPoseNodeToBodySlot(userRoot.LeftHandSlot, _leftHandNode.Target);
        EquipPoseNodeToBodySlot(userRoot.RightHandSlot, _rightHandNode.Target);
        EquipPoseNodeToBodySlot(userRoot.LeftFootSlot, _leftFootNode.Target);
        EquipPoseNodeToBodySlot(userRoot.RightFootSlot, _rightFootNode.Target);

        EnsureSolver();
        RecomputeReferenceOffsets();
        LumoraLogger.Log($"AvatarIK: Tracking connected for UserRoot '{userRoot.Slot.SlotName.Value}'");
    }

    // Build coarse body colliders for a worn avatar: a sphere on the head and a capsule along each limb segment
    // + the torso, so grab/interaction raycasts have something to hit. Idempotent-ish - skips a segment if a
    // "BodyCollider" child already exists on the proximal bone. Each capsule lives on a child slot oriented so
    // its local Y runs down the bone (the capsule shape is Y-aligned). -xlinka
    public void GenerateBodyColliders()
    {
        var rig = Rig.Target;
        if (rig == null)
            return;

        // Head sphere, sized from the neck-to-head length when available.
        var head = rig.TryGetBone(BodyNode.Head);
        if (head != null)
        {
            RemoveBodyCollider(head);
            float r = 0.08f; // world-space radius
            var neck = rig.TryGetBone(BodyNode.Neck) ?? rig.TryGetBone(BodyNode.Chest);
            if (neck != null)
            {
                var d = head.GlobalPosition - neck.GlobalPosition;
                float len = (float)System.Math.Sqrt(d.LengthSquared);
                if (len > 1e-4f) r = System.Math.Clamp(len * 0.5f, 0.04f, 0.1f);
            }
            // Store in the bone's LOCAL units = world size / bone global scale. The collider is a child of the bone,
            // so the laser/physics multiply by the bone's global scale to recover the world size. That scale carries
            // the FBX armature scale (often 50-64x) which only settles a frame after import and changes again on
            // equip - the refit watcher re-runs this against the CURRENT scale so the hitbox stays correct. -xlinka
            float scale = head.GlobalScale.x; if (scale < 1e-4f) scale = 1f;
            var s = head.AddSlot("BodyCollider");
            var headCol = s.AttachComponent<SphereCollider>();
            // Query-only (Trigger): hit by grab/laser raycasts but NEVER a solid wall - a worn avatar's bone capsules
            // were colliding with the wearer's own character controller and flinging the user around. -xlinka
            headCol.Type.Value = Lumora.Core.Physics.ColliderType.Trigger;
            headCol.Radius.Value = r / scale;
        }

        // Limb capsules (ratio = radius / segment length).
        AddLimbCapsule(rig, BodyNode.LeftUpperArm, BodyNode.LeftLowerArm, 0.12f);
        AddLimbCapsule(rig, BodyNode.LeftLowerArm, BodyNode.LeftHand, 0.10f);
        AddLimbCapsule(rig, BodyNode.RightUpperArm, BodyNode.RightLowerArm, 0.12f);
        AddLimbCapsule(rig, BodyNode.RightLowerArm, BodyNode.RightHand, 0.10f);
        AddLimbCapsule(rig, BodyNode.LeftUpperLeg, BodyNode.LeftLowerLeg, 0.14f);
        AddLimbCapsule(rig, BodyNode.LeftLowerLeg, BodyNode.LeftFoot, 0.12f);
        AddLimbCapsule(rig, BodyNode.RightUpperLeg, BodyNode.RightLowerLeg, 0.14f);
        AddLimbCapsule(rig, BodyNode.RightLowerLeg, BodyNode.RightFoot, 0.12f);

        // Torso capsule from the hips up to the highest available spine/neck bone.
        var torsoTop = rig.TryGetBone(BodyNode.Neck) ?? rig.TryGetBone(BodyNode.UpperChest)
            ?? rig.TryGetBone(BodyNode.Chest) ?? rig.TryGetBone(BodyNode.Spine);
        var hips = rig.TryGetBone(BodyNode.Hips);
        if (hips != null && torsoTop != null)
            AddCapsuleBetween(hips, torsoTop, 0.16f);

        LumoraLogger.Log("AvatarIK: Generated body colliders.");
    }

    private static void AddLimbCapsule(HumanoidRig rig, BodyNode proximal, BodyNode distal, float radiusRatio)
    {
        var p = rig.TryGetBone(proximal);
        var c = rig.TryGetBone(distal);
        if (p != null && c != null)
            AddCapsuleBetween(p, c, radiusRatio);
    }

    // Attach a Y-aligned capsule spanning two bone slots, on a child of the proximal slot rotated so its local Y
    // runs from proximal to distal. Skips if a "BodyCollider" child already sits on the proximal bone.
    private static void AddCapsuleBetween(Slot proximal, Slot distal, float radiusRatio)
    {
        if (proximal == null || proximal.IsDestroyed || distal == null || distal.IsDestroyed)
            return;
        RemoveBodyCollider(proximal);

        var pg = proximal.GlobalPosition;
        var cg = distal.GlobalPosition;
        var dir = cg - pg;
        float worldLen = (float)System.Math.Sqrt(dir.LengthSquared);
        if (worldLen < 1e-4f)
            return;

        // Place + orient in WORLD (the joints' real positions), but store the dims in the bone's LOCAL units
        // (world size / bone global scale). The collider is a child of the bone, so laser/physics multiply by the
        // bone's global scale to recover the world size. That scale carries the FBX armature scale (often 50-64x),
        // which only settles a frame after import and changes again on equip - so the refit watcher re-runs this
        // against the CURRENT scale. Measuring in world + clamping in world + dividing keeps the hitbox correct
        // at whatever scale the bone currently has. -xlinka
        float scale = proximal.GlobalScale.x; if (scale < 1e-4f) scale = 1f;
        var s = proximal.AddSlot("BodyCollider");
        s.GlobalPosition = new float3((pg.x + cg.x) * 0.5f, (pg.y + cg.y) * 0.5f, (pg.z + cg.z) * 0.5f);
        s.GlobalRotation = RotateUpTo(dir.Normalized);

        var cap = s.AttachComponent<CapsuleCollider>();
        // Query-only (Trigger): raycast-hittable for grab/laser, never a solid wall the wearer's character body
        // collides with (which was flinging the user around). -xlinka
        cap.Type.Value = Lumora.Core.Physics.ColliderType.Trigger;
        cap.Radius.Value = System.Math.Clamp(worldLen * radiusRatio, 0.02f, 0.12f) / scale;
        cap.Height.Value = worldLen / scale;
    }

    // Destroy any existing "BodyCollider" child of a bone, so GenerateBodyColliders can be re-run to re-fit the
    // colliders to the bone's current scale (the armature scale settles late / changes on equip). -xlinka
    private static void RemoveBodyCollider(Slot bone)
    {
        System.Collections.Generic.List<Slot>? toRemove = null;
        foreach (var child in bone.Children)
            if (child.SlotName.Value == "BodyCollider")
                (toRemove ??= new System.Collections.Generic.List<Slot>()).Add(child);
        if (toRemove != null)
            foreach (var c in toRemove)
                c.Destroy();
    }

    // Rotation taking the capsule's default up (+Y) onto a target unit direction.
    private static floatQ RotateUpTo(float3 to)
    {
        float3 from = float3.Up;
        float d = float3.Dot(from, to);
        if (d >= 0.99999f) return floatQ.Identity;
        if (d <= -0.99999f) return floatQ.AxisAngle(float3.Right, MathF.PI); // AxisAngle is RADIANS, not degrees
        float3 axis = float3.Cross(from, to).Normalized;
        float ang = (float)System.Math.Acos(System.Math.Clamp(d, -1f, 1f));
        return floatQ.AxisAngleRad(axis, ang);
    }

    private void EnsurePoseNodes()
    {
        EnsurePoseNode(_headProxy, _headNode, BodyNode.Head, "Head");
        EnsurePoseNode(_pelvisProxy, _pelvisNode, BodyNode.Hips, "Hips");
        EnsurePoseNode(_chestProxy, _chestNode, BodyNode.Chest, "Chest");
        EnsurePoseNode(_leftHandProxy, _leftHandNode, BodyNode.LeftHand, "LeftHand");
        EnsurePoseNode(_rightHandProxy, _rightHandNode, BodyNode.RightHand, "RightHand");
        EnsurePoseNode(_leftElbowProxy, _leftElbowNode, BodyNode.LeftLowerArm, "LeftElbow");
        EnsurePoseNode(_rightElbowProxy, _rightElbowNode, BodyNode.RightLowerArm, "RightElbow");
        EnsurePoseNode(_leftFootProxy, _leftFootNode, BodyNode.LeftFoot, "LeftFoot");
        EnsurePoseNode(_rightFootProxy, _rightFootNode, BodyNode.RightFoot, "RightFoot");
        EnsurePoseNode(_leftKneeProxy, _leftKneeNode, BodyNode.LeftLowerLeg, "LeftKnee");
        EnsurePoseNode(_rightKneeProxy, _rightKneeNode, BodyNode.RightLowerLeg, "RightKnee");
    }

    private AvatarPoseDriver EnsurePoseNode(
        SyncRef<Slot> proxy,
        SyncRef<AvatarPoseDriver> poseNode,
        BodyNode node,
        string name)
    {
        if (proxy.Target == null)
            proxy.Target = Slot.AddSlot($"{name}Proxy");

        if (poseNode.Target == null)
            poseNode.Target = proxy.Target.AttachComponent<AvatarPoseDriver>();

        poseNode.Target.Node.Value = node;
        return poseNode.Target;
    }

    private static void EquipPoseNodeToBodySlot(Slot bodySlot, AvatarPoseDriver poseNode)
    {
        if (bodySlot == null || poseNode == null)
            return;

        var avatarSlot = bodySlot.GetComponent<AvatarSocket>();
        if (avatarSlot == null)
            avatarSlot = bodySlot.GetComponent<TrackedDevicePositioner>()?.ObjectSlot.Target;
        if (avatarSlot == null)
            avatarSlot = bodySlot.GetComponentInChildren<AvatarSocket>();

        if (avatarSlot == null)
        {
            LumoraLogger.Warn($"AvatarIK: No AvatarSocket found for body slot '{bodySlot.SlotName.Value}'");
            return;
        }

        var dequippedObjects = new HashSet<IAvatarEquippable>();
        if (!avatarSlot.PreEquip(poseNode, dequippedObjects))
        {
            LumoraLogger.Warn($"AvatarIK: Failed to prepare {poseNode.Node.Value} for '{bodySlot.SlotName.Value}'");
            return;
        }

        avatarSlot.Equip(poseNode);
        LumoraLogger.Log($"AvatarIK: Equipped {poseNode.Node.Value} pose node -> '{bodySlot.SlotName.Value}'");
    }

    // Calibration is read straight from AvatarReferencePoint components in
    // the avatar tree (placed by the creator flow) - the data IS the
    // components, no metadata sidecar.
    private void RecomputeReferenceOffsets()
    {
        _viewOffset = default;
        _leftHandOffset = default;
        _rightHandOffset = default;
        _leftFootOffset = default;
        _rightFootOffset = default;

        var rig = Rig.Target;
        if (rig == null)
            return;

        var states = CaptureBoneLocalState(rig);
        bool restorePose = false;
        if (_solver.IsReady)
        {
            _solver.ResetToDefaultPose();
            restorePose = true;
        }
        else if (ForceTpose.Value)
        {
            rig.MakeTPose();
            restorePose = true;
        }

        try
        {
            Slot? view = null, leftHand = null, rightHand = null, leftFoot = null, rightFoot = null;
            foreach (var point in Slot.GetComponentsInChildren<AvatarReferencePoint>())
            {
                switch (point.Kind.Value)
                {
                    case AvatarReferenceKind.View: view ??= point.Slot; break;
                    case AvatarReferenceKind.LeftHandGrip: leftHand ??= point.Slot; break;
                    case AvatarReferenceKind.RightHandGrip: rightHand ??= point.Slot; break;
                    case AvatarReferenceKind.LeftFoot: leftFoot ??= point.Slot; break;
                    case AvatarReferenceKind.RightFoot: rightFoot ??= point.Slot; break;
                }
            }

            if (view != null && !view.IsDestroyed)
                TryComputeReferenceOffset(rig.TryGetBone(BodyNode.Head), view, ref _viewOffset);
            else
                TryComputeReferenceOffset(rig.TryGetBone(BodyNode.Head), AvatarCalibration.ComputeView(Slot, rig), ref _viewOffset);
            TryComputeReferenceOffset(rig.TryGetBone(BodyNode.LeftHand), leftHand!, ref _leftHandOffset);
            TryComputeReferenceOffset(rig.TryGetBone(BodyNode.RightHand), rightHand!, ref _rightHandOffset);
            TryComputeReferenceOffset(rig.TryGetBone(BodyNode.LeftFoot), leftFoot!, ref _leftFootOffset);
            TryComputeReferenceOffset(rig.TryGetBone(BodyNode.RightFoot), rightFoot!, ref _rightFootOffset);

            MeasureAvatarHeight(rig, view, leftFoot, rightFoot);
        }
        finally
        {
            if (restorePose)
                RestoreBoneLocalState(states);
        }
    }

    // Height comes from stable setup references in avatar-local space, not from the live solved skeleton. That keeps
    // crouch/IK/floor-pin output from feeding back into AvatarHeight and avoids the scale oscillation that makes a
    // mis-measured avatar explode in size. -xlinka
    private void MeasureAvatarHeight(HumanoidRig rig, Slot? view, Slot? leftFoot, Slot? rightFoot)
    {
        if (Slot == null || Slot.IsDestroyed)
            return;

        if (!TryMeasureFromReferences(rig, view, leftFoot, rightFoot, out float measured))
            measured = MeasureFromDefaultBonePose(rig);

        if (measured <= 0.01f)
            return;

        if (System.Math.Abs(measured - AvatarHeight.Value) > 0.001f)
        {
            AvatarHeight.Value = measured;
            _lastRescaleHeight = -1f; // force MaybeRescaleAvatar to re-apply with the real measurement
            _lastRescaleCompensation = -1f;
        }
    }

    // Every mapped bone, once: where it is, which way it points, and how it is rolled.
    //
    // Positions are AVATAR-LOCAL so they can be read against the bind table directly. axis is the
    // direction to the next bone down the chain, normalised, which is the thing that actually decides
    // how a limb or a finger folds. rot is the bone's LOCAL rotation as euler degrees - a finger whose
    // roll differs from its neighbours by ninety degrees is a finger the pose model will splay instead
    // of curl. -xlinka
    private void LogEveryBone(HumanoidRig rig)
    {
        LumoraLogger.Log($"AvatarIK[bones] '{Slot.SlotName.Value}' scale={Slot.GlobalScale.x:F3} - positions AVATAR-LOCAL, rot = local euler degrees");

        foreach (var node in AllBodyNodes)
        {
            var bone = rig.TryGetBone(node);
            if (bone == null || bone.IsDestroyed)
                continue;

            var localPos = Slot.GlobalPointToLocal(bone.GlobalPosition);
            var euler = bone.LocalRotation.Value.ToEuler() * (180f / MathF.PI);

            var child = NextInChain(rig, node);
            string axis = "(  end  )";
            if (child != null && !child.IsDestroyed)
            {
                var d = Slot.GlobalPointToLocal(child.GlobalPosition) - localPos;
                if (d.LengthSquared > 1e-10f)
                {
                    d = d.Normalized;
                    axis = $"({d.x,6:F2},{d.y,6:F2},{d.z,6:F2})";
                }
            }

            LumoraLogger.Log(
                $"AvatarIK[bones]  {node,-30} {bone.SlotName.Value,-22} "
                + $"pos=({localPos.x,7:F3},{localPos.y,7:F3},{localPos.z,7:F3}) "
                + $"axis={axis} rot=({euler.x,7:F1},{euler.y,7:F1},{euler.z,7:F1})");
        }
    }

    // The next bone DOWN this bone's own chain, so "axis" means the same thing for a spine, a limb and
    // a finger segment.
    private static Slot? NextInChain(HumanoidRig rig, BodyNode node)
    {
        BodyNode next = node switch
        {
            BodyNode.Hips => BodyNode.Spine,
            BodyNode.Spine => BodyNode.Chest,
            BodyNode.Chest => BodyNode.Neck,
            BodyNode.Neck => BodyNode.Head,
            BodyNode.LeftShoulder => BodyNode.LeftUpperArm,
            BodyNode.LeftUpperArm => BodyNode.LeftLowerArm,
            BodyNode.LeftLowerArm => BodyNode.LeftHand,
            BodyNode.RightShoulder => BodyNode.RightUpperArm,
            BodyNode.RightUpperArm => BodyNode.RightLowerArm,
            BodyNode.RightLowerArm => BodyNode.RightHand,
            BodyNode.LeftUpperLeg => BodyNode.LeftLowerLeg,
            BodyNode.LeftLowerLeg => BodyNode.LeftFoot,
            BodyNode.LeftFoot => BodyNode.LeftToes,
            BodyNode.RightUpperLeg => BodyNode.RightLowerLeg,
            BodyNode.RightLowerLeg => BodyNode.RightFoot,
            BodyNode.RightFoot => BodyNode.RightToes,
            _ => BodyNode.NONE,
        };

        // Finger segments run metacarpal -> proximal -> intermediate -> distal -> tip, which the enum
        // lays out contiguously, so the next segment is simply the next value.
        if (next == BodyNode.NONE && node.IsFinger())
        {
            var candidate = node + 1;
            if (candidate.IsFinger() && candidate.GetFingerSegmentType() > node.GetFingerSegmentType())
                next = candidate;
        }

        return next == BodyNode.NONE ? null : rig.TryGetBone(next);
    }

    private static readonly BodyNode[] AllBodyNodes = BuildBodyNodeOrder();

    private static BodyNode[] BuildBodyNodeOrder()
    {
        var all = (BodyNode[])System.Enum.GetValues(typeof(BodyNode));
        var ordered = new List<BodyNode>(all.Length);
        foreach (var node in all)
        {
            // Skip the markers and aliases the enum carries alongside the real joints.
            if (node <= BodyNode.NONE || node >= BodyNode.END)
                continue;
            if (!ordered.Contains(node))
                ordered.Add(node);
        }
        return ordered.ToArray();
    }

    private float FirstBoneLocalY(HumanoidRig rig, BodyNode node)
    {
        var bone = rig.TryGetBone(node);
        return bone != null && !bone.IsDestroyed ? Slot.GlobalPointToLocal(bone.GlobalPosition).y : float.NaN;
    }

    // The lowest foot/toe bone in avatar-local space. Slightly above the actual sole, which is a far
    // smaller error than measuring from the hips.
    private bool TryFootBoneY(HumanoidRig rig, out float y)
    {
        y = 0f;
        bool found = false;
        foreach (var node in FloorBones)
        {
            var bone = rig.TryGetBone(node);
            if (bone == null || bone.IsDestroyed)
                continue;

            float boneY = Slot.GlobalPointToLocal(bone.GlobalPosition).y;
            if (!found || boneY < y)
                y = boneY;
            found = true;
        }
        return found;
    }

    private static readonly BodyNode[] FloorBones =
    {
        BodyNode.LeftToes, BodyNode.RightToes, BodyNode.LeftFoot, BodyNode.RightFoot,
    };

    private bool TryMeasureFromReferences(HumanoidRig rig, Slot? view, Slot? leftFoot, Slot? rightFoot, out float measured)
    {
        measured = 0f;
        if (view == null || view.IsDestroyed)
            return false;

        float viewY = Slot.GlobalPointToLocal(view.GlobalPosition).y;
        bool hasBase = false;
        float baseY = 0f;

        var rootBone = Skeleton.Target?.RootBone.Target;
        if (rootBone != null && !rootBone.IsDestroyed)
        {
            baseY = Slot.GlobalPointToLocal(rootBone.GlobalPosition).y;
            hasBase = true;
        }

        if (TryFootReferenceY(leftFoot, rightFoot, out float footY))
        {
            if (!hasBase || baseY > footY + 0.25f)
                baseY = footY;
            hasBase = true;
        }
        else if (TryFootBoneY(rig, out float boneFootY) && (!hasBase || baseY > boneFootY + 0.25f))
        {
            // The root bone is not the floor.
            //
            // On most rigs - and on EVERY package import, where the skeleton is rooted at the topmost
            // bone - the root bone IS the hips, which stand a whole leg above the ground. Taken as the
            // base it makes this function return the TORSO height and call it the avatar's height, and
            // nothing downstream can tell: the rescale simply multiplies the avatar by however much it
            // thinks is missing. On the test avatar that measured 0.414 against a 1.75 m user and scaled
            // the body 4x, after which the spine had to fold to reach a head target a third of its own
            // length away, and the whole avatar wore crumpled.
            //
            // Foot REFERENCES are the better answer because they sit at the sole, but they only exist
            // when someone opted into feet calibration. The foot BONES are always there. -xlinka
            baseY = boneFootY;
            hasBase = true;
        }

        if (!hasBase)
        {
            var hips = rig.TryGetBone(BodyNode.Hips);
            if (hips != null && !hips.IsDestroyed)
            {
                baseY = Slot.GlobalPointToLocal(hips.GlobalPosition).y;
                hasBase = true;
            }
        }

        if (!hasBase)
            return false;

        measured = viewY - baseY;
        return measured > 0.01f;
    }

    private bool TryFootReferenceY(Slot? leftFoot, Slot? rightFoot, out float footY)
    {
        footY = 0f;
        bool has = false;
        if (leftFoot != null && !leftFoot.IsDestroyed)
        {
            footY = Slot.GlobalPointToLocal(leftFoot.GlobalPosition).y;
            has = true;
        }
        if (rightFoot != null && !rightFoot.IsDestroyed)
        {
            float y = Slot.GlobalPointToLocal(rightFoot.GlobalPosition).y;
            footY = has ? System.Math.Min(footY, y) : y;
            has = true;
        }
        return has;
    }

    private float MeasureFromDefaultBonePose(HumanoidRig rig)
    {
        var states = CaptureBoneLocalState(rig);
        if (_solver.IsReady)
            _solver.ResetToDefaultPose();
        else if (ForceTpose.Value)
            rig.MakeTPose();

        float measured = 0f;
        try
        {
            var head = rig.TryGetBone(BodyNode.Head) ?? rig.TryGetBone(BodyNode.Neck);
            if (head == null || head.IsDestroyed)
                return 0f;

            var lFoot = rig.TryGetBone(BodyNode.LeftFoot);
            var rFoot = rig.TryGetBone(BodyNode.RightFoot);
            float footY;
            bool hasFoot = false;
            if (lFoot != null && !lFoot.IsDestroyed)
            {
                footY = Slot.GlobalPointToLocal(lFoot.GlobalPosition).y;
                hasFoot = true;
            }
            else
            {
                footY = 0f;
            }
            if (rFoot != null && !rFoot.IsDestroyed)
            {
                float y = Slot.GlobalPointToLocal(rFoot.GlobalPosition).y;
                footY = hasFoot ? System.Math.Min(footY, y) : y;
                hasFoot = true;
            }
            if (!hasFoot)
                footY = 0f;

            measured = Slot.GlobalPointToLocal(head.GlobalPosition).y - footY;
        }
        finally
        {
            RestoreBoneLocalState(states);
        }

        return measured;
    }

    private struct BoneLocalState
    {
        public Slot Bone;
        public float3 LocalPosition;
        public floatQ LocalRotation;
    }

    private List<BoneLocalState> CaptureBoneLocalState(HumanoidRig rig)
    {
        var states = new List<BoneLocalState>();
        var seen = new HashSet<Slot>();
        var skeleton = Skeleton.Target;
        if (skeleton != null)
        {
            for (int i = 0; i < skeleton.BoneSlots.Count; i++)
                AddBoneState(skeleton.BoneSlots[i], states, seen);
        }
        foreach (var entry in rig.Bones)
            AddBoneState(entry.Value.Target, states, seen);
        return states;
    }

    private static void AddBoneState(Slot? bone, List<BoneLocalState> states, HashSet<Slot> seen)
    {
        if (bone == null || bone.IsDestroyed || !seen.Add(bone))
            return;
        states.Add(new BoneLocalState
        {
            Bone = bone,
            LocalPosition = bone.LocalPosition.Value,
            LocalRotation = bone.LocalRotation.Value,
        });
    }

    private static void RestoreBoneLocalState(List<BoneLocalState> states)
    {
        for (int i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (state.Bone == null || state.Bone.IsDestroyed)
                continue;
            state.Bone.LocalPosition.SetValueSilently(state.LocalPosition, change: true);
            state.Bone.LocalRotation.SetValueSilently(state.LocalRotation, change: true);
        }
    }

    private static void TryComputeReferenceOffset(Slot bone, Slot reference, ref ReferenceOffset offset)
    {
        offset = default;
        if (bone == null || reference == null || bone.IsDestroyed || reference.IsDestroyed)
            return;

        TryComputeReferenceOffset(bone, reference.GlobalPosition, reference.GlobalRotation, ref offset);
    }

    private void TryComputeReferenceOffset(Slot bone, in AvatarCalibration.RefPose reference, ref ReferenceOffset offset)
    {
        offset = default;
        if (!reference.Valid || Slot == null || Slot.IsDestroyed)
            return;

        TryComputeReferenceOffset(
            bone,
            Slot.LocalPointToGlobal(reference.LocalPosition),
            Slot.LocalRotationToGlobal(reference.LocalRotation),
            ref offset);
    }

    private static void TryComputeReferenceOffset(Slot bone, in float3 referencePosition, in floatQ referenceRotation, ref ReferenceOffset offset)
    {
        offset = default;
        if (bone == null || bone.IsDestroyed)
            return;

        var boneRotation = bone.GlobalRotation;
        offset.LocalPosition = boneRotation.Inverse * (referencePosition - bone.GlobalPosition);
        offset.LocalRotation = boneRotation.Inverse * referenceRotation;
        offset.Valid = true;
    }

    // Procedural locomotion: an untracked foot stays PLANTED, then STEPS (lerp with a
    // lift arc) to a new stance position when the body strays past a threshold - one foot at a time
    // so it reads as alternating steps, not sliding or teleporting. Feet anchor to the user-root
    // ground plane (crouch/head-bob must not lift them). Every peer derives this from the replicated
    // head/root, so the writes are local - broadcasting would just be churn.
    private void UpdateProceduralFeet()
    {
        var headSlot = _headProxy.Target;
        if (headSlot == null)
        {
            _hasProceduralGroundY = false;
            return;
        }

        // World clock: already scaled + clamped, one timing source for everything.
        float dt = World?.Time.Delta ?? (1f / 60f);

        // Anchor the gait to the BODY ROOT (locomotion position + facing), NOT the head. On desktop the head is the
        // free-look camera: anchoring to it made the stance CENTER drift and the stance BASIS spin as you just LOOKED
        // AROUND, so the feet chased the moving target and stepped in place ("walking without moving"). The user root
        // only moves on real locomotion and only turns on real turn input, so feet now step when you actually move or
        // turn. Place feet relative to the slow-turning body root, not the twitchy head. Falls back to the head only
        // when there's no user root. -xlinka
        var rootSlot = UserRoot.Target?.Slot;
        bool hasRoot = rootSlot != null && !rootSlot.IsDestroyed;
        float3 headPos = headSlot.GlobalPosition;
        float3 bodyPos = hasRoot ? rootSlot!.GlobalPosition : headPos;

        // Stance placement uses the same visible body frame that the solver receives. The raw user-root forward can
        // be opposite after view calibration; using that here puts feet on the wrong side while the solver bends in
        // the visual frame. Velocity prediction still comes from root motion below. - xlinka
        float3 rawForward = (hasRoot ? rootSlot!.GlobalRotation : headSlot.GlobalRotation) * float3.Backward;
        float3 rawLocomotionForward = FlattenDir(rawForward);
        float3 proxyHeadForward = FlattenDir(headSlot.GlobalRotation * float3.Backward);
        var visualHead = ViewProxyTarget(headSlot, _viewOffset);
        float3 visualForward = visualHead.Valid
            ? FlattenDir(visualHead.Rotation * float3.Backward)
            : proxyHeadForward;
        float3 forward = visualForward;
        float visualDot = float3.Dot(rawLocomotionForward, visualForward);
        bool flippedGaitForward = visualDot < -0.5f;
        // The gait reads the head's visual forward straight through _viewOffset, which on a 180-authored View
        // reference (the swapped-label fox) points BACKWARD - the same inversion ResolveHeadTarget already
        // strips for the solver via CorrectInvertedView. The body/user-root forward (rawLocomotionForward) is
        // the reliable anchor, so when the gait forward opposes it, flip the gait forward to match. Without
        // this the feet/toes solve ~180 backward while the torso faces correctly. -xlinka
        if (flippedGaitForward)
        {
            visualForward = -visualForward;
            forward = visualForward;
        }

        // Body RIGHT axis. GODOT is right-handed (forward = -Z, up = +Y), so the right vector is Cross(forward, up)
        // = +X. The old Cross(up, forward) gave -X (LEFT) - wrong handedness - which sent the left foot's target to
        // the right side and vice-versa, CROSSING the legs (the "scrungle"). Cross, not floatQ.LookRotation (which
        // returns an inverse). Same as the solver's _curBodyRight = Cross(curFwd, Up). -xlinka
        float3 right = float3.Cross(forward, float3.Up);
        right = right.LengthSquared < 1e-6f ? float3.Right : right.Normalized;

        float groundY = hasRoot ? bodyPos.y : headPos.y - 1.6f;
        float3 center = new float3(bodyPos.x, groundY, bodyPos.z);

        // Step prediction from the BODY's horizontal velocity (real locomotion), not head jitter, so feet land ahead
        // of actual movement instead of twitching to camera noise. Velocity is CLAMPED so a fast teleport/dash can't
        // fling the feet, and its magnitude shrinks the step threshold so the avatar steps sooner when moving fast.
        // Standing => zero velocity => no prediction. (_prevHeadXZ holds the body XZ.) -xlinka
        float3 bodyXZ = new float3(bodyPos.x, 0f, bodyPos.z);
        float3 velocity = (_hasPrevHead && dt > 1e-4f) ? (bodyXZ - _prevHeadXZ) / dt : default;
        _prevHeadXZ = bodyXZ;
        _hasPrevHead = true;
        float maxV = MathF.Max(MaxStepVelocity.Value, 1e-3f);
        float rawSpeed = velocity.Length;
        if (rawSpeed > maxV)
            velocity = velocity / rawSpeed * maxV;
        // Smooth the locomotion velocity before it feeds prediction and thresholds: raw per-frame deltas
        // carry frame-time noise that wobbled mid-step landing targets and made step intents flap.
        float velK = 1f - MathF.Exp(-GaitVelocitySmoothing * dt);
        _smoothedGaitVel = float3.Lerp(_smoothedGaitVel, velocity, velK);
        velocity = _smoothedGaitVel;
        float speed = velocity.Length;
        float speedFrac = System.Math.Clamp(speed / maxV, 0f, 1f);
        float3 predict = velocity * MathF.Max(StepPrediction.Value, 0f);
        float effThreshold = StepThreshold.Value * (1f - 0.9f * speedFrac); // T at rest .. 0.1*T at full speed

        // Idle-life state: stillness fades the idle motions in slowly (nothing should start moving the
        // instant you stop) and out fast; the arm-swing phase advances with DISTANCE walked so the swing
        // rate tracks the stride, and its weight follows the speed. -xlinka
        float idleTarget = speed < 0.08f ? 1f : 0f;
        _idleWeight += (idleTarget - _idleWeight) * (1f - MathF.Exp(-(idleTarget > _idleWeight ? 1.2f : 8f) * dt));
        _armSwingPhase = (_armSwingPhase + speed * dt * 6f) % (MathF.PI * 2f);
        float swingTarget = System.Math.Clamp(speed, 0f, 1f);
        _armSwingWeight += (swingTarget - _armSwingWeight) * (1f - MathF.Exp(-6f * dt));

        // Stance half-width: drive it from the rig's ACTUAL hip-bone spacing (which already carries the avatar fit
        // scale) so the feet land under the hips at the avatar's true width. A fixed world-meter constant splayed a
        // narrow rig's legs - a fox whose upper-leg roots are only a few cm apart was forced to a 24 cm stance, so
        // the legs bowed out to reach. Project the L->R upper-leg vector onto the stance right axis and take half;
        // clamp to a small floor and a sane cap. FootStanceWidth is the fallback when the hips aren't mapped. -xlinka
        //
        // Per-foot lateral side from GEOMETRY, not the bone label. The 180 import flip that normalizes a
        // back-authored model's facing can leave the bone NAMED "LeftFoot" sitting on the body's RIGHT (and
        // vice-versa). Placing each labeled foot's stance purely by label (left = center - right*half) then drags
        // that foot ACROSS the body to the wrong side - the crossed-legs bug. The arms already sign each side by
        // which side of the body its own bone sits on; do the same here so every foot stands on the side its hip
        // bone is really on, for a mirrored OR a correctly-built rig. leftSide/rightSide are +1 (body right) or
        // -1 (body left). -xlinka
        float half = FootStanceWidth.Value;
        float leftSide = -1f, rightSide = 1f;
        var rigForStance = Rig.Target;
        if (rigForStance != null)
        {
            var lHipBone = rigForStance.TryGetBone(BodyNode.LeftUpperLeg);
            var rHipBone = rigForStance.TryGetBone(BodyNode.RightUpperLeg);
            if (lHipBone != null && rHipBone != null)
            {
                float hipWidth = MathF.Abs(float3.Dot(rHipBone.GlobalPosition - lHipBone.GlobalPosition, right));
                if (hipWidth > 1e-4f)
                    half = System.Math.Clamp(hipWidth * 0.5f, 0.02f, FootStanceWidth.Value * 4f);

                // Sign each foot by which side of the hip midpoint its own hip bone sits on along the body-right
                // axis - so the labeled left foot stands wherever the left hip bone actually is.
                float3 hipMid = (lHipBone.GlobalPosition + rHipBone.GlobalPosition) * 0.5f;
                leftSide = float3.Dot(lHipBone.GlobalPosition - hipMid, right) >= 0f ? 1f : -1f;
                rightSide = float3.Dot(rHipBone.GlobalPosition - hipMid, right) >= 0f ? 1f : -1f;
            }
        }

        // Terrain conformance is a CONTINUOUS blend, never a grounded/airborne mode switch: each foot
        // follows the probed surface only while it sits near the ROOT PLANE, easing back to the plane as
        // the body lifts away. The root plane rides jumps and falls rigidly, so there is no classifier
        // to flap and no state to reset - takeoff, flight and landing are one code path. The old binary
        // airborne flag flipped the ground anchor between the floor and the root every other frame at
        // the transitions, snapping the hips band and both plants (the jump flicker). -xlinka
        float footBackOffset = EstimateFootBackOffset(forward);
        float3 backwardRest = -forward * footBackOffset;
        float3 leftDesired = center + backwardRest + right * (leftSide * half) + predict;
        float3 rightDesired = center + backwardRest + right * (rightSide * half) + predict;
        bool leftGroundHit = TryGroundHeight(leftDesired.x, leftDesired.z, groundY, out float leftGroundY, out float3 leftHitNormal);
        bool rightGroundHit = TryGroundHeight(rightDesired.x, rightDesired.z, groundY, out float rightGroundY, out float3 rightHitNormal);

        float conformScale = hasRoot ? MathF.Max(rootSlot!.GlobalScale.y, 0.05f) : 1f;
        float leftConform = leftGroundHit ? GroundConformance(leftGroundY - groundY, conformScale) : 0f;
        float rightConform = rightGroundHit ? GroundConformance(rightGroundY - groundY, conformScale) : 0f;
        leftDesired.y = groundY + (leftGroundY - groundY) * leftConform;
        rightDesired.y = groundY + (rightGroundY - groundY) * rightConform;
        _leftFootGroundNormal = BlendNormal(leftHitNormal, leftConform);
        _rightFootGroundNormal = BlendNormal(rightHitNormal, rightConform);
        floatQ leftDesiredRot = BuildFootRotation(visualForward);
        floatQ rightDesiredRot = leftDesiredRot;

        bool leftTracked = _leftFootNode.Target?.IsEquippedAndActive ?? false;
        bool rightTracked = _rightFootNode.Target?.IsEquippedAndActive ?? false;

        // The hips-band anchor uses the same blended heights, so it moves continuously between the
        // terrain and the root plane instead of teleporting with a mode flag.
        if (!leftTracked && !rightTracked)
        {
            _proceduralGroundY = (leftDesired.y + rightDesired.y) * 0.5f;
            _hasProceduralGroundY = true;
        }
        else if (!leftTracked)
        {
            _proceduralGroundY = leftDesired.y;
            _hasProceduralGroundY = true;
        }
        else if (!rightTracked)
        {
            _proceduralGroundY = rightDesired.y;
            _hasProceduralGroundY = true;
        }
        else
        {
            _hasProceduralGroundY = false;
        }

        // Plant the foot BONE at its authored height above the sole plane (digitigrade hocks sit well
        // above the ground). Applied AFTER the hips-band anchor is taken, which stays at the sole plane. -xlinka
        leftDesired.y += _solver.LeftFootGroundClearance;
        rightDesired.y += _solver.RightFootGroundClearance;

        // Lifted (jump/fall): feet stop stepping and GLIDE toward their stance point under the body,
        // gait state tracking along so stepping resumes seamlessly on touch-down. Safe at the branch
        // boundary: by the time conformance is this low the blended heights already sit on the root
        // plane, so both branches produce the same positions.
        float groundedness = MathF.Max(leftTracked ? 1f : leftConform, rightTracked ? 1f : rightConform);
        if (groundedness < GlideThreshold && !leftTracked && !rightTracked)
        {
            float glideK = 1f - MathF.Exp(-GlideFollow * dt);
            GlideFoot(ref _leftGait, ref _leftFootPlant, leftDesired, leftDesiredRot, glideK, _leftFootProxy.Target);
            GlideFoot(ref _rightGait, ref _rightFootPlant, rightDesired, rightDesiredRot, glideK, _rightFootProxy.Target);
            // Airborne: no standing idle, arms stop pumping.
            _idleWeight += (0f - _idleWeight) * glideK;
            _armSwingWeight += (0f - _armSwingWeight) * glideK;
            _leftStepLift = 0f;
            _rightStepLift = 0f;
            _leftFootRot = _leftGait.PlantedRot;
            _rightFootRot = _rightGait.PlantedRot;
            _leftFootFwd = FootForward(_leftFootRot);
            _rightFootFwd = FootForward(_rightFootRot);
            _hasLeftPlant = true;
            _hasRightPlant = true;

            UpdateBodySettle(default, false, default, false, center, dt);
            _locomotionOffset = new float3(_bodySettleXZ.x, 0f, _bodySettleXZ.z);
            return;
        }

        // Gait scheduling: each planted foot computes how badly it wants to move, then the higher-priority
        // foot starts if the other foot is planted or far enough through its travel. This avoids the old left-first
        // bias and makes turn/stride correction behave like a paired-foot state machine rather than two independent
        // distance checks.
        FootStepIntent leftIntent = leftTracked
            ? new FootStepIntent(false, 0f, 0f, 0f)
            : ComputeFootIntent(in _leftGait, leftDesired, leftDesiredRot, effThreshold, 0.9f);
        FootStepIntent rightIntent = rightTracked
            ? new FootStepIntent(false, 0f, 0f, 0f)
            : ComputeFootIntent(in _rightGait, rightDesired, rightDesiredRot, effThreshold, 1.0f);

        // A leg stretched near full reach MUST step even when the stance metric hasn't tripped, or a
        // fast strafe leaves one leg pinned behind at full extension. GATED on the step actually going
        // somewhere: a rig authored with near-straight legs sits at ~1.0x leg length just STANDING, and
        // ungated this marched both feet in place forever. -xlinka
        bool leftForced = false, rightForced = false;
        if (!leftTracked && !leftIntent.WantsStep
            && float3.Distance(_leftGait.Planted, leftDesired) > effThreshold * 0.5f
            && FootOverstretched(BodyNode.LeftUpperLeg, BodyNode.LeftLowerLeg, BodyNode.LeftFoot, in _leftGait))
        {
            leftIntent = new FootStepIntent(true, 2f, 0f, 0f);
            leftForced = true;
        }
        if (!rightTracked && !rightIntent.WantsStep
            && float3.Distance(_rightGait.Planted, rightDesired) > effThreshold * 0.5f
            && FootOverstretched(BodyNode.RightUpperLeg, BodyNode.RightLowerLeg, BodyNode.RightFoot, in _rightGait))
        {
            rightIntent = new FootStepIntent(true, 2f, 0f, 0f);
            rightForced = true;
        }

        // Never start a step whose path passes through the other foot (legs scissoring on turns and
        // strafes). Big-angle re-steps, overstretch-forced steps AND badly-lagging feet override: on a
        // strafe the trailing foot's path passes the leading foot EVERY step, so an unconditional block
        // starves it forever - the body walks away from the pinned foot and slams back on key release.
        // The distance bypass uses the UNSCALED threshold (the effective one shrinks to ~10% at speed). -xlinka
        float crossRadius = MathF.Max(0.08f, half * 0.9f);
        float relaxAngleLimit = MathF.Max(StepAngleThreshold.Value, 1f);
        float starveDistance = MathF.Max(StepThreshold.Value * 1.5f, 0.05f);
        if (!leftForced && leftIntent.WantsStep && leftIntent.Angle <= relaxAngleLimit
            && leftIntent.Distance <= starveDistance
            && StepPathBlocked(_leftGait.Planted, leftDesired, _rightGait.Planted, crossRadius))
            leftIntent = new FootStepIntent(false, 0f, 0f, 0f);
        if (!rightForced && rightIntent.WantsStep && rightIntent.Angle <= relaxAngleLimit
            && rightIntent.Distance <= starveDistance
            && StepPathBlocked(_rightGait.Planted, rightDesired, _leftGait.Planted, crossRadius))
            rightIntent = new FootStepIntent(false, 0f, 0f, 0f);

        // Small turns rotate the PLANTED feet smoothly toward the new facing (the support foot barely,
        // the free foot faster), so feet track look/strafe changes without waiting for a full re-step.
        ElectSupportFoot();
        if (!leftTracked && !_leftGait.Stepping)
            RelaxPlantedFoot(ref _leftGait, leftDesiredRot, dt, _leftIsSupport);
        if (!rightTracked && !_rightGait.Stepping)
            RelaxPlantedFoot(ref _rightGait, rightDesiredRot, dt, !_leftIsSupport);

        bool leftOpposingReady = !_rightGait.Stepping || _rightGait.Progress >= 0.55f;
        bool rightOpposingReady = !_leftGait.Stepping || _leftGait.Progress >= 0.55f;
        bool leftWins = leftIntent.Priority >= rightIntent.Priority;
        bool leftCanStep = !leftTracked && leftIntent.WantsStep && leftOpposingReady && (leftWins || !rightIntent.WantsStep || !rightOpposingReady);
        bool rightCanStep = !rightTracked && rightIntent.WantsStep && rightOpposingReady && (!leftCanStep && (!leftIntent.WantsStep || !leftWins || !leftOpposingReady));
        floatQ leftGaitRot = leftDesiredRot;
        _leftStepLift = leftTracked ? 0f
            : StepFoot(ref _leftGait, leftDesired, leftDesiredRot, effThreshold, 0.9f, leftCanStep, dt, _leftFootProxy.Target, out _leftFootPlant, out _, out leftGaitRot);
        floatQ rightGaitRot = rightDesiredRot;
        _rightStepLift = rightTracked ? 0f
            : StepFoot(ref _rightGait, rightDesired, rightDesiredRot, effThreshold, 1.0f, rightCanStep, dt, _rightFootProxy.Target, out _rightFootPlant, out _, out rightGaitRot);
        _leftFootRot = leftGaitRot;
        _rightFootRot = rightGaitRot;
        _leftFootFwd = FootForward(_leftFootRot);
        _rightFootFwd = FootForward(_rightFootRot);
        // Procedural locomotion owns a foot only while it isn't tracked; SolveBody re-asserts these plants.
        _hasLeftPlant = !leftTracked;
        _hasRightPlant = !rightTracked;

        // Weight-shift bob: dip the hips slightly while a foot is airborne.
        float lift = MathF.Max(_leftStepLift, _rightStepLift);

        // Horizontal body settle (the missing counterpart to the vertical bob): ease the torso toward the centroid
        // of the planted feet, biased toward the SUPPORT foot, so the hips don't hover dead-centre over a wide
        // stance. Pure cosmetic offset on the hips output - NOT added to the user root or the gait anchor, so it
        // can't feed back into the step trigger (which reads the user root). Gated to procedural/untracked feet:
        // when a foot is hardware-tracked the trackers rule, matching the GroundY/foot-driven gating. -xlinka
        UpdateBodySettle(_hasLeftPlant ? _leftFootPlant : default, _hasLeftPlant,
                         _hasRightPlant ? _rightFootPlant : default, _hasRightPlant,
                         center, dt);

        // Standing weight-shift: a slow lateral drift on top of the settle so an idle stance reads as a
        // body holding its balance, not a statue. Output-only on the hips (like settle/bob), so it can
        // never feed back into the step trigger. Amplitude is a fraction of the stance width (scale-safe). -xlinka
        float sway = 0f;
        if (_idleWeight > 1e-3f && IdleSway.Value > 1e-3f)
        {
            float ph = (float)(World?.Time.TotalTime ?? 0.0) * (MathF.PI * 2f / 7.3f);
            float noise = MathF.Sin(ph) + 0.45f * MathF.Sin(ph * 1.9f + 1.1f);
            sway = noise * half * 0.35f * System.Math.Clamp(IdleSway.Value, 0f, 2f) * _idleWeight;
        }

        _locomotionOffset = new float3(
            _bodySettleXZ.x + right.x * sway,
            -BodyBob.Value * lift,
            _bodySettleXZ.z + right.z * sway);
    }

    // Smooth the horizontal hips settle toward the planted-foot centroid (support-foot biased). The raw target is the
    // offset from the body centre to that centroid scaled by BodySettle; it is SmoothDamp'd so a step transition (a
    // foot lifting / the support foot flipping) eases instead of popping the torso sideways. Runs only when BOTH feet
    // are procedural; otherwise the target is zero and the offset eases back to centre. -xlinka
    private void UpdateBodySettle(float3 leftPlant, bool hasLeft, float3 rightPlant, bool hasRight, float3 center, float dt)
    {
        float settle = System.Math.Clamp(BodySettle.Value, 0f, 1f);
        float smoothTime = MathF.Max(BodySettleSmoothTime.Value, 1e-3f);

        float3 rawTarget;
        // Only settle when BOTH feet are procedural (the floor-pin's "no foot tracked" regime). If either foot is
        // hardware-tracked the trackers own the stance and a horizontal hips nudge would fight a real planted pose,
        // so the settle eases back to centre. -xlinka
        if (!hasLeft || !hasRight || settle <= 1e-4f)
        {
            rawTarget = default;
        }
        else
        {
            // Support foot = the one currently NOT stepping (planted); when both tie, the previous election stands
            // (hysteretic via _leftIsSupport) so the bias doesn't chatter frame to frame.
            ElectSupportFoot();

            // Lean the settle target from the foot midpoint toward the support foot.
            float3 mid = (leftPlant + rightPlant) * 0.5f;
            float bias = System.Math.Clamp(SupportLegBias.Value, 0f, 1f);
            float3 biased = float3.Lerp(mid, _leftIsSupport ? leftPlant : rightPlant, bias);

            // Offset from the body centre to the (biased) centroid, flattened, scaled by the settle fraction.
            float3 delta = biased - center;
            delta.y = 0f;
            rawTarget = delta * settle;

            // The settle is COSMETIC weight shift - cap it near stance scale. Uncapped, a foot left
            // behind (blocked/starved steps during a strafe) dragged the whole body toward it by half
            // the lag, then slammed back when the foot finally stepped. -xlinka
            float cap = MathF.Max(0.15f, FootStanceWidth.Value * 1.5f) * (UserRoot.Target?.Slot?.GlobalScale.y ?? 1f);
            float mag = rawTarget.Length;
            if (mag > cap)
                rawTarget = rawTarget / mag * cap;
        }

        _bodySettleXZ = SmoothDamp(_bodySettleXZ, rawTarget, ref _bodySettleVel, smoothTime, dt);
        _bodySettleXZ.y = 0f;
    }

    // Pick the support foot (the planted one) with hysteresis. Both feet are procedural here, so support = whichever
    // is NOT mid-step; when they tie (both planted or both stepping) the previous election stands so the bias doesn't
    // flicker. -xlinka
    private void ElectSupportFoot()
    {
        bool leftStepping = _leftGait.Stepping;
        bool rightStepping = _rightGait.Stepping;
        if (!leftStepping && rightStepping)
            _leftIsSupport = true;
        else if (!rightStepping && leftStepping)
            _leftIsSupport = false;
        // else: tie (both planted or both stepping) -> keep the previous support foot.
    }

    // Critically-damped follow toward target (standard SmoothDamp spring). velocity is carried between calls so the
    // result eases with continuous velocity - no pop when the target jumps on a step transition. -xlinka
    private static float3 SmoothDamp(float3 current, float3 target, ref float3 velocity, float smoothTime, float dt)
    {
        if (dt <= 1e-5f)
            return current;
        float omega = 2f / smoothTime;
        float x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float3 change = current - target;
        float3 temp = (velocity + change * omega) * dt;
        velocity = (velocity - temp * omega) * exp;
        return target + (change + temp) * exp;
    }

    private float EstimateFootBackOffset(float3 forward)
    {
        var rig = Rig.Target;
        if (rig == null || forward.LengthSquared < 1e-8f)
            return 0f;

        var hips = rig.TryGetBone(BodyNode.Hips);
        var lFoot = rig.TryGetBone(BodyNode.LeftFoot);
        var rFoot = rig.TryGetBone(BodyNode.RightFoot);
        if (hips == null || hips.IsDestroyed)
            return 0f;

        float3 footMid = float3.Zero;
        int count = 0;
        if (lFoot != null && !lFoot.IsDestroyed)
        {
            footMid += lFoot.GlobalPosition;
            count++;
        }
        if (rFoot != null && !rFoot.IsDestroyed)
        {
            footMid += rFoot.GlobalPosition;
            count++;
        }
        if (count == 0)
            return 0f;

        footMid /= count;
        float offset = -float3.Dot(footMid - hips.GlobalPosition, FlattenDir(forward));
        return System.Math.Clamp(offset, 0f, 0.25f);
    }

    // 1 = fully conform the foot to the probed surface, 0 = ignore it and stand on the root plane,
    // smooth in between so no height ever switches. ASYMMETRIC: a floor ABOVE the root plane always
    // conforms (uphill, steps, a crouch-shrunk capsule that momentarily sinks the root) - only a floor
    // falling AWAY below fades out (jump, fall, ledge overhang), the one case where feet should ride
    // the root. The downward fade completes well inside the probe reach, so a probe MISS lands in a
    // region where the blend is already 0 - a hit/miss alternation changes nothing. -xlinka
    private float GroundConformance(float deltaY, float scale)
    {
        if (deltaY >= 0f)
            return 1f;
        // Range in WORLD units: AvatarHeight is model-intrinsic, so a scaled user needs the band
        // scaled with the root or big avatars lose their feet and small ones conform to cliffs.
        float range = MathF.Max(0.35f, AvatarHeight.Value * 0.3f) * scale;
        float t = -deltaY / range;
        if (t <= 0.5f)
            return 1f;
        if (t >= 1f)
            return 0f;
        float s = (t - 0.5f) * 2f;
        return 1f - s * s * (3f - 2f * s);
    }

    private static float3 BlendNormal(float3 hitNormal, float conform)
    {
        float3 n = hitNormal * conform + float3.Up * (1f - conform);
        return n.LengthSquared > 1e-8f ? n.Normalized : float3.Up;
    }

    // Mid-air foot follow: no step arcs, the foot eases toward its stance point and the gait's planted
    // state follows, so stepping resumes on touch-down from wherever the foot actually is - no snap.
    private static void GlideFoot(ref FootGait gait, ref float3 plant, float3 desired, floatQ desiredRot, float k, Slot? proxy)
    {
        if (!gait.Init)
        {
            gait.Planted = desired;
            gait.PlantedRot = desiredRot;
            gait.PlantedFwd = FootForward(desiredRot);
            gait.Init = true;
        }
        else
        {
            if (gait.Stepping)
            {
                // Hand off from wherever the interrupted step arc left the foot last frame.
                gait.Planted = plant;
                gait.Stepping = false;
                gait.Progress = 0f;
            }
            gait.Planted = float3.Lerp(gait.Planted, desired, k);
            gait.PlantedRot = floatQ.Slerp(gait.PlantedRot, desiredRot, k).Normalized;
            gait.PlantedFwd = FootForward(gait.PlantedRot);
        }

        plant = gait.Planted;
        if (proxy != null && !proxy.IsDestroyed)
            WriteFoot(proxy, gait.Planted);
    }

    // Downward physics probe for the floor under a foot, ignoring this avatar's own colliders so the ray doesn't
    // catch its capsule. The bool matters: a miss is not the same as "the floor is at fallbackY", especially while
    // jumping where the root can be above the contact floor. -xlinka
    private bool TryGroundHeight(float x, float z, float fallbackY, out float height, out float3 normal)
    {
        height = fallbackY;
        normal = float3.Up;
        var world = World;
        var userSlot = UserRoot.Target?.Slot;
        if (world == null || userSlot == null)
            return false;

        _footRayExclude.Clear();
        _footRayExclude.Add(userSlot);

        var origin = new float3(x, fallbackY + GroundProbeUp, z);
        var hit = world.Physics.Raycast(origin, new float3(0f, -1f, 0f), _footRayExclude, GroundProbeUp + GroundProbeDown);
        if (!hit.HasValue)
            return false;

        normal = hit.Value.Normal;
        height = hit.Value.Point.y;
        return true;
    }

    // Advance one foot's gait; returns its step lift (0 planted .. 1 peak swing) and outputs the foot's world
    // position + flattened facing. Begins a step (only when canStep) once the foot strays past the distance
    // threshold OR the body turns past the angle threshold. Constant-rate randomized speed + InOut-sine arc; the
    // landing retargets toward the live desired so a step started while moving lands where the body ends up. -xlinka
    // Leg extended near its full reach: distance from the hip to the planted foot against the summed
    // bone lengths (rigid: rotation-only writes keep them bind-true at the live scale).
    private bool FootOverstretched(BodyNode hipNode, BodyNode kneeNode, BodyNode footNode, in FootGait gait)
    {
        if (!gait.Init || gait.Stepping)
            return false;
        var rig = Rig.Target;
        var hip = rig?.TryGetBone(hipNode);
        var knee = rig?.TryGetBone(kneeNode);
        var foot = rig?.TryGetBone(footNode);
        if (hip == null || knee == null || foot == null || hip.IsDestroyed || knee.IsDestroyed || foot.IsDestroyed)
            return false;

        float legLen = float3.Distance(hip.GlobalPosition, knee.GlobalPosition)
                     + float3.Distance(knee.GlobalPosition, foot.GlobalPosition);
        if (legLen < 1e-4f)
            return false;
        return float3.Distance(gait.Planted, hip.GlobalPosition) > legLen * 0.98f;
    }

    // Would the step path pass within radius of the other foot (horizontal segment-point distance)?
    private static bool StepPathBlocked(float3 from, float3 to, float3 obstacle, float radius)
    {
        float3 seg = to - from;
        seg.y = 0f;
        float3 rel = obstacle - from;
        rel.y = 0f;
        float len2 = seg.LengthSquared;
        float t = len2 > 1e-8f ? System.Math.Clamp(float3.Dot(rel, seg) / len2, 0f, 1f) : 0f;
        float3 closest = rel - seg * t;
        return closest.LengthSquared < radius * radius;
    }

    // Rotate a planted foot toward the desired facing once the error exceeds the relax band - the
    // support foot barely moves (weight on it), the free foot follows the body. Keeps feet tracking
    // small turns without the frozen-then-snap of waiting for the angle re-step.
    private static void RelaxPlantedFoot(ref FootGait gait, floatQ desiredRot, float dt, bool isSupport)
    {
        if (!gait.Init)
            return;
        float angle = FlatAngleDeg(FootForward(gait.PlantedRot), FootForward(desiredRot));
        if (angle <= FootRelaxMinAngleDeg)
            return;
        float stepDeg = MathF.Min(FootRelaxSpeedDeg * (isSupport ? 0.25f : 1f) * dt, angle - FootRelaxMinAngleDeg);
        if (stepDeg <= 0f || angle < 1e-3f)
            return;
        gait.PlantedRot = floatQ.Slerp(gait.PlantedRot, desiredRot, stepDeg / angle).Normalized;
        gait.PlantedFwd = FootForward(gait.PlantedRot);
    }

    private FootStepIntent ComputeFootIntent(in FootGait gait, float3 desired, floatQ desiredRot, float threshold, float sideFactor)
    {
        if (!gait.Init || gait.Stepping)
            return new FootStepIntent(false, 0f, 0f, 0f);

        float distance = float3.Distance(gait.Planted, desired);
        float angle = FlatAngleDeg(FootForward(gait.PlantedRot), FootForward(desiredRot));
        float distanceLimit = MathF.Max(threshold * sideFactor, 0.001f);
        float angleLimit = MathF.Max(StepAngleThreshold.Value, 1f);
        bool wants = distance > distanceLimit || angle > angleLimit;
        float priority = distance / distanceLimit + angle / angleLimit;
        return new FootStepIntent(wants, wants ? priority : 0f, distance, angle);
    }

    private float StepFoot(ref FootGait gait, float3 desired, floatQ desiredRot, float threshold, float sideFactor,
        bool canStep, float dt, Slot proxy, out float3 worldPos, out float3 worldFwd, out floatQ worldRot)
    {
        worldPos = desired;
        worldRot = desiredRot;
        worldFwd = FootForward(desiredRot);
        if (proxy == null)
            return 0f;

        if (!gait.Init)
        {
            gait.Planted = desired;
            gait.PlantedRot = desiredRot;
            gait.PlantedFwd = FootForward(desiredRot);
            gait.Init = true;
        }

        if (gait.Stepping)
        {
            // Retarget the landing toward where the body is now, so a step started while moving lands under the
            // body instead of behind it.
            float k = MathF.Min(dt * StepRetarget, 1f);
            gait.StepTo = float3.Lerp(gait.StepTo, desired, k);
            gait.StepToRot = floatQ.Slerp(gait.StepToRot, desiredRot, k).Normalized;
            gait.StepToFwd = FootForward(gait.StepToRot);

            gait.Progress += dt * gait.Speed;
            if (gait.Progress >= 1f)
            {
                gait.Progress = 1f;
                gait.Planted = gait.StepTo;
                gait.PlantedRot = gait.StepToRot;
                gait.PlantedFwd = gait.StepToFwd;
                gait.Stepping = false;
            }
            float t = gait.Progress;
            float ease = InOutSine(t);
            float3 pos = float3.Lerp(gait.StepFrom, gait.StepTo, ease);
            float liftArc = MathF.Sin(t * MathF.PI);
            pos.y += liftArc * StepHeight.Value;                  // vertical arc atop the ground-interpolated height
            worldPos = pos;
            worldRot = floatQ.Slerp(gait.StepFromRot, gait.StepToRot, ease).Normalized;
            // Swing dangle: the toe dips through the arc (positive pitch about the lateral axis lifts the
            // toe, so the dip is negative), reading as a lifted foot instead of a hovering slide.
            float3 dipAxis = float3.Cross(FootForward(worldRot), float3.Up);
            if (dipAxis.LengthSquared > 1e-8f)
                worldRot = (floatQ.AxisAngleRad(dipAxis.Normalized, -SwingToeDip * liftArc) * worldRot).Normalized;
            worldFwd = FootForward(worldRot);
            WriteFoot(proxy, pos);
            return liftArc;
        }

        if (canStep)
        {
            gait.Stepping = true;
            gait.Progress = 0f;
            gait.StepFrom = gait.Planted;
            gait.StepTo = desired;
            gait.StepFromRot = gait.PlantedRot;
            gait.StepToRot = desiredRot;
            gait.StepFromFwd = gait.PlantedFwd;
            gait.StepToFwd = FootForward(desiredRot);
            // Randomized constant step rate so steps don't lockstep.
            gait.Speed = (1f / MathF.Max(StepDuration.Value, 0.05f)) * (1f + 0.5f * (float)System.Random.Shared.NextDouble());
        }

        worldPos = gait.Planted;
        worldRot = gait.PlantedRot;
        worldFwd = gait.PlantedFwd;
        WriteFoot(proxy, gait.Planted);
        return 0f;
    }

    private static floatQ BuildFootRotation(float3 forward)
    {
        forward = FlattenDir(forward);
        floatQ q1 = FabrikSolver.FromToRotation(float3.Backward, forward);
        float3 curUp = q1 * float3.Up;
        float3 upProj = float3.Up - forward * float3.Dot(float3.Up, forward);
        if (upProj.LengthSquared < 1e-6f)
            return q1.Normalized;
        floatQ q2 = FabrikSolver.FromToRotation(curUp, upProj.Normalized);
        return (q2 * q1).Normalized;
    }

    private static float3 FootForward(floatQ rotation)
        => FlattenDir(rotation * float3.Backward);

    private static float InOutSine(float t)
        => 0.5f * (1f - MathF.Cos(System.Math.Clamp(t, 0f, 1f) * MathF.PI));

    // Angle (degrees) between two near-horizontal direction vectors.
    private static float FlatAngleDeg(float3 a, float3 b)
        => MathF.Acos(System.Math.Clamp(float3.Dot(FlattenDir(a), FlattenDir(b)), -1f, 1f)) * (180f / MathF.PI);

    // Flatten a direction onto the ground plane and normalize (Backward fallback when degenerate).
    private static float3 FlattenDir(float3 v)
    {
        v.y = 0f;
        return v.LengthSquared > 1e-8f ? v.Normalized : float3.Backward;
    }

    private static void WriteFoot(Slot proxy, float3 worldPos)
    {
        var parent = proxy.Parent;
        var local = parent != null ? parent.GlobalPointToLocal(worldPos) : worldPos;
        if ((proxy.LocalPosition.Value - local).LengthSquared > 1e-10f)
            proxy.LocalPosition.SetValueSilently(local, change: true);
    }

    public void LogDiagnosticInfo()
    {
        LumoraLogger.Log("AvatarIK Diagnostic:");
        LumoraLogger.Log($"  IsInitialized:       {_isInitialized}");
        LumoraLogger.Log($"  IsEquipped:          {IsEquipped}");
        LumoraLogger.Log($"  Skeleton:            {Skeleton.Target?.Slot.SlotName.Value ?? "null"}");
        LumoraLogger.Log($"  Rig bones:           {Rig.Target?.Bones.Count ?? 0}");
        LumoraLogger.Log($"  UserRoot:            {UserRoot.Target?.Slot.SlotName.Value ?? "null"}");
        LumoraLogger.Log($"  HeadProxy:           {_headProxy.Target?.SlotName.Value ?? "null"}");
        LumoraLogger.Log($"  HeadNode equipped:   {_headNode.Target?.IsEquipped ?? false}");
        LumoraLogger.Log($"  LHandNode equipped:  {_leftHandNode.Target?.IsEquipped ?? false}");
        LumoraLogger.Log($"  RHandNode equipped:  {_rightHandNode.Target?.IsEquipped ?? false}");
        LumoraLogger.Log($"  LFootNode equipped:  {_leftFootNode.Target?.IsEquipped ?? false}");
        LumoraLogger.Log($"  RFootNode equipped:  {_rightFootNode.Target?.IsEquipped ?? false}");
    }
}
