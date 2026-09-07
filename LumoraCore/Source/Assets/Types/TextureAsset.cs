// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Lumora.Core.Logging;

namespace Lumora.Core.Assets;

// Asset containing texture/image data (RGBA8 with optional mipmaps). URL instances gather and
// decode their image in LoadSelf; procedural instances (atlases, render targets,
// generated textures) are created via InitializeDynamic and fed data through
// SetImageData.
//
// A URL instance resolves a VARIANT before it gathers anything: the descriptor's resolution cap
// plus the source's real dimensions name a generated blob, and if that blob exists locally or on a
// peer it is loaded instead of the source file. The base URL is never rewritten, so a save that
// references local://.../hash keeps working whether or not any variant was ever generated. -xlinka
//
// The load is progressive. TextureLoadChain turns the one requested variant into an ordered list of
// acceptable answers, smallest first, and each one that lands is put on screen straight away; the
// exact one supersedes it when it arrives. A swap is guarded by rank, so a small rung that comes in
// late can never overwrite a better one that already landed.
public class TextureAsset : ImplementableAsset<ITextureAssetHook>
{
    private byte[] _pixelData = null!;
    private byte[][] _mipLevels = Array.Empty<byte[]>();
    private int _width;
    private int _height;
    private bool _hasMipmaps;

    // Longest edge, in pixels, of the best rung already handed to the renderer. -1 means nothing has
    // been delivered yet. The whole guard rail against a progressive load going BACKWARDS is this
    // number plus the check in TryClaimRank.
    private int _deliveredRank = -1;
    private readonly object _deliveryLock = new();

    public int Width => _width;

    public int Height => _height;

    public byte[] PixelData => _pixelData;

    // One entry means base level only; the renderer may still generate mips from it.
    public byte[][] MipLevels => _mipLevels;

    public bool HasMipmaps => _hasMipmaps;

    // Layout of PixelData and MipLevels. RGBA8 for everything except a floating-point source,
    // which is held as RGBA half floats. Anything that reads PixelData as bytes-per-channel must
    // check this first. -xlinka
    public TextureFormatKind PixelFormat { get; private set; } = TextureFormatKind.RGBA8;

    // Counted, mips included.
    public long MemorySize
    {
        get
        {
            if (_mipLevels.Length > 1)
            {
                long total = 0;
                foreach (var level in _mipLevels)
                    total += level?.LongLength ?? 0;
                return total;
            }
            return _pixelData?.LongLength ?? 0;
        }
    }

    // Null for a procedural texture that has not been analyzed.
    public TextureMetadata? Metadata { get; private set; }

    // Null when it loaded the source directly.
    public TextureVariantId? LoadedVariant { get; private set; }

    public bool IsVariant => LoadedVariant is { IsOriginal: false };

    // Set by the font atlas builder.
    public bool IsMSDF { get; set; }

    // Matches the font's msdf_pixel_range. Only meaningful when IsMSDF.
    public int MsdfPixelRange { get; set; } = 8;

