// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Import;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Helio.UI;
using LumoraMeshes = Lumora.Core.Components.Meshes;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// The readout that floats over an import while it happens.
//
// This is the IN-WORLD half, and it starts after the import dialog is done: the dialog asks what you
// want, this says how it is going. It wears the same plate the nameplates do - rounded panel, rim,
// centred label, billboarded at the viewer - because a second floating-text style in the same world
// would just look like a bug.
//
// Everything about it is local. Local slot, non-persistent, no sync generation: the person who dropped
// the file is the only one who needs a progress bar for it, and the model itself is what everybody
// else sees arrive.
//
// Threading: the static calls are safe from any thread because none of them touch the data model -
// they write volatile fields on the import's ticket. Only Show marshals, to raise the plate. OnUpdate
// reads the ticket back on the world thread and writes the visuals only when they actually changed. -xlinka
[ComponentCategory("Assets/Import")]
public class ModelImportIndicator : Component
{
    public readonly SyncRef<Slot> Plate;
    public readonly SyncRef<TextRenderer> TitleText;
    public readonly SyncRef<TextRenderer> StatusText;
    public readonly SyncRef<Slot> ProgressFill;
    // The slot the model imports into. Floating over it means the readout is wherever the model is
    // about to appear, which is in front of the user by construction. -xlinka
    public readonly SyncRef<Slot> Anchor;

    private const float PanelWidth = 0.52f;
    private const float PanelHeight = 0.20f;
    private const float PanelCornerRadius = 0.022f;
    private const float RimWidth = 0.006f;
    private const float PanelDepth = -0.002f;
    private const float BarWidth = 0.44f;
    private const float BarHeight = 0.022f;
    private const float BarHalfWidth = BarWidth * 0.5f;

    private const float AnchorHeight = 1.9f;
    private const float LingerOnDone = 0.6f;
    private const float LingerOnFail = 4f;

    private static readonly color PanelColor = new color(0.07f, 0.08f, 0.11f, 0.88f);
    private static readonly color RimColor = new color(0.30f, 0.55f, 0.95f, 0.95f);
    private static readonly color FailRimColor = new color(0.90f, 0.32f, 0.30f, 0.95f);
    private static readonly color BarTrackColor = new color(0.16f, 0.17f, 0.21f, 0.95f);
    private static readonly color BarFillColor = new color(0.32f, 0.68f, 1f, 1f);
    private static readonly color FailFillColor = new color(0.90f, 0.32f, 0.30f, 1f);
    private static readonly color StatusColor = new color(0.78f, 0.84f, 0.94f, 1f);

    // The import this plate reports on. Every cross-thread write lands on the ticket, never on the
    // component, so the readout is allowed to be one frame behind and there is no lock here on purpose.
    private ImportTicket _ticket = new ImportTicket();

    private LocaleText _title;
    private float _lingerTimer;
    private float _appliedProgress = -1f;
    private string _appliedStatus = null!;
    private bool _appliedFailState;
    private bool _placed;

    private LumoraMeshes.RoundedQuadMesh? _panelMesh;
    private LumoraMeshes.RoundedQuadRingMesh? _rimMesh;
    private LumoraMeshes.RoundedQuadMesh? _fillMesh;

    // ONE PLATE, ONE TICKET PER IMPORT, AND THE TICKET IS THE TOKEN.
    //
    // A multi-file drop starts one import per file and the last Show takes the plate, so a call has to
    // say which import it speaks for or the first one to finish puts "Done" on somebody else's
    // still-running readout, which is the same lie as hiding early.
    //
    // The state lives on the ticket rather than on the plate, and that is what covers the window between
    // Show returning a token and the plate actually existing on the world thread. A Report landing in
    // there updates its own import instead of painting the plate still up from the last one, and a Hide
    // or a Fail landing in there retires the import before it has a plate, so the plate is born finished
    // or never born at all - rather than appearing afterwards with nothing left alive to hide it. -xlinka
    private sealed class ImportTicket
    {
        public volatile string Status = string.Empty;
        public volatile float Progress;
        public volatile bool Done;
        public volatile bool Failed;
        public volatile bool Dismissed;
    }

