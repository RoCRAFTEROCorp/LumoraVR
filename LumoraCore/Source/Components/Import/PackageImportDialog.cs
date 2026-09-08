// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Assets.Interop;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Import;

// Readout for a shared object package from another platform.
//
// The package is read and measured BEFORE anything is built, and the panel shows what would survive
// and what would not. That split is the point: a conversion between two engines always loses
// something, and the only honest way to present it is to say what, up front, instead of importing a
// half-avatar and letting someone work out the holes themselves.
//
// Nothing here reaches the network. Packages carry every byte they reference, so the readout is
// produced entirely from the file on disk. -xlinka
[ComponentCategory("Assets/Import")]
public sealed class PackageImportDialog : ImportDialog
{
    private PackageInspection? _report;
    private PackageImportResult? _result;
    private string? _error;
    private bool _reading;
    private bool _importing;

    protected override string TitleText => "Package";
    protected override float2 CanvasSize => new float2(460f, 600f);

    private static readonly color HeadingColor = new color(0.96f, 0.96f, 1f, 1f);
    private static readonly color BodyColor = new color(0.82f, 0.82f, 0.90f, 1f);
    private static readonly color GoodColor = new color(0.55f, 0.85f, 0.60f, 1f);
    private static readonly color LossColor = new color(0.90f, 0.62f, 0.42f, 1f);
    private static readonly color MutedColor = new color(0.62f, 0.62f, 0.72f, 1f);

    protected override void OpenRoot(UIBuilder ui)
    {
        if (_report == null && _error == null)
        {
            BeginRead();
            BuildReading(ui);
            return;
        }
        if (_error != null)
        {
            BuildError(ui);
            return;
        }
        if (_importing)
        {
            BuildImporting(ui);
            return;
        }
        if (_result != null)
        {
            BuildOutcome(ui);
            return;
        }
        BuildReport(ui);
    }

    private void BuildImporting(UIBuilder ui)
    {
        var body = SetupSection(ui, "Importing", backButton: false);
        body.PushStyle().FlexibleHeight(1f);
        var msg = body.Text("Converting meshes and textures...", 14f, BodyColor);
        msg.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        msg.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        msg.WordWrap.Value = true;
        body.PopStyle();
    }

    // After the import: what actually happened, rather than what was predicted. The two can differ -
    // an asset can fail to decode, a component can refuse to attach - and showing the real numbers is
    // the whole point of putting the readout here instead of only up front.
    private void BuildOutcome(UIBuilder ui)
    {
        var r = _result!;
        var body = SetupSection(ui, r.Succeeded ? "Imported" : "Import Failed", backButton: false);

        if (!r.Succeeded)
        {
            Line(body, r.FailureReason, 13f, LossColor);
            Spacer(body, 8f);
            SetupGrid(body);
            GridButton(body, "Close", () => Slot.Destroy());
            return;
        }

        Line(body, CleanName(_report?.Name), 15f, HeadingColor);
        Spacer(body, 4f);

        Line(body, "BROUGHT IN", 12f, HeadingColor);
        Line(body, $"{r.SlotsCreated} slots, {r.ComponentsAttached} components", 12f, GoodColor);
        Line(body, $"{r.MeshesConverted} meshes, {r.TexturesImported} textures", 12f, GoodColor);

        Spacer(body, 8f);
        Line(body, "LEFT BEHIND", 12f, HeadingColor);
        if (r.ComponentsSkipped > 0)
            Line(body, $"{r.ComponentsSkipped} components with no equivalent", 12f, LossColor);
        if (r.AssetsFailed > 0)
            Line(body, $"{r.AssetsFailed} assets failed to convert", 12f, LossColor);
        if (r.ComponentsSkipped == 0 && r.AssetsFailed == 0)
            Line(body, "Nothing", 12f, GoodColor);

        Spacer(body, 6f);
        Line(body, $"Scripted behaviour stripped ({r.ComponentsStripped} nodes).", 11f, MutedColor);
        Line(body, $"{r.SlotsPruned} empty slots pruned.", 11f, MutedColor);

        Spacer(body, 8f);
        SetupGrid(body);
        GridButton(body, "Full List To Log", DumpToLog);
        GridButton(body, "Close", () => Slot.Destroy());
    }

    // internal so UniversalImporter can drive a silent package import, the same way it already can for
    // images, models, videos and shaders. Packages were the one class with no scripted path at all,
    // which meant no automated test could exercise the real import route. -xlinka
    internal void RunImport()
    {
        if (_importing || _result != null)
            return;

        var path = Paths.Count > 0 ? Paths[0] : null;
        var world = ResolveTargetWorld();
        if (string.IsNullOrEmpty(path) || world == null)
            return;

        _importing = true;
        OpenPage(OpenRoot);

        // Imports land in the world the dialog was opened for, under its root, positioned where the
        // dialog is so the object appears where it was asked for rather than at the origin.
        //
        // Position only. The anchor deliberately keeps identity rotation so the package's own authored
        // rotation ends up in WORLD space, the way its author built it, instead of being composed on top
        // of wherever the dialog happened to be facing.
        var anchor = world.RootSlot.AddSlot("Imported Package");
        anchor.GlobalPosition = Slot.GlobalPosition;

        world.StartTask(async () =>
        {
            PackageImportResult result;
            try
            {
                result = await PackageObjectImporter.ImportAsync(path, anchor);
            }
            catch (Exception ex)
            {
                result = new PackageImportResult { FailureReason = ex.Message };
            }

            await WorldContext.ToWorld();
            if (!anchor.IsDestroyed && !result.Succeeded)
                anchor.Destroy();

            if (IsDestroyed)
                return;

            _result = result;
            _importing = false;
            OpenPage(OpenRoot);
        });
    }

