// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Meshes;

[ComponentCategory("Assets/Procedural Meshes")]
public class CurvedPlaneMesh : ProceduralMesh
{
    public readonly Sync<float2> Size;
    public readonly Sync<float> Curvature;
    public readonly Sync<int> Segments;

    private PhosTriangleSubmesh? _submesh;
    private float2 _size;
    private float _curvature;
    private int _segments;
    private int _lastVertexCount;
    private int _lastTriangleCount;

    public CurvedPlaneMesh()
    {
        Size = new Sync<float2>(this, new float2(1f, 0.5625f));
        Curvature = new Sync<float>(this, 0.5f);
        Segments = new Sync<int>(this, 24);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        SubscribeToChanges(Size);
        SubscribeToChanges(Curvature);
        SubscribeToChanges(Segments);
    }

    protected override void PrepareAssetUpdateData()
    {
        _size = Size.Value;
        _curvature = System.Math.Clamp(Curvature.Value, 0f, 1f);
        _segments = System.Math.Clamp(Segments.Value, 2, 256);
    }

    protected override void UpdateMeshData(PhosMesh mesh)
    {
        int columns = _segments;
        int vertexCount = columns * 2;
        int triangleCount = (columns - 1) * 2;
        bool topologyChanged = _submesh == null ||
                               _lastVertexCount != vertexCount ||
                               _lastTriangleCount != triangleCount;

        if (topologyChanged)
        {
            mesh.Clear();
            mesh.HasNormals = true;
            mesh.SetHasUV(0, true);
            mesh.IncreaseVertexCount(vertexCount);

            _submesh = new PhosTriangleSubmesh(mesh);
            mesh.Submeshes.Add(_submesh);
            _submesh.SetCount(triangleCount);
            _lastVertexCount = vertexCount;
            _lastTriangleCount = triangleCount;
        }
        else
        {
            mesh.HasNormals = true;
            mesh.SetHasUV(0, true);
        }

        BuildPlane(mesh, _submesh!);
        uploadHint[MeshUploadHint.Flag.Geometry] = topologyChanged;
        uploadHint[MeshUploadHint.Flag.Normals] = true;
        uploadHint[MeshUploadHint.Flag.UV0] = true;
    }

    protected override void ClearMeshData()
    {
        _submesh = null;
        _lastVertexCount = 0;
        _lastTriangleCount = 0;
    }

    private void BuildPlane(PhosMesh mesh, PhosTriangleSubmesh submesh)
    {
        float width = _size.x;
        float halfHeight = _size.y * 0.5f;
        float radius = width * 0.5f;
        bool linear = _curvature < 0.01f;

        float totalAngle = MathF.PI * _curvature;
        float startAngle = (MathF.PI - totalAngle) * 0.5f;
        float globalOffset = MathF.Sin(startAngle) * radius;
        float widthAdjust = linear ? 0f : 1f / MathF.Cos(startAngle);
        float invCols = 1f / ColumnsMinusOne();

        for (int i = 0; i < _segments; i++)
        {
            float t = i * invCols;
            float angle = startAngle + totalAngle * t;

            float x, z;
            float3 normal;
            if (linear)
            {
                x = t * width - radius;
                z = 0f;
                normal = new float3(0f, 0f, 1f);
            }
            else
            {
                // CONCAVE toward the viewer. The middle recedes and the edges come forward, so a panel
                // placed in front of a head wraps around it. This used to put the middle at +Z with the
                // edges at zero, which on the dashboard read as a barrel bulging at the person; the
                // source platform's dash curves the other way, edges toward you. The circle's centre is
                // now on the viewer's side, and the normal points back at that centre. -xlinka
                x = -MathF.Cos(angle) * widthAdjust * radius;
                z = globalOffset - MathF.Sin(angle) * radius;
                normal = new float3(MathF.Cos(angle), 0f, MathF.Sin(angle)).Normalized;
            }

            for (int row = 0; row < 2; row++)
            {
                int index = i * 2 + row;
                float y = row == 1 ? halfHeight : -halfHeight;
                mesh.RawPositions[index] = new float3(x, y, z);
                mesh.RawNormals[index] = normal;
                mesh.RawUV0s[index] = new float2(t, row == 1 ? 0f : 1f);
            }
        }

        int triangle = 0;
        for (int i = 0; i < _segments - 1; i++)
        {
            int b0 = i * 2;
            int t0 = i * 2 + 1;
            int b1 = (i + 1) * 2;
            int t1 = (i + 1) * 2 + 1;

            submesh.SetTriangle(triangle++, b0, t0, t1);
            submesh.SetTriangle(triangle++, b0, t1, b1);
        }
    }

    private float ColumnsMinusOne() => _segments > 1 ? _segments - 1 : 1;
}
