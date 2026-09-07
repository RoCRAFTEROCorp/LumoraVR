// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text;

namespace Lumora.Core.Assets;

// Matches bone names that mean the same joint but are not spelled identically.
//
// Rigs disagree with themselves constantly. Exporters decorate names ("mixamorig:Hips",
// "Armature|Hips"), duplication appends a suffix ("Hips.001"), and authors typo them - a real avatar
// binds to "Left_FirstToe" while the slot is called "Left_FirstToey", and that one character silently
// collapses the toe onto bone 0 and deforms the foot.
//
// Escalates deliberately, strictest first, and REFUSES anything ambiguous: a looser rule that matches
// two candidates is worse than no match, because it binds a mesh to the wrong joint and the result
// looks like a skinning bug rather than a naming one. -xlinka
public sealed class BoneNameMatcher
{
    public enum MatchKind
    {
        None,
        // Spelled identically.
        Exact,
        // Same once decoration, separators and case are stripped: "mixamorig:Left_Arm" -> "leftarm".
        Normalized,
        // One is the other with characters appended - the typo case.
        Prefix,
    }

    private readonly Dictionary<string, int> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _normalized = new(StringComparer.Ordinal);
    private readonly List<(string Normalized, int Value)> _all = new();

    public void Add(string? name, int value)
    {
        if (string.IsNullOrEmpty(name))
            return;

        _exact.TryAdd(name, value);

        var normalized = Normalize(name);
        if (normalized.Length == 0)
            return;

        if (!_normalized.TryGetValue(normalized, out var bucket))
            _normalized[normalized] = bucket = new List<int>(1);
        bucket.Add(value);
        _all.Add((normalized, value));
    }

    public bool TryResolve(string? name, out int value, out MatchKind kind)
    {
        value = -1;
        kind = MatchKind.None;
        if (string.IsNullOrEmpty(name))
            return false;

        if (_exact.TryGetValue(name, out value))
        {
            kind = MatchKind.Exact;
            return true;
        }

        var normalized = Normalize(name);
        if (normalized.Length == 0)
            return false;

        if (_normalized.TryGetValue(normalized, out var bucket))
        {
            // Several joints normalize the same way: refuse rather than guess.
            if (bucket.Count != 1)
                return false;
            value = bucket[0];
            kind = MatchKind.Normalized;
            return true;
        }

        // Last resort: one name is the other plus a few characters. Bounded, because at some length a
        // "prefix" stops being a typo and starts being a different bone ("Tail" vs "Tail_012").
        const int maxExtra = 2;
        int found = -1;
        int matches = 0;
        foreach (var (candidate, candidateValue) in _all)
        {
            bool isPrefix =
                (candidate.Length > normalized.Length
                 && candidate.Length - normalized.Length <= maxExtra
                 && candidate.StartsWith(normalized, StringComparison.Ordinal))
                || (normalized.Length > candidate.Length
                    && normalized.Length - candidate.Length <= maxExtra
                    && normalized.StartsWith(candidate, StringComparison.Ordinal));

            if (!isPrefix)
                continue;

            matches++;
            if (matches > 1)
                return false;   // ambiguous
            found = candidateValue;
        }

        if (matches != 1)
            return false;

        value = found;
        kind = MatchKind.Prefix;
        return true;
    }

    // Take what follows the last namespace separator, drop a trailing duplicate suffix like ".001",
    // then keep only letters and digits, lowercased. "mixamorig:LeftArm", "Armature|Left Arm" and
    // "left_arm.001" all reduce to "leftarm".
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var span = name.AsSpan().Trim();

        int cut = -1;
        for (int i = span.Length - 1; i >= 0; i--)
        {
            char c = span[i];
            if (c == ':' || c == '|' || c == '/')
            {
                cut = i;
                break;
            }
        }
        if (cut >= 0)
            span = span[(cut + 1)..];

        int dot = span.LastIndexOf('.');
        if (dot > 0 && dot < span.Length - 1)
        {
            bool digits = true;
            for (int i = dot + 1; i < span.Length; i++)
            {
                if (!char.IsDigit(span[i])) { digits = false; break; }
            }
            if (digits)
                span = span[..dot];
        }

        var builder = new StringBuilder(span.Length);
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
