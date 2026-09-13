// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
// Steam is a desktop-only transport. The Steamworks binding has no Android build, and a type that
// merely MENTIONS a Steamworks type cannot be loaded there: the whole engine init failed on a Pico 4
// with 'Could not load type of field SteamNetworkManager:_listenerMap', long before any runtime
// 'is this Android' check could run. Compile the transport out on mobile entirely. -xlinka
#if GODOT_PC

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;
using Lumora.Nexus.Transport;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Networking.Transports.Steam;

// Top-level Steam relay transport. One instance per engine: owns the global
// SteamNetConnectionStatusChangedCallback_t callback, allocates channel-group indices, and pumps
// every owned SteamListener / SteamConnection from its Update loop.
//
// URI scheme is steam://{hostSteamID}/{channelGroup}/{sessionId}; channel group is doubled to
// virtual ports (foreground/background) inside the listener and connection. - xlinka
public sealed class SteamNetworkManager : INetworkManager
{
    public const string SCHEME = "steam";
    private const int MessageBufferSize = 24;
    private const int SendBufferSize = 8 * 1024 * 1024;
    private const int SendRateBytesPerSecond = 16 * 1024 * 1024;
    private const int InitialTimeoutMs = 15000;
    private const int ConnectedTimeoutMs = 15000;
    private const int MaxSendableMessageSize = 524288;

    internal object Lock { get; } = new();

    private readonly ConcurrentQueue<RawOutMessage> _toTransmit = new();
    private readonly Dictionary<HSteamListenSocket, SteamListener> _listenerMap = new();
    private readonly Dictionary<uint, SteamConnection> _connectionMap = new();
    private readonly List<SteamListener> _listeners = new();
    private readonly List<SteamConnection> _connections = new();
    private readonly Queue<IDisposable> _toDispose = new();

    private Callback<SteamNetConnectionStatusChangedCallback_t> _statusCallback = null!;
    private IntPtr[] _messageBuffer = null!;
    private CSteamID _localUser;
    private DateTime _lastStatUpdate;
    private int _channelPool;
    private bool _initialized;
    private bool _disposed;

    internal SteamNetworkingConfigValue_t[] DefaultConfigMain { get; private set; } = null!;
    internal SteamNetworkingConfigValue_t[] DefaultConfigBackground { get; private set; } = null!;

    internal SteamNetworkingConfigValue_t[] GetConfig(int channelIndex) => channelIndex switch
    {
        0 => DefaultConfigMain,
        1 => DefaultConfigBackground,
        _ => throw new ArgumentOutOfRangeException(nameof(channelIndex), "Steam channel index must be 0 or 1"),
    };

    public int Priority => 100;

    // Steam relay has no host port - addressing is identity-based.
    public bool UsesPort => false;

    // True once Initialize has succeeded. Bootstrap should skip the manager (and not register it)
    // if Steam isn't running. - xlinka
    public bool IsInitialized => _initialized;

    public CSteamID LocalUser => _localUser;

    // Wires up the relay callback and reads the local CSteamID. Must be called after
    // SteamAPI.Init() (or returns false silently if Steam is unavailable). - xlinka
    public bool Initialize()
    {
        if (_initialized) return true;
        try
        {
            _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            _messageBuffer = new IntPtr[MessageBufferSize];
            SteamNetworkingSockets.GetIdentity(out var identity);
            _localUser = identity.GetSteamID();

            DefaultConfigMain = BuildConfig();
            DefaultConfigBackground = BuildConfig();

            _initialized = true;
            LumoraLogger.Log($"SteamNetworkManager: ready, local SteamID={_localUser.m_SteamID}");
            return true;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SteamNetworkManager: init failed ({ex.Message}) - Steam transport disabled");
            return false;
        }
    }

    public IConnection CreateConnection(Uri uri)
    {
        if (uri == null) throw new ArgumentNullException(nameof(uri));
        if (!_initialized) throw new InvalidOperationException("SteamNetworkManager: Initialize() not called");
        ParseUri(uri, out var user, out var channelGroup, out var sessionId);
        lock (Lock)
        {
            var conn = new SteamConnection(user, channelGroup, sessionId, this);
            conn.Address = uri;
            _connections.Add(conn);
            return conn;
        }
    }

