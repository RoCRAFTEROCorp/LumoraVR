// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Interaction;

[ComponentCategory("Users")]
[DefaultUpdateOrder(-900)]
public sealed class InteractionLaser : Component
{
    public readonly Sync<float> MaxDistance = new();

    // How long the beam is DRAWN when it hits nothing. Independent of the reach above.
    private const float BeamIdleLength = 8f;
    public readonly Sync<float> BeamRadius = new();
    public readonly Sync<float> BeamStartOffset = new();
    public readonly Sync<Chirality> ControllerSide = new();
    public readonly Sync<colorHDR> IdleColor = new();
    public readonly Sync<colorHDR> HoverColor = new();
    public readonly Sync<float> HoverSwitchTolerance = new();
    public readonly Sync<float> DefaultHoverRadius = new();
    public readonly Sync<float> StickyHitDistance = new();
    public readonly Sync<float> VisualSmoothing = new();
    public readonly Sync<float> SmoothSpeed = new();
    public readonly Sync<float> SmoothModulateStartAngle = new();
    public readonly Sync<float> SmoothModulateEndAngle = new();
    public readonly Sync<float> SmoothModulateExponent = new();
    public readonly Sync<float> SmoothModulateMultiplier = new();
    public readonly Sync<float> CursorSize = new();
    public readonly Sync<bool> ShowDesktopBeam = new();
    public readonly Sync<bool> ShowDirectCursor = new();

    private readonly List<TargetHit> _hitBuffer = new(64);
    // Interaction targets pulled flat from the world's registry instead of recursing the tree, per frame. -xlinka
    private readonly List<IInteractionTarget> _interactionTargetBuffer = new(64);
    // Slots the physics ray skips (the laser's own user/avatar hierarchy + ignore root), rebuilt per cast. -xlinka
    private readonly List<Slot> _rayExclude = new(4);
    private Slot? _beamSlot;
    private Slot? _pointSlot;
    private Slot? _cursorSlot;
    private Slot? _cursorRootSlot;
    private Slot? _directCursorSlot;
    private Slot? _directLineSlot;
    private CurvedBeamMesh? _beamMesh;
    private OverlayUnlitMaterial? _beamMaterial;
    private OverlayUnlitMaterial? _cursorMaterial;
    private OverlayUnlitMaterial? _directCursorMaterial;
    private OverlayUnlitMaterial? _directLineMaterial;
    private SegmentMesh? _directLineMesh;
    private MeshRenderer? _beamRenderer;
    private MeshRenderer? _cursorRenderer;
    private MeshRenderer? _directCursorRenderer;
    private MeshRenderer? _directLineRenderer;
    private Slot? _ignoreRoot;
    private IInteractionTarget? _currentTarget;
    private RayTarget? _currentRayTarget;
    private ILaserPointerTarget? _currentPointerTarget;
    private Slot? _currentHitSlot;
    private float3 _currentHitPoint;
    private float _currentHitDistance;
    private bool _prevSecondaryState;
    private float3 _smoothedHitPoint;
    private bool _hasSmoothedHitPoint;
    private bool _toolPrimaryPressed;
    private bool _toolBlocksPointerActions;
    private float3 _rayOrigin;
    private float3 _rayDirection = float3.Backward;
    private float3 _castOrigin;
    private float3 _castDirection = float3.Backward;
    // Aim smoother state, kept in LOCAL USER ROOT space on purpose: walking, turning and being scaled move
    // the root, and holding the state there means none of that registers as aim error. Only the aim relative
    // to the body gets damped, so locomotion while carrying something costs nothing. -xlinka
    private float? _raySmoothSpeed;
    private float3 _smoothedRayOriginLocal;
    private float3 _smoothedRayDirectionLocal = float3.Backward;
    private bool _hasSmoothedRay;
    private long _lastRefreshFrame = long.MinValue;
    private float _laserVisibleLerp;
    // Where the beam ends this frame in world space, plus the distance the CAST covered to get there. Under an
    // external camera the cast comes off the camera and the beam off the hand, so the two disagree: the aim
    // point is what both of them share, and the cast distance is what keeps the reticle a constant size on
    // screen (it is the distance from the eye, not from the hand). -xlinka
    private float3 _aimPoint;
    private bool _hasAimPoint;
    private float _lastCastDistance;
    private bool _externalAim;
    private float3 _visualActualPoint;
    private bool _hasVisualActualPoint;
    private color _currentStartColor = color.Transparent;
    private color _currentEndColor = color.Transparent;
    private float _lastDirectHitDistance;
    private float2 _laserTextureOffset;
    private ILaserHitClassifier? _hitClassifier;
    private bool _currentHitPreferred;

    public IInteractionTarget? CurrentTarget => _currentTarget;
    public RayTarget? CurrentRayTarget => _currentRayTarget;
    public ILaserPointerTarget? CurrentPointerTarget => _currentPointerTarget;
    public Slot? CurrentHitSlot => _currentHitSlot;
    public float3 CurrentHitPoint => _currentHitPoint;
    public float CurrentHitDistance => _currentHitDistance;

    // won as a Prefer hit: may be sitting behind or inside solid geometry the beam passed straight
    // through, so anything reasoning about physical contact rather than aim needs to know the difference
    public bool CurrentHitPreferred => _currentHitPreferred;
    public float3 RayOrigin => _rayOrigin;
    public float3 RayDirection => _rayDirection;
    public bool IsActive => _currentTarget != null;

    // World point the beam ends at this frame: the hit when something is hovered, otherwise the far end of
    // the aim ray. The desktop hand-aim step points the hand here so the beam leaves it along the view's aim
    // instead of wherever the parked head happens to face. -xlinka
    public float3 AimPoint => _aimPoint;
    public bool HasAimPoint => _hasAimPoint;

    // The ray the CAST ran along. Identical to RayOrigin/RayDirection everywhere except under an external
    // desktop camera, where the cast is the camera's and the exposed ray is re-anchored at the hand. Anything
    // asking "where on that surface is the aim" wants this one, not the hand's line to the same point.
    // -xlinka
    public float3 CastOrigin => _castOrigin;
    public float3 CastDirection => _castDirection;

    public event Action<IInteractionTarget?>? TargetChanged;
    public event Action<IInteractionTarget, float3>? Activated;

    private readonly struct TargetHit
    {
        public readonly IInteractionTarget Target;
        public readonly Slot Slot;
        public readonly float Distance;
        public readonly float3 HitPoint;
        // Prefer-classified: collected past the blocking distance, and takes the selection away from
        // every ordinary hit on the ray
        public readonly bool Preferred;

        public TargetHit(IInteractionTarget target, Slot slot, float distance, float3 hitPoint, bool preferred)
        {
            Target = target;
            Slot = slot;
            Distance = distance;
            HitPoint = hitPoint;
            Preferred = preferred;
        }
    }

    public override void OnInit()
    {
        base.OnInit();
        // How far the laser REACHES. The other platform puts no limit on this in a world at all
        // (MaxLaserDistance is float.MaxValue outside userspace, where it clamps to the dash distance),
        // and 8m meant you had to walk up to anything you wanted to pick up. Finite rather than
        // infinite only because this feeds a physics raycast. The DRAWN beam is capped separately by
        // BeamIdleLength, or pointing at the sky would paint a line to the horizon. -xlinka
        MaxDistance.Value = 512f;
        BeamRadius.Value = 0.003f;
        BeamStartOffset.Value = 0.020f;
        ControllerSide.Value = Chirality.Right;
        IdleColor.Value = new colorHDR(0.30f, 0.85f, 1.00f, 0.55f);
        HoverColor.Value = new colorHDR(1.00f, 1.00f, 1.00f, 0.85f);
        HoverSwitchTolerance.Value = 0.05f;
        DefaultHoverRadius.Value = 0.05f;
        StickyHitDistance.Value = 0.08f;
        VisualSmoothing.Value = 12f;
        // Aim smoothing, armed only while a tool is carrying something on this laser. The base speed is an
        // exponential rate in 1/s (a 1/SmoothSpeed second time constant); the modulate block scales it by how
        // far the raw aim has run ahead of the smoothed ray, so sub-2-degree mouse noise is damped at the base
        // rate while a deliberate sweep past 45 degrees tracks at up to Multiplier times that. The exponent
        // below 1 makes the ramp bite early, otherwise mid-speed drags feel like dragging through syrup.
        // Faster than a bare pointer wants, because the pose write is already frame-aligned with the view -
        // this only has to eat mouse sampling noise, not a frame of lag. -xlinka
        SmoothSpeed.Value = 12f;
        SmoothModulateStartAngle.Value = 2f;
        SmoothModulateEndAngle.Value = 45f;
        SmoothModulateExponent.Value = 0.75f;
        SmoothModulateMultiplier.Value = 8f;
        CursorSize.Value = 0.018f;
        ShowDesktopBeam.Value = false;
        ShowDirectCursor.Value = true;
    }

    public override void OnStart()
    {
        base.OnStart();
        // Building the laser rig (AddSlot/AttachComponent) happens under the host-allocated HandTool slot. For the
        // OWNER of a joining User the User<->UserRoot ownership link lags a beat, so the first write gets permission-
        // denied and throws - we WANT that throw: the startup-retry re-runs OnStart until the link resolves and the
        // write is authorized, no bypass needed. The networked beam build is idempotent (adopts the replicated visual
        // if it's already there) so a non-owner peer reuses the owner's beam instead of trying to mint its own under a
        // slot it doesn't own. -xlinka
        BuildBeamVisual();
        LumoraLogger.Log($"InteractionLaser: Started on '{Slot.SlotName.Value}' side={ControllerSide.Value}");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        RefreshNow(delta);
    }

    public override void OnDestroy()
    {
        ClearCurrentTarget();
        base.OnDestroy();
    }

    public void SetIgnoreRoot(Slot? root)
    {
        _ignoreRoot = root;
    }

    // pushed from the hand rather than resolved here: a beam is not always under a hand (the userspace
    // pointer is not), and the hand is the only thing that knows when a modal menu has taken the tool's say away
    public void SetHitClassifier(ILaserHitClassifier? classifier)
    {
        _hitClassifier = classifier;
    }

    public void SetToolState(bool primaryPressed, bool blockPointerActions)
    {
        _toolPrimaryPressed = primaryPressed;
        _toolBlocksPointerActions = blockPointerActions;
    }

