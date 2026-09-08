// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Simulation.SoftBody;

namespace Lumora.Core.Components;

// Our own CPU soft body: a Verlet particle mesh with distance constraints, pressure, pinning and
// collision against dynamic-bone colliders + a ground plane. Unlike Godot's Jolt soft body this
// actually collides, holds pins, and can be grabbed - we own every step. Deforms a source mesh and
// renders the result on a child DeformableMesh. Simulation is LOCAL per peer (visual), like dynamic
// bones: same rest-anchored, no-drift model, runs after IK. -xlinka
[ComponentCategory("Physics")]
[DefaultUpdateOrder(-4000)] // after AvatarIK (-5000), like dynamic bones
public class SquishyBody : Component, IInputUpdateReceiver, ISoftBodySpace, ISoftBodyCollisionHandler
{
    // read once for topology + rest verts
    [Group("General")]
    public readonly SyncRef<Component> SourceMesh;

    public readonly AssetRef<MaterialAsset> Material;

    // 0..1; exponent-corrected per iteration so the same setting means the same stiffness at 4 or 16 iterations
    [Group("Dynamics")]
    public readonly Sync<float> Stiffness;

    // resistance to folding, 0..1: 0 = folds like wet paper, ~0.1-0.3 = fabric that holds a drape, higher = stiff sheet
    public readonly Sync<float> BendStiffness;

    // 0 = bouncy, higher = sluggish
    public readonly Sync<float> Damping;

    // 0..1; 1 = full stick on contact, lower keeps tangential sliding so cloth slips down slopes instead of gluing
    public readonly Sync<float> Friction;

    // applied per triangle projected on its facing (edge-on catches nothing, face-on catches everything),
    // so flags/capes billow instead of translating
    [Group("Wind")]
    public readonly Sync<float3> Wind;

    // 0 = perfectly steady wind, ~0.5 = natural gusting
    public readonly Sync<float> WindGustiness;

    // more = stiffer/stabler
    [Group("Simulation")]
    public readonly Sync<int> Iterations;

    // 0 = floppy cloth, positive = holds a balloon
    public readonly Sync<float> Pressure;

    // Shape retention (0..1): each frame, pull particles toward their rest SHAPE. This is what makes
    // a jelly "squishy but hold its form and never explode" - stable by construction (bounded pull, no
    // runaway). 0 = floppy cloth; ~0.2 = a wobbly jelly; ~0.6 = a firm bouncy ball. -xlinka
    public readonly Sync<float> ShapeRetention;

    public readonly Sync<float3> Gravity;

    // pins every rest vertex whose local Y is above this; +Inf = pin none
    [Group("Pinning")]
    public readonly Sync<float> PinAboveLocalY;

    [Group("Collision")]
    public readonly Sync<float> ParticleRadius;

    public readonly SyncRefList<IDynamicBoneCollider> Colliders;

    // world Y; NaN disables
    public readonly Sync<float> GroundY;

    // drapes cloth over arbitrary world geometry, not just the assigned dynamic-bone colliders;
    // costs a raycast per moving particle per frame
    public readonly Sync<bool> CollideWithWorld;

    // Metres from the local user's head past which this body drops iterations and eventually stops
    // simulating. Zero never pauses. Measured from the head rather than the camera node because that
    // is what the renderer's own visibility range is measured from, same as particles and bones.
    //
    // Dropping iterations is safe to do on a curve because the solver corrects stiffness for the
    // iteration count: the same Stiffness setting means the same stiffness at 8 iterations and at 2,
    // so the LOD costs accuracy of the constraint solve, not the look of the fabric. -xlinka
    [Group("Simulation")]
    public readonly Sync<float> MaxViewDistance;

