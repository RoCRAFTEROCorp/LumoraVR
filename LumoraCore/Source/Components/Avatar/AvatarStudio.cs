// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Avatar.IK;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraMeshes = Lumora.Core.Components.Meshes;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

[ComponentCategory("Users/Avatar")]
public sealed class AvatarStudio : Component
{
    public readonly Sync<bool> CalibrateFeet = new();

    public readonly Sync<bool> CalibratePelvis = new();

    public readonly Sync<bool> SetupEyes = new();

    public readonly Sync<bool> ShowDirections = new();

    // Reveal the per-hand tool anchors. Off by default, matching the source platform's own creator: the
    // anchors are always authored and always baked, the box only decides whether you can see and drag
    // them, so someone setting up a plain avatar is not handed four more orbs to worry about. -xlinka
    public readonly Sync<bool> ShowToolAnchors = new();

    // Build a QUADRUPED rig instead of a biped one.
    //
    // Detection reads the bind pose, and a fox authored standing upright scores zero on the spine test
    // no matter how obviously it is an animal. That test cannot simply be relaxed either: an anthro
    // biped with a tail and digitigrade legs hits every other quadruped signal there is. So the answer
    // is to let the person who can SEE the model say, rather than guessing harder. -xlinka
    public readonly Sync<bool> QuadrupedMode = new();

    // Front and rear body anchors, and one marker per paw. A four-legged spine is a bridge with a
    // support at each end rather than a stack on one, which is why there are two body markers here and
    // one on the biped set.
    private readonly SyncRef<Slot> _chestProxy = new();
    private readonly SyncRef<Slot> _frontLeftPawProxy = new();
    private readonly SyncRef<Slot> _frontRightPawProxy = new();
    private readonly SyncRef<Slot> _rearLeftPawProxy = new();
    private readonly SyncRef<Slot> _rearRightPawProxy = new();

    // How far each marker reaches to find the model's colliders.
    private const float HeadDetectionRadius = 0.2f;
    private const float LimbDetectionRadius = 0.15f;

    // Grab core radius and the world->canvas scale for the control panel (1 canvas unit ~= 1.1mm).
    // The core is a CENTRE PIP, not the handle: the laser grabs by its own fixed hover radius at the
    // slot centre, so the ball's size buys nothing. At 5cm it was wider than the hand and the foot it
    // sits inside, and every marker looked like the same lump with bits poking out. -xlinka
    private const float CoreRadius = 0.014f;
    private const float CanvasScale = 0.0011f;

    // Smaller than a body marker's core: an anchor sits INSIDE the hand marker it belongs to, and at
    // the body size the two pips merged into one lump you could not tell apart or grab separately.
    private const float AnchorCoreRadius = 0.009f;

    private static readonly colorHDR HeadFill = new(1f, 1f, 1f, 0.12f);
    private static readonly colorHDR LeftFill = new(0.2f, 0.7f, 1f, 0.15f);
    private static readonly colorHDR RightFill = new(1f, 0.3f, 0.3f, 0.15f);
    private static readonly colorHDR PelvisFill = new(0.7f, 0.4f, 1f, 0.12f);

    // The panel's live readout and the buttons it gates. Aligning to a rig that is not under the
    // markers used to be a silent no-op with a line in the log; now the buttons say so themselves.
    private Text? _statusText;
    private UITheme? _panelTheme;
    private readonly List<(Button button, bool needsHead)> _gated = new();
    private float _statusPoll;
    private bool _lastReady;
    private bool _lastRigFound;
    private bool _stateKnown;

    private readonly SyncRef<Slot> _headProxy = new();
    private readonly SyncRef<Slot> _leftHandProxy = new();
    private readonly SyncRef<Slot> _rightHandProxy = new();
    private readonly SyncRef<Slot> _leftFootProxy = new();
    private readonly SyncRef<Slot> _rightFootProxy = new();
    private readonly SyncRef<Slot> _pelvisProxy = new();

    // Tool anchors, two per hand. These are CHILDREN of their hand marker, not siblings, so dragging or
    // aligning the hand carries its anchors with it - which is the only way the offsets stay meaningful,
    // since they are offsets FROM the grip. -xlinka
    private readonly SyncRef<Slot> _leftToolAnchorProxy = new();
    private readonly SyncRef<Slot> _rightToolAnchorProxy = new();
    private readonly SyncRef<Slot> _leftGrabAnchorProxy = new();
    private readonly SyncRef<Slot> _rightGrabAnchorProxy = new();

    private bool _built;

    public override void OnInit()
    {
        base.OnInit();
        CalibrateFeet.Value = false;
        CalibratePelvis.Value = false;
        SetupEyes.Value = true;
    }

    public override void OnStart()
    {
        base.OnStart();

        ShowDirections.Value = true; // forward arrows on by default so orientation is visible while building

        AnchorToUser();
        BuildMarkers();
        BuildControlPanel();

        CalibrateFeet.OnChanged += _ => RefreshOptionalMarkers();
        CalibratePelvis.OnChanged += _ => RefreshOptionalMarkers();
        ShowDirections.OnChanged += _ => RefreshDirectionArrows();
        ShowToolAnchors.OnChanged += _ => RefreshOptionalMarkers();

        // Flipping the mode swaps the whole marker set: six for a biped, seven for a quadruped, and
        // different bodies. Rebuilt rather than hidden, because a stale RightHand marker sitting inside
        // a fox is worse than no marker at all. Anything the user had dragged is discarded with it,
        // which is why this is a deliberate toggle and not something detection flips underneath them.
        QuadrupedMode.OnChanged += _ => RebuildMarkersForMode();

        // No snapping to whatever model happens to be nearby. With more than one avatar in the world
        // that is a coin toss, and it moves markers the user never asked to move. The figure stands
        // where it spawns; you drag it onto the model you mean, or press Align once it is there. -xlinka
        RefreshOptionalMarkers();
        RefreshDirectionArrows();
    }

    // Stand the figure on the floor in front of the local user, facing them, so the markers overlay an
    // avatar imported in the same spot. Local marker offsets are then rotated into place by the slot.
    private void AnchorToUser()
    {
        var user = World?.LocalUser?.Root;
        if (user == null)
            return;

        // View direction is the head's LOCAL -Z (Godot camera convention) = float3.Backward; float3.Forward
        // (+Z) points behind the user, which is why the creator used to spawn behind them. - xlinka
        var forward = user.HeadRotation * float3.Backward;
        forward.y = 0f;
        forward = forward.LengthSquared > 1e-5f ? forward.Normalized : float3.Backward;

        Slot.GlobalPosition = user.FeetPosition + forward * 1.2f;
        // Face the user: the figure's forward is its local -Z (float3.Backward, our view/forward convention -
        // same axis the camera, HeadFacingDirection and the green arrows use), so point THAT back at the user
        // (-forward = figure->user). Using float3.Forward (+Z) here pointed the figure's real front the same way
        // the user was looking, so you spawned staring at the avatar's back and the arrows pointed away. With
        // -Z toward the user the figure faces you and left/right mirror correctly (their right = your left).
        // FromToRotation, not floatQ.LookRotation - that returns the inverse. - xlinka
        //
        // The Up fallback is not optional. -forward is anti-parallel to Backward whenever the user is
        // looking down -Z, which is where a lot of worlds face you on spawn, and a 180 has no defined
        // axis: the 2-arg version picks Cross(Up, Backward), a HORIZONTAL axis, and spawns the whole
        // tool UPSIDE DOWN. Yaw about Up is the only 180 that means anything here. -xlinka
        Slot.GlobalRotation = FabrikSolver.FromToRotation(float3.Backward, -forward, float3.Up);
    }

    // MARKERS

    private void BuildMarkers()
    {
        if (QuadrupedMode.Value)
        {
            BuildQuadrupedMarkers();
            return;
        }

        if (_built)
            return;
        _built = true;

        // Standing layout for an average humanoid (head ~1.8m); TryAutoAlignToRig then snaps every marker onto
        // the imported model's bones so short/tall rigs land right. The arrow direction is per-part: head/feet/
        // pelvis show their FORWARD (local -Z, the facing axis); hands show their OUTWARD reach (away from the
        // body) since a hand's useful axis runs down the arm, not where the palm faces.
        _headProxy.Target = AddMarker("Headset", new float3(0f, 1.8f, 0f), HeadDetectionRadius, HeadFill, float3.Backward);
        AddHeadsetShape(_headProxy.Target);
        AddEyeBalls(_headProxy.Target);
        _leftHandProxy.Target = AddMarker("LeftHand", new float3(-0.35f, 1.0f, 0f), LimbDetectionRadius, LeftFill, float3.Backward);
        AddHandShape(_leftHandProxy.Target, LeftFill, left: true);
        AddSideLabel(_leftHandProxy.Target, "Left", LeftFill);
        _rightHandProxy.Target = AddMarker("RightHand", new float3(0.35f, 1.0f, 0f), LimbDetectionRadius, RightFill, float3.Backward);
        AddHandShape(_rightHandProxy.Target, RightFill, left: false);
        AddSideLabel(_rightHandProxy.Target, "Right", RightFill);
        _pelvisProxy.Target = AddMarker("Pelvis", new float3(0f, 1.0f, 0f), LimbDetectionRadius, PelvisFill, float3.Backward);
        AddPelvisShape(_pelvisProxy.Target, PelvisFill);
        _leftFootProxy.Target = AddMarker("LeftFoot", new float3(-0.12f, 0.1f, 0f), LimbDetectionRadius, LeftFill, float3.Backward);
        AddFootShape(_leftFootProxy.Target, LeftFill);
        AddSideLabel(_leftFootProxy.Target, "Left", LeftFill);
        _rightFootProxy.Target = AddMarker("RightFoot", new float3(0.12f, 0.1f, 0f), LimbDetectionRadius, RightFill, float3.Backward);
        AddFootShape(_rightFootProxy.Target, RightFill);
        AddSideLabel(_rightFootProxy.Target, "Right", RightFill);

        BuildToolAnchorMarkers();
    }

