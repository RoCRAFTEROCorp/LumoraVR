// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
// Steam is a desktop-only transport. The Steamworks binding has no Android build, and a type that
// merely MENTIONS a Steamworks type cannot be loaded there: the whole engine init failed on a Pico 4
// with 'Could not load type of field SteamNetworkManager:_listenerMap', long before any runtime
// 'is this Android' check could run. Compile the transport out on mobile entirely. -xlinka
#if GODOT_PC

namespace Lumora.Godot.Networking.Transports.Steam;

/// <summary>
/// Why a SteamConnection closed. Reported back through
/// <see cref="Lumora.Core.Networking.IConnection.FailReason"/> for logging /
/// reconnect logic. - xlinka
/// </summary>
public enum SteamCloseReason
{
    Undefined,
    ClosedLocally,
    ClosedRemotely,
    LocalProblem,
    ChannelMismatch,
    TransmissionError,
    ReceiveError,
    UnhandledException,
}
#endif
