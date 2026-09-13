// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
// Steam is a desktop-only transport. The Steamworks binding has no Android build, and a type that
// merely MENTIONS a Steamworks type cannot be loaded there: the whole engine init failed on a Pico 4
// with 'Could not load type of field SteamNetworkManager:_listenerMap', long before any runtime
// 'is this Android' check could run. Compile the transport out on mobile entirely. -xlinka
#if GODOT_PC

namespace Lumora.Godot.Networking.Transports.Steam;

/// <summary>
/// Per-channel reassembly cursor for large reliable messages. The Steam
/// transport caps SendMessageToConnection at 512KB; payloads bigger than that
/// are sent as a 4-byte length prefix followed by 512KB chunks (with the
/// k_nSteamNetworkingSend_UseCurrentThread flag set on every fragment so they
/// arrive in order on the same channel). The receiver collects fragments
/// until <see cref="receivedBytes"/> == <see cref="expectingBytes"/> and then
/// surfaces the assembled buffer as one message. - xlinka
/// </summary>
internal struct ReassemblyState
{
    public byte[] buffer;
    public int expectingBytes;
    public int receivedBytes;
}

internal delegate void SteamMessageHandler(ulong steamId, uint connectionHandle, byte[] data, int length);

internal delegate void SteamConnectionFailureHandler(uint connectionHandle);

internal delegate ref ReassemblyState ReassemblyStateFetcher(uint connectionHandle);
#endif
