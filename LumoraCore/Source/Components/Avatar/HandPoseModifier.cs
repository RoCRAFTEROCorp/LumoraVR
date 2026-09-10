// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// Applies an additional per-finger curl and splay on top of an upstream finger
// pose, in position space. Curl folds each finger further toward the palm; splay
// fans it about the knuckle.
// POSITION-SPACE CURL - the faithful-but-lossy bit, documented honestly:
// Curl is naturally angular. With only wrist-local POSITIONS to work with, we curl
// a finger by rigidly rotating its node chain, joint by joint, about a flex axis at
// each joint - i.e. we reconstruct the joint angles between consecutive source
// nodes and add a flex increment at each one, then re-walk the chain from the
// finger root. This DOES distribute the added curl across the joints (a real fold),
// not a single rigid pivot, because we rebuild segment by segment.
//
// LIMITATION: the flex axis is assumed constant (wrist X, splay-adjusted) rather
// than derived from each joint's own frame - we have no per-node orientation, only
// positions. For near-planar finger curl (the common case) this is faithful; for
// fingers already twisted or splayed hard out of the palm plane the added curl can
// drift slightly off the anatomical flex plane. Segment lengths are preserved
// exactly (we reuse the measured inter-node distances), so the finger never
// stretches. Splay is an exact rigid rotation of the whole finger about wrist Y at
// the knuckle. -xlinka
[ComponentCategory("Users/Avatar/Hands")]
public sealed class HandPoseModifier : HandPoseProcessor
{
    // Upstream pose to modify.
    public readonly SyncRef<IHandPoseSourceComponent> Source = null!;

    // Extra curl per finger, 0..1-ish (added flex about the knuckle).
    public readonly Sync<float> ThumbCurl = new();
    public readonly Sync<float> IndexCurl = new();
    public readonly Sync<float> MiddleCurl = new();
    public readonly Sync<float> RingCurl = new();
    public readonly Sync<float> PinkyCurl = new();

    // Extra splay per finger, radians (fans about wrist Y at the knuckle).
    public readonly Sync<float> ThumbSplay = new();
    public readonly Sync<float> IndexSplay = new();
    public readonly Sync<float> MiddleSplay = new();
    public readonly Sync<float> RingSplay = new();
    public readonly Sync<float> PinkySplay = new();

    // Same per-joint scaling as the generator so an applied curl folds naturally.
    private const float MaxJointFlex = 1.5f;

    private float CurlFor(FingerType f) => f switch
    {
        FingerType.Thumb => ThumbCurl.Value,
        FingerType.Index => IndexCurl.Value,
        FingerType.Middle => MiddleCurl.Value,
        FingerType.Ring => RingCurl.Value,
        _ => PinkyCurl.Value,
    };

    private float SplayFor(FingerType f) => f switch
    {
        FingerType.Thumb => ThumbSplay.Value,
        FingerType.Index => IndexSplay.Value,
        FingerType.Middle => MiddleSplay.Value,
        FingerType.Ring => RingSplay.Value,
        _ => PinkySplay.Value,
    };

    protected override void Evaluate()
    {
        var src = Source?.Target;
        if (src == null)
            return;

        SetTracksMetacarpals(src.TracksMetacarpals);

        foreach (var side in Sides)
        {
            bool tracking = src.IsHandTracked(side);
            SetTracking(side, tracking);
            if (!tracking)
                continue;

            float xSign = side == Chirality.Left ? -1f : 1f;

            foreach (var finger in HandPoseNodes.Fingers)
                ModifyFinger(src, finger, side, xSign);
        }
    }

    // Rebuild one finger's chain from the source positions, adding curl/splay.
    private void ModifyFinger(IHandPoseSource src, FingerType finger, Chirality side, float xSign)
    {
        var segs = HandPoseNodes.SegmentsFor(finger);

        // Pull the source nodes that exist, in order. We only modify from the
        // finger root (proximal) outward; the metacarpal (if present) passes through.
        var nodes = new List<(BodyNode node, float3 pos)>(segs.Length);
        foreach (var seg in segs)
        {
            var node = finger.ComposeFinger(seg, side);
            if (src.TryGetFingerPosition(node, out var p))
                nodes.Add((node, p));
        }
        if (nodes.Count < 2)
        {
            // Nothing to bend - pass through whatever we have.
            foreach (var n in nodes)
                Set(n.node, n.pos);
            return;
        }

        // Find the proximal (finger root) index; metacarpal passes through unmodified.
        int rootIdx = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].node.GetFingerSegmentType() == FingerSegmentType.Proximal)
            {
                rootIdx = i;
                break;
            }
        }

        // Pass through everything up to and including the finger root.
        for (int i = 0; i <= rootIdx; i++)
            Set(nodes[i].node, nodes[i].pos);

        float3 root = nodes[rootIdx].pos;

        // Splay: rotate the whole finger about wrist Y at the root. Sign mirrors by hand.
        float splay = SplayFor(finger) * xSign;
        floatQ splayRot = floatQ.AxisAngleRad(float3.Up, splay);

        // Flex axis: wrist X carried through the splay, so it stays square to the
        // (now splayed) finger. Same axis for both hands: a mirror across the YZ plane keeps the
        // curl direction (+Z toward -Y), so mirroring the axis here un-curled the left hand. Same
        // fix as HandPoseModel. -xlinka
        float3 flexAxis = splayRot * float3.Right;

        float addedCurl = CurlFor(finger);

        // Re-walk from root: keep each source segment's length and its base direction,
        // re-apply splay to the whole chain, then add a progressive flex increment at
        // each joint. Per-joint weights match the generator's natural fold.
        float[] jointWeights = finger == FingerType.Thumb
            ? new[] { 1.0f, 1.3f }
            : new[] { 0.9f, 1.1f, 1.2f };

        float3 pos = root;
        float accumulatedFlex = 0f;
        int jointCounter = 0;
        for (int i = rootIdx; i < nodes.Count - 1; i++)
        {
            float3 segDir = nodes[i + 1].pos - nodes[i].pos;
            float len = segDir.Length;
            if (len < 1e-6f)
            {
                Set(nodes[i + 1].node, pos);
                continue;
            }
            float3 baseDir = segDir / len;

            // Apply splay to the segment direction.
            baseDir = (splayRot * baseDir).Normalized;

            // Add this joint's flex increment, progressive across the chain.
            float w = jointCounter < jointWeights.Length ? jointWeights[jointCounter] : jointWeights[^1];
            accumulatedFlex += addedCurl * MaxJointFlex * w;
            jointCounter++;

            float3 dir = (floatQ.AxisAngleRad(flexAxis, accumulatedFlex) * baseDir).Normalized;
            pos = pos + dir * len;
            Set(nodes[i + 1].node, pos);
        }
    }
}
