// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Avatar.IK;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// Computes avatar calibration reference poses (view, hand grips, feet, pelvis) directly from a
// HumanoidRig, so the avatar can be set up automatically with no manual placement.
//
// The key over a naive "copy the bone's rotation" approach is that bone rotations are arbitrary per
// model - a raw hand-bone rotation makes a useless grip. Instead each reference frame is rebuilt
// from the body geometry: facing from the spine + shoulder line, grips from the forearm + arm-bend
// plane, feet/pelvis flattened to the ground. Results feed AvatarReferencePoints that
// AvatarIK reads.
public static class AvatarCalibration
{
    // A computed reference pose, expressed in the avatar root's local space.
    public struct RefPose
    {
        public bool Valid;
        public float3 LocalPosition;
        public floatQ LocalRotation;
    }

    // Eyes sit a little in front of the head bone.
    private const float ViewForwardOffset = 0.06f;

    // Default tool-anchor offsets, in HAND GRIP local space, taken from the source platform's creator
    // defaults (it spawns its tool point 15cm out along the controller and its grab point 7.5cm out and
    // 2cm under). Our forward is local -Z, theirs is +Z, so the sign is flipped and nothing else.
    //
    // Shared with AvatarStudio on purpose: the studio spawns its draggable anchors at these offsets and
    // the auto path below bakes them at the same place, so an avatar built by dragging the markers
    // without touching the anchors is identical to one built with no markers at all. -xlinka
    public static readonly float3 ToolAnchorGripOffset = new(0f, 0f, -0.15f);
    public static readonly float3 GrabAnchorGripOffset = new(0f, -0.02f, -0.075f);

    // Derive the body's world axes from the rig: up along the spine,
    // forward the facing direction (disambiguated by the head), right
    // the shoulder line. Falls back to world axes / the head's facing when bones are missing.
    public static bool TryComputeBodyAxes(HumanoidRig rig, out float3 up, out float3 right, out float3 forward)
    {
        up = float3.Up;
        right = float3.Right;
        forward = float3.Backward;
        if (rig == null)
            return false;

        var head = rig.TryGetBone(BodyNode.Head);
        if (head == null || head.IsDestroyed)
            return false;

        var hips = rig.TryGetBone(BodyNode.Hips);
        if (hips != null && !hips.IsDestroyed)
        {
            var u = head.GlobalPosition - hips.GlobalPosition;
            if (u.LengthSquared > 1e-6f)
                up = u.Normalized;
        }

        // Forward from the rig's geometric front. GuessForwardAxis builds it from the shoulder line and
        // disambiguates the sign against the toes, so it stays correct even when the rig's left/right arm
        // bones are authored on the swapped physical side - which would otherwise reverse a raw
        // Cross(up, shoulder-line) and place every reference (view/grips/feet) 180 degrees backward. -xlinka
        float3 fwd;
        var geometricFwd = rig.GuessForwardAxis();
        if (geometricFwd.HasValue && geometricFwd.Value.LengthSquared > 1e-6f)
        {
            fwd = geometricFwd.Value;
        }
        else
        {
            var leftUpper = rig.TryGetBone(BodyNode.LeftUpperArm);
            var rightUpper = rig.TryGetBone(BodyNode.RightUpperArm);
            if (leftUpper != null && rightUpper != null && !leftUpper.IsDestroyed && !rightUpper.IsDestroyed)
            {
                var r = rightUpper.GlobalPosition - leftUpper.GlobalPosition;   // toward the avatar's right
                right = r.LengthSquared > 1e-6f ? r.Normalized : float3.Right;
                fwd = float3.Cross(up, right);                                  // points out the front
            }
            else
            {
                fwd = head.GlobalRotation * float3.Backward;
            }
        }
        if (fwd.LengthSquared < 1e-6f)
            fwd = float3.Backward;
        fwd = fwd.Normalized;

        forward = fwd;
        right = float3.Cross(forward, up);
        right = right.LengthSquared > 1e-6f ? right.Normalized : float3.Right;
        return true;
    }

