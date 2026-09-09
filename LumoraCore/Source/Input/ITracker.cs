// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Input;

// A body tracker: a tracked device that is not the headset and not a controller, and whose job is
// decided by the person wearing it rather than by the hardware.
//
// The headset is always the head and a controller is always a hand, so neither needs an identity or a
// mapping. A puck strapped to a foot is a foot only because someone said so, and it has to still be a
// foot after a restart, so it needs a STABLE ID and a mapping stored against that id. That is the whole
// reason this interface exists on top of ITrackedDevice. -xlinka
public interface ITracker : ITrackedDevice
{
    // Stable hardware/runtime identity. LOCAL ONLY: it is a device serial in practice, so it must never
    // be replicated, named into a slot, or written to a world.
    string UniqueID { get; }

    // Shareable stand-in for UniqueID: a hash of the unique id salted with this machine's secret, so it
    // is stable for this user and useless to anyone else. Everything that leaves the machine (slot
    // names, replicated state, logs worth sharing) uses THIS. -xlinka
    string PublicID { get; }

    // What the runtime says the device is for, from the OpenXR role path it arrived on. A suggestion,
    // not a decision: the user's saved mapping always wins, because people put pucks where they like.
    TrackerRole SuggestedRole { get; }

    // 0..1, or -1 when the runtime does not report it. Not every tracker exposes a battery.
    float BatteryLevel { get; }

    bool BatteryCharging { get; }

    // Held still by the user, ignoring live pose. Used while calibrating and when a device starts
    // spewing garbage mid-session.
    bool FreezeTracking { get; set; }
}

// The roles OpenXR's vive tracker extension can hand us. Values are stable and appended-to only: a
// saved mapping stores the role it was calibrated against.
public enum TrackerRole
{
    None = 0,
    Waist = 1,
    Chest = 2,
    LeftFoot = 3,
    RightFoot = 4,
    LeftKnee = 5,
    RightKnee = 6,
    LeftElbow = 7,
    RightElbow = 8,
    LeftShoulder = 9,
    RightShoulder = 10,
    Camera = 11,
    Keyboard = 12,
}

public static class TrackerRoles
{
    // OpenXR role path segment (the tail of /user/vive_tracker_htcx/role/...) to role.
    public static TrackerRole FromRolePath(string? rolePath)
    {
        if (string.IsNullOrEmpty(rolePath))
            return TrackerRole.None;

        int slash = rolePath.LastIndexOf('/');
        string tail = slash >= 0 ? rolePath[(slash + 1)..] : rolePath;

        return tail.ToLowerInvariant() switch
        {
            "waist" => TrackerRole.Waist,
            "chest" => TrackerRole.Chest,
            "left_foot" => TrackerRole.LeftFoot,
            "right_foot" => TrackerRole.RightFoot,
            "left_knee" => TrackerRole.LeftKnee,
            "right_knee" => TrackerRole.RightKnee,
            "left_elbow" => TrackerRole.LeftElbow,
            "right_elbow" => TrackerRole.RightElbow,
            "left_shoulder" => TrackerRole.LeftShoulder,
            "right_shoulder" => TrackerRole.RightShoulder,
            "camera" => TrackerRole.Camera,
            "keyboard" => TrackerRole.Keyboard,
            _ => TrackerRole.None,
        };
    }

    // The body node a role drives by default. Camera and keyboard drive nothing: they are props, not
    // body parts, and mapping them onto a limb is how a spare puck ends up dragging an elbow around.
    public static BodyNode ToBodyNode(TrackerRole role) => role switch
    {
        TrackerRole.Waist => BodyNode.Hips,
        TrackerRole.Chest => BodyNode.Chest,
        TrackerRole.LeftFoot => BodyNode.LeftFoot,
        TrackerRole.RightFoot => BodyNode.RightFoot,
        TrackerRole.LeftKnee => BodyNode.LeftLowerLeg,
        TrackerRole.RightKnee => BodyNode.RightLowerLeg,
        TrackerRole.LeftElbow => BodyNode.LeftLowerArm,
        TrackerRole.RightElbow => BodyNode.RightLowerArm,
        TrackerRole.LeftShoulder => BodyNode.LeftShoulder,
        TrackerRole.RightShoulder => BodyNode.RightShoulder,
        _ => BodyNode.NONE,
    };

    // Body nodes a tracker is allowed to be mapped onto. The calibrator offers exactly these, so a puck
    // cannot be assigned to a fingertip.
    public static readonly BodyNode[] Mappable =
    {
        BodyNode.Hips,
        BodyNode.Chest,
        BodyNode.LeftFoot,
        BodyNode.RightFoot,
        BodyNode.LeftLowerLeg,
        BodyNode.RightLowerLeg,
        BodyNode.LeftLowerArm,
        BodyNode.RightLowerArm,
        BodyNode.LeftShoulder,
        BodyNode.RightShoulder,
    };

    public static bool IsMappable(BodyNode node)
    {
        for (int i = 0; i < Mappable.Length; i++)
        {
            if (Mappable[i] == node)
                return true;
        }
        return false;
    }
}
