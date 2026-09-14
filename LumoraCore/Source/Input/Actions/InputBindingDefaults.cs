// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Input.Actions;

using Cat = InputControlCatalog;

// The shipped binding table.
//
// This reproduces what the engine did before there were actions, control for control, so nobody who
// updates has to relearn anything: desktop primary is still left click, tool secondary is still R,
// the context menu is still middle click OR T, held objects still ride the wheel with shift to
// scale, VR is still trigger/grip/secondary per hand. Anything that reads differently from the old
// hardcoded version is a bug in this file, not a design decision.
//
// The gamepad column is new, because there was nothing to preserve there. It is laid out so a pad
// alone can play: left stick walks, right stick turns, A jumps, RT is the tool, RB grabs, Y opens
// the context menu, Start opens the dashboard.
//
// Desktop input only ever drives the RIGHT hand - there is one mouse - so the left-hand set gets VR
// bindings only. -xlinka
public static class InputBindingDefaults
{
    private static readonly ControlRef LeftCtrl = ControlRef.Key(Key.LeftControl);
    private static readonly ControlRef RightCtrl = ControlRef.Key(Key.RightControl);

    public static void Apply(InputBindingMap map)
    {
        foreach (var action in map.AllActions())
        {
            action.ClearBindings();
            ApplyTo(map, action);
        }
    }

    // Assumes the action's list was already cleared.
    public static void ApplyTo(InputBindingMap map, InputAction action)
    {
        switch (action.Set?.Name)
        {
            case "Locomotion":
                ApplyLocomotion(map, action);
                break;
            case "Interaction.Left":
                ApplyInteraction(map, action, Chirality.Left);
                break;
            case "Interaction.Right":
                ApplyInteraction(map, action, Chirality.Right);
                break;
            case "Menu":
                ApplyMenu(map, action);
                break;
            case "Camera":
                ApplyCamera(map, action);
                break;
            case "Editing":
                ApplyEditing(map, action);
                break;
        }

        foreach (var binding in action.Bindings)
            binding.IsDefault = true;
    }

    private static void ApplyLocomotion(InputBindingMap map, InputAction action)
    {
        var loco = map.Locomotion;

        if (action == loco.Move)
        {
            BindWasd(action);
            action.Bind(ControlRef.VR(Chirality.Left, Cat.VRStick));
            action.Bind(ControlRef.Pad(Cat.PadLeftStick));
        }
        else if (action == loco.Turn)
        {
            action.Bind(ControlRef.VR(Chirality.Right, Cat.VRStick), BindingAxis.X);
            action.Bind(ControlRef.Pad(Cat.PadRightStick), BindingAxis.X);
        }
        else if (action == loco.Fly)
        {
            action.Bind(ControlRef.Key(Key.Space), scale: 1f);
            action.Bind(ControlRef.Key(Key.C), scale: -1f);
            // The right stick's spare axis: turn owns X, so Y is free for altitude.
            action.Bind(ControlRef.VR(Chirality.Right, Cat.VRStick), BindingAxis.Y);
            action.Bind(ControlRef.Pad(Cat.PadDPadUp), scale: 1f);
            action.Bind(ControlRef.Pad(Cat.PadDPadDown), scale: -1f);
            // Matches the old helper: anything under a fifth of a stick's travel is noise, and the
            // remaining range is NOT rescaled so a light push still means a slow climb.
            loco.Fly.Deadzone = 0.2f;
            loco.Fly.RescaleDeadzone = false;
        }
        else if (action == loco.Jump)
        {
            action.Bind(ControlRef.Key(Key.Space));
            // Stick click, not the face buttons: A/X on both hands both jumping left nothing for the
            // context menu and equip actions to sit on. -xlinka
            action.Bind(ControlRef.VR(Chirality.Left, Cat.VRStickPress));
            action.Bind(ControlRef.VR(Chirality.Right, Cat.VRStickPress));
            action.Bind(ControlRef.Pad(Cat.PadFaceDown));
        }
        else if (action == loco.Sprint)
        {
            action.Bind(ControlRef.Key(Key.LeftShift));
            action.Bind(ControlRef.Key(Key.RightShift));
            action.Bind(ControlRef.Pad(Cat.PadLeftShoulder));
        }
        else if (action == loco.Crouch)
        {
            // C, not ctrl: ctrl is the desktop resize modifier and cannot double as crouch.
            action.Bind(ControlRef.Key(Key.C));
            action.Bind(ControlRef.Pad(Cat.PadLeftTrigger));
        }
        else if (action == loco.ToggleMouseCapture)
        {
            action.Bind(ControlRef.Key(Key.Escape));
        }
        else if (action == loco.ScaleUser)
        {
            action.Bind(new InputBinding(ControlRef.Mouse(Cat.MouseWheel), modifiers: new[] { LeftCtrl }));
            action.Bind(new InputBinding(ControlRef.Mouse(Cat.MouseWheel), modifiers: new[] { RightCtrl }));
        }
        else if (action == loco.LeftStick)
        {
            // Per-hand raw sticks stay VR-only. A pad's left stick already means "walk", and letting
            // it also read as a hand tilt would make strafing turn you as well.
            action.Bind(ControlRef.VR(Chirality.Left, Cat.VRStick));
        }
        else if (action == loco.RightStick)
        {
            action.Bind(ControlRef.VR(Chirality.Right, Cat.VRStick));
        }
        else if (action == loco.BlinkAim)
        {
            action.Bind(ControlRef.Key(Key.T));
            action.Bind(ControlRef.Pad(Cat.PadFaceRight));
        }
        else if (action == loco.BlinkBackstep)
        {
            action.Bind(ControlRef.Key(Key.G));
            action.Bind(ControlRef.Pad(Cat.PadRightStickPress));
        }
    }

