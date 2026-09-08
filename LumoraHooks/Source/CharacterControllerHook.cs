// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using System.Collections.Generic;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Motion model: every step the body is RE-BASED from the engine root (plus the
// head reference's ground projection in room-scale), the move is simulated,
// and only the delta physics produced is written back to the root. Root edits
// from anywhere (snap turn, smooth turn, teleport, physical walking) are
// incorporated automatically on the next re-base - there is no "external move"
// detection and nothing ever zeroes velocity behind the player's back.
// Stepping happens once per render frame from CharacterController.OnUpdate on
// the main thread; MoveAndSlide is a kinematic test-move, safe outside the
// physics tick. - xlinka
[ImplementableHook(typeof(CharacterController))]
public partial class CharacterControllerHook : ComponentHook<CharacterController>, ICharacterControllerHook
{
    // How fast airborne input can build velocity toward AirSpeed (m/s^2). Air control only ever ADDS
    // toward the move direction and is capped per-frame by this, so it nudges steering without ever
    // braking existing momentum. -xlinka
    private const float AirControlAcceleration = 12f;

    private CharacterBody3D _characterBody = null!;
    private Dictionary<Collider, CollisionShape3D> _collisionShapes = new Dictionary<Collider, CollisionShape3D>();
    private Vector3 _velocity;
    private Vector3 _moveDirection;
    private bool _jumpRequested;
    private bool _simulationEnabled = true;
    private bool _isCrouching;
    private float _currentHeight;
    private float _targetHeight;
    private TransformStreamDriver _rootStreamDriver = null!;

    // Surface the character is standing on, captured from the floor slide contact each move. Surfaces
    // carry CharacterControllerModifier components (ice, speed strips, low-jump zones); the matching
    // parameters are pulled through them the next step. Modifier list cached per floor slot. -xlinka
    private Lumora.Core.Slot? _floorSlot;
    private readonly List<CharacterControllerModifier> _floorModifiers = new();
    private float3 _floorContactPos;
    private float3 _floorContactNormal;

    // When a TransformStreamDriver shares the root slot, the Root stream is the transport for the root position -
    // so writes must be SILENT (no field sync) or the root would replicate over BOTH the stream and the delta
    // channel. Mirrors TrackedDevicePositioner's handling of stream-shared body nodes. Re-checks until found
    // (the driver is attached during avatar build), then caches. -xlinka
    private bool RootIsStreamed()
    {
        if (_rootStreamDriver == null || _rootStreamDriver.IsDestroyed)
            _rootStreamDriver = Owner?.Slot?.GetComponent<TransformStreamDriver>()!;
        return _rootStreamDriver != null && !_rootStreamDriver.IsDestroyed;
    }

    public CharacterBody3D GodotCharacterBody => _characterBody;

    public override void Initialize()
    {
        base.Initialize();

        _characterBody = new CharacterBody3D();
        _characterBody.Name = $"@CharacterBody3D_{Owner?.Slot?.Name?.Value ?? "Unknown"}";

        // Parent to world root: MoveAndSlide operates in world space and the
        // body is re-based from the slot every step, so it must not inherit
        // slot transforms on top of that.
        Node3D worldRoot = (Owner?.World?.GodotSceneRoot as Node3D)!;
        Node parentNode = (Node)worldRoot ?? attachedNode!;
        parentNode.AddChild(_characterBody);

        _characterBody.GlobalTransform = attachedNode!.GlobalTransform;

        _characterBody.FloorStopOnSlope = true;
        _characterBody.FloorMaxAngle = Mathf.DegToRad(45f);
        _characterBody.WallMinSlideAngle = Mathf.DegToRad(15f);

        // This world's collision bit (plus the legacy default bit) so the character only ever
        // collides with geometry in its own world - overlay/userspace bodies are invisible to it.
        uint worldBit = WorldHook.GetCollisionBitFor(Owner?.World);
        _characterBody.CollisionLayer = worldBit;
        _characterBody.CollisionMask = worldBit | 1u;

        // Initialize crouch state
        _currentHeight = Owner!.StandingHeight;
        _targetHeight = Owner.StandingHeight;
        _isCrouching = false;
    }

    public override void ApplyChanges()
    {
        // Runtime movement is stepped from CharacterController.OnUpdate.
    }