    private static ModelImportIndicator _current = null!;
    private static volatile ImportTicket? _active;

    public ModelImportIndicator()
    {
        Plate = new SyncRef<Slot>(this);
        TitleText = new SyncRef<TextRenderer>(this);
        StatusText = new SyncRef<TextRenderer>(this);
        ProgressFill = new SyncRef<Slot>(this);
        Anchor = new SyncRef<Slot>(this);
    }

    public override void OnInit()
    {
        base.OnInit();
        Compose();
        InitializeNewSyncMembers();
        UpdatePosition();
    }

    private void Compose()
    {
        if (Slot == null || World == null)
            return;

        // The plate theme is a per-world local singleton (one font, one vertex-coloured unlit material).
        // Borrowing it means an import readout costs no font provider and no second material.
        var theme = NameplateTheme.For(World);

        var plate = Slot.AddLocalSlot("Plate");
        plate.Persistent.Value = false;
        plate.AttachComponent<FaceLocalUser>();
        Plate.Target = plate;

        var rim = plate.AddSlot("Rim");
        rim.LocalPosition.Value = new float3(0f, 0f, PanelDepth);
        _rimMesh = rim.AttachComponent<LumoraMeshes.RoundedQuadRingMesh>();
        _rimMesh.Size.Value = new float2(PanelWidth, PanelHeight);
        _rimMesh.CornerRadius.Value = PanelCornerRadius;
        _rimMesh.RimWidth.Value = RimWidth;
        _rimMesh.Color = RimColor;
        AttachRenderer(rim, _rimMesh, theme);

        var panel = plate.AddSlot("Panel");
        panel.LocalPosition.Value = new float3(0f, 0f, PanelDepth);
        _panelMesh = panel.AttachComponent<LumoraMeshes.RoundedQuadMesh>();
        _panelMesh.Size.Value = new float2(PanelWidth, PanelHeight);
        _panelMesh.CornerRadius.Value = PanelCornerRadius;
        _panelMesh.Color = PanelColor;
        AttachRenderer(panel, _panelMesh, theme);

        var title = plate.AddSlot("Title");
        title.LocalPosition.Value = new float3(0f, 0.048f, 0f);
        var titleRenderer = title.AttachComponent<TextRenderer>();
        if (theme?.Font != null)
            titleRenderer.Font.Target = theme.Font;
        titleRenderer.Size.Value = 0.042f;
        titleRenderer.Color.Value = color.White;
        titleRenderer.HorizontalAlign.Value = TextHorizontalAlignment.Center;
        titleRenderer.VerticalAlign.Value = TextVerticalAlignment.Middle;
        titleRenderer.OutlineThickness.Value = 1.2f;
        titleRenderer.Text.Value = _title.IsEmpty ? string.Empty : _title.Resolve();
        TitleText.Target = titleRenderer;

        var status = plate.AddSlot("Status");
        status.LocalPosition.Value = new float3(0f, -0.004f, 0f);
        var statusRenderer = status.AttachComponent<TextRenderer>();
        if (theme?.Font != null)
            statusRenderer.Font.Target = theme.Font;
        statusRenderer.Size.Value = 0.028f;
        statusRenderer.Color.Value = StatusColor;
        statusRenderer.HorizontalAlign.Value = TextHorizontalAlignment.Center;
        statusRenderer.VerticalAlign.Value = TextVerticalAlignment.Middle;
        statusRenderer.Text.Value = string.Empty;
        StatusText.Target = statusRenderer;

        var track = plate.AddSlot("BarTrack");
        track.LocalPosition.Value = new float3(0f, -0.055f, 0f);
        var trackMesh = track.AttachComponent<LumoraMeshes.RoundedQuadMesh>();
        trackMesh.Size.Value = new float2(BarWidth, BarHeight);
        trackMesh.CornerRadius.Value = BarHeight * 0.5f;
        trackMesh.Color = BarTrackColor;
        AttachRenderer(track, trackMesh, theme);

        // Left-anchored fill: a centred quad shifted left by half its missing width and x-scaled by the
        // fraction, so it grows from the left edge without re-meshing anything per update.
        var fill = track.AddSlot("BarFill");
        fill.LocalPosition.Value = new float3(-BarHalfWidth, 0f, 0.001f);
        fill.LocalScale.Value = new float3(0.0001f, 1f, 1f);
        _fillMesh = fill.AttachComponent<LumoraMeshes.RoundedQuadMesh>();
        _fillMesh.Size.Value = new float2(BarWidth, BarHeight * 0.72f);
        _fillMesh.CornerRadius.Value = BarHeight * 0.36f;
        _fillMesh.Color = BarFillColor;
        AttachRenderer(fill, _fillMesh, theme);
        ProgressFill.Target = fill;
    }

