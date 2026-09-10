// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar.IK;

// Pure IK math. No engine/Component dependencies so it stays testable and
// platform agnostic. Three solvers:
//   - FabrikChain: general N-joint forward-and-backward reaching, used for
//     the spine where joint count varies.
//   - TwoBoneIK: analytic law-of-cosines solve for a 3-joint limb (arm/leg)
//     with a pole vector controlling the elbow/knee bend direction. Cleaner
//     and more stable than FABRIK for limbs.
//   - SolveHingeChain (+ SeedPlanarFold): planar N-joint chain with a signed
//     hinge range per joint, for multi-bend quadruped legs. Additive; the two
//     above are untouched by it.
// - xlinka
public static class FabrikSolver
{
    private const float Epsilon = 1e-5f;
    // Never let a two-bone limb fully straighten - a hair of bend avoids the pop/lock and the
    // unstable bend axis you get exactly at full extension.
    private const float MaxReachFraction = 0.999f;

    // Solve an N-joint chain. joints[0] is the anchored root, joints[^1] is
    // the end effector pulled toward target. lengths[i] is the rest distance
    // between joints[i] and joints[i+1] (length == joints.Length - 1).
    // joints is modified in place.
    public static void SolveChain(
        float3[] joints,
        float[] lengths,
        float3 target,
        int iterations = 10,
        float tolerance = 0.001f)
        => SolveChain(joints, lengths, target, 0f, iterations, tolerance);

    // maxBendRadians > 0 constrains each joint to a CONE around the previous bone's direction.
    //
    // Plain FABRIK has no idea a spine is a spine. It satisfies segment lengths and nothing else, so a
    // head target close to the hips is met by folding the chain into whatever hairpin reaches it - the
    // literature's own comparison of constrained against unconstrained on a humanoid is exactly this,
    // and a hunched crouch is where it shows worst. A per-joint cone is the cheapest constraint that
    // fixes it: it costs one dot product and one rotation per joint per iteration, and it cannot make
    // the chain unsolvable because the clamp only ever rotates a bone, never changes its length.
    //
    // Applied in the FORWARD pass alone. That pass walks root to end, so at joint i the previous bone
    // is already final and is a real reference to measure against; doing it in the backward pass too
    // would fight the target and stall the solve short. -xlinka
    public static void SolveChain(
        float3[] joints,
        float[] lengths,
        float3 target,
        float maxBendRadians,
        int iterations = 10,
        float tolerance = 0.001f)
    {
        int n = joints.Length;
        if (n < 2 || lengths.Length < n - 1)
            return;

        float3 root = joints[0];

        float totalLength = 0f;
        for (int i = 0; i < n - 1; i++)
            totalLength += lengths[i];

        float rootToTarget = float3.Distance(root, target);

        // Target unreachable: stretch the chain straight toward it.
        if (rootToTarget > totalLength)
        {
            for (int i = 0; i < n - 1; i++)
            {
                float r = float3.Distance(joints[i], target);
                if (r < Epsilon) continue;
                float lambda = lengths[i] / r;
                joints[i + 1] = joints[i] * (1f - lambda) + target * lambda;
            }
            return;
        }

        for (int iter = 0; iter < iterations; iter++)
        {
            // Backward: end to target, walk toward root.
            joints[n - 1] = target;
            for (int i = n - 2; i >= 0; i--)
            {
                float r = float3.Distance(joints[i + 1], joints[i]);
                if (r < Epsilon) continue;
                float lambda = lengths[i] / r;
                joints[i] = joints[i + 1] * (1f - lambda) + joints[i] * lambda;
            }

            // Forward: root back to anchor, walk toward end.
            joints[0] = root;
            for (int i = 0; i < n - 1; i++)
            {
                float r = float3.Distance(joints[i + 1], joints[i]);
                if (r < Epsilon) continue;
                float lambda = lengths[i] / r;
                joints[i + 1] = joints[i] * (1f - lambda) + joints[i + 1] * lambda;

                if (maxBendRadians > 0f && i > 0)
                {
                    var prev = joints[i] - joints[i - 1];
                    var cur = joints[i + 1] - joints[i];
                    if (prev.LengthSquared > Epsilon && cur.LengthSquared > Epsilon)
                    {
                        joints[i + 1] = joints[i] + ClampToCone(prev.Normalized, cur, maxBendRadians);
                    }
                }
            }

            if (float3.Distance(joints[n - 1], target) < tolerance)
                break;
        }
    }

