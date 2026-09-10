// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Godot;
using LogLevel = Lumora.Core.Logging.Logger.LogLevel;

namespace Lumora.Godot.Debug;

#nullable enable

/// <summary>
/// Debug console scene root for the separate debug process.
/// Receives logs and performance telemetry over UDP from the game process.
/// </summary>
public partial class DebugWindow : Control
{
    private const int MaxLogEntries = 5000;
    private const float ConnectionTimeoutSec = 2.0f;
    private const int MaxMemorySnapshots = 120;
    private const double AutoSnapshotIntervalSec = 5.0;
    // How close (in scrollbar units) to the very bottom still counts as "parked at the tail" for sticky autoscroll. -xlinka
    private const float ScrollBottomEpsilon = 8.0f;

    private readonly List<LogEntry> _logEntries = new();
    private readonly object _logLock = new();
    private readonly Queue<string> _packetQueue = new();
    private readonly object _packetLock = new();

    private readonly List<MemorySnapshot> _memorySnapshots = new();
    private MemorySnapshot? _latestMemorySnapshot;
    private MemorySnapshot? _selectedMemorySnapshot;
    private double _lastAutoSnapshotUnix = -1;

    private UdpClient? _udpClient;
    private Thread? _listenerThread;
    private volatile bool _listenerRunning;

    private bool _logsDirty;
    private bool _memoryUiDirty;
    private double _secondsSincePacket = double.MaxValue;
    private long _packetCount;
    private int _lastShownLogCount;
    private long _memoryPacketCount;

    // UI - Logs tab
    private RichTextLabel? _logDisplay;
    private CheckButton? _filterLog;
    private CheckButton? _filterWarn;
    private CheckButton? _filterError;
    private CheckButton? _filterDebug;
    private LineEdit? _searchBox;
    private Button? _clearBtn;
    private Label? _statusLabel;

    // UI - Performance tab
    private Label? _fpsLabel;
    private Label? _frameTimeLabel;
    private Label? _renderTimeLabel;
    private Label? _physicsTimeLabel;
    private Label? _worldNameLabel;
    private Label? _slotsLabel;
    private Label? _componentsLabel;
    private Label? _usersLabel;
    private Label? _gcMemLabel;
    private Label? _videoMemLabel;
    private Label? _objectsLabel;
    private Label? _nodesLabel;

    // UI - Memory profiler tab
    private Button? _captureSnapshotBtn;
    private Button? _clearSnapshotsBtn;
    private CheckButton? _autoSnapshotToggle;
    private CheckButton? _memoryLiveToggle;
    private Label? _memoryStatusLabel;
    private ItemList? _snapshotList;
    private Label? _memCommittedValue;
    private Label? _memGcValue;
    private Label? _memEstimatedValue;
    private Label? _memWorkingSetValue;
    private Label? _memPrivateValue;
    private Label? _memVideoValue;
    private Label? _memObjectsValue;
    private Label? _memNodesValue;
    private Label? _gcCountValue;
    private LineEdit? _memorySearchBox;
    private Tree? _componentTree;
    private RichTextLabel? _componentDetails;
    private MemoryHistoryGraph? _memoryGraph;

    // UI - Render / GPU tab
    private Label? _drawCallsValue;
    private Label? _primitivesValue;
    private Label? _renderObjectsValue;
    private Label? _textureMemValue;
    private Label? _bufferMemValue;
    private Label? _renderVideoMemValue;

    // UI - Network tab
    private Label? _netRoleValue;
    private Label? _netLatencyValue;
    private Label? _netWorldValue;
    private Label? _netEncryptedValue;
    private Label? _netSessionIdValue;
    private Label? _netVisibilityValue;
    private Label? _netSyncRateValue;
    private Label? _netConnCountValue;
    private Label? _netDeltasValue;
    private Label? _netFullsValue;
    private Label? _netStreamsValue;
    private Label? _netRawValue;
    private Label? _netCorrectionsValue;
    private Label? _netProcessedValue;
    private Label? _netLastDeltaValue;
    private Label? _netToProcessValue;
    private Label? _netToTransmitValue;
    private Label? _netIncomingValue;
    private Label? _netPendingStreamsValue;
    private Label? _netUploadsValue;
    private Label? _netDownloadsValue;
    private Label? _netAssetReqValue;
    private Label? _netRelaysValue;
    private Tree? _netConnectionTree;
    private Label? _netStatusLabel;
    private long _netPacketCount;

    // UI - Profiler tab
    private ProfilerGraph? _profilerGraph;
    private Tree? _profComponentTree;
    private Tree? _profSlotTree;
    private Label? _profStatusLabel;
    private long _profilePacketCount;

    private struct LogEntry
    {
        public LogLevel Level;
        public string Timestamp;
        public string Message;
    }

    private sealed class MemoryComponentEntry
    {
        public string Name = string.Empty;
        public int Count;
        public long Bytes;
        public float Percent;
    }

    private sealed class MemorySnapshot
    {
        public DateTime TimestampUtc;
        public long CommittedBytes;
        public long GcBytes;
        public int Gen0;
        public int Gen1;
        public int Gen2;
        public long EstimatedBytes;
        public long WorkingSetBytes;
        public long PrivateBytes;
        public long VideoBytes;
        public int GodotObjects;
        public int GodotNodes;
        public List<MemoryComponentEntry> Components = new();
    }

    public override void _Ready()
    {
        ConfigureWindow();
        CacheNodeReferences();
        ConfigureMemoryProfilerTree();
        ConfigureProfilerTrees();
        ConfigureNetworkTree();
        ConfigureFrameTab();
        WireEvents();
        StartUdpListener();

        AddLocalLog(LogLevel.LOG, $"Listening for telemetry on udp://127.0.0.1:{DebugUdpSender.Port}");
        _logsDirty = true;
        _memoryUiDirty = true;

        if (_componentDetails != null && string.IsNullOrEmpty(_componentDetails.Text))
        {
            _componentDetails.Text = "No memory snapshot selected.";
        }

        if (_memoryStatusLabel != null && string.IsNullOrEmpty(_memoryStatusLabel.Text))
        {
            _memoryStatusLabel.Text = "Waiting for memory packets...";
        }
    }

