// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Lumora.Core;
using Lumora.Core.Networking.Streams;
using Lumora.Core.Networking.Sync;
using Lumora.Nexus.Protocol;
using Lumora.Nexus.Transport;
using LegacyJoinGrantData = Lumora.Core.Networking.Messages.JoinGrantData;
using LegacyJoinRequestData = Lumora.Core.Networking.Messages.JoinRequestData;
using LegacyJoinRejectData = Lumora.Core.Networking.Messages.JoinRejectData;
using LegacyJoinChallengeData = Lumora.Core.Networking.Messages.JoinChallengeData;
using LegacyJoinAuthenticateData = Lumora.Core.Networking.Messages.JoinAuthenticateData;
using LumoraLogger = Lumora.Core.Logging.Logger;
// Aliased rather than a blanket System.IO import: this file also uses Lumora.Core.Networking.Streams.Stream
// and the two Stream types collide.
using MemoryStream = System.IO.MemoryStream;
using BinaryReader = System.IO.BinaryReader;
using BinaryWriter = System.IO.BinaryWriter;

namespace Lumora.Core.Networking.Session;

// Three dedicated threads: decode, encode, sync.
public class SessionSyncManager : IDisposable
{
    public enum SyncLoopStage
    {
        WaitingForSyncThreadEvent,
        RunningMessageProcessing,
        ExitedMessageProcessing,
        ProcessingStopped,
        ContinuingAfterRefreshFinished,
        GeneratingDeltaBatch,
        GeneratingCorrections,
        EncodingStreams,
        FinishingSyncCycle,
        ProcessingControlMessages,
        InitializingNewUsers,
        Finished
    }

    private Thread _decodeThread = null!;
    private Thread _encodeThread = null!;
    private Thread _syncThread = null!;

    private readonly AutoResetEvent _decodeThreadEvent = new(false);
    private readonly AutoResetEvent _encodeThreadEvent = new(false);
    private readonly AutoResetEvent _syncThreadEvent = new(false);
    private readonly ManualResetEvent _syncThreadInitEvent = new(false);
    private readonly ManualResetEvent _refreshFinished = new(false);
    private readonly ManualResetEvent _lastProcessingStopped = new(false);

    private readonly ConcurrentQueue<RawInMessage> _rawInMessages = new();
    private readonly ConcurrentQueue<SyncMessage> _messagesToProcess = new();
    private readonly ConcurrentQueue<SyncMessage> _messagesToTransmit = new();
    // A LinkedList and not a Queue because the replay path has to be able to STOP at the head and leave
    // the rest of the backlog untouched: order is the only thing that makes replaying ops correct, so a
    // batch we can't apply yet must stay exactly where it is rather than going round to the back.
    private readonly LinkedList<DeltaBatch> _pendingDeltaBatches = new();

    // Reused every sync cycle for the released-drive corrections and stream gathering so we don't
    // allocate a fresh list per tick. SYNC-THREAD ONLY: safe only while the sync cycle runs serially on
    // one thread - if encode is ever parallelized, re-audit these for aliasing. -xlinka
    private readonly List<SyncElement> _releasedDrivesScratch = new();
    private readonly List<StreamMessage> _streamsScratch = new();
    private readonly Queue<StreamMessage> _pendingStreamMessages = new();
    // Deltas are reliable OPS, not values: dropping one leaves this peer permanently wrong about
    // whatever it touched, with nothing to notice it. There is no batch count at which discarding is
    // the right answer, so the backlog is accounted in BYTES - which is what a backlog actually costs -
    // and the ceiling does not drop, it abandons the whole backlog and rebuilds from full state. One
    // expensive round trip beats a world that is quietly incorrect, and unlike a drop it announces
    // itself. -xlinka
    private const long MaxPendingDeltaBytes = 64L * 1024 * 1024;
    // Replay chunk per sync cycle. The sync cycle is gated on the world's refresh signal, so a
    // backgrounded world throttled to 10 Hz drains at 10 x this - still several times the rate the
    // authority can generate deltas, so a backgrounded world catches up instead of falling behind.
    private const int PendingDeltaDrainChunk = 512;
    // Streams are unreliable latest-value poses: a stale one is worthless, so this cap really can drop.
    private const int MaxPendingStreamMessages = 512;

    private bool _running;
    private bool _isDisposed;
    private bool _acceptDeltas;
    private long _pendingDeltaBytes;

    // Per-link delta sequencing. The authority fans out to many peers and keeps one cursor per
    // connection; a client has the single host link. Written from the sync thread and cleared from the
    // transport's disconnect callback, hence the lock. A reconnect arrives as a brand new IConnection
    // and therefore a brand new cursor, which is what forces it to start from full state instead of
    // resuming a run whose middle it missed while it was gone. -xlinka
    private readonly Dictionary<IConnection, LinkSequenceState> _linkSequences = new();
    private readonly object _linkLock = new();
    private ulong _outSequence;

    // Client side: set the moment a gap is seen, cleared when full world state lands. While set, every
    // delta is queued instead of applied - a delta held is recoverable, a delta applied over a hole is
    // not.
    private bool _desynced;
    private ulong _lastFullStateRequestTick;
    private const int FullStateRequestCooldownTicks = 120;

    // Elements whose incremental delta didn't line up with what we hold, and the tick we noticed. Their
    // deltas are skipped until the authority sends a full record back, because every one of them is
    // computed against a base we demonstrably don't have. The tick is there so an element the authority
    // no longer has can't blackhole its own deltas forever. -xlinka
    private readonly Dictionary<RefID, ulong> _elementsAwaitingResync = new();
    private readonly List<RefID> _resyncRequestQueue = new();
    private const int MaxResyncRefIDsPerMessage = 256;
    private const int ElementResyncTimeoutTicks = 600;

    // An unconfirmed change set whose confirmation never comes back (the authority refused the batch
    // while we were resyncing) would otherwise sit in _changesToConfirm forever.
    private const int ChangeConfirmationMaxAgeTicks = 1200;

    private int _expectedComponents = 0;
    private int _receivedComponents = 0;
    private int _initializedComponents = 0;
    private readonly HashSet<RefID> _receivedComponentIds = new();
    private readonly object _progressLock = new();
    private bool _initialFullBatchReceived = false;

    // Initial-state completeness gate: the client only enters the world
    // (State -> Running) once the initial full batch has FULLY decoded - instead of flipping the
    // moment the JoinStartDelta control message lands and silently dropping whatever hadn't decoded
    // yet. We remember that JoinStartDelta arrived, keep retrying pending records, and transition the
    // instant nothing is left unapplied. If it genuinely gets stuck (an orphaned field record whose
    // owning slot/component never materialized) we log EXACTLY what's missing and enter anyway, so a
    // join can't silently brick - it either lands a complete world or tells you precisely what it
    // couldn't decode. -xlinka
    private bool _joinStartDeltaSeen = false;
    private int _lastInitialPendingCount = -1;
    private ulong _lastInitialProgressTick = 0;
    private bool _loggedInitialIncomplete = false;
    private int _fullStateRequestBudget = 1;            // re-request the full state this many times if stuck
    private const int InitialStateMaxAgeTicks = 2000;   // retry far longer than the live delta path
    private const int InitialStateStuckTicks = 120;     // no progress this long -> recover, then enter anyway
                                                        // (kept short so a stuck join becomes usable in
                                                        // a couple seconds instead of stalling for ~30)

    // Client-side change confirmations (sync tick -> RefIDs)
    private readonly Dictionary<ulong, HashSet<RefID>> _changesToConfirm = new();

    // Pending data records that couldn't decode yet (missing elements)
    private readonly Dictionary<RefID, PendingRecord> _pendingFullRecords = new();
    private readonly Dictionary<RefID, PendingRecord> _pendingDeltaRecords = new();
    private readonly object _pendingLock = new();
    private const int PendingRecordMaxAttempts = 20;
    private const int PendingRecordMaxAgeTicks = 400;
    private readonly List<User> _newUsersToInitialize = new();
    private readonly object _newUsersLock = new();

    // Local user pending initialization (wait for sync-created User)
    internal RefID LocalUserRefIDToInit { get; private set; }
    private string? _pendingLocalUserName;
    private ulong _pendingAllocationStart;
    private ulong _pendingAllocationEnd;

    public SyncLoopStage DEBUG_SyncLoopStage { get; private set; }
    public SyncMessage ProcessingSyncMessage { get; private set; } = null!;

    public int TotalProcessedMessages { get; private set; }
    public int TotalReceivedDeltas { get; private set; }
    public int TotalReceivedFulls { get; private set; }
    public int TotalReceivedStreams { get; private set; }
    public int TotalSentDeltas { get; private set; }
    public int TotalSentFulls { get; private set; }
    public int TotalSentStreams { get; private set; }
    public int TotalReceivedRawFrames { get; private set; }
    public int TotalSentRawFrames { get; private set; }
    public int TotalCorrections { get; private set; }
    public int LastGeneratedDeltaChanges { get; private set; }

    public int TotalDesyncs { get; private set; }
    public int TotalDuplicateDeltas { get; private set; }
    public int TotalListMismatches { get; private set; }
    public int TotalTargetedResyncs { get; private set; }
    public bool IsDesynced => _desynced;

    // Live queue depths for diagnostics (the Network debug tab). Reads of ConcurrentQueue.Count are safe
    // from any thread; the plain Queue count is a benign racy read used for display only.
    public int MessagesToProcessCount => _messagesToProcess.Count;
    public int MessagesToTransmitCount => _messagesToTransmit.Count;
    public int IncomingRawCount => _rawInMessages.Count;
    public int PendingStreamCount => _pendingStreamMessages.Count;
    public int PendingDeltaCount => _pendingDeltaBatches.Count;
    public long PendingDeltaBytes => _pendingDeltaBytes;

    public Session Session { get; private set; }
    public World World => (Session?.World) ?? null!;

    // Sync send/process rate in Hz. Owned by the SESSION (WorldSettings.NetworkTickRate, host-only), not
    // by the local machine: the host sets it and it replicates, so every peer's loop runs at the same
    // rate. Cached in a volatile int because the sync loop reads it off-thread before it takes the data
    // model lock - resolving the component from there would race a main-thread attach. -xlinka
    private volatile int _syncRate = WorldSettings.DefaultTickRate;
    // How long one cycle may hold the data model lock applying incoming messages before letting the
    // world thread have it back. Eight milliseconds keeps a joining client rendering at a playable rate
    // while the session streams in, instead of freezing until it is done. The minimum bite stops a
    // trickle of tiny deltas from paying for a timestamp each. -xlinka
    private const double MessageDrainBudgetMs = 8.0;
    private const int MessageDrainMinBite = 4;

    public int SyncRate => _syncRate;

    internal void SetSyncRate(int rate)
    {
        _syncRate = System.Math.Clamp(rate, WorldSettings.MinTickRate, WorldSettings.MaxTickRate);
    }

    public SessionSyncManager(Session session)
    {
        Session = session;
    }