    // A joint's allowed range, as the published FABRIK constraint model rather than the single symmetric
    // cone we had.
    //
    // A circular cone is the wrong shape for every joint on a body. A spine flexes forward far further
    // than it extends backward; a shoulder's reach is wider than it is tall. One angle for all four
    // directions has to be set to the SMALLEST of them or the pose goes somewhere anatomy does not, so
    // every other direction ends up over-restricted.
    //
    // Four half-angles give an irregular cone whose cross-section is an ellipse per quadrant, which is
    // the shape the paper uses and the shape joints actually have. Twist is a separate limit because it
    // is a different degree of freedom: swing is where the bone POINTS, twist is how it is ROLLED about
    // its own axis, and a cone constrains only the first. An unconstrained twist is what lets a spine
    // corkscrew while every bone direction stays perfectly legal. -xlinka
    public readonly struct JointLimit
    {
        // Swing half-angles in radians, measured from the parent bone's direction.
        public readonly float Forward;
        public readonly float Back;
        public readonly float Side;

        // Twist about the bone's own axis, radians, signed. Equal values disable the limit.
        public readonly float TwistMin;
        public readonly float TwistMax;

        public JointLimit(float forward, float back, float side, float twistMin = 0f, float twistMax = 0f)
        {
            Forward = forward;
            Back = back;
            Side = side;
            TwistMin = twistMin;
            TwistMax = twistMax;
        }

        public bool HasSwing => Forward > 0f || Back > 0f || Side > 0f;
        public bool HasTwist => TwistMax > TwistMin;

        // Symmetric cone, i.e. exactly what the old single-angle parameter meant. Kept so the uniform
        // case stays one call and nothing has to special-case it.
        public static JointLimit Cone(float halfAngle) => new(halfAngle, halfAngle, halfAngle);
    }

    // Tan blows up at a right angle, and a limit at or past 90 degrees means "unrestricted that way"
    // anyway, so the angle is held just under it. Anything wider stops being a cone and the paper treats
    // it as an inverted region, which no joint on a humanoid needs.
    private const float MaxLimitAngle = 1.5533f;

    // Clamp `dir` into an irregular cone around `axis`.
    //
    // `reference` orients the cone: its component perpendicular to the axis becomes the FORWARD
    // direction, so Forward/Back/Side mean what they say on a body rather than being arbitrary. The
    // bone's length is preserved; only its direction moves, which is what keeps the chain solvable.
    public static float3 ClampToJointLimit(float3 axis, float3 reference, float3 dir, in JointLimit limit)
    {
        float length = dir.Length;
        if (length < Epsilon || !limit.HasSwing)
            return dir;

        float3 a = axis.Normalized;
        float3 u = dir / length;

        // Cone frame: v is forward, uAxis is lateral.
        float3 v = reference - a * float3.Dot(reference, a);
        if (v.LengthSquared < Epsilon)
        {
            v = float3.Cross(a, float3.Up);
            if (v.LengthSquared < Epsilon)
                v = float3.Cross(a, float3.Right);
            if (v.LengthSquared < Epsilon)
                return dir;
        }
        v = v.Normalized;
        float3 uAxis = float3.Cross(a, v).Normalized;

        float along = float3.Dot(u, a);
        float x = float3.Dot(u, uAxis);
        float y = float3.Dot(u, v);

        // Folded past a right angle: outside every cone a humanoid joint has, and tan cannot express it.
        // Pull back to the widest limit in the plane it is already in, so the correction stays in the
        // bend plane instead of snapping the bone somewhere unrelated.
        if (along <= Epsilon)
        {
            float widest = MathF.Min(MaxLimitAngle, MathF.Max(limit.Forward, MathF.Max(limit.Back, limit.Side)));
            return ClampToCone(a, dir, widest);
        }

        float forwardLimit = MathF.Min(y >= 0f ? limit.Forward : limit.Back, MaxLimitAngle);
        float sideLimit = MathF.Min(limit.Side, MaxLimitAngle);
        if (forwardLimit <= 0f || sideLimit <= 0f)
            return ClampToCone(a, dir, MathF.Max(0f, MathF.Min(forwardLimit, sideLimit)));

        // Ellipse semi-axes on the plane at `along` up the axis.
        float semiSide = along * MathF.Tan(sideLimit);
        float semiForward = along * MathF.Tan(forwardLimit);
        if (semiSide < Epsilon || semiForward < Epsilon)
            return dir;

        float nx = x / semiSide;
        float ny = y / semiForward;
        float radial = nx * nx + ny * ny;
        if (radial <= 1f)
            return dir;

        // Radial pull back onto the ellipse. Not the true nearest point (that needs its own iterative
        // solve) but it is stable, cheap, and moves along the direction the joint was already bending,
        // which is what keeps the correction from being visible.
        float scale = 1f / MathF.Sqrt(radial);
        float3 clamped = (a * along + uAxis * (x * scale) + v * (y * scale)).Normalized;
        return clamped * length;
    }

    // Split a rotation into a twist about `axis` and the swing that remains: project the quaternion's
    // vector part onto the axis and renormalise. Used for twist limits and for anything that needs to
    // redistribute roll along a chain. -xlinka
    public static void DecomposeSwingTwist(floatQ rotation, float3 axis, out floatQ swing, out floatQ twist)
    {
        float3 a = axis.Normalized;
        var vec = new float3(rotation.x, rotation.y, rotation.z);
        float3 proj = a * float3.Dot(vec, a);

        var candidate = new floatQ(proj.x, proj.y, proj.z, rotation.w);
        float mag = candidate.x * candidate.x + candidate.y * candidate.y
                  + candidate.z * candidate.z + candidate.w * candidate.w;
        if (mag < Epsilon)
        {
            // A half turn perpendicular to the axis: the twist is genuinely undefined, so report none
            // rather than inventing one.
            twist = floatQ.Identity;
            swing = rotation;
            return;
        }

        twist = candidate.Normalized;
        swing = rotation * twist.Inverse;
    }