    // URL (static) instances only; procedural instances set data through SetImageData.
    protected override async Task LoadSelf()
    {
        var descriptor = TargetVariant as TextureVariantDescriptor ?? TextureVariantDescriptor.Default;

        var db = Engine?.LocalDB;
        // A cloud texture that was fetched into the database is addressed by its local record from
        // here on, so it gets variants like anything imported here. Its hash is kept so the server's
        // own variants can be asked for before this machine computes any. -xlinka
        string? cloudHash = AssetURL?.Scheme == "lumora" ? Persistence.CloudAssetPacker.HashOf(AssetURL.OriginalString) : null;
        var baseUri = AssetURL?.Scheme == "local" ? AssetURL.OriginalString
            : cloudHash != null ? db?.UriForHash(cloudHash) : null;
        TextureMetadata? sourceMeta = null;

        if (db != null && baseUri != null)
        {
            sourceMeta = await TextureVariantStore.TryLoadMetadataAsync(db, baseUri, AssetManager).ConfigureAwait(false);
            if (sourceMeta == null || sourceMeta.Width <= 0)
            {
                // Never analyzed. Generating on the spot would put a full decode plus a stack of mip
                // chains in front of the user, so queue it instead and load the source this time: the
                // cap and the progressive chain start applying from the next load. This is what gives
                // textures that never went through the image importer (a model's embedded maps,
                // anything restored from a save) their variants.
                TextureVariantStore.EnsureGeneratedInBackground(db, baseUri, descriptor.IsNormalMap, descriptor.GenerateMipmaps);
                sourceMeta = null;
            }
        }

        Task<byte[]>? prefetch = null;

        // The ladder is RGBA8. An HDR source has no rung in it and loads from its own bytes only.
        if (db != null && baseUri != null && sourceMeta != null && !sourceMeta.IsHdr)
        {
            var chain = TextureLoadChain.Build(sourceMeta.Width, sourceMeta.Height, descriptor);
            if (cloudHash != null)
                await CloudVariants.EnsureAsync(db, baseUri, cloudHash, chain).ConfigureAwait(false);
            var target = chain[chain.Count - 1];
            var targetUri = ResolveReachableVariantUri(db, baseUri, target);
            bool previews = chain.Count > 1 && !SkipPreviewRungs(db, baseUri, target, sourceMeta, descriptor);

            // Peer-owned: strictly one gather at a time, small rung first. The whole point of the
            // preview on a peer asset is to get pixels up before the big transfer finishes, and
            // running both transfers at once just makes them share the same pipe. Locally owned: the
            // "gather" is a disk read, nothing to contend for, so start the exact one now and let it
            // run underneath the previews. -xlinka
            bool peerOwned = !TextureVariantStore.IsOwnedLocally(db, baseUri);
            if (previews && !peerOwned)
                prefetch = StartGather(targetUri != null ? new Uri(targetUri) : AssetURL!);

            if (previews)
            {
                bool delivered = false;
                for (int i = 0; i < chain.Count - 1; i++)
                    delivered |= await TryDeliverVariantAsync(db, baseUri, chain[i], sourceMeta, descriptor, null, final: false)
                        .ConfigureAwait(false);

                // Every rung was missing on an asset we own, which is what an import from before the
                // cheap rung existed looks like. Queue a fill so the next load has something to show
                // while it waits. Generation is deduplicated internally, so spamming this is free.
                if (!delivered && !peerOwned)
                    TextureVariantStore.EnsureGeneratedInBackground(db, baseUri, descriptor.IsNormalMap, descriptor.GenerateMipmaps);
            }

            if (targetUri != null
                && await TryDeliverVariantAsync(db, baseUri, target, sourceMeta, descriptor, prefetch, final: true)
                    .ConfigureAwait(false))
                return;

            // The exact variant turned out to be unreadable, so the source below is the answer. Any
            // prefetch we started was aimed at the variant address and is not it; drop it and gather
            // the source instead. When targetUri was null all along the prefetch already IS the
            // source gather, so leave that one alone.
            if (targetUri != null)
                prefetch = null;
        }

        var bytes = await (prefetch ?? StartGather(AssetURL!)).ConfigureAwait(false);
        if (bytes == null || bytes.Length == 0)
        {
            // A delivered preview is a usable texture. Failing the whole asset here would tear that
            // back off screen to replace it with nothing, which is strictly worse than staying on the
            // rung we already have.
            if (_deliveredRank >= 0)
            {
                Logger.Warn($"TextureAsset: no source data for {AssetURL}; staying on the {_deliveredRank}px rung");
                return;
            }
            FailLoad($"No image data gathered for {AssetURL}");
            return;
        }

        bool normalMap = descriptor.IsNormalMap || (sourceMeta?.IsNormalMap ?? false);

        if (TextureMetadata.DetectHdr(bytes))
        {
            await LoadHdrSourceAsync(bytes, descriptor, normalMap).ConfigureAwait(false);
            return;
        }

        int width, height;
        byte[]? rgba;
        try
        {
            // Decoding a full-resolution source image is real CPU work, and this line runs on whichever
            // thread completed the gather. Say where it goes instead of taking whatever the completer
            // was standing on. -xlinka
            var decoded = await DecodeOffThread(() =>
            {
                var pixels = TextureVariantStore.DecodeRgba(bytes, out int w, out int h);
                return (Pixels: pixels, Width: w, Height: h);
            }).ConfigureAwait(false);
            rgba = decoded.Pixels;
            width = decoded.Width;
            height = decoded.Height;
        }
        catch (Exception ex)
        {
            if (_deliveredRank >= 0)
            {
                Logger.Warn($"TextureAsset: cannot decode {AssetURL} ({ex.Message}); staying on the {_deliveredRank}px rung");
                return;
            }
            FailLoad($"Failed to decode image {AssetURL}: {ex.Message}");
            return;
        }

        if (rgba == null || width <= 0 || height <= 0)
        {
            if (_deliveredRank >= 0)
                return;
            FailLoad($"Failed to decode image {AssetURL}: no pixels");
            return;
        }

        int mipCount = descriptor.GenerateMipmaps ? TextureMetadata.FullMipCount(width, height) : 1;
        var metadata = TextureMetadata.Analyze(
            rgba, width, height, mipCount,
            bytes.LongLength,
            descriptor.GenerateMipmaps
                ? TextureMetadata.ComputeGpuBytes(TextureFormatKind.RGBA8, width, height, mipCount)
                : (long)width * height * 4,
            TextureMetadata.DetectSRgb(bytes),
            normalMap);

        var original = new TextureVariantId(0, descriptor.GenerateMipmaps, descriptor.Compression);
        if (!TryClaimRank(System.Math.Max(width, height)))
            return;

        Metadata = metadata;
        LoadedVariant = original;

        SetImageData(rgba, width, height, descriptor.GenerateMipmaps);
        Hook?.SetWrapMode(descriptor.WrapU, descriptor.WrapV);

        // Don't let LoadSelf return (-> FullyLoaded) until the GPU texture is actually built. SetImageData ->
        // Hook.UploadData only QUEUES a deferred main-thread build; if we reported loaded now, the asset's single
        // load-complete notification would fire while Hook.IsValid is still false, the consuming material would bind
        // a null albedo, and nothing re-binds it (white body). Awaiting the upload makes that notification fire when
        // the texture is genuinely usable: the asset isn't marked loaded until the GPU upload completes. -xlinka
        if (Hook != null)
            await Hook.WaitForUploadAsync().ConfigureAwait(false);
    }

