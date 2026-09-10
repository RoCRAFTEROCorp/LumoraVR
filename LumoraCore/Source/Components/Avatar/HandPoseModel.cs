// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// A small parametric hand model that generates wrist-local finger-node positions
// from a per-finger curl (and splay) angle. Used to synthesize preset poses and
// to apply curl/splay offsets, all in position space.
// Kinematic model with eyeballed numbers. Coordinate convention (purely internal:
// HandPoseDriver consumes only direction-between-consecutive-nodes, so the absolute
// frame just has to be self-consistent):
//   +Z  fingers extend away from the wrist
//   -Y  palm-facing (curl flexes fingertips from +Z toward -Y)
//   +X  splay spread axis (toward the thumb side / away)
// A finger is a chain of straight segments. Each joint adds a cumulative flex
// rotation about the splay-adjusted knuckle X axis, so successive segments fold
// like real flexion (proximal bends a little, then intermediate and distal bend
// progressively more for a fist). Splay is a single rotation of the whole finger
// about Y at the knuckle.
//
// LIMITATION: lengths and the rest fan-out are approximate, hand-tuned placeholders
// in normalized wrist units - they are NOT measured anthropometry. Because the
// consumer uses only segment DIRECTIONS, absolute scale is irrelevant; only the
// relative segment lengths and the angles matter, and those are eyeballed. Good
// enough to read as idle/point/fist; refine against real hand data if needed.
internal static class HandPoseModel
{
    // Per finger: knuckle (metacarpal-proximal joint) offset from the wrist and the
    // three/four segment lengths, in normalized wrist units. Order: Thumb, Index,
    // Middle, Ring, Pinky. Approximate placeholders. -xlinka
    private readonly struct FingerSpec
    {
        public readonly float3 Knuckle;   // proximal joint position relative to wrist
        public readonly float RestSplay;  // neutral fan-out about Y, radians (+ = toward pinky side)
        public readonly float Meta;        // metacarpal length (wrist -> knuckle contribution along finger)
        public readonly float Proximal;
        public readonly float Intermediate; // 0 for thumb
        public readonly float Distal;

        public FingerSpec(float3 knuckle, float restSplay, float meta, float proximal, float intermediate, float distal)
        {
            Knuckle = knuckle;
            RestSplay = restSplay;
            Meta = meta;
            Proximal = proximal;
            Intermediate = intermediate;
            Distal = distal;
        }
    }

    // Knuckles fan across X; the hand extends along +Z. Thumb sits lower (-Y) and to
    // the side, angled out. Numbers are normalized placeholders, eyeballed. -xlinka
    private static readonly FingerSpec Thumb =
        new(new float3(0.34f, -0.10f, 0.18f), -0.70f, 0.10f, 0.16f, 0f, 0.12f);
    private static readonly FingerSpec Index =
        new(new float3(0.16f, 0f, 0.34f), -0.12f, 0.12f, 0.16f, 0.11f, 0.09f);
    private static readonly FingerSpec Middle =
        new(new float3(0.05f, 0f, 0.36f), 0f, 0.12f, 0.18f, 0.12f, 0.10f);
    private static readonly FingerSpec Ring =
        new(new float3(-0.06f, 0f, 0.34f), 0.12f, 0.12f, 0.16f, 0.11f, 0.09f);
    private static readonly FingerSpec Pinky =
        new(new float3(-0.16f, 0f, 0.30f), 0.26f, 0.11f, 0.13f, 0.09f, 0.08f);

    private static FingerSpec SpecFor(FingerType finger) => finger switch
    {
        FingerType.Thumb => Thumb,
        FingerType.Index => Index,
        FingerType.Middle => Middle,
        FingerType.Ring => Ring,
        _ => Pinky,
    };

