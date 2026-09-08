// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text;

namespace Lumora.Core.Assets.Interop;

// What a component type from an imported package becomes on this side.
public enum PackageTypeFate
{
    // We have a component that does the same job; the instance can be rebuilt.
    Mapped,
    // Stripped on the way in, by design. Their visual scripting runtime is a whole language and
    // execution model rather than a set of components, and we do not carry it. These are NOT counted
    // as conversion losses: an object arrives without its scripted behaviour but otherwise intact,
    // and reporting hundreds of node types as "missing" buries the gaps that can actually be acted
    // on. Dropped silently at import. -xlinka
    Stripped,
    // A licensed third-party IK package. We never port that; our own solver stands in at the rig level.
    ForeignIK,
    // Materials and shaders. The parameters survive, the shader program does not.
    Shader,
    // No equivalent here yet.
    Unsupported,
}

// Translates the type names carried in a package's type table into Lumora types.
//
// Names arrive as "[AssemblyName]Namespace.TypeName", with generics written inline and nested types
// appended after a '+'. The mapping is deliberately name-driven rather than a giant hand-maintained
// table: most of what a package contains has an obvious counterpart here under the same name, and the
// alias table below only has to carry the cases where we deliberately diverged. -xlinka
public static class PackageTypeMap
{
    // Where we chose a different name for the same job. Left side is their simple name.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        // Anything whose name already matches resolves by the assembly scan below and needs no entry
        // here; this table is only for the places we deliberately picked a different name.
        // The plain PBS_Metallic / PBS_Specular names match ours exactly and resolve by scan. Only the
        // variants they spell differently need an entry, and each target below is a component that
        // actually exists here - an alias pointing at a name we do not have is worse than no alias,
        // because it turns a component we CAN build into a reported loss.
        ["PBS_DualSidedMetallic"] = "PBS_DualSided",
        ["PBS_TriplanarMetallic"] = "PBS_Triplanar",
        ["PBS_VertexColorSpecular"] = "PBS_VertexColor",
        ["PBS_VertexColorMetallic"] = "PBS_VertexColor",
        ["PBSLerpMetallic"] = "PBS_Metallic",
        ["PBS_RimMetallic"] = "PBS_Metallic",
        // XiexeToonMaterial resolves to our own component of the same name now - it used to fall
        // back to the plain ToonMaterial, which is a different and simpler shading model.
        ["AvatarPoseNode"] = "AvatarPoseDriver",
        // The marker that says "this slot is the avatar". Unmapped, an imported avatar arrives with no
        // root at all: the equip pass finds no body-node coverage, leaves Head empty, and the socket
        // filler drops the default grey sphere head on the user. Ours is AvatarForm, which is the same
        // idea under a different name (Node => BodyNode.Root). -xlinka
        ["AvatarRoot"] = "AvatarForm",
        ["BipedRig"] = "HumanoidRig",
        ["VRIKAvatar"] = "AvatarIK",
        ["VRIK"] = "AvatarIK",

        // Asset providers: theirs name the concrete asset, ours name the provider role.
        ["StaticTexture2D"] = "ImageProvider",
        ["StaticMesh"] = "MeshProvider",
        ["StaticFont"] = "FontProvider",
        ["StaticSprite"] = "SpriteProvider",

        // Variables. We dropped their "Dynamic" prefix and named the container a scope.
        ["DynamicVariableSpace"] = "VariableScope",
        ["DynamicValueVariable"] = "ValueVariable",
        ["DynamicReferenceVariable"] = "ReferenceVariable",
        ["DynamicField"] = "FieldVariable",
        ["DynamicTypeVariable"] = "TypeVariable",
        ["DynamicValueVariableDriver"] = "ValueVariableDriver",
        ["DynamicReferenceVariableDriver"] = "ReferenceVariableDriver",

        // Drivers with the words the other way round.
        ["ValueCopy"] = "CopyValue",
        ["ReferenceCopy"] = "CopyReference",

