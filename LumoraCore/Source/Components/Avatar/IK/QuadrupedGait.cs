// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar.IK;

// Which corners move together, and when.
public enum QuadGaitMode
{
    // Diagonal pairs. Front-right with rear-left, then front-left with rear-right. The default, and
    // what a fox or a horse does at speed.
    Trot = 0,
    // Four-beat lateral sequence. Slower, always three feet down, for a careful walk.
    Walk = 1,
    // Lateral pairs - both left legs, then both right. Camels and some horses.
    Pace = 2,
    // Both fronts, then both rears. A bounding run.
    Bound = 3,
}

// Per-limb gait state.
//
// Structurally the biped's FootGait minus the per-limb Speed. The biped RANDOMIZES that field on every
// step specifically to break lockstep between its two feet, which is the exact opposite of what a
// trot needs: the two members of a pair lift on the same frame and swing at the same rate, so they
// land together. -xlinka
internal struct QuadLimbGait
{
    public bool Init;
    public bool Stepping;
    public float3 Planted;
    public float3 StepFrom;
    public float3 StepTo;
    public floatQ PlantedRot;
    public floatQ StepFromRot;
    public floatQ StepToRot;
    public float StepLift;
    public float3 GroundNormal;
    public floatQ CurrentRot;
    public float3 CurrentPos;
    public float SwingT;
    // Flat distance from the planted point to this frame's home point. Drives the step trigger and
    // the settle, and is what the diagnostic line prints.
    public float DistFromHome;
    // Root-to-planted distance over the limb's stretch limit. Over 1 means the leg is being pulled
    // near full extension and must step whatever the distance says.
    public float Stretch;
    public float HomeStretch;
}

internal struct QuadGaitInput
{
    public float Dt;
    public float3 BodyCentre;
    public float3 BodyForward;
    public float BodyLength;
    public float StrideLength;
    public float DutyFactor;
    public float StepHeight;
    public float StepThreshold;
    public float StepAngleThresholdDeg;
    // Seconds of velocity lookahead on the landing point. Zero lead at rest by construction.
    public float StepPrediction;
    // Longest a swing may take, seconds. Faster when the body outruns it.
    public float StepDuration;
    public float MaxStepVelocity;
    public float GroundY;
    public bool Grounded;
    public QuadGaitMode Mode;
    public float3[] Home;               // [4] body-fixed home point per limb, y = root plane
    public float3[] LimbRoots;          // [4] live limb root, for the stretch test
    public float[] LimbReach;           // [4] root-to-paw distance the leg may not exceed, 0 = no test
    public Slot?[] Proxies;             // [4]
    public float ConformScale;
}

// The four-limb scheduler.
//
// A limb steps because it NEEDS to, not because a clock said it may. The trigger is distance from a
// body-fixed home point, or the leg being pulled near full extension, or the paw facing having drifted
// too far from the body; the only cross-limb rules are the pairing rule (nothing outside the lifting
// limb's pair group may be in the air) and a floor of two paws on the ground. The phase clock survives
// only to ORDER which group goes first when several want to lift on the same frame.
//
// It used to be the other way round. Steps were gated by a phase window that advanced with distance
// travelled, and the window was open for the first half of a swing slot only: with a 0.32 stride and a
// 0.42 duty the gate was open for 0.07 of travel and shut for 0.26, so a limb that missed its opening
// was dragged a quarter of a metre behind home before it was allowed to go. Standing still the clock
// froze, no planted limb could ever step, and a limb caught mid-swing held its arc. Measured on a real
// avatar: front paws planted well ahead of the shoulders, rears stretched out behind the hips, and a
// log reading vel=0.000 advance=0.0000 phase=0.063 for as long as the animal stood there.
//
// Swings are TIME-based for the same reason: progress used to be read off the clock, so a swing begun
// while moving could not finish once the body stopped. Now a swing always completes. -xlinka
internal sealed class QuadrupedGait
{
    // Indexed by QuadLimbId: FrontLeft, FrontRight, RearLeft, RearRight. Two limbs sharing an offset
    // form a pair group and are allowed in the air together.
    private static readonly float[][] PhaseOffsets =
    {
        new[] { 0.5f, 0.0f, 0.0f, 0.5f },     // Trot:  FR + RL together, then FL + RR
        new[] { 0.5f, 0.0f, 0.25f, 0.75f },   // Walk:  four-beat lateral sequence, one at a time
        new[] { 0.0f, 0.5f, 0.0f, 0.5f },     // Pace:  both left, then both right
        new[] { 0.0f, 0.0f, 0.5f, 0.5f },     // Bound: both front, then both rear
    };

