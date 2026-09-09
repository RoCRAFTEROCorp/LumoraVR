// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumora.Core.Networking.Session;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Components;
using Lumora.Core.Localization;
using Lumora.Core.Persistence;
using Lumora.Warden;
using Lumora.Nexus.Cloud;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

public class World : IPermissionWorldFacts
{
	public enum WorldState
	{
		Created,

		InitializingNetwork,

		WaitingForJoinGrant,

		InitializingDataModel,

		Running,

		Failed,

		Destroyed
	}

	public enum InitializationState
	{
		Created,
		InitializingNetwork,
		WaitingForJoinGrant,
		InitializingDataModel,
		Finished,
		Failed
	}

	public enum WorldFocus
	{
		Background,
		Focused,
		Overlay,
		PrivateOverlay
	}

	public enum WorldEvent
	{
		OnFocusChanged,
		OnUserJoined,
		OnUserLeft,
		OnWorldDestroy,
		OnUserSpawn,
		OnWorldSaved
	}

	public class WorldMetrics
	{
		public int SlotCount { get; internal set; }

		public int ComponentCount { get; internal set; }

		public int SyncElementCount { get; internal set; }

		public long RefIDsAllocated { get; internal set; }

		public long MessagesSent { get; internal set; }

		public long MessagesReceived { get; internal set; }

		public long BytesSent { get; internal set; }

		public long BytesReceived { get; internal set; }

		public long UpdatesProcessed { get; internal set; }

		public double AverageUpdateTimeMs { get; internal set; }

		public double PeakUpdateTimeMs { get; internal set; }

		public double GodotFps { get; set; }

		public double GodotFrameTimeMs { get; set; }

		// Godot CPU process time. Named RenderTimeMs for compatibility.
		public double RenderTimeMs { get; set; }

		public double PhysicsTimeMs { get; set; }

		public long VideoMemoryBytes { get; set; }

		public int GodotObjectCount { get; set; }

		public int GodotNodeCount { get; set; }

		public override string ToString()
		{
			return $"Slots: {SlotCount}, Components: {ComponentCount}, Users: N/A, " +
			       $"Messages: {MessagesSent}/{MessagesReceived}, Updates: {UpdatesProcessed}";
		}
	}

	public enum WorldAccessLevel
	{
		Private,
		LAN,
		Contacts,
		ContactsPlus,
		GroupMembers,
		GroupPlus,
		GroupPublic,
		RegisteredUsers,
		Anyone,
	}


	private readonly Dictionary<string, List<Slot>> _slotsByTag = new();
	private readonly List<Slot> _rootSlots = new();
	private readonly List<User> _users = new();
	private readonly List<User> _joinedUsers = new();
	private readonly List<User> _leftUsers = new();
	// Users whose body hasn't finished appearing yet. A joined user only counts as SPAWNED once its
	// UserRoot is bound AND that root has a head node - on the authority that lands right after the
	// scaffold build, on a peer whenever the replicated subtree finishes arriving, and this is the only
	// test that answers the same way on both. Watched instead of hooked because no single call site owns
	// the completion on every peer. The watch only runs while something is actually listening. -xlinka
	private readonly List<User> _awaitingSpawn = new();
	private readonly List<User> _spawnedUsers = new();
	private readonly List<string> _savedWorldPaths = new();

	private Networking.Sync.ReplicatedSlotCollection? _slotCollection;
	private Networking.Sync.ReplicatedUserCollection? _userCollection;

	// The flat world slot registry. Exposed so the permission gate can tell a guest's own-byte slot
	// REGISTRATION (allowed - that's how a user spawns its own content) from an own-byte add onto a
	// host-owned per-slot collection like a component list (denied - that would bolt components onto host
	// geometry). See DataModelPermissions.Authorize. -xlinka
	internal IWorldElement? SlotRegistryElement => _slotCollection;
	private readonly List<IWorldEventReceiver>[] _worldEventReceivers;
	private WorldState _state = WorldState.Created;
	private InitializationState _initState = InitializationState.Created;
	private Session _session = null!;
	private HookManager _hookManager;
	private TrashBin _trashBin;
	private RefIDAllocator _refIDAllocator;
	private WorldFocus _focus = WorldFocus.Background;
	private HookTypeRegistry _hookTypes;
	private UpdateManager _updateManager;
	private Queue<Action> _synchronousActions = new Queue<Action>();
	private object _syncLock = new object();

	// Set by StartSessionDeferred; returns true once the payload landed and the world finished starting.
	// Polled by the world manager, always on the world thread.
	private Func<bool>? _pendingSessionStart;

	// A bulk load or a join queues one deferred hook creation per slot, and the drain used to run the whole
	// backlog in a single update - that is the startup flush that stalls the first frames and has tripped a
	// driver-level race on the first 3D frame. While a backlog exists the drain takes one time-boxed bite per
	// update; the moment it comes up empty the flag clears for good and RunSynchronously is back to its
	// same-frame semantics. The minimum action count keeps small worlds (and the harnesses) draining in one
	// go regardless of how slow the machine is. -xlinka
	private const double StartupDrainBudgetMs = 4.0;
	private const int StartupDrainMinActions = 64;
	private bool _startupActionBacklog = true;

	// The startup flag alone only covers the FIRST backlog. A join's full batch arrives long after that
	// has cleared - one RunSynchronously per replicated slot, thousands of them, landing in a single
	// update - and so does a big inventory spawn. So any queue that turns up this large gets the same
	// time-boxed treatment, and keeps it until it is empty, whenever it happens. Anything under the
	// threshold still runs same-frame, which is what the spawn path's inline contract depends on. -xlinka
	private const int BurstDrainThreshold = 256;
	private bool _burstDraining;

	// LOCAL LOAD HOOK BACKLOG
	// Integrating a saved tree used to build one platform node per slot inline inside Slot.Initialize, so a
	// 500-slot world paid all 500 node creations in the frame that integrated it. Inside a deferral scope
	// each slot queues its creation here instead, and the scope's owner drains it in time-boxed bites while
	// the world is still held pre-Running - nothing user-visible can read a half-hooked tree, because
	// nothing ticks yet. Untouched outside a scope, so a spawn, an import or a mid-session load still gets
	// its hook the moment the slot initializes. -xlinka
	private readonly Queue<Action> _deferredHookCreations = new();
	private int _deferHookDepth;

	// Coroutines ticked once per Update (see StartCoroutine) and time-delayed one-shots (see
	// RunInSeconds). The buffered add list lets a coroutine start another without mutating the live
	// list mid-tick. All guarded by _syncLock.
	private readonly List<CoroutineRunner> _coroutines = new();
	private readonly List<CoroutineRunner> _coroutinesToAdd = new();
	private readonly List<DelayedAction> _delayedActions = new();
	private readonly List<UpdateAction> _updateActions = new();
	private readonly List<NextUpdateAction> _nextUpdateActions = new();
	// Guards the WhenRunning/WhenDestroyed deferred-or-immediate accessors so a late subscriber can't
	// slip between the state flip and the fan-out. Separate from _syncLock on purpose - _syncLock is held
	// while running queued user actions and must not be entangled with this. -xlinka
	private readonly object _stateLock = new object();
	private readonly WorldMetrics _metrics = new WorldMetrics();
	private readonly DataModelPermissionController _dataModelPermissions;

	// Owned-namespace build is armed once the local user's allocation byte is known. Gated so a per-peer
	// spawn that runs a frame too early falls back to NOT building (and retries) instead of minting into
	// authority byte 0 and resurrecting the permission-bypass problem. -xlinka
	private bool _localAllocationReady;
	// First valid local-user assignment wins for the session. Guards a stray second SetLocalUser from
	// silently re-pointing the actor mid-session. -xlinka
	private bool _localUserSet;
	// When the client entered data-model init (join window opened). Diagnostics + bounded-wait logging. 0 = not joining. -xlinka
	private double _dataModelInitStartTime;
	// Bounded deferral of Running on a client while we wait for the local user to resolve. -xlinka
	private int _awaitingLocalUserFrames;
	private const int MaxAwaitLocalUserFrames = 120; // ~2s @60, generous for a slow initial decode
	// Missing-root respawn watchdog state - re-fires spawn if a set LocalUser never gets a body. -xlinka
	private int _missingRootFrames;
	private int _respawnAttempts;
	private const int MissingRootGraceFrames = 180; // ~3s
	private const int MaxRespawnAttempts = 3;

	// Seconds since this client began data-model init, or 0 on the authority / before join. -xlinka
	public double TimeSinceDataModelInit => _dataModelInitStartTime <= 0 ? 0 : TotalTime - _dataModelInitStartTime;

	// Static global hook type registry (shared across all worlds)
	private static HookTypeRegistry _staticHookTypes = new HookTypeRegistry();

	public IWorldHook Hook { get; set; } = null!;

	// Godot scene access - set by WorldHook
	public object GodotSceneRoot { get; set; } = null!;

	private Physics.WorldPhysics _physics = null!;

	// The platform owns the simulation.
	public Physics.WorldPhysics Physics => _physics ??= new Physics.WorldPhysics(this);

	public Management.WorldManager WorldManager { get; internal set; } = null!;

	private static int _worldEventTypeCount = Enum.GetValues(typeof(WorldEvent)).Length;

	public static HookTypeRegistry HookTypes => _staticHookTypes;

	public event Action<WorldState, WorldState>? OnStateChanged;

	private Action<World>? _whenRunning;

	// Fires once the world is Running. If it's ALREADY running when you subscribe, your handler runs
	// immediately (inline, on the calling thread); otherwise it's queued and fires at the transition.
	// Either way a late subscriber never misses the running edge. Unsubscribe with -=. -xlinka
	public event Action<World> WhenRunning
	{
		add
		{
			// Fast path: already running, run it now without holding the lock.
			if (_state == WorldState.Running)
			{
				value(this);
				return;
			}
			lock (_stateLock)
			{
				// Re-check under the lock: the transition may have landed between the check above and here.
				if (_state == WorldState.Running)
					value(this);
				else
					_whenRunning += value;
			}
		}
		remove
		{
			lock (_stateLock)
			{
				_whenRunning -= value;
			}
		}
	}

	private Action<World>? _whenDestroyed;

	// Fires once the world is destroyed. If it's ALREADY destroyed when you subscribe, your handler runs
	// immediately; otherwise it's queued and fires at teardown. A late subscriber never misses the
	// destroyed edge - handy for cleanup that may register after the world is already gone. -xlinka
	public event Action<World> WhenDestroyed
	{
		add
		{
			if (_state == WorldState.Destroyed)
			{
				value(this);
				return;
			}
			lock (_stateLock)
			{
				if (_state == WorldState.Destroyed)
					value(this);
				else
					_whenDestroyed += value;
			}
		}
		remove
		{
			lock (_stateLock)
			{
				_whenDestroyed -= value;
			}
		}
	}

	public WorldState State => _state;

	// Set at host time from the world's allowed modes and applied to the permission gate when the world starts
	// running. Not a live toggle - the Social/Event lock is enforced host-authoritatively and cannot be turned
	// off in-session.
	public WorldMode Mode
	{
		get => Configuration?.Mode?.Value ?? WorldMode.Builder;
		set
		{
			// Baked at host time, not a live toggle. Once the world is running the mode is fixed for
			// the session - re-host (or load a differently-moded world) to change it.
			if (_state == WorldState.Running)
			{
				if (value != Mode)
					LumoraLogger.Warn($"World.Mode is baked for the session and can't change live (ignored {value}).");
				return;
			}
			var c = Configuration;
			if (c != null)
				c.Mode.Value = value;
		}
	}

	// False in Social/Event worlds. This is a UX/availability hint - the actual lock is the host-authoritative
	// SocialLock gate, which reads the same floor.
	public bool AllowsWorldEditing => !WorldModePolicy.SocialLockFloor(Mode);

	public bool AllowsItemSpawning => WorldModePolicy.AllowsOwnItems(Mode);

	public InitializationState InitState => _initState;

	public string InitializationFailureReason { get; private set; } = "";

	public Slot RootSlot { get; private set; } = null!;

