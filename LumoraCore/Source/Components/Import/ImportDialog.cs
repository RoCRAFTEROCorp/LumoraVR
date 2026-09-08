// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Import;

public abstract class ImportDialog : Component
{
    public const int GridItemsPerRow = 3;
    public const float GridItemSpacing = 8f;
    public const float GridPadding = 12f;
    public const float ButtonHeight = 56f;
    public const float HeaderRowHeight = 44f;

    public static readonly color BackColor = new color(0.85f, 0.30f, 0.34f, 1f);
    public static readonly color SectionTitleColor = new color(0.96f, 0.96f, 1f, 1f);
    // BorderColor matches ButtonFill at full alpha so the border layer doesn't
    // wash the button at glancing viewing angles: BorderedImage draws the border
    // layer across the whole rect first, then the fill layer inset by
    // BorderThickness. At extreme angles the 2-px inset rasterizes to sub-pixel
    // and gets skipped, leaving only the border layer visible. If border alpha
    // was <1 (or a different color), the panel BG would show through and the
    // button would look transparent/desaturated. - xlinka
    public static readonly color BorderColor = new color(0.22f, 0.20f, 0.34f, 1f);
    public static readonly color ButtonFill = new color(0.22f, 0.20f, 0.34f, 1f);
    public static readonly color ButtonText = new color(0.95f, 0.95f, 1f, 1f);

    // World-agnostic font URL for import dialogs. UserspaceDashboard registers
    // its font URL into this at engine startup; each dialog instantiates its own
    // FontProvider on its own slot (in its own world) from this URL - avoids
    // the cross-world AssetRef.Target rejection that happens when a synced/local
    // ref points across separate World instances. - xlinka
    public static System.Uri? DefaultFontUrl { get; set; }

    // Where the font comes from, in order: the URL the dashboard registers at startup, then whatever
    // FontProvider the dialog's own world already carries.
    //
    // The startup URL is null in anything that comes up without the dashboard - a headless run, a world
    // template that spawns a dialog itself - and text with no font renders NOTHING at all, so the dialog
    // would be a blank frame with invisible buttons. Borrowing the URL rather than the provider keeps the
    // per-dialog provider rule intact: no ref points at somebody else's slot, so a dialog outliving the
    // slot it borrowed from is not a thing. -xlinka
    public static Uri? ResolveFontUrl(World? world)
    {
        if (DefaultFontUrl != null)
            return DefaultFontUrl;

        var provider = world?.RootSlot?.GetComponentInChildren<FontProvider>();
        var url = provider?.URL?.Value;
        return url;
    }

    public readonly List<string> Paths = new();

    // World where the import's resulting slot/components are spawned. The dialog
    // itself lives in UserspaceWorld (so it can reference the dashboard font),
    // but the imported asset belongs in the focused session world so other users
    // see it. Set by UniversalImporter before OnStart fires. - xlinka
    public World? TargetWorld { get; set; }

    private User? _importingUser;
    private PanelShell? _panel;
    private WizardForm? _wizard;
    private RoundedRectTextureProvider? _rounded;
    private FontProvider? _fontProvider;

    protected World ResolveTargetWorld() => TargetWorld ?? World;

    protected virtual float2 CanvasSize => new float2(420f, 520f);
    protected virtual string TitleText => "Import";

    // Target physical height of the panel in world meters. Canvas pixels are
    // multiplied by PhysicalHeight / CanvasSize.y so the panel sits at a comfortable
    // arm's-length size regardless of how many rows the wizard needs. - xlinka
    protected virtual float PhysicalHeight => 0.5f;

    protected bool CanInteract => _importingUser == null || _importingUser == World?.LocalUser;

    public PanelShell? Panel => _panel;
    public WizardForm? Wizard => _wizard;

    public void SetLocalUserAsImporting()
    {
        if (_importingUser != null)
            throw new InvalidOperationException("Importing user is already set!");
        _importingUser = World.LocalUser;
    }

    public override void OnStart()
    {
        base.OnStart();
        EnsureBuilt();
    }