    // Point the avatar root's forward (-Z) at the body's geometric front WITHOUT moving anything visibly:
    // the root frame is yawed onto the mesh front by the exact delta and every direct child is restored to
    // its world pose. Equip resets the root to identity, so the root frame IS the worn facing - this is what
    // makes an avatar walk snout-first regardless of how the model was authored or dropped in the world.
    // Exact-angle (no binary 180 guess), yaw-only (root stays upright), idempotent. Run it before references
    // are baked; re-running after only rewrites the same frame. Returns true when the frame moved. -xlinka
    public static bool AlignAvatarFacing(Slot avatarRoot, HumanoidRig rig)
    {
        if (avatarRoot == null || avatarRoot.IsDestroyed || rig == null || rig.IsDestroyed)
            return false;

        var geom = rig.GuessForwardAxis();
        if (!geom.HasValue || geom.Value.LengthSquared < 1e-6f)
            return false;

        float3 front = geom.Value;
        front.y = 0f;
        float3 rootFwd = avatarRoot.GlobalRotation * float3.Backward;
        rootFwd.y = 0f;
        if (front.LengthSquared < 1e-6f || rootFwd.LengthSquared < 1e-6f)
            return false;
        front = front.Normalized;
        rootFwd = rootFwd.Normalized;

        float delta = System.MathF.Atan2(front.x, front.z) - System.MathF.Atan2(rootFwd.x, rootFwd.z);
        while (delta > System.MathF.PI) delta -= 2f * System.MathF.PI;
        while (delta < -System.MathF.PI) delta += 2f * System.MathF.PI;
        if (System.MathF.Abs(delta) < 0.01f)
            return false;

        // Counter-restore the children so the world appearance is untouched - only the FRAME turns. The
        // references/markers/bones are all read as live transforms, so they stay consistent by construction.
        var restore = new System.Collections.Generic.List<(Slot child, float3 pos, floatQ rot)>();
        foreach (var child in avatarRoot.Children)
        {
            if (child != null && !child.IsDestroyed)
                restore.Add((child, child.GlobalPosition, child.GlobalRotation));
        }

        avatarRoot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, delta) * avatarRoot.GlobalRotation;

        foreach (var (child, pos, rot) in restore)
        {
            child.GlobalPosition = pos;
            child.GlobalRotation = rot;
        }