	// Live collider registry for this world, maintained by Collider on attach/destroy. Lets raycasts
	// iterate a flat list instead of walking the entire slot tree every frame, per laser. Holds ALL
	// colliders (active or not); raycast callers filter by their own candidacy checks, so a momentarily
	// stale entry (e.g. a just-destroyed collider) can't produce a wrong hit. -xlinka
	private readonly HashSet<Components.Collider> _colliders = new();

	public void RegisterCollider(Components.Collider collider)
	{
		if (collider != null)
			_colliders.Add(collider);
	}

	public void UnregisterCollider(Components.Collider collider)
	{
		if (collider != null)
			_colliders.Remove(collider);
	}

	// Copy the live colliders into a caller-provided (reusable) buffer - allocation-free. -xlinka
	public void CopyCollidersTo(List<Components.Collider> buffer)
	{
		buffer.Clear();
		foreach (var c in _colliders)
			buffer.Add(c);
	}

	// Live interaction-target registry for this world, maintained centrally by ComponentBase on init/destroy
	// (so EVERY IInteractionTarget component is in here, no per-implementer opt-in to forget). Lets the laser
	// iterate a flat list instead of walking the entire slot tree every frame, per laser. Holds ALL targets
	// (active, disabled, even momentarily stale); the laser filters each candidate by enabled/active/hierarchy
	// at use-site, so a stale entry can't produce a wrong hit. A target moved across worlds re-registers via
	// its re-init (same caveat as the collider registry). -xlinka
	private readonly HashSet<Components.Interaction.IInteractionTarget> _interactionTargets = new();

	public void RegisterInteractionTarget(Components.Interaction.IInteractionTarget target)
	{
		if (target != null)
			_interactionTargets.Add(target);
	}

	public void UnregisterInteractionTarget(Components.Interaction.IInteractionTarget target)
	{
		if (target != null)
			_interactionTargets.Remove(target);
	}

	// Copy the live interaction targets into a caller-provided (reusable) buffer - allocation-free. -xlinka
	public void CopyInteractionTargetsTo(List<Components.Interaction.IInteractionTarget> buffer)
	{
		buffer.Clear();
		foreach (var t in _interactionTargets)
			buffer.Add(t);
	}

	public Sync<string> WorldName { get; private set; }

	public Sync<string> SessionID { get; private set; }

	// -1 means local-only world.
	public int AuthorityID { get; set; } = -1;

	public int LocalID { get; set; } = -1;

	public bool IsAuthority => AuthorityID == -1 || AuthorityID == LocalID;

	public Session Session => _session;

	public IReadOnlyList<Uri> SessionURLs => (_session?.Metadata?.SessionURLs as IReadOnlyList<Uri>) ?? Array.Empty<Uri>();

	public SyncController SyncController { get; private set; } = null!;

	// Tracks fields that just lost their driving link so the sync loop can re-broadcast their
	// real current value to peers (authority only).
	public LinkManager LinkManager { get; private set; } = null!;

	public ReferenceController ReferenceController { get; private set; } = null!;

	public WorkerManager Workers { get; private set; } = null!;

	public HookManager HookManager => _hookManager;

	public TrashBin TrashBin => _trashBin;

	public RefIDAllocator RefIDAllocator => _refIDAllocator;

	public HookTypeRegistry InstanceHookTypes => _hookTypes;

	public UpdateManager UpdateManager => _updateManager;

	public User LocalUser { get; private set; } = null!;

	public float TimeScale { get; set; } = 1.0f;

	// Per-world clock: frame deltas (raw/clamped/smoothed), total time, update index, FPS. Read this instead of
	// threading deltas through call chains.
	public WorldClock Time { get; } = new WorldClock();

	// Seconds.
	public double TotalTime => Time.TotalTime;

	// Anything whose PHASE has to match across peers - a spinner's angle, an animator's anchor - reads
	// SessionSeconds from here instead of the world clock, which starts at zero per peer, or wall clock, which
	// only agrees as well as the machines happen to.
	public SessionClock SessionClock { get; } = new SessionClock();

	public double SessionSeconds => SessionClock.SessionSeconds;

	// Incremented every sync cycle.
	public ulong SyncTick { get; private set; }

	// Incremented whenever the authority makes a state change.
	public ulong StateVersion { get; private set; }

	public bool IsDestroyed { get; internal set; }

	public bool IsDisposed { get; private set; }

	// Managed id of the thread that drives this world's update loop, or 0 before the first update. Stamped
	// rather than assumed: a world is built on whatever thread the loader happened to be on, and only the
	// update loop settles the answer. 0 never matches a real id, so an unknown thread reads as "not the
	// world thread", which is the safe way for a context switch to be wrong. -xlinka
	public int WorldThreadId { get; private set; }

	public bool IsOnWorldThread => WorldThreadId == Environment.CurrentManagedThreadId;

	// Seconds.
	public float LastDelta => Time.RawDelta;

	public string Name => WorldName?.Value ?? "Unknown";

	public bool IsFocused => _focus == WorldFocus.Focused;

	public int UserCount
	{
		get
		{
			lock (_users)
			{
				return _users.Count;
			}
		}
	}

	public WorldFocus Focus
	{
		get => _focus;
		set
		{
			if (_focus == value) return;
			_focus = value;

			if (Hook is IWorldHook worldHook)
			{
				worldHook.ChangeFocus(value);
			}
		}
	}

	public WorldMetrics Metrics => _metrics;

	// On the authority it's created on first access; on a client it's null until state-synced - callers that
	// may run client-side should null-check.
	public WorldSettings Configuration
	{
		get
		{
			var root = RootSlot;
			if (root == null)
				return null!;
			var settings = root.GetComponent<WorldSettings>();
			if (settings == null && IsAuthority)
				settings = root.AttachComponent<WorldSettings>();
			return settings!;
		}
	}

	public DataModelPermissionController DataModelPermissions => _dataModelPermissions;

	// The host's replicated permission config: role assignments, per-role capability toggles, escalation
	// thresholds. Created on demand on the authority, null on a client until state-synced - same shape as
	// Configuration, and null-check it the same way.
	public WorldPermissionConfig PermissionConfig
	{
		get
		{
			var root = RootSlot;
			if (root == null)
				return null!;
			var config = root.GetComponent<WorldPermissionConfig>();
			if (config == null && IsAuthority)
				config = root.AttachComponent<WorldPermissionConfig>();
			return config!;
		}
	}

	// PERMISSION GATE VIEW
	// The gate reads world state through these, and only these. They are explicit so nothing else picks
	// them up by accident, and every one of them is host-authoritative state the local client cannot
	// author for itself. -xlinka

	bool IPermissionWorldFacts.IsRunning => _state == WorldState.Running;

	IPermissionActor? IPermissionWorldFacts.LocalActor => LocalUser;

	IPermissionTarget? IPermissionWorldFacts.SlotRegistry => SlotRegistryElement;

	// The delta batch the authority is validating right now, or null. Set by SyncController for the length
	// of ValidateDeltaMessages and read only by BatchClaimsGrab. It exists because a grab is authored as
	// several records in ONE batch and the authority judges every record before it decodes any of them, so
	// "is this user the holder" is answered by pre-batch state at the exact moment a pickup needs it to be
	// answered by post-batch state. -xlinka
	internal Networking.Sync.BinaryMessageBatch? ValidatingBatch { get; set; }

	// Whether the batch under validation also carries a record that would make this actor the holder of
	// that grabbable. Peeks at another record in the same stream, so it saves and restores the shared
	// reader position - the caller is mid-record and the decode path re-seeks by offset. -xlinka
	bool IPermissionWorldFacts.BatchClaimsGrab(IPermissionGrabSurface surface, IPermissionActor actor)
	{
		var batch = ValidatingBatch;
		if (batch == null || surface == null || actor is not User claimant)
			return false;

		// The holder ref lives on whichever grab surface the write landed on - a Grabbable, a dynamic
		// bone chain, anything implementing the holder seam - so ask the surface instead of assuming
		// the prop case. -xlinka
		var holderCarrier = surface as Components.IGrabHolderSurface
			?? (surface as Slot)?.GetComponent<Component>(c => c is Components.IGrabHolderSurface) as Components.IGrabHolderSurface;
		if (holderCarrier == null || (holderCarrier is Component holderComponent && holderComponent.IsDestroyed))
			return false;

		int index = batch.FindDataRecordIndex(holderCarrier.GrabHolderRefId);
		if (index < 0)
			return false;

		var reader = batch.SeekDataRecord(index);
		var stream = reader.BaseStream;
		if (!stream.CanSeek)
			return false;

		long resume = stream.Position;
		RefID claimed;
		try
		{
			claimed = new RefID(reader.ReadUInt64());
		}
		catch (Exception)
		{
			// A truncated record proves nothing; the record carrying it is refused on its own merits.
			return false;
		}
		finally
		{
			stream.Position = resume;
		}

		return ReferenceController?.GetObjectOrNull(in claimed) is Components.Interaction.Grabber grabber
			&& !grabber.IsDestroyed
			&& ReferenceEquals(grabber.OwningUser, claimant);
	}

	// PERSISTENCE
	// Serialize/restore the whole world (its slot tree) to/from a data tree. Permissions are NOT
	// serialized: they're a hard runtime policy derived from ownership/authority, applied live.
	// The local home is hosted by the local user (authority), so save/load pass the permission gate.

	private const int WorldFormatVersion = 1;

	public DataTreeDictionary SaveWorld()
	{
		var translator = new ReferenceTranslator();
		var control = new SaveControl(RootSlot, translator);

		// Claim an identity for every member the tree points at before any of it serializes. Without
		// this a plain reference resolves on load only when its target happened to be written first.
		control.ReserveSubtreeIdentities(RootSlot);

		// Save the tree first so type versions are collected before they're stored.
		var rootNode = RootSlot.Save(control);

		var dictionary = new DataTreeDictionary();
		dictionary.Add("FormatVersion", WorldFormatVersion);
		dictionary.Add("Name", WorldName.Value);

		var typeVersions = new DataTreeDictionary();
		control.StoreTypeVersions(typeVersions);
		dictionary.Add("TypeVersions", typeVersions);

		// World settings persist as the WorldSettings component on the root slot (part of rootNode),
		// so there's no separate "Config" blob.
		dictionary.Add("Root", rootNode);
		return dictionary;
	}

	public void LoadWorld(DataTreeDictionary dictionary)
	{
		var translator = new ReferenceTranslator();
		var control = new LoadControl(this, translator);

		try
		{
			if (dictionary.TryGetDictionary("TypeVersions") is { } typeVersions)
				control.LoadTypeVersions(typeVersions);

			if (dictionary.ContainsKey("Name"))
				WorldName.Value = dictionary.ExtractOrDefault("Name", WorldName.Value);

			if (dictionary.TryGetNode("Root") is { } rootNode)
				RootSlot.Load(rootNode, control);

			// The WorldSettings component (with the persisted Mode) is now loaded under the root. If
			// we loaded into an already-running authority world, re-apply the mode's permission preset
			// (otherwise StartRunning applies it).
			if (_state == WorldState.Running && IsAuthority)
			{
				WorldModePermissions.Apply(this, Mode);
				// The saved role assignments and toggles came up with the tree; push them at the gate now
				// that the mode preset underneath them has been re-applied.
				RootSlot?.GetComponent<WorldPermissionConfig>()?.Republish();
			}
		}
		finally
		{
			// Always resolve deferred refs + report leftovers, even if a sub-load threw, so a
			// partial load doesn't leave dangling waiters. A thrown load still propagates to the
			// caller (WorldStorage.LoadFromFile) so it can fall back to the template.
			control.FinishLoad();
		}
	}

	public string GetDiagnostics()
	{
		var sb = new System.Text.StringBuilder();
		sb.AppendLine($"World: {Name}");
		sb.AppendLine($"State: {State}");
		sb.AppendLine($"SessionID: {SessionID?.Value ?? "N/A"}");
		sb.AppendLine($"IsAuthority: {IsAuthority}");
		sb.AppendLine($"Users: {UserCount}");
		sb.AppendLine($"TotalTime: {TotalTime:F2}s");
		sb.AppendLine($"SyncTick: {SyncTick}");
		sb.AppendLine($"StateVersion: {StateVersion}");
		sb.AppendLine($"Metrics: {_metrics}");
		return sb.ToString();
	}

