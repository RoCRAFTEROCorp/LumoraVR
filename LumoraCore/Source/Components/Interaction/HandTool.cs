// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

[ComponentCategory("Interaction")]
[DefaultUpdateOrder(-1000)]
public sealed class HandTool : Tool
{
    public enum LaserRotationMode
    {
        AxisX,
        AxisY,
        AxisZ,
        Unconstrained
    }

    public readonly Sync<float> HoldScrollStep = new();
    public readonly Sync<float> HoldScaleStep = new();
    public readonly Sync<float> HoldRotationSensitivity = new();
    public readonly Sync<float> GrabSmoothing = new();
    public readonly Sync<LaserRotationMode> RotationMode = new();
    public readonly SyncRef<ToolItem> ActiveToolItem = new();

    // DESKTOP TOOL HOLD
    //
    // Outside VR nothing tracks a hand, so the controller body node keeps the pose the rig authored -
    // down at the hip - and AvatarIK sees an untracked hand and drops its arm to rest weight. The tool
    // ends up hanging at the waist behind the camera and the avatar never lifts an arm to it.
    //
    // The source platform does not leave the desktop hand untracked at all: it SIMULATES it. While a tool
    // is equipped the hand node is driven to a fixed spot relative to the head - out to the side, below
    // the eye line, in front - and reported as tracked, so its IK solves to a real target like any other.
    //
    // Same model here, at the one slot that makes everything else follow for free. The controller body
    // node is the parent of the hand socket, this tool rig and the tool holder, so moving it carries the
    // tool, the beam origin and the avatar's hand target together. Only the OWNER writes it (a remote peer
    // reads the pose off the node's transform stream); the intent replicates as HoldToolInView so every
    // peer runs the same blend and AvatarIK can weight the arm by HoldWeight on both ends. -xlinka
    public readonly Sync<bool> HoldToolInView = new();

    private Slot? _grabberSlot;
    private Slot? _laserSlot;
    private Slot? _toolHolderSlot;
    private Grabber? _grabber;
    private InteractionLaser? _laser;

    // AVATAR TOOL ANCHORS
    //
    // The source platform lets an avatar say where a hand's tool and grab area sit: an anchor component
    // in the avatar's hand object, and on equip the interaction handler re-parents its tool root onto the
    // tool anchor and its grabber onto the grab anchor (keepGlobalTransform false, so each takes the
    // anchor's pose), then back onto its own slot on dequip. Ours are the {Left,Right}HandToolAnchor and
    // {Left,Right}HandGrabAnchor reference points Avatar Studio bakes.
    //
    // NOT a re-parent here, for a reason that took verifying: our reference points are STATIC avatar-local
    // poses under "AvatarReferences" (AvatarStudio.PlaceReferenceFromMarker, AvatarCalibration.Place),
    // the same class of data as the grip reference, which AvatarIK turns into a bone OFFSET and never
    // parents anything to. Nothing moves them with the hand, so parenting Tool Holder onto one would pin
    // the tool to the T-pose hand spot in the avatar's root frame. Moving the rig slots out of this slot
    // would also break ToolSnapper.OnToolTaken and TransformHandle.ResolveLaser, which walk UP from the
    // grabber to find this component, and a remote peer's EnsureRig, which adopts the rig by child name.
    //
    // So the same model is expressed the way our references are consumed: the anchor's pose RELATIVE TO
    // THE GRIP reference is the pose the rig slot takes relative to the hand socket, because the IK
    // places the hand bone so that the grip reference lands exactly on the hand socket
    // (AvatarIK.TargetFromPose). Tool Holder and Grabber stay children of this slot; only their local pose
    // changes, which is all the source's re-parent changes in the end. Restored to identity on dequip -
    // the pose EnsureRig built them with - so an avatar without anchors behaves exactly as before.
    //
    // Recomputed per frame (cheap: a handful of transforms, no tree walk) and written change-gated, since
    // the avatar can be rescaled against the user after equip and the world-metre offset has to follow;
    // the tree walk that finds the points runs only when the worn avatar changes or a point dies. -xlinka
    private Slot? _anchorAvatar;
    private Chirality _anchorSide;
    private Slot? _gripReference;
    private Slot? _toolAnchorReference;
    private Slot? _grabAnchorReference;
    private bool _toolAnchorApplied;
    private bool _grabAnchorApplied;

    // HELD-TOOL HAND FRAME
    //
    // The hold pose used to swing the controller node's authored rest onto the aim and leave the hand
    // bone to follow. That only reads right if the bone hangs off the node with its fingers down the
    // node's -Z and the back of its hand up the node's +Y, and nothing guarantees either: Chiki's right
    // hand bone (raw-aligned to the node, see below) keeps its fingers along -X and its back along +Z,
    // so the swing put the fingers sideways and the palm square at the camera - the "stop" hand in
    // the front screenshot.
    //
    // So the hold is built the other way round: decide where the BONE should be - fingers along the
    // aim, back of the hand outward and a little up, palm in toward the body, the way the source
    // platform's simulated hand rests (fingers down, back outward) and then swings to the aim - and
    // derive the node from that through whatever attachment AvatarIK will use. That attachment is one
    // of two: an avatar whose own hand object took the socket gets its bone aligned to the socket RAW
    // (AvatarIK.TryTargetFromSocket), and an avatar whose socket holds AvatarIK's pose node gets its
    // bone placed so the GRIP reference lands on the socket (TargetFromPose, grip = -Z down the forearm,
    // +Y out of the palm as AvatarCalibration builds it). Both are expressed here as "the socket frame
    // in bone space" and multiplied back out.
    //
    // The bone's own finger and back axes are measured off the rig once per worn avatar: wrist to
    // middle proximal is the finger axis (metacarpals are never driven, so it is pose-invariant), and
    // the thumb gives the back by the per-side cross rule HandPoseDriver documents. -xlinka
    private Slot? _handBone;
    private AvatarSocket? _handSocket;
    private HandPoseDriver? _fingerDriver;
    private float3 _boneFingerAxis;
    private float3 _boneBackAxis;
    // The grip reference's frame in bone space (-Z down the fingers, +Y out of the palm, the way
    // AvatarCalibration builds it), rebuilt from the measured axes. Off by the few degrees between the
    // forearm line the calibration used and the finger line measured here; the beam does not go
    // through it and a hand can not show that much.
    private floatQ _gripInBone = floatQ.Identity;
    private bool _handFrameValid;
    private double _nextHandFrameRetry = double.NegativeInfinity;
    private bool _holdViaGrip;
    private float3 _lastToolVisualWorld;
    private bool _hasToolVisualWorld;

    private bool _primaryHeld;
    private bool _prevPrimaryHeld;
    private bool _secondaryHeld;
    private bool _prevSecondaryHeld;
    private bool _gripHeld;
    private bool _prevGripHeld;
    // Handle drag started by a bare-hand primary press (no tool equipped); the dev tool tracks its own.
    private Gizmos.TransformHandle? _bareHandle;
    private ToolItem? _activePrimaryToolItem;
    private ToolItem? _activeSecondaryToolItem;
    private bool _isHoldingWithLaser;
    private float _laserGrabDistance;
    private float _holderAxisOffset;
    private floatQ _holderRotationOffset = floatQ.Identity;
    private floatQ? _holderRotationReference;
    private bool _desktopInputSuppressed;
    private bool _scrollWheelCaptured;
    private double _lastAlignPress = -1000.0;
    private long _holdPoseFrame = long.MinValue;
    private long _holdPoseWrittenFrame = long.MinValue;

    // Desktop tool hold state. See HoldToolInView.
    private float _holdWeight;
    private float3 _restLocalPosition;
    private floatQ _restLocalRotation = floatQ.Identity;
    private bool _hasRestPose;
    private bool _wroteHoldPose;
    private floatQ _holdAimRotation = floatQ.Identity;
    private bool _holdAimValid;
    private Slot? _bodyNodeSlot;
    private TransformStreamDriver? _bodyNodeStream;
    private bool _bodyNodeStreamChecked;
    private bool _holdWanted;
    private float _ikHandWeight = -1f;
    private HoldLogState _holdLogState;
    private double _holdLogAt = double.NegativeInfinity;
    private bool _holdLogPending;

    // Head-relative hold offset in user-root metres: out to the side, below the eye line, in front. The
    // numbers are the source platform's desktop hand override, with its +Z-forward Z flipped to ours
    // (float3.Backward is the way a slot points here). -xlinka
    private static readonly float3 HoldOffset = new float3(0.25f, -0.22f, -0.20f);

    // 1/s ease between rest and hold. Roughly a fifth of a second to settle, so equipping reads as the arm
    // coming up rather than the tool teleporting into view.
    private const float HoldBlendRate = 12f;

    // 1/s ease on the held tool's AIM. The beam's end point jumps the moment the hover crosses an object
    // edge (half a metre away, then the skybox), and the tool convergence swings with it, so taking the raw
    // direction snaps the tool round on every sweep. Matches the rate the desktop hand aim already uses.
    private const float HoldAimRate = 10f;

    // 0..1: how far this hand is into its desktop tool hold. AvatarIK multiplies the arm's IK weight by it,
    // so an untracked desktop hand gets a real target exactly while one is being driven. Zero in VR.
    public float HoldWeight => _holdWeight;

    // The hold this hand is ASKING for, before the ease. Separate from HoldWeight so a caller can tell
    // "not holding" apart from "holding, still on the way up".
    public bool WantsToolHold => _holdWanted;

    // AvatarIK pushes back the weight it actually handed this hand's IK target, so one log line can carry
    // both ends of the chain. Pulling it the other way would mean this component walking an avatar tree for
    // a solver it otherwise knows nothing about. -xlinka
    internal void ReportIKHandWeight(float weight) => _ikHandWeight = weight;

    public override Grabber? Grabber => _grabber;
    public override InteractionLaser? Laser => _laser;
    public override bool PrimaryHeld => _primaryHeld;
    public override bool SecondaryHeld => _secondaryHeld;
    public override bool GripHeld => _gripHeld;
    public bool IsHoldingObjects => _grabber?.IsHoldingObjects == true;
    public bool IsHoldingObjectsWithLaser => _isHoldingWithLaser && IsHoldingObjects;

    public override void OnInit()
    {
        base.OnInit();
        HoldScrollStep.Value = 0.12f;
        HoldScaleStep.Value = 0.10f;
        HoldRotationSensitivity.Value = MathF.PI * 2f;
        // Base damping rate for the laser's aim while this hand is carrying something, in 1/s (a 1/8 second
        // time constant). Below the laser's bare-pointer SmoothSpeed because a load on the end of a
        // several-metre ray magnifies every bit of sampling noise into a visible swing. Sized so a stop
        // settles inside 0.2s with no overshoot (first order, so it can't overshoot) while a 90 degree sweep
        // in a quarter second ends about 10 degrees behind and closes that in another 0.2s. -xlinka
        GrabSmoothing.Value = 8f;
        RotationMode.Value = LaserRotationMode.AxisY;
    }

