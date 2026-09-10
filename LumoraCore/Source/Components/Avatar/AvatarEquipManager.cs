// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Input;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// Per-user avatar coordinator. Dispatches IAvatarEquippables from a target avatar
// slot tree onto the user's AvatarSockets by matching BodyNode. Allows
// a custom avatar's hands + default headset + default view to coexist on
// the same user since each body node is filled independently. - xlinka
[ComponentCategory("Users/Avatar")]
public class AvatarEquipManager : UserRootComponent
{
    public readonly SyncRef<Slot> CurrentAvatar = new();
    public readonly SyncRef<UserRoot> UserRoot = new();
    public readonly SyncRef<Slot> DefaultAvatarSlot = new();
    public readonly Sync<bool> IsUsingDefaultAvatar = new();

    // Plug-in supplier for default head/hands/view pieces when a body-node
    // slot has nothing equipped. AvatarAssembler installs itself here on
    // user spawn. Set Target to null to disable auto-filling.
    [OldName("EmptySlotHandler")]
    public readonly SyncRef<IAvatarSocketFiller> SocketFiller = new();

    private CancellationTokenSource _fillCts = null!;
    private bool _isFillingEmptySlots;

    // Display name/color values used by name badges/tags. Per-user state that
    // NameBadgeDriver pushes into avatar text components.
    [OldName("NameTagText")]
    public readonly Sync<string> BadgeText = new();
    [OldName("NameTagColor")]
    public readonly Sync<color> BadgeColor = new();
    [OldName("NameTagOutline")]
    public readonly Sync<color> BadgeOutline = new();
    [OldName("NameTagBackground")]
    public readonly Sync<color> BadgeBackground = new();

    // The trim ring drawn around the plate. This is the supporter-reward surface: one authored colour
    // that every peer draws, so whatever grants a tier writes it HERE and the plate follows. Additive
    // and name-keyed like every other member, so a save written before it existed loads the quiet
    // neutral below rather than a colour nobody picked. -xlinka
    public readonly Sync<color> BadgeRim = new();

    // Whether this user gets the built-in nameplate. NameplateManager reads it live and hides itself
    // when it goes false or when an equipped avatar brings its own NameBadgeDriver.
    public readonly Sync<bool> AutoAddNameBadge = new();

    // Handle on the REPLICATED name badge the old inline builder made. Kept only so a session or save
    // carrying one can be cleaned up: the plate is composed locally per peer now and nothing new is ever
    // written here. -xlinka
    private readonly SyncRef<Slot> _autoNameBadge = new();

    public readonly SyncRefList<Slot> AvailableAvatars;

    public event Action<Slot> OnAvatarChanged = null!;

    public bool IsEquippingManually { get; private set; }

    public AvatarEquipManager()
    {
        AvailableAvatars = new SyncRefList<Slot>(this);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        BadgeColor.Value = color.White;
        BadgeOutline.Value = color.Black;
        BadgeBackground.Value = new color(0f, 0f, 0f, 0.6f);
        BadgeRim.Value = new color(0.14f, 0.15f, 0.19f, 0.85f);
        AutoAddNameBadge.Value = true;
        BadgeText.OnChanged += _ => UpdateBadges();
        BadgeColor.OnChanged += _ => UpdateBadges();
        BadgeOutline.OnChanged += _ => UpdateBadges();
        Logger.Debug($"AvatarEquipManager: Awake on slot '{Slot.SlotName.Value}'");
    }

    public void UpdateBadges()
    {
        if (Slot == null || World?.IsAuthority != true)
            return;

        foreach (var assigner in Slot.GetComponentsInChildren<NameBadgeDriver>())
        {
            assigner.UpdateBadge(this);
        }
    }

