// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Meshes;

// A whole hand skeleton (joint spheres + bone tubes) as ONE mesh, rebuilt in place from a pose.
//
// The default hand used to be a slot, a CylinderMesh and a MeshRenderer per bone and per joint,
// roughly fifty draw calls per hand, each casting its own shadow, and every frame it rewrote the
// height and radius of twenty cylinders, which is twenty procedural regenerations and twenty GPU
// uploads for one hand moving. On a Pico 4 looking at your own hands that was a third of the visible
// draw calls and every shadow draw in the frame. Now the topology is built once for a given joint
// and bone count and a pose update only rewrites positions and normals: one upload, one draw.
//
// The pose is deliberately NOT a sync field. The hand visual is per-viewer (each peer drives it from
// its own input or from the controller's rest pose), so replicating vertices would only fight that. -xlinka
[ComponentCategory("Assets/Procedural Meshes")]
public sealed class HandSkeletonMesh : ProceduralMesh
{
    public readonly Sync<int> SphereSegments;
    public readonly Sync<int> SphereRings;
    public readonly Sync<int> TubeSegments;

    private float3[] _joints = Array.Empty<float3>();
    private float[] _jointRadii = Array.Empty<float>();
    private int _jointCount;
    private (int a, int b)[] _bones = Array.Empty<(int, int)>();
    private int _boneCount;
    private float _boneRadius;

    private int _segments;
    private int _rings;
    private int _tubeSegments;

    private PhosTriangleSubmesh? _submesh;
    private int _builtJoints = -1;
    private int _builtBones = -1;
    private int _builtSegments;
    private int _builtRings;
    private int _builtTubeSegments;

    public HandSkeletonMesh()
    {
        SphereSegments = new Sync<int>(this, 8);
        SphereRings = new Sync<int>(this, 6);
        TubeSegments = new Sync<int>(this, 6);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        SubscribeToChanges(SphereSegments);
        SubscribeToChanges(SphereRings);
        SubscribeToChanges(TubeSegments);
    }

    // Joint positions are in THIS slot's local space. Bones index into the joint array. Call once per
    // frame; the geometry is rewritten in place and uploaded once.
    public void SetPose(ReadOnlySpan<float3> joints, ReadOnlySpan<float> jointRadii, ReadOnlySpan<(int a, int b)> bones, float boneRadius)
    {
        if (_joints.Length < joints.Length)
        {
            _joints = new float3[joints.Length];
            _jointRadii = new float[joints.Length];
        }
        if (_bones.Length < bones.Length)
            _bones = new (int, int)[bones.Length];

        // A hand at rest (desktop, or a controller lying still) must not cost an upload a frame.
        if (_builtJoints == joints.Length && _builtBones == bones.Length && boneRadius == _boneRadius
            && joints.SequenceEqual(_joints.AsSpan(0, joints.Length))
            && jointRadii.SequenceEqual(_jointRadii.AsSpan(0, jointRadii.Length))
            && bones.SequenceEqual(_bones.AsSpan(0, bones.Length)))
            return;

        joints.CopyTo(_joints);
        jointRadii.CopyTo(_jointRadii);
        bones.CopyTo(_bones);
        _jointCount = joints.Length;
        _boneCount = bones.Length;
        _boneRadius = boneRadius;

        RegenerateMesh();
    }

    protected override void PrepareAssetUpdateData()
    {
        _segments = System.Math.Clamp(SphereSegments.Value, 4, 32);
        _rings = System.Math.Clamp(SphereRings.Value, 3, 24);
        _tubeSegments = System.Math.Clamp(TubeSegments.Value, 3, 32);
    }