	internal void UpdateMetrics()
	{
		_metrics.SlotCount = RootSlot?.GetDescendants(true).Count() ?? 0;
		_metrics.ComponentCount = RootSlot?.GetDescendants(true).Sum(s => s.ComponentCount) ?? 0;
		_metrics.RefIDsAllocated = ReferenceController?.ObjectCount ?? 0;
	}

	public event Action<Slot> OnSlotAdded = null!;

	public event Action<Slot> OnSlotRemoved = null!;

	public World()
	{
		WorldName = new Sync<string>(null, "New World");
		SessionID = new Sync<string>(null, Guid.NewGuid().ToString());
		_hookManager = new HookManager(this);
		_trashBin = new TrashBin(this);
		_refIDAllocator = new RefIDAllocator(this);
		_hookTypes = new HookTypeRegistry();
		_updateManager = new UpdateManager(this);
		_dataModelPermissions = new DataModelPermissionController(this);

		int length = Enum.GetValues(typeof(WorldEvent)).Length;
		_worldEventReceivers = new List<IWorldEventReceiver>[length];
		for (int i = 0; i < length; i++)
		{
			_worldEventReceivers[i] = new List<IWorldEventReceiver>();
		}
	}

	public static World LocalWorld(Engine engine, string name, Action<World> init = null!)
	{
		var world = new World();
		world.WorldName.Value = name;
		world.AuthorityID = -1; // Local-only (no authority)
		world.LocalID = -1;
		world.IsDestroyed = false;
		world.IsDisposed = false;

		world.Initialize();

		// Run initialization callback first so event receivers (like SimpleUserSpawn) are registered
		init?.Invoke(world);

		// Create local user (this triggers OnUserJoined after SimpleUserSpawn is ready)
		world.CreateHostUser("LocalUser");

		world.StartRunning();
		LumoraLogger.Log($"Local world '{name}' created and started");

		return world;
	}

	public static World StartSession(Engine engine, string name, ushort port, string hostUserName = null!, Action<World> init = null!)
	{
		return StartSession(engine, name, port, hostUserName, SessionVisibility.Private, 16, init);
	}

	public static World StartSession(
		Engine engine,
		string name,
		ushort port,
		string hostUserName,
		SessionVisibility visibility,
		int maxUsers = 16,
		Action<World> init = null!)
	{
		var world = BeginSession(name, port, hostUserName, visibility, maxUsers);

		// Run initialization callback first so event receivers (like SimpleUserSpawn) are registered
		init?.Invoke(world);

		world.CompleteSessionStart(name, port, hostUserName, visibility);
		return world;
	}

	// Everything a hosted session needs before its content exists: data model, configuration and the
	// network listener. Split out of StartSession so a saved world can have its payload prepared off-thread
	// between this and CompleteSessionStart. -xlinka
	private static World BeginSession(
		string name,
		ushort port,
		string hostUserName,
		SessionVisibility visibility,
		int maxUsers)
	{
		var world = new World();
		world.WorldName.Value = name;
		world.AuthorityID = 0; // This instance is authority
		world.LocalID = 0;
		world.IsDestroyed = false;
		world.IsDisposed = false;

		world.Initialize();

		world.Configuration.MaxUsers.Value = global::System.Math.Max(1, maxUsers);
		world.Configuration.AllowJoin.Value = true;
		world.Configuration.IsPublic.Value = visibility == SessionVisibility.Public;
		// Seed the access level from the hosted visibility so the Settings radio shows the right selection and
		// later live toggles have the correct baseline. This runs before the world is Running, so the
		// AccessLevel.OnChanged beacon handler (Running-gated) won't fire here - the initial beacon still comes
		// from the metadata visibility below. -xlinka
		world.Configuration.AccessLevel.Value = visibility switch
		{
			SessionVisibility.LAN => WorldAccessLevel.LAN,
			SessionVisibility.Contacts => WorldAccessLevel.Contacts,
			SessionVisibility.Public => WorldAccessLevel.Anyone,
			_ => WorldAccessLevel.Private,
		};

		var metadata = new SessionMetadata
		{
			Name = name,
			HostUsername = hostUserName ?? Environment.MachineName,
			HostMachineId = Environment.MachineName,
			Visibility = visibility,
			MaxUsers = world.Configuration.MaxUsers.Value
		};

		// Start session network (creates LNL listener) but don't create user yet
		world.StartSessionNetwork(port, metadata);

		world.SessionID.Value = world._session?.Metadata?.SessionId ?? SessionIdentifier.Generate();

		return world;
	}

	private void CompleteSessionStart(string name, ushort port, string hostUserName, SessionVisibility visibility)
	{
		// Now create the host user (triggers OnUserJoined after SimpleUserSpawn is ready)
		CreateHostUser(hostUserName);

		StartRunning();
		LumoraLogger.Log($"Session '{name}' started on port {port} with visibility {visibility}");
	}

	// Same as StartSession, except the caller's payload is built on a task (file read, decompress, parse -
	// nothing that touches the world) while frames keep drawing. The world is held pre-Running until the
	// payload lands, so nothing ticks a half-built world; integration, the host user and StartRunning then
	// all happen on the world thread in the same order as the synchronous path. The manager pumps the
	// pending start each frame. -xlinka
	public static World StartSessionDeferred<T>(
		Engine engine,
		string name,
		ushort port,
		string hostUserName,
		SessionVisibility visibility,
		int maxUsers,
		Func<T> prepare,
		Action<World, T> integrate)
	{
		var world = BeginSession(name, port, hostUserName, visibility, maxUsers);
		var payload = Task.Run(prepare);
		bool integrated = false;

		world._pendingSessionStart = () =>
		{
			if (!integrated)
			{
				if (!payload.IsCompleted)
					return false;

				T value = default!;
				try
				{
					value = payload.GetAwaiter().GetResult();
				}
				catch (Exception ex)
				{
					LumoraLogger.Error($"World: preparing '{name}' failed: {ex}");
				}

				// Platform node creation is collected rather than run, so integrating the tree costs the
				// data model only. The nodes get built below, a bite per frame, and the world stays
				// pre-Running for all of it.
				world.BeginDeferredHookCreation();
				try
				{
					integrate(world, value);
				}
				catch (Exception ex)
				{
					// The synchronous path let this bubble to the manager, which dropped the world. Here the
					// world is already registered, so an empty-but-live world beats a zombie one. -xlinka
					LumoraLogger.Error($"World: integrating '{name}' failed: {ex}");
				}
				finally
				{
					world.EndDeferredHookCreation();
				}

				integrated = true;
			}

			if (!world.DrainDeferredHookCreations())
				return false;

			// Cleared before the world starts running, so anything reacting to Running already sees a
			// world that is done loading.
			world._pendingSessionStart = null;
			world.CompleteSessionStart(name, port, hostUserName, visibility);
			return true;
		};

		return world;
	}

	// True while a deferred session start is still waiting on its payload. The world exists but has no host
	// user yet, so it must not be focused or ticked.
	public bool IsSessionStartPending => _pendingSessionStart != null;

	// Pumped by the world manager, once a frame, on the world thread. Public for the same reason Update is:
	// it is one of the world's tick entry points, not something a component should be calling.
	public void TickPendingSessionStart()
	{
		var pending = _pendingSessionStart;
		if (pending == null)
			return;

		if (IsDestroyed || IsDisposed)
		{
			_pendingSessionStart = null;
			return;
		}

		try
		{
			if (pending())
				_pendingSessionStart = null;
		}
		catch (Exception ex)
		{
			_pendingSessionStart = null;
			LumoraLogger.Error($"World: deferred session start failed: {ex}");
		}
	}

	public static World JoinSession(Engine engine, string name, Uri address)
	{
		var world = new World();
		world.WorldName.Value = name;
		world.SessionID.Value = "Unknown"; // Will be set by server
		world.AuthorityID = 0; // Server is authority
		world.LocalID = -1; // Will be assigned by server
		world.IsDestroyed = false;
		world.IsDisposed = false;

		// Initialize world as CLIENT (uses LOCAL RefID space to avoid collisions with host's Authority RefIDs)
		world.Initialize(isAuthority: false);

		world.JoinSession(address);

		// World will transition to Running when connection succeeds
		LumoraLogger.Log($"Joining session at {address}");

		return world;
	}

	public static async Task<World?> JoinSessionAsync(Engine engine, string name, Uri address)
	{
		var world = new World();
		world.WorldName.Value = name;
		world.SessionID.Value = "Unknown"; // Will be set by server
		world.AuthorityID = 0; // Server is authority
		world.LocalID = -1; // Will be assigned by server
		world.IsDestroyed = false;
		world.IsDisposed = false;

		// Initialize world as CLIENT (uses LOCAL RefID space to avoid collisions with host's Authority RefIDs)
		world.Initialize(isAuthority: false);

		var joined = await world.JoinSessionAsync(address);
		if (!joined)
		{
			return null;
		}

		LumoraLogger.Log($"Joining session at {address}");
		return world;
	}

	// Initialize the World and create the root Slot.
	public void Initialize(bool isAuthority = true)
	{
		if (_state != WorldState.Created) return;

		_initState = InitializationState.Created;
		InitializationFailureReason = "";
		LumoraLogger.Log($"World initializing data model (isAuthority={isAuthority})");

		// Create reference controller BEFORE anything else
		ReferenceController = new ReferenceController(this);

		// Create sync controller first (doesn't need RefID)
		SyncController = new SyncController(this);
		LumoraLogger.Log("SyncController initialized");

		// Link arbitration + released-drive tracker. Rides alongside the sync controller: every drive link
		// asks it for its target, and it feeds released drives back into the sync loop. -xlinka
		LinkManager = new LinkManager(this);
		LumoraLogger.Log("LinkManager initialized");

		// Create worker manager for type encoding/decoding during sync
		// Needs SyncController available so the type index table registers for replication.
		Workers = new WorkerManager(this);

		// Always create network replicators with consistent RefIDs
		// This ensures both client and host use the same RefIDs for replicators
		_slotCollection = new Networking.Sync.ReplicatedSlotCollection();
		_slotCollection.Initialize(this, null);
		_userCollection = new Networking.Sync.ReplicatedUserCollection();
		_userCollection.Initialize(this, null);
		LumoraLogger.Log($"Network Collection initialized: SlotCollection={_slotCollection.ReferenceID}, UserCollection={_userCollection.ReferenceID}");

		// Clients must create shared world structures (RootSlot) in authority RefID space
		// so incoming ParentSlotRef and other references resolve correctly.
		// Local-only structures should explicitly use LocalAllocationBlockBegin where created.
		if (!isAuthority)
		{
			LumoraLogger.Log("Client: Using authority RefID space for shared world structures");
		}

		// Create root Slot (uses Authority RefID space on both host and client)
		RootSlot = new Slot();
		RootSlot.SlotName.Value = "Root";
		RootSlot.Initialize(this);
		RegisterSlot(RootSlot);

		// Note: Godot scene attachment handled by WorldDriver wrapper
		// World itself is pure C# and doesn't use AddChild

		// Network session is started via StartSession() or JoinSession()
		// No automatic network initialization

		// DON'T transition to Running here for clients!
		// Clients need to wait for full state download.
		// Only local/authority worlds can go to Running immediately.
		if (_slotCollection != null && _slotCollection.IsInInitPhase)
		{
			_slotCollection.EndInitPhase();
		}
		if (_userCollection != null && _userCollection.IsInInitPhase)
		{
			_userCollection.EndInitPhase();
		}
		LumoraLogger.Log($"World '{WorldName.Value}' initialized successfully - state={_state}, initState={_initState}");
	}

	// Get or create the Users container slot (NOT a UserRoot component!) - just a container; each user gets
	// their own UserRootComponent via SimpleUserSpawn.
	public Slot GetOrCreateUsersSlot()
	{
		var usersSlot = FindSlotsByTag("UserRoot").FirstOrDefault();

		if (usersSlot == null)
		{
			usersSlot = RootSlot.AddSlot("Users");
			usersSlot.Tag.Value = "UserRoot";
			LumoraLogger.Log("Created Users container slot in world");
		}

		// NO UserRootComponent here! Users container is just a parent slot.
		// Each individual user gets UserRootComponent via SimpleUserSpawn!

		return usersSlot;
	}