    public IListener CreateListener(ushort port, string sessionId)
    {
        if (!_initialized) throw new InvalidOperationException("SteamNetworkManager: Initialize() not called");
        lock (Lock)
        {
            var channel = AllocateChannelGroup();
            var listener = new SteamListener(_localUser, channel, sessionId ?? string.Empty, this);
            _listeners.Add(listener);
            return listener;
        }
    }

    public void GetSupportedSchemes(List<string> schemes)
    {
        if (schemes != null) schemes.Add(SCHEME);
    }

    public bool SupportsScheme(string scheme)
        => string.Equals(scheme, SCHEME, StringComparison.OrdinalIgnoreCase);

    public List<Uri> GetPrioritizedUriList(IEnumerable<Uri> uris, out string expectedSessionId)
    {
        var result = new List<Uri>();
        expectedSessionId = null!;
        if (uris == null) return result;
        foreach (var uri in uris)
        {
            if (uri == null || !SupportsScheme(uri.Scheme)) continue;
            ParseUri(uri, out _, out _, out var sessionId);
            expectedSessionId ??= sessionId;
            result.Add(uri);
        }
        return result;
    }

    public void Update()
    {
        if (!_initialized) return;
        lock (Lock)
        {
            while (_toTransmit.TryDequeue(out var msg)) TransmitInternal(msg);
            while (_toDispose.Count > 0) _toDispose.Dequeue().Dispose();
            foreach (var listener in _listeners) listener.Update(_messageBuffer);
            foreach (var connection in _connections) connection.Update(_messageBuffer);

            if ((DateTime.UtcNow - _lastStatUpdate).TotalSeconds < 1.0) return;
            _lastStatUpdate = DateTime.UtcNow;
            foreach (var listener in _listeners) listener.UpdateStats();
            foreach (var connection in _connections) connection.UpdateStats();
        }
    }

