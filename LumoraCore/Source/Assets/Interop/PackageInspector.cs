// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// What kind of file an asset payload turned out to be, decided from its leading bytes.
public enum PackageAssetKind
{
    Unknown,
    ObjectGraph,
    Mesh,
    Png,
    Jpeg,
    WebP,
    Wav,
    Ogg,
    OpenExr,
}

public sealed class PackageAssetSummary
{
    public string Signature { get; init; } = string.Empty;
    public PackageAssetKind Kind { get; init; }
    public long Bytes { get; init; }
    public int Variants { get; init; }
    public string? MetadataKind { get; init; }
    public int MeshVersion { get; init; }
}

public sealed class PackageTypeSummary
{
    public string EncodedName { get; init; } = string.Empty;
    public string SimpleName { get; init; } = string.Empty;
    public PackageTypeFate Fate { get; init; }
    public int Instances { get; set; }
    public string? MappedTo { get; init; }
}

// Everything a package reports about itself before anything is built in the world.
public sealed class PackageInspection
{
    public string SourcePath { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? RecordType { get; init; }
    public string? EngineVersion { get; init; }
    public bool HasObjectGraph { get; init; }
    public string? FailureReason { get; init; }

    public int SlotCount { get; set; }
    public int ComponentInstances { get; set; }

    // Slots that would hold nothing once stripped components are dropped, and that have no surviving
    // descendant either. Scaffolding for scripted behaviour, in other words - worth pruning, because
    // importing hundreds of empty slots is its own kind of broken.
    public int PrunableSlots { get; set; }

    public List<PackageTypeSummary> Types { get; } = new();
    public List<PackageAssetSummary> Assets { get; } = new();

    // References that point somewhere other than into this package. We never fetch these; they are
    // listed so it is obvious when a package expects content it did not bring with it.
    public List<string> ExternalReferences { get; } = new();

    public int VariantCount { get; init; }

    public IEnumerable<PackageTypeSummary> OfFate(PackageTypeFate fate) => Types.Where(t => t.Fate == fate);

    public int InstancesOfFate(PackageTypeFate fate) => Types.Where(t => t.Fate == fate).Sum(t => t.Instances);

    public int AssetsOfKind(PackageAssetKind kind) => Assets.Count(a => a.Kind == kind);

    public float ConvertibleFraction =>
        ComponentInstances <= 0 ? 0f : (float)InstancesOfFate(PackageTypeFate.Mapped) / ComponentInstances;
}

// Reads a package and reports what it contains and what would survive conversion, without building
// anything in a world. This is the pass behind the import panel's readout, and it is deliberately
// separate from any actual import: knowing what will be lost is useful on its own, and it is the only
// honest way to show it before the user commits. -xlinka
public static class PackageInspector
{
    public static PackageInspection Inspect(string file)
    {
        using var archive = PackageArchive.Open(file);
        return Inspect(archive);
    }

    public static PackageInspection Inspect(PackageArchive archive)
    {
        var record = archive.MainRecord;
        var assets = SummarizeAssets(archive);

        if (record == null)
        {
            return Fail(archive, assets, "Package has no main record.");
        }
        if (!record.IsObject)
        {
            return Fail(archive, assets, $"Main record is a '{record.RecordType}' package; only object packages convert.");
        }

        string? graphSignature = PackageArchive.SignatureOf(TryUri(record.AssetUri));
        if (graphSignature == null || !archive.HasAsset(graphSignature))
        {
            return Fail(archive, assets, "Main record does not point at an object graph inside this package.");
        }

        DataTreeDictionary root;
        try
        {
            root = PackageGraphReader.Read(archive.ReadAsset(graphSignature));
        }
        catch (Exception ex)
        {
            return Fail(archive, assets, "Could not decode the object graph: " + ex.Message);
        }

        var inspection = new PackageInspection
        {
            SourcePath = archive.SourcePath,
            Name = record.Name,
            RecordType = record.RecordType,
            EngineVersion = (root.TryGetNode("VersionNumber") as DataTreeValue)?.Value?.ToString(),
            HasObjectGraph = true,
            VariantCount = archive.VariantCount,
        };
        inspection.Assets.AddRange(assets);

        var typeTable = ReadTypeTable(root);
        var summaries = new List<PackageTypeSummary>(typeTable.Count);
        foreach (var encoded in typeTable)
        {
            var resolution = PackageTypeMap.Resolve(encoded);
            summaries.Add(new PackageTypeSummary
            {
                EncodedName = encoded,
                SimpleName = resolution.SimpleName,
                Fate = resolution.Fate,
                MappedTo = resolution.Target?.Name,
            });
        }

        CountInstances(root, summaries, inspection);
        CollectExternalReferences(root, inspection);

        // Types that never actually appear are noise in a readout; a package's table routinely carries
        // entries the saved subtree does not use.
        inspection.Types.AddRange(summaries.Where(s => s.Instances > 0)
            .OrderByDescending(s => s.Instances));
        return inspection;
    }