    // Clamp the roll of `rotation` about `axis` into [minRadians, maxRadians], leaving the swing alone.
    public static floatQ ClampTwist(floatQ rotation, float3 axis, float minRadians, float maxRadians)
    {
        if (maxRadians <= minRadians)
            return rotation;

        float3 a = axis.Normalized;
        DecomposeSwingTwist(rotation, a, out var swing, out var twist);

        // Signed roll. The quaternion's vector part points along +axis for a positive roll and against
        // it for a negative one, so the sign has to come from that rather than from w alone.
        var twistVec = new float3(twist.x, twist.y, twist.z);
        float sinHalf = twistVec.Length;
        float w = MathF.Max(-1f, MathF.Min(1f, twist.w));
        float angle = 2f * MathF.Atan2(sinHalf, MathF.Abs(w));
        if (float3.Dot(twistVec, a) < 0f)
            angle = -angle;
        if (w < 0f)
            angle = -angle;

        float clamped = MathF.Max(minRadians, MathF.Min(maxRadians, angle));
        if (MathF.Abs(clamped - angle) < 1e-6f)
            return rotation;

        return swing * floatQ.AxisAngleRad(a, clamped);
    }

    // Per-joint constrained solve. `limits[i]` applies to the bone leaving joint i, so entry 0 is unused
    // (the root bone has no parent to measure against) and the array is indexed the same way as `joints`.
    // `reference` is the body-forward direction that orients every cone.
    //
    // Same placement as the uniform version: the FORWARD pass only. That pass walks root to end, so at
    // joint i the previous bone is already final and is a real reference to measure against; constraining
    // in the backward pass too would fight the target and stall the solve short of it. -xlinka
    public static void SolveChain(
        float3[] joints,
        float[] lengths,
        float3 target,
        JointLimit[] limits,
        float3 reference,
        int iterations = 10,
        float tolerance = 0.001f)
    {
        int n = joints.Length;
        if (n < 2 || lengths.Length < n - 1)
            return;
        if (limits == null || limits.Length < n - 1)
        {
            SolveChain(joints, lengths, target, 0f, iterations, tolerance);
            return;
        }

        float3 root = joints[0];

        float totalLength = 0f;
        for (int i = 0; i < n - 1; i++)
            totalLength += lengths[i];

        if (float3.Distance(root, target) > totalLength)
        {
            for (int i = 0; i < n - 1; i++)
            {
                float r = float3.Distance(joints[i], target);
                if (r < Epsilon) continue;
                float lambda = lengths[i] / r;
                joints[i + 1] = joints[i] * (1f - lambda) + target * lambda;
            }
            return;
        }

        for (int iter = 0; iter < iterations; iter++)
        {
            joints[n - 1] = target;
            for (int i = n - 2; i >= 0; i--)
            {
                float r = float3.Distance(joints[i + 1], joints[i]);
                if (r < Epsilon) continue;
                float lambda = lengths[i] / r;
                joints[i] = joints[i + 1] * (1f - lambda) + joints[i] * lambda;
            }

            joints[0] = root;
            for (int i = 0; i < n - 1; i++)
            {
                float r = float3.Distance(joints[i + 1], joints[i]);
                if (r < Epsilon) continue;
                float lambda = lengths[i] / r;
                joints[i + 1] = joints[i] * (1f - lambda) + joints[i + 1] * lambda;

                if (i > 0)
                {
                    var prev = joints[i] - joints[i - 1];
                    var cur = joints[i + 1] - joints[i];
                    if (prev.LengthSquared > Epsilon && cur.LengthSquared > Epsilon)
                    {
                        joints[i + 1] = joints[i]
                            + ClampToJointLimit(prev.Normalized, reference, cur, limits[i]);
                    }
                }
            }

            if (float3.Distance(joints[n - 1], target) < tolerance)
                break;
        }
    }

    // Swing `dir` back onto the cone of half-angle `maxAngle` around `axis`, keeping its length. The
    // rotation is about the axis perpendicular to both, so a clamped bone stays in the plane it was
    // already bending in and the chain does not twist sideways to obey the limit. -xlinka
    private static float3 ClampToCone(float3 axis, float3 dir, float maxAngle)
    {
        float length = dir.Length;
        if (length < Epsilon)
            return dir;

        float3 unit = dir / length;
        float cos = float3.Dot(axis, unit);
        cos = MathF.Max(-1f, MathF.Min(1f, cos));
        float angle = MathF.Acos(cos);
        if (angle <= maxAngle)
            return dir;

        float3 perp = float3.Cross(axis, unit);
        if (perp.LengthSquared < Epsilon)
        {
            // Exactly opposed: any perpendicular will do, and the bone is folded straight back anyway.
            perp = float3.Cross(axis, float3.Up);
            if (perp.LengthSquared < Epsilon)
                perp = float3.Cross(axis, float3.Right);
            if (perp.LengthSquared < Epsilon)
                return dir;
        }

        return floatQ.AxisAngle(perp.Normalized, maxAngle) * axis * length;
    }