	internal void RegisterSlot(Slot slot)
	{
		if (slot == null) return;

		// Only register if not already registered (InitializeFromReplicator may have already registered)
		if (!ReferenceController.ContainsObject(slot.ReferenceID))
		{
			ReferenceController?.RegisterObject(slot);
		}
		Metrics.SlotCount++;

		// Add to slot replicator for network sync (skip local-only slots)
		if (!slot.IsLocalElement && _slotCollection != null && !_slotCollection.ContainsKey(slot.ReferenceID))
		{
			_slotCollection.Add(slot.ReferenceID, slot, isNewlyCreated: true, skipSync: false);
		}

		if (!string.IsNullOrEmpty(slot.Tag.Value))
		{
			if (!_slotsByTag.TryGetValue(slot.Tag.Value, out var slots))
			{
				slots = new List<Slot>();
				_slotsByTag[slot.Tag.Value] = slots;
			}
			slots.Add(slot);
		}

		if (slot.IsRootSlot)
		{
			_rootSlots.Add(slot);
		}

		OnSlotAdded?.Invoke(slot);
	}

	internal void UnregisterSlot(Slot slot)
	{
		if (slot == null) return;

		ReferenceController?.UnregisterObject(slot.ReferenceID);
		Metrics.SlotCount--;

		if (!slot.IsLocalElement)
		{
			_slotCollection?.Remove(slot.ReferenceID);
		}

		if (!string.IsNullOrEmpty(slot.Tag.Value))
		{
			if (_slotsByTag.TryGetValue(slot.Tag.Value, out var slots))
			{
				slots.Remove(slot);
			}
		}

		_rootSlots.Remove(slot);
		OnSlotRemoved?.Invoke(slot);
	}

	internal void RegisterComponent(Component component)
	{
		if (component == null) return;
		if (!ReferenceController.ContainsObject(component.ReferenceID))
		{
			ReferenceController?.RegisterObject(component);
		}
		Metrics.ComponentCount++;
	}

	internal void UnregisterComponent(Component component)
	{
		if (component == null) return;
		ReferenceController?.UnregisterObject(component.ReferenceID);
		Metrics.ComponentCount--;
	}

	public IEnumerable<KeyValuePair<RefID, IWorldElement>> GetAllElements()
	{
		return ReferenceController?.AllObjects ?? Array.Empty<KeyValuePair<RefID, IWorldElement>>();
	}

	public IWorldElement FindElement(RefID refID)
	{
		return (ReferenceController?.GetObjectOrNull(refID)) ?? null!;
	}

	public IWorldElement FindElement(ulong refID)
	{
		return FindElement(new RefID(refID));
	}

	public IWorldElement TryRetrieveFromTrash(ulong tick, RefID id)
	{
		return (ReferenceController?.TryRetrieveFromTrash(tick, id)) ?? null!;
	}

	public IEnumerable<Slot> FindSlotsByTag(string tag)
	{
		if (_slotsByTag.TryGetValue(tag, out var slots))
		{
			return slots.ToArray();
		}
		return Array.Empty<Slot>();
	}

	public Slot FindSlotByName(string name)
	{
		return FindSlotByNameRecursive(RootSlot, name);
	}

	private Slot FindSlotByNameRecursive(Slot slot, string name)
	{
		if (slot.SlotName.Value == name)
			return slot;

		foreach (var child in slot.Children)
		{
			var found = FindSlotByNameRecursive(child, name);
			if (found != null) return found;
		}
		foreach (var child in slot.LocalChildren)
		{
			var found = FindSlotByNameRecursive(child, name);
			if (found != null) return found;
		}

		return null!;
	}

	public void DestroyWorld()
	{
		if (_state == WorldState.Destroyed) return;

		LumoraLogger.Log($"Destroying world '{WorldName.Value}'...");

		_state = WorldState.Destroyed;

		// Destroy all root slots (which will cascade to children)
		foreach (var slot in _rootSlots.ToArray())
		{
			slot.Destroy();
		}

		_slotsByTag.Clear();
		_rootSlots.Clear();
		_users.Clear();

		_hookManager?.Dispose();

		ReferenceController?.Reset();

		// Note: Godot scene cleanup handled by WorldDriver wrapper
	}

	public void AddUser(User user)
	{
		if (user == null) return;

		// Reject banned users (host-authoritative). Never ban-check our own/host user.
		if (IsAuthority && LocalUser != null && user != LocalUser
			&& Security.BanManager.IsBanned(user.UserID?.Value, user.MachineID?.Value, WorldName?.Value))
		{
			LumoraLogger.Warn($"Rejecting banned user '{user.UserName.Value}'");
			return;
		}

		lock (_users)
		{
			if (!_users.Contains(user))
			{
				_users.Add(user);
				// Only register if not already registered (User constructor may have already registered)
				if (!ReferenceController.ContainsObject(user.ReferenceID))
				{
					ReferenceController?.RegisterObject(user);
				}

				// Add to user replicator for network sync (only if not already present)
				if (_userCollection != null && !_userCollection.ContainsKey(user.ReferenceID))
				{
					_userCollection.Add(user.ReferenceID, user, isNewlyCreated: true, skipSync: false);
				}

				LumoraLogger.Log($"User added to world: {user.UserName.Value}");

				// Trigger user joined event - ONLY on authority!
				// Only host fires OnUserJoined events. Host's SimpleUserSpawn creates
				// avatar slots in authority namespace, which are then synced to clients.
				// If clients also fired OnUserJoined, they'd create duplicate slots.
				if (IsAuthority)
				{
					TriggerUserJoinedEvent(user);
				}

				_session?.OnUserCountChanged(_users.Count);
			}
		}
	}

	internal void AddUserToCollection(User user, RefID id, bool isNewlyCreated)
	{
		if (user == null || _userCollection == null)
		{
			return;
		}

		if (!_userCollection.ContainsKey(id))
		{
			_userCollection.Add(id, user, isNewlyCreated, skipSync: false);
		}
	}

	public void RemoveUser(User user)
	{
		if (user == null) return;

		lock (_users)
		{
			_users.Remove(user);
			ReferenceController?.UnregisterObject(user.ReferenceID);

			_userCollection?.Remove(user.ReferenceID);

			LumoraLogger.Log($"User removed from world: {user.UserName.Value}");

			// Trigger user left event - ONLY on authority!
			// Same reason as OnUserJoined - host handles user lifecycle events.
			if (IsAuthority)
			{
				var userByte = user.AllocationID.Value;
				if (!RefIDConstants.IsValidUserByte(userByte))
					userByte = user.ReferenceID.GetUserByte();

				// Purge ALL of this user's objects (streams, body slots, etc.) from the RefID registry, not
				// just the User object above. Otherwise the leftovers stay registered and the next joiner who
				// recycles this byte collides ("RefID collision! User[NNN]:... already registered"). -xlinka
				ReferenceController?.PurgeUserByte(userByte);

				// Hand this user's byte back to the allocator so a long session with join/leave churn
				// doesn't run the 253 user-byte space dry. Authority owns allocation, so only it reclaims. -xlinka
				_refIDAllocator?.ReleaseUserAllocation(userByte);

				TriggerUserLeftEvent(user);
			}

			_session?.OnUserCountChanged(_users.Count);
		}
	}

	internal void RegisterUser(User user) => AddUser(user);

	internal void UnregisterUser(User user) => RemoveUser(user);

	public void SetLocalUser(User user)
	{
		if (user == null)
		{
			LumoraLogger.Warn("SetLocalUser(null) ignored.");
			return;
		}

		// One-shot: first valid assignment wins. A redundant call with the SAME user is a quiet no-op (the
		// grant-claim path and User.Initialize both race to set it), a DIFFERENT user is a bug (mismatched
		// grant / wrong RefID) and is rejected loudly rather than silently re-pointing the actor. -xlinka
		if (_localUserSet)
		{
			if (!ReferenceEquals(LocalUser, user))
				LumoraLogger.Error($"SetLocalUser called again with a DIFFERENT user " +
					$"(have '{LocalUser?.UserName.Value}' {LocalUser?.ReferenceID}, got '{user.UserName.Value}' {user.ReferenceID}). Ignoring.");
			return;
		}

		_localUserSet = true;
		LocalUser = user;

		// Only this machine knows what it is running on, so it stamps its own platform here rather than
		// letting the host guess. Same reasoning as the group card below: the host authors what it can
		// see, the peer authors what only it can know. -xlinka
		if (user.UserPlatform.Value != Engine.CurrentPlatform)
			user.UserPlatform.Value = Engine.CurrentPlatform;

		// The host authors a joiner's identity fields, but not the group card: only the peer holding the
		// account can read its own profile, so the joining client writes its own the moment it knows which
		// user is its. -xlinka
		user.ApplyRepresentedGroup(Engine.Current?.CDNClient?.RepresentedGroup);

		// Only configure streams if world is already Running. For clients, streams are decoded AFTER
		// SetLocalUser during FullBatch processing, so StartRunning() configures them instead.
		if (_state == WorldState.Running)
			user.ConfigureLocalTrackingStreams();

		AddUser(user);
		LumoraLogger.Log($"Local user set: {user.UserName.Value}");

		// Arm owned-namespace building now we have the local user. No-op until the allocation byte is
		// resolvable, the spawn driver retries if AllocationID hasn't synced yet. -xlinka
		InitializeAllocationForLocalUser();

		// On a client this is the moment we definitively know our own user - fire the joined event so the
		// per-peer spawn builds OUR equipment under the (replicated) scaffold. The host already fires it for
		// every user via AddUser, so only do this on a client to avoid a double-fire. -xlinka
		if (!IsAuthority)
			TriggerUserJoinedEvent(user);
	}

	// Resolve which RefID byte the local user's own content should mint into. On the host the local user IS
	// the authority, so this returns the authority byte (host owns everything anyway). On a client it's the
	// local user's allocation byte once known, else the byte carried in the user's own RefID (set even
	// before AllocationID syncs), else authority as a last resort. -xlinka
	private byte ResolveOwnedAllocationByte()
	{
		var u = LocalUser;
		if (u == null || IsAuthority)
			return RefIDConstants.AUTHORITY_BYTE;

		var b = u.AllocationID.Value;
		if (RefIDConstants.IsValidUserByte(b))
			return b;

		b = u.ReferenceID.GetUserByte();
		return RefIDConstants.IsValidUserByte(b) ? b : RefIDConstants.AUTHORITY_BYTE;
	}

	// Arm owned-namespace building for the local user. Idempotent. Must run AFTER LocalUser is set and its
	// allocation byte is resolvable, BEFORE any per-peer spawn builds the user's own equipment, so that
	// equipment lands in the owned namespace. Host short-circuits true (it authors in authority byte 0). -xlinka
	public bool InitializeAllocationForLocalUser()
	{
		if (IsAuthority)
		{
			_localAllocationReady = true;
			return true;
		}

		var u = LocalUser;
		if (u == null)
			return false;

		var b = ResolveOwnedAllocationByte();
		if (!RefIDConstants.IsValidUserByte(b))
		{
			LumoraLogger.Warn($"InitializeAllocationForLocalUser: local user '{u.UserName.Value}' has no valid allocation byte yet");
			return false;
		}

		_localAllocationReady = true;
		return true;
	}

	// True once owned-namespace building is armed for the local user (or we're the host). -xlinka
	public bool IsLocalAllocationReady => _localAllocationReady || IsAuthority;

	// Run an action with the allocation context scoped to the local user's own namespace, so everything
	// built inside - slots, components, sub-slots - is minted into and OWNED by the local user. The entry
	// point a per-peer spawn uses to build its own equipment. On the host this is the authority byte (a
	// no-op scope). -xlinka
	public IDisposable EnterLocalUserAllocation() => new OwnedAllocationScope(this, ResolveOwnedAllocationByte());

