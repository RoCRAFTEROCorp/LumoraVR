// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Components.Gizmos;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Interaction;

[ComponentCategory("Interaction/Tools")]
public sealed class DevToolItem : ToolItem, ILaserHitClassifier
{
    public enum SelectionMode
    {
        Single,
        Multi
    }

    // marks a slot as editing chrome: the hit filter pulls it in front of the world
    private const string DeveloperTag = "Developer";

    public readonly Sync<SelectionMode> Selection = new();
    public readonly SyncRef<Slot> SelectedSlot = new();
    public readonly SyncRef<Slot> CurrentGizmoSlot = new();

    private TransformHandle? _activeHandle;
    private Slot? _visualSlot;
    private OverlayFresnelMaterial? _visualMaterial;

    public override Slot? HeldVisual => _visualSlot;

    public override bool UsesLaser => true;

    public override bool UsesSecondary => true;

    protected override float3 DefaultLocalTip => float3.Backward * 0.075f;

    public override void OnInit()
    {
        base.OnInit();
        Selection.Value = SelectionMode.Single;
        EquipName.Value = "Dev Tool";
    }

    public override void OnStart()
    {
        base.OnStart();
        // Tag as a developer/editing tool so it's identifiable and excluded from social spaces.
        if (Slot != null && string.IsNullOrEmpty(Slot.Tag.Value))
            Slot.Tag.Value = DeveloperTag;
        EnsureVisual();

        // Gizmo mode items (Translate/Rotate/Scale/space) ride the radial menu while this tool is in a
        // hand - the menu collector only scans the user hierarchy, so equip state gates them for free.
        if (Slot != null)
        {
            var menuSource = Slot.GetComponent<GizmoModeMenuSource>() ?? Slot.AttachComponent<GizmoModeMenuSource>();
            menuSource.Tool.Target = this;
        }
    }

    // Editing is disabled in Social/Event worlds. This is the UX gate (don't place dead gizmos); the
    // hard lock is the host-authoritative permission floor, which denies the edits regardless.
    private bool EditingDisabled => World != null && !World.AllowsWorldEditing;

    // The dev tool IS the dev gear the ToolUse domain is about, so it answers the same question the
    // build tools do: no right to use tools, no picking this one up and no press once it is in hand.
    public override bool AllowsEquip(User? user)
        => World?.DataModelPermissions?.AllowsDomain(user, DataModelPermissionDomain.ToolUse) != false;

    // HIT FILTER: while this tool is the one pointing, editor chrome comes forward through whatever is
    // in front of it. The handles ask for that themselves (a bare hand can drag them with no tool
    // equipped), so what this adds is the rest of the rig - the base gizmo interaction shapes and
    // anything wearing the Developer tag - which have ordinary colliders and would otherwise be
    // occluded by the object they are attached to. Nothing is dropped: a tool that needs to ignore
    // hits returns Ignore here and the laser skips them entirely. -xlinka
    public LaserHitClass ClassifyLaserHit(InteractionLaser laser, Slot hitSlot, IInteractionTarget target)
    {
        if (EditingDisabled || hitSlot == null || hitSlot.IsDestroyed)
            return LaserHitClass.Allow;
        if (target is TransformHandle)
            return LaserHitClass.Prefer;
        if (hitSlot.GetComponentInParents<SlotGizmo>() != null
            || hitSlot.GetComponentInParents<ComponentGizmo>() != null)
            return LaserHitClass.Prefer;
        if (hitSlot.Tag.Value == DeveloperTag)
            return LaserHitClass.Prefer;
        return LaserHitClass.Allow;
    }

    public override bool OnPrimaryPress()
    {
        if (EditingDisabled)
        {
            return false;
        }

        var laser = ActiveTool?.Laser;
        var hitSlot = laser?.CurrentHitSlot;
        if (laser == null || hitSlot == null)
        {
            return false;
        }

        // Manipulation handles (arrows/rings/cubes) take the tool's primary: press starts a ray-driven
        // drag session on the handle itself, which then self-updates from the laser every frame. -xlinka
        var handle = hitSlot.GetComponentInParents<TransformHandle>();
        if (handle != null)
        {
            if (handle.BeginToolDrag(laser))
            {
                _activeHandle = handle;
                return true;
            }
            LumoraLogger.Debug($"DevToolItem: handle '{handle.Slot?.SlotName.Value}' refused the drag (dragging={handle.IsDragging}, target={(handle.TargetSlot.Target == null ? "null" : handle.TargetSlot.Target.IsDestroyed ? "destroyed" : "ok")})");
        }

        return false;
    }

    public override bool OnPrimaryHold()
    {
        if (_activeHandle != null)
        {
            // The handle consumes its laser directly in OnUpdate; holding just keeps the claim alive.
            if (_activeHandle.IsDragging && !_activeHandle.IsDestroyed)
            {
                return true;
            }
            _activeHandle = null;
        }

        return false;
    }

    public override bool OnPrimaryRelease()
    {
        if (_activeHandle != null)
        {
            _activeHandle.EndToolDrag();
            _activeHandle = null;
            return true;
        }

        return false;
    }

