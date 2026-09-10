// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.Avatar;

// Bone keys for a four-legged rig.
//
// A SEPARATE keyspace from BodyNode, and it has to be. BodyNode.END is a live array bound, not a
// label: InputInterface allocates ITrackedDevice[(int)BodyNode.END] and range-guards against it, and
// AvatarIK guards against it too. Values appended AFTER END are silently dropped by every one of those
// guards; values inserted BEFORE END renumber every member above them and orphan every saved rig. So a
// quadruped gets its own enum, which costs nothing: SyncCoder wires an arbitrary enum through its
// generic path and DataTreeCoder persists it by NAME.
//
// That last part is the one rule with teeth. These members may be RENUMBERED freely but must never be
// RENAMED once shipped - a save stores "FrontLeftPaw" as a string, and Enum.Parse throws on a name it
// does not know, so a rename does not silently lose a bone, it fails the whole load. -xlinka
public enum QuadNode
{
    NONE = 0,

    Root,
    Pelvis,
    Spine0,
    Spine1,
    Spine2,
    Spine3,
    Chest,
    Neck0,
    Neck1,
    Neck2,
    Head,
    Jaw,

    // Six segments per leg, in the order they descend. Scapula, Cannon and Toe are OPTIONAL - plenty of
    // rigs stop at three bones - while Upper, Lower and Paw are required for a limb to solve at all.
    FrontLeftScapula,
    FrontLeftUpper,
    FrontLeftLower,
    FrontLeftCannon,
    FrontLeftPaw,
    FrontLeftToe,

    FrontRightScapula,
    FrontRightUpper,
    FrontRightLower,
    FrontRightCannon,
    FrontRightPaw,
    FrontRightToe,

    RearLeftScapula,
    RearLeftUpper,
    RearLeftLower,
    RearLeftCannon,
    RearLeftPaw,
    RearLeftToe,

    RearRightScapula,
    RearRightUpper,
    RearRightLower,
    RearRightCannon,
    RearRightPaw,
    RearRightToe,

    Tail0,
    Tail1,
    Tail2,
    Tail3,
    Tail4,
    Tail5,
    Tail6,
    Tail7,
}

// Dense 0..3 index for the four legs.
//
// Every per-limb thing in here is an array of four indexed by this, which is the single structural
// difference that makes four legs the same code as two. The biped keeps its equivalent state as
// duplicated field PAIRS - _leftGait/_rightGait, _leftFootFwd/_rightFootFwd, _leftStepLift/
// _rightStepLift, _leftFootPlant/_rightFootPlant, _hasLeftPlant/_hasRightPlant - and every one of
// those pairs would have to become a quadruplet, selected by an if-ladder, to grow a third leg. -xlinka
public enum QuadLimbId
{
    FrontLeft = 0,
    FrontRight = 1,
    RearLeft = 2,
    RearRight = 3,
}

public static class QuadLimb
{
    public const int Count = 4;

    public static bool IsFront(QuadLimbId id) => id <= QuadLimbId.FrontRight;

    public static bool IsLeft(QuadLimbId id) => id == QuadLimbId.FrontLeft || id == QuadLimbId.RearLeft;

    // The limb on the opposite corner - the one that swings WITH this one in a trot.
    public static QuadLimbId Diagonal(QuadLimbId id) => id switch
    {
        QuadLimbId.FrontLeft => QuadLimbId.RearRight,
        QuadLimbId.FrontRight => QuadLimbId.RearLeft,
        QuadLimbId.RearLeft => QuadLimbId.FrontRight,
        _ => QuadLimbId.FrontLeft,
    };

    // First QuadNode of a limb (its Scapula slot). The six segments of a limb are contiguous and in
    // descending order, so a segment is base + offset and no per-limb switch is needed anywhere else.
    public static QuadNode Base(QuadLimbId id) => id switch
    {
        QuadLimbId.FrontLeft => QuadNode.FrontLeftScapula,
        QuadLimbId.FrontRight => QuadNode.FrontRightScapula,
        QuadLimbId.RearLeft => QuadNode.RearLeftScapula,
        _ => QuadNode.RearRightScapula,
    };

    public const int SegmentsPerLimb = 6;

    public const int SegScapula = 0;
    public const int SegUpper = 1;
    public const int SegLower = 2;
    public const int SegCannon = 3;
    public const int SegPaw = 4;
    public const int SegToe = 5;

    // Is this node one of the 24 limb segments, as opposed to spine, head, tail or root? The tagged
    // anatomy rule only applies to limbs: body bones must stay open to untagged names so a rig with no
    // author tags at all behaves exactly as it did.
    public static bool IsLimbNode(QuadNode node)
        => node >= QuadNode.FrontLeftScapula && node <= QuadNode.RearRightToe;

    public static QuadNode Segment(QuadLimbId id, int segment)
        => (QuadNode)((int)Base(id) + segment);

    // Which corner a limb-segment node belongs to. Only meaningful when IsLimbNode(node).
    public static QuadLimbId LimbOf(QuadNode node)
        => (QuadLimbId)(((int)node - (int)QuadNode.FrontLeftScapula) / SegmentsPerLimb);

    public static string Name(QuadLimbId id) => id switch
    {
        QuadLimbId.FrontLeft => "FrontLeft",
        QuadLimbId.FrontRight => "FrontRight",
        QuadLimbId.RearLeft => "RearLeft",
        _ => "RearRight",
    };
}
