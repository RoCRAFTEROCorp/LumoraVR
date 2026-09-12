// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Meshes;

// A field of bubbles as real geometry, animated in the vertex shader.
//
// The local home's rising bubbles used to be an analytic raytrace: every pixel on screen tested a
// ray against 72 spheres and 72 cylinders, then the floor did the same loop again for ripple rings.
// That is O(pixels x bubbles) and it put a Pico 4 at 3 fps with five draw calls. This mesh holds
// one low-poly unit sphere, one unit neck and one floor ring quad per bubble, all in ONE surface,
// and the LocalHomeRising shader moves each part to its bubble's centre from the same hash-driven
// life curve. Cost becomes O(bubble pixels) and the bubbles get real MSAA edges, real depth and
// proper sorting against the avatar, which the raytrace never had.
//
// Per-vertex data the shader reads:
//   UV0 = (bubble index, part)   part 0 = bubble, 1 = neck, 2 = floor ripple ring
//   UV1 = shape parameters       sphere (phi, theta) in [0,1], neck (phi, level), ring (x, z) in [-1,1]
// The authored positions are the unit shapes; the shader rebuilds them from UV1 so it does not
// depend on the slot's transform being identity. The bounding box is overridden with the animated
// volume, otherwise Godot culls the mesh on its 1 m unit-shape AABB. -xlinka
[ComponentCategory("Assets/Procedural Meshes")]
public class BubbleFieldMesh : ProceduralMesh
{
    public readonly Sync<int> Count;
    public readonly Sync<int> SphereRings;
    public readonly Sync<int> SphereSegments;
    public readonly Sync<int> NeckSegments;
    public readonly Sync<bool> Necks;
    public readonly Sync<bool> Ripples;

    // Animated volume, mirrored from the material so the culling box matches what the shader draws.
    public readonly Sync<float2> VolumeExtents;
    public readonly Sync<float> VolumeHeight;
    public readonly Sync<float3> VolumeOffset;
    public readonly Sync<float> BoundsMargin;

    public const int MaxCount = 256;

    private int _count;
    private int _rings;
    private int _segments;
    private int _neckSegments;
    private bool _necks;
    private bool _ripples;
    private float2 _extents;
    private float _height;
    private float3 _offset;
    private float _margin;

    private PhosTriangleSubmesh? _submesh;
    private int _builtCount = -1;
    private int _builtRings;
    private int _builtSegments;
    private int _builtNeckSegments;
    private bool _builtNecks;
    private bool _builtRipples;

    public BubbleFieldMesh()
    {
        Count = new Sync<int>(this, 72);
        SphereRings = new Sync<int>(this, 8);
        SphereSegments = new Sync<int>(this, 12);
        NeckSegments = new Sync<int>(this, 10);
        Necks = new Sync<bool>(this, true);
        Ripples = new Sync<bool>(this, true);
        VolumeExtents = new Sync<float2>(this, new float2(24f, 24f));
        VolumeHeight = new Sync<float>(this, 7f);
        VolumeOffset = new Sync<float3>(this, new float3(0f, -3.5f, 0f));
        BoundsMargin = new Sync<float>(this, 1.5f);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        SubscribeToChanges(Count);
        SubscribeToChanges(SphereRings);
        SubscribeToChanges(SphereSegments);
        SubscribeToChanges(NeckSegments);
        SubscribeToChanges(Necks);
        SubscribeToChanges(Ripples);
        SubscribeToChanges(VolumeExtents);
        SubscribeToChanges(VolumeHeight);
        SubscribeToChanges(VolumeOffset);
        SubscribeToChanges(BoundsMargin);
    }

    protected override void PrepareAssetUpdateData()
    {
        _count = System.Math.Clamp(Count.Value, 1, MaxCount);
        _rings = System.Math.Clamp(SphereRings.Value, 3, 32);
        _segments = System.Math.Clamp(SphereSegments.Value, 4, 48);
        _neckSegments = System.Math.Clamp(NeckSegments.Value, 3, 48);
        _necks = Necks.Value;
        _ripples = Ripples.Value;
        _extents = VolumeExtents.Value;
        _height = VolumeHeight.Value;
        _offset = VolumeOffset.Value;
        _margin = System.Math.Max(0f, BoundsMargin.Value);

        // The shader places everything relative to the slot origin plus VolumeOffset, so the culling
        // box is the volume itself with room for a bubble to burst past its ceiling.
        var min = _offset + new float3(-_extents.x - _margin, -_margin, -_extents.y - _margin);
        var max = _offset + new float3(_extents.x + _margin, _height + _margin, _extents.y + _margin);
        OverrideBoundingBox.Value = true;
        OverridenBoundingBox.Value = new BoundingBox(min, max);
    }