    public override bool OnSecondaryPress()
    {
        if (EditingDisabled)
        {
            return false;
        }

        DropStaleSelection();

        var laser = ActiveTool?.Laser;
        var target = ResolveSelectableSlot(laser?.CurrentHitSlot);
        if (target == null)
        {
            return false;
        }

        if (Selection.Value == SelectionMode.Single && SelectedSlot.Target != null && !ReferenceEquals(SelectedSlot.Target, target))
        {
            GizmoHelper.DestroyGizmo(SelectedSlot.Target);
        }

        var gizmo = GizmoHelper.ToggleGizmo(target);
        SelectedSlot.Target = gizmo == null ? null! : target!;
        CurrentGizmoSlot.Target = (gizmo as Component)?.Slot!;
        return true;
    }

    // called whenever the gizmo behind it went away, so the tool never holds a selection with nothing
    // on screen to back it
    public void ClearSelection()
    {
        SelectedSlot.Target = null!;
        CurrentGizmoSlot.Target = null!;
    }

    public void DeselectAll()
    {
        GizmoHelper.DeselectAll(World);
        ClearSelection();
    }

    public void DeselectLocal()
    {
        GizmoHelper.DeselectLocal(World);
        ClearSelection();
    }

    // The selection is only real while its gizmo is: the gizmo can go without this tool asking (a
    // deselect from the radial menu, the inspector letting go, the slot being deleted). CurrentGizmoSlot
    // is not the test - a dismissed rig keeps its slot so the next selection can reuse it.
    private void DropStaleSelection()
    {
        var selected = SelectedSlot.Target;
        if (selected == null)
            return;
        if (selected.IsDestroyed || !GizmoHelper.HasGizmo(selected))
            ClearSelection();
    }

    public override void OnDequipped()
    {
        _activeHandle?.EndToolDrag();
        _activeHandle = null;
    }

    private Slot? ResolveSelectableSlot(Slot? hitSlot)
    {
        if (hitSlot == null || hitSlot.IsRootSlot)
        {
            return null;
        }

        var slotGizmo = hitSlot.GetComponentInParents<SlotGizmo>();
        if (slotGizmo?.TargetSlot != null)
        {
            return slotGizmo.TargetSlot;
        }

        // A component gizmo's chrome stands for the object it annotates: pointing at a collider's
        // wireframe and pressing select has to pick the collider's slot, not the gizmo's.
        var componentGizmo = hitSlot.GetComponentInParents<ComponentGizmo>();
        if (componentGizmo?.TargetComponent?.Slot is { IsDestroyed: false } annotated)
        {
            return annotated;
        }

        if (IsInActiveToolHierarchy(hitSlot))
        {
            return null;
        }

        // Same answer as pointing at the sky: no selection, so no gizmo is minted over something the
        // person cannot edit anyway.
        if (ImmutableComponent.IsProtected(hitSlot))
        {
            return null;
        }

        return hitSlot;
    }

    private bool IsInActiveToolHierarchy(Slot slot)
    {
        var toolSlot = ActiveTool?.Slot;
        var current = slot;
        while (current != null)
        {
            if (ReferenceEquals(current, toolSlot) || ReferenceEquals(current, Slot))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private void EnsureVisual()
    {
        if (_visualSlot != null && !_visualSlot.IsRemoved)
        {
            return;
        }

        _visualSlot = Slot.FindChild("Visual", recursive: false) ?? Slot.AddSlot("Visual");
        _visualSlot.LocalPosition.Value = float3.Backward * 0.05f;
        // The cone's apex points along +Y; rotate so it points down the tool's tip direction
        // (Backward), so the dev tip reads as a forward-facing pointer. AxisAngle is RADIANS.
        _visualSlot.LocalRotation.Value = floatQ.AxisAngle(float3.Right, -System.MathF.PI * 0.5f);
        _visualSlot.LocalScale.Value = float3.One;

        // The source platform's dev tool: a 5cm truncated cone, 5cm ahead of the tool root, drawn in an
        // OVERLAY fresnel so it stays visible through the hand holding it and whatever it is pointed
        // into. Ours was a plain unlit alpha cone in the ordinary pass, which the paw wrapped round it
        // covered up: in the front-view screenshot it survived as one green pixel-dot at the heel of the
        // palm and read as "no tool at all". Sizes and the black-to-red fresnel are theirs (red is what
        // their users see; green is their dev-mode tint). -xlinka
        var cone = _visualSlot.GetComponent<ConeMesh>() ?? _visualSlot.AttachComponent<ConeMesh>();
        cone.RadiusBase.Value = 0.015f;
        cone.RadiusTop.Value = 0.0025f;
        cone.Height.Value = 0.05f;
        cone.Segments.Value = 16;

        _visualMaterial = _visualSlot.GetComponent<OverlayFresnelMaterial>() ?? _visualSlot.AttachComponent<OverlayFresnelMaterial>();
        _visualMaterial.Exponent.Value = 1f;
        _visualMaterial.FrontNearColor.Value = new colorHDR(0f, 0f, 0f, 1f);
        _visualMaterial.FrontFarColor.Value = new colorHDR(1f, 0f, 0f, 1f);
        _visualMaterial.BehindNearColor.Value = new colorHDR(0f, 0f, 0f, 0.25f);
        _visualMaterial.BehindFarColor.Value = new colorHDR(0.8f, 0f, 0f, 0.25f);

        var renderer = _visualSlot.GetComponent<MeshRenderer>() ?? _visualSlot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = cone;
        renderer.Material.Target = _visualMaterial;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }
}