    // Reconcile a chain whose BOTH ends are pinned (e.g. hips exact + head exact): alternate a
    // backward pass from the pinned end and a forward pass from the pinned root so the interior
    // joints satisfy segment lengths without moving either end. Hard-pinning the end after a
    // one-ended solve stretches the last segment instead - the neck visibly lengthened on crouch.
    // The final end re-pin leaves any residual in the last segment, where the neck absorbs it. -xlinka
    public static void ReconcilePinnedEnds(
        float3[] joints,
        float[] lengths,
        float3 root,
        float3 end,
        int iterations = 2)
    {
        int n = joints.Length;
        if (n < 3 || lengths.Length < n - 1)
            return;

        for (int iter = 0; iter < iterations; iter++)
        {
            joints[n - 1] = end;
            for (int i = n - 2; i >= 1; i--)
            {
                float r = float3.Distance(joints[i + 1], joints[i]);
                if (r < Epsilon) continue;
                float lambda = lengths[i] / r;
                joints[i] = joints[i + 1] * (1f - lambda) + joints[i] * lambda;
            }

            joints[0] = root;
            for (int i = 0; i < n - 1; i++)
            {
                float r = float3.Distance(joints[i + 1], joints[i]);
                if (r < Epsilon) continue;
                float lambda = lengths[i] / r;
                joints[i + 1] = joints[i] * (1f - lambda) + joints[i + 1] * lambda;
            }
        }

        joints[n - 1] = end;
    }

    // HINGE-CONSTRAINED CHAIN SOLVE. Additive: nothing above this line changed, so SolveTwoBone, the
    // JointLimit SolveChain and ReconcilePinnedEnds - the three entry points the biped uses - behave
    // exactly as they did. This one exists for the quadruped limbs and anything else that is a planar
    // zigzag with a known fold side per joint.
    //
    // Why a hinge and not the cone above. A cone is measured from STRAIGHT and is the same size in every
    // direction, so a cone wide enough for a deep crouch (2*acos(0.35) = 139 degrees) also lets a hock
    // hyperextend 139 degrees the WRONG way; nothing but the seed's locality stopped an inversion, and a
    // stalled solve happily walked through it. A hinge is measured from the AUTHORED rest deflection with
    // a different allowance on each side: a little extension (anti-lock) and however much flexion the
    // crouch floor needs. The sign comes from the zigzag the artist built, so no pole is ever needed.
    //
    // Two things the plain solve above does not do, both from the same line of work the cone came from:
    //  1. The hinge PLANE is enforced in BOTH passes (subtract the normal component, one dot product per
    //     joint) but the angular RANGE only in the root-to-end pass. The backward pass is where the end
    //     reaches for the target; clamping angles there fights the target and deadlocks the chain, which
    //     is exactly the failure the model-constraints paper documents.
    //  2. The BEST iterate by end error is kept, not the last one. Constrained FABRIK is not monotone: a
    //     clamp in iteration k can leave the end further off than iteration k-1 did, and a fixed-count
    //     loop that returns whatever it ended on was leaving the paw at the residual. Exit early once the
    //     error stops moving; when it stalls short of the target, nudge every hinged joint a few degrees
    //     toward its flex side (the paper's escape from a deadlocked configuration) rather than widening
    //     the range, and keep iterating from there.
    // A final end-pin pass puts the end exactly on the target and leaves the residual in the ROOT
    // segment, which is where ReconcilePinnedEnds puts it for the spine too. -xlinka
    public struct HingeLimit
    {
        // All radians, signed about the chain's plane normal (right-hand rule), measured from the
        // parent bone direction to this bone's direction.
        public float Rest;
        public float Min;
        public float Max;
        // +1 or -1: which side the joint folds toward. Rest carries the same sign unless the joint is
        // authored nearly straight, in which case the caller decided.
        public float FlexSign;
        public bool Active;

        public float FlexLimit => FlexSign >= 0f ? Max : Min;
        public float ExtendLimit => FlexSign >= 0f ? Min : Max;

        // `extensionLimit` and `flexLimit` are in FOLD-SIDE coordinates: positive is more fold, so the
        // extension limit is the smaller number. Converted to the signed frame here so a caller reasons
        // about one joint the same way whichever way its zigzag points.
        public static HingeLimit Create(float rest, float flexSign, float extensionLimit, float flexLimit)
        {
            var h = new HingeLimit { Rest = rest, FlexSign = flexSign >= 0f ? 1f : -1f, Active = true };
            float lo = MathF.Min(extensionLimit, flexLimit);
            float hi = MathF.Max(extensionLimit, flexLimit);
            if (h.FlexSign > 0f)
            {
                h.Min = lo;
                h.Max = hi;
            }
            else
            {
                h.Min = -hi;
                h.Max = -lo;
            }
            return h;
        }
    }

