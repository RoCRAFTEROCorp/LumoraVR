// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components;

// Keeps the slot positioned at its user's head plus a vertical offset
// (nameplate anchor). Runs on every peer from the replicated head transform
// and writes the result locally (no sync generation) - broadcasting it would
// duplicate the head stream every frame. Offset scales with the user so
// plates stay above scaled avatars. - xlinka
[ComponentCategory("Utility")]
public class PositionAtUser : Component
{
    public readonly Sync<float> VerticalOffset = new();

    // Follow THIS slot instead of the wearer's tracked head node, when it is set.
    //
    // HeadSlot is where the PERSON's head is, which on a two-legged avatar is also where the avatar's
    // head is, so nothing ever needed to distinguish them. On an animal they are nowhere near each
    // other: the wearer's head node stays at human standing height while the dog's head is out front
    // and low, so the nameplate hung in the air over empty space. Anything that knows better points
    // this at the avatar's own head bone. Null falls back to the tracked node, so an unworn or
    // unrecognised avatar behaves exactly as before. -xlinka
    public readonly SyncRef<Slot> Anchor = new();

    public override void OnInit()
    {
        base.OnInit();
        VerticalOffset.Value = 0.25f;
    }

    private Avatar.AvatarForm? _avatarForm;

    // How much bigger the worn avatar is than the user root it hangs off.
    //
    // The avatar fit scale lives on the AVATAR slot here, while the user root stays at 1. That is the one
    // place this diverges from the platform it mirrors, which scales the user root itself - and anything
    // offset from the head by a fixed distance inherits the difference. A model scaled up to match its
    // wearer gets a head nearly twice the size while the offset stays put, and the nameplate ends up
    // inside it. Fold the avatar's own scale back in so the offset stays proportional to the body it is
    // sitting above. Returns 1 for an unworn or unscaled user, so nothing else moves. -xlinka
    private float AvatarFitScale(UserRoot userRoot)
    {
        if (_avatarForm == null || _avatarForm.IsDestroyed || _avatarForm.Slot == null || _avatarForm.Slot.IsDestroyed)
            _avatarForm = userRoot.Slot?.GetComponentInChildren<Avatar.AvatarForm>();

        var avatar = _avatarForm?.Slot;
        if (avatar == null || avatar.IsDestroyed)
            return 1f;

        float root = userRoot.GlobalScale;
        if (root < 1e-4f)
            return 1f;

        float fit = avatar.GlobalScale.x / root;
        return fit > 1e-4f ? fit : 1f;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var userRoot = Slot?.ActiveUserRoot;
        var anchor = Anchor.Target;
        var head = anchor != null && !anchor.IsDestroyed ? anchor : userRoot?.HeadSlot;
        var parent = Slot?.Parent;
        if (head == null || head.IsDestroyed || parent == null)
            return;

        float scale = userRoot!.GlobalScale * AvatarFitScale(userRoot);
        var target = head.GlobalPosition + float3.Up * (VerticalOffset.Value * scale);
        var local = parent.GlobalPointToLocal(target);

        if ((Slot!.LocalPosition.Value - local).LengthSquared > 1e-10f)
        {
            // This anchor lives under the (possibly REMOTE) user's root, so on an observer the write's actor is
            // the observer's own user, who doesn't own it -> the permission gate denies it and the badge stops
            // tracking the head. It's a purely-local visual follow of already-replicated head data (no sync
            // generated), so bypass the gate - same treatment as the remote-body stream apply. -xlinka
            using var bypass = World?.DataModelPermissions?.EnterSystemBypass();
            Slot.LocalPosition.SetValueSilently(local, change: true);
        }
    }
}
