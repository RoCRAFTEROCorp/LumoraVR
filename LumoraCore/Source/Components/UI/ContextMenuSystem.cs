// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Helio.UI;
using Lumora.Core.Components.Assets;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.UI;

// what the menu was opened against: the pointer that summoned it and the slot the laser was
// hitting at open time. item sources use this to add contextual actions (equip the avatar you
// point at, etc.).
public sealed class ContextMenuContext
{
    public Slot? Pointer;
    public Slot? Target;

    // which hand summoned the menu - enables stick flick-select
    public Input.Chirality? Side;
}

// The user's radial context menu. Owns the page stack, collects items from
// sources, and renders itself as an engine-side mesh UI - a LOCAL Helio canvas
// per peer (AddLocalSlot, never replicated/saved), hit by the interaction laser
// like any world canvas. Only the owning user builds/drives it (see Open/Close).
// There is no platform view layer.
// NOTE: a naive replicated visual (AddSlot) was tried so others could see the
// menu, but the lifecycle's Destroy is denied by the datamodel ownership gate
// (structural ownership under the user root isn't "strong" per-byte), so the old
// canvas couldn't be torn down and menus stacked. Making it visible to others
// needs a proper owned/bypassed lifecycle, not a slot-type flip. -xlinka
[ComponentCategory("UI/Context Menu")]
public class ContextMenuSystem : Component
{
    private const float CanvasScale = 0.001f;
    private const float CanvasSize = 460f;
    private const float ItemSize = 120f;
    private const float OpenDistance = 0.35f;

    public readonly Sync<bool> IsOpen = null!;

    // null when closed
    public ContextMenuPage? CurrentPage { get; private set; }

    // null when closed
    public ContextMenuContext? CurrentContext { get; private set; }

    public bool HasPageHistory => _pageStack.Count > 0;

    public event Action<ContextMenuPage>? MenuOpened;
    public event Action? MenuClosed;
    public event Action<ContextMenuPage>? PageChanged;

    private readonly Stack<ContextMenuPage> _pageStack = new();

    private Slot _menuRoot = null!;

    // null while closed
    public Slot? VisualRoot => _menuRoot != null && !_menuRoot.IsDestroyed ? _menuRoot : null;
    private Slot _canvasSlot = null!;
    private FontProvider _font = null!;

    // Flick select: deflect the summoning hand's stick toward an item, then
    // release to pick it.
    private const float FlickEngageThreshold = 0.65f;
    private const float FlickReleaseThreshold = 0.35f;
    private readonly List<(ContextMenuItem item, ArcSegment arc, color fill)> _builtItems = new();
    private ContextMenuItem? _flickItem;

    // Public API

    public void Toggle(ContextMenuContext? context = null)
    {
        if (IsOpen.Value) Close();
        else Open(context);
    }

    // collects items from RootContextMenuItem and ContextMenuItemSource components under this
    // slot's user hierarchy
    public void Open(ContextMenuContext? context = null)
    {
        // Only the owning user builds/drives their own menu. This component also runs on observers for a
        // REMOTE user's hand; without this gate the observer's own input could open a ghost menu on the
        // remote user's ContextMenuSystem. -xlinka
        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
        {
            return;
        }

        CurrentContext = context ?? new ContextMenuContext();
        _guardOpeningPress = false;  // radial menu opens on a non-select button; no opening-press guard needed
        _disableAutoClose = false;   // radial keeps its edge-close (point away to dismiss)
        var page = BuildRootPage(CurrentContext);
        if (page.Items.Count == 0)
        {
            LumoraLogger.Log("ContextMenuSystem: No items to show");
            CurrentContext = null;
            return;
        }

        page.LayoutItems();

        _pageStack.Clear();
        CurrentPage = page;
        IsOpen.Value = true;

        _openTime = 0f;
        PositionMenu(CurrentContext.Pointer);
        RebuildVisual();
        SuppressLocomotion();

        MenuOpened?.Invoke(page);
        LumoraLogger.Log($"ContextMenuSystem: Opened '{page.Title}' ({page.Items.Count} items)");
    }