    public void SetSimulationEnabled(bool enabled)
    {
        if (_simulationEnabled == enabled)
            return;
        _simulationEnabled = enabled;
        if (!enabled)
            _velocity = Vector3.Zero;
    }

    public void Simulate(float delta)
    {
        if (_characterBody == null || !_characterBody.IsInsideTree())
            return;

        if (Owner?.Slot == null)
            return;

        // A non-physical module (noclip) owns rig movement; gravity/collision
        // stepping must not run or it would fight that motion back down.
        if (!_simulationEnabled)
            return;

        // Skip physics processing if the world is not focused (background worlds have ProcessMode.Disabled)
        if (Owner.World?.Focus == World.WorldFocus.Background)
            return;

        if (delta <= 0f)
            return;

        var bodyRid = _characterBody.GetRid();
        var spaceRid = PhysicsServer3D.BodyGetSpace(bodyRid);
        if (!spaceRid.IsValid)
            return;

        // Re-base the body from the engine root. With a head reference the
        // body stands at the head's ground projection so the collider follows
        // the player through room-scale space.
        var referencePos = ComputeReferencePosition();
        _characterBody.GlobalPosition = new Vector3(referencePos.x, referencePos.y, referencePos.z);

        StepMovement(delta);

        // Write back only the delta the physics step produced (collision
        // response, gravity, locomotion). Head-relative motion already lives
        // in the tracking data; the root must not absorb it twice.
        var bodyPos = _characterBody.GlobalPosition;
        var moved = new float3(bodyPos.X, bodyPos.Y, bodyPos.Z) - referencePos;
        if (moved.LengthSquared > 1e-12f)
        {
            // The local user driving their own root via locomotion is engine movement, not a user edit. Bypass
            // the data-model permission gate for this write: the root slot is host-allocated under the authority
            // byte, so ownership rests on the User<->UserRoot link which lags during join - without this the
            // joiner's own walking write is silently denied for a window and movement stutters/freezes. -xlinka
            using (Owner.World?.DataModelPermissions?.EnterSystemBypass())
            {
                var newGlobal = Owner.Slot.GlobalPosition + moved;
                if (RootIsStreamed())
                    Owner.Slot.SetGlobalPositionSilently(newGlobal); // Root stream is the transport - don't also delta it.
                else
                    Owner.Slot.GlobalPosition = newGlobal;
            }
            Owner.World?.UpdateManager?.RegisterHookUpdate(Owner.Slot);
        }
    }

    private float3 ComputeReferencePosition()
    {
        var root = Owner.Slot;
        var head = Owner.HeadReference.Target;
        if (head == null || head.IsDestroyed)
            return root.GlobalPosition;

        // Head position in root space, projected to the root's ground plane.
        var local = root.GlobalPointToLocal(head.GlobalPosition);
        local.y = 0f;
        return root.LocalPointToGlobal(local);
    }