    public override void OnStart()
    {
        base.OnStart();
        EnsureRig();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        EnsureRig();

        // Ahead of the dash stand-down below on purpose: the hold is a body pose, not an interaction, and
        // bailing out of it while the dash is up would freeze the arm halfway wherever the dash caught it.
        UpdateToolHold(delta);
        // Same reason: where the tool sits IN the hand is avatar shape, not interaction. The two compose -
        // the hold moves the controller node, the anchor moves the rig slots under it.
        UpdateAvatarAnchors();

        if (_laser == null)
        {
            return;
        }

        // While the userspace dash pointer owns the cursor (dash open, desktop or VR), this in-world
        // tool stands down completely: no second cursor floating in the world behind the dash, and
        // no presses bleeding through the panel into whatever is behind it. The userspace pointer
        // rig raises this flag while it is live (it is the thing actually pointing at the dash, on a
        // controller-tracked hand in VR or the free cursor on desktop), so we just back off. -xlinka
        var dashOwner = Engine.Current?.InputInterface;
        if (dashOwner != null && dashOwner.IsAnyUserspaceLaserActive)
        {
            _laser.SetToolState(false, false);
            _laser.ArmRaySmoothing(false);
            _laser.SetExclusiveRoot(null);
            _laser.SetDormant(true);
            SetDesktopInputSuppression(false);
            SetScrollWheelCapture(false);
            return;
        }
        _laser.SetDormant(false);

        SampleInput(_laser);
        // In VR the laser stays visible while this hand's menu is open so the
        // user can see what they're aiming at. On desktop the menu owns a
        // mouse-driven pointer instead, so the laser goes fully inactive to
        // keep its frozen center aim from pressing menu items.
        var inputInterface = Engine.Current?.InputInterface;
        bool vrActive = inputInterface?.IsVRActive == true;
        bool menuVisible = IsContextMenuOpenByThisHand();
        bool desktopMenuOpen = menuVisible && !vrActive;
        // While the desktop menu is open the camera is frozen and the mouse
        // deflects the laser instead - the laser cursor IS the pointer. Press
        // state stays the real primary. (The dash uses the free-cursor ray the
        // platform pushes, not this deflection.)
        UpdateDesktopMenuAim(desktopMenuOpen);
        // Modal pointer targets: while our menu is open it is the only thing
        // this laser can touch; while the desktop dash is open, the dash surface
        // is - no click-through into the world behind it. Primary presses must
        // reach the target even mid-grab (otherwise the held-object actions
        // could never be clicked - holding normally suppresses canvas presses).
        Slot? exclusiveRoot = null;
        if (menuVisible)
            exclusiveRoot = _contextMenu?.VisualRoot;
        else if (!vrActive && inputInterface?.IsDashboardOpen == true)
            exclusiveRoot = UI.UserspaceDashboard.LocalInstance?.SurfaceSlot;
        _laser.SetExclusiveRoot(exclusiveRoot);
        bool uiPress = _primaryHeld && (menuVisible || !IsHoldingObjectsWithLaser)
            && EyedropperFor(_laser) == null;
        bool carryingOnLaser = IsHoldingObjectsWithLaser && !menuVisible;
        // Damp the aim only while something is actually riding the laser. A bare pointer wants to be
        // pixel-exact, and while the menu is open the mouse IS the pointer.
        _laser.ArmRaySmoothing(carryingOnLaser, GrabSmoothing.Value);
        _laser.SetToolState(uiPress, carryingOnLaser);
        // Hit classification belongs to whatever is equipped: the tool is the only thing that knows
        // which chrome it wants pulled in front of the world. A modal menu takes that away - while one
        // is up the beam may touch nothing but the menu, and a tool preferring its own targets through
        // the modal filter would be fighting it. Pushed per frame rather than on equip because the
        // beam is built lazily and an equip can land before it exists. -xlinka
        _laser.SetHitClassifier(menuVisible ? null : ActiveToolItem.Target as ILaserHitClassifier);
        _laser.RefreshNow(delta);
        ProcessPrimary(_laser);
        ProcessSecondary(_laser);
        ProcessMenuKey(_laser);
        ProcessGrip(_laser);

        // The wheel is the held object's distance control while something rides the laser. Say so, or the
        // third-person orbit zooms the camera on the same notch that pulls the object in. -xlinka
        SetScrollWheelCapture(!vrActive && IsHoldingObjectsWithLaser && !menuVisible);

        if (IsHoldingObjectsWithLaser && !menuVisible)
        {
            ProcessLaserHold(_laser, delta);
        }
        else
        {
            SetDesktopInputSuppression(false);
        }

        ProcessToolShortcuts(vrActive, menuVisible);
    }

    // Offered every frame by whatever normally parks this hand on desktop (LocomotionController, which
    // writes the controller body node's rest position and camera-pitched aim). While a tool is held in view
    // this component owns that node instead, so the offer becomes the blend basis rather than a write:
    // two components writing one transform field alternate frame by frame and the hand jitters between
    // them. Returns TRUE when the offer was taken, meaning the caller must not write the node itself.
    //
    // Taking the rest pose live rather than snapshotting it at equip matters on the way back down: the
    // right hand's rest rotation pitches with the camera, so a frozen snapshot would land the arm at
    // whatever pitch it was equipped at. -xlinka
    public bool OfferRestPose(in float3 localPosition, in floatQ localRotation)
    {
        _restLocalPosition = localPosition;
        _restLocalRotation = localRotation;
        _hasRestPose = true;
        return _wroteHoldPose || _holdWeight > 0f;
    }

    // Drive the controller body node toward the desktop tool-hold pose (or back to the rig's authored rest
    // when nothing is equipped). See HoldToolInView for why this lives on the body node rather than on the
    // avatar's hand target. -xlinka
    private void UpdateToolHold(float delta)
    {
        var node = ResolveBodyNodeSlot();
        if (node == null)
        {
            LogToolHold(null, "no-body-node");
            return;
        }

        bool vrActive = Engine.Current?.InputInterface?.IsVRActive == true;

        // Only the owner decides. HoldToolInView replicates, so a remote peer runs the identical blend
        // against the pose already arriving on this node's transform stream.
        //
        // The owner blends off its OWN decision, never off the value it just wrote. A synced field write
        // can be refused - a driven field bails out of BeginModification, and the data-model gate denies
        // foreign writes for the window where the User<->UserRoot ownership link is still settling after a
        // join - and both refusals are near-silent. Reading the flag back as the blend input would turn
        // either one into "the arm just never comes up", with nothing in the log tying it to the write.
        // -xlinka
        bool localOwner = IsUnderLocalUser;
        if (localOwner)
        {
            _holdWanted = !vrActive && ActiveToolItem.Target != null;
            if (HoldToolInView.Value != _holdWanted)
            {
                HoldToolInView.Value = _holdWanted;
            }
        }
        else
        {
            _holdWanted = HoldToolInView.Value;
        }

        // Capture rest while we are actually AT rest, so the pose we ease back to is whatever the rig
        // authored (or, after a VR->desktop switch, wherever tracking last left the node) rather than a
        // constant baked in here. Once a hold write lands this stops, or the hold would capture itself.
        if (!vrActive && _holdWeight <= 0f)
        {
            _restLocalPosition = node.LocalPosition.Value;
            _restLocalRotation = node.LocalRotation.Value;
            _hasRestPose = true;
        }

        float target = _holdWanted ? 1f : 0f;
        float t = System.Math.Clamp(1f - MathF.Exp(-HoldBlendRate * MathF.Max(delta, 0f)), 0f, 1f);
        _holdWeight += (target - _holdWeight) * t;
        if (_holdWeight < 0.001f)
        {
            _holdWeight = 0f;
        }
        else if (_holdWeight > 0.999f)
        {
            _holdWeight = 1f;
        }

        // A remote peer's node is driven by its owner's transform stream; a second writer here would
        // fight it. It still runs the blend above so AvatarIK can weight that user's arm.
        if (!localOwner || !_hasRestPose)
        {
            LogToolHold(node, !localOwner ? "remote" : "no-rest-pose");
            return;
        }

        if (_holdWeight <= 0f)
        {
            // Hand the node back exactly once. Leaving it parked would strand the arm up after a dequip,
            // and re-writing rest every frame would fight anything else that ever drives this node.
            if (_wroteHoldPose)
            {
                WriteBodyNode(node, _restLocalPosition, _restLocalRotation);
                _wroteHoldPose = false;
            }
            _holdAimValid = false;
            LogToolHold(node, "released");
            return;
        }

        if (!TryComputeHoldPose(node, out float3 holdPosition, out floatQ holdRotation))
        {
            LogToolHold(node, "no-hold-pose");
            return;
        }

        // Ease the aim, but snap to it on the first frame of a hold so the arm does not start out pointing
        // wherever the last hold left off.
        float aimT = _holdAimValid
            ? System.Math.Clamp(1f - MathF.Exp(-HoldAimRate * MathF.Max(delta, 0f)), 0f, 1f)
            : 1f;
        _holdAimRotation = floatQ.Slerp(_holdAimRotation, holdRotation, aimT).Normalized;
        _holdAimValid = true;
        holdRotation = _holdAimRotation;

        WriteBodyNode(node,
            float3.Lerp(_restLocalPosition, holdPosition, _holdWeight),
            floatQ.Slerp(_restLocalRotation, holdRotation, _holdWeight));
        _wroteHoldPose = true;
        LogToolHold(node, "holding");
    }

    // TOOL-HOLD TRACE
    //
    // Everything the desktop hold depends on sits in a different component from everything that consumes
    // it, and every link in the chain fails QUIETLY: a synced write refused by a drive or by the data-model
    // gate, a body node that never resolved, a pose the head/user-root could not be built in, a solver that
    // never found this tool. From outside, all of those look identical - an arm hanging at the hip.
    //
    // So print the whole chain in one line: what the hand asked for, what replicated, what the ease is at,
    // where the node actually ended up, whether the field would even accept a write, and what weight the
    // solver handed the arm at the far end. Change-gated (a settled hold says nothing) with a floor between
    // lines, so a stuck state costs one line every two seconds and a working equip costs about four.
    //
    // Reading it: ikHandWeight stays at -1 until an AvatarIK actually reports, so -1 alongside a healthy
    // holdWeight means the solver never found this tool rather than that it weighted the arm to nothing.
    // -xlinka
    private const double HoldLogInterval = 2.0;

    // Compared field by field rather than through a formatted string: this runs every frame on both hands
    // and the string only ever gets built on the frames that actually print. Floats are quantized to a
    // hundredth so a hold breathing on its last decimal does not read as a change. -xlinka
    private readonly struct HoldLogState : IEquatable<HoldLogState>
    {
        private readonly string _outcome;
        private readonly System.Type? _item;
        private readonly int _hold;
        private readonly int _ik;
        private readonly int _x;
        private readonly int _y;
        private readonly int _z;
        private readonly bool _wanted;
        private readonly bool _synced;
        private readonly bool _driven;
        private readonly int _finger;
        private readonly int _grip;
        private readonly int _attach;
        private readonly int _visualDistance;

        public HoldLogState(string outcome, System.Type? item, float hold, float ik, float3 local,
            bool wanted, bool synced, bool driven, int finger, float grip, int attach, float visualDistance)
        {
            _outcome = outcome;
            _item = item;
            _hold = (int)MathF.Round(hold * 100f);
            _ik = (int)MathF.Round(ik * 100f);
            _x = (int)MathF.Round(local.x * 100f);
            _y = (int)MathF.Round(local.y * 100f);
            _z = (int)MathF.Round(local.z * 100f);
            _wanted = wanted;
            _synced = synced;
            _driven = driven;
            _finger = finger;
            _grip = (int)MathF.Round(grip * 100f);
            _attach = attach;
            _visualDistance = (int)MathF.Round(visualDistance * 100f);
        }

        public bool Equals(HoldLogState other)
            => ReferenceEquals(_outcome, other._outcome) && ReferenceEquals(_item, other._item)
               && _hold == other._hold && _ik == other._ik
               && _x == other._x && _y == other._y && _z == other._z
               && _wanted == other._wanted && _synced == other._synced && _driven == other._driven
               && _finger == other._finger && _grip == other._grip && _attach == other._attach
               && _visualDistance == other._visualDistance;

        public override bool Equals(object? obj) => obj is HoldLogState other && Equals(other);
        public override int GetHashCode() => System.HashCode.Combine(_outcome, _item, _hold, _ik, _x, _y, _wanted);
    }

