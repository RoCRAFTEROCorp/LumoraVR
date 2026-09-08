// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// Decoder for the serialized object graph a package carries as its main asset.
//
// Layout is a 9-byte header - the ASCII tag, an int32 version, and one byte naming the codec - wrapped
// around a BSON document. The codec byte is the part worth getting right: the tag is followed by the
// VERSION first, so reading the byte immediately after the tag gives you the version's low byte and a
// confident wrong answer about the compression.
//
// The payload decodes into Lumora's own DataTree nodes, so everything downstream (URL rewriting, the
// loader, the inspector) works on one node model rather than a parallel foreign one. -xlinka
public static class PackageGraphReader
{
    private const string Tag = "FrDT";
    private const int MaxSupportedVersion = 0;

    public enum Codec
    {
        None = 0,
        LZ4 = 1,
        LZMA = 2,
        Brotli = 3,
    }

    public readonly struct Header
    {
        public readonly int Version;
        public readonly Codec Codec;
        public Header(int version, Codec codec) { Version = version; Codec = codec; }
    }

    public static bool TryReadHeader(Stream stream, out Header header)
    {
        header = default;
        Span<byte> buffer = stackalloc byte[9];
        if (stream.Read(buffer) != buffer.Length)
            return false;
        for (int i = 0; i < Tag.Length; i++)
        {
            if (buffer[i] != (byte)Tag[i])
                return false;
        }
        int version = BinaryPrimitives.ReadInt32LittleEndian(buffer[4..8]);
        header = new Header(version, (Codec)buffer[8]);
        return true;
    }

    public static DataTreeDictionary Read(byte[] asset)
    {
        using var stream = new MemoryStream(asset, writable: false);
        return Read(stream);
    }

    public static DataTreeDictionary Read(Stream stream)
    {
        if (!TryReadHeader(stream, out var header))
            throw new InvalidDataException("Not a package object graph (bad header tag).");
        if (header.Version > MaxSupportedVersion)
            throw new NotSupportedException($"Package graph version {header.Version} is newer than supported ({MaxSupportedVersion}).");

        byte[] document = Decompress(stream, header.Codec);
        int offset = 0;
        var node = ReadDocument(document, ref offset);
        return node as DataTreeDictionary
            ?? throw new InvalidDataException("Package object graph root is not a dictionary.");
    }

    private static byte[] Decompress(Stream stream, Codec codec)
    {
        using var output = new MemoryStream();
        switch (codec)
        {
            case Codec.None:
                stream.CopyTo(output);
                break;

            case Codec.Brotli:
                using (var brotli = new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: true))
                    brotli.CopyTo(output);
                break;

            case Codec.LZ4:
            {
                using var raw = new MemoryStream();
                stream.CopyTo(raw);
                var decoded = PackageLz4.Decode(raw.GetBuffer().AsSpan(0, (int)raw.Length));
                output.Write(decoded, 0, decoded.Length);
                break;
            }

            default:
                // LZMA has not turned up in any package we have seen, and pulling in a decoder for a
                // codec with no observed traffic is not worth the dependency. Say so plainly instead of
                // failing with a corrupt-data error that would send someone hunting the wrong bug.
                throw new NotSupportedException(
                    $"Package graph uses the {codec} codec, which this importer does not decode.");
        }
        return output.ToArray();
    }

    // ---- BSON ----
    //
    // Only the eight element types a real graph actually contains are handled. Binary, regex, dates and
    // the other exotica have never appeared in one; if that changes the reader says which byte it choked
    // on rather than silently skipping a subtree and producing a plausible-looking wrong scene.

    private const byte TypeDouble = 0x01;
    private const byte TypeString = 0x02;
    private const byte TypeDocument = 0x03;
    private const byte TypeArray = 0x04;
    private const byte TypeBool = 0x08;
    private const byte TypeNull = 0x0A;
    private const byte TypeInt32 = 0x10;
    private const byte TypeUInt64 = 0x11;
    private const byte TypeInt64 = 0x12;

    private static DataTreeNode ReadDocument(byte[] b, ref int offset)
    {
        int start = offset;
        int size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset, 4));
        if (size < 5 || start + size > b.Length)
            throw new InvalidDataException($"BSON document length {size} at {start} runs past the buffer.");
        int end = start + size;
        offset += 4;

        // A BSON array is a document whose keys are "0", "1", "2"... Decide which node we are building
        // from the first key rather than from the parent's element type, so a nested array read through
        // the document path still comes out as a list.
        var pairs = new System.Collections.Generic.List<(string Key, DataTreeNode Node)>();
        bool looksLikeArray = true;
        int index = 0;

        while (offset < end - 1)
        {
            byte type = b[offset++];
            string key = ReadCString(b, ref offset);
            DataTreeNode value = ReadElement(b, ref offset, type);
            if (looksLikeArray && key != index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                looksLikeArray = false;
            index++;
            pairs.Add((key, value));
        }

        if (offset >= b.Length || b[offset] != 0x00)
            throw new InvalidDataException($"BSON document at {start} is missing its terminator.");
        offset = end;

        if (looksLikeArray && pairs.Count > 0)
        {
            var list = new DataTreeList();
            foreach (var (_, node) in pairs)
                list.Add(node);
            return list;
        }

        var dict = new DataTreeDictionary();
        foreach (var (key, node) in pairs)
            dict.AddOrUpdate(key, node);
        return dict;
    }

    private static DataTreeNode ReadElement(byte[] b, ref int offset, byte type)
    {
        switch (type)
        {
            case TypeDouble:
            {
                double v = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(offset, 8)));
                offset += 8;
                return new DataTreeValue(v);
            }
            case TypeString:
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset, 4));
                offset += 4;
                if (len < 1 || offset + len > b.Length)
                    throw new InvalidDataException($"BSON string length {len} at {offset} runs past the buffer.");
                string s = Encoding.UTF8.GetString(b, offset, len - 1);
                offset += len;
                // RawString keeps the leading '@' that marks a URL intact; the normal string constructor
                // would treat it as something to escape and the URL rewrite pass would then miss it.
                return DataTreeValue.RawString(s);
            }
            case TypeDocument:
            case TypeArray:
                return ReadDocument(b, ref offset);
            case TypeBool:
            {
                bool v = b[offset] != 0;
                offset += 1;
                return new DataTreeValue(v);
            }
            case TypeNull:
                return new DataTreeValue((IConvertible?)null);
            case TypeInt32:
            {
                int v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset, 4));
                offset += 4;
                return new DataTreeValue(v);
            }
            case TypeInt64:
            {
                long v = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(offset, 8));
                offset += 8;
                return new DataTreeValue(v);
            }
            case TypeUInt64:
            {
                ulong v = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(offset, 8));
                offset += 8;
                return new DataTreeValue(v);
            }
            default:
                throw new InvalidDataException(
                    $"Unsupported BSON element type 0x{type:X2} at offset {offset}.");
        }
    }

    private static string ReadCString(byte[] b, ref int offset)
    {
        int start = offset;
        while (offset < b.Length && b[offset] != 0x00)
            offset++;
        if (offset >= b.Length)
            throw new InvalidDataException($"Unterminated BSON key at {start}.");
        string s = Encoding.UTF8.GetString(b, start, offset - start);
        offset++;
        return s;
    }
}
