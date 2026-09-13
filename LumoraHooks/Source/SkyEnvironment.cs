// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core;

namespace Lumora.Godot.Hooks;

// Decides which sky hook actually gets to write the scene's WorldEnvironment.
//
// There is one WorldEnvironment node for the whole tree, but any number of worlds can be open at
// once and a world can hold both a Skybox and a GradientSkybox. Without a single arbiter each hook
// stomps the node in whatever order its change pass happened to drain, which is not the same order
// twice, and a hook tearing down would restore a snapshot taken before somebody else's claim - the
// sky ends up depending on load timing.
//
// So no sky hook touches the node. They hand over a built Environment and a priority; this picks the
// winner among the claims belonging to the FOCUSED world and applies exactly that one. Worlds stay
// loaded when the user switches away, so their claims stay registered and simply stop winning, and
// the bootstrap environment comes back when nothing claims at all. -xlinka
public static class SkyEnvironment
{
    // loses to anything that carries a cubemap
    public const int GradientPriority = 0;

    // the core has already picked one per world
    public const int SkyboxPriority = 100;

    private sealed class Claim
    {
        public object Owner = null!;
        public World World = null!;
        public int Priority;
        public long Sequence;
        public global::Godot.Environment Environment = null!;
    }

    private static readonly List<Claim> _claims = new();
    private static readonly object _lock = new();

    private static WorldEnvironment _node = null!;
    private static bool _ownsNode;
    private static global::Godot.Environment _bootstrap = null!;
    private static bool _bootstrapCaptured;
    private static FocusManager _focus = null!;
    private static long _sequence;

    // The sky the scene is currently drawing, for anything that has to render the same sky through its
    // own Environment instead of the shared WorldEnvironment - an offscreen camera clearing to skybox,
    // for one. Null before the node exists or when the winning environment is not sky-backed.
    public static Sky? CurrentSky
    {
        get
        {
            if (_node == null || !GodotObject.IsInstanceValid(_node))
                return null;
            var environment = _node.Environment;
            if (environment == null || !GodotObject.IsInstanceValid(environment))
                return null;
            return environment.Sky;
        }
    }

    // The environment the scene shows before any sky hook exists. Sky hooks duplicate it so they
    // inherit whatever fog, tonemapping and post settings the project set up, instead of starting
    // from an empty Environment and quietly dropping all of it.
    public static global::Godot.Environment Bootstrap(Node anyNodeInTree)
    {
        EnsureNode(anyNodeInTree);
        return _bootstrap;
    }

    // Push the post-processing settings onto the bootstrap environment AND every claimed one.
    //
    // Sky hooks DUPLICATE the bootstrap rather than referencing it, which is what lets a world carry
    // its own sky without stamping on everyone else's - but it also means a live settings change has
    // to be written to each copy, because a duplicate stopped tracking its source the moment it was
    // made. Applied to every claim rather than only the winner so switching worlds cannot reveal a
    // stale environment. -xlinka
    public static void ApplyPostSettings(int tonemap, float whitePoint, float bloom, Node anyNodeInTree)
    {
        EnsureNode(anyNodeInTree);

        var mapper = tonemap switch
        {
            0 => global::Godot.Environment.ToneMapper.Linear,
            1 => global::Godot.Environment.ToneMapper.Reinhardt,
            2 => global::Godot.Environment.ToneMapper.Filmic,
            3 => global::Godot.Environment.ToneMapper.Aces,
            _ => global::Godot.Environment.ToneMapper.Agx,
        };

        if (_bootstrap != null && GodotObject.IsInstanceValid(_bootstrap))
            Write(_bootstrap, mapper, whitePoint, bloom);

        lock (_lock)
        {
            foreach (var claim in _claims)
            {
                if (claim.Environment != null && GodotObject.IsInstanceValid(claim.Environment))
                    Write(claim.Environment, mapper, whitePoint, bloom);
            }
        }
    }