    private void StepMovement(float delta)
    {
        // Apply gravity
        if (_characterBody.IsOnFloor())
        {
            // Clamp downward velocity to prevent bouncing
            if (_velocity.Y < 0)
                _velocity.Y = 0;
        }
        else
        {
            _velocity.Y -= 9.81f * delta * 2.0f; // 2x gravity for snappier feel
        }

        // Smoothly transition crouch height
        if (Mathf.Abs(_currentHeight - _targetHeight) > 0.01f)
        {
            _currentHeight = Mathf.MoveToward(_currentHeight, _targetHeight, Owner.CrouchTransitionSpeed * delta);
            UpdateColliderHeights();
        }

        bool onFloor = _characterBody.IsOnFloor();
        bool hasInput = _moveDirection.LengthSquared() > 0.001f;

        if (onFloor)
        {
            // Grounded: drive horizontal velocity directly to the target speed (instant traction).
            if (hasInput)
            {
                float speed = _isCrouching ? Owner.CrouchSpeed : Owner.Speed;
                if (!_isCrouching && Owner.IsSprinting)
                    speed *= Owner.SprintMultiplier;
                speed = ApplySurfaceParameter(CharacterControllerParameter.TractionSpeed, speed);
                _velocity.X = _moveDirection.X * speed;
                _velocity.Z = _moveDirection.Z * speed;
            }
            else
            {
                // Decelerate to stop on the ground only.
                _velocity.X = Mathf.MoveToward(_velocity.X, 0, Owner.Speed * delta * 10);
                _velocity.Z = Mathf.MoveToward(_velocity.Z, 0, Owner.Speed * delta * 10);
            }
        }
        else if (hasInput)
        {
            // Airborne: never overwrite or brake horizontal velocity - that is what carries
            // walk/sprint momentum through a jump. Input only ADDS toward the move direction, up to
            // AirSpeed, and only while we're below AirSpeed along that direction (so high momentum from
            // a sprint jump is left fully intact). -xlinka
            var horiz = new Vector2(_velocity.X, _velocity.Z);
            var dir = new Vector2(_moveDirection.X, _moveDirection.Z).Normalized();
            float along = horiz.Dot(dir);
            if (along < Owner.AirSpeed)
            {
                float add = Mathf.Min(AirControlAcceleration * delta, Owner.AirSpeed - along);
                _velocity.X += dir.X * add;
                _velocity.Z += dir.Y * add;
            }
        }
        // Airborne with no input: leave horizontal velocity untouched so momentum is preserved.

        // Apply jump
        if (_jumpRequested && _characterBody.IsOnFloor())
        {
            _velocity.Y = ApplySurfaceParameter(CharacterControllerParameter.TractionJumpSpeed, Owner.JumpSpeed);
            _jumpRequested = false;
        }

        _characterBody.Velocity = _velocity;
        _characterBody.MoveAndSlide();
        _velocity = _characterBody.Velocity;

        // Push any rigid bodies we collided with, and capture the floor surface for modifiers.
        Lumora.Core.Slot? floorSlot = null;
        int collisionCount = _characterBody.GetSlideCollisionCount();
        for (int i = 0; i < collisionCount; i++)
        {
            var collision = _characterBody.GetSlideCollision(i);
            var collider = collision.GetCollider();
            if (collider is RigidBody3D rigidBody)
            {
                var pushDir = -collision.GetNormal();
                var pushForce = pushDir * Owner.Speed * 0.5f;
                rigidBody.ApplyCentralImpulse(new Vector3(pushForce.X, 0, pushForce.Z));
            }

            var normal = collision.GetNormal();
            if (floorSlot == null && normal.Y > 0.5f && collider is Node colliderNode)
            {
                floorSlot = ResolveSlotFromCollider(colliderNode);
                var pos = collision.GetPosition();
                _floorContactPos = new float3(pos.X, pos.Y, pos.Z);
                _floorContactNormal = new float3(normal.X, normal.Y, normal.Z);
            }
        }
        SetFloorSurface(floorSlot);
    }

    private void SetFloorSurface(Lumora.Core.Slot? slot)
    {
        // Keep the last floor while airborne (a jump keeps the launch surface's parameters).
        if (slot == null || ReferenceEquals(slot, _floorSlot))
            return;

        _floorSlot = slot;
        _floorModifiers.Clear();
        foreach (var modifier in slot.GetComponents<CharacterControllerModifier>())
            _floorModifiers.Add(modifier);

        // Traction slope is a body property, not a per-step multiplier: apply it on surface change.
        float slopeDeg = 45f;
        if (_floorModifiers.Count > 0)
            slopeDeg = ApplySurfaceParameter(CharacterControllerParameter.MaximumTractionSlope, slopeDeg);
        _characterBody.FloorMaxAngle = Mathf.DegToRad(System.Math.Clamp(slopeDeg, 1f, 89f));
    }

    private float ApplySurfaceParameter(CharacterControllerParameter parameter, float value)
    {
        if (_floorModifiers.Count == 0 || _floorSlot == null || _floorSlot.IsDestroyed)
            return value;
        for (int i = 0; i < _floorModifiers.Count; i++)
        {
            var modifier = _floorModifiers[i];
            if (modifier != null && !modifier.IsDestroyed && modifier.Enabled && modifier.Parameter.Value == parameter)
            {
                modifier.ComputeParameter(ref value, in _floorContactPos, in _floorContactNormal);
                break;
            }
        }
        return value;
    }

    private Lumora.Core.Slot? ResolveSlotFromCollider(Node? collider)
    {
        var node = collider;
        while (node != null && GodotObject.IsInstanceValid(node))
        {
            if (node.HasMeta("LumoraSlotRef") &&
                ulong.TryParse(node.GetMeta("LumoraSlotRef").AsString(), out var raw))
            {
                return Owner?.World?.ReferenceController?.GetObjectOrNull(new Lumora.Core.RefID(raw)) as Lumora.Core.Slot;
            }
            node = node.GetParent();
        }
        return null;
    }

