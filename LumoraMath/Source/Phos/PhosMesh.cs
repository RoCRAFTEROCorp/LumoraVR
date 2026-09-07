// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Math;

namespace Lumora.Core.Phos;

/// <summary>
/// Platform-agnostic procedural mesh container.
/// Stores vertex data (positions, normals, tangents, colors, UVs, bones, blend shapes).
/// Core mesh system architecture (PhosMeshSys).
/// </summary>
public class PhosMesh
{
    // Vertex Data Arrays
    internal float3[] positions = Array.Empty<float3>();
    internal float3[] normals = Array.Empty<float3>();
    internal float4[] tangents = Array.Empty<float4>();
    internal color[] colors = Array.Empty<color>();
    internal PhosUVArray[] uvChannels = new PhosUVArray[4];  // Support up to 4 UV channels
    internal PhosBoneBinding[] boneBindings = Array.Empty<PhosBoneBinding>();
    internal List<PhosBone> bones = new List<PhosBone>();
    internal BitArray flags = new BitArray(0);

    // Vertex Tracking
    internal int[] vertexIDs = Array.Empty<int>();  // For version tracking
    private int _currentVertexID = 0;

    /// <summary>Number of vertices in this mesh</summary>
    public int VertexCount { get; private set; }

    /// <summary>Version number - increments when vertices are removed</summary>
    internal int VerticesVersion { get; private set; } = -1;

    /// <summary>Enable removal tracking (required for vertex/triangle removal)</summary>
    public bool TrackRemovals { get; set; }

    // Metadata Flags
    /// <summary>Whether this mesh has normal data</summary>
    public bool HasNormals { get; set; }

    /// <summary>Whether this mesh has tangent data</summary>
    public bool HasTangents { get; set; }

    /// <summary>Whether this mesh has vertex color data</summary>
    public bool HasColors { get; set; }

    /// <summary>Whether this mesh has bone binding data (for skinning)</summary>
    public bool HasBoneBindings { get; set; }

    /// <summary>Whether this mesh has per-vertex flags</summary>
    public bool HasFlags { get; set; }

    /// <summary>Number of UV channels (0-4)</summary>
    public int UVChannelCount { get; private set; }

    /// <summary>Whether mesh has UV channel 0</summary>
    public bool HasUV0s
    {
        get => uvChannels[0].HasData;
        set => SetHasUV(0, value);
    }

    public bool HasUV1s
    {
        get => uvChannels[1].HasData;
        set => SetHasUV(1, value);
    }

    /// <summary>Raw array of positions - direct access</summary>
    public float3[] RawPositions => positions;

    /// <summary>Raw array of normals - direct access</summary>
    public float3[] RawNormals => normals;

    /// <summary>Raw array of tangents - direct access</summary>
    public float4[] RawTangents => tangents;

    /// <summary>Raw array of colors - direct access</summary>
    public color[] RawColors => colors;

    /// <summary>Raw array of UV channel 0 - direct access</summary>
    public float2[] RawUV0s => uvChannels[0].uv2D ?? Array.Empty<float2>();

    /// <summary>Raw array of UV channel 1 - direct access</summary>
    public float2[] RawUV1s => uvChannels[1].uv2D ?? Array.Empty<float2>();

    /// <summary>Raw array of UV channel 2 - direct access</summary>
    public float2[] RawUV2s => uvChannels[2].uv2D ?? Array.Empty<float2>();

    /// <summary>Raw array of UV channel 3 - direct access</summary>
    public float2[] RawUV3s => uvChannels[3].uv2D ?? Array.Empty<float2>();

    /// <summary>Raw array of per-vertex bone bindings - direct access</summary>
    public PhosBoneBinding[] RawBoneBindings => boneBindings;

    // Submeshes
    /// <summary>List of submeshes (different topology groups)</summary>
    public List<PhosSubmesh> Submeshes { get; } = new List<PhosSubmesh>();

    // Blend Shapes
    /// <summary>List of blend shapes (morph targets)</summary>
    public List<PhosBlendShape> BlendShapes { get; } = new List<PhosBlendShape>();

    // Constructor
    public PhosMesh()
    {
    }

    // Vertex Management