        // PARTICLES. We have the whole family; ours simply carries a Particle prefix. Every target here
        // was checked to exist before being written down - an alias pointing at a name we do not have
        // turns a component we CAN build into a reported loss, which is worse than no alias at all.
        ["ColorRangeInitializer"] = "ParticleColorRangeInitializer",
        ["LifetimeRangeInitializer"] = "ParticleLifetimeRangeInitializer",
        ["SpeedRangeInitializer"] = "ParticleSpeedRangeInitializer",
        ["RotationRangeInitializer"] = "ParticleRotationRangeInitializer",
        ["Rotation3DEulerRangeInitializer"] = "ParticleRotation3DRangeInitializer",
        ["AngularVelocityRangeInitializer"] = "ParticleAngularVelocityRangeInitializer",
        ["AngularVelocity3DRangeInitializer"] = "ParticleAngularVelocity3DRangeInitializer",
        ["UniformSizeRangeInitializer"] = "ParticleUniformSizeRangeInitializer",

        // Trail initializers keep the same shape, prefixed.
        ["TrailColorRangeInitializer"] = "ParticleTrailColorRangeInitializer",
        ["TrailWidthRangeInitializer"] = "ParticleTrailWidthRangeInitializer",
        ["TrailLifetimeRangeInitializer"] = "ParticleTrailLifetimeRangeInitializer",
        ["TrailLifetimeFromSizeInitializer"] = "ParticleTrailLifetimeFromSizeInitializer",

        // Their "Module" suffix is our bare noun.
        ["ParticleLightsModule"] = "ParticleLights",
        ["ParticleTrailsModule"] = "ParticleTrails",
        ["ParticleRibbonsModule"] = "ParticleRibbons",

        ["OrientByVelocity"] = "ParticleOrientByVelocity",
        ["AlphaOverLifetimeLinearGradient"] = "ParticleAlphaOverLifetime",
        ["ColorOverLifetimeLinearGradient"] = "ParticleColorOverLifetime",

