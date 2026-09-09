// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lumora.Core.Math;

namespace Lumora.Core.Input;

// What the user decided a tracker is. Keyed on the RAW UniqueID on purpose: this lives in the local
// settings store and never leaves the machine, so the serial is the correct key here. Anything that is
// replicated or shared keys on PublicID instead. -xlinka
public struct TrackerMapping
{
    public string UniqueID;
    public BodyNode Node;

    // Tracker pose -> body node pose, in the tracker's own frame and in tracking-space units:
    //   bodyNodePose = trackerPose * offset
    // which is exactly how TrackedDevicePositioner applies it (the offset is a CHILD transform under
    // the device slot). Calibration is the only writer.
    public float3 PositionOffset;
    public floatQ RotationOffset;

    public bool Enabled;
    public string? CustomName;

    // The runtime's role suggestion at the moment this was calibrated. Diagnostic only: a later session
    // that hands the same serial a different role is a reason to warn, never to move the mapping.
    public TrackerRole CalibratedRole;
    public long CalibratedUtcTicks;

    public bool IsValid => !string.IsNullOrEmpty(UniqueID) && Node != BodyNode.NONE;

    public static TrackerMapping Default(string uniqueId, BodyNode node) => new()
    {
        UniqueID = uniqueId,
        Node = node,
        PositionOffset = float3.Zero,
        RotationOffset = floatQ.Identity,
        Enabled = true,
        CustomName = null,
        CalibratedRole = TrackerRole.None,
        CalibratedUtcTicks = 0,
    };
}

// Optional seam for a device that wants mappings pushed at it the moment they change, instead of polling
// the store. The device layer may implement it on its tracker type; nothing here requires it.
public interface ITrackerMappingReceiver
{
    void ApplyMapping(in TrackerMapping mapping);
    void ClearMapping();
}

// Persisted per-tracker mapping, on top of the general Settings store so it rides the same file, the
// same load path and the same failure handling as every other durable preference.
//
// A mapping for a tracker that is not connected is kept, not deleted. Pucks die, get left in a bag, or
// are off charging at boot; the whole point of persisting is that the foot is still a foot when the puck
// comes back. Only an explicit Remove drops an entry. -xlinka
public static class TrackerMappingStore
{
    private const string Root = "Input.Trackers";

    private const string KeyUniqueID = "UniqueID";
    private const string KeyNode = "Node";
    private const string KeyPositionOffset = "PositionOffset";
    private const string KeyRotationOffset = "RotationOffset";
    private const string KeyEnabled = "Enabled";
    private const string KeyName = "Name";
    private const string KeyRole = "Role";
    private const string KeyCalibratedUtc = "CalibratedUtc";

    // Fired after a mapping is written or its enabled/name flags change. The device layer subscribes so a
    // fresh calibration takes effect without a reconnect.
    public static event Action<TrackerMapping>? Changed;

    // Fired after an entry is removed, with the raw UniqueID it was stored under.
    public static event Action<string>? Removed;

    public static bool Has(string uniqueId)
    {
        if (string.IsNullOrEmpty(uniqueId))
            return false;
        return Settings.HasValue(Key(uniqueId, KeyNode));
    }

    public static bool TryGet(string uniqueId, out TrackerMapping mapping)
    {
        mapping = default;
        if (string.IsNullOrEmpty(uniqueId))
            return false;

        string segment = Segment(uniqueId);
        if (!Settings.HasValue(Root + "." + segment + "." + KeyNode))
            return false;

        return TryRead(segment, out mapping) && mapping.IsValid;
    }

