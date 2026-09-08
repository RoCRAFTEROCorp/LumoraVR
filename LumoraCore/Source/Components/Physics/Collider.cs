// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// Base class for every physics collider. The platform hook builds the real collision shape from the
// typed sync fields; this side owns the data model and the registry entry.
[ComponentCategory("Physics/Colliders")]
public abstract class Collider : ImplementableComponent
{
    // SYNC FIELDS

    public readonly Sync<float3> Offset;
    public readonly Sync<ColliderType> Type;
    public readonly Sync<float> Mass;
    public readonly Sync<bool> CharacterCollider;
    public readonly Sync<bool> IgnoreRaycasts;

    // STATE

    protected IColliderOwner _owner = null!;
    protected bool _shapeChanged = true;
    private bool _ownerSearchComplete = false;
    private int _updatesSinceAwake = 0;

    // The component that manages this collider, e.g. a CharacterController.
    public IColliderOwner ColliderOwner => _owner;

    // Local bounds offset after the owner has post-processed it.
    public float3 LocalBoundsOffset
    {
        get
        {
            float3 offset = Offset.Value;
            _owner?.PostprocessBoundsOffset(ref offset);
            return offset;
        }
    }

    // INITIALIZATION

    public Collider()
    {
        Offset = new Sync<float3>(this, float3.Zero);
        Type = new Sync<ColliderType>(this, ColliderType.Static);
        Mass = new Sync<float>(this, 1.0f);
        CharacterCollider = new Sync<bool>(this, false);
        IgnoreRaycasts = new Sync<bool>(this, false);
    }

    public override void OnAwake()
    {
        base.OnAwake();

        // Join this world's collider registry so raycasts don't have to walk the whole tree to find us. -xlinka
        World?.RegisterCollider(this);

        // Keep hook in sync when collider properties change
        Offset.OnChanged += _ => RunApplyChanges();
        Type.OnChanged += _ => RunApplyChanges();
        Mass.OnChanged += _ => RunApplyChanges();
        CharacterCollider.OnChanged += _ => RunApplyChanges();
        IgnoreRaycasts.OnChanged += _ => RunApplyChanges();
        // Re-sync whenever this slot's WORLD transform changes - its own local move, an ancestor
        // moving, or a scale change - coalesced to once per frame. (The old per-field local hooks
        // only caught this slot's own position/rotation; ancestor moves and scale were missed.)
        Slot.WorldTransformChanged += OnSlotTransformChanged;

        // Lumora Pattern: If this is a CharacterController collider,
        // notify CharacterController components in the SAME slot (not parent slots)
        if (Type.Value == ColliderType.CharacterController)
        {
            foreach (var component in Slot.Components)
            {
                if (component is IColliderOwner owner)
                {
                    _owner = owner;
                    _owner.OnColliderAdded(this);
                    LumoraLogger.Log($"Collider: Notified owner {_owner.GetType().Name} in same slot");
                    break; // Only register with first owner found
                }
            }

            if (_owner == null)
            {
                LumoraLogger.Warn($"Collider: No IColliderOwner found in same slot '{Slot.SlotName.Value}' for CharacterController collider");
            }
        }
        // For non-CharacterController colliders, register with physics system directly
        // (handled by physics hook)
        RunApplyChanges();
    }

    // UPDATE

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Keep searching for owner for first few updates (components might not be awake yet)
        if (!_ownerSearchComplete)
        {
            _updatesSinceAwake++;

            // Re-search for owner if we didn't find one yet (CharacterController colliders only)
            if (_owner == null && _updatesSinceAwake <= 3 && Type.Value == ColliderType.CharacterController)
            {
                // Search same slot for IColliderOwner
                foreach (var component in Slot.Components)
                {
                    if (component is IColliderOwner owner)
                    {
                        _owner = owner;
                        _owner.OnColliderAdded(this);
                        LumoraLogger.Log($"Collider: Found owner {_owner.GetType().Name} after {_updatesSinceAwake} updates");
                        _ownerSearchComplete = true;
                        break;
                    }
                }
            }
            else if (_updatesSinceAwake > 3)
            {
                // After 3 updates, give up searching - this is a standalone collider
                _ownerSearchComplete = true;
                if (_owner == null)
                {
                    LumoraLogger.Debug($"Collider: No owner found after {_updatesSinceAwake} updates, creating standalone static body");
                }
            }

            if (_ownerSearchComplete)
                DropOffTheUpdateDispatch();
        }
    }

    // The owner search is the ONLY thing this component wanted a per-frame update for, and it finishes
    // in four frames. Everything after that is event driven: sync fields queue the hook, and the world
    // transform event covers moving. A world like Scratch carries hundreds of colliders, and leaving
    // them all on the dispatch means hundreds of virtual calls a frame to run a branch that is already
    // false. Deferred by one update because the removal happens from inside the dispatch's own loop.
    // -xlinka
    private void DropOffTheUpdateDispatch()
    {
        if (_droppedFromUpdates)
            return;
        _droppedFromUpdates = true;
        World?.RunInUpdates(1, () =>
        {
            if (!IsDestroyed)
                World?.UpdateManager?.UnregisterFromUpdates(this);
        });
    }

    private bool _droppedFromUpdates;

    private void OnSlotTransformChanged(Slot _) => RunApplyChanges();

    // ABSTRACT METHODS

    // Shape-tight local bounding box, centred on Offset. The platform hook builds the actual collision
    // shape from the typed sync fields, so this is purely the engine-side answer used for culling,
    // fitting and overlap pre-tests.
    public abstract BoundingBox GetLocalBounds();

    // CLEANUP

    public override void OnDestroy()
    {
        World?.UnregisterCollider(this);

        if (Slot != null)
            Slot.WorldTransformChanged -= OnSlotTransformChanged;

        _owner?.OnColliderRemoved(this);
        _owner = null!;

        base.OnDestroy();
        LumoraLogger.Debug($"Collider: Destroyed {GetType().Name}");
    }
}

