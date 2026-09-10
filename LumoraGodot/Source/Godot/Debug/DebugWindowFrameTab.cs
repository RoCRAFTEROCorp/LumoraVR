// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using LogLevel = Lumora.Core.Logging.Logger.LogLevel;

namespace Lumora.Godot.Debug;

#nullable enable

// The Frame tab: where a frame's time actually went, and the CSV that lets someone hand us the evidence.
//
// Everything on this tab is measured rather than derived. The world's six update phases were already
// being timed every frame and thrown away unless the frame broke 25 ms, at which point they became a
// sentence in a log; render CPU/GPU come from the renderer with measurement explicitly enabled; draw
// calls arrive split by pass. The old Profiler tab plots a "frame time" that is really 1000/fps off a
// rounded, replicated value, which is why a world can sit at 40 fps with nothing on screen looking
// wrong and no number to point at.
//
// Separate file because DebugWindow.cs is already 1500 lines and none of this needs to touch it beyond
// four call sites. -xlinka
public partial class DebugWindow
{
    // 0.25 s per sample, so this is a little over 20 minutes of history. The cap matters: a session left
    // recording overnight must not grow until it is the thing eating the memory it is reporting on.
    private const int MaxFrameSamples = 5000;

    // Bump whenever the FRAM layout changes. Mismatch is reported, never guessed at.
    private const int FrameFieldCount = 43;
    private bool _warnedFrameLayout;

    private readonly List<FrameSample> _frameSamples = new();
    private FrameSample? _latestFrame;
    private bool _frameUiDirty;
    private long _framePacketCount;
    private bool _frameRecording = true;
    private string _lastExportDirectory = string.Empty;

    private CheckButton? _frameRecordToggle;
    private Button? _frameExportBtn;
    private Button? _frameOpenFolderBtn;
    private Button? _frameClearBtn;
    private Label? _frameCaptureStatus;
    private Tree? _framePhaseTree;
    private Tree? _frameHookTree;
    private Label? _frameShadowHint;
    private Label? _frameStutterHint;
    private Tree? _frameViewportTree;
    private Label? _frameRenderCpuValue;
    private Label? _frameRenderGpuValue;
    private Label? _frameSetupValue;
    private Label? _frameCpuValue;
    private Label? _frameDrawsVisibleValue;
    private Label? _frameDrawsShadowValue;
    private Label? _frameDrawsCanvasValue;
    private Label? _frameObjectsVisibleValue;
    private Label? _frameObjectsShadowValue;
    private Label? _frameGpuMemValue;

    private sealed class HookCost
    {
        public string Name = string.Empty;
        public double Ms;
        public int Count;
    }

    private sealed class FrameSample
    {
        public double WallClockSeconds;
        public double CpuFrameMs;
        public double WorldTotalMs;
        public double SyncMs;
        public double PreMs;
        public double CompMs;
        public double ChangeMs;
        public double HooksMs;
        public double EndMs;
        public double LateMs;
        public double RenderCpuMs;
        public double RenderGpuMs;
        public double FrameSetupCpuMs;
        public double EngineInputMs;
        public double EngineCoroutinesMs;
        public double EngineFixedMs;
        public double EngineAssetsMs;
        public double HostNetworkMs;
        public double HostEngineMs;
        public double HostTailMs;
        public long DrawsVisible;
        public long DrawsShadow;
        public long DrawsCanvas;
        public long ObjectsVisible;
        public long ObjectsShadow;
        public long GpuTextureBytes;
        public long GpuBufferBytes;
        public long GpuTotalBytes;
        public long OrphanNodes;
        public long StaticMemBytes;
        public long PhysicsActiveObjects;
        public long PhysicsCollisionPairs;
        public long PhysicsIslands;
        public long PipeCanvas;
        public long PipeMesh;
        public long PipeSurface;
        public long PipeDraw;
        public long PipeSpecialization;
        public long DriverAllocationCount;
        public long DriverTotalMemBytes;
        public string PerfReport = string.Empty;
        public List<HookCost> Hooks = new();
        public List<ViewportCost> Viewports = new();

        public long PipelineTotal => PipeCanvas + PipeMesh + PipeSurface + PipeDraw + PipeSpecialization;
    }