    private static void Write(global::Godot.Environment environment, global::Godot.Environment.ToneMapper mapper, float whitePoint, float bloom)
    {
        environment.TonemapMode = mapper;
        environment.TonemapWhite = whitePoint;
        // Zero turns the pass OFF rather than running it at no strength: a glow pass costs its blur
        // chain whether or not anything is bright enough to feed it.
        //
        // And on a standalone headset it is always off, whatever the world asked for. The bootstrap
        // environment had glow disabled at XR init, then this replaced it with the world's own
        // environment and glow came straight back: measured 270 ms of GPU per frame at 1504x1504 on
        // a Pico 4 with the bloom chain running. Worlds keep their bloom on desktop. -xlinka
        bool standalone = OS.HasFeature("android");
        environment.GlowEnabled = !standalone && bloom > 0.001f;
        environment.GlowIntensity = bloom;
    }

    // re-claiming with the same owner replaces its environment
    public static void Claimed(object owner, World world, int priority, global::Godot.Environment environment, Node anyNodeInTree)
    {
        EnsureNode(anyNodeInTree);

        lock (_lock)
        {
            var existing = Find(owner);
            if (existing != null)
            {
                existing.World = world;
                existing.Priority = priority;
                existing.Environment = environment;
            }
            else
            {
                _claims.Add(new Claim
                {
                    Owner = owner,
                    World = world,
                    Priority = priority,
                    Environment = environment,
                    Sequence = ++_sequence,
                });
            }
        }

        Resolve();
    }

    // safe to call for an owner that never claimed
    public static void Released(object owner)
    {
        lock (_lock)
        {
            var existing = Find(owner);
            if (existing == null)
                return;
            _claims.Remove(existing);
        }

        Resolve();
    }

    // called after any claim change and on every focus switch
    public static void Resolve()
    {
        if (_node == null || !GodotObject.IsInstanceValid(_node))
            return;

        var focused = _focus?.FocusedWorld;
        Claim? winner = null;

        lock (_lock)
        {
            foreach (var claim in _claims)
            {
                if (claim.Environment == null || !GodotObject.IsInstanceValid(claim.Environment))
                    continue;
                // With no focus manager (the single-world boot path) there is nothing to filter on,
                // so every claim is eligible and priority alone decides.
                if (focused != null && claim.World != focused)
                    continue;
                if (winner == null || Outranks(claim, winner))
                    winner = claim;
            }
        }

        var target = winner?.Environment ?? _bootstrap;
        if (_node.Environment != target)
            _node.Environment = target;
    }

    private static bool Outranks(Claim candidate, Claim current)
    {
        if (candidate.Priority != current.Priority)
            return candidate.Priority > current.Priority;
        // Equal priority means two hooks of the same kind in one world, which the core is supposed to
        // have already resolved. Newest wins so the situation at least settles instead of oscillating.
        return candidate.Sequence > current.Sequence;
    }

    private static Claim? Find(object owner)
    {
        foreach (var claim in _claims)
        {
            if (ReferenceEquals(claim.Owner, owner))
                return claim;
        }
        return null;
    }

    private static void EnsureNode(Node anyNodeInTree)
    {
        if (_node != null && GodotObject.IsInstanceValid(_node))
        {
            EnsureFocusHook();
            return;
        }

        var tree = anyNodeInTree?.GetTree();
        var root = tree?.Root;
        if (root == null)
            return;

        _node = (root.FindChild("WorldEnvironment", true, false) as WorldEnvironment)!;
        if (_node == null)
        {
            _node = new WorldEnvironment { Name = "WorldEnvironment" };
            root.AddChild(_node);
            _ownsNode = true;
        }

        if (!_bootstrapCaptured)
        {
            _bootstrapCaptured = true;
            _bootstrap = _node.Environment;
        }

        EnsureFocusHook();
    }

    private static void EnsureFocusHook()
    {
        if (_focus != null)
            return;
        _focus = Lumora.Core.Engine.Current?.FocusManager!;
        if (_focus != null)
            _focus.OnFocusedWorldChanged += (_, _) => Resolve();
    }

    // Only the hook that created the node would ever have freed it, and it has no way to know
    // whether another hook is still using it, so the decision lives here with the claim list.
    public static void ReleaseNodeIfUnused()
    {
        lock (_lock)
        {
            if (_claims.Count > 0)
                return;
        }

        if (!_ownsNode || _node == null || !GodotObject.IsInstanceValid(_node))
            return;

        _node.QueueFree();
        _node = null!;
        _ownsNode = false;
        _bootstrapCaptured = false;
        _bootstrap = null!;
    }
}
