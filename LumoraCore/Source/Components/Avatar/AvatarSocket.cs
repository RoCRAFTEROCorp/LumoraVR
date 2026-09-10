// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

// A slot that can have avatar objects equipped to it for a specific body node.
// Used by TrackedDevicePositioner to create body node tracking points.
[ComponentCategory("Users/Avatar")]
public class AvatarSocket : UserRootComponent
{
    // The currently equipped avatar object. Synced so every peer can see
    // equip state (pose nodes derive their drive links from it, and
    // FillEmptySockets must not re-fill slots the authority already filled).
    public readonly SyncRef<IAvatarEquippable> Equipped = null!;

    public readonly Sync<BodyNode> Node = new();

    public readonly Sync<bool> IsTracking = new();

    public readonly Sync<bool> IsActive = new();

    public readonly Sync<bool> DriveActive = new();

    public readonly Sync<bool> DriveScale = new();

    // Using a simple list since IPoseFilter doesn't implement IWorldElement.
    private readonly List<IPoseFilter> _filters = new();

    public IEnumerable<IPoseFilter> Filters => _filters;

    public void AddFilter(IPoseFilter filter)
    {
        if (filter != null && !_filters.Contains(filter))
            _filters.Add(filter);
    }

    public void RemoveFilter(IPoseFilter filter)
    {
        _filters.Remove(filter);
    }

    public bool HasEquipped => Equipped?.Target != null;

    // Internal state
    private UserRoot _userRoot = null!;

    private PoseSmoother _autoSmoothing = null!;

    public override void OnAwake()
    {
        base.OnAwake();
        Node.OnChanged += _ => RefreshAutoSmoothing();
    }

    // Hips/feet jitter visibly without smoothing in any IK/tracker setup
    // (controller skew, network noise). Head and hands stay direct so input
    // doesn't gain latency. - xlinka
    private static bool ShouldAutoSmooth(BodyNode node)
        => node == BodyNode.Hips || node == BodyNode.LeftFoot || node == BodyNode.RightFoot;

    private void RefreshAutoSmoothing()
    {
        // Node syncs, so OnChanged fires on every peer. Only the authority
        // mutates the slot tree; the smoothing components replicate from there.
        if (World?.IsAuthority != true)
            return;

        if (ShouldAutoSmooth(Node.Value))
        {
            if (_autoSmoothing == null)
            {
                var smoothSlot = Slot.AddSlot("AutoSmoothing");
                _autoSmoothing = smoothSlot.AttachComponent<PoseSmoother>();
                _autoSmoothing.PositionSmoothSpeed.Value = -1f;
                _autoSmoothing.RotationSmoothSpeed.Value = 20f;
                AddFilter(_autoSmoothing);
            }
        }
        else if (_autoSmoothing != null)
        {
            RemoveFilter(_autoSmoothing);
            _autoSmoothing.Slot?.Destroy();
            _autoSmoothing = null!;
        }
    }

    // The filter list is per-peer, but pose computation now runs on every
    // peer. Non-authority peers pick up the replicated smoothing component
    // here once it arrives; the enum gate keeps the FindChild walk off the
    // hot path for nodes that never smooth.
    private void EnsureReplicatedSmoothing()
    {
        if (_autoSmoothing != null || !ShouldAutoSmooth(Node.Value))
            return;

        var smoothing = Slot?.FindChild("AutoSmoothing", recursive: false)?.GetComponent<PoseSmoother>();
        if (smoothing != null)
        {
            _autoSmoothing = smoothing;
            AddFilter(smoothing);
        }
    }

    public override void OnInit()
    {
        base.OnInit();
        // BodyNode.NONE is not enum value 0 - set it explicitly
        Node.Value = BodyNode.NONE;
        // IsTracking defaults to true (not C# default false)
        IsTracking.Value = true;
        // IsActive defaults to true
        IsActive.Value = true;
        // DriveActive = false (C# default, skip)
        // DriveScale = false (C# default, skip)
    }

    public override void OnStart()
    {
        base.OnStart();
        FindUserRoot();
    }

    private void FindUserRoot()
    {
        _userRoot = Slot?.ActiveUserRoot!;
    }

