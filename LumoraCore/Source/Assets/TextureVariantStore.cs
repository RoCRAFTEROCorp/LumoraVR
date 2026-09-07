// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using K4os.Compression.LZ4;
using Lumora.Core.Logging;
using StbImageSharp;

namespace Lumora.Core.Assets;

public sealed class TextureVariantData
{
    public byte[][] Levels { get; init; } = Array.Empty<byte[]>();

    public int Width { get; init; }
    public int Height { get; init; }
    public bool HasAlpha { get; init; }
    public TextureFormatKind ContentFormat { get; init; } = TextureFormatKind.Unknown;

    public byte[] BaseLevel => Levels.Length > 0 ? Levels[0] : Array.Empty<byte>();
    public int MipCount => System.Math.Max(1, Levels.Length);

    // Counted, not estimated.
    public long DecodedBytes
    {
        get
        {
            long total = 0;
            foreach (var level in Levels)
                total += level?.LongLength ?? 0;
            return total;
        }
    }
}

// Generates, stores and loads texture resolution variants.
//
// Storage model: a variant is an ordinary local asset with a DERIVED address rather than a
// content address. Its URI is the base texture's URI with the variant identifier appended
// (local://machine/hash~v1-max512-mips1-compnone), which buys three things at once: the
// existing local:// fetch path serves it with no changes, the peer transferer already resolves the
// owner from the machine id in the URI, and a peer that has only ever seen the BASE uri can
// construct the address of any variant it wants and ask for it by name. No variant manifest has to
// be replicated. -xlinka
//
// Payload is a small container: RGBA8 mip levels back to back, LZ4-compressed only when that
// actually shrinks them (measured per blob, not assumed - LZ4 on photographic RGBA frequently
// does not, and storing a "compressed" blob that grew would be a lie in both directions).
public static class TextureVariantStore
{
    private const uint Magic = 0x5854564C; // "LVTX" little-endian
    // 2: rows are stored top-down; version 1 variants were built from flipped rows and must not be reused.
    private const ushort ContainerVersion = 2;
    private const ushort FlagLz4 = 1 << 0;

    public const string VariantExtension = ".lvtex";

    public const string MetadataExtension = ".lvmeta";

    public const string MetadataSuffix = "meta";

    // ADDRESSING

    public static string? GetVariantUri(string? baseUri, TextureVariantId id) =>
        id.IsOriginal ? baseUri : LocalDB.DeriveUri(baseUri, id.Identifier);

    public static string? GetMetadataUri(string? baseUri) => LocalDB.DeriveUri(baseUri, MetadataSuffix);

    // DECODE

    // Every path that turns file bytes into pixels goes through here so the base texture and its variants can
    // never disagree about which way up the rows are. Rows stay TOP-DOWN, the decoder's own order: the
    // renderer samples V=0 at row 0, mesh UVs are top-origin (see MeshDecoder, no FlipUVs), the glyph atlas
    // and every procedural texture upload top-down, and the UI puts V=0 on the top edge of a quad. The
    // flip that used to live here turned every loaded picture upside down in the world browser and on
    // any model whose UVs were right. Cubemap faces take these rows as they are too. -xlinka
    // Formats our own decoder cannot read are handed to the platform, which usually can. Installed by
    // the rendering layer at startup; null means there is nothing to fall back to.
    //
    // WebP is the case that forced this. The built-in decoder handles PNG, JPEG, BMP, TGA, PSD and GIF
    // and nothing else, so a WebP texture failed outright even though the platform decodes it happily
    // one layer down - the failure just came too early to ever reach it. Imported avatars routinely
    // carry WebP. -xlinka
    public delegate byte[]? PlatformImageDecoder(byte[] encoded, out int width, out int height);

    public static PlatformImageDecoder? PlatformDecoder { get; set; }

    // Same shape for floating-point sources, returning RGBA half floats (8 bytes per texel). Only
    // EXR needs it: Radiance is decoded here. Null means EXR is not readable on this build. -xlinka
    public static PlatformImageDecoder? PlatformHdrDecoder { get; set; }

