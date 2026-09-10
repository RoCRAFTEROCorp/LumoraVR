// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.Avatar;

// The semantic meaning of a persistent avatar reference point.
public enum AvatarReferenceKind
{
    None = 0,
    View = 1,
    LeftHandGrip = 2,
    RightHandGrip = 3,
    LeftFoot = 4,
    RightFoot = 5,
    Pelvis = 6,

    // Four legs. APPENDED with explicit values and never reordered: saved content stores this enum by
    // NAME, so inserting anything above these would re-key every reference point already on disk.
    // A quadruped's front pair are its arms and its rear pair its legs, but neither maps onto a grip or
    // a boot, so they get their own kinds rather than being crammed into the biped four. -xlinka
    FrontLeftPaw = 7,
    FrontRightPaw = 8,
    RearLeftPaw = 9,
    RearRightPaw = 10,

    // Where the hand tool rig sits once the avatar is worn. Two points per hand, because the source
    // platform authors two distinct ones and they are genuinely different places on a hand: the TOOL
    // point is where the held tool's own root is pinned (out past the fingertips, so a pen or a laser
    // reads as coming OUT of the hand), and the GRAB point is the centre of the grab sphere that picks
    // things up (inside the palm, so you close on what you are touching). One shared anchor would put
    // the grab sphere out in front of the hand or the tool inside the wrist.
    //
    // APPENDED with explicit values for the same reason the paws were: the enum persists BY NAME and
    // inserting above these would re-key every reference point already on disk. -xlinka
    LeftHandToolAnchor = 11,
    RightHandToolAnchor = 12,
    LeftHandGrabAnchor = 13,
    RightHandGrabAnchor = 14
}

// Tags a slot as a persistent reference point authored by the avatar creator flow.
[ComponentCategory("Users/Avatar")]
public sealed class AvatarReferencePoint : Component
{
    public readonly Sync<AvatarReferenceKind> Kind = new();

    public override void OnInit()
    {
        base.OnInit();
        Kind.Value = AvatarReferenceKind.None;
    }
}