    // Floating-point sources: decode to half, keep the full range all the way to the renderer. No
    // ladder, no preview rung, and the resolution cap does not apply, because every one of those
    // is an RGBA8 mechanism and an RGBA8 copy of a radiance map is a different, clipped picture.
    // Decode failure here fails the load outright rather than falling back to the 8-bit decoder,
    // which would put that clipped picture on screen and call it done. -xlinka
    private async Task LoadHdrSourceAsync(byte[] bytes, TextureVariantDescriptor descriptor, bool normalMap)
    {
        int width, height;
        byte[]? half;
        try
        {
            var decoded = await DecodeOffThread(() =>
            {
                var pixels = TextureVariantStore.DecodeRgbaHalf(bytes, out int w, out int h);
                return (Pixels: pixels, Width: w, Height: h);
            }).ConfigureAwait(false);
            half = decoded.Pixels;
            width = decoded.Width;
            height = decoded.Height;
        }
        catch (Exception ex)
        {
            FailLoad($"Failed to decode HDR image {AssetURL}: {ex.Message}");
            return;
        }

        if (half == null || width <= 0 || height <= 0)
        {
            FailLoad($"Failed to decode HDR image {AssetURL}: no HDR decoder for this format on this build");
            return;
        }

        int mipCount = descriptor.GenerateMipmaps ? TextureMetadata.FullMipCount(width, height) : 1;
        var metadata = TextureMetadata.AnalyzeHalf(
            half, width, height, mipCount,
            bytes.LongLength,
            TextureMetadata.ComputeGpuBytes(TextureFormatKind.RGBA16F, width, height, mipCount),
            normalMap);

        if (!TryClaimRank(System.Math.Max(width, height)))
            return;

        Metadata = metadata;
        LoadedVariant = new TextureVariantId(0, descriptor.GenerateMipmaps, descriptor.Compression);

        SetHdrImageData(half, width, height, descriptor.GenerateMipmaps);
        Hook?.SetWrapMode(descriptor.WrapU, descriptor.WrapV);

        if (Hook != null)
            await Hook.WaitForUploadAsync().ConfigureAwait(false);
    }

    private Task<byte[]> StartGather(Uri url) => AssetManager.RequestGather(url);

    // The address of a variant we can actually get hold of, or null if asking would be a wasted trip.
    //
    // Held locally: straight read. Owned by a peer: worth asking for, because a variant is exactly
    // what we want crossing the wire instead of a full-resolution original, and a peer that hasn't
    // generated it answers "not available" and we fall through. Owned by US and missing: nobody else
    // has it either. The original is never a "variant" address - that is the base URI. -xlinka
    private string? ResolveReachableVariantUri(LocalDB db, string baseUri, TextureVariantId id)
    {
        if (id.IsOriginal)
            return null;

        var uri = TextureVariantStore.GetVariantUri(baseUri, id.Storage);
        if (uri == null)
            return null;

        if (db.Exists(uri))
            return uri;

        if (TextureVariantStore.IsOwnedLocally(db, baseUri) || Engine?.ActiveSessionTransferer == null)
            return null;

        return uri;
    }