    // Open a small two-button confirm menu (a confirm action + Cancel), bypassing the source-collected root
    // page. Used for the touch-to-equip confirmation popup. -xlinka
    public void OpenConfirm(string title, string confirmLabel, float[] confirmColor, Action onConfirm, ContextMenuContext? context = null)
    {
        var page = new ContextMenuPage(title);
        page.AddItem(new ContextMenuItem
        {
            Label = confirmLabel,
            FillColor = confirmColor,
            OnPressed = _ => { Close(); onConfirm?.Invoke(); },
        });
        page.AddItem(new ContextMenuItem
        {
            Label = "Cancel",
            FillColor = new[] { 0.30f, 0.30f, 0.32f, 0.92f },
            OnPressed = _ => Close(),
        });
        OpenPage(page, context);
    }

    // Open a prebuilt custom page, bypassing the source-collected root page. Item handlers are
    // responsible for calling Close themselves (so multi-step flows can PushPage instead).
    // Persistent: stays open until an item is picked rather than edge-closing when the pointer drifts. -xlinka
    public void OpenPage(ContextMenuPage page, ContextMenuContext? context = null, bool persistent = true)
    {
        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
            return;
        if (page == null || page.Items.Count == 0)
            return;

        // Carry the summoning hand's laser slot + side (like the radial menu does), or the edge-close ray-cast and
        // desktop mouse-aim have no laser to work with and the menu dismisses itself a frame after opening. -xlinka
        CurrentContext = context ?? new ContextMenuContext { Pointer = Slot };
        _guardOpeningPress = true;   // opened by the primary button - don't let its release select an item
        _openingPrimaryReleased = false;
        _disableAutoClose = persistent;
        page.LayoutItems();
        _pageStack.Clear();
        CurrentPage = page;
        IsOpen.Value = true;
        _openTime = 0f;
        PositionMenu(CurrentContext.Pointer);
        RebuildVisual();
        SuppressLocomotion();
        MenuOpened?.Invoke(page);
    }

    // PopPage goes back
    public void PushPage(ContextMenuPage page)
    {
        if (page == null || CurrentPage == null) return;

        _pageStack.Push(CurrentPage);
        CurrentPage = page;
        CurrentPage.LayoutItems();
        RebuildVisual();
        PageChanged?.Invoke(CurrentPage);
    }

    // closes the menu if already at the root page
    public void PopPage()
    {
        // Same guard as SelectItem: the press that OPENED the menu lands on the center "back" disc (the menu opens
        // under the laser), and an unguarded PopPage there closes the menu instantly. Ignore until that press is
        // released once. -xlinka
        if (_guardOpeningPress) return;

        if (_pageStack.Count == 0) { Close(); return; }

        CurrentPage = _pageStack.Pop();
        RebuildVisual();
        PageChanged?.Invoke(CurrentPage);
    }

    public void Close()
    {
        // Only the owner tears down their own menu - see Open. -xlinka
        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
        {
            return;
        }

        if (!IsOpen.Value) return;

        _pageStack.Clear();
        CurrentPage = null;
        CurrentContext = null;
        IsOpen.Value = false;

        ReleaseLocomotionSuppression();
        DestroyVisual();
        MenuClosed?.Invoke();
    }

    public void SelectItem(ContextMenuItem item)
    {
        if (item == null || !item.IsEnabled) return;

        // Ignore the click from the very press that OPENED this menu. The confirm opens on the primary (left/
        // trigger) button - the same button used to select - so without this the opening press's release clicks
        // an item and the menu closes instantly ("only stays while holding"). Selection arms once that press is
        // released (UpdateOpeningPressGuard). The radial menu opens on a different button, so it isn't guarded. -xlinka
        if (_guardOpeningPress) return;

        if (item.SubPage != null)
        {
            PushPage(item.SubPage);
        }
        else
        {
            var pressed = item.OnPressed;
            Close();
            pressed?.Invoke(item);
        }
    }

