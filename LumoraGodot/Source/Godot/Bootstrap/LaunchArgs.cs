// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;

namespace Lumora.Source.Godot.Bootstrap;

// Godot splits the command line at "--": engine args before it, user args after. A launch flag is
// written on whichever side the operator happened to put it, so every lookup walks both. -xlinka
public static class LaunchArgs
{
    public const string DebugFlag = "--Lumora-Debug";

    public static IEnumerable<string> All()
    {
        foreach (var arg in OS.GetCmdlineArgs())
            yield return arg;
        foreach (var arg in OS.GetCmdlineUserArgs())
            yield return arg;
    }

    public static bool HasFlag(string flag)
    {
        foreach (var arg in All())
        {
            if (arg.Trim().Equals(flag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // --flag=value. Null when the flag is absent; quotes around the value are stripped.
    public static string? ReadValue(string flag)
    {
        var prefix = flag + "=";
        foreach (var raw in All())
        {
            var arg = raw.Trim();
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return arg.Substring(prefix.Length).Trim('"');
        }
        return null;
    }
}
