// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Assets.Interop;

// Decoder for the mesh payloads inside a package, into our own PhosMesh.
//
// The two formats line up almost field for field - positions, normals, tangents, colours, four-index
// bone bindings, named bones with a bind pose, per-channel UVs of 2/3/4 dimensions, submeshes carrying
// a topology name - so this is a transcription rather than a conversion.
//
// The header is stored plainly and only the BODY is compressed, with the codec named by a byte at the
// end of the header. Layout is version-gated (v4 through v7 all appear in real packages) and the reads
// below follow that gating exactly; getting one conditional wrong shifts every subsequent field and
// produces a mesh that decodes "successfully" into noise.
//
// Verified against 88 meshes from real packages: every one consumes its buffer exactly, with no
// trailing bytes, indices inside the vertex range and unit-length normals. -xlinka
public static class PackageMeshReader
{
    private const string Tag = "MeshX";
    private const int MaxSupportedVersion = 7;

    private enum BodyEncoding
    {
        Plain = 0,
        LZ4 = 1,
        LZMA = 2,
    }

    public static PhosMesh Read(byte[] asset)
    {
        var head = new Reader(asset);

        if (!string.Equals(head.String(), Tag, StringComparison.Ordinal))
            throw new InvalidDataException("Not a package mesh (bad tag).");

        int version = head.Int32();
        if (version > MaxSupportedVersion)
            throw new NotSupportedException($"Package mesh version {version} is newer than supported ({MaxSupportedVersion}).");

        uint flagBits = head.UInt32();
        int vertexCount = (int)head.VarInt();

        var mesh = new PhosMesh();
        if (vertexCount == 0)
            return mesh;

        int submeshCount = 0;
        int legacyTriangles = 0;
        if (version >= 3)
            submeshCount = (int)head.VarInt();
        else
            legacyTriangles = (int)head.VarInt();

        int boneCount = (int)head.VarInt();
        int blendShapeCount = version >= 4 ? (int)head.VarInt() : 0;

        // v1 and v2 carried a submesh-kind count here that later versions dropped.
        if (version >= 1 && version < 3)
            head.VarInt();

        int[] uvDimensions;
        if (version >= 6)
        {
            int channels = (int)head.VarInt();
            uvDimensions = new int[channels];
            for (int i = 0; i < channels; i++)
                uvDimensions[i] = head.Byte();
        }
        else
        {
            uvDimensions = Array.Empty<int>();
        }

        bool hasNormals = ReadFlag(ref flagBits);
        bool hasTangents = ReadFlag(ref flagBits);
        bool hasColors = ReadFlag(ref flagBits);
        bool hasBoneBindings = ReadFlag(ref flagBits);

        // Before v6 the UV channels were four more bits in the same flag word, always 2D.
        if (version < 6)
        {
            var legacy = new System.Collections.Generic.List<int>(4);
            for (int i = 0; i < 4; i++)
            {
                if (ReadFlag(ref flagBits))
                    legacy.Add(2);
            }
            uvDimensions = legacy.ToArray();
        }

        if (version > 6)
            head.String();   // colour profile, carried on the material side here

        var encoding = version >= 2 ? (BodyEncoding)head.Byte() : BodyEncoding.Plain;
        byte[] body = DecodeBody(asset, head.Offset, encoding);
        var r = new Reader(body);

        mesh.HasNormals = hasNormals;
        mesh.HasTangents = hasTangents;
        mesh.HasColors = hasColors;
        mesh.HasBoneBindings = hasBoneBindings;
        mesh.IncreaseVertexCount(vertexCount);
        for (int i = 0; i < uvDimensions.Length && i < 4; i++)
        {
            if (uvDimensions[i] > 0)
                mesh.SetHasUV(i, true);
        }

        var positions = mesh.RawPositions;
        for (int i = 0; i < vertexCount; i++)
            positions[i] = r.Float3();

        if (hasNormals)
        {
            var normals = mesh.RawNormals;
            for (int i = 0; i < vertexCount; i++)
                normals[i] = r.Float3();
        }
        if (hasTangents)
        {
            // W is the bitangent handedness, and mirroring V below reverses which way the bitangent
            // runs. Carry the stored sign across unchanged and every normal map on the avatar reads
            // inverted down one axis - bumps as dents. Blend-shape tangents are deltas of the tangent
            // DIRECTION only, no handedness, so they are left alone.
            var tangents = mesh.RawTangents;
            for (int i = 0; i < vertexCount; i++)
            {
                var t = r.Float4();
                tangents[i] = new float4(t.x, t.y, t.z, -t.w);
            }
        }
        if (hasColors)
        {
            var colors = mesh.RawColors;
            for (int i = 0; i < vertexCount; i++)
                colors[i] = new color(r.Single(), r.Single(), r.Single(), r.Single());
        }
        if (hasBoneBindings)
        {
            var bindings = mesh.RawBoneBindings;
            for (int i = 0; i < vertexCount; i++)
            {
                // Indices are varints, weights are plain floats - an easy pair to read the wrong way
                // round. And the indices are SIGNED: an unused influence is written as -1, which their
                // writer casts to an unsigned varint, so it arrives as 0xFFFF_FFFF_FFFF_FFFF. Taken at
                // face value that is 1.8e19, and the renderer then sizes its per-bone bounds array from
                // it - which is the "mesh_get_aabb: bs > sbs" the platform spams every frame. An unused
                // influence carries zero weight, so it is safe to fold to bone 0. -xlinka
                var indices = new float4(
                    BoneIndex(r.VarInt()), BoneIndex(r.VarInt()),
                    BoneIndex(r.VarInt()), BoneIndex(r.VarInt()));
                var weights = new float4(r.Single(), r.Single(), r.Single(), r.Single());
                bindings[i] = new PhosBoneBinding(indices, weights);
            }
        }

        // Our UV channels are 2D. A 3D or 4D channel still has to be READ in full or every field after
        // it shifts; the extra components are dropped, which costs nothing real - they carry packed
        // shader data for effects we do not reproduce anyway.
        //
        // V is MIRRORED on the way in. This engine samples from the top left (image row 0 is V=0) and
        // every other importer already lands its meshes in that convention - Assimp through FlipUVs for
        // the formats that need it, the OBJ parser by hand. This format counts V upward from the bottom
        // left, so read verbatim its atlas rows land on the wrong faces: paint that belongs down one
        // flank comes out across the chest, and it reads as a broken material rather than a flipped
        // axis. Same fix, same reason, one more format. -xlinka
        for (int channel = 0; channel < uvDimensions.Length; channel++)
        {
            int dimension = uvDimensions[channel];
            if (dimension <= 0)
                continue;

            bool storable = channel < 4;
            for (int i = 0; i < vertexCount; i++)
            {
                float u = r.Single();
                float v = dimension >= 2 ? r.Single() : 0f;
                for (int extra = 2; extra < dimension; extra++)
                    r.Single();
                if (storable)
                    mesh.SetUV(channel, i, new float2(u, 1f - v));
            }
        }

        if (version >= 3)
        {
            for (int s = 0; s < submeshCount; s++)
            {
                string topology = r.String();
                if (topology.Length == 0)
                    continue;   // a null submesh is written as an empty name and holds no indices
                ReadSubmesh(mesh, topology, ref r);
            }
        }
        else if (legacyTriangles > 0)
        {
            var submesh = new PhosTriangleSubmesh(mesh);
            submesh.IncreaseCount(legacyTriangles);
            var indices = submesh.RawIndices;
            for (int i = 0; i < legacyTriangles * 3; i++)
                indices[i] = r.Int32();
            ReverseWinding(indices, legacyTriangles * 3);
            mesh.Submeshes.Add(submesh);
        }

        for (int i = 0; i < boneCount; i++)
        {
            string boneName = r.String();
            var bindPose = r.Float4x4();
            mesh.AddBone(boneName).BindPose = bindPose;
        }

        for (int i = 0; i < blendShapeCount; i++)
            ReadBlendShape(mesh, vertexCount, ref r);

        return mesh;
    }