        Lumora.Core.Logging.Logger.Log(
            $"[IK-FACING-ALIGN] root '{avatarRoot.SlotName.Value}' frame yawed {delta * 180f / System.MathF.PI:F1} deg " +
            $"onto body front={front} (root forward was {rootFwd}); world appearance unchanged");
        return true;
    }

    // View/head reference: in front of the head bone, looking along the (horizontal) facing.
    public static RefPose ComputeView(Slot avatarRoot, HumanoidRig rig)
    {
        var head = rig?.TryGetBone(BodyNode.Head);
        if (avatarRoot == null || head == null || head.IsDestroyed)
            return default;

        TryComputeBodyAxes(rig!, out _, out _, out var forward);
        float3 flat = Flatten(forward);
        float3 worldPos = head.GlobalPosition + flat * ViewForwardOffset;
        return ToLocal(avatarRoot, worldPos, BuildRotation(flat, float3.Up));
    }

    // Hand grip reference: at the hand bone, oriented along the forearm with "up" along the arm-bend
    // plane normal - a usable controller grip frame regardless of the bone's authored rotation.
    public static RefPose ComputeHandGrip(Slot avatarRoot, HumanoidRig rig, bool rightSide)
    {
        var hand = rig?.TryGetBone(rightSide ? BodyNode.RightHand : BodyNode.LeftHand);
        if (avatarRoot == null || hand == null || hand.IsDestroyed)
            return default;

        var lower = rig!.TryGetBone(rightSide ? BodyNode.RightLowerArm : BodyNode.LeftLowerArm);
        var upper = rig.TryGetBone(rightSide ? BodyNode.RightUpperArm : BodyNode.LeftUpperArm);

        float3 handPos = hand.GlobalPosition;
        float3 pointDir;
        if (lower != null && !lower.IsDestroyed)
        {
            var d = handPos - lower.GlobalPosition;
            pointDir = d.LengthSquared > 1e-6f ? d.Normalized : hand.GlobalRotation * float3.Backward;
        }
        else
        {
            pointDir = hand.GlobalRotation * float3.Backward;
        }

        // Palm normal (the grip's "up" roll), and it needs the same left/right sign correction the arm-bend
        // fallback below already has.
        //
        // The old reasoning here was that deriving from the THUMB cancels the mirror, because the thumb
        // sits on mirror-opposite sides of the two hands. It does not, and a cross product is why: it is a
        // pseudovector, so mirroring BOTH of its inputs flips the result rather than mirroring it.
        // Measured on a perfectly symmetric rig, forearm and thumb mirror exactly and the normals come out
        // as exact opposites - (0, +0.996, +0.09) on the left against (0, -0.996, -0.09) on the right. The
        // right grip was rolled 180 degrees from the left on every rig that has a thumb bone, which is
        // every real one, and it reads as one paw held palm-up. -xlinka
        float3 palmNormal = float3.Up;
        var thumb = FirstBone(rig, rightSide
            ? new[] { BodyNode.RightThumb_Proximal, BodyNode.RightThumb_Metacarpal, BodyNode.RightThumb_Distal }
            : new[] { BodyNode.LeftThumb_Proximal, BodyNode.LeftThumb_Metacarpal, BodyNode.LeftThumb_Distal });
        if (thumb != null && !thumb.IsDestroyed)
        {
            var n = float3.Cross(pointDir, thumb.GlobalPosition - handPos);
            if (rightSide)
                n = -n;
            if (n.LengthSquared > 1e-6f)
                palmNormal = n.Normalized;
        }
        else if (upper != null && lower != null && !upper.IsDestroyed && !lower.IsDestroyed)
        {
            var a = lower.GlobalPosition - upper.GlobalPosition;
            var b = handPos - lower.GlobalPosition;
            var n = float3.Cross(a, b);
            if (rightSide)
                n = -n; // share the labeled-left roll so both grips are consistent on a labeled rig
            if (n.LengthSquared > 1e-6f)
                palmNormal = n.Normalized;
        }

        return ToLocal(avatarRoot, handPos, BuildRotation(pointDir, palmNormal));
    }

    // Foot reference: at the foot bone, flattened to face the body's forward on the ground.
    public static RefPose ComputeFoot(Slot avatarRoot, HumanoidRig rig, bool rightSide)
    {
        var foot = rig?.TryGetBone(rightSide ? BodyNode.RightFoot : BodyNode.LeftFoot);
        if (avatarRoot == null || foot == null || foot.IsDestroyed)
            return default;

        TryComputeBodyAxes(rig!, out _, out _, out var forward);
        return ToLocal(avatarRoot, foot.GlobalPosition, BuildRotation(Flatten(forward), float3.Up));
    }

    // Pelvis reference: at the hips bone, flattened to the body's forward.
    public static RefPose ComputePelvis(Slot avatarRoot, HumanoidRig rig)
    {
        var hips = rig?.TryGetBone(BodyNode.Hips);
        if (avatarRoot == null || hips == null || hips.IsDestroyed)
            return default;

        TryComputeBodyAxes(rig!, out _, out _, out var forward);
        return ToLocal(avatarRoot, hips.GlobalPosition, BuildRotation(Flatten(forward), float3.Up));
    }

    // Build (or rebuild) the "AvatarReferences" subtree with auto-aligned AvatarReferencePoints.
    // Returns the reference root, or null if the rig is unusable.
    public static Slot AutoPlaceReferences(Slot avatarRoot, HumanoidRig rig, bool feet, bool pelvis)
    {
        if (avatarRoot == null || rig == null)
            return null!;

        var existing = avatarRoot.FindChild("AvatarReferences", recursive: false);
        if (existing != null && !existing.IsDestroyed)
            existing.Destroy();

        var root = avatarRoot.AddSlot("AvatarReferences");
        root.LocalPosition.Value = float3.Zero;
        root.LocalRotation.Value = floatQ.Identity;

        Place(root, AvatarReferenceKind.View, "View", ComputeView(avatarRoot, rig));
        var leftGrip = ComputeHandGrip(avatarRoot, rig, rightSide: false);
        var rightGrip = ComputeHandGrip(avatarRoot, rig, rightSide: true);
        Place(root, AvatarReferenceKind.LeftHandGrip, "LeftHandGrip", leftGrip);
        Place(root, AvatarReferenceKind.RightHandGrip, "RightHandGrip", rightGrip);

        // Tool anchors ride off the grip frames rather than being computed from bones of their own: they
        // are defined as an offset from where the hand holds things, so a grip that came out right gives
        // anchors that come out right, and one that came out wrong is wrong in one place instead of three.
        Place(root, AvatarReferenceKind.LeftHandToolAnchor, "LeftHandToolAnchor", OffsetInPose(leftGrip, ToolAnchorGripOffset));
        Place(root, AvatarReferenceKind.RightHandToolAnchor, "RightHandToolAnchor", OffsetInPose(rightGrip, ToolAnchorGripOffset));
        Place(root, AvatarReferenceKind.LeftHandGrabAnchor, "LeftHandGrabAnchor", OffsetInPose(leftGrip, GrabAnchorGripOffset));
        Place(root, AvatarReferenceKind.RightHandGrabAnchor, "RightHandGrabAnchor", OffsetInPose(rightGrip, GrabAnchorGripOffset));

        if (feet)
        {
            Place(root, AvatarReferenceKind.LeftFoot, "LeftFoot", ComputeFoot(avatarRoot, rig, rightSide: false));
            Place(root, AvatarReferenceKind.RightFoot, "RightFoot", ComputeFoot(avatarRoot, rig, rightSide: true));
        }
        if (pelvis)
            Place(root, AvatarReferenceKind.Pelvis, "Pelvis", ComputePelvis(avatarRoot, rig));

        return root;
    }

    private static void Place(Slot referenceRoot, AvatarReferenceKind kind, string name, in RefPose pose)
    {
        if (!pose.Valid)
            return;
        // referenceRoot has an identity local transform under the avatar, so avatar-local == here.
        var slot = referenceRoot.AddSlot(name);
        slot.LocalPosition.Value = pose.LocalPosition;
        slot.LocalRotation.Value = pose.LocalRotation;
        slot.AttachComponent<AvatarReferencePoint>().Kind.Value = kind;
    }

    // Slide a pose along its OWN axes, keeping its rotation. Both are already avatar-local, so the
    // result drops straight into the same reference subtree.
    private static RefPose OffsetInPose(in RefPose pose, in float3 localOffset)
    {
        if (!pose.Valid)
            return default;
        return new RefPose
        {
            Valid = true,
            LocalPosition = pose.LocalPosition + pose.LocalRotation * localOffset,
            LocalRotation = pose.LocalRotation,
        };
    }

    private static RefPose ToLocal(Slot avatarRoot, in float3 worldPos, in floatQ worldRot)
        => new RefPose
        {
            Valid = true,
            LocalPosition = avatarRoot.GlobalPointToLocal(worldPos),
            LocalRotation = avatarRoot.GlobalRotation.Inverse * worldRot,
        };

    // First existing (non-destroyed) bone among the candidates, or null.
    private static Slot? FirstBone(HumanoidRig rig, BodyNode[] nodes)
    {
        foreach (var node in nodes)
        {
            var bone = rig.TryGetBone(node);
            if (bone != null && !bone.IsDestroyed)
                return bone;
        }
        return null;
    }

    private static float3 Flatten(float3 v)
    {
        v.y = 0f;
        return v.LengthSquared > 1e-6f ? v.Normalized : float3.Backward;
    }

    // Rotation whose facing axis (Backward, matching the IK/slot convention) points along forward and
    // whose up rolls toward up. Built from two swings to avoid floatQ.LookRotation (returns inverse).
    private static floatQ BuildRotation(float3 forward, float3 up)
    {
        if (forward.LengthSquared < 1e-8f)
            return floatQ.Identity;
        forward = forward.Normalized;

        floatQ q1 = FabrikSolver.FromToRotation(float3.Backward, forward);
        float3 curUp = q1 * float3.Up;
        float3 upProj = up - forward * float3.Dot(up, forward);
        if (upProj.LengthSquared < 1e-6f)
            return q1;
        floatQ q2 = FabrikSolver.FromToRotation(curUp, upProj.Normalized);
        return q2 * q1;
    }
}