    public static void Set(in TrackerMapping mapping)
    {
        if (!mapping.IsValid)
            return;

        string segment = Segment(mapping.UniqueID);
        string prefix = Root + "." + segment + ".";

        Settings.WriteValue(prefix + KeyUniqueID, mapping.UniqueID);
        Settings.WriteValue(prefix + KeyNode, mapping.Node.ToString());
        Settings.WriteValue(prefix + KeyPositionOffset, Pack(mapping.PositionOffset));
        Settings.WriteValue(prefix + KeyRotationOffset, Pack(mapping.RotationOffset));
        Settings.WriteValue(prefix + KeyEnabled, mapping.Enabled);
        Settings.WriteValue(prefix + KeyRole, mapping.CalibratedRole.ToString());
        Settings.WriteValue(prefix + KeyCalibratedUtc, mapping.CalibratedUtcTicks);
        if (string.IsNullOrEmpty(mapping.CustomName))
            Settings.DeleteValue(prefix + KeyName);
        else
            Settings.WriteValue(prefix + KeyName, mapping.CustomName);

        Changed?.Invoke(mapping);
    }

    public static bool SetEnabled(string uniqueId, bool enabled)
    {
        if (!TryGet(uniqueId, out var mapping))
            return false;
        mapping.Enabled = enabled;
        Set(mapping);
        return true;
    }

    public static bool SetCustomName(string uniqueId, string? name)
    {
        if (!TryGet(uniqueId, out var mapping))
            return false;
        mapping.CustomName = string.IsNullOrWhiteSpace(name) ? null : name!.Trim();
        Set(mapping);
        return true;
    }

    public static bool Remove(string uniqueId)
    {
        if (string.IsNullOrEmpty(uniqueId))
            return false;
        string segment = Segment(uniqueId);
        if (!Settings.HasValue(Root + "." + segment + "." + KeyNode))
            return false;
        Settings.ClearSettings(Root + "." + segment);
        Removed?.Invoke(uniqueId);
        return true;
    }

    // Every stored mapping, connected or not. Broken entries (a hand-edited file, a half-written save)
    // are skipped rather than surfaced as a mapping to NONE.
    public static void GetAll(List<TrackerMapping> into)
    {
        foreach (var segment in Settings.ListSettings(Root))
        {
            if (TryRead(segment, out var mapping) && mapping.IsValid)
                into.Add(mapping);
        }
    }

    // Push the stored mapping (if any) into a live tracker. Returns true when something accepted it.
    //
    // Three paths, most specific first: the receiver seam, then the engine's generic TrackedObject
    // setters, and finally the Changed event which this does not raise (the caller that wrote the
    // mapping already did). A device layer that implements neither of the first two must subscribe to
    // Changed and call TryGet on connect; otherwise a mapping is stored but never worn. -xlinka
    public static bool TryApply(ITracker tracker)
    {
        if (tracker == null)
            return false;
        if (!TryGet(tracker.UniqueID, out var mapping))
            return false;
        return Apply(tracker, mapping);
    }

    public static bool Apply(ITracker tracker, in TrackerMapping mapping)
    {
        if (tracker == null || !mapping.IsValid)
            return false;

        if (tracker is ITrackerMappingReceiver receiver)
        {
            if (mapping.Enabled)
                receiver.ApplyMapping(mapping);
            else
                receiver.ClearMapping();
            return true;
        }

        if (tracker is TrackedObject tracked)
        {
            if (mapping.Enabled)
            {
                tracked.CorrespondingBodyNode = mapping.Node;
                tracked.BodyNodePositionOffset = mapping.PositionOffset;
                tracked.BodyNodeRotationOffset = mapping.RotationOffset;
            }
            else
            {
                tracked.CorrespondingBodyNode = BodyNode.NONE;
                tracked.BodyNodePositionOffset = float3.Zero;
                tracked.BodyNodeRotationOffset = floatQ.Identity;
            }
            return true;
        }

        return false;
    }

    // The body node a tracker should drive right now: the saved mapping when there is one and it is
    // enabled, otherwise whatever the runtime suggested. Default-before-calibration lives here so every
    // caller agrees on it.
    public static BodyNode ResolveNode(ITracker tracker)
    {
        if (tracker == null)
            return BodyNode.NONE;
        if (TryGet(tracker.UniqueID, out var mapping))
            return mapping.Enabled ? mapping.Node : BodyNode.NONE;

        // No saved mapping means no mapping. Falling back to the runtime's suggested role would drive a
        // body node through an uncalibrated identity offset, which puts the joint exactly on the puck
        // instead of where the puck is strapped. Calibration is what turns a suggestion into a mapping.
        return BodyNode.NONE;
    }

