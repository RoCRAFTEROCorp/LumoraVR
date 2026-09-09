// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Math;

namespace Helio.UI;

// one per slot in a UI tree. anchored offsets relative to parent rect; the actual rects are
// computed by the Canvas (ComputeRects/ApplyLayout) each rebuild, and cached in LocalComputeRect.
[SingleInstancePerSlot]
[ComponentCategory("UI/Helio")]
public class RectTransform : Component
{
    [Flags]
    public enum DataModelFlag
    {
        None = 0,
        RectChanged = 1 << 0,
        LayoutChanged = 1 << 1,
        ComponentsChanged = 1 << 2,
        StructureChanged = 1 << 3,
        ChildrenOrderChanged = 1 << 4,
    }

    // anchors are in parent 0..1 space. offsets are in parent local units. - xlinka
    public readonly Sync<float2> AnchorMin;
    public readonly Sync<float2> AnchorMax;
    public readonly Sync<float2> OffsetMin;
    public readonly Sync<float2> OffsetMax;
    public readonly Sync<float2> Pivot;

    private Rect _localComputeRect;
    // Aggregated bounds: the union of this rect with every active descendant's bounds, computed bottom-up by
    // the canvas AFTER the layout pass (see UpdateBounds). Used to size an optional canvas collider and to
    // cheaply reject rays that miss all content. -xlinka
    private Rect _boundingRect;
    private DataModelFlag _dataModelFlags;
    // Cached-layout tracking. _layoutSelfDirty = this rect's own layout inputs changed; _descendantLayoutDirty
    // = something below changed (bubbled up). Both start true so the first pass computes everything; the
    // layout pass then skips any clean subtree (neither set) whose recomputed rect matches the cached one. -xlinka
    private bool _layoutSelfDirty = true;
    private bool _descendantLayoutDirty = true;
    private RectTransform? _rectParent;
    private readonly List<RectTransform> _rectChildren = new();
    private Canvas? _registeredCanvas;
    // Bottom-up measured metrics (min/preferred/flexible per axis), populated by the canvas MEASURE pass
    // before the top-down arrange so flexible/preferred sizing propagates through nested containers
    // (without this, a flexible child inside a plain container collapses to 0). -xlinka
    private LayoutMetrics _measuredHorizontal;
    private LayoutMetrics _measuredVertical;
    private bool _metricsValid;
    // PER-AXIS measure track, PARALLEL to the combined _layoutSelfDirty/_descendantLayoutDirty above (which
    // is left untouched - it still gates the whole-rect skip + arrange). These say which axis's cached metric
    // is stale, so the measure pass can re-measure just the changed axis and reuse the clean one. Init true so
    // the first pass measures both. A change sets BOTH unless it provably touches one axis. -xlinka
    private bool _metricsDirtyH = true;
    private bool _metricsDirtyV = true;

    public RectTransform()
    {
        AnchorMin = new Sync<float2>(this, new float2(0.5f, 0.5f));
        AnchorMax = new Sync<float2>(this, new float2(0.5f, 0.5f));
        OffsetMin = new Sync<float2>(this, new float2(-50f, -50f));
        OffsetMax = new Sync<float2>(this, new float2(50f, 50f));
        Pivot = new Sync<float2>(this, new float2(0.5f, 0.5f));
    }

    public Rect LocalComputeRect => _localComputeRect;
    public Rect BoundingRect => _boundingRect;
    public Canvas? Canvas => _registeredCanvas;
    public RectTransform? RectParent => _rectParent;
    public IReadOnlyList<RectTransform> RectChildren => _rectChildren;
    public int ChildrenCount => _rectChildren.Count;

    // Bottom-up bounds aggregation: union of this rect with every active child's already-aggregated bounds.
    // Called by the canvas AFTER ComputeRects so LocalComputeRect + RectChildren are final for every rect
    // (calling it earlier would aggregate the 100x100 pre-layout default). Helio renders nested chunks in
    // place (no per-chunk render-translate), so there's no offset to fold in. -xlinka
    internal void UpdateBounds()
    {
        var bounds = _localComputeRect;
        for (int i = 0; i < _rectChildren.Count; i++)
        {
            var child = _rectChildren[i];
            if (!child.Slot.ActiveSelf.Value) continue;
            child.UpdateBounds();
            bounds = bounds.Encapsulate(child._boundingRect);
        }
        _boundingRect = bounds;
    }

    // A position/size change (anchor/offset/pivot). Scoped: only this rect's chunk subtree needs
    // re-laying-out (e.g. a slider handle moving), not the whole canvas.
    public new void MarkChangeDirty()
    {
        _dataModelFlags |= DataModelFlag.RectChanged | DataModelFlag.LayoutChanged;
        SignalLayoutDirty(scoped: true);
        // FAIL-SAFE: an anchor/offset/pivot move can resize EITHER axis, so both metrics are stale.
        BubbleMetricsDirty(true, true);
    }

