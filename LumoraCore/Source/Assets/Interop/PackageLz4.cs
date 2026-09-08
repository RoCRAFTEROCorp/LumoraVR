// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using K4os.Compression.LZ4;

namespace Lumora.Core.Assets.Interop;

// LZ4 payloads inside a package are NOT in the standard LZ4 frame format, which is the trap here: point
// any off-the-shelf frame decoder at one and it rejects the magic. They use an older library's chunked
// stream instead, which is a much simpler thing:
//
//   repeat until the stream ends:
//     byte      flags          bit 0 set = this chunk is compressed
//     varint    originalLength
//     varint    compressedLength   (present only when the chunk is compressed)
//     bytes     payload            a raw LZ4 block, or the literal bytes when not compressed
//
// The payload of a compressed chunk is a plain LZ4 block, so the block decoder we already carry handles
// it and no extra dependency is needed. Verified against real packages: mesh bodies decode to exactly
// their stated original length and the leading floats come out as sane vertex coordinates. -xlinka
public static class PackageLz4
{
    private const int ChunkCompressed = 0x01;

    // A guard against a corrupt length turning into a huge allocation. No asset we have seen comes
    // close; a chunk claiming more than this is a malformed file, not a big mesh.
    private const int MaxChunkBytes = 256 * 1024 * 1024;

    public static byte[] Decode(ReadOnlySpan<byte> source)
    {
        using var output = new MemoryStream();
        int offset = 0;
        while (offset < source.Length)
        {
            if (!TryDecodeChunk(source, ref offset, output))
                break;
        }
        return output.ToArray();
    }

    private static bool TryDecodeChunk(ReadOnlySpan<byte> source, ref int offset, Stream output)
    {
        if (offset >= source.Length)
            return false;

        int flags = source[offset++];
        if (!TryReadVarInt(source, ref offset, out int originalLength))
            return false;
        if (originalLength <= 0 || originalLength > MaxChunkBytes)
            throw new InvalidDataException($"LZ4 chunk declares an implausible length ({originalLength}).");

        bool compressed = (flags & ChunkCompressed) != 0;
        if (!compressed)
        {
            if (offset + originalLength > source.Length)
                throw new InvalidDataException("LZ4 chunk runs past the end of the payload.");
            output.Write(source.Slice(offset, originalLength));
            offset += originalLength;
            return true;
        }

        if (!TryReadVarInt(source, ref offset, out int compressedLength))
            return false;
        if (compressedLength <= 0 || offset + compressedLength > source.Length)
            throw new InvalidDataException("LZ4 chunk runs past the end of the payload.");

        var target = new byte[originalLength];
        int written = LZ4Codec.Decode(source.Slice(offset, compressedLength), target);
        if (written != originalLength)
            throw new InvalidDataException($"LZ4 chunk decoded to {written} bytes, expected {originalLength}.");

        output.Write(target, 0, written);
        offset += compressedLength;
        return true;
    }

    // Same 7-bit little-endian varint the rest of the format uses for counts and string lengths.
    private static bool TryReadVarInt(ReadOnlySpan<byte> source, ref int offset, out int value)
    {
        value = 0;
        int shift = 0;
        while (offset < source.Length)
        {
            byte b = source[offset++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return true;
            shift += 7;
            if (shift > 28)
                throw new InvalidDataException("Malformed varint in LZ4 chunk header.");
        }
        return false;
    }
}