    private sealed class ViewportCost
    {
        public string Name = string.Empty;
        public long Draws;
        public double GpuMs;
        public double CpuMs;
    }

    private void CacheFrameTabReferences()
    {
        _frameRecordToggle = GetNodeOrNull<CheckButton>("%FrameRecordToggle");
        _frameExportBtn = GetNodeOrNull<Button>("%FrameExportBtn");
        _frameOpenFolderBtn = GetNodeOrNull<Button>("%FrameOpenFolderBtn");
        _frameClearBtn = GetNodeOrNull<Button>("%FrameClearBtn");
        _frameCaptureStatus = GetNodeOrNull<Label>("%FrameCaptureStatus");
        _framePhaseTree = GetNodeOrNull<Tree>("%FramePhaseTree");
        _frameHookTree = GetNodeOrNull<Tree>("%FrameHookTree");
        _frameShadowHint = GetNodeOrNull<Label>("%FrameShadowHint");
        _frameStutterHint = GetNodeOrNull<Label>("%FrameStutterHint");
        _frameViewportTree = GetNodeOrNull<Tree>("%FrameViewportTree");
        _frameRenderCpuValue = GetNodeOrNull<Label>("%FrameRenderCpuValue");
        _frameRenderGpuValue = GetNodeOrNull<Label>("%FrameRenderGpuValue");
        _frameSetupValue = GetNodeOrNull<Label>("%FrameSetupValue");
        _frameCpuValue = GetNodeOrNull<Label>("%FrameCpuValue");
        _frameDrawsVisibleValue = GetNodeOrNull<Label>("%FrameDrawsVisibleValue");
        _frameDrawsShadowValue = GetNodeOrNull<Label>("%FrameDrawsShadowValue");
        _frameDrawsCanvasValue = GetNodeOrNull<Label>("%FrameDrawsCanvasValue");
        _frameObjectsVisibleValue = GetNodeOrNull<Label>("%FrameObjectsVisibleValue");
        _frameObjectsShadowValue = GetNodeOrNull<Label>("%FrameObjectsShadowValue");
        _frameGpuMemValue = GetNodeOrNull<Label>("%FrameGpuMemValue");
    }

    private void ConfigureFrameTab()
    {
        ConfigureProfilerTree(_framePhaseTree, "Phase");
        _framePhaseTree?.SetColumnTitle(2, "Share");
        ConfigureProfilerTree(_frameHookTree, "Hook Type");
        _frameHookTree?.SetColumnTitle(2, "Calls");
        ConfigureProfilerTree(_frameViewportTree, "Viewport");
        _frameViewportTree?.SetColumnTitle(1, "GPU");
        _frameViewportTree?.SetColumnTitle(2, "Draws");

        _frameRecordToggle?.Connect("toggled", Callable.From<bool>(pressed => _frameRecording = pressed));
        _frameExportBtn?.Connect("pressed", Callable.From(ExportFrameCsv));
        _frameOpenFolderBtn?.Connect("pressed", Callable.From(OpenExportFolder));
        _frameClearBtn?.Connect("pressed", Callable.From(ClearFrameSamples));
    }

    // Folded across the whole capture, from the PROF packet. The frame CSV says WHICH PHASE the time
    // went to; this says which component type. Without it a capture can prove that 40% of the frame is
    // the queued input work and still not name the component doing it, which is where the answer
    // actually lives. -xlinka
    private readonly Dictionary<string, HookCost> _componentTotals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _componentPeaks = new(StringComparer.Ordinal);
    private int _componentSamples;

    private void AccumulateComponentProfile(List<(string name, float ms, int count)> components)
    {
        if (!_frameRecording)
        {
            return;
        }

        _componentSamples++;
        foreach (var entry in components)
        {
            if (!_componentTotals.TryGetValue(entry.name, out var acc))
            {
                acc = new HookCost { Name = entry.name };
                _componentTotals[entry.name] = acc;
            }
            acc.Ms += entry.ms;
            acc.Count += entry.count;

            _componentPeaks.TryGetValue(entry.name, out double worst);
            if (entry.ms > worst)
                _componentPeaks[entry.name] = entry.ms;
        }
    }

