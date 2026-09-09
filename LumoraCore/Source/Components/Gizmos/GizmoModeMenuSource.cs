// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.UI;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components.Gizmos;

// lives on the tool item's slot so the menu only collects it while the tool is actually in a hand
// (the collector scans the user hierarchy); the active mode's item is tinted so the current state
// reads at a glance
[ComponentCategory("Utility/Gizmos")]
public class GizmoModeMenuSource : ContextMenuItemSource
{
    public readonly SyncRef<Interaction.DevToolItem> Tool;

    public const string ToolSubmenuLabel = "Tool Actions";
    public static readonly float[] ToolSubmenuFill = { 0.20f, 0.17f, 0.30f, 0.92f };
    private static readonly float[] ItemFill = { 0.16f, 0.15f, 0.24f, 0.92f };
    private static readonly float[] ActiveFill = { 0.35f, 0.28f, 0.62f, 0.95f };
    private static readonly float[] ClearFill = { 0.42f, 0.16f, 0.18f, 0.94f };

    public GizmoModeMenuSource()
    {
        Tool = new SyncRef<Interaction.DevToolItem>(this);
    }

    public override void PopulateContextMenu(ContextMenuPage page, ContextMenuContext context)
    {
        // The mode items need a slot gizmo; the deselect items do not. A user with only component
        // gizmos up still has something to clear, and gating the whole menu on a slot gizmo left them
        // with no way to do it. -xlinka
        var gizmo = ResolveGizmo();
        if (gizmo == null && !GizmoHelper.HasLocalGizmos(World) && GizmoHelper.AnyGizmo(World) == null)
            return;

        // Everything tool-related lives one level down under a single root entry; mode switches and
        // deselects next to Undo/Locomotion/Inspector turned the root ring into a wall of slices.
        page = page.GetOrAddSubPage(ToolSubmenuLabel, ToolSubmenuFill);

        // The spawn catalog. Sits on the dev tool's menu because that is where it lives on the source
        // platform, and because the dev tool is the one you already have out when you are building.
        page.AddItem(new ContextMenuItem
        {
            Label = "Create New",
            FillColor = ActiveFill,
            OnPressed = _ => OpenCreateNew(),
        });
        page.AddItem(new ContextMenuItem
        {
            Label = "Reflection Probes",
            FillColor = ItemFill,
            OnPressed = _ => OpenProbeWizard(),
        });
        page.AddItem(new ContextMenuItem
        {
            Label = "Lighting",
            FillColor = ItemFill,
            OnPressed = _ => OpenLightingWizard(),
        });

        if (gizmo != null)
        {
            int mode = gizmo.ActiveMode.Value;
            page.AddItem(new ContextMenuItem
            {
                Label = "Translate",
                FillColor = mode == 0 ? ActiveFill : ItemFill,
                OnPressed = _ => { if (!gizmo.IsDestroyed) gizmo.SwitchToTranslation(); },
            });
            page.AddItem(new ContextMenuItem
            {
                Label = "Rotate",
                FillColor = mode == 1 ? ActiveFill : ItemFill,
                OnPressed = _ => { if (!gizmo.IsDestroyed) gizmo.SwitchToRotation(); },
            });
            page.AddItem(new ContextMenuItem
            {
                Label = "Scale",
                FillColor = mode == 2 ? ActiveFill : ItemFill,
                OnPressed = _ => { if (!gizmo.IsDestroyed) gizmo.SwitchToScale(); },
            });
            page.AddItem(new ContextMenuItem
            {
                Label = gizmo.IsLocalSpace.Value ? "Space: Local" : "Space: Global",
                FillColor = ItemFill,
                OnPressed = _ => { if (!gizmo.IsDestroyed) gizmo.ToggleSpace(); },
            });
        }

        // Deselect Local only appears when there is something of YOURS to clear; without that check the
        // item sits there doing nothing while somebody else's selection is the only one in the room.
        var tool = Tool.Target;
        if (GizmoHelper.HasLocalGizmos(World))
        {
            page.AddItem(new ContextMenuItem
            {
                Label = "Deselect Local",
                FillColor = ClearFill,
                OnPressed = _ =>
                {
                    GizmoHelper.DeselectLocal(World);
                    if (tool is { IsDestroyed: false })
                        tool.ClearSelection();
                },
            });
        }

        page.AddItem(new ContextMenuItem
        {
            Label = "Deselect All",
            FillColor = ClearFill,
            OnPressed = _ =>
            {
                GizmoHelper.DeselectAll(World);
                if (tool is { IsDestroyed: false })
                    tool.ClearSelection();
            },
        });
    }

    // The tool's own selection wins; otherwise fall back to any live gizmo (the inspector spawns one
    // on tree selection), so the menu works no matter which flow put the gizmo up.
    private SlotGizmo? ResolveGizmo()
    {
        var selected = Tool.Target?.SelectedSlot.Target;
        if (selected != null && !selected.IsDestroyed)
        {
            var owned = GizmoHelper.GetGizmo(selected);
            if (owned != null && !owned.IsDestroyed)
                return owned;
        }
        return GizmoHelper.AnyGizmo(World);
    }

    // One panel per user at a time: pressing again focuses the one already up rather than stacking a
    // second, which is the same rule the scene inspector had to grow.
    private void OpenCreateNew()
    {
        var world = World;
        if (world?.RootSlot == null)
            return;

        var existing = world.RootSlot.GetComponentInChildren<CreateNewPanel>();
        if (existing != null && !existing.IsDestroyed)
        {
            existing.Slot.Destroy();
            return;
        }

        CreateNewPanel.Spawn(world);
    }

    private void OpenProbeWizard()
    {
        var world = World;
        if (world?.RootSlot == null)
            return;

        var existing = world.RootSlot.GetComponentInChildren<ReflectionProbeWizard>();
        if (existing != null && !existing.IsDestroyed)
        {
            existing.Slot.Destroy();
            return;
        }

        ReflectionProbeWizard.Spawn(world);
    }

    private void OpenLightingWizard()
    {
        var world = World;
        if (world?.RootSlot == null)
            return;

        var existing = world.RootSlot.GetComponentInChildren<LightingWizard>();
        if (existing != null && !existing.IsDestroyed)
        {
            existing.Slot.Destroy();
            return;
        }

        LightingWizard.Spawn(world);
    }
}
