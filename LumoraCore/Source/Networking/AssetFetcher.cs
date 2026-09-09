// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Nexus.Assets;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking;

// Supported URI schemes
//   local://machineId/hash - served from LocalDB; if not held locally, requested
//                             from the active session peer via SessionAssetTransferer.
//   file:///abs/path - direct disk read.
//   http:// / https:// - HTTP download.
//   builtin://... - built-in engine assets.
//   test://... - local test assets (editor/dev only).
public static class AssetFetcher
{
    private static readonly HttpClient Http = new();

    // In-flight tasks keyed by URI string: (task, pending callbacks)
    private static readonly Dictionary<string, (Task<byte[]> task, List<Action<byte[]>> callbacks)> _active = new();

    // _active is written from WORKER threads (FetchAsset/FetchLocalAsset run inside each asset's LoadSelf Task.Run)
    // and read+removed from the WORLD thread (ProcessQueue). A plain Dictionary mutated from multiple threads
    // silently corrupts/drops entries - which intermittently dropped one of two model textures (white body), and
    // got worse once more concurrent loads (the import indicator's font/text textures) were in flight. Serialize
    // every access through this lock; callbacks are invoked OUTSIDE the lock (a TCS continuation can run inline and
    // re-enter FetchAsset). -xlinka
    private static readonly object _lock = new();

    // Calls callback with the raw bytes on the calling thread once the fetch task completes (via ProcessQueue).
    // Returns immediately; duplicate requests share a single in-flight task.
    public static void FetchAsset(string uri, Action<byte[]> callback)
    {
        // local:// - may need peer-to-peer transfer
        if (uri.StartsWith("local://", StringComparison.Ordinal))
        {
            FetchLocalAsset(uri, callback);
            return;
        }

        lock (_lock)
        {
            if (_active.TryGetValue(uri, out var existing))
            {
                existing.callbacks.Add(callback);
                return;
            }

            var task = Task.Run(() => FetchSync(uri));
            _active[uri] = (task, [callback]);
        }
    }

    // Must be called every frame by the platform driver update loop.
    public static void ProcessQueue()
    {
        if (_active.Count == 0)
            return;

        // Collect completed fetches + remove them UNDER the lock, then fire callbacks OUTSIDE it (a callback is a
        // TCS completion whose continuation can run inline and re-enter FetchAsset -> deadlock if held). -xlinka
        List<(Action<byte[]> cb, byte[] result, string uri)> ready = null!;
        lock (_lock)
        {
            if (_active.Count == 0)
                return;

            List<string> done = null!;
            foreach (var (uri, (task, callbacks)) in _active)
            {
                if (!task.IsCompleted)
                    continue;

                byte[] result = task.IsCompletedSuccessfully ? task.Result : null!;
                ready ??= new List<(Action<byte[]>, byte[], string)>();
                foreach (var cb in callbacks)
                    ready.Add((cb, result, uri));
                (done ??= new List<string>()).Add(uri);
            }

            if (done != null)
                foreach (var uri in done)
                    _active.Remove(uri);
        }

        if (ready != null)
            foreach (var (cb, result, uri) in ready)
            {
                try { cb(result); }
                catch (Exception ex) { LumoraLogger.Error($"AssetFetcher: callback exception for '{uri}': {ex.Message}"); }
            }
    }

    private static void FetchLocalAsset(string uri, Action<byte[]> callback)
    {
        var engine = Engine.Current;
        var localDB = engine?.LocalDB;

        // 1. Served from our own LocalDB?
        if (localDB != null)
        {
            var filePath = localDB.GetFilePath(uri);
            if (filePath != null && File.Exists(filePath))
            {
                lock (_lock)
                {
                    // Coalesce: a blind overwrite here would drop a prior requester's callback for the same URI.
                    if (_active.TryGetValue(uri, out var existing))
                    {
                        existing.callbacks.Add(callback);
                        return;
                    }
                    // Read async so we don't block the main thread
                    var task = Task.Run(() => File.ReadAllBytes(filePath));
                    _active[uri] = (task, [callback]);
                }
                return;
            }
        }

        // 2. Request from session peer
        var transferer = engine?.ActiveSessionTransferer;
        if (transferer != null)
        {
            LumoraLogger.Log($"AssetFetcher: requesting remote asset '{uri}' from session");
            transferer.RequestAsset(new Uri(uri), (assetUri, localPath) =>
            {
                if (localPath == null)
                {
                    LumoraLogger.Warn($"AssetFetcher: peer could not provide '{uri}'");
                    callback(null!);
                    return;
                }

                try
                {
                    // Adopt under the hash the URI already carries, before reading a byte. Importing
                    // instead would re-read and re-hash the whole file to rediscover an address we were
                    // handed, and it wrote the record asynchronously - so the decoder that runs straight
                    // after this callback could look the asset up, find no record, and be unable to tell
                    // what format it just received. Adoption is synchronous and keeps the extension the
                    // sender declared, which is exactly what that lookup needs. -xlinka
                    var cachedPath = localDB?.AdoptGatheredAsset(uri, localPath) ?? localPath;
                    var data = File.ReadAllBytes(cachedPath);
                    callback(data);
                }
                catch (Exception ex)
                {
                    LumoraLogger.Error($"AssetFetcher: error reading received asset '{localPath}': {ex.Message}");
                    callback(null!);
                }
            });
            return;
        }

        LumoraLogger.Warn($"AssetFetcher: '{uri}' not in LocalDB and no active session - cannot fetch");
        callback(null!);
    }

    private static byte[] FetchSync(string uri)
    {
        try
        {
            if (BuiltinAssetHelper.ValidPath(uri))
                return BuiltinAssetHelper.GetBuiltinAssetData(uri);

            if (LocalTestAssetHelper.ValidPath(uri))
                return LocalTestAssetHelper.GetLocalTestAssetData(uri);

            var u = new Uri(uri);

            if (u.Scheme == Uri.UriSchemeFile)
                return File.ReadAllBytes(u.LocalPath);

            if (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            {
                // Route remote downloads through the shared content cache (concurrent-request
                // dedup, disk LRU, persistence). Only fall back to a direct, uncached download
                // if the cache isn't up yet (early init / tests).
                var cache = Engine.Current?.ContentCache;
                if (cache != null)
                    return cache.Get(u).GetAwaiter().GetResult()!;

                return Http.GetByteArrayAsync(u).GetAwaiter().GetResult();
            }

            LumoraLogger.Warn($"AssetFetcher: unsupported URI scheme '{u.Scheme}' in '{uri}'");
            return null!;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"AssetFetcher: failed to fetch '{uri}': {ex.Message}");
            return null!;
        }
    }
}