    // Where the hand tool rig lands once this avatar is worn.
    //
    // The source platform authors two anchor points per hand from its creator - the tool point and the
    // grab-area point - and drops a third for a toolshelf we have no counterpart for. Both of ours have a
    // real destination: HandTool builds a "Tool Holder" slot and a "Grabber" slot under itself, which is
    // exactly the pair the other engine re-parents onto these anchors on equip.
    //
    // Biped only. The quadruped set has no hands to hang them off, and the source platform has no
    // four-legged mode at all, so there is nothing to port for one. -xlinka
    private void BuildToolAnchorMarkers()
    {
        _leftToolAnchorProxy.Target = AddToolAnchorMarker(_leftHandProxy.Target, "LeftHandToolAnchor",
            AvatarCalibration.ToolAnchorGripOffset, LeftFill, tool: true);
        _rightToolAnchorProxy.Target = AddToolAnchorMarker(_rightHandProxy.Target, "RightHandToolAnchor",
            AvatarCalibration.ToolAnchorGripOffset, RightFill, tool: true);
        _leftGrabAnchorProxy.Target = AddToolAnchorMarker(_leftHandProxy.Target, "LeftHandGrabAnchor",
            AvatarCalibration.GrabAnchorGripOffset, LeftFill, tool: false);
        _rightGrabAnchorProxy.Target = AddToolAnchorMarker(_rightHandProxy.Target, "RightHandGrabAnchor",
            AvatarCalibration.GrabAnchorGripOffset, RightFill, tool: false);
    }

    // Laid out for an animal standing on all fours facing +Z: head out front and low, the two body
    // anchors along the spine, four paws on the ground. Roughly fox proportions, which is only a
    // starting pose - every marker is grabbable and the whole point is that you drag them onto the
    // model you actually have. -xlinka
    private void BuildQuadrupedMarkers()
    {
        _headProxy.Target = AddMarker("Head", new float3(0f, 0.62f, 0.42f), HeadDetectionRadius, HeadFill, float3.Backward);
        AddHeadsetShape(_headProxy.Target);
        AddEyeBalls(_headProxy.Target);

        // Front anchor: the shoulders / withers, where the forelegs hang from.
        _chestProxy.Target = AddMarker("Chest", new float3(0f, 0.55f, 0.18f), LimbDetectionRadius, PelvisFill, float3.Backward);
        AddPelvisShape(_chestProxy.Target, PelvisFill);
        AddSideLabel(_chestProxy.Target, "Front", PelvisFill);

        // Rear anchor: the hips proper.
        _pelvisProxy.Target = AddMarker("Pelvis", new float3(0f, 0.55f, -0.22f), LimbDetectionRadius, PelvisFill, float3.Backward);
        AddPelvisShape(_pelvisProxy.Target, PelvisFill);
        AddSideLabel(_pelvisProxy.Target, "Rear", PelvisFill);

        _frontLeftPawProxy.Target = AddMarker("FrontLeftPaw", new float3(-0.11f, 0.06f, 0.20f), LimbDetectionRadius, LeftFill, float3.Backward);
        AddFootShape(_frontLeftPawProxy.Target, LeftFill);
        AddSideLabel(_frontLeftPawProxy.Target, "FL", LeftFill);

        _frontRightPawProxy.Target = AddMarker("FrontRightPaw", new float3(0.11f, 0.06f, 0.20f), LimbDetectionRadius, RightFill, float3.Backward);
        AddFootShape(_frontRightPawProxy.Target, RightFill);
        AddSideLabel(_frontRightPawProxy.Target, "FR", RightFill);

        _rearLeftPawProxy.Target = AddMarker("RearLeftPaw", new float3(-0.11f, 0.06f, -0.24f), LimbDetectionRadius, LeftFill, float3.Backward);
        AddFootShape(_rearLeftPawProxy.Target, LeftFill);
        AddSideLabel(_rearLeftPawProxy.Target, "RL", LeftFill);

        _rearRightPawProxy.Target = AddMarker("RearRightPaw", new float3(0.11f, 0.06f, -0.24f), LimbDetectionRadius, RightFill, float3.Backward);
        AddFootShape(_rearRightPawProxy.Target, RightFill);
        AddSideLabel(_rearRightPawProxy.Target, "RR", RightFill);
    }

    private void RebuildMarkersForMode()
    {
        foreach (var proxy in new[]
        {
            // Anchor proxies first: they are children of the hand markers, so destroying a hand takes its
            // anchors with it and the refs left behind would point at removed slots.
            _leftToolAnchorProxy, _rightToolAnchorProxy, _leftGrabAnchorProxy, _rightGrabAnchorProxy,
            _headProxy, _leftHandProxy, _rightHandProxy, _leftFootProxy, _rightFootProxy, _pelvisProxy,
            _chestProxy, _frontLeftPawProxy, _frontRightPawProxy, _rearLeftPawProxy, _rearRightPawProxy,
        })
        {
            var slot = proxy.Target;
            if (slot != null && !slot.IsDestroyed)
                slot.Destroy();
            proxy.Target = null!;
        }

        // The biped set latches _built so OnStart cannot double-build it, and only the biped path latches
        // (the quadruped path returns before the check). Nothing cleared it here, so biped -> quadruped
        // -> biped tore the biped markers down, took the quadruped path, then hit the stale latch on the
        // way back and built NOTHING: an empty studio with a live panel. The markers are gone as of the
        // loop above, so the latch is stale by definition at this point. -xlinka
        _built = false;

        BuildMarkers();
        RefreshOptionalMarkers();
        RefreshDirectionArrows();
        _stateKnown = false;
    }

    private Slot AddMarker(string name, float3 localPos, float detectionRadius, colorHDR fill, float3 arrowDir)
    {
        var slot = Slot.AddSlot(name);
        slot.LocalPosition.Value = localPos;

        // Grab core: a small solid ball that IS the grab handle. NO collider - the laser grabs via its
        // fixed hover radius at the slot center, and a collider bigger than that radius would block the
        // ray before the grab sphere and make the marker ungrabbable (InteractionLaser hover/block model).
        var core = slot.AttachComponent<LumoraMeshes.SphereMesh>();
        core.Radius.Value = CoreRadius;
        core.Segments.Value = 16;
        core.Rings.Value = 12;
        var coreRenderer = slot.AttachComponent<MeshRenderer>();
        coreRenderer.Mesh.Target = core;
        var coreMaterial = slot.AttachComponent<UnlitMaterial>();
        coreMaterial.Color = new colorHDR(fill.r, fill.g, fill.b, 1f);
        coreRenderer.Material.Target = coreMaterial;

        var grab = slot.AttachComponent<Grabbable>();
        grab.FollowRotation.Value = true;
        grab.Scalable.Value = true;
        grab.GrabPriority.Value = 20;        // beat the imported model's per-bone pose grabs (priority 5)
        grab.InteractionPriority.Value = 20;

        // Detection halo: a big translucent sphere showing how far Create reaches. NO collider (so it
        // never blocks the grab ray) - purely a cue. Create overlaps at this marker's center.
        var halo = slot.AddSlot("Detection");
        var haloMesh = halo.AttachComponent<LumoraMeshes.SphereMesh>();
        haloMesh.Radius.Value = detectionRadius;
        haloMesh.Segments.Value = 20;
        haloMesh.Rings.Value = 14;
        var haloRenderer = halo.AttachComponent<MeshRenderer>();
        haloRenderer.Mesh.Target = haloMesh;
        var haloMaterial = halo.AttachComponent<UnlitMaterial>();
        haloMaterial.Color = fill;
        haloMaterial.BlendMode.Value = BlendMode.Transparent;
        haloRenderer.Material.Target = haloMaterial;

        BuildAxisGizmo(slot, arrowDir);
        return slot;
    }

    // One draggable tool anchor, parented under its hand marker.
    //
    // Deliberately NOT built through AddMarker: that one hangs a detection halo on the marker showing how
    // far Create reaches when it hunts for bones, and an anchor is not searched for anything - Create bakes
    // it wherever it sits. A halo here would promise a search that never happens. It also skips the green
    // direction arrow, which at 22cm is longer than the whole hand and would bury the thing it labels; the
    // tool anchor's cone already shows which way it points.
    private Slot AddToolAnchorMarker(Slot hand, string name, float3 localPos, colorHDR fill, bool tool)
    {
        if (hand == null || hand.IsDestroyed)
            return null!;

        var slot = hand.AddSlot(name);
        slot.LocalPosition.Value = localPos;

        var core = slot.AttachComponent<LumoraMeshes.SphereMesh>();
        core.Radius.Value = AnchorCoreRadius;
        core.Segments.Value = 12;
        core.Rings.Value = 10;
        var coreRenderer = slot.AttachComponent<MeshRenderer>();
        coreRenderer.Mesh.Target = core;
        coreRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
        var coreMaterial = slot.AttachComponent<UnlitMaterial>();
        coreMaterial.Color = new colorHDR(fill.r, fill.g, fill.b, 1f);
        coreRenderer.Material.Target = coreMaterial;

        var grab = slot.AttachComponent<Grabbable>();
        grab.FollowRotation.Value = true;
        // Not scalable: an anchor is a point, and scaling one only changes how big its cue draws while
        // silently doing nothing to where the tool lands. The hand marker it lives under still scales.
        grab.Scalable.Value = false;
        // Above the hand marker's 20. These sit INSIDE that marker's reach, so on a tie the laser would
        // take the hand and the anchor would be unreachable without dragging the hand away first. -xlinka
        grab.GrabPriority.Value = 30;
        grab.InteractionPriority.Value = 30;

        if (tool)
            AddToolAnchorShape(slot, fill);
        else
            AddGrabAnchorShape(slot, fill);

        return slot;
    }