    public struct HingeSolveStats
    {
        public int Iterations;
        public int BestIteration;
        public int Nudges;
        public bool Stalled;
        public bool Converged;
        public bool EndPinned;
        // End error of the best iterate BEFORE the end-pin pass; this is the honest convergence number.
        public float EndError;
    }

    // Signed angle from `from` to `to` about `axis`, both taken in the plane perpendicular to it. Rotating
    // `from` by the result about `axis` (right-hand rule, which is what floatQ.AxisAngle does) lands on `to`.
    public static float SignedAngle(float3 from, float3 to, float3 axis)
        => MathF.Atan2(float3.Dot(float3.Cross(from, to), axis), float3.Dot(from, to));

    // Rotate a unit vector lying in the plane perpendicular to `normal` (unit) by `angle` about it. Rodrigues
    // with the axis term dropped, which is exact for a vector already in the plane.
    public static float3 RotateInPlane(float3 unitInPlane, float3 normal, float angle)
        => unitInPlane * MathF.Cos(angle) + float3.Cross(normal, unitInPlane) * MathF.Sin(angle);

    // Clamp a WRAPPED angle into [min, max] by the nearer limit ON THE CIRCLE, not on the number line.
    //
    // A hock whose cannon has been dragged past 180 degrees on its fold side reads as -157, which a
    // linear clamp against [10, 139] sends to 10: the extension limit, 167 degrees away through
    // straight, when the flex limit is 64 degrees away the other way. That snap put a folding leg back
    // to nearly straight every iteration and the solve deadlocked on the spot. Measured on the circle
    // the nearer limit is the fold one, which is also the physically right answer. -xlinka
    public static float ClampAngleWrapped(float angle, float min, float max)
    {
        if (angle >= min && angle <= max)
            return angle;
        float toMin = MathF.Abs(WrapPi(angle - min));
        float toMax = MathF.Abs(WrapPi(angle - max));
        return toMin <= toMax ? min : max;
    }

    private static float WrapPi(float angle)
    {
        while (angle > MathF.PI) angle -= MathF.PI * 2f;
        while (angle < -MathF.PI) angle += MathF.PI * 2f;
        return angle;
    }

    // `limits[i]` applies to the bone LEAVING joint i, measured against the bone arriving at it. Entry 0
    // has nothing to measure against unless `hasRootReference`, in which case it is measured against
    // `rootReference` (the girdle bone that carries the chain root). `best` is caller-owned scratch of at
    // least joints.Length so this allocates nothing per call. Returns the best end error seen.
    public static float SolveHingeChain(
        float3[] joints,
        float[] lengths,
        float3 target,
        float3 planeNormal,
        HingeLimit[] limits,
        bool hasRootReference,
        float3 rootReference,
        float3[] best,
        out HingeSolveStats stats,
        int iterations = 10,
        float tolerance = 0.0005f,
        float stallNudgeRadians = 0.05f,
        int maxNudges = 2,
        float endPinMaxError = -1f)
    {
        stats = default;
        int n = joints.Length;
        if (n < 2 || lengths.Length < n - 1 || best == null || best.Length < n || limits == null || limits.Length < n - 1)
            return float.MaxValue;

        float3 root = joints[0];
        bool planar = planeNormal.LengthSquared > Epsilon;
        float3 normal = planar ? planeNormal.Normalized : float3.Zero;

        // The target is in-plane by construction on every caller so far; projecting it anyway costs one
        // dot product and means a slightly off-plane bend hint cannot make the solve chase a point the
        // constrained chain can never touch.
        if (planar)
        {
            float3 offset = target - root;
            target = root + (offset - normal * float3.Dot(offset, normal));
        }

        float3 rootRef = float3.Zero;
        bool useRootRef = hasRootReference && limits[0].Active;
        if (useRootRef)
        {
            rootRef = rootReference;
            if (planar)
                rootRef -= normal * float3.Dot(rootRef, normal);
            if (rootRef.LengthSquared < Epsilon)
                useRootRef = false;
            else
                rootRef = rootRef.Normalized;
        }

        float stallEpsilon = tolerance * 0.1f;
        float bestErr = float.MaxValue;
        float prevErr = float.MaxValue;
        int iter = 0;
        for (; iter < iterations; iter++)
        {
            // Backward: end to target, walk toward root. Plane only; no angular clamp here.
            joints[n - 1] = target;
            for (int i = n - 2; i >= 0; i--)
            {
                float3 d = joints[i] - joints[i + 1];
                if (planar)
                    d -= normal * float3.Dot(d, normal);
                float r = d.Length;
                if (r < Epsilon)
                    continue;
                joints[i] = joints[i + 1] + d * (lengths[i] / r);
            }

            // Forward: root fixed, walk toward end. Plane AND signed hinge range.
            joints[0] = root;
            ForwardHingePass(joints, lengths, limits, normal, planar, useRootRef, rootRef);

            float err = float3.Distance(joints[n - 1], target);
            if (err < bestErr)
            {
                bestErr = err;
                stats.BestIteration = iter;
                Array.Copy(joints, best, n);
            }

            if (err < tolerance)
            {
                stats.Converged = true;
                iter++;
                break;
            }

            // Stalled: no improvement worth an iteration, absolute OR relative. A chain creeping toward
            // a limit it cannot pass loses a hair per pass for ever; that is a stall, not progress.
            if (prevErr - err < MathF.Max(stallEpsilon, err * 0.02f))
            {
                if (stats.Nudges < maxNudges && stallNudgeRadians > 0f)
                {
                    NudgeTowardFlex(joints, lengths, limits, normal, planar, useRootRef, rootRef, stallNudgeRadians);
                    stats.Nudges++;
                    prevErr = float.MaxValue;
                    continue;
                }
                stats.Stalled = true;
                iter++;
                break;
            }
            prevErr = err;
        }
        stats.Iterations = iter;
        stats.EndError = bestErr;

        if (bestErr < float.MaxValue)
            Array.Copy(best, joints, n);

        // End-pin: the end goes exactly to the target, lengths are re-satisfied walking back toward the
        // root, and the root is NOT moved. Whatever cannot be absorbed by rotation sits in the root
        // segment's length. The caller writes rotations from these directions, so a length error there
        // shows only as a small shift along the root bone, never as the paw sitting off its target.
        //
        // A POLISH, so gated on the residual being small. This pass has no angular clamp, and on a
        // solve that stalled a long way from the target it is nothing but an unconstrained backward
        // pass: it happily folded a hock through itself to reach a point the constraints had refused,
        // which is the exact inversion the hinges exist to prevent. Under the gate every joint moves by
        // at most the residual, so a range is overrun by no more than residual over segment length,
        // a fraction of a degree at the tolerances in use. -xlinka
        if (endPinMaxError < 0f)
        {
            float total = 0f;
            for (int i = 0; i < n - 1; i++)
                total += lengths[i];
            endPinMaxError = total * 0.02f;
        }
        if (bestErr <= endPinMaxError)
        {
            joints[n - 1] = target;
            for (int i = n - 2; i >= 1; i--)
            {
                float3 d = joints[i] - joints[i + 1];
                if (planar)
                    d -= normal * float3.Dot(d, normal);
                float r = d.Length;
                if (r < Epsilon)
                    continue;
                joints[i] = joints[i + 1] + d * (lengths[i] / r);
            }
            joints[0] = root;
            stats.EndPinned = true;
        }

        return bestErr;
    }