    protected override void UpdateMeshData(PhosMesh mesh)
    {
        bool rebuild = _submesh == null
            || _builtJoints != _jointCount
            || _builtBones != _boneCount
            || _builtSegments != _segments
            || _builtRings != _rings
            || _builtTubeSegments != _tubeSegments;

        if (rebuild)
        {
            mesh.Clear();
            mesh.Submeshes.Clear();
            _submesh = new PhosTriangleSubmesh(mesh);
            mesh.Submeshes.Add(_submesh);
            mesh.HasNormals = true;

            int sphereVerts = (_segments + 1) * (_rings + 1);
            int tubeVerts = (_tubeSegments + 1) * 2;
            mesh.IncreaseVertexCount(_jointCount * sphereVerts + _boneCount * tubeVerts);

            int first = 0;
            for (int j = 0; j < _jointCount; j++)
            {
                SphereTopology(_submesh, first);
                first += sphereVerts;
            }
            for (int b = 0; b < _boneCount; b++)
            {
                TubeTopology(_submesh, first);
                first += tubeVerts;
            }

            _builtJoints = _jointCount;
            _builtBones = _boneCount;
            _builtSegments = _segments;
            _builtRings = _rings;
            _builtTubeSegments = _tubeSegments;
        }

        // Positions and normals move every frame; the index buffer and everything else stay put.
        uploadHint.SetAll();
        uploadHint[MeshUploadHint.Flag.Dynamic] = true;

        var positions = mesh.RawPositions;
        var normals = mesh.RawNormals;
        int index = 0;
        for (int j = 0; j < _jointCount; j++)
            WriteSphere(positions, normals, ref index, _joints[j], _jointRadii[j]);
        for (int b = 0; b < _boneCount; b++)
        {
            var (a, c) = _bones[b];
            bool valid = a >= 0 && a < _jointCount && c >= 0 && c < _jointCount;
            WriteTube(positions, normals, ref index, valid ? _joints[a] : float3.Zero, valid ? _joints[c] : float3.Zero, _boneRadius);
        }
    }

    protected override void ClearMeshData()
    {
        _submesh = null;
        _builtJoints = -1;
        _builtBones = -1;
    }

    // Clockwise seen from outside, which is what Godot treats as a front face.

    private void SphereTopology(PhosTriangleSubmesh submesh, int first)
    {
        int cols = _segments + 1;
        for (int r = 0; r < _rings; r++)
        {
            for (int s = 0; s < _segments; s++)
            {
                int a = first + r * cols + s;
                int b = a + 1;
                int d = a + cols;
                int c = d + 1;
                if (r < _rings - 1) submesh.AddTriangle(a, d, c);
                if (r > 0) submesh.AddTriangle(a, c, b);
            }
        }
    }

    private void TubeTopology(PhosTriangleSubmesh submesh, int first)
    {
        int cols = _tubeSegments + 1;
        for (int s = 0; s < _tubeSegments; s++)
        {
            int a = first + s;
            int b = a + 1;
            int d = a + cols;
            int c = d + 1;
            submesh.AddTriangle(a, b, c);
            submesh.AddTriangle(a, c, d);
        }
    }

    private void WriteSphere(float3[] positions, float3[] normals, ref int index, float3 centre, float radius)
    {
        int cols = _segments + 1;
        int rows = _rings + 1;
        for (int r = 0; r < rows; r++)
        {
            float theta = (float)r / _rings * LuminaMath.PI;
            float sinT = LuminaMath.Sin(theta);
            float cosT = LuminaMath.Cos(theta);
            for (int s = 0; s < cols; s++)
            {
                float phi = (float)s / _segments * (2f * LuminaMath.PI);
                var n = new float3(sinT * LuminaMath.Cos(phi), cosT, sinT * LuminaMath.Sin(phi));
                positions[index] = centre + n * radius;
                normals[index] = n;
                index++;
            }
        }
    }

    // A tube from a to b. The frame (u, d, v) is right-handed like (X, Y, Z) so the tube winds the same
    // way the sphere does. A bone shorter than a hair collapses to a point and draws nothing. -xlinka
    private void WriteTube(float3[] positions, float3[] normals, ref int index, float3 a, float3 b, float radius)
    {
        var d = b - a;
        float length = d.Length;
        int cols = _tubeSegments + 1;
        if (length < 0.0005f)
        {
            for (int i = 0; i < cols * 2; i++)
            {
                positions[index] = a;
                normals[index] = float3.Up;
                index++;
            }
            return;
        }

        d /= length;
        var reference = LuminaMath.Abs(d.y) < 0.9f ? float3.Up : float3.Right;
        var u = LuminaMath.Cross(reference, d).Normalized;
        var v = LuminaMath.Cross(u, d);

        for (int level = 0; level < 2; level++)
        {
            var origin = level == 0 ? a : b;
            for (int s = 0; s < cols; s++)
            {
                float phi = (float)s / _tubeSegments * (2f * LuminaMath.PI);
                var n = u * LuminaMath.Cos(phi) + v * LuminaMath.Sin(phi);
                positions[index] = origin + n * radius;
                normals[index] = n;
                index++;
            }
        }
    }
}