    // Compose the built-in nameplate for this user.
    //
    // Runs on EVERY peer, not just the authority: the plate and everything under it is local, so each
    // machine builds its own out of state that already replicated. There is nothing here for the
    // authority to hand anybody. -xlinka
    private void EnsureNameplate()
    {
        if (Slot == null || World == null)
            return;

        TrackDisplayName();
        DestroyLegacyBadge();

        var existing = Slot.FindChild(NameplateManager.RootSlotName, recursive: false);
        if (existing != null && !existing.IsDestroyed)
        {
            if (existing.GetComponent<NameplateManager>() != null)
                return;
            // A stray root with no manager on it: a half-built plate from a torn-down equip, or an
            // orphan of the same name out of an old tree. Take the name back.
            existing.Destroy();
        }

        var root = Slot.AddLocalSlot(NameplateManager.RootSlotName);
        root.Persistent.Value = false;
        root.AttachComponent<NameplateManager>();
    }

    // The old inline builder put a REPLICATED "Name Badge" slot under this one. Nothing writes that
    // shape any more, so anything still carrying it - a save from before the plate went local, or a
    // session where one peer is older - gets it taken away rather than ending up with two names over
    // one head. Authority only: destroying a replicated slot is a mutation, and on an observer it would
    // be refused by the gate anyway.
    private void DestroyLegacyBadge()
    {
        if (World?.IsAuthority != true)
            return;

        var tracked = _autoNameBadge.Target;
        if (tracked != null && !tracked.IsDestroyed)
            tracked.Destroy();
        if (_autoNameBadge.Target != null)
            _autoNameBadge.Target = null!;

        foreach (var child in Slot.Children)
        {
            if (child == null || child.IsDestroyed || child.IsLocalElement)
                continue;
            if (child.SlotName.Value != LegacyBadgeSlotName)
                continue;
            if (child.GetComponent<NameBadgeDriver>() == null)
                continue;
            child.Destroy();
            break;
        }
    }

    private const string LegacyBadgeSlotName = "Name Badge";

    private User _nameSource = null!;
    private Action<IChangeable> _nameChanged = null!;

    // BadgeText is the replicated display-name channel every badge and plate reads, seeded from the
    // account name at spawn. Keep it ON that name, or a rename mid-session never reaches anybody's plate
    // and the room keeps calling you what you were called an hour ago. Authority only: it is a synced
    // field and a guest's write would be refused anyway. -xlinka
    private void TrackDisplayName()
    {
        var user = UserRoot.Target?.ActiveUser ?? Slot?.ActiveUserRoot?.ActiveUser;
        if (ReferenceEquals(user, _nameSource))
        {
            PushDisplayName();
            return;
        }

        UntrackDisplayName();
        if (user == null || user.IsDestroyed)
            return;

        _nameSource = user;
        _nameChanged = _ => PushDisplayName();
        user.UserName.Changed += _nameChanged;
        PushDisplayName();
    }

    private void PushDisplayName()
    {
        if (World?.IsAuthority != true || _nameSource == null || _nameSource.IsDestroyed)
            return;
        var name = _nameSource.UserName.Value ?? string.Empty;
        if (BadgeText.Value != name)
            BadgeText.Value = name;
    }

    private void UntrackDisplayName()
    {
        if (_nameSource != null && !_nameSource.IsDestroyed && _nameChanged != null)
            _nameSource.UserName.Changed -= _nameChanged;
        _nameSource = null!;
        _nameChanged = null!;
    }

    public override void OnDestroy()
    {
        UntrackDisplayName();
        base.OnDestroy();
    }

    public override void OnInit()
    {
        base.OnInit();
        IsUsingDefaultAvatar.Value = true;
    }

    public override void OnStart()
    {
        base.OnStart();
        ResolveUserRoot();
        EnsureRootObjectSlot();

        // Fill default head/hands/view once everything's wired. If the handler
        // isn't attached yet (SimpleUserSpawn sets it after AvatarEquipManager
        // attach), the fill is a no-op and EquipDefaultAvatar can re-trigger
        // it later. - xlinka
        FillEmptySockets();
        EnsureNameplate();
    }

