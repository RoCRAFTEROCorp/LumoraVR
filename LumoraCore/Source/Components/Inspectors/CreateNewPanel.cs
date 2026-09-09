// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// The "Create New" browser: walk a category tree, press a leaf, get the thing in front of you.
//
// Structurally the same panel as the component selector, for the same reason it is built that way -
// rows carry their action in a SYNCED string argument rather than a closure, so a press works for
// every user rather than only the one whose client happened to build the row.
//
// The catalog itself lives in CreateNewCatalog; this is only the browser over it. -xlinka
[ComponentCategory("Utility/Inspectors")]
public sealed class CreateNewPanel : Component, IInspectorActionHandler
{
    public readonly Sync<string> CategoryPath;

    private readonly SyncRef<Slot> _listContent;
    private readonly Sync<string> _builtPath;

    private const float RowHeight = 30f;

    public CreateNewPanel()
    {
        CategoryPath = new Sync<string>(this, "");
        _listContent = new SyncRef<Slot>(this);
        _builtPath = new Sync<string>(this, "unbuilt");
    }

    // Placement copies the scene inspector's: a metre out along the head's flattened forward, a little
    // below eye line, then yawed back to face the user. Never LookRotation, which returns the inverse.
    public static CreateNewPanel Spawn(World world)
    {
        if (world?.RootSlot == null)
            return null!;

        var panelSlot = world.RootSlot.AddSlot("Create New");
        panelSlot.Persistent.Value = false;
        panelSlot.Tag.Value = "Developer";

        var head = world.LocalUser?.Root?.HeadSlot;
        if (head != null && !head.IsDestroyed)
        {
            float3 forward = head.GlobalRotation * float3.Backward;
            forward.y = 0f;
            forward = forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;

            panelSlot.GlobalPosition = head.GlobalPosition + forward * 1.0f + new float3(0f, -0.12f, 0f);
            panelSlot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(-forward.x, -forward.z));
        }

        return panelSlot.AttachComponent<CreateNewPanel>();
    }

    public override void OnAttach()
    {
        base.OnAttach();

        var theme = Slot.GetOrAttachComponent<UITheme>();
        theme.PanelBackground.Value = new color(0.075f, 0.07f, 0.115f, 1f);
        theme.Header.Value = new color(0.11f, 0.10f, 0.17f, 1f);
        theme.ButtonFill.Value = new color(0.22f, 0.20f, 0.34f, 1f);
        theme.Accent.Value = InspectorUI.AccentColor;
        theme.Separator.Value = new color(0.52f, 0.46f, 0.82f, 0.6f);
        theme.Border.Value = new color(0.52f, 0.46f, 0.82f, 0.45f);

        var shell = Slot.GetOrAttachComponent<PanelShell>();
        shell.Title.Value = "Create New";
        shell.Size.Value = new float2(520f, 900f);
        theme.ApplyTo(shell);
        shell.RebuildContent(BuildLayout);
    }

    private void BuildLayout(UIBuilder ui)
    {
        var page = ui.Current;
        InspectorUI.ApplyTheme(ui, Slot);

        var vLayout = page.AttachComponent<VerticalLayout>();
        vLayout.Spacing.Value = 6f;
        vLayout.PaddingLeft.Value = 6f;
        vLayout.PaddingRight.Value = 6f;
        vLayout.PaddingTop.Value = 6f;
        vLayout.PaddingBottom.Value = 6f;
        vLayout.ForceExpandWidth.Value = true;
        vLayout.ForceExpandHeight.Value = false;

        var host = page.AddSlot("List");
        host.AttachComponent<RectTransform>();
        var hostLE = host.AttachComponent<LayoutElement>();
        hostLE.FlexibleHeight.Value = 1f;
        hostLE.MinHeight.Value = 200f;

        var scrollUi = new UIBuilder(host);
        InspectorUI.ApplyTheme(scrollUi, Slot);
        var scroll = scrollUi.ScrollRect(out var content, null, InspectorUI.PaneColor);
        InspectorUI.FillParent(scroll.Slot.GetComponent<RectTransform>()!);

        var contentLayout = content.Slot.AttachComponent<VerticalLayout>();
        contentLayout.Spacing.Value = 2f;
        contentLayout.ForceExpandWidth.Value = true;
        contentLayout.ForceExpandHeight.Value = false;
        _listContent.Target = content.Slot;
    }

    public override void OnChanges()
    {
        base.OnChanges();
        if (World?.IsAuthority != true)
            return;
        if (_builtPath.Value == CategoryPath.Value)
            return;
        _builtPath.Value = CategoryPath.Value;
        RebuildList();
    }

    private void RebuildList()
    {
        var container = _listContent.Target;
        if (container == null || container.IsDestroyed)
            return;
        container.DestroyChildren();

        var node = CreateNewCatalog.GetNode(CategoryPath.Value) ?? CreateNewCatalog.Root;

        if (!string.IsNullOrEmpty(CategoryPath.Value))
            AddRow(container, "< Back", "back:", InspectorUI.MutedColor);

        foreach (var sub in node.Subcategories.Values)
            AddRow(container, sub.Name + " >", "cat:" + sub.Path, InspectorUI.AccentColor);

        // Index rather than name, so two entries could share a label without colliding.
        for (int i = 0; i < node.Entries.Count; i++)
            AddRow(container, node.Entries[i].Name, "make:" + i, InspectorUI.TextColor);
    }

    private void AddRow(Slot container, string label, string action, color tint)
    {
        InspectorUI.FixedRow(container, label, RowHeight, out var ui, Slot);
        ui.PushStyle();
        ui.TextColor(tint);
        var button = InspectorUI.RelayButton(ui, this, action, label, 0f);
        ui.PopStyle();
    }

    public void HandleInspectorAction(string action)
    {
        if (action.StartsWith("back:", StringComparison.Ordinal))
        {
            var path = CategoryPath.Value ?? "";
            int slash = path.LastIndexOf('/');
            CategoryPath.Value = slash > 0 ? path[..slash] : "";
            return;
        }

        if (action.StartsWith("cat:", StringComparison.Ordinal))
        {
            CategoryPath.Value = action[4..];
            return;
        }

        if (action.StartsWith("make:", StringComparison.Ordinal))
        {
            if (int.TryParse(action[5..], out int index))
                Build(index);
        }
    }

    private void Build(int index)
    {
        var world = World;
        var node = CreateNewCatalog.GetNode(CategoryPath.Value) ?? CreateNewCatalog.Root;
        if (world?.RootSlot == null || index < 0 || index >= node.Entries.Count)
            return;

        var entry = node.Entries[index];
        var slot = world.RootSlot.AddSlot(entry.Name);
        if (slot == null)
            return;

        // In front of the user at chest height, not at the world origin. Same head-relative math the
        // panel itself uses to place itself.
        var head = world.LocalUser?.Root?.HeadSlot;
        if (head != null && !head.IsDestroyed)
        {
            float3 forward = head.GlobalRotation * float3.Backward;
            forward.y = 0f;
            forward = forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;
            slot.GlobalPosition = head.GlobalPosition + forward * 0.8f + new float3(0f, -0.3f, 0f);
        }

        try
        {
            entry.Build(slot);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"CreateNewPanel: '{entry.Name}' failed to build - {ex.Message}");
            slot.Destroy();
            return;
        }

        // Spawning is an undoable action like any other creation, so it joins the same stack rather
        // than being the one way to add things to a world that cannot be taken back.
        InspectorUndo.Record(this, SlotExistenceUndoBatch.Created(world, new[] { slot }, UndoLocale.SpawnShape));
    }
}