    private void EnsureBuilt()
    {
        if (_panel != null) return;

        // Apply canvas->world scale so the panel renders at PhysicalHeight meters
        // tall regardless of canvas px height. Without this, 1 canvas px = 1 world
        // unit and the panel fills the view. - xlinka
        var canvasScale = CanvasSize.y > 0f ? PhysicalHeight / CanvasSize.y : 0.001f;
        Slot.LocalScale.Value = new float3(canvasScale, canvasScale, canvasScale);

        _panel = Slot.GetComponent<PanelShell>() ?? Slot.AttachComponent<PanelShell>();
        _panel.Title.Value = TitleText;
        _panel.Size.Value = CanvasSize;

        // Create a per-dialog FontProvider in this dialog's own world (so the
        // PanelShell.Font.Target ref doesn't cross worlds, which SyncRef rejects
        // even when one of the worlds is the userspace overlay). - xlinka
        var fontUrl = ResolveFontUrl(World);
        if (fontUrl != null)
        {
            var fontSlot = Slot.AddSlot("DialogFont");
            _fontProvider = fontSlot.AttachComponent<FontProvider>();
            _fontProvider.URL.Value = fontUrl;
            _fontProvider.FallbackURLs.Add(fontUrl);
            _panel.Font.Target = _fontProvider;
        }

        // Shared rounded-corner texture for borders on grid buttons. Reused across
        // all BorderedImage attachments in the dialog. - xlinka
        _rounded = Slot.AddSlot("Theme").AttachComponent<RoundedRectTextureProvider>();
        _rounded.Size.Value = 48;
        _rounded.Radius.Value = 12;

        var wizardHost = _panel.ContentSlot!.AddSlot("Wizard");
        var rect = wizardHost.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;

        _wizard = wizardHost.AttachComponent<WizardForm>();
        _wizard.CanvasSize.Value = CanvasSize;
        _wizard.OpenRoot(WithFont(OpenRoot));
    }

    protected abstract void OpenRoot(UIBuilder ui);

    // Wrap a page-build delegate so the incoming UIBuilder has the dialog's own
    // FontProvider applied before subclass build code runs. Each dialog
    // instantiates its own provider in its own world (see EnsureBuilt) to avoid
    // cross-world AssetRef rejection. - xlinka
    protected Action<UIBuilder> WithFont(Action<UIBuilder> build)
    {
        return ui =>
        {
            if (_fontProvider != null) ui.Font(_fontProvider);
            ui.FontSize(14f);
            build(ui);
        };
    }

    // Open a wizard page with the default font/style applied. Subclasses should
    // call this instead of `Wizard?.Open(menu)` directly. - xlinka
    protected void OpenPage(Action<UIBuilder> build)
    {
        _wizard?.Open(WithFont(build));
    }

    // Page layout: attach VerticalLayout directly to the page slot (matches the
    // file-browser pattern) so its children inherit the page rect. If we used
    // ui.VerticalLayout() instead, it'd create a child slot with the default
    // 100x100 centered RectTransform and squash everything inside it. - xlinka
    protected UIBuilder SetupSection(UIBuilder ui, string title, bool backButton = true)
    {
        var page = ui.Current;
        var vLayout = page.AttachComponent<VerticalLayout>();
        vLayout.Spacing.Value = 8f;
        vLayout.PaddingLeft.Value = GridPadding;
        vLayout.PaddingRight.Value = GridPadding;
        vLayout.PaddingTop.Value = GridPadding;
        vLayout.PaddingBottom.Value = GridPadding;
        vLayout.ForceExpandWidth.Value = true;
        vLayout.ForceExpandHeight.Value = false;

        if (backButton)
        {
            var headerRow = page.AddSlot("BackRow");
            headerRow.AttachComponent<RectTransform>();
            var hLE = headerRow.AttachComponent<LayoutElement>();
            hLE.MinHeight.Value = HeaderRowHeight;
            hLE.PreferredHeight.Value = HeaderRowHeight;
            var hLayout = headerRow.AttachComponent<HorizontalLayout>();
            hLayout.Spacing.Value = 8f;
            hLayout.ForceExpandWidth.Value = false;
            hLayout.ForceExpandHeight.Value = true;

            AddBackButton(headerRow);

            var promptSlot = headerRow.AddSlot("Prompt");
            promptSlot.AttachComponent<RectTransform>();
            var pLE = promptSlot.AttachComponent<LayoutElement>();
            pLE.FlexibleWidth.Value = 1f;
            pLE.MinHeight.Value = HeaderRowHeight;
            pLE.PreferredHeight.Value = HeaderRowHeight;
            var pBuilder = new UIBuilder(promptSlot);
            if (_fontProvider != null) pBuilder.Font(_fontProvider);
            var pText = pBuilder.Text(title, 16f, SectionTitleColor);
            pText.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
            pText.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            FillRect(pText.RectTransform!);
        }
        else
        {
            var promptRow = page.AddSlot("Prompt");
            promptRow.AttachComponent<RectTransform>();
            var pLE = promptRow.AttachComponent<LayoutElement>();
            pLE.MinHeight.Value = HeaderRowHeight;
            pLE.PreferredHeight.Value = HeaderRowHeight;
            var pBuilder = new UIBuilder(promptRow);
            if (_fontProvider != null) pBuilder.Font(_fontProvider);
            var pText = pBuilder.Text(title, 16f, SectionTitleColor);
            pText.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
            pText.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            FillRect(pText.RectTransform!);
        }

        var body = page.AddSlot("Body");
        body.AttachComponent<RectTransform>();
        var bLE = body.AttachComponent<LayoutElement>();
        bLE.FlexibleHeight.Value = 1f;
        bLE.MinHeight.Value = 200f;

        // Body MUST carry its own layout controller. Without one, a fresh RectTransform is a 100x100
        // box centred in its parent, so every row a dialog added landed on top of every other row in
        // the middle of the panel - which is exactly how all of these looked. The page's own
        // VerticalLayout only arranges the header and this Body slot; it does not reach inside. -xlinka
        var bodyLayout = body.AttachComponent<VerticalLayout>();
        bodyLayout.Spacing.Value = 6f;
        bodyLayout.PaddingLeft.Value = GridPadding;
        bodyLayout.PaddingRight.Value = GridPadding;
        bodyLayout.PaddingTop.Value = 4f;
        bodyLayout.PaddingBottom.Value = 4f;
        bodyLayout.ForceExpandWidth.Value = true;
        bodyLayout.ForceExpandHeight.Value = false;

        var bodyBuilder = new UIBuilder(body);
        if (_fontProvider != null) bodyBuilder.Font(_fontProvider);
        bodyBuilder.FontSize(13f);
        return bodyBuilder;
    }

