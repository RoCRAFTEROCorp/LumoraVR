// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Networking.Streams;

namespace Lumora.Core.Components;

[ComponentCategory("Users")]
[DefaultUpdateOrder(-10000)]
public class TransformStreamDriver : Component
{
    // Synced references to the streams - these sync over the network
    // so clients can resolve them by RefID
    // Initialized by Worker.InitializeSyncMembers() via reflection
    public readonly SyncRef<Float3ValueStream> PositionStream = null!;
    public readonly SyncRef<FloatQValueStream> RotationStream = null!;

    public User? User
    {
        get
        {
            var posTarget = PositionStream?.Target;
            if (posTarget != null) return posTarget.Owner;

            var rotTarget = RotationStream?.Target;
            if (rotTarget != null) return rotTarget.Owner;

            return null;
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var currentUser = User;
        if (currentUser == null)
            return;

        if (currentUser.IsLocal)
        {
            // Local user: read slot transform, write to streams
            UpdateLocalStreams();
        }
        else
        {
            // Remote user: read streams, write to slot transform
            ApplyRemoteStreams();
        }
    }

    private void UpdateLocalStreams()
    {
        var positionStream = PositionStream?.Target;
        var rotationStream = RotationStream?.Target;

        // Write position to stream
        if (positionStream != null && positionStream.IsLocal)
        {
            var position = Slot.LocalPosition.Value;
            positionStream.Value = position;
        }

        // Write rotation to stream
        if (rotationStream != null && rotationStream.IsLocal)
        {
            var rotation = Slot.LocalRotation.Value;
            rotationStream.Value = rotation;
        }
    }

    private void ApplyRemoteStreams()
    {
        var positionStream = PositionStream?.Target;
        var rotationStream = RotationStream?.Target;

        // Applying a REMOTE user's authoritative streamed pose to their OWN body/root slots is engine playback of
        // already-validated replicated data, not a user edit. Without this bypass, the write's actor resolves to
        // the OBSERVER's own user (it's a non-network local write), who doesn't own the remote user's slots, so on
        // a client observer GuestRole denies the foreign write and the remote avatar freezes. The host already
        // validated the stream upstream; bypass the per-actor gate here. -xlinka
        using var bypass = World?.DataModelPermissions?.EnterSystemBypass();

        // Apply position from stream. Skip ONLY the position this frame if it's non-finite - don't 'return',
        // which would also skip the rotation apply below and stall the remote avatar's facing on a glitched
        // position sample. -xlinka
        if (positionStream != null && positionStream.HasValidData)
        {
            var position = positionStream.Value;
            if (float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z))
                Slot.LocalPosition.SetValueSilently(position, change: true);
        }

        // Apply rotation from stream
        if (rotationStream != null && rotationStream.HasValidData)
        {
            var rotation = rotationStream.Value;

            // Skip invalid values
            if (!float.IsFinite(rotation.x) || !float.IsFinite(rotation.y) ||
                !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w))
                return;

            // Skip zero-length quaternion
            float lengthSq = rotation.x * rotation.x + rotation.y * rotation.y +
                             rotation.z * rotation.z + rotation.w * rotation.w;
            if (lengthSq < 0.0001f)
                return;

            Slot.LocalRotation.SetValueSilently(rotation, change: true);
        }
    }
}