    /// <summary>
    /// Increase vertex count by specified amount.
    /// Automatically resizes internal arrays.
    /// </summary>
    public void IncreaseVertexCount(int count)
    {
        int oldCount = VertexCount;
        VertexCount += count;

        // Grow backing arrays geometrically. Emitters (UI glyphs/quads) call this once per quad,
        // so resizing to the exact VertexCount every time made buffer growth O(n^2): each quad
        // reallocated and copied the whole buffer. Over-allocate to amortize to O(n). VertexCount
        // stays exact and every consumer reads up to VertexCount, never array.Length. - xlinka
        int capacity = positions.Length;
        if (capacity < VertexCount)
        {
            capacity = System.Math.Max(VertexCount, System.Math.Max(capacity * 2, 64));
        }

        EnsureArray(true, ref positions, capacity, float3.Zero);
        EnsureArray(HasNormals, ref normals, capacity, float3.Zero);
        EnsureArray(HasTangents, ref tangents, capacity, float4.Zero);
        EnsureArray(HasColors, ref colors, capacity, color.White);
        EnsureArray(HasBoneBindings, ref boneBindings, capacity, new PhosBoneBinding());

        // Resize UV channels
        for (int i = 0; i < UVChannelCount; i++)
        {
            if (uvChannels[i].uv2D != null)
                EnsureArray(true, ref uvChannels[i].uv2D!, capacity, float2.Zero);
        }

        // Resize flags
        if (HasFlags)
        {
            var newFlags = new BitArray(VertexCount);
            for (int i = 0; i < oldCount; i++)
                newFlags[i] = flags[i];
            flags = newFlags;
        }

        // Track vertex IDs for removal detection
        if (TrackRemovals)
        {
            EnsureArray(true, ref vertexIDs, VertexCount, 0);
            for (int i = oldCount; i < VertexCount; i++)
                vertexIDs[i] = _currentVertexID++;
        }
    }