    // Seed a planar hinged chain at the SPAN the target needs before the iterative solve ever runs.
    //
    // Constrained FABRIK deadlocks on a crouch, and it does so from a good-looking seed. The rest pose
    // swung onto the goal direction has the REST span; a crouch target is far shorter. The backward
    // pass moves each joint only a little per iteration, the forward pass clamps whichever joint took
    // the fold first, and the solve sits there with one joint saturated and the others at rest; a few
    // degrees of nudge do not cross a sixty-degree gap. The fix is not more iterations. Every joint has a
    // known fold side and range, so add ONE extra fold angle to all of them on their own sides, clamped
    // to their ranges, and bisect that angle until the chain's end-to-end span equals the distance to
    // the target. That is a dozen cheap rebuilds of a four-joint chain, it spreads a crouch across the
    // stifle and the hock the way the ranges say, and the same scalar run negative straightens the leg
    // for a long stride until the anti-lock margins stop it. The solve then only has to polish. Joints
    // with no range are left at their seeded angle. Returns the fold angle used. -xlinka
    public static float SeedPlanarFold(
        float3[] joints,
        float[] lengths,
        HingeLimit[] limits,
        float3 root,
        float3 target,
        float3 planeNormal,
        bool hasRootReference,
        float3 rootReference,
        int bisections = 12)
    {
        int n = joints.Length;
        if (n < 3 || lengths.Length < n - 1 || limits == null || limits.Length < n - 1)
            return 0f;
        if (planeNormal.LengthSquared < Epsilon)
            return 0f;
        float3 normal = planeNormal.Normalized;

        float3 toGoal = target - root;
        toGoal -= normal * float3.Dot(toGoal, normal);
        float dist = toGoal.Length;
        if (dist < Epsilon)
            return 0f;

        float3 d0 = joints[1] - joints[0];
        d0 -= normal * float3.Dot(d0, normal);
        d0 = d0.LengthSquared > Epsilon ? d0.Normalized : toGoal / dist;

        float3 rootRef = float3.Zero;
        if (hasRootReference && limits[0].Active)
        {
            rootRef = rootReference - normal * float3.Dot(rootReference, normal);
            rootRef = rootRef.LengthSquared > Epsilon ? rootRef.Normalized : float3.Zero;
        }

        float span0 = BuildFolded(joints, lengths, limits, root, d0, normal, rootRef, 0f);
        float delta = 0f;
        if (MathF.Abs(span0 - dist) > 1e-4f)
        {
            float lo, hi;
            if (dist < span0)
            {
                lo = 0f;
                hi = MathF.PI;
            }
            else
            {
                lo = -MathF.PI;
                hi = 0f;
            }
            float fLo = BuildFolded(joints, lengths, limits, root, d0, normal, rootRef, lo) - dist;
            float fHi = BuildFolded(joints, lengths, limits, root, d0, normal, rootRef, hi) - dist;
            if (fLo * fHi > 0f)
            {
                // No crossing inside the ranges: the chain cannot reach that span. Take the end that
                // gets nearest and let the solve report the residual.
                delta = MathF.Abs(fLo) <= MathF.Abs(fHi) ? lo : hi;
            }
            else
            {
                for (int k = 0; k < bisections; k++)
                {
                    float mid = (lo + hi) * 0.5f;
                    float fMid = BuildFolded(joints, lengths, limits, root, d0, normal, rootRef, mid) - dist;
                    if (fMid * fLo > 0f)
                    {
                        lo = mid;
                        fLo = fMid;
                    }
                    else
                    {
                        hi = mid;
                        fHi = fMid;
                    }
                }
                delta = (lo + hi) * 0.5f;
            }
        }

        BuildFolded(joints, lengths, limits, root, d0, normal, rootRef, delta);

        float3 end = joints[n - 1] - root;
        if (end.LengthSquared > Epsilon)
        {
            floatQ aim = FromToRotation(end, toGoal, normal);
            for (int i = 1; i < n; i++)
                joints[i] = root + aim * (joints[i] - root);
        }
        return delta;
    }

