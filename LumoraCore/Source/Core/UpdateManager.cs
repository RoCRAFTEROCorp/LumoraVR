// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lumora.Core;

public class UpdateManager
{
    private World _world;
    private readonly Queue<IImplementable> _pendingHookUpdates = new Queue<IImplementable>();
    private readonly Queue<IImplementable> _pendingSlotHookUpdates = new Queue<IImplementable>();
    private readonly List<IImplementable> _slotFlushBatch = new List<IImplementable>();
    private readonly HashSet<IImplementable> _queuedHookUpdates = new HashSet<IImplementable>();
    private readonly object _hookUpdatesLock = new object();

    private readonly HashSet<Slot> _pendingMovedSlots = new HashSet<Slot>();
    private readonly object _movedSlotsLock = new object();
    private readonly List<Slot> _movedSlotsBatch = new List<Slot>();
    private bool _processingMovedSlots;
    private float _currentDeltaTime = 0f;

    private SortedDictionary<int, List<IUpdatable>> _updateBuckets = new SortedDictionary<int, List<IUpdatable>>();
    private Queue<IUpdatable> _startupQueue = new Queue<IUpdatable>();
    private Queue<IUpdatable> _destructionQueue = new Queue<IUpdatable>();
    private SortedDictionary<int, Queue<IUpdatable>> _changeBuckets = new SortedDictionary<int, Queue<IUpdatable>>();
    private readonly List<Component> _lateUpdatables = new List<Component>();
    private readonly List<IUpdatable> _reDirtied = new List<IUpdatable>();

    // Flat mirrors of the two sorted-bucket dictionaries. A SortedDictionary foreach allocates its
    // traversal stack, and the per-frame loops here were the entire idle allocation floor of a world.
    // Rebuilt only when a bucket APPEARS (buckets are never removed, only emptied). -xlinka
    private readonly List<List<IUpdatable>> _updateBucketList = new List<List<IUpdatable>>();
    private readonly List<Queue<IUpdatable>> _changeBucketList = new List<Queue<IUpdatable>>();
    private bool _bucketListsDirty = true;

    private void EnsureBucketLists()
    {
        if (!_bucketListsDirty)
            return;
        _bucketListsDirty = false;
        _updateBucketList.Clear();
        foreach (var kvp in _updateBuckets)
            _updateBucketList.Add(kvp.Value);
        _changeBucketList.Clear();
        foreach (var kvp in _changeBuckets)
            _changeBucketList.Add(kvp.Value);
    }

    // Updatables whose startup threw - retried on later frames instead of dropped. A transient join-window
    // permission denial or a not-yet-synced ref shouldn't kill the component for the session. Bounded so a
    // genuinely broken OnStart doesn't spin forever. -xlinka
    private sealed class FailedStartup { public IUpdatable Target = null!; public int Attempts; public int CooldownFrames; }
    private readonly List<FailedStartup> _failedStartups = new();
    private const int MaxStartupRetries = 120;  // ~2s of frames, comfortably covers the join ownership-lag window
    private const int StartupRetryCooldown = 1; // next frame
    private int _changeUpdateIndex = 0;
    private Dictionary<IInitializable, List<IInitializable>> _initializableChildren = new Dictionary<IInitializable, List<IInitializable>>();

    public IUpdatable CurrentlyUpdating { get; private set; } = null!;

    // PER-FRAME UPDATE PROFILER. Opt-in (zero overhead when off) - the host turns it on only while the debug
    // console is attached. When on, each updatable's OnUpdate is timed and the cost is aggregated BOTH by
    // component type AND by the slot it lives on, so the profiler can show "which slots are expensive", not just
    // which component types. Holds the LATEST frame only (cleared at the start of each RunUpdates), so a reader
    // gets an instantaneous sample. Engine update + telemetry read are both on the main loop, so no lock. -xlinka
    public static bool ProfilingEnabled;
    private sealed class ProfBucket { public string Name = string.Empty; public long Ticks; public int Count; }
    private readonly Dictionary<string, ProfBucket> _profByType = new(StringComparer.Ordinal);
    private readonly Dictionary<Slot, ProfBucket> _profBySlot = new();