    private static void AttachRenderer(Slot slot, LumoraMeshes.ProceduralMesh mesh, NameplateTheme? theme)
    {
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        if (theme?.PlateMaterial != null)
            renderer.Material.Target = theme.PlateMaterial;
        // A floating readout has no business darkening the floor under it.
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var ticket = _ticket;
        if (ticket.Dismissed)
        {
            Slot?.Destroy();
            return;
        }

        UpdatePosition(delta);
        ApplyState(ticket);

        if (ticket.Done || ticket.Failed)
        {
            _lingerTimer += delta;
            if (_lingerTimer >= (ticket.Failed ? LingerOnFail : LingerOnDone))
                Slot?.Destroy();
        }
    }

    private void ApplyState(ImportTicket ticket)
    {
        bool failState = ticket.Failed;
        float progress = failState ? _appliedProgress : System.Math.Clamp(ticket.Progress, 0f, 1f);
        string status = ticket.Status ?? string.Empty;

        if (failState != _appliedFailState)
        {
            _appliedFailState = failState;
            if (_rimMesh != null && !_rimMesh.IsDestroyed)
                _rimMesh.Color = failState ? FailRimColor : RimColor;
            if (_fillMesh != null && !_fillMesh.IsDestroyed)
                _fillMesh.Color = failState ? FailFillColor : BarFillColor;
        }

        if (!ReferenceEquals(status, _appliedStatus) && status != _appliedStatus)
        {
            _appliedStatus = status;
            if (StatusText.Target != null && !StatusText.Target.IsDestroyed)
                StatusText.Target.Text.Value = status;
        }

        // A hundredth of the bar is well under a pixel at any sane distance; below that the write is
        // pure sync churn on a slot transform.
        if (System.Math.Abs(progress - _appliedProgress) > 0.005f)
        {
            _appliedProgress = progress;
            var fill = ProgressFill.Target;
            if (fill != null && !fill.IsDestroyed)
            {
                float scale = System.Math.Max(0.0001f, progress);
                fill.LocalScale.Value = new float3(scale, 1f, 1f);
                fill.LocalPosition.Value = new float3(BarHalfWidth * (progress - 1f), 0f, 0.001f);
            }
        }
    }

    private void UpdatePosition(float delta = 0f)
    {
        var anchor = Anchor.Target;
        var head = World?.LocalUser?.Root?.HeadSlot;

        float3 target;
        if (anchor != null && !anchor.IsDestroyed)
        {
            target = anchor.GlobalPosition + float3.Up * AnchorHeight;
        }
        else if (head != null && !head.IsDestroyed)
        {
            target = head.GlobalPosition + (head.GlobalRotation * float3.Forward) * 1.1f + float3.Up * 0.18f;
        }
        else
        {
            // Nothing to hang off yet. Retry next frame rather than snapping to the world origin, which
            // is what "just place it somewhere" looks like from inside the world. -xlinka
            return;
        }

        if (Slot == null || Slot.IsDestroyed)
            return;

        // Facing is FaceLocalUser's job on the plate child; this only carries position, so the readout
        // does not fight the billboard.
        if (delta > 0f && _placed)
            Slot.GlobalPosition = float3.Lerp(Slot.GlobalPosition, target, System.Math.Min(1f, delta * 6f));
        else
            Slot.GlobalPosition = target;
        _placed = true;
    }

    // STATIC API - safe from any thread