    // Rebuild the chain from the root with every hinged joint at rest + FlexSign * extraFold, clamped
    // to its range; unhinged joints keep a straight continuation of the previous segment. Returns the
    // root-to-end span.
    private static float BuildFolded(
        float3[] joints, float[] lengths, HingeLimit[] limits,
        float3 root, float3 d0, float3 normal, float3 rootRef, float extraFold)
    {
        int n = joints.Length;
        joints[0] = root;
        float3 dir = d0;
        if (limits[0].Active && rootRef.LengthSquared > 0.5f)
        {
            float a0 = MathF.Max(limits[0].Min, MathF.Min(limits[0].Max, limits[0].Rest + limits[0].FlexSign * extraFold));
            dir = RotateInPlane(rootRef, normal, a0);
        }
        joints[1] = root + dir * lengths[0];
        for (int i = 1; i < n - 1; i++)
        {
            if (limits[i].Active)
            {
                float a = MathF.Max(limits[i].Min, MathF.Min(limits[i].Max, limits[i].Rest + limits[i].FlexSign * extraFold));
                dir = RotateInPlane(dir, normal, a);
            }
            joints[i + 1] = joints[i] + dir * lengths[i];
        }
        return float3.Distance(joints[n - 1], root);
    }

    private static void ForwardHingePass(
        float3[] joints, float[] lengths, HingeLimit[] limits,
        float3 normal, bool planar, bool useRootRef, float3 rootRef)
    {
        int n = joints.Length;
        for (int i = 0; i < n - 1; i++)
        {
            float3 d = joints[i + 1] - joints[i];
            if (planar)
                d -= normal * float3.Dot(d, normal);
            float r = d.Length;
            if (r < Epsilon)
                continue;
            float3 u = d / r;

            if (planar && limits[i].Active)
            {
                float3 p;
                if (i > 0)
                {
                    p = joints[i] - joints[i - 1];
                    p -= normal * float3.Dot(p, normal);
                    if (p.LengthSquared < Epsilon)
                        p = float3.Zero;
                    else
                        p = p.Normalized;
                }
                else
                {
                    p = useRootRef ? rootRef : float3.Zero;
                }

                if (p.LengthSquared > 0.5f)
                {
                    float angle = SignedAngle(p, u, normal);
                    float clamped = ClampAngleWrapped(angle, limits[i].Min, limits[i].Max);
                    if (clamped != angle)
                        u = RotateInPlane(p, normal, clamped);
                }
            }

            joints[i + 1] = joints[i] + u * lengths[i];
        }
    }

    // Deadlock escape: turn every hinged joint a few degrees toward its flex side, carrying the distal
    // chain with it (relative angles preserved), so the next backward pass starts from a configuration
    // the clamps were not pinning. A few degrees is enough to leave the fixed point and small enough not
    // to be visible when the next iterations pull the end back to the target.
    private static void NudgeTowardFlex(
        float3[] joints, float[] lengths, HingeLimit[] limits,
        float3 normal, bool planar, bool useRootRef, float3 rootRef, float nudge)
    {
        if (!planar)
            return;

        int n = joints.Length;
        float3 prevOldPos = joints[0];
        float3 prevDir = useRootRef ? rootRef : float3.Zero;
        for (int i = 0; i < n - 1; i++)
        {
            float3 d = joints[i + 1] - prevOldPos;
            d -= normal * float3.Dot(d, normal);
            float r = d.Length;
            float3 u = r > Epsilon ? d / r : prevDir;
            if (u.LengthSquared < Epsilon)
            {
                prevOldPos = joints[i + 1];
                continue;
            }

            if (limits[i].Active && prevDir.LengthSquared > 0.5f)
            {
                float angle = SignedAngle(prevDir, u, normal);
                float target = ClampAngleWrapped(angle + limits[i].FlexSign * nudge, limits[i].Min, limits[i].Max);
                if (MathF.Abs(target - angle) > 1e-6f)
                    u = RotateInPlane(prevDir, normal, target);
            }

            prevOldPos = joints[i + 1];
            joints[i + 1] = joints[i] + u * lengths[i];
            prevDir = u;
        }
    }