    // Floating-point sources decode to RGBA half floats and NEVER through DecodeRgba: the 8-bit
    // decoder tone-maps Radiance data on the way in without saying so, and the picture that comes
    // out is a clipped, gamma-bent version of what the creator made. Radiance goes through the
    // float path of the built-in decoder; EXR is the platform's, when it has one. -xlinka
    public static byte[]? DecodeRgbaHalf(byte[] encoded, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (encoded == null || encoded.Length == 0)
            return null;

        bool exr = encoded.Length >= 4 && encoded[0] == 0x76 && encoded[1] == 0x2F && encoded[2] == 0x31 && encoded[3] == 0x01;
        if (!exr)
        {
            using var stream = new MemoryStream(encoded);
            var result = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (result?.Data == null || result.Width <= 0 || result.Height <= 0)
                return null;
            width = result.Width;
            height = result.Height;
            return PackHalf(result.Data, width, height);
        }

        var platform = PlatformHdrDecoder;
        if (platform == null)
            return null;
        var pixels = platform(encoded, out width, out height);
        return pixels != null && width > 0 && height > 0 ? pixels : null;
    }

    // Float RGBA to half RGBA, little-endian. NaN and infinity are clamped away here because a
    // single bad texel poisons every mip below it once the chain is filtered. -xlinka
    public static byte[] PackHalf(float[] rgba, int width, int height)
    {
        long count = (long)width * height * 4;
        var packed = new byte[count * 2];
        for (long i = 0, o = 0; i < count && i < rgba.Length; i++, o += 2)
        {
            float v = rgba[i];
            if (float.IsNaN(v))
                v = 0f;
            else if (float.IsInfinity(v))
                v = v > 0 ? 65504f : -65504f;
            ushort bits = BitConverter.HalfToUInt16Bits((Half)v);
            packed[o] = (byte)bits;
            packed[o + 1] = (byte)(bits >> 8);
        }
        return packed;
    }

    public static byte[]? DecodeRgba(byte[] encoded, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (encoded == null || encoded.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(encoded);
            var result = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (result?.Data != null && result.Width > 0 && result.Height > 0)
            {
                width = result.Width;
                height = result.Height;
                return result.Data;
            }
        }
        catch (Exception) when (PlatformDecoder != null)
        {
            // Fall through: an unreadable format is the platform's problem now, not a failure.
        }

        var platform = PlatformDecoder;
        if (platform == null)
            return null;

        var pixels = platform(encoded, out width, out height);
        return pixels != null && width > 0 && height > 0 ? pixels : null;
    }

    // RESAMPLING

