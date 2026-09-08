// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// Copies member values from a package's saved component onto ours, by name.
//
// Worth doing generically rather than hand-coding each component: the member names line up almost
// everywhere that matters - AlbedoColor, AlbedoTexture, NormalMap, TextureScale, Metallic, Smoothness -
// so one mapper configures every material, collider and renderer at once. A hand-written table would
// cover a handful of types and silently leave the rest at defaults, which is how an import ends up
// technically successful and uniformly grey.
//
// Anything that does not line up is skipped and counted. Nothing is guessed at: a member is written
// only when the name matches AND the stored shape converts to our type. -xlinka
public static class PackageMemberMapper
{
    // Where the same data lives under a different member name on our side. Keyed by OUR component type
    // name, then by THEIR member name.
    //
    // SkinnedMeshRenderer is the one that matters: theirs derives from their MeshRenderer and so calls
    // the geometry "Mesh", while ours is its own component and calls it "MeshAsset". Nothing matched,
    // the mesh was never wired, and every skinned mesh - which on an avatar means the body, the head,
    // the arms, essentially all of it - rendered nothing at all while the rigid props came through
    // fine. Ours also takes a single Material where theirs holds a Materials list. -xlinka
    private static readonly Dictionary<string, Dictionary<string, string>> MemberAliases =
        new(StringComparer.Ordinal)
        {
            ["SkinnedMeshRenderer"] = new(StringComparer.Ordinal)
            {
                ["Mesh"] = "MeshAsset",
            },

            // PARTICLE INITIALIZERS. Theirs name every parameter the same way whatever it holds -
            // Value, or MinValue and MaxValue - while ours name it after the quantity: MinColor,
            // MinSpeed, MinLifetime, MinSize. Not one member lined up, so not one initializer parameter
            // ever imported: every system inherited our defaults, which is white particles at whatever
            // size the component happens to start at. It reads as "the conversion does not do colour",
            // and it was really a table of names nobody had written down. -xlinka
            ["ParticleColorInitializer"] = Range("Color"),
            ["ParticleColorRangeInitializer"] = Range("MinColor", "MaxColor"),
            ["ParticleLifetimeInitializer"] = Range("Lifetime"),
            ["ParticleLifetimeRangeInitializer"] = Range("MinLifetime", "MaxLifetime"),
            ["ParticleSpeedInitializer"] = Range("Speed"),
            ["ParticleSpeedRangeInitializer"] = Range("MinSpeed", "MaxSpeed"),
            ["ParticleVelocityInitializer"] = Range("Velocity"),
            ["ParticleVelocityRangeInitializer"] = Range("MinVelocity", "MaxVelocity"),
            ["ParticleSizeInitializer"] = Range("Size"),
            ["ParticleSizeRangeInitializer"] = Range("MinSize", "MaxSize"),
            ["ParticleUniformSizeInitializer"] = Range("Size"),
            ["ParticleUniformSizeRangeInitializer"] = Range("MinSize", "MaxSize"),
            ["ParticleRotationInitializer"] = Range("Roll"),
            ["ParticleRotationRangeInitializer"] = Range("MinRoll", "MaxRoll"),
            ["ParticleRotation3DInitializer"] = Range("EulerAngles"),
            ["ParticleRotation3DRangeInitializer"] = Range("MinEulerAngles", "MaxEulerAngles"),
            ["ParticleAngularVelocityInitializer"] = Range("AngularVelocity"),
            ["ParticleAngularVelocityRangeInitializer"] = Range("MinAngularVelocity", "MaxAngularVelocity"),
            ["ParticleAngularVelocity3DInitializer"] = Range("AngularVelocity"),
            ["ParticleAngularVelocity3DRangeInitializer"] = Range("MinAngularVelocity", "MaxAngularVelocity"),
            ["ParticleTrailColorInitializer"] = Range("Color"),
            ["ParticleTrailColorRangeInitializer"] = Range("MinColor", "MaxColor"),
            ["ParticleTrailWidthInitializer"] = Range("Width"),
            ["ParticleTrailWidthRangeInitializer"] = Range("MinWidth", "MaxWidth"),
            ["ParticleTrailLifetimeInitializer"] = Range("Lifetime"),
            ["ParticleTrailLifetimeRangeInitializer"] = Range("MinLifetime", "MaxLifetime"),

            // Their Color/MainTexture would collide with the ICommonMaterial properties of the same
            // names, so the fields carry our usual Albedo* naming and bind through here.
            ["XiexeToonMaterial"] = new(StringComparer.Ordinal)
            {
                ["Color"] = "AlbedoColor",
                ["MainTexture"] = "AlbedoTexture",
                ["BlendMode"] = "AlphaMode",
            },

            // Every other material in the engine calls this member BlendMode and spells the same six
            // values the source does, so it maps with no help. The two toon materials are the odd ones
            // out: they call it AlphaMode over a three-value enum. Nothing matched, so a fur or hair
            // shell authored as Cutout landed on our Opaque default and every alpha card rendered as a
            // solid quad - dark blotches over the body and a hatched block where the tail should be.
            // The fix belongs here rather than in a rename: AlphaMode is a live sync member and saved
            // content is keyed by member name. -xlinka
            ["ToonMaterial"] = new(StringComparer.Ordinal)
            {
                ["BlendMode"] = "AlphaMode",
            },
        };