    // Read-only view of the press state the owning tool last pushed in. Anything that wants to act on
    // a HELD primary rather than a click edge reads this instead of sampling the controller itself, so
    // it inherits the tool's arbitration for free: suppressed while carrying an object, dead while the
    // dash owns the pointer, dead while the laser is dormant. -xlinka
    public bool ToolPrimaryPressed => _toolPrimaryPressed;

    // Desktop aim steering: while the context menu owns the mouse the camera is
    // frozen, so the mouse instead deflects the laser ray off head-forward
    // (yaw, pitch in radians). The laser cursor stays the one and only cursor. - xlinka
    private float2 _desktopAimOffset;

    public void SetDesktopAimOffset(in float2 yawPitch)
    {
        _desktopAimOffset = yawPitch;
    }

    // While set, only hits under this slot are interactable and world colliders
    // don't block the ray. Used while the context menu is open: the menu is the
    // only thing the summoning laser can touch. - xlinka
    private Slot? _exclusiveRoot;

    public void SetExclusiveRoot(Slot? root)
    {
        _exclusiveRoot = root;
    }

    // When true the beam mesh never shows, only the cursor. The userspace dash pointer
    // uses this: you want the little cursor on the panel, not a laser line stabbing out
    // of nothing (the userspace pointer has no visible hand to anchor a beam to). -xlinka
    private bool _suppressBeam;

    public void SetBeamSuppressed(bool suppressed)
    {
        _suppressBeam = suppressed;
    }

    // Dormant pointer: skips the cast and pulls all visuals offscreen this frame. The userspace
    // dash pointer flips this with the dash open state, so when the dash is closed there's no
    // stray cursor floating in the overlay world and no wasted raycasts. -xlinka
    private bool _dormant;

    public void SetDormant(bool dormant)
    {
        _dormant = dormant;
    }

    // Arm the aim smoother for a tool that is carrying something on this laser. speedOverride replaces
    // SmoothSpeed for the duration (a hand tool passes its GrabSmoothing, so a loaded laser damps harder
    // than a bare pointer); null uses SmoothSpeed. Disarming drops the state so the next arm seeds clean
    // instead of dragging the object in from wherever the pointer was last time. -xlinka
    public void ArmRaySmoothing(bool armed, float? speedOverride = null)
    {
        if (!armed)
        {
            _raySmoothSpeed = null;
            _hasSmoothedRay = false;
            return;
        }

        float speed = speedOverride ?? SmoothSpeed.Value;
        _raySmoothSpeed = speed > 0f ? speed : null;
        if (_raySmoothSpeed == null)
        {
            _hasSmoothedRay = false;
        }
    }

    // Re-resolve the aim ray late in the frame, after locomotion has moved the head, the root and the hand,
    // and slide the beam onto it. The cast is NOT repeated: hover, hit slot and hit distance stay as the cast
    // found them, only the geometry moves. A tool that hangs an object off this ray calls this immediately
    // before it writes the holder pose, so the beam and the thing on the end of it cannot disagree. -xlinka
    public void RefreshHeldAim(float delta)
    {
        if (_dormant || _beamSlot == null || IsRemoteUserLaser())
        {
            return;
        }

        float3 origin;
        float3 direction;
        float3 endPoint;
        float cursorDistance;

        if (_externalAim && _hasAimPoint)
        {
            // The camera cast already nailed the aim point into the world this frame and nothing but the hand
            // has moved since, so re-aim from where the hand is NOW at that same point. Cursor sizing keeps
            // the CAST distance: the reticle is measured from the eye, and the eye is the camera. -xlinka
            AnchorRayAtHand(_aimPoint, delta, advance: true);
            origin = _rayOrigin;
            direction = _rayDirection;
            // Hovering something pins the beam to that surface; otherwise it runs the same length down the
            // damped hand ray, so a carried object never floats off the side of its own beam.
            endPoint = _currentTarget != null
                ? _aimPoint
                : origin + direction * (_aimPoint - origin).Length;
            cursorDistance = MathF.Max(_lastCastDistance, 0f);
        }
        else
        {
            ResolveRayPose(out origin, out direction);
            ApplyRaySmoothing(ref origin, ref direction, delta, advance: true);
            _rayOrigin = origin;
            _rayDirection = direction;

            // A hovered surface point stays where the surface is; with nothing hovered the beam ends at the
            // same distance down the new ray as the cast gave it.
            cursorDistance = MathF.Max(_lastDirectHitDistance, 0f);
            endPoint = _currentTarget != null
                ? _currentHitPoint
                : origin + direction * cursorDistance;
        }

        float3 visualOrigin = origin;
        var input = Engine.Current?.InputInterface;
        if (input != null && !input.IsVRActive && Slot != null)
        {
            visualOrigin = Slot.GlobalPosition;
        }

        float3 visualDir = endPoint - visualOrigin;
        float visualLen = visualDir.Length;
        visualDir = visualLen > 0.0001f ? visualDir / visualLen : direction;

        // Zero delta: the beam's own visual smoothing already took its step during the cast, and this pass
        // is the same frame.
        PositionBeam(visualOrigin, visualDir, visualLen, endPoint, 0f);
        // Cursor scale keys off the ORIGIN-to-cursor distance, not the hand-to-cursor one the beam geometry
        // uses, or the reticle would change size between this pass and the cast on desktop (where the ray
        // comes off the head and the beam off the hand). -xlinka
        OrientCursor(cursorDistance);
    }

    // Exponential aim damping with the rate modulated by how far the raw aim has run ahead of the smoothed
    // ray. Under SmoothModulateStartAngle it is all sampling noise and gets the base rate; past
    // SmoothModulateEndAngle it is a deliberate sweep and gets up to SmoothModulateMultiplier times that, so
    // a fast 90 degree turn does not leave the object trailing halfway across the room. Everything happens in
    // local user root space (see the field comment) and only 'advance' steps the state. -xlinka
    private void ApplyRaySmoothing(ref float3 origin, ref float3 direction, float delta, bool advance)
    {
        if (_raySmoothSpeed is not float baseSpeed)
        {
            _hasSmoothedRay = false;
            return;
        }

        var space = Slot?.ActiveUserRoot?.Slot;
        float3 rawOrigin = space != null ? space.GlobalPointToLocal(origin) : origin;
        float3 rawDirection = space != null ? space.GlobalDirectionToLocal(direction) : direction;
        float rawLength = rawDirection.Length;
        if (rawLength <= 0.0001f)
        {
            _hasSmoothedRay = false;
            return;
        }
        rawDirection /= rawLength;

        if (!_hasSmoothedRay)
        {
            _smoothedRayOriginLocal = rawOrigin;
            _smoothedRayDirectionLocal = rawDirection;
            _hasSmoothedRay = true;
            return;
        }

        if (advance)
        {
            float angle = float3.Angle(_smoothedRayDirectionLocal, rawDirection) * (180f / MathF.PI);
            float start = SmoothModulateStartAngle.Value;
            float end = SmoothModulateEndAngle.Value;
            float x = end > start
                ? System.Math.Clamp((angle - start) / (end - start), 0f, 1f)
                : (angle > start ? 1f : 0f);
            x = MathF.Pow(x, MathF.Max(SmoothModulateExponent.Value, 0.0001f));
            if (float.IsNaN(x) || float.IsInfinity(x))
            {
                x = 0f;
            }

            float multiplier = MathF.Max(SmoothModulateMultiplier.Value, 1f);
            float speed = baseSpeed * (1f + (multiplier - 1f) * x);
            float t = System.Math.Clamp(1f - MathF.Exp(-speed * MathF.Max(delta, 0f)), 0f, 1f);

            _smoothedRayOriginLocal = float3.Lerp(_smoothedRayOriginLocal, rawOrigin, t);
            float3 blended = float3.Lerp(_smoothedRayDirectionLocal, rawDirection, t);
            // A near-180 flip in one frame cancels to nothing under a straight lerp; snap rather than
            // hand back a zero-length aim.
            _smoothedRayDirectionLocal = blended.Length > 0.0001f ? blended.Normalized : rawDirection;
        }

        origin = space != null ? space.LocalPointToGlobal(_smoothedRayOriginLocal) : _smoothedRayOriginLocal;
        float3 outDirection = space != null
            ? space.LocalDirectionToGlobal(_smoothedRayDirectionLocal)
            : _smoothedRayDirectionLocal;
        direction = outDirection.Length > 0.0001f ? outDirection.Normalized : direction;
    }

    private void HideVisuals()
    {
        _laserVisibleLerp = 0f;
        if (_beamSlot != null) SetIfChanged(_beamSlot.ActiveSelf, false);
        if (_cursorSlot != null) SetIfChanged(_cursorSlot.ActiveSelf, false);
        if (_directCursorSlot != null) SetIfChanged(_directCursorSlot.ActiveSelf, false);
        if (_directLineSlot != null) SetIfChanged(_directLineSlot.ActiveSelf, false);
        if (_beamRenderer != null) SetIfChanged(_beamRenderer.Enabled, false);
        if (_cursorRenderer != null) SetIfChanged(_cursorRenderer.Enabled, false);
        if (_directCursorRenderer != null) SetIfChanged(_directCursorRenderer.Enabled, false);
    }

    public void RefreshNow(float delta)
    {
        long frame = Engine.Current?.FrameCount ?? -1;
        if (frame >= 0 && _lastRefreshFrame == frame)
        {
            return;
        }

        _lastRefreshFrame = frame;

        // The cast + cursor are per-viewer and only meaningful for the local user's own hands. Another
        // user's laser is shown purely through its REPLICATED beam - we must not recast it here, or every
        // observer would build and position a local cursor for each remote hand and you'd see everyone
        // else's cursor floating in the world (issue #104). The beam is left untouched (its writes are
        // host/owner-authoritative and replicate on their own). -xlinka
        if (IsRemoteUserLaser())
        {
            HideLocalCursor();
            return;
        }

        CastAndUpdate(delta);
    }

    // True only when this laser definitively belongs to a DIFFERENT user than the local one. A laser with
    // no owning user (e.g. the userspace dash pointer) is not "remote" and keeps working normally. -xlinka
    private bool IsRemoteUserLaser()
    {
        var owningUser = Slot?.ActiveUser;
        var localUser = World?.LocalUser;
        return owningUser != null && localUser != null && !ReferenceEquals(owningUser, localUser);
    }

    // Hide just the per-viewer cursor visuals (cursor, direct cursor, direct line) without touching the
    // replicated beam, so a remote user's beam still shows but their cursor never does. -xlinka
    private void HideLocalCursor()
    {
        if (_cursorSlot != null) SetIfChanged(_cursorSlot.ActiveSelf, false);
        if (_directCursorSlot != null) SetIfChanged(_directCursorSlot.ActiveSelf, false);
        if (_directLineSlot != null) SetIfChanged(_directLineSlot.ActiveSelf, false);
        if (_cursorRenderer != null) SetIfChanged(_cursorRenderer.Enabled, false);
        if (_directCursorRenderer != null) SetIfChanged(_directCursorRenderer.Enabled, false);
    }