    private static PackageInspection Fail(PackageArchive archive, List<PackageAssetSummary> assets, string reason)
    {
        var inspection = new PackageInspection
        {
            SourcePath = archive.SourcePath,
            Name = archive.MainRecord?.Name,
            RecordType = archive.MainRecord?.RecordType,
            HasObjectGraph = false,
            FailureReason = reason,
            VariantCount = archive.VariantCount,
        };
        inspection.Assets.AddRange(assets);
        return inspection;
    }

    private static Uri? TryUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static List<string> ReadTypeTable(DataTreeDictionary root)
    {
        var list = new List<string>();
        if (root.TryGetNode("Types") is not DataTreeList types)
            return list;
        foreach (var node in types)
            list.Add((node as DataTreeValue)?.Value?.ToString() ?? string.Empty);
        return list;
    }

    // Every component in the graph is a dictionary carrying a "Type" index into the table and a "Data"
    // payload; every slot is a dictionary with an "ID" and a "Children" list.
    //
    // The walk is slot-AWARE rather than a flat sweep, because deciding whether a slot survives means
    // knowing which components are its own and which belong to its children. A slot lives if it keeps
    // at least one component of its own, or if any descendant does - so structural parents holding a
    // surviving subtree are never pruned out from under it.
    private static void CountInstances(DataTreeDictionary root, List<PackageTypeSummary> types, PackageInspection into)
    {
        // Returns true when this slot (or something under it) survives stripping.
        bool WalkSlot(DataTreeDictionary slot)
        {
            into.SlotCount++;

            int kept = 0;
            int strippedHere = 0;
            foreach (var pair in slot.Children)
            {
                if (pair.Key == "Children")
                    continue;
                kept += CountComponents(pair.Value, types, ref strippedHere);
            }

            int survivingChildren = 0;
            if (slot.TryGetNode("Children") is DataTreeList children)
            {
                foreach (var child in children.Children)
                {
                    if (child is DataTreeDictionary childSlot && WalkSlot(childSlot))
                        survivingChildren++;
                }
            }

            // Matches the importer: only scaffolding that STRIPPING emptied is prunable. A slot that
            // never held a component is structure - a bone, most importantly - and counting those as
            // prunable both overstates the tidy-up and describes a rig deletion as a feature.
            bool survives = kept > 0 || survivingChildren > 0;
            if (!survives && strippedHere > 0)
                into.PrunableSlots++;
            return survives || strippedHere == 0;
        }

        if (root.TryGetNode("Object") is DataTreeDictionary obj)
            WalkSlot(obj);

        // Dependency assets hang outside the slot tree and are counted, never pruned.
        if (root.TryGetNode("Assets") is { } extra)
        {
            int ignored = 0;
            CountComponents(extra, types, ref ignored);
        }

        into.ComponentInstances = types.Sum(t => t.Instances);
    }

