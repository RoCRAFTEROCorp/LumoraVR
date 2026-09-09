// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Security.Cryptography;
using System.Text;
using Lumora.Core.Math;

namespace Lumora.Core.Input;

// A body tracker as an input device: the TrackedObject shape plus the identity and freeze behaviour
// ITracker asks for. Drivers write through SetPose/SetUntracked/SetOffline so the freeze rule lives in
// one place instead of at every write site.
//
// Deliberately not a TrackedObject subclass: InputInterface resets IsTracking on every TrackedObject it
// finds in its body-node table each frame, and a tracker's flag has to mean what the runtime last said,
// not what a bookkeeping pass zeroed. -xlinka
public class TrackerDevice : InputDevice, ITracker
{
    private float3 _rawPosition = float3.Zero;
    private floatQ _rawRotation = floatQ.Identity;
    private bool _isTracking;
    private float _trackingConfidence;

    public TrackerDevice()
    {
        IsDeviceActive = false;
    }

    public string UniqueID { get; private set; } = string.Empty;

    public string PublicID { get; private set; } = string.Empty;

    public TrackerRole SuggestedRole { get; set; } = TrackerRole.None;

    // -1 until a runtime actually reports one. Godot's OpenXR tracker surface has no battery path.
    public float BatteryLevel { get; set; } = -1f;

    public bool BatteryCharging { get; set; }

    public bool FreezeTracking { get; set; }

    public BodyNode CorrespondingBodyNode { get; set; } = BodyNode.NONE;

    public TrackingSpace TrackingSpace { get; set; } = null!;

    public int Priority { get; set; }

    // 0 not tracked, 0.5 the runtime is extrapolating (pose valid, not tracked), 1 fully tracked.
    // IsTracking is true for both non-zero levels; a consumer that wants only fresh optical fixes reads
    // this and gates on 1.
    public float TrackingConfidence => _trackingConfidence;

    public bool IsTracking
    {
        get => _isTracking;
        set
        {
            if (!FreezeTracking)
                _isTracking = value;
        }
    }

    public float3 RawPosition
    {
        get => _rawPosition;
        set
        {
            if (!FreezeTracking)
                _rawPosition = value;
        }
    }

    public floatQ RawRotation
    {
        get => _rawRotation;
        set
        {
            if (!FreezeTracking)
                _rawRotation = value;
        }
    }

    public float3 Position => TrackingSpace?.Transform(RawPosition) ?? RawPosition;

    public floatQ Rotation => TrackingSpace?.Transform(RawRotation) ?? RawRotation;

    public float3 BodyNodePositionOffset { get; set; } = float3.Zero;

    public floatQ BodyNodeRotationOffset { get; set; } = floatQ.Identity;

    public void SetIdentity(string uniqueId)
    {
        UniqueID = uniqueId ?? string.Empty;
        PublicID = TrackerIdentity.PublicIdFor(UniqueID);
    }

    // The per-frame write for a pose the runtime vouches for. A frozen device keeps everything it had,
    // tracking flag included, so a calibration pose does not blink off when the puck is occluded.
    public void SetPose(float3 position, floatQ rotation, float confidence)
    {
        if (FreezeTracking)
            return;

        _rawPosition = position;
        _rawRotation = rotation;
        _isTracking = true;
        _trackingConfidence = confidence;
    }

    // The runtime has no pose for it this frame. The last pose stays readable, the flag says not to
    // trust it. Frozen devices ignore this too: freeze is the user overriding the runtime.
    public void SetUntracked()
    {
        if (FreezeTracking)
            return;

        _isTracking = false;
        _trackingConfidence = 0f;
    }

    // Hardware gone, profile unbound, or VR mode off. Freeze does not apply: a device that is not there
    // cannot claim to track, whatever the user asked for.
    public void SetOffline()
    {
        _isTracking = false;
        _trackingConfidence = 0f;
        IsDeviceActive = false;
    }
}

// PublicID derivation.
//
// The install's RSA identity key is the only secret this machine keeps and it is never exported, so
// the salt goes in by signing: RSASSA-PKCS1-v1_5 is deterministic, nobody without the private key can
// produce the same bytes, and hashing the signature leaves nothing that inverts back to the unique id.
// The domain prefix keeps these payloads disjoint from the join handshake's "<context>:<nonce>" shape,
// so a tracker id can never be a valid join proof and vice versa.
//
// If the identity cannot be loaded at all (LocalDB has the same fallback for the same reason), a random
// secret persisted in Settings keys an HMAC instead, so PublicID still survives a restart. -xlinka
public static class TrackerIdentity
{
    private const string Domain = "lumora.tracker.publicid.v1:";
    private const string FallbackSecretKey = "Input.Trackers.PublicIdSecret";
    private static byte[]? _fallbackSecret;

    public static string PublicIdFor(string uniqueId)
    {
        if (string.IsNullOrEmpty(uniqueId))
            return string.Empty;

        var payload = Encoding.UTF8.GetBytes(Domain + uniqueId);
        byte[] keyed;
        try
        {
            keyed = Security.MachineIdentity.Local.SignChallenge(payload);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"TrackerIdentity: machine identity unavailable ({ex.Message}); keying with the persisted fallback secret.");
            keyed = HMACSHA256.HashData(FallbackSecret(), payload);
        }

        var hash = SHA256.HashData(keyed);
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static byte[] FallbackSecret()
    {
        if (_fallbackSecret != null)
            return _fallbackSecret;

        var stored = Settings.ReadValue<string>(FallbackSecretKey, string.Empty);
        if (!string.IsNullOrEmpty(stored))
        {
            try
            {
                _fallbackSecret = Convert.FromBase64String(stored);
                return _fallbackSecret;
            }
            catch (FormatException)
            {
                // Unreadable value: regenerate below. Old PublicIDs from it are gone either way.
            }
        }

        var fresh = RandomNumberGenerator.GetBytes(32);
        Settings.WriteValue(FallbackSecretKey, Convert.ToBase64String(fresh));
        _fallbackSecret = fresh;
        return fresh;
    }
}