    // Reading a large package means inflating a multi-megabyte graph and sniffing every asset, which
    // is far too much to do on the world's update thread. Read on a worker, then reopen the page.
    private void BeginRead()
    {
        if (_reading)
            return;
        _reading = true;

        var path = Paths.Count > 0 ? Paths[0] : null;
        if (string.IsNullOrEmpty(path))
        {
            _error = "No package file was given.";
            _reading = false;
            return;
        }

        var world = ResolveTargetWorld();
        world?.StartTask(async () =>
        {
            await WorldContext.ToBackground();

            PackageInspection? report = null;
            string? error = null;
            try
            {
                report = PackageInspector.Inspect(path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            await WorldContext.ToWorld();
            if (IsDestroyed)
                return;

            _report = report;
            _error = error ?? (report is { HasObjectGraph: false } ? report.FailureReason : null);
            _reading = false;
            OpenPage(OpenRoot);
        });
    }

    private void BuildReading(UIBuilder ui)
    {
        var body = SetupSection(ui, "Reading Package", backButton: false);
        body.PushStyle().FlexibleHeight(1f);
        var msg = body.Text("Reading the package and working out what converts...", 14f, BodyColor);
        msg.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        msg.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        msg.WordWrap.Value = true;
        body.PopStyle();
    }

    private void BuildError(UIBuilder ui)
    {
        var body = SetupSection(ui, "Cannot Read Package", backButton: false);
        body.PushStyle().FlexibleHeight(1f);
        var msg = body.Text(_error ?? "Unknown error.", 14f, LossColor);
        msg.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        msg.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        msg.WordWrap.Value = true;
        body.PopStyle();

        SetupGrid(body);
        GridButton(body, "As Raw File", AsRawFile, BackColor);
        GridButton(body, "Close", () => Slot.Destroy());
    }

    private void BuildReport(UIBuilder ui)
    {
        var report = _report!;
        var body = SetupSection(ui, "Package Contents", backButton: false);

        Line(body, CleanName(report.Name), 15f, HeadingColor);
        int arrivingSlots = System.Math.Max(0, report.SlotCount - report.PrunableSlots);
        Line(body, $"{arrivingSlots} slots, {report.ComponentInstances - report.InstancesOfFate(PackageTypeFate.Stripped)} components", 12f, MutedColor);

        // Stripped components are out of the sum entirely, top and bottom. They are not losses to be
        // weighed against the conversion rate - they are things we deliberately do not carry, and the
        // question this panel answers is whether the object itself arrives intact.
        int stripped = report.InstancesOfFate(PackageTypeFate.Stripped);
        int convertible = report.InstancesOfFate(PackageTypeFate.Mapped);
        int considered = System.Math.Max(1, report.ComponentInstances - stripped);
        int percent = (int)System.Math.Round(100.0 * convertible / considered);

        Spacer(body, 6f);
        Line(body, $"{percent}% of components map", 15f, percent >= 50 ? GoodColor : LossColor);
        Line(body, $"{convertible} of {considered}", 11f, MutedColor);

        Spacer(body, 8f);
        Line(body, "CARRIED OVER", 12f, HeadingColor);
        Line(body, Describe(report), 12f, BodyColor);

        Spacer(body, 8f);
        Line(body, "NOT CARRIED OVER", 12f, HeadingColor);
        foreach (var line in LossLines(report))
            Line(body, line, 12f, LossColor);

        // Mentioned once, quietly, in the muted colour the counts use - it is a note about what this
        // importer does, not an item on the damage list.
        if (stripped > 0)
        {
            Spacer(body, 6f);
            Line(body, $"Scripted behaviour is stripped ({stripped} nodes).", 11f, MutedColor);
            if (report.PrunableSlots > 0)
                Line(body, $"{report.PrunableSlots} empty slots pruned with it.", 11f, MutedColor);
        }

        Spacer(body, 8f);
        SetupGrid(body);
        GridButton(body, "Import", RunImport, new color(0.35f, 0.62f, 0.42f, 1f));
        GridButton(body, "Full List To Log", DumpToLog);
        GridButton(body, "Close", () => Slot.Destroy());
    }

    private static string Describe(PackageInspection report)
    {
        var sb = new StringBuilder();
        sb.Append(report.InstancesOfFate(PackageTypeFate.Mapped)).Append(" components");

        int meshes = report.AssetsOfKind(PackageAssetKind.Mesh);
        int textures = report.AssetsOfKind(PackageAssetKind.Png)
                     + report.AssetsOfKind(PackageAssetKind.Jpeg)
                     + report.AssetsOfKind(PackageAssetKind.WebP)
                     + report.AssetsOfKind(PackageAssetKind.OpenExr);
        if (meshes > 0) sb.Append(", ").Append(meshes).Append(" meshes");
        if (textures > 0) sb.Append(", ").Append(textures).Append(" textures");
        return sb.ToString();
    }

    private static IEnumerable<string> LossLines(PackageInspection report)
    {
        var shaders = report.OfFate(PackageTypeFate.Shader).ToList();
        if (shaders.Count > 0)
        {
            yield return $"Shaders with no match: {shaders.Sum(s => s.Instances)}";
            foreach (var s in shaders.OrderByDescending(s => s.Instances).Take(4))
                yield return $"   {s.Instances}x {s.SimpleName}";
        }

        int audio = report.AssetsOfKind(PackageAssetKind.Wav) + report.AssetsOfKind(PackageAssetKind.Ogg);
        if (audio > 0)
            yield return $"Audio clips: {audio}";

        var missing = report.OfFate(PackageTypeFate.Unsupported)
            .OrderByDescending(t => t.Instances)
            .Take(5)
            .ToList();
        if (missing.Count > 0)
        {
            yield return $"No equivalent yet: {report.InstancesOfFate(PackageTypeFate.Unsupported)}";
            foreach (var t in missing)
                yield return $"   {t.Instances}x {t.SimpleName}";
        }
    }

    // The panel only has room for the headline losses; the log gets everything, which is what you
    // actually want when deciding whether a specific object is worth converting.
    private void DumpToLog()
    {
        var report = _report;
        if (report == null)
            return;

        Logger.Log($"Package '{CleanName(report.Name)}' ({Path.GetFileName(report.SourcePath)}):");
        Logger.Log($"  {report.SlotCount} slots, {report.ComponentInstances} component instances, built with {report.EngineVersion}");
        foreach (PackageTypeFate fate in Enum.GetValues<PackageTypeFate>())
        {
            var group = report.OfFate(fate).OrderByDescending(t => t.Instances).ToList();
            if (group.Count == 0)
                continue;
            Logger.Log($"  -- {fate}: {group.Sum(t => t.Instances)} instances across {group.Count} types");
            // Stripped types are summarised, never enumerated. Hundreds of node names is the exact
            // noise this log exists to cut through.
            if (fate == PackageTypeFate.Stripped)
                continue;
            foreach (var t in group)
            {
                var arrow = t.MappedTo != null ? " -> " + t.MappedTo : string.Empty;
                Logger.Log($"       {t.Instances,5}x {t.SimpleName}{arrow}");
            }
        }
        foreach (PackageAssetKind kind in Enum.GetValues<PackageAssetKind>())
        {
            int n = report.AssetsOfKind(kind);
            if (n > 0)
                Logger.Log($"  asset {kind}: {n}");
        }
        if (report.ExternalReferences.Count > 0)
        {
            Logger.Log($"  {report.ExternalReferences.Count} reference(s) point outside the package and are NOT fetched:");
            foreach (var r in report.ExternalReferences)
                Logger.Log("       " + r);
        }
    }

    // Package names routinely carry rich-text colour tags. They would render as literal markup here.
    private static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Untitled package";

        var sb = new StringBuilder(name.Length);
        int depth = 0;
        foreach (char c in name)
        {
            if (c == '<') { depth++; continue; }
            if (c == '>') { if (depth > 0) depth--; continue; }
            if (depth == 0) sb.Append(c);
        }
        var cleaned = sb.ToString().Trim();
        return cleaned.Length > 0 ? cleaned : "Untitled package";
    }

    // Rows go through the builder's own style stack, NOT a hand-made slot with a nested UIBuilder.
    // That was the first attempt and it rendered as a single dot: a fresh RectTransform is a 100x100
    // box centred in its parent, and Next() only overrides that when the parent carries a layout
    // controller. Building into a private child bypassed the section's VerticalLayout entirely, so
    // every line collapsed to the default box. PushStyle + Text lets Next() attach the LayoutElement
    // from the style and the section lays the row out properly. -xlinka
    private static void Line(UIBuilder ui, string? text, float size, color tint)
    {
        if (string.IsNullOrEmpty(text))
            return;

        float height = size + 7f;
        ui.PushStyle().MinHeight(height).PreferredHeight(height).FlexibleHeight(0f);
        var label = ui.Text(text, size, tint);
        label.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        label.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        label.WordWrap.Value = false;
        ui.PopStyle();
    }

    private static void Spacer(UIBuilder ui, float height)
    {
        ui.PushStyle().MinHeight(height).PreferredHeight(height).FlexibleHeight(0f);
        ui.Next("Spacer");
        ui.PopStyle();
    }
}