    public override void _Process(double delta)
    {
        _secondsSincePacket += delta;
        DrainPackets();

        if (_logsDirty)
        {
            _logsDirty = false;
            RebuildLogDisplay();
        }

        RefreshFrameUi();

        if (_memoryUiDirty)
        {
            _memoryUiDirty = false;
            RebuildMemoryProfilerUi();
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        {
            GetTree().Quit();
        }
    }

    public override void _ExitTree()
    {
        StopUdpListener();
    }

    private void ConfigureWindow()
    {
        DisplayServer.WindowSetTitle("Lumora Debug Console");
        DisplayServer.WindowSetSize(new Vector2I(1060, 760));
        DisplayServer.WindowSetMinSize(new Vector2I(860, 620));

        // Keep UI text at a stable pixel size even when the window is resized.
        var window = GetWindow();
        if (window != null)
        {
            window.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;
            window.ContentScaleAspect = Window.ContentScaleAspectEnum.Ignore;
            window.ContentScaleFactor = 1.0f;
        }

        var screenCount = DisplayServer.GetScreenCount();
        if (screenCount > 1)
        {
            var secondScreen = DisplayServer.ScreenGetPosition(1);
            DisplayServer.WindowSetPosition(secondScreen + new Vector2I(50, 50));
        }
    }

    private void CacheNodeReferences()
    {
        _filterLog = GetNodeOrNull<CheckButton>("%FilterLog");
        _filterWarn = GetNodeOrNull<CheckButton>("%FilterWarn");
        _filterError = GetNodeOrNull<CheckButton>("%FilterError");
        _filterDebug = GetNodeOrNull<CheckButton>("%FilterDebug");
        _searchBox = GetNodeOrNull<LineEdit>("%SearchBox");
        _clearBtn = GetNodeOrNull<Button>("%ClearBtn");
        _logDisplay = GetNodeOrNull<RichTextLabel>("%LogDisplay");
        _statusLabel = GetNodeOrNull<Label>("%StatusLabel");

        _fpsLabel = GetNodeOrNull<Label>("%FPSValue");
        _frameTimeLabel = GetNodeOrNull<Label>("%FrameTimeValue");
        _renderTimeLabel = GetNodeOrNull<Label>("%RenderTimeValue");
        _physicsTimeLabel = GetNodeOrNull<Label>("%PhysicsTimeValue");
        _worldNameLabel = GetNodeOrNull<Label>("%WorldNameValue");
        _slotsLabel = GetNodeOrNull<Label>("%SlotsValue");
        _componentsLabel = GetNodeOrNull<Label>("%ComponentsValue");
        _usersLabel = GetNodeOrNull<Label>("%UsersValue");
        _gcMemLabel = GetNodeOrNull<Label>("%GCMemValue");
        _videoMemLabel = GetNodeOrNull<Label>("%VideoMemValue");
        _objectsLabel = GetNodeOrNull<Label>("%ObjectsValue");
        _nodesLabel = GetNodeOrNull<Label>("%NodesValue");

        _captureSnapshotBtn = GetNodeOrNull<Button>("%CaptureSnapshotBtn");
        _clearSnapshotsBtn = GetNodeOrNull<Button>("%ClearSnapshotsBtn");
        _autoSnapshotToggle = GetNodeOrNull<CheckButton>("%AutoSnapshotToggle");
        _memoryLiveToggle = GetNodeOrNull<CheckButton>("%MemoryLiveToggle");
        _memoryStatusLabel = GetNodeOrNull<Label>("%MemoryStatusLabel");
        _snapshotList = GetNodeOrNull<ItemList>("%SnapshotList");
        _memCommittedValue = GetNodeOrNull<Label>("%MemCommittedValue");
        _memGcValue = GetNodeOrNull<Label>("%MemGCValue");
        _memEstimatedValue = GetNodeOrNull<Label>("%MemEstimatedValue");
        _memWorkingSetValue = GetNodeOrNull<Label>("%MemWorkingSetValue");
        _memPrivateValue = GetNodeOrNull<Label>("%MemPrivateValue");
        _memVideoValue = GetNodeOrNull<Label>("%MemVideoValue");
        _memObjectsValue = GetNodeOrNull<Label>("%MemObjectsValue");
        _memNodesValue = GetNodeOrNull<Label>("%MemNodesValue");
        _gcCountValue = GetNodeOrNull<Label>("%GCCountValue");
        _memorySearchBox = GetNodeOrNull<LineEdit>("%MemorySearchBox");
        _componentTree = GetNodeOrNull<Tree>("%ComponentTree");
        _componentDetails = GetNodeOrNull<RichTextLabel>("%ComponentDetails");
        _memoryGraph = GetNodeOrNull<MemoryHistoryGraph>("%MemoryGraph");

        _drawCallsValue = GetNodeOrNull<Label>("%DrawCallsValue");
        _primitivesValue = GetNodeOrNull<Label>("%PrimitivesValue");
        _renderObjectsValue = GetNodeOrNull<Label>("%RenderObjectsValue");
        _textureMemValue = GetNodeOrNull<Label>("%TextureMemValue");
        _bufferMemValue = GetNodeOrNull<Label>("%BufferMemValue");
        _renderVideoMemValue = GetNodeOrNull<Label>("%RenderVideoMemValue");

        _netRoleValue = GetNodeOrNull<Label>("%NetRoleValue");
        _netLatencyValue = GetNodeOrNull<Label>("%NetLatencyValue");
        _netWorldValue = GetNodeOrNull<Label>("%NetWorldValue");
        _netEncryptedValue = GetNodeOrNull<Label>("%NetEncryptedValue");
        _netSessionIdValue = GetNodeOrNull<Label>("%NetSessionIdValue");
        _netVisibilityValue = GetNodeOrNull<Label>("%NetVisibilityValue");
        _netSyncRateValue = GetNodeOrNull<Label>("%NetSyncRateValue");
        _netConnCountValue = GetNodeOrNull<Label>("%NetConnCountValue");
        _netDeltasValue = GetNodeOrNull<Label>("%NetDeltasValue");
        _netFullsValue = GetNodeOrNull<Label>("%NetFullsValue");
        _netStreamsValue = GetNodeOrNull<Label>("%NetStreamsValue");
        _netRawValue = GetNodeOrNull<Label>("%NetRawValue");
        _netCorrectionsValue = GetNodeOrNull<Label>("%NetCorrectionsValue");
        _netProcessedValue = GetNodeOrNull<Label>("%NetProcessedValue");
        _netLastDeltaValue = GetNodeOrNull<Label>("%NetLastDeltaValue");
        _netToProcessValue = GetNodeOrNull<Label>("%NetToProcessValue");
        _netToTransmitValue = GetNodeOrNull<Label>("%NetToTransmitValue");
        _netIncomingValue = GetNodeOrNull<Label>("%NetIncomingValue");
        _netPendingStreamsValue = GetNodeOrNull<Label>("%NetPendingStreamsValue");
        _netUploadsValue = GetNodeOrNull<Label>("%NetUploadsValue");
        _netDownloadsValue = GetNodeOrNull<Label>("%NetDownloadsValue");
        _netAssetReqValue = GetNodeOrNull<Label>("%NetAssetReqValue");
        _netRelaysValue = GetNodeOrNull<Label>("%NetRelaysValue");
        _netConnectionTree = GetNodeOrNull<Tree>("%NetConnectionTree");
        _netStatusLabel = GetNodeOrNull<Label>("%NetStatusLabel");

        _profilerGraph = GetNodeOrNull<ProfilerGraph>("%ProfilerGraph");
        _profComponentTree = GetNodeOrNull<Tree>("%ProfComponentTree");
        _profSlotTree = GetNodeOrNull<Tree>("%ProfSlotTree");
        _profStatusLabel = GetNodeOrNull<Label>("%ProfStatusLabel");

        CacheFrameTabReferences();
    }

    private void ConfigureMemoryProfilerTree()
    {
        if (_componentTree == null)
        {
            return;
        }

        _componentTree.Columns = 4;
        _componentTree.HideRoot = true;
        _componentTree.ColumnTitlesVisible = true;
        _componentTree.SetColumnTitle(0, "Component");
        _componentTree.SetColumnTitle(1, "Count");
        _componentTree.SetColumnTitle(2, "Memory");
        _componentTree.SetColumnTitle(3, "%");
        _componentTree.SetColumnExpand(0, true);
        _componentTree.SetColumnExpand(1, false);
        _componentTree.SetColumnExpand(2, false);
        _componentTree.SetColumnExpand(3, false);
        _componentTree.SetColumnCustomMinimumWidth(1, 70);
        _componentTree.SetColumnCustomMinimumWidth(2, 110);
        _componentTree.SetColumnCustomMinimumWidth(3, 60);
    }

    private void ConfigureProfilerTrees()
    {
        ConfigureProfilerTree(_profComponentTree, "Component");
        ConfigureProfilerTree(_profSlotTree, "Slot");
    }

    private void ConfigureNetworkTree()
    {
        if (_netConnectionTree == null)
        {
            return;
        }

        _netConnectionTree.Columns = 6;
        _netConnectionTree.HideRoot = true;
        _netConnectionTree.ColumnTitlesVisible = true;
        _netConnectionTree.SetColumnTitle(0, "User");
        _netConnectionTree.SetColumnTitle(1, "Transport");
        _netConnectionTree.SetColumnTitle(2, "Endpoint");
        _netConnectionTree.SetColumnTitle(3, "Ping");
        _netConnectionTree.SetColumnTitle(4, "Enc");
        _netConnectionTree.SetColumnTitle(5, "Recv");
        _netConnectionTree.SetColumnExpand(0, true);
        _netConnectionTree.SetColumnExpand(1, false);
        _netConnectionTree.SetColumnExpand(2, true);
        _netConnectionTree.SetColumnExpand(3, false);
        _netConnectionTree.SetColumnExpand(4, false);
        _netConnectionTree.SetColumnExpand(5, false);
        _netConnectionTree.SetColumnCustomMinimumWidth(1, 100);
        _netConnectionTree.SetColumnCustomMinimumWidth(3, 64);
        _netConnectionTree.SetColumnCustomMinimumWidth(4, 48);
        _netConnectionTree.SetColumnCustomMinimumWidth(5, 96);
    }

    private static void ConfigureProfilerTree(Tree? tree, string firstColumnTitle)
    {
        if (tree == null)
        {
            return;
        }

        tree.Columns = 3;
        tree.HideRoot = true;
        tree.ColumnTitlesVisible = true;
        tree.SetColumnTitle(0, firstColumnTitle);
        tree.SetColumnTitle(1, "Time");
        tree.SetColumnTitle(2, "Count");
        tree.SetColumnExpand(0, true);
        tree.SetColumnExpand(1, false);
        tree.SetColumnExpand(2, false);
        tree.SetColumnCustomMinimumWidth(1, 90);
        tree.SetColumnCustomMinimumWidth(2, 60);
    }

    private void WireEvents()
    {
        _filterLog?.Connect("toggled", Callable.From<bool>(_ => _logsDirty = true));
        _filterWarn?.Connect("toggled", Callable.From<bool>(_ => _logsDirty = true));
        _filterError?.Connect("toggled", Callable.From<bool>(_ => _logsDirty = true));
        _filterDebug?.Connect("toggled", Callable.From<bool>(_ => _logsDirty = true));
        _searchBox?.Connect("text_changed", Callable.From<string>(_ => _logsDirty = true));

        if (_clearBtn != null)
        {
            _clearBtn.Pressed += () =>
            {
                lock (_logLock)
                {
                    _logEntries.Clear();
                }

                _logsDirty = true;
            };
        }

        if (_captureSnapshotBtn != null)
        {
            _captureSnapshotBtn.Pressed += CaptureManualSnapshot;
        }

        if (_clearSnapshotsBtn != null)
        {
            _clearSnapshotsBtn.Pressed += ClearSnapshots;
        }

        if (_snapshotList != null)
        {
            _snapshotList.ItemSelected += OnSnapshotSelected;
        }

        if (_memoryLiveToggle != null)
        {
            _memoryLiveToggle.Toggled += OnLiveToggled;
        }

        if (_memorySearchBox != null)
        {
            _memorySearchBox.TextChanged += _ => _memoryUiDirty = true;
        }

        if (_componentTree != null)
        {
            _componentTree.ItemSelected += OnComponentSelectionChanged;
        }
    }

    private void StartUdpListener()
    {
        try
        {
            _udpClient = new UdpClient(DebugUdpSender.Port);
            _udpClient.Client.ReceiveTimeout = 1000;

            _listenerRunning = true;
            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "LumoraDebugUdpListener"
            };
            _listenerThread.Start();
        }
        catch (Exception ex)
        {
            AddLocalLog(LogLevel.ERROR, $"Failed to bind UDP port {DebugUdpSender.Port}: {ex.Message}");
            _logsDirty = true;
        }
    }

