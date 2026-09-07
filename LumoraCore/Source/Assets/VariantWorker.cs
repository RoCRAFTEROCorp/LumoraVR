// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Assets;

// The variant worker: a trusted process that takes queued variant jobs from the content service,
// computes them with the same texture code a client uses for its own variants, and posts the result
// back. Runs headless from the engine executable with --variant-worker=<key>. A job the source
// cannot satisfy (a 1024 rung for a 256 texture) is finished as skipped so nobody waits on it; a
// job that throws is finished with the error so it can be retried up to the service's limit and then
// left alone. Nothing here touches a world. -xlinka
public static class VariantWorker
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorWait = TimeSpan.FromSeconds(15);

    public static async Task RunAsync(LumoraClient client, string workerKey, string workerName, CancellationToken token, Action<string>? log = null)
    {
        log ??= message => Logging.Logger.Log($"VariantWorker: {message}");
        log($"started as '{workerName}' against {ServiceConfig.Current.ContentBase}");
        int done = 0, skipped = 0, failed = 0;
        while (!token.IsCancellationRequested)
        {
            var next = await client.WorkerNextVariant(workerKey, workerName).ConfigureAwait(false);
            if (next.Failed)
            {
                log($"queue unavailable: {next.Message ?? next.Status.ToString()}");
                await Delay(ErrorWait, token).ConfigureAwait(false);
                continue;
            }
            var job = next.Data;
            if (job == null)
            {
                await Delay(IdleWait, token).ConfigureAwait(false);
                continue;
            }

            var outcome = await ComputeAsync(client, job, token).ConfigureAwait(false);
            ApiResponse finish;
            if (outcome.Bytes != null)
            {
                finish = await client.WorkerFinishVariant(workerKey, job.AssetHash, job.VariantId, outcome.Bytes).ConfigureAwait(false);
                done++;
            }
            else if (outcome.Skipped)
            {
                finish = await client.WorkerFinishVariant(workerKey, job.AssetHash, job.VariantId, null, skipped: true).ConfigureAwait(false);
                skipped++;
            }
            else
            {
                finish = await client.WorkerFinishVariant(workerKey, job.AssetHash, job.VariantId, null, error: outcome.Error ?? "unknown").ConfigureAwait(false);
                failed++;
            }
            log($"{job.VariantId} of {Short(job.AssetHash)}: {(outcome.Bytes != null ? $"ready, {outcome.Bytes.Length} bytes" : outcome.Skipped ? "skipped" : "failed: " + outcome.Error)}"
                + (finish.Failed ? $" (finish refused: {finish.Message})" : string.Empty)
                + $" [done {done}, skipped {skipped}, failed {failed}]");
        }
        log("stopped");
    }

    private readonly struct Outcome
    {
        public readonly byte[]? Bytes;
        public readonly bool Skipped;
        public readonly string? Error;
        public Outcome(byte[]? bytes, bool skipped, string? error) { Bytes = bytes; Skipped = skipped; Error = error; }
        public static Outcome Ready(byte[] bytes) => new(bytes, false, null);
        public static Outcome Skip() => new(null, true, null);
        public static Outcome Fail(string error) => new(null, false, error);
    }

    // Texture variants only for now; a variant id the worker does not understand is failed with a
    // reason so it does not spin. Mesh levels come with the mesh variant descriptor.
    private static async Task<Outcome> ComputeAsync(LumoraClient client, VariantJob job, CancellationToken token)
    {
        if (!TextureVariantId.TryParse(job.VariantId, out var id))
            return Outcome.Fail($"variant id not understood: {job.VariantId}");
        if (id.IsOriginal)
            return Outcome.Skip();

        var source = await client.FetchContent(job.AssetHash, ct: token).ConfigureAwait(false);
        if (source.Failed || source.Data == null || source.Data.Length == 0)
            return Outcome.Fail($"source fetch failed: {source.Message ?? source.Status.ToString()}");

        try
        {
            // The ladder is RGBA8; a radiance map has no honest rung in it.
            if (TextureMetadata.DetectHdr(source.Data))
                return Outcome.Skip();

            var rgba = TextureVariantStore.DecodeRgba(source.Data, out int width, out int height);
            if (rgba == null || width <= 0 || height <= 0)
                return Outcome.Fail("source is not a decodable image");
            if (id.MaxSize >= System.Math.Max(width, height))
                return Outcome.Skip();

            // GPU block compression is done by the machine that uploads to its GPU; the worker's job
            // is the resolution and mip ladder everyone shares.
            if (id.Compression != TextureCompressionKind.None)
                return Outcome.Skip();

            var metadata = TextureMetadata.Analyze(rgba, width, height,
                id.Mipmaps ? TextureMetadata.FullMipCount(width, height) : 1,
                source.Data.LongLength, rgba.LongLength, TextureMetadata.DetectSRgb(source.Data), isNormalMap: false);
            var variant = TextureVariantStore.BuildVariant(rgba, width, height, id, metadata.HasAlpha, metadata.ContentFormat);
            return Outcome.Ready(TextureVariantStore.Encode(variant));
        }
        catch (Exception ex)
        {
            return Outcome.Fail(ex.Message);
        }
    }

    private static async Task Delay(TimeSpan wait, CancellationToken token)
    {
        try { await Task.Delay(wait, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static string Short(string hash) => hash.Length > 12 ? hash.Substring(0, 12) : hash;
}
