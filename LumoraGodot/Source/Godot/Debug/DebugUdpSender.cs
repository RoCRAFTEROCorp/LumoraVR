// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Lumora.Core.Logging;

namespace Lumora.Godot.Debug;

#nullable enable

/// <summary>
/// Sends log messages, performance data, and memory breakdowns
/// to the debug console process via UDP on localhost.
/// Fire-and-forget: if the console isn't running, packets are silently dropped.
/// </summary>
public class DebugUdpSender : IDisposable
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _endpoint;
    public const int Port = 19840;

    public DebugUdpSender()
    {
        _client = new UdpClient();
        _endpoint = new IPEndPoint(IPAddress.Loopback, Port);

        Logger.OnLogWritten += OnLog;
        AppDomain.CurrentDomain.UnhandledException += OnCrash;
    }

    private void OnLog(Logger.LogLevel level, string timestamp, string message)
    {
        // Sanitize pipes from message to not break protocol
        var safeMsg = message.Replace('|', '/');
        Send($"{level}|{timestamp}|{safeMsg}");
    }

    private void OnCrash(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Send($"ERROR|{DateTime.Now:HH:mm:ss}|UNHANDLED EXCEPTION: {ex?.Message}");
        Send($"ERROR|{DateTime.Now:HH:mm:ss}|Stack: {ex?.StackTrace}");
        if (e.IsTerminating)
            Send($"ERROR|{DateTime.Now:HH:mm:ss}|APPLICATION TERMINATING");
    }

    /// <summary>
    /// Send performance metrics to the debug console.
    /// </summary>
    public void SendPerf(
        float fps, float frameTime, float renderTime, float physicsTime,
        string worldName, int slots, int components, int users,
        long gcMemBytes, long videoMemBytes, int godotObjects, int godotNodes)
    {
        // worldName is the one user-authored string in this packet and was the only one going in raw.
        // '|' is the field separator, so a world called "Bob | Test" shifted every field after it: slot
        // count read the world name's tail, component count read the slot count, and so on down the
        // line. The receiver only checks the field COUNT, so nothing rejected it - the tab just showed
        // confident nonsense. -xlinka
        Send($"PERF|{fps:F1}|{frameTime:F2}|{renderTime:F2}|{physicsTime:F2}" +
             $"|{SanitizeField(worldName)}|{slots}|{components}|{users}" +
             $"|{gcMemBytes}|{videoMemBytes}|{godotObjects}|{godotNodes}");
    }

    /// <summary>
    /// Send memory breakdown to the debug console.
    /// Format:
    /// MEM|committedBytes|gcBytes|gen0|gen1|gen2|estimatedBytes|workingSetBytes|privateBytes|videoBytes|godotObjects|godotNodes|name:count:bytes,name:count:bytes,...
    /// </summary>
    public void SendMemory(
        long committedBytes,
        long gcBytes,
        int gen0, int gen1, int gen2,
        long estimatedBytes,
        long workingSetBytes,
        long privateBytes,
        long videoBytes,
        int godotObjects,
        int godotNodes,
        IEnumerable<(string name, int count, long bytes)> topComponents)
    {
        var components = string.Join(",",
            topComponents.Select(c => $"{SanitizeField(c.name)}:{c.count}:{c.bytes}"));

        Send(
            $"MEM|{committedBytes}|{gcBytes}|{gen0}|{gen1}|{gen2}|{estimatedBytes}" +
            $"|{workingSetBytes}|{privateBytes}|{videoBytes}|{godotObjects}|{godotNodes}|{components}");
    }

    /// <summary>
    /// Send GPU/render-pipeline stats to the debug console (the "Render / GPU" tab).
    /// Format: RNDR|drawCalls|primitives|objectsInFrame|textureMemBytes|bufferMemBytes|videoMemBytes
    /// </summary>
    public void SendRenderStats(
        long drawCalls, long primitives, long objectsInFrame,
        long textureMemBytes, long bufferMemBytes, long videoMemBytes)
    {
        Send($"RNDR|{drawCalls}|{primitives}|{objectsInFrame}|{textureMemBytes}|{bufferMemBytes}|{videoMemBytes}");
    }

    /// <summary>
    /// Send the latest-frame update profile, both by component type and by slot, for the profiler tab.
    /// Format: PROF|name:ms:count,name:ms:count,...|name:ms:count,... (components segment | slots segment)
    /// </summary>
    public void SendProfile(
        IEnumerable<(string name, double ms, int count)> byComponent,
        IEnumerable<(string name, double ms, int count)> bySlot)
    {
        string comps = string.Join(",", byComponent.Select(c =>
            $"{SanitizeField(c.name)}:{c.ms.ToString("F4", CultureInfo.InvariantCulture)}:{c.count}"));
        string slots = string.Join(",", bySlot.Select(c =>
            $"{SanitizeField(c.name)}:{c.ms.ToString("F4", CultureInfo.InvariantCulture)}:{c.count}"));
        Send($"PROF|{comps}|{slots}");
    }

    /// <summary>
    /// Send the networking snapshot to the debug console (the "Network" tab): session role/identity,
    /// the sync traffic counters, and one row per live connection (user, transport, endpoint, ping,
    /// encryption, bytes received).
    /// Format:
    /// NET|role|world|sessionId|visibility|syncRate|connCount|latencyMs|allEncrypted
    ///    |sDeltas|rDeltas|sFulls|rFulls|sStreams|rStreams|sRaw|rRaw|corrections|processed|lastDeltaChanges
    ///    |toProcess|toTransmit|incoming|pendingStreams|uploads|downloads|assetRequests|relays
    ///    |name~transport~endpoint~ping~enc~recvBytes;name~...
    /// </summary>
    public void SendNetwork(
        string role, string worldName, string sessionId, string visibility,
        int syncRate, int connCount, int latencyMs, bool allEncrypted,
        int sentDeltas, int recvDeltas, int sentFulls, int recvFulls,
        int sentStreams, int recvStreams, int sentRaw, int recvRaw,
        int corrections, int processed, int lastDeltaChanges,
        int toProcess, int toTransmit, int incoming, int pendingStreams,
        int uploads, int downloads, int assetRequests, int relays,
        IEnumerable<(string name, string transport, string endpoint, int ping, bool encrypted, ulong recvBytes)> connections)
    {
        var rows = string.Join(";", connections.Select(c =>
            $"{NetField(c.name)}~{NetField(c.transport)}~{NetField(c.endpoint)}~{c.ping}~{(c.encrypted ? 1 : 0)}~{c.recvBytes}"));

        Send(
            $"NET|{NetField(role)}|{NetField(worldName)}|{NetField(sessionId)}|{NetField(visibility)}" +
            $"|{syncRate}|{connCount}|{latencyMs}|{(allEncrypted ? 1 : 0)}" +
            $"|{sentDeltas}|{recvDeltas}|{sentFulls}|{recvFulls}|{sentStreams}|{recvStreams}|{sentRaw}|{recvRaw}" +
            $"|{corrections}|{processed}|{lastDeltaChanges}" +
            $"|{toProcess}|{toTransmit}|{incoming}|{pendingStreams}|{uploads}|{downloads}|{assetRequests}|{relays}" +
            $"|{rows}");
    }

    private static string SanitizeField(string value)
    {
        return value
            .Replace('|', '/')
            .Replace(':', '_')
            .Replace(',', ';');
    }

    // Network rows use '~' (field) and ';' (row) separators on top of the '|' top-level delimiter, so
    // strip all three from free-text fields (user names, endpoints) to keep the packet parseable.
    private static string NetField(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "-";
        return value
            .Replace('|', '/')
            .Replace('~', '-')
            .Replace(';', ',');
    }

    // One frame's full cost breakdown: the six world phases plus the late pass, alongside what the
    // renderer charged for the same frame. This is the packet that answers "what is eating my frame",
    // which nothing before it could - the world phases were computed every frame and thrown away, and
    // the old PERF frame time was not a measurement at all, just 1000/fps off a rounded replicated
    // value. Everything here is measured. -xlinka
    //
    // Draw calls arrive split by PASS (visible / shadow / canvas) because "12000 draw calls" and "9000
    // of them are the shadow pass" are completely different problems with completely different fixes.
    // Viewports arrive separately for the same reason: the dash, every mirror and every camera render
    // the world again, and a single global number cannot say which one is doing it.
    public void SendFrame(in FramePacket f)
    {
        var sb = new StringBuilder(1024);
        sb.Append("FRAM");
        void N(double v) => sb.Append('|').Append(v.ToString("F3", CultureInfo.InvariantCulture));
        void L(long v) => sb.Append('|').Append(v.ToString(CultureInfo.InvariantCulture));

        N(f.CpuFrameMs);
        N(f.WorldTotalMs); N(f.SyncMs); N(f.PreMs); N(f.CompMs); N(f.ChangeMs); N(f.HooksMs); N(f.EndMs); N(f.LateMs);
        N(f.RenderCpuMs); N(f.RenderGpuMs); N(f.FrameSetupCpuMs);
        N(f.EngineInputMs); N(f.EngineCoroutinesMs); N(f.EngineFixedMs); N(f.EngineAssetsMs);
        N(f.HostNetworkMs); N(f.HostEngineMs); N(f.HostTailMs);
        L(f.DrawsVisible); L(f.DrawsShadow); L(f.DrawsCanvas);
        L(f.ObjectsVisible); L(f.ObjectsShadow);
        L(f.GpuTextureBytes); L(f.GpuBufferBytes); L(f.GpuTotalBytes);
        L(f.OrphanNodes); L(f.StaticMemBytes);
        L(f.PhysicsActiveObjects); L(f.PhysicsCollisionPairs); L(f.PhysicsIslands);
        // Pipeline compilations are the shader-stutter tell: a nonzero count mid-session means the
        // driver stopped to compile something, which reads as a hitch nothing else in the profile
        // explains. Zero on a warm frame is the healthy answer.
        L(f.PipeCanvas); L(f.PipeMesh); L(f.PipeSurface); L(f.PipeDraw); L(f.PipeSpecialization);
        L(f.DriverAllocationCount); L(f.DriverTotalMemBytes);

        sb.Append('|');
        bool first = true;
        foreach (var entry in f.HookCosts)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(SanitizeField(entry.Name)).Append(':')
              .Append(entry.Ms.ToString("F4", CultureInfo.InvariantCulture)).Append(':')
              .Append(entry.Count.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append('|');
        first = true;
        foreach (var viewport in f.Viewports)
        {
            if (!first) sb.Append(';');
            first = false;
            sb.Append(NetField(viewport.Name)).Append('~')
              .Append(viewport.Draws.ToString(CultureInfo.InvariantCulture)).Append('~')
              .Append(viewport.GpuMs.ToString("F3", CultureInfo.InvariantCulture)).Append('~')
              .Append(viewport.CpuMs.ToString("F3", CultureInfo.InvariantCulture));
        }

        sb.Append('|').Append(SanitizeField(f.PerfReport ?? string.Empty));

        Send(sb.ToString());
    }

    public readonly struct FrameViewport
    {
        public FrameViewport(string name, long draws, double gpuMs, double cpuMs)
        {
            Name = name; Draws = draws; GpuMs = gpuMs; CpuMs = cpuMs;
        }
        public string Name { get; }
        public long Draws { get; }
        public double GpuMs { get; }
        public double CpuMs { get; }
    }

    // Grouped into a struct because the argument list had passed the point where a caller could get the
    // order right by reading it.
    public struct FramePacket
    {
        public double CpuFrameMs;
        public double WorldTotalMs, SyncMs, PreMs, CompMs, ChangeMs, HooksMs, EndMs, LateMs;
        public double RenderCpuMs, RenderGpuMs, FrameSetupCpuMs;
        public double EngineInputMs, EngineCoroutinesMs, EngineFixedMs, EngineAssetsMs;
        public double HostNetworkMs, HostEngineMs, HostTailMs;
        public long DrawsVisible, DrawsShadow, DrawsCanvas, ObjectsVisible, ObjectsShadow;
        public long GpuTextureBytes, GpuBufferBytes, GpuTotalBytes;
        public long OrphanNodes, StaticMemBytes;
        public long PhysicsActiveObjects, PhysicsCollisionPairs, PhysicsIslands;
        public long PipeCanvas, PipeMesh, PipeSurface, PipeDraw, PipeSpecialization;
        public long DriverAllocationCount, DriverTotalMemBytes;
        public string? PerfReport;
        public IEnumerable<(string Name, double Ms, int Count)> HookCosts;
        public IEnumerable<FrameViewport> Viewports;
    }

    // UDP datagram ceiling with headroom. Anything past this cannot be delivered at all.
    private const int MaxDatagramBytes = 65000;

    private void Send(string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            if (bytes.Length <= MaxDatagramBytes)
            {
                _client.Send(bytes, bytes.Length, _endpoint);
                return;
            }

            // An oversize packet used to vanish here with no trace at all, so a tab would quietly stop
            // updating and the only symptom was stale numbers that still looked plausible. The list
            // packets (MEM, PROF, NET) are the ones that can grow, and they grow exactly when a session
            // gets big - which is when someone is most likely to be profiling it.
            //
            // A marker rather than a log call, because Send is what Logger.OnLogWritten calls: logging
            // from in here would recurse straight back into itself. -xlinka
            int split = message.IndexOf('|');
            string prefix = split > 0 ? message.Substring(0, split) : "?";
            var marker = Encoding.UTF8.GetBytes($"DROP|{prefix}|{bytes.Length}");
            _client.Send(marker, marker.Length, _endpoint);
        }
        catch { /* fire and forget */ }
    }

    public void Dispose()
    {
        Logger.OnLogWritten -= OnLog;
        AppDomain.CurrentDomain.UnhandledException -= OnCrash;
        _client?.Dispose();
    }
}