    private void LogToolHold(Slot? node, string outcome)
    {
        bool alive = node != null && !node.IsDestroyed;
        bool driven = alive && node!.LocalPosition.IsDriven;
        float3 local = alive ? node!.LocalPosition.Value : float3.Zero;
        var item = ActiveToolItem.Target;

        // Finger side of the chain: which source is shaping the hand and how far the grip is in. -1 means
        // no poser was found on the worn avatar's hand bone at all. The poser is attached by the equip
        // path, which can land after this tool first saw the avatar, so a miss is re-asked here.
        if ((_fingerDriver == null || _fingerDriver.IsDestroyed) && _handBone != null && !_handBone.IsDestroyed)
        {
            _fingerDriver = _handBone.GetComponent<HandPoseDriver>();
        }
        var driver = _fingerDriver;
        bool driverAlive = driver != null && !driver.IsDestroyed;
        int finger = driverAlive ? (int)driver!.CurrentState : -1;
        float grip = driverAlive ? driver!.GripWeight : 0f;

        // 0 = no measured hand frame (rest swing), 1 = raw bone-to-socket, 2 = through the grip reference.
        int attach = !_handFrameValid ? 0 : (_holdViaGrip ? 2 : 1);

        // Where the tool's visual actually is against the hand bone, in world metres. The number that
        // says whether "nothing rendered" is a missing visual or one sitting a forearm away.
        float visualDistance = -1f;
        _hasToolVisualWorld = false;
        var visual = item?.HeldVisual ?? item?.Slot;
        if (visual != null && !visual.IsDestroyed)
        {
            _lastToolVisualWorld = visual.GlobalPosition;
            _hasToolVisualWorld = true;
            if (_handBone != null && !_handBone.IsDestroyed)
            {
                visualDistance = float3.Distance(_lastToolVisualWorld, _handBone.GlobalPosition);
            }
        }

        var state = new HoldLogState(outcome, item?.GetType(), _holdWeight, _ikHandWeight, local,
            _holdWanted, HoldToolInView.Value, driven, finger, grip, attach, visualDistance);
        if (!state.Equals(_holdLogState))
        {
            _holdLogState = state;
            _holdLogPending = true;
        }
        if (!_holdLogPending)
        {
            return;
        }

        double now = World?.Time.TotalTime ?? 0.0;
        if (now - _holdLogAt < HoldLogInterval)
        {
            return;
        }
        _holdLogAt = now;
        _holdLogPending = false;

        string fingerText = driverAlive ? $"{driver!.CurrentState}/{grip:F2}" : "<no-poser>";
        string attachText = attach switch { 1 => "raw", 2 => "grip", _ => "rest-swing" };
        string visualText = _hasToolVisualWorld
            ? $"{_lastToolVisualWorld} dHand={(visualDistance >= 0f ? visualDistance.ToString("F3") : "<no-bone>")}"
            : "<none>";
        string boneText = _handBone != null && !_handBone.IsDestroyed ? _handBone.GlobalPosition.ToString() : "<none>";

        Logging.Logger.Log(
            $"TOOLHOLD: side={Side.Value} outcome={outcome} wants={_holdWanted} synced={HoldToolInView.Value} "
            + $"item={item?.GetType().Name ?? "<null>"} holdWeight={_holdWeight:F3} ikHandWeight={_ikHandWeight:F3} "
            + $"nodeDriven={driven} nodeLocal={local} vr={Engine.Current?.InputInterface?.IsVRActive == true} "
            + $"localOwner={IsUnderLocalUser} fingers={fingerText} attach={attachText} boneW={boneText} "
            + $"toolVisualW={visualText}");
    }

    // The controller body node this rig hangs off, or null when this tool is not on one. The check is
    // structural rather than a name match: the userspace pointer rig also has LeftController/RightController
    // slots, and a tool somebody parented by hand has no body node at all - neither should have its parent
    // moved out from under it. -xlinka
    private Slot? ResolveBodyNodeSlot()
    {
        if (_bodyNodeSlot != null && !_bodyNodeSlot.IsDestroyed)
        {
            return _bodyNodeSlot;
        }

        _bodyNodeSlot = null;
        _bodyNodeStream = null;
        _bodyNodeStreamChecked = false;

        var parent = Slot?.Parent;
        if (parent == null || parent.IsDestroyed)
        {
            return null;
        }

        var node = parent.GetComponent<TrackedDevicePositioner>()?.AutoBodyNode.Value;
        if (node != BodyNode.LeftController && node != BodyNode.RightController)
        {
            return null;
        }

        _bodyNodeSlot = parent;
        return _bodyNodeSlot;
    }

    // Where the hand should sit and which way it should point while holding a tool, in the body node's
    // parent space (the space its rest pose is already in, so the two can be blended directly).
    private bool TryComputeHoldPose(Slot node, out float3 localPosition, out floatQ localRotation)
    {
        localPosition = _restLocalPosition;
        localRotation = _restLocalRotation;

        var root = FindUserRootSlot();
        var head = Slot?.ActiveUserRoot?.HeadSlot;
        var parent = node.Parent;
        if (root == null || root.IsDestroyed || head == null || head.IsDestroyed
            || parent == null || parent.IsDestroyed)
        {
            return false;
        }

        // Placed in USER-ROOT space so the offset scales with the user: a shrunk user holds the tool a
        // proportionally shorter way from their eye, not at a fixed 20cm that would put it through them.
        float sign = Side.Value == Chirality.Left ? -1f : 1f;
        float3 headLocal = root.GlobalPointToLocal(head.GlobalPosition);
        floatQ headLocalRotation = root.GlobalRotationToLocal(head.GlobalRotation);
        float3 holdLocal = headLocal
            + headLocalRotation * new float3(HoldOffset.x * sign, HoldOffset.y, HoldOffset.z);
        float3 holdGlobal = root.LocalPointToGlobal(holdLocal);

        // Aim at what the beam is on, so the tool points where the user is pointing rather than merely
        // parallel to the view; head forward is the fallback while the beam is dormant (dash open) or has
        // never cast. The 5cm floor rejects an aim point that has landed on the hand itself.
        float3 aim = head.GlobalRotation * float3.Backward;
        if (_laser != null && _laser.HasAimPoint)
        {
            float3 toAim = _laser.AimPoint - holdGlobal;
            if (toAim.Length > 0.05f)
            {
                aim = toAim;
            }
        }
        if (aim.Length <= 0.0001f)
        {
            aim = float3.Backward;
        }
        aim = aim.Normalized;

        floatQ holdGlobalRotation;
        var hand = Side.Value == Chirality.Left ? Slot?.ActiveUserRoot?.LeftHandSlot : Slot?.ActiveUserRoot?.RightHandSlot;
        if (_handFrameValid && hand != null && !hand.IsDestroyed && ReferenceEquals(hand.Parent, node))
        {
            holdGlobalRotation = ComputeHoldFromHandFrame(hand, root, head, aim, sign);
        }
        else
        {
            // No measurable hand (no worn avatar, no finger bones): SWING the authored rest orientation
            // onto the aim rather than building a facing from raw axes. The rig decides which way a hand's
            // frame faces at rest, and a rotation composed here from float3.Backward/Up would silently roll
            // the wrist by however far that frame differs from ours. A shortest-arc swing carries the
            // authored roll through untouched. -xlinka
            floatQ restGlobalRotation = parent.LocalRotationToGlobal(_restLocalRotation);
            float3 restAim = restGlobalRotation * float3.Backward;
            holdGlobalRotation = RotationFromTo(restAim, aim) * restGlobalRotation;
            _holdViaGrip = false;
        }

        localPosition = parent.GlobalPointToLocal(holdGlobal);
        localRotation = parent.GlobalRotationToLocal(holdGlobalRotation);
        return true;
    }

    // How far the back of the held hand tips up from straight-outward. A tool pointed forward at chest
    // height is held a little pronated: palm in and slightly down, thumb up and a bit in. Twenty degrees
    // reads as a relaxed pointer grip rather than a karate-chop (0) or a palm-down grab (90). -xlinka
    private const float HoldBackTiltRadians = 20f * MathF.PI / 180f;

    // The node rotation that leaves the hand BONE with its fingers down `aim` and the back of its hand
    // outward. See the HELD-TOOL HAND FRAME block. -xlinka
    private floatQ ComputeHoldFromHandFrame(Slot hand, Slot root, Slot head, float3 aim, float sign)
    {
        // Outward is the head's right for the right hand and its left for the left; up is the user's up
        // so a pitched camera does not roll the wrist.
        float3 lateral = head.GlobalRotation * float3.Right * sign;
        float3 up = root.GlobalRotation * float3.Up;
        float3 backTarget = lateral * MathF.Cos(HoldBackTiltRadians) + up * MathF.Sin(HoldBackTiltRadians);
        backTarget -= aim * float3.Dot(backTarget, aim);
        if (backTarget.LengthSquared < 1e-4f)
        {
            // Pointing straight along the outward axis (arm out to the side): keep the back of the hand up.
            backTarget = up - aim * float3.Dot(up, aim);
            if (backTarget.LengthSquared < 1e-4f)
                backTarget = lateral;
        }
        backTarget = backTarget.Normalized;

        // Desired bone rotation: swing the finger axis onto the aim, then roll ABOUT THE AIM until the
        // back of the hand faces backTarget. The roll is an explicit signed angle about the aim rather
        // than a second shortest-arc: both vectors are perpendicular to the aim, so a shortest-arc would
        // pick the aim as its axis anyway except in the antiparallel case, where it picks any axis at all
        // and pulls the fingers off the aim.
        floatQ swing = RotationFromTo(_boneFingerAxis, aim);
        float3 backNow = swing * _boneBackAxis;
        floatQ roll = RollAbout(aim, backNow, backTarget);
        floatQ boneRotation = (roll * swing).Normalized;

        // Bone -> socket. Raw when the avatar's own hand object holds the socket; through the grip frame
        // when AvatarIK's pose node does. On the grip path the socket's -Z lands exactly on the aim; on
        // the raw path it lands wherever the bone's own -Z is, which on desktop costs nothing (the beam
        // aims from the camera) and is why the anchors below are placed off the grip frame instead.
        bool viaGrip = SocketDrivenByPoseNode();
        _holdViaGrip = viaGrip;
        floatQ socketRotation = viaGrip ? (boneRotation * _gripInBone).Normalized : boneRotation;

        // Socket -> node: the hand socket is the node's direct child carrying the assembler's grip offset.
        return (socketRotation * hand.LocalRotation.Value.Inverse).Normalized;
    }

    // Whether AvatarIK is placing this hand's bone THROUGH the grip reference (its own pose node holds
    // the socket) or aligning it to the socket raw (the avatar's own hand object took the socket).
    private bool SocketDrivenByPoseNode()
        => _handSocket != null && !_handSocket.IsDestroyed
           && _handSocket.Equipped?.Target is AvatarPoseDriver poseNode
           && poseNode.IsEquippedAndActive;

    // Where the grip frame sits in world given how the bone is attached: on the socket itself when the IK
    // drives the grip onto it, a bone-frame turn away when the bone is aligned raw. Anchors are authored
    // against the grip, so this is the frame they hang off.
    //
    // The raw-path turn is weighted by the desktop hold, because that is the only time the raw bone is
    // actually pinned to the socket: with no hold a desktop hand carries no IK weight and sits wherever
    // the rig authored it, and in VR the hold never runs and the tool has to keep pointing down the
    // controller, which is where the VR beam goes. Blended rather than switched so the cone does not pop
    // on the equip frame. -xlinka
    private floatQ GripWorldRotation(Slot hand)
    {
        var socket = hand.GlobalRotation;
        if (_handFrameValid && _holdWeight > 0f && !SocketDrivenByPoseNode())
        {
            var boneFrame = (socket * _gripInBone).Normalized;
            return _holdWeight >= 1f ? boneFrame : floatQ.Slerp(socket, boneFrame, _holdWeight).Normalized;
        }
        return socket;
    }

    // The rotation about unit `axis` that carries `from` onto `to`, both taken perpendicular to it.
    private static floatQ RollAbout(float3 axis, float3 from, float3 to)
    {
        from -= axis * float3.Dot(from, axis);
        to -= axis * float3.Dot(to, axis);
        if (from.LengthSquared < 1e-10f || to.LengthSquared < 1e-10f)
        {
            return floatQ.Identity;
        }
        from = from.Normalized;
        to = to.Normalized;
        float angle = MathF.Atan2(float3.Dot(float3.Cross(from, to), axis), float3.Dot(from, to));
        return floatQ.AxisAngle(axis, angle);
    }

    // A TransformStreamDriver on the body node IS the transport for its pose, so write the fields silently
    // when one is present - a plain assignment would put every frame's pose on the wire a second time.
    // Same rule TrackedDevicePositioner follows on the slot it shares. -xlinka
    private void WriteBodyNode(Slot node, float3 localPosition, floatQ localRotation)
    {
        if (!_bodyNodeStreamChecked)
        {
            _bodyNodeStream = node.GetComponent<TransformStreamDriver>();
            _bodyNodeStreamChecked = true;
        }

        if (_bodyNodeStream != null && !_bodyNodeStream.IsDestroyed)
        {
            node.LocalPosition.SetValueSilently(localPosition, change: true);
            node.LocalRotation.SetValueSilently(localRotation, change: true);
        }
        else
        {
            node.LocalPosition.Value = localPosition;
            node.LocalRotation.Value = localRotation;
        }
    }

