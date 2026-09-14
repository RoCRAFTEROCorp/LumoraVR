// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The in-VR typing surface. It lives in userspace beside the dash rather than inside it: a focused
// field can be on the dash, on a world panel or on an inspector, and one keyboard has to reach all
// three. Keys drive the focused TextInput through its public typing surface, so the caret, the
// selection replace and the length clamp are the same code the hardware keyboard runs.
//
// Showing is driven by focus, not by a button: TextInput has no focus-changed event, so this polls
// the single static focus each common update. That poll has to keep running while the panel is
// HIDDEN, which is why CanRunUpdates drops the active-slot gate below - an inactive slot gets no
// updates, and a keyboard that only wakes up while it is already visible never appears. The update
// order puts that poll ahead of UserspacePointer (-1000), so the frame the panel appears is already
// the frame a laser wakes for it rather than a frame of keyboard nothing can press. -xlinka
[ComponentCategory("UI")]
[DefaultUpdateOrder(-1100)]
public sealed class VrKeyboard : Component
{
    // Canvas units. Left column is a preview strip over four glyph rows and a modifier row; the right
    // column carries the numeric pad up top and the arrow cluster at the bottom.
    private const float PanelWidth = 800f;
    private const float PanelHeight = 276f;
    private const float PanelScale = 0.0005f;
    private const float Padding = 6f;
    private const float RowSpacing = 4f;
    private const float KeyGap = 3f;
    private const float PreviewHeight = 32f;
    private const float KeyHeight = 33f;
    private const float KeyWidth = 49f;
    private const float LabelSize = 16f;
    // The corner glyph on a dual-face key, as a fraction of the main face.
    private const float AltLabelScale = 0.55f;
    private const float PreviewSize = 15f;
    private const float CornerRadius = 8f;
    private const float LabelInset = 8f;

    // Right column: three keys wide, so the numpad and the arrow cluster share one width.
    private const float SideColumnWidth = KeyWidth * 3f + KeyGap * 2f;
    // Breathing room between the main grid and the right column.
    private const float SideColumnGap = 7f;
    private const float NumpadHeight = KeyHeight * 5f + RowSpacing * 4f;
    private const float ArrowsHeight = KeyHeight * 2f + RowSpacing;

    // Modifier row, left to right. Sums with the eight gaps to exactly the padded content width.
    private const float ShiftKeyWidth = 78f;
    private const float TabKeyWidth = 48f;
    private const float CopyKeyWidth = 52f;
    private const float SpaceKeyWidth = 130f;
    private const float PasteKeyWidth = 52f;
    private const float BackspaceKeyWidth = 78f;
    private const float EnterKeyWidth = 66f;
    private const float EscapeKeyWidth = 44f;
    private const float CloseKeyWidth = 56f;

    // Re-place the panel when it has drifted out of comfortable reach: too far, or too far around
    // the head to read without turning. Anything inside both stays exactly where it was put.
    private const float ReachDistance = 1f;
    private const float ReachAngleDegrees = 70f;

    // POKE. Metres, measured along the panel's own normal from the readable face.
    // Press and release sit at different depths on purpose: one threshold means a tip resting exactly
    // at the line machine-guns the key as the hand's tracking jitter crosses it a hundred times a
    // second. Press deep, release shallow, and a resting hand does nothing. -xlinka
    private const float PokePressDepth = 0.005f;
    private const float PokeReleaseDepth = 0.002f;
    // How far in front of the face a tip still counts as pointing at the panel (so a key lights up as
    // the hand approaches) and how far through it before we call it a miss and let go.
    private const float PokeHoverDistance = 0.03f;
    private const float PokeMaxDepth = 0.06f;
    // The synthetic ray handed to the canvas starts this far in front of the contact point and aims
    // straight back at it, which is the shape UpdatePointer wants (see UserspaceDashboard).
    private const float PokeRayLift = 0.5f;
    // Lasers own pointer ids 1 and 2 (InteractionLaser.GetPointerId). Poke sits in its own range under
    // its own source, so a hand poking a key can never stomp the pointer state of a laser aimed at it.
    private const int PokePointerIdBase = 900;
    private const UIInteractionSource PokeSource = UIInteractionSource.Touch;

    // One band above the dash surface (UserspaceDashboard.DashSurfaceSortingOrder) and below the
    // laser cursor bands, so the keyboard covers the dash it is typing into and the pointer still
    // shows on top of the keys. -xlinka
    private const int KeyboardSortingOrder = 20100;

    private static readonly color KeyFill = new color(0.22f, 0.20f, 0.34f, 1f);
    private static readonly color KeyBorder = new color(0.52f, 0.46f, 0.82f, 0.45f);
    private static readonly color PanelBorder = new color(0.52f, 0.46f, 0.82f, 0.55f);
    // Held caps reads brighter than the one-shot accent so the two shift states are never confused.
    private static readonly color ShiftHeldFill = new color(0.62f, 0.55f, 0.98f, 1f);
    // A key that is present but cannot do anything - Paste on a platform with no clipboard.
    private static readonly color KeyDisabledFill = new color(0.15f, 0.14f, 0.20f, 1f);

    private static readonly string[] GlyphRows =
    {
        "1234567890-=",
        "qwertyuiop[]",
        "asdfghjkl;'\\",
        "zxcvbnm,./",
    };

