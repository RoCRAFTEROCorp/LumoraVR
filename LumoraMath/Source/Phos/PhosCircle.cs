// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Phos;

// A flat filled disc in the local XY plane, facing -Z, optionally cut to an arc.
//
// Two windings, because the choice actually matters downstream. A FAN puts a vertex at the centre and
// gives every triangle a share of it, which is what you want when the surface is shaded or textured
// radially - the centre carries its own UV and normal. Without the fan the disc is a single polygon
// triangulated from its first rim vertex, which is fewer triangles and fewer vertices and perfectly good
// for a flat unlit shape. The arc case always fans: a wedge has no sensible polygon triangulation from a
// rim vertex once it stops being convex about that vertex. -xlinka
public class PhosCircle : PhosShape
{
    public float Radius = 0.5f;

    // Radians. A full turn gives a closed disc; anything less leaves a wedge open at the centre.
    public float Arc = MathF.PI * 2f;

    public float2 UVScale = float2.One;

    public color? Color;

    public readonly int Segments;

    public readonly bool TriangleFan;

    public PhosVertex FirstVertex;

    private readonly PhosTriangleSubmesh _submesh;
    private readonly int _vertexCount;
    private readonly int _firstIndex;

    public PhosCircle(PhosTriangleSubmesh submesh, int segments = 32, bool triangleFan = true)
        : base(submesh.Mesh)
    {
        _submesh = submesh;
        Segments = System.Math.Max(3, segments);
        TriangleFan = triangleFan;

        Mesh.HasNormals = true;
        Mesh.HasTangents = true;
        Mesh.HasUV0s = true;

        // A closed disc shares its first and last rim vertex; an arc does not, so it needs one more.
        int rim = Segments + 1;
        _vertexCount = TriangleFan ? rim + 1 : rim;

        Mesh.IncreaseVertexCount(_vertexCount);
        FirstVertex = Mesh.GetVertex(Mesh.VertexCount - _vertexCount);
        _firstIndex = FirstVertex.IndexUnsafe;

        BuildTriangles();
    }

    private void BuildTriangles()
    {
        if (TriangleFan)
        {
            int centre = _firstIndex;
            for (int i = 0; i < Segments; i++)
            {
                // (v0, v2, v1) rather than (v0, v1, v2): this engine winds its front faces the opposite
                // way round from the naive order, the same reason AddQuadAsTriangles reorders. -xlinka
                _submesh.AddTriangle(centre, _firstIndex + 1 + i + 1, _firstIndex + 1 + i);
            }
            return;
        }

        for (int i = 1; i < Segments; i++)
            _submesh.AddTriangle(_firstIndex, _firstIndex + i + 1, _firstIndex + i);
    }

    public override void Update()
    {
        var mesh = Mesh;
        var positions = mesh.RawPositions;
        var normals = mesh.RawNormals;
        var tangents = mesh.RawTangents;
        var uvs = mesh.RawUV0s;
        bool colored = Color.HasValue && mesh.HasColors;

        float arc = System.Math.Clamp(Arc, 0f, MathF.PI * 2f);
        float3 normal = float3.Backward;
        var tangent = new float4(1f, 0f, 0f, -1f);

        int rimStart = TriangleFan ? _firstIndex + 1 : _firstIndex;

        if (TriangleFan)
        {
            positions[_firstIndex] = float3.Zero;
            normals[_firstIndex] = normal;
            tangents[_firstIndex] = tangent;
            uvs[_firstIndex] = new float2(0.5f, 0.5f) * UVScale;
            if (colored)
                mesh.RawColors[_firstIndex] = Color!.Value;
        }

        for (int i = 0; i <= Segments; i++)
        {
            float t = (float)i / Segments;
            float angle = t * arc;
            float x = MathF.Cos(angle);
            float y = MathF.Sin(angle);

            int index = rimStart + i;
            positions[index] = new float3(x * Radius, y * Radius, 0f);
            normals[index] = normal;
            tangents[index] = tangent;
            // Centre of the UV square at 0.5, so the disc maps into the middle of its texture the way a
            // quad of the same size would.
            uvs[index] = new float2(x * 0.5f + 0.5f, y * 0.5f + 0.5f) * UVScale;
            if (colored)
                mesh.RawColors[index] = Color!.Value;
        }

        TransformVertices(normalsAndTangents: true);
    }

    public override void Remove()
    {
        _submesh.Remove(AllTriangles);
        base.Remove();
    }
}