	// Create a slot in the local user's own RefID namespace under the given parent. Networked (others see
	// it) but OWNED by the local user, so the permission gate lets them keep mutating it with no system
	// bypass. For a joining user's own equipment - NOT shared world content (that stays host-authoritative
	// via AddSlot). -xlinka
	public Slot AddLocalUserSlot(Slot parent, string name = "Slot")
	{
		if (parent == null) throw new ArgumentNullException(nameof(parent));
		var b = ResolveOwnedAllocationByte();
		if (b == RefIDConstants.AUTHORITY_BYTE)
			return parent.AddSlot(name);

		ReferenceController.OwnedAllocationBlockBegin(b);
		try { return parent.AddSlot(name); }
		finally { ReferenceController.OwnedAllocationBlockEnd(b); }
	}

	// Scoped owned-allocation block. Authority byte means no scope (host already owns everything), so we
	// skip begin/end entirely and the dispose is a no-op. -xlinka
	private sealed class OwnedAllocationScope : IDisposable
	{
		private readonly World _world;
		private readonly byte _byte;
		private bool _active;

		public OwnedAllocationScope(World world, byte userByte)
		{
			_world = world;
			_byte = userByte;
			if (RefIDConstants.IsValidUserByte(userByte))
			{
				_world.ReferenceController.OwnedAllocationBlockBegin(userByte);
				_active = true;
			}
		}

		public void Dispose()
		{
			if (_active)
			{
				_active = false;
				_world.ReferenceController.OwnedAllocationBlockEnd(_byte);
			}
		}
	}

	public List<User> GetAllUsers()
	{
		lock (_users)
		{
			return new List<User>(_users);
		}
	}

	public void StartSession(ushort port = 7777, string hostUserName = null!)
	{
		StartSessionNetwork(port);
		CreateHostUser(hostUserName);
	}