        // Same family: a "constant" initializer is our plain single-value one, as opposed to the range
        // variants above.
        ["ColorConstantInitializer"] = "ParticleColorInitializer",
        ["LifetimeConstantInitializer"] = "ParticleLifetimeInitializer",
        ["Rotation3DConstantInitializer"] = "ParticleRotation3DInitializer",
    };

    // Components that BELONG TO THE PERSON WEARING THE OBJECT, not to the object.
    //
    // An avatar authored elsewhere ships its own hand laser, built out of a real component plus the
    // mesh, material and cursor slots under it. We have that component too, so it imports perfectly -
    // and now the wearer has two lasers fighting over the same hand, one of them owned by an avatar
    // rather than by the user root that is supposed to own it. There is nothing to salvage: the local
    // user already grows its own. The slot carrying one of these is dropped WITH ITS SUBTREE, because
    // the children are that laser's own parts and keeping them leaves the wreckage behind. -xlinka
    private static readonly HashSet<string> UserspaceConflicts = new(StringComparer.Ordinal)
    {
        "InteractionLaser",
    };

    public static bool ConflictsWithUserspace(string simpleName) =>
        simpleName.Length > 0 && UserspaceConflicts.Contains(simpleName);

    // Slots a component BUILT for itself, matched by the literal name that component gave them.
    //
    // Normally matching a slot on its name would be wrong, because a name is whatever the author typed.
    // These are the exception: the owning component creates the slot under itself with a hardcoded name
    // and later finds it again by that same string to destroy it, so the name is the component's own
    // contract rather than a label anyone chose. The component does not come across, which leaves its
    // scaffolding behind as an orphan nothing drives - a bare sphere on the head that follows the wearer
    // around looking like a bug in the avatar. Dropped WITH ITS SUBTREE: the parts underneath belong to
    // the thing being dropped. -xlinka
    private static readonly HashSet<string> GeneratedOrphanSlots = new(StringComparer.Ordinal)
    {
        "Voice Range Visual",
    };

    public static bool IsGeneratedOrphanSlot(string slotName) =>
        slotName.Length > 0 && GeneratedOrphanSlots.Contains(slotName);

    // Prefixes that identify a whole subsystem rather than one component.
    private const string VisualScriptingMarker = "ProtoFlux";
    private const string ForeignIkMarker = "FinalIK";

    public readonly struct Resolution
    {
        public readonly PackageTypeFate Fate;
        public readonly string SimpleName;
        public readonly Type? Target;

        public Resolution(PackageTypeFate fate, string simpleName, Type? target)
        {
            Fate = fate;
            SimpleName = simpleName;
            Target = target;
        }
    }

    public static Resolution Resolve(string encodedName)
    {
        if (string.IsNullOrWhiteSpace(encodedName))
            return new Resolution(PackageTypeFate.Unsupported, string.Empty, null);

        if (encodedName.Contains(VisualScriptingMarker, StringComparison.Ordinal))
            return new Resolution(PackageTypeFate.Stripped, SimpleName(encodedName), null);

        if (encodedName.Contains(ForeignIkMarker, StringComparison.Ordinal))
            return new Resolution(PackageTypeFate.ForeignIK, SimpleName(encodedName), null);

        string simple = SimpleName(encodedName);
        if (simple.Length == 0)
            return new Resolution(PackageTypeFate.Unsupported, simple, null);

        string candidate = Aliases.TryGetValue(simple, out var alias) ? alias : simple;
        var target = FindType(candidate);
        if (target != null)
        {
            // A generic component resolves to its OPEN definition, which cannot be instantiated. Close
            // it with the arguments the package recorded - ValueVariable<bool>, CopyValue<float3> and
            // so on. Left open, every generic component threw "ContainsGenericParameters is true" and
            // was skipped, which on a real avatar is hundreds of drivers and variables. -xlinka
            if (target.ContainsGenericParameters)
            {
                target = Close(target, GenericArguments(encodedName));
                if (target == null)
                    return new Resolution(PackageTypeFate.Unsupported, simple, null);
            }
            return new Resolution(PackageTypeFate.Mapped, simple, target);
        }

        // Materials and shader providers land here when we have no counterpart. Called out separately
        // because the shader program is the one thing a conversion genuinely cannot carry across, and
        // seeing them listed is more useful than burying them among unrelated misses.
        if (LooksLikeShader(simple))
            return new Resolution(PackageTypeFate.Shader, simple, null);

        return new Resolution(PackageTypeFate.Unsupported, simple, null);
    }

    // Their value-type names for the primitives that show up as generic arguments.
    private static readonly Dictionary<string, Type> ValueTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = typeof(bool),
        ["byte"] = typeof(byte),
        ["sbyte"] = typeof(sbyte),
        ["short"] = typeof(short),
        ["ushort"] = typeof(ushort),
        ["int"] = typeof(int),
        ["uint"] = typeof(uint),
        ["long"] = typeof(long),
        ["ulong"] = typeof(ulong),
        ["float"] = typeof(float),
        ["double"] = typeof(double),
        ["string"] = typeof(string),
        ["float2"] = typeof(Math.float2),
        ["float3"] = typeof(Math.float3),
        ["float4"] = typeof(Math.float4),
        ["floatQ"] = typeof(Math.floatQ),
        ["color"] = typeof(Math.color),
        // Their high-dynamic-range colour is our colorHDR.
        ["colorX"] = typeof(Math.colorHDR),
        ["colorHDR"] = typeof(Math.colorHDR),
    };

    private static Type? Close(Type openType, IReadOnlyList<string> arguments)
    {
        var parameters = openType.GetGenericArguments();
        if (arguments.Count != parameters.Length)
            return null;

        var resolved = new Type[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            if (ValueTypes.TryGetValue(arguments[i], out var valueType))
            {
                resolved[i] = valueType;
                continue;
            }
            // A component or asset type as the argument: look it up the same way as any other.
            var asComponent = FindType(arguments[i]);
            if (asComponent == null)
                return null;
            resolved[i] = asComponent;
        }

        try
        {
            var closed = openType.MakeGenericType(resolved);
            // A constraint the arguments do not satisfy throws here rather than at construction.
            return closed.ContainsGenericParameters ? null : closed;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeShader(string simple) =>
        simple.EndsWith("Material", StringComparison.Ordinal)
        || simple.StartsWith("PBS", StringComparison.Ordinal)
        || simple.Contains("Shader", StringComparison.Ordinal);

    // Index of every component this build actually has, by simple name, built once from the loaded
    // assemblies. A hardcoded namespace list was the first attempt and it quietly under-reported: it
    // named folders that are not namespaces, missed real ones, and so declared components we do have -
    // materials, meshes - as unsupported. Asking the assembly is the only version of this that cannot
    // drift as the component tree gets reorganised. -xlinka
    private static Dictionary<string, Type>? _componentsByName;
    private static readonly object _indexLock = new();

    private static Dictionary<string, Type> ComponentIndex
    {
        get
        {
            lock (_indexLock)
            {
                if (_componentsByName != null)
                    return _componentsByName;

                var index = new Dictionary<string, Type>(StringComparer.Ordinal);
                var componentBase = typeof(Component);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = assembly.GetName().Name;
                    if (name == null || !name.StartsWith("Lumora", StringComparison.Ordinal))
                        continue;

                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (System.Reflection.ReflectionTypeLoadException ex)
                    {
                        types = Array.ConvertAll(
                            Array.FindAll(ex.Types, t => t != null), t => t!);
                    }

                    foreach (var type in types)
                    {
                        if (type == null || type.IsAbstract || !componentBase.IsAssignableFrom(type))
                            continue;
                        // Generic components are indexed by their bare name: "ValueVariable`1" is the
                        // counterpart of a generic on the other side, and the arguments are matched
                        // separately by whatever rebuilds the instance.
                        var key = type.Name;
                        int tick = key.IndexOf('`');
                        if (tick > 0)
                            key = key[..tick];
                        index.TryAdd(key, type);
                    }
                }
                return _componentsByName = index;
            }
        }
    }

    private static Type? FindType(string simpleName) => ComponentIndex.GetValueOrDefault(simpleName);

    // "[Assembly]Namespace.Outer+Nested<...>" -> "Nested". Generic arguments are dropped: the component
    // identity is the open type, and the arguments are carried separately by whatever rebuilds it.
    public static string SimpleName(string encodedName)
    {
        var s = encodedName;

        int close = s.IndexOf(']');
        if (s.Length > 0 && s[0] == '[' && close > 0)
            s = s[(close + 1)..];

        int generic = s.IndexOf('<');
        if (generic >= 0)
            s = s[..generic];

        int nested = s.LastIndexOf('+');
        if (nested >= 0)
            s = s[(nested + 1)..];

        int dot = s.LastIndexOf('.');
        if (dot >= 0)
            s = s[(dot + 1)..];

        return s.Trim();
    }

    // The generic arguments, in source order, for reporting. "Foo<bar, [X]Y.Z>" -> ["bar", "Z"].
    public static IReadOnlyList<string> GenericArguments(string encodedName)
    {
        int open = encodedName.IndexOf('<');
        if (open < 0)
            return Array.Empty<string>();

        var args = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        for (int i = open; i < encodedName.Length; i++)
        {
            char c = encodedName[i];
            if (c == '<')
            {
                depth++;
                if (depth == 1) continue;
            }
            else if (c == '>')
            {
                depth--;
                if (depth == 0)
                {
                    Flush(args, current);
                    break;
                }
            }
            else if (c == ',' && depth == 1)
            {
                Flush(args, current);
                continue;
            }
            current.Append(c);
        }
        return args;

        static void Flush(List<string> into, StringBuilder sb)
        {
            var text = sb.ToString().Trim();
            sb.Clear();
            if (text.Length > 0)
                into.Add(SimpleName(text));
        }
    }
}