    // Generate the wrist-local positions for one finger.
    // curl01 0 = straight, 1 = full fist. extraSplay
    // is added to the finger's rest splay (radians). Writes positions through
    // emit keyed by node.
    public static void GenerateFinger(
        FingerType finger, Chirality side, float curl01, float extraSplay,
        Action<BodyNode, float3> emit)
    {
        var spec = SpecFor(finger);
        bool hasIntermediate = finger != FingerType.Thumb;

        // Mirror X for the left hand so both hands fan/splay outward symmetrically.
        float xSign = side == Chirality.Left ? -1f : 1f;
        float3 knuckle = new float3(spec.Knuckle.x * xSign, spec.Knuckle.y, spec.Knuckle.z);

        // Splay: rotate the finger about wrist Y at the knuckle. Sign mirrors with the hand.
        float splay = (spec.RestSplay + extraSplay) * xSign;
        floatQ splayRot = floatQ.AxisAngleRad(float3.Up, splay);

        // Forward direction the finger points before curl (knuckle -> tip), splayed.
        float3 forward = splayRot * float3.Forward;
        // Flexion axis: perpendicular to forward in the palm plane. With forward
        // along +Z and palm facing -Y, flexion is about X; carry the splay so the
        // axis stays square to the splayed finger.
        //
        // NOT mirrored with the hand. A left hand is the right hand reflected across the YZ plane,
        // and a reflection leaves Y and Z alone: both hands curl from +Z toward -Y. Negating the axis
        // here as well turned the left hand into the RIGHT hand rotated half a turn about the finger
        // axis (thumb at -X, curl toward +Y), which no rotation of a bone frame can ever turn back
        // into a left hand. Verified numerically: with the sign in, the left middle finger's first
        // segment came out (0, +0.48, 0.88), the right's (0, -0.48, 0.88). -xlinka
        float3 flexAxis = splayRot * float3.Right;

        // Metacarpal node sits at the wrist end of the metacarpal bone (origin side);
        // we place it slightly back along -forward from the knuckle so the
        // metacarpal->proximal direction reads correctly.
        float3 metaPos = knuckle - forward * spec.Meta;
        Emit(emit, finger, FingerSegmentType.Metacarpal, side, metaPos);

        // Walk the joints, accumulating flex. Each joint bends a bit more than the
        // last for a natural fold; distal curls most.
        float[] lengths = hasIntermediate
            ? new[] { spec.Proximal, spec.Intermediate, spec.Distal }
            : new[] { spec.Proximal, spec.Distal };
        float[] jointWeights = hasIntermediate
            ? new[] { 0.9f, 1.1f, 1.2f }   // proximal, intermediate, distal share of the curl
            : new[] { 1.0f, 1.3f };        // thumb: proximal, distal

        var segTypes = hasIntermediate
            ? new[] { FingerSegmentType.Proximal, FingerSegmentType.Intermediate, FingerSegmentType.Distal, FingerSegmentType.Tip }
            : new[] { FingerSegmentType.Proximal, FingerSegmentType.Distal, FingerSegmentType.Tip };

        // Full fist folds each finger through roughly a right angle per major joint.
        const float MaxJointFlex = 1.5f; // radians at curl01 = 1 before per-joint weighting

        float3 pos = knuckle;
        Emit(emit, finger, segTypes[0], side, pos); // proximal node at the knuckle

        float3 dir = forward;
        float accumulatedAngle = 0f;
        for (int i = 0; i < lengths.Length; i++)
        {
            accumulatedAngle += curl01 * MaxJointFlex * jointWeights[i];
            // Rotate the running direction about the flex axis toward the palm (-Y).
            var bend = floatQ.AxisAngleRad(flexAxis, accumulatedAngle);
            dir = (bend * forward).Normalized;
            pos = pos + dir * lengths[i];
            Emit(emit, finger, segTypes[i + 1], side, pos);
        }
    }

    private static void Emit(Action<BodyNode, float3> emit, FingerType finger,
        FingerSegmentType seg, Chirality side, float3 pos)
        => emit(finger.ComposeFinger(seg, side), pos);

    // Generate a whole hand at a uniform curl/splay.
    public static void GenerateHand(Chirality side, float curl01, float extraSplay, Action<BodyNode, float3> emit)
    {
        foreach (var finger in HandPoseNodes.Fingers)
            GenerateFinger(finger, side, curl01, extraSplay, emit);
    }
}
