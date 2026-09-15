// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Lumora.Core.Logging;
using Lumora.Core.Management;
using Lumora.Core.Helpers;
using Lumora.Core.Assets;
using Lumora.Core.Coroutines;
using Lumora.Core.Physics;
using Lumora.Core.Persistence;
using Lumora.Core.Templates;
using Lumora.Nexus;
using Lumora.Nexus.Cloud.Cdn;
using Lumora.Nexus.Diagnostics;
using Lumora.Nexus.Cloud;
using Lumora.Nexus.Transport;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

public enum EngineState
{
    NotInitialized,
    Initializing,
    Running,
    ShuttingDown,
    Disposed,
    Failed
}

public enum SubsystemStatus
{
    Pending,
    Initializing,
    Ready,
    Failed,
    Disposed
}

public static class EngineVersion
{
    public const int Major = 0;
    public const int Minor = 1;
    public const int Patch = 0;
    public const string Suffix = "alpha";
    public static readonly string VersionString = $"{Major}.{Minor}.{Patch}-{Suffix}";
    public static readonly DateTime BuildDate = new DateTime(2024, 1, 1); // Update with actual build
}

public class EngineMetrics
{
    private readonly Stopwatch _frameTimer = new Stopwatch();
    private readonly Queue<double> _frameTimes = new Queue<double>();
    private const int FrameTimeHistorySize = 120;

    public long TotalFrames { get; private set; }

    // Seconds.
    public double TotalTime { get; private set; }

    // Seconds.
    public double LastFrameTime { get; private set; }

    public double AverageFrameTime { get; private set; }

    public double CurrentFPS => LastFrameTime > 0 ? 1.0 / LastFrameTime : 0;

    public double AverageFPS => AverageFrameTime > 0 ? 1.0 / AverageFrameTime : 0;

    // Bytes.
    public long PeakMemoryUsage { get; private set; }

    public long CurrentMemoryUsage => GC.GetTotalMemory(false);

    public int GCCollections => GC.CollectionCount(0);

    internal void BeginFrame()
    {
        _frameTimer.Restart();
    }

    internal void EndFrame(double delta)
    {
        _frameTimer.Stop();
        TotalFrames++;
        TotalTime += delta;
        LastFrameTime = delta;

        _frameTimes.Enqueue(delta);
        while (_frameTimes.Count > FrameTimeHistorySize)
            _frameTimes.Dequeue();

        double sum = 0;
        foreach (var t in _frameTimes) sum += t;
        AverageFrameTime = sum / _frameTimes.Count;

        long currentMem = CurrentMemoryUsage;
        if (currentMem > PeakMemoryUsage)
            PeakMemoryUsage = currentMem;
    }

    public override string ToString()
    {
        return $"FPS: {CurrentFPS:F1} (avg {AverageFPS:F1}), Frames: {TotalFrames}, Memory: {CurrentMemoryUsage / 1024 / 1024}MB";
    }
}

public class Engine : IDisposable
{
    private static Engine _instance = null!;
    private static readonly object _instanceLock = new object();

    private EngineState _state = EngineState.NotInitialized;
    private readonly object _stateLock = new object();
    private bool _hostingLocalHome;
    private int? _localHomePort;
    private World _pendingUserSpaceSetup = null!;
    private readonly Dictionary<string, SubsystemStatus> _subsystemStatus = new Dictionary<string, SubsystemStatus>();
    private Exception _initializationError = null!;

    private readonly Stopwatch _engineTimer = new Stopwatch();
    private readonly EngineMetrics _metrics = new EngineMetrics();
    private double _fixedTimeAccumulator;
    private const double DefaultFixedTimestep = 1.0 / 60.0;