    // Index-aligned with GlyphRows: row r, column c on one is the same key on the other.
    private static readonly string[] ShiftGlyphRows =
    {
        "!@#$%^&*()_+",
        "QWERTYUIOP{}",
        "ASDFGHJKL:\"|",
        "ZXCVBNM<>?",
    };

    // Numeric pad, top row first. These type themselves whether or not shift is up - there is no
    // second face for a number pad, and nobody wants a shifted 7 to come out as an ampersand.
    private static readonly string[] NumpadRows =
    {
        "789",
        "456",
        "123",
        "0.-",
        "+*/",
    };

    // Shift is applying right now, whether that is a one-shot or a held caps.
    public readonly Sync<bool> ShiftActive;
    // Sticky: shift survives the next character instead of clearing after it.
    public readonly Sync<bool> ShiftHeld;
    public readonly Sync<float> Distance;
    public readonly Sync<float> DropBelowGaze;
    // Seconds a repeating key is held before it starts re-firing, and the gap between re-fires after
    // that. The defaults are the usual OS shape: long enough that a normal keypress never repeats,
    // fast enough that a held backspace clears a line at a sensible rate.
    public readonly Sync<float> RepeatDelay;
    public readonly Sync<float> RepeatInterval;
    // How long after a shift tap a second tap still counts as a DOUBLE tap and locks caps on.
    public readonly Sync<float> ShiftHoldWindow;

    private readonly SyncRef<Text> _preview;
    private readonly SyncRef<VrKey> _shiftKey;
    private readonly SyncRef<VrKey> _pasteKey;

    // Per-peer handles: the panel is rebuilt locally, never loaded, so these are refilled by
    // BuildLayout rather than replicated.
    private readonly List<VrKey> _keys = new();
    private UITheme? _theme;
    private Canvas? _canvas;
    private bool _built;
    // Whether a field held focus last poll. Without it, a keyboard summoned by hand with nothing
    // focused would hide itself on its very first update.
    private bool _hadFocus;
    // World time of the last shift tap, for the double-tap window.
    private double _lastShiftTap = double.NegativeInfinity;

    // Per-hand poke state, indexed by PokeIndex. Whether the canvas currently holds a pointer for this
    // hand at all, and whether that pointer is pressed.
    private readonly bool[] _pokeActive = new bool[2];
    private readonly bool[] _pokePressed = new bool[2];
    private readonly Slot?[] _pokeTips = new Slot?[2];

    public static VrKeyboard? LocalInstance { get; private set; }

    // What UserspacePointer reads to keep a laser alive for the keyboard the way it does for the dash.
    public static bool LocalShown => LocalInstance is { IsDestroyed: false } keyboard && keyboard.IsShown;

    public bool IsShown => Slot != null && !Slot.IsDestroyed && Slot.ActiveSelf.Value;

    // The focus poll is the whole point of this component and it has to run while the panel is off.
    protected override bool CanRunUpdates => !IsDestroyed && Slot != null && !Slot.IsDestroyed;

    public VrKeyboard()
    {
        ShiftActive = new Sync<bool>(this, false);
        ShiftHeld = new Sync<bool>(this, false);
        Distance = new Sync<float >(this, 0.55f);
        DropBelowGaze = new Sync<float>(this, 0.2f);
        RepeatDelay = new Sync<float>(this, 0.45f);
        RepeatInterval = new Sync<float>(this, 0.05f);
        ShiftHoldWindow = new Sync<float>(this, 0.5f);
        _preview = new SyncRef<Text>(this);
        _shiftKey = new SyncRef<VrKey>(this);
        _pasteKey = new SyncRef<VrKey>(this);
    }

    public override void OnStart()
    {
        base.OnStart();
        LocalInstance = this;
        BuildLayout();
        RefreshShiftVisuals();
        RefreshPasteKey();
    }

    public override void OnDestroy()
    {
        if (ReferenceEquals(LocalInstance, this))
            LocalInstance = null;
        _keys.Clear();
        base.OnDestroy();
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();

        var focused = TextInput.Focused;
        bool hasFocus = focused is { IsDestroyed: false };

        // The dashboard is a SECOND reason to be up, and it is the one that was missing.
        //
        // Dash screens do not focus a TextInput: their inline fields take raw keystrokes through the
        // dashboard's own feed, which until now had exactly one caller, a desktop key handler. So in a
        // headset the keyboard never appeared for a dash field and would not have typed into it if it
        // had, which meant you could not sign in, name a folder, rename a world or search your
        // inventory without taking the headset off. Track the two reasons separately so dismissing one
        // does not strand the other. -xlinka
        bool dashWantsKeys = Engine.Current?.InputInterface?.IsVRActive == true
                             && Engine.Current?.InputInterface?.IsDashboardOpen == true
                             && UserspaceDashboard.LocalInstance?.CurrentScreenTakesKeys == true;

        if (hasFocus)
        {
            _hadFocus = true;
            // Auto-show is VR-only: on desktop the hardware keyboard is right there and a panel
            // covering the view would be in the way. Show() itself stays open to anyone.
            if (!IsShown && Engine.Current?.InputInterface?.IsVRActive == true)
                Show();
        }
        else if (dashWantsKeys)
        {
            if (!IsShown)
                Show();
        }
        else if (_hadFocus || _hadDashKeys)
        {
            _hadFocus = false;
            if (IsShown)
                Hide();
        }

        _hadDashKeys = dashWantsKeys;

        if (IsShown)
            UpdatePreview(focused);

        UpdatePoke();
    }