    // The simulation itself lives in LumoraSimulation. This component owns one solver, feeds it the
    // topology and the tunables, and answers its two questions: where is the anchor space, and what did
    // this particle just hit. Nothing about the maths lives here any more. -xlinka
    private readonly SoftBodySolver _solver = new();
    private float3[]? _writeBuffer;   // local-space positions handed to the DeformableMesh
    private float3[]? _uploadedLocal; // last local positions actually pushed to the mesh (rest skip check)
    private readonly List<Slot> _selfExclude = new();
    private readonly List<Slot> _overlapScratch = new(); // broadphase: is anything near the body this frame?
    private DeformableMesh? _deformed;
    private bool _registered;
    private bool _built;
    private int _readyRetries;

    // Sleep/wake: a settled body stops simulating AND stops re-uploading its mesh (the expensive part).
    // It wakes only when something can perturb it - its own slot moving, or one of its colliders moving.
    // This is the big perf win: a room full of resting cloth/jelly costs almost nothing. -xlinka
    private bool _perturbedThisFrame;
    private float3 _lastSlotPos;
    private floatQ _lastSlotRot = floatQ.Identity;
    private float3[]? _lastColliderPos;
    private bool _worldNear;                              // broadphase: a world collider overlaps the body this frame
    private readonly List<Slot> _nearSlots = new();       // world colliders currently near - to detect one intruding
    private readonly List<float3> _nearPos = new();
    private readonly List<floatQ> _nearRot = new();
    private float3 _boundsCentre;                         // body AABB this frame, from the collider snapshot
    private float3 _boundsSize;
    private SoftBodyBudget? _budget;
    private World? _budgetWorld;

    public SquishyBody()
    {
        SourceMesh = new SyncRef<Component>(this);
        Material = new AssetRef<MaterialAsset>(this);
        Stiffness = new Sync<float>(this, 0.6f);
        BendStiffness = new Sync<float>(this, 0.15f);
        Damping = new Sync<float>(this, 0.02f);
        Friction = new Sync<float>(this, 1f);
        Wind = new Sync<float3>(this, float3.Zero);
        WindGustiness = new Sync<float>(this, 0.5f);
        Iterations = new Sync<int>(this, 8);
        Pressure = new Sync<float>(this, 0f);
        ShapeRetention = new Sync<float>(this, 0f);
        Gravity = new Sync<float3>(this, new float3(0f, -9.81f, 0f));
        PinAboveLocalY = new Sync<float>(this, float.PositiveInfinity);
        ParticleRadius = new Sync<float>(this, 0.02f);
        Colliders = new SyncRefList<IDynamicBoneCollider>(this);
        GroundY = new Sync<float>(this, float.NaN);
        CollideWithWorld = new Sync<bool>(this, false);
        MaxViewDistance = new Sync<float>(this, 0f);
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
        ArmReadyRetry();
    }

    public override void OnDestroy()
    {
        if (_registered)
            Engine.Current?.InputInterface?.UnregisterInputEventReceiver(this);
        base.OnDestroy();
    }

    public void BeforeInputUpdate() { }