    public WorldManager WorldManager { get; private set; } = null!;
    public WorldLoadingService WorldLoadingService { get; private set; } = null!;
    public FocusManager FocusManager { get; private set; } = null!;
    public Input.InputInterface InputInterface { get; private set; } = null!;
    public AssetManager AssetManager { get; private set; } = null!;
    public GlobalCoroutineManager CoroutineManager { get; private set; } = null!;
    public EngineMixerManager AudioManager { get; private set; } = null!;

    public LumoraClient? CDNClient { get; private set; }
    public ContentCache? ContentCache { get; private set; }

    private Action<Lumora.Nexus.Cloud.Cdn.RepresentedGroupInfo?>? _representedGroupChanged;

    public LocalDB? LocalDB { get; set; }

    // Set automatically when a session is created or joined; cleared on dispose. AssetFetcher uses this to pull
    // remote local:// assets from peers.
    public Networking.Session.SessionAssetTransferer? ActiveSessionTransferer { get; set; }

    public string ResourceRoot { get; set; } = null!;

    public event Action<EngineState> OnStateChanged = null!;

    public event Action<double> OnPreUpdate = null!;

    public event Action<double> OnPostUpdate = null!;

    public event Action<string, SubsystemStatus> OnSubsystemStatusChanged = null!;

    #region Static Properties

    public static Engine Instance
    {
        get
        {
            if (_instance == null)
                throw new InvalidOperationException("Engine not initialized. Call Engine.InitializeAsync() first.");
            return _instance;
        }
    }

    public static Engine Current => _instance;

    public static bool IsInitialized => _instance?._state == EngineState.Running;

    public static bool HasInstance => _instance != null;

    public static bool ShowDebug { get; set; } = true;

    public static bool IsDedicatedServer { get; set; } = false;

    public static string Platform => Environment.OSVersion.Platform.ToString();

    // What this build is actually RUNNING on, set once at startup by the platform layer.
    //
    // Environment.OSVersion.Platform above cannot answer this: it reports Unix for Linux AND for
    // Android, which is precisely the distinction anything platform-gated needs. The core has no way to
    // ask the renderer, so the renderer tells it, the same way the texture cache is told which block
    // formats this device has. Defaults to the desktop case so a harness with no platform layer behaves
    // as it always did. -xlinka
    public static Lumora.Core.Platform CurrentPlatform { get; set; } = Lumora.Core.Platform.Windows;

    public static bool IsMobilePlatform => CurrentPlatform == Lumora.Core.Platform.Android;

    public static bool IsEditor { get; set; } = false;

    #endregion

    #region Instance Properties

    public EngineState State
    {
        get { lock (_stateLock) return _state; }
        private set
        {
            lock (_stateLock)
            {
                if (_state != value)
                {
                    _state = value;
                    OnStateChanged?.Invoke(value);
                }
            }
        }
    }

    public bool IsShuttingDown => State == EngineState.ShuttingDown;

    public bool IsRunning => State == EngineState.Running;

    // Seconds.
    public double TotalTime => _metrics.TotalTime;

    public long FrameCount => _metrics.TotalFrames;

    public EngineMetrics Metrics => _metrics;

    public double FixedTimestep { get; set; } = DefaultFixedTimestep;

    // Caps the fixed-step catch-up so a long frame can't spiral.
    public int MaxFixedUpdatesPerFrame { get; set; } = 8;

    public bool AutoHostLocalHome { get; set; } = true;

    public bool AutoConnectLocalHome { get; set; } = true;

    public Exception InitializationError => _initializationError;

    public IReadOnlyDictionary<string, SubsystemStatus> SubsystemStatuses => _subsystemStatus;

    #endregion

    #region Initialization

    public Engine()
    {
        lock (_instanceLock)
        {
            if (_instance != null)
                throw new InvalidOperationException("Engine instance already exists. Call Dispose() first.");
            _instance = this;
        }
    }