    public override void OnDestroy()
    {
        ReleaseLocomotionSuppression();
        DestroyVisual();
        base.OnDestroy();
    }

    // ── Flick select ────────────────────────────────────────────────────────

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        UpdateOpenAnimation(delta);
        UpdateOpeningPressGuard();
        UpdateFlickSelect();
        UpdateEdgeClose(delta);
    }

    // Open/page-change pop: the canvas scales 0.6 -> 1.0 over ~0.12s with a smoother-step ease. Scale only -
    // it's one slot-transform write per frame while animating, no re-mesh and no per-material alpha churn. -xlinka
    private float _openAnim = 1f;

    private void UpdateOpenAnimation(float delta)
    {
        if (_openAnim >= 1f || _canvasSlot == null || _canvasSlot.IsDestroyed)
            return;
        _openAnim = MathF.Min(1f, _openAnim + delta / 0.12f);
        float t = _openAnim;
        float ease = t * t * t * (t * (t * 6f - 15f) + 10f);
        _canvasSlot.LocalScale.Value = float3.One * (CanvasScale * (0.6f + 0.4f * ease));
    }

    // The confirm menu opens on the primary (left/trigger) button, which is also the select button. Ignore item
    // selection until that opening press is released once, or its release immediately clicks an item and closes
    // the menu. Cleared the frame AFTER release so the release-click itself stays guarded. -xlinka
    private bool _guardOpeningPress;
    private bool _openingPrimaryReleased;
    // Confirm menus are PERSISTENT: they stay open until a button is clicked (Equip/Cancel), with no auto-close
    // when the pointer wanders. The radial menu keeps its edge-close. -xlinka
    private bool _disableAutoClose;

    private void UpdateOpeningPressGuard()
    {
        if (!_guardOpeningPress) return;
        if (_openingPrimaryReleased)
        {
            _guardOpeningPress = false;
            return;
        }
        if (!OpeningHandPrimaryHeld())
            _openingPrimaryReleased = true;
    }

    private bool OpeningHandPrimaryHeld()
    {
        var side = CurrentContext?.Side;
        var userRoot = Slot?.ActiveUserRoot?.Slot;
        if (side == null || userRoot == null) return false;
        foreach (var hand in userRoot.GetComponentsInChildren<Lumora.Core.Components.Interaction.HandTool>())
            if (hand.Side.Value == side) return hand.PrimaryHeld;
        return false;
    }

    // Desktop selection: mouse look is frozen while the menu is open and the
    // mouse deflects the hand laser instead (HandTool.UpdateDesktopMenuAim),
    // so the laser cursor is the single pointer and hover/click flow through
    // the normal laser-canvas path.
    private bool _restoreMouseLook;

    // Close when the pointer wanders past the ring edge - moving the aim
    // well off the menu dismisses it without a button press.
    private const float EdgeCloseRadius = 320f;
    private float _openTime;

    private void UpdateEdgeClose(float delta)
    {
        if (!IsOpen.Value || _canvasSlot == null || _canvasSlot.IsDestroyed)
            return;

        // Persistent (confirm) menus never auto-close on pointer movement - only an item click closes them. -xlinka
        if (_disableAutoClose)
            return;

        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
            return;

        // Grace so the menu doesn't instantly dismiss while it settles.
        _openTime += delta;
        if (_openTime < 0.35f)
            return;

        Slot? rayOrigin = CurrentContext?.Pointer;
        if (rayOrigin == null || rayOrigin.IsDestroyed)
            rayOrigin = Slot?.ActiveUserRoot?.HeadSlot;
        if (rayOrigin == null || rayOrigin.IsDestroyed)
            return;

        // Use the laser's actual cast ray when available - on desktop the slot pose isn't the ray (head or
        // camera, plus the mouse deflection, is). The CAST ray specifically: under an external camera the
        // laser's exposed ray is re-anchored at the hand, and a hand standing metres off the view line
        // crosses this plane somewhere else entirely (often behind it, which reads as pointing away and
        // slams the menu shut the moment the aim leaves the ring). -xlinka
        float3 origin, direction;
        var laser = rayOrigin.GetComponent<Interaction.InteractionLaser>();
        if (laser != null)
        {
            origin = laser.CastOrigin;
            direction = laser.CastDirection;
        }
        else
        {
            origin = rayOrigin.GlobalPosition;
            direction = rayOrigin.GlobalRotation * float3.Backward;
        }

        // Intersect the aim ray with the menu plane; pointing away entirely
        // also counts as leaving.
        var planeNormal = _canvasSlot.GlobalRotation * float3.Forward;
        float denom = float3.Dot(direction, planeNormal);
        if (MathF.Abs(denom) < 1e-4f)
            return;

        float t = float3.Dot(_canvasSlot.GlobalPosition - origin, planeNormal) / denom;
        if (t < 0f)
        {
            Close();
            return;
        }

        var local = _canvasSlot.GlobalPointToLocal(origin + direction * t);
        float distance = MathF.Sqrt(local.x * local.x + local.y * local.y);
        if (distance > EdgeCloseRadius)
            Close();
    }

    // Deflect the summoning hand's stick toward an item, release to pick it.
    // Locomotion is suppressed while open so the gesture doesn't snap-turn.
    private void UpdateFlickSelect()
    {
        if (!IsOpen.Value || _builtItems.Count == 0)
            return;

        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
            return;

        var side = CurrentContext?.Side;
        if (side == null)
            return;

        var actions = Engine.Current?.InputInterface?.Actions;
        if (actions == null)
            return;

        var stick = actions.Interaction(side.Value).Stick.Value;
        float magnitude = MathF.Sqrt(stick.x * stick.x + stick.y * stick.y);

        if (magnitude >= FlickEngageThreshold)
        {
            // Stick up = top item: same clockwise-from-right angle convention
            // as the arc layout.
            float angle = MathF.Atan2(-stick.y, stick.x) * (180f / MathF.PI);
            SetFlickItem(FindItemAtAngle(angle));
        }
        else if (magnitude <= FlickReleaseThreshold && _flickItem != null)
        {
            var selected = _flickItem;
            SetFlickItem(null);
            SelectItem(selected);
        }
    }

    private ContextMenuItem? FindItemAtAngle(float angle)
    {
        foreach (var (item, _, _) in _builtItems)
        {
            if (!item.IsEnabled)
                continue;
            float relative = angle - item.AngleStart;
            relative -= MathF.Floor(relative / 360f) * 360f;
            if (relative <= item.ArcLength)
                return item;
        }
        return null;
    }

    private void SetFlickItem(ContextMenuItem? item)
    {
        if (ReferenceEquals(_flickItem, item))
            return;

        _flickItem = item;
        foreach (var (built, arc, fill) in _builtItems)
        {
            if (arc == null || arc.IsDestroyed)
                continue;
            bool highlighted = ReferenceEquals(built, item);
            arc.Tint.Value = highlighted
                ? new color(
                    MathF.Min(fill.r + 0.18f, 1f),
                    MathF.Min(fill.g + 0.18f, 1f),
                    MathF.Min(fill.b + 0.18f, 1f),
                    fill.a)
                : fill;
        }
    }

    private void SuppressLocomotion()
    {
        if (Slot?.ActiveUserRoot?.ActiveUser != World?.LocalUser)
            return;

        var state = UserInputState.ForFocusedLocalUser;
        if (state == null)
            return;

        if (Engine.Current?.InputInterface?.VR_Active == true)
        {
            // VR: the flick gesture shares the stick with locomotion.
            state.SetDesktopInputSuppressed(this, true);
        }
        else if (Engine.Current?.InputInterface?.DesktopExternalCameraAim == true)
        {
            // Third-person / free-cam: the mouse is steering the CAMERA, and the menu aim rides the camera
            // ray. Leaving it live would move the aim twice per mouse delta - once by orbiting/flying the
            // view, once by the menu's own deflection. Freeze the camera the way first person freezes head
            // look; WASD stays live either way. -xlinka
            state.SetDesktopInputSuppressed(this, true);
        }
        else if (!state.MouseLookSuppressed)
        {
            // Desktop: freeze the camera and hand the mouse to the menu
            // pointer. WASD stays live.
            state.SetMouseLookSuppressed(true);
            _restoreMouseLook = true;
        }
    }

    private void ReleaseLocomotionSuppression()
    {
        var state = UserInputState.ForFocusedLocalUser;
        state?.SetDesktopInputSuppressed(this, false);

        if (_restoreMouseLook)
        {
            state?.SetMouseLookSuppressed(false);
            _restoreMouseLook = false;
        }
    }

    // Visual (engine-side mesh UI, LOCAL per peer)

    private void PositionMenu(Slot? pointer)
    {
        EnsureVisualRoot();

        var head = Slot.ActiveUserRoot?.HeadSlot;
        var input = Engine.Current?.InputInterface;
        bool vrActive = input?.VR_Active == true;

        // F6 free-cam flies a drone camera away from the body and parks the head wherever mouse look last
        // left it, so the head is the wrong viewpoint the whole time it is up. `external` below is a flag
        // the PLATFORM publishes each frame off the head output's pose override (HeadOutput.
        // UpdateScreenPositioning -> InputInterface.SetDesktopCameraOverride); the free-cam flag is written
        // by the camera controller in the mode switch itself (DesktopCameraController.SwitchMode ->
        // UserInputState.SetFreeCamActive), so it cannot be a frame - or a missed publish - out of step with
        // the mode the user is actually flying. Either one is enough to anchor on the camera, because a
        // free-cam that falls through to the head branch opens the ring at the avatar's body, metres behind
        // the lens the user is looking through. -xlinka
        bool freeCam = !vrActive
            && input?.DesktopCameraPoseValid == true
            && UserInputState.FocusedFreeCamActive;

        // The viewpoint the menu opens in front of and turns to face. First person is the head; third-person
        // and free-cam are a camera flying somewhere else entirely, and putting the menu on the parked head
        // there would drop it off screen (or inside the avatar's skull) where nothing can click it. -xlinka
        bool external = freeCam || input?.DesktopExternalCameraAim == true;
        bool hasView = external || head != null;
        float3 viewPosition = external ? input!.DesktopCameraPosition : (head?.GlobalPosition ?? float3.Zero);
        floatQ viewRotation = external ? input!.DesktopCameraRotation : (head?.GlobalRotation ?? floatQ.Identity);

        // View/laser facing is -Z (rotation * Backward) - Slot.Forward (+Z)
        // points behind the camera and was putting the menu out of view.
        //
        // Desktop: directly in front of the active camera at half a meter, so it
        // always lands in view. VR: in front of the summoning pointer.
        // (FaceLocalUser turns it toward the viewer either way.)
        if (!vrActive && hasView)
        {
            // The drone camera is not the avatar and does not grow with it, so its stand-off distance must
            // not ride the user's scale: a 10x user would otherwise get the ring parked 5 m off the lens at
            // its fixed 0.46 m across. Avatar scale still drives the first-person/third-person placement,
            // where the viewpoint really is the (scaled) head. -xlinka
            float scale = freeCam ? 1f : (Slot.ActiveUserRoot?.GlobalScale ?? 1f);
            var viewDirection = viewRotation * float3.Backward;
            _menuRoot.GlobalPosition = viewPosition + viewDirection * (0.5f * scale);
        }
        else if (pointer != null && !pointer.IsDestroyed)
        {
            var pointDirection = pointer.GlobalRotation * float3.Backward;
            _menuRoot.GlobalPosition = pointer.GlobalPosition + pointDirection * OpenDistance;
        }
        else if (hasView)
        {
            var viewDirection = viewRotation * float3.Backward;
            _menuRoot.GlobalPosition = viewPosition + viewDirection * (OpenDistance + 0.15f);
        }

        // Face the viewpoint ONCE here (yaw billboard). The menu is a child of the user root, so after this
        // it rides along rigidly with no per-frame world re-derivation - locked in place when you move
        // (never re-faces or re-positions the menu per frame).
        if (freeCam)
        {
            // A drone camera spends most of its life pitched at the ground. The yaw billboard below keeps
            // the ring vertical, so a steep pitch shows it edge-on, and past ~89 degrees the flattened
            // direction falls under the epsilon and NO rotation is written at all - the ring silently keeps
            // whatever yaw the body happens to be standing at. Square it to the lens instead. Free-cam
            // composes yaw*pitch with no roll (DesktopCameraController.UpdateFreeCam), so camera-up is a
            // safe reference. -xlinka
            if (Utility.UserFacing.TryLookRotation(
                    _menuRoot.GlobalPosition, viewPosition, viewRotation * float3.Up, yawOnly: false, out var facing))
            {
                _menuRoot.GlobalRotation = facing;
            }
        }
        else if (hasView)
        {
            var toView = viewPosition - _menuRoot.GlobalPosition;
            toView.y = 0f;
            if (toView.LengthSquared > 1e-6f)
                _menuRoot.GlobalRotation = floatQ.AxisAngle(float3.Up, MathF.Atan2(toView.x, toView.z));
        }
    }

    private void EnsureVisualRoot()
    {
        if (_menuRoot != null && !_menuRoot.IsDestroyed)
            return;

        // The menu is a child of the user root, so it already moves + turns rigidly with you. Re-facing
        // it toward the head every frame (in world space) lagged the body translation by a frame and
        // made it jitter while moving. We face it once on open (in PositionMenu) and animate open/close
        // by scale only.
        _menuRoot = Slot.AddLocalSlot("ContextMenu");
        _font = _menuRoot.AttachComponent<FontProvider>();
        _font.URL.Value = new Uri("res://Assets/Fonts/FiraCode/FiraCode-SemiBold.ttf");
    }

    private void RebuildVisual()
    {
        EnsureVisualRoot();

        _builtItems.Clear();
        _flickItem = null;

        _canvasSlot?.Destroy();
        _canvasSlot = _menuRoot.AddLocalSlot("Canvas");
        // Start small; UpdateOpenAnimation eases to full size (also fires on page changes - each rebuild pops).
        _openAnim = 0f;
        _canvasSlot.LocalScale.Value = float3.One * (CanvasScale * 0.6f);

        // Canvas root size comes from a centered RectTransform on the canvas
        // slot (same convention as PanelShell).
        var rootRect = _canvasSlot.AttachComponent<RectTransform>();
        rootRect.OffsetMin.Value = new float2(-CanvasSize * 0.5f, -CanvasSize * 0.5f);
        rootRect.OffsetMax.Value = new float2(CanvasSize * 0.5f, CanvasSize * 0.5f);
        var canvas = _canvasSlot.AttachComponent<Canvas>();
        // The menu is overlay UI: it renders over everything. SortingOrder wins the transparent sort;
        // Overlay makes every canvas material skip the DEPTH test too - without it, world geometry still
        // clips into the menu (the "objects showing through the ring" render bug). -xlinka
        canvas.SortingOrder.Value = 10000;
        canvas.Overlay.Value = true;
        // Black outline + slight bolding on every label: floating text over an arbitrary world background
        // is unreadable without a contrast ring, and thin glyphs wash out on bright skies. -xlinka
        canvas.TextOutlineColor.Value = new colorHDR(0f, 0f, 0f, 1f);
        canvas.TextOutlineThickness.Value = 0.22f;
        canvas.TextFaceDilate.Value = 0.06f;

        var page = CurrentPage;
        if (page == null) return;

        BuildCenter(page);

        for (int i = 0; i < page.Items.Count; i++)
        {
            BuildItem(page.Items[i]);
        }

    }

    private void BuildCenter(ContextMenuPage page)
    {
        var center = _canvasSlot.AddLocalSlot("Center");
        var rect = center.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(-46f, -46f);
        rect.OffsetMax.Value = new float2(46f, 46f);

        // Round center disc, matching the ring (a full 360-degree arc), not a
        // square image plate.
        var disc = center.AttachComponent<ArcSegment>();
        disc.AngleStart.Value = 0f;
        disc.ArcLength.Value = 360f;
        disc.InnerRadius.Value = 0f;
        disc.OuterRadius.Value = 46f;
        disc.Tint.Value = new color(0.08f, 0.09f, 0.12f, 1f);
        disc.OutlineColor.Value = new color(0.30f, 0.32f, 0.40f, 0.95f);
        disc.OutlineThickness.Value = 2f;

        var button = center.AttachComponent<ArcButton>();
        button.Clicked += (_, _) => PopPage();

        var labelSlot = center.AddLocalSlot("Title");
        var labelRect = labelSlot.AttachComponent<RectTransform>();
        labelRect.AnchorMin.Value = float2.Zero;
        labelRect.AnchorMax.Value = float2.One;

        var text = labelSlot.AttachComponent<Text>();
        text.Content.Value = HasPageHistory ? "Back" : page.Title;
        text.Font.Target = _font;
        text.Size.Value = 22f;
        text.Color.Value = color.White;
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        text.WordWrap.Value = false;
    }

    private void BuildItem(ContextMenuItem item)
    {
        // Arc segment spanning the item's slice of the ring (polar layout from
        // ContextMenuPage.LayoutItems), with arc-accurate hit testing.
        var itemSlot = _canvasSlot.AddLocalSlot(item.Label);
        var rect = itemSlot.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;

        // Wedge style: dark neutral fill with the item's color moved to the OUTLINE accent. A colored fill
        // reads as a pie chart; a dark petal with a colored ring reads as a menu. Toggles signal state by
        // outline: green = on, red = off. Fills stay near-opaque - this is an overlay, and anything bleeding
        // through the wedges reads as a rendering error, not a style. -xlinka
        var sourceColor = ToColor(item.FillColor, new color(0.45f, 0.47f, 0.55f, 1f));
        color accent;
        if (item.IsToggle)
            accent = item.IsToggled ? new color(0.42f, 0.92f, 0.55f, 1f) : new color(0.95f, 0.32f, 0.32f, 1f);
        else
            accent = BrightenAccent(sourceColor);
        accent = ToColor(item.OutlineColor, accent); // an explicit outline from the source still wins
        // Fully opaque petals (0.97 still ghosted the world grid through), tinted a touch toward the accent
        // so each wedge carries its identity in the body, not just the ring. -xlinka
        var fill = new color(
            0.085f + accent.r * 0.05f,
            0.09f + accent.g * 0.05f,
            0.115f + accent.b * 0.05f,
            1f);
        if (!item.IsEnabled)
        {
            fill = new color(0.06f, 0.06f, 0.075f, 1f);
            accent = new color(0.38f, 0.38f, 0.42f, 0.8f);
        }

        var arc = itemSlot.AttachComponent<ArcSegment>();
        // Separated petals: each wedge gives up a couple of degrees per side so neighbors don't fuse into
        // one solid donut. Visual + hit arc only - flick lookup reads the item's LOGICAL angles
        // (FindItemAtAngle), so the gaps don't create flick dead zones. -xlinka
        float gap = MathF.Min(2.5f, item.ArcLength * 0.08f);
        arc.AngleStart.Value = item.AngleStart + gap;
        arc.ArcLength.Value = MathF.Max(4f, item.ArcLength - gap * 2f);
        arc.InnerRadius.Value = item.RadiusStart;
        arc.OuterRadius.Value = item.RadiusEnd;
        arc.Tint.Value = fill;
        arc.OutlineColor.Value = accent;
        arc.OutlineThickness.Value = 3f;
        arc.CornerRadius.Value = 10f;

        if (item.IsEnabled && (item.OnPressed != null || item.SubPage != null))
        {
            var button = itemSlot.AttachComponent<ArcButton>();
            button.AddColorDriver(arc.Tint, fill);
            var captured = item;
            button.Clicked += (_, _) => SelectItem(captured);
        }

        // Label centered on the arc's midpoint.
        float midRad = item.AngleMiddle * (MathF.PI / 180f);
        float2 midDirection = new float2(MathF.Cos(midRad), -MathF.Sin(midRad));
        float2 labelCenter = new float2(0.5f, 0.5f) + midDirection * (item.RadiusMiddle / CanvasSize);

        var labelSlot = itemSlot.AddLocalSlot("Label");
        var labelRect = labelSlot.AttachComponent<RectTransform>();
        labelRect.AnchorMin.Value = labelCenter;
        labelRect.AnchorMax.Value = labelCenter;
        labelRect.OffsetMin.Value = new float2(-ItemSize * 0.5f, -ItemSize * 0.5f);
        labelRect.OffsetMax.Value = new float2(ItemSize * 0.5f, ItemSize * 0.5f);

        var text = labelSlot.AttachComponent<Text>();
        text.Content.Value = item.Label;
        text.Font.Target = _font;
        text.Size.Value = 20f;
        text.Color.Value = ToColor(item.LabelColor, color.White);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;

        _builtItems.Add((item, arc, fill));
    }

    private void DestroyVisual()
    {
        if (_menuRoot != null && !_menuRoot.IsDestroyed)
            _menuRoot.Destroy();
        _menuRoot = null!;
        _canvasSlot = null!;
        _font = null!;
    }

    private static color ToColor(float[]? rgba, color fallback)
    {
        if (rgba == null || rgba.Length < 4)
            return fallback;
        return new color(rgba[0], rgba[1], rgba[2], rgba[3]);
    }

    // Lift a (usually dark, fill-styled) source color to outline-accent brightness, preserving hue. The
    // menu sources still pass muted panel fills; as outlines those read near-black, so scale the max
    // channel up to a readable accent. Near-grey/black sources fall back to a neutral light outline. -xlinka
    private static color BrightenAccent(color c)
    {
        float max = MathF.Max(c.r, MathF.Max(c.g, c.b));
        if (max < 1e-3f)
            return new color(0.62f, 0.64f, 0.72f, 1f);
        float s = 0.88f / max;
        return new color(MathF.Min(1f, c.r * s), MathF.Min(1f, c.g * s), MathF.Min(1f, c.b * s), 1f);
    }

    // Item collection

    private ContextMenuPage BuildRootPage(ContextMenuContext context)
    {
        var page = new ContextMenuPage("Menu");

        // 1. Fixed items from RootContextMenuItem components (sorted by Priority desc)
        var rootItems = new List<(int priority, ContextMenuItem item)>();
        foreach (var root in CollectComponents<RootContextMenuItem>())
            rootItems.Add((root.Priority.Value, root.ToMenuItem()));

        foreach (var (_, item) in rootItems.OrderByDescending(x => x.priority))
            page.AddItem(item);

        // 2. Contextual items from ContextMenuItemSource components
        foreach (var source in CollectComponents<ContextMenuItemSource>()
                                    .OrderByDescending(s => s.Priority.Value))
        {
            try
            {
                source.PopulateContextMenu(page, context);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"ContextMenuSystem: {source.GetType().Name}.PopulateContextMenu threw: {ex.Message}");
            }
        }

        return page;
    }

    private IEnumerable<T> CollectComponents<T>() where T : Component
    {
        var results = new List<T>();
        var root = Slot.ActiveUserRoot?.Slot ?? Slot;
        CollectRecursive<T>(root, results);
        return results;
    }

    private static void CollectRecursive<T>(Slot slot, List<T> results) where T : Component
    {
        foreach (var comp in slot.Components)
            if (comp is T t) results.Add(t);
        foreach (var child in slot.Children)
            CollectRecursive<T>(child, results);
    }
}