    protected override void UpdateMeshData(PhosMesh mesh)
    {
        bool rebuild = _submesh == null
            || _builtCount != _count
            || _builtRings != _rings
            || _builtSegments != _segments
            || _builtNeckSegments != _neckSegments
            || _builtNecks != _necks
            || _builtRipples != _ripples;

        uploadHint[MeshUploadHint.Flag.Geometry] = rebuild;
        if (!rebuild)
            return;

        mesh.Clear();
        mesh.Submeshes.Clear();
        _submesh = new PhosTriangleSubmesh(mesh);
        mesh.Submeshes.Add(_submesh);

        mesh.HasNormals = true;
        mesh.HasUV0s = true;
        mesh.SetHasUV(1, true);

        for (int i = 0; i < _count; i++)
        {
            AddSphere(mesh, _submesh, i);
            if (_necks) AddNeck(mesh, _submesh, i);
            if (_ripples) AddRing(mesh, _submesh, i);
        }

        _builtCount = _count;
        _builtRings = _rings;
        _builtSegments = _segments;
        _builtNeckSegments = _neckSegments;
        _builtNecks = _necks;
        _builtRipples = _ripples;
    }

    protected override void ClearMeshData()
    {
        _submesh = null;
        _builtCount = -1;
    }

    // Godot wants clockwise front faces seen from outside; every quad below is wound that way.

    private void AddSphere(PhosMesh mesh, PhosTriangleSubmesh submesh, int bubble)
    {
        int cols = _segments + 1;
        int rows = _rings + 1;
        int first = mesh.VertexCount;
        mesh.IncreaseVertexCount(cols * rows);

        var tag = new float2(bubble, 0f);
        int index = first;
        for (int r = 0; r < rows; r++)
        {
            float v = (float)r / _rings;
            float theta = v * LuminaMath.PI;
            float sinT = LuminaMath.Sin(theta);
            float cosT = LuminaMath.Cos(theta);
            for (int s = 0; s < cols; s++)
            {
                float u = (float)s / _segments;
                float phi = u * (2f * LuminaMath.PI);
                var p = new float3(sinT * LuminaMath.Cos(phi), cosT, sinT * LuminaMath.Sin(phi));
                mesh.RawPositions[index] = p;
                mesh.RawNormals[index] = p;
                mesh.RawUV0s[index] = tag;
                mesh.SetUV(1, index, new float2(u, v));
                index++;
            }
        }

        for (int r = 0; r < _rings; r++)
        {
            for (int s = 0; s < _segments; s++)
            {
                int a = first + r * cols + s;
                int b = a + 1;
                int d = a + cols;
                int c = d + 1;
                // The band touching a pole has one degenerate triangle per quad; leave it out.
                if (r < _rings - 1) submesh.AddTriangle(a, d, c);
                if (r > 0) submesh.AddTriangle(a, c, b);
            }
        }
    }

    private void AddNeck(PhosMesh mesh, PhosTriangleSubmesh submesh, int bubble)
    {
        int cols = _neckSegments + 1;
        int first = mesh.VertexCount;
        mesh.IncreaseVertexCount(cols * 2);

        var tag = new float2(bubble, 1f);
        int index = first;
        for (int level = 0; level < 2; level++)
        {
            for (int s = 0; s < cols; s++)
            {
                float u = (float)s / _neckSegments;
                float phi = u * (2f * LuminaMath.PI);
                float cx = LuminaMath.Cos(phi);
                float cz = LuminaMath.Sin(phi);
                mesh.RawPositions[index] = new float3(cx, level, cz);
                mesh.RawNormals[index] = new float3(cx, 0f, cz);
                mesh.RawUV0s[index] = tag;
                mesh.SetUV(1, index, new float2(u, level));
                index++;
            }
        }

        for (int s = 0; s < _neckSegments; s++)
        {
            int a = first + s;
            int b = a + 1;
            int d = a + cols;
            int c = d + 1;
            submesh.AddTriangle(a, b, c);
            submesh.AddTriangle(a, c, d);
        }
    }

    private void AddRing(PhosMesh mesh, PhosTriangleSubmesh submesh, int bubble)
    {
        int first = mesh.VertexCount;
        mesh.IncreaseVertexCount(4);

        var tag = new float2(bubble, 2f);
        Corner(mesh, first + 0, -1f, -1f, tag);
        Corner(mesh, first + 1, 1f, -1f, tag);
        Corner(mesh, first + 2, 1f, 1f, tag);
        Corner(mesh, first + 3, -1f, 1f, tag);

        submesh.AddTriangle(first + 0, first + 1, first + 2);
        submesh.AddTriangle(first + 0, first + 2, first + 3);
    }

    private static void Corner(PhosMesh mesh, int index, float x, float z, float2 tag)
    {
        mesh.RawPositions[index] = new float3(x, 0f, z);
        mesh.RawNormals[index] = float3.Up;
        mesh.RawUV0s[index] = tag;
        mesh.SetUV(1, index, new float2(x, z));
    }
}