    // Analytic 3-joint solve. root is fixed, end goes to target (clamped to
    // reach), mid (elbow/knee) bends toward pole. Returns solved mid + end.
    public static void SolveTwoBone(
        float3 root,
        float3 pole,
        float3 target,
        float upperLength,
        float lowerLength,
        out float3 mid,
        out float3 end)
    {
        float3 toTarget = target - root;
        float reach = upperLength + lowerLength;
        float maxReach = reach * MaxReachFraction;
        float dist = toTarget.Length;

        // Clamp so the law of cosines stays valid and the limb keeps a slight bend (no lock).
        float clampedDist = MathF.Max(Epsilon, MathF.Min(dist, maxReach));
        float3 dir = dist > Epsilon ? toTarget / dist : float3.Backward;

        // Angle at the root between the upper bone and the root->target line.
        float cosRoot = (upperLength * upperLength + clampedDist * clampedDist - lowerLength * lowerLength)
                        / (2f * upperLength * clampedDist);
        cosRoot = MathF.Max(-1f, MathF.Min(1f, cosRoot));
        float angleRoot = MathF.Acos(cosRoot);

        // Bend axis: perpendicular to dir, in the plane that contains the pole.
        float3 poleDir = pole - root;
        float3 bendAxis = float3.Cross(dir, poleDir);
        if (bendAxis.LengthSquared < Epsilon)
        {
            // Pole degenerate/colinear: pick a stable perpendicular.
            bendAxis = float3.Cross(dir, float3.Up);
            if (bendAxis.LengthSquared < Epsilon)
                bendAxis = float3.Cross(dir, float3.Right);
        }
        bendAxis = bendAxis.Normalized;

        float3 upperDir = floatQ.AxisAngle(bendAxis, angleRoot) * dir;
        mid = root + upperDir * upperLength;

        // End sits at clamped reach along root->target so the limb never
        // over-extends past the target (and keeps a hair of bend).
        end = root + dir * MathF.Min(dist, maxReach);

        // Re-anchor the lower bone exactly: keep mid, push end to lowerLength
        // from mid along (target - mid).
        float3 midToEnd = end - mid;
        float midEndLen = midToEnd.Length;
        if (midEndLen > Epsilon)
            end = mid + midToEnd / midEndLen * lowerLength;
    }

    // Rotation that turns `from` onto `to`. Both should be non-zero; they get
    // normalized internally. Used to swing a bone's rest direction onto the
    // solved direction. - xlinka
    public static floatQ FromToRotation(float3 from, float3 to)
    {
        float3 f = from.Normalized;
        float3 t = to.Normalized;
        float d = float3.Dot(f, t);

        if (d >= 1f - Epsilon)
            return floatQ.Identity;

        if (d <= -1f + Epsilon)
        {
            // Opposite: rotate 180 around any perpendicular axis.
            float3 axis = float3.Cross(float3.Up, f);
            if (axis.LengthSquared < Epsilon)
                axis = float3.Cross(float3.Right, f);
            return floatQ.AxisAngle(axis.Normalized, MathF.PI);
        }

        float3 c = float3.Cross(f, t);
        if (c.LengthSquared < Epsilon)
            return floatQ.Identity;
        float angle = MathF.Acos(MathF.Max(-1f, MathF.Min(1f, d)));
        return floatQ.AxisAngle(c.Normalized, angle);
    }

    // FromToRotation with an explicit fallback axis for the antiparallel (180-degree) case. Behaves IDENTICALLY to
    // the 2-arg version except when `from` and `to` are near-opposite, where the plain version would pick an
    // arbitrary perpendicular and flip the result discontinuously. Passing a stable per-limb bend axis here keeps a
    // knee/elbow from snapping to the wrong side when the reach direction reverses (hyperextend / reach behind). - xlinka
    public static floatQ FromToRotation(float3 from, float3 to, float3 fallbackAxis)
    {
        float3 f = from.Normalized;
        float3 t = to.Normalized;
        float d = float3.Dot(f, t);

        if (d >= 1f - Epsilon)
            return floatQ.Identity;

        if (d <= -1f + Epsilon)
        {
            float3 axis = fallbackAxis;
            if (axis.LengthSquared < Epsilon)
            {
                axis = float3.Cross(float3.Up, f);
                if (axis.LengthSquared < Epsilon)
                    axis = float3.Cross(float3.Right, f);
            }
            return floatQ.AxisAngle(axis.Normalized, MathF.PI);
        }

        float3 c = float3.Cross(f, t);
        if (c.LengthSquared < Epsilon)
            return floatQ.Identity;
        float angle = MathF.Acos(MathF.Max(-1f, MathF.Min(1f, d)));
        return floatQ.AxisAngle(c.Normalized, angle);
    }
}
