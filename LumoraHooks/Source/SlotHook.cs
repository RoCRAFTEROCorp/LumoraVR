// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Logging;

namespace Lumora.Godot.Hooks;

// Lazy creation: Node3D is only created when RequestNode3D() is first called. Reference counting
// tracks how many components need the Node3D; when count reaches 0 and shouldDestroy is set, it's freed.
[ImplementableHook(typeof(Slot))]
public class SlotHook : Hook<Slot>, ISlotHook
{
	// Static registry to find Slot from Godot Node
	private static readonly Dictionary<Node, Slot> _nodeToSlot = new();

	private Slot _lastParent = null!;
	private int _node3DRequests;
	private bool _shouldDestroy;
	private SlotHook _parentHook = null!;
	private WorldHook _worldHook = null!;
	private bool _didDeferLog;

	// LocalViewOverride pushes its per-view transform through here instead of writing the node itself.
	// The slot stays the only writer of its own transform, so an override cannot lose a race with the
	// slot's own update and flicker for a frame. Null falls through to the slot's real value. -xlinka
	private float3? _overridePosition;
	private floatQ? _overrideRotation;
	private float3? _overrideScale;

	// What this hook last pushed to the node. A flush lands here for every change event on the slot, and
	// the silent write paths (IK bind-pose reset, stream playback, dynamic bones) fire that event without
	// checking whether the value moved, so a resting avatar re-flushes every bone every frame with the
	// numbers it already has. Comparing against the last push turns those into a compare and a return
	// instead of three marshalled setters and three transform propagations through the node's subtree.
	// The name is the same story with an allocation on top: reading Node.Name marshals a StringName and
	// comparing it to a string marshals another, both finalizable, on every flush of every slot. -xlinka
	private Vector3 _appliedPosition;
	private Quaternion _appliedRotation;
	private Vector3 _appliedScale;
	private string _appliedName = null!;

	private bool ShouldDeferHierarchy
	{
		get
		{
			if (Owner?.World == null)
				return false;

			// Defer if we're still decoding a batch - sync fields (Name, ParentSlotRef) may not be populated yet
			var refController = Owner.World.ReferenceController;
			if (refController?.IsDecodingBatch == true)
				return true;

			// Defer if world not running yet (client still connecting)
			if (!Owner.World.IsAuthority && Owner.World.State != World.WorldState.Running)
				return true;

			return false;
		}
	}

	public Node3D GeneratedNode3D { get; private set; } = null!;

	public WorldHook WorldHook => _worldHook ??= (WorldHook)Owner.World.Hook;

	public static IHook<Slot> Constructor()
	{
		return new SlotHook();
	}

	public static Slot? GetSlotFromNode(Node? node)
	{
		var current = node;
		while (current != null)
		{
			if (_nodeToSlot.TryGetValue(current, out var slot))
				return slot;
			current = current.GetParent();
		}
		return null;
	}

	public Node3D ForceGetNode3D()
	{
		if (GeneratedNode3D == null || !GodotObject.IsInstanceValid(GeneratedNode3D))
		{
			GenerateNode3D();
		}
		return GeneratedNode3D!;
	}

	public Node3D RequestNode3D()
	{
		_node3DRequests++;
		return ForceGetNode3D();
	}

	public void FreeNode3D()
	{
		_node3DRequests--;
		TryDestroy();
	}

	private void TryDestroy(bool destroyingWorld = false)
	{
		if (!_shouldDestroy || _node3DRequests > 0)
		{
			return;
		}

		if (!destroyingWorld)
		{
			if (GeneratedNode3D != null && GodotObject.IsInstanceValid(GeneratedNode3D))
			{
				_nodeToSlot.Remove(GeneratedNode3D);
				GeneratedNode3D.QueueFree();
			}

			_parentHook?.FreeNode3D();
		}
		else if (GeneratedNode3D != null)
		{
			// Still need to unregister even when destroying world
			_nodeToSlot.Remove(GeneratedNode3D);
		}

		GeneratedNode3D = null!;
		_lastParent = null!;
		_parentHook = null!;
	}

	private void GenerateNode3D()
	{
		GeneratedNode3D = new Node3D();
		_appliedName = SafeNodeName(Owner.SlotName.Value);
		GeneratedNode3D.Name = _appliedName;

		_nodeToSlot[GeneratedNode3D] = Owner;

		UpdateParent();
		SetData();
	}

	// Godot forbids empty node names (some engines allow them). During load a child slot is created
	// before its name member decodes, so fall back to a non-empty default instead of erroring.
	private static string SafeNodeName(string? name)
		=> string.IsNullOrWhiteSpace(name) ? "Slot" : name;