    // Tally every component under this node WITHOUT descending into nested slots, and report how many
    // of them would survive a strip.
    private static int CountComponents(DataTreeNode node, List<PackageTypeSummary> types, ref int stripped)
    {
        int kept = 0;
        switch (node)
        {
            case DataTreeDictionary dict:
                if (dict.TryGetNode("Type") is DataTreeValue typeValue
                    && dict.ContainsKey("Data")
                    && typeValue.Value is not null)
                {
                    int index = SafeInt(typeValue);
                    if (index >= 0 && index < types.Count)
                    {
                        types[index].Instances++;
                        if (types[index].Fate != PackageTypeFate.Stripped)
                            kept++;
                        else
                            stripped++;
                    }
                    return kept;
                }
                foreach (var pair in dict.Children)
                {
                    if (pair.Key == "Children")
                        continue;
                    kept += CountComponents(pair.Value, types, ref stripped);
                }
                break;

            case DataTreeList list:
                foreach (var child in list.Children)
                    kept += CountComponents(child, types, ref stripped);
                break;
        }
        return kept;
    }

    private static int SafeInt(DataTreeValue value)
    {
        try { return Convert.ToInt32(value.Value); }
        catch { return -1; }
    }

    private static void CollectExternalReferences(DataTreeDictionary root, PackageInspection into)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in root.EnumerateTree())
        {
            if (node is not DataTreeValue { IsUrl: true } value)
                continue;
            var uri = value.ExtractUrl();
            if (uri == null)
                continue;
            if (string.Equals(uri.Scheme, PackageArchive.AssetScheme, StringComparison.OrdinalIgnoreCase))
                continue;
            if (seen.Add(uri.OriginalString) && into.ExternalReferences.Count < 64)
                into.ExternalReferences.Add(uri.OriginalString);
        }
    }

    private static List<PackageAssetSummary> SummarizeAssets(PackageArchive archive)
    {
        var list = new List<PackageAssetSummary>(archive.AssetCount);
        foreach (var signature in archive.AssetSignatures)
        {
            var head = archive.PeekAsset(signature, 16);
            var kind = Sniff(head, out int meshVersion);
            list.Add(new PackageAssetSummary
            {
                Signature = signature,
                Kind = kind,
                Bytes = archive.AssetLength(signature),
                Variants = archive.VariantsOf(signature).Count(),
                MetadataKind = archive.MetadataKind(signature),
                MeshVersion = meshVersion,
            });
        }
        return list;
    }

    // Container sniffing from the leading bytes. Textures and audio in a package are almost always the
    // creator's original files, which is why so much of a conversion needs no decoder at all.
    private static PackageAssetKind Sniff(ReadOnlySpan<byte> head, out int meshVersion)
    {
        meshVersion = 0;
        if (head.Length >= 4)
        {
            if (head[0] == 'F' && head[1] == 'r' && head[2] == 'D' && head[3] == 'T')
                return PackageAssetKind.ObjectGraph;
            if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
                return PackageAssetKind.Png;
            if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
                return PackageAssetKind.Jpeg;
            // RIFF is a container: audio and WebP images share it, told apart at offset 8.
            if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F')
            {
                if (head.Length >= 12 && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
                    return PackageAssetKind.WebP;
                return PackageAssetKind.Wav;
            }
            if (head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S')
                return PackageAssetKind.Ogg;
            if (head[0] == 0x76 && head[1] == 0x2F && head[2] == 0x31 && head[3] == 0x01)
                return PackageAssetKind.OpenExr;
        }

        // Mesh payloads start with a length-prefixed "MeshX" tag followed by an int32 version.
        if (head.Length >= 10 && head[0] == 5
            && head[1] == 'M' && head[2] == 'e' && head[3] == 's' && head[4] == 'h' && head[5] == 'X')
        {
            meshVersion = head[6] | (head[7] << 8) | (head[8] << 16) | (head[9] << 24);
            return PackageAssetKind.Mesh;
        }

        return PackageAssetKind.Unknown;
    }
}