    public bool Equip(Slot target, bool isManualEquip = false, bool forceDestroyOld = false, bool isFillingEmptySlot = false)
    {
        if (target == null || target.IsDestroyed)
            return false;

        ResolveUserRoot();
        var userRoot = UserRoot.Target ?? Slot.ActiveUserRoot;
        if (userRoot == null)
        {
            Logger.Warn("AvatarEquipManager: Cannot equip, no user root");
            return false;
        }

        IsEquippingManually = isManualEquip;

        var equipObjects = new List<IAvatarEquippable>();
        CollectEquippableObjects(target, equipObjects);
        if (equipObjects.Count == 0)
        {
            Logger.Warn($"AvatarEquipManager: No equippable IAvatarEquippable found on '{target.SlotName.Value}'");
            return false;
        }

        // Descending priority - Root last (it's MaxValue and reparents the rest).
        equipObjects.Sort((a, b) => -a.EquipPriority.CompareTo(b.EquipPriority));

        var objectSlots = new List<AvatarSocket>();
        var seenNodes = new HashSet<BodyNode>();
        foreach (var s in userRoot.GetRegisteredComponents<AvatarSocket>())
        {
            if (seenNodes.Add(s.Node.Value))
                objectSlots.Add(s);
        }

        var pairs = new List<(AvatarSocket objSlot, IAvatarEquippable obj)>();
        var dequipped = new HashSet<IAvatarEquippable>();

        foreach (var obj in equipObjects)
        {
            for (int i = 0; i < objectSlots.Count; i++)
            {
                var objSlot = objectSlots[i];
                if (!objSlot.PreEquip(obj, dequipped)) continue;
                pairs.Add((objSlot, obj));
                objectSlots.RemoveAt(i);
                break;
            }
        }

        if (pairs.Count == 0)
        {
            Logger.Warn($"AvatarEquipManager: No body-node slots matched on '{target.SlotName.Value}'");
            return false;
        }

        // A full-hand avatar piece dequips the controller slot (and similar):
        // collect every exclusive node declared by the incoming objects and
        // dequip those slots from the still-unmatched set.
        var exclusiveNodes = new HashSet<BodyNode>();
        foreach (var (_, obj) in pairs)
        {
            foreach (var node in obj.ConflictingNodes)
                exclusiveNodes.Add(node);
        }
        foreach (var node in exclusiveNodes)
        {
            for (int i = objectSlots.Count - 1; i >= 0; i--)
            {
                if (objectSlots[i].Node.Value == node)
                    objectSlots[i].Dequip(dequipped);
            }
        }

        // Dequipped trees must not linger under the manager - orphan them to
        // the world root so the old avatar doesn't accumulate as a hidden
        // child, then optionally destroy.
        foreach (var dq in dequipped)
        {
            if (dq is not Component dqc || dqc.Slot == null || dqc.Slot.IsDestroyed)
                continue;

            if (dqc.Slot.IsDescendantOf(Slot))
                dqc.Slot.SetParent(World.RootSlot);

            if (forceDestroyOld)
                dqc.Slot.Destroy();
        }

        foreach (var (objSlot, obj) in pairs)
        {
            objSlot.Equip(obj);
        }

        // Reparent the target under us so the dequipped tree doesn't leak in the world.
        // AvatarForm.Equip already reparents itself; this handles non-root targets.
        if (target != Slot && !target.IsDescendantOf(Slot))
        {
            target.SetParent(Slot);
        }

        CurrentAvatar.Target = target;
        IsUsingDefaultAvatar.Value = false;

        if (!AvailableAvatars.Contains(target))
            AvailableAvatars.Add(target);

        if (!isFillingEmptySlot)
        {
            FillEmptySockets(objectSlots);
        }

        EnsureNameplate();

        Logger.Log($"AvatarEquipManager: Equipped {pairs.Count} object(s) from '{target.SlotName.Value}'");
        OnAvatarChanged?.Invoke(target);
        return true;
    }