    // Renames that hold whatever component they appear on.
    private static readonly Dictionary<string, string> GlobalMemberAliases = new(StringComparer.Ordinal)
    {
        // Theirs names the side to DRAW; ours names the side to CULL. See CullingValues - the values
        // invert, so this rename alone is not enough.
        ["Sidedness"] = "Culling",
        ["UseVertexColors"] = "UseVertexColor",

        // The particle family, outside the initializers. Both names are unambiguous - nothing else in
        // either engine carries them - so they sit here rather than in a per-type table.
        //
        // MaxParticleCount is the whole budget: unmapped, every imported system silently kept OUR default
        // instead of the author's, and EmitFromShell is the difference between emitting from a volume and
        // emitting from its surface, which is a completely different effect. -xlinka
        ["MaxParticleCount"] = "MaxParticles",
        ["EmitFromShell"] = "FromShell",
    };

    // Enum values that mean something different from what they spell, keyed by THEIR member name.
    //
    // Keying by OUR name is wrong and was a real bug: the source has TWO different face-culling members
    // with OPPOSITE conventions, and both land on our single `Culling`.
    //
    //   Sidedness { Auto, Front, Back, Double }  - names the face to DRAW  -> inverts
    //   Culling   { Off, Front, Back }           - names the face to CULL  -> matches ours
    //
    // Inverting both (which keying by our name does) fixes the Sidedness materials and breaks every
    // Culling one - and the two are split across different material types, so half the avatar comes out
    // inside-out either way. Which member the value CAME FROM is the only thing that disambiguates. -xlinka
    private static readonly Dictionary<string, Dictionary<string, string>> EnumValueAliases =
        new(StringComparer.Ordinal)
        {
            // Names the face to DRAW: invert into our cull-this-face convention.
            ["Sidedness"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Auto"] = "Back",      // their default: draw front, so cull back
                ["Front"] = "Back",     // draw front == cull back
                ["Back"] = "Front",     // draw back  == cull front
                ["Double"] = "None",    // draw both  == cull nothing
                ["DualSided"] = "None",
                ["Both"] = "None",
            },
            // Already names the face to CULL: Front and Back carry straight across, only "Off" differs.
            ["Culling"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Off"] = "None",
            },

            // Their BlendMode has six values; our AlphaMode has three. Only the two toon materials
            // narrow to it, and for everything else BlendMode maps onto our identical six-value
            // BlendMode and needs none of this - which is exactly why the translation below is tried
            // and not forced. See the parse-or-fall-back in TryConvert. -xlinka
            ["BlendMode"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Alpha"] = "Blend",
                ["Transparent"] = "Blend",
                ["Additive"] = "Blend",
                ["Multiply"] = "Blend",
            },
        };

    // Their Value / MinValue / MaxValue onto whatever this initializer calls the same thing.
    private static Dictionary<string, string> Range(string value) =>
        new(StringComparer.Ordinal) { ["Value"] = value };

    private static Dictionary<string, string> Range(string min, string max) =>
        new(StringComparer.Ordinal) { ["MinValue"] = min, ["MaxValue"] = max };