    public readonly struct ProfileEntry
    {
        public readonly string Name;
        public readonly double Ms;
        public readonly int Count;
        public ProfileEntry(string name, double ms, int count) { Name = name; Ms = ms; Count = count; }
    }

    public UpdateManager(World world)
    {
        _world = world;
    }

    public float DeltaTime => _currentDeltaTime;

    // How many updatables are actually on the per-frame dispatch. The profiler shows cost by type; this
    // shows the size of the set that cost is being paid over. -xlinka
    public int RegisteredUpdatableCount
    {
        get
        {
            EnsureBucketLists();
            int n = 0;
            for (int b = 0; b < _updateBucketList.Count; b++)
                n += _updateBucketList[b].Count;
            return n;
        }
    }

    // Runs before the first update.
    public void RegisterForStartup(IUpdatable updatable)
    {
        if (updatable != null && !updatable.IsDestroyed)
        {
            _startupQueue.Enqueue(updatable);
        }
    }

    public void RegisterForUpdates(IUpdatable updatable)
    {
        if (updatable == null || updatable.IsDestroyed)
            return;

        int order = updatable.UpdateOrder;
        if (!_updateBuckets.TryGetValue(order, out var bucket))
        {
            bucket = new List<IUpdatable>();
            _updateBuckets[order] = bucket;
            _bucketListsDirty = true;
        }
        // No duplicate scan: registration happens once per component (startup), and UpdateBucketChanged
        // removes before re-adding. The old Contains made every load O(n^2) in the order-0 bucket. -xlinka
        bucket.Add(updatable);
    }

    public void RegisterForLateUpdates(Component component)
    {
        if (component == null || component.IsDestroyed)
            return;

        _lateUpdatables.Add(component);
    }

    public void UnregisterFromLateUpdates(Component component)
    {
        if (component == null)
            return;

        _lateUpdatables.Remove(component);
    }

    // Only components that actually override OnLateUpdate land in this list (WorkerInitializer detects the
    // override), so this replaces a whole-tree walk that visited every component for a handful of overriders.
    public void RunLateUpdates(float delta)
    {
        for (int i = 0; i < _lateUpdatables.Count; i++)
        {
            var component = _lateUpdatables[i];
            if (component.IsDestroyed)
                continue;

            try
            {
                CurrentlyUpdating = component;
                if (ProfilingEnabled)
                {
                    long start = Stopwatch.GetTimestamp();
                    component.InternalRunLateUpdate(delta);
                    RecordProfile(component, Stopwatch.GetTimestamp() - start, PhaseLate);
                }
                else
                {
                    component.InternalRunLateUpdate(delta);
                }
            }
            catch (Exception ex)
            {
                Logging.Logger.Error($"UpdateManager: Error in late update for {component}: {ex.Message}");
            }
            finally
            {
                CurrentlyUpdating = null!;
            }
        }
    }

    public void UnregisterFromUpdates(IUpdatable updatable)
    {
        if (updatable == null)
            return;

        int order = updatable.UpdateOrder;
        if (_updateBuckets.TryGetValue(order, out var bucket))
        {
            bucket.Remove(updatable);
        }
    }

    public void UpdateBucketChanged(IUpdatable updatable)
    {
        if (updatable == null)
            return;

        foreach (var bucket in _updateBuckets.Values)
        {
            bucket.Remove(updatable);
        }

        if (!updatable.IsDestroyed && updatable.IsStarted)
        {
            RegisterForUpdates(updatable);
        }
    }

    public void RegisterForChanges(IUpdatable updatable)
    {
        if (updatable == null || updatable.IsDestroyed)
            return;

        int order = updatable.UpdateOrder;
        if (!_changeBuckets.TryGetValue(order, out var queue))
        {
            queue = new Queue<IUpdatable>();
            _changeBuckets[order] = queue;
            _bucketListsDirty = true;
        }
        queue.Enqueue(updatable);
    }

    public void RegisterForDestruction(IUpdatable updatable)
    {
        if (updatable != null)
        {
            _destructionQueue.Enqueue(updatable);
        }
    }

