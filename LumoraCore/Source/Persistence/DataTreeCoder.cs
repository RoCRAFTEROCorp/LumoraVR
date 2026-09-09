// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.Persistence;

/// <summary>
/// Converts individual values to and from <see cref="DataTreeNode"/>s. Primitives become a single
/// <see cref="DataTreeValue"/>; math types decompose into a <see cref="DataTreeList"/> of components;
/// enums save as their name. This is the per-value layer that member serialization builds on.
/// <see cref="RefID"/>s code as their raw value here - remapping is the save/load control's job.
/// </summary>
public static class DataTreeCoder
{
    private static readonly Dictionary<Type, Func<object, DataTreeNode>> Encoders = new();
    private static readonly Dictionary<Type, Func<DataTreeNode, object>> Decoders = new();

    static DataTreeCoder()
    {
        RegConvertible<bool>();
        RegConvertible<byte>();
        RegConvertible<sbyte>();
        RegConvertible<short>();
        RegConvertible<ushort>();
        RegConvertible<int>();
        RegConvertible<uint>();
        RegConvertible<long>();
        RegConvertible<ulong>();
        RegConvertible<float>();
        RegConvertible<double>();
        RegConvertible<decimal>();
        RegConvertible<char>();
        RegConvertible<string>();
        RegConvertible<DateTime>();

        Reg(v => new DataTreeValue((Uri?)v), n => ((DataTreeValue)n).ExtractUrl()!);
        Reg(v => new DataTreeValue(((RefID)v).RawValue), n => new RefID(((DataTreeValue)n).Extract<ulong>()));

        Reg(v => Components(((float2)v).x, ((float2)v).y),
            n => { var l = List(n); return new float2(F(l, 0), F(l, 1)); });
        Reg(v => Components(((float3)v).x, ((float3)v).y, ((float3)v).z),
            n => { var l = List(n); return new float3(F(l, 0), F(l, 1), F(l, 2)); });
        Reg(v => Components(((float4)v).x, ((float4)v).y, ((float4)v).z, ((float4)v).w),
            n => { var l = List(n); return new float4(F(l, 0), F(l, 1), F(l, 2), F(l, 3)); });
        Reg(v => Components(((floatQ)v).x, ((floatQ)v).y, ((floatQ)v).z, ((floatQ)v).w),
            n => { var l = List(n); return new floatQ(F(l, 0), F(l, 1), F(l, 2), F(l, 3)); });
        Reg(v => Components(((color)v).r, ((color)v).g, ((color)v).b, ((color)v).a),
            n => { var l = List(n); return new color(F(l, 0), F(l, 1), F(l, 2), F(l, 3)); });
        Reg(v => Components(((colorHDR)v).r, ((colorHDR)v).g, ((colorHDR)v).b, ((colorHDR)v).a),
            n => { var l = List(n); return new colorHDR(F(l, 0), F(l, 1), F(l, 2), F(l, 3)); });

        // BoundingBox and int4 both replicate perfectly and had NO persistence coder, and
        // SyncField<T>.Save THROWS on a type it cannot encode rather than skipping it. ProceduralMesh
        // carries a Sync<BoundingBox> and every quad, sphere and box in the engine derives from it, so
        // saving a world with any procedural mesh in it threw. Wire-encodable and save-encodable have to
        // be the same set or a field works all session and dies at the one moment that matters. -xlinka
        Reg(v => Components(((BoundingBox)v).Min.x, ((BoundingBox)v).Min.y, ((BoundingBox)v).Min.z,
                            ((BoundingBox)v).Max.x, ((BoundingBox)v).Max.y, ((BoundingBox)v).Max.z),
            n =>
            {
                var l = List(n);
                return new BoundingBox(
                    new float3(F(l, 0), F(l, 1), F(l, 2)),
                    new float3(F(l, 3), F(l, 4), F(l, 5)));
            });

        Reg(v => Components(((int4)v).x, ((int4)v).y, ((int4)v).z, ((int4)v).w),
            n => { var l = List(n); return new int4(I(l, 0), I(l, 1), I(l, 2), I(l, 3)); });
    }

    private static int I(DataTreeList list, int index)
        => index < list.Count ? ((DataTreeValue)list[index]).Extract<int>() : 0;

    public static bool IsSupported(Type type)
        => type.IsEnum || Encoders.ContainsKey(type);

    public static DataTreeNode Encode<T>(T value) => Encode(typeof(T), value);

    public static DataTreeNode Encode(Type type, object? value)
    {
        if (value == null)
            return new DataTreeValue((IConvertible?)null);
        if (type.IsEnum)
            return new DataTreeValue(value.ToString());
        if (Encoders.TryGetValue(type, out var encode))
            return encode(value);
        throw new NotSupportedException($"DataTreeCoder: no encoder for '{type}'");
    }

    public static T Decode<T>(DataTreeNode node) => (T)Decode(typeof(T), node)!;

    public static object? Decode(Type type, DataTreeNode node)
    {
        if (type.IsEnum)
            return Enum.Parse(type, ((DataTreeValue)node).Extract<string>());
        if (Decoders.TryGetValue(type, out var decode))
            return decode(node);
        throw new NotSupportedException($"DataTreeCoder: no decoder for '{type}'");
    }

    private static void RegConvertible<T>() where T : IConvertible
    {
        Encoders[typeof(T)] = o => new DataTreeValue((IConvertible)o);
        Decoders[typeof(T)] = n => ((DataTreeValue)n).Extract<T>()!;
    }

    private static void Reg<T>(Func<object, DataTreeNode> encode, Func<DataTreeNode, T> decode)
    {
        Encoders[typeof(T)] = encode;
        Decoders[typeof(T)] = n => decode(n)!;
    }

    private static DataTreeList Components(params float[] components)
    {
        var list = new DataTreeList();
        foreach (var c in components)
            list.Add(new DataTreeValue(c));
        return list;
    }

    // Integers get their own overload rather than riding the float one: a float carries 24 bits of
    // mantissa, so anything past about 16.7 million would come back off by one having gone out and in
    // through a float. -xlinka
    private static DataTreeList Components(params int[] components)
    {
        var list = new DataTreeList();
        foreach (var c in components)
            list.Add(new DataTreeValue(c));
        return list;
    }

    private static DataTreeList List(DataTreeNode node) => (DataTreeList)node;
    private static float F(DataTreeList list, int index) => ((DataTreeValue)list[index]).Extract<float>();
}