    // Seat Tool Holder and Grabber at the worn avatar's tool / grab anchors. See the field block above for
    // the model. Owner only: the rig slots are synced in this user's byte, so the local pose written here
    // is what every peer sees, and a non-owner's write would be refused by the data-model gate (silently,
    // every frame) if it were attempted at all. -xlinka
    private void UpdateAvatarAnchors()
    {
        if (_toolHolderSlot == null || _grabberSlot == null || !IsUnderLocalUser)
        {
            return;
        }

        var userRoot = Slot?.ActiveUserRoot;
        var avatar = userRoot?.GetRegisteredComponent<AvatarEquipManager>()?.CurrentAvatar.Target;

        // Re-walk the avatar only when what we cached stopped being true: a different avatar (or none), the
        // side this tool serves changed under it, or a point we hold was destroyed (Avatar Studio rebuilds
        // the whole reference subtree when it re-bakes). A missing point on an avatar that simply has none
        // stays missing without a walk per frame.
        if (!ReferenceEquals(avatar, _anchorAvatar) || _anchorSide != Side.Value
            || IsGone(_gripReference) || IsGone(_toolAnchorReference) || IsGone(_grabAnchorReference)
            || IsGone(_handBone))
        {
            ResolveAvatarAnchors(avatar);
        }

        var hand = Side.Value == Chirality.Left ? userRoot?.LeftHandSlot : userRoot?.RightHandSlot;
        if (!ReferenceEquals(_handSocket?.Slot, hand))
        {
            _handSocket = hand != null && !hand.IsDestroyed ? hand.GetComponent<AvatarSocket>() : null;
        }

        // A rig can still be filling in its finger bones the first time the avatar is seen; keep asking,
        // at a walk, until it measures or the avatar changes.
        if (!_handFrameValid && avatar != null && !avatar.IsDestroyed)
        {
            double now = World?.Time.TotalTime ?? 0.0;
            if (now >= _nextHandFrameRetry)
            {
                _nextHandFrameRetry = now + 1.0;
                ResolveHandFrame(avatar);
            }
        }

        ApplyAvatarAnchor(_toolHolderSlot, _toolAnchorReference, hand, ref _toolAnchorApplied);
        ApplyAvatarAnchor(_grabberSlot, _grabAnchorReference, hand, ref _grabAnchorApplied);
    }

    private void ResolveHandFrame(Slot? avatar)
    {
        _handBone = null;
        _fingerDriver = null;
        _handFrameValid = false;
        if (avatar == null || avatar.IsDestroyed)
        {
            return;
        }

        var rig = avatar.GetComponentInChildren<HumanoidRig>();
        if (rig == null || rig.IsDestroyed)
        {
            return;
        }

        var side = Side.Value;
        var bone = rig.TryGetBone(side == Chirality.Left ? BodyNode.LeftHand : BodyNode.RightHand);
        if (bone == null || bone.IsDestroyed)
        {
            return;
        }
        _handBone = bone;
        _fingerDriver = bone.GetComponent<HandPoseDriver>();

        // Rotation only: the avatar root carries the wear scale, and a direction wants none of it.
        var inverse = bone.GlobalRotation.Inverse;
        float3 origin = bone.GlobalPosition;

        var knuckle = FirstBone(rig,
            FingerType.Middle.ComposeFinger(FingerSegmentType.Proximal, side),
            FingerType.Index.ComposeFinger(FingerSegmentType.Proximal, side),
            FingerType.Ring.ComposeFinger(FingerSegmentType.Proximal, side));
        var thumb = FirstBone(rig,
            FingerType.Thumb.ComposeFinger(FingerSegmentType.Proximal, side),
            FingerType.Thumb.ComposeFinger(FingerSegmentType.Metacarpal, side),
            FingerType.Thumb.ComposeFinger(FingerSegmentType.Distal, side));
        if (knuckle == null || thumb == null)
        {
            // A rig without those bones will not grow them: say so once and stop asking until the avatar
            // changes (ResolveAvatarAnchors resets the clock).
            _nextHandFrameRetry = double.PositiveInfinity;
            Logging.Logger.Log($"HandTool: {side} hand frame unmeasured on '{bone.SlotName.Value}' (knuckle={knuckle != null} thumb={thumb != null}); hold keeps the rest swing");
            return;
        }

        float3 fingers = inverse * (knuckle.GlobalPosition - origin);
        float3 toThumb = inverse * (thumb.GlobalPosition - origin);
        if (fingers.LengthSquared < 1e-10f || toThumb.LengthSquared < 1e-10f)
        {
            return;
        }
        fingers = fingers.Normalized;

        // Same per-side rule as HandPoseDriver.MeasurePoseBasis, same evidence: the plain cross is the
        // back of the RIGHT hand and the palm of the left.
        float3 back = float3.Cross(fingers, toThumb.Normalized);
        if (side == Chirality.Left)
        {
            back = -back;
        }
        back -= fingers * float3.Dot(back, fingers);
        if (back.LengthSquared < 1e-8f)
        {
            return;
        }

        _boneFingerAxis = fingers;
        _boneBackAxis = back.Normalized;

        floatQ gripSwing = RotationFromTo(float3.Backward, _boneFingerAxis);
        float3 gripUpNow = gripSwing * float3.Up;
        floatQ gripRoll = RollAbout(_boneFingerAxis, gripUpNow, -_boneBackAxis);
        _gripInBone = (gripRoll * gripSwing).Normalized;

        _handFrameValid = true;
        Logging.Logger.Log($"HandTool: {side} hand frame in '{bone.SlotName.Value}' space fingers={_boneFingerAxis} back={_boneBackAxis}");
    }

    private static Slot? FirstBone(HumanoidRig rig, params BodyNode[] nodes)
    {
        foreach (var node in nodes)
        {
            var bone = rig.TryGetBone(node);
            if (bone != null && !bone.IsDestroyed)
            {
                return bone;
            }
        }
        return null;
    }

    private static bool IsGone(Slot? slot) => slot != null && slot.IsDestroyed;

    private void ResolveAvatarAnchors(Slot? avatar)
    {
        _anchorAvatar = avatar;
        _anchorSide = Side.Value;
        _gripReference = null;
        _toolAnchorReference = null;
        _grabAnchorReference = null;
        // The hand frame re-measures through the throttled retry in UpdateAvatarAnchors, immediately on
        // the first pass since the clock is reset here.
        _handBone = null;
        _fingerDriver = null;
        _handFrameValid = false;
        _nextHandFrameRetry = double.NegativeInfinity;
        if (avatar == null || avatar.IsDestroyed)
        {
            return;
        }

        bool left = Side.Value == Chirality.Left;
        var gripKind = left ? AvatarReferenceKind.LeftHandGrip : AvatarReferenceKind.RightHandGrip;
        var toolKind = left ? AvatarReferenceKind.LeftHandToolAnchor : AvatarReferenceKind.RightHandToolAnchor;
        var grabKind = left ? AvatarReferenceKind.LeftHandGrabAnchor : AvatarReferenceKind.RightHandGrabAnchor;

        // Whole avatar, first of each kind wins: the same walk and tie-break AvatarIK uses for the grip.
        foreach (var point in avatar.GetComponentsInChildren<AvatarReferencePoint>())
        {
            var slot = point?.Slot;
            if (slot == null || slot.IsDestroyed)
            {
                continue;
            }
            var kind = point!.Kind.Value;
            if (kind == gripKind)
            {
                _gripReference ??= slot;
            }
            else if (kind == toolKind)
            {
                _toolAnchorReference ??= slot;
            }
            else if (kind == grabKind)
            {
                _grabAnchorReference ??= slot;
            }
        }

        // An anchor is authored as an offset from the grip. Without the grip there is no frame to read it
        // in, so it counts as absent rather than being guessed against the wrist.
        if (_gripReference == null)
        {
            _toolAnchorReference = null;
            _grabAnchorReference = null;
        }
    }

    // Change gates on the synced local pose. The pose is constant frame to frame up to float noise (the
    // controller's own rotation cancels out of it), so anything under these is noise and a write would be
    // a delta on the wire for nothing. A millimetre and a sixth of a degree are both well below what a
    // hand can see.
    private const float AnchorPositionEpsilon = 0.001f;
    private const float AnchorRotationDotFloor = 1f - 1e-6f;

    private void ApplyAvatarAnchor(Slot rigSlot, Slot? anchor, Slot? hand, ref bool applied)
    {
        if (rigSlot.IsDestroyed || Slot == null)
        {
            return;
        }

        var grip = _gripReference;
        if (anchor == null || anchor.IsDestroyed || grip == null || grip.IsDestroyed || hand == null || hand.IsDestroyed)
        {
            // Nothing authored for this hand (or no hand socket to read it against): back to the identity
            // EnsureRig built the slot with, the way the source parks its slot back on the handler. Written
            // once on the way out, so an avatar with no anchors never costs a write.
            if (applied)
            {
                rigSlot.LocalPosition.Value = float3.Zero;
                rigSlot.LocalRotation.Value = floatQ.Identity;
                applied = false;
            }
            return;
        }

        // Anchor relative to the grip, both static under the avatar, then that offset hung off the hand
        // socket - the frame the IK drives the grip reference onto - and finally into this slot's space,
        // since the rig slot stays our child. Global math end to end so avatar scale and the socket's grip
        // offset come out in the units the rig slot is actually measured in.
        //
        // "The frame the IK drives the grip reference onto" holds only while AvatarIK's pose node has the
        // socket. When the avatar's own hand object has it the bone is aligned to the socket RAW and the
        // grip frame lands a bone-frame turn away, so hanging the anchor off the socket put the dev cone
        // down the bone's -Z, which on Chiki's right hand is out of the palm. GripWorldRotation picks the
        // frame that matches the attachment, so the tool points down the fingers either way. -xlinka
        floatQ gripInverse = grip.GlobalRotation.Inverse;
        float3 relativePosition = gripInverse * (anchor.GlobalPosition - grip.GlobalPosition);
        floatQ relativeRotation = gripInverse * anchor.GlobalRotation;
        floatQ handRotation = GripWorldRotation(hand);
        float3 worldPosition = hand.GlobalPosition + handRotation * relativePosition;
        floatQ worldRotation = handRotation * relativeRotation;
        float3 localPosition = Slot.GlobalPointToLocal(worldPosition);
        floatQ localRotation = Slot.GlobalRotationToLocal(worldRotation).Normalized;

        if (applied
            && float3.DistanceSquared(rigSlot.LocalPosition.Value, localPosition) <= AnchorPositionEpsilon * AnchorPositionEpsilon
            && MathF.Abs(floatQ.Dot(rigSlot.LocalRotation.Value, localRotation)) >= AnchorRotationDotFloor)
        {
            return;
        }

        rigSlot.LocalPosition.Value = localPosition;
        rigSlot.LocalRotation.Value = localRotation;
        applied = true;
    }

    // Number-row tool shortcuts, desktop only.
    //
    // Ported from the source platform's interaction handler, including the layout: it walks key
    // indices 0..10, mapping 0..9 onto the number row and 10 onto Minus, and looks each one up in a
    // table. An index with no tool PUTS THE CURRENT TOOL AWAY rather than doing nothing, which is why
    // 1 is the unequip key over there and is the unequip key here.
    //
    // The numbers we share with it are pinned to its layout so muscle memory carries across:
    // 2 dev, 4 material, 5 shape, 6 light, 0 glue, 1 away. The two tools it has no shortcut for sit on
    // keys it spends on things we will never build. Slots 7, 8 and Minus stay empty on purpose - they
    // are its grabbable setter, collider setter and component clone, and squatting on them now would
    // move those keys under someone once we build the real ones.
    //
    // It spawns from a cloud record path; we attach the component directly, which is the same result
    // without a round trip. -xlinka
    private static readonly System.Type?[] ShortcutTools =
    {
        typeof(GlueTool),          // 0
        null,                            // 1  - put the current tool away
        typeof(DevToolItem),             // 2  - same key it has over there
        typeof(DuplicatorTool),    // 3  - their node-graph tool, which we do not build
        typeof(MaterialTool),      // 4
        typeof(ShapeTool),         // 5
        typeof(LightTool),         // 6
        null,                            // 7  - reserved: their grabbable setter
        null,                            // 8  - reserved: their collider setter
        typeof(MeterTool),         // 9  - their microphone tool, which is not ours to build
        null,                            // 10 - Minus. reserved: their component clone
    };