	// Lets the init callback run before user creation.
	private void StartSessionNetwork(ushort port, SessionMetadata metadata = null!)
	{
		if (_session != null)
		{
			LumoraLogger.Warn("Session already started");
			return;
		}

		try
		{
			NetworkInitStart();

			if (metadata != null)
			{
				_session = Session.NewSession(this, port, metadata);
			}
			else
			{
				_session = Session.NewSession(this, port);
			}
			AuthorityID = -1; // This is the host

			_refIDAllocator.Reset();

			// NOTE: State remains InitializingNetwork here!
			// The caller (StartSession factory) will set Running AFTER init callback completes.
			// This allows the init callback to modify the world before the DataModel lock check kicks in.

			LumoraLogger.Log($"Started session network on port {port}");
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"Failed to start session network: {ex.Message}");
			InitializationFailed();
		}
	}

	public User CreateHostUser(string userName = null!)
	{
		var (rangeStart, rangeEnd) = _refIDAllocator.GetAuthorityIDRange();

		// Reserve the next RefID without advancing the allocation cursor.
		// User.InitializeWorker will advance the cursor for the user and its sync members.
		var userRefId = ReferenceController.PeekID();
		var hostUser = new User();
		var resolvedName = string.IsNullOrWhiteSpace(userName) ? System.Environment.MachineName : userName;

		hostUser.UserName.Value = resolvedName;
		hostUser.UserID.Value = userRefId.ToString();
		hostUser.MachineID.Value = System.Environment.MachineName;
		// Declared since the User class was written and never once assigned, so every user in every
		// session reported the enum's default regardless of what they were on.
		hostUser.UserPlatform.Value = Engine.CurrentPlatform;
		hostUser.AllocationIDStart.Value = rangeStart;
		hostUser.AllocationIDEnd.Value = rangeEnd;
		hostUser.AllocationID.Value = userRefId.GetUserByte();
		hostUser.IsPresent.Value = true;
		hostUser.PresentInWorld.Value = true;
		hostUser.IsSilenced.Value = false;

		var inputInterface = Engine.Current?.InputInterface;
		hostUser.HeadDevice.Value = inputInterface?.CurrentHeadOutputDevice ?? HeadOutputDevice.Screen;
		hostUser.VRActive.Value = inputInterface?.IsVRActive ?? false;

		hostUser.UserPlatform.Value = GetCurrentPlatform();
		// The group this machine's account wears. Empty when signed out, which is the guest case. -xlinka
		hostUser.ApplyRepresentedGroup(Engine.Current?.CDNClient?.RepresentedGroup);

		LocalUser = hostUser;
		_localUserSet = true; // host's local user is final - keep SetLocalUser's one-shot guard consistent. -xlinka
		AddUserToCollection(hostUser, userRefId, isNewlyCreated: true);
		hostUser.ConfigureLocalTrackingStreams();
		LumoraLogger.Log($"Created host user '{resolvedName}' with RefID {userRefId}");
		return hostUser;
	}

	private static Platform GetCurrentPlatform()
	{
		if (OperatingSystem.IsWindows())
			return Platform.Windows;
		if (OperatingSystem.IsLinux())
			return Platform.Linux;
		if (OperatingSystem.IsAndroid())
			return Platform.Android;
		return Platform.Other;
	}

	public void JoinSession(Uri address)
	{
		if (_session != null)
		{
			LumoraLogger.Warn("Session already active");
			return;
		}

		try
		{
			NetworkInitStart();

			_session = Session.JoinSession(this, new[] { address });

			WaitForJoinGrant();

			CreateSessionJoinIndicator();
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"Failed to join session: {ex.Message}");
			InitializationFailed();
		}
	}

	public async Task<bool> JoinSessionAsync(Uri address)
	{
		if (_session != null)
		{
			LumoraLogger.Warn("Session already active");
			return false;
		}

		try
		{
			NetworkInitStart();

			_session = (await Session.JoinSessionAsync(this, new[] { address }))!;
			if (_session == null)
			{
				InitializationFailed();
				return false;
			}

			WaitForJoinGrant();
			return true;
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"Failed to join session: {ex.Message}");
			InitializationFailed();
			return false;
		}
	}

	// Transitions from WaitingForJoinGrant to InitializingDataModel. Note: Allocation context switch is handled
	// in SessionSyncManager before SetLocalUser.
	public void OnJoinGrantReceived()
	{
		if (_state == WorldState.WaitingForJoinGrant)
		{
			StartDataModelInit();
		}

		// The full-state batch (data channel) can be applied BEFORE this JoinGrant (control channel) sets the
		// local-user target. When that happens our own User was added to the collection while the target was
		// still null, so User.Initialize() skipped the local-user match and nothing retried it - the client
		// ends up with no LocalUser (no actor -> its avatar/hands/laser/movement get permission-denied). Now
		// that the target RefID is known, claim an already-present user here. If it hasn't synced yet this is a
		// no-op and User.Initialize() catches it on add. -xlinka
		TryClaimPendingLocalUser();
	}

	// Order-independent companion to the per-user check in User.Initialize().
	private void TryClaimPendingLocalUser()
	{
		if (LocalUser != null)
			return;

		var target = _session?.Sync?.LocalUserRefIDToInit ?? RefID.Null;
		if (target.IsNull)
			return;

		foreach (var user in GetAllUsers())
		{
			if (user != null && user.ReferenceID == target)
			{
				SetLocalUser(user);
				LumoraLogger.Log($"OnJoinGrantReceived: claimed already-synced local user '{user.UserName.Value}' (RefID: {target})");
				return;
			}
		}
	}

	// Transitions InitializingDataModel to Running.
	public void OnFullStateReceived()
	{
		if (_state != WorldState.InitializingDataModel)
			return;

		// A client needs an actor before going Running, or the whole session is headless (no LocalUser ->
		// no permission actor -> avatar/equipment denied). If the user isn't claimed yet, defer Running a
		// bounded number of pumps and keep retrying. The host never reaches here. -xlinka
		if (IsAuthority || TryReadyLocalUserForRunning())
		{
			StartRunning();
			return;
		}

		_awaitingLocalUserFrames = 0;
		LumoraLogger.Warn($"OnFullStateReceived: full state in but LocalUser not resolved yet " +
			$"(target {_session?.Sync?.LocalUserRefIDToInit}). Deferring Running, retrying up to {MaxAwaitLocalUserFrames} pumps " +
			$"(t+{TimeSinceDataModelInit:F1}s).");
	}

	private bool TryReadyLocalUserForRunning()
	{
		if (LocalUser != null)
			return true;
		TryClaimPendingLocalUser();
		return LocalUser != null;
	}

	// Pumped from the session control-drain while we deferred Running waiting for the local user. Promotes
	// to Running the moment the user lands, after the bound it goes Running anyway but logs a LOUD error so
	// a genuinely-missing local user is a visible failure, never silent. -xlinka
	private void TickAwaitingLocalUser()
	{
		if (_state != WorldState.InitializingDataModel || IsAuthority)
			return;

		if (TryReadyLocalUserForRunning())
		{
			LumoraLogger.Log($"Local user resolved after {_awaitingLocalUserFrames} deferred pump(s), going Running.");
			StartRunning();
			return;
		}

		if (++_awaitingLocalUserFrames >= MaxAwaitLocalUserFrames)
		{
			LumoraLogger.Error($"JOIN: LocalUser never resolved after {_awaitingLocalUserFrames} pumps " +
				$"(target {_session?.Sync?.LocalUserRefIDToInit}, t+{TimeSinceDataModelInit:F1}s). Entering world WITHOUT a local " +
				$"actor - avatar/equipment will be permission-denied until it heals. This is a join ordering/decode failure.");
			StartRunning();
		}
	}

	// Pumped each session control-drain so join progress advances while the world is still pre-Running. -xlinka
	public void PumpJoinProgress()
	{
		if (_state == WorldState.InitializingDataModel)
			TickAwaitingLocalUser();
	}

	// If LocalUser is set but never gets a Root (spawn never ran, or the body tree never decoded), re-fire
	// spawn after a grace window so a one-frame race doesn't leave the user permanently bodyless. Bounded so
	// a genuinely un-spawnable user doesn't loop forever. Healthy sessions reset every frame and never fire. -xlinka
	private void TickMissingRootWatchdog()
	{
		var lu = LocalUser;
		if (lu == null || lu.Root != null)
		{
			_missingRootFrames = 0;
			return;
		}

		if (++_missingRootFrames < MissingRootGraceFrames)
			return;
		_missingRootFrames = 0;

		if (_respawnAttempts >= MaxRespawnAttempts)
		{
			LumoraLogger.Error($"Missing-root watchdog gave up after {_respawnAttempts} attempts for local user " +
				$"'{lu.UserName.Value}' ({lu.ReferenceID}); user remains bodyless.");
			return;
		}

		_respawnAttempts++;
		LumoraLogger.Warn($"Missing-root watchdog: local user '{lu.UserName.Value}' has no Root after grace - " +
			$"re-firing spawn (attempt {_respawnAttempts}/{MaxRespawnAttempts}).");
		TriggerUserJoinedEvent(lu);
	}

	public void NetworkInitStart()
	{
		var oldState = _state;
		_initState = InitializationState.InitializingNetwork;
		_state = WorldState.InitializingNetwork;
		LumoraLogger.Log("World entering network initialization");
		OnStateChanged?.Invoke(oldState, _state);
	}

	public void WaitForJoinGrant()
	{
		if (IsAuthority)
		{
			LumoraLogger.Warn("Authority cannot wait for join grant");
			return;
		}

		var oldState = _state;
		_initState = InitializationState.WaitingForJoinGrant;
		_state = WorldState.WaitingForJoinGrant;
		LumoraLogger.Log("Waiting for join grant");
		OnStateChanged?.Invoke(oldState, _state);
	}

	public void StartDataModelInit()
	{
		if (IsAuthority)
		{
			LumoraLogger.Warn("Authority cannot enter data model init");
			return;
		}

		var oldState = _state;
		_initState = InitializationState.InitializingDataModel;
		_state = WorldState.InitializingDataModel;
		_dataModelInitStartTime = TotalTime; // join window opened - clock for bounded local-user wait. -xlinka
		LumoraLogger.Log("Starting data model initialization");
		OnStateChanged?.Invoke(oldState, _state);
	}

	public void StartRunning()
	{
		if (_state == WorldState.Destroyed)
			return;

		if (!IsAuthority && LocalUser == null)
			LumoraLogger.Error("StartRunning: client going Running with no LocalUser - see prior JOIN error.");

		var oldState = _state;
		_initState = InitializationState.Finished;
		lock (_stateLock)
		{
			_state = WorldState.Running;
			// Fan out to anyone who subscribed before we flipped. Snapshot-and-null so it can't re-fire. -xlinka
			var running = _whenRunning;
			_whenRunning = null;
			running?.Invoke(this);
		}
		LumoraLogger.Log("World is now running");

		// Configure the permission gate for this world's mode. Authority-only: the lock is enforced on
		// the authority (it rejects unauthorized client deltas), and a client never escapes it because
		// the host is the one that accepts/rebroadcasts changes.
		if (IsAuthority)
		{
			WorldModePermissions.Apply(this, Mode);

			// Stand the config component up on the authority so a host that never opens the session screen
			// still has somewhere for role assignments to persist, and so it is in the world's full state
			// for the first joiner. The mode preset above runs FIRST: config toggles resolve on top of the
			// mode's caps, never the other way round. -xlinka
			PermissionConfig?.Republish();
		}

		// For clients: configure local user's tracking streams now that all sync members are decoded.
		// This was deferred from SetLocalUser because streams weren't decoded yet during FullBatch processing.
		if (!IsAuthority && LocalUser != null)
		{
			LocalUser.ConfigureLocalTrackingStreams();
			LumoraLogger.Log($"Configured tracking streams for local user '{LocalUser.UserName.Value}' on world Running");
		}

		OnStateChanged?.Invoke(oldState, _state);
	}

	public void InitializationFailed(string? reason = null)
	{
		var oldState = _state;
		_initState = InitializationState.Failed;
		_state = WorldState.Failed;
		InitializationFailureReason = string.IsNullOrWhiteSpace(reason) ? "World initialization failed" : reason;
		LumoraLogger.Log($"World initialization failed: {InitializationFailureReason}");
		OnStateChanged?.Invoke(oldState, _state);
	}

	// Thread-safe for cross-thread calls.
	public void RunSynchronously(Action action)
	{
		if (IsDisposed) return;

		lock (_syncLock)
		{
			_synchronousActions.Enqueue(action);
		}
	}

	// Counts real updates. The old version chained RunSynchronously calls, and the synchronous drain
	// loops until its queue is empty, so a countdown that re-queued itself was picked straight back up
	// inside the same drain and the whole count burned off in the update that scheduled it: every
	// "in ten updates" retry in the engine was running immediately. Same list shape as the seconds timer,
	// ticked once per update in the same place. -xlinka
	public void RunInUpdates(int updateCount, Action action)
	{
		if (IsDisposed || action == null) return;

		if (updateCount <= 0)
		{
			RunSynchronously(action);
			return;
		}

		lock (_syncLock)
		{
			_updateActions.Add(new UpdateAction { Remaining = updateCount, Action = action });
		}
	}

	// Runs during the NEXT update, wherever in the frame it was scheduled from. RunInUpdates counts its
	// wait down inside UpdateCoroutines, which sits midway through the frame, so a one-update wait armed
	// before that point burns off in the same frame it was armed in; this keys off the clock's update
	// index instead and always costs a real frame. Thread-safe. -xlinka
	internal void RunOnNextUpdate(Action action)
	{
		if (IsDisposed || action == null) return;

		lock (_syncLock)
		{
			_nextUpdateActions.Add(new NextUpdateAction { TargetIndex = Time.UpdateIndex + 1, Action = action });
		}
	}

	// Scaled world time. Thread-safe.
	public void RunInSeconds(float seconds, Action action)
	{
		if (IsDisposed || action == null) return;
		if (seconds <= 0f) { RunSynchronously(action); return; }
		lock (_syncLock)
		{
			_delayedActions.Add(new DelayedAction { Remaining = seconds, Action = action });
		}
	}

	// Scaled world time. Awaitable from a worker task.
	public Task DelaySeconds(float seconds)
	{
		var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		RunInSeconds(seconds, () => tcs.TrySetResult(true));
		return tcs.Task;
	}

	// Runs with this world as its context, so the body can await WorldContext.ToWorld() / ToBackground() /
	// NextUpdate() to move itself between threads instead of posting closures back through
	// RunSynchronously. Nothing ties the task to a component, so it runs until the world goes; use
	// Worker.StartTask when it should die with the thing that started it. Exceptions are logged.
	public void StartTask(Func<Task> task) => WorldContext.Start(this, null, task);

	// A step may yield: null (wait one update), a number (wait that many scaled seconds), or a Task (wait until
	// it completes). Thread-safe.
	public void StartCoroutine(IEnumerator routine)
	{
		if (IsDisposed || routine == null) return;
		lock (_syncLock)
		{
			_coroutinesToAdd.Add(new CoroutineRunner { Routine = routine });
		}
	}

	// One running coroutine: either counting down a time wait or blocked on a task between steps.
	private sealed class CoroutineRunner
	{
		public IEnumerator Routine = null!;
		public float Wait;
		public Task? WaitTask;
	}

	private sealed class DelayedAction
	{
		public float Remaining;
		public Action Action = null!;
	}

	private sealed class UpdateAction
	{
		public int Remaining;
		public Action Action = null!;
	}

	private sealed class NextUpdateAction
	{
		public ulong TargetIndex;
		public Action Action = null!;
	}

	// Continuations parked by an await on the next update (see WorldContext.NextUpdate). Drained ahead of
	// the synchronous action queue so anything one of them posts still lands in the same frame.
	private void ProcessNextUpdateActions()
	{
		List<Action>? ready = null;
		lock (_syncLock)
		{
			if (_nextUpdateActions.Count == 0)
				return;

			for (int i = 0; i < _nextUpdateActions.Count; i++)
			{
				var parked = _nextUpdateActions[i];
				if (parked.TargetIndex > Time.UpdateIndex)
					continue;

				(ready ??= new List<Action>()).Add(parked.Action);
				_nextUpdateActions.RemoveAt(i);
				i--;
			}
		}

		if (ready == null)
			return;

		foreach (var action in ready)
		{
			try { action(); }
			catch (Exception ex) { LumoraLogger.Error($"World: next-update action error: {ex}"); }
		}
	}

	private void ProcessSynchronousActions()
	{
		bool budgeted;
		lock (_syncLock)
		{
			if (_synchronousActions.Count >= BurstDrainThreshold)
				_burstDraining = true;
			else if (_synchronousActions.Count == 0)
				_burstDraining = false;
			budgeted = _startupActionBacklog || _burstDraining;
		}

		long started = budgeted ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
		int ran = 0;

		lock (_syncLock)
		{
			while (_synchronousActions.Count > 0)
			{
				try
				{
					_synchronousActions.Dequeue()?.Invoke();
				}
				catch (Exception ex)
				{
					LumoraLogger.Error($"World: Error in synchronous action: {ex}");
				}

				if (!budgeted)
					continue;

				ran++;
				if (ran < StartupDrainMinActions)
					continue;

				double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				if (elapsedMs >= StartupDrainBudgetMs)
					return; // rest of the backlog next update
			}

			_startupActionBacklog = false;
			_burstDraining = false;
		}
	}

	// True while a load scope is collecting hook creations instead of running them. Read by Slot when it
	// would otherwise build its platform node inline.
	public bool DeferHookCreation => _deferHookDepth > 0;

	// Slots left over from a drained backlog. Only meaningful while a deferred load is in flight.
	public int PendingHookCreations
	{
		get { lock (_syncLock) { return _deferredHookCreations.Count; } }
	}

	// A deferred scope that somebody else opened (a big object spawn, a join) leaves platform nodes
	// queued behind it. The session-start pump only covers worlds that have not started yet, so a
	// RUNNING world drains its own leftovers here, one budgeted bite per update, exactly like a load.
	// No scope open and nothing queued is the normal case and costs a count check. -xlinka
	private void DrainLoadBacklog()
	{
		if (_deferHookDepth > 0)
			return;
		lock (_syncLock)
		{
			if (_deferredHookCreations.Count == 0)
				return;
		}
		DrainDeferredHookCreations();
	}

	// What this world is doing while it is not yet somewhere you can stand. Read by the world browser
	// row and the loading overlay; both want the same sentence, so it lives here rather than being
	// spelled out twice. -xlinka
	public LocaleText LoadStateDescription
	{
		get
		{
			// Order matters: a world can be Running and STILL have platform nodes queued behind a big
			// spawn, and "Ready" over a backlog is the lie this whole readout exists to stop. -xlinka
			int pending = PendingHookCreations;
			if (pending > 0)
				return "WorldLoad.Building".AsLocale("Building {0} remaining", pending);

			if (IsSessionStartPending)
				return "WorldLoad.Integrating".AsLocale("Integrating");

			if (_state == WorldState.Running)
				return "WorldLoad.Ready".AsLocale("Ready");

			return _state switch
			{
				WorldState.InitializingNetwork => "WorldLoad.Connecting".AsLocale("Connecting"),
				WorldState.WaitingForJoinGrant => "WorldLoad.Connecting".AsLocale("Connecting"),
				WorldState.InitializingDataModel => "WorldLoad.Integrating".AsLocale("Integrating"),
				WorldState.Created => "WorldLoad.Preparing".AsLocale("Preparing"),
				_ => "WorldLoad.Starting".AsLocale("Starting up")
			};
		}
	}

	// True while this world is still assembling itself - pre-Running, mid-integrate, or with platform
	// nodes still queued behind a load scope.
	public bool IsLoading => IsSessionStartPending || _state != WorldState.Running || PendingHookCreations > 0;

	internal void QueueHookCreation(Action create)
	{
		if (create == null)
			return;
		lock (_syncLock)
		{
			_deferredHookCreations.Enqueue(create);
		}
	}

	// Nestable so a load that triggers another load doesn't drop the outer scope. The scope only decides
	// WHERE creations go; whoever opened it owns draining them, and must not let the world run until
	// DrainDeferredHookCreations has come up empty.
	public void BeginDeferredHookCreation() => _deferHookDepth++;

	public void EndDeferredHookCreation()
	{
		if (_deferHookDepth > 0)
			_deferHookDepth--;
	}

	// One time-boxed bite. Returns true when the backlog is empty. Creation order is preserved, which is
	// what keeps a child slot's node from looking for a parent node that hasn't been built yet: a parent
	// slot always initializes (and so queues) before its children. -xlinka
	public bool DrainDeferredHookCreations()
	{
		long started = System.Diagnostics.Stopwatch.GetTimestamp();
		double ticksToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		int ran = 0;

		while (true)
		{
			Action? create;
			lock (_syncLock)
			{
				if (_deferredHookCreations.Count == 0)
					return true;
				create = _deferredHookCreations.Dequeue();
			}

			try
			{
				create?.Invoke();
			}
			catch (Exception ex)
			{
				LumoraLogger.Error($"World: deferred hook creation failed: {ex}");
			}

			ran++;
			if (ran < StartupDrainMinActions)
				continue;
			if ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * ticksToMs >= StartupDrainBudgetMs)
				break;
		}

		lock (_syncLock)
		{
			return _deferredHookCreations.Count == 0;
		}
	}

	public void LeaveSession()
	{
		if (_session != null)
		{
			_session.Dispose();
			_session = null!;
			LumoraLogger.Log("Left session");
		}
	}

	public void IncrementSyncTick()
	{
		SyncTick++;
	}

	// Only the authority owns the state version, and only by incrementing it. A non-authority calling
	// this is a bug.
	public void IncrementStateVersion()
	{
		if (!IsAuthority)
		{
			throw new InvalidOperationException("Only the host can increment the state version");
		}
		StateVersion++;
	}

	// The host never adopts a foreign version (it only increments), and the version can only move forward - a
	// stale/reordered or out-of-range lower version is rejected so a peer can't roll our view of authority
	// state backward. We log-and-ignore rather than throw, so a stale batch doesn't abort the rest of the sync
	// drain.
	public void SetStateVersion(ulong version)
	{
		if (IsAuthority)
		{
			LumoraLogger.Warn("SetStateVersion called on the host - the host increments its own version, it does not adopt one. Ignoring.");
			return;
		}
		if (version < StateVersion)
		{
			LumoraLogger.Warn($"Rejecting a backward state version: have {StateVersion}, asked to set {version}. Ignoring (anti-rewind).");
			return;
		}
		StateVersion = version;
	}

	// Slow-frame accounting. One summary line per window instead of a warning per frame: a warning is a
	// stack-traced push on the platform side, and an import or a spawn burst produces dozens of legitimately
	// slow frames in a row. The first slow frame after a quiet spell logs at once so the timing is visible,
	// the rest of the window is folded into a count with the worst breakdown and the hook types behind it.
	// Startup gets a grace period: the first seconds of a world are all hitches by nature. -xlinka
	private const double SlowFrameMs = 25.0;
	private const double SlowReportWindowSeconds = 5.0;
	private const double SlowStartupGraceSeconds = 8.0;
	private int _slowFrameCount;
	private double _slowWindowStart;
	private double _slowWorstMs;
	private string _slowWorstDetail = "";

	private void NoteSlowFrame(double total, double sync, double pre, double comp, double change, double hooks, double end)
	{
		if (Time.TotalTime < SlowStartupGraceSeconds)
			return;
		if (_slowFrameCount == 0)
		{
			_slowWindowStart = Time.TotalTime;
			_slowWorstMs = 0;
		}
		_slowFrameCount++;
		if (total > _slowWorstMs)
		{
			_slowWorstMs = total;
			string hookDetail = hooks >= comp && hooks >= change ? _updateManager?.DescribeHookCost() ?? "" : "";
			_slowWorstDetail = $"sync={sync:F0} pre={pre:F0} comp={comp:F0} change={change:F0} hooks={hooks:F0} end={end:F0}"
				+ (hookDetail.Length > 0 ? $" [{hookDetail}]" : "");
		}
		if (_slowFrameCount == 1)
			// Back at Log level. Demoting this left a SIXTY SECOND stall reporting "0 slow frames": the
			// window summary below only fires on a SECOND slow frame, so one catastrophic frame logged
			// nothing at all. This line fires once per quiet spell, which is rare and is exactly the
			// evidence you want when something locks up. -xlinka
			LumoraLogger.Log($"World.Update slow frame {total:F0}ms: {_slowWorstDetail}");
		else if (Time.TotalTime - _slowWindowStart >= SlowReportWindowSeconds)
			FlushSlowFrameReport();
	}

	private void FlushSlowFrameReport()
	{
		if (_slowFrameCount > 1)
			LumoraLogger.Log($"World.Update: {_slowFrameCount} slow frames in {Time.TotalTime - _slowWindowStart:F1}s, worst {_slowWorstMs:F0}ms ({_slowWorstDetail})");
		_slowFrameCount = 0;
		_slowWorstMs = 0;
		_slowWorstDetail = "";
	}

	public void Update(double delta)
	{
		WorldThreadId = Environment.CurrentManagedThreadId;

		if (_state != WorldState.Running) return;

		_hookManager?.ImplementerLock(System.Threading.Thread.CurrentThread);
		try
		{
			var scaledDelta = delta * TimeScale;
			Time.Advance(scaledDelta);

			// Unscaled delta: the correction is chasing another machine's clock, which does not care
			// what this world's time scale is set to.
			SessionClock.Advance(delta);

			// Replicated FPS for the session UI: only the local user writes, rounded so it isn't a
			// per-frame sync churn source. -xlinka
			var localUser = LocalUser;
			if (localUser != null && Time.UpdateIndex % 30 == 0)
			{
				float fps = MathF.Round(Time.FramesPerSecond);
				if (System.Math.Abs(localUser.FPS.Value - fps) >= 1f)
					localUser.FPS.Value = fps;
			}

			// Per-stage timing so a frame can say WHICH stage ate it instead of leaving it to guesswork.
			// GetTimestamp is allocation-free. These used to be discarded unless the frame broke 25 ms, and
			// then only into a log line - so the ordinary "why is this world at 40 fps" case, where no single
			// frame is slow enough to trip the threshold, had no numbers at all. They are now published every
			// frame on LastFrameProfile whatever the cost, and the log threshold only governs the LOG. -xlinka
			_updateManager?.BeginFrameProfiling();
			long _ts = System.Diagnostics.Stopwatch.GetTimestamp();
			double _mspt = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			double Lap() { long n = System.Diagnostics.Stopwatch.GetTimestamp(); double ms = (n - _ts) * _mspt; _ts = n; return ms; }

			// Poll network transport so packets are dispatched before any world logic runs
			_session?.Poll();

			ProcessNextUpdateActions();
			ProcessSynchronousActions();
			DrainLoadBacklog();
			double msSync = Lap();

			Networking.AssetFetcher.ProcessQueue();

			RunWorldEvents();

			// Register any newly used worker types (authority only)
			if (IsAuthority)
			{
				Workers?.RegisterTypes();
			}

			if (_focus == WorldFocus.Focused)
			{
				ProcessInput((float)scaledDelta);
			}

			UpdateCoroutines((float)scaledDelta);

			// Hand out link grants before anything reads a drive. A link whose target resolved since the
			// last frame (arrived over the wire, finished loading, or was freed by another driver letting
			// go) becomes active here, so a component reading IsLinkValid in its update sees the settled
			// answer instead of lagging a frame behind. -xlinka
			LinkManager?.GrantLinks();
			double msPre = Lap();

			UpdateComponents((float)scaledDelta);
			double msComp = Lap();

			_updateManager?.RunChangeApplications();
			double msChange = Lap();

			// Fire deferred WorldTransformChanged events. Before hooks, so a handler
			// that re-drives a transform gets pushed to the engine this same frame.
			_updateManager?.ProcessMovedSlots();

			_updateManager?.ProcessHookUpdates((float)scaledDelta);
			double msHooks = Lap();

			ProcessDestructions();

			_trashBin?.Update();
			double msEnd = Lap();

			double msTotal = msSync + msPre + msComp + msChange + msHooks + msEnd;

			// LateMs is the previous frame's late pass, because LateUpdate has not run yet this frame and a
			// background world skips it entirely - waiting for it would mean never publishing at all. One
			// frame stale on one field beats a hole in the data. -xlinka
			LastFrameProfile = new FrameProfile(
				Time.UpdateIndex, msTotal, msSync, msPre, msComp, msChange, msHooks, msEnd, _lastLateMs);

			Metrics.UpdatesProcessed++;
			Metrics.PeakUpdateTimeMs = System.Math.Max(Metrics.PeakUpdateTimeMs, msTotal);
			// Exponential average: no ring buffer to carry, and it tracks a world that gets heavier as it
			// loads instead of being dragged down forever by the first quiet second.
			Metrics.AverageUpdateTimeMs = Metrics.AverageUpdateTimeMs <= 0.0
				? msTotal
				: Metrics.AverageUpdateTimeMs * 0.95 + msTotal * 0.05;

			if (msTotal > SlowFrameMs)
				NoteSlowFrame(msTotal, msSync, msPre, msComp, msChange, msHooks, msEnd);
			else if (_slowFrameCount > 0 && Time.TotalTime - _slowWindowStart >= SlowReportWindowSeconds)
				FlushSlowFrameReport();

			// Signal sync manager that world refresh is complete
			// Sync thread waits for this before new-user initialization
			// This ensures OnUserJoined events have fired and avatars are created
			if (_session?.Sync != null)
			{
				_session.Sync.SignalRefreshFinished();
			}

			// missing-root respawn watchdog - heals a bodyless local user. -xlinka
			TickMissingRootWatchdog();
		}
		finally
		{
			_hookManager?.ImplementerUnlock();
		}
	}

	public void FixedUpdate(double fixedDelta)
	{
		// Physics is delegated to the platform layer and nothing engine-side consumes a fixed tick.
		// Kept as a stable entry point for the world manager's pacing loop. -xlinka
	}

	public void LateUpdate(double delta)
	{
		WorldThreadId = Environment.CurrentManagedThreadId;

		if (_state != WorldState.Running) return;

		_hookManager?.ImplementerLock(System.Threading.Thread.CurrentThread);
		try
		{
			var scaledDelta = delta * TimeScale;

			long lateStart = System.Diagnostics.Stopwatch.GetTimestamp();

			_updateManager?.RunLateUpdates((float)scaledDelta);

			// Same order as the main pass: anything a late component moved fires its WorldTransformChanged
			// before the hooks flush, so a collider or a follower riding a slot that only gets its final pose
			// here (a laser-held object, say) resyncs this frame instead of the next. -xlinka
			_updateManager?.ProcessMovedSlots();

			_updateManager?.ProcessHookUpdates((float)scaledDelta);

			// The late pass runs a SECOND full hook flush and was completely untimed, so any cost here was
			// invisible to every frame number we had. -xlinka
			_lastLateMs = (System.Diagnostics.Stopwatch.GetTimestamp() - lateStart)
				* 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		}
		finally
		{
			_hookManager?.ImplementerUnlock();
		}
	}

	private double _lastLateMs;

	// One frame's cost, split by stage. Published every frame.
	public readonly struct FrameProfile
	{
		public readonly ulong UpdateIndex;
		public readonly double TotalMs;
		public readonly double SyncMs;
		public readonly double PreMs;
		public readonly double CompMs;
		public readonly double ChangeMs;
		public readonly double HooksMs;
		public readonly double EndMs;
		public readonly double LateMs;

		public FrameProfile(ulong updateIndex, double total, double sync, double pre, double comp,
			double change, double hooks, double end, double late)
		{
			UpdateIndex = updateIndex;
			TotalMs = total;
			SyncMs = sync;
			PreMs = pre;
			CompMs = comp;
			ChangeMs = change;
			HooksMs = hooks;
			EndMs = end;
			LateMs = late;
		}
	}

	public FrameProfile LastFrameProfile { get; private set; }

	private void ProcessInput(float delta)
	{
	}

	private void UpdateCoroutines(float delta)
	{
		// Promote staged coroutines and fire any elapsed delayed actions. Both lists are mutated under
		// _syncLock from any thread; snapshot the ready work under the lock, run it outside so a
		// callback that schedules more (or starts a coroutine) doesn't re-enter the lock. -xlinka
		List<Action>? readyActions = null;
		lock (_syncLock)
		{
			if (_coroutinesToAdd.Count > 0)
			{
				_coroutines.AddRange(_coroutinesToAdd);
				_coroutinesToAdd.Clear();
			}

			for (int i = _delayedActions.Count - 1; i >= 0; i--)
			{
				var d = _delayedActions[i];
				d.Remaining -= delta;
				if (d.Remaining <= 0f)
				{
					(readyActions ??= new List<Action>()).Add(d.Action);
					_delayedActions.RemoveAt(i);
				}
			}

			for (int i = _updateActions.Count - 1; i >= 0; i--)
			{
				var u = _updateActions[i];
				u.Remaining--;
				if (u.Remaining <= 0)
				{
					(readyActions ??= new List<Action>()).Add(u.Action);
					_updateActions.RemoveAt(i);
				}
			}
		}

		if (readyActions != null)
		{
			foreach (var action in readyActions)
			{
				try { action(); }
				catch (Exception ex) { LumoraLogger.Error($"World: delayed action error: {ex}"); }
			}
		}

		// Tick coroutines: one step per coroutine per update. Iterate a snapshot so a step that starts
		// another coroutine (staged above, promoted next update) doesn't disturb this pass.
		if (_coroutines.Count == 0)
			return;

		for (int i = _coroutines.Count - 1; i >= 0; i--)
		{
			var c = _coroutines[i];

			if (c.WaitTask != null)
			{
				if (!c.WaitTask.IsCompleted) continue;
				c.WaitTask = null;
			}
			else if (c.Wait > 0f)
			{
				c.Wait -= delta;
				if (c.Wait > 0f) continue;
			}

			bool moved;
			try { moved = c.Routine.MoveNext(); }
			catch (Exception ex)
			{
				LumoraLogger.Error($"World: coroutine error: {ex}");
				_coroutines.RemoveAt(i);
				continue;
			}

			if (!moved)
			{
				_coroutines.RemoveAt(i);
				continue;
			}

			switch (c.Routine.Current)
			{
				case float f: c.Wait = f; break;
				case double d: c.Wait = (float)d; break;
				case int n: c.Wait = n; break;
				case Task t: c.WaitTask = t; break;
				default: c.Wait = 0f; break; // yield null -> resume next update
			}
		}
	}

	private void UpdateComponents(float delta)
	{
		_updateManager?.RunStartups();
		_updateManager?.RunStartupRetries(); // re-drive anything whose startup threw (transient join-window denials)
		_updateManager?.RunUpdates(delta);
	}

	private void ProcessDestructions()
	{
		_updateManager?.RunDestructions();
	}

	public void RegisterEventReceiver(IWorldEventReceiver receiver)
	{
		foreach (WorldEvent eventType in Enum.GetValues(typeof(WorldEvent)))
		{
			if (receiver.HasEventHandler(eventType))
			{
				_worldEventReceivers[(int)eventType].Add(receiver);
			}
		}
	}

	public void UnregisterEventReceiver(IWorldEventReceiver receiver)
	{
		foreach (WorldEvent eventType in Enum.GetValues(typeof(WorldEvent)))
		{
			if (receiver.HasEventHandler(eventType))
			{
				_worldEventReceivers[(int)eventType].Remove(receiver);
			}
		}
	}

	private void TriggerUserJoinedEvent(User user)
	{
		_joinedUsers.Add(user);
		if (user != null && !_awaitingSpawn.Contains(user))
			_awaitingSpawn.Add(user);
	}

	private void TriggerUserLeftEvent(User user)
	{
		_leftUsers.Add(user);
		_awaitingSpawn.Remove(user);
	}

	// Raise OnWorldSaved for a save that actually landed. Deliberately NOT called from the serializer:
	// a data tree that built fine but never reached disk is not a save, and a handler that trusts this
	// event (a "last saved" stamp, a dirty flag being cleared) would be lying. -xlinka
	public void NotifyWorldSaved(string path)
	{
		_savedWorldPaths.Add(path ?? string.Empty);
	}

	// Write this world to a file and raise OnWorldSaved on success. The one entry point that keeps the
	// event honest, so prefer it over calling the storage API directly.
	public bool SaveToFile(string path, bool encrypt = false)
	{
		if (!Persistence.WorldStorage.SaveToFile(this, path, encrypt))
			return false;
		NotifyWorldSaved(path);
		return true;
	}

	// A user counts as spawned once its root is bound and that root has a head node. The empty-list check
	// is the whole cost on a normal frame; while a user IS pending it is a couple of null tests per frame
	// for the few frames a body takes to appear. NOT gated on there being a receiver, deliberately: the
	// world's own startup dispatches events before component startups run, so a receiver registering in
	// the same frame as a join would miss a spawn that had already been thrown away. -xlinka
	private void ProcessPendingUserSpawns()
	{
		if (_awaitingSpawn.Count == 0)
			return;

		for (int i = _awaitingSpawn.Count - 1; i >= 0; i--)
		{
			var user = _awaitingSpawn[i];
			if (user == null || user.IsDestroyed)
			{
				_awaitingSpawn.RemoveAt(i);
				continue;
			}

			var root = user.UserRootRef?.Target;
			if (root == null || root.IsDestroyed || root.Slot == null || root.HeadSlot == null)
				continue;

			_awaitingSpawn.RemoveAt(i);
			_spawnedUsers.Add(user);
		}
	}

	private void RunWorldEvents()
	{
		if (_joinedUsers.Count > 0)
		{
			foreach (var user in _joinedUsers)
			{
				foreach (var receiver in _worldEventReceivers[(int)WorldEvent.OnUserJoined])
				{
					try
					{
						receiver.OnUserJoined(user);
					}
					catch (Exception ex)
					{
						LumoraLogger.Error($"Error in OnUserJoined handler: {ex.Message}");
					}
				}
			}
			_joinedUsers.Clear();
		}

		if (_leftUsers.Count > 0)
		{
			foreach (var user in _leftUsers)
			{
				foreach (var receiver in _worldEventReceivers[(int)WorldEvent.OnUserLeft])
				{
					try
					{
						receiver.OnUserLeft(user);
					}
					catch (Exception ex)
					{
						LumoraLogger.Error($"Error in OnUserLeft handler: {ex.Message}");
					}
				}
			}
			_leftUsers.Clear();
		}

		ProcessPendingUserSpawns();

		if (_spawnedUsers.Count > 0)
		{
			foreach (var user in _spawnedUsers)
			{
				foreach (var receiver in _worldEventReceivers[(int)WorldEvent.OnUserSpawn])
				{
					try
					{
						receiver.OnUserSpawn(user);
					}
					catch (Exception ex)
					{
						LumoraLogger.Error($"Error in OnUserSpawn handler: {ex.Message}");
					}
				}
			}
			_spawnedUsers.Clear();
		}

		if (_savedWorldPaths.Count > 0)
		{
			foreach (var path in _savedWorldPaths)
			{
				foreach (var receiver in _worldEventReceivers[(int)WorldEvent.OnWorldSaved])
				{
					try
					{
						receiver.OnWorldSaved(path);
					}
					catch (Exception ex)
					{
						LumoraLogger.Error($"Error in OnWorldSaved handler: {ex.Message}");
					}
				}
			}
			_savedWorldPaths.Clear();
		}
	}

	public Slot AddSlot(string name = "Slot")
	{
		return RootSlot.AddSlot(name);
	}

	// REFERENCE REMAPPING
	// Re-aim every reference in the world that points at one element so it points at another. Walks the
	// flat element registry rather than the slot tree: it is one dictionary pass instead of a recursive
	// descent, and it also catches references held by elements that are not currently parented under a
	// slot, which a tree walk silently misses. -xlinka

	public int ReplaceReferenceTargets(IWorldElement currentTarget, IWorldElement newTarget, bool clearIfIncompatible)
	{
		if (currentTarget == null)
			return 0;

		var single = new Dictionary<RefID, IWorldElement>(1) { [currentTarget.ReferenceID] = newTarget };
		return ReplaceReferenceTargets(single, clearIfIncompatible);
	}

	// A reference whose declared target type will not accept the replacement cannot be re-aimed; with
	// clearIfIncompatible it is emptied instead, because pointing at a dead element is worse than pointing
	// at nothing. Driven references are left alone - the drive owns them and would overwrite us anyway.
	public int ReplaceReferenceTargets(IReadOnlyDictionary<RefID, IWorldElement> replacements, bool clearIfIncompatible)
	{
		if (replacements == null || replacements.Count == 0 || ReferenceController == null)
			return 0;

		// Snapshot first: a re-aim fires change callbacks, and a callback that touches the data model
		// would invalidate an in-flight enumeration of the registry.
		var found = new List<ISyncRef>();
		foreach (var entry in ReferenceController.AllObjects)
		{
			if (entry.Value is ISyncRef syncRef && !syncRef.Value.IsNull && replacements.ContainsKey(syncRef.Value))
				found.Add(syncRef);
		}

		int changed = 0;
		int refused = 0;
		foreach (var syncRef in found)
		{
			if (syncRef is Networking.Sync.SyncElement { IsDestroyed: true } or Networking.Sync.SyncElement { IsBlockedByDrive: true })
				continue;
			if (!replacements.TryGetValue(syncRef.Value, out var replacement))
				continue;

			try
			{
				if (syncRef.TrySet(replacement))
				{
					changed++;
				}
				else if (clearIfIncompatible)
				{
					syncRef.Clear();
					changed++;
				}
			}
			catch (Exception)
			{
				// Denied by the permission gate, or refused by the member itself. Count it and keep going:
				// abandoning the sweep halfway would leave a worse mess than a partial one. -xlinka
				refused++;
			}
		}

		if (refused > 0)
			LumoraLogger.Warn($"World: {refused} reference(s) could not be re-aimed (denied or refused).");

		return changed;
	}

	private void CreateSessionJoinIndicator()
	{
		if (IsAuthority || LocalUser == null || WorldManager?.FocusedWorld == null)
			return;

		try
		{
			var currentWorld = WorldManager.FocusedWorld;
			
			RunSynchronously(() =>
			{
				SessionJoinIndicator.CreateIndicatorAsync(currentWorld, this, _session!.Sync, indicator =>
				{
					if (indicator != null)
					{
						LumoraLogger.Log($"Created session join indicator in world '{currentWorld.Name}' for joining '{Name}'");
					}
					else
					{
						LumoraLogger.Warn("Failed to create session join indicator");
					}
				});
			});
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"Error creating session join indicator: {ex.Message}");
		}
	}

	public void Dispose()
	{
		if (IsDisposed)
		{
			LumoraLogger.Warn($"World: Already disposed world '{WorldName.Value}'");
			return;
		}

		LumoraLogger.Log($"World: Disposing world '{WorldName.Value}'");

		IsDestroyed = true;
		IsDisposed = true;

		// Fire the destroyed edge for late-or-early subscribers. This is the real teardown path (the
		// WorldManager drains its destroy queue into Dispose), so it's the load-bearing fire site.
		// Snapshot-and-null under the lock means it fires exactly once even if something flipped the
		// state earlier. -xlinka
		Action<World>? destroyed;
		lock (_stateLock)
		{
			if (_state != WorldState.Destroyed)
				_state = WorldState.Destroyed;
			destroyed = _whenDestroyed;
			_whenDestroyed = null;
		}
		try { destroyed?.Invoke(this); }
		catch (Exception ex) { LumoraLogger.Error($"World: Error in WhenDestroyed handler during dispose: {ex}"); }

		lock (_syncLock)
		{
			while (_synchronousActions.Count > 0)
			{
				try
				{
					_synchronousActions.Dequeue()?.Invoke();
				}
				catch (Exception ex)
				{
					LumoraLogger.Error($"World: Error in disposal synchronous action: {ex}");
				}
			}
		}

		// Anything parked on an update that will never come. Running them here is what lets a task waiting
		// on NextUpdate unwind through its own finally blocks; IsDisposed is already set, so the awaiter
		// cancels the task instead of letting it back into a dead world. -xlinka
		List<Action> parked;
		lock (_syncLock)
		{
			parked = _nextUpdateActions.Select(p => p.Action).ToList();
			_nextUpdateActions.Clear();
		}
		foreach (var action in parked)
		{
			try { action(); }
			catch (Exception ex) { LumoraLogger.Error($"World: Error in disposal next-update action: {ex}"); }
		}

		try
		{
			_session?.Dispose();
			_session = null!;
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"World: Error disposing session: {ex.Message}");
		}

		try
		{
			SyncController?.Dispose();
			SyncController = null!;
			LinkManager?.Dispose();
			LinkManager = null!;
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"World: Error disposing sync controller: {ex.Message}");
		}

		foreach (var user in _users.ToList())
		{
			try
			{
				user?.Dispose();
			}
			catch (Exception ex)
			{
				LumoraLogger.Error($"World: Error disposing user: {ex.Message}");
			}
		}

		try
		{
			RootSlot?.Destroy();
			// Destroy queues component OnDestroy callbacks; flush them before managers
			// and the ReferenceController are cleared so components can unregister cleanly.
			_updateManager?.RunDestructions();
			RootSlot = null!;
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"World: Error disposing root slot: {ex.Message}");
		}

		_users?.Clear();
		_slotsByTag?.Clear();
		_rootSlots?.Clear();
		_joinedUsers?.Clear();
		_leftUsers?.Clear();
		_awaitingSpawn?.Clear();
		_spawnedUsers?.Clear();
		_savedWorldPaths?.Clear();

		if (_worldEventReceivers != null)
		{
			for (int i = 0; i < _worldEventReceivers.Length; i++)
			{
				_worldEventReceivers[i]?.Clear();
			}
		}

		try
		{
			_hookManager?.Dispose();
			_hookManager = null!;
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"World: Error disposing hook manager: {ex.Message}");
		}

		try
		{
			// TrashBin doesn't have Dispose, just clear reference
			_trashBin = null!;
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"World: Error clearing trash bin: {ex.Message}");
		}

		try
		{
			// UpdateManager doesn't have Dispose, just clear reference
			_updateManager = null!;
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"World: Error clearing update manager: {ex.Message}");
		}

		try
		{
			_refIDAllocator = null!;
			_hookTypes = null!;
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"World: Error clearing managers: {ex.Message}");
		}

		try
		{
			ReferenceController?.Dispose();
			ReferenceController = null!;
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"World: Error disposing reference controller: {ex.Message}");
		}

		LumoraLogger.Log($"World: Disposed world '{WorldName.Value}'");
	}
}