    // LumoraNexus sits below the engine and cannot see the logger or the version constant, so push both
    // down before anything in it runs. Without this, transport and cloud lines go to the console only and
    // the session directory advertises a placeholder version. -xlinka
    private static void WireNexus()
    {
        NexusRuntime.AppVersion = EngineVersion.VersionString;
        NexusLog.EnableDebug = LumoraLogger.EnableDebug;
        NexusLog.Sink = (level, message) =>
        {
            switch (level)
            {
                case NexusLogLevel.Warn: LumoraLogger.Warn(message); break;
                case NexusLogLevel.Error: LumoraLogger.Error(message); break;
                case NexusLogLevel.Debug: LumoraLogger.Debug(message); break;
                default: LumoraLogger.Log(message); break;
            }
        };
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (State != EngineState.NotInitialized)
        {
            LumoraLogger.Warn($"Engine already in state {State}, skipping initialization.");
            return;
        }

        State = EngineState.Initializing;
        _engineTimer.Start();

        WireNexus();

        LumoraLogger.Log("=====================================");
        LumoraLogger.Log($"Lumora Engine v{EngineVersion.VersionString}");
        LumoraLogger.Log($"Platform: {Platform}");
        LumoraLogger.Log("=====================================");
        LumoraLogger.Log("Engine Initialization Starting");
        LumoraLogger.Log("=====================================");

        try
        {
            LumoraLogger.Log("Initializing core systems...");

            await InitializeSubsystem("FocusManager", async () =>
            {
                FocusManager = new FocusManager();
                await Task.CompletedTask;
            }, cancellationToken);

            await InitializeSubsystem("InputInterface", async () =>
            {
                InputInterface = new Input.InputInterface();
                await InputInterface.InitializeAsync();
            }, cancellationToken);

            await InitializeSubsystem("CoroutineManager", async () =>
            {
                CoroutineManager = new GlobalCoroutineManager();
                await Task.CompletedTask;
            }, cancellationToken);

            LumoraLogger.Log("Initializing asset systems...");

            await InitializeSubsystem("AssetManager", async () =>
            {
                AssetManager = new AssetManager(this);
                await AssetManager.InitializeAsync();
            }, cancellationToken);

            await InitializeSubsystem("ContentCache", async () =>
            {
                var deviceId = Environment.MachineName;
                CDNClient = new LumoraClient(deviceId, "LumoraVR", EngineVersion.VersionString);
                var cachePath = Path.Combine(Lumora.Core.Persistence.PathResolver.CachePath, "LumoraVR", "Cache");
                ContentCache = new ContentCache(CDNClient, cachePath);
                await Task.CompletedTask;
            }, cancellationToken);

            AudioManager = new();
            foreach (string name in new string[] { "Music", "Effects", "Voice" })
            {
                if (AudioManager.Mixer.TryCreateAudioBus(name, out var bus) && AudioManager.Mixer.TryGetAudioBusByName("Master", out var master))
                    bus.Target = master;
            }
            if(AudioManager.Mixer.TryGetAudioBusByName("Voice",out var voicebus)){
                voicebus.Mute = true;
            }


            // Physics is per-world and delegated to the platform engine (Godot/Jolt), accessed
            // through World.Physics - there is no engine-level physics subsystem to initialize.

            LumoraLogger.Log("Initializing world management...");

            await InitializeSubsystem("WorldManager", async () =>
            {
                WorldManager = new WorldManager();
                await WorldManager.InitializeAsync(this);
                WorldLoadingService = new WorldLoadingService(this);
            }, cancellationToken);

            // Wearing a different group has to show up on the nametag in every world this machine is in,
            // not just the one that happened to be focused when the button was pressed. Hooked here rather
            // than in the cloud client because the client knows nothing about worlds. -xlinka
            if (CDNClient != null)
            {
                _representedGroupChanged = OnRepresentedGroupChanged;
                CDNClient.RepresentedGroupChanged += _representedGroupChanged;
            }

            LumoraLogger.Log("Post-initialization setup...");
            ProcessStartupArguments();

            State = EngineState.Running;

            var initTime = _engineTimer.Elapsed.TotalSeconds;
            LumoraLogger.Log("=====================================");
            LumoraLogger.Log($"Engine Initialized Successfully in {initTime:F2}s");
            LumoraLogger.Log($"Subsystems: {_subsystemStatus.Count} initialized");
            LumoraLogger.Log("=====================================");
        }
        catch (OperationCanceledException)
        {
            LumoraLogger.Warn("Engine initialization was cancelled.");
            State = EngineState.Failed;
            _initializationError = new OperationCanceledException("Initialization cancelled");
            throw;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"Engine initialization failed: {ex.Message}");
            LumoraLogger.Error(ex.StackTrace ?? ex.Message);
            State = EngineState.Failed;
            _initializationError = ex;
            lock (_instanceLock) { _instance = null!; }
            throw;
        }
    }

    private async Task InitializeSubsystem(string name, Func<Task> initializer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        SetSubsystemStatus(name, SubsystemStatus.Initializing);

        try
        {
            var sw = Stopwatch.StartNew();
            await initializer();
            sw.Stop();
            SetSubsystemStatus(name, SubsystemStatus.Ready);
            LumoraLogger.Log($"  [{sw.ElapsedMilliseconds}ms] {name} initialized");
        }
        catch (Exception ex)
        {
            SetSubsystemStatus(name, SubsystemStatus.Failed);
            LumoraLogger.Error($"  [FAILED] {name}: {ex.Message}");
            throw;
        }
    }

    private void SetSubsystemStatus(string name, SubsystemStatus status)
    {
        _subsystemStatus[name] = status;
        OnSubsystemStatusChanged?.Invoke(name, status);
    }

    private void ProcessStartupArguments()
    {
        bool skipLocalHome = false;

        if (!skipLocalHome && AutoHostLocalHome)
        {
            StartLocalHome();

            if (AutoConnectLocalHome)
            {
                SwitchToLocalHome();
            }
        }
    }

    #endregion

    #region Local Home Management

    public static string LocalHomeSavePath => Path.Combine(
        Lumora.Core.Persistence.PathResolver.RoamingPath,
        "LumoraVR", "home.lworld");

    private void StartLocalHome()
    {
        if (_hostingLocalHome)
        {
            SwitchToLocalHome();
            return;
        }

        if (_localHomePort is null)
        {
            _localHomePort = SimpleIpHelpers.GetAvailablePortUdp(10) ?? 6000;
        }

        // Load the saved home if one exists (build it into a blank world so the template's default
        // content isn't duplicated); otherwise build the default from the LocalHome template. The save's
        // read, decrypt, decompress and parse run on a task so startup doesn't freeze on them; only the
        // tree integration happens on the world thread, once the parse lands. -xlinka
        var savePath = LocalHomeSavePath;
        var world = File.Exists(savePath)
            ? WorldManager?.StartSessionDeferred(
                "LocalHome", (ushort)_localHomePort.Value, GetHostUserName(), "",
                SessionVisibility.Private, 16,
                () => WorldStorage.ReadTree(savePath),
                (w, tree) =>
                {
                    if (!WorldStorage.IntegrateTree(w, tree, savePath))
                    {
                        LumoraLogger.Warn("Engine: LocalHome save failed to load; falling back to template.");
                        WorldTemplates.ApplyTemplate(w, "LocalHome");
                    }
                })
            : WorldManager?.StartSession("LocalHome", (ushort)_localHomePort.Value, GetHostUserName(), "LocalHome", null!);
        if (world == null)
        {
            LumoraLogger.Error("Engine: Failed to start LocalHome session.");
            return;
        }

        _hostingLocalHome = true;
        LumoraLogger.Log($"Engine: LocalHome hosted on port {_localHomePort.Value}.");

        _pendingUserSpaceSetup = world;
    }

    private void SwitchToLocalHome()
    {
        var world = WorldManager?.GetWorldByName("LocalHome");
        if (world != null)
        {
            WorldManager!.SwitchToWorld(world);
            LumoraLogger.Log("Engine: Switched to LocalHome world.");
        }
        else
        {
            LumoraLogger.Warn("Engine: LocalHome world not found.");
        }
    }

    private string GetHostUserName()
    {
        return Environment.MachineName;
    }

    // Fires off the cloud client's thread, so every write goes through the owning world's own queue. Only
    // the LOCAL user of each world gets touched: everyone else's card arrives as a delta from the peer that
    // owns that account. -xlinka
    private void OnRepresentedGroupChanged(Lumora.Nexus.Cloud.Cdn.RepresentedGroupInfo? group)
    {
        var manager = WorldManager;
        if (manager == null)
            return;

        var worlds = manager.Worlds;
        for (int i = 0; i < worlds.Count; i++)
        {
            var world = worlds[i];
            if (world == null || world.IsDestroyed)
                continue;
            world.RunSynchronously(() =>
            {
                var user = world.LocalUser;
                if (user != null && !user.IsDestroyed)
                    user.ApplyRepresentedGroup(group);
            });
        }
    }

    public World GetPendingUserSpaceSetup() => _pendingUserSpaceSetup;

    public void ClearPendingUserSpaceSetup() => _pendingUserSpaceSetup = null!;

    public void JoinLocalHome() => SwitchToLocalHome();

    public void JoinServer(string address, int port, string worldName = "RemoteWorld")
    {
        WorldManager?.JoinSession(worldName, address, (ushort)port);
    }

    // Join via NAT punch-through: punch a hole to the session host through the relay
    // server, then connect directly to the resolved endpoint.
    public void JoinNatServer(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            LumoraLogger.Warn("Engine: NAT join called with an empty session identifier.");
            return;
        }

        var addr = Networking.Session.Session.SessionServerAddress;
        var port = Networking.Session.Session.SessionServerPort;
        LumoraLogger.Log($"Engine: NAT punch join for session '{identifier}' via {addr}:{port}");

        var client = new SessionServerClient(addr, port);
        var joined = false;

        client.OnNATPunchSuccess += ep =>
        {
            if (joined) return;
            joined = true;

            LumoraLogger.Log($"Engine: NAT punch succeeded -> {ep}; connecting directly.");
            var wm = WorldManager;

            // The punch callback runs on the client's poll task; hop to the main
            // update thread to create/join the world.
            if (wm?.FocusedWorld != null)
                wm.FocusedWorld.RunSynchronously(() => wm.JoinSession("RemoteWorld", ep.Address.ToString(), (ushort)ep.Port));
            else
                wm?.JoinSession("RemoteWorld", ep.Address.ToString(), (ushort)ep.Port);

            // Hold the punch socket open briefly so the NAT mapping stays warm while
            // the direct connection establishes, then release it. (Hard NATs that
            // can't reuse the hole for a fresh socket fall back to relay.)
            _ = Task.Delay(5000).ContinueWith(_ => client.Dispose(), TaskScheduler.Default);
        };

        _ = client.RequestNATPunchAsync(identifier).ContinueWith(t =>
        {
            if (t.IsFaulted)
                LumoraLogger.Warn($"Engine: NAT punch request failed: {t.Exception?.GetBaseException().Message}");
        }, TaskScheduler.Default);
    }

    // Join via relay server: tunnel the session through the relay when a direct or
    // punched path isn't available.
    public void JoinNatServerRelay(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            LumoraLogger.Warn("Engine: Relay join called with an empty session identifier.");
            return;
        }

        // Make sure the relay transport is available (Register dedups by type).
        NetworkManagerRegistry.Register(new Networking.RelayNetworkManager());

        var addr = Networking.Session.Session.SessionServerAddress;
        var port = Networking.Session.Session.SessionServerPort;
        LumoraLogger.Log($"Engine: relay join for session '{identifier}' via {addr}:{port}");

        var relayUri = new Uri($"{Networking.RelayNetworkManager.SCHEME}://relay/{Uri.EscapeDataString(identifier)}");
        WorldManager?.JoinSession("RemoteWorld", relayUri);
    }

    #endregion

    #region Update Loop

    public void Update(double delta)
    {
        if (State != EngineState.Running)
            return;

        _metrics.BeginFrame();
        OnPreUpdate?.Invoke(delta);

        try
        {
            // Engine-level phase timing. The first capture showed 31% of the frame - 9 ms of a 30 ms
            // frame - sitting in NO phase at all, because everything the world profile measures happens
            // inside WorldManager.Update and this method does four other things around it. An unmeasured
            // third of the frame is not a profiler. -xlinka
            long _ts = System.Diagnostics.Stopwatch.GetTimestamp();
            double _mspt = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double Lap() { long n = System.Diagnostics.Stopwatch.GetTimestamp(); double ms = (n - _ts) * _mspt; _ts = n; return ms; }

            InputInterface?.ProcessInput(delta);
            double msInput = Lap();

            CoroutineManager?.Update((float)delta);
            double msCoroutines = Lap();

            WorldManager?.Update(delta);
            double msWorlds = Lap();

            ProcessFixedUpdates(delta);
            InputInterface?.SyncTrackingSpaceToFocusedLocalUser();
            double msFixed = Lap();

            AssetManager?.Update((float)delta);
            double msAssets = Lap();

            LastEnginePhases = new EnginePhaseProfile(msInput, msCoroutines, msWorlds, msFixed, msAssets);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"Engine Update error: {ex.Message}");
            if (ShowDebug) LumoraLogger.Error(ex.StackTrace ?? ex.Message);
        }

        OnPostUpdate?.Invoke(delta);
        _metrics.EndFrame(delta);
    }

    // What Engine.Update spends its time on, outside any single world. Published every frame.
    //
    // InputMs is the input pass itself (driver polling, action evaluation, and QUEUEING the receiver
    // callbacks) - the receivers' actual execution is charged to the world's sync phase, because
    // RunSynchronously defers them there.
    public readonly struct EnginePhaseProfile
    {
        public readonly double InputMs;
        public readonly double CoroutinesMs;
        public readonly double WorldsMs;
        public readonly double FixedMs;
        public readonly double AssetsMs;

        public EnginePhaseProfile(double input, double coroutines, double worlds, double fixedUpdates, double assets)
        {
            InputMs = input;
            CoroutinesMs = coroutines;
            WorldsMs = worlds;
            FixedMs = fixedUpdates;
            AssetsMs = assets;
        }

        public double TotalMs => InputMs + CoroutinesMs + WorldsMs + FixedMs + AssetsMs;
    }

    public EnginePhaseProfile LastEnginePhases { get; private set; }

    private void ProcessFixedUpdates(double delta)
    {
        _fixedTimeAccumulator += delta;

        int fixedUpdates = 0;
        while (_fixedTimeAccumulator >= FixedTimestep && fixedUpdates < MaxFixedUpdatesPerFrame)
        {
            FixedUpdateInternal(FixedTimestep);
            _fixedTimeAccumulator -= FixedTimestep;
            fixedUpdates++;
        }

        // Prevent spiral of death
        if (_fixedTimeAccumulator > FixedTimestep * MaxFixedUpdatesPerFrame)
        {
            _fixedTimeAccumulator = 0;
        }
    }

    private void FixedUpdateInternal(double fixedDelta)
    {
        WorldManager?.FixedUpdate(fixedDelta);
    }

    // Called automatically by Update; can also be driven manually for a custom fixed step.
    public void FixedUpdate(double fixedDelta)
    {
        if (State != EngineState.Running)
            return;

        FixedUpdateInternal(fixedDelta);
    }

    public void LateUpdate(double delta)
    {
        if (State != EngineState.Running)
            return;

        WorldManager?.LateUpdate(delta);
    }

    #endregion

    #region Shutdown

    public void RequestShutdown()
    {
        if (State != EngineState.Running)
            return;

        LumoraLogger.Log("Engine: Shutdown requested.");
        Dispose();
    }

    // Raised by RequestQuit. The platform layer subscribes to close the app
    // (which tears the engine down through the normal exit path).
    public event Action? QuitRequested;

    // Request the application quit. Fires QuitRequested rather than disposing
    // inline, so it's safe to call from UI during a world update.
    public void RequestQuit() => QuitRequested?.Invoke();

    public void Dispose()
    {
        if (State == EngineState.Disposed || State == EngineState.NotInitialized)
            return;

        State = EngineState.ShuttingDown;

        LumoraLogger.Log("=====================================");
        LumoraLogger.Log("Engine Shutdown Starting...");
        LumoraLogger.Log("=====================================");

        if (CDNClient != null && _representedGroupChanged != null)
        {
            CDNClient.RepresentedGroupChanged -= _representedGroupChanged;
            _representedGroupChanged = null;
        }

        // Dispose in reverse initialization order
        DisposeSubsystem("WorldManager", () => { WorldManager?.Dispose(); WorldManager = null!; });
        DisposeSubsystem("AudioManager", () => { AudioManager.Dispose(); });
        DisposeSubsystem("ContentCache", () => { ContentCache?.Dispose(); ContentCache = null; CDNClient?.Dispose(); CDNClient = null; });
        DisposeSubsystem("AssetManager", () => { AssetManager?.Dispose(); AssetManager = null!; });
        DisposeSubsystem("CoroutineManager", () => { CoroutineManager?.Dispose(); CoroutineManager = null!; });
        DisposeSubsystem("InputInterface", () => { InputInterface?.Dispose(); InputInterface = null!; });
        DisposeSubsystem("FocusManager", () => { FocusManager = null!; });

        _engineTimer.Stop();
        var runTime = _engineTimer.Elapsed.TotalSeconds;

        State = EngineState.Disposed;

        lock (_instanceLock)
        {
            if (_instance == this)
                _instance = null!;
        }

        LumoraLogger.Log("=====================================");
        LumoraLogger.Log($"Engine Shutdown Complete");
        LumoraLogger.Log($"Total runtime: {runTime:F2}s, Frames: {_metrics.TotalFrames}");
        LumoraLogger.Log("=====================================");
    }

    private void DisposeSubsystem(string name, Action disposer)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            disposer();
            sw.Stop();
            SetSubsystemStatus(name, SubsystemStatus.Disposed);
            LumoraLogger.Log($"  [{sw.ElapsedMilliseconds}ms] {name} disposed");
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"  [ERROR] {name} disposal: {ex.Message}");
        }
    }

    #endregion

    #region Utility

    public string GetDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Lumora Engine v{EngineVersion.VersionString}");
        sb.AppendLine($"State: {State}");
        sb.AppendLine($"Platform: {Platform}");
        sb.AppendLine($"Runtime: {TotalTime:F2}s");
        sb.AppendLine($"Frames: {FrameCount}");
        sb.AppendLine($"FPS: {_metrics.CurrentFPS:F1} (avg {_metrics.AverageFPS:F1})");
        sb.AppendLine($"Memory: {_metrics.CurrentMemoryUsage / 1024 / 1024}MB (peak {_metrics.PeakMemoryUsage / 1024 / 1024}MB)");
        sb.AppendLine($"GC Collections: {_metrics.GCCollections}");
        sb.AppendLine("Subsystems:");
        foreach (var kvp in _subsystemStatus)
            sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
        if (WorldManager != null)
            sb.AppendLine($"Worlds: {WorldManager.WorldCount}");
        return sb.ToString();
    }

    #endregion
}