    // The returned token identifies THIS import. Pass it back to Report/Hide/Fail/Dismiss so a call
    // that arrives after another import has taken the plate is dropped rather than applied to it.
    public static object Show(World world, Slot anchor, LocaleText title)
    {
        var ticket = new ImportTicket();
        if (world == null)
            return ticket;

        // Published before the hop, so a Report or a Fail from the importer's threads reaches THIS import
        // from the moment its caller holds the token. It reaches the ticket, not whatever plate happens to
        // be up, which is why publishing early is safe here.
        _active = ticket;
        world.RunSynchronously(() =>
        {
            try
            {
                // Superseded by a later Show, or already cancelled: no plate to raise. The plate that is
                // up stays up, because whoever superseded us clears it when they raise theirs.
                if (!ReferenceEquals(_active, ticket) || ticket.Dismissed)
                    return;

                var previous = _current;
                if (previous != null && !previous.IsDestroyed)
                    previous.Slot?.Destroy();
                _current = null!;

                var slot = world.RootSlot.AddLocalSlot("Import Indicator");
                slot.Persistent.Value = false;
                var indicator = slot.AttachComponent<ModelImportIndicator>();
                indicator._title = title;
                indicator._ticket = ticket;
                if (indicator.TitleText.Target != null && !title.IsEmpty)
                    indicator.TitleText.Target.Text.Value = title.Resolve();
                if (anchor != null && !anchor.IsDestroyed)
                    indicator.Anchor.Target = anchor;
                _current = indicator;
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"ModelImportIndicator.Show: {ex.Message}");
            }
        });
        return ticket;
    }

    // A token from another Show still names its own import, so it is answered rather than dropped: its
    // plate is gone but its ticket is not, and writing there cannot touch the live one. No token at all
    // means "whichever import is current", which is what the template callers pass.
    private static ImportTicket? Resolve(object? token)
        => token as ImportTicket ?? (token == null ? _active : null);

    // Retire the ticket so a later call with no token cannot land on a finished import.
    private static void Retire(ImportTicket ticket)
    {
        if (ReferenceEquals(_active, ticket))
            _active = null;
    }

    public static void Report(in ImportProgress progress, object? token = null)
    {
        var ticket = Resolve(token);
        if (ticket == null)
            return;

        if (progress.Fraction >= 0f)
            ticket.Progress = progress.Fraction;

        var text = progress.Describe();
        ticket.Status = text.IsEmpty ? string.Empty : text.Resolve();
    }

    // Finished cleanly: the bar completes and the plate goes away a beat later, so the eye gets to see
    // it land instead of the readout just blinking out.
    public static void Hide(object? token = null)
    {
        var ticket = Resolve(token);
        if (ticket == null)
            return;

        ticket.Progress = 1f;
        var done = new ImportProgress(ImportStage.Complete, 1f).Describe();
        ticket.Status = done.Resolve();
        ticket.Done = true;
        Retire(ticket);
    }

    // Failed: say WHY, in the world, for long enough to read, then go. A silent disappearance is
    // indistinguishable from the import having worked and the model being somewhere else. -xlinka
    public static void Fail(string reason, object? token = null)
    {
        var ticket = Resolve(token);
        if (ticket == null)
            return;

        var label = string.IsNullOrWhiteSpace(reason)
            ? "Import.Failed".AsLocale("Import failed")
            : "Import.FailedReason".AsLocale("Import failed: {0}", reason);
        ticket.Status = label.Resolve();
        ticket.Failed = true;
        Retire(ticket);
    }

    // Cancelled: whatever the readout was reporting on is gone (the target slot was deleted, or the
    // world was left), so there is nothing to call finished and nobody to read a failure. Take the
    // plate away rather than leaving it hanging in front of the user at "Meshes 3 of 9". -xlinka
    public static void Dismiss(object? token = null)
    {
        var ticket = Resolve(token);
        if (ticket == null)
            return;

        // The plate reads this on its own update and destroys itself; a plate that does not exist yet
        // reads it in Show and is never raised.
        ticket.Dismissed = true;
        Retire(ticket);
    }

    public override void OnDestroy()
    {
        if (ReferenceEquals(_current, this))
            _current = null!;
        base.OnDestroy();
    }
}