    private void StopUdpListener()
    {
        _listenerRunning = false;

        try
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
        }
        catch
        {
            // no-op
        }

        if (_listenerThread != null && _listenerThread.IsAlive)
        {
            _listenerThread.Join(500);
        }
    }

    private void ListenLoop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);

        while (_listenerRunning)
        {
            try
            {
                var bytes = _udpClient!.Receive(ref remote);
                var packet = Encoding.UTF8.GetString(bytes);

                lock (_packetLock)
                {
                    _packetQueue.Enqueue(packet);
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }

                if (!_listenerRunning)
                {
                    break;
                }

                EnqueueInternalError($"UDP socket error: {ex.SocketErrorCode}");
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_listenerRunning)
                {
                    break;
                }

                EnqueueInternalError($"UDP listener exception: {ex.Message}");
            }
        }
    }

    private void EnqueueInternalError(string message)
    {
        lock (_packetLock)
        {
            _packetQueue.Enqueue($"ERROR|{DateTime.Now:HH:mm:ss}|{message}");
        }
    }

    private void DrainPackets()
    {
        while (true)
        {
            string? packet = null;

            lock (_packetLock)
            {
                if (_packetQueue.Count > 0)
                {
                    packet = _packetQueue.Dequeue();
                }
            }

            if (packet == null)
            {
                break;
            }

            _packetCount++;
            _secondsSincePacket = 0;
            HandlePacket(packet);
        }
    }

    private void HandlePacket(string packet)
    {
        var segments = packet.Split('|');
        if (segments.Length == 0)
        {
            return;
        }

        var kind = segments[0].Trim().ToUpperInvariant();

        switch (kind)
        {
            case "LOG":
            case "WARN":
            case "ERROR":
            case "DEBUG":
                HandleLogPacket(packet, kind);
                break;
            case "PERF":
                HandlePerfPacket(segments);
                break;
            case "MEM":
                HandleMemoryPacket(segments);
                break;
            case "RNDR":
                HandleRenderPacket(segments);
                break;
            case "NET":
                HandleNetworkPacket(segments);
                break;
            case "PROF":
                HandleProfilePacket(segments);
                break;
            case "FRAM":
                HandleFramePacket(segments);
                break;
            case "DROP":
                // The sender could not fit a packet in a datagram. Say so loudly: the alternative is a
                // tab that silently stops updating while its stale numbers still look believable.
                AddLocalLog(LogLevel.WARN, segments.Length >= 3
                    ? $"Telemetry packet '{segments[1]}' dropped: {segments[2]} bytes exceeds the datagram limit. That tab is missing data."
                    : "Telemetry packet dropped: over the datagram limit.");
                _logsDirty = true;
                break;
        }
    }

    private void HandleLogPacket(string packet, string kind)
    {
        var parts = packet.Split('|', 3);
        if (parts.Length < 3)
        {
            return;
        }

        var timestamp = parts[1].Trim();
        var message = parts[2];

        var level = kind switch
        {
            "WARN" => LogLevel.WARN,
            "ERROR" => LogLevel.ERROR,
            "DEBUG" => LogLevel.DEBUG,
            _ => LogLevel.LOG
        };

        OnLogReceived(level, timestamp, message);
    }

    private void HandlePerfPacket(string[] parts)
    {
        if (parts.Length < 13)
        {
            return;
        }

        if (!TryFloat(parts[1], out var fps)) return;
        if (!TryFloat(parts[2], out var frameTime)) return;
        if (!TryFloat(parts[3], out var renderTime)) return;
        if (!TryFloat(parts[4], out var physicsTime)) return;

        var worldName = parts[5];

        if (!TryInt(parts[6], out var slots)) return;
        if (!TryInt(parts[7], out var components)) return;
        if (!TryInt(parts[8], out var users)) return;
        if (!TryLong(parts[9], out var gcMemBytes)) return;
        if (!TryLong(parts[10], out var videoMemBytes)) return;
        if (!TryInt(parts[11], out var godotObjects)) return;
        if (!TryInt(parts[12], out var godotNodes)) return;

        SetLabel(_fpsLabel, $"{fps:F1}",
            fps < 30f ? new Color(1f, 0.3f, 0.3f) :
            fps < 60f ? new Color(1f, 0.85f, 0.2f) :
            new Color(0.3f, 0.9f, 0.4f));

        SetLabel(_frameTimeLabel, $"{frameTime:F2} ms");
        SetLabel(_renderTimeLabel, $"{renderTime:F2} ms");
        SetLabel(_physicsTimeLabel, $"{physicsTime:F2} ms");
        SetLabel(_worldNameLabel, worldName);
        SetLabel(_slotsLabel, $"{slots:N0}");
        SetLabel(_componentsLabel, $"{components:N0}");
        SetLabel(_usersLabel, $"{users:N0}");
        SetLabel(_gcMemLabel, FormatBytes(gcMemBytes));
        SetLabel(_videoMemLabel, FormatBytes(videoMemBytes));
        SetLabel(_objectsLabel, $"{godotObjects:N0}");
        SetLabel(_nodesLabel, $"{godotNodes:N0}");

        _profilerGraph?.AddFrame(frameTime);
    }

    private void HandleRenderPacket(string[] parts)
    {
        // RNDR|drawCalls|primitives|objects|textureMem|bufferMem|videoMem
        if (parts.Length < 7) return;
        if (!TryLong(parts[1], out var drawCalls)) return;
        if (!TryLong(parts[2], out var primitives)) return;
        if (!TryLong(parts[3], out var objects)) return;
        if (!TryLong(parts[4], out var textureMem)) return;
        if (!TryLong(parts[5], out var bufferMem)) return;
        if (!TryLong(parts[6], out var videoMem)) return;

        SetLabel(_drawCallsValue, $"{drawCalls:N0}",
            drawCalls > 2000 ? new Color(1f, 0.5f, 0.4f) :
            drawCalls > 800 ? new Color(1f, 0.85f, 0.3f) :
            new Color(0.8f, 0.85f, 0.9f));
        SetLabel(_primitivesValue, $"{primitives:N0}");
        SetLabel(_renderObjectsValue, $"{objects:N0}");
        SetLabel(_textureMemValue, FormatBytes(textureMem));
        SetLabel(_bufferMemValue, FormatBytes(bufferMem));
        SetLabel(_renderVideoMemValue, FormatBytes(videoMem));
    }

    private void HandleNetworkPacket(string[] parts)
    {
        // NET|role|world|sessionId|visibility|syncRate|connCount|latencyMs|allEncrypted
        //    |sDeltas|rDeltas|sFulls|rFulls|sStreams|rStreams|sRaw|rRaw|corrections|processed|lastDeltaChanges
        //    |toProcess|toTransmit|incoming|pendingStreams|uploads|downloads|assetRequests|relays|ROWS
        if (parts.Length < 28)
        {
            return;
        }

        _netPacketCount++;

        SetLabel(_netRoleValue, parts[1]);
        SetLabel(_netWorldValue, parts[2]);
        SetLabel(_netSessionIdValue, parts[3]);
        SetLabel(_netVisibilityValue, parts[4]);
        SetLabel(_netSyncRateValue, $"{parts[5]} Hz");
        SetLabel(_netConnCountValue, parts[6]);

        if (TryInt(parts[7], out var latency) && latency >= 0)
        {
            SetLabel(_netLatencyValue, $"{latency} ms", PingColor(latency));
        }
        else
        {
            SetLabel(_netLatencyValue, "-", new Color(0.6f, 0.6f, 0.65f));
        }

        bool allEncrypted = parts[8] == "1";
        SetLabel(_netEncryptedValue, allEncrypted ? "Yes" : "No",
            allEncrypted ? new Color(0.4f, 0.9f, 0.5f) : new Color(1f, 0.55f, 0.4f));

        SetLabel(_netDeltasValue, $"{Count(parts[9])} / {Count(parts[10])}");
        SetLabel(_netFullsValue, $"{Count(parts[11])} / {Count(parts[12])}");
        SetLabel(_netStreamsValue, $"{Count(parts[13])} / {Count(parts[14])}");
        SetLabel(_netRawValue, $"{Count(parts[15])} / {Count(parts[16])}");
        SetLabel(_netCorrectionsValue, Count(parts[17]));
        SetLabel(_netProcessedValue, Count(parts[18]));
        SetLabel(_netLastDeltaValue, Count(parts[19]));

        // Queues: amber when anything is backed up (a healthy session sits near zero).
        SetQueueLabel(_netToProcessValue, parts[20]);
        SetQueueLabel(_netToTransmitValue, parts[21]);
        SetQueueLabel(_netIncomingValue, parts[22]);
        SetQueueLabel(_netPendingStreamsValue, parts[23]);

        // Asset transfers: green while active, dim when idle.
        SetActivityLabel(_netUploadsValue, parts[24]);
        SetActivityLabel(_netDownloadsValue, parts[25]);
        SetActivityLabel(_netAssetReqValue, parts[26]);
        SetActivityLabel(_netRelaysValue, parts[27]);

        RebuildNetworkConnections(parts.Length > 28 ? parts[28] : string.Empty);

        SetLabel(_netStatusLabel,
            $"{parts[1]} | Connections: {parts[6]} | Packets: {_netPacketCount:N0}");
    }

    // A pending-queue depth: neutral at zero, amber when work is backing up.
    private static void SetQueueLabel(Label? label, string raw)
    {
        bool busy = TryInt(raw, out var n) && n > 0;
        SetLabel(label, Count(raw), busy ? new Color(1f, 0.85f, 0.3f) : new Color(0.85f, 0.85f, 0.9f));
    }

    // An activity count (asset transfers): green when active, dim when idle.
    private static void SetActivityLabel(Label? label, string raw)
    {
        bool active = TryInt(raw, out var n) && n > 0;
        SetLabel(label, Count(raw), active ? new Color(0.4f, 0.9f, 0.5f) : new Color(0.6f, 0.6f, 0.65f));
    }

    private void RebuildNetworkConnections(string rows)
    {
        if (_netConnectionTree == null)
        {
            return;
        }

        _netConnectionTree.Clear();
        var root = _netConnectionTree.CreateItem();

        if (string.IsNullOrEmpty(rows))
        {
            return;
        }

        foreach (var rowStr in rows.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = rowStr.Split('~');
            if (f.Length < 6)
            {
                continue;
            }

            var item = _netConnectionTree.CreateItem(root);
            item.SetText(0, f[0]);
            item.SetText(1, f[1]);
            item.SetCustomColor(1, f[1] == "Steam"
                ? new Color(0.45f, 0.7f, 1f)
                : new Color(0.65f, 0.85f, 0.7f));
            item.SetText(2, f[2]);

            if (TryInt(f[3], out var ping) && ping >= 0)
            {
                item.SetText(3, $"{ping}");
                item.SetCustomColor(3, PingColor(ping));
            }
            else
            {
                item.SetText(3, "-");
            }

            bool enc = f[4] == "1";
            item.SetText(4, enc ? "Yes" : "No");
            item.SetCustomColor(4, enc ? new Color(0.4f, 0.9f, 0.5f) : new Color(1f, 0.55f, 0.4f));

            item.SetText(5, TryLong(f[5], out var rb) ? FormatBytes(rb) : f[5]);
        }
    }

    private static Color PingColor(int ping)
    {
        return ping > 150 ? new Color(1f, 0.4f, 0.4f) :
            ping > 60 ? new Color(1f, 0.85f, 0.3f) :
            new Color(0.4f, 0.9f, 0.5f);
    }

    // Display helper for the integer traffic counters: thousands-separated, raw on parse failure.
    private static string Count(string raw)
    {
        return TryLong(raw, out var v) ? v.ToString("N0", CultureInfo.InvariantCulture) : raw;
    }

    private void HandleProfilePacket(string[] parts)
    {
        // PROF|components|slots   (each segment: name:ms:count,name:ms:count,...)
        if (parts.Length < 3) return;
        _profilePacketCount++;

        var components = ParseProfileEntries(parts[1]);
        var slots = ParseProfileEntries(parts[2]);

        RebuildProfileTree(_profComponentTree, components);
        RebuildProfileTree(_profSlotTree, slots);

        AccumulateComponentProfile(components);

        SetLabel(_profStatusLabel,
            $"Live | Packets: {_profilePacketCount:N0} | Components: {components.Count} | Slots: {slots.Count} | latest frame");
    }

    private static List<(string name, float ms, int count)> ParseProfileEntries(string raw)
    {
        var list = new List<(string name, float ms, int count)>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return list;
        }

        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = entry.Split(':', 3);
            if (p.Length != 3) continue;
            if (!TryFloat(p[1], out var ms)) continue;
            if (!TryInt(p[2], out var count)) continue;
            list.Add((p[0].Trim(), ms, count));
        }

        list.Sort((a, b) => b.ms.CompareTo(a.ms));
        return list;
    }

    private static void RebuildProfileTree(Tree? tree, List<(string name, float ms, int count)> entries)
    {
        if (tree == null)
        {
            return;
        }

        tree.Clear();
        var root = tree.CreateItem();

        float maxMs = 0.0001f;
        foreach (var e in entries)
        {
            if (e.ms > maxMs) maxMs = e.ms;
        }

        foreach (var e in entries)
        {
            var row = tree.CreateItem(root);
            row.SetText(0, e.name);
            row.SetText(1, $"{e.ms:F3} ms");
            row.SetText(2, $"{e.count:N0}");

            // Hotter (more time) rows shade toward red, so the expensive component types / slots pop. -xlinka
            var intensity = Mathf.Clamp(e.ms / maxMs, 0f, 1f);
            var color = new Color(
                Mathf.Lerp(0.7f, 1f, intensity),
                Mathf.Lerp(0.85f, 0.45f, intensity),
                Mathf.Lerp(0.55f, 0.35f, intensity),
                1f);
            row.SetCustomColor(1, color);
        }
    }

    private void HandleMemoryPacket(string[] parts)
    {
        if (!TryParseMemoryPacket(parts, out var snapshot))
        {
            return;
        }

        _latestMemorySnapshot = snapshot;
        _memoryPacketCount++;

        _memoryGraph?.AddSample(
            snapshot.CommittedBytes,
            snapshot.GcBytes,
            snapshot.EstimatedBytes,
            snapshot.VideoBytes);

        if (_autoSnapshotToggle?.ButtonPressed == true)
        {
            double now = Time.GetUnixTimeFromSystem();
            if (_lastAutoSnapshotUnix < 0 || now - _lastAutoSnapshotUnix >= AutoSnapshotIntervalSec)
            {
                AddSnapshot(CloneSnapshot(snapshot), keepLiveSelection: true);
                _lastAutoSnapshotUnix = now;
            }
        }

        if (_memoryLiveToggle?.ButtonPressed ?? true)
        {
            _selectedMemorySnapshot = null;
        }

        _memoryUiDirty = true;
    }

    private void OnLogReceived(LogLevel level, string timestamp, string message)
    {
        lock (_logLock)
        {
            _logEntries.Add(new LogEntry
            {
                Level = level,
                Timestamp = timestamp,
                Message = message
            });

            if (_logEntries.Count > MaxLogEntries)
            {
                _logEntries.RemoveRange(0, _logEntries.Count - MaxLogEntries);
            }
        }

        _logsDirty = true;
    }

    private void AddLocalLog(LogLevel level, string message)
    {
        OnLogReceived(level, DateTime.Now.ToString("HH:mm:ss"), message);
    }

    private void RebuildLogDisplay()
    {
        if (_logDisplay == null)
        {
            return;
        }

        var showLog = _filterLog?.ButtonPressed ?? true;
        var showWarn = _filterWarn?.ButtonPressed ?? true;
        var showError = _filterError?.ButtonPressed ?? true;
        var showDebug = _filterDebug?.ButtonPressed ?? true;
        var search = _searchBox?.Text?.Trim() ?? string.Empty;
        var hasSearch = !string.IsNullOrEmpty(search);

        // Sticky autoscroll: follow the tail only while the user is already parked at the bottom. The moment they
        // scroll up to read, we hold their position; following resumes on its own once they scroll back down. There is
        // no "Auto" toggle anymore - position is the source of truth. A full Clear()+rebuild resets the scrollbar, so
        // we snapshot the intent (and the exact offset) up front and reapply it after the content is back. -xlinka
        var scrollBar = _logDisplay.GetVScrollBar();
        bool stickToBottom = scrollBar == null || scrollBar.MaxValue <= scrollBar.Page
            || scrollBar.Value + scrollBar.Page >= scrollBar.MaxValue - ScrollBottomEpsilon;
        double priorScroll = scrollBar?.Value ?? 0.0;

        _logDisplay.Clear();

        int total;
        int shown = 0;

        lock (_logLock)
        {
            total = _logEntries.Count;

            foreach (var entry in _logEntries)
            {
                if (entry.Level == LogLevel.LOG && !showLog) continue;
                if (entry.Level == LogLevel.WARN && !showWarn) continue;
                if (entry.Level == LogLevel.ERROR && !showError) continue;
                if (entry.Level == LogLevel.DEBUG && !showDebug) continue;

                if (hasSearch && !entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                shown++;

                _logDisplay.PushColor(new Color(0.4f, 0.4f, 0.45f));
                _logDisplay.AddText($"[{entry.Timestamp}] ");
                _logDisplay.Pop();

                var (levelColor, levelTag) = GetLevelStyle(entry.Level);
                _logDisplay.PushColor(levelColor);
                _logDisplay.PushBold();
                _logDisplay.AddText($"[{levelTag}] ");
                _logDisplay.Pop();
                _logDisplay.Pop();

                var msgColor = entry.Level == LogLevel.ERROR ? new Color(1f, 0.5f, 0.5f) :
                    entry.Level == LogLevel.WARN ? new Color(1f, 0.9f, 0.6f) :
                    entry.Level == LogLevel.DEBUG ? new Color(0.55f, 0.55f, 0.6f) :
                    new Color(0.85f, 0.85f, 0.9f);

                _logDisplay.PushColor(msgColor);
                _logDisplay.AddText(entry.Message);
                _logDisplay.Pop();
                _logDisplay.Newline();
            }
        }

        _lastShownLogCount = shown;
        UpdateStatusLabel(total);

        if (stickToBottom)
        {
            if (_logDisplay.GetLineCount() > 0)
            {
                _logDisplay.ScrollToLine(_logDisplay.GetLineCount() - 1);
            }
        }
        else if (scrollBar != null)
        {
            // Reapply the held position deferred: right after a rebuild the scrollbar's MaxValue can still be stale, so
            // setting Value inline would clamp and yank the view to the top. CallDeferred runs after the label
            // re-lays-out, within this same frame (before draw), so there's no visible jump. -xlinka
            var bar = scrollBar;
            var target = priorScroll;
            Callable.From(() => { if (GodotObject.IsInstanceValid(bar)) bar.Value = target; }).CallDeferred();
        }
    }

    private void RebuildMemoryProfilerUi()
    {
        var liveMode = _memoryLiveToggle?.ButtonPressed ?? true;
        var snapshot = liveMode || _selectedMemorySnapshot == null
            ? _latestMemorySnapshot
            : _selectedMemorySnapshot;

        RefreshSnapshotListSelection();

        if (snapshot == null)
        {
            SetLabel(_memCommittedValue, "-");
            SetLabel(_memGcValue, "-");
            SetLabel(_memEstimatedValue, "-");
            SetLabel(_memWorkingSetValue, "-");
            SetLabel(_memPrivateValue, "-");
            SetLabel(_memVideoValue, "-");
            SetLabel(_memObjectsValue, "-");
            SetLabel(_memNodesValue, "-");
            SetLabel(_gcCountValue, "GC Collections: Gen0 0 | Gen1 0 | Gen2 0");
            SetLabel(_memoryStatusLabel, "Waiting for memory packets...");
            RebuildComponentTree(null);
            RebuildComponentDetails(null, null);
            return;
        }

        SetLabel(_memCommittedValue, FormatBytes(snapshot.CommittedBytes));
        SetLabel(_memGcValue, FormatBytes(snapshot.GcBytes));
        SetLabel(_memEstimatedValue, FormatBytes(snapshot.EstimatedBytes));
        SetLabel(_memWorkingSetValue, FormatBytes(snapshot.WorkingSetBytes));
        SetLabel(_memPrivateValue, FormatBytes(snapshot.PrivateBytes));
        SetLabel(_memVideoValue, FormatBytes(snapshot.VideoBytes));
        SetLabel(_memObjectsValue, $"{snapshot.GodotObjects:N0}");
        SetLabel(_memNodesValue, $"{snapshot.GodotNodes:N0}");
        SetLabel(_gcCountValue, $"GC Collections: Gen0 {snapshot.Gen0} | Gen1 {snapshot.Gen1} | Gen2 {snapshot.Gen2}");

        var modeText = liveMode ? "Live" : "Snapshot";
        SetLabel(
            _memoryStatusLabel,
            $"{modeText} | Packets: {_memoryPacketCount:N0} | Snapshots: {_memorySnapshots.Count} | Last: {snapshot.TimestampUtc.ToLocalTime():HH:mm:ss}");

        var selectedComponentName = _componentTree?.GetSelected()?.GetText(0);
        RebuildComponentTree(snapshot);
        RebuildComponentDetails(snapshot, selectedComponentName);
    }

    private void RefreshSnapshotListSelection()
    {
        if (_snapshotList == null)
        {
            return;
        }

        _snapshotList.Clear();
        for (int i = 0; i < _memorySnapshots.Count; i++)
        {
            var snapshot = _memorySnapshots[i];
            var localTime = snapshot.TimestampUtc.ToLocalTime();
            var label = $"{localTime:HH:mm:ss}  {FormatBytes(snapshot.CommittedBytes)}";
            _snapshotList.AddItem(label);
        }

        if (_selectedMemorySnapshot != null)
        {
            int index = _memorySnapshots.IndexOf(_selectedMemorySnapshot);
            if (index >= 0)
            {
                _snapshotList.Select(index);
            }
        }
        else
        {
            _snapshotList.DeselectAll();
        }
    }

    private void RebuildComponentTree(MemorySnapshot? snapshot)
    {
        if (_componentTree == null)
        {
            return;
        }

        _componentTree.Clear();
        var root = _componentTree.CreateItem();
        if (snapshot == null)
        {
            return;
        }

        var filter = _memorySearchBox?.Text?.Trim() ?? string.Empty;
        var hasFilter = !string.IsNullOrEmpty(filter);

        foreach (var component in snapshot.Components)
        {
            if (hasFilter && !component.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var row = _componentTree.CreateItem(root);
            row.SetText(0, component.Name);
            row.SetText(1, $"{component.Count:N0}");
            row.SetText(2, FormatBytes(component.Bytes));
            row.SetText(3, $"{component.Percent:F1}%");

            var intensity = Mathf.Clamp(component.Percent / 35f, 0f, 1f);
            var rowColor = new Color(
                Mathf.Lerp(0.78f, 1f, intensity),
                Mathf.Lerp(0.78f, 0.64f, intensity),
                Mathf.Lerp(0.84f, 0.48f, intensity),
                1f);
            row.SetCustomColor(2, rowColor);
            row.SetCustomColor(3, rowColor);
        }
    }

    private void RebuildComponentDetails(MemorySnapshot? snapshot, string? selectedComponentName)
    {
        if (_componentDetails == null)
        {
            return;
        }

        _componentDetails.Clear();

        if (snapshot == null)
        {
            _componentDetails.Text = "No memory snapshot selected.";
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedComponentName))
        {
            _componentDetails.AppendText(
                $"[b]Summary[/b]\n" +
                $"Components listed: {snapshot.Components.Count}\n" +
                $"Estimated component memory: {FormatBytes(snapshot.EstimatedBytes)}\n" +
                $"Committed memory: {FormatBytes(snapshot.CommittedBytes)}");
            return;
        }

        MemoryComponentEntry? selected = null;
        foreach (var component in snapshot.Components)
        {
            if (string.Equals(component.Name, selectedComponentName, StringComparison.Ordinal))
            {
                selected = component;
                break;
            }
        }

        if (selected == null)
        {
            _componentDetails.AppendText("Selected component is no longer present in this snapshot.");
            return;
        }

        long perInstance = selected.Count > 0 ? selected.Bytes / selected.Count : 0;

        _componentDetails.AppendText(
            $"[b]{selected.Name}[/b]\n" +
            $"Instances: {selected.Count:N0}\n" +
            $"Estimated memory: {FormatBytes(selected.Bytes)} ({selected.Percent:F1}% of estimated)\n" +
            $"Per instance estimate: {FormatBytes(perInstance)}");
    }

    private void CaptureManualSnapshot()
    {
        if (_latestMemorySnapshot == null)
        {
            return;
        }

        var cloned = CloneSnapshot(_latestMemorySnapshot);
        AddSnapshot(cloned, keepLiveSelection: false);
        _selectedMemorySnapshot = cloned;
        if (_memoryLiveToggle != null)
        {
            _memoryLiveToggle.ButtonPressed = false;
        }
        _memoryUiDirty = true;
    }

    private void ClearSnapshots()
    {
        _memorySnapshots.Clear();
        _selectedMemorySnapshot = null;
        _lastAutoSnapshotUnix = -1;
        _snapshotList?.Clear();
        if (_memoryLiveToggle != null)
        {
            _memoryLiveToggle.ButtonPressed = true;
        }
        _memoryUiDirty = true;
    }

    private void AddSnapshot(MemorySnapshot snapshot, bool keepLiveSelection)
    {
        _memorySnapshots.Add(snapshot);

        if (_memorySnapshots.Count > MaxMemorySnapshots)
        {
            int removeCount = _memorySnapshots.Count - MaxMemorySnapshots;
            _memorySnapshots.RemoveRange(0, removeCount);
            if (_selectedMemorySnapshot != null && !_memorySnapshots.Contains(_selectedMemorySnapshot))
            {
                _selectedMemorySnapshot = null;
            }
        }

        if (!keepLiveSelection && _snapshotList != null)
        {
            _snapshotList.Select(_memorySnapshots.Count - 1);
        }

        _memoryUiDirty = true;
    }

    private void OnSnapshotSelected(long index)
    {
        if (index < 0 || index >= _memorySnapshots.Count)
        {
            return;
        }

        _selectedMemorySnapshot = _memorySnapshots[(int)index];
        if (_memoryLiveToggle != null)
        {
            _memoryLiveToggle.ButtonPressed = false;
        }

        _memoryUiDirty = true;
    }

    private void OnLiveToggled(bool pressed)
    {
        if (pressed)
        {
            _selectedMemorySnapshot = null;
            _snapshotList?.DeselectAll();
        }

        _memoryUiDirty = true;
    }

    private void OnComponentSelectionChanged()
    {
        _memoryUiDirty = true;
    }

    private static MemorySnapshot CloneSnapshot(MemorySnapshot source)
    {
        var clone = new MemorySnapshot
        {
            TimestampUtc = source.TimestampUtc,
            CommittedBytes = source.CommittedBytes,
            GcBytes = source.GcBytes,
            Gen0 = source.Gen0,
            Gen1 = source.Gen1,
            Gen2 = source.Gen2,
            EstimatedBytes = source.EstimatedBytes,
            WorkingSetBytes = source.WorkingSetBytes,
            PrivateBytes = source.PrivateBytes,
            VideoBytes = source.VideoBytes,
            GodotObjects = source.GodotObjects,
            GodotNodes = source.GodotNodes,
            Components = new List<MemoryComponentEntry>(source.Components.Count)
        };

        foreach (var component in source.Components)
        {
            clone.Components.Add(new MemoryComponentEntry
            {
                Name = component.Name,
                Count = component.Count,
                Bytes = component.Bytes,
                Percent = component.Percent
            });
        }

        return clone;
    }

    private static bool TryParseMemoryPacket(string[] parts, out MemorySnapshot snapshot)
    {
        snapshot = null!;

        // New format:
        // MEM|committed|gc|gen0|gen1|gen2|estimated|workingSet|private|video|objects|nodes|components
        if (parts.Length >= 13)
        {
            if (!TryLong(parts[1], out var committedBytes)) return false;
            if (!TryLong(parts[2], out var gcBytes)) return false;
            if (!TryInt(parts[3], out var gen0)) return false;
            if (!TryInt(parts[4], out var gen1)) return false;
            if (!TryInt(parts[5], out var gen2)) return false;
            if (!TryLong(parts[6], out var estimatedBytes)) return false;
            if (!TryLong(parts[7], out var workingSetBytes)) return false;
            if (!TryLong(parts[8], out var privateBytes)) return false;
            if (!TryLong(parts[9], out var videoBytes)) return false;
            if (!TryInt(parts[10], out var godotObjects)) return false;
            if (!TryInt(parts[11], out var godotNodes)) return false;

            var components = ParseMemoryComponents(parts[12], estimatedBytes);

            snapshot = new MemorySnapshot
            {
                TimestampUtc = DateTime.UtcNow,
                CommittedBytes = committedBytes,
                GcBytes = gcBytes,
                Gen0 = gen0,
                Gen1 = gen1,
                Gen2 = gen2,
                EstimatedBytes = estimatedBytes,
                WorkingSetBytes = workingSetBytes,
                PrivateBytes = privateBytes,
                VideoBytes = videoBytes,
                GodotObjects = godotObjects,
                GodotNodes = godotNodes,
                Components = components
            };
            return true;
        }

        // Legacy format:
        // MEM|totalEstimated|gcBytes|gen0|gen1|gen2|name:count:bytes,...
        if (parts.Length >= 7)
        {
            if (!TryLong(parts[1], out var totalEstimated)) return false;
            if (!TryLong(parts[2], out var gcLegacy)) return false;
            if (!TryInt(parts[3], out var legacyGen0)) return false;
            if (!TryInt(parts[4], out var legacyGen1)) return false;
            if (!TryInt(parts[5], out var legacyGen2)) return false;

            var legacyComponents = ParseMemoryComponents(parts[6], totalEstimated);

            snapshot = new MemorySnapshot
            {
                TimestampUtc = DateTime.UtcNow,
                CommittedBytes = gcLegacy,
                GcBytes = gcLegacy,
                Gen0 = legacyGen0,
                Gen1 = legacyGen1,
                Gen2 = legacyGen2,
                EstimatedBytes = totalEstimated,
                WorkingSetBytes = 0,
                PrivateBytes = 0,
                VideoBytes = 0,
                GodotObjects = 0,
                GodotNodes = 0,
                Components = legacyComponents
            };
            return true;
        }

        return false;
    }

    private static List<MemoryComponentEntry> ParseMemoryComponents(string raw, long totalEstimated)
    {
        var list = new List<MemoryComponentEntry>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return list;
        }

        var entries = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var parts = entry.Split(':', 3);
            if (parts.Length != 3)
            {
                continue;
            }

            var name = parts[0].Trim();
            if (!TryInt(parts[1], out var count)) continue;
            if (!TryLong(parts[2], out var bytes)) continue;

            float pct = totalEstimated > 0 ? (bytes * 100f / totalEstimated) : 0f;

            list.Add(new MemoryComponentEntry
            {
                Name = name,
                Count = count,
                Bytes = bytes,
                Percent = pct
            });
        }

        list.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return list;
    }

    private void UpdateStatusLabel(int total)
    {
        if (_statusLabel == null)
        {
            return;
        }

        var connectionState = _secondsSincePacket <= ConnectionTimeoutSec ? "Connected" : "Waiting";
        _statusLabel.Text =
            $"{total} messages | Showing: {_lastShownLogCount} | UDP: {connectionState} | Packets: {_packetCount}";
    }

    private static (Color color, string tag) GetLevelStyle(LogLevel level)
    {
        return level switch
        {
            LogLevel.LOG => (new Color(0.6f, 0.8f, 0.6f), "LOG"),
            LogLevel.WARN => (new Color(1f, 0.85f, 0.2f), "WRN"),
            LogLevel.ERROR => (new Color(1f, 0.3f, 0.3f), "ERR"),
            LogLevel.DEBUG => (new Color(0.5f, 0.5f, 0.55f), "DBG"),
            _ => (new Color(0.7f, 0.7f, 0.7f), "???")
        };
    }

    private static bool TryFloat(string value, out float parsed)
    {
        return float.TryParse(
            value,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture,
            out parsed);
    }

    private static bool TryInt(string value, out int parsed)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool TryLong(string value, out long parsed)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static void SetLabel(Label? label, string text, Color? color = null)
    {
        if (label == null)
        {
            return;
        }

        label.Text = text;
        if (color.HasValue)
        {
            label.AddThemeColorOverride("font_color", color.Value);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