    // Walk the user's AvatarSockets and ask SocketFiller to fill any
    // that have nothing equipped. Called automatically after every non-fill
    // Equip(); also callable directly when the handler swaps. - xlinka
    //
    // Only the authority fills: Equipped/SocketFiller replicate to every
    // peer, so without the gate each client would see "empty" slots and spawn
    // duplicate default pieces.
    public void FillEmptySockets(List<AvatarSocket> candidateSlots = null!)
    {
        if (World?.IsAuthority != true) return;
        if (_isFillingEmptySlots) return;
        if (SocketFiller.Target == null) return;

        var userRoot = UserRoot.Target ?? Slot?.ActiveUserRoot;
        if (userRoot == null) return;

        // Don't equip into an untracked user - pieces would spawn at authored
        // defaults and visibly snap once the first head pose arrives. Re-check
        // every few frames until tracking is live (desktop reports true
        // immediately).
        if (!userRoot.ReceivedFirstPositionalData)
        {
            Slot?.RunInUpdates(10, () =>
            {
                if (!IsDestroyed)
                    FillEmptySockets(candidateSlots);
            });
            return;
        }

        _fillCts?.Cancel();
        _fillCts?.Dispose();
        _fillCts = new CancellationTokenSource();
        var token = _fillCts.Token;

        candidateSlots ??= userRoot.GetRegisteredComponents<AvatarSocket>();
        _isFillingEmptySlots = true;
        try
        {
            foreach (var slot in candidateSlots)
            {
                if (slot == null || slot.IsDestroyed) continue;
                if (slot.HasEquipped) continue;
                var handler = SocketFiller.Target;
                if (handler == null) break;
                _ = handler.FillSocket(slot.Node.Value, this, token);
            }
        }
        finally
        {
            _isFillingEmptySlots = false;
        }
    }

    public bool CanEquip(IAvatarEquippable avatarObject)
    {
        if (avatarObject == null) return false;
        if (avatarObject.IsEquipped) return false;
        if (avatarObject is Component c)
        {
            if (c.Slot == null || c.Slot.IsDestroyed) return false;
            // Already under a user - can't equip something already worn.
            if (c.Slot.ActiveUserRoot != null) return false;
        }
        if (Slot.ActiveUserRoot == null) return false;
        return true;
    }

    private void CollectEquippableObjects(Slot slot, List<IAvatarEquippable> output)
    {
        if (slot == null || slot.IsDestroyed) return;

        foreach (var c in slot.Components)
        {
            if (c is IAvatarEquippable obj && CanEquip(obj))
                output.Add(obj);
        }
        foreach (var child in slot.Children)
        {
            CollectEquippableObjects(child, output);
        }
    }

    private void EnsureRootObjectSlot()
    {
        if (Slot == null || Slot.ActiveUserRoot == null) return;

        var existing = Slot.GetComponent<AvatarSocket>();
        if (existing == null || existing.Node.Value != BodyNode.Root)
        {
            var rootObjSlot = Slot.AttachComponent<AvatarSocket>();
            rootObjSlot.Node.Value = BodyNode.Root;
        }
    }

