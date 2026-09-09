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
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

[ComponentCategory("Hidden")]
public sealed class FileBrowserScreen : DashboardScreen, IDashboardKeyInput
{
    private const int GridColumns = 8;
    // A square-ish tile: the picture area on top (a folder shape, or a badge with the file's type),
    // the name under it.
    private const float CardHeight = 112f;
    private const float TileArtHeight = 62f;
    private const float CardSpacing = 6f;
    private const float ContentPad = 6f;
    // Extra rows built above/below the viewport so a fast scroll doesn't flash a blank row before the recycle
    // catches up. Higher = smoother but more live draw surfaces. - xlinka
    private const int BufferRows = 2;

    // Cards are tinted by file type and those tints are translucent washes over the viewport, so the
    // folder tint keeps its alpha: opaque amber turns a folder card into a road sign. -xlinka
    private static readonly color FolderColor =
        new color(DashTheme.Warning.r, DashTheme.Warning.g, DashTheme.Warning.b, 0.40f);
    private static readonly color FileColor = DashTheme.Surface;
    private static readonly color RowBorder = DashTheme.Outline;
    private static readonly color ViewportFill = DashTheme.Field;
    private static readonly color BarFill = DashTheme.Surface;
    private static readonly color ToolFill = DashTheme.Surface;
    private static readonly color TextPrimary = DashTheme.Text;
    private static readonly color TextDim = DashTheme.TextDim;
    private static readonly color ScrollHandleColor = DashTheme.Accent;

    private const float LaidOutViewportFloor = 100f;

    private const double DoublePressSeconds = 0.45;

    private string _currentPath = string.Empty;
    private string? _selectedPath;
    private string _search = string.Empty;
    private DateTime _lastPress;
    private string? _lastPressPath;
    private Text? _pathLabel;
    private Text? _statusLabel;
    private Text? _searchLabel;
    private Slot? _contentSlot;
    private RectTransform? _contentRect;
    private Dashboard? _dashboard;

    private ScrollRect? _scroll;
    private RectTransform? _viewportRect;
    private Slot? _scrollTrack;
    private RectTransform? _scrollHandle;
    private float _handlePressY;
    private float _handlePressScroll;
    private int _scrollHandleRetries;

    private bool _newFolderActive;
    private string _newFolderName = string.Empty;
    private Slot? _newFolderSlot;
    private Text? _newFolderNameText;
    private Text? _newFolderErrorText;
    private readonly List<Entry> _entries = new();
    private readonly List<Entry> _filtered = new();

    // Virtualized card pool: we only build enough card visuals to cover the visible viewport (plus a small
    // buffer) and recycle them as you scroll, instead of one card per file. A folder with hundreds of files
    // used to build a card each = hundreds of draw surfaces that blew past the render-priority band and cost
    // ~380ms to build in one frame. Pool slot for entry E is E % _pool.Count, so scrolling one row only
    // re-binds the row that wrapped around instead of every visible card. - xlinka
    private readonly List<CardView> _pool = new();
    private int _boundFirstRow = -1;
    private float _lastCardW = -1f;
    private IAssetProvider<FontSet>? _font;
    private RoundedRectTextureProvider? _rounded;