    private void ProcessToolShortcuts(bool vrActive, bool menuVisible)
    {
        // Desktop only, primary hand only, and never while a menu is up. The right hand is the
        // desktop primary here, the same rule the menu key already follows.
        if (vrActive || menuVisible || Side.Value != Chirality.Right)
            return;

        var input = Engine.Current?.InputInterface;
        var keyboard = input?.Keyboard;
        if (input == null || keyboard == null)
            return;

        // Typing must never fire a shortcut. The dash owns the keyboard whenever it is open, and a
        // focused text field owns it wherever it lives.
        if (input.IsDashboardOpen || Helio.UI.TextInput.Focused != null)
            return;

        for (int i = 0; i < ShortcutTools.Length; i++)
        {
            var key = i < 10 ? (Key)((int)Key.Alpha0 + i) : Key.Minus;
            if (!keyboard.IsKeyJustPressed(key))
                continue;

            var toolType = ShortcutTools[i];
            if (toolType == null)
            {
                StashOrDequipTool();
                continue;
            }

            EquipToolByType(toolType);
        }
    }

    // Pressing the same tool's key again puts it away, so one key is equip and unequip both. The
    // source platform reaches this state through its stash path; the observable behaviour is the same.
    // public so a test harness can equip a tool without synthesising a keypress. The number-row
    // shortcut path calls exactly this, so a scripted run exercises the same code a person does.
    public void EquipToolByType(System.Type toolType)
    {
        if (ActiveToolItem.Target != null && ActiveToolItem.Target.GetType() == toolType)
        {
            StashOrDequipTool();
            return;
        }

        var item = EquipNewToolItemOfType(toolType, toolType.Name);
        if (item == null)
            Lumora.Core.Logging.Logger.Warn($"HandTool: tool shortcut could not equip {toolType.Name}");
    }

    private ToolItem? EquipNewToolItemOfType(System.Type toolType, string slotName)
    {
        EnsureRig();
        var holder = _toolHolderSlot ?? Slot;
        if (holder == null || holder.IsDestroyed)
            return null;

        var itemSlot = holder.FindChild(slotName, recursive: false) ?? holder.AddSlot(slotName);
        var item = itemSlot.GetComponent(toolType) as ToolItem
                   ?? itemSlot.AttachComponent(toolType) as ToolItem;
        if (item == null)
            return null;

        EquipToolItem(item);
        return item;
    }

    private void StashOrDequipTool()
    {
        if (ActiveToolItem.Target == null)
            return;
        EquipToolItem(null);
    }

    public override void OnDestroy()
    {
        ResetInteraction(releaseHeld: true);
        // Give the body node its authored pose back. Only this component knows it was moved, and a rig torn
        // down mid-hold (avatar swap, user leaving) would otherwise leave the hand parked in front of the
        // face for whatever gets built next. -xlinka
        if (_wroteHoldPose && _hasRestPose && IsUnderLocalUser)
        {
            var node = _bodyNodeSlot;
            if (node != null && !node.IsDestroyed)
            {
                WriteBodyNode(node, _restLocalPosition, _restLocalRotation);
            }
        }
        _wroteHoldPose = false;
        _holdWeight = 0f;
        // Don't pop the item into the world mid-teardown; let it go down with the rig.
        _suppressHolderRelease = true;
        EquipToolItem(null);
        base.OnDestroy();
    }

    private bool _suppressHolderRelease;

    private ToolItem? _refusedEquip;

    private void EnsureRig()
    {
        if (Slot == null || Slot.IsRemoved)
        {
            return;
        }

        // Builds the tool rig (Grabber/Laser/Tool Holder slots + components) under the HandTool slot. This is
        // idempotent: every slot is FindChild-or-add, so a non-owner peer adopts the rig that replicated from the
        // owner instead of minting a duplicate. The owner's writes can be permission-denied for a beat during join
        // (the User<->UserRoot link lags), but we run this from OnUpdate every frame as well as OnStart, so the next
        // frame retries and lands once the link resolves - no bypass needed, just let it throw and re-drive. -xlinka
        _grabberSlot ??= Slot.FindChild("Grabber", recursive: false) ?? Slot.AddSlot("Grabber");
        if (_grabberSlot.GetComponent<SearchBlock>() == null)
        {
            _grabberSlot.AttachComponent<SearchBlock>();
        }
        _grabber ??= _grabberSlot.GetComponent<Grabber>() ?? _grabberSlot.AttachComponent<Grabber>();

        _laserSlot ??= Slot.FindChild("Laser", recursive: false) ?? Slot.AddSlot("Laser");
        _laser ??= _laserSlot.GetComponent<InteractionLaser>() ?? _laserSlot.AttachComponent<InteractionLaser>();
        _laser.ControllerSide.Value = Side.Value;
        _laser.SetIgnoreRoot(Slot);

        _toolHolderSlot ??= Slot.FindChild("Tool Holder", recursive: false) ?? Slot.AddSlot("Tool Holder");
        if (_toolHolderSlot.GetComponent<GrabBlock>() == null)
        {
            _toolHolderSlot.AttachComponent<GrabBlock>();
        }
        if (_toolHolderSlot.GetComponent<SearchBlock>() == null)
        {
            _toolHolderSlot.AttachComponent<SearchBlock>();
        }

        if (ActiveToolItem.Target == null)
        {
            var item = _toolHolderSlot.GetComponentInChildren<ToolItem>(includeSelf: false);
            if (item != null)
            {
                EquipToolItem(item);
            }
        }
    }

    public void EquipToolItem(ToolItem? item)
    {
        var previous = ActiveToolItem.Target;
        if (ReferenceEquals(previous, item))
        {
            return;
        }

        // Putting a tool DOWN is never gated - a role losing ToolUse mid-session must not be left
        // holding something it cannot let go of. EnsureRig re-offers the holder's item every frame, so
        // the refusal is logged once per item or the console fills up. -xlinka
        if (item != null && !item.AllowsEquip(World?.LocalUser))
        {
            if (!ReferenceEquals(_refusedEquip, item))
            {
                _refusedEquip = item;
                Logging.Logger.Log($"{item.GetType().Name} not equipped: tools are not available to you in this world.");
            }
            return;
        }
        _refusedEquip = null;

        if (previous != null)
        {
            previous.OnDequipped();
            previous.SetActiveTool(null);
            ReleaseFromHolder(previous);
        }

        ActiveToolItem.Target = item!;
        if (item != null)
        {
            item.SetActiveTool(this);
            item.OnEquipped();
            DockInHolder(item);
        }
    }

    // Physically snap the equipped item into the Tool Holder (a world tool stays put without this - the equip
    // link alone doesn't move anything). No-op for items already in the holder (the rig's default tool). -xlinka
    private void DockInHolder(ToolItem item)
    {
        var itemSlot = item?.Slot;
        if (itemSlot == null || itemSlot.IsDestroyed)
            return;
        EnsureRig();
        if (_toolHolderSlot == null || itemSlot == _toolHolderSlot || itemSlot.IsDescendantOf(_toolHolderSlot))
            return;

        // Still held (menu equip releases first; this covers stragglers) - let go before reparenting or the
        // grabber keeps a stale ref to a slot it no longer holds.
        var grabbable = itemSlot.GetComponent<Grabbable>();
        if (grabbable != null && grabbable.IsGrabbed)
            grabbable.Grabber?.Release(grabbable);

        itemSlot.SetParent(_toolHolderSlot, preserveGlobalTransform: false);
        itemSlot.LocalPosition.Value = float3.Zero;
        itemSlot.LocalRotation.Value = floatQ.Identity;
    }

    // Dequip must physically remove the item from the Tool Holder, otherwise
    // EnsureRig's auto-equip finds it there next update and snaps it right back.
    // Drop it into the world just off the hand.
    private void ReleaseFromHolder(ToolItem item)
    {
        if (_suppressHolderRelease)
            return;
        var itemSlot = item?.Slot;
        if (itemSlot == null || itemSlot.IsDestroyed || _toolHolderSlot == null)
            return;
        if (itemSlot != _toolHolderSlot && !itemSlot.IsDescendantOf(_toolHolderSlot))
            return;

        var userRootSlot = Slot?.ActiveUserRoot?.Slot;
        var newParent = userRootSlot?.Parent ?? World?.RootSlot;
        if (newParent == null || newParent.IsDestroyed)
            return;

        itemSlot.SetParent(newParent, preserveGlobalTransform: true);
        // Pop it off the hand a little so it isn't left intersecting the grip.
        if (Slot != null)
        {
            itemSlot.GlobalPosition += Slot.Forward * 0.05f;
        }
    }

    public T EquipNewToolItem<T>(string slotName) where T : ToolItem, new()
    {
        EnsureRig();
        var holder = _toolHolderSlot ?? Slot;
        var itemSlot = holder.FindChild(slotName, recursive: false) ?? holder.AddSlot(slotName);
        var item = itemSlot.GetComponent<T>() ?? itemSlot.AttachComponent<T>();
        EquipToolItem(item);
        return item;
    }

    private ToolItem? GetUsableToolItem()
    {
        var toolItem = ActiveToolItem.Target;
        if (toolItem == null || !toolItem.Enabled.Value || toolItem.IsDestroyed)
        {
            return null;
        }

        if (IsHoldingObjects && !toolItem.CanUseWhenHolding)
        {
            return null;
        }

        // Asked again at the press and not only at the equip: a role can be changed, or the world
        // locked, while the thing is already in your hand. A press by someone who has lost the right
        // to use it does nothing at all rather than reaching the tool's own handler.
        if (!toolItem.AllowsEquip(World?.LocalUser))
        {
            return null;
        }

        return toolItem;
    }

    private void SampleInput(InteractionLaser laser)
    {
        // Tool secondary on desktop is a KEY, and a focused text field owns the keyboard outright -
        // the keyboard source is gated at that point, so typing never reaches an action and the VR
        // controller buttons carry on regardless.
        _primaryHeld = ReadPrimaryPressed(laser);
        _secondaryHeld = ReadSecondaryPressed(laser);
        _gripHeld = ReadGripPressed(laser);
    }

    private void ProcessPrimary(InteractionLaser laser)
    {
        // A press while this hand's menu is up is a menu press and nothing else. The canvas already
        // receives it (uiPress stays true with the menu open so held-object actions are clickable), so
        // letting the chain below run too meant "Destroy" on a held reference card first activated the
        // card - it opened its inspector and spent itself - and Destroy then found an empty hand. -xlinka
        if (_primaryHeld && !_prevPrimaryHeld && IsContextMenuOpenByThisHand())
        {
            _prevPrimaryHeld = _primaryHeld;
            return;
        }

        // An armed color picker owns the next world press outright, ahead of the tool, the held object
        // and the hit target - the whole point of the mode is that the click means "sample that", not
        // whatever it would otherwise have meant.
        if (_primaryHeld && !_prevPrimaryHeld && TryEyedropperPress(laser))
        {
            _prevPrimaryHeld = _primaryHeld;
            return;
        }

        if (_primaryHeld && !_prevPrimaryHeld)
        {
            var toolItem = GetUsableToolItem();
            if (toolItem != null && toolItem.OnPrimaryPress())
            {
                _activePrimaryToolItem = toolItem;
            }
            else if (IsHoldingObjectsWithLaser)
            {
                // Order matters. Holding with the laser SUPPRESSES canvas presses (uiPress below), so a
                // press aimed at a UI row can never reach the row's own button - the row has to be
                // offered the hand's contents from here instead. That has to happen BEFORE the held
                // object gets the press, or clicking a reference field while carrying a card would run
                // the CARD's action (open an inspector on its target) and spend the card without ever
                // assigning it: you aim at the field you wanted to fill, click, and lose the card. So:
                // a receiver under the pointer wins, then the held object's own action, then align.
                // -xlinka
                if (!TryDropProxyOnUI(laser) && !TryActivateHeldObject())
                    ProcessAlignPress(laser);
            }
            else if (laser.CurrentTarget is Gizmos.TransformHandle handle && handle.BeginToolDrag(laser))
            {
                // A gizmo handle is a control, not an object: primary on it drags whether or not a tool is
                // equipped. The inspector spawns gizmos with no tool in hand, and a handle that only answers
                // the dev tool's primary (or a grip) reads as dead to a mouse user. -xlinka
                _bareHandle = handle;
            }
            else if (laser.CurrentTarget != null && laser.CurrentPointerTarget == null)
            {
                laser.CurrentRayTarget?.NotifyActivated(laser.CurrentHitPoint);
                laser.NotifyActivatedByTool(laser.CurrentTarget, laser.CurrentHitPoint);
            }
        }
        else if (_primaryHeld && _activePrimaryToolItem != null)
        {
            _activePrimaryToolItem.OnPrimaryHold();
        }
        else if (_primaryHeld && _bareHandle != null)
        {
            if (!_bareHandle.IsDragging || _bareHandle.IsDestroyed)
                _bareHandle = null;
        }
        else if (!_primaryHeld && _prevPrimaryHeld && _activePrimaryToolItem != null)
        {
            _activePrimaryToolItem.OnPrimaryRelease();
            _activePrimaryToolItem = null;
        }
        else if (!_primaryHeld && _prevPrimaryHeld && _bareHandle != null)
        {
            _bareHandle.EndToolDrag();
            _bareHandle = null;
        }

        _prevPrimaryHeld = _primaryHeld;
    }