    public void RegisterHookUpdate(IImplementable component)
    {
        if (component != null && component.Hook != null)
        {
            lock (_hookUpdatesLock)
            {
                if (_queuedHookUpdates.Add(component))
                {
                    // Slots go to their own always-drained queue: a slot hook is the transform flush.
                    if (component is Slot)
                        _pendingSlotHookUpdates.Enqueue(component);
                    else
                        _pendingHookUpdates.Enqueue(component);
                }
            }
        }
    }

    // Same shape as the world's startup-drain budget: a big queue only exists when a load or join just
    // integrated a whole tree, and draining every OnStart in the first Running frame is the last chunk
    // of the load spike. While that initial backlog exists the drain takes a time-boxed bite per frame
    // (the floor keeps small worlds and the harnesses draining in one go); once the queue first comes up
    // empty the flag clears for good and a mid-session attach starts the same frame it always did. The
    // retry machinery already tolerates an OnStart seeing a not-yet-started sibling. -xlinka
    private const double StartupBudgetMs = 4.0;
    private const int StartupMinBites = 64;
    private bool _startupBacklog = true;

    // Same reasoning as the world's synchronous-action drain: the one-way startup flag only covers the
    // world's FIRST batch. A big object spawn dropped into a world that has been running for an hour
    // queues just as many startups at once, and running them all in one update is the spike. Any queue
    // that turns up this large gets budgeted until it is empty, whenever it happens. -xlinka
    private const int StartupBurstThreshold = 256;
    private bool _startupBurst;

    public void RunStartups()
    {
        if (_startupQueue.Count >= StartupBurstThreshold)
            _startupBurst = true;
        else if (_startupQueue.Count == 0)
            _startupBurst = false;

        if (!_startupBacklog && !_startupBurst)
        {
            while (_startupQueue.Count > 0)
            {
                var updatable = _startupQueue.Dequeue();
                if (updatable.IsDestroyed) continue;
                TryStartup(updatable, isRetry: false);
            }
            return;
        }

        long started = Stopwatch.GetTimestamp();
        int ran = 0;
        while (_startupQueue.Count > 0)
        {
            var updatable = _startupQueue.Dequeue();
            if (updatable.IsDestroyed) continue;
            TryStartup(updatable, isRetry: false);

            ran++;
            if (ran < StartupMinBites)
                continue;

            double elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= StartupBudgetMs)
                return;
        }