	private void UpdateParent()
	{
		if (ShouldDeferHierarchy)
		{
			return;
		}

		// Defer if parent is unknown (ParentSlotRef not decoded yet) or pending (waiting for async resolution)
		// This prevents orphaned slots from being incorrectly attached to world root
		// during network decode when sync members haven't been decoded yet
		if (Owner.IsParentUnknown)
		{
			Lumora.Core.Logging.Logger.Debug($"SlotHook: Deferring hierarchy for '{Owner.SlotName.Value}' - parent ref not decoded yet");
			return;
		}
		if (Owner.HasPendingParent)
		{
			Lumora.Core.Logging.Logger.Debug($"SlotHook: Deferring hierarchy for '{Owner.SlotName.Value}' - parent pending resolution");
			return;
		}

		if (_lastParent == Owner.Parent && !Owner.IsRootSlot)
		{
			return;
		}

		_lastParent = Owner.Parent;

		if (_parentHook != null)
		{
			_parentHook.FreeNode3D();
			_parentHook = null!;
		}

		if (_lastParent != null && !Owner.IsRootSlot)
		{
			_parentHook = (SlotHook)_lastParent.Hook;
			if (_parentHook != null)
			{
				Node3D parentNode = _parentHook.RequestNode3D();
				if (GeneratedNode3D.GetParent() != parentNode)
				{
					if (GeneratedNode3D.GetParent() != null)
					{
						GeneratedNode3D.Reparent(parentNode, false);
					}
					else
					{
						parentNode.AddChild(GeneratedNode3D);
					}
				}
				Lumora.Core.Logging.Logger.Debug($"SlotHook: Attached child slot '{Owner.SlotName.Value}' to parent '{_lastParent.SlotName.Value}'");
			}
			else
			{
				Lumora.Core.Logging.Logger.Warn($"SlotHook: Parent hook is null for slot '{Owner.SlotName.Value}' (parent: '{_lastParent.SlotName.Value}')");
			}
		}
		else
		{
			// Root slot - attach to world root ONLY if this is the actual World.RootSlot
			// Don't use IsTrueRootSlot because ParentSlotRef.IsInInitPhase becomes false
			// during slot initialization, but the actual Value is decoded later as a separate
			// sync element. This causes false positives for "true root slot" detection.
			bool isActualRootSlot = Owner == Owner.World?.RootSlot;
			if (isActualRootSlot)
			{
				var worldRoot = Owner.World!.GodotSceneRoot as Node3D;
				if (worldRoot != null)
				{
					if (GeneratedNode3D.GetParent() != worldRoot)
					{
						if (GeneratedNode3D.GetParent() != null)
						{
							GeneratedNode3D.Reparent(worldRoot, false);
						}
						else
						{
							worldRoot.AddChild(GeneratedNode3D);
						}
					}
					Lumora.Core.Logging.Logger.Debug($"SlotHook: Attached root slot '{Owner.SlotName.Value}' to world root");
				}
				else
				{
					// Not an error, just startup ordering: the root slot initializes before the world's own scene
					// node exists (World.Initialize builds the root slot; the world hook creates the scene root and
					// reparents this node right after). Fires on every world creation, so trace it, don't warn. - xlinka
					Lumora.Core.Logging.Logger.Debug($"SlotHook: root slot '{Owner.SlotName.Value}' waiting for world scene root (the world hook attaches it once created)");
				}
			}
			else
			{
				// Not World.RootSlot and no parent - check if we should fall back to RootSlot
				// This happens when:
				// 1. ParentSlotRef.Value was decoded as RefID.Null (true orphan, should parent to RootSlot)
				// 2. We're still waiting for parent decode (defer)
				var parentRefValue = Owner.ParentSlotRef?.Value ?? RefID.Null;
				bool worldIsRunning = Owner.World?.State == World.WorldState.Running;
				bool parentRefIsNull = parentRefValue.IsNull;

				if (worldIsRunning && parentRefIsNull && Owner.World?.RootSlot != null)
				{
					var rootSlot = Owner.World.RootSlot;
					var rootHook = rootSlot.Hook as SlotHook;
					if (rootHook != null)
					{
						_parentHook = rootHook;
						Node3D parentNode = _parentHook.RequestNode3D();
						if (GeneratedNode3D.GetParent() != parentNode)
						{
							if (GeneratedNode3D.GetParent() != null)
							{
								GeneratedNode3D.Reparent(parentNode, false);
							}
							else
							{
								parentNode.AddChild(GeneratedNode3D);
							}
						}
						Lumora.Core.Logging.Logger.Debug($"SlotHook: Attached orphan slot '{Owner.SlotName.Value}' to RootSlot '{rootSlot.SlotName.Value}' (fallback)");
					}
				}
				else
				{
					// Still waiting for parent decode or world not running yet
					Lumora.Core.Logging.Logger.Debug($"SlotHook: Deferring attachment for '{Owner.SlotName.Value}' - waiting for parent decode (ParentRef.Value={parentRefValue}, WorldRunning={worldIsRunning})");
				}
			}
		}
	}

