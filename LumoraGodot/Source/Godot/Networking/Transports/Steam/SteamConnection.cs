// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
// Steam is a desktop-only transport. The Steamworks binding has no Android build, and a type that
// merely MENTIONS a Steamworks type cannot be loaded there: the whole engine init failed on a Pico 4
// with 'Could not load type of field SteamNetworkManager:_listenerMap', long before any runtime
// 'is this Android' check could run. Compile the transport out on mobile entirely. -xlinka
#if GODOT_PC

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Steamworks;
using Lumora.Nexus.Transport;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Networking.Transports.Steam;

// One peer connection over SteamNetworkingSockets. Maintains two channels (foreground +
// background) so high-priority frames don't queue behind bulk asset transfers - same shape as the
// LNL transport's reliable/sequenced channel split. Handles >512KB reliable payloads via
// length-prefix + fragment reassembly; small frames go straight through. - xlinka
public sealed class SteamConnection : IConnection
{
    private const int ChannelCount = 2;
    private const int MaxMessageSize = 524288; // 512 KB SendMessageToConnection cap.
    private const int SizePrefixLength = 4;
    private const int SendFlagsReliable = Constants.k_nSteamNetworkingSend_Reliable;
    private const int SendFlagsUnreliable = Constants.k_nSteamNetworkingSend_Unreliable;

    private enum State { Connecting, Open, Closed }

    private readonly SteamNetworkManager _manager;
    private readonly CSteamID _remoteId;
    private readonly int _remoteChannelGroup;
    private readonly string _sessionId;

    private readonly HSteamNetConnection[] _connections = new HSteamNetConnection[ChannelCount];
    private readonly bool[] _connectionsConnected = new bool[ChannelCount];
    private readonly ReassemblyState[] _reassembly = new ReassemblyState[ChannelCount];
    private HSteamNetPollGroup _pollGroup;

    private State _state;
    private ulong _receivedBytes;
    private SteamNetConnectionRealTimeStatus_t _lastMainStatus;
    private SteamNetConnectionRealTimeStatus_t _lastBackgroundStatus;

    public bool IsOpen => _state == State.Open;
    public string FailReason { get; private set; } = string.Empty;
    public SteamCloseReason CloseReason { get; private set; }
    public IPAddress IP => null!;
    public Uri Address { get; internal set; } = null!;
    public string Identifier { get; }
    public ulong ReceivedBytes => _receivedBytes;
    // m_nPing is refreshed every transport Update via UpdateStats. Steam Datagram Relay is always
    // end-to-end encrypted, so IsEncrypted is unconditionally true once open. -xlinka
    public int Ping => IsOpen ? _lastMainStatus.m_nPing : -1;
    public bool IsEncrypted => IsOpen;
    public string TransportName => "Steam";

    public CSteamID RemoteId => _remoteId;

    public event Action<IConnection> Connected = null!;
    public event Action<IConnection> ConnectionFailed = null!;
    public event Action<IConnection> Closed = null!;
    public event Action<byte[], int> DataReceived = null!;

    internal IEnumerable<HSteamNetConnection> Connections
    {
        get
        {
            for (int i = 0; i < _connections.Length; i++)
            {
                var handle = _connections[i];
                if (handle != HSteamNetConnection.Invalid) yield return handle;
            }
        }
    }

    internal SteamConnection(CSteamID remoteId, int remoteChannelGroup, string sessionId, SteamNetworkManager manager)
    {
        _remoteId = remoteId;
        _remoteChannelGroup = remoteChannelGroup;
        _sessionId = sessionId;
        _manager = manager;
        Identifier = "Steam:" + remoteId.m_SteamID.ToString(CultureInfo.InvariantCulture);
        _pollGroup = SteamNetworkingSockets.CreatePollGroup();
        for (int i = 0; i < ChannelCount; i++)
        {
            _reassembly[i].buffer = null!;
            _reassembly[i].expectingBytes = -1;
            _reassembly[i].receivedBytes = -1;
        }
    }