    private static void ReadSubmesh(PhosMesh mesh, string topology, ref Reader r)
    {
        int count = (int)r.VarInt();

        // Lines and quads have no counterpart here. They must still be CONSUMED at their real stride or
        // the bone table after them decodes into noise, so read them out and drop them.
        int strideOverride = topology switch
        {
            "Lines" => 2,
            "Quads" => 4,
            _ => 0,
        };
        if (strideOverride > 0)
        {
            for (int i = 0; i < count * strideOverride; i++)
                r.Int32();
            return;
        }

        PhosSubmesh submesh = topology == "Points"
            ? new PhosPointSubmesh(mesh)
            : new PhosTriangleSubmesh(mesh);
        if (count > 0)
            submesh.IncreaseCount(count);

        var indices = submesh.RawIndices;
        int total = count * submesh.IndicesPerElement;
        for (int i = 0; i < total; i++)
            indices[i] = r.Int32();
        if (submesh is PhosTriangleSubmesh)
            ReverseWinding(indices, total);
        mesh.Submeshes.Add(submesh);
    }

    // Their triangles are wound the opposite way round from ours.
    //
    // The test that settles it is the triangle's own geometry against its stored vertex normals: take
    // cross(v1 - v0, v2 - v0) and see which way it points. Every mesh in a package has that cross
    // pointing ALONG the normal. Everything our own generators emit has it pointing AGAINST - see
    // PhosTriangleSubmesh.AddQuadAsTriangles, which reorders to (v0, v2, v1) for exactly this reason -
    // and those render correctly under the default back-face cull. So an imported mesh arrives
    // inside-out: you see the far wall of the model and the near side is culled away.
    //
    // It reads as a lighting or material fault rather than a geometry one, which is how it got blamed
    // on the shader. Flipping the material to front-cull hides it, but that is a per-material patch on
    // a whole-format mismatch, and it lies to anything that later asks the material which side it
    // draws. Fix it once, here, where the data comes in. -xlinka
    private static void ReverseWinding(int[] indices, int count)
    {
        for (int i = 0; i + 2 < count; i += 3)
            (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
    }

    private static void ReadBlendShape(PhosMesh mesh, int vertexCount, ref Reader r)
    {
        string name = r.String();
        uint flags = (uint)r.VarInt();
        bool hasNormals = ReadFlag(ref flags);
        bool hasTangents = ReadFlag(ref flags);
        int frameCount = (int)r.VarInt();

        var shape = new PhosBlendShape(name, frameCount);
        for (int f = 0; f < frameCount; f++)
        {
            var frame = shape.Frames[f];
            // Per-frame weight is read and dropped: our blend shapes are single-frame, driven by a
            // weight on the renderer rather than carrying one per frame. It still has to come off the
            // stream in the right place.
            r.Single();
            frame.positions = new float3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                frame.positions[i] = r.Float3();

            if (hasNormals)
            {
                frame.normals = new float3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                    frame.normals[i] = r.Float3();
            }
            if (hasTangents)
            {
                frame.tangents = new float3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                    frame.tangents[i] = r.Float3();
            }
        }
        mesh.BlendShapes.Add(shape);
    }

    // Bodies over 1 MiB are split into several chunks, so this always loops. Decoding only the first
    // chunk still "works" on small meshes and fails on exactly the big ones, which is a nasty way to
    // find out - PackageLz4 handles the framing.
    private static byte[] DecodeBody(byte[] asset, int offset, BodyEncoding encoding) => encoding switch
    {
        BodyEncoding.Plain => asset[offset..],
        BodyEncoding.LZ4 => PackageLz4.Decode(asset.AsSpan(offset)),
        _ => throw new NotSupportedException($"Package mesh body uses the {encoding} codec, which this importer does not decode."),
    };

    // Reinterpret the unsigned varint as the signed index it was written from, then clamp: anything
    // negative marks an unused influence.
    private static float BoneIndex(ulong raw)
    {
        long signed = unchecked((long)raw);
        return signed < 0 ? 0f : signed;
    }

    private static bool ReadFlag(ref uint data)
    {
        bool flag = (data & 1) != 0;
        data >>= 1;
        return flag;
    }

    // Matches the writer on the other side: a 7-bit length prefix then UTF-8 bytes for strings, plain
    // little-endian for everything else.
    private ref struct Reader
    {
        private readonly byte[] _b;
        public int Offset;

        public Reader(byte[] buffer)
        {
            _b = buffer;
            Offset = 0;
        }

        public byte Byte() => _b[Offset++];

        public int Int32()
        {
            int v = BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(Offset, 4));
            Offset += 4;
            return v;
        }

        public uint UInt32()
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_b.AsSpan(Offset, 4));
            Offset += 4;
            return v;
        }

        public float Single()
        {
            float v = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(Offset, 4)));
            Offset += 4;
            return v;
        }

        public float3 Float3() => new float3(Single(), Single(), Single());

        public float4 Float4() => new float4(Single(), Single(), Single(), Single());

        public float4x4 Float4x4()
        {
            var m = new float4x4();
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                    m[row, col] = Single();
            }
            return m;
        }

        public ulong VarInt()
        {
            ulong value = 0;
            int shift = 0;
            while (true)
            {
                byte b = _b[Offset++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return value;
                shift += 7;
                if (shift > 63)
                    throw new InvalidDataException("Malformed varint in package mesh.");
            }
        }

        public string String()
        {
            int length = (int)VarInt();
            if (length == 0)
                return string.Empty;
            if (Offset + length > _b.Length)
                throw new InvalidDataException("Package mesh string runs past the buffer.");
            string s = Encoding.UTF8.GetString(_b, Offset, length);
            Offset += length;
            return s;
        }
    }
}