	// Set or clear the render-only transform override. Pass null for a component to leave that part of
	// the transform alone. Callers must be on the main thread; this writes the node immediately so an
	// override that changes without the slot changing still lands.
	public void SetLocalTransformOverride(float3? position, floatQ? rotation, float3? scale)
	{
		if (SameOverride(_overridePosition, position)
			&& SameOverride(_overrideRotation, rotation)
			&& SameOverride(_overrideScale, scale))
			return;

		_overridePosition = position;
		_overrideRotation = rotation;
		_overrideScale = scale;

		if (GeneratedNode3D != null && GodotObject.IsInstanceValid(GeneratedNode3D))
			SetData();
	}

	private void SetData()
	{
		if (GeneratedNode3D == null) return;

		GeneratedNode3D.Visible = Owner.ActiveSelf.Value;
		_appliedPosition = ToGodotVector3(_overridePosition ?? Owner.LocalPosition.Value);
		GeneratedNode3D.Position = _appliedPosition;
		_appliedRotation = ToGodotQuaternion(_overrideRotation ?? Owner.LocalRotation.Value);
		GeneratedNode3D.Quaternion = _appliedRotation;
		_appliedScale = ToGodotVector3(_overrideScale ?? Owner.LocalScale.Value);
		GeneratedNode3D.Scale = _appliedScale;
	}

	private void UpdateData()
	{
		if (GeneratedNode3D == null) return;

		if (Owner.ActiveSelf.GetWasChangedAndClear())
		{
			GeneratedNode3D.Visible = Owner.ActiveSelf.Value;
		}

		if (Owner.LocalPosition.GetWasChangedAndClear())
		{
			var position = ToGodotVector3(_overridePosition ?? Owner.LocalPosition.Value);
			if (position != _appliedPosition)
			{
				_appliedPosition = position;
				GeneratedNode3D.Position = position;
			}
		}

		if (Owner.LocalRotation.GetWasChangedAndClear())
		{
			var rotation = ToGodotQuaternion(_overrideRotation ?? Owner.LocalRotation.Value);
			if (rotation != _appliedRotation)
			{
				_appliedRotation = rotation;
				GeneratedNode3D.Quaternion = rotation;
			}
		}

		if (Owner.LocalScale.GetWasChangedAndClear())
		{
			var scale = ToGodotVector3(_overrideScale ?? Owner.LocalScale.Value);
			if (scale != _appliedScale)
			{
				_appliedScale = scale;
				GeneratedNode3D.Scale = scale;
			}
		}

		// Compared against what WE asked for, not what the node reports: siblings that share a name get
		// uniquified by the engine on attach, and asking the node back and re-setting it on every flush
		// only made it uniquify the same name again, forever. -xlinka
		bool nameChanged = Owner.SlotName.GetWasChangedAndClear();
		var slotName = SafeNodeName(Owner.SlotName.Value);
		if (nameChanged || !string.Equals(_appliedName, slotName, System.StringComparison.Ordinal))
		{
			_appliedName = slotName;
			GeneratedNode3D.Name = slotName;
		}
	}

	public override void Initialize()
	{
		if (ShouldDeferHierarchy)
		{
			if (!_didDeferLog)
			{
				Lumora.Core.Logging.Logger.Debug($"SlotHook.Initialize: Deferring Node3D creation for '{Owner.SlotName.Value}'");
				_didDeferLog = true;
			}
			return;
		}

		GenerateNode3D();
		Lumora.Core.Logging.Logger.Debug($"SlotHook.Initialize: Created Node3D for slot '{Owner.SlotName.Value}'");
	}

	public override void ApplyChanges()
	{
		if (GeneratedNode3D == null || !GodotObject.IsInstanceValid(GeneratedNode3D))
		{
			if (ShouldDeferHierarchy)
			{
				return;
			}

			GenerateNode3D();
			if (GeneratedNode3D == null || !GodotObject.IsInstanceValid(GeneratedNode3D))
			{
				return;
			}
		}

		if (Owner.Parent != _lastParent)
		{
			UpdateParent();
		}

		UpdateData();
	}

	public override void Destroy(bool destroyingWorld)
	{
		_shouldDestroy = true;
		TryDestroy(destroyingWorld);
	}

	private static Vector3 ToGodotVector3(float3 v)
	{
		return new Vector3(v.x, v.y, v.z);
	}

	private static Quaternion ToGodotQuaternion(floatQ q)
	{
		return new Quaternion(q.x, q.y, q.z, q.w);
	}

	private static bool SameOverride(float3? a, float3? b)
	{
		if (a.HasValue != b.HasValue) return false;
		return !a.HasValue || a.Value == b!.Value;
	}

	private static bool SameOverride(floatQ? a, floatQ? b)
	{
		if (a.HasValue != b.HasValue) return false;
		return !a.HasValue || a.Value == b!.Value;
	}
}

public interface ISlotHook : IHook<Slot>
{
}

