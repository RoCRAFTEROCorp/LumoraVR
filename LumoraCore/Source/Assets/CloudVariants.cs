// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Assets;

// Server-made variants for cloud textures. A texture that came down from the content service is
// asked about there before this machine computes anything for itself: a ready rung is fetched and
// filed as the derived asset the normal load path expects, a pending one is left to the worker and
// the local generator fills in meanwhile. One ask per rung per run; the answer is cached so a texture
// used forty times does not ask forty times. -xlinka
public static class CloudVariants
{
    private static readonly Dictionary<string, bool> _asked = new(StringComparer.Ordinal);
    private static readonly object _gate = new();

    // Every rung of the chain that is missing locally is tried once against the service. Returns how
    // many rungs came down.
    public static async Task<int> EnsureAsync(LocalDB db, string baseUri, string hash, IReadOnlyList<TextureVariantId> chain)
    {
        var client = Engine.Current?.CDNClient;
        if (client == null || db == null || string.IsNullOrEmpty(baseUri) || string.IsNullOrEmpty(hash))
            return 0;
        int fetched = 0;
        foreach (var id in chain)
        {
            if (id.IsOriginal)
                continue;
            var uri = TextureVariantStore.GetVariantUri(baseUri, id);
            if (uri == null || db.Exists(uri))
                continue;
            if (await TryFetchAsync(db, client, baseUri, hash, id).ConfigureAwait(false))
                fetched++;
        }
        return fetched;
    }

    private static async Task<bool> TryFetchAsync(LocalDB db, LumoraClient client, string baseUri, string hash, TextureVariantId id)
    {
        string key = hash + "|" + id.Storage.Identifier;
        lock (_gate)
        {
            if (_asked.TryGetValue(key, out var done) && done)
                return false;
        }
        var state = await client.GetVariantState(hash, id.Storage.Identifier).ConfigureAwait(false);
        if (state.Failed || state.Data == null)
            return false;
        if (state.Data.IsSkipped)
        {
            lock (_gate) _asked[key] = true;
            return false;
        }
        if (!state.Data.IsReady)
            return false;
        var blob = await client.FetchContent(state.Data.ResultHash!).ConfigureAwait(false);
        if (blob.Failed || blob.Data == null || blob.Data.Length == 0)
            return false;
        await db.SaveDerivedAssetAsync(baseUri, id.Storage.Identifier, blob.Data, TextureVariantStore.VariantExtension).ConfigureAwait(false);
        lock (_gate) _asked[key] = true;
        return true;
    }
}