    public new bool IsUnderLocalUser
    {
        get
        {
            if (_userRoot == null)
                FindUserRoot();
            return _userRoot?.ActiveUser == World?.LocalUser;
        }
    }

    // Pre-equip an avatar object - dequips any existing object first.
    public bool PreEquip(IAvatarEquippable avatarObject, HashSet<IAvatarEquippable> dequippedObjects)
    {
        if (avatarObject.Node == Node.Value)
        {
            Dequip(dequippedObjects);

            // Call OnPreEquip on all IAvatarEquipReceiver in the avatar's slot
            ForeachObjectComponent((avatarObject as Component)!, (c) =>
            {
                try
                {
                    c.OnPreEquip(this);
                }
                catch (Exception ex)
                {
                    LumoraLogger.Error($"Exception in OnPreEquip: {ex.Message}");
                }
            });

            return true;
        }
        return false;
    }

    public void Equip(IAvatarEquippable avatarObject)
    {
        Equipped.Target = avatarObject;
        avatarObject.Equip(this);

        // Call OnEquip on all IAvatarEquipReceiver
        ForeachObjectComponent((avatarObject as Component)!, (c) =>
        {
            try
            {
                c.OnEquip(this);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"Exception in OnEquip: {ex.Message}");
            }
        });

        LumoraLogger.Log($"AvatarSocket: Equipped {avatarObject.Node} to slot on '{Slot.SlotName.Value}'");
    }

    public void Dequip(HashSet<IAvatarEquippable> dequippedObjects)
    {
        if (Equipped?.Target == null)
            return;

        dequippedObjects?.Add(Equipped.Target);

        // Call OnDequip on all IAvatarEquipReceiver
        ForeachObjectComponent((Equipped.Target as Component)!, (c) =>
        {
            try
            {
                c.OnDequip(this);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"Exception in OnDequip: {ex.Message}");
            }
        });

        Equipped.Target.Dequip();
        Equipped.Target = null!;
    }

    public void ForeachObjectComponent(Action<IAvatarEquipReceiver> action)
    {
        if (Equipped?.Target is Component comp)
        {
            ForeachObjectComponent(comp, action);
        }
    }

    public static void ForeachObjectComponent(Component component, Action<IAvatarEquipReceiver> action)
    {
        if (component?.Slot == null)
            return;

        var components = new List<IAvatarEquipReceiver>();
        CollectObjectComponents(component.Slot, components);

        foreach (var c in components)
        {
            action(c);
        }
    }

    private static void CollectObjectComponents(Slot slot, List<IAvatarEquipReceiver> objectComponents)
    {
        // Get all IAvatarEquipReceiver from this slot
        foreach (var comp in slot.Components)
        {
            if (comp is IAvatarEquipReceiver avatarComp)
            {
                objectComponents.Add(avatarComp);
            }
        }

        // Recurse into children, but stop at AvatarSocket boundaries
        foreach (var child in slot.Children)
        {
            if (child.GetComponent<AvatarSocket>() == null)
            {
                CollectObjectComponents(child, objectComponents);
            }
        }
    }

    // Applies all pose filters to the raw tracking data.
    public Slot GetFilteredPose(out float3 position, out floatQ rotation, out bool isTracking)
    {
        if (_userRoot == null)
            FindUserRoot();

        var space = _userRoot?.Slot;
        if (space == null)
        {
            position = float3.Zero;
            rotation = floatQ.Identity;
            isTracking = false;
            return Slot;
        }

        EnsureReplicatedSmoothing();

        // Convert this slot's world transform into UserRoot-local space.
        // Using global->local conversion handles any depth of nesting under UserRoot
        // (e.g. UserRoot > Body Nodes > Head > BodyNode) correctly.
        position   = space.GlobalPointToLocal(Slot.GlobalPosition);
        rotation   = space.GlobalRotation.Inverse * Slot.GlobalRotation;
        isTracking = IsTracking.Value;

        // Apply all filters
        foreach (var filter in Filters)
        {
            filter?.ProcessPose(this, space, ref position, ref rotation, ref isTracking);
        }

        return space;
    }
}