    // Fraction of the cycle a limb spends in the air at speed. Only feeds the swing rate floor while
    // moving fast; trot is capped at 0.5 so two pairs can never both be airborne.
    private static readonly float[] DutyFactors = { 0.42f, 0.28f, 0.42f, 0.48f };

    public float Phase;
    public readonly QuadLimbGait[] Limbs = new QuadLimbGait[QuadLimb.Count];

    private float3 _smoothedVel;
    private float3 _prevBodyXZ;
    private bool _hasPrev;
    private float3 _prevForward = float3.Backward;
    private readonly List<Slot> _rayExclude = new(1);

    private readonly float3[] _landing = new float3[QuadLimb.Count];
    private readonly bool[] _wants = new bool[QuadLimb.Count];
    private readonly bool[] _wantsRelaxed = new bool[QuadLimb.Count];
    private float _logAccum;

    private const float StepRetarget = 10f;
    private const float GroundProbeUp = 0.5f;
    private const float GroundProbeDown = 1.0f;
    private const float GaitVelocitySmoothing = 10f;
    private const float SwingToeDip = 0.22f;
    private const float GlideFollow = 12f;

    // The relaxed trigger, as a fraction of StepThreshold. Two uses: the standing settle (a stopped
    // animal steps any limb further than this from home, one at a time, until none is) and the mate
    // join (a limb whose pair-mate has just lifted goes with it at this distance rather than waiting
    // for the full threshold, which is what keeps a diagonal pair welded through turns).
    private const float RelaxedStepFraction = 0.5f;

    // A mate may join a swing already in the air only this far into it; later than that it would be
    // alone in the air after the leader lands and hold the other group off for most of a swing.
    private const float MateJoinWindow = 0.35f;

    // Below this the animal counts as standing. Body-relative, so a mouse and a horse both settle,
    // with an absolute floor for a rig that measured tiny.
    private const float SettleSpeedPerLength = 0.06f;
    private const float MinSettleSpeed = 0.03f;

    private World? _world;
    private Slot? _userSlot;

    public void Bind(World? world, Slot? userSlot)
    {
        _world = world;
        _userSlot = userSlot;
    }

    public float LocalPhase(QuadLimbId id, QuadGaitMode mode)
        => Frac(Phase - PhaseOffsets[(int)mode][(int)id]);