    public void DequipCurrentAvatar()
    {
        var avatarSlot = CurrentAvatar.Target;
        if (avatarSlot == null) return;

        Logger.Log($"AvatarEquipManager: Dequipping '{avatarSlot.SlotName.Value}'");

        // Dequip every body-node slot whose equipped object lives under this
        // tree so OnDequip fires and pose-node drives release on all peers.
        var userRoot = UserRoot.Target ?? Slot?.ActiveUserRoot;
        if (userRoot != null)
        {
            var dequipped = new HashSet<IAvatarEquippable>();
            foreach (var objSlot in userRoot.GetRegisteredComponents<AvatarSocket>())
            {
                if (objSlot.Equipped?.Target is Component equipped && equipped.Slot != null &&
                    (equipped.Slot == avatarSlot || equipped.Slot.IsDescendantOf(avatarSlot)))
                {
                    objSlot.Dequip(dequipped);
                }
            }
        }
        else
        {
            ReleaseAvatarTracking(avatarSlot);
        }

        var avatarRoot = avatarSlot.GetComponent<AvatarForm>();
        if (avatarRoot != null)
            avatarRoot.IsActive.Value = false;

        // Don't leave the dead tree hidden under the manager.
        if (Slot != null && avatarSlot.IsDescendantOf(Slot))
            avatarSlot.SetParent(World.RootSlot);

        avatarSlot.ActiveSelf.Value = false;
        CurrentAvatar.Target = null!;
    }

    public void EquipDefaultAvatar()
    {
        Logger.Log("AvatarEquipManager: Equipping default avatar via SocketFiller");

        DequipCurrentAvatar();
        ResolveUserRoot();
        FillEmptySockets();
        IsUsingDefaultAvatar.Value = true;
    }

    public void RemoveAvatar(Slot avatarSlot)
    {
        if (avatarSlot == null) return;

        if (CurrentAvatar.Target == avatarSlot)
            EquipDefaultAvatar();

        if (avatarSlot == DefaultAvatarSlot.Target) return;

        AvailableAvatars.Remove(avatarSlot);
        avatarSlot.Destroy();
        Logger.Log("AvatarEquipManager: Removed avatar");
    }

    public int GetAvatarIndex(Slot avatarSlot)
    {
        for (int i = 0; i < AvailableAvatars.Count; i++)
        {
            if (AvailableAvatars[i] == avatarSlot)
                return i;
        }
        return -1;
    }

    public void CycleNextAvatar()
    {
        if (AvailableAvatars.Count == 0)
        {
            EquipDefaultAvatar();
            return;
        }
        int currentIndex = GetAvatarIndex(CurrentAvatar.Target);
        int nextIndex = (currentIndex + 1) % AvailableAvatars.Count;
        EquipAvatar(AvailableAvatars[nextIndex]!);
    }

    public void CyclePreviousAvatar()
    {
        if (AvailableAvatars.Count == 0)
        {
            EquipDefaultAvatar();
            return;
        }
        int currentIndex = GetAvatarIndex(CurrentAvatar.Target);
        int prevIndex = currentIndex - 1;
        if (prevIndex < 0) prevIndex = AvailableAvatars.Count - 1;
        EquipAvatar(AvailableAvatars[prevIndex]!);
    }