    public void Stop()
    {
        lock (Lock)
        {
            for (int i = _listeners.Count - 1; i >= 0; i--) _listeners[i].Close();
            for (int i = _connections.Count - 1; i >= 0; i--) _connections[i].Close();
            while (_toDispose.Count > 0) _toDispose.Dequeue().Dispose();
            _listeners.Clear();
            _connections.Clear();
            _listenerMap.Clear();
            _connectionMap.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _statusCallback?.Dispose();
        _statusCallback = null!;
    }

    internal void RegisterListener(HSteamListenSocket socket, SteamListener listener) => _listenerMap[socket] = listener;

    internal void RegisterConnection(HSteamNetConnection connection, SteamConnection peer)
        => _connectionMap[connection.m_HSteamNetConnection] = peer;

    internal void UnregisterListener(SteamListener listener)
    {
        foreach (var s in listener.Listeners) _listenerMap.Remove(s);
        _listeners.Remove(listener);
        _toDispose.Enqueue(listener);
    }

    internal void UnregisterConnection(SteamConnection connection)
    {
        foreach (var handle in connection.Connections) _connectionMap.Remove(handle.m_HSteamNetConnection);
        _connections.Remove(connection);
        _toDispose.Enqueue(connection);
    }

    internal int AllocateChannelGroup() => _channelPool++;

    // Reliable messages over the per-frame send cap are split into a 4-byte size prefix + 512KB
    // chunks; receivers reassemble before surfacing the frame as one message. - xlinka
    internal static bool NeedReassembly(uint size)
    {
        // The 4-byte case is the prefix itself - never reassemble it. - xlinka
        if (size == 4) return true;
        return size > MaxSendableMessageSize;
    }

    // Stored in session metadata for clients to dial back through the relay. - xlinka
    public static Uri BuildUri(CSteamID host, int channelGroup, string sessionId)
        => new($"{SCHEME}://{host.m_SteamID}/{channelGroup}/{sessionId}");

    private static void ParseUri(Uri uri, out CSteamID user, out int channelGroup, out string sessionId)
    {
        if (!string.Equals(uri.Scheme, SCHEME, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Not a Steam URI: {uri}");
        user = new CSteamID(ulong.Parse(uri.Host));
        channelGroup = int.Parse(uri.Segments[1].Trim('/'));
        sessionId = uri.Segments.Length > 2 ? uri.Segments[2].Trim('/') : string.Empty;
    }

    private static SteamNetworkingConfigValue_t[] BuildConfig() => new[]
    {
        ConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, SendBufferSize),
        ConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, SendRateBytesPerSecond),
        ConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, SendRateBytesPerSecond),
        ConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, InitialTimeoutMs),
        ConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, ConnectedTimeoutMs),
    };

    private static SteamNetworkingConfigValue_t ConfigInt(ESteamNetworkingConfigValue key, int value)
    {
        var v = new SteamNetworkingConfigValue_t
        {
            m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
            m_eValue = key,
        };
        v.m_val.m_int32 = value;
        return v;
    }

    private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t info)
    {
        lock (Lock)
        {
            if (_listenerMap.TryGetValue(info.m_info.m_hListenSocket, out var listener))
            {
                listener.ConnectionStatusChanged(ref info);
                return;
            }
            if (_connectionMap.TryGetValue(info.m_hConn.m_HSteamNetConnection, out var connection))
            {
                connection.ConnectionStatusChanged(ref info);
                return;
            }
            LumoraLogger.Log(
                $"SteamNetworkManager: unrouted status {info.m_info.m_eState} conn={info.m_hConn} remote={info.m_info.m_identityRemote.GetSteamID()}");
        }
    }

    private void TransmitInternal(RawOutMessage msg)
    {
        var pinned = GCHandle.Alloc(msg.Data, GCHandleType.Pinned);
        try
        {
            var ptr = Marshal.UnsafeAddrOfPinnedArrayElement(msg.Data, 0);
            foreach (var target in msg.Targets)
            {
                if (target is SteamConnection sc) sc.Send(msg.Data, msg.Length, msg.UseReliable, msg.Background);
                // Non-Steam targets are routed via their own IConnection.Send;
                // we ignore them here to avoid double-sending. - xlinka
                _ = ptr;
            }
        }
        finally
        {
            pinned.Free();
        }
        msg.TransmissionFinished();
    }

    internal static void ExtractMessages(
        int count,
        IntPtr[] messages,
        SteamMessageHandler messageHandler,
        SteamConnectionFailureHandler failureHandler,
        ReassemblyStateFetcher reassemblyFetcher)
    {
        HashSet<uint> aborted = null!;
        for (int i = 0; i < count; i++)
        {
            var msgPtr = messages[i];
            var raw = Marshal.PtrToStructure<SteamNetworkingMessage_t>(msgPtr);

            uint handle = raw.m_conn.m_HSteamNetConnection;
            if (aborted != null && aborted.Contains(handle))
            {
                SteamNetworkingMessage_t.Release(msgPtr);
                continue;
            }

            ref var state = ref reassemblyFetcher(handle);
            bool isReliable = (raw.m_nFlags & Constants.k_nSteamNetworkingSend_Reliable) != 0;
            int payloadSize = raw.m_cbSize;

            if (isReliable && (state.buffer != null || payloadSize == 4))
            {
                if (state.buffer == null)
                {
                    state.expectingBytes = Marshal.ReadInt32(raw.m_pData);
                    state.buffer = state.expectingBytes > 0 ? new byte[state.expectingBytes] : Array.Empty<byte>();
                    state.receivedBytes = 0;
                }
                else
                {
                    if (state.receivedBytes < 0 || state.expectingBytes < 0
                        || state.receivedBytes + payloadSize > state.expectingBytes)
                    {
                        failureHandler(handle);
                        aborted ??= new HashSet<uint>();
                        aborted.Add(handle);
                        SteamNetworkingMessage_t.Release(msgPtr);
                        continue;
                    }
                    Marshal.Copy(raw.m_pData, state.buffer, state.receivedBytes, payloadSize);
                    state.receivedBytes += payloadSize;
                    if (state.receivedBytes == state.expectingBytes)
                    {
                        messageHandler(raw.m_identityPeer.GetSteamID().m_SteamID, handle, state.buffer, state.receivedBytes);
                        state.buffer = null!;
                        state.expectingBytes = -1;
                        state.receivedBytes = -1;
                    }
                }
            }
            else
            {
                var data = new byte[payloadSize];
                Marshal.Copy(raw.m_pData, data, 0, payloadSize);
                messageHandler(raw.m_identityPeer.GetSteamID().m_SteamID, handle, data, payloadSize);
            }

            SteamNetworkingMessage_t.Release(msgPtr);
        }
    }
}
#endif