    private static void ApplyInteraction(InputBindingMap map, InputAction action, Chirality side)
    {
        var hand = map.Interaction(side);
        // One mouse, one keyboard: the desktop hand is the right one. The left hand is VR-only.
        bool desktopHand = side == Chirality.Right;

        if (action == hand.Primary)
        {
            action.Bind(ControlRef.VR(side, Cat.VRTrigger));
            if (desktopHand)
            {
                action.Bind(ControlRef.Mouse(Cat.MouseLeft));
                action.Bind(ControlRef.Pad(Cat.PadRightTrigger));
            }
        }
        else if (action == hand.Grab)
        {
            action.Bind(ControlRef.VR(side, Cat.VRGrip));
            if (desktopHand)
            {
                action.Bind(ControlRef.Mouse(Cat.MouseRight));
                action.Bind(ControlRef.Pad(Cat.PadRightShoulder));
            }
        }
        else if (action == hand.Secondary)
        {
            action.Bind(ControlRef.VR(side, Cat.VRSecondary));
            if (desktopHand)
            {
                action.Bind(ControlRef.Key(Key.R));
                action.Bind(ControlRef.Pad(Cat.PadFaceLeft));
            }
        }
        else if (action == hand.ContextMenu)
        {
            // Middle click and T each toggle on their own edge, which is why both are here rather
            // than one being a modifier of the other.
            if (desktopHand)
            {
                action.Bind(ControlRef.Mouse(Cat.MouseMiddle));
                action.Bind(ControlRef.Key(Key.T));
                action.Bind(ControlRef.Pad(Cat.PadFaceUp));
            }
        }
        else if (action == hand.HoldFreeze)
        {
            if (desktopHand)
                action.Bind(ControlRef.Key(Key.E));
        }
        else if (action == hand.HoldModifier)
        {
            action.Bind(ControlRef.VR(side, Cat.VRSecondary));
            if (desktopHand)
            {
                action.Bind(ControlRef.Key(Key.LeftShift));
                action.Bind(ControlRef.Key(Key.RightShift));
            }
        }
        else if (action == hand.HoldScroll)
        {
            if (desktopHand)
            {
                action.Bind(ControlRef.Mouse(Cat.MouseWheel));
                action.Bind(ControlRef.Pad(Cat.PadDPadUp), scale: 1f);
                action.Bind(ControlRef.Pad(Cat.PadDPadDown), scale: -1f);
            }
        }
        else if (action == hand.HoldLook)
        {
            if (desktopHand)
                action.Bind(ControlRef.Mouse(Cat.MouseDelta));
        }
        else if (action == hand.HoldAxis)
        {
            // VR only, and left unbound elsewhere on purpose: outside VR a carried object is moved
            // with the scroll/rotate controls below, so a pad binding here would sit in the controls
            // screen looking real while doing nothing.
            action.Bind(ControlRef.VR(side, Cat.VRStick));
        }
        else if (action == hand.Pointer)
        {
            action.Bind(ControlRef.VR(side, Cat.VRStick));
            if (desktopHand)
                action.Bind(ControlRef.Mouse(Cat.MouseWheel), target: BindingAxis.Y);
        }
        else if (action == hand.Stick)
        {
            action.Bind(ControlRef.VR(side, Cat.VRStick));
            action.Bind(ControlRef.Pad(side == Chirality.Left ? Cat.PadLeftStick : Cat.PadRightStick));
        }
    }

    private static void ApplyMenu(InputBindingMap map, InputAction action)
    {
        if (action == map.Menu.ToggleDashboard)
        {
            action.Bind(ControlRef.Key(Key.Escape));
            action.Bind(ControlRef.Pad(Cat.PadStart));
            action.Bind(ControlRef.VR(Chirality.Left, Cat.VRMenu));
            action.Bind(ControlRef.VR(Chirality.Right, Cat.VRMenu));
        }
    }

    private static void ApplyCamera(InputBindingMap map, InputAction action)
    {
        var camera = map.Camera;

        if (action == camera.ThirdPerson)
            action.Bind(ControlRef.Key(Key.F5));
        else if (action == camera.FreeCam)
            action.Bind(ControlRef.Key(Key.F6));
        else if (action == camera.FlyMove)
            BindWasd(action);
        else if (action == camera.FlyVertical)
        {
            action.Bind(ControlRef.Key(Key.Space), scale: 1f);
            action.Bind(LeftCtrl, scale: -1f);
            action.Bind(RightCtrl, scale: -1f);
        }
        else if (action == camera.FlyFast)
        {
            action.Bind(ControlRef.Key(Key.LeftShift));
            action.Bind(ControlRef.Key(Key.RightShift));
        }
        else if (action == camera.OrbitZoom)
            action.Bind(ControlRef.Mouse(Cat.MouseWheel));
    }

    private static void ApplyEditing(InputBindingMap map, InputAction action)
    {
        if (action == map.Editing.Paste)
        {
            action.Bind(new InputBinding(ControlRef.Key(Key.V), modifiers: new[] { LeftCtrl }));
            action.Bind(new InputBinding(ControlRef.Key(Key.V), modifiers: new[] { RightCtrl }));
        }
    }

    private static void BindWasd(InputAction action)
    {
        action.Bind(ControlRef.Key(Key.W), target: BindingAxis.Y, scale: 1f);
        action.Bind(ControlRef.Key(Key.S), target: BindingAxis.Y, scale: -1f);
        action.Bind(ControlRef.Key(Key.A), target: BindingAxis.X, scale: -1f);
        action.Bind(ControlRef.Key(Key.D), target: BindingAxis.X, scale: 1f);
    }
}