    private string BuildComponentCsv()
    {
        var ordered = new List<HookCost>(_componentTotals.Values);
        ordered.Sort((a, b) => b.Ms.CompareTo(a.Ms));

        var sb = new StringBuilder(ordered.Count * 90);
        sb.AppendLine("component_type,total_ms,mean_ms_per_sample,peak_ms_in_one_sample,total_calls,samples_seen");
        int samples = System.Math.Max(1, _componentSamples);
        foreach (var entry in ordered)
        {
            _componentPeaks.TryGetValue(entry.Name, out double worst);
            sb.Append(Csv(entry.Name)).Append(',');
            sb.Append(F(entry.Ms)).Append(',');
            sb.Append(F(entry.Ms / samples)).Append(',');
            sb.Append(F(worst)).Append(',');
            sb.Append(entry.Count).Append(',');
            sb.AppendLine(samples.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private void ClearFrameSamples()
    {
        _frameSamples.Clear();
        _componentTotals.Clear();
        _componentPeaks.Clear();
        _componentSamples = 0;
        _frameUiDirty = true;
    }

    // FRAM|cpuFrame|total|sync|pre|comp|change|hooks|end|late|renderCpu|renderGpu|frameSetup
    //     |drawsVis|drawsShadow|drawsCanvas|objVis|objShadow|gpuTex|gpuBuf|gpuTotal|hookList
    private void HandleFramePacket(string[] parts)
    {
        // EXACT count, not ">=". A stale debug console left running against a newer game parsed a longer
        // packet at its own older indices and produced a CSV full of confident nonsense - draw calls read
        // from a float field and came out zero, the hook list read from a number and came out empty, and
        // pipeline counts read from byte totals and came out in the billions. A minimum-length check
        // cannot detect a layout change, only a truncation. -xlinka
        if (parts.Length != FrameFieldCount)
        {
            if (!_warnedFrameLayout)
            {
                _warnedFrameLayout = true;
                AddLocalLog(LogLevel.ERROR,
                    $"Frame packet has {parts.Length} fields, this console expects {FrameFieldCount}. "
                    + "The game and the debug console are different builds - close this window and let the "
                    + "game respawn it. Ignoring frame packets until then.");
                _logsDirty = true;
            }
            return;
        }

        var sample = new FrameSample
        {
            WallClockSeconds = Time.GetTicksMsec() / 1000.0,
            CpuFrameMs = ParseD(parts[1]),
            WorldTotalMs = ParseD(parts[2]),
            SyncMs = ParseD(parts[3]),
            PreMs = ParseD(parts[4]),
            CompMs = ParseD(parts[5]),
            ChangeMs = ParseD(parts[6]),
            HooksMs = ParseD(parts[7]),
            EndMs = ParseD(parts[8]),
            LateMs = ParseD(parts[9]),
            RenderCpuMs = ParseD(parts[10]),
            RenderGpuMs = ParseD(parts[11]),
            FrameSetupCpuMs = ParseD(parts[12]),
            EngineInputMs = ParseD(parts[13]),
            EngineCoroutinesMs = ParseD(parts[14]),
            EngineFixedMs = ParseD(parts[15]),
            EngineAssetsMs = ParseD(parts[16]),
            HostNetworkMs = ParseD(parts[17]),
            HostEngineMs = ParseD(parts[18]),
            HostTailMs = ParseD(parts[19]),
            DrawsVisible = ParseL(parts[20]),
            DrawsShadow = ParseL(parts[21]),
            DrawsCanvas = ParseL(parts[22]),
            ObjectsVisible = ParseL(parts[23]),
            ObjectsShadow = ParseL(parts[24]),
            GpuTextureBytes = ParseL(parts[25]),
            GpuBufferBytes = ParseL(parts[26]),
            GpuTotalBytes = ParseL(parts[27]),
            OrphanNodes = ParseL(parts[28]),
            StaticMemBytes = ParseL(parts[29]),
            PhysicsActiveObjects = ParseL(parts[30]),
            PhysicsCollisionPairs = ParseL(parts[31]),
            PhysicsIslands = ParseL(parts[32]),
            PipeCanvas = ParseL(parts[33]),
            PipeMesh = ParseL(parts[34]),
            PipeSurface = ParseL(parts[35]),
            PipeDraw = ParseL(parts[36]),
            PipeSpecialization = ParseL(parts[37]),
            DriverAllocationCount = ParseL(parts[38]),
            DriverTotalMemBytes = ParseL(parts[39]),
            PerfReport = parts[42],
        };

        foreach (var row in parts[41].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = row.Split('~');
            if (f.Length < 4)
                continue;
            sample.Viewports.Add(new ViewportCost
            {
                Name = f[0],
                Draws = ParseL(f[1]),
                GpuMs = ParseD(f[2]),
                CpuMs = ParseD(f[3]),
            });
        }

        foreach (var entry in parts[40].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = entry.Split(':');
            if (fields.Length != 3)
                continue;
            sample.Hooks.Add(new HookCost
            {
                Name = fields[0],
                Ms = ParseD(fields[1]),
                Count = TryInt(fields[2], out int c) ? c : 0,
            });
        }

        _framePacketCount++;
        _latestFrame = sample;

        if (_frameRecording)
        {
            _frameSamples.Add(sample);
            if (_frameSamples.Count > MaxFrameSamples)
            {
                _frameSamples.RemoveRange(0, _frameSamples.Count - MaxFrameSamples);
            }
        }

        _frameUiDirty = true;
    }

    private void RefreshFrameUi()
    {
        if (!_frameUiDirty)
        {
            return;
        }
        _frameUiDirty = false;

        var sample = _latestFrame;
        if (sample == null)
        {
            SetLabel(_frameCaptureStatus, "Waiting for frame packets...");
            return;
        }

        SetLabel(_frameCaptureStatus,
            $"{_framePacketCount} frame packets   |   {_frameSamples.Count} samples held"
            + (_frameRecording ? " (recording)" : " (paused)")
            + (_lastExportDirectory.Length > 0 ? $"   |   last export: {_lastExportDirectory}" : ""));

        SetLabel(_frameRenderCpuValue, Ms(sample.RenderCpuMs));
        SetLabel(_frameRenderGpuValue, sample.RenderGpuMs > 0.0 ? Ms(sample.RenderGpuMs) : "n/a");
        SetLabel(_frameSetupValue, Ms(sample.FrameSetupCpuMs));
        SetLabel(_frameCpuValue, Ms(sample.CpuFrameMs));
        SetLabel(_frameDrawsVisibleValue, sample.DrawsVisible.ToString("N0", CultureInfo.InvariantCulture));
        SetLabel(_frameDrawsShadowValue, sample.DrawsShadow.ToString("N0", CultureInfo.InvariantCulture));
        SetLabel(_frameDrawsCanvasValue, sample.DrawsCanvas.ToString("N0", CultureInfo.InvariantCulture));
        SetLabel(_frameObjectsVisibleValue, sample.ObjectsVisible.ToString("N0", CultureInfo.InvariantCulture));
        SetLabel(_frameObjectsShadowValue, sample.ObjectsShadow.ToString("N0", CultureInfo.InvariantCulture));
        SetLabel(_frameGpuMemValue,
            $"{FormatBytes(sample.GpuTextureBytes)} / {FormatBytes(sample.GpuBufferBytes)} / {FormatBytes(sample.GpuTotalBytes)}");

        UpdateShadowHint(sample);
        UpdateStutterHint(sample);
        RebuildPhaseTree(sample);
        RebuildHookTree(sample);
        RebuildViewportTree(sample);
    }

    // Pipeline compilations are the one number that explains a hitch nothing else in the profile
    // accounts for: the driver stopped to build a shader. On a warm frame this is zero, so anything
    // above it during play is worth saying out loud rather than burying in a grid. -xlinka
    private void UpdateStutterHint(FrameSample sample)
    {
        if (_frameStutterHint == null)
        {
            return;
        }

        if (sample.PipelineTotal > 0)
        {
            SetLabel(_frameStutterHint,
                $"Shader pipelines compiled this frame: {sample.PipelineTotal} "
                + $"(canvas {sample.PipeCanvas}, mesh {sample.PipeMesh}, surface {sample.PipeSurface}, "
                + $"draw {sample.PipeDraw}, specialization {sample.PipeSpecialization}). "
                + "That is a compile stall this sample, not slow content.",
                new Color(1f, 0.6f, 0.35f));
        }
        else
        {
            string orphans = sample.OrphanNodes > 0 ? $"   |   orphan nodes: {sample.OrphanNodes}" : "";
            SetLabel(_frameStutterHint,
                $"No shader compiles this frame.   |   physics: {sample.PhysicsActiveObjects} active, "
                + $"{sample.PhysicsCollisionPairs} pairs, {sample.PhysicsIslands} islands{orphans}",
                new Color(0.55f, 0.55f, 0.62f));
        }
    }

    private void RebuildViewportTree(FrameSample sample)
    {
        if (_frameViewportTree == null)
        {
            return;
        }

        _frameViewportTree.Clear();
        var root = _frameViewportTree.CreateItem();

        if (sample.Viewports.Count == 0)
        {
            var empty = _frameViewportTree.CreateItem(root);
            empty.SetText(0, "No viewports reported.");
            return;
        }

        sample.Viewports.Sort((a, b) => b.GpuMs.CompareTo(a.GpuMs));
        foreach (var viewport in sample.Viewports)
        {
            var item = _frameViewportTree.CreateItem(root);
            item.SetText(0, viewport.Name);
            item.SetText(1, viewport.GpuMs > 0.0 ? Ms(viewport.GpuMs) : "n/a");
            item.SetText(2, viewport.Draws.ToString("N0", CultureInfo.InvariantCulture));
        }

        if (sample.PerfReport.Length > 0)
        {
            var report = _frameViewportTree.CreateItem(root);
            report.SetText(0, "driver: " + sample.PerfReport);
            report.SetCustomColor(0, new Color(0.55f, 0.55f, 0.62f));
        }
        if (sample.DriverTotalMemBytes > 0)
        {
            var driver = _frameViewportTree.CreateItem(root);
            driver.SetText(0, $"driver allocations: {sample.DriverAllocationCount:N0}");
            driver.SetText(1, FormatBytes(sample.DriverTotalMemBytes));
            driver.SetCustomColor(0, new Color(0.55f, 0.55f, 0.62f));
        }
    }

    // The one piece of interpretation on the tab, and only because the number is easy to read past.
    // Shadow draw calls are a whole second pass over the geometry, so a scene where they dominate is
    // paying for shadows rather than for anything the player can see.
    private void UpdateShadowHint(FrameSample sample)
    {
        long total = sample.DrawsVisible + sample.DrawsShadow + sample.DrawsCanvas;
        if (_frameShadowHint == null || total <= 0)
        {
            SetLabel(_frameShadowHint, "");
            return;
        }

        double shadowShare = sample.DrawsShadow * 100.0 / total;
        if (shadowShare >= 40.0)
        {
            SetLabel(_frameShadowHint,
                $"Shadows are {shadowShare:F0}% of all draw calls this frame. Cutting shadow distance, "
                + "splits, or turning shadows off on lights that do not need them is the biggest single lever here.",
                new Color(1f, 0.6f, 0.35f));
        }
        else
        {
            SetLabel(_frameShadowHint, $"Shadow pass is {shadowShare:F0}% of draw calls.",
                new Color(0.55f, 0.55f, 0.62f));
        }
    }

    private void RebuildPhaseTree(FrameSample sample)
    {
        if (_framePhaseTree == null)
        {
            return;
        }

        _framePhaseTree.Clear();
        var root = _framePhaseTree.CreateItem();

        // Denominator is the world total, not the process delta: these seven are the parts OF the world
        // update, and dividing them by the whole frame would quietly hide how much of the update one of
        // them owns whenever the renderer is the thing that is slow.
        double denominator = sample.WorldTotalMs > 0.0001 ? sample.WorldTotalMs : 1.0;

        void Row(string name, double ms, bool emphasise = false)
        {
            var item = _framePhaseTree.CreateItem(root);
            item.SetText(0, name);
            item.SetText(1, Ms(ms));
            item.SetText(2, $"{ms * 100.0 / denominator:F1}%");
            if (emphasise)
            {
                var colour = new Color(1f, 0.75f, 0.4f);
                item.SetCustomColor(0, colour);
                item.SetCustomColor(1, colour);
                item.SetCustomColor(2, colour);
            }
        }

        double worst = System.Math.Max(sample.SyncMs, System.Math.Max(sample.PreMs,
            System.Math.Max(sample.CompMs, System.Math.Max(sample.ChangeMs,
            System.Math.Max(sample.HooksMs, System.Math.Max(sample.EndMs, sample.LateMs))))));

        // Named for what is ACTUALLY in it. Every IInputUpdateReceiver - particles, cloth, dynamic bones,
        // IK - is queued by the input pass and drains here, so a scene heavy on simulation shows up in
        // this row and reads as a networking cost unless the label says otherwise. The Profiler tab's
        // component list breaks it down by type, suffixed "(input)".
        Row("sync (queued input work: particles, cloth, bones, IK + transport)", sample.SyncMs, sample.SyncMs >= worst);
        Row("pre (assets, events, input, coroutines)", sample.PreMs, sample.PreMs >= worst);
        Row("comp (component OnUpdate)", sample.CompMs, sample.CompMs >= worst);
        Row("change (OnChanges drain)", sample.ChangeMs, sample.ChangeMs >= worst);
        Row("hooks (engine flush, budgeted)", sample.HooksMs, sample.HooksMs >= worst);
        Row("end (destructions, trash)", sample.EndMs, sample.EndMs >= worst);
        Row("late (late updates + 2nd hook flush)", sample.LateMs, sample.LateMs >= worst);

        var engine = _framePhaseTree.CreateItem(root);
        engine.SetText(0, "-- outside the world update --");
        engine.SetCustomColor(0, new Color(0.55f, 0.55f, 0.62f));

        void Flat(string name, double ms)
        {
            var item = _framePhaseTree.CreateItem(root);
            item.SetText(0, name);
            item.SetText(1, Ms(ms));
            item.SetText(2, sample.CpuFrameMs > 0.0001 ? $"{ms * 100.0 / sample.CpuFrameMs:F1}% of frame" : "-");
        }

        Flat("engine: input pass (poll + queue)", sample.EngineInputMs);
        Flat("engine: coroutines", sample.EngineCoroutinesMs);
        Flat("engine: fixed updates", sample.EngineFixedMs);
        Flat("engine: assets", sample.EngineAssetsMs);
        Flat("host: network pump", sample.HostNetworkMs);
        Flat("host: overlays + telemetry", sample.HostTailMs);
        Flat("render submit (CPU)", sample.RenderCpuMs);

        // Everything above is OURS and measured. What is left is Godot's own frame - scene tree
        // processing, servers, presentation - plus any vsync wait. If this row dominates, the cost is
        // not in engine code and no amount of profiling our own phases will find it.
        double named = sample.HostEngineMs + sample.HostNetworkMs + sample.HostTailMs + sample.RenderCpuMs;
        Flat("GODOT + vsync (not our code)", System.Math.Max(0.0, sample.CpuFrameMs - named));

        var totalItem = _framePhaseTree.CreateItem(root);
        totalItem.SetText(0, "world update total");
        totalItem.SetText(1, Ms(sample.WorldTotalMs));
        totalItem.SetText(2, sample.CpuFrameMs > 0.0001
            ? $"{sample.WorldTotalMs * 100.0 / sample.CpuFrameMs:F1}% of frame"
            : "-");
    }

    private void RebuildHookTree(FrameSample sample)
    {
        if (_frameHookTree == null)
        {
            return;
        }

        _frameHookTree.Clear();
        var root = _frameHookTree.CreateItem();

        if (sample.Hooks.Count == 0)
        {
            var empty = _frameHookTree.CreateItem(root);
            empty.SetText(0, "No hook cost recorded this frame.");
            return;
        }

        sample.Hooks.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        foreach (var hook in sample.Hooks)
        {
            var item = _frameHookTree.CreateItem(root);
            item.SetText(0, hook.Name);
            item.SetText(1, Ms(hook.Ms));
            item.SetText(2, hook.Count.ToString(CultureInfo.InvariantCulture));
        }
    }

    // Export

    // Two files, because they answer two different questions. The timeline is one row per sample and is
    // what you scrub to find the stutter; the hook table is the whole capture folded down and is what
    // says which type to go and look at. Both are plain CSV so they open in anything. -xlinka
    private void ExportFrameCsv()
    {
        if (_frameSamples.Count == 0)
        {
            SetLabel(_frameCaptureStatus, "Nothing captured yet - turn Record on and reproduce the problem first.",
                new Color(1f, 0.6f, 0.35f));
            return;
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string directory = ProjectSettings.GlobalizePath("user://profiles");

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            string framesPath = System.IO.Path.Combine(directory, $"lumora-frames-{stamp}.csv");
            string hooksPath = System.IO.Path.Combine(directory, $"lumora-hooks-{stamp}.csv");
            string componentsPath = System.IO.Path.Combine(directory, $"lumora-components-{stamp}.csv");

            System.IO.File.WriteAllText(framesPath, BuildFrameCsv(), Encoding.UTF8);
            System.IO.File.WriteAllText(hooksPath, BuildHookCsv(), Encoding.UTF8);
            System.IO.File.WriteAllText(componentsPath, BuildComponentCsv(), Encoding.UTF8);

            _lastExportDirectory = directory;
            SetLabel(_frameCaptureStatus,
                $"Exported {_frameSamples.Count} frame samples and {_componentTotals.Count} component types to {directory}",
                new Color(0.5f, 0.9f, 0.55f));
            AddLocalLog(LogLevel.LOG, $"Profile exported: {framesPath}");
        }
        catch (Exception ex)
        {
            SetLabel(_frameCaptureStatus, $"Export failed: {ex.Message}", new Color(1f, 0.45f, 0.45f));
            AddLocalLog(LogLevel.ERROR, $"Profile export failed: {ex}");
        }
    }

    private string BuildFrameCsv()
    {
        var sb = new StringBuilder(_frameSamples.Count * 160);
        sb.AppendLine("sample,time_s,process_delta_ms,world_total_ms,sync_ms,pre_ms,comp_ms,change_ms,"
            + "hooks_ms,end_ms,late_ms,render_cpu_ms,render_gpu_ms,frame_setup_cpu_ms,"
            + "engine_input_ms,engine_coroutines_ms,engine_fixed_ms,engine_assets_ms,"
            + "host_network_ms,host_engine_ms,host_tail_ms,godot_and_vsync_ms,"
            + "draws_visible,draws_shadow,draws_canvas,objects_visible,objects_shadow,"
            + "gpu_texture_bytes,gpu_buffer_bytes,gpu_total_bytes,"
            + "orphan_nodes,static_mem_bytes,physics_active,physics_pairs,physics_islands,"
            + "pipeline_compilations,driver_allocations,driver_total_bytes,"
            + "viewport_count,busiest_viewport,busiest_viewport_gpu_ms,top_hook,top_hook_ms");

        double start = _frameSamples[0].WallClockSeconds;
        for (int i = 0; i < _frameSamples.Count; i++)
        {
            var s = _frameSamples[i];
            HookCost? top = null;
            foreach (var hook in s.Hooks)
            {
                if (top == null || hook.Ms > top.Ms)
                    top = hook;
            }

            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(F(s.WallClockSeconds - start)).Append(',');
            sb.Append(F(s.CpuFrameMs)).Append(',');
            sb.Append(F(s.WorldTotalMs)).Append(',');
            sb.Append(F(s.SyncMs)).Append(',');
            sb.Append(F(s.PreMs)).Append(',');
            sb.Append(F(s.CompMs)).Append(',');
            sb.Append(F(s.ChangeMs)).Append(',');
            sb.Append(F(s.HooksMs)).Append(',');
            sb.Append(F(s.EndMs)).Append(',');
            sb.Append(F(s.LateMs)).Append(',');
            sb.Append(F(s.RenderCpuMs)).Append(',');
            sb.Append(F(s.RenderGpuMs)).Append(',');
            sb.Append(F(s.FrameSetupCpuMs)).Append(',');
            sb.Append(F(s.EngineInputMs)).Append(',');
            sb.Append(F(s.EngineCoroutinesMs)).Append(',');
            sb.Append(F(s.EngineFixedMs)).Append(',');
            sb.Append(F(s.EngineAssetsMs)).Append(',');
            sb.Append(F(s.HostNetworkMs)).Append(',');
            sb.Append(F(s.HostEngineMs)).Append(',');
            sb.Append(F(s.HostTailMs)).Append(',');
            sb.Append(F(System.Math.Max(0.0, s.CpuFrameMs - (s.HostEngineMs + s.HostNetworkMs
                + s.HostTailMs + s.RenderCpuMs)))).Append(',');
            sb.Append(s.DrawsVisible).Append(',');
            sb.Append(s.DrawsShadow).Append(',');
            sb.Append(s.DrawsCanvas).Append(',');
            sb.Append(s.ObjectsVisible).Append(',');
            sb.Append(s.ObjectsShadow).Append(',');
            sb.Append(s.GpuTextureBytes).Append(',');
            sb.Append(s.GpuBufferBytes).Append(',');
            sb.Append(s.GpuTotalBytes).Append(',');
            sb.Append(s.OrphanNodes).Append(',');
            sb.Append(s.StaticMemBytes).Append(',');
            sb.Append(s.PhysicsActiveObjects).Append(',');
            sb.Append(s.PhysicsCollisionPairs).Append(',');
            sb.Append(s.PhysicsIslands).Append(',');
            sb.Append(s.PipelineTotal).Append(',');
            sb.Append(s.DriverAllocationCount).Append(',');
            sb.Append(s.DriverTotalMemBytes).Append(',');

            ViewportCost? busiest = null;
            foreach (var viewport in s.Viewports)
            {
                if (busiest == null || viewport.GpuMs > busiest.GpuMs)
                    busiest = viewport;
            }
            sb.Append(s.Viewports.Count).Append(',');
            sb.Append(Csv(busiest?.Name ?? "")).Append(',');
            sb.Append(F(busiest?.GpuMs ?? 0.0)).Append(',');
            sb.Append(Csv(top?.Name ?? "")).Append(',');
            sb.AppendLine(F(top?.Ms ?? 0.0));
        }
        return sb.ToString();
    }

    private string BuildHookCsv()
    {
        var totals = new Dictionary<string, HookCost>(StringComparer.Ordinal);
        var peak = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var sample in _frameSamples)
        {
            foreach (var hook in sample.Hooks)
            {
                if (!totals.TryGetValue(hook.Name, out var acc))
                {
                    acc = new HookCost { Name = hook.Name };
                    totals[hook.Name] = acc;
                }
                acc.Ms += hook.Ms;
                acc.Count += hook.Count;

                peak.TryGetValue(hook.Name, out double worst);
                if (hook.Ms > worst)
                    peak[hook.Name] = hook.Ms;
            }
        }

        var ordered = new List<HookCost>(totals.Values);
        ordered.Sort((a, b) => b.Ms.CompareTo(a.Ms));

        var sb = new StringBuilder(ordered.Count * 80);
        sb.AppendLine("hook_type,total_ms,mean_ms_per_sample,peak_ms_in_one_sample,total_calls,samples_seen");
        int samples = System.Math.Max(1, _frameSamples.Count);
        foreach (var hook in ordered)
        {
            peak.TryGetValue(hook.Name, out double worst);
            sb.Append(Csv(hook.Name)).Append(',');
            sb.Append(F(hook.Ms)).Append(',');
            sb.Append(F(hook.Ms / samples)).Append(',');
            sb.Append(F(worst)).Append(',');
            sb.Append(hook.Count).Append(',');
            sb.AppendLine(samples.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private void OpenExportFolder()
    {
        string directory = _lastExportDirectory.Length > 0
            ? _lastExportDirectory
            : ProjectSettings.GlobalizePath("user://profiles");
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            OS.ShellOpen(directory);
        }
        catch (Exception ex)
        {
            SetLabel(_frameCaptureStatus, $"Could not open {directory}: {ex.Message}",
                new Color(1f, 0.45f, 0.45f));
        }
    }

    // Helpers

    private static string Ms(double value) => value.ToString("F2", CultureInfo.InvariantCulture) + " ms";

    private static string F(double value) => value.ToString("F4", CultureInfo.InvariantCulture);

    // A hook type name cannot contain a comma or a quote today, but the CSV is going to be opened by
    // someone else's spreadsheet and a name is still text off the wire.
    private static string Csv(string value)
    {
        if (value.IndexOf(',') < 0 && value.IndexOf('"') < 0 && value.IndexOf('\n') < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static double ParseD(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0.0;

    private static long ParseL(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0L;
}