    public void Start()
    {
        if (_syncThread != null)
            throw new InvalidOperationException("Sync threads already started");

        // Seed from the world's configured rate before the loop exists. WorldSettings.OnStart pushes it
        // too, but a world whose settings started before the session did would otherwise leave the loop
        // on the default until the host next touched the slider. Main thread here, so the plain
        // GetComponent is safe. -xlinka
        int configured = World?.RootSlot?.GetComponent<WorldSettings>()?.NetworkTickRate.Value ?? 0;
        if (configured > 0)
            SetSyncRate(configured);

        _running = true;

        _decodeThread = new Thread(DecodeLoop)
        {
            Name = "SessionDecodeThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };

        _encodeThread = new Thread(EncodeLoop)
        {
            Name = "SessionEncodeThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };

        _syncThread = new Thread(SyncLoop)
        {
            Name = "SessionSyncThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };

        _decodeThread.Start();
        _encodeThread.Start();
        _syncThread.Start();

        // Wait for sync thread to initialize - BOUNDED so a stuck/failed sync thread can never permanently block
        // the caller (the join handshake). The sync thread sets this almost immediately at the top of SyncLoop;
        // 10s is purely a safety net. -xlinka
        if (!_syncThreadInitEvent.WaitOne(10000))
        {
            LumoraLogger.Error("[Sync] SessionSyncManager: sync thread did not initialize within 10s - join may fail");
        }

        LumoraLogger.Log("[Sync] SessionSyncManager: All threads started");
    }

    // Called from main thread after World.Update(). Allows the sync thread to advance past the refresh wait.
    public void SignalRefreshFinished()
    {
        _refreshFinished.Set();
    }

    // Sync thread parks here while the main thread runs World.Update().
    public ManualResetEvent ProcessingStopped => _lastProcessingStopped;

    public WaitHandle RefreshFinished => _refreshFinished;

    public void StopProcessing()
    {
        _syncThreadEvent.Set();
    }

    public void QueueRawIncoming(RawInMessage message)
    {
        _rawInMessages.Enqueue(message);
        _decodeThreadEvent.Set();
    }

    public void EnqueueForTransmission(SyncMessage message)
    {
        _messagesToTransmit.Enqueue(message);
        _encodeThreadEvent.Set();
    }

    private void EnqueueForProcessing(SyncMessage message)
    {
        _messagesToProcess.Enqueue(message);
        _syncThreadEvent.Set();
    }

    public void QueueUserForInitialization(User user)
    {
        LumoraLogger.Log($"[Sync] QueueUserForInitialization: Queuing user '{user.UserName.Value}' (RefID: {user.ReferenceID})");
        // Hold off all live fan-out to this user until its full state is on the wire (re-enabled once
        // initialization finishes). The flag already defaults false, but a re-init of an existing user needs the reset. -xlinka
        user.StopTransmittingStreamData();
        lock (_newUsersLock)
        {
            _newUsersToInitialize.Add(user);
            LumoraLogger.Log($"[Sync] QueueUserForInitialization: Queue now has {_newUsersToInitialize.Count} users");
        }
    }

    // DECODE THREAD

    private void DecodeLoop()
    {
        LumoraLogger.Log("[Sync] DecodeLoop started");

        while (_running && !_isDisposed)
        {
            _decodeThreadEvent.WaitOne();

            if (_isDisposed) break;

            while (_rawInMessages.TryDequeue(out var rawMessage))
            {
                try
                {
                    var syncMessage = SyncMessage.Decode(rawMessage);

                    switch (syncMessage)
                    {
                        case DeltaBatch: TotalReceivedDeltas++; break;
                        case FullBatch: TotalReceivedFulls++; break;
                        case StreamMessage: TotalReceivedStreams++; break;
                        case RawFrameMessage: TotalReceivedRawFrames++; break;
                    }

                    EnqueueForProcessing(syncMessage);
                }
                catch (Exception ex)
                {
                    LumoraLogger.Error($"[Sync] DecodeLoop: Exception decoding message: {ex.Message}");
                }
            }
        }

        LumoraLogger.Log("[Sync] DecodeLoop stopped");
    }

    // ENCODE THREAD

    private void EncodeLoop()
    {
        LumoraLogger.Log("[Sync] EncodeLoop started");

        while (_running && !_isDisposed)
        {
            _encodeThreadEvent.WaitOne();

            if (_isDisposed) break;

            while (_messagesToTransmit.TryDequeue(out var message))
            {
                if (message.Targets.Count == 0)
                {
                    message.Dispose();
                    continue;
                }

                try
                {
                    switch (message)
                    {
                        case DeltaBatch: TotalSentDeltas++; break;
                        case FullBatch: TotalSentFulls++; break;
                        case StreamMessage: TotalSentStreams++; break;
                        case RawFrameMessage: TotalSentRawFrames++; break;
                    }

                    // Encode and transmit. Compress the encoded frame when the message opts
                    // in (Delta/Full/Stream) and it's actually big enough to be worth it - the
                    // codec returns the frame unchanged if it wouldn't shrink, and the receiver
                    // unwraps transparently. - xlinka
                    var encoded = message.Encode();
                    int threshold = message.CompressionThreshold;
                    if (threshold > 0 && encoded.Length >= threshold)
                        encoded = SyncFrameCodec.WrapCompressed(encoded);
                    Session.Connections.Broadcast(encoded, message.Targets, message.Reliable);
                }
                catch (Exception ex)
                {
                    LumoraLogger.Error($"[Sync] EncodeLoop: Exception encoding message: {ex.Message}");
                }
                finally
                {
                    message.Dispose();
                }
            }
        }

        LumoraLogger.Log("[Sync] EncodeLoop stopped");
    }

    // SYNC THREAD

    private void SyncLoop()
    {
        LumoraLogger.Log("[Sync] SyncLoop started");

        var controlMessagesToProcess = new List<ControlMessage>();
        ulong lastDeltaSyncTime = 0;

        _syncThreadInitEvent.Set();

        while (_running && !_isDisposed)
        {
            bool lockHeld = false;
            try
            {
                DEBUG_SyncLoopStage = SyncLoopStage.WaitingForSyncThreadEvent;

                if (_messagesToProcess.IsEmpty)
                {
                    _syncThreadEvent.WaitOne(1000 / SyncRate);
                }

                if (_isDisposed) break;

                World.HookManager?.DataModelLock(Thread.CurrentThread);
                lockHeld = true;

                DEBUG_SyncLoopStage = SyncLoopStage.RunningMessageProcessing;

                // Peek-then-conditional-dequeue: we only remove a message from the queue once it's been
                // accepted. ProcessMessage returns false to DEFER (currently only the authority
                // delta-staleness guard does this) - we leave that message at the front and stop draining
                // this cycle, so it gets reprocessed next cycle after our StateVersion has advanced.
                // Dequeuing only on success is what makes that requeue free. -xlinka
                // THE JOIN HITCH LIVES HERE.
                //
                // This drain used to run until the queue was empty, and it runs while holding the data
                // model lock - which is Monitor.Enter on the SAME object World.Update takes as the
                // implementer lock (HookManager.cs:64 and :83). So the main thread is not slow during a
                // join, it is PARKED: it asks for the lock and waits however long it takes this thread to
                // apply the entire world. Backlog replay right below has been bounded per cycle since it
                // was written; this loop never was.
                //
                // Bounded by TIME, and the lock is dropped between chunks so the world thread gets to run
                // frames while the rest of the session streams in. The peek-then-dequeue pairing already
                // supports stopping mid-drain (that is how a deferred message stays at the front), so
                // stopping on budget costs nothing and loses nothing: whatever is left is still queued and
                // is picked up on the very next cycle. -xlinka
                long drainStart = System.Diagnostics.Stopwatch.GetTimestamp();
                double drainTicksToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                int drained = 0;

                while (_messagesToProcess.TryPeek(out var message))
                {
                    ProcessingSyncMessage = message;

                    if (!ProcessMessage(message, lastDeltaSyncTime, controlMessagesToProcess))
                    {
                        // Deferred - leave it at the front, stop draining. (If anyone adds another
                        // `return false` to ProcessMessage, it stalls the drain here: false means defer.)
                        ProcessingSyncMessage = null!;
                        break;
                    }

                    ProcessingSyncMessage = null!;
                    TotalProcessedMessages++;

                    _messagesToProcess.TryDequeue(out var dequeued);
                    if (!ReferenceEquals(message, dequeued))
                    {
                        // Only this thread dequeues from the front; a changed front means something else
                        // is racing the queue and our peek/process pairing is unsound - fail loud. -xlinka
                        throw new InvalidOperationException("Sync message queue front was modified outside the sync loop");
                    }

                    // A minimum bite so a steady trickle of small deltas never pays the cost of a clock
                    // read per message, and so progress is guaranteed even if one message alone is over
                    // budget (a single huge FullBatch still applies whole; it cannot be split here).
                    drained++;
                    if (drained < MessageDrainMinBite)
                        continue;
                    if ((System.Diagnostics.Stopwatch.GetTimestamp() - drainStart) * drainTicksToMs >= MessageDrainBudgetMs)
                        break;
                }

                DEBUG_SyncLoopStage = SyncLoopStage.ExitedMessageProcessing;

                // Backlog replay, bounded per cycle. Nothing here ever discards to make room - the only
                // way a queued delta dies is being superseded by a tagged full batch or the byte
                // ceiling deliberately blowing the whole backlog away.
                DrainPendingDeltas(PendingDeltaDrainChunk);
                ExpireStaleElementResyncs();

                ProcessPendingRecords();

                // Drive the join completeness gate every tick (not only when records are pending), so a
                // client awaiting initial state always advances toward Running and can never hang -
                // ProcessPendingRecords early-returns when nothing is pending. Self-guards on state.
                // -xlinka
                TryEnterRunningWhenComplete();

                if (World.IsAuthority)
                {
                    World.IncrementStateVersion();
                }

                World.HookManager?.DataModelUnlock();
                lockHeld = false;
                _lastProcessingStopped.Set();

                DEBUG_SyncLoopStage = SyncLoopStage.ProcessingStopped;

                if (!_refreshFinished.WaitOne(1000))
                {
                    if (_newUsersToInitialize.Count > 0)
                    {
                        LumoraLogger.Warn("[Sync] SyncLoop: Timeout waiting for world refresh, continuing anyway");
                    }
                }

                if (_isDisposed) break;

                _lastProcessingStopped.Reset();
                _refreshFinished.Reset();

                DEBUG_SyncLoopStage = SyncLoopStage.ContinuingAfterRefreshFinished;

                World.HookManager?.DataModelLock(Thread.CurrentThread);
                lockHeld = true;

                DEBUG_SyncLoopStage = SyncLoopStage.GeneratingDeltaBatch;

                // Null on an idle tick - nothing was dirty, so no batch was built at all.
                var deltaBatch = World.SyncController.CollectDeltaMessages();
                LastGeneratedDeltaChanges = deltaBatch?.DataRecordCount ?? 0;

                if (deltaBatch != null && deltaBatch.DataRecordCount > 0)
                {
                    if (World.IsAuthority)
                    {
                        AddAuthorityFanoutTargets(deltaBatch);
                    }
                    else
                    {
                        var hostConnection = Session.Connections.HostConnection;
                        if (hostConnection != null)
                        {
                            deltaBatch.Targets.Add(hostConnection);
                        }
                    }

                    if (deltaBatch.Targets.Count > 0)
                    {
                        if (!World.IsAuthority)
                        {
                            var confirmTime = deltaBatch.SenderSyncTick;
                            if (!_changesToConfirm.TryGetValue(confirmTime, out var ids))
                            {
                                ids = new HashSet<RefID>();
                                _changesToConfirm[confirmTime] = ids;
                            }

                            for (int i = 0; i < deltaBatch.DataRecordCount; i++)
                            {
                                ids.Add(deltaBatch.GetDataRecord(i).TargetID);
                            }
                        }

                        // Consume this link stream's next number. Every live peer is a target of this
                        // message, so one counter keeps every one of their runs unbroken; the single
                        // fan-out that does NOT reach everybody is the relay below, which pays for the
                        // number it takes by carrying it to the excluded peer in that peer's
                        // confirmation. -xlinka
                        deltaBatch.LinkSequence = NextOutSequence();
                        EnqueueForTransmission(deltaBatch);
                    }
                    else
                    {
                        deltaBatch.Dispose();
                    }
                }
                else
                {
                    deltaBatch?.Dispose();
                }

                DEBUG_SyncLoopStage = SyncLoopStage.GeneratingCorrections;

                // A field that was being driven suppresses its deltas while the drive is active, so once
                // the drive is released peers are stuck on the last value the drive pushed. The authority
                // collects those just-freed fields here and re-sends their real current value as a small
                // full-state batch so everyone snaps back to the truth. -xlinka
                if (World.IsAuthority)
                {
                    var released = _releasedDrivesScratch;
                    released.Clear();
                    World.LinkManager?.GetReleasedDrives(released);

                    if (released.Count > 0)
                    {
                        // Partial full batch: LinkSequence stays 0 so it repairs the records it
                        // carries without moving anyone's cursor past deltas still in flight for
                        // everything else.
                        var corrections = World.SyncController.EncodeFullBatch(released);

                        AddAuthorityFanoutTargets(corrections);

                        if (corrections.Targets.Count > 0)
                        {
                            TotalCorrections += released.Count;
                            EnqueueForTransmission(corrections);
                        }
                        else
                        {
                            corrections.Dispose();
                        }
                    }
                }

                DEBUG_SyncLoopStage = SyncLoopStage.EncodingStreams;

                if (World.State == World.WorldState.Running)
                {
                    var streams = _streamsScratch;
                    streams.Clear();
                    World.SyncController.GatherStreams(streams);

                    int sentCount = 0;
                    foreach (var stream in streams)
                    {
                        AddStreamTargets(stream, excludeSender: false);
                        if (stream.Targets.Count > 0)
                        {
                            EnqueueForTransmission(stream);
                            sentCount++;
                        }
                        else
                        {
                            stream.Dispose();
                        }
                    }

                    // Log stream transmission summary periodically (every 60 ticks = ~1 sec at 60 fps)
                    if (streams.Count > 0 && World.SyncTick % 60 == 0)
                    {
                        LumoraLogger.Debug($"[Sync] [Stream] Gathered {streams.Count} messages, sent {sentCount} (LocalUser streams: {World.LocalUser?.StreamCount ?? 0})");
                    }
                }

                DEBUG_SyncLoopStage = SyncLoopStage.FinishingSyncCycle;

                World.LocalUser?.ClearJustAddedStreams();

                lastDeltaSyncTime = World.StateVersion;
                World.IncrementSyncTick();

                DEBUG_SyncLoopStage = SyncLoopStage.ProcessingControlMessages;

                foreach (var controlMessage in controlMessagesToProcess)
                {
                    ProcessControlMessage(controlMessage);
                    controlMessage.Dispose();
                }
                controlMessagesToProcess.Clear();

                SendClockProbe();
                SendPendingResyncRequests();
                PruneStaleChangeConfirmations();

                // Keep a deferred join advancing - OnFullStateReceived may have held Running back waiting for
                // the local user, and every sync cycle is a chance to claim it. Cheap + idempotent. -xlinka
                World.PumpJoinProgress();

                DEBUG_SyncLoopStage = SyncLoopStage.InitializingNewUsers;

                if (World.IsAuthority)
                {
                    List<User> usersToInit = null!;
                    lock (_newUsersLock)
                    {
                        if (_newUsersToInitialize.Count > 0)
                        {
                            usersToInit = new List<User>(_newUsersToInitialize);
                            _newUsersToInitialize.Clear();
                        }
                    }

                    if (usersToInit != null && usersToInit.Count > 0)
                    {
                        LumoraLogger.Log($"[Sync] Stage 8: Encoding FullBatch for {usersToInit.Count} new users");
                        var fullBatch = World.SyncController.EncodeFullBatch();
                        // Tag, not a consumed number: this snapshot supersedes every delta issued so
                        // far, so the joiner's cursor starts here and the first live delta it sees is
                        // the next one we send.
                        fullBatch.LinkSequence = _outSequence;
                        LumoraLogger.Log($"[Sync] Stage 8: FullBatch has {fullBatch.DataRecordCount} records at sequence {fullBatch.LinkSequence}");

                        foreach (var user in usersToInit)
                        {
                            if (Session.Connections.TryGetConnection(user, out var connection))
                            {
                                LumoraLogger.Log($"[Sync] Stage 8: Adding target connection for user '{user.UserName.Value}'");
                                fullBatch.Targets.Add(connection);
                            }
                            else
                            {
                                LumoraLogger.Warn($"[Sync] Stage 8: No connection found for user '{user.UserName.Value}'");
                            }
                        }

                        LumoraLogger.Log($"[Sync] Stage 8: Enqueueing FullBatch with {fullBatch.Targets.Count} targets");
                        EnqueueForTransmission(fullBatch);

                        var startDeltaMessage = new ControlMessage(ControlMessage.Message.JoinStartDelta);
                        foreach (var user in usersToInit)
                        {
                            if (Session.Connections.TryGetConnection(user, out var connection))
                            {
                                startDeltaMessage.Targets.Add(connection);
                                // Full state + the "deltas may flow now" signal are on the wire for this
                                // user, so open its gate. The very next sync tick's fan-out will start
                                // including it. Order matters: full batch enqueued, then JoinStartDelta,
                                // then gate open - so live traffic only ever trails full state. -xlinka
                                user.StartTransmittingStreamData();
                            }
                        }
                        LumoraLogger.Log($"[Sync] Stage 8: Enqueueing JoinStartDelta with {startDeltaMessage.Targets.Count} targets");
                        EnqueueForTransmission(startDeltaMessage);
                    }
                }

                DEBUG_SyncLoopStage = SyncLoopStage.Finished;
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"[Sync] SyncLoop: Exception: {ex.Message}\n{ex.StackTrace}");
                if (World.HookManager?.Lock == HookManager.LockOwner.DataModel)
                {
                    World.HookManager?.DataModelUnlock();
                }
            }
            finally
            {
                if (lockHeld)
                {
                    World.HookManager?.DataModelUnlock();
                }
            }
        }

        LumoraLogger.Log("[Sync] SyncLoop stopped");
    }

    private bool ProcessMessage(SyncMessage msg, ulong lastDeltaSyncTime, List<ControlMessage> controlMessagesToProcess)
    {
        try
        {
            if (msg is not ControlMessage)
            {
                if (World.IsAuthority)
                {
                    if (!Session.Connections.TryGetUser(msg.Sender, out var user))
                    {
                        msg.Dispose();
                        return true;
                    }
                    msg.LinkSenderUser(user);
                }
            }

            switch (msg)
            {
                case DeltaBatch deltaBatch:
                    if (World.State != World.WorldState.Running)
                    {
                        EnqueuePendingDelta(deltaBatch, $"world state {World.State}");
                        break;
                    }

                    // Authority defers a delta whose sender is at-or-ahead of where our state was at the
                    // last sync-cycle boundary. Returning false (NOT disposing) leaves it at the queue
                    // front; next cycle our StateVersion has advanced and lastDeltaSyncTime is re-captured,
                    // so validate/retransmit runs against strictly-newer authority state. This is the ONLY
                    // place ProcessMessage returns false - the drain treats false as "defer". -xlinka
                    if (World.IsAuthority && deltaBatch.SenderStateVersion >= lastDeltaSyncTime)
                    {
                        return false;
                    }

                    if (!World.IsAuthority && !_acceptDeltas)
                    {
                        EnqueuePendingDelta(deltaBatch, "JoinStartDelta not received yet");
                        break;
                    }

                    if (!World.IsAuthority && _desynced)
                    {
                        // Hole in front of this batch. Hold it in arrival order; the tagged full batch
                        // decides which of the backlog is still worth replaying.
                        EnqueuePendingDelta(deltaBatch, "awaiting full-state resync");
                        break;
                    }

                    ApplySequencedDelta(deltaBatch);
                    break;

                case FullBatch fullBatch:
                    if (!World.IsAuthority && World.InitState == World.InitializationState.InitializingDataModel)
                    {
                        TrackFullBatchProgress(fullBatch);
                    }

                    ApplyDataRecords(fullBatch);

                    if (!World.IsAuthority)
                    {
                        World.SetStateVersion(fullBatch.SenderStateVersion);
                        NoteFullStateApplied(fullBatch);
                        // Transition happens on JoinStartDelta to avoid starting before deltas are allowed.
                    }

                    fullBatch.Dispose();
                    break;

                case ConfirmationMessage confirmation:
                    NoteLinkSequence(confirmation);

                    for (int i = 0; i < confirmation.DataRecordCount; i++)
                    {
                        World.SyncController.DecodeCorrection(i, confirmation);
                    }

                    if (_changesToConfirm.TryGetValue(confirmation.ConfirmTime, out var confirmedIds))
                    {
                        World.SyncController.ApplyConfirmations(confirmedIds, confirmation.ConfirmTime);

                        foreach (var id in confirmedIds)
                        {
                            World.ReferenceController?.DeleteFromTrash(id);
                        }

                        _changesToConfirm.Remove(confirmation.ConfirmTime);
                    }
                    else
                    {
                        LumoraLogger.Debug($"[Sync] Confirmation received for unknown tick {confirmation.ConfirmTime}");
                    }

                    if (!World.IsAuthority)
                    {
                        World.SetStateVersion(confirmation.SenderStateVersion);
                    }

                    confirmation.Dispose();
                    break;

                case StreamMessage streamMessage:
                    if (World.IsAuthority)
                    {
                        // Authority sees the original sender connection (no relay). A peer
                        // can put any UserID in the StreamMessage, so we must reject the
                        // message - and refuse to relay it - when the claimed UserID does
                        // not belong to the connection it actually arrived on. Otherwise a
                        // peer could claim another user's identity and we would relay that
                        // stream data to everyone else in the session.
                        if (!ValidateUserSender(streamMessage.Sender, streamMessage.UserID, "StreamMessage"))
                        {
                            streamMessage.Dispose();
                            break;
                        }

                        var forwarded = CloneStreamMessage(streamMessage);
                        AddStreamTargets(forwarded, excludeSender: true);
                        if (forwarded.Targets.Count > 0)
                        {
                            EnqueueForTransmission(forwarded);
                        }
                        else
                        {
                            forwarded.Dispose();
                        }
                    }
                    if (World.State != World.WorldState.Running)
                    {
                        // Streams are latest-value avatar poses. While we're still loading the world
                        // there's nothing to apply them to yet, and the very next stream after we go
                        // Running carries the current pose - so a queued backlog is useless and just
                        // floods the log when the load takes a moment (a joiner shouldn't be sent
                        // streams until it's ready). Drop them quietly until we're live.
                        // -xlinka
                        streamMessage.Dispose();
                        break;
                    }
                    ApplyStreamMessage(streamMessage);
                    break;

                case RawFrameMessage rawFrame:
                    if (World.IsAuthority)
                    {
                        // Same sender-identity check as StreamMessage: a peer cannot claim
                        // another user's UserID and have us relay it to everyone.
                        if (!ValidateUserSender(rawFrame.Sender, rawFrame.UserID, "RawFrameMessage"))
                        {
                            rawFrame.Dispose();
                            break;
                        }

                        var forwarded = rawFrame.CloneForRelay();
                        AddStreamTargets(forwarded, excludeSender: true);
                        if (forwarded.Targets.Count > 0)
                        {
                            EnqueueForTransmission(forwarded);
                        }
                        else
                        {
                            forwarded.Dispose();
                        }
                    }

                    DispatchRawFrame(rawFrame);
                    rawFrame.Dispose();
                    break;

                case ControlMessage controlMessage:
                    controlMessagesToProcess.Add(controlMessage);
                    break;

                default:
                    LumoraLogger.Warn($"[Sync] Unknown message type: {msg.GetType()}");
                    msg.Dispose();
                    break;
            }

            return true;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"[Sync] ProcessMessage: Exception: {ex.Message}");
            throw;
        }
    }

    // LINK SEQUENCING

    // Sync thread only.
    private ulong NextOutSequence() => ++_outSequence;

    private LinkSequenceState? GetLinkState(IConnection? connection)
    {
        if (connection == null)
        {
            return null;
        }

        lock (_linkLock)
        {
            if (!_linkSequences.TryGetValue(connection, out var state))
            {
                state = new LinkSequenceState();
                _linkSequences[connection] = state;
            }
            return state;
        }
    }

    private static string LinkName(IConnection? connection) => connection?.Identifier ?? "<no sender>";

    // Forget a link's cursor when its connection goes away, so a reconnect can never resume a delta run
    // across the gap it was absent for - a fresh IConnection gets a fresh cursor, and the only thing
    // that legitimately sets it is the tagged full batch the host sends on join. -xlinka
    public void OnConnectionClosed(IConnection connection)
    {
        if (connection == null)
        {
            return;
        }

        lock (_linkLock)
        {
            _linkSequences.Remove(connection);
        }
    }

    // Applies it when it is exactly the next batch on its link, ignores it when it is at or behind the cursor,
    // and treats anything further ahead as lost batches.
    private void ApplySequencedDelta(DeltaBatch batch)
    {
        var state = GetLinkState(batch.Sender);
        if (state == null)
        {
            ApplyDeltaBatch(batch);
            return;
        }

        if (state.AwaitingResync)
        {
            // Authority side: this peer is still sending deltas computed against state we already told
            // it to throw away. Applying them puts back exactly the divergence we are repairing.
            batch.Dispose();
            return;
        }

        switch (state.Classify(batch.LinkSequence))
        {
            case LinkSequenceVerdict.Apply:
                state.Advance(batch.LinkSequence);
                ApplyDeltaBatch(batch);
                return;

            case LinkSequenceVerdict.Unsequenced:
                ApplyDeltaBatch(batch);
                return;

            case LinkSequenceVerdict.Duplicate:
                TotalDuplicateDeltas++;
                state.DuplicatesSinceLog++;
                if (!state.LoggedDuplicate)
                {
                    state.LoggedDuplicate = true;
                    LumoraLogger.Warn($"[Sync] Duplicate delta on link {LinkName(batch.Sender)}: sequence {batch.LinkSequence} is at or behind cursor {state.LastApplied} - ignoring (further duplicates on this link are counted, not logged)");
                }
                batch.Dispose();
                return;

            default:
                TriggerDesync(state, batch.Sender, batch.LinkSequence, "delta sequence gap");
                if (World.IsAuthority)
                {
                    batch.Dispose();
                }
                else
                {
                    // Keep it: the tagged full batch decides whether it is still worth replaying.
                    EnqueuePendingDelta(batch, "delta sequence gap");
                }
                return;
        }
    }

    // One link lost batches. On the authority that means a peer's own delta stream has holes in it, so
    // we stop taking its deltas and push it authoritative state. On a client it means we are missing
    // world state, so we stop applying and ask for it. Either way this logs exactly one line naming the
    // counts. -xlinka
    private void TriggerDesync(LinkSequenceState state, IConnection? sender, ulong sequence, string reason)
    {
        TotalDesyncs++;
        ulong missing = state.MissingBefore(sequence);

        if (World.IsAuthority)
        {
            state.AwaitingResync = true;
            LumoraLogger.Error($"[Sync] DESYNC ({reason}): {missing} delta batch(es) lost from {LinkName(sender)} - cursor {state.LastApplied}, received {sequence}. Refusing its deltas and pushing full world state. Total desyncs {TotalDesyncs}, duplicates {TotalDuplicateDeltas}, list mismatches {TotalListMismatches}.");
            if (sender != null)
            {
                Session.Connections.TryGetUser(sender, out var user);
                SendFullWorldState(sender, user, reason);
            }
            return;
        }

        _desynced = true;
        LumoraLogger.Error($"[Sync] DESYNC ({reason}): {missing} delta batch(es) lost from {LinkName(sender)} - cursor {state.LastApplied}, received {sequence}. Holding {_pendingDeltaBatches.Count} queued batch(es) and requesting full world state. Total desyncs {TotalDesyncs}, duplicates {TotalDuplicateDeltas}, list mismatches {TotalListMismatches}.");
        RequestFullStateFromHost(reason);
    }

    // Advance (or trip) a link cursor for a message that carries a sequence but no ops we could hold
    // back. That is the confirmation answering a relayed delta: it exists on this link precisely so the
    // peer whose own delta was deliberately not echoed back still sees an unbroken run. -xlinka
    private void NoteLinkSequence(BinaryMessageBatch message)
    {
        var state = GetLinkState(message.Sender);
        if (state == null)
        {
            return;
        }

        switch (state.Classify(message.LinkSequence))
        {
            case LinkSequenceVerdict.Apply:
                state.Advance(message.LinkSequence);
                break;
            case LinkSequenceVerdict.Gap:
                TriggerDesync(state, message.Sender, message.LinkSequence, "confirmation sequence gap");
                break;
            case LinkSequenceVerdict.Duplicate:
                TotalDuplicateDeltas++;
                break;
        }
    }

    // Full world state landed on a client: the cursor moves to its tag, everything queued at or below that tag
    // is superseded, and the link is clean again.
    private void NoteFullStateApplied(FullBatch fullBatch)
    {
        // Sequence 0 marks a PARTIAL full batch - drive corrections, a single-element resync. Those fix
        // the records they carry and nothing else, so moving the cursor to "now" would make every delta
        // still in flight for other elements read back as a duplicate and get thrown away. -xlinka
        if (fullBatch.LinkSequence == 0)
        {
            return;
        }

        var state = GetLinkState(fullBatch.Sender);
        if (state == null)
        {
            return;
        }

        state.AcceptFull(fullBatch.LinkSequence);
        int discarded = DiscardSupersededPendingDeltas(state.LastApplied);

        if (_desynced)
        {
            _desynced = false;
            LumoraLogger.Log($"[Sync] Resynced from full world state at sequence {state.LastApplied}: {discarded} superseded batch(es) discarded, {_pendingDeltaBatches.Count} still to replay.");
        }
    }

    // Stops at the first batch past the tag - the rest are newer than the snapshot and still have to be
    // replayed in order.
    private int DiscardSupersededPendingDeltas(ulong tag)
    {
        int discarded = 0;
        while (_pendingDeltaBatches.Count > 0)
        {
            var head = _pendingDeltaBatches.First!.Value;
            if (head.LinkSequence == 0 || head.LinkSequence > tag)
            {
                break;
            }

            _pendingDeltaBatches.RemoveFirst();
            _pendingDeltaBytes -= head.PayloadByteSize;
            head.Dispose();
            discarded++;
        }

        if (_pendingDeltaBytes < 0)
        {
            _pendingDeltaBytes = 0;
        }
        return discarded;
    }

    private void RequestFullStateFromHost(string reason)
    {
        if (World.IsAuthority)
        {
            return;
        }

        var host = Session.Connections.HostConnection;
        if (host == null)
        {
            return;
        }

        // One request per cooldown window. Delivery is reliable, so the answer is already on its way -
        // a request per gapped batch would ask the host to re-encode the whole world dozens of times.
        if (_lastFullStateRequestTick != 0 && World.SyncTick - _lastFullStateRequestTick < (ulong)FullStateRequestCooldownTicks)
        {
            return;
        }
        _lastFullStateRequestTick = World.SyncTick;

        var request = new ControlMessage(ControlMessage.Message.RequestFullState);
        request.Targets.Add(host);
        EnqueueForTransmission(request);
        LumoraLogger.Warn($"[Sync] Requested full world state from host ({reason})");
    }

    // The full batch is TAGGED with the current sequence rather than consuming a new one, so it can go to a
    // single peer without punching a hole in everyone else's run.
    private void SendFullWorldState(IConnection connection, User? user, string reason)
    {
        if (!World.IsAuthority || connection == null)
        {
            return;
        }

        var fullBatch = World.SyncController.EncodeFullBatch();
        fullBatch.LinkSequence = _outSequence;
        fullBatch.Targets.Add(connection);
        EnqueueForTransmission(fullBatch);

        var startDeltaMessage = new ControlMessage(ControlMessage.Message.JoinStartDelta);
        startDeltaMessage.Targets.Add(connection);
        EnqueueForTransmission(startDeltaMessage);

        // This path does NOT go through the Stage-8 init queue, so it is the only place this user's
        // fan-out gate gets opened. Miss it and the peer receives full state and then nothing ever
        // moves again. Full batch is enqueued first, so live traffic still trails the state. -xlinka
        user?.StartTransmittingStreamData();

        LumoraLogger.Log($"[Sync] Sent full world state to {LinkName(connection)} ({reason}): {fullBatch.DataRecordCount} records tagged at sequence {fullBatch.LinkSequence}");
    }

    // ELEMENT-LEVEL RESYNC

    private bool IsAwaitingElementResync(RefID id) => _elementsAwaitingResync.ContainsKey(id);

    // An incremental collection refused its delta because the sender's pre-op count disagreed with
    // ours. One bad collection costs one collection: we ask the authority for a full record of just
    // that element and skip its deltas until it arrives, since every one of them is computed against a
    // base we demonstrably don't have. -xlinka
    private void NoteElementNeedsResync(RefID id, string reason)
    {
        TotalListMismatches++;

        if (World.IsAuthority)
        {
            // Validation rejects a mismatching peer record through the conflict path before it ever
            // reaches decode, so reaching here on the authority means one slipped past. We ARE the
            // state, so there is nobody to ask - drop the record and say so.
            LumoraLogger.Warn($"[Sync] Authority refused an out-of-sync collection delta for {id}: {reason}");
            return;
        }

        if (_elementsAwaitingResync.ContainsKey(id))
        {
            return;
        }

        _elementsAwaitingResync[id] = World.SyncTick;
        _resyncRequestQueue.Add(id);
        LumoraLogger.Warn($"[Sync] Collection desync on {id} ({reason}) - requesting a full re-encode of that element; its deltas are skipped until it lands. Total list mismatches {TotalListMismatches}.");
    }

    private void ClearElementResync(RefID id)
    {
        if (_elementsAwaitingResync.Remove(id))
        {
            LumoraLogger.Log($"[Sync] Collection {id} repaired from a full record - resuming its deltas");
        }
    }

    // Better to resume and let the next mismatch re-request than to ignore an element for the rest of the
    // session.
    private void ExpireStaleElementResyncs()
    {
        if (_elementsAwaitingResync.Count == 0)
        {
            return;
        }

        ulong now = World.SyncTick;
        List<RefID>? stale = null;
        foreach (var kvp in _elementsAwaitingResync)
        {
            if (now - kvp.Value > (ulong)ElementResyncTimeoutTicks)
            {
                (stale ??= new List<RefID>()).Add(kvp.Key);
            }
        }

        if (stale == null)
        {
            return;
        }

        foreach (var id in stale)
        {
            _elementsAwaitingResync.Remove(id);
            LumoraLogger.Warn($"[Sync] Collection {id} never came back from its resync request - resuming its deltas anyway");
        }
    }

    private void SendPendingResyncRequests()
    {
        if (World.IsAuthority || _resyncRequestQueue.Count == 0)
        {
            return;
        }

        var host = Session.Connections.HostConnection;
        if (host == null)
        {
            _resyncRequestQueue.Clear();
            return;
        }

        while (_resyncRequestQueue.Count > 0)
        {
            int take = System.Math.Min(_resyncRequestQueue.Count, MaxResyncRefIDsPerMessage);
            byte[] payload;
            using (var ms = new MemoryStream())
            {
                using var writer = new BinaryWriter(ms);
                writer.Write7BitEncoded((ulong)take);
                for (int i = 0; i < take; i++)
                {
                    writer.WriteRefID(_resyncRequestQueue[i]);
                }
                writer.Flush();
                payload = ms.ToArray();
            }
            _resyncRequestQueue.RemoveRange(0, take);

            var request = new ControlMessage(ControlMessage.Message.ResyncElements) { Payload = payload };
            request.Targets.Add(host);
            EnqueueForTransmission(request);
            TotalTargetedResyncs++;
        }
    }

    // Sent as a PARTIAL full batch (sequence 0) so repairing one element never moves that peer's cursor past
    // deltas still in flight for everything else.
    private void HandleResyncElementsRequest(ControlMessage message)
    {
        if (!World.IsAuthority || message.Sender == null || message.Payload == null || message.Payload.Length == 0)
        {
            return;
        }

        List<SyncElement>? elements = null;
        int requested;
        try
        {
            using var ms = new MemoryStream(message.Payload);
            using var reader = new BinaryReader(ms);
            int count = (int)reader.Read7BitEncoded();
            if (count <= 0 || count > MaxResyncRefIDsPerMessage)
            {
                LumoraLogger.Warn($"[Sync] ResyncElements from {LinkName(message.Sender)}: refusing a request for {count} element(s), cap is {MaxResyncRefIDsPerMessage}");
                return;
            }
            requested = count;
            for (int i = 0; i < count; i++)
            {
                var id = reader.ReadRefID();
                if (World.SyncController.TryGetElement(id, out var element))
                {
                    (elements ??= new List<SyncElement>()).Add(element);
                }
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"[Sync] ResyncElements from {LinkName(message.Sender)}: malformed payload - {ex.Message}");
            return;
        }

        if (elements == null || elements.Count == 0)
        {
            LumoraLogger.Warn($"[Sync] ResyncElements from {LinkName(message.Sender)}: none of the {requested} requested element(s) exist here");
            return;
        }

        var batch = World.SyncController.EncodeFullBatch(elements);
        batch.Targets.Add(message.Sender);
        EnqueueForTransmission(batch);
        TotalTargetedResyncs++;
        LumoraLogger.Log($"[Sync] ResyncElements: re-encoded {elements.Count}/{requested} element(s) for {LinkName(message.Sender)}");
    }

    // The authority refuses a peer's deltas outright while that peer is resyncing, so those ticks never get
    // answered and would sit here for the rest of the session.
    private void PruneStaleChangeConfirmations()
    {
        if (_changesToConfirm.Count == 0 || World.SyncTick < (ulong)ChangeConfirmationMaxAgeTicks)
        {
            return;
        }

        ulong cutoff = World.SyncTick - (ulong)ChangeConfirmationMaxAgeTicks;
        List<ulong>? stale = null;
        foreach (var tick in _changesToConfirm.Keys)
        {
            if (tick < cutoff)
            {
                (stale ??= new List<ulong>()).Add(tick);
            }
        }

        if (stale == null)
        {
            return;
        }

        foreach (var tick in stale)
        {
            _changesToConfirm.Remove(tick);
        }
        LumoraLogger.Debug($"[Sync] Dropped {stale.Count} change set(s) that were never confirmed within {ChangeConfirmationMaxAgeTicks} ticks");
    }

    private void EnqueuePendingDelta(DeltaBatch batch, string reason)
    {
        _pendingDeltaBatches.AddLast(batch);
        _pendingDeltaBytes += batch.PayloadByteSize;

        if (_pendingDeltaBytes <= MaxPendingDeltaBytes)
        {
            LumoraLogger.Debug($"[Sync] Queued delta batch ({reason}); backlog {_pendingDeltaBatches.Count} batches / {_pendingDeltaBytes / 1024} KB");
            return;
        }

        // Ceiling. This is NOT a drop of one batch - a single missing op is exactly what we cannot
        // recover from - it is abandoning the whole backlog on purpose so full state can rebuild it.
        int abandoned = _pendingDeltaBatches.Count;
        long bytes = _pendingDeltaBytes;
        while (_pendingDeltaBatches.Count > 0)
        {
            _pendingDeltaBatches.First!.Value.Dispose();
            _pendingDeltaBatches.RemoveFirst();
        }
        _pendingDeltaBytes = 0;

        TotalDesyncs++;
        LumoraLogger.Error($"[Sync] DESYNC (delta backlog ceiling): backlog reached {bytes / 1024} KB over {abandoned} batch(es) while '{reason}', past the {MaxPendingDeltaBytes / (1024 * 1024)} MB ceiling. Abandoning it and rebuilding from full state. Total desyncs {TotalDesyncs}.");

        if (!World.IsAuthority)
        {
            _desynced = true;
            RequestFullStateFromHost("delta backlog ceiling");
        }
    }

    // Replay queued deltas, at most maxBatches per call. Stops at the head when the
    // head still has a hole in front of it: order is the only thing that makes replaying ops correct,
    // so the backlog is left exactly as it is rather than reordered or trimmed. Returns how many were
    // consumed. -xlinka
    private int DrainPendingDeltas(int maxBatches)
    {
        if (_pendingDeltaBatches.Count == 0 || World.State != World.WorldState.Running)
        {
            return 0;
        }

        if (!World.IsAuthority && (!_acceptDeltas || _desynced))
        {
            return 0;
        }

        int consumed = 0;
        while (consumed < maxBatches && _pendingDeltaBatches.Count > 0)
        {
            var batch = _pendingDeltaBatches.First!.Value;
            var state = GetLinkState(batch.Sender);
            if (state != null && !state.AwaitingResync && state.Classify(batch.LinkSequence) == LinkSequenceVerdict.Gap)
            {
                TriggerDesync(state, batch.Sender, batch.LinkSequence, "gap in queued delta backlog");
                break;
            }

            _pendingDeltaBatches.RemoveFirst();
            _pendingDeltaBytes -= batch.PayloadByteSize;
            if (_pendingDeltaBytes < 0)
            {
                _pendingDeltaBytes = 0;
            }

            ApplySequencedDelta(batch);
            consumed++;

            if (_desynced)
            {
                break;
            }
        }

        return consumed;
    }

    private void EnqueuePendingStream(StreamMessage streamMessage, string reason)
    {
        if (_pendingStreamMessages.Count >= MaxPendingStreamMessages)
        {
            LumoraLogger.Warn($"[Sync] ProcessMessage: Dropping stream message - pending limit reached ({MaxPendingStreamMessages})");
            streamMessage.Dispose();
            return;
        }

        LumoraLogger.Debug($"[Sync] ProcessMessage: Queueing stream message ({reason})");
        _pendingStreamMessages.Enqueue(streamMessage);
    }

    private void FlushPendingMessages()
    {
        if (World.State != World.WorldState.Running)
            return;

        // A join backlog has to be replayed in full, not in chunks - the joiner is not live until it is
        // caught up. It still goes through the sequence gate, so anything the join snapshot already
        // superseded is skipped rather than re-applied.
        DrainPendingDeltas(int.MaxValue);

        while (_pendingStreamMessages.Count > 0)
        {
            ApplyStreamMessage(_pendingStreamMessages.Dequeue());
        }
    }

    private void ApplyDeltaBatch(DeltaBatch deltaBatch)
    {
        // Always validate before applying deltas; host will retransmit if needed.
        ValidateDeltaBatchAndRetransmit(deltaBatch);

        ApplyDataRecords(deltaBatch);

        if (!World.IsAuthority)
        {
            World.SetStateVersion(deltaBatch.SenderStateVersion);
        }

        deltaBatch.Dispose();
    }

    private void ApplyStreamMessage(StreamMessage streamMessage)
    {
        if (World.State == World.WorldState.Running)
        {
            World.SyncController.ApplyStreams(streamMessage);
        }
        streamMessage.Dispose();
    }

    private void ValidateDeltaBatchAndRetransmit(DeltaBatch batch)
    {
        World.SyncController.ValidateDeltaMessages(batch);

        var conflicting = new List<RefID>();
        batch.GetConflictingDataRecords(conflicting);
        batch.RemoveInvalidRecords();

        if (World.IsAuthority)
        {
            // ONE number covers this relay in both directions. Everyone else learns it from the
            // forwarded copy; the peer that produced the delta - the one target deliberately left out -
            // learns it from the confirmation it already gets back. That is what keeps every link's run
            // unbroken even though no single message here reaches all of them, and it is why the
            // forward goes out even when validation left it with zero records: it is the sequence
            // carrier for every other peer, and a peer that never sees the number reads it as a hole
            // and resyncs the whole world over nothing. -xlinka
            ulong relaySequence = NextOutSequence();

            var forward = CopyDeltaBatch(batch);
            forward.LinkSequence = relaySequence;

            // Relay this peer's delta to everyone else who's live, but never echo it back to the
            // sender that produced it. -xlinka
            AddAuthorityFanoutTargets(forward, batch.Sender);

            if (forward.Targets.Count > 0)
            {
                EnqueueForTransmission(forward);
            }
            else
            {
                forward.Dispose();
            }

            if (batch.Sender != null)
            {
                // Always confirm an accepted delta back to the client that sent it - even with ZERO
                // conflicts. The client keys its own outgoing changes by this tick and only advances
                // their confirmed state (and clears its pending-confirm set) when it hears back. An empty
                // confirmation is exactly how a clean, non-conflicting change gets acknowledged; without
                // it the client's _changesToConfirm for clean ticks leaks forever and those changes never
                // register as confirmed. When there ARE conflicts we also carry the authoritative current
                // values so the client corrects. -xlinka
                var confirmation = new ConfirmationMessage(batch.SenderSyncTick, World.StateVersion, World.SyncTick)
                {
                    LinkSequence = relaySequence
                };
                if (conflicting.Count > 0)
                {
                    TotalCorrections += conflicting.Count;
                    foreach (var id in conflicting)
                    {
                        World.SyncController.EncodeFull(id, confirmation);
                    }
                }
                confirmation.Targets.Add(batch.Sender);
                EnqueueForTransmission(confirmation);
            }
        }
    }

    // Routes to the host on clients, fans out to all peers (including loopback to local handlers) on the
    // authority. Returns false if the payload is over the cap or the stream isn't owned by the local user.
    public bool EnqueueRawFrame(Stream stream, ushort sequence, ReadOnlySpan<byte> payload)
    {
        if (stream == null) return false;
        if (payload.Length > NetworkLimits.MaxRawFrameBytes) return false;
        if (World == null) return false;

        var owner = stream.Owner;
        if (owner == null || !owner.IsLocal) return false;

        var msg = new RawFrameMessage(World.StateVersion, World.SyncTick)
        {
            UserID = (ulong)owner.ReferenceID,
            StreamRefID = stream.ReferenceID,
            Sequence = sequence,
        };
        msg.SetPayload(payload);

        AddStreamTargets(msg, excludeSender: false);
        if (msg.Targets.Count == 0)
        {
            msg.Dispose();
            return false;
        }

        EnqueueForTransmission(msg);
        return true;
    }

    // Runs on the sync thread; subscribers must not block (push to a lock-free queue and return).
    private void DispatchRawFrame(RawFrameMessage rawFrame)
    {
        if (rawFrame == null || World == null) return;

        var userElement = World.ReferenceController?.GetObjectOrNull(new RefID(rawFrame.UserID));
        if (userElement is not User sender)
        {
            LumoraLogger.Warn($"[Sync] RawFrame: user {rawFrame.UserID} not found; dropping.");
            return;
        }

        Session.HandleIncomingRawFrame(sender, rawFrame.StreamRefID, rawFrame.Sequence, rawFrame.Payload);
    }

    // Authority-side use only - clients receive messages relayed via the host, whose Sender is the host
    // connection rather than the original peer.
    private bool ValidateUserSender(IConnection sender, ulong claimedUserID, string typeName)
    {
        if (sender == null)
        {
            LumoraLogger.Warn($"[Sync] {typeName} with no Sender; dropping.");
            return false;
        }

        if (!Session.Connections.TryGetUser(sender, out var senderUser) || senderUser == null)
        {
            LumoraLogger.Warn($"[Sync] {typeName} from {sender.Identifier}: no user mapping; dropping.");
            return false;
        }

        if ((ulong)senderUser.ReferenceID != claimedUserID)
        {
            LumoraLogger.Warn($"[Sync] {typeName} sender mismatch: connection {sender.Identifier} is user {senderUser.UserName?.Value} ({(ulong)senderUser.ReferenceID}) but claimed UserID {claimedUserID}; dropping.");
            return false;
        }

        return true;
    }

    // Populate message.Targets with the appropriate fan-out for
    // stream-class messages: on the authority, every other connection (optionally
    // excluding the sender of a relayed message and any users still initializing);
    // on a client, just the host. Used for both StreamMessage and
    // RawFrameMessage.
    // Adds every connection that should receive an authority-side replicated batch (delta, correction,
    // or stream) as a target on message. A user becomes eligible only once it has
    // been handed full world state - its ReceiveStreams flips true at the JoinStartDelta
    // point - so until then we leave it out and let its initial full batch carry the state instead;
    // sending it live traffic earlier would just race the join. A connection with no mapped user yet
    // (mid-handshake) is always included so nothing is silently dropped. Pass the original sender as
    // Single fan-out walk shared by the delta batch, released-drive corrections, the stream loop, and
    // relayed-delta retransmit, instead of a separate per-tick `new HashSet<User>()` snapshot of the
    // init queue for each: the ReceiveStreams flag already carries the "still initializing" state, so a
    // dedicated snapshot per call site would just be per-message allocation and lock round-trip waste. -xlinka
    private void AddAuthorityFanoutTargets(SyncMessage message, IConnection? excludeConnection = null)
    {
        var connections = Session.Connections.GetAllConnections();
        foreach (var connection in connections)
        {
            if (excludeConnection != null && connection == excludeConnection)
            {
                continue;
            }

            // A mapped user that hasn't been handed full state yet (or opted out) is skipped. An unmapped
            // connection still gets included to match the prior behavior. -xlinka
            if (Session.Connections.TryGetUser(connection, out var user) && user != null && !user.ReceiveStreams)
            {
                continue;
            }

            message.Targets.Add(connection);
        }
    }

    private void AddStreamTargets(SyncMessage message, bool excludeSender)
    {
        if (World.IsAuthority)
        {
            AddAuthorityFanoutTargets(message, excludeSender ? message.Sender : null);
        }
        else
        {
            var hostConnection = Session.Connections.HostConnection;
            if (hostConnection != null)
            {
                message.Targets.Add(hostConnection);
            }
        }
    }

    private static StreamMessage CloneStreamMessage(StreamMessage source)
    {
        var clone = new StreamMessage(source.SenderStateVersion, source.SenderSyncTick)
        {
            IsAsynchronous = source.IsAsynchronous,
            UserID = source.UserID,
            StreamStateVersion = source.StreamStateVersion,
            StreamTime = source.StreamTime,
            StreamGroup = source.StreamGroup
        };

        var sourceData = source.GetData();
        sourceData.Position = 0;
        var cloneData = clone.GetData();
        sourceData.CopyTo(cloneData);

        return clone;
    }

    private DeltaBatch CopyDeltaBatch(DeltaBatch source)
    {
        var copy = new DeltaBatch(World.StateVersion, World.SyncTick);

		for (int i = 0; i < source.DataRecordCount; i++)
		{
			var record = source.GetDataRecord(i);
			var length = record.EndOffset - record.StartOffset;
			var reader = source.SeekDataRecord(i);
			var buffer = length > 0 ? reader.ReadBytes(length) : Array.Empty<byte>();

			var writer = copy.BeginNewDataRecord(record.TargetID);
			if (buffer.Length > 0)
			{
				writer.Write(buffer);
			}
			copy.FinishDataRecord(record.TargetID);
		}

        return copy;
    }

    private int ApplyDataRecords(BinaryMessageBatch batch)
    {
        int remaining = batch.DataRecordCount;
        int passes = 0;

        // LumoraLogger.Log($"[Sync] ApplyDataRecords: Starting with {batch.DataRecordCount} records, isFull={batch is FullBatch}");

        while (remaining > 0)
        {
            int startRemaining = remaining;
            int decodedThisPass = 0;

            for (int i = 0; i < batch.DataRecordCount; i++)
            {
                if (batch.IsProcessed(i))
                    continue;

                var targetID = batch.GetDataRecord(i).TargetID;

                // This element's incremental deltas are meaningless until its full record lands, since
                // every one of them is computed against a base we don't have. Consume the record so the
                // pass still counts as progress, and wait.
                if (batch is DeltaBatch && IsAwaitingElementResync(targetID))
                {
                    batch.MarkDataRecordAsProcessed(i);
                    remaining--;
                    decodedThisPass++;
                    continue;
                }

                bool decoded = false;
                try
                {
                    decoded = batch switch
                    {
                        DeltaBatch delta => World.SyncController.DecodeDeltaMessage(i, delta),
                        FullBatch full => World.SyncController.DecodeFullMessage(i, full),
                        _ => false
                    };

                    if (decoded)
                    {
                        batch.MarkDataRecordAsProcessed(i);
                        remaining--;
                        decodedThisPass++;

                        if (batch is FullBatch)
                        {
                            ClearElementResync(targetID);
                        }
                        // Count records that actually MATERIALIZED, so the loading bar reflects real
                        // progress (this counter used to be declared but never incremented). -xlinka
                        if (batch is FullBatch && !World.IsAuthority)
                        {
                            lock (_progressLock) { _initializedComponents++; }
                        }
                    }
                }
                catch (ElementResyncRequiredException ex)
                {
                    // The collection refused its own delta: the sender's pre-op count didn't match what
                    // we hold, so nothing was applied. Retrying is pointless - the base is wrong, not
                    // the timing - so consume the record and go get the element in full.
                    batch.MarkDataRecordAsProcessed(i);
                    remaining--;
                    decodedThisPass++;
                    NoteElementNeedsResync(ex.TargetID, ex.Message);
                }
                catch (Exception ex)
                {
                    // Log but don't retry immediately - might succeed in next pass
                    var dataRecord = batch.GetDataRecord(i);
                    LumoraLogger.Debug($"[Sync] ApplyDataRecords: Decode failed for {dataRecord.TargetID}: {ex.Message}");
                }
            }

            // LumoraLogger.Log($"[Sync] ApplyDataRecords: Pass {passes} decoded {decodedThisPass} records, {remaining} remaining");

            if (remaining == startRemaining)
            {
                break;
            }

            passes++;
        }

        if (remaining > 0)
        {
            LumoraLogger.Debug($"[Sync] ApplyDataRecords: {remaining} records could not be decoded after {passes} passes");
            for (int i = 0; i < batch.DataRecordCount; i++)
            {
                if (!batch.IsProcessed(i))
                {
                    var record = batch.GetDataRecord(i);
                    var obj = World.ReferenceController?.GetObjectOrNull(record.TargetID);
                    LumoraLogger.Debug($"[Sync]   FAILED RefID={record.TargetID} Type={obj?.GetType().Name ?? "NOT_FOUND"}");
                    QueuePendingRecord(batch, i);
                }
            }
        }

        return remaining;
    }

    private void QueuePendingRecord(BinaryMessageBatch batch, int recordIndex)
    {
        if (World == null)
            return;

        var record = batch.GetDataRecord(recordIndex);
        var reader = batch.SeekDataRecord(recordIndex);
        var length = record.EndOffset - record.StartOffset;
        var buffer = length > 0 ? reader.ReadBytes(length) : Array.Empty<byte>();

        var pending = new PendingRecord
        {
            TargetID = record.TargetID,
            Data = buffer,
            SenderStateVersion = batch.SenderStateVersion,
            SenderSyncTick = batch.SenderSyncTick,
            FirstSeenTick = World.SyncTick,
            Attempts = 0
        };

        var isFull = batch is FullBatch;
        var map = isFull ? _pendingFullRecords : _pendingDeltaRecords;

        lock (_pendingLock)
        {
            if (map.TryGetValue(record.TargetID, out var existing))
            {
                if (pending.SenderSyncTick >= existing.SenderSyncTick)
                {
                    map[record.TargetID] = pending;
                }
            }
            else
            {
                map[record.TargetID] = pending;
            }
        }
    }

    private void ProcessPendingRecords()
    {
        if (World == null || World.ReferenceController == null || World.SyncController == null)
            return;

        List<PendingRecord> fullRecords;
        List<PendingRecord> deltaRecords;

        lock (_pendingLock)
        {
            if (_pendingFullRecords.Count == 0 && _pendingDeltaRecords.Count == 0)
                return;

            fullRecords = new List<PendingRecord>(_pendingFullRecords.Values);
            deltaRecords = new List<PendingRecord>(_pendingDeltaRecords.Values);
        }

        foreach (var record in fullRecords)
        {
            ProcessPendingRecord(record, isFull: true);
        }

        // Initial-state records just got a retry pass; see if the snapshot is now complete enough to
        // enter the world (or stuck long enough that we should enter anyway and say what's missing).
        TryEnterRunningWhenComplete();

        if (World.State == World.WorldState.Running && (_acceptDeltas || World.IsAuthority))
        {
            foreach (var record in deltaRecords)
            {
                ProcessPendingRecord(record, isFull: false);
            }
        }
    }

    // Flip the joined world to Running only after every record of the initial full state has been
    // applied: hold until the pending-full-record set drains to empty, then transition. If progress stalls for a long stretch (an orphaned record whose owner
    // never decoded - e.g. a component whose type couldn't resolve), we log exactly what's stuck and
    // enter anyway, so a join surfaces the problem loudly instead of either bricking or silently
    // showing a half-world. -xlinka
    private void TryEnterRunningWhenComplete()
    {
        if (World == null || World.IsAuthority) return;
        if (!_joinStartDeltaSeen) return;
        if (World.InitState != World.InitializationState.InitializingDataModel) return;

        int pending;
        lock (_pendingLock)
        {
            pending = _pendingFullRecords.Count;
        }

        // Clean case: the batch was tracked and everything applied - enter right away.
        if (pending == 0 && _initialFullBatchReceived)
        {
            LumoraLogger.Log("[Sync] Initial full state fully applied - entering world (Running)");
            World.OnFullStateReceived();
            return;
        }

        // Otherwise fall through to the stall detector below, which always force-enters after a short
        // window. Crucially we do NOT hard-require _initialFullBatchReceived here: if that flag never
        // got set the join must still become usable rather than hang forever. -xlinka

        // Still applying. Track progress so we can tell "slowly resolving" from "genuinely stuck".
        if (pending != _lastInitialPendingCount)
        {
            _lastInitialPendingCount = pending;
            _lastInitialProgressTick = World.SyncTick;
            return;
        }

        if (World.SyncTick - _lastInitialProgressTick < (ulong)InitialStateStuckTicks)
            return;

        // Stuck. Before giving up, ask the host to resend the full
        // state - missing records often just need another delivery. A deterministic failure (a type
        // that won't resolve) won't recover, so we cap the retries and then enter with diagnostics
        // rather than loop forever. -xlinka
        if (_fullStateRequestBudget > 0)
        {
            _fullStateRequestBudget--;
            LumoraLogger.Warn($"[Sync] Initial state stuck with {pending} record(s) - re-requesting full state from host ({_fullStateRequestBudget} retries left)");
            var request = new ControlMessage(ControlMessage.Message.RequestFullState);
            var host = Session.Connections.HostConnection;
            if (host != null)
            {
                request.Targets.Add(host);
                EnqueueForTransmission(request);
            }
            // Reset the stall detector so we wait for the resent batch instead of re-firing every tick.
            _lastInitialProgressTick = World.SyncTick;
            _lastInitialPendingCount = -1;
            return;
        }

        if (!_loggedInitialIncomplete)
        {
            _loggedInitialIncomplete = true;
            LumoraLogger.Error($"[Sync] Initial full state STUCK with {pending} record(s) that never resolved their owner - entering world INCOMPLETE. Unresolved:");
            lock (_pendingLock)
            {
                foreach (var kvp in _pendingFullRecords)
                {
                    var obj = World.ReferenceController?.GetObjectOrNull(kvp.Key);
                    LumoraLogger.Error($"[Sync]   unresolved RefID={kvp.Key} owner={(obj?.GetType().Name ?? "NEVER CREATED")}");
                }
            }
        }

        LumoraLogger.Warn("[Sync] Entering world despite incomplete initial state (see unresolved records above)");
        World.OnFullStateReceived();
    }

    private void ProcessPendingRecord(PendingRecord record, bool isFull)
    {
        if (World == null || World.ReferenceController == null || World.SyncController == null)
            return;

        // During the initial join we keep retrying instead of giving
        // up after a few frames - a record is usually only pending because its owner hasn't decoded
        // yet, and dropping it is permanent lost world content. We use a much longer ceiling here and,
        // if we ever do drop, we say loudly that it cost world content. -xlinka
        bool initialState = isFull && !World.IsAuthority
            && World.InitState == World.InitializationState.InitializingDataModel;
        ulong maxAge = (ulong)(initialState ? InitialStateMaxAgeTicks : PendingRecordMaxAgeTicks);

        var isExpired = World.SyncTick - record.FirstSeenTick > maxAge;
        if (isExpired)
        {
            if (initialState)
            {
                var owner = World.ReferenceController.GetObjectOrNull(record.TargetID);
                DropPendingRecord(record.TargetID, isFull,
                    $"expired during initial state - MISSING WORLD CONTENT (owner={owner?.GetType().Name ?? "never created"})");
            }
            else
            {
                DropPendingRecord(record.TargetID, isFull, "expired");
            }
            return;
        }

        if (!World.ReferenceController.ContainsObject(record.TargetID))
        {
            return;
        }

        if (!isFull)
        {
            var syncElement = World.ReferenceController.GetObjectOrNull(record.TargetID) as SyncElement;
            if (syncElement != null && syncElement.IsSyncDirty)
            {
                // Defer pending delta while local changes are still dirty.
                return;
            }
        }

        var decoded = TryDecodePendingRecord(record, isFull);
        if (decoded)
        {
            if (isFull && !World.IsAuthority)
            {
                lock (_progressLock) { _initializedComponents++; }
            }
            DropPendingRecord(record.TargetID, isFull, null!);
            return;
        }

        record.Attempts++;
        // No per-attempt drop during initial state - the age ceiling above is the only give-up there,
        // so a record whose owner is still on its way isn't discarded early. -xlinka
        int maxAttempts = initialState ? int.MaxValue : PendingRecordMaxAttempts;
        if (record.Attempts >= maxAttempts)
        {
            DropPendingRecord(record.TargetID, isFull, "attempts exceeded");
            return;
        }

        UpdatePendingRecord(record, isFull);
    }

    private bool TryDecodePendingRecord(PendingRecord record, bool isFull)
    {
        BinaryMessageBatch batch = isFull
            ? new FullBatch(record.SenderStateVersion, record.SenderSyncTick)
            : new DeltaBatch(record.SenderStateVersion, record.SenderSyncTick);

        try
        {
            var writer = batch.BeginNewDataRecord(record.TargetID);
            if (record.Data.Length > 0)
            {
                writer.Write(record.Data);
            }
            batch.FinishDataRecord(record.TargetID);

            return isFull
                ? World.SyncController.DecodeFullMessage(0, (FullBatch)batch)
                : World.SyncController.DecodeDeltaMessage(0, (DeltaBatch)batch);
        }
        catch (ElementResyncRequiredException ex)
        {
            // Same as the batch path: a base-count mismatch never resolves by waiting, so stop retrying
            // this record and ask for the element in full instead.
            NoteElementNeedsResync(ex.TargetID, ex.Message);
            DropPendingRecord(record.TargetID, isFull, "collection base-count mismatch");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            // Common case: delta applied to a locally dirty element. Keep pending for later.
            LumoraLogger.Debug($"[Sync] TryDecodePendingRecord: Deferred {record.TargetID} ({(isFull ? "full" : "delta")}) - {ex.Message}");
            return false;
        }
        finally
        {
            batch.Dispose();
        }
    }

    private void UpdatePendingRecord(PendingRecord record, bool isFull)
    {
        lock (_pendingLock)
        {
            var map = isFull ? _pendingFullRecords : _pendingDeltaRecords;
            if (map.ContainsKey(record.TargetID))
            {
                map[record.TargetID] = record;
            }
        }
    }

    private void DropPendingRecord(RefID targetId, bool isFull, string reason)
    {
        lock (_pendingLock)
        {
            var map = isFull ? _pendingFullRecords : _pendingDeltaRecords;
            if (map.Remove(targetId) && !string.IsNullOrEmpty(reason))
            {
                // Per-record, at Debug: a joiner can pend+drop dozens of records (e.g. its own avatar
                // echoed back before its local copy exists), and a Warn-per-record buries the console.
                // The real avatar-on-join sync gap is tracked separately. -xlinka
                LumoraLogger.Debug($"[Sync] Pending record dropped for {targetId}: {reason}");
            }
        }
    }

    private struct PendingRecord
    {
        public RefID TargetID;
        public byte[] Data;
        public ulong SenderStateVersion;
        public ulong SenderSyncTick;
        public ulong FirstSeenTick;
        public int Attempts;
    }

    private void TrackFullBatchProgress(FullBatch fullBatch)
    {
        lock (_progressLock)
        {
            if (!_initialFullBatchReceived)
            {
                _initialFullBatchReceived = true;
                _expectedComponents = fullBatch.DataRecordCount;
                LumoraLogger.Log($"[Sync] TrackFullBatchProgress: Expecting {_expectedComponents} components from initial FullBatch");
            }

            for (int i = 0; i < fullBatch.DataRecordCount; i++)
            {
                var record = fullBatch.GetDataRecord(i);
                if (!_receivedComponentIds.Contains(record.TargetID))
                {
                    _receivedComponentIds.Add(record.TargetID);
                    _receivedComponents++;
                }
            }

            LumoraLogger.Log($"[Sync] TrackFullBatchProgress: Received {_receivedComponents}/{_expectedComponents} components ({(_receivedComponents * 100.0f / _expectedComponents):F1}%)");
        }
    }

    // NOTE: World transitions to Running when JoinStartDelta is received,
    // not based on percentage of components synchronized.

    public float GetInitializationProgress()
    {
        lock (_progressLock)
        {
            if (!_initialFullBatchReceived || _expectedComponents == 0)
                return 0f;

            return MathF.Min(1f, (float)_initializedComponents / _expectedComponents);
        }
    }

    public string GetInitializationStatus()
    {
        if (World.IsAuthority)
            return "Authority";

        if (World.InitState == World.InitializationState.Failed)
            return "Connection failed";

        if (World.InitState == World.InitializationState.WaitingForJoinGrant)
            return "Waiting for join approval...";

        if (World.InitState == World.InitializationState.InitializingNetwork)
            return "Connecting to session...";

        lock (_progressLock)
        {
            if (!_initialFullBatchReceived)
                return "Waiting for world data...";

            if (_expectedComponents == 0)
                return "Initializing...";

            var progress = (int)((float)_initializedComponents / _expectedComponents * 100f);
            return $"Loading world components... {_initializedComponents}/{_expectedComponents} ({progress}%)";
        }
    }

    // Ask the authority what time it is, on the schedule the clock itself decides. Sent from the sync
    // loop so the probe leaves and its reply is read on the same thread, and so the cadence rides the
    // network tick rather than the render frame rate.
    private void SendClockProbe()
    {
        if (World.IsAuthority || World.State != World.WorldState.Running)
            return;

        var host = Session.Connections.HostConnection;
        if (host == null || !World.SessionClock.TryTakeProbe(out var stamp))
            return;

        var probe = new ControlMessage(ControlMessage.Message.ClockRequest)
        {
            Payload = SessionClock.EncodeProbe(stamp)
        };
        probe.Targets.Add(host);
        EnqueueForTransmission(probe);
    }

    private void ProcessControlMessage(ControlMessage message)
    {
        switch (message.ControlMessageType)
        {
            case ControlMessage.Message.JoinRequest:
                if (World.IsAuthority)
                {
                    if (message.Payload == null || message.Payload.Length == 0)
                    {
                        LumoraLogger.Warn("[Sync] ProcessControlMessage: JoinRequest missing payload");
                        return;
                    }

                    var requestData = LegacyJoinRequestData.Decode(message.Payload);
                    LumoraLogger.Log($"[Sync] ProcessControlMessage: JoinRequest from '{requestData.UserName}'");
                    Session.Connections.HandleJoinRequest(message.Sender, requestData);
                }
                break;

            case ControlMessage.Message.JoinChallenge:
                // Client side only: the host wants us to sign a nonce to prove our machine identity.
                if (!World.IsAuthority)
                {
                    if (message.Payload == null || message.Payload.Length == 0)
                    {
                        LumoraLogger.Warn("[Sync] ProcessControlMessage: JoinChallenge missing payload");
                        return;
                    }
                    var challengeData = LegacyJoinChallengeData.Decode(message.Payload);
                    Session.Connections.HandleJoinChallenge(message.Sender, challengeData);
                }
                break;

            case ControlMessage.Message.JoinAuthenticate:
                // Host side only: the joiner answered our challenge; verify it before granting.
                if (World.IsAuthority)
                {
                    if (message.Payload == null || message.Payload.Length == 0)
                    {
                        LumoraLogger.Warn("[Sync] ProcessControlMessage: JoinAuthenticate missing payload");
                        return;
                    }
                    var authData = LegacyJoinAuthenticateData.Decode(message.Payload);
                    // Fire-and-forget: account verification may hit the backend (async), and the handler
                    // grants or rejects internally, so we don't block the control-message loop on it. -xlinka
                    _ = Session.Connections.HandleJoinAuthenticate(message.Sender, authData);
                }
                break;

            case ControlMessage.Message.JoinGrant:
                if (message.Payload == null || message.Payload.Length == 0)
                {
                    LumoraLogger.Warn("[Sync] ProcessControlMessage: JoinGrant missing payload");
                    return;
                }

                var grantData = LegacyJoinGrantData.Decode(message.Payload);
                LumoraLogger.Log($"[Sync] ProcessControlMessage: JoinGrant UserID={grantData.AssignedUserID}");

                var assignedRefID = new RefID(grantData.AssignedUserID);

                // Store the assigned RefID and wait for the User to sync from host.
                // When User syncs and its RefID matches, it will call SetLocalUser(this).
                LocalUserRefIDToInit = assignedRefID;
                _pendingLocalUserName = Environment.MachineName;
                _pendingAllocationStart = grantData.AllocationIDStart;
                _pendingAllocationEnd = grantData.AllocationIDEnd;

                LumoraLogger.Log($"[Sync] ProcessControlMessage: Stored pending local user RefID {assignedRefID} - waiting for User to sync from host");

                Session.World.SetStateVersion(grantData.StateVersion);

                // Scope new client-side allocations (the local user's tracking streams + the rigs that
                // InteractionLaser/HandTool/ControllerHandVisual/LocomotionController build at OnStart) into the
                // joining user's OWN byte. Without this they allocate in authority byte 0 and collide with the
                // host's objects there -> "Exception during initializing Worker of type Slot" and the laser/hands/
                // locomotion never build.
                //
                // AllocationIDStart is a PACKED RefID (the start of the range the host reserved for us), so its
                // POSITION is what an allocation context wants - the raw value is that position shifted up by a
                // byte plus the byte itself. Feeding the raw value in "worked" only because a fresh byte packs to
                // ~256 and that happened to clear the User object; once the host started resuming a recycled byte
                // above its previous high-water mark, the same mistake would re-pack a real position and multiply
                // it by 256 on every reuse, overflowing the 56-bit position space in a handful of rejoins.
                //
                // Add the shared headroom the host reserves for the User object it builds at the range start and
                // that object's own members, then seed the owned-block high-water to match. -xlinka
                var userByte = assignedRefID.GetUserByte();
                var grantedStart = new RefID(grantData.AllocationIDStart);
                var startPos = System.Math.Max(grantedStart.GetPosition(), 1UL) + RefIDConstants.USER_JOIN_HEADROOM;
                World.ReferenceController.SetAllocationContext(userByte, startPos);
                World.ReferenceController.SetOwnedStartPosition(userByte, startPos);
                LumoraLogger.Log($"[Sync] Scoped allocation to user namespace: byte={userByte}, startPos={startPos}");

                Session.World.OnJoinGrantReceived();
                break;

            case ControlMessage.Message.JoinReject:
                var rejectData = message.Payload != null && message.Payload.Length > 0
                    ? LegacyJoinRejectData.Decode(message.Payload)
                    : new LegacyJoinRejectData { Reason = "Join rejected" };
                var reason = string.IsNullOrWhiteSpace(rejectData.Reason) ? "Join rejected" : rejectData.Reason;
                LumoraLogger.Warn($"[Sync] ProcessControlMessage: Join rejected - {reason}");

                if (!World.IsAuthority)
                {
                    World.InitializationFailed(reason);
                    Session.Connections.HostConnection?.Close();
                }
                break;

            case ControlMessage.Message.JoinStartDelta:
            {
                // Seeing this a SECOND time means the host re-sent full state because our delta stream
                // (as it saw it) had a hole - not that we are joining.
                bool isResync = _joinStartDeltaSeen && !World.IsAuthority;

                LumoraLogger.Log("[Sync] ProcessControlMessage: JoinStartDelta received - can now accept delta updates");
                _acceptDeltas = true;
                _joinStartDeltaSeen = true;
                if (!World.IsAuthority && World.InitState == World.InitializationState.InitializingDataModel)
                {
                    // Enter the world now. The host considers us initialized once it has sent the full
                    // batch, so it starts pushing deltas immediately - staying out of Running just piles
                    // them up in the backlog, and a backlog big enough to hit the byte ceiling costs a
                    // whole extra full-state round trip. Records that didn't decode yet keep retrying in
                    // the background; if any
                    // never resolve the WorkerManager/decoder logs say exactly which type/element. We
                    // surface that here instead of blocking the whole join on it. -xlinka
                    int stillPending;
                    lock (_pendingLock) { stillPending = _pendingFullRecords.Count; }
                    if (stillPending > 0)
                    {
                        LumoraLogger.Warn($"[Sync] Entering world with {stillPending} initial record(s) still pending - they'll keep retrying. If content is missing, look above for 'UNRESOLVED TYPE' / 'Unknown component type'.");
                    }
                    LumoraLogger.Log("[Sync] JoinStartDelta: transitioning client world to Running");
                    World.OnFullStateReceived();
                }
                if (isResync)
                {
                    // The host refuses our deltas until we say we are standing on the state it just
                    // pushed. It cannot infer that: our own outgoing counter keeps running straight
                    // through the repair, so the acknowledgement is the only signal that separates
                    // "rebased" from "still sending pre-repair ops". -xlinka
                    _desynced = false;
                    var host = Session.Connections.HostConnection;
                    if (host != null)
                    {
                        var ack = new ControlMessage(ControlMessage.Message.ResyncComplete);
                        ack.Targets.Add(host);
                        EnqueueForTransmission(ack);
                        LumoraLogger.Log("[Sync] Rebased on host full state - acknowledging resync");
                    }
                }

                FlushPendingMessages();
                break;
            }

            case ControlMessage.Message.ResyncElements:
                HandleResyncElementsRequest(message);
                break;

            case ControlMessage.Message.ResyncComplete:
                if (World.IsAuthority && message.Sender != null)
                {
                    var ackState = GetLinkState(message.Sender);
                    // Rebase rather than clear the flag alone: the peer's counter never restarted, so
                    // the cursor has to be re-established from whatever it sends next.
                    ackState?.Rebase();
                    LumoraLogger.Log($"[Sync] {LinkName(message.Sender)} acknowledged its resync - accepting its deltas again from its next batch");
                }
                break;

            case ControlMessage.Message.RequestFullState:
                LumoraLogger.Log("[Sync] ProcessControlMessage: RequestFullState received from client");
                
                if (World.IsAuthority && Session.Connections.TryGetUser(message.Sender, out var requestingUser))
                {
                    LumoraLogger.Log($"[Sync] ProcessControlMessage: Sending full world state to user {requestingUser.UserName.Value}");
                    SendFullWorldState(message.Sender, requestingUser, "peer requested full state");

                    // Our cursor for ITS deltas is deliberately left alone: the peer lost OUR traffic,
                    // its own outgoing run never broke, so rebasing here would only throw away the
                    // baseline that would catch a real hole in its stream later.
                }
                else if (!World.IsAuthority)
                {
                    LumoraLogger.Warn("[Sync] ProcessControlMessage: Non-authority received RequestFullState - ignoring");
                }
                break;

            case ControlMessage.Message.ClockRequest:
                // Answer with our own reading and echo the asker's stamp back untouched, so the round
                // trip is measured entirely on their side and we keep no per-peer clock state.
                if (World.IsAuthority && SessionClock.TryDecodeProbe(message.Payload, out var probeStamp))
                {
                    var clockReply = new ControlMessage(ControlMessage.Message.ClockReply)
                    {
                        Payload = SessionClock.EncodeReply(probeStamp, World.SessionClock.SessionSeconds)
                    };
                    clockReply.Targets.Add(message.Sender);
                    EnqueueForTransmission(clockReply);
                }
                break;

            case ControlMessage.Message.ClockReply:
                if (!World.IsAuthority
                    && SessionClock.TryDecodeReply(message.Payload, out var replyStamp, out var authoritySeconds))
                {
                    World.SessionClock.ReceiveReply(replyStamp, authoritySeconds);
                }
                break;

            case ControlMessage.Message.AssetRequest:
            case ControlMessage.Message.AssetTransmissionStart:
            case ControlMessage.Message.AssetChunk:
            case ControlMessage.Message.AssetNextChunkRequest:
            case ControlMessage.Message.AssetNotAvailable:
                Session.AssetTransferer?.ProcessMessage(message);
                break;

            default:
                LumoraLogger.Log($"[Sync] ProcessControlMessage: {message.ControlMessageType}");
                break;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _running = false;

        _decodeThreadEvent.Set();
        _encodeThreadEvent.Set();
        _syncThreadEvent.Set();
        _refreshFinished.Set();

        _decodeThread?.Join(1000);
        _encodeThread?.Join(1000);
        _syncThread?.Join(1000);

        _decodeThreadEvent.Dispose();
        _encodeThreadEvent.Dispose();
        _syncThreadEvent.Dispose();
        _syncThreadInitEvent.Dispose();
        _refreshFinished.Dispose();
        _lastProcessingStopped.Dispose();
        _changesToConfirm.Clear();
        lock (_pendingLock)
        {
            _pendingFullRecords.Clear();
            _pendingDeltaRecords.Clear();
        }
        while (_pendingDeltaBatches.Count > 0)
        {
            _pendingDeltaBatches.First!.Value.Dispose();
            _pendingDeltaBatches.RemoveFirst();
        }
        _pendingDeltaBytes = 0;
        lock (_linkLock)
        {
            _linkSequences.Clear();
        }
        _elementsAwaitingResync.Clear();
        _resyncRequestQueue.Clear();
        while (_pendingStreamMessages.Count > 0)
        {
            _pendingStreamMessages.Dequeue().Dispose();
        }

        LumoraLogger.Log("[Sync] SessionSyncManager disposed");
    }
}

