// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Components.Interaction;

namespace Lumora.Core.Components.Avatar;

// Avatar root tag + IAvatarEquippable implementation. Lives on the root slot of
// an avatar hierarchy. When the user equips this avatar through AvatarEquipManager,
// the root reparents under the user's AvatarSocket tagged with
// BodyNode.Root (always present on AvatarEquipManager). MaxValue priority means
// the root is processed last after all per-body-node IAvatarEquippables on the
// tree are already dispatched. - xlinka
[ComponentCategory("Users/Avatar")]
public class AvatarForm : Component, IAvatarEquippable
{
    public readonly SyncRef<UserRoot> Owner = new();
    public readonly Sync<bool> IsActive = new();

    public readonly Sync<float3> Scale = new();

    public BodyNode Node => BodyNode.Root;
    public int EquipPriority => int.MaxValue;
    public bool IsEquipped => CurrentSocket != null;

    public AvatarSocket CurrentSocket
    {
        get
        {
            // When equipped via the new dispatch, our slot is reparented under
            // the AvatarSocket.Slot. Walk parents looking for one.
            var current = Slot?.Parent;
            while (current != null)
            {
                var objSlot = current.GetComponent<AvatarSocket>();
                if (objSlot != null && objSlot.Node.Value == BodyNode.Root)
                    return objSlot;
                current = current.Parent;
            }
            return null!;
        }
    }

    public IEnumerable<BodyNode> ConflictingNodes
    {
        get { yield break; }
    }

    public User AllowedEquipUser { get; private set; } = null!;

    public override void OnInit()
    {
        base.OnInit();
        IsActive.Value = true;
        Scale.Value = float3.One;
    }

    // CLICK-TO-EQUIP
    //
    // This lives on the avatar root component rather than on whatever built the avatar, because it has to
    // hold for EVERY avatar: one made in the studio, one imported from a package, one loaded back out of
    // inventory. It used to be wired by the studio's creation flow alone, so an imported avatar had no
    // click behaviour at all and the only way to wear it was the radial menu on middle-click - which is
    // not where anyone looks for it.
    //
    // Re-wired in OnStart, not once at creation: RayTarget.Activated is a plain C# event, so nothing
    // survives a save and reload. The component that owns the behaviour has to re-establish it every time
    // the avatar comes back. -xlinka
    private RayTarget _equipTarget = null!;

    public override void OnStart()
    {
        base.OnStart();
        EnsureEquipTarget();
    }

    public void EnsureEquipTarget()
    {
        if (Slot == null || Slot.IsDestroyed || _equipTarget != null)
            return;

        // An avatar someone is already WEARING is not an offer. Without this, every remote player in the
        // room grows a half-metre laser target on their body that steals hover from whatever is behind
        // them and does nothing when clicked. The confirm refuses a worn avatar anyway; this stops the
        // target existing in the first place. -xlinka
        if (Slot.ActiveUserRoot != null)
            return;

        var target = Slot.GetComponent<RayTarget>() ?? Slot.AttachComponent<RayTarget>();
        target.HoverRadius.Value = 0.5f;
        // Beat the root Grabbable for the laser's hovered target so a LEFT-click (use/interact) lands on this
        // RayTarget. Grab is GRIP/right-click and resolves the Grabbable by walking parents, so it's unaffected.
        target.InteractionPriority.Value = 10;
        target.Activated += OnEquipRayActivated;
        _equipTarget = target;
    }

    private void OnEquipRayActivated(float3 _) => ConfirmEquip();

