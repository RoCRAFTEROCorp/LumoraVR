// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;

namespace Lumora.Core.Assets;

// Addressing for the renderer's machine-local compressed-texture cache.
//
// The renderer owns the contents of that cache (only it knows which block format the device took),
// but both sides have to agree on the NAME of a blob or the engine cannot tell whether a rung is
// already cheap to put on screen. The key lives here so it can only ever be computed one way. It
// hashes the identity of the pixels (address plus variant) together with the dimensions, so a blob
// can never be read back for a different image; dimensions are in there because a procedural resize
// reuses the same address. -xlinka
public static class TextureGpuCache
{
    public const string Extension = ".lvgpu";

    // Set once at startup by the render layer, which is the only thing that knows what this device can
    // sample. It is PART OF THE KEY: a blob baked as BPTC on a desktop and one baked as ASTC on a
    // headset are different bytes for the same picture, and without the tag they hash the same and the
    // wrong one gets handed over. Empty until set, which keys the same as the old scheme did. -xlinka
    public static string CompressionTag { get; set; } = string.Empty;

    // The per-texture inputs the format decision reads. Alpha is a property of the pixels and so of
    // the address, but the normal-map flag comes from the descriptor and two providers can load the
    // same address with it set differently; both would otherwise hash to one blob and one of them
    // would be handed the other's format. HDR is in for the same reason it is in the sidecar: a
    // blob baked as BC6H and one baked as BC1 must never share a name. -xlinka
    public static string Intent(bool hasAlpha, bool isNormalMap, bool isHdr) =>
        string.Concat(hasAlpha ? "a1" : "a0", isNormalMap ? "n1" : "n0", isHdr ? "h1" : "h0");

    public static string BuildKey(string uri, TextureVariantId? variant, int width, int height, int mipCount, string? intent = null)
    {
        string identity = $"{uri}|{variant?.Identifier ?? "src"}|{width}x{height}|{mipCount}|{CompressionTag}|{intent ?? string.Empty}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    public static string? GetPath(LocalDB? db, string? key)
    {
        if (db == null || string.IsNullOrEmpty(key))
            return null;
        var directory = db.GetGpuCachePath();
        return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, key + Extension);
    }

    // True when this exact payload has already been compressed on this machine. The point of asking
    // is that a cached rung skips both the decode and the compression, so a second visit to the same
    // world can go straight to full quality instead of paying for preview rungs it will throw away
    // milliseconds later.
    public static bool IsCached(LocalDB? db, string uri, TextureVariantId? variant, int width, int height, int mipCount, string? intent = null)
    {
        if (db == null || string.IsNullOrEmpty(uri) || width <= 0 || height <= 0)
            return false;
        var path = GetPath(db, BuildKey(uri, variant, width, height, mipCount, intent));
        try
        {
            return path != null && File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