    public void Connect(Action<string> statusCallback)
    {
        statusCallback?.Invoke($"SteamNetworkingSockets P2P to {_remoteId.m_SteamID}");
        var identity = default(SteamNetworkingIdentity);
        identity.SetSteamID64(_remoteId.m_SteamID);

        lock (_manager.Lock)
        {
            int basePort = _remoteChannelGroup * ChannelCount;
            for (int i = 0; i < ChannelCount; i++)
            {
                var config = _manager.GetConfig(i);
                _connections[i] = SteamNetworkingSockets.ConnectP2P(ref identity, basePort + i, config.Length, config);
                SteamNetworkingSockets.SetConnectionPollGroup(_connections[i], _pollGroup);
                _manager.RegisterConnection(_connections[i], this);
            }
        }
    }

    // polled centrally by the manager - xlinka
    public void Poll() { }

    public void Send(byte[] data, int length, bool reliable, bool background)
    {
        if (!IsOpen) return;
        if (length <= 0) return;

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var ptr = Marshal.UnsafeAddrOfPinnedArrayElement(data, 0);
            TransmitInternal(ptr, (uint)length, reliable, background);
        }
        finally
        {
            handle.Free();
        }
    }

    public void Close() => Close(SteamCloseReason.ClosedLocally);

    internal void Close(SteamCloseReason reason)
    {
        if (_state == State.Closed) return;
        lock (_manager.Lock)
        {
            if (_state == State.Closed) return;
            bool wasOpen = _state == State.Open;
            _state = State.Closed;
            CloseReason = reason;
            FailReason = reason.ToString();
            _manager.UnregisterConnection(this);
            if (!wasOpen)
            {
                ConnectionFailed?.Invoke(this);
            }
            Closed?.Invoke(this);
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < _connections.Length; i++)
        {
            var handle = _connections[i];
            if (handle != HSteamNetConnection.Invalid)
            {
                SteamNetworkingSockets.CloseConnection(handle, (int)CloseReason, null, false);
                _connections[i] = default;
            }
        }
        if (_pollGroup != default)
        {
            SteamNetworkingSockets.DestroyPollGroup(_pollGroup);
            _pollGroup = default;
        }
    }

    // Listener calls this after AcceptConnection to bind a channel handle to this peer. Returns
    // false if the handle/channel combination is invalid. - xlinka
    internal bool AssignConnection(HSteamNetConnection connection, int channel)
    {
        if (_state != State.Connecting) return false;
        if (channel < 0 || channel >= ChannelCount) return false;
        if (_connectionsConnected[channel]) return false;
        _connections[channel] = connection;
        _connectionsConnected[channel] = true;
        SteamNetworkingSockets.SetConnectionPollGroup(connection, _pollGroup);
        if (AllConnected()) _state = State.Open;
        return true;
    }

    internal void Update(IntPtr[] buffer)
    {
        try
        {
            if (!IsOpen) return;
            int count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_pollGroup, buffer, buffer.Length);
            SteamNetworkManager.ExtractMessages(count, buffer, OnMessage, OnReceiveError, GetReassembly);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"SteamConnection.Update: {ex}");
            Close(SteamCloseReason.UnhandledException);
        }
    }

    internal void UpdateStats()
    {
        if (!IsOpen) return;
        // nLanes=0 so we skip per-lane status; we only care about overall ping
        // / send rate / pending bytes. pLanes still has to be a valid ref  - 
        // pass a stack-local sentinel. - xlinka
        SteamNetConnectionRealTimeLaneStatus_t lanes = default;
        SteamNetworkingSockets.GetConnectionRealTimeStatus(_connections[0], ref _lastMainStatus, 0, ref lanes);
        SteamNetworkingSockets.GetConnectionRealTimeStatus(_connections[1], ref _lastBackgroundStatus, 0, ref lanes);
    }

    internal ref ReassemblyState GetReassembly(uint connectionHandle)
    {
        for (int i = 0; i < _connections.Length; i++)
        {
            if (_connections[i].m_HSteamNetConnection == connectionHandle) return ref _reassembly[i];
        }
        throw new InvalidOperationException("Steam connection handle not owned by this SteamConnection");
    }

    private void OnReceiveError(uint connectionHandle)
    {
        for (int i = 0; i < _connections.Length; i++)
        {
            if (_connections[i].m_HSteamNetConnection == connectionHandle)
            {
                Close(SteamCloseReason.ReceiveError);
                return;
            }
        }
    }

    internal void OnMessage(ulong senderId, uint connectionHandle, byte[] data, int length)
    {
        if (!IsOpen) return;
        if (GetChannel(connectionHandle) < 0)
        {
            LumoraLogger.Warn($"SteamConnection: invalid channel for sender {senderId}, handle {connectionHandle}, size {length}");
            return;
        }
        _receivedBytes += (ulong)length;
        DataReceived?.Invoke(data, length);
    }

    internal void ConnectionStatusChanged(ref SteamNetConnectionStatusChangedCallback_t info)
    {
        if (_state == State.Closed) return;
        switch (info.m_info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                if (_state == State.Open)
                {
                    Close(SteamCloseReason.ChannelMismatch);
                    return;
                }
                int channel = GetChannel(info.m_hConn.m_HSteamNetConnection);
                if (channel < 0)
                {
                    Close(SteamCloseReason.ChannelMismatch);
                    return;
                }
                _connectionsConnected[channel] = true;
                if (AllConnected())
                {
                    _state = State.Open;
                    Connected?.Invoke(this);
                }
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                Close(SteamCloseReason.ClosedRemotely);
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                LumoraLogger.Warn($"SteamConnection: local problem, end={info.m_info.m_eEndReason}, debug={info.m_info.m_szEndDebug}");
                Close(SteamCloseReason.LocalProblem);
                break;
        }
    }

    private bool AllConnected()
    {
        for (int i = 0; i < _connectionsConnected.Length; i++)
        {
            if (!_connectionsConnected[i]) return false;
        }
        return true;
    }

    internal int GetChannel(uint connectionHandle)
    {
        for (int i = 0; i < _connections.Length; i++)
        {
            if (_connections[i].m_HSteamNetConnection == connectionHandle) return i;
        }
        return -1;
    }

    private void TransmitInternal(IntPtr data, uint count, bool reliable, bool background)
    {
        if (SteamNetworkManager.NeedReassembly(count) && reliable)
        {
            // Send the 4-byte total length so the receiver can allocate one
            // contiguous buffer, then walk the payload in <=512KB chunks. Pin a
            // small heap buffer for the prefix rather than reach for stackalloc
            // so this file stays safe-code. - xlinka
            var sizeBytes = BitConverter.GetBytes(count);
            var sizeHandle = GCHandle.Alloc(sizeBytes, GCHandleType.Pinned);
            try
            {
                if (!TransmitRaw(sizeHandle.AddrOfPinnedObject(), SizePrefixLength, true, background)) return;
            }
            finally
            {
                sizeHandle.Free();
            }

            uint remaining = count;
            var cursor = data;
            while (remaining != 0)
            {
                uint chunk = remaining < MaxMessageSize ? remaining : MaxMessageSize;
                if (!TransmitRaw(cursor, chunk, true, background)) break;
                cursor += (int)chunk;
                remaining -= chunk;
            }
        }
        else
        {
            TransmitRaw(data, count, reliable, background);
        }
    }

    private bool TransmitRaw(IntPtr data, uint count, bool reliable, bool background)
    {
        if (count > MaxMessageSize)
            throw new InvalidOperationException($"Steam frame too large ({count}); reassembly only supports reliable messages.");

        var handle = _connections[background ? 1 : 0];
        int flags = reliable ? SendFlagsReliable : SendFlagsUnreliable;
        var result = SteamNetworkingSockets.SendMessageToConnection(handle, data, count, flags, out _);
        if (result != EResult.k_EResultOK && result != EResult.k_EResultIgnored)
        {
            SteamNetworkingSockets.GetConnectionInfo(handle, out var info);
            LumoraLogger.Warn(
                $"SteamConnection: send {count}B failed, result={result}, state={info.m_eState}, end={info.m_eEndReason}");
            Close(SteamCloseReason.TransmissionError);
            return false;
        }
        return true;
    }

    public override string ToString()
        => $"SteamConnection({_remoteId.m_SteamID}, state={_state}, handles=[{string.Join(",", _connections)}])";
}
#endif