        _startupBacklog = false;
        _startupBurst = false;
    }

    // Re-attempt updatables whose startup previously threw. Drops one only when it finally starts, is
    // destroyed, or exhausts its retry budget (logged loudly then). Pumped each frame after RunStartups. -xlinka
    public void RunStartupRetries()
    {
        if (_failedStartups.Count == 0) return;

        for (int i = _failedStartups.Count - 1; i >= 0; i--)
        {
            var f = _failedStartups[i];
            if (f.Target.IsDestroyed || f.Target.IsStarted)
            {
                _failedStartups.RemoveAt(i);
                continue;
            }
            if (--f.CooldownFrames > 0)
                continue;

            f.CooldownFrames = StartupRetryCooldown;
            f.Attempts++;
            if (TryStartup(f.Target, isRetry: true))
            {
                _failedStartups.RemoveAt(i);
            }
            else if (f.Attempts >= MaxStartupRetries)
            {
                Logging.Logger.Error($"UpdateManager: startup permanently failed for {f.Target} after {f.Attempts} retries.");
                _failedStartups.RemoveAt(i);
            }
        }
    }

    // True if startup completed (IsStarted), false if it threw and was (re)queued for retry. -xlinka
    private bool TryStartup(IUpdatable updatable, bool isRetry)
    {
        try
        {
            CurrentlyUpdating = updatable;
            if (ProfilingEnabled)
            {
                long start = Stopwatch.GetTimestamp();
                updatable.InternalRunStartup();
                RecordProfile(updatable, Stopwatch.GetTimestamp() - start, PhaseStart);
            }
            else
            {
                updatable.InternalRunStartup();
            }
            return updatable.IsStarted;
        }
        catch (Exception ex)
        {
            // Log only the first failure, not every retry - a join-window denial retried 30x would otherwise
            // spam 30 identical lines. The final give-up (if any) is logged once by RunStartupRetries. -xlinka
            if (!isRetry)
            {
                Logging.Logger.Error($"UpdateManager: error in startup for {updatable} (will retry): {ex.Message}");
                if (!updatable.IsDestroyed)
                    _failedStartups.Add(new FailedStartup { Target = updatable, Attempts = 0, CooldownFrames = StartupRetryCooldown });
            }
            return false;
        }
        finally
        {
            CurrentlyUpdating = null!;
        }
    }

    public void RunUpdates(float deltaTime)
    {
        _currentDeltaTime = deltaTime;

        bool prof = ProfilingEnabled;

        EnsureBucketLists();
        for (int b = 0; b < _updateBucketList.Count; b++)
        {
            var bucket = _updateBucketList[b];
            for (int i = 0; i < bucket.Count; i++)
            {
                var updatable = bucket[i];
                if (!updatable.IsDestroyed)
                {
                    try
                    {
                        CurrentlyUpdating = updatable;
                        if (prof)
                        {
                            long start = Stopwatch.GetTimestamp();
                            updatable.InternalRunUpdate();
                            RecordProfile(updatable, Stopwatch.GetTimestamp() - start);
                        }
                        else
                        {
                            updatable.InternalRunUpdate();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logging.Logger.Error($"UpdateManager: Error in update for {updatable}: {ex.Message}");
                    }
                    finally
                    {
                        CurrentlyUpdating = null!;
                    }
                }
            }
        }
    }

    // Phase suffixes. Only OnUpdate goes in unsuffixed, because that is what the profiler has always
    // meant and a bare type name should keep meaning it. Everything else is tagged, so a component that
    // costs nothing per frame but two seconds on startup finally shows up as what it is. The SLOT view
    // stays untagged on purpose: there you want a slot's whole cost, not it split six ways. -xlinka
    private const string PhaseStart = " (start)";
    private const string PhaseChanges = " (changes)";
    private const string PhaseLate = " (late)";
    private const string PhaseDestroy = " (destroy)";

    private void RecordProfile(IUpdatable updatable, long ticks, string phaseSuffix = "")
    {
        if (updatable is not Component comp)
        {
            return;
        }

        var typeName = comp.GetType().Name + phaseSuffix;
        if (!_profByType.TryGetValue(typeName, out var tb))
        {
            tb = new ProfBucket { Name = typeName };
            _profByType[typeName] = tb;
        }
        tb.Ticks += ticks;
        tb.Count++;

        var slot = comp.Slot;
        if (slot != null)
        {
            if (!_profBySlot.TryGetValue(slot, out var sb))
            {
                sb = new ProfBucket { Name = string.IsNullOrEmpty(slot.SlotName.Value) ? "<unnamed slot>" : slot.SlotName.Value };
                _profBySlot[slot] = sb;
            }
            sb.Ticks += ticks;
            sb.Count++;
        }
    }

    // Copy the latest frame's update profile into the caller's lists (by component type, and by slot), converted
    // to milliseconds. Cheap and allocation-light; the host reads this for the debug console's profiler. -xlinka
    public void CollectProfile(List<ProfileEntry> byType, List<ProfileEntry> bySlot)
    {
        double tickToMs = 1000.0 / Stopwatch.Frequency;
        foreach (var kv in _profByType)
        {
            byType.Add(new ProfileEntry(kv.Value.Name, kv.Value.Ticks * tickToMs, kv.Value.Count));
        }
        foreach (var kv in _profBySlot)
        {
            bySlot.Add(new ProfileEntry(kv.Value.Name, kv.Value.Ticks * tickToMs, kv.Value.Count));
        }
    }

    public void RunChangeApplications()
    {
        _changeUpdateIndex++;

        EnsureBucketLists();
        for (int b = 0; b < _changeBucketList.Count; b++)
        {
            var queue = _changeBucketList[b];
            while (queue.Count > 0)
            {
                var updatable = queue.Dequeue();
                if (updatable.IsDestroyed || !updatable.IsChangeDirty)
                    continue;

                // Already ran this cycle: OnChanges re-dirtied it (directly or via a write loop between
                // two components). Running it again now can spin the drain forever; it stays dirty and
                // gets its turn next cycle instead. -xlinka
                if (updatable.LastChangeUpdateIndex == _changeUpdateIndex)
                {
                    _reDirtied.Add(updatable);
                    continue;
                }

                try
                {
                    CurrentlyUpdating = updatable;
                    if (ProfilingEnabled)
                    {
                        long start = Stopwatch.GetTimestamp();
                        updatable.InternalRunApplyChanges(_changeUpdateIndex);
                        RecordProfile(updatable, Stopwatch.GetTimestamp() - start, PhaseChanges);
                    }
                    else
                    {
                        updatable.InternalRunApplyChanges(_changeUpdateIndex);
                    }
                }
                catch (Exception ex)
                {
                    Logging.Logger.Error($"UpdateManager: Error in change application for {updatable}: {ex.Message}");
                }
                finally
                {
                    CurrentlyUpdating = null!;
                }
            }
        }

        if (_reDirtied.Count > 0)
        {
            for (int i = 0; i < _reDirtied.Count; i++)
            {
                RegisterForChanges(_reDirtied[i]);
            }
            _reDirtied.Clear();
        }
    }

    public void RunDestructions()
    {
        while (_destructionQueue.Count > 0)
        {
            var updatable = _destructionQueue.Dequeue();
            try
            {
                CurrentlyUpdating = updatable;
                if (ProfilingEnabled)
                {
                    long start = Stopwatch.GetTimestamp();
                    updatable.InternalRunDestruction();
                    RecordProfile(updatable, Stopwatch.GetTimestamp() - start, PhaseDestroy);
                }
                else
                {
                    updatable.InternalRunDestruction();
                }
            }
            catch (Exception ex)
            {
                Logging.Logger.Error($"UpdateManager: Error in destruction for {updatable}: {ex.Message}");
            }
            finally
            {
                CurrentlyUpdating = null!;
            }
        }
    }

    private readonly Dictionary<Type, double> _hookMsByType = new();
    private readonly Dictionary<Type, int> _hookCallsByType = new();

    // One hash per dictionary instead of a lookup and a store each; this runs once per hook call.
    private void AccumulateHookCost(Type hookType, double ms, int calls)
    {
        ref double totalMs = ref CollectionsMarshal.GetValueRefOrAddDefault(_hookMsByType, hookType, out _);
        totalMs += ms;
        ref int totalCalls = ref CollectionsMarshal.GetValueRefOrAddDefault(_hookCallsByType, hookType, out _);
        totalCalls += calls;
    }

    // Hook cost used to be cleared at the top of every ProcessHookUpdates, and World runs that TWICE per
    // frame (main pass, then again after late updates). So the numbers a reader saw depended entirely on
    // when it looked: the slow-frame log reads before the late pass and gets the main pass, anything
    // reading afterwards got ONLY the late pass and no way to know it. Clearing is now owned by the frame
    // instead of the pass, so both passes accumulate into one honest per-frame total. -xlinka
    public void BeginFrameProfiling()
    {
        _hookMsByType.Clear();
        _hookCallsByType.Clear();

        // The update profile used to clear itself at the top of RunUpdates. RunStartups runs BEFORE that
        // inside the same UpdateComponents call, so a component's OnStart cost was recorded and then
        // immediately wiped - startups were structurally unmeasurable, which is exactly the phase a world
        // spends its first seconds in. Clearing belongs to the frame, not to one phase of it. -xlinka
        _profByType.Clear();
        _profBySlot.Clear();
    }

    // Per hook type, milliseconds and call count for the frame so far. A type costing 10 ms across 500
    // calls is a different problem from one costing 10 ms in a single call, and the string form could
    // never tell them apart.
    public void CollectHookCost(List<ProfileEntry> into)
    {
        foreach (var pair in _hookMsByType)
        {
            _hookCallsByType.TryGetValue(pair.Key, out int calls);
            into.Add(new ProfileEntry(pair.Key.Name, pair.Value, calls));
        }
    }

    // Allocates; call only when reporting.
    public string DescribeHookCost(int top = 3)
    {
        if (_hookMsByType.Count == 0)
            return "";
        var sb = new System.Text.StringBuilder();
        int n = 0;
        foreach (var pair in _hookMsByType.OrderByDescending(p => p.Value))
        {
            if (n++ >= top || pair.Value < 0.5)
                break;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(pair.Key.Name).Append(' ').Append(pair.Value.ToString("F0")).Append("ms");
        }
        return sb.ToString();
    }

    public void ProcessHookUpdates(float deltaTime)
    {
        _currentDeltaTime = deltaTime;

        const int maxUpdates = 100000;
        int processed = 0;

        // Per-frame wall-clock budget so a burst of hook work spreads across frames instead of freezing the one.
        // Without it, an import that dirties N skinned renderers at once builds all N Godot meshes (ArrayMesh +
        // per-blendshape arrays + Skin) back-to-back in a single frame - a multi-second stall. A single hook can't
        // be split, so one heavy mesh still costs its frame, but N meshes now spread over N frames and the main
        // thread (which also renders + paints the loading bar) stays responsive. Normal frames drain a tiny queue
        // far under budget, so this is a no-op except during bursts. GetTimestamp is allocation-free. -xlinka
        const double budgetMs = 6.0;
        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double ticksToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // Slot hooks are the transform/visibility flush and cost microseconds each. The render camera
        // reads the engine's fresh head pose every frame, so any slot flush left behind by the budget
        // shows up as the world trailing the view - the dash sliding away in a fall, a dragged panel
        // rubber-banding. They drain completely every pass, ahead of and outside the budget; the budget
        // keeps governing the hooks that are actually expensive (mesh builds, canvas chunks). -xlinka
        //
        // The queue is taken in one bite per round rather than one entry per lock, and the cost of a round
        // is booked per hook type in one dictionary write instead of two per flush; a few hundred flushes a
        // frame made the bookkeeping a measurable share of what it was measuring. A flush that re-queues a
        // slot mid-round lands in the queue for the next round, and the loop keeps taking rounds until the
        // queue comes up empty, so nothing is left behind. -xlinka
        while (true)
        {
            lock (_hookUpdatesLock)
            {
                if (_pendingSlotHookUpdates.Count == 0)
                    break;
                while (_pendingSlotHookUpdates.Count > 0)
                {
                    var queued = _pendingSlotHookUpdates.Dequeue();
                    _queuedHookUpdates.Remove(queued);
                    _slotFlushBatch.Add(queued);
                }
            }

            Type runType = null!;
            double runMs = 0.0;
            int runCalls = 0;
            for (int i = 0; i < _slotFlushBatch.Count; i++)
            {
                var slotFlush = _slotFlushBatch[i];
                if (slotFlush == null || slotFlush.IsDestroyed ||
                    (slotFlush is Worker removedWorker && removedWorker.IsRemoved) || slotFlush.Hook == null)
                {
                    continue;
                }

                long slotStart = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    slotFlush.Hook.ApplyChanges();
                }
                catch (Exception ex)
                {
                    Logging.Logger.Error($"UpdateManager: Error in hook update for {slotFlush}: {ex}");
                }
                double slotMs = (System.Diagnostics.Stopwatch.GetTimestamp() - slotStart) * ticksToMs;

                var slotHookType = slotFlush.Hook.GetType();
                if (slotHookType != runType)
                {
                    if (runType != null)
                        AccumulateHookCost(runType, runMs, runCalls);
                    runType = slotHookType;
                    runMs = 0.0;
                    runCalls = 0;
                }
                runMs += slotMs;
                runCalls++;
            }
            if (runType != null)
                AccumulateHookCost(runType, runMs, runCalls);
            _slotFlushBatch.Clear();
        }

        while (true)
        {
            IImplementable implementable;
            lock (_hookUpdatesLock)
            {
                if (_pendingHookUpdates.Count == 0)
                {
                    return;
                }

                implementable = _pendingHookUpdates.Dequeue();
                _queuedHookUpdates.Remove(implementable);
            }

            if (implementable == null || implementable.IsDestroyed ||
                (implementable is Worker worker && worker.IsRemoved) ||
                implementable.Hook == null)
            {
                continue;
            }

            // Contain hook failures like every other phase. A throwing hook used to
            // abort the whole world update mid-frame, which left queued startups,
            // changed-element processing and destructions undrained - destroyed UI
            // kept getting hover writes and the same hook re-threw every frame. - xlinka
            long hookStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                implementable.Hook.ApplyChanges();
            }
            catch (Exception ex)
            {
                Logging.Logger.Error($"UpdateManager: Error in hook update for {implementable}: {ex}");
            }
            // Per-type cost for the slow-frame report: a timestamp pair per hook, no allocation, so the
            // question "which hook ate the frame" is answerable without a profiler attached.
            var hookType = implementable.Hook.GetType();
            double hookMs = (System.Diagnostics.Stopwatch.GetTimestamp() - hookStart) * ticksToMs;
            AccumulateHookCost(hookType, hookMs, 1);

            processed++;
            if (processed >= maxUpdates)
            {
                Logging.Logger.Warn("UpdateManager: Hook update queue hit safety limit.");
                return;
            }

            // Out of frame budget: leave the rest of the queue for next frame so rendering isn't stalled. The
            // undrained items stay in _pendingHookUpdates/_queuedHookUpdates and get picked up next tick.
            if ((System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * ticksToMs >= budgetMs)
            {
                return;
            }
        }
    }

    // Deduplicated; queued slots fire WorldTransformChanged once, in ProcessMovedSlots.
    public void RegisterMovedSlot(Slot slot)
    {
        if (slot == null || slot.IsDestroyed)
            return;
        lock (_movedSlotsLock)
        {
            _pendingMovedSlots.Add(slot);
        }
    }

    // Runs before hook updates so a handler that re-drives a transform reaches the engine the same frame.
    // Returns the number fired.
    public int ProcessMovedSlots()
    {
        // Reused batch buffer. A handler is free to move more slots (they queue for the next pass), but a
        // handler that fires a nested pass gets a throwaway list so it can't trample the outer iteration.
        bool outer = !_processingMovedSlots;
        List<Slot> batch = outer ? _movedSlotsBatch : new List<Slot>();

        lock (_movedSlotsLock)
        {
            if (_pendingMovedSlots.Count == 0)
                return 0;
            batch.Clear();
            batch.AddRange(_pendingMovedSlots);
            _pendingMovedSlots.Clear();
        }

        // Parents before children, so a child handler reading parent state sees it updated.
        batch.Sort((a, b) => a.Depth.CompareTo(b.Depth));

        _processingMovedSlots = true;
        try
        {
            for (int i = 0; i < batch.Count; i++)
            {
                var slot = batch[i];
                if (slot == null || slot.IsDestroyed)
                    continue;
                try
                {
                    slot.FireWorldTransformChanged();
                }
                catch (Exception ex)
                {
                    Logging.Logger.Error($"UpdateManager: Error in moved event for {slot}: {ex}");
                }
            }
            return batch.Count;
        }
        finally
        {
            if (outer)
            {
                _processingMovedSlots = false;
                _movedSlotsBatch.Clear();
            }
        }
    }

    public void Clear()
    {
        lock (_hookUpdatesLock)
        {
            _pendingHookUpdates.Clear();
            _pendingSlotHookUpdates.Clear();
            _queuedHookUpdates.Clear();
        }
        _slotFlushBatch.Clear();
        lock (_movedSlotsLock)
        {
            _pendingMovedSlots.Clear();
        }
        _movedSlotsBatch.Clear();
        _updateBuckets.Clear();
        _startupQueue.Clear();
        _failedStartups.Clear();
        _destructionQueue.Clear();
        _changeBuckets.Clear();
        _initializableChildren.Clear();
    }

    public void AddInitializableChild(IInitializable parent, IInitializable child)
    {
        if (parent == null || child == null)
            return;

        if (!_initializableChildren.TryGetValue(parent, out var list))
        {
            list = new List<IInitializable>();
            _initializableChildren[parent] = list;
        }

        list.Add(child);
    }

    public void EndInitPhaseInChildren(IInitializable parent)
    {
        if (parent == null)
            return;

        if (_initializableChildren.TryGetValue(parent, out var children))
        {
            foreach (var child in children)
            {
                try
                {
                    if (child.IsInInitPhase)
                    {
                        child.EndInitPhase();
                    }
                }
                catch (Exception ex)
                {
                    Logging.Logger.Error($"UpdateManager: Error ending init phase for {child}: {ex.Message}");
                }
            }

            _initializableChildren.Remove(parent);
        }
    }
}
