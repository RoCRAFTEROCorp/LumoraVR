// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Assets;

// A provider whose asset comes from a URL and therefore has a fetch that can still be in flight.
// Implemented by StaticAssetProvider so the readiness check can tell "still gathering" apart from
// "nothing to gather" without knowing the asset type. -xlinka
public interface IUrlAssetProvider
{
    bool IsLoadPending { get; }

    // The address the fetch was issued for, so a surface stuck on the loading skin can say WHICH asset
    // never arrived instead of just that something did not. -xlinka
    Uri? SourceUrl { get; }
}

// A provider that GENERATES its asset in-process (a checker, a gradient, a rounded rect, a render
// texture). There is no fetch behind it, so it is never "still arriving" - it is either built or it
// has not been asked for yet, and neither is something to hold a loading skin over. -xlinka
public interface IProceduralAssetProvider
{
}

// Answers one question for the renderers: is this thing STILL ARRIVING, or is what it shows now what
// it is going to show?
//
// The distinction that matters is between an unset reference (authored - a mesh with no material is
// meant to have no material) and a set reference whose bytes have not landed (loading - and worth
// telling the user about). A dynamic provider is ready the instant it exists; a URL-backed one is
// ready when its asset reaches PartiallyLoaded, and a FAILED load counts as ready too - it is never
// going to arrive, so parking a loading skin on it forever would be a lie. -xlinka
public static class AssetReadiness
{
    public static bool IsPending(IAssetProvider? provider, bool inspectDependencies = true)
    {
        if (provider == null)
            return false;

        // A material asset exists the moment something references it, so IsAssetAvailable says nothing
        // about whether its TEXTURES are here. That gap is the whole "unready material pops" complaint,
        // and it is checked FIRST because a material is itself a procedural provider.
        if (provider is MaterialProvider material)
            return inspectDependencies && !material.DependenciesReady;

        if (provider is IUrlAssetProvider url)
            return url.IsLoadPending;

        if (provider is IProceduralAssetProvider)
            return false;

        return !provider.IsAssetAvailable;
    }
}

// Per-surface "have we shown the real thing yet" latch.
//
// One-way on purpose. The placeholder goes on at bind time when the surface's material is still
// arriving, and comes off exactly once, on the arrival notification that already re-drives the
// renderer. Without the latch a provider that momentarily reports pending again (a variant re-request,
// a texture ref rewritten by a driver) would flip the surface back to the checker and the user would
// see a flicker. Re-pointing the surface at a DIFFERENT provider clears the latch, because that is a
// new thing loading, not the same one wobbling. -xlinka
public sealed class LoadingSurfaceLatch
{
    private object?[] _latched = Array.Empty<object?>();

    public bool IsLoading(int index, IAssetProvider? provider)
    {
        if (index < 0)
            return false;
        if (provider == null)
            return false;
        if (provider is MaterialProvider material && !material.UseLoadingPlaceholder)
            return false;

        if (index < _latched.Length && ReferenceEquals(_latched[index], provider))
            return false;

        if (AssetReadiness.IsPending(provider))
            return true;

        Latch(index, provider);
        return false;
    }

    public void Reset()
    {
        Array.Clear(_latched, 0, _latched.Length);
    }

    private void Latch(int index, object provider)
    {
        if (index >= _latched.Length)
        {
            int size = _latched.Length == 0 ? 4 : _latched.Length;
            while (size <= index)
                size *= 2;
            Array.Resize(ref _latched, size);
        }
        _latched[index] = provider;
    }
}