    public void Tick(in QuadGaitInput input)
    {
        float dt = MathF.Max(input.Dt, 1e-5f);
        var mode = input.Mode;
        float duty = System.Math.Clamp(
            input.DutyFactor > 0f ? input.DutyFactor : DutyFactors[(int)mode],
            0.1f,
            mode == QuadGaitMode.Trot ? 0.5f : 0.9f);

        AdvancePhase(input, dt);

        float speed = _smoothedVel.Length;
        float3 lead = _smoothedVel * MathF.Max(input.StepPrediction, 0f);
        float settleSpeed = MathF.Max(MinSettleSpeed, SettleSpeedPerLength * MathF.Max(input.BodyLength, 0.1f));
        bool standing = speed < settleSpeed;
        float threshold = MathF.Max(input.StepThreshold, 0.001f);
        float angleThreshold = MathF.Max(input.StepAngleThresholdDeg, 1f);
        floatQ desiredRot = BuildPawRotation(input.BodyForward);

        // Landing point per limb = home pushed along the travel direction, on the probed ground.
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            ref var gait = ref Limbs[i];
            float3 home = input.Home[i];
            float3 landing = home + lead;

            // A MISS is not the same as "the floor is at the root plane" - it matters while the body
            // is above the contact floor - so the bool is carried.
            bool hit = TryGroundHeight(landing.x, landing.z, input.GroundY, out float groundY, out float3 normal);
            float conform = hit ? GroundConformance(groundY - input.GroundY, input.ConformScale, input.BodyLength) : 0f;
            gait.GroundNormal = BlendNormal(normal, conform);
            landing.y = hit ? input.GroundY + (groundY - input.GroundY) * conform : input.GroundY;
            _landing[i] = landing;

            if (!input.Grounded)
            {
                GlideLimb(ref gait, landing, desiredRot, MathF.Min(dt * GlideFollow, 1f), input.Proxies[i]);
                gait.StepLift = 0f;
                gait.CurrentPos = gait.Planted;
                gait.CurrentRot = gait.PlantedRot;
                gait.DistFromHome = FlatDistance(gait.Planted, home);
                _wants[i] = false;
                _wantsRelaxed[i] = false;
                continue;
            }

            if (!gait.Init)
            {
                gait.Planted = landing;
                gait.PlantedRot = desiredRot;
                gait.CurrentPos = landing;
                gait.CurrentRot = desiredRot;
                gait.Init = true;
            }

            gait.DistFromHome = FlatDistance(gait.Planted, home);
            gait.Stretch = input.LimbReach[i] > 1e-4f
                ? float3.Distance(input.LimbRoots[i], gait.Planted) / input.LimbReach[i]
                : 0f;

            float angle = FlatAngleDeg(PawForward(gait.PlantedRot), PawForward(desiredRot));
            bool turned = angle > angleThreshold;

            // Over-stretch may only trigger a step that would actually RELIEVE it.
            //
            // Measured on a real avatar standing still: one rear leg re-stepped forever (s=1 on every
            // sample, swingT restarting each time) with x=1.02 while sitting exactly at home, d=0.000.
            // Its home point was itself beyond the leg's reach, so every settle step landed back on the
            // same unreachable spot and immediately qualified again. A step is only worth taking on
            // stretch grounds if home is closer to the root than where the paw already is; otherwise
            // the stretch is the home point's problem and the solver's stretch handles it. -xlinka
            gait.HomeStretch = input.LimbReach[i] > 1e-4f
                ? float3.Distance(input.LimbRoots[i], home) / input.LimbReach[i]
                : 0f;
            bool overStretched = gait.Stretch > 1f && gait.HomeStretch < gait.Stretch - 0.02f;
            bool planted = !gait.Stepping;
            _wants[i] = planted && (gait.DistFromHome > threshold || turned || overStretched);
            _wantsRelaxed[i] = planted && (gait.DistFromHome > threshold * RelaxedStepFraction || turned || overStretched);
        }

        if (input.Grounded)
        {
            Schedule(mode, standing, desiredRot);

            // Swing rate: never slower than the authored duration, faster when the body is covering a
            // stride quicker than that duration allows for. The floor is what lets a swing finish after
            // the body has stopped under it.
            float stride = MathF.Max(input.StrideLength, 0.05f);
            float rate = MathF.Max(1f / MathF.Max(input.StepDuration, 0.05f), speed / MathF.Max(duty * stride, 1e-3f));

            for (int i = 0; i < QuadLimb.Count; i++)
                StepLimb(ref Limbs[i], _landing[i], desiredRot, dt, rate, input.StepHeight, input.Proxies[i]);
        }