    private void BuildBeamVisual()
    {
        BuildNetworkedBeam();
        BuildLocalCursorParts();
    }

    // The beam (slot + curved mesh + material + renderer + the Point anchor) is NETWORKED via AddSlot, so a remote
    // user sees your laser. That means every peer runs this, but only the OWNER may mint it - the rest must REUSE the
    // replicated one. So: if the visual is already here (we built it on a retry, or it arrived from the owner), adopt
    // it by re-resolving the field refs and DON'T touch the values (we'd clobber whatever the owner set). Only a fresh
    // build sets values. If the owner's write isn't authorized yet the AddSlot throws and the startup-retry heals it.
    // -xlinka
    private void BuildNetworkedBeam()
    {
        var existing = Slot.FindChild("InteractionLaserVisual");
        if (existing != null)
        {
            _beamSlot = existing;
            _beamMesh = existing.GetComponent<CurvedBeamMesh>();
            _beamRenderer = existing.GetComponent<MeshRenderer>();
            _beamMaterial = existing.FindChild("InteractionLaserMaterial")?.GetComponent<OverlayUnlitMaterial>();
            _pointSlot = existing.FindChild("Point");
            // Adopted - the owner already set every value, and the local cursor build below fills its own refs.
            // Anything still null (a half-synced adopt) is tolerated: CastAndUpdate/PositionBeam null-guard _beamSlot
            // and _beamMesh, and a later sync doesn't re-run OnStart, but the renderer/material drive themselves once
            // their values land. -xlinka
            return;
        }

        _beamSlot = Slot.AddSlot("InteractionLaserVisual");

        _beamMesh = _beamSlot.AttachComponent<CurvedBeamMesh>();
        _beamMesh.Radius.Value = BeamRadius.Value;
        _beamMesh.Sides.Value = 6;
        _beamMesh.Segments.Value = 16;
        _beamMesh.Capped.Value = true;
        _beamMesh.StartPoint.Value = float3.Zero;
        _beamMesh.DirectTargetPoint.Value = float3.Backward * BeamIdleLength;
        _beamMesh.ActualTargetPoint.Value = float3.Backward * BeamIdleLength;
        _beamMesh.StartPointColor.Value = IdleColor.Value.ToLDR();
        _beamMesh.EndPointColor.Value = IdleColor.Value.ToLDR();

        var matSlot = _beamSlot.AddSlot("InteractionLaserMaterial");
        _beamMaterial = matSlot.AttachComponent<OverlayUnlitMaterial>();
        _beamMaterial.FrontTintColor.Value = colorHDR.White;
        _beamMaterial.BehindTintColor.Value = new colorHDR(0.5f, 0.5f, 0.5f, 0.35f);
        _beamMaterial.UseVertexColor.Value = true;
        _beamMaterial.BlendMode.Value = BlendMode.Additive;
        _beamMaterial.Culling.Value = Culling.None;
        _beamMaterial.RenderQueue.Value = 4010;

        _beamRenderer = _beamSlot.AttachComponent<MeshRenderer>();
        _beamRenderer.Mesh.Target = _beamMesh;
        _beamRenderer.Material.Target = _beamMaterial;
        _beamRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
        _beamRenderer.SortingOrder.Value = 110;

        _pointSlot = _beamSlot.AddSlot("Point");
        _pointSlot.LocalPosition.Value = float3.Backward * BeamIdleLength;

        _beamSlot.ActiveSelf.Value = false;
    }

    // Cursor render band, ABOVE the dash surface's reserved band (UserspaceDashboard.DashSurfaceSortingOrder
    // = 20000) so the pointer stays visible on the open dashboard. Effective draw order is
    // SortingOffset = material RenderQueue + SortingOrder. -xlinka
    private const int CursorSortingOrder = 21000;
    private const int DirectCursorSortingOrder = 20900;

    // The cursor / direct-cursor / direct-line are LOCAL (per-viewer, like an OS mouse cursor) - AddLocalSlot mints a
    // LOCAL_BYTE RefID the world never replicates, so the whole cursor subtree stays on this machine only and other
    // users never see your cursor (just your beam, built above). Because they're local, they DON'T arrive when a
    // non-owner adopts the replicated beam, so every peer builds its own here. FindChild-first keeps it idempotent so
    // a retry or re-entry adopts what's already there instead of double-adding. Hit-testing/dash clicks use the ray
    // pose, never these slots, so they're unaffected. -xlinka
    private void BuildLocalCursorParts()
    {
        if (_pointSlot == null || _beamSlot == null) return;

        _cursorSlot = _pointSlot.FindChild("Cursor");
        if (_cursorSlot == null)
        {
            _cursorSlot = _pointSlot.AddLocalSlot("Cursor");
            _cursorMaterial = _cursorSlot.AttachComponent<OverlayUnlitMaterial>();
            _cursorMaterial.BlendMode.Value = BlendMode.Additive;
            _cursorMaterial.FrontTintColor.Value = colorHDR.White;
            _cursorMaterial.BehindTintColor.Value = new colorHDR(1f, 1f, 1f, 0.35f);
            _cursorMaterial.UseVertexColor.Value = true;
            _cursorMaterial.RenderQueue.Value = 4005;

            _cursorRootSlot = _cursorSlot.AddSlot("Image");
            var cursorMesh = _cursorRootSlot.AttachComponent<QuadMesh>();
            cursorMesh.Size.Value = float2.One * CursorSize.Value;
            cursorMesh.DualSided.Value = true;
            cursorMesh.Color = color.White;

            _cursorRenderer = _cursorRootSlot.AttachComponent<MeshRenderer>();
            _cursorRenderer.Mesh.Target = cursorMesh;
            _cursorRenderer.Material.Target = _cursorMaterial;
            _cursorRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
            // Draw order is SortingOffset = material RenderQueue + SortingOrder (uncapped). The cursor sits in
            // a band ABOVE the dash surface (UserspaceDashboard reserves ~20000) so the pointer stays visible
            // when aiming at the open dashboard; in-world it just keeps the cursor on top of UI it points at.
            // -xlinka
            _cursorRenderer.SortingOrder.Value = CursorSortingOrder;
        }
        else
        {
            _cursorMaterial = _cursorSlot.GetComponent<OverlayUnlitMaterial>();
            _cursorRootSlot = _cursorSlot.FindChild("Image");
            _cursorRenderer = _cursorRootSlot?.GetComponent<MeshRenderer>();
        }

        _directCursorSlot = _beamSlot.FindChild("DirectCursor");
        if (_directCursorSlot == null)
        {
            _directCursorSlot = _beamSlot.AddLocalSlot("DirectCursor");
            _directCursorMaterial = _directCursorSlot.AttachComponent<OverlayUnlitMaterial>();
            _directCursorMaterial.BlendMode.Value = BlendMode.Additive;
            _directCursorMaterial.FrontTintColor.Value = new colorHDR(1f, 1f, 1f, 0.25f);
            _directCursorMaterial.BehindTintColor.Value = new colorHDR(1f, 1f, 1f, 0.15f);
            _directCursorMaterial.UseVertexColor.Value = true;
            _directCursorMaterial.RenderQueue.Value = 4000;

            var directCursorMesh = _directCursorSlot.AttachComponent<QuadMesh>();
            directCursorMesh.Size.Value = float2.One * (CursorSize.Value * 0.75f);
            directCursorMesh.DualSided.Value = true;
            directCursorMesh.Color = new color(1f, 1f, 1f, 0.25f);

            _directCursorRenderer = _directCursorSlot.AttachComponent<MeshRenderer>();
            _directCursorRenderer.Mesh.Target = directCursorMesh;
            _directCursorRenderer.Material.Target = _directCursorMaterial;
            _directCursorRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
            _directCursorRenderer.SortingOrder.Value = DirectCursorSortingOrder;
        }
        else
        {
            _directCursorMaterial = _directCursorSlot.GetComponent<OverlayUnlitMaterial>();
            _directCursorRenderer = _directCursorSlot.GetComponent<MeshRenderer>();
        }

        _directLineSlot = _beamSlot.FindChild("DirectLine");
        if (_directLineSlot == null)
        {
            _directLineSlot = _beamSlot.AddLocalSlot("DirectLine");
            _directLineMesh = _directLineSlot.AttachComponent<SegmentMesh>();
            _directLineMesh.Radius.Value = BeamRadius.Value * 0.75f;
            _directLineMesh.Sides.Value = 6;
            _directLineMesh.PointA.Value = float3.Backward * BeamIdleLength;
            _directLineMesh.PointB.Value = float3.Backward * BeamIdleLength;
            _directLineMesh.PointAColor.Value = new color(1f, 1f, 1f, 0.18f);
            _directLineMesh.PointBColor.Value = new color(1f, 1f, 1f, 0.18f);

            _directLineMaterial = _directLineSlot.AttachComponent<OverlayUnlitMaterial>();
            _directLineMaterial.BlendMode.Value = BlendMode.Additive;
            _directLineMaterial.FrontTintColor.Value = colorHDR.White;
            _directLineMaterial.BehindTintColor.Value = new colorHDR(1f, 1f, 1f, 0.35f);
            _directLineMaterial.UseVertexColor.Value = true;
            _directLineMaterial.RenderQueue.Value = 4000;

            _directLineRenderer = _directLineSlot.AttachComponent<MeshRenderer>();
            _directLineRenderer.Mesh.Target = _directLineMesh;
            _directLineRenderer.Material.Target = _directLineMaterial;
            _directLineRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
            _directLineRenderer.SortingOrder.Value = 100;
        }
        else
        {
            _directLineMesh = _directLineSlot.GetComponent<SegmentMesh>();
            _directLineMaterial = _directLineSlot.GetComponent<OverlayUnlitMaterial>();
            _directLineRenderer = _directLineSlot.GetComponent<MeshRenderer>();
        }

        SetIfChanged(_cursorSlot.ActiveSelf, false);
        SetIfChanged(_directCursorSlot.ActiveSelf, false);
        SetIfChanged(_directLineSlot.ActiveSelf, false);
    }