    public void SetMovementDirection(float3 direction)
    {
        _moveDirection = new Vector3(direction.x, direction.y, direction.z);
    }

    public void RequestJump()
    {
        _jumpRequested = true;
    }

    public void Teleport(float3 position)
    {
        _velocity = Vector3.Zero;
        if (_characterBody != null)
            _characterBody.GlobalPosition = new Vector3(position.x, position.y, position.z);
        if (Owner?.Slot != null)
        {
            // Same as Simulate: the local user moving their own root (here, a teleport) is engine movement, not a
            // user edit. Bypass the permission gate so a teleport firing in the join ownership-lag window isn't
            // silently denied. -xlinka
            using (Owner.World?.DataModelPermissions?.EnterSystemBypass())
            {
                if (RootIsStreamed())
                    Owner.Slot.SetGlobalPositionSilently(position); // Root stream is the transport - don't also delta it.
                else
                    Owner.Slot.GlobalPosition = position;
            }
            Owner.World?.UpdateManager?.RegisterHookUpdate(Owner.Slot);
        }
    }

    public bool IsOnFloor()
    {
        return _characterBody != null && _characterBody.IsOnFloor();
    }

    public void SetCrouching(bool crouching)
    {
        if (_isCrouching == crouching)
            return;

        _isCrouching = crouching;
        _targetHeight = crouching ? Owner.CrouchHeight : Owner.StandingHeight;
        LumoraLogger.Debug($"CharacterControllerHook: Crouch={crouching}, TargetHeight={_targetHeight}");
    }

    private void UpdateColliderHeights()
    {
        foreach (var kvp in _collisionShapes)
        {
            var collider = kvp.Key;
            var shape = kvp.Value;
            if (shape.Shape is CapsuleShape3D capsule && collider is CapsuleCollider capCollider)
            {
                capsule.Height = _currentHeight;

                // CapsuleShape3D is CENTRE-anchored: shrinking the height lifts the bottom off the floor
                // and gravity drops the whole body, sinking the user root (and the avatar with it) under
                // the ground on crouch. Lower the shape's centre by half the height change so the capsule
                // BOTTOM stays put. Measured against the controller's STANDING height (the value the
                // transition runs between) - the collider's authored Height can legitimately differ, and
                // using it offset the capsule even while standing. -xlinka
                var offset = capCollider.Offset.Value;
                float centerDrop = (Owner.StandingHeight - _currentHeight) * 0.5f;
                shape.Position = new Vector3(offset.x, offset.y - centerDrop, offset.z);
            }
        }
    }

    public void AddColliderShape(Collider collider)
    {
        if (_collisionShapes.ContainsKey(collider))
            return;

        if (_characterBody == null)
        {
            LumoraLogger.Error("CharacterControllerHook: _characterBody is null!");
            return;
        }

        CollisionShape3D collisionShape = new CollisionShape3D();
        collisionShape.Name = $"Shape_{collider.GetType().Name}";

        switch (collider)
        {
            case BoxCollider box:
                float3 size = box.Size.Value;
                collisionShape.Shape = new BoxShape3D { Size = new Vector3(size.x, size.y, size.z) };
                break;
            case CapsuleCollider capsule:
                collisionShape.Shape = new CapsuleShape3D { Height = capsule.Height.Value, Radius = capsule.Radius.Value };
                break;
            case SphereCollider sphere:
                collisionShape.Shape = new SphereShape3D { Radius = sphere.Radius.Value };
                break;
            default:
                LumoraLogger.Warn($"CharacterControllerHook: Unknown collider type {collider.GetType().Name}");
                break;
        }

        var offset = collider.Offset.Value;
        collisionShape.Position = new Vector3(offset.x, offset.y, offset.z);

        _characterBody.AddChild(collisionShape);
        _collisionShapes[collider] = collisionShape;
    }

    public void RemoveColliderShape(Collider collider)
    {
        if (_collisionShapes.TryGetValue(collider, out CollisionShape3D? shape))
        {
            shape?.QueueFree();
            _collisionShapes.Remove(collider);
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _characterBody != null && GodotObject.IsInstanceValid(_characterBody))
        {
            _characterBody.QueueFree();
        }

        _characterBody = null!;
        _collisionShapes.Clear();

        base.Destroy(destroyingWorld);
    }
}
