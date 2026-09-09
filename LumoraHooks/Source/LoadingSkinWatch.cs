// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core.Assets;
using System.Collections.Generic;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Watches how long a surface has been wearing the loading skin, and says something exactly once when
// that stops being a load and starts being a bug.
//
// A checkered surface has two possible causes and they need different fixes: the material really is
// still waiting on a texture, or the material is ready and the re-drive that would swap it in never
// came. This tells them apart. A line names the dependency that is still out; SILENCE on a surface the
// user can see is checkered means the second case, because the check only runs on a pass that reached
// the renderer at all. -xlinka
internal sealed class LoadingSkinWatch
{
    // Long enough that a big texture on a cold cache never trips it.
    private const ulong StuckAfterMs = 10_000;

    private Dictionary<int, ulong>? _since;
    private HashSet<int>? _reported;

    public void Clear(int surface)
    {
        _since?.Remove(surface);
    }

    public void Note(int surface, IAssetProvider? provider, string? owner)
    {
        _since ??= new Dictionary<int, ulong>();
        ulong now = Time.GetTicksMsec();

        if (!_since.TryGetValue(surface, out ulong since))
        {
            _since[surface] = now;
            return;
        }

        if (now - since < StuckAfterMs)
            return;

        _reported ??= new HashSet<int>();
        if (!_reported.Add(surface))
            return;

        string detail = provider is MaterialProvider material
            ? material.DescribePendingDependency()
            : "no material provider on this surface";

        LumoraLogger.Warn(
            $"Loading skin stuck: '{owner}' surface {surface} has been on the placeholder for " +
            $"{(now - since) / 1000}s - {detail}");
    }
}