    // Single-avatar equip: an avatar is a self-describing component tree
    // (AvatarForm + skeleton + rig + pose nodes). If it has a rigged body,
    // wire IK; either way, dispatch through the body-node pipeline. No
    // finalize gate - matching how avatars work everywhere else. - xlinka
    public bool EquipAvatar(Slot avatarSlot)
    {
        if (avatarSlot == null || avatarSlot.IsDestroyed)
            return false;

        ResolveUserRoot();
        if (UserRoot.Target == null)
        {
            Logger.Warn("AvatarEquipManager: Cannot equip avatar without a UserRoot");
            return false;
        }

        if (avatarSlot.GetComponent<AvatarForm>() == null)
            avatarSlot.AttachComponent<AvatarForm>();

        // Rigged avatars get IK; unrigged trees still equip as plain
        // pose-node compositions.
        var skeleton = avatarSlot.GetComponentInChildren<SkeletonBuilder>();
        var rig = avatarSlot.GetComponentInChildren<HumanoidRig>();
        if (skeleton != null && rig != null)
        {
            // Root frame must face the body: equip resets this slot to identity, so a stale frame wears the
            // avatar yawed off the user's forward. World-invariant + reference slots are live transforms, so
            // healing an old avatar here is safe. No-op when already aligned.
            // Four legs get the quadruped solver, and get it INSTEAD - never alongside. Both write the
            // same bone rotations in the same phase at the same update order, and the dispatch sort is
            // not stable, so having both means the pose flickers frame to frame.
            (avatarSlot.GetComponent<QuadrupedRig>() ?? avatarSlot.GetComponentInChildren<QuadrupedRig>())
                ?.LogGirdleSpan("before-TryAttachFor");
            var quadrupedIk = QuadrupedIK.TryAttachFor(avatarSlot, skeleton, rig);
            quadrupedIk?.Rig.Target?.LogGirdleSpan("after-TryAttachFor");
            AvatarIK? avatarIk = null;
            if (quadrupedIk != null)
            {
                if (!quadrupedIk.SetupFromAvatar(UserRoot.Target))
                {
                    Logger.Warn("AvatarEquipManager: quadruped IK setup failed");
                    return false;
                }
            }
            else
            {
                AvatarCalibration.AlignAvatarFacing(avatarSlot, rig);

                avatarIk = avatarSlot.GetComponent<AvatarIK>() ?? avatarSlot.AttachComponent<AvatarIK>();
                if (!avatarIk.SetupFromAvatar(UserRoot.Target))
                {
                    Logger.Warn("AvatarEquipManager: Avatar IK setup failed");
                    return false;
                }
            }

            // Rigs with finger bones get a per-hand poser. It drives fingers only
            // from a live source (VR tracking); without one the hand rests.
            //
            // NOT on four legs. The HumanoidRig stays attached on a quadruped - it is the thing that got
            // us here, and the quadruped path is entered THROUGH it - so every humanoid consumer still
            // finds it and carries on as if the avatar were a person. On a fox the "hands" are front
            // paws: a hand poser curls them to whatever the controller's fingers are doing while the
            // quadruped solver is trying to plant them on the ground. -xlinka
            if (quadrupedIk == null)
            {
                if (rig.HasLeftFingerBones)
                    EnsureHandPoser(rig, Chirality.Left);
                if (rig.HasRightFingerBones)
                    EnsureHandPoser(rig, Chirality.Right);
            }

            // Coarse body colliders so a created/equipped avatar has grab/point hitboxes on every equip
            // path, not just model-import. Idempotent - skips bones that already have one.
            if (avatarIk != null)
                avatarIk.GenerateBodyColliders();
            else
                quadrupedIk?.GenerateBodyColliders();

            // Face drivers (blink/eye-look/eye-expression/mouth/viseme). The in-world AvatarStudio wires
            // these, but an import-built or directly-equipped avatar otherwise has a dead face. Attach the
            // same set here so every equip path gets it; idempotent, so a creator-built avatar isn't doubled.
            AttachFaceDrivers(avatarSlot, rig);

            FitBodyColliderToAvatar(quadrupedIk);
            AnchorNameplateToAvatar(quadrupedIk, rig);
        }

        var spanRig = avatarSlot.GetComponent<QuadrupedRig>() ?? avatarSlot.GetComponentInChildren<QuadrupedRig>();
        spanRig?.LogGirdleSpan("before-Equip");
        DequipCurrentAvatar();
        bool equipped = Equip(avatarSlot, isManualEquip: true);
        spanRig?.LogGirdleSpan("after-Equip");
        return equipped;
    }