    // SHOW / HIDE

    public void Show()
    {
        if (Slot == null || Slot.IsDestroyed)
            return;

        // Coming up for a field that is already focused counts as the start of a typing session, so
        // the poll knows to put the panel away again when that focus goes. Without it a field that
        // focuses and blurs inside one frame leaves the keyboard stranded on screen. -xlinka
        if (TextInput.Focused is { IsDestroyed: false })
            _hadFocus = true;

        bool wasShown = IsShown;
        Slot.ActiveSelf.Value = true;
        if (!wasShown || !IsWithinReach())
            PlaceInFrontOfHead();
        // The clipboard service is wired at startup, before userspace exists, but a harness (or a
        // platform that hands one over late) can arrive after the panel was built.
        RefreshPasteKey();
    }

    // Putting the keyboard away ends the typing session too. Leaving the field focused would keep
    // the whole hardware keyboard gated off (InputInterface.TextFocusHeld) with nothing on screen
    // saying why you cannot walk. -xlinka
    public void Hide()
    {
        ReleasePoke(Chirality.Left);
        ReleasePoke(Chirality.Right);
        if (Slot != null && !Slot.IsDestroyed)
            Slot.ActiveSelf.Value = false;
        _hadFocus = false;
        BlurFocused();
    }

    private static void BlurFocused()
    {
        var focused = TextInput.Focused;
        if (focused is { IsDestroyed: false })
            focused.Unfocus();
    }