        LogThrottled(input, dt, speed, lead.Length, threshold, standing);
    }

    // Decide which limbs lift this frame.
    //
    // Standing: the settle. One limb at a time, the one furthest from home first, at the relaxed
    // threshold. The animal ends up with every paw within half a threshold of its home point, which is
    // the neutral stance, and because a settle step lands with zero lead it converges rather than
    // chasing.
    //
    // Moving: pair groups. If nothing is in the air, the limb that wants to go and is earliest on the
    // phase clock leads, and its group-mates go with it at the relaxed threshold. If a group is already
    // in the air, its late mates may still join early in the swing; nothing from another group may.
    // Two paws stay down no matter what. -xlinka
    private void Schedule(QuadGaitMode mode, bool standing, floatQ desiredRot)
    {
        if (standing)
        {
            if (AnyStepping())
                return;

            int pick = -1;
            float worst = -1f;
            for (int i = 0; i < QuadLimb.Count; i++)
            {
                if (!_wantsRelaxed[i] || !Limbs[i].Init)
                    continue;
                if (Limbs[i].DistFromHome > worst)
                {
                    worst = Limbs[i].DistFromHome;
                    pick = i;
                }
            }
            if (pick >= 0 && PlantedWithout(pick) >= 2)
                Lift(pick, desiredRot);
            return;
        }

        int group = SteppingGroup(mode);
        if (group < 0)
        {
            int leader = -1;
            float bestPhase = 2f;
            for (int i = 0; i < QuadLimb.Count; i++)
            {
                if (!_wants[i] || !Limbs[i].Init)
                    continue;
                float phase = LocalPhase((QuadLimbId)i, mode);
                if (phase < bestPhase)
                {
                    bestPhase = phase;
                    leader = i;
                }
            }
            if (leader < 0 || PlantedWithout(leader) < 2)
                return;

            Lift(leader, desiredRot);
            group = GroupOf(leader, mode);
            for (int i = 0; i < QuadLimb.Count; i++)
            {
                if (i == leader || Limbs[i].Stepping || !Limbs[i].Init || GroupOf(i, mode) != group)
                    continue;
                if (_wantsRelaxed[i] && PlantedWithout(i) >= 2)
                    Lift(i, desiredRot);
            }
            return;
        }

        float furthest = 0f;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (Limbs[i].Stepping)
                furthest = MathF.Max(furthest, Limbs[i].SwingT);
        }
        if (furthest >= MateJoinWindow)
            return;

        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (Limbs[i].Stepping || !Limbs[i].Init || GroupOf(i, mode) != group)
                continue;
            if (_wantsRelaxed[i] && PlantedWithout(i) >= 2)
                Lift(i, desiredRot);
        }
    }

    private void Lift(int index, floatQ desiredRot)
    {
        ref var gait = ref Limbs[index];
        gait.Stepping = true;
        gait.SwingT = 0f;
        gait.StepFrom = gait.Planted;
        gait.StepFromRot = gait.PlantedRot;
        gait.StepTo = _landing[index];
        gait.StepToRot = desiredRot;
    }

    private bool AnyStepping()
    {
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (Limbs[i].Stepping)
                return true;
        }
        return false;
    }

    // Pair groups are the phase offsets: limbs sharing one lift together. Encoded as the offset in
    // hundredths so a group is an int and two groups compare with ==.
    private static int GroupOf(int index, QuadGaitMode mode)
        => (int)MathF.Round(PhaseOffsets[(int)mode][index] * 100f);

    private int SteppingGroup(QuadGaitMode mode)
    {
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (Limbs[i].Stepping)
                return GroupOf(i, mode);
        }
        return -1;
    }

    // The clock. Advanced by how far the body actually travelled, plus a turn term so spinning in place
    // still orders the groups. It no longer gates anything; it only breaks ties between groups that
    // want to lift on the same frame.
    private void AdvancePhase(in QuadGaitInput input, float dt)
    {
        float3 bodyXZ = new float3(input.BodyCentre.x, 0f, input.BodyCentre.z);
        if (!_hasPrev)
        {
            _prevBodyXZ = bodyXZ;
            _prevForward = input.BodyForward;
            _hasPrev = true;
            return;
        }

        float3 delta = bodyXZ - _prevBodyXZ;
        _prevBodyXZ = bodyXZ;

        float3 instant = delta / dt;
        if (instant.Length > input.MaxStepVelocity)
            instant = instant.Normalized * input.MaxStepVelocity;
        float k = MathF.Min(dt * GaitVelocitySmoothing, 1f);
        _smoothedVel = float3.Lerp(_smoothedVel, instant, k);

        float yawDelta = MathF.Abs(SignedFlatAngle(_prevForward, input.BodyForward));
        _prevForward = input.BodyForward;

        float stride = MathF.Max(input.StrideLength, 0.05f);
        float travel = _smoothedVel.Length * dt + yawDelta * MathF.Max(input.BodyLength, 0.1f) * 0.5f;

        // Cap the per-frame advance so a teleport or a frame spike cannot skip a whole cycle and
        // scramble which group is first.
        float advance = MathF.Min(travel / stride, 0.25f);
        Phase = Frac(Phase + advance);
    }

    // How many limbs would still be down if THIS one lifted. Counts the others only, because the
    // question is what remains after the step starts, not what is down right now.
    private int PlantedWithout(int lifting)
    {
        int planted = 0;
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            if (i == lifting)
                continue;
            if (Limbs[i].Init && !Limbs[i].Stepping)
                planted++;
        }
        return planted;
    }

    // One limb's arc, on its own clock.
    private void StepLimb(ref QuadLimbGait gait, float3 landing, floatQ desiredRot, float dt, float rate,
        float stepHeight, Slot? proxy)
    {
        if (gait.Stepping)
        {
            gait.SwingT = MathF.Min(gait.SwingT + dt * rate, 1f);
            float t = gait.SwingT;

            // Track the landing point: eased early in the swing so ground-probe jumps do not jerk the
            // paw, exact by touchdown. A constant-rate lerp alone lagged the landing by speed/rate,
            // which at a walk ate most of the lead it was supposed to land with.
            float k = MathF.Max(MathF.Min(dt * StepRetarget, 1f), t * t);
            gait.StepTo = float3.Lerp(gait.StepTo, landing, k);
            gait.StepToRot = floatQ.Slerp(gait.StepToRot, desiredRot, k).Normalized;

            if (t >= 1f)
            {
                gait.Planted = gait.StepTo;
                gait.PlantedRot = gait.StepToRot;
                gait.Stepping = false;
                gait.StepLift = 0f;
                gait.CurrentPos = gait.Planted;
                gait.CurrentRot = gait.PlantedRot;
                WriteProxy(proxy, gait.Planted);
                return;
            }

            float ease = InOutSine(t);
            float3 pos = float3.Lerp(gait.StepFrom, gait.StepTo, ease);
            float liftArc = MathF.Sin(t * MathF.PI);
            pos.y += liftArc * stepHeight;

            floatQ rot = floatQ.Slerp(gait.StepFromRot, gait.StepToRot, ease).Normalized;
            float3 dipAxis = float3.Cross(PawForward(rot), float3.Up);
            if (dipAxis.LengthSquared > 1e-8f)
                rot = (floatQ.AxisAngleRad(dipAxis.Normalized, -SwingToeDip * liftArc) * rot).Normalized;

            gait.StepLift = liftArc;
            gait.CurrentPos = pos;
            gait.CurrentRot = rot;
            WriteProxy(proxy, pos);
            return;
        }

        // Planted. A planted paw relaxes slowly toward the body facing so a turn does not leave it
        // splayed until its next step comes round.
        RelaxPlanted(ref gait, desiredRot, dt);
        gait.StepLift = 0f;
        gait.CurrentPos = gait.Planted;
        gait.CurrentRot = gait.PlantedRot;
        WriteProxy(proxy, gait.Planted);
    }

    private static void RelaxPlanted(ref QuadLimbGait gait, floatQ desiredRot, float dt)
    {
        const float RelaxMinAngleDeg = 12f;
        const float RelaxSpeedDeg = 160f;
        float angle = FlatAngleDeg(PawForward(gait.PlantedRot), PawForward(desiredRot));
        if (angle <= RelaxMinAngleDeg)
            return;
        float step = RelaxSpeedDeg * dt;
        if (step <= 0f || angle < 1e-3f)
            return;
        gait.PlantedRot = floatQ.Slerp(gait.PlantedRot, desiredRot, MathF.Min(step / angle, 1f)).Normalized;
    }

    // Airborne: no arcs, the paw eases toward its landing point and the planted state follows, so
    // stepping resumes on touch-down from wherever the paw actually is instead of snapping.
    private static void GlideLimb(ref QuadLimbGait gait, float3 landing, floatQ desiredRot, float k, Slot? proxy)
    {
        if (!gait.Init)
        {
            gait.Planted = landing;
            gait.PlantedRot = desiredRot;
            gait.Init = true;
        }
        else
        {
            if (gait.Stepping)
            {
                gait.Planted = gait.CurrentPos;
                gait.Stepping = false;
            }
            gait.Planted = float3.Lerp(gait.Planted, landing, k);
            gait.PlantedRot = floatQ.Slerp(gait.PlantedRot, desiredRot, k).Normalized;
        }
        WriteProxy(proxy, gait.Planted);
    }

    // The throttled diagnostic. One line every two seconds with the body state and, per limb, how far
    // it is from home, whether it is in the air and how far through its swing: a run that stops walking
    // should show every d shrinking under the settle and every s returning to 0. -xlinka
    private void LogThrottled(in QuadGaitInput input, float dt, float speed, float lead, float threshold, bool standing)
    {
        _logAccum += dt;
        if (_logAccum < 2.0f)
            return;
        _logAccum = 0f;

        var sb = new System.Text.StringBuilder(256);
        sb.Append("QuadGait: vel=").Append(speed.ToString("F3"))
          .Append(" lead=").Append(lead.ToString("F3"))
          .Append(" phase=").Append(Phase.ToString("F3"))
          .Append(" thr=").Append(threshold.ToString("F3"))
          .Append(" stride=").Append(MathF.Max(input.StrideLength, 0.05f).ToString("F3"))
          .Append(" bodyLen=").Append(input.BodyLength.ToString("F3"))
          .Append(standing ? " standing" : " moving");
        for (int i = 0; i < QuadLimb.Count; i++)
        {
            ref var limb = ref Limbs[i];
            sb.Append(" | ").Append(QuadLimb.Name((QuadLimbId)i))
              .Append(" d=").Append(limb.DistFromHome.ToString("F3"))
              .Append(" s=").Append(limb.Stepping ? '1' : '0')
              .Append(" t=").Append(limb.SwingT.ToString("F2"))
              .Append(" x=").Append(limb.Stretch.ToString("F2"))
              .Append(" h=").Append(limb.HomeStretch.ToString("F2"));
        }
        Lumora.Core.Logging.Logger.Log(sb.ToString());
    }

    // CHASSIS QUERIES

    // Mean CONTACT height of one girdle's two paws. The body hangs off these, so it pitches when the
    // front and rear pairs are at different heights without anything animating a pitch. Planted, not
    // CurrentPos: CurrentPos carries the swing arc and would lift the body every time a paw lifts.
    // Returns false until both limbs have planted at least once. -xlinka
    public bool TryGirdleGroundHeight(bool front, out float height)
    {
        var a = front ? QuadLimbId.FrontLeft : QuadLimbId.RearLeft;
        var b = front ? QuadLimbId.FrontRight : QuadLimbId.RearRight;
        ref var la = ref Limbs[(int)a];
        ref var lb = ref Limbs[(int)b];
        if (!la.Init || !lb.Init)
        {
            height = 0f;
            return false;
        }
        height = (la.Planted.y + lb.Planted.y) * 0.5f;
        return true;
    }

    public float GirdleRoll(bool front, float trackWidth)
    {
        if (trackWidth < 1e-4f)
            return 0f;
        var left = front ? QuadLimbId.FrontLeft : QuadLimbId.RearLeft;
        var right = front ? QuadLimbId.FrontRight : QuadLimbId.RearRight;
        float dY = Limbs[(int)left].CurrentPos.y - Limbs[(int)right].CurrentPos.y;
        return MathF.Asin(System.Math.Clamp(dY / trackWidth, -1f, 1f));
    }

    // HELPERS

    private bool TryGroundHeight(float x, float z, float fallbackY, out float height, out float3 normal)
    {
        height = fallbackY;
        normal = float3.Up;
        if (_world == null || _userSlot == null || _userSlot.IsDestroyed)
            return false;

        _rayExclude.Clear();
        _rayExclude.Add(_userSlot);

        var origin = new float3(x, fallbackY + GroundProbeUp, z);
        var hit = _world.Physics.Raycast(origin, new float3(0f, -1f, 0f), _rayExclude, GroundProbeUp + GroundProbeDown);
        if (!hit.HasValue)
            return false;

        normal = hit.Value.Normal;
        height = hit.Value.Point.y;
        return true;
    }

    // Asymmetric on purpose: a floor ABOVE the root plane always conforms (uphill, a step), and only a
    // floor falling away below fades out (a ledge, a jump). The fade completes inside the probe reach,
    // so a probe MISS lands where the blend is already zero and a hit/miss flicker changes nothing.
    private static float GroundConformance(float deltaY, float scale, float bodyLength)
    {
        if (deltaY >= 0f)
            return 1f;
        float range = MathF.Max(0.35f, bodyLength * 0.3f) * MathF.Max(scale, 0.01f);
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

    private static floatQ BuildPawRotation(float3 forward)
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

    private static float3 PawForward(floatQ rotation) => FlattenDir(rotation * float3.Backward);

    private static float InOutSine(float t)
        => 0.5f * (1f - MathF.Cos(System.Math.Clamp(t, 0f, 1f) * MathF.PI));

    private static float FlatDistance(float3 a, float3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float FlatAngleDeg(float3 a, float3 b)
        => MathF.Acos(System.Math.Clamp(float3.Dot(FlattenDir(a), FlattenDir(b)), -1f, 1f)) * (180f / MathF.PI);

    private static float SignedFlatAngle(float3 from, float3 to)
    {
        from = FlattenDir(from);
        to = FlattenDir(to);
        float angle = MathF.Acos(System.Math.Clamp(float3.Dot(from, to), -1f, 1f));
        return float3.Dot(float3.Cross(from, to), float3.Up) < 0f ? -angle : angle;
    }

    private static float3 FlattenDir(float3 v)
    {
        v.y = 0f;
        return v.LengthSquared > 1e-8f ? v.Normalized : float3.Backward;
    }

    private static float Frac(float v)
    {
        v -= MathF.Floor(v);
        return v < 0f ? v + 1f : v;
    }

    private static void WriteProxy(Slot? proxy, float3 worldPos)
    {
        if (proxy == null || proxy.IsDestroyed)
            return;
        // A paw proxy that carries an equipped pose driver has its transform driven from the socket,
        // and a driven field refuses a plain write at BeginModification. The refusal is silent, so
        // this was a wasted conversion and a wasted call per limb per frame; the gait keeps its own
        // planted state and never reads the proxy back, so nothing is lost by skipping it.
        if (proxy.LocalPosition.IsDriven)
            return;
        var parent = proxy.Parent;
        var local = parent != null ? parent.GlobalPointToLocal(worldPos) : worldPos;
        if ((proxy.LocalPosition.Value - local).LengthSquared > 1e-10f)
            proxy.LocalPosition.SetValueSilently(local, change: true);
    }

    public void Reset()
    {
        Phase = 0f;
        _hasPrev = false;
        _smoothedVel = float3.Zero;
        _logAccum = 0f;
        for (int i = 0; i < QuadLimb.Count; i++)
            Limbs[i] = default;
    }
}