    // Left-click (use) on the avatar pops a small "Equip Avatar / Cancel" confirm, then equips.
    // Falls back to a direct equip if no context menu is available.
    private void ConfirmEquip()
    {
        var avatar = Slot;
        if (avatar == null || avatar.IsDestroyed)
            return;

        // Already worn: nothing to offer.
        if (IsEquipped || avatar.ActiveUserRoot != null)
            return;

        var userRootSlot = World?.LocalUser?.Root?.Slot;
        var menu = userRootSlot?.GetComponentInChildren<UI.ContextMenuSystem>();
        if (userRootSlot == null || menu == null)
        {
            TryEquip();
            return;
        }

        // Idempotent: the activation can fire repeatedly while the laser sits on the avatar, and
        // re-opening rebuilds the whole menu visual every frame.
        if (menu.IsOpen.Value)
            return;

        // Anchor the confirm to the hand that OWNS the menu, so the camera-freeze / mouse-aim AND the opening-press
        // guard engage (both key off context.Side, and the desktop aim only runs for the owner hand). On DESKTOP the
        // menu is right-hand-owned; in VR it's the hand whose laser is on the avatar. Matching the wrong hand made
        // the camera not freeze, so moving the mouse turned the view and the menu edge-closed.
        bool vr = Engine.Current?.InputInterface?.IsVRActive == true;
        var ctx = new UI.ContextMenuContext { Target = avatar };
        foreach (var hand in userRootSlot.GetComponentsInChildren<HandTool>())
        {
            bool isOwner = vr
                ? (hand.Laser != null && ReferenceEquals(hand.Laser.CurrentRayTarget, _equipTarget))
                : hand.Side.Value == Chirality.Right;
            if (!isOwner)
                continue;
            ctx.Pointer = hand.Laser?.Slot;
            ctx.Side = hand.Side.Value;
            break;
        }

        menu.OpenConfirm("Equip Avatar?", "Equip Avatar", new[] { 0.14f, 0.30f, 0.18f, 0.92f }, TryEquip, ctx);
    }

    private void TryEquip()
    {
        var avatar = Slot;
        if (avatar == null || avatar.IsDestroyed)
            return;

        var userRoot = World?.LocalUser?.Root;
        if (userRoot == null)
        {
            Logging.Logger.Warn("AvatarForm: no local user root to equip onto");
            return;
        }
        var manager = userRoot.Slot.GetComponent<AvatarEquipManager>() ?? userRoot.Slot.AttachComponent<AvatarEquipManager>();
        if (manager.UserRoot.Target == null)
            manager.UserRoot.Target = userRoot;
        manager.EquipAvatar(avatar);
    }

    public override void OnDestroy()
    {
        if (_equipTarget != null && !_equipTarget.IsDestroyed)
            _equipTarget.Activated -= OnEquipRayActivated;
        _equipTarget = null!;
        base.OnDestroy();
    }

    public void Equip(AvatarSocket slot)
    {
        if (slot == null || slot.Slot == null) return;

        Slot.SetParent(slot.Slot);
        Slot.LocalPosition.Value = float3.Zero;
        Slot.LocalRotation.Value = floatQ.Identity;
        // Only restore a CALIBRATED scale. Scale defaults to One and is only set once AvatarIK.MaybeRescaleAvatar
        // has fit the avatar to the user. On the FIRST equip it's still One - writing that here would discard the
        // import-compensation scale (which cancels the FBX armature's ~54x) and the avatar renders massive. Keep
        // the existing LocalScale until the rescale provides a real value. -xlinka
        var sc = Scale.Value;
        bool calibrated = System.MathF.Abs(sc.x - 1f) > 1e-5f
                       || System.MathF.Abs(sc.y - 1f) > 1e-5f
                       || System.MathF.Abs(sc.z - 1f) > 1e-5f;
        if (calibrated)
            Slot.LocalScale.Value = sc;

        Owner.Target = slot.Slot.ActiveUserRoot;
        IsActive.Value = true;
    }

    public void Dequip()
    {
        Owner.Target = null!;
        IsActive.Value = false;
    }

    public void AllowEquip(User user)
    {
        if (AllowedEquipUser != null && user != AllowedEquipUser)
            throw new System.InvalidOperationException("Another user has already been assigned!");
        AllowedEquipUser = user;
    }
}