    // With the dash up, the keyboard hangs off its bottom edge, tilted up toward the wearer, and
    // rides along as the dash glides: the reference platform's keyboard offset slot lives inside its
    // dash rig for the same reason. Placing both from the head independently put the keyboard right
    // in front of the panel it was typing into. -xlinka
    private const float DockGap = 0.06f;
    private const float DockTowardViewer = 0.18f;
    private const float DockTiltDegrees = -45f;

    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);
        if (!IsShown || Engine.Current?.InputInterface?.IsVRActive != true)
            return;

        var dash = UserspaceDashboard.LocalInstance;
        var surface = dash?.IsOpen.Value == true ? dash.SurfaceSlot : null;
        if (surface == null || Slot == null || Slot.IsDestroyed)
            return;

        float halfHeight = dash!.DisplayHeight.Value * 0.5f;
        var local = new float3(0f, -halfHeight - DockGap, DockTowardViewer);
        Slot.GlobalPosition = surface.LocalPointToGlobal(local);
        Slot.GlobalRotation = surface.GlobalRotation * floatQ.AxisAngle(float3.Right, DockTiltDegrees);
        Slot.LocalScale.Value = float3.One * PanelScale;
    }

    private void PlaceInFrontOfHead()
    {
        if (Slot == null || Slot.IsDestroyed)
            return;

        var head = HeadSlot();
        if (head == null)
        {
            Slot.LocalPosition.Value = new float3(0f, 1.4f, -Distance.Value);
            Slot.LocalRotation.Value = floatQ.Identity;
            Slot.LocalScale.Value = float3.One * PanelScale;
            return;
        }

        Slot.GlobalPosition = head.GlobalPosition
            + FlatViewForward(head) * Distance.Value
            + float3.Down * DropBelowGaze.Value;

        // Yaw so the panel's readable +Z face turns back at the head. Never floatQ.LookRotation here:
        // it hands back the inverse rotation and the panel goes edge-on at oblique angles. -xlinka
        var toViewer = head.GlobalPosition - Slot.GlobalPosition;
        toViewer.y = 0f;
        if (toViewer.LengthSquared > 1e-6f)
            Slot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(toViewer.x, toViewer.z));

        Slot.LocalScale.Value = float3.One * PanelScale;
    }

    private bool IsWithinReach()
    {
        var head = HeadSlot();
        // No head to measure against is not a reason to yank the panel somewhere else.
        if (head == null || Slot == null || Slot.IsDestroyed)
            return true;

        var offset = Slot.GlobalPosition - head.GlobalPosition;
        if (offset.Length > ReachDistance)
            return false;

        var flat = new float3(offset.x, 0f, offset.z);
        if (flat.LengthSquared <= 1e-6f)
            return false;

        float alignment = float3.Dot(flat.Normalized, FlatViewForward(head));
        return alignment >= MathF.Cos(ReachAngleDegrees * (MathF.PI / 180f));
    }

    private Slot? HeadSlot()
    {
        var head = World?.LocalUser?.Root?.HeadSlot;
        return head != null && !head.IsDestroyed ? head : null;
    }

    private static float3 FlatViewForward(Slot head)
    {
        float3 forward = head.GlobalRotation * float3.Backward; // view forward is -Z
        forward.y = 0f;
        return forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;
    }

    // POKE
    // Pushing a controller into the panel presses whatever key the tip lands on. Deliberately scoped
    // to this component rather than built as a general Helio touch layer: the keyboard is the one
    // surface where poking is the natural gesture, and the canvas already has a pointer API that does
    // everything a poke needs - hover, press, release, submit-on-release. All this has to do is turn a
    // tip position into that API's ray. -xlinka

    private void UpdatePoke()
    {
        // Desktop has no tip to poke with: the controller slots there are parked at fixed offsets by
        // the locomotion controller, and one of them sitting inside the panel would type forever.
        if (!IsShown || Engine.Current?.InputInterface?.IsVRActive != true)
        {
            ReleasePoke(Chirality.Left);
            ReleasePoke(Chirality.Right);
            return;
        }

        PokeFromTip(Chirality.Left);
        PokeFromTip(Chirality.Right);
    }

    private void PokeFromTip(Chirality side)
    {
        var tip = ControllerSlot(side);
        if (tip == null)
        {
            ReleasePoke(side);
            return;
        }
        PokeAt(side, tip.GlobalPosition);
    }

    // The tracked controller slot for a hand, resolved off the local user's root through the same
    // body-node registry HeadSlot uses. The userspace pointer rig registers both under BodyNode
    // LeftController/RightController (see Templates.Userspace.BuildPointerHand).
    private Slot? ControllerSlot(Chirality side)
    {
        int index = PokeIndex(side);
        var cached = _pokeTips[index];
        if (cached != null && !cached.IsDestroyed)
            return cached;

        var root = World?.LocalUser?.Root;
        if (root == null || root.IsDestroyed)
            return null;

        var node = side == Chirality.Left ? BodyNode.LeftController : BodyNode.RightController;
        var slot = root.GetRegisteredComponent<TrackedDevicePositioner>(p => p.AutoBodyNode.Value == node)?.Slot;
        _pokeTips[index] = slot != null && !slot.IsDestroyed ? slot : null;
        return _pokeTips[index];
    }

    // Drive one hand's poke from an explicit world-space tip. The per-frame path above resolves the
    // tracked controller and calls this; anything that wants to feed its own tip (a harness, a future
    // finger-tracked hand) calls it directly. The controller slot's own origin IS the tip here - there
    // is no fingertip node on the userspace rig to offset to, and inventing one would just be a
    // constant nobody could tune. -xlinka
    public void PokeAt(Chirality side, in float3 tipPosition)
    {
        var canvas = PokeCanvas();
        if (canvas == null || Slot == null || Slot.IsDestroyed)
        {
            ReleasePoke(side);
            return;
        }

        // Depth along the panel's own normal: positive once the tip is PAST the readable face. The
        // normal is rotation-only so this stays in metres however the panel has been scaled.
        float3 normal = Slot.Forward;
        float depth = -float3.Dot(tipPosition - Slot.GlobalPosition, normal);

        // Where on the face, in canvas units. GlobalPointToLocal takes the panel scale back off, so
        // the bounds test is against the layout rect whatever size the user pulled the panel to.
        float3 local = Slot.GlobalPointToLocal(tipPosition);
        bool onFace = MathF.Abs(local.x) <= PanelWidth * 0.5f && MathF.Abs(local.y) <= PanelHeight * 0.5f;

        if (!onFace || depth < -PokeHoverDistance || depth > PokeMaxDepth)
        {
            ReleasePoke(side);
            return;
        }

        int index = PokeIndex(side);
        bool pressed = _pokePressed[index] ? depth > PokeReleaseDepth : depth >= PokePressDepth;

        var contact = Slot.LocalPointToGlobal(new float3(local.x, local.y, 0f));
        canvas.UpdatePointer(PokeSource, PokePointerId(side), contact + normal * PokeRayLift, -normal, pressed,
            World?.LocalUser);

        _pokeActive[index] = true;
        _pokePressed[index] = pressed;
    }

    // Let go of a hand's poke pointer. Goes through ClearPointer, not a pressed=false update, so a tip
    // dragged sideways off a key CANCELS instead of typing it - the same thing a mouse does when you
    // slide off a button before letting go. A straight withdraw still types, because that path comes
    // through PokeAt with pressed false. -xlinka
    public void ReleasePoke(Chirality side)
    {
        int index = PokeIndex(side);
        if (!_pokeActive[index])
            return;
        PokeCanvas()?.ClearPointer(PokeSource, PokePointerId(side), World?.LocalUser);
        _pokeActive[index] = false;
        _pokePressed[index] = false;
    }

    private Canvas? PokeCanvas()
    {
        if (_canvas != null && !_canvas.IsDestroyed)
            return _canvas;
        _canvas = Slot != null && !Slot.IsDestroyed ? Slot.GetComponent<Canvas>() : null;
        return _canvas;
    }

    private static int PokeIndex(Chirality side) => side == Chirality.Left ? 0 : 1;

    private static int PokePointerId(Chirality side) => PokePointerIdBase + PokeIndex(side);

    // KEY DISPATCH

    public void HandleKey(VrKey key)
    {
        if (key == null || key.IsDestroyed || IsDestroyed)
            return;

        switch (key.Role.Value)
        {
            case VrKeyRole.Shift:
                CycleShift();
                return;
            case VrKeyRole.Close:
                Hide();
                return;
            case VrKeyRole.Escape:
                // Cancel the edit without committing it; the focus poll puts the panel away next
                // update. With no field focused the dashboard is the one editing, so it gets the
                // cancel instead - otherwise Escape is a dead key on every dash screen.
                if (TextInput.Focused is { IsDestroyed: false })
                    BlurFocused();
                else
                    UserspaceDashboard.LocalInstance?.FeedEscape();
                return;
        }

        var input = TextInput.Focused;
        if (input == null || input.IsDestroyed)
        {
            // Nothing owns a text caret, so the dashboard is the consumer. Its feed already routes at
            // the current screen and falls back to the file-browser search well, which is exactly what
            // the desktop key handler does with the same keystrokes.
            FeedDashboard(key);
            return;
        }

        switch (key.Role.Value)
        {
            case VrKeyRole.Character:
                input.TypeString(key.ActiveGlyph(ShiftActive.Value));
                ConsumeOneShotShift();
                break;
            case VrKeyRole.Space:
                input.TypeString(" ");
                break;
            case VrKeyRole.Backspace:
                input.PressBackspace();
                break;
            case VrKeyRole.Enter:
                input.PressEnter();
                break;
            case VrKeyRole.Tab:
                input.PressTab();
                break;
            case VrKeyRole.ArrowLeft:
                input.MoveCaretBy(-1);
                break;
            case VrKeyRole.ArrowRight:
                input.MoveCaretBy(1);
                break;
            case VrKeyRole.ArrowUp:
                input.MoveCaretLine(up: true);
                break;
            case VrKeyRole.ArrowDown:
                input.MoveCaretLine(up: false);
                break;
            case VrKeyRole.Copy:
                input.CopySelection();
                break;
            case VrKeyRole.Paste:
                input.PasteClipboard();
                break;
        }
    }

    private bool _hadDashKeys;

    private void FeedDashboard(VrKey key)
    {
        var dash = UserspaceDashboard.LocalInstance;
        if (dash == null || dash.IsDestroyed)
            return;

        switch (key.Role.Value)
        {
            case VrKeyRole.Character:
                string glyph = key.ActiveGlyph(ShiftActive.Value);
                if (!string.IsNullOrEmpty(glyph))
                    dash.FeedSearchChar(glyph[0]);
                ConsumeOneShotShift();
                break;
            case VrKeyRole.Space:
                dash.FeedSearchChar(' ');
                break;
            case VrKeyRole.Backspace:
                dash.FeedSearchBackspace();
                break;
            case VrKeyRole.Enter:
                dash.FeedEnter();
                break;
            case VrKeyRole.Tab:
                break;
        }
    }

    // SHIFT STATE
    // Off -> one-shot -> held -> off. A one-shot clears itself after the next character key; held
    // rides through as many as you like. Two QUICK taps lock caps, a third drops it. -xlinka

    public void CycleShift()
    {
        double now = World?.Time?.TotalTime ?? 0.0;
        double sinceLast = now - _lastShiftTap;

        if (ShiftHeld.Value)
        {
            ShiftHeld.Value = false;
            ShiftActive.Value = false;
        }
        else if (ShiftActive.Value)
        {
            // Caps locks on a genuine double tap only. A second tap a minute later is somebody
            // reaching for shift again and finding it already armed - locking caps on there is a
            // surprise you then have to notice and undo, so it cancels the one-shot instead. -xlinka
            if (sinceLast <= ShiftHoldWindow.Value)
                ShiftHeld.Value = true;
            else
                ShiftActive.Value = false;
        }
        else
        {
            ShiftActive.Value = true;
        }

        _lastShiftTap = now;
        RefreshShiftVisuals();
    }

    private void ConsumeOneShotShift()
    {
        if (!ShiftActive.Value || ShiftHeld.Value)
            return;
        ShiftActive.Value = false;
        RefreshShiftVisuals();
    }

    // One pass over the registered keys instead of a driver component per key: fifty drivers on a
    // panel that already re-meshes its whole row on a text change buys nothing. -xlinka
    private void RefreshShiftVisuals()
    {
        bool shift = ShiftActive.Value;
        for (int i = 0; i < _keys.Count; i++)
        {
            var key = _keys[i];
            if (key != null && !key.IsDestroyed)
                key.ApplyShiftState(shift);
        }

        var shiftKey = _shiftKey.Target;
        if (shiftKey == null || shiftKey.IsDestroyed || shiftKey.Slot == null)
            return;

        color fill = KeyFill;
        if (ShiftHeld.Value)
            fill = ShiftHeldFill;
        else if (ShiftActive.Value)
            fill = InspectorUI.AccentColor;

        // Straight through the shared tint rule: a Helio button DRIVES its own backing tint, so a
        // direct write is reverted on the driver's next pass.
        InspectorUI.ApplyStateTint(shiftKey.Slot.GetComponent<BorderedImage>()?.Tint, fill);
    }

    // A platform with no clipboard service gets a Paste key that says so: muted fill, muted label, and
    // Interactable off so it cannot even be pressed. The alternative is a key that looks live and
    // silently does nothing, which is the worst of both. -xlinka
    private void RefreshPasteKey()
    {
        var key = _pasteKey.Target;
        if (key == null || key.IsDestroyed || key.Slot == null)
            return;

        bool available = TextInput.ClipboardAvailable;

        var button = key.Slot.GetComponent<Button>();
        if (button != null && !button.IsDestroyed)
            button.Interactable.Value = available;

        InspectorUI.ApplyStateTint(key.Slot.GetComponent<BorderedImage>()?.Tint, available ? KeyFill : KeyDisabledFill);

        var label = key.Label.Target;
        if (label != null && !label.IsDestroyed)
            label.Color.Value = available ? InspectorUI.TextColor : InspectorUI.MutedColor;
    }

    // PREVIEW

    private void UpdatePreview(TextInput? input)
    {
        var preview = _preview.Target;
        if (preview == null || preview.IsDestroyed)
            return;

        string next = string.Empty;
        if (input != null && !input.IsDestroyed)
        {
            string value = input.Text.Value ?? string.Empty;
            // A password field stays a password field on the big readable panel in front of your face.
            if (input.Mask.Value && value.Length > 0)
                value = new string('•', value.Length);
            int caret = System.Math.Clamp(input.CaretIndex, 0, value.Length);
            next = value.Insert(caret, "|");
        }

        // Only on change: writing the content every frame re-tessellates the strip's chunk every frame.
        if (preview.Content.Value != next)
            preview.Content.Value = next;
    }

    // LAYOUT

    private void BuildLayout()
    {
        if (_built || Slot == null || Slot.IsDestroyed)
            return;
        _built = true;

        _theme = Slot.GetOrAttachComponent<UITheme>();
        // Borrow the dash's font provider when there is one: same world so the ref is legal, one glyph
        // atlas between the two panels, and the keys never come up blank because the theme's own
        // default font URL had not been registered yet. -xlinka
        var dash = UserspaceDashboard.LocalInstance;
        if (dash != null && !dash.IsDestroyed && ReferenceEquals(dash.World, World) && dash.Font.Target != null)
            _theme.Font.Target = dash.Font.Target;
        _theme.PanelBackground.Value = InspectorUI.PaneColor;
        _theme.Header.Value = InspectorUI.HeaderColor;
        _theme.ButtonFill.Value = KeyFill;
        _theme.Accent.Value = InspectorUI.AccentColor;
        _theme.Separator.Value = PanelBorder;
        _theme.Border.Value = KeyBorder;
        _theme.CornerRadius.Value = CornerRadius;

        var rect = Slot.GetOrAttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(PanelWidth * -0.5f, PanelHeight * -0.5f);
        rect.OffsetMax.Value = new float2(PanelWidth * 0.5f, PanelHeight * 0.5f);

        var canvas = Slot.GetOrAttachComponent<Canvas>();
        // Overlay + a reserved band: a keyboard that world geometry can cut into is unusable, and the
        // depth test alone would clip it against whatever you happen to be standing in front of.
        canvas.Overlay.Value = true;
        canvas.SortingOrder.Value = KeyboardSortingOrder;
        _canvas = canvas;

        // Placement is by hand today. Nothing on the userspace pointer rig carries a Grabber yet, so
        // this only bites once one does; until then the reposition rule in Show() is what moves the
        // panel. Scalable so a user can size it to their reach rather than ours. -xlinka
        var grab = Slot.GetOrAttachComponent<Grabbable>();
        grab.AllowGrab.Value = true;
        grab.Scalable.Value = true;
        grab.FollowRotation.Value = true;
        grab.Receivable.Value = false;

        var background = Slot.GetOrAttachComponent<BorderedImage>();
        background.Tint.Value = InspectorUI.PaneColor;
        background.BorderTint.Value = PanelBorder;
        ApplyRounded(background);

        // The theme parks its font and rounded-sprite providers on child slots of this one, so the
        // rows go in their own container - a layout on the root would try to lay those out as keys.
        var content = Slot.FindChildOrAdd("Content");
        var contentRect = content.GetOrAttachComponent<RectTransform>();
        contentRect.AnchorMin.Value = float2.Zero;
        contentRect.AnchorMax.Value = float2.One;
        contentRect.OffsetMin.Value = new float2(Padding, Padding);
        // Stops short of the right column, so the main grid's rows are the exact width they always
        // were and every row below keeps its own key sizes.
        contentRect.OffsetMax.Value = new float2(-(Padding + SideColumnWidth + SideColumnGap), -Padding);

        var column = content.AttachComponent<VerticalLayout>();
        column.Spacing.Value = RowSpacing;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;
        // The main grid is shorter than the right column (six rows against seven), so centre it rather
        // than leaving all the slack in one lump under the modifier row.
        column.MainAlignment.Value = MainAxisAlignment.Center;

        BuildPreviewStrip(content);
        for (int i = 0; i < GlyphRows.Length; i++)
            BuildGlyphRow(content, i);
        BuildModifierRow(content);

        BuildSideColumn();
    }

    // The right-hand block: numeric pad anchored to the top, arrow cluster to the bottom. Two
    // independently anchored containers rather than one long layout, so the gap between the two blocks
    // is deliberate instead of whatever the leftover space happened to be. -xlinka
    private void BuildSideColumn()
    {
        var side = Slot.FindChildOrAdd("SideColumn");
        var sideRect = side.GetOrAttachComponent<RectTransform>();
        sideRect.AnchorMin.Value = new float2(1f, 0f);
        sideRect.AnchorMax.Value = new float2(1f, 1f);
        sideRect.OffsetMin.Value = new float2(-(Padding + SideColumnWidth), Padding);
        sideRect.OffsetMax.Value = new float2(-Padding, -Padding);

        var numpad = side.AddSlot("Numpad");
        var numpadRect = numpad.AttachComponent<RectTransform>();
        numpadRect.AnchorMin.Value = new float2(0f, 1f);
        numpadRect.AnchorMax.Value = new float2(1f, 1f);
        numpadRect.OffsetMin.Value = new float2(0f, -NumpadHeight);
        numpadRect.OffsetMax.Value = float2.Zero;
        var numpadColumn = numpad.AttachComponent<VerticalLayout>();
        numpadColumn.Spacing.Value = RowSpacing;
        numpadColumn.ForceExpandWidth.Value = true;
        numpadColumn.ForceExpandHeight.Value = false;

        for (int i = 0; i < NumpadRows.Length; i++)
        {
            var row = BuildRow(numpad, "Numpad" + (i + 1), KeyHeight);
            AddKeyLayout(row);
            string glyphs = NumpadRows[i];
            for (int c = 0; c < glyphs.Length; c++)
            {
                string glyph = glyphs[c].ToString();
                // Same glyph on both faces: the pad types literally whatever shift is doing.
                BuildKey(row, "Num" + glyph, glyph, glyph, VrKeyRole.Character, KeyWidth, KeyFill);
            }
        }

        var arrows = side.AddSlot("Arrows");
        var arrowsRect = arrows.AttachComponent<RectTransform>();
        arrowsRect.AnchorMin.Value = new float2(0f, 0f);
        arrowsRect.AnchorMax.Value = new float2(1f, 0f);
        arrowsRect.OffsetMin.Value = float2.Zero;
        arrowsRect.OffsetMax.Value = new float2(0f, ArrowsHeight);
        var arrowColumn = arrows.AttachComponent<VerticalLayout>();
        arrowColumn.Spacing.Value = RowSpacing;
        arrowColumn.ForceExpandWidth.Value = true;
        arrowColumn.ForceExpandHeight.Value = false;

        // Inverted T: up on its own over left/down/right, which is the shape every hand already knows
        // where to reach on.
        var top = BuildRow(arrows, "ArrowsTop", KeyHeight);
        AddKeyLayout(top);
        BuildKey(top, "ArrowUp", "↑", string.Empty, VrKeyRole.ArrowUp, KeyWidth, KeyFill);

        var bottom = BuildRow(arrows, "ArrowsBottom", KeyHeight);
        AddKeyLayout(bottom);
        BuildKey(bottom, "ArrowLeft", "←", string.Empty, VrKeyRole.ArrowLeft, KeyWidth, KeyFill);
        BuildKey(bottom, "ArrowDown", "↓", string.Empty, VrKeyRole.ArrowDown, KeyWidth, KeyFill);
        BuildKey(bottom, "ArrowRight", "→", string.Empty, VrKeyRole.ArrowRight, KeyWidth, KeyFill);
    }

    private void BuildPreviewStrip(Slot content)
    {
        var strip = BuildRow(content, "Preview", PreviewHeight);

        var backing = strip.AttachComponent<BorderedImage>();
        backing.Tint.Value = InspectorUI.RowColor;
        backing.BorderTint.Value = KeyBorder;
        ApplyRounded(backing);

        var labelSlot = strip.AddSlot("Text");
        var labelRect = labelSlot.AttachComponent<RectTransform>();
        labelRect.AnchorMin.Value = float2.Zero;
        labelRect.AnchorMax.Value = float2.One;
        labelRect.OffsetMin.Value = new float2(LabelInset, 0f);
        labelRect.OffsetMax.Value = new float2(-LabelInset, 0f);

        var text = labelSlot.AttachComponent<Text>();
        text.Content.Value = string.Empty;
        text.Size.Value = PreviewSize;
        text.Color.Value = InspectorUI.TextColor;
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        ApplyFont(text);
        _preview.Target = text;
    }

    private void BuildGlyphRow(Slot content, int index)
    {
        var row = BuildRow(content, "Row" + (index + 1), KeyHeight);
        AddKeyLayout(row);

        string glyphs = GlyphRows[index];
        string shifted = ShiftGlyphRows[index];
        for (int i = 0; i < glyphs.Length; i++)
        {
            string glyph = glyphs[i].ToString();
            string shiftGlyph = i < shifted.Length ? shifted[i].ToString() : glyph;
            BuildKey(row, glyph, glyph, shiftGlyph, VrKeyRole.Character, KeyWidth, KeyFill);
        }
    }

    private void BuildModifierRow(Slot content)
    {
        var row = BuildRow(content, "Modifiers", KeyHeight);
        AddKeyLayout(row);

        _shiftKey.Target = BuildKey(row, "Shift", "Shift", string.Empty, VrKeyRole.Shift, ShiftKeyWidth, KeyFill);
        BuildKey(row, "Tab", "Tab", string.Empty, VrKeyRole.Tab, TabKeyWidth, KeyFill);
        BuildKey(row, "Copy", "Copy", string.Empty, VrKeyRole.Copy, CopyKeyWidth, KeyFill);
        BuildKey(row, "Space", "Space", string.Empty, VrKeyRole.Space, SpaceKeyWidth, KeyFill);
        _pasteKey.Target = BuildKey(row, "Paste", "Paste", string.Empty, VrKeyRole.Paste, PasteKeyWidth, KeyFill);
        BuildKey(row, "Backspace", "Back", string.Empty, VrKeyRole.Backspace, BackspaceKeyWidth, KeyFill);
        BuildKey(row, "Enter", "Enter", string.Empty, VrKeyRole.Enter, EnterKeyWidth, InspectorUI.AccentColor);
        BuildKey(row, "Escape", "Esc", string.Empty, VrKeyRole.Escape, EscapeKeyWidth, KeyFill);
        BuildKey(row, "Close", "Close", string.Empty, VrKeyRole.Close, CloseKeyWidth, InspectorUI.DangerColor);
    }

    private static Slot BuildRow(Slot content, string name, float height)
    {
        var row = content.AddSlot(name);
        row.AttachComponent<RectTransform>();
        var element = row.AttachComponent<LayoutElement>();
        element.MinHeight.Value = height;
        element.PreferredHeight.Value = height;
        element.FlexibleHeight.Value = 0f;
        element.FlexibleWidth.Value = 1f;
        // Own chunk per row: a shift flip rewrites four rows of labels, and the preview strip changes
        // on every keystroke. Chunked, each of those re-meshes one strip instead of the whole panel.
        row.AttachComponent<GraphicChunkRoot>();
        return row;
    }

    // Rows are narrower than the panel by design (twelve keys, then ten), so they centre rather than
    // leaving a ragged gutter down one side.
    private static void AddKeyLayout(Slot row)
    {
        var layout = row.AttachComponent<HorizontalLayout>();
        layout.Spacing.Value = KeyGap;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = true;
        layout.MainAlignment.Value = MainAxisAlignment.Center;
    }

    private VrKey BuildKey(Slot row, string name, string glyph, string shiftGlyph, VrKeyRole role,
        float width, color fill)
    {
        var slot = row.AddSlot(name);
        slot.AttachComponent<RectTransform>();
        var element = slot.AttachComponent<LayoutElement>();
        element.MinWidth.Value = width;
        element.PreferredWidth.Value = width;
        element.FlexibleWidth.Value = 0f;
        element.MinHeight.Value = KeyHeight;
        element.PreferredHeight.Value = KeyHeight;
        element.FlexibleHeight.Value = 0f;

        var backing = slot.AttachComponent<BorderedImage>();
        backing.Tint.Value = fill;
        backing.BorderTint.Value = KeyBorder;
        ApplyRounded(backing);

        var button = slot.AttachComponent<Button>();
        // Button.OnAttach only adopts a plain Image; the rounded keys use BorderedImage, so the
        // hover/press driver has to be pointed at the tint by hand or the keys never light up.
        button.SetupBackgroundColor(backing.Tint);

        var key = slot.AttachComponent<VrKey>();
        key.Glyph.Value = glyph;
        key.ShiftGlyph.Value = shiftGlyph;
        key.Role.Value = role;
        key.Keyboard.Target = this;
        button.SetAction(key.OnPressed);

        key.Label.Target = BuildKeyFace(slot, "Label", glyph, LabelSize, InspectorUI.TextColor,
            TextHorizontalAlignment.Center, TextVerticalAlignment.Middle, float2.Zero, float2.One,
            float2.Zero, float2.Zero);

        // Two genuinely different glyphs (a symbol key, not a letter) get a second small face in the
        // corner showing the one shift is NOT on, so you can read what a key does both ways without
        // pressing shift to find out. Letters skip it - a big 'a' with a little 'A' beside it is
        // noise. -xlinka
        if (key.HasDualFace)
        {
            key.AltLabel.Target = BuildKeyFace(slot, "AltLabel", key.ActiveGlyph(true), LabelSize * AltLabelScale,
                InspectorUI.MutedColor, TextHorizontalAlignment.Right, TextVerticalAlignment.Top,
                new float2(0.45f, 0.45f), float2.One, float2.Zero, new float2(-4f, -2f));
        }

        _keys.Add(key);
        return key;
    }

    private Text BuildKeyFace(Slot keySlot, string name, string content, float size, color tint,
        TextHorizontalAlignment horizontal, TextVerticalAlignment vertical,
        float2 anchorMin, float2 anchorMax, float2 offsetMin, float2 offsetMax)
    {
        var faceSlot = keySlot.AddSlot(name);
        var faceRect = faceSlot.AttachComponent<RectTransform>();
        faceRect.AnchorMin.Value = anchorMin;
        faceRect.AnchorMax.Value = anchorMax;
        faceRect.OffsetMin.Value = offsetMin;
        faceRect.OffsetMax.Value = offsetMax;

        var text = faceSlot.AttachComponent<Text>();
        text.Content.Value = content;
        text.Size.Value = size;
        text.Color.Value = tint;
        text.HorizontalAlignment.Value = horizontal;
        text.VerticalAlignment.Value = vertical;
        ApplyFont(text);
        return text;
    }

    private void ApplyRounded(BorderedImage image)
    {
        var sprite = _theme?.RoundedSprite;
        if (sprite == null)
            return;
        image.Texture.Target = sprite;
        image.NineSlice.Value = true;
        image.Borders.Value = new float4(CornerRadius, CornerRadius, CornerRadius, CornerRadius);
    }

    private void ApplyFont(Text text)
    {
        var font = _theme?.ThemeFont;
        if (font != null)
            text.Font.Target = font;
    }
}