    public void MarkInvalidateHorizontalLayout()
    {
        _dataModelFlags |= DataModelFlag.LayoutChanged;
        SignalLayoutDirty(scoped: true);
        BubbleMetricsDirty(true, false);
    }

    public void MarkInvalidateVerticalLayout()
    {
        _dataModelFlags |= DataModelFlag.LayoutChanged;
        SignalLayoutDirty(scoped: true);
        BubbleMetricsDirty(false, true);
    }

    // called by UIComputeComponents on this slot when enable/disable/attach/destroy happens - xlinka
    // Structure changes can resize this rect and reflow siblings, so a full LAYOUT recompute is
    // needed - but only the chunks that actually MOVE re-tessellate. MarkStructuralDirty marks the
    // changed chunk + root and lets the canvas re-mesh moved siblings only, instead of _fullDirty
    // re-meshing every chunk (which flashed the whole panel on any add/remove). -xlinka
    public void NotifyComponentsChanged()
    {
        _dataModelFlags |= DataModelFlag.ComponentsChanged;
        MarkLayoutSelfDirty();
        var canvas = ResolveCanvas();
        if (canvas != null)
            canvas.MarkStructuralDirty(this);
        else
            SignalLayoutDirty(scoped: false);
        BubbleMetricsDirty(true, true);
    }

    public override void OnChanges()
    {
        base.OnChanges();
        MarkChangeDirty();
    }

    public override void OnAwake()
    {
        base.OnAwake();
        // Showing/hiding a slot must re-render the canvas, so we dirty on active-state changes and
        // subscribe in OnAwake - NOT OnStart, which can run after the content is first shown and miss
        // it, leaving it blank until a tab swap. Slot.ActiveChanged
        // is EFFECTIVE - it fires both on this slot's own ActiveSelf and (via Slot's descendant
        // propagation) when an ANCESTOR flips, so content shown by reactivating any ancestor (a
        // screen, a menu, the dashboard's render rig) re-renders.
        if (Slot != null)
            Slot.ActiveChanged += OnSlotActiveChanged;
    }

    public override void OnDestroy()
    {
        if (Slot != null)
            Slot.ActiveChanged -= OnSlotActiveChanged;

        // Tell the PARENT the tree under it changed. NotifyComponentsChanged only ever fired on the rect
        // whose own components moved, so destroying a subtree marked the chunk that was going away and
        // told the surviving parent nothing: its layout never re-ran, the rows below kept the positions
        // they had while the removed ones were still there, and the panel stayed wrong until some other
        // edit forced a full pass. That is the inspector's "collapse a slot and it glitches until you
        // reopen the parent" - reopening attaches components to a LIVE rect, which finally marks it.
        // -xlinka
        var parent = _rectParent;
        if (parent != null && !parent.IsDestroyed)
            parent.NotifyComponentsChanged();

        base.OnDestroy();
    }

    private void OnSlotActiveChanged(Slot slot)
    {
        _dataModelFlags |= DataModelFlag.StructureChanged;
        MarkLayoutSelfDirty();
        BubbleMetricsDirty(true, true);
        var canvas = ResolveCanvas();
        if (canvas == null)
            return;

        // Showing/hiding a slot only reflows its SIBLINGS when a parent arranges it via a LayoutController.
        // A free-anchored leaf (a checkbox/radio dot, a hover overlay) reflows nothing, so it just needs its
        // own chunk re-meshed - not a full-canvas relayout + root rebuild (which churns, and used to drop,
        // the whole panel). Layout-participating slots take the safe full path. -xlinka
        if (ParticipatesInParentLayout())
            canvas.MarkVisibilityDirty();
        else
            canvas.MarkVisibilityDirty(this);
    }

    // True when a parent LayoutController arranges this slot, so showing/hiding it can move its siblings and
    // a layout pass is required. False for free-anchored slots, which can't reflow anything. -xlinka
    private bool ParticipatesInParentLayout()
    {
        var parent = Slot?.Parent;
        if (parent == null)
            return false;
        var layout = parent.GetComponent<LayoutController>();
        return layout != null && layout.Enabled.Value;
    }

    // A purely visual change to a graphic on this slot (tint/texture/etc.): re-mesh its chunk,
    // but no layout recompute is needed. Keeps hover/press tints off the layout path.
    public void MarkGraphicDirty()
    {
        _dataModelFlags |= DataModelFlag.RectChanged;
        ResolveCanvas()?.MarkDirty(this);
    }