    public void AfterInputUpdate()
    {
        if (!Enabled || IsDestroyed)
        {
            _solver.ResetVelocities();
            return;
        }
        if (!_built)
            return;

        // Not while the world is still building. IsLoading covers PendingHookCreations, which means
        // platform bodies are still being made - including the box this sheet is supposed to land on.
        // Stepping through that window drops the cloth through a collider that does not exist yet and
        // leaves it on the floor, which is exactly what "it clips through the cube" was. Hold the pose
        // the author placed until there is a world to fall into. -xlinka
        if (World?.IsLoading == true)
        {
            _solver.ResetVelocities();
            return;
        }

        if (IsBeyondViewDistance())
        {
            // Nothing is written and nothing is uploaded: the garment holds its last pose, which at that
            // range is a still frame nobody can tell from a settled one.
            _paused = true;
            return;
        }
        if (_paused)
        {
            // Back in range. The anchor may be anywhere by now, so start from the rest shape instead of
            // from wherever the body was parked - see SoftBodySolver.ResetToRest.
            _paused = false;
            _solver.ResetToRest(this);
            _uploadedLocal = null;
            InvalidateContacts();
        }

        SnapshotColliders();
        _perturbedThisFrame = CheckPerturbed();
        // Fully at rest and nothing can have disturbed it -> skip the sim AND the mesh upload entirely.
        if (!_solver.IsAwake && !_perturbedThisFrame)
            return;

        PushSolverParameters();
        if (!ReferenceEquals(_budgetWorld, World))
        {
            _budgetWorld = World;
            _budget = SoftBodyBudget.For(World);
        }
        var budget = _budget;
        _solver.Iterations = ScaleIterations(IterationsForDistance(), budget);
        _queryMargin = MathF.Max(_solver.AverageEdgeLength * 2f, MathF.Max(ParticleRadius.Value, 0f));

        // CLAMPED delta, and never one big step. SmoothDelta is an average of the RAW frame time with no
        // ceiling on it, so one world-load hitch drags it to a fifth of a second and stays there for a
        // while: gravity then moves every particle further in a single step than the box it is meant to
        // land on is thick, and the sheet goes straight through and ends up on the floor. The clock
        // already publishes a clamped Delta for exactly this ("what simulation code wants"); substepping
        // on top keeps one slow frame from tunnelling through anything. -xlinka
        float dt = World?.Time.Delta ?? (1f / 60f);
        double now = World?.Time.TotalTime ?? 0.0;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        int steps = (int)MathF.Ceiling(dt / MaxSubstep);
        if (steps < 1)
            steps = 1;
        else if (steps > MaxSubsteps)
            steps = MaxSubsteps;
        float sub = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            _stepIndex++;
            _solver.Step(sub, now, this, this);
        }
        WriteBack();
        budget?.Report(World?.Time.UpdateIndex ?? 0UL,
            (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }

    // Longest slice the solver is allowed to integrate in one go, and the cap on how many of them one
    // frame may pay for. Past the cap the body simply runs slow rather than eating the frame.
    private const float MaxSubstep = 1f / 60f;
    private const int MaxSubsteps = 4;

    private static int ScaleIterations(int iterations, SoftBodyBudget? budget)
    {
        if (budget == null || budget.IterationScale >= 1f)
            return iterations;
        return System.Math.Max(1, (int)MathF.Round(iterations * budget.IterationScale));
    }

    // DISTANCE GATING
    //
    // Two levers off one field. Past MaxViewDistance the body stops stepping entirely; approaching it,
    // the constraint iteration count falls off toward a quarter of the configured value. The iteration
    // ramp is the one that matters in a busy room: a garment nobody is looking straight at still moves,
    // it just solves its constraints less exactly, and because the solver corrects stiffness for the
    // iteration count that costs no visible stiffness change. -xlinka

    private bool _paused;
    private Slot? _localHead;
    private double _nextHeadScan = double.NegativeInfinity;

    // Keep simulating a little past the stated distance: the renderer has a fade band of its own and a
    // garment that froze inside it would be visibly still.
    private const float ViewDistanceFadeMargin = 3f;

    // How often the local head is re-resolved when it is missing. The body-node lookup behind it builds
    // a predicate to search the user's component registry, and that predicate is garbage no per-frame
    // path may make.
    private const double HeadScanInterval = 1.0;

    private bool TryGetHeadDistance(out float distance)
    {
        distance = 0f;
        if (_localHead == null || _localHead.IsDestroyed)
        {
            _localHead = null;
            double now = World?.Time.TotalTime ?? 0d;
            if (now < _nextHeadScan)
                return false;
            _nextHeadScan = now + HeadScanInterval;

            var head = World?.LocalUser?.Root?.HeadSlot;
            if (head == null || head.IsDestroyed)
                return false;
            _localHead = head;
        }

        distance = (Slot.GlobalPosition - _localHead.GlobalPosition).Length;
        return true;
    }

    private bool IsBeyondViewDistance()
    {
        float max = MaxViewDistance.Value;
        if (max <= 0f || !TryGetHeadDistance(out float distance))
            return false;

        // Tighter to come back than to leave, so a body sitting exactly on the line does not pause and
        // reseed itself every other frame.
        float cutoff = (max + ViewDistanceFadeMargin) * (_paused ? 0.95f : 1f);
        return distance > cutoff;
    }

    private int IterationsForDistance()
    {
        int full = System.Math.Clamp(Iterations.Value, 1, 32);
        float max = MaxViewDistance.Value;
        if (max <= 0f || !TryGetHeadDistance(out float distance))
            return full;

        float near = max * 0.35f;
        if (distance <= near)
            return full;

        int floor = System.Math.Max(1, full / 4);
        float t = System.Math.Clamp((distance - near) / MathF.Max(max - near, 1e-3f), 0f, 1f);
        return System.Math.Max(floor, (int)MathF.Round(full + (floor - full) * t));
    }

    // Did anything that can move this body change since last frame? Its own slot transform (pinned
    // particles ride it) or any assigned collider's slot. A few cheap transform compares; returns true
    // the moment something moved so a sleeping body wakes on the same frame it's touched. -xlinka
    private bool CheckPerturbed()
    {
        bool moved = false;

        // Wind is a continuous external force - a windy body never truly rests, and a dormant one must
        // wake the moment wind is switched on (nothing else in the wake model would notice it).
        if (Wind.Value.LengthSquared > 1e-8f)
            moved = true;

        float3 pos = Slot.GlobalPosition;
        floatQ rot = Slot.GlobalRotation;
        if (float3.DistanceSquared(pos, _lastSlotPos) > 1e-8f)
            moved = true;
        else
        {
            float dot = _lastSlotRot.x * rot.x + _lastSlotRot.y * rot.y + _lastSlotRot.z * rot.z + _lastSlotRot.w * rot.w;
            if (MathF.Abs(dot) < 0.99999f)
                moved = true;
        }
        _lastSlotPos = pos;
        _lastSlotRot = rot;

        // Collider movement is read off the snapshot taken this frame, so a sleeping body pays one
        // world-space resolve per collider and not one per collider per particle.
        int count = _shapeCount;
        if (_lastColliderPos == null || _lastColliderPos.Length != count)
        {
            _lastColliderPos = new float3[count];
            moved = true;
        }
        for (int i = 0; i < count; i++)
        {
            float3 cp = _shapes[i].BoundsMin;
            if (float3.DistanceSquared(cp, _lastColliderPos[i]) > 1e-8f)
                moved = true;
            _lastColliderPos[i] = cp;
        }

        // World collision wake: run the broadphase here too so a dormant body wakes the instant a world
        // collider (a grabbed/pushed object, or the player) enters its bounds or moves within them. Resting
        // on a STATIC box keeps the box in the near-set but unmoving -> no wake -> the body stays asleep.
        //
        // The overlap query is the one thing a dormant body still pays for every frame, and it is not
        // free: it builds a shape and a query object on the platform side and walks node metadata for
        // every hit. A body that is asleep and otherwise undisturbed asks every other frame instead; the
        // wake it would trigger lands one frame later, which nothing can see. An awake body asks every
        // frame because its contact cache below depends on knowing what moved. -xlinka
        if (CollideWithWorld.Value && World?.Physics != null)
        {
            bool probe = moved || _solver.IsAwake || ((World.Time?.UpdateIndex ?? 0UL) & 1UL) == 0UL;
            if (probe)
            {
                _worldNear = World.Physics.OverlapBox(_boundsCentre, _boundsSize, floatQ.Identity, _overlapScratch) > 0;
                if (NearSetChangedOrMoved(out float displacement, out bool rotated))
                {
                    moved = true;
                    // A collider that only translated cannot have come closer to any particle than the
                    // distance it travelled, so free-air answers survive it as a debt against their
                    // clearance. Cached surfaces do not: the plane they hold is the old one. Anything
                    // entering, leaving or turning throws everything out.
                    if (rotated || displacement < 0f)
                        InvalidateContacts();
                    else
                    {
                        _nearMotion += displacement;
                        _surfaceEpoch++;
                    }
                }
            }
        }
        else
        {
            _worldNear = false;
        }
        return moved;
    }

    // True if the set of nearby world colliders changed (one entered/left) or any of them moved since last
    // frame - i.e. something is intruding on or shifting against the body and it must wake. Rebuilds the
    // cached near-set as a side effect. Reports the largest translation of a surviving member and whether
    // any of them turned; a membership change comes back as a negative displacement. -xlinka
    private bool NearSetChangedOrMoved(out float displacement, out bool rotated)
    {
        displacement = 0f;
        rotated = false;
        bool membership = _overlapScratch.Count != _nearSlots.Count;
        if (!membership)
        {
            for (int i = 0; i < _overlapScratch.Count; i++)
            {
                var s = _overlapScratch[i];
                int idx = _nearSlots.IndexOf(s);
                if (idx < 0)
                {
                    membership = true;
                    break;
                }
                float moved = float3.Distance(s.GlobalPosition, _nearPos[idx]);
                if (moved > 1e-4f)
                    displacement = MathF.Max(displacement, moved);
                var rot = s.GlobalRotation;
                var was = _nearRot[idx];
                float dot = rot.x * was.x + rot.y * was.y + rot.z * was.z + rot.w * was.w;
                if (MathF.Abs(dot) < 0.99999f)
                    rotated = true;
            }
        }
        _nearSlots.Clear();
        _nearPos.Clear();
        _nearRot.Clear();
        for (int i = 0; i < _overlapScratch.Count; i++)
        {
            var s = _overlapScratch[i];
            _nearSlots.Add(s);
            _nearPos.Add(s.GlobalPosition);
            _nearRot.Add(s.GlobalRotation);
        }
        if (membership)
        {
            displacement = -1f;
            return true;
        }
        return displacement > 0f || rotated;
    }

    private void ArmReadyRetry()
    {
        if (IsDestroyed || World == null || _built)
            return;
        var source = ResolveSourceMesh();
        if (source != null)
        {
            Build(source.Value.positions, source.Value.indices, source.Value.uvs);
            return;
        }
        if (SourceMesh.Target == null || _readyRetries++ > 600)
            return;
        World.RunInUpdates(10, ArmReadyRetry);
    }

    private (float3[] positions, int[] indices, float2[]? uvs)? ResolveSourceMesh()
    {
        Phos.PhosMesh? mesh = SourceMesh.Target switch
        {
            Meshes.ProceduralMesh procedural => procedural.PhosMesh,
            MeshProvider provider => provider.Asset?.MeshData,
            _ => null
        };
        if (mesh == null || mesh.VertexCount == 0)
            return null;

        var positions = new float3[mesh.VertexCount];
        Array.Copy(mesh.RawPositions, positions, mesh.VertexCount);
        var uvs = mesh.HasUV0s ? (float2[]?)mesh.RawUV0s.Clone() : null;

        var indices = new List<int>();
        foreach (var submesh in mesh.Submeshes)
        {
            if (submesh.Topology != Phos.PhosTopology.Triangles)
                continue;
            var raw = submesh.RawIndices;
            for (int i = 0; i + 2 < submesh.IndexCount; i += 3)
            {
                indices.Add(raw[i]);
                indices.Add(raw[i + 1]);
                indices.Add(raw[i + 2]);
            }
        }
        if (indices.Count == 0)
            return null;
        return (positions, indices.ToArray(), uvs);
    }

    private void Build(float3[] localPositions, int[] indices, float2[]? uvs)
    {
        int n = localPositions.Length;

        // The solver derives the stretch edges, the bend cross-edges, the rest volume, the shape-match
        // offsets and the long-range attachment map from the topology; all this component supplies is the
        // geometry and where the pin line sits.
        PushSolverParameters();
        _solver.Build(localPositions, indices, PinAboveLocalY.Value, this);

        _writeBuffer = new float3[n];
        _uploadedLocal = null; // force the first upload

        _lastColliderPos = null;
        _nearSlots.Clear();
        _nearPos.Clear();
        _nearRot.Clear();
        _contacts = new WorldContact[n];
        InvalidateContacts();
        _lastSlotPos = Slot.GlobalPosition;
        _lastSlotRot = Slot.GlobalRotation;

        _selfExclude.Clear();
        _selfExclude.Add(Slot);

        // Render the deformed result on a child slot (the source mesh is data only, hidden by MeshHook).
        var deformedSlot = Slot.FindChildOrAdd("Deformed");
        _deformed = deformedSlot.GetComponent<DeformableMesh>() ?? deformedSlot.AttachComponent<DeformableMesh>();
        _deformed.SetGeometry((float3[])localPositions.Clone(), indices, uvs);
        var renderer = deformedSlot.GetComponent<MeshRenderer>() ?? deformedSlot.AttachComponent<MeshRenderer>();
        // Cloth is a single thin layer, so its shadow has to be cast from both faces. Under the default
        // mode the shadow pass culls back faces and the sheet stops casting the moment a light is on
        // its far side - the shadow lands on the wrong side of the surface, or vanishes.
        renderer.ShadowCastMode.Value = Components.ShadowCastMode.DoubleSided;
        renderer.Mesh.Target = _deformed;
        if (Material.Target != null)
            renderer.Material.Target = Material.Target;

        _built = true;
        _solver.ResetVelocities();
    }

    // cheap enough to call every step
    private void PushSolverParameters()
    {
        _solver.Stiffness = Stiffness.Value;
        _solver.BendStiffness = BendStiffness.Value;
        _solver.Damping = Damping.Value;
        _solver.Friction = Friction.Value;
        _solver.Wind = Wind.Value;
        _solver.WindGustiness = WindGustiness.Value;
        _solver.Iterations = Iterations.Value;
        _solver.Pressure = Pressure.Value;
        _solver.ShapeRetention = ShapeRetention.Value;
        _solver.Gravity = Gravity.Value;
        _solver.ParticleRadius = ParticleRadius.Value;
        _solver.GroundY = GroundY.Value;
    }

    // The solver's anchor space is this component's slot: pinned particles are rebuilt through it every
    // step, which is what carries a grabbed body along with whatever is holding it.
    float3 ISoftBodySpace.LocalPointToGlobal(in float3 local) => Slot.LocalPointToGlobal(local);

    float3 ISoftBodySpace.GlobalPointToLocal(in float3 global) => Slot.GlobalPointToLocal(global);

    // Collision back end for the solver. World geometry first, then the assigned dynamic-bone colliders;
    // the ground plane is the solver's own last word so nothing can push a particle back under it.
    //
    // The world pass is a resting-contact push-out rather than a movement raycast, which is what lets
    // cloth drape over a static box and STAY there instead of sinking through it one frame at a time.
    // It is gated on the broadphase computed in CheckPerturbed, so a body falling through empty air does
    // zero world queries. -xlinka
    bool ISoftBodyCollisionHandler.ResolveParticle(int index, ref float3 position, float radius)
    {
        bool hit = false;

        if (_worldNear && CollideWithWorld.Value && World?.Physics != null
            && ResolveAgainstWorld(index, ref position, radius))
        {
            hit = true;
        }

        var shapes = _shapes;
        for (int i = 0; i < _shapeCount; i++)
        {
            if (shapes[i].Resolve(ref position, radius))
                hit = true;
        }
        return hit;
    }

    // WORLD CONTACT CACHE
    //
    // Every world resolve is a platform physics query: a rest-info call that hands back a dictionary,
    // and on a hit a walk up the node tree reading metadata to find the slot. Called once per free
    // particle per substep that was the whole cost of a curtain somebody stood next to - seven hundred
    // queries a frame to learn that most of the sheet was hanging in open air, and the constraint
    // budget could not touch it because none of it was constraint work.
    //
    // So each particle remembers the answer it got and how long it stays true. Every query is made with
    // the sphere grown by a margin, which turns a miss into a distance: nothing within the margin, or
    // the nearest surface is this far along its normal. A particle in open air then does not ask again
    // until it has travelled that far, with any translation of a nearby collider charged against the
    // same allowance. A particle ON a surface keeps the contact plane and resolves against it in
    // arithmetic for a few steps, as long as it has not slid along the surface by more than a fraction
    // of its own radius; the periodic refresh and the slide limit are what keep a particle from being
    // held up by a phantom plane past the edge of the box it was resting on. Anything in the near set
    // turning, entering or leaving throws all of it away, so a moving collider costs exactly what it
    // cost before. -xlinka
    private struct WorldContact
    {
        public float3 Point;
        public float3 Normal;
        public float3 QueriedAt;
        public float Clearance;
        public float MotionAtQuery;
        public int Step;
        public int Epoch;
        public byte State;
    }

    private const byte ContactNone = 0;
    private const byte ContactClear = 1;
    private const byte ContactSurface = 2;

    // Substeps a cached surface plane is trusted for before the world is asked again.
    private const int SurfaceRefreshSteps = 8;

    // Slide along a cached plane, as a fraction of the particle radius, past which it is asked again.
    private const float SurfaceSlideFraction = 0.25f;

    private WorldContact[] _contacts = Array.Empty<WorldContact>();
    private int _clearEpoch;
    private int _surfaceEpoch;
    private int _stepIndex;
    private float _nearMotion;
    private float _queryMargin;

    private void InvalidateContacts()
    {
        _clearEpoch++;
        _surfaceEpoch++;
        _nearMotion = 0f;
    }

    private bool ResolveAgainstWorld(int index, ref float3 position, float radius)
    {
        var contacts = _contacts;
        if ((uint)index >= (uint)contacts.Length)
        {
            if (!World!.Physics!.ResolveSphere(position, radius, _selfExclude, out var direct, out _))
                return false;
            position = direct;
            return true;
        }

        ref var c = ref contacts[index];
        switch (c.State)
        {
            case ContactClear when c.Epoch == _clearEpoch:
            {
                float travelled = float3.Distance(position, c.QueriedAt) + (_nearMotion - c.MotionAtQuery);
                if (travelled < c.Clearance)
                    return false;
                break;
            }
            case ContactSurface when c.Epoch == _surfaceEpoch && _stepIndex - c.Step < SurfaceRefreshSteps:
            {
                var offset = position - c.QueriedAt;
                float along = float3.Dot(offset, c.Normal);
                var slide = offset - c.Normal * along;
                float limit = radius * SurfaceSlideFraction;
                if (slide.LengthSquared < limit * limit)
                {
                    float depth = float3.Dot(position - c.Point, c.Normal);
                    if (depth >= radius)
                        return false;
                    position += c.Normal * (radius - depth);
                    return true;
                }
                break;
            }
        }
        return QueryWorld(ref c, ref position, radius);
    }

    private bool QueryWorld(ref WorldContact c, ref float3 position, float radius)
    {
        float margin = _queryMargin;
        c.Step = _stepIndex;
        c.MotionAtQuery = _nearMotion;

        if (!World!.Physics!.ResolveSphere(position, radius + margin, _selfExclude, out var corrected, out var normal))
        {
            c.State = ContactClear;
            c.Epoch = _clearEpoch;
            c.QueriedAt = position;
            c.Clearance = margin;
            return false;
        }

        // The grown sphere's rest point is the nearest surface point; how far it sits along the normal
        // is the real clearance, and the push-out for the true radius follows from the same point.
        var point = corrected - normal * (radius + margin);
        float depth = float3.Dot(position - point, normal);
        if (depth >= radius)
        {
            c.State = ContactClear;
            c.Epoch = _clearEpoch;
            c.QueriedAt = position;
            c.Clearance = depth - radius;
            return false;
        }

        position = point + normal * radius;
        c.State = ContactSurface;
        c.Epoch = _surfaceEpoch;
        c.Point = point;
        c.Normal = normal;
        c.QueriedAt = position;
        return true;
    }

    // COLLIDER SNAPSHOT
    //
    // This is called once per free particle per step - six hundred times for a garment - and it used to
    // walk the collider list and ask each one to resolve, which meant re-reading that collider's slot
    // world matrix and global scale six hundred times a frame for an answer that cannot change mid-step.
    // Freeze them once and the inner loop is arithmetic over a flat array. The body's own bounds throw
    // out anything it cannot reach before the particle loop ever sees it. -xlinka
    private DynamicBoneColliderShape[] _shapes = Array.Empty<DynamicBoneColliderShape>();
    private int _shapeCount;

    // Slack on the body's bounds when culling, in metres: a particle's per-step travel is capped at a
    // couple of edge lengths and a collider is something a person swings.
    private const float BroadphaseMotionMargin = 0.25f;

    private void SnapshotColliders()
    {
        _shapeCount = 0;
        float radius = MathF.Max(ParticleRadius.Value, 0f);
        _solver.ComputeBounds(radius, out var centre, out var size);
        _boundsCentre = centre;
        _boundsSize = size;

        var bones = CollideWithWorld.Value ? DynamicBoneManager.For(World) : null;
        bones?.EnsurePlayerColliders();
        int players = bones?.PlayerColliderCount ?? 0;
        int count = Colliders.Count + players;
        if (count == 0)
            return;
        if (_shapes.Length < count)
            _shapes = new DynamicBoneColliderShape[System.Math.Max(count, 4)];

        var half = size * 0.5f;
        var min = centre - half;
        var max = centre + half;
        float margin = _solver.AverageEdgeLength * 2f + BroadphaseMotionMargin;

        foreach (var collider in Colliders)
        {
            if (collider == null || !collider.TryGetShape(out var shape))
                continue;
            if (!shape.BoundsOverlap(in min, in max, margin))
                continue;
            _shapes[_shapeCount++] = shape;
        }

        // PEOPLE.
        //
        // A worn avatar's colliders are deliberately Trigger, so the hook makes them Area3D sensors that
        // cannot shove their wearer's own character controller around. That also makes them invisible to
        // every physics query this body runs - ResolveSphere says "solids only" in as many words - so a
        // person could walk straight through a curtain and it would not notice.
        //
        // The bone manager already gathers every user's head and hands once a frame, in exactly the shape
        // type this snapshot holds, with the replicated AffectOthersBones toggle honoured at the source.
        // Reading that costs nothing and inherits the privacy answer. The alternative, letting these
        // queries see triggers, would also hand us every grab sensor and image plane in the world and
        // would ignore that toggle entirely. -xlinka
        if (bones == null)
            return;

        var playerShapes = bones.PlayerColliders;
        for (int i = 0; i < players && _shapeCount < _shapes.Length; i++)
        {
            var shape = playerShapes[i];
            if (!shape.BoundsOverlap(in min, in max, margin))
                continue;
            _shapes[_shapeCount++] = shape;
        }
    }

    private void WriteBack()
    {
        if (_deformed == null || _writeBuffer == null)
            return;

        // The solver converts to local and reports whether anything actually moved since the last upload.
        // The mesh re-upload (ArrayMesh rebuild) is the single most expensive step per frame, so when a
        // body is settled we skip it entirely - a resting cloth/jelly does no GPU work. -xlinka
        if (_solver.WriteLocalPositions(this, _writeBuffer, ref _uploadedLocal))
            _deformed.UpdatePositions(_writeBuffer);
    }
}