    private static string ResolveMemberName(Component component, string theirName)
    {
        var type = component.GetType();
        while (type != null)
        {
            if (MemberAliases.TryGetValue(type.Name, out var table)
                && table.TryGetValue(theirName, out var ours))
                return ours;
            type = type.BaseType;
        }
        return GlobalMemberAliases.TryGetValue(theirName, out var global) ? global : theirName;
    }

    // What a value looks like in the stored graph, per type:
    //   numbers  a double / int
    //   bool     a bool
    //   string   a string, also used for enum names AND for references (the target's id)
    //   vectors  an ARRAY of numbers - NOT a string. Positions, rotations, scales and colours all
    //            arrive this way, and reading them as text is why a first pass applied no transforms
    //            at all and stacked the whole object at the origin.
    //   colours  an array of numbers with a trailing colour-profile string, which is ignored here.
    public static void Apply(
        Component component,
        DataTreeDictionary data,
        Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList,
        Func<Uri, Uri?> remapUrl)
    {
        // Index our members by name once, then walk THEIR stored members - that way an alias can point
        // a differently-named member at the right one, which a walk over our members alone cannot do.
        var ours = new Dictionary<string, ISyncMember>(StringComparer.Ordinal);
        for (int i = 0; i < component.SyncMemberCount; i++)
        {
            try
            {
                var memberName = component.GetSyncMemberName(i);
                var syncMember = component.GetSyncMember(i);
                if (!string.IsNullOrEmpty(memberName) && syncMember != null)
                    ours[memberName] = syncMember;
            }
            catch
            {
                // A member that will not introspect is simply not available to map onto.
            }
        }

        foreach (var pair in data.Children)
        {
            string name = pair.Key;
            if (pair.Value is not DataTreeDictionary holder)
                continue;
            var node = holder.TryGetNode("Data");
            if (node == null)
                continue;

            string ourName = ResolveMemberName(component, name);
            if (!ours.TryGetValue(ourName, out var member))
                continue;

            try
            {
                Assign(member, node, ourName, name, queueReference, queueReferenceList, remapUrl);
            }
            catch
            {
                // A member that will not take the stored value is left at its default. Never fatal:
                // one odd field must not cost the whole component.
            }
        }
    }

    private static void Assign(
        ISyncMember member,
        DataTreeNode node,
        string name,
        string theirName,
        Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList,
        Func<Uri, Uri?> remapUrl)
    {
        // References carry the target's id and can only be joined once every component exists.
        if (member is ISyncRef reference)
        {
            if (node is DataTreeValue { Value: string id } && id.Length > 0 && !IsNullId(id))
            {
                queueReference(reference, id);
                return;
            }

            // A LIST arriving at a single reference is the Materials -> Material case: theirs holds a
            // list where ours holds one. Take the first entry rather than dropping the material and
            // rendering the mesh untextured.
            if (node is DataTreeList single)
            {
                foreach (var first in single.Children)
                {
                    string? firstId = first switch
                    {
                        DataTreeDictionary d => (d.TryGetNode("Data") as DataTreeValue)?.Value?.ToString(),
                        DataTreeValue v => v.Value?.ToString(),
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(firstId) && !IsNullId(firstId))
                        queueReference(reference, firstId);
                    break;
                }
            }
            return;
        }

        // A KEYED bag of members, written as a dictionary of name -> member-shaped holder rather than a
        // list. The one that matters is the humanoid bone map: their rig stores it as bone name -> slot
        // id, ours is a SyncObjectDictionary<BodyNode, SyncRef<Slot>>, and every key they write is a
        // BodyNode we already have under the same spelling, eyes included. Without this the rig arrives
        // with an empty map, which is the difference between an avatar you equip and one you re-pick 47
        // bones for by hand. -xlinka
        if (node is DataTreeDictionary entries && ObjectDictionary(member.GetType()) is { } bag)
        {
            foreach (var entry in entries.Children)
            {
                if (!Enum.TryParse(bag.KeyType, entry.Key, ignoreCase: true, out var key) || key == null)
                    continue;

                var data = entry.Value is DataTreeDictionary holder ? holder.TryGetNode("Data") : entry.Value;
                if (data == null)
                    continue;

                object? slotForKey;
                try
                {
                    slotForKey = bag.GetOrAdd.Invoke(member, new[] { key });
                }
                catch
                {
                    continue;
                }

                if (slotForKey is ISyncRef keyed)
                {
                    if (data is DataTreeValue { Value: string id } && id.Length > 0 && !IsNullId(id))
                        queueReference(keyed, id);
                    continue;
                }

                // Not a reference bag - a keyed VALUE then, so it goes through the same conversion as
                // any other member.
                var property = slotForKey == null ? null : ValueProperty(slotForKey.GetType());
                if (property != null && TryConvert(data, property.PropertyType, out var keyedValue, remapUrl, theirName))
                    property.SetValue(slotForKey, keyedValue);
            }
            return;
        }

        // A list of references (materials on a renderer, for instance) is a list of member-shaped
        // holders, each carrying one id.
        if (node is DataTreeList list && LooksLikeReferenceList(list))
        {
            var ids = new List<string>(list.Count);
            foreach (var entry in list.Children)
            {
                string? id = entry switch
                {
                    DataTreeDictionary d => (d.TryGetNode("Data") as DataTreeValue)?.Value?.ToString(),
                    DataTreeValue v => v.Value?.ToString(),
                    _ => null,
                };
                if (!string.IsNullOrEmpty(id) && !IsNullId(id))
                    ids.Add(id);
            }
            if (ids.Count > 0)
                queueReferenceList(member, name, ids);
            return;
        }

        var valueProperty = ValueProperty(member.GetType());
        if (valueProperty == null)
            return;

        if (TryConvert(node, valueProperty.PropertyType, out var value, remapUrl, theirName))
            valueProperty.SetValue(member, value);
    }