    // The tool point: a stubby cone lying along the anchor's forward, so a held tool's direction is
    // readable at a glance. Same proportions the source platform draws for it.
    private static void AddToolAnchorShape(Slot marker, in colorHDR fill)
    {
        var model = ShapePart(marker, "Model", fill, 0.85f);
        model.LocalPosition.Value = float3.Backward * 0.05f;
        model.LocalRotation.Value = FabrikSolver.FromToRotation(float3.Up, float3.Backward);
        var cone = model.AttachComponent<LumoraMeshes.ConeMesh>();
        cone.RadiusBase.Value = 0.015f;
        cone.RadiusTop.Value = 0.0025f;
        cone.Height.Value = 0.05f;
        Draw(model, cone);
    }

    // The grab point: the grab sphere itself, at the source platform's 7cm radius, drawn faint. Solid it
    // would swallow the hand shape and both anchor pips inside it. Its size is a cue only - the grab
    // radius the engine actually uses is the Grabber's, not this. -xlinka
    private static void AddGrabAnchorShape(Slot marker, in colorHDR fill)
    {
        var model = ShapePart(marker, "Model", fill, 0.16f);
        var sphere = model.AttachComponent<LumoraMeshes.SphereMesh>();
        sphere.Radius.Value = 0.07f;
        sphere.Segments.Value = 18;
        sphere.Rings.Value = 12;
        Draw(model, sphere);
    }

    // One GREEN direction arrow per marker, pointing along the part's chosen local axis (head/feet/pelvis =
    // forward/-Z; hands = outward along the arm). It shows which way that orb is oriented while aligning: if a
    // marker's arrow points somewhere the part clearly shouldn't, that bone is authored off and needs the Align
    // buttons. Toggled by ShowDirections. - xlinka
    private static void BuildAxisGizmo(Slot marker, float3 dir)
    {
        var axes = marker.AddSlot("Axes");

        var green = new colorHDR(0.15f, 1f, 0.25f, 1f);
        var mat = axes.AttachComponent<UnlitMaterial>();
        mat.Color = green;

        const float len = 0.22f;
        const float shaftLen = len * 0.74f;
        const float headLen = len - shaftLen;
        const float r = 0.01f;
        dir = dir.LengthSquared > 1e-6f ? dir.Normalized : float3.Backward;

        var shaft = axes.AddSlot("Shaft");
        shaft.LocalRotation.Value = FabrikSolver.FromToRotation(float3.Up, dir);
        shaft.LocalPosition.Value = dir * (shaftLen * 0.5f);
        var cyl = shaft.AttachComponent<LumoraMeshes.CylinderMesh>();
        cyl.Radius.Value = r;
        cyl.Height.Value = shaftLen;
        var sr = shaft.AttachComponent<MeshRenderer>();
        sr.Mesh.Target = cyl;
        sr.Material.Target = mat;

        var head = axes.AddSlot("Head");
        head.LocalRotation.Value = FabrikSolver.FromToRotation(float3.Up, dir);
        head.LocalPosition.Value = dir * (shaftLen + headLen * 0.5f);
        var cone = head.AttachComponent<LumoraMeshes.ConeMesh>();
        cone.RadiusBase.Value = r * 2.8f;
        cone.RadiusTop.Value = 0f;
        cone.Height.Value = headLen;
        var hr = head.AttachComponent<MeshRenderer>();
        hr.Mesh.Target = cone;
        hr.Material.Target = mat;
    }

    private void RefreshDirectionArrows()
    {
        foreach (var proxy in EnumerateMarkers())
        {
            var axes = proxy?.FindChild("Axes");
            if (axes != null && !axes.IsDestroyed)
                axes.ActiveSelf.Value = ShowDirections.Value;
        }
    }

    private IEnumerable<Slot> EnumerateMarkers()
    {
        yield return _headProxy.Target;
        yield return _leftHandProxy.Target;
        yield return _rightHandProxy.Target;
        yield return _leftFootProxy.Target;
        yield return _rightFootProxy.Target;
        yield return _pelvisProxy.Target;
    }

    private void RefreshOptionalMarkers()
    {
        SetActive(_leftFootProxy.Target, CalibrateFeet.Value);
        SetActive(_rightFootProxy.Target, CalibrateFeet.Value);

        // On a biped the pelvis is genuinely optional: hips can be inferred from head and feet. On a
        // quadruped it is one of the two ends of the spine and the rear legs hang off it, so hiding it
        // would leave the back half of the animal with nothing to solve against. The four paws are not
        // optional either, which is why nothing here touches them. -xlinka
        SetActive(_pelvisProxy.Target, QuadrupedMode.Value || CalibratePelvis.Value);
        SetActive(_chestProxy.Target, true);

        // Hidden is only hidden: these still get baked on Create either way, exactly as the source
        // platform does it. The toggle is about clutter while you work, not about whether the avatar
        // ends up with tool anchors. -xlinka
        bool anchors = ShowToolAnchors.Value && !QuadrupedMode.Value;
        SetActive(_leftToolAnchorProxy.Target, anchors);
        SetActive(_rightToolAnchorProxy.Target, anchors);
        SetActive(_leftGrabAnchorProxy.Target, anchors);
        SetActive(_rightGrabAnchorProxy.Target, anchors);
    }

    private static void SetActive(Slot slot, bool active)
    {
        if (slot != null && !slot.IsDestroyed)
            slot.ActiveSelf.Value = active;
    }

    // Two small eye markers on the headset, showing where the eyes sit and which way they face -
    // a visible cue for the eye drivers.
    // MARKER SHAPES
    //
    // The other tool ships modelled proxies for these: a headset, a left and right hand, feet, a
    // pelvis. We have no such art, so each marker gets a shape built from primitives that reads as
    // the thing at a glance and, more importantly, shows which way it is facing. Local -Z is the
    // view/front direction throughout. All of it is a child of the marker, so grabbing, scaling and
    // the detection halo are untouched. -xlinka
    private static Slot ShapePart(Slot marker, string name, in colorHDR fill, float alpha)
    {
        var part = marker.AddSlot(name);
        var material = part.AttachComponent<UnlitMaterial>();
        material.Color = new colorHDR(fill.r, fill.g, fill.b, alpha);
        if (alpha < 0.99f)
            material.BlendMode.Value = BlendMode.Transparent;
        return part;
    }

    private static void Draw(Slot part, LumoraMeshes.ProceduralMesh mesh)
    {
        var renderer = part.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = part.GetComponent<UnlitMaterial>();
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }

    // A visor: a wide slab across the eyes with a strap band behind it.
    private static void AddHeadsetShape(Slot marker)
    {
        if (marker == null || marker.IsDestroyed)
            return;
        var visor = ShapePart(marker, "Visor", HeadFill, 0.85f);
        visor.LocalPosition.Value = new float3(0f, 0.02f, -0.055f);
        var visorMesh = visor.AttachComponent<LumoraMeshes.BevelBoxMesh>();
        visorMesh.Size.Value = new float3(0.17f, 0.075f, 0.05f);
        visorMesh.Bevel.Value = 0.012f;
        Draw(visor, visorMesh);

        var band = ShapePart(marker, "Band", HeadFill, 0.5f);
        band.LocalPosition.Value = new float3(0f, 0.02f, 0.02f);
        band.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, MathF.PI * 0.5f);
        var bandMesh = band.AttachComponent<LumoraMeshes.CylinderMesh>();
        bandMesh.Radius.Value = 0.085f;
        bandMesh.Height.Value = 0.03f;
        bandMesh.Segments.Value = 16;
        Draw(band, bandMesh);
    }

    // A palm slab with four fingers and a thumb, each a run of joint balls. Until there is a hand
    // model this is what you line up against a model's knuckles, and a slab with a stub on it gave you
    // nothing to aim with. One sphere mesh and one material do all fifteen balls: the slots just scale
    // it, so the whole hand costs two assets. Fingers run down local -Z, the way the marker points, and
    // the thumb sits on the inner edge so left and right read apart from any angle. -xlinka
    private static void AddHandShape(Slot marker, in colorHDR fill, bool left)
    {
        if (marker == null || marker.IsDestroyed)
            return;

        var palm = ShapePart(marker, "Palm", fill, 0.9f);
        palm.LocalPosition.Value = new float3(0f, 0f, -0.022f);
        var palmMesh = palm.AttachComponent<LumoraMeshes.BevelBoxMesh>();
        palmMesh.Size.Value = new float3(0.078f, 0.024f, 0.085f);
        palmMesh.Bevel.Value = 0.01f;
        Draw(palm, palmMesh);

        var material = palm.GetComponent<UnlitMaterial>()!;
        // Unit sphere shared by every joint; the ball slots carry the real radius in their scale.
        var joint = palm.AttachComponent<LumoraMeshes.SphereMesh>();
        joint.Radius.Value = 1f;
        joint.Segments.Value = 8;
        joint.Rings.Value = 6;

        // +X is the thumb side on a left hand, so the finger order flips with the hand.
        float side = left ? 1f : -1f;
        float index = side * 0.028f;
        float middle = side * 0.0095f;
        float ring = -side * 0.0095f;
        float little = -side * 0.028f;

        AddJointChain(marker, material, joint, "Index",
            (index, -0.064f, 0.0095f), (index, -0.095f, 0.0085f), (index, -0.119f, 0.0072f));
        AddJointChain(marker, material, joint, "Middle",
            (middle, -0.066f, 0.0098f), (middle, -0.100f, 0.0088f), (middle, -0.127f, 0.0075f));
        AddJointChain(marker, material, joint, "Ring",
            (ring, -0.064f, 0.0093f), (ring, -0.095f, 0.0083f), (ring, -0.119f, 0.0070f));
        AddJointChain(marker, material, joint, "Little",
            (little, -0.060f, 0.0085f), (little, -0.085f, 0.0075f), (little, -0.104f, 0.0064f));
        AddJointChain(marker, material, joint, "Thumb",
            (side * 0.038f, -0.008f, 0.0115f), (side * 0.050f, -0.034f, 0.0100f), (side * 0.057f, -0.056f, 0.0086f));

        // The direction arrow runs down the same axis as the fingers, so lift it clear of them or it
        // is a green shaft through the middle of the hand and you can see neither.
        var axes = marker.FindChild("Axes", recursive: false);
        if (axes != null && !axes.IsDestroyed)
            axes.LocalPosition.Value = new float3(0f, 0.045f, 0f);
    }

    private static void AddJointChain(Slot marker, UnlitMaterial material, LumoraMeshes.SphereMesh mesh,
        string name, (float x, float z, float r) a, (float x, float z, float r) b, (float x, float z, float r) c)
    {
        var joints = new[] { a, b, c };
        for (int i = 0; i < joints.Length; i++)
        {
            var ball = marker.AddSlot(name + (i + 1));
            ball.LocalPosition.Value = new float3(joints[i].x, 0f, joints[i].z);
            ball.LocalScale.Value = float3.One * joints[i].r;
            var renderer = ball.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = mesh;
            renderer.Material.Target = material;
            renderer.ShadowCastMode.Value = ShadowCastMode.Off;
        }
    }

    // A sole with a toe end, pointing the way the marker faces.
    private static void AddFootShape(Slot marker, in colorHDR fill)
    {
        if (marker == null || marker.IsDestroyed)
            return;
        var sole = ShapePart(marker, "Sole", fill, 0.9f);
        sole.LocalPosition.Value = new float3(0f, -0.02f, -0.03f);
        var soleMesh = sole.AttachComponent<LumoraMeshes.BevelBoxMesh>();
        soleMesh.Size.Value = new float3(0.075f, 0.03f, 0.17f);
        soleMesh.Bevel.Value = 0.012f;
        Draw(sole, soleMesh);

        var toe = ShapePart(marker, "Toe", fill, 0.9f);
        toe.LocalPosition.Value = new float3(0f, -0.012f, -0.115f);
        var toeMesh = toe.AttachComponent<LumoraMeshes.SphereMesh>();
        toeMesh.Radius.Value = 0.032f;
        toeMesh.Segments.Value = 12;
        toeMesh.Rings.Value = 8;
        Draw(toe, toeMesh);
    }

    // Hips: a wide, shallow block.
    private static void AddPelvisShape(Slot marker, in colorHDR fill)
    {
        if (marker == null || marker.IsDestroyed)
            return;
        var block = ShapePart(marker, "Hips", fill, 0.85f);
        var mesh = block.AttachComponent<LumoraMeshes.BevelBoxMesh>();
        mesh.Size.Value = new float3(0.2f, 0.09f, 0.13f);
        mesh.Bevel.Value = 0.018f;
        Draw(block, mesh);
    }

    // Which marker is which, written next to it. Colour matched to the marker so the word and the ball
    // agree, and turned to face whoever is reading rather than the figure's own front. -xlinka
    private static void AddSideLabel(Slot marker, string text, colorHDR fill)
    {
        if (marker == null || marker.IsDestroyed)
            return;
        var label = marker.AddSlot("Label");
        label.LocalPosition.Value = new float3(0f, 0.085f, 0f);
        label.AttachComponent<FaceLocalUser>();
        var renderer = label.AttachComponent<TextRenderer>();
        renderer.Text.Value = text;
        renderer.Size.Value = 0.05f;
        renderer.Color.Value = new color(fill.r, fill.g, fill.b, 1f);
        renderer.HorizontalAlign.Value = TextHorizontalAlignment.Center;
        renderer.VerticalAlign.Value = TextVerticalAlignment.Middle;
        renderer.OutlineThickness.Value = 1.2f;
    }

    private static void AddEyeBalls(Slot head)
    {
        if (head == null || head.IsDestroyed)
            return;
        AddEyeBall(head, -0.032f);
        AddEyeBall(head, 0.032f);
    }

    private static void AddEyeBall(Slot head, float xOffset)
    {
        var eye = head.AddSlot("Eye");
        eye.LocalPosition.Value = new float3(xOffset, 0.02f, -0.06f);   // local -Z is view/front
        var mesh = eye.AttachComponent<LumoraMeshes.SphereMesh>();
        mesh.Radius.Value = 0.014f;
        mesh.Segments.Value = 12;
        mesh.Rings.Value = 8;
        var renderer = eye.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        var material = eye.AttachComponent<UnlitMaterial>();
        material.Color = new colorHDR(1f, 1f, 1f, 1f);
        renderer.Material.Target = material;
    }

    // CONTROL PANEL (Helio canvas)

    private void BuildControlPanel()
    {
        var panelSlot = Slot.AddSlot("Controls");
        panelSlot.LocalPosition.Value = new float3(0.7f, 1.15f, 0f);
        panelSlot.LocalScale.Value = float3.One * CanvasScale;
        // The tool root faces the user (its -Z points at them so the figure looks back), and the canvas reads
        // along its +Z, so without this it would face the same way as the figure - away from the user. Yaw it
        // 180 so the controls face you, with a slight downward tilt for reading at chest height. Our floatQ.Euler
        // takes RADIANS in (yaw, pitch, roll) order, not degrees. -xlinka
        const float deg = MathF.PI / 180f;
        panelSlot.LocalRotation.Value = floatQ.Euler(180f * deg, 10f * deg, 0f);

        // The dashboard's palette, on this panel only: UITheme is shared with the inspectors, so the
        // colours are set on this instance rather than on the type's defaults. -xlinka
        var theme = panelSlot.AttachComponent<UITheme>();
        _panelTheme = theme;
        theme.PanelBackground.Value = DashTheme.Panel;
        theme.Header.Value = DashTheme.Surface;
        theme.Separator.Value = DashTheme.Divider;
        theme.Border.Value = DashTheme.OutlineStrong;
        theme.Accent.Value = DashTheme.Accent;
        theme.ButtonFill.Value = DashTheme.Surface;
        theme.ButtonText.Value = DashTheme.Text;
        theme.PositiveFill.Value = DashTheme.Positive;
        theme.NegativeFill.Value = DashTheme.Negative;
        theme.TextPrimary.Value = DashTheme.Text;
        theme.TextDim.Value = DashTheme.TextDim;
        theme.CornerRadius.Value = DashTheme.RadiusPanel;
        // The dash's face, not the generic in-world one: it matches the rest of the interface and it
        // actually carries the tick glyph the checkboxes draw, which the fallback font renders as a
        // question mark. -xlinka
        var font = panelSlot.AddSlot("Font").AttachComponent<FontProvider>();
        font.URL.Value = DashTheme.FontRegular;
        font.FallbackURLs.Add(DashTheme.FontRegular);
        theme.Font.Target = font;

        var panel = panelSlot.AttachComponent<PanelShell>();
        panel.Title.Value = "Avatar Studio";
        // Sized to its rows, not guessed: header 52, top+bottom padding 28, sixteen rows summing 570, and
        // the fifteen 6px gaps between them. A panel shorter than its content does not scroll or clip
        // here, it spills into the world. -xlinka
        panel.Size.Value = new float2(400f, 740f);
        panel.TitleTextSize.Value = 22f;
        panel.HeaderHeight.Value = 52f;
        // The panel grabs by its OWN title bar and moves ONLY itself - the markers are independent siblings, so
        // repositioning the controls never drags the orbs or rotates their direction arrows. There is no
        // whole-tool grab: you place each marker on the model individually, like the avatar's own parts.
        panel.AllowGrab.Value = true;
        panel.Scalable.Value = false;
        theme.ApplyTo(panel);
        panel.CloseRequested += _ => Slot.Destroy();

        var content = panel.ContentSlot!;
        var b = new UIBuilder(content);
        b.Font(theme.ThemeFont)
            .TextColor(DashTheme.Text)
            .ForegroundColor(DashTheme.Accent)
            .BackgroundColor(DashTheme.Surface)
            .RoundedSprite(theme.RoundedSprite);   // rounded button corners, matching the panel

        var layout = b.VerticalLayout(6f, DashTheme.Gap);
        layout.ForceExpandWidth.Value = true;
        layout.ForceExpandHeight.Value = false;
        layout.PaddingLeft.Value = 14f;
        layout.PaddingRight.Value = 14f;
        layout.PaddingTop.Value = 14f;
        layout.PaddingBottom.Value = 14f;
        FillToParent(b.Current);

        // What the tool is waiting for, in its own words. Two lines of room so the longer states do
        // not clip, and dim so the buttons under it stay the loud thing.
        SetRowHeight(b, 44f);
        _statusText = b.Text("Looking for a model...", 14f, DashTheme.TextDim);
        _statusText.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        _statusText.VerticalAlignment.Value = TextVerticalAlignment.Middle;

        // HEAD. Center Head sets the position; the three axis buttons snap the view frame's forward,
        // up and right onto the head bone's nearest axis, so an oddly authored skull still gives a
        // clean upright view.
        AddSectionLabel(b, "HEAD");
        SetRowHeight(b, 38f);
        Gate(b.Button("Center Head", (_, _) => AlignHeadPosition(), DashTheme.SurfaceHover), needsHead: true);

        SetRowHeight(b, 38f);
        var axes = SplitRow(b, 3);
        Gate(CellButton(b, axes[0], "Fwd", () => AlignHeadAxis(forward: true, up: false, right: false), DashTheme.SurfaceHover), needsHead: true);
        Gate(CellButton(b, axes[1], "Up", () => AlignHeadAxis(forward: false, up: true, right: false), DashTheme.SurfaceHover), needsHead: true);
        Gate(CellButton(b, axes[2], "Right", () => AlignHeadAxis(forward: false, up: false, right: true), DashTheme.SurfaceHover), needsHead: true);

        // LIMBS.
        AddSectionLabel(b, "LIMBS");
        SetRowHeight(b, 38f);
        var limbs = SplitRow(b, 2);
        Gate(CellButton(b, limbs[0], "Hands", AlignHands, DashTheme.SurfaceHover), needsHead: true);
        Gate(CellButton(b, limbs[1], "Body", AlignBody, DashTheme.SurfaceHover), needsHead: true);

        SetRowHeight(b, 42f);
        Gate(b.Button("Align All", (_, _) => AlignMarkersToRig(), DashTheme.Accent), needsHead: true);

        // Ungated on purpose: this one squares the anchors against their own hand markers and never looks
        // at the model, so there is nothing for a missing rig to break. Rounded by hand since Gate, which
        // normally does it, is what we are skipping.
        SetRowHeight(b, 38f);
        Round(b.Button("Align Tool Anchors", (_, _) => AlignToolAnchors(), DashTheme.SurfaceHover), DashTheme.RadiusControl);

        SetRowHeight(b, 50f);
        Gate(b.Button("Create Avatar", (_, _) => RunCreate(), DashTheme.Positive), needsHead: true);

        AddSectionLabel(b, "OPTIONS");
        AddToggleRow(b, "Calibrate feet", CalibrateFeet);
        AddToggleRow(b, "Calibrate pelvis", CalibratePelvis);
        AddToggleRow(b, "Set up eyes", SetupEyes);
        AddToggleRow(b, "Show directions", ShowDirections);
        AddToggleRow(b, "Show tool anchors", ShowToolAnchors);
        AddToggleRow(b, "Quadruped", QuadrupedMode);
    }

    // A quiet heading over each group, in the dash's muted label colour.
    private static void AddSectionLabel(UIBuilder b, string text)
    {
        SetRowHeight(b, 22f);
        var label = b.Text(text, 12f, DashTheme.TextMuted);
        label.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        label.VerticalAlignment.Value = TextVerticalAlignment.Middle;
    }

    // Columns with a gap between them, for controls that belong on one line. Equal width unless the
    // caller hands over its own.
    private static List<RectTransform> SplitRow(UIBuilder b, int columns, float[]? widths = null)
    {
        b.Empty("Row");
        b.Nest();
        var weights = new float[columns * 2 - 1];
        for (int i = 0; i < weights.Length; i++)
            weights[i] = (i % 2 == 0) ? (widths != null ? widths[i / 2] : 1f) : 0.06f;
        var splits = b.SplitHorizontally(weights);
        var cells = new List<RectTransform>(columns);
        for (int i = 0; i < splits.Count; i += 2)
            cells.Add(splits[i]);
        b.NestOut();
        return cells;
    }

    // A control built under a split cell is NOT laid out by anything: a fresh RectTransform is a
    // 100x100 box in the middle of its parent, so the pills came out three rows tall and hanging over
    // the edge of the panel and the labels sat nowhere near their buttons. Anything that goes in a
    // cell is told to fill it. -xlinka
    private Button CellButton(UIBuilder b, RectTransform cell, string label, Action pressed, in color fill)
    {
        b.NestInto(cell);
        var button = b.Button(label, (_, _) => pressed(), fill);
        FillToParent(button.Slot);
        b.NestOut();
        return button;
    }

    private static Text CellText(UIBuilder b, RectTransform cell, string content, float size, in color tint,
        TextHorizontalAlignment alignment)
    {
        b.NestInto(cell);
        var text = b.Text(content, size, tint);
        text.WordWrap.Value = false;
        text.HorizontalAlignment.Value = alignment;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        FillToParent(text.Slot);
        b.NestOut();
        return text;
    }

    private void Gate(Button button, bool needsHead)
    {
        if (button == null || button.IsDestroyed)
            return;
        _gated.Add((button, needsHead));
        Round(button, DashTheme.RadiusControl);

        // A Button paints its own disabled state from a derived flat grey. Against this panel that
        // grey is LIGHTER than the button it replaces, so every switched-off control read as a white
        // slab. Recolour it, and dim the label with it so the row goes quiet as a whole. -xlinka
        foreach (var driver in button.ColorDrivers)
            driver.DisabledColor.Value = DashTheme.Field;

        var label = button.Slot?.FindChild("Text", recursive: true)?.GetComponent<Text>();
        if (label != null && !label.IsDestroyed)
        {
            var textDriver = button.AddColorDriver(label.Color, label.Color.Value, InteractionColorMode.Direct);
            textDriver.HighlightColor.Value = label.Color.Value;
            textDriver.PressedColor.Value = label.Color.Value;
            textDriver.DisabledColor.Value = DashTheme.TextMuted;
        }
    }

    // The studio opens in any world, model or not: it is the tool you use to FIND the model, so
    // refusing to appear until one exists is backwards. What it does gate is the work: aligning and
    // creating need a rig, and the head marker has to be sitting on it, because every align is
    // measured from that frame. The panel says which of the two is missing. -xlinka
    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        if (_statusText == null || _statusText.IsDestroyed)
            return;

        _statusPoll -= World?.Time?.Delta ?? 0f;
        if (_statusPoll > 0f)
            return;
        _statusPoll = 0.25f;

        var rig = FindRigQuiet();
        bool rigFound = rig != null && !rig.IsDestroyed;
        bool headOnRig = rigFound && IsHeadMarkerOnRig(rig!);
        bool ready = rigFound && headOnRig;

        if (_stateKnown && ready == _lastReady && rigFound == _lastRigFound)
            return;
        _stateKnown = true;
        _lastReady = ready;
        _lastRigFound = rigFound;

        _statusText.Content.Value = !rigFound
            ? "No rigged model in this world.\nImport one, then line the markers up."
            : !headOnRig
                ? "Model found. Put the head marker\non its head to switch these on."
                : "Ready: align what you need, then Create.";

        foreach (var (button, needsHead) in _gated)
        {
            if (button == null || button.IsDestroyed)
                continue;
            button.Interactable.Value = needsHead ? ready : rigFound;
        }
    }

    // Is the head marker actually over the rig? Measured against the head bone when the rig has one,
    // and against the nearest bone otherwise, so a rig with an odd head name still counts.
    private bool IsHeadMarkerOnRig(HumanoidRig rig)
    {
        var marker = _headProxy.Target;
        if (marker == null || marker.IsDestroyed)
            return false;
        var head = rig.TryGetBone(BodyNode.Head);
        if (head != null && !head.IsDestroyed)
            return (head.GlobalPosition - marker.GlobalPosition).Length <= HeadDetectionRadius;

        foreach (var node in HumanoidRig.RequiredBones)
        {
            var bone = rig.TryGetBone(node);
            if (bone == null || bone.IsDestroyed)
                continue;
            if ((bone.GlobalPosition - marker.GlobalPosition).Length <= HeadDetectionRadius)
                return true;
        }
        return false;
    }

    // FindRig warns when it finds nothing, which is right for a button press and wrong for a poll
    // that runs four times a second. Same search, no log. -xlinka
    //
    // THIS WAS THE THIRD MOST EXPENSIVE COMPONENT IN THE ENGINE, measured: 4.8 MILLISECONDS per call,
    // peaking at 25 ms, roughly 3 ms per frame amortised, to keep a status label up to date. Three
    // things stacked up:
    //
    //  1. It walked the WHOLE WORLD twice per poll, once for HumanoidRig and once for SkeletonBuilder.
    //  2. It used the IEnumerable overload of GetComponentsInChildren, which allocates an iterator state
    //     machine and an OfType wrapper AT EVERY SLOT. Slot.cs:1800 says so and provides an
    //     allocation-free List overload right underneath it, which this was not using.
    //  3. It wrapped each walk in `new List<>(...)`, which fully materialises the entire world before the
    //     loop body runs even once, so the early `return` on the first match saved nothing at all.
    //
    // Now: the found rig is CACHED and revalidated cheaply, so the steady state (a rig exists) does no
    // walk whatsoever. The walk only runs when there is no valid cached rig, uses the allocation-free
    // overload with reused buffers, and backs off to once a second because "no model in this world" is
    // both the most expensive case and the least urgent one. -xlinka
    private HumanoidRig? _cachedRig;
    private float _rigSearchBackoff;
    private readonly List<HumanoidRig> _rigSearchBuffer = new();
    private readonly List<SkeletonBuilder> _skeletonSearchBuffer = new();

    private bool CachedRigStillValid()
    {
        var rig = _cachedRig;
        return rig != null && !rig.IsDestroyed && IsCandidate(rig.Slot) && rig.Bones.Count > 0;
    }

    private HumanoidRig? FindRigQuiet()
    {
        if (CachedRigStillValid())
            return _cachedRig;

        _cachedRig = null;

        // Only the miss path is throttled. A hit never reaches here.
        float delta = World?.Time?.Delta ?? 0f;
        _rigSearchBackoff -= delta;
        if (_rigSearchBackoff > 0f)
            return null;
        _rigSearchBackoff = RigSearchBackoffSeconds;

        var world = World;
        if (world?.RootSlot == null)
            return null;

        _rigSearchBuffer.Clear();
        world.RootSlot.GetComponentsInChildren(_rigSearchBuffer);
        for (int i = 0; i < _rigSearchBuffer.Count; i++)
        {
            var rig = _rigSearchBuffer[i];
            if (rig != null && !rig.IsDestroyed && IsCandidate(rig.Slot) && rig.Bones.Count > 0)
            {
                _rigSearchBuffer.Clear();
                _cachedRig = rig;
                return rig;
            }
        }
        _rigSearchBuffer.Clear();

        _skeletonSearchBuffer.Clear();
        world.RootSlot.GetComponentsInChildren(_skeletonSearchBuffer);
        for (int i = 0; i < _skeletonSearchBuffer.Count; i++)
        {
            var skeleton = _skeletonSearchBuffer[i];
            if (IsCandidate(skeleton?.Slot))
            {
                _skeletonSearchBuffer.Clear();
                _cachedRig = EnsureRig(skeleton!);
                return _cachedRig;
            }
        }
        _skeletonSearchBuffer.Clear();
        return null;
    }

    // A model appearing is a human-scale event; a second of latency on a status label is invisible, and
    // it is a quarter of the walks.
    private const float RigSearchBackoffSeconds = 1f;

    // "Align All": snap every marker onto the same computed reference frames Create will write.
    private void AlignMarkersToRig()
    {
        var rig = RequireRig();
        if (rig == null) return;
        var avatar = ResolveAvatarRoot(rig);
        if (avatar == null || avatar.IsDestroyed)
            return;
        AlignMarkersToComputedReferences(avatar, rig);
    }

    // Snap the headset/view marker onto the model's head bone POSITION only. Orientation is handled by the
    // Align Forward/Up/Right buttons, so you can recentre without losing a hand-tuned facing and vice-versa.
    private void AlignHeadPosition()
    {
        var rig = RequireRig();
        if (rig == null) return;
        AlignMarker(_headProxy.Target, rig.TryGetBone(BodyNode.Head));
    }

    // Snap the head marker's chosen view axis onto the head bone's NEAREST axis. The marker is the View/HMD
    // frame the avatar calibrates against, so this is what makes a back- or sideways-authored skull read
    // upright: GetClosestAxis ignores the bone's authored roll and just picks the local axis nearest where
    // you've aimed the marker, then we rotate that one axis onto it. Off-axis tilt is flattened (forward and
    // right stay level, up stays vertical) so aligning one axis never pitches the others. -xlinka
    private void AlignHeadAxis(bool forward, bool up, bool right)
    {
        var rig = RequireRig();
        if (rig == null) return;
        var head = _headProxy.Target;
        var bone = rig.TryGetBone(BodyNode.Head);
        if (head == null || head.IsDestroyed || bone == null || bone.IsDestroyed)
            return;

        if (forward)
            SnapMarkerAxis(head, bone, head.Backward, flattenLocalY: true);  // view forward is local -Z; keep level
        if (up)
            SnapMarkerAxis(head, bone, head.Up, flattenLocalZ: true);        // up stays out of the forward plane
        if (right)
            SnapMarkerAxis(head, bone, head.Right, flattenLocalY: true);     // right stays level
    }

    // Rotate `marker` so its current `fromWorld` axis points along the bone axis nearest to it, optionally
    // flattening the chosen direction in the marker's OWN frame (drop local Y to keep it level, or local Z to
    // keep it out of the forward plane) so the snap doesn't pitch/roll the marker's other axes.
    private static void SnapMarkerAxis(Slot marker, Slot bone, float3 fromWorld, bool flattenLocalY = false, bool flattenLocalZ = false)
    {
        if (fromWorld.LengthSquared < 1e-8f)
            return;
        fromWorld = fromWorld.Normalized;
        float3 axis = GetClosestAxis(bone, fromWorld);
        float3 local = marker.GlobalDirectionToLocal(axis);
        if (flattenLocalY) local.y = 0f;
        if (flattenLocalZ) local.z = 0f;
        float3 toWorld = marker.LocalDirectionToGlobal(local);
        if (toWorld.LengthSquared < 1e-8f)
            return;
        marker.GlobalRotation = FabrikSolver.FromToRotation(fromWorld, toWorld.Normalized) * marker.GlobalRotation;
    }

    private static readonly float3[] LocalAxes = [float3.Right, float3.Up, float3.Forward];

    // The bone's local axis (one of +-X/+-Y/+-Z, returned in world space) that best lines up with worldDir.
    // Lets the head frame lock onto however the skull bone was actually authored instead of assuming a
    // convention. -xlinka
    private static float3 GetClosestAxis(Slot slot, float3 worldDir)
    {
        floatQ rot = slot.GlobalRotation;
        float3 best = worldDir;
        float bestAbs = -1f;
        foreach (var local in LocalAxes)
        {
            float3 world = rot * local;
            float d = float3.Dot(world, worldDir);
            float a = d < 0f ? -d : d;
            if (a > bestAbs)
            {
                bestAbs = a;
                best = d < 0f ? -world : world;
            }
        }
        return best.LengthSquared > 1e-8f ? best.Normalized : worldDir;
    }

    // Snap both hand markers onto computed grip frames. Raw imported hand-bone rotations are often rolled
    // sideways, so the grip axes come from the forearm chain instead. -xlinka
    private void AlignHands()
    {
        var rig = RequireRig();
        if (rig == null) return;
        var avatar = ResolveAvatarRoot(rig);
        if (avatar == null || avatar.IsDestroyed)
            return;
        ApplyMarkerPose(_leftHandProxy.Target, avatar, AvatarCalibration.ComputeHandGrip(avatar, rig, rightSide: false));
        ApplyMarkerPose(_rightHandProxy.Target, avatar, AvatarCalibration.ComputeHandGrip(avatar, rig, rightSide: true));
    }

    // Put every tool anchor back on its hand's frame, keeping where it has been dragged to.
    //
    // This is the source platform's own "align tool anchors": rotation only. An anchor that has been
    // twisted while positioning it points the tool off at an angle that is very hard to see and very
    // obvious once you are holding something, and there is no way to eyeball your way back to square -
    // so the button hands the rotation back rather than resetting the whole anchor and throwing away the
    // placement work. Needs no rig, only the hand markers, which is why it is not gated. -xlinka
    private void AlignToolAnchors()
    {
        SquareAnchorToHand(_leftToolAnchorProxy.Target, _leftHandProxy.Target);
        SquareAnchorToHand(_leftGrabAnchorProxy.Target, _leftHandProxy.Target);
        SquareAnchorToHand(_rightToolAnchorProxy.Target, _rightHandProxy.Target);
        SquareAnchorToHand(_rightGrabAnchorProxy.Target, _rightHandProxy.Target);
    }

    private static void SquareAnchorToHand(Slot anchor, Slot hand)
    {
        if (anchor == null || anchor.IsDestroyed || hand == null || hand.IsDestroyed)
            return;
        anchor.GlobalRotation = hand.GlobalRotation;
    }

    // Snap pelvis + feet markers from geometric body-forward frames, turning on those calibration options when
    // the rig has the bones.
    private void AlignBody()
    {
        var rig = RequireRig();
        if (rig == null) return;
        var avatar = ResolveAvatarRoot(rig);
        if (avatar == null || avatar.IsDestroyed)
            return;
        if (rig.TryGetBone(BodyNode.LeftFoot) != null && rig.TryGetBone(BodyNode.RightFoot) != null)
            CalibrateFeet.Value = true;
        if (rig.TryGetBone(BodyNode.Hips) != null)
            CalibratePelvis.Value = true;
        ApplyMarkerPose(_pelvisProxy.Target, avatar, AvatarCalibration.ComputePelvis(avatar, rig));
        ApplyMarkerPose(_leftFootProxy.Target, avatar, AvatarCalibration.ComputeFoot(avatar, rig, rightSide: false));
        ApplyMarkerPose(_rightFootProxy.Target, avatar, AvatarCalibration.ComputeFoot(avatar, rig, rightSide: true));
    }

    private void AlignMarkersToComputedReferences(Slot avatar, HumanoidRig rig)
    {
        // Root frame onto the body front (world-invariant, nothing visibly moves) so the references and the
        // equip facing agree. Heals avatars set up before this existed; no-op when already aligned.
        AvatarCalibration.AlignAvatarFacing(avatar, rig);

        bool hasFeet = rig.TryGetBone(BodyNode.LeftFoot) != null && rig.TryGetBone(BodyNode.RightFoot) != null;
        bool hasPelvis = rig.TryGetBone(BodyNode.Hips) != null;
        if (hasFeet)
            CalibrateFeet.Value = true;
        if (hasPelvis)
            CalibratePelvis.Value = true;

        ApplyMarkerPose(_headProxy.Target, avatar, AvatarCalibration.ComputeView(avatar, rig));
        ApplyMarkerPose(_leftHandProxy.Target, avatar, AvatarCalibration.ComputeHandGrip(avatar, rig, rightSide: false));
        ApplyMarkerPose(_rightHandProxy.Target, avatar, AvatarCalibration.ComputeHandGrip(avatar, rig, rightSide: true));
        if (hasFeet)
        {
            ApplyMarkerPose(_leftFootProxy.Target, avatar, AvatarCalibration.ComputeFoot(avatar, rig, rightSide: false));
            ApplyMarkerPose(_rightFootProxy.Target, avatar, AvatarCalibration.ComputeFoot(avatar, rig, rightSide: true));
        }
        if (hasPelvis)
            ApplyMarkerPose(_pelvisProxy.Target, avatar, AvatarCalibration.ComputePelvis(avatar, rig));

        RefreshOptionalMarkers();
        RefreshDirectionArrows();
        LumoraLogger.Log($"AvatarStudio: auto-aligned markers from computed references on '{avatar.SlotName.Value}'");
    }

    private static void ApplyMarkerPose(Slot marker, Slot avatar, in AvatarCalibration.RefPose pose)
    {
        if (!pose.Valid || marker == null || marker.IsDestroyed || avatar == null || avatar.IsDestroyed)
            return;
        marker.GlobalPosition = avatar.LocalPointToGlobal(pose.LocalPosition);
        marker.GlobalRotation = avatar.GlobalRotation * pose.LocalRotation;
    }

    // Yaw the pelvis marker so its forward (local -Z) points along the rig's GEOMETRIC forward - the body
    // facing measured from bone POSITIONS (shoulder line x hips->head), never a bone's authored rotation. This
    // is the fix for "hips wrong way round": the hips bone on imported anthro rigs is often authored facing
    // behind the body, so copying its rotation spun the marker and its arrow backwards. The geometric forward
    // can't be fooled by that. Stays upright (horizontal yaw only). -xlinka
    private void FacePelvisForward(HumanoidRig rig)
    {
        var pelvis = _pelvisProxy.Target;
        if (pelvis == null || pelvis.IsDestroyed)
            return;
        float3? fwd = rig.ForwardAxis.Value ?? rig.GuessForwardAxis();
        if (!fwd.HasValue || fwd.Value.LengthSquared < 1e-8f)
            return;
        // Up fallback for the same reason as the spawn: a model facing exactly away from the marker is
        // a 180 with no axis, and any horizontal one turns the hips upside down instead of round.
        pelvis.GlobalRotation = FabrikSolver.FromToRotation(pelvis.Backward, fwd.Value.Normalized, float3.Up) * pelvis.GlobalRotation;
    }

    private HumanoidRig RequireRig()
    {
        var rig = FindRig();
        if (rig == null || rig.IsDestroyed)
        {
            LumoraLogger.Warn("AvatarStudio: nothing to align to - put the head marker over the avatar's head first");
            return null!;
        }
        return rig;
    }

    private static void AlignMarker(Slot marker, Slot bone, bool copyRotation = false)
    {
        if (marker == null || marker.IsDestroyed || bone == null || bone.IsDestroyed)
            return;
        marker.GlobalPosition = bone.GlobalPosition;
        if (copyRotation)
            marker.GlobalRotation = bone.GlobalRotation;
    }

    // The label, then a pill that says On or Off and carries the accent when it is on. The shared
    // checkbox draws a tick CHARACTER, and neither the dash face nor the in-world fallback has a
    // glyph for it, so every switched-on option rendered as a question mark. A word and a colour
    // need no glyph, and it matches how the dash writes its own switches. -xlinka
    private void AddToggleRow(UIBuilder b, string label, Sync<bool> state)
    {
        SetRowHeight(b, 36f);
        var cells = SplitRow(b, 2, new[] { 0.74f, 0.26f });
        CellText(b, cells[0], label, 14f, DashTheme.Text, TextHorizontalAlignment.Left);

        var pill = CellButton(b, cells[1], state.Value ? "On" : "Off", () => state.Value = !state.Value, DashTheme.Surface);
        Round(pill, 14f);
        PaintToggle(pill, state.Value);
        // Follow the value, not the press: the optional-marker refresh flips these too. -xlinka
        state.OnChanged += _ => PaintToggle(pill, state.Value);
    }

    private void Round(Button? button, float radius)
    {
        if (button == null || button.IsDestroyed || _panelTheme == null || _panelTheme.IsDestroyed)
            return;
        var image = button.Slot?.GetComponent<Image>();
        if (image == null || image.IsDestroyed)
            return;
        image.Texture.Target = _panelTheme.RoundedSprite;
        image.NineSlice.Value = true;
        image.Borders.Value = new float4(radius, radius, radius, radius);
    }

    private static void PaintToggle(Button? pill, bool on)
    {
        if (pill == null || pill.IsDestroyed)
            return;

        var fill = on ? DashTheme.Accent : DashTheme.Surface;
        var image = pill.Slot?.GetComponent<Image>();
        // The tint is DRIVEN by the button's own color driver, so writing the field would be
        // overwritten on the next apply. Reconfigure the driver instead; AddColorDriver reuses the
        // one already holding the field. -xlinka
        if (image != null && !image.IsDestroyed)
            pill.AddColorDriver(image.Tint, fill);

        var text = pill.Slot?.FindChild("Text", recursive: true)?.GetComponent<Text>();
        if (text != null && !text.IsDestroyed)
        {
            text.Content.Value = on ? "On" : "Off";
            text.Color.Value = on ? DashTheme.OnAccent : DashTheme.TextDim;
        }
    }

    private static void SetRowHeight(UIBuilder b, float height)
    {
        b.MinHeight(height).PreferredHeight(height).FlexibleHeight(0f);
    }

    private static void FillToParent(Slot slot)
    {
        var rect = slot.GetComponent<RectTransform>() ?? slot.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;
    }

    // CREATE

    public void RunCreate()
    {
        var rig = FindRig();
        if (rig == null || rig.IsDestroyed)
        {
            LumoraLogger.Warn("AvatarStudio: no rigged model found under the markers - line them up and try again");
            return;
        }

        var avatar = ResolveAvatarRoot(rig);
        if (avatar == null || avatar.IsDestroyed)
            return;

        // The model import flow already builds a FULL avatar (AvatarForm + AvatarIK + references) on the model slot
        // when the rig is a biped. ResolveObjectRoot returns the OBJECT-ROOT wrapper, which is a different slot, so
        // attaching here would leave TWO AvatarIK driving the same rig - they fight every frame and contort/float the
        // body. Reuse the import-built avatar's slot as the canonical root, then sweep any stray duplicate
        // AvatarIK/AvatarForm off other slots in the subtree so exactly one solver remains. -xlinka
        foreach (var strayIk in new List<AvatarIK>(avatar.GetComponentsInChildren<AvatarIK>()))
            if (strayIk.Slot != avatar)
                strayIk.Destroy();
        foreach (var strayRoot in new List<AvatarForm>(avatar.GetComponentsInChildren<AvatarForm>()))
            if (strayRoot.Slot != avatar)
                strayRoot.Destroy();

        var skeleton = avatar.GetComponentInChildren<SkeletonBuilder>() ?? rig.Slot.GetComponentInChildren<SkeletonBuilder>();
        if (skeleton == null)
        {
            LumoraLogger.Warn("AvatarStudio: rig has no skeleton; cannot create");
            return;
        }

        // Strip the import-time authoring helpers: per-bone pose handles (grab + collider + visual),
        // any remaining whole-model grab, and object roots. The finished avatar gets a single root
        // grab via the equip path instead.
        AvatarRigSetup.RemovePoseHandles(rig);
        foreach (var grab in new List<Grabbable>(avatar.GetComponentsInChildren<Grabbable>()))
            grab.Destroy();
        foreach (var objectRoot in new List<ObjectRoot>(avatar.GetComponentsInChildren<ObjectRoot>()))
            objectRoot.Destroy();

        if (avatar.GetComponent<AvatarForm>() == null)
            avatar.AttachComponent<AvatarForm>();

        // With the toggle on, the rig is attached and populated HERE rather than left to detection.
        // TryAttachFor honours a pre-existing QuadrupedRig ("someone already decided"), so building one
        // first is the whole override: no force flag, no second code path, and the heuristic never runs.
        // If the bones still do not resolve into four legs TryAttachFor backs out and says so, which is
        // the honest outcome - the toggle says what the avatar IS, it cannot invent bones. -xlinka
        if (QuadrupedMode.Value
            && avatar.GetComponent<QuadrupedRig>() == null
            && avatar.GetComponentInChildren<QuadrupedRig>() == null)
        {
            var forced = avatar.AttachComponent<QuadrupedRig>();
            forced.PopulateFromSkeleton(skeleton);
            LumoraLogger.Log(
                $"AvatarStudio: quadruped mode - built a QuadrupedRig with {forced.Bones.Count} bones mapped "
                + $"(IsQuadruped={forced.IsQuadruped}), bypassing pose detection");
        }

        // Quadruped avatars take their own solver here too. TryAttachFor sweeps any AvatarIK off the
        // subtree itself, so the stray sweep above and this branch cannot leave two behind.
        var quadrupedIk = QuadrupedIK.TryAttachFor(avatar, skeleton, rig);
        if (quadrupedIk != null)
            return;

        var avatarIk = avatar.GetComponent<AvatarIK>() ?? avatar.AttachComponent<AvatarIK>();
        avatarIk.Skeleton.Target = skeleton;
        avatarIk.Rig.Target = rig;

        // The root frame must face the body before references bake, since equip resets the root to identity.
        // World-invariant, so manually-adjusted markers stay exactly where the user put them.
        AvatarCalibration.AlignAvatarFacing(avatar, rig);

        // Write creator marker transforms as the avatar references. The
        // headset marker is the View/HMD frame; AvatarIK captures the head
        // bone relative to that frame, so arbitrary imported skull rotations
        // do not get copied directly onto the live camera rotation.
        if (!BuildReferencesFromMarkers(avatar))
            AvatarCalibration.AutoPlaceReferences(avatar, rig, CalibrateFeet.Value, CalibratePelvis.Value);

        // Make the finished (unworn) avatar grabbable: coarse body colliders to hit +
        // a single root Grabbable. BlockWhenWorn keeps it pickable on the ground / passable, but ungrabbable
        // once equipped under a user root. (Equipping is still its own action via the equip target/menu.)
        avatarIk.GenerateBodyColliders();
        var avatarGrab = avatar.GetComponent<Grabbable>() ?? avatar.AttachComponent<Grabbable>();
        avatarGrab.BlockWhenWorn.Value = true;

        if (SetupEyes.Value && (rig.TryGetBone(BodyNode.LeftEye) != null || rig.TryGetBone(BodyNode.RightEye) != null))
        {
            if (avatar.GetComponent<BlinkDriver>() == null)
                avatar.AttachComponent<BlinkDriver>();
            if (avatar.GetComponent<EyeGazeDriver>() == null)
                avatar.AttachComponent<EyeGazeDriver>();
            // Pupil dilation (procedural + tracked) plus widen/squint/eye-frown.
            if (avatar.GetComponent<EyeExpressionDriver>() == null)
                avatar.AttachComponent<EyeExpressionDriver>();
        }

        // Mouth/lip blendshapes driven from replicated face tracking (rests when nothing is tracking).
        if (avatar.GetComponent<MouthExpressionDriver>() == null)
            avatar.AttachComponent<MouthExpressionDriver>();

        if (avatar.GetComponent<LipSyncAnalyzer>() == null)
            avatar.AttachComponent<LipSyncAnalyzer>();
        if (avatar.GetComponent<VisemeWeightDriver>() == null)
            avatar.AttachComponent<VisemeWeightDriver>();
        if (avatar.GetComponent<BreathingDriver>() == null)
            avatar.AttachComponent<BreathingDriver>();

        (avatar.GetComponent<AvatarForm>() ?? avatar.AttachComponent<AvatarForm>()).EnsureEquipTarget();
        LumoraLogger.Log($"AvatarStudio: created avatar '{avatar.SlotName.Value}' - click it to equip");
        Slot.Destroy();
    }

    private bool BuildReferencesFromMarkers(Slot avatar)
    {
        if (avatar == null || avatar.IsDestroyed || _headProxy.Target == null || _headProxy.Target.IsDestroyed)
            return false;

        var existing = avatar.FindChild("AvatarReferences", recursive: false);
        if (existing != null && !existing.IsDestroyed)
            existing.Destroy();

        var root = avatar.AddSlot("AvatarReferences");
        root.LocalPosition.Value = float3.Zero;
        root.LocalRotation.Value = floatQ.Identity;

        PlaceReferenceFromMarker(root, avatar, _headProxy.Target, AvatarReferenceKind.View, "View");
        PlaceReferenceFromMarker(root, avatar, _leftHandProxy.Target, AvatarReferenceKind.LeftHandGrip, "LeftHandGrip");
        PlaceReferenceFromMarker(root, avatar, _rightHandProxy.Target, AvatarReferenceKind.RightHandGrip, "RightHandGrip");
        if (CalibrateFeet.Value)
        {
            PlaceReferenceFromMarker(root, avatar, _leftFootProxy.Target, AvatarReferenceKind.LeftFoot, "LeftFoot");
            PlaceReferenceFromMarker(root, avatar, _rightFootProxy.Target, AvatarReferenceKind.RightFoot, "RightFoot");
        }
        if (CalibratePelvis.Value)
            PlaceReferenceFromMarker(root, avatar, _pelvisProxy.Target, AvatarReferenceKind.Pelvis, "Pelvis");

        // Tool anchors bake unconditionally on a biped, whether or not "show tool anchors" was ever
        // ticked. They spawn at usable default offsets from the grip, so an avatar built by someone who
        // never opened that toggle still ships anchors sitting where the source platform's defaults put
        // them - the alternative is shipping nothing and having the hand rig fall back to the wrist.
        if (!QuadrupedMode.Value)
        {
            PlaceReferenceFromMarker(root, avatar, _leftToolAnchorProxy.Target, AvatarReferenceKind.LeftHandToolAnchor, "LeftHandToolAnchor");
            PlaceReferenceFromMarker(root, avatar, _rightToolAnchorProxy.Target, AvatarReferenceKind.RightHandToolAnchor, "RightHandToolAnchor");
            PlaceReferenceFromMarker(root, avatar, _leftGrabAnchorProxy.Target, AvatarReferenceKind.LeftHandGrabAnchor, "LeftHandGrabAnchor");
            PlaceReferenceFromMarker(root, avatar, _rightGrabAnchorProxy.Target, AvatarReferenceKind.RightHandGrabAnchor, "RightHandGrabAnchor");
        }

        // The four paw markers were built, shown, and then thrown away.
        //
        // Quadruped mode spawns FrontLeftPaw through RearRightPaw and lets you drag each one onto the
        // animal, and none of them were ever read: Create placed head, hands, feet and pelvis and
        // returned. So the whole point of lining a quadruped up by hand produced nothing, and the paws
        // fell back to whatever the bone classifier had guessed. Only placed in quadruped mode, so a
        // biped avatar's reference set is unchanged. -xlinka
        if (QuadrupedMode.Value)
        {
            PlaceReferenceFromMarker(root, avatar, _frontLeftPawProxy.Target, AvatarReferenceKind.FrontLeftPaw, "FrontLeftPaw");
            PlaceReferenceFromMarker(root, avatar, _frontRightPawProxy.Target, AvatarReferenceKind.FrontRightPaw, "FrontRightPaw");
            PlaceReferenceFromMarker(root, avatar, _rearLeftPawProxy.Target, AvatarReferenceKind.RearLeftPaw, "RearLeftPaw");
            PlaceReferenceFromMarker(root, avatar, _rearRightPawProxy.Target, AvatarReferenceKind.RearRightPaw, "RearRightPaw");
        }

        return true;
    }

    private static void PlaceReferenceFromMarker(Slot referenceRoot, Slot avatar, Slot marker, AvatarReferenceKind kind, string name)
    {
        if (referenceRoot == null || avatar == null || marker == null || marker.IsDestroyed)
            return;

        var slot = referenceRoot.AddSlot(name);
        slot.LocalPosition.Value = avatar.GlobalPointToLocal(marker.GlobalPosition);
        slot.LocalRotation.Value = avatar.GlobalRotation.Inverse * marker.GlobalRotation;
        slot.AttachComponent<AvatarReferencePoint>().Kind.Value = kind;
    }

    // Find the rig the markers are lined up over. A transform scan, NOT a physics overlap: the
    // collision query backend (Godot/Jolt) only sees registered bodies, and an imported avatar's bone
    // colliders aren't necessarily in it - so we match bone slot positions directly. Any skinned model
    // gets a HumanoidRig built on demand (a plain import has a SkeletonBuilder but no rig), so Create works
    // on any imported humanoid, not just avatar-flagged ones.
    private HumanoidRig FindRig()
    {
        var world = World;
        if (world?.RootSlot == null)
            return null!;

        var rigs = new List<HumanoidRig>();
        foreach (var skeleton in new List<SkeletonBuilder>(world.RootSlot.GetComponentsInChildren<SkeletonBuilder>()))
        {
            if (!IsCandidate(skeleton?.Slot))
                continue;
            var rig = EnsureRig(skeleton!);
            if (rig != null && !rigs.Contains(rig))
                rigs.Add(rig);
        }
        foreach (var rig in new List<HumanoidRig>(world.RootSlot.GetComponentsInChildren<HumanoidRig>()))
        {
            if (rig == null || rig.IsDestroyed || !IsCandidate(rig.Slot))
                continue;
            if (!rigs.Contains(rig))
                rigs.Add(rig);
        }

        if (rigs.Count == 0)
        {
            LumoraLogger.Warn("AvatarStudio: no rigged/skinned model in the world - import a humanoid model first");
            return null!;
        }

        HumanoidRig? best = null;
        int bestScore = 0;
        foreach (var rig in rigs)
        {
            int score = ScoreRig(rig);
            if (score > bestScore)
            {
                bestScore = score;
                best = rig;
            }
        }
        if (best != null)
            return best;

        // Nothing lined up under the markers, but there's exactly one candidate - the user clearly
        // spawned the creator to set up the one avatar present, so use it.
        if (rigs.Count == 1)
            return rigs[0];

        LumoraLogger.Warn("AvatarStudio: markers aren't over any of the rigged models - line them up over the avatar");
        return null!;
    }

    private bool IsCandidate(Slot? slot)
        => slot != null && !slot.IsDestroyed && !slot.IsDescendantOf(Slot) && slot.ActiveUserRoot == null;

    // Get the model's rig, building one from its skeleton if it doesn't have one yet (mirrors what the
    // avatar-import path does). Returns null if no usable bones could be mapped.
    private static HumanoidRig? EnsureRig(SkeletonBuilder skeleton)
    {
        var rig = skeleton.Slot.GetComponent<HumanoidRig>();
        if (rig == null)
        {
            rig = skeleton.Slot.AttachComponent<HumanoidRig>();
            rig.PopulateFromSkeleton(skeleton);
            LumoraLogger.Log($"AvatarStudio: built a HumanoidRig from '{skeleton.Slot.SlotName.Value}' (IsHumanoid={rig.IsHumanoid}, {rig.Bones.Count} bones)");
        }
        return rig.Bones.Count > 0 ? rig : null;
    }

    // +1 per marker sitting over its matching bone. Feet/pelvis only count when their toggle is on.
    private int ScoreRig(HumanoidRig rig)
    {
        int score = 0;
        score += MarkerOverBone(_headProxy.Target, rig.TryGetBone(BodyNode.Head), HeadDetectionRadius) ? 1 : 0;
        score += MarkerOverBone(_leftHandProxy.Target, rig.TryGetBone(BodyNode.LeftHand), LimbDetectionRadius) ? 1 : 0;
        score += MarkerOverBone(_rightHandProxy.Target, rig.TryGetBone(BodyNode.RightHand), LimbDetectionRadius) ? 1 : 0;
        if (CalibrateFeet.Value)
        {
            score += MarkerOverBone(_leftFootProxy.Target, rig.TryGetBone(BodyNode.LeftFoot), LimbDetectionRadius) ? 1 : 0;
            score += MarkerOverBone(_rightFootProxy.Target, rig.TryGetBone(BodyNode.RightFoot), LimbDetectionRadius) ? 1 : 0;
        }
        if (CalibratePelvis.Value)
            score += MarkerOverBone(_pelvisProxy.Target, rig.TryGetBone(BodyNode.Hips), LimbDetectionRadius) ? 1 : 0;
        return score;
    }

    // The detection radius tracks the visible halo, which scales with the marker (so scaling the figure
    // up keeps the cue and the test in sync).
    private static bool MarkerOverBone(Slot marker, Slot bone, float radius)
    {
        if (marker == null || marker.IsDestroyed || bone == null || bone.IsDestroyed)
            return false;
        float scaled = radius * MathF.Max(marker.GlobalScale.x, 0.0001f);
        return (marker.GlobalPosition - bone.GlobalPosition).LengthSquared <= scaled * scaled;
    }

    // Walk up from the rig to the model's logical root: the highest ObjectRoot ancestor, or failing
    // that the topmost non-world ancestor (the import root). That's where the avatar runtime lives.
    private static Slot ResolveObjectRoot(Slot from)
    {
        Slot best = from;
        Slot withRoot = null!;
        for (var s = from; s != null && !s.IsRootSlot; s = s.Parent)
        {
            best = s;
            if (s.GetComponent<ObjectRoot>() != null)
                withRoot = s;
        }
        return withRoot ?? best;
    }

    private static Slot ResolveAvatarRoot(HumanoidRig rig)
    {
        if (rig == null || rig.IsDestroyed)
            return null!;
        var avatar = ResolveObjectRoot(rig.Slot);
        var existingIk = avatar.GetComponentInChildren<AvatarIK>();
        return existingIk != null && !existingIk.IsDestroyed ? existingIk.Slot : avatar;
    }

}