    private void CastAndUpdate(float delta)
    {
        if (_beamSlot == null || World?.RootSlot == null) return;

        if (_dormant)
        {
            // Pointer is parked (e.g. userspace dash pointer while the dash is closed). Drop the
            // hover target so nothing stays "pressed", hide everything, and skip the cast. -xlinka
            if (_currentTarget != null) UpdatePointerTarget(null, _rayOrigin, _rayDirection, false);
            _currentTarget = null;
            _currentHitSlot = null;
            _currentHitPreferred = false;
            _hasAimPoint = false;
            HideVisuals();
            return;
        }

        float maxDist = MathF.Max(MaxDistance.Value, 0.01f);
        _externalAim = UseExternalCameraAim(Engine.Current?.InputInterface);
        ResolveRayPose(out float3 origin, out float3 direction);
        // Read the smoother without stepping it. The step belongs to RefreshHeldAim, which runs later in the
        // frame off a head/root that has already moved; stepping here as well would double the rate and make
        // the damping frame-order dependent. Under an external camera the cast is the CAMERA's and stays
        // crosshair-exact - the smoothing moves to the hand ray, the one carrying the load. -xlinka
        if (!_externalAim)
        {
            ApplyRaySmoothing(ref origin, ref direction, delta, advance: false);
        }
        _rayOrigin = origin;
        _rayDirection = direction;
        _castOrigin = origin;
        _castDirection = direction;

        bool exclusive = _exclusiveRoot != null && !_exclusiveRoot.IsDestroyed;

        // One cast, two consumers. The blocking distance and the shape-hit target resolve are the same ray
        // with the same arguments; casting it twice doubled the physics cost of every hand every frame,
        // and each cast can re-issue itself up to RaycastSkipLimit times through filtered hits. -xlinka
        _castHitValid = false;
        float blockingDistance = maxDist;
        _colliderHitSlot = null;
        _colliderHitPoint = float3.Zero;
        if (!exclusive && TryFindNearestColliderHitDistance(origin, direction, maxDist, out float colliderHitDistance))
        {
            blockingDistance = colliderHitDistance;
        }

        _hitBuffer.Clear();
        CollectInteractionHits(World, origin, direction, maxDist, blockingDistance);

        // The dashboard lives in the userspace overlay world and is pointed at by a rig that also
        // lives there (the userspace pointer rig, see UserspacePointer) in BOTH desktop and VR. So
        // this in-world laser only ever touches its OWN world - no cross-world cast. That's what
        // lets the dash keep a working cursor even after you delete the world you were standing in:
        // the pointer was never tied to this avatar to begin with. -xlinka

        if (exclusive)
        {
            for (int i = _hitBuffer.Count - 1; i >= 0; i--)
            {
                var hitSlot = _hitBuffer[i].Slot;
                if (hitSlot == null || (hitSlot != _exclusiveRoot && !hitSlot.IsDescendantOf(_exclusiveRoot!)))
                {
                    _hitBuffer.RemoveAt(i);
                }
            }
        }

        // PREFERRED HITS TAKE THE WHOLE SELECTION. A gizmo arrow lives inside the object it moves and a
        // ring can end up behind a wall, so the occluder is always the nearer hit - the only way the
        // control is ever reachable is for its hit to beat every ordinary one on the ray, not merely
        // outrank it by priority. Dropping the rest here means nearest-wins, sticky-keep and priority
        // promotion below all run over preferred hits alone, and the ordinary path is untouched on
        // every frame where nothing asked to be preferred. -xlinka
        bool anyPreferred = false;
        for (int i = 0; i < _hitBuffer.Count; i++)
        {
            if (!_hitBuffer[i].Preferred) continue;
            anyPreferred = true;
            break;
        }
        if (anyPreferred)
        {
            for (int i = _hitBuffer.Count - 1; i >= 0; i--)
            {
                if (!_hitBuffer[i].Preferred)
                    _hitBuffer.RemoveAt(i);
            }
        }

        IInteractionTarget? hoveredTarget = null;
        Slot? hoveredSlot = null;
        float hoveredDistance = maxDist;
        float3 hoveredHitPoint = float3.Zero;
        bool hoveredPreferred = false;

        if (_hitBuffer.Count > 0)
        {
            int nearestIndex = 0;
            float nearestDistance = _hitBuffer[0].Distance;
            for (int i = 1; i < _hitBuffer.Count; i++)
            {
                if (_hitBuffer[i].Distance < nearestDistance)
                {
                    nearestDistance = _hitBuffer[i].Distance;
                    nearestIndex = i;
                }
            }

            int selectedIndex = nearestIndex;
            float keepThreshold = nearestDistance + MathF.Max(HoverSwitchTolerance.Value, 0f);
            if (_currentTarget != null)
            {
                for (int i = 0; i < _hitBuffer.Count; i++)
                {
                    var hit = _hitBuffer[i];
                    if (ReferenceEquals(hit.Target, _currentTarget) && hit.Distance <= keepThreshold)
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }

            var selected = _hitBuffer[selectedIndex];
            // walk parent chain on the selected slot for a higher-priority target. - xlinka
            var promoted = PromoteToHighestPriority(selected.Target, selected.Slot);
            hoveredTarget = promoted;
            hoveredSlot = selected.Slot;
            hoveredDistance = selected.Distance;
            hoveredHitPoint = selected.HitPoint;
            hoveredPreferred = selected.Preferred;
        }
        else if (!exclusive && TryKeepStickyTarget(origin, direction, maxDist, blockingDistance,
            out var stickyTarget, out var stickySlot, out float stickyDistance, out float3 stickyPoint))
        {
            hoveredTarget = stickyTarget;
            hoveredSlot = stickySlot;
            hoveredDistance = stickyDistance;
            hoveredHitPoint = stickyPoint;
            // Same target as last frame, so it keeps the class it was selected with - a handle the ray
            // slid off for one frame must not start being occluded by the thing it sits inside.
            hoveredPreferred = _currentHitPreferred;
        }

        if (!ReferenceEquals(hoveredTarget, _currentTarget))
        {
            ClearCurrentTarget();
            _currentTarget = hoveredTarget;
            _currentRayTarget = hoveredTarget as RayTarget;
            _currentRayTarget?.NotifyHoverEntered();
            TargetChanged?.Invoke(_currentTarget);
        }
        if (hoveredTarget != null)
        {
            hoveredHitPoint = ApplyHitPointSmoothing(hoveredSlot, hoveredHitPoint, delta);
        }
        else
        {
            _hasSmoothedHitPoint = false;
        }
        _currentHitSlot = hoveredSlot;
        _currentHitPoint = hoveredHitPoint;
        _currentHitDistance = hoveredDistance;
        _currentHitPreferred = hoveredTarget != null && hoveredPreferred;

        UpdatePointerTarget(hoveredTarget as ILaserPointerTarget, origin, direction, _toolPrimaryPressed);
        ProcessPointerActions();

        float beamLength = hoveredTarget != null ? hoveredDistance : blockingDistance;
        float3 beamEndPoint = hoveredTarget != null
            ? _currentHitPoint
            : new float3(
                origin.x + direction.x * beamLength,
                origin.y + direction.y * beamLength,
                origin.z + direction.z * beamLength);

        // What gets DRAWN when the ray hits nothing. The cast runs to the full reach so distant things
        // are still grabbable; the beam stops short so a miss is a short pointer, not a stripe across
        // the world. -xlinka
        float3 visualEndPoint = beamEndPoint;
        if (hoveredTarget == null && beamLength > BeamIdleLength)
        {
            visualEndPoint = new float3(
                origin.x + direction.x * BeamIdleLength,
                origin.y + direction.y * BeamIdleLength,
                origin.z + direction.z * BeamIdleLength);
        }

        _aimPoint = beamEndPoint;
        _hasAimPoint = true;
        _lastCastDistance = beamLength;

        // Under an external camera the cast came off the camera, but the held object, the UI resolution and
        // the beam all hang off the HAND. Re-anchor the ray the tool sees there, aimed at what the crosshair
        // found, and damp THAT instead of the cast. The hit distance moves to the same anchor or a grab would
        // shove the object out by however far the camera is parked behind the body. -xlinka
        if (_externalAim && Slot != null)
        {
            AnchorRayAtHand(beamEndPoint, delta, advance: false);
            _currentHitDistance = (beamEndPoint - _rayOrigin).Length;

            // Reticle sizing is a distance-from-the-EYE measure and the cast started short of the lens by
            // the body skip. Measure from the camera itself or the cursor comes out undersized by however
            // far the view is parked behind the avatar. -xlinka
            var aimInput = Engine.Current?.InputInterface;
            if (aimInput != null && aimInput.DesktopCameraPoseValid)
            {
                _lastCastDistance = (beamEndPoint - aimInput.DesktopCameraPosition).Length;
            }
        }

        // The aim RAY is cast from the head on desktop (so it lines up with the screen reticle), but the
        // visible beam must start at the HAND - this laser's own slot. The beam slot's transform replicates,
        // so anchoring it at the ray origin made every other user see the beam shoot out of the desktop
        // user's head. Anchor it at the hand and aim it at the hit point instead. In VR the ray origin is
        // already the hand, so this is a no-op there. -xlinka
        float3 visualOrigin = origin;
        var beamInput = Engine.Current?.InputInterface;
        if (beamInput != null && !beamInput.IsVRActive && Slot != null)
        {
            visualOrigin = Slot.GlobalPosition;
        }
        float3 visualDir = visualEndPoint - visualOrigin;
        float visualLen = visualDir.Length;
        visualDir = visualLen > 0.0001f ? visualDir / visualLen : direction;

        PositionBeam(visualOrigin, visualDir, visualLen, visualEndPoint, delta);

        if (_beamMesh != null)
        {
            // Color the beam + reticle by what the hovered target offers (grab/scale/receive/disabled/...),
            // read from its interaction description. Nothing hovered falls back to the idle color. -xlinka
            color wantedColor;
            if (hoveredTarget != null)
            {
                var description = hoveredTarget.GetInteractionDescription(this);
                wantedColor = ResolveCursorColor(in description);
            }
            else
            {
                wantedColor = IdleColor.Value.ToLDR();
            }
            SetIfChanged(_beamMesh.Radius, BeamRadius.Value);
            // Cursor sizing still uses the CAST distance so the reticle keeps a stable on-screen size; only
            // the beam geometry uses the hand origin. -xlinka
            // The VISUAL distance, not the cast distance. The cursor is sized by how far away it is so it
            // keeps a constant angular size, and on a miss the cast now runs to the full reach - so
            // looking at the sky sized the cursor for a point hundreds of metres away and filled the
            // screen with it. It is drawn at the capped beam end, so it must be sized for there too.
            // -xlinka
            float visualDistance = hoveredTarget != null
                ? _lastCastDistance
                : MathF.Min(_lastCastDistance, BeamIdleLength);
            UpdateLaserVisual(delta, hoveredTarget != null, wantedColor, visualDistance);
        }
    }

    private static void SetIfChanged(Sync<colorHDR> field, colorHDR value)
    {
        colorHDR current = field.Value;
        if (current.r != value.r || current.g != value.g ||
            current.b != value.b || current.a != value.a)
        {
            field.Value = value;
        }
    }

    private static void SetIfChanged(Sync<color> field, color value)
    {
        color current = field.Value;
        if (current.r != value.r || current.g != value.g ||
            current.b != value.b || current.a != value.a)
        {
            field.Value = value;
        }
    }

    private static void SetIfChanged(Sync<float3> field, float3 value)
    {
        float3 current = field.Value;
        if (MathF.Abs(current.x - value.x) > 0.0001f ||
            MathF.Abs(current.y - value.y) > 0.0001f ||
            MathF.Abs(current.z - value.z) > 0.0001f)
        {
            field.Value = value;
        }
    }

    private static void SetIfChanged(Sync<float2> field, float2 value)
    {
        float2 current = field.Value;
        if (MathF.Abs(current.x - value.x) > 0.0001f ||
            MathF.Abs(current.y - value.y) > 0.0001f)
        {
            field.Value = value;
        }
    }

    private static void SetIfChanged(Sync<float> field, float value)
    {
        if (MathF.Abs(field.Value - value) > 0.0001f)
        {
            field.Value = value;
        }
    }

    private static void SetIfChanged(Sync<bool> field, bool value)
    {
        if (field.Value != value)
        {
            field.Value = value;
        }
    }

    // Per-cursor-type colors so the beam + reticle signal what the hovered target offers: green = grab,
    // amber = scale, blue = receive/drop, red = can't interact. Default/Activated use the configured hover
    // color, and a target may override the color outright. -xlinka
    private static readonly color GrabCursorColor = new(0.35f, 0.95f, 0.5f, 1f);
    private static readonly color ScaleCursorColor = new(1f, 0.8f, 0.3f, 1f);
    private static readonly color ReceiveCursorColor = new(0.45f, 0.7f, 1f, 1f);
    private static readonly color TextCursorColor = new(0.85f, 0.9f, 1f, 1f);
    private static readonly color DisabledCursorColor = new(0.78f, 0.36f, 0.36f, 1f);

    private color ResolveCursorColor(in InteractionDescription description)
    {
        if (description.OverrideHitColor.HasValue)
        {
            return description.OverrideHitColor.Value;
        }

        return (description.Cursor ?? LaserCursor.Default) switch
        {
            LaserCursor.Grab => GrabCursorColor,
            LaserCursor.Scale => ScaleCursorColor,
            LaserCursor.Receive => ReceiveCursorColor,
            LaserCursor.Text => TextCursorColor,
            LaserCursor.Disabled => DisabledCursorColor,
            _ => HoverColor.Value.ToLDR(),
        };
    }

    private void UpdateLaserVisual(float delta, bool hasTarget, color targetColor, float pointDistance)
    {
        var input = Engine.Current?.InputInterface;
        bool isVr = input?.IsVRActive == true;
        // Desktop has no screen-space reticle - this in-world cursor is the only
        // pointer, so it stays visible even with nothing hovered.
        bool isDesktop = input != null && !isVr;
        bool shouldShow = hasTarget || isVr || isDesktop;
        float visualStep = MathF.Max(delta, 0f) * 6f;
        _laserVisibleLerp = Progress01(_laserVisibleLerp, visualStep, shouldShow);

        bool rootVisible = _laserVisibleLerp > 0.001f;
        // Under an external camera the beam is the only thing that tells you where the hand is pointing, but a
        // line hanging off it at all times is just clutter in a shot you can see your whole avatar in. Show it
        // while it means something: something hovered, a press down, or a load on the end of it. -xlinka
        // Right side only: the desktop pointer is the right hand (that is where the mouse buttons are read),
        // so the off hand casts along for hit resolution but never grows a beam of its own.
        bool externalBeam = _externalAim && ControllerSide.Value == Chirality.Right
            && (hasTarget || _toolPrimaryPressed || _toolBlocksPointerActions);
        bool beamVisible = rootVisible && (isVr || ShowDesktopBeam.Value || externalBeam) && !_suppressBeam;
        bool cursorVisible = rootVisible;
        bool directVisible = rootVisible && ShowDirectCursor.Value && isVr;

        if (_beamSlot != null) SetIfChanged(_beamSlot.ActiveSelf, rootVisible);
        if (_cursorSlot != null) SetIfChanged(_cursorSlot.ActiveSelf, cursorVisible);
        if (_directCursorSlot != null) SetIfChanged(_directCursorSlot.ActiveSelf, directVisible);
        if (_directLineSlot != null) SetIfChanged(_directLineSlot.ActiveSelf, directVisible);
        if (_beamRenderer != null) SetIfChanged(_beamRenderer.Enabled, beamVisible);
        if (_cursorRenderer != null) SetIfChanged(_cursorRenderer.Enabled, cursorVisible);
        if (_directCursorRenderer != null) SetIfChanged(_directCursorRenderer.Enabled, directVisible);
        if (_directLineRenderer != null) SetIfChanged(_directLineRenderer.Enabled, directVisible);

        if (!rootVisible)
        {
            return;
        }

        float endVisibility = MathF.Min(1f, _laserVisibleLerp * 2f);
        float startVisibility = MathF.Min(1f, MathF.Max(0f, _laserVisibleLerp - 0.35f) / 0.65f);
        float colorStep = System.Math.Clamp(MathF.Max(delta, 0f) * 10f, 0f, 1f);
        _currentStartColor = color.Lerp(_currentStartColor, _currentEndColor, colorStep);
        _currentEndColor = color.Lerp(_currentEndColor, targetColor, colorStep);

        color startColor = color.Lerp(_currentStartColor, color.White, 0.25f) * startVisibility;
        color endColor = color.Lerp(_currentEndColor, color.White, 0.25f) * endVisibility;
        SetIfChanged(_beamMesh!.StartPointColor, startColor);
        SetIfChanged(_beamMesh.EndPointColor, endColor);

        // Keep the reticle bright but let the target color read through (grab/disabled/etc. should be
        // visible on the cursor, not washed to white). -xlinka
        color cursorColor = color.Lerp(_currentEndColor, color.White, 0.55f);
        cursorColor.a *= endVisibility;
        if (_cursorMaterial != null)
        {
            var tint = new colorHDR(cursorColor.r, cursorColor.g, cursorColor.b, cursorColor.a);
            SetIfChanged(_cursorMaterial.FrontTintColor, tint);
            SetIfChanged(_cursorMaterial.BehindTintColor, new colorHDR(cursorColor.r, cursorColor.g, cursorColor.b, cursorColor.a * 0.5f));
        }

        if (_directCursorMaterial != null)
        {
            var directTint = new colorHDR(cursorColor.r, cursorColor.g, cursorColor.b, cursorColor.a * 0.25f);
            SetIfChanged(_directCursorMaterial.FrontTintColor, directTint);
            SetIfChanged(_directCursorMaterial.BehindTintColor, new colorHDR(cursorColor.r, cursorColor.g, cursorColor.b, cursorColor.a * 0.15f));
        }

        if (_directLineMaterial != null)
        {
            var lineTint = new colorHDR(cursorColor.r, cursorColor.g, cursorColor.b, cursorColor.a * 0.25f);
            SetIfChanged(_directLineMaterial.FrontTintColor, lineTint);
            SetIfChanged(_directLineMaterial.BehindTintColor, new colorHDR(cursorColor.r, cursorColor.g, cursorColor.b, cursorColor.a * 0.15f));
        }

        OrientCursor(pointDistance);
        _laserTextureOffset += new float2(0f, delta * 2f);
        if (_beamMaterial != null)
        {
            SetIfChanged(_beamMaterial.FrontTextureOffset, _laserTextureOffset);
            SetIfChanged(_beamMaterial.BehindTextureOffset, _laserTextureOffset);
        }
    }

    private static float Progress01(float current, float amount, bool target)
    {
        if (target)
        {
            return System.Math.Clamp(current + amount, 0f, 1f);
        }
        return System.Math.Clamp(current - amount, 0f, 1f);
    }

    private void OrientCursor(float pointDistance)
    {
        if (_pointSlot == null || _cursorSlot == null)
        {
            return;
        }

        var input = Engine.Current?.InputInterface;
        if (input != null && !input.IsVRActive && input.DesktopCameraPoseValid)
        {
            // Desktop: screen-aligned, like an OS cursor. A look-at billboard
            // would visibly yaw and roll as the cursor sweeps across the view.
            _cursorSlot.GlobalRotation = input.DesktopCameraRotation;
        }
        else
        {
            var head = FindHeadSlot();
            float3 viewPosition = head?.GlobalPosition ?? (_rayOrigin - _rayDirection);
            float3 viewUp = head?.Up ?? float3.Up;
            float3 toView = viewPosition - _pointSlot.GlobalPosition;
            if (toView.Length <= 0.0001f)
            {
                toView = -_rayDirection;
            }
            if (viewUp.Length <= 0.0001f)
            {
                viewUp = float3.Up;
            }

            _cursorSlot.GlobalRotation = SafeLookRotation(toView, viewUp);
        }
        float cursorScale = MathF.Max(0.35f, pointDistance) * 0.6f;
        _cursorSlot.LocalScale.Value = float3.One * cursorScale;

        if (_directCursorSlot != null)
        {
            _directCursorSlot.GlobalRotation = _cursorSlot.GlobalRotation;
            _directCursorSlot.LocalScale.Value = float3.One * cursorScale;
        }
    }

    private static floatQ SafeLookRotation(float3 forward, float3 up)
    {
        if (forward.Length <= 0.0001f)
        {
            return floatQ.Identity;
        }

        forward = forward.Normalized;
        if (up.Length <= 0.0001f)
        {
            up = float3.Up;
        }

        up = up - forward * float3.Dot(up, forward);
        if (up.Length <= 0.0001f)
        {
            up = MathF.Abs(float3.Dot(forward, float3.Up)) > 0.95f ? float3.Right : float3.Up;
            up = up - forward * float3.Dot(up, forward);
        }

        return floatQ.LookRotation(forward, up.Normalized);
    }

    private void ClearCurrentTarget()
    {
        if (_currentRayTarget != null)
        {
            _currentRayTarget.NotifyHoverExited();
            _currentRayTarget = null;
        }
        _currentTarget = null;
        _currentHitSlot = null;
        _currentHitPreferred = false;
        ClearPointerTarget();
        _hasSmoothedHitPoint = false;
    }

    private void UpdatePointerTarget(ILaserPointerTarget? pointerTarget, float3 origin, float3 direction, bool isPressed)
    {
        int pointerId = GetPointerId();
        if (!ReferenceEquals(pointerTarget, _currentPointerTarget))
        {
            _currentPointerTarget?.ClearLaserPointer(this, pointerId);
            _currentPointerTarget = pointerTarget;
        }

        _currentPointerTarget?.UpdateLaserPointer(this, pointerId, origin, direction, isPressed);
    }

    private void ProcessPointerActions()
    {
        int pointerId = GetPointerId();

        if (!_toolBlocksPointerActions && _currentPointerTarget is ILaserAxisTarget axisTarget)
        {
            float2 axis = ReadPointerAxis();
            if (axis != float2.Zero)
            {
                axisTarget.ProcessLaserAxis(this, pointerId, in axis);
            }
        }

        bool secondaryNow = ReadSecondaryPressed();
        if (!_toolBlocksPointerActions && secondaryNow && !_prevSecondaryState &&
            _currentPointerTarget is ILaserSecondaryTarget secondaryTarget)
        {
            secondaryTarget.TriggerLaserSecondary(this, pointerId);
        }
        _prevSecondaryState = secondaryNow;
    }

    private void ClearPointerTarget()
    {
        if (_currentPointerTarget == null) return;

        _currentPointerTarget.ClearLaserPointer(this, GetPointerId());
        _currentPointerTarget = null;
    }

    private int GetPointerId()
    {
        return ControllerSide.Value == Chirality.Left ? 1 : 2;
    }

    // The id this laser hovers canvases under, so a grab or a drop can address the same pointer state.
    public int PointerId => GetPointerId();

    internal void NotifyActivatedByTool(IInteractionTarget target, float3 point)
    {
        Activated?.Invoke(target, point);
    }

    // walk parent chain from the hit slot. take the highest-priority IInteractionTarget
    // found. stop at SearchBlock on a non-origin ancestor. - xlinka
    private static IInteractionTarget PromoteToHighestPriority(IInteractionTarget seed, Slot hitSlot)
    {
        IInteractionTarget best = seed;
        int bestPriority = seed.InteractionTargetPriority;
        var current = hitSlot;
        while (current != null)
        {
            if (!ReferenceEquals(current, hitSlot) && current.GetComponent<SearchBlock>() != null)
            {
                break;
            }
            foreach (var t in current.GetComponentsImplementing<IInteractionTarget>())
            {
                if (t.InteractionTargetPriority > bestPriority)
                {
                    best = t;
                    bestPriority = t.InteractionTargetPriority;
                }
            }
            current = current.Parent;
        }
        return best;
    }

    private bool TryKeepStickyTarget(float3 origin, float3 direction, float maxDistance, float blockingDistance,
        out IInteractionTarget? target, out Slot? slot, out float distance, out float3 hitPoint)
    {
        target = null;
        slot = null;
        distance = 0f;
        hitPoint = float3.Zero;

        if (_currentTarget == null || _currentHitSlot == null) return false;
        if (_currentTarget is Component comp && (!comp.Enabled.Value || comp.IsDestroyed)) return false;
        if (!_currentHitSlot.IsActive) return false;

        float3 oldDelta = new(
            _currentHitPoint.x - origin.x,
            _currentHitPoint.y - origin.y,
            _currentHitPoint.z - origin.z);
        float projected = float3.Dot(oldDelta, direction);
        if (projected <= 0f || projected > maxDistance) return false;
        // A preferred target was never occluded to begin with, so the blocking distance has no say over
        // whether it can be kept.
        if (!_currentHitPreferred && projected > blockingDistance) return false;

        float3 candidate = new(
            origin.x + direction.x * projected,
            origin.y + direction.y * projected,
            origin.z + direction.z * projected);
        float3 miss = new(
            candidate.x - _currentHitPoint.x,
            candidate.y - _currentHitPoint.y,
            candidate.z - _currentHitPoint.z);
        if (miss.Length > MathF.Max(StickyHitDistance.Value, 0f)) return false;
        if (!TryApplyLaserModifiers(_currentHitSlot, direction, candidate, out candidate)) return false;

        target = _currentTarget;
        slot = _currentHitSlot;
        distance = projected;
        hitPoint = candidate;
        return true;
    }

    private bool TryApplyLaserModifiers(Slot hitSlot, float3 direction, float3 point, out float3 filteredPoint)
    {
        filteredPoint = point;

        var current = hitSlot;
        while (current != null)
        {
            if (!ReferenceEquals(current, hitSlot) && current.GetComponent<SearchBlock>() != null)
            {
                break;
            }

            foreach (var modifier in current.GetComponentsImplementing<ILaserInteractionModifier>())
            {
                if (modifier is Component comp && (!comp.Enabled.Value || comp.IsDestroyed)) continue;
                if (!modifier.IsInteractionHit(filteredPoint, direction)) return false;
                filteredPoint = modifier.FilterPoint(this, filteredPoint);
            }

            current = current.Parent;
        }

        return true;
    }

    private float3 ApplyHitPointSmoothing(Slot? hitSlot, float3 newPoint, float delta)
    {
        if (hitSlot == null)
        {
            _smoothedHitPoint = newPoint;
            _hasSmoothedHitPoint = true;
            return newPoint;
        }

        if (!_hasSmoothedHitPoint)
        {
            _smoothedHitPoint = newPoint;
            _hasSmoothedHitPoint = true;
            return newPoint;
        }

        float? smoothSpeed = null;
        var current = hitSlot;
        while (current != null)
        {
            if (!ReferenceEquals(current, hitSlot) && current.GetComponent<SearchBlock>() != null)
            {
                break;
            }

            foreach (var modifier in current.GetComponentsImplementing<ILaserInteractionModifier>())
            {
                if (modifier is Component comp && (!comp.Enabled.Value || comp.IsDestroyed)) continue;
                float? speed = modifier.GetSmoothSpeed(this, newPoint, _smoothedHitPoint);
                if (!speed.HasValue) continue;
                smoothSpeed = smoothSpeed.HasValue
                    ? MathF.Min(smoothSpeed.Value, speed.Value)
                    : speed.Value;
            }

            current = current.Parent;
        }

        if (!smoothSpeed.HasValue || smoothSpeed.Value <= 0f)
        {
            _smoothedHitPoint = newPoint;
            return newPoint;
        }

        float t = 1f - MathF.Exp(-smoothSpeed.Value * MathF.Max(delta, 0f));
        _smoothedHitPoint = float3.Lerp(_smoothedHitPoint, newPoint, System.Math.Clamp(t, 0f, 1f));
        return _smoothedHitPoint;
    }

    // Pull every interaction target in this world straight from its registry instead of recursing the whole
    // slot tree per frame, per laser. Each candidate is fully filtered below (enabled/active/hierarchy), so a
    // momentarily stale registry entry can't produce a wrong hit - same guarantee as the collider raycast.
    // The old subtree skip (!slot.IsActive -> return) becomes a per-target slot.IsActive check, which is the
    // hierarchical active state, so an inactive ancestor still excludes its targets. -xlinka
    private void CollectInteractionHits(World? world, float3 origin, float3 direction, float maxDist, float blockingDistance)
    {
        if (world == null) return;

        world.CopyInteractionTargetsTo(_interactionTargetBuffer);
        for (int ti = 0; ti < _interactionTargetBuffer.Count; ti++)
        {
            var target = _interactionTargetBuffer[ti];

            // Registry only holds Components (registration is in ComponentBase), so the slot is the component's
            // own slot - the same slot the old recursion was visiting it on.
            if (target is not Component comp) continue;
            if (!comp.Enabled.Value || comp.IsDestroyed) continue;
            var slot = comp.Slot;
            if (slot == null || !slot.IsActive) continue;

            if (target is ILaserPointerTarget pointerTarget)
            {
                if (!pointerTarget.TryGetLaserPointerHit(this, origin, direction, maxDist, out var pointerHit)) continue;
                if (IsSlotOnThisHierarchy(slot)) continue;
                var pointerClass = ClassifyHit(target, slot);
                if (pointerClass == LaserHitClass.Ignore) continue;
                bool pointerPreferred = pointerClass == LaserHitClass.Prefer;
                if (!pointerPreferred && pointerHit.Distance > blockingDistance) continue;

                _hitBuffer.Add(new TargetHit(target, slot, pointerHit.Distance, pointerHit.Point, pointerPreferred));
                continue;
            }

            float radius = GetHoverRadius(target);
            if (radius <= 0f) continue;

            float3 center = slot.GlobalPosition;
            if (!RaySphereIntersect(origin, direction, center, radius, out float distance)) continue;
            if (distance > maxDist) continue;
            if (IsSlotOnThisHierarchy(slot)) continue;
            var sphereClass = ClassifyHit(target, slot);
            if (sphereClass == LaserHitClass.Ignore) continue;
            bool spherePreferred = sphereClass == LaserHitClass.Prefer;
            if (!spherePreferred && distance > blockingDistance) continue;

            var hitPoint = new float3(
                origin.x + direction.x * distance,
                origin.y + direction.y * distance,
                origin.z + direction.z * distance);
            if (!TryApplyLaserModifiers(slot, direction, hitPoint, out hitPoint)) continue;

            _hitBuffer.Add(new TargetHit(target, slot, distance, hitPoint, spherePreferred));
        }

        // Resolve interaction targets through the SHAPE the laser actually hits, not just a sphere at each target's
        // slot origin. A large object (e.g. an avatar) carries its Grabbable/RayTarget on the ROOT, but you point at
        // its body/mesh - sphere-testing the target at its slot origin misses the body entirely. The physics ray
        // hits the real collision shape (mesh colliders included) and we walk up to the owning interaction target.
        // This is what makes a big avatar grabbable/equippable anywhere on its body. -xlinka
        if (CastRayCached(origin, direction, maxDist, out var shapeHit) &&
            shapeHit.Distance <= blockingDistance && shapeHit.Slot != null)
        {
            var ctarget = FindInteractionTargetInParents(shapeHit.Slot);
            if (ctarget != null)
            {
                // Classified like everything else, but this hit is by definition the FIRST solid thing on
                // the ray: a Prefer here can outrank the nearer sphere and pointer hits, it cannot pull
                // something out from behind a wall, because the cast never saw behind the wall. Anything
                // that needs THAT answers the beam itself, the way the gizmo handles do. -xlinka
                var shapeClass = ClassifyHit(ctarget, shapeHit.Slot);
                if (shapeClass != LaserHitClass.Ignore)
                {
                    _hitBuffer.Add(new TargetHit(ctarget, shapeHit.Slot, shapeHit.Distance, shapeHit.Point,
                        shapeClass == LaserHitClass.Prefer));
                }
            }
        }
    }

    // What this hit is allowed to do. A target can ask to be reachable through the geometry in front of
    // it (gizmo handles do, so they still work with a bare hand), and the equipped tool gets the final
    // say so it can pull its own chrome forward or drop hits it must never act on. A tool can only
    // raise a hit or kill it: an Allow from the tool leaves the target's own request standing.
    //
    // Called only for hits the ray actually crossed, after the cheap distance and hierarchy rejects.
    // Classifying every registered target up front would be a parent walk per target per frame, and
    // almost none of them are anywhere near the beam. -xlinka
    private LaserHitClass ClassifyHit(IInteractionTarget target, Slot slot)
    {
        var result = target is ILaserPreferredTarget preferred && preferred.PreferLaserHit(this)
            ? LaserHitClass.Prefer
            : LaserHitClass.Allow;

        var classifier = _hitClassifier;
        if (classifier == null)
            return result;
        if (classifier is Component component && (component.IsDestroyed || !component.Enabled.Value))
            return result;

        var toolClass = classifier.ClassifyLaserHit(this, slot, target);
        if (toolClass == LaserHitClass.Ignore)
            return LaserHitClass.Ignore;
        return toolClass == LaserHitClass.Prefer ? LaserHitClass.Prefer : result;
    }

    // Walk up from a collider's slot to the first interaction target that owns it (the Grabbable/RayTarget on a
    // big object's root). Stops at a SearchBlock so a contained sub-object isn't resolved past its boundary. -xlinka
    private static IInteractionTarget? FindInteractionTargetInParents(Slot? slot)
    {
        var current = slot;
        while (current != null)
        {
            foreach (var t in current.GetComponentsImplementing<IInteractionTarget>())
                return t;
            if (current.GetComponent<SearchBlock>() != null)
                break;
            current = current.Parent;
        }
        return null;
    }

    private float GetHoverRadius(IInteractionTarget target)
    {
        if (target is RayTarget rt) return MathF.Max(rt.HoverRadius.Value, 0.001f);
        return MathF.Max(DefaultHoverRadius.Value, 0.001f);
    }

    // Clearance past the body when the cast starts beyond the avatar, and the floor the skip never drops
    // below so a camera parked away from the body still ignores its own rig.
    private const float ExternalAimSkipMargin = 0.35f;
    private const float ExternalAimMinSkip = 0.20f;

    // True when this laser has to aim off the desktop CAMERA rather than the avatar head. An open dash owns
    // the pointer outright - the OS cursor is unlocked there, so the mouse rather than the crosshair is the
    // pointer and the platform's free-cursor ray is the aim - and the in-world tools stand down for it
    // anyway, so the whole dash case stays out of this. -xlinka
    private static bool UseExternalCameraAim(InputInterface? input)
    {
        return input != null && input.DesktopExternalCameraAim && !input.IsDashboardOpen;
    }

    // How far along the camera ray the cast starts. Third-person puts the whole avatar between the lens and
    // whatever you are pointing at, and the hand holding the beam is the closest part of it, so start just
    // past the hand's DEPTH along the ray - projecting instead of measuring straight-line distance keeps the
    // skip honest when the body sits off to one side, and a body behind the camera (free-cam) projects
    // negative and falls back to the minimum. The own-user-root exclusion still runs; this covers everything
    // else riding on you that the exclusion never hears about. A modal target is exempt: the radial menu
    // opens between the camera and the body, so skipping past it would start the cast beyond the one thing
    // it is allowed to hit. -xlinka
    private float ResolveExternalAimSkip(float3 cameraOrigin, float3 cameraDirection)
    {
        if (_exclusiveRoot != null && !_exclusiveRoot.IsDestroyed)
            return 0f;

        float3 hand = Slot?.GlobalPosition ?? cameraOrigin;
        float depth = float3.Dot(hand - cameraOrigin, cameraDirection);
        return MathF.Max(depth + ExternalAimSkipMargin, ExternalAimMinSkip);
    }

    // Re-aim the tool-facing ray so it leaves the HAND and lands on a world point, then run it through the
    // aim smoother. Under an external camera the cast belongs to the camera but the beam, the held object and
    // every UI resolution downstream belong to the hand, and this is the seam between them. -xlinka
    private void AnchorRayAtHand(float3 targetPoint, float delta, bool advance)
    {
        if (Slot == null)
            return;

        float3 handPosition = Slot.GlobalPosition;
        float3 toTarget = targetPoint - handPosition;
        float length = toTarget.Length;
        float3 handDirection = length > 0.0001f ? toTarget / length : _rayDirection;
        float3 handOrigin = handPosition + handDirection * MathF.Max(BeamStartOffset.Value, 0f);

        ApplyRaySmoothing(ref handOrigin, ref handDirection, delta, advance);
        _rayOrigin = handOrigin;
        _rayDirection = handDirection;
    }

    private void ResolveRayPose(out float3 origin, out float3 direction)
    {
        float startOffset = MathF.Max(BeamStartOffset.Value, 0f);
        var input = Engine.Current?.InputInterface;

        // desktop: aim from the head/camera so the ray passes through the reticle. - xlinka
        if (input != null && !input.IsVRActive)
        {
            // Free-cursor mode (dash open): the platform-supplied ray through the
            // unlocked OS cursor IS the laser ray, so the in-world cursor follows
            // the mouse over the projected dash.
            if (input.IsDashboardOpen && input.DesktopCursorRayValid)
            {
                direction = input.DesktopCursorRayDirection;
                float cursorLen = direction.Length;
                direction = cursorLen > 0.0001f ? direction / cursorLen : float3.Backward;
                origin = input.DesktopCursorRayOrigin + direction * startOffset;
                return;
            }

            // Third-person / free-cam: the view flies on its own and the head is parked where mouse look
            // last left it, so head-forward is a stale direction. Aim from the CAMERA through the screen
            // centre - the mouse is captured in these modes, so the crosshair IS the pointer - and start the
            // cast past the body so the avatar standing between the lens and the target never eats the ray.
            // -xlinka
            if (UseExternalCameraAim(input) &&
                input.TryGetDesktopViewRay(float2.Zero, out float3 viewOrigin, out float3 viewDirection))
            {
                if (_desktopAimOffset != float2.Zero)
                {
                    floatQ aimRotation = input.DesktopCameraRotation
                        * floatQ.AxisAngle(float3.Up, _desktopAimOffset.x)
                        * floatQ.AxisAngle(new float3(1f, 0f, 0f), _desktopAimOffset.y);
                    float3 deflected = aimRotation * float3.Backward;
                    if (deflected.Length > 0.0001f)
                    {
                        viewDirection = deflected.Normalized;
                    }
                }

                direction = viewDirection;
                origin = viewOrigin + direction * (ResolveExternalAimSkip(viewOrigin, direction) + startOffset);
                return;
            }

            var headSlot = FindHeadSlot();
            floatQ headRot = headSlot != null ? headSlot.GlobalRotation : floatQ.Identity;
            float3 headPos = headSlot != null ? headSlot.GlobalPosition : Slot.GlobalPosition;

            // Mouse-steered deflection while the camera is frozen (context menu open).
            if (_desktopAimOffset != float2.Zero)
            {
                headRot = headRot
                    * floatQ.AxisAngle(float3.Up, _desktopAimOffset.x)
                    * floatQ.AxisAngle(new float3(1f, 0f, 0f), _desktopAimOffset.y);
            }

            direction = headRot * new float3(0f, 0f, -1f);
            float dLen = direction.Length;
            if (dLen > 0.0001f) direction /= dLen;
            else direction = float3.Backward;

            origin = new float3(
                headPos.x + direction.x * startOffset,
                headPos.y + direction.y * startOffset,
                headPos.z + direction.z * startOffset);
            return;
        }

        // VR: controller slot forward, with optional fingertip refinement. - xlinka
        direction = -Slot.Forward;
        float dirLen = direction.Length;
        if (dirLen < 0.0001f) direction = float3.Backward;
        else direction /= dirLen;

        origin = new float3(
            Slot.GlobalPosition.x + direction.x * startOffset,
            Slot.GlobalPosition.y + direction.y * startOffset,
            Slot.GlobalPosition.z + direction.z * startOffset);

        if (input == null) return;

        BodyNode tipNode = ControllerSide.Value == Chirality.Left
            ? BodyNode.LeftIndexFinger_Tip
            : BodyNode.RightIndexFinger_Tip;
        BodyNode distalNode = ControllerSide.Value == Chirality.Left
            ? BodyNode.LeftIndexFinger_Distal
            : BodyNode.RightIndexFinger_Distal;

        var tip = input.GetBodyNode(tipNode);
        if (tip == null || !tip.IsTracking) return;

        float3 resolvedOrigin = tip.Position;
        var distal = input.GetBodyNode(distalNode);
        if (distal != null && distal.IsTracking)
        {
            float3 fingertipDirection = new float3(
                tip.Position.x - distal.Position.x,
                tip.Position.y - distal.Position.y,
                tip.Position.z - distal.Position.z);
            float ftLen = fingertipDirection.Length;
            if (ftLen > 0.0001f) direction = fingertipDirection / ftLen;
        }

        origin = new float3(
            resolvedOrigin.x + direction.x * startOffset,
            resolvedOrigin.y + direction.y * startOffset,
            resolvedOrigin.z + direction.z * startOffset);
    }

    // The physics hit's slot and point survive the cast so a sampler can read plain scenery: the
    // interaction hit list only carries interaction targets, and a ground plate or a bare prop is
    // exactly the thing an eyedropper gets pointed at. -xlinka
    private Slot? _colliderHitSlot;
    private float3 _colliderHitPoint;

    public Slot? CurrentColliderHitSlot => _colliderHitSlot;
    public float3 CurrentColliderHitPoint => _colliderHitPoint;

    private bool TryFindNearestColliderHitDistance(float3 origin, float3 direction, float maxDistance, out float hitDistance)
    {
        hitDistance = maxDistance;
        if (!CastRayCached(origin, direction, maxDistance, out var hit))
            return false;

        hitDistance = hit.Distance;
        _colliderHitSlot = hit.Slot;
        _colliderHitPoint = hit.Point;
        return true;
    }

    // Per-cast memo for the world ray. Reset at the top of CastAndUpdate; both consumers inside one frame's
    // cast share the result. Keyed on the ray so a caller passing different arguments re-casts. -xlinka
    private bool _castHitValid;
    private bool _castHitFound;
    private PhysicsRaycastHit _castHit;
    private float3 _castHitOrigin;
    private float3 _castHitDirection;
    private float _castHitMaxDistance;

    private bool CastRayCached(float3 origin, float3 direction, float maxDistance, out PhysicsRaycastHit hit)
    {
        if (_castHitValid &&
            _castHitOrigin == origin &&
            _castHitDirection == direction &&
            _castHitMaxDistance == maxDistance)
        {
            hit = _castHit;
            return _castHitFound;
        }

        _castHitFound = TryPhysicsRaycast(origin, direction, maxDistance, out _castHit);
        _castHitOrigin = origin;
        _castHitDirection = direction;
        _castHitMaxDistance = maxDistance;
        _castHitValid = true;
        hit = _castHit;
        return _castHitFound;
    }

    // Shape-accurate ray cast against the world's real physics space (delegated to the platform). Replaces the
    // old hand-rolled ray-vs-AABB over the collider registry, which treated every shape as its bounding box and
    // couldn't hit mesh colliders at all. Skips the laser's own avatar (exclude list, like AvatarIK's foot probe),
    // then re-applies the engine-only filters the platform doesn't know - IgnoreRaycasts colliders and own-hierarchy
    // slots - by nudging just past a filtered hit and re-casting, bounded so a wall of ignored colliders can't spin.
    // -xlinka
    private const int RaycastSkipLimit = 8;

    private bool TryPhysicsRaycast(float3 origin, float3 direction, float maxDistance, out PhysicsRaycastHit hit)
    {
        hit = default;
        var physics = World?.Physics;
        if (physics == null)
            return false;

        BuildRayExclude();

        float3 castOrigin = origin;
        float remaining = maxDistance;
        float traveled = 0f;

        for (int skip = 0; skip < RaycastSkipLimit; skip++)
        {
            if (remaining <= 0.0001f)
                return false;
            // hitTriggers MUST be true: grabbables, image colliders, and a worn avatar's body colliders are all
            // sensor (Trigger/Area3D) colliders on this platform - query-only shapes meant to be raycast-hittable
            // without acting as walls. With triggers off the ray would pass straight through every grabbable and
            // avatar body, so equip/grab targeting would never resolve a hit on them. Solid bodies still block
            // (CollideWithBodies stays on), so walls/floor occlude as before. -xlinka
            if (!physics.Raycast(in castOrigin, in direction, remaining, _rayExclude, out var candidate, hitTriggers: true))
                return false;

            float candidateDistance = traveled + candidate.Distance;
            var slot = candidate.Slot;
            if (slot != null && IsPhysicsHitFiltered(slot))
            {
                // Step just past this hit and re-cast so the next surface behind it can register. -xlinka
                float step = MathF.Max(candidate.Distance, 0f) + 0.001f;
                castOrigin = new float3(
                    castOrigin.x + direction.x * step,
                    castOrigin.y + direction.y * step,
                    castOrigin.z + direction.z * step);
                traveled += step;
                remaining -= step;
                continue;
            }

            hit = candidate;
            hit.Distance = candidateDistance;
            return true;
        }

        return false;
    }

    // Rebuild the exclude list: the laser's own avatar/user hierarchy (so the ray doesn't catch the user's own
    // body colliders) plus any explicit ignore root. The platform skips these and all their descendants. -xlinka
    private void BuildRayExclude()
    {
        _rayExclude.Clear();
        var ownRoot = Slot?.ActiveUserRoot?.Slot;
        if (ownRoot != null)
            _rayExclude.Add(ownRoot);
        else if (Slot != null)
            _rayExclude.Add(Slot);
        if (_ignoreRoot != null && !ReferenceEquals(_ignoreRoot, ownRoot))
            _rayExclude.Add(_ignoreRoot);
    }

    // Engine-only filters the platform raycast doesn't apply: a collider opted out of raycasts, a non-collision
    // collider, or a slot on this laser's own hierarchy (belt-and-suspenders with the exclude list). -xlinka
    private bool IsPhysicsHitFiltered(Slot slot)
    {
        if (IsSlotOnThisHierarchy(slot))
            return true;
        var collider = slot.GetComponent<Collider>();
        if (collider != null && (collider.IgnoreRaycasts.Value || collider.Type.Value == ColliderType.NoCollision))
            return true;
        return false;
    }

    private void PositionBeam(float3 origin, float3 direction, float length, float3 endPoint, float delta)
    {
        if (_beamSlot == null || _beamMesh == null) return;

        _beamSlot.GlobalPosition = origin;
        _beamSlot.GlobalRotation = floatQ.Identity;
        _beamSlot.GlobalScale = float3.One;

        float3 localDirect = _beamSlot.GlobalDirectionToLocal(direction);
        if (localDirect.Length > 0.0001f)
        {
            localDirect = localDirect.Normalized * length;
        }
        else
        {
            localDirect = float3.Backward * length;
        }

        float3 localActual = _beamSlot.GlobalPointToLocal(endPoint);
        // The exponential smoothing here damps real hand jitter in VR. On DESKTOP the mouse is already pixel
        // precise, so smoothing it just makes the cursor trail behind the pointer - the "sluggish / floaty"
        // feel. Snap 1:1 on desktop; keep the smoothing only for VR. -xlinka
        var rayInput = Engine.Current?.InputInterface;
        bool isDesktopPointer = rayInput != null && !rayInput.IsVRActive;
        if (!_hasVisualActualPoint || isDesktopPointer)
        {
            _visualActualPoint = localActual;
            _hasVisualActualPoint = true;
        }
        else
        {
            float t = 1f - MathF.Exp(-MathF.Max(VisualSmoothing.Value, 0f) * MathF.Max(delta, 0f));
            _visualActualPoint = float3.Lerp(_visualActualPoint, localActual, System.Math.Clamp(t, 0f, 1f));
        }

        _lastDirectHitDistance = length;
        SetIfChanged(_beamMesh.StartPoint, float3.Zero);
        SetIfChanged(_beamMesh.DirectTargetPoint, localDirect);
        SetIfChanged(_beamMesh.ActualTargetPoint, _visualActualPoint);

        if (_pointSlot != null)
        {
            SetIfChanged(_pointSlot.LocalPosition, _visualActualPoint);
        }

        if (_directCursorSlot != null)
        {
            SetIfChanged(_directCursorSlot.LocalPosition, localDirect);
        }

        if (_directLineMesh != null)
        {
            SetIfChanged(_directLineMesh.Radius, BeamRadius.Value * 0.75f);
            SetIfChanged(_directLineMesh.PointA, localDirect);
            SetIfChanged(_directLineMesh.PointB, _visualActualPoint);
        }
    }

    internal Slot? FindHeadSlot()
    {
        // Walk parent ActiveUserRoot directly - skips the per-ancestor GetComponent scan. - xlinka
        var headSlot = Slot?.Parent?.ActiveUserRoot?.HeadSlot;
        if (headSlot != null) return headSlot;

        // Fallback: legacy ancestor walk for nested cases without registered UserRoot.
        var current = Slot?.Parent;
        while (current != null)
        {
            var userRoot = current.GetComponent<UserRoot>();
            if (userRoot != null)
            {
                var hs = userRoot.HeadSlot;
                if (hs != null) return hs;
                break;
            }
            current = current.Parent;
        }

        current = Slot!.Parent;
        while (current != null)
        {
            if (current.Name.Value == "Body Nodes")
            {
                foreach (var child in current.Children)
                {
                    if (child.Name.Value == "Head") return child;
                }
            }
            current = current.Parent;
        }
        return null;
    }

    // Scroll input for whatever the laser is pointing at. The action carries its own per-axis
    // deadzone and, on desktop, only the right hand has a wheel bound to it - there is one mouse.
    private float2 ReadPointerAxis()
    {
        return Engine.Current?.InputInterface?.Actions?.Interaction(ControllerSide.Value).Pointer.Value
            ?? float2.Zero;
    }

    // VR only, deliberately. Outside VR the secondary control belongs to the equipped tool, and
    // letting the laser fire a target's secondary on the same press would double up on every click.
    private bool ReadSecondaryPressed()
    {
        var input = Engine.Current?.InputInterface;
        if (input == null || !input.IsVRActive) return false;
        return input.Actions?.Interaction(ControllerSide.Value).Secondary.Held == true;
    }

    private static bool RaySphereIntersect(float3 origin, float3 direction, float3 center, float radius, out float t)
    {
        float3 oc = new(origin.x - center.x, origin.y - center.y, origin.z - center.z);
        float b = float3.Dot(oc, direction);
        float c = float3.Dot(oc, oc) - radius * radius;
        float disc = b * b - c;

        if (disc < 0f) { t = 0f; return false; }

        float sqrtDisc = MathF.Sqrt(disc);
        float nearT = -b - sqrtDisc;
        float farT = -b + sqrtDisc;

        if (nearT > 0f) { t = nearT; return true; }
        if (farT > 0f) { t = farT; return true; }

        t = 0f;
        return false;
    }

    private bool IsSlotOnThisHierarchy(Slot candidate)
    {
        var current = candidate;
        while (current != null)
        {
            if (ReferenceEquals(current, Slot)) return true;
            if (_ignoreRoot != null && ReferenceEquals(current, _ignoreRoot)) return true;
            current = current.Parent;
        }
        return false;
    }

    private static floatQ AlignYToDirection(float3 direction)
    {
        float len = direction.Length;
        if (len < 0.0001f) return floatQ.Identity;

        float3 dir = new(direction.x / len, direction.y / len, direction.z / len);
        float3 axis = float3.Cross(float3.Up, dir);
        float axisLen = axis.Length;

        if (axisLen < 0.001f)
        {
            return float3.Dot(float3.Up, dir) > 0f
                ? floatQ.Identity
                : floatQ.AxisAngle(float3.Forward, MathF.PI);
        }

        float3 normAxis = new(axis.x / axisLen, axis.y / axisLen, axis.z / axisLen);
        float angle = MathF.Acos(System.Math.Clamp(float3.Dot(float3.Up, dir), -1f, 1f));
        return floatQ.AxisAngle(normAxis, angle);
    }
}