    // One reflection lookup per member TYPE, not per member per component. An import touches thousands
    // of members and GetProperty is not cheap; uncached it was a measurable part of why importing an
    // avatar stalled the frame. -xlinka
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> _valueProperties = new();

    private static System.Reflection.PropertyInfo? ValueProperty(Type memberType) =>
        _valueProperties.GetOrAdd(memberType, static t =>
        {
            if (!t.IsGenericType)
                return null;
            var property = t.GetProperty("Value");
            return property != null && property.CanWrite ? property : null;
        });

    private readonly struct DictionaryShape
    {
        public readonly Type KeyType;
        public readonly System.Reflection.MethodInfo GetOrAdd;

        public DictionaryShape(Type keyType, System.Reflection.MethodInfo getOrAdd)
        {
            KeyType = keyType;
            GetOrAdd = getOrAdd;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, DictionaryShape?> _objectDictionaries = new();

    // Is this member a SyncObjectDictionary with an ENUM key? Anything else is left to the paths below:
    // a string-keyed bag has no fixed vocabulary to translate against, and guessing one is how you get
    // silently mis-assigned members.
    private static DictionaryShape? ObjectDictionary(Type memberType) =>
        _objectDictionaries.GetOrAdd(memberType, static t =>
        {
            for (var walk = t; walk != null; walk = walk.BaseType)
            {
                if (!walk.IsGenericType || walk.GetGenericTypeDefinition() != typeof(SyncObjectDictionary<,>))
                    continue;

                var keyType = walk.GetGenericArguments()[0];
                if (!keyType.IsEnum)
                    return null;

                var getOrAdd = t.GetMethod("GetOrAdd", new[] { keyType });
                return getOrAdd == null ? null : new DictionaryShape(keyType, getOrAdd);
            }
            return null;
        });

    private static bool LooksLikeReferenceList(DataTreeList list)
    {
        foreach (var entry in list.Children)
        {
            if (entry is DataTreeDictionary d && d.TryGetNode("Data") is DataTreeValue { Value: string })
                return true;
            return false;
        }
        return false;
    }

    // An all-zero id is how a cleared reference is written.
    private static bool IsNullId(string id)
    {
        foreach (char c in id)
        {
            if (c != '0' && c != '-')
                return false;
        }
        return true;
    }

    private static bool TryConvert(DataTreeNode node, Type target, out object? value, Func<Uri, Uri?> remapUrl,
        string theirMemberName)
    {
        value = null;

        // A nullable member (the rig's ForwardAxis is a float3?) stores the same shape as its plain
        // counterpart; unwrap so the conversions below see the type they know. Boxing back into the
        // nullable property is free.
        target = Nullable.GetUnderlyingType(target) ?? target;

        if (target.IsEnum)
        {
            if (node is not DataTreeValue { Value: string enumName } || enumName.Length == 0)
                return false;

            // Translate where the two enums disagree about meaning, not just spelling - but only if the
            // translation actually exists on the target. One of their member names can land on two
            // different enums of ours (BlendMode narrows to AlphaMode on the toon materials and maps
            // one-to-one everywhere else), and forcing the translation would drop the value on whichever
            // of the two did not need it. Try the translated name, keep the original as the fallback.
            // -xlinka
            if (EnumValueAliases.TryGetValue(theirMemberName, out var valueMap)
                && valueMap.TryGetValue(enumName, out var translated)
                && Enum.TryParse(target, translated, ignoreCase: true, out var aliased))
            {
                value = aliased;
                return true;
            }

            if (Enum.TryParse(target, enumName, ignoreCase: true, out var parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        }

        if (node is DataTreeValue scalar)
        {
            if (scalar.Value == null)
                return false;

            try
            {
                if (target == typeof(string)) { value = scalar.Value.ToString(); return true; }
                if (target == typeof(bool)) { value = Convert.ToBoolean(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(float)) { value = Convert.ToSingle(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(double)) { value = Convert.ToDouble(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(int)) { value = Convert.ToInt32(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(long)) { value = Convert.ToInt64(scalar.Value, CultureInfo.InvariantCulture); return true; }
                if (target == typeof(Uri) && scalar.Value is string)
                {
                    // A URL inside the package points at package storage, which means nothing once the
                    // file is closed. Swap it for the local asset we wrote during conversion; if there
                    // is no local copy, leave the member alone rather than storing a dead address.
                    var original = scalar.ExtractUrl();
                    value = original == null ? null : remapUrl(original);
                    return value != null;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        // Vectors and colours: an array of numbers, with any trailing non-numeric entry ignored.
        if (node is DataTreeList numbers)
        {
            var parts = ReadNumbers(numbers);
            return TryBuildVector(parts, target, out value);
        }

        return false;
    }

    private static float[] ReadNumbers(DataTreeList list)
    {
        var values = new List<float>(list.Count);
        foreach (var entry in list.Children)
        {
            if (entry is not DataTreeValue { Value: not null } v)
                continue;
            try
            {
                // A colour carries its profile name as a trailing string; stop rather than fail.
                if (v.Value is string)
                    break;
                values.Add(Convert.ToSingle(v.Value, CultureInfo.InvariantCulture));
            }
            catch
            {
                break;
            }
        }
        return values.ToArray();
    }

    private static bool TryBuildVector(float[] p, Type target, out object? value)
    {
        value = null;
        if (p.Length == 0)
            return false;

        if (target == typeof(float2) && p.Length >= 2) { value = new float2(p[0], p[1]); return true; }
        if (target == typeof(float3) && p.Length >= 3) { value = new float3(p[0], p[1], p[2]); return true; }
        if (target == typeof(float4) && p.Length >= 4) { value = new float4(p[0], p[1], p[2], p[3]); return true; }
        if (target == typeof(floatQ) && p.Length >= 4) { value = new floatQ(p[0], p[1], p[2], p[3]); return true; }
        if (target == typeof(color) && p.Length >= 3) { value = new color(p[0], p[1], p[2], p.Length > 3 ? p[3] : 1f); return true; }
        if (target == typeof(colorHDR) && p.Length >= 3) { value = new colorHDR(p[0], p[1], p[2], p.Length > 3 ? p[3] : 1f); return true; }

        return false;
    }

    // Slot transforms live on the slot itself rather than on a component, so they get their own reader
    // that understands the same array shape.
    public static float[]? ReadNumberArray(DataTreeDictionary node, string member)
    {
        if (node.TryGetNode(member) is not DataTreeDictionary holder)
            return null;
        if (holder.TryGetNode("Data") is not DataTreeList list)
            return null;
        var parts = ReadNumbers(list);
        return parts.Length > 0 ? parts : null;
    }
}