    // Resize the wearer's character capsule to the avatar actually being worn.
    //
    // The capsule is built once for a standing person: 1.8 m tall, 0.3 m radius, centred at 0.9. Wear a
    // fox and you are still a person-shaped pillar, so you cannot get under anything, you collide with
    // walls a foot before you reach them, and the body the world pushes around has nothing to do with
    // the body on screen. CapsuleCollider is Y-axis only, so an animal cannot get a capsule along its
    // spine; what it CAN get is one that is the right height and the right width, which is the part
    // that decides where you fit. Restored to the standing defaults whenever a humanoid is worn, so
    // taking the fox off gives the person their own body back. -xlinka
    private void FitBodyColliderToAvatar(QuadrupedIK? quadrupedIk)
    {
        var userRoot = UserRoot.Target ?? Slot?.ActiveUserRoot;
        if (userRoot?.Slot == null || userRoot.Slot.IsDestroyed)
            return;

        CapsuleCollider? capsule = null;
        foreach (var candidate in userRoot.Slot.GetComponents<CapsuleCollider>())
        {
            if (candidate.Type.Value == Lumora.Core.Physics.ColliderType.CharacterController)
            {
                capsule = candidate;
                break;
            }
        }
        if (capsule == null)
            return;

        float height = HumanCapsuleHeight;
        float radius = HumanCapsuleRadius;

        if (quadrupedIk != null && quadrupedIk.TryGetBodyMetrics(out float standHeight, out _, out float width))
        {
            height = MathF.Max(standHeight, 0.2f);
            // Half the shoulder track, floored so a narrow animal still has a body, and capped at half
            // the height so the capsule cannot become a sphere wider than the thing is tall.
            radius = System.Math.Clamp(width * 0.5f, 0.1f, height * 0.5f);
        }

        capsule.Height.Value = height;
        capsule.Radius.Value = radius;
        capsule.Offset.Value = new float3(0f, height * 0.5f, 0f);

        Logger.Log($"AvatarEquipManager: body capsule fitted to {(quadrupedIk != null ? "quadruped" : "humanoid")} "
                 + $"height={height:F2} radius={radius:F2}");
    }

    // Put the nameplate on the AVATAR's head, not the wearer's tracked head node.
    //
    // The two are the same place on a person, which is why nothing ever distinguished them. On an animal
    // the wearer's head node stays at human standing height while the dog's head is out front and low,
    // so the plate hung over empty air. Resolved on every equip and cleared when nothing is worn.
    private void AnchorNameplateToAvatar(QuadrupedIK? quadrupedIk, HumanoidRig? rig)
    {
        var plateRoot = Slot?.FindChild(NameplateManager.RootSlotName, recursive: false);
        var positioner = plateRoot?.GetComponentInChildren<PositionAtUser>();
        if (positioner == null || positioner.IsDestroyed)
            return;

        var head = quadrupedIk != null
            ? quadrupedIk.HeadBone
            : rig?.TryGetBone(BodyNode.Head);

        positioner.Anchor.Target = head != null && !head.IsDestroyed ? head : null!;
    }

    private const float HumanCapsuleHeight = 1.8f;
    private const float HumanCapsuleRadius = 0.3f;

    // Attach the avatar's face-driver set, mirroring the in-world creator. Eye drivers gate on eye bones;
    // mouth/viseme drivers always attach (they rest when nothing is tracking). Each is added only if absent.
    // LipSyncAnalyzer has no audio pipeline yet - it's attached ready, but stays inert until one feeds it.
    private static void AttachFaceDrivers(Slot avatarSlot, HumanoidRig rig)
    {
        if (avatarSlot == null || avatarSlot.IsDestroyed)
            return;

        if (rig.TryGetBone(BodyNode.LeftEye) != null || rig.TryGetBone(BodyNode.RightEye) != null)
        {
            if (avatarSlot.GetComponent<BlinkDriver>() == null)
                avatarSlot.AttachComponent<BlinkDriver>();
            if (avatarSlot.GetComponent<EyeGazeDriver>() == null)
                avatarSlot.AttachComponent<EyeGazeDriver>();
            if (avatarSlot.GetComponent<EyeExpressionDriver>() == null)
                avatarSlot.AttachComponent<EyeExpressionDriver>();
        }

        // Mouth/lip blendshapes from replicated face tracking; rests when nothing is tracking.
        if (avatarSlot.GetComponent<MouthExpressionDriver>() == null)
            avatarSlot.AttachComponent<MouthExpressionDriver>();

        if (avatarSlot.GetComponent<LipSyncAnalyzer>() == null)
            avatarSlot.AttachComponent<LipSyncAnalyzer>();
        if (avatarSlot.GetComponent<VisemeWeightDriver>() == null)
            avatarSlot.AttachComponent<VisemeWeightDriver>();

        // Breath blendshape (chest/belly rise) on the same rhythm as the bone breathing; no-op when
        // the avatar has no breath shape. -xlinka
        if (avatarSlot.GetComponent<BreathingDriver>() == null)
            avatarSlot.AttachComponent<BreathingDriver>();
    }

