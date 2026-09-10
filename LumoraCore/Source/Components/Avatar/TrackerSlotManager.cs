// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Input;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

// One slot per TRACKER, under the local user's root, named by the tracker's public id.
//
// The first version of this built one slot per BODY NODE instead, and keying on the destination rather
// than the device is wrong in three ways that only show up with real hardware:
//
//   - Two pucks claiming the same role collide, and the second one silently has nowhere to go.
//   - A tracker mapped to nothing has no slot, so there is no object to see, select, or hang a mapping
//     off. An uncalibrated setup is invisible rather than merely uncalibrated.
//   - A camera or keyboard puck is a real tracked object that should be mountable as a prop, but it is
//     not a body part. Keyed by node it has no home at all.
//
// Keyed by device, all three fall out for free: the slot exists from the moment the hardware appears,
// the body node is just a property of it, and an unmapped tracker is a slot with no avatar attachment.
//
// This also settles the which-world question by construction. It lives UNDER THE USER ROOT and refuses
// to run for a non-local user, so there is no need for a world-kind concept and no implicit guard.
// -xlinka
[ComponentCategory("Users/Avatar")]
public class TrackerSlotManager : Component
{
    private readonly Dictionary<string, Slot> _trackerSlots = new();
    private bool _hooked;
    private readonly List<ITracker> _scratch = new();

    public override void OnStart()
    {
        base.OnStart();

        if (!IsUnderLocalUser)
            return;

        var input = Engine.Current?.InputInterface;
        if (input == null)
            return;

        input.InputDeviceAdded += OnInputDeviceAdded;
        _hooked = true;

        // Trackers already connected before this component existed, which is the normal case: the input
        // layer registers devices at startup, long before a user root is assembled.
        _scratch.Clear();
        input.GetDevices(_scratch);
        for (int i = 0; i < _scratch.Count; i++)
            EnsureSlot(_scratch[i]);
        _scratch.Clear();
    }

    public override void OnDestroy()
    {
        if (_hooked)
        {
            var input = Engine.Current?.InputInterface;
            if (input != null)
                input.InputDeviceAdded -= OnInputDeviceAdded;
            _hooked = false;
        }

        base.OnDestroy();
    }

    private void OnInputDeviceAdded(IInputDevice device)
    {
        if (device is not ITracker tracker)
            return;

        var world = World;
        if (world == null || world.IsDisposed)
            return;

        // Devices register from whichever thread noticed the hardware. Building a slot is a data model
        // write, so it has to be queued onto the world thread where the lock makes it legal.
        world.RunSynchronously(() =>
        {
            if (!IsDestroyed)
                EnsureSlot(tracker);
        });
    }

    private void EnsureSlot(ITracker tracker)
    {
        if (tracker == null || string.IsNullOrEmpty(tracker.PublicID))
            return;

        var userRoot = Slot;
        if (userRoot == null || userRoot.IsDestroyed)
            return;

        if (_trackerSlots.TryGetValue(tracker.PublicID, out var existing)
            && existing != null && !existing.IsDestroyed)
            return;

        // PublicID, never UniqueID. The raw id is a device identity and this slot name is visible to
        // anything that can read the hierarchy.
        var slot = userRoot.AddSlot(tracker.PublicID);

        var positioner = slot.AttachComponent<TrackedDevicePositioner>();
        // Bound to THIS device, not resolved by body node. That is what lets two trackers hold the same
        // role, and what keeps an unmapped tracker attached to something real.
        positioner.DeviceIndex.Value = tracker.DeviceIndex;

        // A tracker that is not on a body part is still a tracked object worth having (a camera puck is
        // a mount point), it just must not create an avatar attachment.
        if (!TrackerRoles.IsMappable(tracker.CorrespondingBodyNode))
            positioner.CreateAvatarObjectSlot.Value = false;

        // Per-tracker streams, keyed by public id rather than by body node, for the same reason the slot
        // is: the body node can change under calibration and the stream must not have to move with it.
        // Without a stream this is local-only and every peer sees legs that never move. -xlinka
        var user = World?.LocalUser;
        if (user != null)
        {
            var stream = slot.AttachComponent<TransformStreamDriver>();
            stream.PositionStream.Target =
                user.GetStreamOrAdd<Lumora.Core.Networking.Streams.Float3ValueStream>(tracker.PublicID + ".Pos");
            stream.RotationStream.Target =
                user.GetStreamOrAdd<Lumora.Core.Networking.Streams.FloatQValueStream>(tracker.PublicID + ".Rot");
        }

        _trackerSlots[tracker.PublicID] = slot;
        LumoraLogger.Log(
            $"TrackerSlotManager: built slot for tracker {tracker.PublicID} " +
            $"(suggested {tracker.SuggestedRole}, mapped {tracker.CorrespondingBodyNode})");
    }
}