    // scoped: re-lay-out only this rect's nearest built chunk subtree (cheap, for self-contained
    // changes like a slider handle); false: recompute the whole canvas layout (structure changes).
    private void SignalLayoutDirty(bool scoped)
    {
        MarkLayoutSelfDirty();
        var canvas = ResolveCanvas();
        if (canvas == null)
            return;
        if (scoped)
            canvas.MarkLayoutDirty(this);
        else
            canvas.MarkLayoutDirty();
    }

    private Canvas? ResolveCanvas()
    {
        if (_registeredCanvas != null)
            return _registeredCanvas;
        for (var s = Slot; s != null; s = s.Parent)
        {
            var canvas = s.GetComponent<Canvas>();
            if (canvas != null)
                return canvas;
        }
        return null;
    }

    // true if this rect or any descendant has a pending layout change (so the cached-layout pass
    // must not skip it)
    public bool LayoutSubtreeDirty => _layoutSelfDirty || _descendantLayoutDirty;

    internal void ClearLayoutDirty()
    {
        _layoutSelfDirty = false;
        _descendantLayoutDirty = false;
    }

    // Mark this rect's own layout dirty and bubble a "descendant changed" flag up the slot tree, so the
    // cached-layout pass won't skip any ancestor that contains us. Stops at the first ancestor already
    // flagged (keeps it amortized cheap). -xlinka
    private void MarkLayoutSelfDirty()
    {
        _layoutSelfDirty = true;
        for (var s = Slot?.Parent; s != null; s = s.Parent)
        {
            var rt = s.GetComponent<RectTransform>();
            if (rt == null)
                continue;
            if (rt._descendantLayoutDirty)
                break;
            rt._descendantLayoutDirty = true;
        }
    }

    // Mark this rect's measured metric(s) stale for the given axes and bubble that up the slot tree (a
    // parent's metric aggregates its children's, so a child change invalidates the parent's metric for that
    // axis). Separate from MarkLayoutSelfDirty so the combined dirty/reflow path is untouched. Early-stops at
    // an ancestor that already covers the requested axes. -xlinka
    private void BubbleMetricsDirty(bool h, bool v)
    {
        if (h) _metricsDirtyH = true;
        if (v) _metricsDirtyV = true;
        for (var s = Slot?.Parent; s != null; s = s.Parent)
        {
            var rt = s.GetComponent<RectTransform>();
            if (rt == null)
                continue;
            bool needH = h && !rt._metricsDirtyH;
            bool needV = v && !rt._metricsDirtyV;
            if (!needH && !needV)
                break;
            if (needH) rt._metricsDirtyH = true;
            if (needV) rt._metricsDirtyV = true;
        }
    }

    // true if this rect's cached measured metric for the axis is stale and must be re-measured
    public bool MetricsDirty(LayoutDirection direction)
        => direction == LayoutDirection.Horizontal ? _metricsDirtyH : _metricsDirtyV;

    // true once the canvas measure pass has cached this rect's metrics
    public bool MetricsValid => _metricsValid;

    // bottom-up measured metrics for an axis, valid only after the measure pass (see MetricsValid)
    public LayoutMetrics GetMeasuredMetrics(LayoutDirection direction)
        => direction == LayoutDirection.Horizontal ? _measuredHorizontal : _measuredVertical;

    internal void SetMeasuredMetrics(LayoutDirection direction, in LayoutMetrics metrics)
    {
        if (direction == LayoutDirection.Horizontal)
        {
            _measuredHorizontal = metrics;
            _metricsDirtyH = false;
        }
        else
        {
            _measuredVertical = metrics;
            _metricsDirtyV = false;
        }
        _metricsValid = true;
    }

    // Chunk geometry is canvas-absolute and bakes against this rect: a layout pass that moves a
    // graphic inside an already-built chunk must re-mesh that chunk, or it keeps stale geometry
    // until an unrelated dirty rebuilds it (the render-only-after-hover bug - ChunkMoved only
    // watches the chunk ROOT's rect, so inner moves were invisible). Equality-gated so idle passes
    // and scrolling (a render offset, never a rect write) cost nothing. -xlinka
    internal void SetLocalComputeRect(in Rect rect)
    {
        if (_localComputeRect.Equals(rect))
            return;
        _localComputeRect = rect;
        _registeredCanvas?.MarkDirty(this);
    }
    internal void SetRegisteredCanvas(Canvas? canvas) => _registeredCanvas = canvas;
    internal void SetRectParent(RectTransform? parent) => _rectParent = parent;
    internal void AddRectChild(RectTransform child) => _rectChildren.Add(child);
    internal void RemoveRectChild(RectTransform child) => _rectChildren.Remove(child);
    internal void ClearRectChildren() => _rectChildren.Clear();
    internal DataModelFlag DataModelFlags => _dataModelFlags;
    internal void ClearDataModelFlags(DataModelFlag mask) => _dataModelFlags &= ~mask;
}