    private static bool TryRead(string segment, out TrackerMapping mapping)
    {
        mapping = default;
        string prefix = Root + "." + segment + ".";

        string uniqueId = Settings.ReadValue<string>(prefix + KeyUniqueID, string.Empty) ?? string.Empty;
        if (uniqueId.Length == 0)
            return false;

        string nodeText = Settings.ReadValue<string>(prefix + KeyNode, string.Empty) ?? string.Empty;
        if (!Enum.TryParse<BodyNode>(nodeText, ignoreCase: false, out var node) || node == BodyNode.NONE || node == BodyNode.END)
            return false;

        mapping.UniqueID = uniqueId;
        mapping.Node = node;
        mapping.PositionOffset = UnpackFloat3(Settings.ReadValue<string>(prefix + KeyPositionOffset, string.Empty), float3.Zero);
        mapping.RotationOffset = UnpackFloatQ(Settings.ReadValue<string>(prefix + KeyRotationOffset, string.Empty), floatQ.Identity);
        mapping.Enabled = Settings.ReadValue<bool>(prefix + KeyEnabled, true);
        string name = Settings.ReadValue<string>(prefix + KeyName, string.Empty) ?? string.Empty;
        mapping.CustomName = name.Length > 0 ? name : null;
        string roleText = Settings.ReadValue<string>(prefix + KeyRole, string.Empty) ?? string.Empty;
        mapping.CalibratedRole = Enum.TryParse<TrackerRole>(roleText, ignoreCase: false, out var role) ? role : TrackerRole.None;
        mapping.CalibratedUtcTicks = Settings.ReadValue<long>(prefix + KeyCalibratedUtc, 0L);
        return true;
    }

    private static string Key(string uniqueId, string leaf) => Root + "." + Segment(uniqueId) + "." + leaf;

    // Settings keys are dotted paths and ListSettings splits on the dot, so the serial cannot be used
    // raw: an OpenXR path or a serial with a dot in it would shatter into nested segments. The readable
    // part keeps entries recognisable in the file; the hash keeps two ids that sanitise to the same text
    // apart. The raw id is stored inside the entry, so nothing needs to reverse this. -xlinka
    private static string Segment(string uniqueId)
    {
        var sb = new StringBuilder(uniqueId.Length + 10);
        int kept = 0;
        for (int i = 0; i < uniqueId.Length && kept < 40; i++)
        {
            char c = uniqueId[i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            sb.Append(ok ? c : '_');
            kept++;
        }
        sb.Append('_');
        sb.Append(Fnv1a(uniqueId).ToString("x8", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static uint Fnv1a(string text)
    {
        uint hash = 2166136261u;
        for (int i = 0; i < text.Length; i++)
        {
            hash ^= text[i];
            hash *= 16777619u;
        }
        return hash;
    }

    private static string Pack(in float3 v)
        => string.Join(" ", F(v.x), F(v.y), F(v.z));

    private static string Pack(in floatQ q)
        => string.Join(" ", F(q.x), F(q.y), F(q.z), F(q.w));

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static float3 UnpackFloat3(string? text, float3 fallback)
    {
        if (!TryParseFloats(text, 3, out var parts))
            return fallback;
        return new float3(parts[0], parts[1], parts[2]);
    }

    private static floatQ UnpackFloatQ(string? text, floatQ fallback)
    {
        if (!TryParseFloats(text, 4, out var parts))
            return fallback;
        var q = new floatQ(parts[0], parts[1], parts[2], parts[3]);
        float len = q.Length;
        if (len < 1e-6f || float.IsNaN(len) || float.IsInfinity(len))
            return fallback;
        return q.Normalized;
    }

    private static bool TryParseFloats(string? text, int count, out float[] parts)
    {
        parts = new float[count];
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var tokens = text!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != count)
            return false;
        for (int i = 0; i < count; i++)
        {
            if (!float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parts[i]))
                return false;
            if (float.IsNaN(parts[i]) || float.IsInfinity(parts[i]))
                return false;
        }
        return true;
    }
}
