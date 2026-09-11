// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

[ComponentCategory("Interaction/Tools")]
public abstract class ToolItem : Component
{
    private static readonly float[] EquipMenuColor = { 0.30f, 0.22f, 0.12f, 0.92f };
    public readonly SyncRef<Slot> TipReference = new();
    public readonly Sync<bool> BlockGripEquip = new();
    public readonly Sync<bool> BlockRemoteEquip = new();
    public readonly Sync<string> EquipName = new();
    public readonly SyncRef<Tool> OverrideActiveTool = new();

    // Equipped = the equip link is set (EquipToolItem wires it both ways). NOT a parent walk: a merely HELD
    // tool is also parented under the hand (grabber holder), so walking parents for a Tool would read a
    // grabbed-but-not-equipped item as equipped and lock it out of every equip route. - xlinka
    public Tool? ActiveTool => OverrideActiveTool.Target;

    public bool IsEquipped => ActiveTool != null;

    public float3 Tip => Slot.LocalPointToGlobal(LocalTip);

    // The slot carrying this tool's in-hand visual, when it has one. Read by the hand's TOOLHOLD trace
    // to say where the thing being held actually is in the world; null means "the tool's own slot".
    public virtual Slot? HeldVisual => null;

    public virtual float3 TipForward => TipReference.Target?.Forward ?? Slot.Forward;

    public virtual float3 LocalTip
    {
        get
        {
            if (TipReference.Target == null)
            {
                return DefaultLocalTip;
            }

            if (ReferenceEquals(TipReference.Target.Parent, Slot))
            {
                return TipReference.Target.LocalPosition.Value;
            }

            return Slot.GlobalPointToLocal(TipReference.Target.GlobalPosition);
        }
    }

    protected virtual float3 DefaultLocalTip => float3.Zero;

    public virtual float3 InteractionOrigin => ActiveTool?.Laser?.RayOrigin ?? Tip;

    public virtual float3 InteractionDirection => ActiveTool?.Laser?.RayDirection ?? TipForward;

    public virtual bool UsesLaser => false;

    public virtual bool UsesLaserGrip => false;

    public virtual bool BlocksPrimaryWhenTouching => true;

    public virtual bool UsesSecondary => true;

    public virtual bool CanUseWhenHolding => false;

    public virtual bool IsInUse => ActiveTool?.PrimaryHeld == true;

    // Whether this user may take the item into a hand at all. Base items are ordinary props and say
    // yes; the build tools answer the permission gate. Asked by the hand before it equips, so a
    // refusal is a tool that will not be picked up rather than one that throws on first press.
    public virtual bool AllowsEquip(User? user) => true;

    public override void OnAttach()
    {
        base.OnAttach();
        // A tool in the world is an ordinary grabbable prop. BlockWhenWorn doubles as the "can't grab while
        // equipped" guard: an equipped tool sits under the user root, same as a worn avatar. - xlinka
        var grabbable = Slot.GetComponent<Grabbable>() ?? Slot.AttachComponent<Grabbable>();
        grabbable.BlockWhenWorn.Value = true;
    }

    public override void OnStart()
    {
        base.OnStart();
        // Touch-to-equip: primary-click on the tool pops an "Equip <name>" confirm. The RayTarget persists but
        // its Activated event doesn't, so re-hook every session (OnStart, not OnAttach). Priority beats the
        // sibling Grabbable for the hovered target; grab resolves the Grabbable by parent-walk regardless. -xlinka
        var target = Slot.GetComponent<RayTarget>() ?? Slot.AttachComponent<RayTarget>();
        if (target.InteractionPriority.Value < 10)
            target.InteractionPriority.Value = 10;
        if (target.HoverRadius.Value < 0.15f)
            target.HoverRadius.Value = 0.15f;
        target.Activated += _ => ConfirmEquip();
    }

    private void ConfirmEquip()
    {
        if (IsEquipped || BlockRemoteEquip.Value || IsDestroyed)
            return;
        // Held items equip through the held-object context menu, not touch (the grab state has to be released
        // by the same code that equips, or the grabber is left pointing at a docked slot). - xlinka
        if (Slot.GetComponent<Grabbable>()?.IsGrabbed == true)
            return;

        var userRootSlot = World?.LocalUser?.Root?.Slot;
        if (userRootSlot == null)
            return;

        // Owner hand = right on desktop (the menu-owning hand), the pointing hand in VR. Same owner resolution
        // as the avatar equip confirm: the wrong hand means no camera freeze and the menu edge-closes. - xlinka
        bool vr = Engine.Current?.InputInterface?.IsVRActive == true;
        var rayTarget = Slot.GetComponent<RayTarget>();
        HandTool? owner = null;
        foreach (var hand in userRootSlot.GetComponentsInChildren<HandTool>())
        {
            bool isOwner = vr
                ? (hand.Laser != null && ReferenceEquals(hand.Laser.CurrentRayTarget, rayTarget))
                : hand.Side.Value == Input.Chirality.Right;
            if (!isOwner)
                continue;
            owner = hand;
            break;
        }
        if (owner == null)
            return;

        var menu = userRootSlot.GetComponentInChildren<UI.ContextMenuSystem>();
        if (menu == null)
        {
            owner.EquipToolItem(this);
            return;
        }
        if (menu.IsOpen.Value)
            return;

        string name = string.IsNullOrWhiteSpace(EquipName.Value) ? Slot.SlotName.Value ?? "Tool" : EquipName.Value;
        var ctx = new UI.ContextMenuContext { Target = Slot, Pointer = owner.Laser?.Slot, Side = owner.Side.Value };
        var hand2 = owner;
        menu.OpenConfirm($"Equip {name}?", "Equip Tool", EquipMenuColor, () => hand2.EquipToolItem(this), ctx);
    }

    internal void SetActiveTool(Tool? tool)
    {
        OverrideActiveTool.Target = tool!;
    }

    public virtual bool UpdateTool(float primaryStrength, float2 secondaryAxis)
    {
        return false;
    }

    public virtual bool IsInteractionTarget(Slot target)
    {
        return true;
    }

    public virtual bool IsMovingTarget(Slot target)
    {
        return false;
    }

    public virtual float3? OverrideTargetPoint(float3 origin, float3 direction)
    {
        return null;
    }

    public virtual bool OnPrimaryPress()
    {
        return false;
    }

    public virtual bool OnPrimaryHold()
    {
        return false;
    }

    public virtual bool OnPrimaryRelease()
    {
        return false;
    }

    public virtual bool OnSecondaryPress()
    {
        return false;
    }

    public virtual bool OnSecondaryHold()
    {
        return false;
    }

    public virtual bool OnSecondaryRelease()
    {
        return false;
    }

    public virtual bool OnGrabbing(bool isLaserGrab)
    {
        return false;
    }

    public virtual void OnEquipped()
    {
    }

    public virtual void OnDequipped()
    {
    }
}