    // Every destination pixel averages the full source rectangle it covers, so a large reduction keeps detail
    // instead of point-sampling it away. Alpha is averaged straight (not premultiplied): our textures are
    // sampled with straight alpha and premultiplying here would darken the fringes of cutout art.
    public static byte[] Downscale(byte[] source, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        if (dstWidth == srcWidth && dstHeight == srcHeight)
            return source;

        var dst = new byte[(long)dstWidth * dstHeight * 4];
        double xRatio = (double)srcWidth / dstWidth;
        double yRatio = (double)srcHeight / dstHeight;

        for (int y = 0; y < dstHeight; y++)
        {
            int y0 = (int)(y * yRatio);
            int y1 = System.Math.Max(y0 + 1, System.Math.Min(srcHeight, (int)System.Math.Ceiling((y + 1) * yRatio)));

            for (int x = 0; x < dstWidth; x++)
            {
                int x0 = (int)(x * xRatio);
                int x1 = System.Math.Max(x0 + 1, System.Math.Min(srcWidth, (int)System.Math.Ceiling((x + 1) * xRatio)));

                long r = 0, g = 0, b = 0, a = 0;
                int count = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * srcWidth * 4;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int o = row + sx * 4;
                        r += source[o];
                        g += source[o + 1];
                        b += source[o + 2];
                        a += source[o + 3];
                        count++;
                    }
                }

                if (count == 0)
                    count = 1;
                int d = (y * dstWidth + x) * 4;
                dst[d] = (byte)(r / count);
                dst[d + 1] = (byte)(g / count);
                dst[d + 2] = (byte)(b / count);
                dst[d + 3] = (byte)(a / count);
            }
        }

        return dst;
    }

    // Each level is filtered from the one above it, which is both cheaper and smoother than resampling the base
    // every time.
    public static byte[][] BuildMipChain(byte[] baseLevel, int width, int height)
    {
        int levelCount = TextureMetadata.FullMipCount(width, height);
        var levels = new byte[levelCount][];
        levels[0] = baseLevel;

        int w = width, h = height;
        for (int level = 1; level < levelCount; level++)
        {
            int nw = System.Math.Max(1, w >> 1);
            int nh = System.Math.Max(1, h >> 1);
            levels[level] = Downscale(levels[level - 1], w, h, nw, nh);
            w = nw;
            h = nh;
        }

        return levels;
    }

    // CONTAINER

    public static byte[] Encode(TextureVariantData data)
    {
        long payloadLength = data.DecodedBytes;
        if (payloadLength > int.MaxValue)
            throw new InvalidOperationException($"Texture variant payload too large: {payloadLength} bytes");

        var payload = new byte[payloadLength];
        int offset = 0;
        foreach (var level in data.Levels)
        {
            if (level == null)
                continue;
            Buffer.BlockCopy(level, 0, payload, offset, level.Length);
            offset += level.Length;
        }

        // Try LZ4 and keep it only if it genuinely shrank. Blobs that don't compress stay raw and
        // say so in the flags, so the reader never pays a decompress that buys nothing.
        byte[] stored = payload;
        ushort flags = 0;
        var scratch = new byte[LZ4Codec.MaximumOutputSize(payload.Length)];
        int written = LZ4Codec.Encode(payload, scratch, LZ4Level.L00_FAST);
        if (written > 0 && written < payload.Length)
        {
            stored = new byte[written];
            Buffer.BlockCopy(scratch, 0, stored, 0, written);
            flags |= FlagLz4;
        }

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(ContainerVersion);
            w.Write(flags);
            w.Write(data.Width);
            w.Write(data.Height);
            w.Write((byte)data.ContentFormat);
            w.Write((byte)System.Math.Min(byte.MaxValue, data.Levels.Length));
            w.Write((byte)(data.HasAlpha ? 1 : 0));
            w.Write((byte)0); // reserved
            w.Write(payload.Length);
            w.Write(stored.Length);
            w.Write(stored);
        }
        return ms.ToArray();
    }

    // Null on a truncated or foreign blob.
    public static TextureVariantData? Decode(byte[]? blob)
    {
        if (blob == null || blob.Length < 24)
            return null;

        try
        {
            using var ms = new MemoryStream(blob, writable: false);
            using var r = new BinaryReader(ms);

            if (r.ReadUInt32() != Magic)
                return null;
            ushort version = r.ReadUInt16();
            if (version != ContainerVersion)
                return null;

            ushort flags = r.ReadUInt16();
            int width = r.ReadInt32();
            int height = r.ReadInt32();
            var format = (TextureFormatKind)r.ReadByte();
            int levelCount = r.ReadByte();
            bool hasAlpha = r.ReadByte() != 0;
            r.ReadByte(); // reserved
            int plainLength = r.ReadInt32();
            int storedLength = r.ReadInt32();

            if (width <= 0 || height <= 0 || levelCount <= 0 || plainLength <= 0 || storedLength <= 0)
                return null;
            if (storedLength > ms.Length - ms.Position)
                return null;

            var stored = r.ReadBytes(storedLength);
            byte[] payload;
            if ((flags & FlagLz4) != 0)
            {
                payload = new byte[plainLength];
                int decoded = LZ4Codec.Decode(stored, payload);
                if (decoded != plainLength)
                    return null;
            }
            else
            {
                if (stored.Length != plainLength)
                    return null;
                payload = stored;
            }

            var levels = new byte[levelCount][];
            int offset = 0;
            int w = width, h = height;
            for (int level = 0; level < levelCount; level++)
            {
                int size = w * h * 4;
                if (offset + size > payload.Length)
                    return null;
                var data = new byte[size];
                Buffer.BlockCopy(payload, offset, data, 0, size);
                levels[level] = data;
                offset += size;
                w = System.Math.Max(1, w >> 1);
                h = System.Math.Max(1, h >> 1);
            }

            return new TextureVariantData
            {
                Levels = levels,
                Width = width,
                Height = height,
                HasAlpha = hasAlpha,
                ContentFormat = format,
            };
        }
        catch (Exception ex)
        {
            Logger.Warn($"TextureVariantStore: malformed variant container ({ex.Message})");
            return null;
        }
    }

    // GENERATION

    // The base level is area-downscaled to the variant's size, then the mip chain is built off that.
    public static TextureVariantData BuildVariant(byte[] baseRgba, int srcWidth, int srcHeight, TextureVariantId id, bool hasAlpha, TextureFormatKind contentFormat)
    {
        var (width, height) = id.ResolveSize(srcWidth, srcHeight);
        byte[] level0 = (width == srcWidth && height == srcHeight)
            ? baseRgba
            : Downscale(baseRgba, srcWidth, srcHeight, width, height);

        var levels = id.Mipmaps ? BuildMipChain(level0, width, height) : new[] { level0 };

        return new TextureVariantData
        {
            Levels = levels,
            Width = width,
            Height = height,
            HasAlpha = hasAlpha,
            ContentFormat = contentFormat,
        };
    }

    // Decodes the source exactly once and reuses that buffer for every bucket. Safe to call more than once for
    // the same asset: variants that already exist are skipped, so a re-import or a second world load costs
    // nothing.
    //
    // Returns the metadata for the BASE asset, which the caller can hand straight to a loaded
    // texture without a second decode.
    public static async Task<TextureMetadata?> GenerateAsync(
        LocalDB db,
        string baseLocalUri,
        bool isNormalMap = false,
        bool mipmaps = true,
        TextureCompressionKind compression = TextureCompressionKind.Block)
    {
        if (db == null || string.IsNullOrEmpty(baseLocalUri))
            return null;

        try
        {
            var sourceBytes = await db.ReadAssetBytesAsync(baseLocalUri).ConfigureAwait(false);
            if (sourceBytes == null || sourceBytes.Length == 0)
            {
                Logger.Warn($"TextureVariantStore: no source bytes for '{baseLocalUri}'");
                return null;
            }

            byte[]? rgba;
            int width, height;

            // An HDR source gets its sidecar (so a joiner learns the dimensions and the HDR fact
            // without pulling pixels) and nothing else. Every rung of the ladder is RGBA8, and an
            // RGBA8 rung of a radiance map is a clipped picture with a wrong name.
            if (TextureMetadata.DetectHdr(sourceBytes))
            {
                byte[]? half;
                try
                {
                    half = DecodeRgbaHalf(sourceBytes, out width, out height);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"TextureVariantStore: cannot decode HDR '{baseLocalUri}': {ex.Message}");
                    return null;
                }
                if (half == null || width <= 0 || height <= 0)
                    return null;

                int hdrMips = mipmaps ? TextureMetadata.FullMipCount(width, height) : 1;
                var hdrMeta = TextureMetadata.AnalyzeHalf(half, width, height, hdrMips,
                    sourceBytes.LongLength,
                    TextureMetadata.ComputeGpuBytes(TextureFormatKind.RGBA16F, width, height, hdrMips),
                    isNormalMap);
                await db.SetAssetMetadataAsync(baseLocalUri, bag => hdrMeta.WriteTo(bag)).ConfigureAwait(false);
                var hdrSidecar = GetMetadataUri(baseLocalUri);
                if (hdrSidecar != null && !db.Exists(hdrSidecar))
                {
                    await db.SaveDerivedAssetAsync(baseLocalUri, MetadataSuffix, hdrMeta.ToSidecarBytes(), MetadataExtension)
                        .ConfigureAwait(false);
                }
                return hdrMeta;
            }

            try
            {
                rgba = DecodeRgba(sourceBytes, out width, out height);
            }
            catch (Exception ex)
            {
                Logger.Warn($"TextureVariantStore: cannot decode '{baseLocalUri}': {ex.Message}");
                return null;
            }

            if (rgba == null || width <= 0 || height <= 0)
                return null;

            // Base metadata describes the SOURCE as it will actually be uploaded: source resolution,
            // the mip count the renderer will hold, and the measured channel usage.
            int baseMips = mipmaps ? TextureMetadata.FullMipCount(width, height) : 1;
            long baseDecoded = mipmaps
                ? TextureMetadata.ComputeGpuBytes(TextureFormatKind.RGBA8, width, height, baseMips)
                : (long)width * height * 4;

            var metadata = TextureMetadata.Analyze(
                rgba, width, height, baseMips,
                sourceBytes.LongLength, baseDecoded,
                TextureMetadata.DetectSRgb(sourceBytes), isNormalMap);

            await db.SetAssetMetadataAsync(baseLocalUri, bag => metadata.WriteTo(bag)).ConfigureAwait(false);

            var sidecarUri = GetMetadataUri(baseLocalUri);
            if (sidecarUri != null && !db.Exists(sidecarUri))
            {
                await db.SaveDerivedAssetAsync(baseLocalUri, MetadataSuffix, metadata.ToSidecarBytes(), MetadataExtension)
                    .ConfigureAwait(false);
            }

            var plan = TextureVariantId.PlanFor(width, height, mipmaps, compression);
            foreach (var id in plan)
            {
                var uri = GetVariantUri(baseLocalUri, id);
                if (uri == null || db.Exists(uri))
                    continue;

                var variant = BuildVariant(rgba, width, height, id, metadata.HasAlpha, metadata.ContentFormat);
                var blob = Encode(variant);
                await db.SaveDerivedAssetAsync(baseLocalUri, id.Identifier, blob, VariantExtension).ConfigureAwait(false);

                var variantMeta = new TextureMetadata
                {
                    Width = variant.Width,
                    Height = variant.Height,
                    ContentFormat = variant.ContentFormat,
                    HasAlpha = variant.HasAlpha,
                    MipCount = variant.MipCount,
                    SourceBytes = blob.LongLength,
                    DecodedBytes = variant.DecodedBytes,
                    SRgb = metadata.SRgb,
                    IsNormalMap = isNormalMap,
                    VariantId = id.Identifier,
                };
                await db.SetAssetMetadataAsync(uri, bag => variantMeta.WriteTo(bag)).ConfigureAwait(false);

                Logger.Log($"TextureVariantStore: {baseLocalUri} -> {id.Identifier} " +
                           $"({variant.Width}x{variant.Height}, {variant.MipCount} mips, {blob.Length / 1024} KB on disk)");
            }

            return metadata;
        }
        catch (Exception ex)
        {
            Logger.Error($"TextureVariantStore: variant generation failed for '{baseLocalUri}': {ex.Message}");
            return null;
        }
    }

    // Read straight off the URI, the same way the peer transferer resolves an owner.
    public static bool IsOwnedLocally(LocalDB db, string? localUri)
    {
        if (db == null || string.IsNullOrEmpty(localUri) || !localUri!.StartsWith("local://", StringComparison.Ordinal))
            return true;
        int slash = localUri.IndexOf('/', 8);
        var machineId = slash > 8 ? localUri.Substring(8, slash - 8) : localUri.Substring(8);
        return string.Equals(machineId, db.MachineId, StringComparison.Ordinal);
    }

    // Read a base texture's metadata without decoding a single pixel: the local record first, then
    // the sidecar blob on disk, then - for a texture another machine owns - the sidecar fetched
    // from that peer.
    //
    // That last hop is the point of having a sidecar at all. It is a couple of hundred bytes and it
    // tells a joiner the source's real dimensions, which is exactly what it needs to work out WHICH
    // variant to ask for. Without it a joiner would have to pull the full-resolution original just
    // to discover it should have asked for the 512px one. Fetched sidecars are written into the
    // local cache under the same derived address so the hop happens once. -xlinka
    public static async Task<TextureMetadata?> TryLoadMetadataAsync(LocalDB? db, string? baseLocalUri, AssetManager? manager = null)
    {
        if (db == null || string.IsNullOrEmpty(baseLocalUri))
            return null;

        var fromRecord = TextureMetadata.ReadFrom(db.GetAssetMetadata(baseLocalUri!));
        if (fromRecord != null)
            return fromRecord;

        var sidecarUri = GetMetadataUri(baseLocalUri);
        if (sidecarUri == null)
            return null;

        if (db.Exists(sidecarUri))
        {
            var local = await db.ReadAssetBytesAsync(sidecarUri).ConfigureAwait(false);
            return TextureMetadata.FromSidecarBytes(local);
        }

        // Only worth a network hop for an asset somebody else owns, and only inside a session.
        if (manager == null
            || IsOwnedLocally(db, baseLocalUri)
            || manager.Engine?.ActiveSessionTransferer == null)
            return null;

        byte[]? bytes;
        try
        {
            bytes = await manager.RequestGather(new Uri(sidecarUri)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"TextureVariantStore: metadata fetch failed for {sidecarUri}: {ex.Message}");
            return null;
        }

        var metadata = TextureMetadata.FromSidecarBytes(bytes);
        if (metadata != null && bytes != null)
            await db.SaveDerivedAssetAsync(baseLocalUri!, MetadataSuffix, bytes, MetadataExtension).ConfigureAwait(false);
        return metadata;
    }

    // Assets whose generation is already running, so a texture referenced by twenty slots queues
    // one generation pass and not twenty.
    private static readonly HashSet<string> _generating = new(StringComparer.Ordinal);

    // Queue variant generation for an asset that does not have any yet, without blocking the
    // caller. This is the catch-all: the import path generates eagerly, but textures reach the
    // engine by other routes too (a model's embedded textures, an asset received from a peer, a
    // world someone else built), and this makes the first load of any of them the trigger for
    // generating the variants that every load after it will use.
    //
    // Only for assets this machine owns. A texture a peer owns is theirs to generate; we ask them
    // for the variant and get the original if they have not made one yet. -xlinka
    public static void EnsureGeneratedInBackground(LocalDB? db, string? baseLocalUri, bool isNormalMap = false, bool mipmaps = true)
    {
        if (db == null || string.IsNullOrEmpty(baseLocalUri) || !IsOwnedLocally(db, baseLocalUri))
            return;

        lock (_generating)
        {
            if (!_generating.Add(baseLocalUri!))
                return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await GenerateAsync(db, baseLocalUri!, isNormalMap, mipmaps).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"TextureVariantStore: background generation failed for '{baseLocalUri}': {ex.Message}");
            }
            finally
            {
                lock (_generating)
                    _generating.Remove(baseLocalUri!);
            }
        });
    }

    public static List<TextureVariantId> ListCachedVariants(LocalDB? db, string? baseLocalUri)
    {
        var found = new List<TextureVariantId>();
        if (db == null || string.IsNullOrEmpty(baseLocalUri))
            return found;

        foreach (int bucket in TextureVariantId.SizeBuckets)
        {
            foreach (bool mips in new[] { true, false })
            {
                foreach (var compression in new[] { TextureCompressionKind.Block, TextureCompressionKind.None })
                {
                    var id = new TextureVariantId(bucket, mips, compression);
                    var uri = GetVariantUri(baseLocalUri, id);
                    if (uri != null && db.Exists(uri))
                        found.Add(id);
                }
            }
        }
        return found;
    }
}