    // Eyedropper. While a color picker is armed the next world press samples what the beam is on and
    // goes no further - not to the tool, not to the held object, and not to a canvas under the pointer
    // (see uiPress): clicking somebody else's panel to sample its color must not also press the button
    // you happened to aim at.
    //
    // The armed panel's OWN surface is the exception, on both paths. Its Cancel, its Save and the Pick
    // toggle itself all have to stay clickable, or arming the mode is a trap you cannot get out of.
    // Returns the panel that owns this press, or null when the press is nobody's business. -xlinka
    private static ColorPickerPanel? EyedropperFor(InteractionLaser laser)
    {
        var sampler = ColorPickerPanel.ActiveSampler;
        if (sampler == null || sampler.IsDestroyed || !sampler.IsSampling)
        {
            return null;
        }

        var hitSlot = laser.CurrentHitSlot;
        var panelSlot = sampler.Slot;
        if (hitSlot != null && panelSlot != null && !panelSlot.IsDestroyed
            && (ReferenceEquals(hitSlot, panelSlot) || hitSlot.IsDescendantOf(panelSlot)))
        {
            return null;
        }
        return sampler;
    }

    // A miss leaves the picker's value alone but still ends the mode: a press that did nothing visible
    // and left you armed reads as broken.
    private static bool TryEyedropperPress(InteractionLaser laser)
    {
        if (EyedropperFor(laser) is not { } sampler)
        {
            return false;
        }

        // Interaction hits only cover interaction targets; plain scenery stops the beam at its
        // collider, whose slot and point the laser now keeps, so the material walk works on a ground
        // plate too. The pixel fallback only remains for things with no collider at all. -xlinka
        var hitSlot = laser.CurrentHitSlot ?? laser.CurrentColliderHitSlot;
        float3 point = laser.CurrentHitSlot != null ? laser.CurrentHitPoint
            : laser.CurrentColliderHitSlot != null ? laser.CurrentColliderHitPoint
            : laser.HasAimPoint ? laser.AimPoint : laser.CurrentHitPoint;

        if (ColorSampling.TrySample(hitSlot, point, out var sampled))
        {
            sampler.ApplySampledColor(sampled);
        }
        sampler.DisarmSampling();
        return true;
    }

    // Cancel works wherever the beam is pointing, the picker's own panel included - the secondary is not
    // a click on anything, it is "get me out of this mode".
    private static bool TryCancelEyedropper()
    {
        var sampler = ColorPickerPanel.ActiveSampler;
        if (sampler == null || sampler.IsDestroyed || !sampler.IsSampling)
        {
            return false;
        }
        sampler.DisarmSampling();
        return true;
    }

    // Offer the primary press to any held object that wants to run its own action instead of aligning
    // (a reference card opens its target). Snapshot the hold list first: a claimer may remove itself
    // from the hand mid-iteration. Returns true when one consumed the press. -xlinka
    private bool TryActivateHeldObject()
    {
        if (_grabber == null)
            return false;

        var held = new List<IGrabbable>(_grabber.GrabbedObjects);
        foreach (var grabbable in held)
        {
            if (grabbable is not Component component || component.Slot == null || component.IsDestroyed)
                continue;
            foreach (var activatable in component.Slot.GetComponentsImplementing<IHeldActivatable>())
            {
                if (activatable is Component c && (!c.Enabled.Value || c.IsDestroyed))
                    continue;
                if (activatable.OnHeldActivate(_grabber))
                    return true;
            }
        }
        return false;
    }

    private void ProcessSecondary(InteractionLaser laser)
    {
        if (_secondaryHeld && !_prevSecondaryHeld)
        {
            // Secondary is this tool's "back out of whatever mode you are in", so it cancels an armed
            // eyedropper before anything else looks at the press. Escape is not the cancel here: it is
            // already the mouse-capture toggle AND the dashboard toggle, and stealing it would make
            // arming the picker break both. -xlinka
            if (TryCancelEyedropper())
            {
                _prevSecondaryHeld = _secondaryHeld;
                return;
            }

            // While our menu is open, the button closes it before any tool
            // gets a say - otherwise an equipped tool would eat the press and
            // the menu could never be dismissed.
            if (IsContextMenuOpenByThisHand())
            {
                FindContextMenu()?.Close();
            }
            else
            {
                var toolItem = GetUsableToolItem();
                if (toolItem != null && toolItem.UsesSecondary && toolItem.OnSecondaryPress())
                {
                    _activeSecondaryToolItem = toolItem;
                }
                else if (!IsHoldingObjects && Engine.Current?.InputInterface?.IsVRActive == true)
                {
                    // VR fallback: free secondary opens the menu. Desktop uses
                    // the dedicated T binding instead.
                    ToggleContextMenu(laser);
                }
            }
        }
        else if (_secondaryHeld && _activeSecondaryToolItem != null)
        {
            _activeSecondaryToolItem.OnSecondaryHold();
        }
        else if (!_secondaryHeld && _prevSecondaryHeld && _activeSecondaryToolItem != null)
        {
            _activeSecondaryToolItem.OnSecondaryRelease();
            _activeSecondaryToolItem = null;
        }

        _prevSecondaryHeld = _secondaryHeld;
    }

    // Secondary press with no tool/held-object claim toggles the user's
    // radial context menu at the laser, carrying what it was pointing at so
    // sources can add contextual actions (equip avatar, etc.).
    private void ToggleContextMenu(InteractionLaser laser)
    {
        var menu = FindContextMenu();
        if (menu == null)
            return;

        menu.Toggle(new UI.ContextMenuContext
        {
            Pointer = _laserSlot ?? Slot,
            Target = laser?.CurrentHitSlot,
            Side = Side.Value,
        });
    }

    // Accumulated mouse deflection (yaw, pitch radians) steering the laser while
    // the desktop context menu has the camera frozen. Same sign convention as
    // the camera: mouse right = look right, mouse up = look up.
    private float2 _menuAim;
    private const float MenuAimRadiansPerScreen = 1.5f;
    private const float MenuAimMaxRadians = 0.85f;

    private void UpdateDesktopMenuAim(bool active)
    {
        if (_laser == null)
            return;

        if (!active)
        {
            if (_menuAim != float2.Zero)
            {
                _menuAim = float2.Zero;
                _laser.SetDesktopAimOffset(float2.Zero);
            }
            return;
        }

        var mouse = Engine.Current?.InputInterface?.Mouse;
        if (mouse == null)
            return;

        var d = mouse.DirectDelta.Value;
        _menuAim = new float2(
            System.Math.Clamp(_menuAim.x - d.x * MenuAimRadiansPerScreen, -MenuAimMaxRadians, MenuAimMaxRadians),
            System.Math.Clamp(_menuAim.y - d.y * MenuAimRadiansPerScreen, -MenuAimMaxRadians, MenuAimMaxRadians));
        _laser.SetDesktopAimOffset(_menuAim);
    }