    /// <summary>
    /// Get a vertex reference at the specified index.
    /// </summary>
    public PhosVertex GetVertex(int index)
    {
        if (index < 0 || index >= VertexCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return new PhosVertex(index, this);
    }

    /// <summary>
    /// Remove vertices starting at index.
    /// </summary>
    public void RemoveVertices(int index, int count, bool updateSubmeshes = true)
    {
        if (count == 0) return;
        if (index < 0 || index >= VertexCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (index + count > VertexCount)
            throw new ArgumentOutOfRangeException(nameof(count));

        // Update submesh indices
        if (updateSubmeshes)
        {
            foreach (var submesh in Submeshes)
                submesh.VerticesRemoved(index, count);
        }

        // Shift arrays
        RemoveElements(positions, index, count, VertexCount, false, float3.Zero);
        RemoveElements(normals, index, count, VertexCount, false, float3.Zero);
        RemoveElements(tangents, index, count, VertexCount, false, float4.Zero);
        RemoveElements(colors, index, count, VertexCount, false, color.White);
        RemoveElements(boneBindings, index, count, VertexCount, false, new PhosBoneBinding());
        RemoveElements(vertexIDs, index, count, VertexCount, true, int.MaxValue);

        // Update UV channels
        for (int i = 0; i < UVChannelCount; i++)
        {
            if (uvChannels[i].uv2D != null)
                RemoveElements(uvChannels[i].uv2D!, index, count, VertexCount, false, float2.Zero);
        }

        VertexCount -= count;
        VerticesVersion--;
    }

    /// <summary>
    /// Remove vertices in a collection.
    /// </summary>
    public void RemoveVertices(Collections.PhosVertexCollection vertices)
    {
        if (vertices.Count == 0) return;

        // Sort by index descending to remove from back to front
        var sorted = vertices.OrderByDescending(v => v.Index).ToList();

        foreach (var vertex in sorted)
        {
            RemoveVertices(vertex.Index, 1, true);
        }
    }

    /// <summary>
    /// Remove triangles in a collection.
    /// </summary>
    public void RemoveTriangles(Collections.PhosTriangleCollection triangles)
    {
        if (triangles.Count == 0) return;

        // Group by submesh
        var bySubmesh = triangles.GroupBy(t => t.Submesh);

        foreach (var group in bySubmesh)
        {
            group.Key.Remove(new Collections.PhosTriangleCollection(group.ToList()));
        }
    }

    /// <summary>
    /// Remove points in a collection.
    /// </summary>
    public void RemovePoints(Collections.PhosPointCollection points)
    {
        if (points.Count == 0) return;

        var bySubmesh = points.GroupBy(p => p.Submesh);

        foreach (var group in bySubmesh)
        {
            group.Key.Remove(new Collections.PhosPointCollection(group.ToList()));
        }
    }

    /// <summary>
    /// Clear all mesh data.
    /// </summary>
    public void Clear()
    {
        VertexCount = 0;
        positions = Array.Empty<float3>();
        normals = Array.Empty<float3>();
        tangents = Array.Empty<float4>();
        colors = Array.Empty<color>();
        boneBindings = Array.Empty<PhosBoneBinding>();
        flags = new BitArray(0);
        vertexIDs = Array.Empty<int>();

        // Reset metadata flags so arrays get properly recreated
        HasNormals = false;
        HasTangents = false;
        HasColors = false;
        HasBoneBindings = false;
        HasFlags = false;
        UVChannelCount = 0;

        for (int i = 0; i < 4; i++)
            uvChannels[i] = new PhosUVArray();

        Submeshes.Clear();
        BlendShapes.Clear();
        _blendShapeByName.Clear();
        _mappedBlendShapeCount = 0;
        bones.Clear();
    }

    // UV Channel Management

    /// <summary>
    /// Set whether a UV channel is present.
    /// </summary>
    public void SetHasUV(int channel, bool state)
    {
        if (channel < 0 || channel >= 4)
            throw new ArgumentOutOfRangeException(nameof(channel));

        if (state)
        {
            if (uvChannels[channel].uv2D == null)
            {
                uvChannels[channel].uv2D = new float2[VertexCount];
            }
            UVChannelCount = System.Math.Max(UVChannelCount, channel + 1);
        }
    }

    /// <summary>
    /// Check if a UV channel exists, create if needed.
    /// </summary>
    internal void CheckUV(int channel)
    {
        if (channel < 0 || channel >= 4)
            throw new ArgumentOutOfRangeException(nameof(channel));

        if (uvChannels[channel].uv2D == null)
            SetHasUV(channel, true);
    }

    /// <summary>
    /// Get raw UVs for a channel (creates if doesn't exist).
    /// </summary>
    internal float2[] GetRawUVs(int channel)
    {
        CheckUV(channel);
        return uvChannels[channel].uv2D!;
    }

    /// <summary>
    /// Set a UV coordinate at a specific vertex index.
    /// </summary>
    public void SetUV(int channel, int vertexIndex, float2 uv)
    {
        CheckUV(channel);
        if (vertexIndex >= 0 && vertexIndex < VertexCount)
        {
            uvChannels[channel].uv2D![vertexIndex] = uv;
        }
    }

    /// <summary>
    /// Try to get UV array (doesn't create).
    /// </summary>
    internal PhosUVArray TryGetRawUV_Array(int channel)
    {
        if (channel < 0 || channel >= 4)
            return new PhosUVArray();
        return uvChannels[channel];
    }

    // Normals/Tangents/Colors Management

    internal void CheckNormals()
    {
        if (!HasNormals)
        {
            HasNormals = true;
            normals = new float3[VertexCount];
        }
    }

    internal void CheckTangents()
    {
        if (!HasTangents)
        {
            HasTangents = true;
            tangents = new float4[VertexCount];
        }
    }

    internal void CheckColors()
    {
        if (!HasColors)
        {
            HasColors = true;
            colors = new color[VertexCount];
            for (int i = 0; i < VertexCount; i++)
                colors[i] = color.White;
        }
    }

    internal void CheckBoneBindings()
    {
        if (!HasBoneBindings)
        {
            HasBoneBindings = true;
            boneBindings = new PhosBoneBinding[VertexCount];
        }
    }

    internal void CheckFlags()
    {
        if (!HasFlags)
        {
            HasFlags = true;
            flags = new BitArray(VertexCount);
        }
    }

    // Submesh Management

    /// <summary>
    /// Get index of a submesh.
    /// </summary>
    public int IndexOfSubmesh(PhosSubmesh submesh)
    {
        return Submeshes.IndexOf(submesh);
    }

    // Bone Management

    /// <summary>Number of bones in this mesh's skeleton table.</summary>
    public int BoneCount => bones.Count;

    /// <summary>Get a bone by index.</summary>
    public PhosBone GetBone(int index) => bones[index];

    /// <summary>Index of a bone in the table, or -1 if not present.</summary>
    public int IndexOfBone(PhosBone bone) => bones.IndexOf(bone);

    /// <summary>Bone name at the given index.</summary>
    public string GetBoneName(int index) => bones[index].Name;

    /// <summary>Bone bind pose at the given index.</summary>
    public float4x4 GetBoneBindPose(int index) => bones[index].BindPose;

    /// <summary>Append a new bone (BindPose defaults to identity) and return it.</summary>
    public PhosBone AddBone(string name = "Bone")
    {
        var bone = new PhosBone(name);
        bones.Add(bone);
        return bone;
    }

    /// <summary>
    /// Ensure the per-vertex bone-binding array exists, sized to VertexCount. Thin public wrapper over
    /// the internal CheckBoneBindings so the asset path can request bindings without reaching into
    /// internals. - xlinka
    /// </summary>
    public void EnsureBoneBindings() => CheckBoneBindings();

    // Blend Shape Management

    // A delta smaller than this in every component of every channel is one the exporter wrote out for
    // completeness and nothing can ever see. Facial sources ship hundreds of them per mesh.
    public const float BlendShapeDeltaEpsilon = 0.001f;

    // BlendShapes stays a plain List because its ORDER is the public contract (renderers and drivers
    // address shapes by index). The map is a lookup cache over that list, never a second source of
    // truth: name lookup used to be a linear scan and decode does one lookup per shape, so a face mesh
    // with 756 shapes burned a quarter of a million string compares before reading a single delta.
    // Callers still append to the list directly, so a count that disagrees with the map rebuilds it.
    // -xlinka
    private readonly Dictionary<string, PhosBlendShape> _blendShapeByName = new Dictionary<string, PhosBlendShape>(StringComparer.Ordinal);
    private int _mappedBlendShapeCount = -1;

    // Rename or in-place replacement leaves the count unchanged, so it cannot be detected; say so here.
    public void InvalidateBlendShapeMap() => _mappedBlendShapeCount = -1;

    private Dictionary<string, PhosBlendShape> BlendShapeMap()
    {
        if (_mappedBlendShapeCount != BlendShapes.Count)
        {
            _blendShapeByName.Clear();
            for (int i = 0; i < BlendShapes.Count; i++)
            {
                var key = BlendShapes[i].Name ?? string.Empty;
                if (!_blendShapeByName.ContainsKey(key))
                    _blendShapeByName[key] = BlendShapes[i];
            }
            _mappedBlendShapeCount = BlendShapes.Count;
        }
        return _blendShapeByName;
    }

    // Look up without creating, which the create-on-miss GetBlendShape cannot express.
    public bool TryGetBlendShape(string name, out PhosBlendShape shape)
        => BlendShapeMap().TryGetValue(name ?? string.Empty, out shape!);

    // Frames come back unsized on purpose: the producer already holds packed delta arrays and assigns
    // them whole, so allocating VertexCount here would only be overwritten and thrown away. Use
    // PhosBlendShapeFrame.Allocate when the frame really is filled per vertex.
    public PhosBlendShape AddBlendShape(string name, int frameCount = 1)
    {
        var shape = new PhosBlendShape(name, frameCount);
        BlendShapes.Add(shape);

        if (_mappedBlendShapeCount >= 0)
        {
            var key = shape.Name ?? string.Empty;
            if (!_blendShapeByName.ContainsKey(key))
                _blendShapeByName[key] = shape;
            _mappedBlendShapeCount = BlendShapes.Count;
        }
        return shape;
    }

    /// <summary>
    /// Get or create a blend shape by name.
    /// </summary>
    public PhosBlendShape GetBlendShape(string name)
    {
        if (TryGetBlendShape(name, out var existing))
            return existing;
        return AddBlendShape(name, 1);
    }

    // Drop every shape whose deltas are all below the epsilon, returning how many went. Reported
    // indices are the ORIGINAL positions, ascending, because a caller remapping animation tracks needs
    // to know where a shape used to sit, not where it ended up.
    //
    // Half of a real avatar's shapes are dead weight: the incident file carried 756 attachments of
    // which 387 never moved a vertex, and every one of them was costing a dense per-vertex array in
    // memory and a full block on disk. -xlinka
    //
    // epsilon is the caller's, because only the caller knows the model's scale: a delta that is noise on a
    // metre-authored mesh is real motion on the same mesh authored in centimetres. Default keeps the old
    // absolute behaviour for callers that have no bounds to hand.
    public int StripEmptyBlendShapes(List<int>? removedIndices = null, float epsilon = BlendShapeDeltaEpsilon)
    {
        int reportStart = removedIndices?.Count ?? 0;
        int removed = 0;

        // Back to front: each removal then shifts only entries already examined.
        for (int i = BlendShapes.Count - 1; i >= 0; i--)
        {
            if (!IsBlendShapeEmpty(BlendShapes[i], epsilon))
                continue;

            BlendShapes.RemoveAt(i);
            removedIndices?.Add(i);
            removed++;
        }

        if (removed > 0)
        {
            // Duplicate names make per-entry map removal ambiguous, and the map is only a cache.
            InvalidateBlendShapeMap();
            removedIndices?.Reverse(reportStart, removedIndices.Count - reportStart);
        }
        return removed;
    }

    private static bool IsBlendShapeEmpty(PhosBlendShape shape, float epsilon)
    {
        var frames = shape?.Frames;
        if (frames == null)
            return true;

        for (int f = 0; f < frames.Length; f++)
        {
            var frame = frames[f];
            if (frame == null)
                continue;
            // Normals and tangents are unit vectors whatever the model's units are, so a scale-derived
            // epsilon does not apply to them: take whichever threshold is stricter and never let a large
            // model's positional epsilon swallow real shading motion.
            float directional = System.Math.Min(epsilon, BlendShapeDeltaEpsilon);
            if (AnyDeltaAboveEpsilon(frame.positions, epsilon)
                || AnyDeltaAboveEpsilon(frame.normals, directional)
                || AnyDeltaAboveEpsilon(frame.tangents, directional))
                return false;
        }
        return true;
    }

    private static bool AnyDeltaAboveEpsilon(float3[] deltas, float epsilon)
    {
        if (deltas == null)
            return false;

        for (int i = 0; i < deltas.Length; i++)
        {
            var d = deltas[i];
            if (System.Math.Abs(d.x) >= epsilon
                || System.Math.Abs(d.y) >= epsilon
                || System.Math.Abs(d.z) >= epsilon)
                return true;
        }
        return false;
    }

    /// <summary>Number of blend shapes on this mesh.</summary>
    public int BlendShapeCount => BlendShapes.Count;

    /// <summary>Index of the named blend shape, or -1 if absent.</summary>
    public int BlendShapeIndex(string name) => BlendShapes.FindIndex(bs => bs.Name == name);

    /// <summary>Whether a blend shape with this name exists.</summary>
    public bool HasBlendShape(string name) => BlendShapeMap().ContainsKey(name ?? string.Empty);

    // Utility Methods

    /// <summary>
    /// Calculate bounding box of entire mesh.
    /// </summary>
    public BoundingBox CalculateBoundingBox()
    {
        var bounds = new BoundingBox();
        bounds.MakeEmpty();

        for (int i = 0; i < VertexCount; i++)
            bounds.Encapsulate(positions[i]);

        return bounds;
    }

    // Internal Helper Methods

    internal void EnsureArray<T>(bool condition, ref T[] array, int size, T defaultValue)
    {
        if (!condition) return;

        if (array == null || array.Length < size)
        {
            int oldLength = array?.Length ?? 0;
            Array.Resize(ref array, size);

            // Initialize new elements
            for (int i = oldLength; i < size; i++)
                array[i] = defaultValue;
        }
    }

    internal void RemoveElements<T>(T[] array, int index, int count, int totalCount, bool clear, T clearValue)
    {
        if (array == null || array.Length == 0) return;

        // Shift elements down
        if (index + count < totalCount)
        {
            Array.Copy(array, index + count, array, index, totalCount - index - count);
        }

        // Clear removed elements if requested
        if (clear)
        {
            for (int i = totalCount - count; i < totalCount; i++)
                array[i] = clearValue;
        }
    }

    internal void RemoveElements(int[] array, Action<int, int> callback)
    {
        // Used for removing elements with callback
        // Implementation using PhosMeshSys pattern
        var toRemove = new List<(int index, int count)>();

        // Group consecutive indices
        Array.Sort(array);
        int startIndex = array[0];
        int count = 1;

        for (int i = 1; i < array.Length; i++)
        {
            if (array[i] == array[i - 1] + 1)
            {
                count++;
            }
            else
            {
                toRemove.Add((startIndex, count));
                startIndex = array[i];
                count = 1;
            }
        }
        toRemove.Add((startIndex, count));

        // Remove in reverse order
        for (int i = toRemove.Count - 1; i >= 0; i--)
        {
            callback(toRemove[i].index, toRemove[i].count);
        }
    }
}