    // Skip the cheap rungs entirely when the exact one is already compressed and sitting in the GPU
    // cache. On a revisit that path is a file read plus one upload, so previews would only buy a
    // flicker of blur and an extra upload nobody asked for. -xlinka
    private bool SkipPreviewRungs(LocalDB db, string baseUri, TextureVariantId target, TextureMetadata sourceMeta, TextureVariantDescriptor descriptor)
    {
        if (descriptor.Compression != TextureCompressionKind.Block)
            return false;

        var (width, height) = target.ResolveSize(sourceMeta.Width, sourceMeta.Height);
        int mipCount = descriptor.GenerateMipmaps ? TextureMetadata.FullMipCount(width, height) : 1;
        string intent = TextureGpuCache.Intent(
            sourceMeta.HasAlpha, descriptor.IsNormalMap || sourceMeta.IsNormalMap, sourceMeta.IsHdr);
        return TextureGpuCache.IsCached(db, baseUri, target, width, height, mipCount, intent);
    }

    // Fetch one rung and put it on screen. gather is a transfer already in flight for this rung's
    // address, or null to start one here.
    private async Task<bool> TryDeliverVariantAsync(
        LocalDB db,
        string baseUri,
        TextureVariantId id,
        TextureMetadata sourceMeta,
        TextureVariantDescriptor descriptor,
        Task<byte[]>? gather,
        bool final)
    {
        var variantUri = gather != null ? TextureVariantStore.GetVariantUri(baseUri, id.Storage) : ResolveReachableVariantUri(db, baseUri, id);
        if (variantUri == null)
            return false;

        // Nothing to gain from a rung that is no better than what is already up.
        int rank = TextureLoadChain.Rank(id, System.Math.Max(sourceMeta.Width, sourceMeta.Height));
        if (rank <= _deliveredRank)
            return false;

        bool local = db.Exists(variantUri);
        byte[]? blob;
        try
        {
            blob = await (gather ?? StartGather(new Uri(variantUri))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"TextureAsset: variant fetch failed for {variantUri}: {ex.Message}");
            return false;
        }

        var data = await DecodeOffThread(() => TextureVariantStore.Decode(blob)).ConfigureAwait(false);
        if (data == null)
        {
            if (blob != null && final)
                Logger.Warn($"TextureAsset: variant {id.Identifier} of {baseUri} is unreadable; using the source");
            return false;
        }

        // A peer-supplied variant is written into our cache under the SAME derived address, so the
        // next load resolves it locally. Without this the generic receive path would re-key it by
        // content hash and every session would pull it again. -xlinka
        if (!local && blob != null)
        {
            await db.SaveDerivedAssetAsync(baseUri, id.Storage.Identifier, blob, TextureVariantStore.VariantExtension)
                .ConfigureAwait(false);
        }

        if (!TryClaimRank(rank))
            return false;

        Metadata = new TextureMetadata
        {
            Width = data.Width,
            Height = data.Height,
            ContentFormat = data.ContentFormat,
            HasAlpha = data.HasAlpha,
            MipCount = data.MipCount,
            SourceBytes = blob?.LongLength ?? 0,
            DecodedBytes = data.DecodedBytes,
            SRgb = sourceMeta.SRgb,
            IsNormalMap = descriptor.IsNormalMap || sourceMeta.IsNormalMap,
            VariantId = id.Identifier,
        };
        LoadedVariant = id;

        SetMipData(data.Levels, data.Width, data.Height);
        Hook?.SetWrapMode(descriptor.WrapU, descriptor.WrapV);

        if (Hook != null)
            await Hook.WaitForUploadAsync().ConfigureAwait(false);

        // Only after the GPU texture exists, for the same reason the final rung waits: a requester
        // told to re-bind while the hook is still invalid binds nothing and never comes back.
        if (!final)
            ReportPartiallyLoaded();

        return true;
    }

    // Claim the right to replace the contents at this quality. Losing the claim means something
    // better already landed - a late preview must never overwrite it, so the caller drops its
    // payload on the floor and leaves the good one alone.
    private bool TryClaimRank(int rank)
    {
        lock (_deliveryLock)
        {
            if (rank <= _deliveredRank)
                return false;
            _deliveredRank = rank;
            return true;
        }
    }

