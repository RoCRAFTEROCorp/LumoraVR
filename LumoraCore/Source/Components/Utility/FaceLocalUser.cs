// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// Yaw-billboards the slot toward the local viewer. Every peer computes its
// own facing locally (no sync generation), so each viewer sees the plate
// turned toward themselves - per-user visual divergence by design. - xlinka
[ComponentCategory("Utility")]
public class FaceLocalUser : Component
{
    // Where the viewer is this frame, resolved once per world and shared by every plate in it.
    //
    // Finding the viewpoint means asking whether an external camera is active, and that question is
    // answered by scanning the local user's root slot for its input-state component. A showcase world
    // has a hundred and sixty of these plates, and each one was running that scan on its own every
    // frame for an answer that cannot differ between them. -xlinka
    private sealed class ViewpointCache
    {
        public ulong Frame = ulong.MaxValue;
        public bool Valid;
        public float3 Point;
    }

    private static readonly ConditionalWeakTable<World, ViewpointCache> Viewpoints = new();

    // A viewer move below this leaves every plate where it is. At the closest a plate is ever read
    // from it turns the facing by less than the write threshold below already ignores.
    private const float ViewerMoveEpsilon = 0.001f;

    private ViewpointCache? _viewpoint;
    private bool _hasLast;
    private float3 _lastViewer;
    private float3 _lastPosition;
    private floatQ _lastParentRotation;
    private floatQ _lastLocal;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var slot = Slot;
        var parent = slot?.Parent;
        var world = World;
        if (slot == null || parent == null || world == null)
            return;

        var cache = _viewpoint ??= Viewpoints.GetValue(world, static _ => new ViewpointCache());
        ulong frame = world.Time.UpdateIndex;
        if (cache.Frame != frame)
        {
            cache.Frame = frame;
            cache.Valid = TryResolveViewpoint(world, out cache.Point);
        }
        if (!cache.Valid)
            return;

        // Nothing that feeds the answer has moved: the viewer, this slot, the frame it is turned in,
        // or the rotation itself since the last write. A standing viewer then costs a few compares per
        // plate rather than the whole facing solve. -xlinka
        var viewPoint = cache.Point;
        var position = slot.GlobalPosition;
        var parentRotation = parent.GlobalRotation;
        var current = slot.LocalRotation.Value;
        if (_hasLast
            && float3.DistanceSquared(viewPoint, _lastViewer) < ViewerMoveEpsilon * ViewerMoveEpsilon
            && Same(in position, in _lastPosition)
            && Same(in parentRotation, in _lastParentRotation)
            && Same(in current, in _lastLocal))
        {
            return;
        }

        // Yaw-only: the readable front of quad/canvas content is its +Z side, so point local +Z at the
        // viewer without tipping. Shared with FaceUser so both get the hand-built basis that avoids
        // floatQ.LookRotation's inverted result.
        if (!Utility.UserFacing.TryLookRotation(position, viewPoint, float3.Up, yawOnly: true, out var global))
            return;

        var local = parentRotation.Inverse * global;

        float dot = floatQ.Dot(local, current);
        if (1f - (dot < 0 ? -dot : dot) > 1e-6f)
        {
            // Per-viewer local billboard of a (possibly REMOTE) user's nameplate. On an observer the write's actor
            // is the observer's own user, who doesn't own the remote plate -> denied, and the badge stops facing
            // you. Local-only visual, no sync generated, so bypass the gate. -xlinka
            using var bypass = world.DataModelPermissions?.EnterSystemBypass();
            slot.LocalRotation.SetValueSilently(local, change: true);
            current = local;
        }

        _hasLast = true;
        _lastViewer = viewPoint;
        _lastPosition = position;
        _lastParentRotation = parentRotation;
        _lastLocal = current;
    }

    // The point of view is the CAMERA, not the avatar's head: with a freecam or third person the
    // head stays put while the eye orbits, and a billboard aimed at the head shows that eye its
    // back. Head stays the fallback (VR and plain first person, where the two coincide). -xlinka
    private static bool TryResolveViewpoint(World world, out float3 point)
    {
        var input = Engine.Current?.InputInterface;
        if (input != null && world.Focus == World.WorldFocus.Focused && UserInputState.FocusedExternalCameraActive)
        {
            point = input.DesktopCameraPosition;
            return true;
        }

        var viewerHead = world.LocalUser?.Root?.HeadSlot;
        if (viewerHead == null || viewerHead.IsDestroyed)
        {
            point = default;
            return false;
        }
        point = viewerHead.GlobalPosition;
        return true;
    }

    private static bool Same(in float3 a, in float3 b) => a.x == b.x && a.y == b.y && a.z == b.z;

    private static bool Same(in floatQ a, in floatQ b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
}
