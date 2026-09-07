// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Simulation.Particles;

// A single particle handed from one simulation to another, in the SOURCE simulation's space.
public struct SubEmissionParticle
{
    public float3 Position;
    public floatQ Rotation;
    public float3 Size;
    public colorHDR Color;
    public float3 Direction;
    public float Lifetime;
}

// A batch of particles queued for another simulation. The source transform travels with the batch
// because the two simulations do not share a space: the target rebases every particle on arrival.
// Batches are pooled per target - sub-emission runs every frame and allocating a list per frame per
// effect is exactly the kind of steady garbage that shows up as stutter an hour into a session. -xlinka
public sealed class SubEmissionBatch
{
    private SubEmissionParticle[] _particles = new SubEmissionParticle[64];

    public float4x4 SourceTransform = float4x4.Identity;

    public int Count { get; private set; }

    public ReadOnlySpan<SubEmissionParticle> Particles => _particles.AsSpan(0, Count);

    public void Add(in SubEmissionParticle particle)
    {
        if (Count == _particles.Length)
            System.Array.Resize(ref _particles, _particles.Length * 2);
        _particles[Count++] = particle;
    }

    public void Clear()
    {
        Count = 0;
        SourceTransform = float4x4.Identity;
    }
}

// Routes particles from one simulation into others. Each module that spawns elsewhere borrows a batch,
// fills it during the step, and the manager hands every batch to its target at the end of the step -
// never mid-step, so a target that has already emitted this frame picks the batch up on the next one
// instead of being mutated while it is iterating its own buffers.
public sealed class SubEmissionManager
{
    private readonly Dictionary<ParticleSimulation, List<SubEmissionBatch>> _pending = new();
    private readonly Stack<SubEmissionBatch> _pool = new();

    public ParticleSimulation Owner { get; }

    public SubEmissionManager(ParticleSimulation owner) => Owner = owner;

    // Declare which simulations this one can spawn into. Targets not listed are dropped.
    public void SetTargets(List<ParticleSimulation> targets)
    {
        foreach (var target in targets)
        {
            if (!_pending.ContainsKey(target))
                _pending.Add(target, new List<SubEmissionBatch>());
        }
        if (_pending.Count == targets.Count)
            return;

        List<ParticleSimulation>? stale = null;
        foreach (var pair in _pending)
        {
            if (!targets.Contains(pair.Key))
                (stale ??= new List<ParticleSimulation>()).Add(pair.Key);
        }
        if (stale == null)
            return;
        foreach (var key in stale)
        {
            foreach (var batch in _pending[key])
                Return(batch);
            _pending.Remove(key);
        }
    }

    // Borrow a batch aimed at a target. Returns null when the target is not a declared target.
    //
    // The pool is the one piece of this manager two simulations can reach at once: the owner borrows
    // from it during its step and the TARGET hands batches back from inside its own step, and when the
    // host steps simulations side by side those are different threads. The lock is uncontended in
    // practice and costs nothing measurable; a torn pool would cost a batch. -xlinka
    public SubEmissionBatch? BorrowBatch(ParticleSimulation target)
    {
        if (!_pending.TryGetValue(target, out var list))
            return null;
        SubEmissionBatch? batch = null;
        lock (_pool)
        {
            if (_pool.Count > 0)
                batch = _pool.Pop();
        }
        batch ??= new SubEmissionBatch();
        batch.Clear();
        list.Add(batch);
        return batch;
    }

    // Deliver every filled batch to its target and recycle the storage.
    public void SubmitAll()
    {
        foreach (var pair in _pending)
        {
            var list = pair.Value;
            for (int i = 0; i < list.Count; i++)
            {
                var batch = list[i];
                if (batch.Count == 0)
                {
                    Return(batch);
                    continue;
                }
                batch.SourceTransform = Owner.GlobalTransform;
                pair.Key.SubmitSubEmission(batch, this);
            }
            list.Clear();
        }
    }

    internal void Return(SubEmissionBatch batch)
    {
        batch.Clear();
        lock (_pool)
        {
            if (_pool.Count < 32)
                _pool.Push(batch);
        }
    }
}