    // Attach a HandPoseDriver (+ its equip-time assigner) to a hand's wrist bone, once.
    // The poser binds to the rig's finger bones itself and drives them locally. An idle finger-pose preset
    // is wired as the poser's fallback so the hand holds a relaxed shape when no live tracking is present;
    // the user-root VR finger stream still wins whenever it's actually tracking.
    private static void EnsureHandPoser(HumanoidRig rig, Chirality side)
    {
        var wrist = rig.TryGetBone(side == Chirality.Left ? BodyNode.LeftHand : BodyNode.RightHand);
        if (wrist == null)
            return;

        foreach (var comp in wrist.Components)
            if (comp is HandPoseDriver)
                return;

        var poser = wrist.AttachComponent<HandPoseDriver>();
        poser.Side.Value = side;
        poser.HandRoot.Target = wrist;
        poser.IdlePose.Target = EnsureIdleHandPose(rig);

        var assigner = wrist.AttachComponent<HandPoseBinder>();
        assigner.Side.Value = side;
        assigner.TargetPoser.Target = poser;
    }

    // One idle finger-pose source per avatar, shared by both hands. A HandPosePreset on Idle serves a
    // relaxed hand shape (synthesized per-peer, only the Shape enum syncs). Finger-pose components were
    // otherwise never attached, so hands had no idle source at all.
    private static HandPosePreset EnsureIdleHandPose(HumanoidRig rig)
    {
        var host = rig.Slot;
        var preset = host.GetComponent<HandPosePreset>();
        if (preset == null)
        {
            preset = host.AttachComponent<HandPosePreset>();
            preset.Shape.Value = HandPoseShape.Idle;
        }
        return preset;
    }

    public bool CanEquipAvatar(Slot avatarSlot)
    {
        if (avatarSlot == null || avatarSlot.IsDestroyed)
            return false;
        return avatarSlot.GetComponentInChildren<SkeletonBuilder>() != null
            && avatarSlot.GetComponentInChildren<HumanoidRig>() != null;
    }

    public async void ImportAndEquipAvatar(string filePath, LocalDB localDB = null!)
    {
        try
        {
            var importSlot = Slot.AddSlot("ImportedAvatar");
            var result = await ModelImporter.ImportAvatarAsync(filePath, importSlot, localDB);

            if (result.Success && result.RootSlot != null)
            {
                Logger.Log("AvatarEquipManager: Avatar imported - calibrate via the avatar studio, then equip");
            }
            else
            {
                Logger.Error($"AvatarEquipManager: Failed to import avatar: {result.ErrorMessage}");
                importSlot.Destroy();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"AvatarEquipManager: Exception importing avatar: {ex.Message}");
        }
    }

    private void ResolveUserRoot()
    {
        if (UserRoot.Target != null) return;
        UserRoot.Target = Slot?.ActiveUserRoot ?? Slot?.GetComponentInParent<UserRoot>()!;
    }

    private static void ReleaseAvatarTracking(Slot avatarSlot)
    {
        if (avatarSlot == null || avatarSlot.IsDestroyed) return;

        var dequipped = new HashSet<IAvatarEquippable>();
        foreach (var poseNode in avatarSlot.GetComponentsInChildren<AvatarPoseDriver>())
        {
            poseNode.CurrentSocket?.Dequip(dequipped);
        }
    }
}