    // Expects RGBA8, 4 bytes per pixel.
    public void SetImageData(byte[] pixels, int width, int height, bool mipmaps = false)
    {
        if (pixels == null)
        {
            throw new ArgumentNullException(nameof(pixels));
        }

        int expectedSize = width * height * 4; // RGBA8
        if (pixels.Length < expectedSize)
        {
            throw new ArgumentException($"Pixel data too small. Expected at least {expectedSize} bytes, got {pixels.Length}");
        }

        _pixelData = pixels;
        _mipLevels = new[] { pixels };
        _width = width;
        _height = height;
        _hasMipmaps = mipmaps;
        PixelFormat = TextureFormatKind.RGBA8;
        Version++;

        Upload(generateMipmaps: mipmaps);
    }

    // RGBA half floats, 8 bytes per pixel, rows top-down like everything else.
    public void SetHdrImageData(byte[] rgbaHalf, int width, int height, bool mipmaps = false)
    {
        if (rgbaHalf == null)
            throw new ArgumentNullException(nameof(rgbaHalf));

        long expectedSize = (long)width * height * 8;
        if (rgbaHalf.LongLength < expectedSize)
            throw new ArgumentException($"Pixel data too small. Expected at least {expectedSize} bytes, got {rgbaHalf.LongLength}");

        _pixelData = rgbaHalf;
        _mipLevels = new[] { rgbaHalf };
        _width = width;
        _height = height;
        _hasMipmaps = mipmaps;
        PixelFormat = TextureFormatKind.RGBA16F;
        Version++;

        Upload(generateMipmaps: mipmaps);
    }

    // Used by the variant path, where the chain was filtered once at generation time and every machine that
    // loads it gets the same levels instead of re-deriving them.
    public void SetMipData(byte[][] levels, int width, int height)
    {
        if (levels == null || levels.Length == 0)
            throw new ArgumentException("At least the base mip level is required", nameof(levels));

        _mipLevels = levels;
        _pixelData = levels[0];
        _width = width;
        _height = height;
        _hasMipmaps = levels.Length > 1;
        PixelFormat = TextureFormatKind.RGBA8;
        Version++;

        Upload(generateMipmaps: false);
    }

    private void Upload(bool generateMipmaps)
    {
        var hook = Hook;
        if (hook == null)
            return;

        var metadata = Metadata;
        string? cacheKey = null;
        string? cacheDirectory = null;

        // Only a URL-backed texture gets a compressed-cache slot: its bytes are addressed by a
        // stable URI, so the same key always means the same pixels. A procedural texture is
        // regenerated by whatever built it and has no such identity to key on.
        if (AssetURL != null && metadata != null)
        {
            var db = Engine?.LocalDB;
            if (db != null)
            {
                cacheKey = TextureGpuCache.BuildKey(
                    AssetURL.OriginalString, LoadedVariant,
                    metadata.Width, metadata.Height, metadata.MipCount,
                    TextureGpuCache.Intent(metadata.HasAlpha, metadata.IsNormalMap, metadata.IsHdr));
                cacheDirectory = db.GetGpuCachePath();
            }
        }

        hook.UploadTexture(new TextureUploadRequest
        {
            MipLevels = _mipLevels,
            Width = _width,
            Height = _height,
            GenerateMipmaps = generateMipmaps,
            AllowBlockCompression = LoadedVariant?.Compression == TextureCompressionKind.Block,
            HasAlpha = metadata?.HasAlpha ?? true,
            IsNormalMap = metadata?.IsNormalMap ?? false,
            SRgb = metadata?.SRgb,
            IsHdr = PixelFormat == TextureFormatKind.RGBA16F,
            CacheKey = cacheKey,
            CacheDirectory = cacheDirectory,
            Report = ReportUploaded,
        });
    }

    // This is the only way GpuFormat is ever set, which is why the VRAM figures downstream are measurements and
    // not estimates.
    public void ReportUploaded(TextureFormatKind gpuFormat, int mipCount, long gpuBytes)
    {
        var metadata = Metadata;
        if (metadata == null)
            return;
        metadata.GpuFormat = gpuFormat;
        _hasMipmaps = mipCount > 1;
    }

    public static TextureAsset FromRgba(byte[] rgbaData, int width, int height, bool mipmaps = false)
    {
        var texture = new TextureAsset();
        texture.InitializeDynamic();
        texture.SetImageData(rgbaData, width, height, mipmaps);
        return texture;
    }

    public override void Unload()
    {
        _pixelData = null!;
        _mipLevels = Array.Empty<byte[]>();
        _width = 0;
        _height = 0;
        _hasMipmaps = false;
        PixelFormat = TextureFormatKind.RGBA8;
        Metadata = null;
        LoadedVariant = null;
        lock (_deliveryLock)
            _deliveredRank = -1;
        base.Unload();
    }
}
