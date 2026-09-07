// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Phos;

// Blend shape (morph target) data: position/normal/tangent deltas applied on top of the base vertices.
public class PhosBlendShape
{
    public string Name { get; set; } = "";

    public PhosBlendShapeFrame[] Frames { get; set; } = Array.Empty<PhosBlendShapeFrame>();

    public PhosBlendShape(string name, int frameCount = 1)
    {
        Name = name;
        // PhosBlendShapeFrame is a CLASS, so `new PhosBlendShapeFrame[frameCount]` leaves every slot NULL.
        // Callers (PhosMesh.GetBlendShape, PhosVertex blendshape access) write straight into
        // Frames[i].positions/normals/tangents without null-checking, so instantiate each frame here or they NRE.
        // This was the "MeshDecoder: Failed to decode mesh - Object reference not set" crash on any model with
        // morphs/blendshapes (e.g. a facial-expression avatar). -xlinka
        Frames = new PhosBlendShapeFrame[frameCount];
        for (int i = 0; i < frameCount; i++)
            Frames[i] = new PhosBlendShapeFrame();
    }
}

// One frame of a blend shape. Deltas are added to the base vertex data.
public class PhosBlendShapeFrame
{
    public float3[] positions = Array.Empty<float3>();

    public float3[] normals = Array.Empty<float3>();

    public float3[] tangents = Array.Empty<float3>();

    private bool _hasNormals;
    private bool _hasTangents;

    // Storage stays dense: the renderer uploads whole delta arrays and the mesh writer walks them
    // straight through, so neither can take a sparse form. The flags only decide whether an array is
    // allocated at all, which is what a source with position-only morphs needs: one avatar carrying
    // 756 shapes pays VertexCount * 12 bytes per shape for every channel it declares. Every writer in
    // the codebase assigns these arrays directly onto the fields, so a populated array is itself proof
    // the channel is present and the flag only has to carry a declaration made before (or instead of)
    // filling it. -xlinka
    public bool HasNormals
    {
        get => _hasNormals || normals.Length > 0;
        set
        {
            _hasNormals = value;
            if (!value)
                normals = Array.Empty<float3>();
        }
    }

    public bool HasTangents
    {
        get => _hasTangents || tangents.Length > 0;
        set
        {
            _hasTangents = value;
            if (!value)
                tangents = Array.Empty<float3>();
        }
    }

    // Size every channel this frame actually carries in one call, so a caller that knows the source
    // has no normal or tangent deltas never allocates them.
    public void Allocate(int vertexCount, bool withNormals, bool withTangents)
    {
        if (vertexCount < 0)
            vertexCount = 0;

        positions = vertexCount > 0 ? new float3[vertexCount] : Array.Empty<float3>();
        _hasNormals = withNormals;
        _hasTangents = withTangents;
        normals = withNormals && vertexCount > 0 ? new float3[vertexCount] : Array.Empty<float3>();
        tangents = withTangents && vertexCount > 0 ? new float3[vertexCount] : Array.Empty<float3>();
    }

    public void EnsurePositions(int vertexCount)
    {
        if (positions.Length < vertexCount)
            Array.Resize(ref positions, vertexCount);
    }

    public void EnsureNormals(int vertexCount)
    {
        _hasNormals = true;
        if (normals.Length < vertexCount)
            Array.Resize(ref normals, vertexCount);
    }

    public void EnsureTangents(int vertexCount)
    {
        _hasTangents = true;
        if (tangents.Length < vertexCount)
            Array.Resize(ref tangents, vertexCount);
    }

    public void SetNormalDelta(int index, float3 delta)
    {
        // A frame reached through the mesh may never have been sized, since lookup no longer
        // pre-allocates. Grow to fit instead of throwing.
        if (normals.Length <= index)
            EnsureNormals(System.Math.Max(positions.Length, index + 1));
        normals[index] = delta;
    }

    public void SetTangentDelta(int index, float3 delta)
    {
        if (tangents.Length <= index)
            EnsureTangents(System.Math.Max(positions.Length, index + 1));
        tangents[index] = delta;
    }
}