    // The button grid gets its OWN row under the body, and the builder is pointed at it so existing
    // `SetupGrid(body); GridButton(body, ...)` call sites keep working unchanged.
    //
    // It used to attach a GridLayout to the body slot itself, which meant the grid then arranged every
    // piece of content the dialog had already added as though it were a button - text, spacers and all -
    // packing them into three columns on top of each other. -xlinka
    protected void SetupGrid(UIBuilder ui)
    {
        var row = ui.Current.AddSlot("Buttons");
        row.AttachComponent<RectTransform>();

        _gridRowElement = row.AttachComponent<LayoutElement>();
        _gridRowElement.FlexibleHeight.Value = 0f;
        _gridButtonCount = 0;
        GrowGridRow();

        var grid = row.AttachComponent<GridLayout>();
        grid.Columns.Value = GridItemsPerRow;
        grid.Spacing.Value = GridItemSpacing;
        grid.PaddingLeft.Value = GridPadding;
        grid.PaddingRight.Value = GridPadding;
        grid.PaddingTop.Value = GridPadding;
        grid.PaddingBottom.Value = GridPadding;

        ui.NestInto(row);
    }

    private LayoutElement? _gridRowElement;
    private int _gridButtonCount;

    // The row has to be tall enough for however many buttons actually get added, which is only known
    // as they arrive - a fixed height clips the second row the moment a dialog has four buttons.
    private void GrowGridRow()
    {
        if (_gridRowElement == null)
            return;

        int rows = System.Math.Max(1, (_gridButtonCount + GridItemsPerRow - 1) / GridItemsPerRow);
        float height = rows * ButtonHeight + (rows - 1) * GridItemSpacing + GridPadding * 2f;
        _gridRowElement.MinHeight.Value = height;
        _gridRowElement.PreferredHeight.Value = height;
    }

    protected void SetupCheckbox(UIBuilder ui, Sync<bool> field, string label)
    {
        var row = ui.Current.AddSlot(label);
        row.AttachComponent<RectTransform>();
        var rLE = row.AttachComponent<LayoutElement>();
        rLE.MinHeight.Value = 28f;
        rLE.PreferredHeight.Value = 28f;
        var rLayout = row.AttachComponent<HorizontalLayout>();
        rLayout.Spacing.Value = 8f;
        rLayout.PaddingLeft.Value = 4f;
        rLayout.PaddingRight.Value = 4f;

        var boxBuilder = new UIBuilder(row);
        if (_fontProvider != null) boxBuilder.Font(_fontProvider);
        boxBuilder.PushStyle().MinWidth(28f).PreferredWidth(28f).FlexibleWidth(0f);
        boxBuilder.Checkbox(field.Value, (_, v) => { if (CanInteract) field.Value = v; });
        boxBuilder.PopStyle();

        boxBuilder.PushStyle().FlexibleWidth(1f);
        var t = boxBuilder.Text(label, 13f, ButtonText);
        t.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        t.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        FillRect(t.RectTransform!);
        boxBuilder.PopStyle();
    }

