// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Meshes;

// A flat disc in the local XY plane, facing -Z, optionally cut to an arc.
//
// Sits between QuadMesh and RingMesh: a quad with a round silhouette, a ring with no hole. Worth having
// on its own because it is what gauges, radial fills and particle billboards are actually made of, and
// standing one up out of a ring with a zero inner radius wastes half its triangles on a degenerate band.
[ComponentCategory("Assets/Procedural Meshes")]
public class CircleMesh : ProceduralMesh
{
    public readonly Sync<float> Radius;

    public readonly Sync<int> Segments;

    // Degrees. 360 is a closed disc; less leaves a wedge.
    public readonly Sync<float> Arc;

    public readonly Sync<floatQ> Rotation;

    public readonly Sync<float2> UVScale;

    public readonly Sync<bool> ScaleUVWithSize;

    // A fan carries a real centre vertex, so a radial texture or a shaded surface has something to
    // interpolate toward. Off, the disc is one polygon triangulated from its first rim vertex - cheaper,
    // and indistinguishable on a flat unlit shape.
    public readonly Sync<bool> TriangleFan;

    private PhosCircle? _circle;
    private float _radius;
    private int _segments;
    private float _arc;
    private floatQ _rotation;
    private float2 _uvScale;
    private bool _triangleFan;

    public CircleMesh()
    {
        Radius = new Sync<float>(this, 0.5f);
        Segments = new Sync<int>(this, 32);
        Arc = new Sync<float>(this, 360f);
        Rotation = new Sync<floatQ>(this, floatQ.Identity);
        UVScale = new Sync<float2>(this, float2.One);
        ScaleUVWithSize = new Sync<bool>(this, false);
        TriangleFan = new Sync<bool>(this, true);
    }

    public override void OnAwake()
    {
        base.OnAwake();

        SubscribeToChanges(Radius);
        SubscribeToChanges(Segments);
        SubscribeToChanges(Arc);
        SubscribeToChanges(Rotation);
        SubscribeToChanges(UVScale);
        SubscribeToChanges(ScaleUVWithSize);
        SubscribeToChanges(TriangleFan);
    }

    protected override void PrepareAssetUpdateData()
    {
        _radius = Radius.Value;
        _segments = System.Math.Clamp(Segments.Value, 3, 512);
        _arc = System.Math.Clamp(Arc.Value, 0f, 360f) * (System.MathF.PI / 180f);
        _rotation = Rotation.Value;
        _triangleFan = TriangleFan.Value;

        _uvScale = UVScale.Value;
        if (ScaleUVWithSize.Value)
            _uvScale *= _radius;
    }

    protected override void UpdateMeshData(PhosMesh mesh)
    {
        // Segment count and winding decide how many vertices exist, so either changing means starting
        // over; radius, arc and UVs are just values written into the ones already there.
        bool rebuild = _circle != null
            && (_circle.Segments != _segments || _circle.TriangleFan != _triangleFan);

        uploadHint[MeshUploadHint.Flag.Geometry] = _circle == null || rebuild;

        if (_circle == null || rebuild)
        {
            if (_circle != null)
                mesh.Clear();

            var submesh = new PhosTriangleSubmesh(mesh);
            mesh.Submeshes.Add(submesh);
            _circle = new PhosCircle(submesh, _segments, _triangleFan);
        }

        _circle.Radius = _radius;
        _circle.Arc = _arc;
        _circle.UVScale = _uvScale;
        _circle.Rotation = _rotation;
        _circle.Update();
    }

    protected override void ClearMeshData()
    {
        _circle = null;
    }
}