    // Desktop context menu toggle. Only the right hand listens so both hands cannot double-toggle;
    // in VR the menu is summoned by the tool itself, not from here. Whichever control is bound
    // (stock: middle click, T, or the pad's top face button) toggles on its press edge, and a
    // focused text field takes the keyboard out of play before an action ever sees a keystroke.
    // -xlinka
    private void ProcessMenuKey(InteractionLaser laser)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null || input.IsVRActive || Side.Value != Chirality.Right)
        {
            return;
        }

        if (input.Actions?.Right.ContextMenu.Pressed == true)
        {
            ToggleContextMenu(laser);
        }
    }

    private UI.ContextMenuSystem? _contextMenu;

    private UI.ContextMenuSystem? FindContextMenu()
    {
        if (_contextMenu == null || _contextMenu.IsDestroyed)
            _contextMenu = Slot?.ActiveUserRoot?.Slot?.GetComponentInChildren<UI.ContextMenuSystem>();
        return _contextMenu;
    }

    private bool IsContextMenuOpenByThisHand()
    {
        // Resolve, don't just read the cache: a confirm menu opened by something else (a tool's
        // touch-to-equip prompt) never goes through ToggleContextMenu, and an unfilled cache reads as
        // "no menu", so the laser keeps its world aim and the prompt can't be clicked until the
        // radial menu has been opened once by hand. -xlinka
        var menu = FindContextMenu();
        if (menu == null || menu.IsDestroyed || !menu.IsOpen.Value)
            return false;
        return menu.CurrentContext?.Side == Side.Value;
    }

    private void ProcessGrip(InteractionLaser laser)
    {
        // Grab state freezes while this hand's menu is open: letting go of grip
        // to work the menu must not drop the held object, or Destroy/Duplicate
        // would always act on an empty hand. The release edge is processed
        // after the menu closes.
        if (IsContextMenuOpenByThisHand())
            return;

        if (_gripHeld && !_prevGripHeld)
        {
            // Prefer a touch (physical) grab when something is within reach of the hand; fall back to the
            // laser grab when nothing is. -xlinka
            if (!TryTouchGrab())
            {
                TryGrabCurrentTarget(laser);
            }
        }
        else if (!_gripHeld && _prevGripHeld)
        {
            // Letting go over a UI row that accepts references consumes the held card before the
            // release puts anything back into the world. -xlinka
            TryDropProxyOnUI(laser);
            _grabber?.ReleaseAll();
            ResetInteraction(releaseHeld: false);
        }

        _prevGripHeld = _gripHeld;
    }

    private void TryGrabCurrentTarget(InteractionLaser laser)
    {
        if (_grabber == null)
        {
            return;
        }

        // A UI row under the pointer that offers a reference card wins over grabbing the panel:
        // gripping a member row pulls the card, gripping the frame still moves the window. -xlinka
        var grabbable = TryPullProxyFromUI(laser) ?? FindBestGrabbable(laser.CurrentTarget, laser.CurrentHitSlot);
        if (grabbable == null)
        {
            return;
        }

        var holder = _grabber.HolderSlot;
        if (holder == null)
        {
            return;
        }

        _laserGrabDistance = MathF.Max(0.05f, laser.CurrentHitDistance);
        _holderAxisOffset = 0f;
        _holderRotationOffset = floatQ.Identity;
        // Frozen at grab, never recomputed. The held object keeps the facing it had when you picked it up;
        // letting it re-derive from the head every frame makes it swing to face you as the view pitches and
        // yaws, which is the last thing you want while trying to place something. Resolve it to a real value
        // here so UpdateHolderRotation can never fall through to the chasing path. -xlinka
        _holderRotationReference = GetHeadFacingRotation(laser)
            ?? laser.FindHeadSlot()?.GlobalRotation
            ?? Slot.GlobalRotation;
        RotationMode.Value = LaserRotationMode.AxisY;

        holder.GlobalPosition = laser.CurrentHitPoint;
        holder.GlobalScale = float3.One;
        UpdateHolderRotation(laser, holder);

        if (!_grabber.TryGrab(grabbable))
        {
            return;
        }

        _isHoldingWithLaser = true;
    }

    // Pull a reference card out of a hovered UI panel: resolve the exact row under the pointer
    // (the interactable hit test can't see labels or plain containers) and ask up its parent chain
    // for a proxy source. Returns the freshly spawned card, ready to grab at the hit point. -xlinka
    private IGrabbable? TryPullProxyFromUI(InteractionLaser laser)
    {
        if (_grabber == null || laser.CurrentTarget is not Helio.UI.Canvas canvas || canvas.Slot == null)
        {
            return null;
        }
        if (!canvas.TryResolveUISlot(laser.RayOrigin, laser.RayDirection, out var uiSlot, out var worldPoint) || uiSlot == null)
        {
            return null;
        }
        var source = FindUIBehavior<IProxySource>(uiSlot, canvas.Slot);
        return source?.TryCreateProxy(_grabber, worldPoint);
    }

    // Offer everything in the hand to a reference receiver under the pointer. Two callers: the grip
    // release edge (before the release puts the held items back into the world) and the primary press
    // while laser-holding. Returns true when a receiver consumed something. -xlinka
    private bool TryDropProxyOnUI(InteractionLaser laser)
    {
        if (_grabber == null || !_grabber.IsHoldingObjects)
        {
            return false;
        }
        if (laser.CurrentTarget is not Helio.UI.Canvas canvas || canvas.Slot == null)
        {
            return false;
        }
        if (!canvas.TryResolveUISlot(laser.RayOrigin, laser.RayDirection, out var uiSlot, out _) || uiSlot == null)
        {
            return false;
        }
        var receiver = FindUIBehavior<IProxyReceiver>(uiSlot, canvas.Slot);
        // Snapshot the hold list: a receiver consumes the card it took (releases it from this hand and
        // destroys it), which mutates the grabber's list mid-call. -xlinka
        if (receiver == null)
        {
            return false;
        }
        var held = new List<IGrabbable>(_grabber.GrabbedObjects);
        return held.Count > 0 && receiver.TryReceiveProxy(held, _grabber);
    }

    // First T on the slot or its parents, stopping at the canvas root (UI rows never reach outside
    // their own panel).
    private static T? FindUIBehavior<T>(Slot start, Slot canvasRoot) where T : class
    {
        for (var current = start; current != null; current = current.Parent)
        {
            foreach (var behavior in current.GetComponentsImplementing<T>())
            {
                if (behavior is Component component && component.Enabled.Value && !component.IsDestroyed)
                {
                    return behavior;
                }
            }
            if (ReferenceEquals(current, canvasRoot))
            {
                break;
            }
        }
        return null;
    }

    // Hand reach for a touch grab, in metres at unit user scale. Roughly the grab-sphere of the hand.
    private const float TouchGrabRadius = 0.1f;

    // Touch (physical) grab: in VR, if a grabbable collider is physically within reach of the hand, grab it
    // straight into the hand instead of using the laser. The held object rides the grabber slot via the
    // holder (pinned to the hand), and an IGrabAlignable object snaps to its defined in-hand pose. Gated to
    // VR: on desktop the hand isn't a tracked physical thing, so this no-ops and laser grab runs. Returns
    // false when nothing is in reach. -xlinka
    private bool TryTouchGrab()
    {
        var input = Engine.Current?.InputInterface;
        if (input == null || !input.IsVRActive || _grabber == null)
        {
            return false;
        }

        var handSlot = _grabber.Slot;
        var holder = _grabber.HolderSlot;
        if (handSlot == null || holder == null)
        {
            return false;
        }

        // Pin the holder to the hand before grabbing so the object rides the controller directly - Grab
        // keeps the object's world pose, capturing its offset from the hand. -xlinka
        holder.LocalPosition.Value = float3.Zero;
        holder.LocalRotation.Value = floatQ.Identity;
        holder.LocalScale.Value = float3.One;

        var scale = handSlot.GlobalScale;
        float avgScale = (scale.x + scale.y + scale.z) / 3f;
        float radius = TouchGrabRadius * (avgScale > 0.0001f ? avgScale : 1f);

        if (!_grabber.TryGrabNearby(handSlot.GlobalPosition, radius, out var grabbed) || grabbed == null)
        {
            return false;
        }

        TryAlignGrabbed(grabbed);

        // Touch-held objects follow the hand through the hierarchy, so the laser-hold path must not drive
        // them. Leaving _isHoldingWithLaser false also keeps the laser cursor in its normal state. -xlinka
        _isHoldingWithLaser = false;
        return true;
    }

    // Snap a single touch-grabbed object to its IGrabAlignable pose (relative to the holder), if it
    // declares one. Mirrors the laser-grab path leaving the grab offset alone when there's no alignment.
    private void TryAlignGrabbed(IGrabbable grabbed)
    {
        if (_grabber == null || grabbed is not Component component)
        {
            return;
        }

        var slot = component.Slot;
        if (slot == null || slot.IsRemoved)
        {
            return;
        }

        foreach (var alignable in slot.GetComponentsImplementing<IGrabAlignable>())
        {
            if (alignable.GetGrabAlignmentPose(_grabber, out var pos, out var rot, out var scale))
            {
                slot.LocalPosition.Value = pos;
                slot.LocalRotation.Value = rot;
                slot.LocalScale.Value = scale;
                return;
            }
        }
    }

    private void ProcessLaserHold(InteractionLaser laser, float delta)
    {
        if (_grabber == null || !_grabber.IsHoldingObjects)
        {
            ResetInteraction(releaseHeld: false);
            return;
        }

        var holder = _grabber.HolderSlot;
        if (holder == null || holder.IsRemoved)
        {
            ResetInteraction(releaseHeld: false);
            return;
        }

        ApplyHoldInputs(laser, holder, delta);
        _laserGrabDistance = Clamp(_laserGrabDistance, 0.05f, MathF.Max(laser.MaxDistance.Value, 0.05f));

        // The pose write waits for the late pass. This tool updates at -1000, but on desktop the head pitch,
        // the body yaw and the very hand slot the holder hangs off are all written at update order 0 - so
        // writing here aims the object down LAST frame's view, and then those ancestors rotate underneath it
        // before anything renders, dragging it around the feet and the hand instead of the eye. Next frame
        // recomputes from the eye and yanks it back. That push-pull is the stepping, and it flips sign
        // between looking up and looking down because the hand's aim pitch clamps on the way down.
        //
        // A world whose late pass is throttled off (a background world) never reaches OnLateUpdate, so fall
        // back to writing inline the moment the late write goes missing. -xlinka
        long frame = Engine.Current?.FrameCount ?? -1;
        _holdPoseFrame = frame;
        if (frame < 0 || _holdPoseWrittenFrame < frame - 2)
        {
            WriteHolderPose(laser, holder, delta);
        }
    }

    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);

        long frame = Engine.Current?.FrameCount ?? -1;
        if (frame < 0 || _holdPoseFrame != frame || _laser == null || _grabber == null)
        {
            return;
        }

        var holder = _grabber.HolderSlot;
        if (holder == null || holder.IsRemoved)
        {
            return;
        }

        WriteHolderPose(_laser, holder, delta);
        _holdPoseWrittenFrame = frame;
    }

    // Aim, then place. RefreshHeldAim re-resolves the laser ray off the head/root as they stand NOW and takes
    // the smoothing step, so the beam and the object it carries come off the same ray. -xlinka
    private void WriteHolderPose(InteractionLaser laser, Slot holder, float delta)
    {
        laser.RefreshHeldAim(delta);
        UpdateHolderPosition(laser, holder);
        UpdateHolderRotation(laser, holder);
    }

    private void ApplyHoldInputs(InteractionLaser laser, Slot holder, float delta)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null)
        {
            SetDesktopInputSuppression(false);
            return;
        }

        var hand = input.Actions?.Interaction(Side.Value);
        if (hand == null)
        {
            SetDesktopInputSuppression(false);
            return;
        }

        if (input.IsVRActive)
        {
            SetDesktopInputSuppression(false);
            ApplyVrHoldInputs(hand, delta, holder);
            return;
        }

        bool freezeCursor = hand.HoldFreeze.Held;
        SetDesktopInputSuppression(freezeCursor);

        float scroll = hand.HoldScroll.Value;
        if (scroll != 0f)
        {
            if (hand.HoldModifier.Held && CanScaleHeldObjects())
            {
                ScaleHolder(holder, scroll * HoldScaleStep.Value);
            }
            else
            {
                float step = MathF.Max(0.05f, _laserGrabDistance * HoldScrollStep.Value);
                _laserGrabDistance += scroll * step;
            }
        }

        if (!freezeCursor)
        {
            return;
        }

        float2 mouseDelta = hand.HoldLook.Value;
        if (mouseDelta == float2.Zero)
        {
            return;
        }

        if (hand.HoldModifier.Held)
        {
            _holderAxisOffset += mouseDelta.x * HoldRotationSensitivity.Value;
        }
        else
        {
            ApplyFreeformRotation(laser, new float3(
                -mouseDelta.y * HoldRotationSensitivity.Value,
                mouseDelta.x * HoldRotationSensitivity.Value,
                0f));
        }
    }

    private void ApplyVrHoldInputs(Input.Actions.InteractionActions hand, float delta, Slot holder)
    {
        // The axis action applies its own PER-AXIS deadzone, so slide and twist stay independent: a
        // hard pull toward you must not smear rotation onto the object as well.
        var axis = hand.HoldAxis.Value;
        float slide = axis.y;
        float rotate = axis.x;

        if (hand.HoldModifier.Held && slide != 0f && CanScaleHeldObjects())
        {
            ScaleHolder(holder, slide * delta);
        }
        else if (slide != 0f)
        {
            _laserGrabDistance += slide * MathF.Max(1f, _laserGrabDistance) * 4f * delta;
        }

        if (rotate != 0f)
        {
            _holderAxisOffset += rotate * MathF.PI * 2f * delta;
        }
    }

    private void UpdateHolderPosition(InteractionLaser laser, Slot holder)
    {
        float3 origin = laser.RayOrigin;
        float3 direction = laser.RayDirection;
        if (direction.Length <= 0.0001f)
        {
            ResolveFallbackRay(laser, out origin, out direction);
        }

        holder.GlobalPosition = origin + direction.Normalized * _laserGrabDistance;
    }

    private void UpdateHolderRotation(InteractionLaser laser, Slot holder)
    {
        if (RotationMode.Value == LaserRotationMode.Unconstrained)
        {
            holder.GlobalRotation = Slot.GlobalRotation;
            return;
        }

        var root = FindUserRootSlot();
        var head = laser.FindHeadSlot();
        float3 rootUp = root?.Up ?? float3.Up;
        if (rootUp.Length <= 0.0001f)
        {
            rootUp = float3.Up;
        }
        rootUp = rootUp.Normalized;

        floatQ reference = _holderRotationReference ?? GetHeadFacingRotation(laser) ?? (head?.GlobalRotation ?? Slot.GlobalRotation);
        float3 rightAxis = reference * float3.Right;
        if (rightAxis.Length <= 0.0001f)
        {
            rightAxis = Slot.Right;
        }
        rightAxis = rightAxis.Normalized;

        float3 forward = ComputeHolderForward(laser, holder, reference, root, head);
        if (forward.Length <= 0.0001f)
        {
            forward = reference * float3.Backward;
        }
        if (forward.Length <= 0.0001f)
        {
            forward = float3.Backward;
        }
        forward = forward.Normalized;

        float angle = _holderAxisOffset;
        switch (RotationMode.Value)
        {
            case LaserRotationMode.AxisX:
                forward = (floatQ.AxisAngle(rightAxis, angle) * forward).Normalized;
                break;
            case LaserRotationMode.AxisY:
                forward = (floatQ.AxisAngle(rootUp, angle) * forward).Normalized;
                break;
            case LaserRotationMode.AxisZ:
                rootUp = (floatQ.AxisAngle(forward, angle) * rootUp).Normalized;
                break;
        }

        holder.GlobalRotation = _holderRotationOffset * FacingRotation(forward, rootUp);
    }

    private float3 ComputeHolderForward(InteractionLaser laser, Slot holder, floatQ reference, Slot? root, Slot? head)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null || !input.IsVRActive)
        {
            return reference * float3.Backward;
        }

        if (root == null || head == null)
        {
            return laser.RayDirection;
        }

        float3 fromHead = holder.GlobalPosition - head.GlobalPosition;
        float3 radial = ProjectOnPlane(fromHead, root.Up);
        float3 ray = ProjectOnPlane(laser.RayDirection, root.Up);

        if (radial.Length > 0.0001f && ray.Length > 0.0001f)
        {
            return (radial.Normalized + ray.Normalized).Normalized;
        }
        if (radial.Length > 0.0001f)
        {
            return radial.Normalized;
        }
        return laser.RayDirection;
    }

    private void ApplyFreeformRotation(InteractionLaser laser, float3 rotation)
    {
        if (_grabber == null || rotation.Length <= 0.0001f)
        {
            return;
        }

        floatQ delta = floatQ.Euler(rotation);
        var holder = _grabber.HolderSlot;
        if (holder == null)
        {
            _holderRotationOffset = delta * _holderRotationOffset;
            return;
        }

        var slots = GetGrabbedSlots();
        if (slots.Count == 0)
        {
            _holderRotationOffset = delta * _holderRotationOffset;
            return;
        }

        floatQ viewRotation = GetHeadFacingRotation(laser) ?? (laser.FindHeadSlot()?.GlobalRotation ?? Slot.GlobalRotation);
        floatQ inverseView = viewRotation.Inverse;
        foreach (var slot in slots)
        {
            floatQ globalRotation = viewRotation * (delta * (inverseView * slot.GlobalRotation));
            slot.GlobalRotation = globalRotation;
        }
    }

    private void ProcessAlignPress(InteractionLaser laser)
    {
        double now = World?.TotalTime ?? 0.0;
        if (now - _lastAlignPress < 0.5)
        {
            RotationMode.Value = RotationMode.Value == LaserRotationMode.AxisY
                ? LaserRotationMode.Unconstrained
                : LaserRotationMode.AxisY;
            PreserveHeldGlobalTransforms(() =>
            {
                if (_grabber?.HolderSlot != null)
                {
                    UpdateHolderRotation(laser, _grabber.HolderSlot);
                }
            });
            AlignHeldObjects(laser, GetLaserRotationAxis());
            _lastAlignPress = -1000.0;
            return;
        }

        AlignHeldObjects(laser, float3.Up);
        _lastAlignPress = now;
    }

    private void AlignHeldObjects(InteractionLaser laser, float3 referenceAxis)
    {
        foreach (var slot in GetGrabbedSlots())
        {
            if (TryAlignUiSlot(laser, slot))
            {
                continue;
            }
            AlignSlotToReferenceAxis(slot, referenceAxis);
        }
    }

    private static bool TryAlignUiSlot(InteractionLaser laser, Slot slot)
    {
        if (slot.GetComponent<Helio.UI.Canvas>() == null && slot.GetComponent<Helio.UI.RectTransform>() == null)
        {
            return false;
        }

        var head = laser.FindHeadSlot();
        if (head == null)
        {
            return false;
        }

        float3 faceDirection = head.GlobalPosition - slot.GlobalPosition;
        if (faceDirection.Length <= 0.0001f)
        {
            faceDirection = head.Forward;
        }
        if (faceDirection.Length <= 0.0001f)
        {
            return false;
        }
        faceDirection = faceDirection.Normalized;

        float3 upDirection = ProjectOnPlane(head.Up, faceDirection);
        if (upDirection.Length <= 0.0001f)
        {
            upDirection = ProjectOnPlane(float3.Up, faceDirection);
        }
        if (upDirection.Length <= 0.0001f)
        {
            upDirection = float3.Right;
        }

        // Point the readable front (+Z) at the head. With the corrected facing this takes the toward-head
        // direction directly; the old code negated it to compensate for LookRotation's inverse. -xlinka
        slot.GlobalRotation = FacingRotation(faceDirection, upDirection.Normalized);
        return true;
    }

    private void AlignSlotToReferenceAxis(Slot slot, float3 referenceAxis)
    {
        var root = FindUserRootSlot();
        float3 referenceGlobal = root != null ? root.LocalDirectionToGlobal(referenceAxis) : referenceAxis;
        if (referenceGlobal.Length <= 0.0001f)
        {
            return;
        }
        referenceGlobal = referenceGlobal.Normalized;

        float3 referenceInTarget = slot.GlobalDirectionToLocal(referenceGlobal);
        if (referenceInTarget.Length <= 0.0001f)
        {
            return;
        }

        float3 localAxis = GetClosestLocalAxis(referenceInTarget.Normalized);
        float3 selectedGlobal = slot.LocalDirectionToGlobal(localAxis);
        if (selectedGlobal.Length <= 0.0001f)
        {
            return;
        }

        var parent = slot.Parent;
        float3 selectedParent = parent != null ? parent.GlobalDirectionToLocal(selectedGlobal.Normalized) : selectedGlobal.Normalized;
        float3 referenceParent = parent != null ? parent.GlobalDirectionToLocal(referenceGlobal) : referenceGlobal;
        floatQ delta = RotationFromTo(selectedParent, referenceParent);
        slot.GlobalRotation = delta * slot.GlobalRotation;
    }

    private void PreserveHeldGlobalTransforms(Action action)
    {
        var slots = GetGrabbedSlots();
        var positions = new List<float3>(slots.Count);
        var rotations = new List<floatQ>(slots.Count);
        foreach (var slot in slots)
        {
            positions.Add(slot.GlobalPosition);
            rotations.Add(slot.GlobalRotation);
        }

        action();

        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].IsRemoved)
            {
                continue;
            }

            slots[i].GlobalPosition = positions[i];
            slots[i].GlobalRotation = rotations[i];
        }
    }

    public void ResetInteraction(bool releaseHeld)
    {
        if (releaseHeld)
        {
            _grabber?.ReleaseAll();
        }

        SetDesktopInputSuppression(false);
        SetScrollWheelCapture(false);
        _laser?.ArmRaySmoothing(false);
        _holdPoseFrame = long.MinValue;
        _holdPoseWrittenFrame = long.MinValue;
        _isHoldingWithLaser = false;
        _laserGrabDistance = 0f;
        _holderAxisOffset = 0f;
        _holderRotationOffset = floatQ.Identity;
        _holderRotationReference = null;
        RotationMode.Value = LaserRotationMode.AxisY;
        _primaryHeld = false;
        _prevPrimaryHeld = false;
        _secondaryHeld = false;
        _prevSecondaryHeld = false;
        _activePrimaryToolItem = null;
        _activeSecondaryToolItem = null;
        _gripHeld = false;
        _prevGripHeld = false;
    }

    private List<Slot> GetGrabbedSlots()
    {
        var slots = new List<Slot>();
        if (_grabber == null)
        {
            return slots;
        }

        foreach (var grabbable in _grabber.GrabbedObjects)
        {
            if (grabbable is Component component && component.Slot != null && !component.Slot.IsRemoved)
            {
                slots.Add(component.Slot);
            }
        }
        return slots;
    }

    private static IGrabbable? FindBestGrabbable(IInteractionTarget? target, Slot? hitSlot)
    {
        IGrabbable? best = target as IGrabbable;
        int bestPriority = best?.GrabPriority ?? int.MinValue;

        var current = hitSlot;
        while (current != null)
        {
            if (!ReferenceEquals(current, hitSlot) && current.GetComponent<SearchBlock>() != null)
            {
                break;
            }

            foreach (var grabbable in current.GetComponentsImplementing<IGrabbable>())
            {
                if (grabbable.AllowOnlyPhysicalGrab)
                {
                    continue;
                }

                if (grabbable.GrabPriority > bestPriority)
                {
                    best = grabbable;
                    bestPriority = grabbable.GrabPriority;
                }
            }

            current = current.Parent;
        }

        return best;
    }

    private float3 GetLaserRotationAxis()
    {
        return RotationMode.Value switch
        {
            LaserRotationMode.AxisX => float3.Right,
            LaserRotationMode.AxisY => float3.Up,
            LaserRotationMode.AxisZ => float3.Forward,
            _ => float3.Zero
        };
    }

    private Slot? FindUserRootSlot() => Slot?.ActiveUserRoot?.Slot;

    // floatQ.LookRotation builds its basis from matrix ROWS, so it returns the INVERSE of the intended
    // facing (see FaceLocalUser). Inverting it yields a usable facing whose local +Z points along
    // 'forward'. Held-object orientation, twist (axis offset), and align all depend on this being correct -
    // the raw LookRotation made objects face/rotate the wrong way. -xlinka
    private static floatQ FacingRotation(float3 forward, float3 up) => floatQ.LookRotation(forward, up).Inverse;

    private static floatQ? GetHeadFacingRotation(InteractionLaser laser)
    {
        var head = laser.FindHeadSlot();
        if (head == null)
        {
            return null;
        }

        float3 forward = head.GlobalRotation * float3.Backward;
        forward = ProjectOnPlane(forward, float3.Up);
        if (forward.Length <= 0.0001f)
        {
            forward = float3.Backward;
        }
        return FacingRotation(forward.Normalized, float3.Up);
    }

    private void ResolveFallbackRay(InteractionLaser laser, out float3 origin, out float3 direction)
    {
        var input = Engine.Current?.InputInterface;
        if (input != null && !input.IsVRActive)
        {
            var head = laser.FindHeadSlot();
            origin = head?.GlobalPosition ?? Slot.GlobalPosition;
            direction = (head?.GlobalRotation ?? Slot.GlobalRotation) * float3.Backward;
        }
        else
        {
            origin = laser.Slot.GlobalPosition;
            direction = -laser.Slot.Forward;
        }

        if (direction.Length <= 0.0001f)
        {
            direction = float3.Backward;
        }
        direction = direction.Normalized;
    }

    private bool CanScaleHeldObjects()
    {
        if (_grabber == null || _grabber.GrabbedObjects.Count == 0)
        {
            return false;
        }

        foreach (var grabbable in _grabber.GrabbedObjects)
        {
            if (grabbable == null || !grabbable.Scalable)
            {
                return false;
            }

            if (grabbable is Component component && component.IsDestroyed)
            {
                return false;
            }
        }
        return true;
    }

    private void ScaleHolder(Slot holder, float delta)
    {
        float factor = MathF.Max(0.05f, 1f + delta);
        holder.GlobalScale = holder.GlobalScale * factor;
    }

    private void SetDesktopInputSuppression(bool active)
    {
        if (_desktopInputSuppressed == active)
        {
            return;
        }

        _desktopInputSuppressed = active;
        UserInputState.ForFocusedLocalUser?.SetDesktopInputSuppressed(this, active);
    }

    private void SetScrollWheelCapture(bool active)
    {
        if (_scrollWheelCaptured == active)
        {
            return;
        }

        _scrollWheelCaptured = active;
        UserInputState.ForFocusedLocalUser?.SetScrollWheelCaptured(this, active);
    }

    private static float3 ProjectOnPlane(float3 vector, float3 normal)
    {
        if (normal.Length <= 0.0001f)
        {
            return vector;
        }
        normal = normal.Normalized;
        return vector - normal * float3.Dot(vector, normal);
    }

    private static float3 GetClosestLocalAxis(float3 direction)
    {
        float3 best = float3.Up;
        float bestDot = float3.Dot(best, direction);
        TestLocalAxis(float3.Down, direction, ref best, ref bestDot);
        TestLocalAxis(float3.Right, direction, ref best, ref bestDot);
        TestLocalAxis(float3.Left, direction, ref best, ref bestDot);
        TestLocalAxis(float3.Forward, direction, ref best, ref bestDot);
        TestLocalAxis(float3.Backward, direction, ref best, ref bestDot);
        return best;
    }

    private static void TestLocalAxis(float3 axis, float3 direction, ref float3 best, ref float bestDot)
    {
        float dot = float3.Dot(axis, direction);
        if (dot > bestDot)
        {
            bestDot = dot;
            best = axis;
        }
    }

    private static floatQ RotationFromTo(float3 from, float3 to)
    {
        if (from.Length <= 0.0001f || to.Length <= 0.0001f)
        {
            return floatQ.Identity;
        }

        from = from.Normalized;
        to = to.Normalized;
        float dot = System.Math.Clamp(float3.Dot(from, to), -1f, 1f);
        if (dot > 0.9999f)
        {
            return floatQ.Identity;
        }

        if (dot < -0.9999f)
        {
            float3 fallback = MathF.Abs(float3.Dot(from, float3.Up)) > 0.9f ? float3.Right : float3.Up;
            float3 axis = float3.Cross(from, fallback);
            return axis.Length <= 0.0001f ? floatQ.Identity : floatQ.AxisAngle(axis.Normalized, MathF.PI);
        }

        float3 rotationAxis = float3.Cross(from, to);
        return rotationAxis.Length <= 0.0001f
            ? floatQ.Identity
            : floatQ.AxisAngle(rotationAxis.Normalized, MathF.Acos(dot));
    }

    private static bool ReadPrimaryPressed(InteractionLaser laser)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null)
        {
            return false;
        }

        return input.Actions?.Interaction(laser.ControllerSide.Value).Primary.Held == true;
    }

    private static bool ReadGripPressed(InteractionLaser laser)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null)
        {
            return false;
        }

        return input.Actions?.Interaction(laser.ControllerSide.Value).Grab.Held == true;
    }

    private static bool ReadSecondaryPressed(InteractionLaser laser)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null)
        {
            return false;
        }

        return input.Actions?.Interaction(laser.ControllerSide.Value).Secondary.Held == true;
    }

    private static float Clamp(float value, float min, float max)
    {
        if (value < min)
        {
            return min;
        }
        if (value > max)
        {
            return max;
        }
        return value;
    }
}