    // Build a button styled like a file browser card - BorderedImage background
    // with rounded corners, centered label. - xlinka
    protected void GridButton(UIBuilder ui, string label, Action onClick, color? tint = null)
    {
        _gridButtonCount++;
        GrowGridRow();

        var slot = ui.Current.AddSlot(label);
        slot.AttachComponent<RectTransform>();
        var le = slot.AttachComponent<LayoutElement>();
        le.MinHeight.Value = ButtonHeight;
        le.PreferredHeight.Value = ButtonHeight;

        var bg = slot.AttachComponent<BorderedImage>();
        var fill = tint ?? ButtonFill;
        bg.Tint.Value = fill;
        bg.BorderTint.Value = BorderColor;
        bg.BorderThickness.Value = 2f;
        if (_rounded != null)
        {
            bg.Texture.Target = _rounded;
            bg.NineSlice.Value = true;
            bg.Borders.Value = new float4(12f, 12f, 12f, 12f);
        }

        var labelBuilder = new UIBuilder(slot);
        if (_fontProvider != null) labelBuilder.Font(_fontProvider);
        var text = labelBuilder.Text(label, 13f, ButtonText);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        text.WordWrap.Value = true;
        FillRect(text.RectTransform!, 6f, 6f, 4f, 4f);

        var button = slot.AttachComponent<Button>();
        button.Clicked += (_, _) => { if (CanInteract) onClick(); };
        button.AddColorDriver(bg.Tint, fill, InteractionColorMode.Direct);
    }

    // Honest "not supported yet" page: shows a message + a Close button that just
    // tears down the dialog. No content is spawned - we don't fake success for a
    // pipeline that doesn't exist. Call from a dialog whose format Lumora can't
    // import yet. - xlinka
    protected void ShowUnsupported(string message)
    {
        OpenPage(ui =>
        {
            var body = SetupSection(ui, "Not Supported Yet", backButton: false);
            body.PushStyle().FlexibleHeight(1f);
            var msg = body.Text(message, 14f, ButtonText);
            msg.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
            msg.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            msg.WordWrap.Value = true;
            body.PopStyle();

            body.PushStyle().MinHeight(36f).PreferredHeight(36f);
            body.Button("Close", (_, _) => { if (CanInteract) Slot.Destroy(); }, ButtonFill);
            body.PopStyle();
        });
    }

    private void AddBackButton(Slot parent)
    {
        var slot = parent.AddSlot("Back");
        slot.AttachComponent<RectTransform>();
        var le = slot.AttachComponent<LayoutElement>();
        le.MinWidth.Value = 64f;
        le.PreferredWidth.Value = 64f;
        le.FlexibleWidth.Value = 0f;

        var bg = slot.AttachComponent<BorderedImage>();
        bg.Tint.Value = BackColor;
        bg.BorderTint.Value = BorderColor;
        bg.BorderThickness.Value = 2f;
        if (_rounded != null)
        {
            bg.Texture.Target = _rounded;
            bg.NineSlice.Value = true;
            bg.Borders.Value = new float4(12f, 12f, 12f, 12f);
        }

        var b = new UIBuilder(slot);
        if (_fontProvider != null) b.Font(_fontProvider);
        var t = b.Text("Back", 13f, ButtonText);
        t.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        t.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        FillRect(t.RectTransform!);

        var button = slot.AttachComponent<Button>();
        button.Clicked += (_, _) => { if (CanInteract) _wizard?.Return(); };
        button.AddColorDriver(bg.Tint, BackColor, InteractionColorMode.Direct);
    }

    private static void FillRect(RectTransform rect, float left = 0f, float right = 0f, float top = 0f, float bottom = 0f)
    {
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = new float2(left, bottom);
        rect.OffsetMax.Value = new float2(-right, -top);
    }

    // raw-file fallback: route through registered handler if one is plugged in,
    // otherwise spawn a labeled grabbable as a visible placeholder. - xlinka
    public void AsRawFile()
    {
        if (!CanInteract) return;
        int count = Paths.Count;
        int rowSize = (int)MathF.Max(1f, MathF.Ceiling(MathF.Sqrt(count)));
        int index = 0;
        var basePos = Slot.GlobalPosition;
        var baseRot = Slot.GlobalRotation;
        float scale = 1f;

        var target = ResolveTargetWorld();
        foreach (var file in Paths)
        {
            var name = Path.GetFileName(file);
            var s = target.RootSlot.AddSlot(string.IsNullOrEmpty(name) ? file : name);
            var offset = UniversalImporter.GridOffset(ref index, rowSize) * scale;
            s.GlobalPosition = basePos + baseRot * offset;
            s.GlobalRotation = baseRot;
            s.GlobalScale = new float3(scale, scale, scale);

            var handler = ImportHandlers.Raw;
            if (handler != null)
            {
                var pathCaptured = file;
                _ = handler.ImportAsync(s, pathCaptured);
            }
            else
            {
                var label = s.AttachComponent<TextRenderer>();
                label.Text.Value = name;
                label.Size.Value = 0.08f;
                var grab = s.AttachComponent<Grabbable>();
                grab.AllowGrab.Value = true;
                grab.Scalable.Value = true;
            }
        }
        Slot.Destroy();
    }
}