    private readonly struct Entry
    {
        public readonly string Path;
        public readonly string Name;
        public readonly string NameLower;
        public readonly bool IsDirectory;
        public Entry(string path, bool isDirectory)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path) ?? path;
            NameLower = Name.ToLowerInvariant();
            IsDirectory = isDirectory;
        }
    }

    // One reusable card visual in the virtualized pool. EntryIndex is the filtered-list index it currently
    // shows (-1 = unbound/hidden); Path/IsDirectory/BaseColor are the live state the click handler and the
    // selection highlight read back off the recycled card. - xlinka
    private sealed class CardView
    {
        public Slot Slot = null!;
        public RectTransform Rect = null!;
        public BorderedImage Bg = null!;
        public Text Label = null!;
        public Slot FolderArt = null!;
        public Slot BadgeSlot = null!;
        public RoundedPanel Badge = null!;
        public Text BadgeLabel = null!;
        public string Path = string.Empty;
        public bool IsDirectory;
        public color BaseColor;
        public int EntryIndex = -1;
        public bool Active;
    }

    protected override void BuildContent(UIBuilder builder)
    {
        _dashboard = FindDashboard();
        var font = _dashboard?.Font.Target;
        var rounded = _dashboard?.RoundedSprite;
        _font = font;
        _rounded = rounded;

        var root = builder.Current;
        var v = root.AttachComponent<VerticalLayout>();
        v.Spacing.Value = 8f;
        v.PaddingLeft.Value = 12f;
        v.PaddingRight.Value = 12f;
        v.PaddingTop.Value = 12f;
        v.PaddingBottom.Value = 12f;
        v.ForceExpandWidth.Value = true;
        v.ForceExpandHeight.Value = false;

        BuildToolbar(root, font, rounded);
        BuildViewport(root, font, rounded);
        BuildStatusLine(root, font);
        BuildNewFolderModal(root, font, rounded);

        string initial;
        try { initial = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
        catch { initial = string.Empty; }
        if (string.IsNullOrEmpty(initial) || !Directory.Exists(initial))
            initial = Directory.GetCurrentDirectory();
        NavigateTo(initial);
    }

    private Dashboard? FindDashboard()
    {
        var s = Slot;
        while (s != null)
        {
            var d = s.GetComponent<Dashboard>();
            if (d != null) return d;
            s = s.Parent;
        }
        return null;
    }

    private void BuildToolbar(Slot root, IAssetProvider<FontSet>? font, RoundedRectTextureProvider? rounded)
    {
        var toolbar = root.AddSlot("Toolbar");
        toolbar.AttachComponent<RectTransform>();
        var le = toolbar.AttachComponent<LayoutElement>();
        le.MinHeight.Value = 40f;
        le.PreferredHeight.Value = 40f;
        var h = toolbar.AttachComponent<HorizontalLayout>();
        h.Spacing.Value = 6f;
        h.ForceExpandHeight.Value = true;
        h.ForceExpandWidth.Value = false;

        AddToolButton(toolbar, "Up", GoUp, font, rounded, 56f);

        var pathSlot = toolbar.AddSlot("Path");
        pathSlot.AttachComponent<RectTransform>();
        var pathLE = pathSlot.AttachComponent<LayoutElement>();
        pathLE.FlexibleWidth.Value = 1f;
        pathLE.MinHeight.Value = 40f;
        pathLE.PreferredHeight.Value = 40f;
        var pathBg = pathSlot.AttachComponent<BorderedImage>();
        ApplyRounded(pathBg, BarFill, RowBorder, rounded);
        var pathBuilder = new UIBuilder(pathSlot);
        pathBuilder.Font(font).FontSize(13f);
        _pathLabel = pathBuilder.Text(string.Empty, 13f, TextPrimary);
        _pathLabel.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        _pathLabel.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        FillRect(_pathLabel.RectTransform!, 14f, 14f, 0f, 0f);

        // The search well sits in the same row as everything else; typing anywhere on this screen
        // goes into it, so it needs no focus press.
        var well = toolbar.AddSlot("Search");
        well.AttachComponent<RectTransform>();
        var wellLE = well.AttachComponent<LayoutElement>();
        wellLE.MinWidth.Value = 200f;
        wellLE.PreferredWidth.Value = 200f;
        wellLE.MinHeight.Value = 40f;
        wellLE.PreferredHeight.Value = 40f;
        var wellBg = well.AttachComponent<BorderedImage>();
        ApplyRounded(wellBg, DashTheme.Field, RowBorder, rounded);
        var wb = new UIBuilder(well);
        wb.Font(font).FontSize(13f);
        _searchLabel = wb.Text("Type to search", 13f, TextDim);
        _searchLabel.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        _searchLabel.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        FillRect(_searchLabel.RectTransform!, 14f, 14f, 0f, 0f);

        AddToolButton(toolbar, "Refresh", Refresh, font, rounded, 84f);
        AddToolButton(toolbar, "+ Folder", OpenNewFolderDialog, font, rounded, 96f);
        AddToolButton(toolbar, "Open folder", OpenFolderHere, font, rounded, 110f);
    }

    private void BuildViewport(Slot root, IAssetProvider<FontSet>? font, RoundedRectTextureProvider? rounded)
    {
        // Viewport + scrollbar sit side by side. The scrollbar's handle-drag sets the scroll position
        // directly (the file grid is a wall of card buttons, so grabbing empty space to drag-scroll or
        // relying on the wheel alone is unreliable - every other scrolling dash screen has one). -xlinka
        var area = root.AddSlot("ViewportArea");
        area.AttachComponent<RectTransform>();
        var areaLE = area.AttachComponent<LayoutElement>();
        areaLE.FlexibleHeight.Value = 1f;
        areaLE.MinHeight.Value = 200f;
        var areaLayout = area.AttachComponent<HorizontalLayout>();
        areaLayout.Spacing.Value = 6f;
        areaLayout.ForceExpandWidth.Value = false;
        areaLayout.ForceExpandHeight.Value = true;

        var viewport = area.AddSlot("Viewport");
        _viewportRect = viewport.AttachComponent<RectTransform>();
        var le = viewport.AttachComponent<LayoutElement>();
        le.FlexibleWidth.Value = 1f;
        le.FlexibleHeight.Value = 1f;

        var viewportBg = viewport.AttachComponent<BorderedImage>();
        ApplyRounded(viewportBg, ViewportFill, RowBorder, rounded);

        viewport.AttachComponent<Mask>();
        _scroll = viewport.AttachComponent<ScrollRect>();
        _scroll.ScrollSensitivity.Value = new float2(1f, 1f);
        _scroll.ScrollChanged += (_, _) => OnScrollChanged();

        _contentSlot = viewport.AddSlot("Content");
        _contentRect = _contentSlot.AttachComponent<RectTransform>();
        _contentRect.AnchorMin.Value = new float2(0f, 1f);
        _contentRect.AnchorMax.Value = new float2(1f, 1f);
        _contentRect.OffsetMin.Value = new float2(0f, -CardHeight);
        _contentRect.OffsetMax.Value = new float2(0f, 0f);

        // No GridLayout here on purpose: cards are virtualized and positioned manually by index (RelayoutVirtual),
        // so an auto-layout that walks every child every frame would defeat the whole point. - xlinka

        _scroll.Content.Target = _contentRect;

        BuildScrollbar(area, rounded);

        _ = font;
    }

    private void BuildScrollbar(Slot area, RoundedRectTextureProvider? rounded)
    {
        var track = area.AddSlot("Scrollbar");
        _scrollTrack = track;
        track.AttachComponent<RectTransform>();
        // Own graphic chunk: the handle's position is rewritten every scroll frame (UpdateScrollHandle sets its
        // RectTransform offsets). Without a chunk boundary here that layout change finds no owning chunk and
        // escalates to a FULL canvas rebuild - re-tessellating the whole file grid every frame (the scroll
        // flicker + fps drop). With its own chunk the move re-meshes just this tiny track+handle. -xlinka
        track.AttachComponent<GraphicChunkRoot>();
        var trackLE = track.AttachComponent<LayoutElement>();
        trackLE.MinWidth.Value = 16f;
        trackLE.PreferredWidth.Value = 16f;
        trackLE.FlexibleWidth.Value = 0f;
        trackLE.FlexibleHeight.Value = 1f;
        var trackBg = track.AttachComponent<BorderedImage>();
        ApplyRounded(trackBg, BarFill, RowBorder, rounded);

        var handleSlot = track.AddSlot("Handle");
        _scrollHandle = handleSlot.AttachComponent<RectTransform>();
        _scrollHandle.AnchorMin.Value = new float2(0f, 1f);
        _scrollHandle.AnchorMax.Value = new float2(1f, 1f);
        _scrollHandle.OffsetMin.Value = new float2(2f, -60f);
        _scrollHandle.OffsetMax.Value = new float2(-2f, 0f);
        var handleBg = handleSlot.AttachComponent<BorderedImage>();
        ApplyRounded(handleBg, ScrollHandleColor, new color(0f, 0f, 0f, 0f), rounded);

        var interaction = handleSlot.AttachComponent<InteractionElement>();
        interaction.Pressed += OnHandlePress;
        interaction.Dragged += OnHandleDrag;
    }

    private void OnHandlePress(UIInteractionContext context)
    {
        _handlePressY = context.LocalPoint.y;
        _handlePressScroll = _scroll?.AbsolutePosition.y ?? 0f;
    }

    private void OnHandleDrag(UIInteractionContext context)
    {
        if (_scroll == null || _viewportRect == null || _contentRect == null)
            return;
        float viewportHeight = _viewportRect.LocalComputeRect.height;
        float contentHeight = _contentRect.LocalComputeRect.height;
        if (viewportHeight <= LaidOutViewportFloor || contentHeight <= 0f)
            return;
        float maxScroll = MathF.Max(0f, contentHeight - viewportHeight);
        if (maxScroll <= 0f)
            return;
        float handleHeight = MathF.Max(30f, viewportHeight * (viewportHeight / contentHeight));
        float travel = viewportHeight - handleHeight;
        if (travel <= 0f)
            return;
        // Dragging the handle down (local Y decreases) scrolls the content down.
        float deltaY = context.LocalPoint.y - _handlePressY;
        float scrolled = _handlePressScroll - deltaY * (maxScroll / travel);
        // Setting AbsolutePosition routes through ScrollRect -> ApplyScrollOffset (moves the content chunk +
        // requests a repaint) and fires ScrollChanged -> UpdateScrollHandle. No explicit canvas dirty: the old
        // MarkDirty() here was the parameterless FULL-rebuild, which re-tessellated the whole grid on every drag
        // frame (the drag flicker + fps drop). -xlinka
        _scroll.AbsolutePosition = new float2(0f, System.Math.Clamp(scrolled, 0f, maxScroll));
    }

    private void UpdateScrollHandle()
    {
        if (_scroll == null || _scrollTrack == null || _scrollHandle == null
            || _viewportRect == null || _contentRect == null)
            return;
        float viewportHeight = _viewportRect.LocalComputeRect.height;
        if (viewportHeight <= LaidOutViewportFloor)
        {
            if (_scrollHandleRetries++ < 30)
                World?.RunInUpdates(1, UpdateScrollHandle);
            return;
        }
        _scrollHandleRetries = 0;

        float contentHeight = _contentRect.LocalComputeRect.height;
        if (contentHeight <= 0f)
        {
            if (_scrollHandleRetries++ < 30)
                World?.RunInUpdates(1, UpdateScrollHandle);
            return;
        }

        float maxScroll = MathF.Max(0f, contentHeight - viewportHeight);
        // Gate the ActiveSelf writes: Sync.Value is NOT change-gated, so setting it to its current value still
        // fires a change -> the track participates in a layout, so that's MarkVisibilityDirty() (root re-mesh +
        // reconcile). UpdateScrollHandle runs every scroll frame, so an unconditional write here was a full-ish
        // rebuild per frame (the scroll flicker + fps drop). Only write on an actual transition. -xlinka
        if (maxScroll <= 0.5f)
        {
            if (_scrollTrack.ActiveSelf.Value)
                _scrollTrack.ActiveSelf.Value = false; // everything fits; hide the bar
            if (_scroll.NormalizedPosition.y != 0f)
                _scroll.NormalizedPosition = float2.Zero;
            return;
        }
        if (!_scrollTrack.ActiveSelf.Value)
            _scrollTrack.ActiveSelf.Value = true;
        if (_scroll.AbsolutePosition.y > maxScroll)
            _scroll.AbsolutePosition = new float2(0f, maxScroll);
        float handleHeight = MathF.Max(30f, viewportHeight * (viewportHeight / contentHeight));
        float fraction = System.Math.Clamp(_scroll.AbsolutePosition.y / maxScroll, 0f, 1f);
        float offset = fraction * (viewportHeight - handleHeight);
        _scrollHandle.OffsetMax.Value = new float2(-2f, -offset);
        _scrollHandle.OffsetMin.Value = new float2(2f, -(offset + handleHeight));
    }

    // A line of text under the grid: what is selected, or what went wrong. No controls live here;
    // the one row at the top holds every button.
    private void BuildStatusLine(Slot root, IAssetProvider<FontSet>? font)
    {
        var status = root.AddSlot("Status");
        status.AttachComponent<RectTransform>();
        var le = status.AttachComponent<LayoutElement>();
        le.MinHeight.Value = 24f;
        le.PreferredHeight.Value = 24f;
        var ib = new UIBuilder(status);
        ib.Font(font).FontSize(13f);
        _statusLabel = ib.Text("Press a file to select it, press it twice to import it.", 13f, TextDim);
        _statusLabel.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        _statusLabel.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        FillRect(_statusLabel.RectTransform!, 4f, 4f, 0f, 0f);
    }

    private static void AddToolButton(Slot parent, string label, Action onClick, IAssetProvider<FontSet>? font, RoundedRectTextureProvider? rounded, float width)
    {
        var slot = parent.AddSlot(label);
        slot.AttachComponent<RectTransform>();
        var le = slot.AttachComponent<LayoutElement>();
        if (width > 0f)
        {
            le.MinWidth.Value = width;
            le.PreferredWidth.Value = width;
        }
        else
        {
            le.MinWidth.Value = 80f;
            le.FlexibleWidth.Value = 1f;
        }
        le.MinHeight.Value = 40f;
        le.PreferredHeight.Value = 40f;
        var img = slot.AttachComponent<BorderedImage>();
        ApplyRounded(img, ToolFill, RowBorder, rounded);
        var b = new UIBuilder(slot);
        b.Font(font).FontSize(13f);
        var t = b.Text(label, 13f, TextPrimary);
        t.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        t.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        FillRect(t.RectTransform!, 0f, 0f, 0f, 0f);
        var btn = slot.AttachComponent<Button>();
        btn.Clicked += (_, _) => onClick();
        // DisabledColor too: the screen builds while its slot is inactive, and a driver's first Apply
        // in that state paints the disabled grey and never revisits it. -xlinka
        var toolDriver = btn.AddColorDriver(img.Tint, ToolFill, InteractionColorMode.Direct);
        toolDriver.HighlightColor.Value = DashTheme.SurfaceHover;
        toolDriver.PressedColor.Value = DashTheme.SurfacePressed;
        toolDriver.DisabledColor.Value = ToolFill;
        toolDriver.Apply();
    }

    private static void ApplyRounded(BorderedImage img, color tint, color border, RoundedRectTextureProvider? rounded)
    {
        img.Tint.Value = tint;
        img.BorderTint.Value = border;
        img.BorderThickness.Value = DashTheme.OutlineWidth;
        if (rounded != null)
        {
            img.Texture.Target = rounded;
            img.NineSlice.Value = true;
            img.Borders.Value = new float4(12f, 12f, 12f, 12f);
        }
    }

    private static void FillRect(RectTransform rect, float left, float right, float top, float bottom)
    {
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = new float2(left, bottom);
        rect.OffsetMax.Value = new float2(-right, -top);
    }

    private void NavigateTo(string path)
    {
        _currentPath = path;
        _selectedPath = null;
        SetSearch(string.Empty);
        if (_pathLabel != null) _pathLabel.Content.Value = path;
        if (_statusLabel != null)
        {
            _statusLabel.Content.Value = "Press a file to select it, press it twice to import it.";
            _statusLabel.Color.Value = TextDim;
        }
        Refresh();
    }

    public void SetSearch(string text)
    {
        text ??= string.Empty;
        if (_search == text) return;
        _search = text;
        if (_searchLabel != null)
        {
            if (text.Length == 0)
            {
                _searchLabel.Content.Value = "Type to search";
                _searchLabel.Color.Value = TextDim;
            }
            else
            {
                _searchLabel.Content.Value = text;
                _searchLabel.Color.Value = TextPrimary;
            }
        }
        ApplyFilter();
    }

    private void GoUp()
    {
        if (string.IsNullOrEmpty(_currentPath)) return;
        try
        {
            var parent = Path.GetDirectoryName(_currentPath);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                NavigateTo(parent);
        }
        catch { }
    }

    private void Refresh()
    {
        if (_contentSlot == null) return;

        _entries.Clear();

        string[] directories = Array.Empty<string>();
        string[] files = Array.Empty<string>();
        try
        {
            if (Directory.Exists(_currentPath))
            {
                directories = Directory.GetDirectories(_currentPath);
                files = Directory.GetFiles(_currentPath);
            }
        }
        catch (Exception ex)
        {
            if (_statusLabel != null)
                _statusLabel.Content.Value = "Error: " + ex.Message;
            SetContentHeight(0);
            return;
        }

        Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (var dir in directories) _entries.Add(new Entry(dir, isDirectory: true));
        foreach (var file in files) _entries.Add(new Entry(file, isDirectory: false));

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_contentSlot == null) return;

        _filtered.Clear();
        var filter = _search.ToLowerInvariant();
        foreach (var e in _entries)
        {
            if (filter.Length > 0 && !e.NameLower.Contains(filter))
                continue;
            _filtered.Add(e);
        }

        // Size the content to the FULL list so the scrollbar range is right, even though we only build the
        // handful of cards that are actually on screen.
        int rows = (_filtered.Count + GridColumns - 1) / GridColumns;
        if (rows < 1) rows = 1;
        SetContentHeight(rows * CardHeight + (rows - 1) * CardSpacing + ContentPad * 2f);

        // New listing: snap to the top and force a full re-bind (resets every pooled card so none keeps showing
        // a stale entry). Runs now if laid out; if not, OnUpdate picks it up (the pool starts empty so a fresh
        // screen binds cleanly without the force anyway).
        if (_scroll != null)
            _scroll.AbsolutePosition = float2.Zero;
        _boundFirstRow = -1;
        RelayoutVirtual(force: true);

        UpdateScrollHandle();
        World?.RunInUpdates(2, UpdateScrollHandle);
    }

    private void SetContentHeight(float height)
    {
        if (_contentRect == null) return;
        _contentRect.OffsetMin.Value = new float2(0f, -height);
        _contentRect.OffsetMax.Value = new float2(0f, 0f);
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        // Drive the virtualized layout from the frame loop so it binds reliably once the viewport lays out, and
        // keeps up with scroll/resize. Early-outs cheaply when nothing crossed a row boundary. - xlinka
        RelayoutVirtual(force: false);
    }

    private void OnScrollChanged()
    {
        UpdateScrollHandle();
        RelayoutVirtual(force: false);
    }

    // Rebuild the set of on-screen cards for the current scroll position. Early-outs when the scroll hasn't
    // crossed a row boundary; when it has, the ring-buffer mapping (entry E -> pool slot E % poolCount) means
    // only the row that wrapped around gets re-bound, not every visible card. - xlinka
    private void RelayoutVirtual(bool force)
    {
        if (_contentSlot == null || _contentRect == null || _viewportRect == null || _scroll == null)
            return;

        float viewportH = _viewportRect.LocalComputeRect.height;
        float contentW = _contentRect.LocalComputeRect.width;
        if (viewportH <= LaidOutViewportFloor || contentW <= 0f)
        {
            // Not laid out yet (fresh screen / just navigated). Don't retry-spam here - OnUpdate re-drives this
            // every frame and binds the moment the rects come up. A self-scheduling retry raced the first-open
            // layout hitch and left the grid permanently empty. - xlinka
            return;
        }

        int cols = GridColumns;
        float rowStride = CardHeight + CardSpacing;
        int visibleRows = (int)MathF.Ceiling(viewportH / rowStride) + BufferRows;
        int poolCount = System.Math.Max(1, visibleRows * cols);
        EnsurePool(poolCount);

        float cardW = (contentW - 2f * ContentPad - (cols - 1) * CardSpacing) / cols;
        if (cardW <= 0f)
            return;

        // Width change (panel resized) moves every card, so force a full re-bind.
        if (MathF.Abs(cardW - _lastCardW) > 0.5f)
        {
            force = true;
            _lastCardW = cardW;
        }

        float scrollY = _scroll.AbsolutePosition.y;
        int firstRow = (int)MathF.Floor((scrollY - ContentPad) / rowStride);
        if (firstRow < 0) firstRow = 0;

        if (!force && firstRow == _boundFirstRow)
            return;
        if (force)
        {
            for (int i = 0; i < _pool.Count; i++)
                _pool[i].EntryIndex = -1;
        }
        _boundFirstRow = firstRow;

        int startEntry = firstRow * cols;
        int endEntry = System.Math.Min(_filtered.Count, (firstRow + visibleRows) * cols);

        for (int e = startEntry; e < endEntry; e++)
        {
            var card = _pool[e % poolCount];
            if (card.EntryIndex == e && card.Active)
                continue;
            BindCard(card, _filtered[e], e, e / cols, e % cols, cardW);
        }

        // Hide anything that fell outside the window (only bites at the list ends; a steady scroll reuses all).
        for (int i = 0; i < _pool.Count; i++)
        {
            var card = _pool[i];
            if (card.Active && (card.EntryIndex < startEntry || card.EntryIndex >= endEntry))
            {
                if (card.Slot.ActiveSelf.Value) card.Slot.ActiveSelf.Value = false;
                card.Active = false;
            }
        }
    }

    private void EnsurePool(int count)
    {
        if (_contentSlot == null) return;
        while (_pool.Count < count)
        {
            var card = new CardView();
            var slot = _contentSlot.AddSlot("Card");
            slot.ActiveSelf.Value = false;
            card.Slot = slot;

            card.Rect = slot.AttachComponent<RectTransform>();
            // Anchor to the content's top-left corner; BindCard places each card by pixel offset from there.
            card.Rect.AnchorMin.Value = new float2(0f, 1f);
            card.Rect.AnchorMax.Value = new float2(0f, 1f);

            card.Bg = slot.AttachComponent<BorderedImage>();
            ApplyRounded(card.Bg, FileColor, RowBorder, _rounded);

            // The folder shape, the same one the inventory draws: a tab over a body in the folder tint.
            card.FolderArt = slot.AddSlot("Folder");
            var folderRect = card.FolderArt.AttachComponent<RectTransform>();
            folderRect.AnchorMin.Value = new float2(0.5f, 1f);
            folderRect.AnchorMax.Value = new float2(0.5f, 1f);
            folderRect.OffsetMin.Value = new float2(-26f, -TileArtHeight + 10f);
            folderRect.OffsetMax.Value = new float2(26f, -10f);
            var folderBody = card.FolderArt.AddSlot("Body");
            var bodyRect = folderBody.AttachComponent<RectTransform>();
            bodyRect.AnchorMin.Value = float2.Zero;
            bodyRect.AnchorMax.Value = float2.One;
            bodyRect.OffsetMin.Value = float2.Zero;
            bodyRect.OffsetMax.Value = new float2(0f, -9f);
            BrowserParts.Panel(folderBody, new color(FolderColor.r, FolderColor.g, FolderColor.b, 0.85f), 5f);
            var folderTab = card.FolderArt.AddSlot("Tab");
            var tabRect = folderTab.AttachComponent<RectTransform>();
            tabRect.AnchorMin.Value = new float2(0f, 1f);
            tabRect.AnchorMax.Value = new float2(0f, 1f);
            tabRect.OffsetMin.Value = new float2(0f, -9f);
            tabRect.OffsetMax.Value = new float2(22f, 0f);
            BrowserParts.Panel(folderTab, FolderColor, 3f);

            // A file's badge: its type, on the class colour the old flat cards were washed with.
            card.BadgeSlot = slot.AddSlot("Badge");
            var badgeRect = card.BadgeSlot.AttachComponent<RectTransform>();
            badgeRect.AnchorMin.Value = new float2(0.5f, 1f);
            badgeRect.AnchorMax.Value = new float2(0.5f, 1f);
            badgeRect.OffsetMin.Value = new float2(-28f, -TileArtHeight + 12f);
            badgeRect.OffsetMax.Value = new float2(28f, -12f);
            card.Badge = BrowserParts.Panel(card.BadgeSlot, FileColor, 8f);
            var badgeBuilder = new UIBuilder(card.BadgeSlot);
            badgeBuilder.Font(_font).FontSize(12f);
            card.BadgeLabel = badgeBuilder.Text(string.Empty, 12f, TextPrimary);
            card.BadgeLabel.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
            card.BadgeLabel.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            FillRect(card.BadgeLabel.RectTransform!, 2f, 2f, 2f, 2f);

            var nameSlot = slot.AddSlot("Name");
            var nameRect = nameSlot.AttachComponent<RectTransform>();
            nameRect.AnchorMin.Value = new float2(0f, 0f);
            nameRect.AnchorMax.Value = new float2(1f, 0f);
            nameRect.OffsetMin.Value = new float2(6f, 4f);
            nameRect.OffsetMax.Value = new float2(-6f, CardHeight - TileArtHeight - 2f);
            var nameBuilder = new UIBuilder(nameSlot);
            nameBuilder.Font(_font).FontSize(11f);
            card.Label = nameBuilder.Text(string.Empty, 11f, TextPrimary);
            card.Label.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
            card.Label.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            card.Label.WordWrap.Value = true;
            FillRect(card.Label.RectTransform!, 0f, 0f, 0f, 0f);

            // Handler reads the card's LIVE path/type, not a captured value, because the card is recycled to a
            // different entry as you scroll. - xlinka
            // A folder opens on one press. A file selects on one press and imports on a second press
            // within the double press window, the same rule the inventory uses. -xlinka
            var captured = card;
            var btn = slot.AttachComponent<Button>();
            btn.Clicked += (_, _) =>
            {
                if (captured.IsDirectory)
                {
                    NavigateTo(captured.Path);
                    return;
                }
                var now = DateTime.UtcNow;
                bool second = _lastPressPath != null
                    && string.Equals(_lastPressPath, captured.Path, StringComparison.OrdinalIgnoreCase)
                    && (now - _lastPress).TotalSeconds <= DoublePressSeconds;
                _lastPress = now;
                _lastPressPath = captured.Path;
                if (second)
                {
                    _lastPressPath = null;
                    Select(captured);
                    SpawnImport(captured.Path);
                    return;
                }
                Select(captured);
            };

            _pool.Add(card);
        }
    }

    private void BindCard(CardView card, in Entry entry, int entryIndex, int row, int col, float cardW)
    {
        card.EntryIndex = entryIndex;
        card.Path = entry.Path;
        card.IsDirectory = entry.IsDirectory;
        card.BaseColor = FileColor;
        card.Label.Content.Value = TruncateLabel(entry.Name);
        card.Bg.Tint.Value = entry.Path == _selectedPath ? Highlight(card.BaseColor) : card.BaseColor;

        bool folder = entry.IsDirectory;
        if (card.FolderArt.ActiveSelf.Value != folder)
            card.FolderArt.ActiveSelf.Value = folder;
        if (card.BadgeSlot.ActiveSelf.Value == folder)
            card.BadgeSlot.ActiveSelf.Value = !folder;
        if (!folder)
        {
            var wash = GetFileClassColor(entry.Path);
            bool known = wash.a < 0.99f;
            card.Badge.Color.Value = known ? new color(wash.r, wash.g, wash.b, 0.85f) : DashTheme.Field;
            card.BadgeLabel.Content.Value = TypeLabel(entry.Path);
            card.BadgeLabel.Color.Value = known ? DashTheme.Backdrop : TextDim;
        }

        float x = ContentPad + col * (cardW + CardSpacing);
        float y = ContentPad + row * (CardHeight + CardSpacing);
        card.Rect.OffsetMin.Value = new float2(x, -(y + CardHeight));
        card.Rect.OffsetMax.Value = new float2(x + cardW, -y);

        if (!card.Active)
        {
            card.Slot.ActiveSelf.Value = true;
            card.Active = true;
        }
    }

    private void Select(CardView card)
    {
        _selectedPath = card.Path;
        for (int i = 0; i < _pool.Count; i++)
        {
            var c = _pool[i];
            if (c.Active)
                c.Bg.Tint.Value = c.Path == _selectedPath ? Highlight(c.BaseColor) : c.BaseColor;
        }
        if (_statusLabel != null)
        {
            _statusLabel.Color.Value = TextPrimary;
            try
            {
                var info = new FileInfo(card.Path);
                _statusLabel.Content.Value = $"{Path.GetFileName(card.Path)}  ·  {FormatBytes(info.Length)}";
            }
            catch
            {
                _statusLabel.Content.Value = Path.GetFileName(card.Path) ?? card.Path;
            }
        }
    }

    private void OpenFolderHere()
    {
        if (string.IsNullOrEmpty(_currentPath)) return;
        SpawnImport(_currentPath);
    }

    // The world an import belongs in: the one the user is standing in, NOT this screen's own. A dash
    // screen lives in the userspace overlay world, so `World` here is that overlay - importing into it
    // put the model somewhere the session cannot see and nothing worked on it. Same property the
    // inventory spawn and the clipboard importer already use. -xlinka
    private static World? FocusedWorld => Lumora.Core.Engine.Current?.WorldManager?.FocusedWorld;

    private void SpawnImport(string path)
    {
        var world = FocusedWorld;
        if (world == null)
            return;

        var (position, rotation) = ResolveSpawnPose(world);
        Lumora.Core.Components.Import.UniversalImporter.Import(path, world, position, rotation);
    }

    private static (float3 position, floatQ rotation) ResolveSpawnPose(World world)
    {
        // The head in the world being imported INTO. Taking it off this screen's own world would be
        // a position in the overlay's coordinate space, which means nothing over there.
        var head = world.LocalUser?.Root?.HeadSlot;
        if (head != null)
        {
            // View direction is the head's -Z (float3.Backward). Using +Z put the dialog BEHIND the
            // user, so they'd turn around and see its back ("wrong way round"). - xlinka
            var fwd = head.GlobalRotation * float3.Backward;
            return (head.GlobalPosition + fwd * 0.75f, head.GlobalRotation);
        }
        // No local user yet: drop it at the world's own origin rather than at some overlay-space
        // coordinate that happens to parse as a position.
        var root = world.RootSlot;
        return (root?.GlobalPosition ?? float3.Zero, root?.GlobalRotation ?? floatQ.Identity);
    }

    private void BuildNewFolderModal(Slot root, IAssetProvider<FontSet>? font, RoundedRectTextureProvider? rounded)
    {
        var modal = root.AddSlot("NewFolderModal");
        var modalRect = modal.AttachComponent<RectTransform>();
        modalRect.AnchorMin.Value = float2.Zero;
        modalRect.AnchorMax.Value = float2.One;
        modalRect.OffsetMin.Value = float2.Zero;
        modalRect.OffsetMax.Value = float2.Zero;
        modal.AttachComponent<IgnoreLayout>();
        modal.ActiveSelf.Value = false;
        _newFolderSlot = modal;

        var backdrop = modal.AttachComponent<Image>();
        backdrop.Tint.Value = new color(0f, 0f, 0f, 0.78f);
        modal.AttachComponent<InteractionBlock>();

        var panel = modal.AddSlot("Panel");
        var panelRect = panel.AttachComponent<RectTransform>();
        panelRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        panelRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        panelRect.OffsetMin.Value = new float2(-180f, -110f);
        panelRect.OffsetMax.Value = new float2(180f, 110f);

        var panelBg = panel.AttachComponent<BorderedImage>();
        panelBg.Tint.Value = new color(0.10f, 0.09f, 0.16f, 1f);
        panelBg.BorderTint.Value = new color(0.62f, 0.56f, 0.92f, 0.95f);
        panelBg.BorderThickness.Value = DashTheme.OutlineWidth;
        if (rounded != null)
        {
            panelBg.Texture.Target = rounded;
            panelBg.NineSlice.Value = true;
            panelBg.Borders.Value = new float4(12f, 12f, 12f, 12f);
        }

        var layout = panel.AttachComponent<VerticalLayout>();
        layout.Spacing.Value = 10f;
        layout.PaddingLeft.Value = 18f;
        layout.PaddingRight.Value = 18f;
        layout.PaddingTop.Value = 18f;
        layout.PaddingBottom.Value = 18f;
        layout.ForceExpandWidth.Value = true;
        layout.ForceExpandHeight.Value = false;

        AddDialogRow(panel, "Title", 26f, b =>
        {
            var t = b.Text("Create new folder", 16f, TextPrimary);
            t.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
            t.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            FillRect(t.RectTransform!, 0f, 0f, 0f, 0f);
        }, font, 16f);

        var inputSlot = panel.AddSlot("Input");
        inputSlot.AttachComponent<RectTransform>();
        var inputLE = inputSlot.AttachComponent<LayoutElement>();
        inputLE.MinHeight.Value = 40f;
        inputLE.PreferredHeight.Value = 40f;
        var inputBg = inputSlot.AttachComponent<BorderedImage>();
        ApplyRounded(inputBg, BarFill, RowBorder, rounded);
        var inputBuilder = new UIBuilder(inputSlot);
        inputBuilder.Font(font).FontSize(14f);
        _newFolderNameText = inputBuilder.Text("Folder name…", 14f, TextDim);
        _newFolderNameText.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        _newFolderNameText.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        FillRect(_newFolderNameText.RectTransform!, 14f, 14f, 0f, 0f);

        AddDialogRow(panel, "Error", 18f, b =>
        {
            _newFolderErrorText = b.Text(string.Empty, 11f, new color(1f, 0.45f, 0.45f, 1f));
            _newFolderErrorText.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
            _newFolderErrorText.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            FillRect(_newFolderErrorText.RectTransform!, 0f, 0f, 0f, 0f);
        }, font, 11f);

        var spacer = panel.AddSlot("Spacer");
        spacer.AttachComponent<RectTransform>();
        spacer.AttachComponent<LayoutElement>().FlexibleHeight.Value = 1f;

        var btnRow = panel.AddSlot("Buttons");
        btnRow.AttachComponent<RectTransform>();
        var btnRowLE = btnRow.AttachComponent<LayoutElement>();
        btnRowLE.MinHeight.Value = 40f;
        btnRowLE.PreferredHeight.Value = 40f;
        var bh = btnRow.AttachComponent<HorizontalLayout>();
        bh.Spacing.Value = 8f;
        bh.ForceExpandWidth.Value = true;
        bh.ForceExpandHeight.Value = true;

        AddToolButton(btnRow, "Cancel", CloseNewFolderDialog, font, rounded, 0f);
        AddToolButton(btnRow, "Create", ConfirmNewFolder, font, rounded, 0f);
    }

    private static void AddDialogRow(Slot panel, string name, float height, Action<UIBuilder> build, IAssetProvider<FontSet>? font, float fontSize)
    {
        var row = panel.AddSlot(name);
        row.AttachComponent<RectTransform>();
        var le = row.AttachComponent<LayoutElement>();
        le.MinHeight.Value = height;
        le.PreferredHeight.Value = height;
        var b = new UIBuilder(row);
        b.Font(font).FontSize(fontSize);
        build(b);
    }

    private void OpenNewFolderDialog()
    {
        if (_newFolderSlot == null) return;
        _newFolderActive = true;
        _newFolderName = string.Empty;
        if (_newFolderErrorText != null) _newFolderErrorText.Content.Value = string.Empty;
        UpdateNewFolderUI();
        _newFolderSlot.ActiveSelf.Value = true;
    }

    private void CloseNewFolderDialog()
    {
        _newFolderActive = false;
        _newFolderName = string.Empty;
        if (_newFolderSlot != null) _newFolderSlot.ActiveSelf.Value = false;
    }

    private void ConfirmNewFolder()
    {
        var name = _newFolderName.Trim();
        if (string.IsNullOrWhiteSpace(name)) { ShowNewFolderError("Name cannot be empty."); return; }
        if (name.Length > 255) { ShowNewFolderError("Name is too long."); return; }
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            if (name.IndexOf(ch) >= 0) { ShowNewFolderError("Name has invalid characters."); return; }
        }
        try
        {
            var fullPath = Path.Combine(_currentPath, name);
            if (Directory.Exists(fullPath)) { ShowNewFolderError("Folder already exists."); return; }
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex)
        {
            ShowNewFolderError(ex.Message);
            return;
        }
        CloseNewFolderDialog();
        Refresh();
    }

    private void ShowNewFolderError(string msg)
    {
        if (_newFolderErrorText != null) _newFolderErrorText.Content.Value = msg;
    }

    private void UpdateNewFolderUI()
    {
        if (_newFolderNameText == null) return;
        if (_newFolderName.Length == 0)
        {
            _newFolderNameText.Content.Value = "Folder name…";
            _newFolderNameText.Color.Value = TextDim;
        }
        else
        {
            _newFolderNameText.Content.Value = _newFolderName;
            _newFolderNameText.Color.Value = TextPrimary;
        }
    }

    public bool ConsumeChar(char c)
    {
        if (!_newFolderActive) return false;
        if (char.IsControl(c)) return true;
        _newFolderName += c;
        UpdateNewFolderUI();
        return true;
    }

    public bool ConsumeBackspace()
    {
        if (!_newFolderActive) return false;
        if (_newFolderName.Length > 0)
            _newFolderName = _newFolderName.Substring(0, _newFolderName.Length - 1);
        UpdateNewFolderUI();
        return true;
    }

    public bool ConsumeEnter()
    {
        if (!_newFolderActive) return false;
        ConfirmNewFolder();
        return true;
    }

    public bool ConsumeEscape()
    {
        if (!_newFolderActive) return false;
        CloseNewFolderDialog();
        return true;
    }

    private static color Highlight(in color c)
    {
        return new color(
            c.r + (1f - c.r) * 0.35f,
            c.g + (1f - c.g) * 0.35f,
            c.b + (1f - c.b) * 0.35f,
            c.a + (1f - c.a) * 0.55f);
    }

    private static color GetFileClassColor(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".tga" or ".tif" or ".tiff"
                => new color(0.45f, 0.85f, 0.55f, 0.45f),
            ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a" or ".aac" or ".opus"
                => new color(0.85f, 0.55f, 0.95f, 0.45f),
            ".mp4" or ".mov" or ".avi" or ".webm" or ".mkv" or ".m4v" or ".wmv"
                => new color(0.95f, 0.45f, 0.55f, 0.45f),
            ".obj" or ".fbx" or ".glb" or ".gltf" or ".dae" or ".blend" or ".stl"
                => new color(0.95f, 0.62f, 0.25f, 0.45f),
            ".cs" or ".py" or ".js" or ".ts" or ".cpp" or ".c" or ".h" or ".hpp" or ".java" or ".go" or ".rs" or ".rb" or ".lua" or ".swift" or ".kt"
                => new color(0.45f, 0.65f, 0.95f, 0.45f),
            ".txt" or ".md" or ".rst" or ".log"
                => new color(0.85f, 0.85f, 0.95f, 0.45f),
            ".pdf" or ".doc" or ".docx" or ".rtf" or ".odt"
                => new color(0.95f, 0.75f, 0.55f, 0.45f),
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz"
                => new color(0.70f, 0.55f, 0.35f, 0.45f),
            ".exe" or ".bat" or ".sh" or ".msi" or ".cmd" or ".com" or ".app" or ".dll"
                => new color(0.95f, 0.40f, 0.40f, 0.45f),
            ".ttf" or ".otf" or ".woff" or ".woff2"
                => new color(0.95f, 0.85f, 0.45f, 0.45f),
            ".json" or ".xml" or ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg" or ".conf"
                => new color(0.55f, 0.85f, 0.85f, 0.45f),
            _ => FileColor,
        };
    }

    private static string TruncateLabel(string label)
    {
        const int Max = 30;
        if (label.Length <= Max) return label;
        return label.Substring(0, Max - 1) + "…";
    }

    // The badge's word: the extension in capitals, or FILE when there is none.
    private static string TypeLabel(string path)
    {
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || ext.Length < 2)
            return "FILE";
        ext = ext.Substring(1).ToUpperInvariant();
        return ext.Length > 5 ? ext.Substring(0, 5) : ext;
    }

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / (double)GB:0.0} GB";
        if (bytes >= MB) return $"{bytes / (double)MB:0.0} MB";
        if (bytes >= KB) return $"{bytes / (double)KB:0.0} KB";
        return $"{bytes} B";
    }
}
