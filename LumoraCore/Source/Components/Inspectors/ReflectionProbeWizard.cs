// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Gizmos;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using Lumora.Core.Assets;

namespace Lumora.Core.Components;

// Everything about the reflection probes in a world, in one panel.
//
// Modelled on the source platform's probe wizard, which does four things: collect probes under a root,
// filter them, bake them one at a time with a delay, and toggle a debug visual. This does those and
// then the things it does not: it tells you whether anything global is switched off before you blame a
// probe, it audits the world for the specific mistakes that make a probe useless, it can drop a chrome
// test sphere so "is this probe working" is a one-second yes or no rather than a guess, and it can
// edit a probe's parameters, which theirs cannot do at all.
//
// The audit list is not generic advice. Every check in it is a failure mode that was found in THIS
// codebase while working out why probes looked broken. -xlinka
[ComponentCategory("Utility/Inspectors")]
public sealed class ReflectionProbeWizard : Component, IInspectorActionHandler
{
    public readonly SyncRef<Slot> Root;
    public readonly Sync<bool> IncludeDisabled;
    public readonly Sync<int> SelectedIndex;

    private readonly SyncRef<Slot> _listContent;
    private readonly Sync<int> _revision;
    private readonly Sync<int> _builtRevision;

    private const float RowHeight = 28f;
    private const string TestSphereName = "ProbeTestSphere";
    private const int TempLayer = 1 << 2;

    private readonly List<ReflectionProbe> _probes = new();
    private readonly List<string> _audit = new();

    public ReflectionProbeWizard()
    {
        Root = new SyncRef<Slot>(this);
        IncludeDisabled = new Sync<bool>(this, false);
        SelectedIndex = new Sync<int>(this, -1);
        _listContent = new SyncRef<Slot>(this);
        _revision = new Sync<int>(this, 0);
        _builtRevision = new Sync<int>(this, -1);
    }

    public static ReflectionProbeWizard Spawn(World world)
    {
        if (world?.RootSlot == null)
            return null!;

        var panelSlot = world.RootSlot.AddSlot("Reflection Probes");
        panelSlot.Persistent.Value = false;
        panelSlot.Tag.Value = "Developer";

        var head = world.LocalUser?.Root?.HeadSlot;
        if (head != null && !head.IsDestroyed)
        {
            float3 forward = head.GlobalRotation * float3.Backward;
            forward.y = 0f;
            forward = forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;
            panelSlot.GlobalPosition = head.GlobalPosition + forward * 1.0f + new float3(0f, -0.1f, 0f);
            panelSlot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(-forward.x, -forward.z));
        }

        var wizard = panelSlot.AttachComponent<ReflectionProbeWizard>();
        wizard.Root.Target = world.RootSlot;
        return wizard;
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
        shell.Title.Value = "Reflection Probes";
        shell.Size.Value = new float2(660f, 1000f);
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
        hostLE.MinHeight.Value = 260f;

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
        if (_builtRevision.Value == _revision.Value)
            return;
        _builtRevision.Value = _revision.Value;
        Rebuild();
    }

    private void Dirty() => _revision.Value++;

    // BUILD

    private void Rebuild()
    {
        var container = _listContent.Target;
        if (container == null || container.IsDestroyed)
            return;
        container.DestroyChildren();

        CollectProbes();

        BuildWorldState(container);
        BuildActions(container);
        BuildProbeList(container);
        BuildSelectedDetail(container);
        BuildAudit(container);
    }

    private void CollectProbes()
    {
        _probes.Clear();
        var root = Root.Target ?? World?.RootSlot;
        if (root == null || root.IsDestroyed)
            return;

        foreach (var probe in root.GetComponentsInChildren<ReflectionProbe>())
        {
            if (probe == null || probe.IsDestroyed)
                continue;
            if (!IncludeDisabled.Value && !probe.Enabled.Value)
                continue;
            _probes.Add(probe);
        }
    }

    // Answer "is anything switched off globally" BEFORE the user starts blaming an individual probe.
    // Three of these four were the actual reason probes looked broken, and none of them were visible
    // anywhere in the product.
    private void BuildWorldState(Slot container)
    {
        InspectorUI.SectionHeader(container, "World lighting", Slot);

        AddInfo(container, "Reflections",
            EngineSettings.ReflectionsEnabled ? "on" : "OFF in settings - no probe renders",
            EngineSettings.ReflectionsEnabled ? InspectorUI.TextColor : InspectorUI.DangerColor);

        // The sky is the only other specular source. Every shipped world template uses the gradient
        // skybox, whose hook keeps sky reflections off, so a probe is usually the ONLY reflection in
        // the world and its failure is total rather than partial.
        var world = World;
        bool skyReflects = false;
        string skyLabel = "none";
        if (world?.RootSlot != null)
        {
            var gradient = world.RootSlot.GetComponentInChildren<GradientSkybox>();
            var skybox = world.RootSlot.GetComponentInChildren<Skybox>();
            if (skybox != null)
            {
                skyLabel = "Skybox";
                skyReflects = skybox.ReflectionsFromSky.Value;
            }
            else if (gradient != null)
            {
                skyLabel = "GradientSkybox";
                skyReflects = false;
            }
        }
        AddInfo(container, "Sky", skyLabel + (skyReflects ? " (reflects)" : " (no sky reflection)"),
            skyReflects ? InspectorUI.TextColor : InspectorUI.MutedColor);

        AddInfo(container, "Probes found", _probes.Count.ToString(),
            _probes.Count > 0 ? InspectorUI.TextColor : InspectorUI.MutedColor);

        int always = 0;
        foreach (var probe in _probes)
        {
            if (probe.UpdateMode.Value == ProbeUpdateMode.Always)
                always++;
        }
        AddInfo(container, "Always-update", always == 0 ? "0" : $"{always} (6 renders each, every frame)",
            always == 0 ? InspectorUI.TextColor : InspectorUI.DangerColor);
    }

    private void BuildActions(Slot container)
    {
        InspectorUI.SectionHeader(container, "Actions", Slot);
        AddButton(container, "Bake all probes", "bakeall", InspectorUI.AccentColor);
        AddButton(container, "Show all volumes", "volumes", InspectorUI.TextColor);
        AddButton(container, "Hide all volumes", "novolumes", InspectorUI.TextColor);
        AddButton(container, "Run audit", "audit", InspectorUI.CyanColor);
        AddButton(container, IncludeDisabled.Value ? "Hiding nothing" : "Include disabled", "toggledisabled", InspectorUI.MutedColor);
        AddButton(container, "Remove test spheres", "cleartest", InspectorUI.MutedColor);
    }

    private void BuildProbeList(Slot container)
    {
        InspectorUI.SectionHeader(container, "Probes", Slot);

        if (_probes.Count == 0)
        {
            AddInfo(container, "", "No reflection probes under this root.", InspectorUI.MutedColor);
            return;
        }

        for (int i = 0; i < _probes.Count; i++)
        {
            var probe = _probes[i];
            var size = probe.Size.Value;
            // The reach that actually applies. MaxDistance of 0 means unlimited, which is a trap worth
            // showing rather than leaving the reader to infer.
            float half = MathF.Max(size.x, MathF.Max(size.y, size.z)) * 0.5f;
            string reach = probe.MaxDistance.Value <= 0f ? "unlimited" : $"{probe.MaxDistance.Value:0.#}m";
            string label = $"{(i == SelectedIndex.Value ? "> " : "  ")}{probe.Slot.SlotName.Value}"
                           + $"   {size.x:0.#}x{size.y:0.#}x{size.z:0.#}"
                           + $"   {probe.UpdateMode.Value}"
                           + $"   reach {reach}"
                           + (probe.Interior.Value ? "   interior" : "")
                           + (probe.Enabled.Value ? "" : "   DISABLED");

            AddButton(container, label,
                "sel:" + i,
                i == SelectedIndex.Value ? InspectorUI.AccentColor : InspectorUI.TextColor);
        }
    }

    private void BuildSelectedDetail(Slot container)
    {
        var probe = SelectedProbe();
        if (probe == null)
            return;

        InspectorUI.SectionHeader(container, "Selected: " + probe.Slot.SlotName.Value, Slot);

        AddInfo(container, "Capture at", Describe(probe.Slot.GlobalPosition + probe.OriginOffset.Value), InspectorUI.TextColor);
        AddInfo(container, "Box", Describe(probe.Size.Value), InspectorUI.TextColor);
        AddInfo(container, "Blend", $"{probe.BlendDistance.Value:0.##}m", InspectorUI.TextColor);
        AddInfo(container, "Ambient", probe.AmbientMode.Value.ToString(), InspectorUI.TextColor);

        AddButton(container, "Bake this probe", "bake", InspectorUI.AccentColor);
        AddButton(container, "Toggle its gizmo", "gizmo", InspectorUI.TextColor);
        AddButton(container, "Drop chrome test sphere", "test", InspectorUI.CyanColor);
        AddButton(container, "Refit box to parent bounds", "refit", InspectorUI.TextColor);
        AddButton(container, "Toggle Interior", "interior", InspectorUI.TextColor);
        AddButton(container, "Set update mode Once", "once", InspectorUI.TextColor);
        AddButton(container, "Jump here", "jump", InspectorUI.TextColor);
    }

    private void BuildAudit(Slot container)
    {
        if (_audit.Count == 0)
            return;

        InspectorUI.SectionHeader(container, $"Audit ({_audit.Count})", Slot);
        foreach (var line in _audit)
            AddInfo(container, "", line, InspectorUI.DangerColor);
    }

    // ROWS

    private void AddButton(Slot container, string label, string action, color tint)
    {
        InspectorUI.FixedRow(container, label, RowHeight, out var ui, Slot);
        ui.PushStyle();
        ui.TextColor(tint);
        InspectorUI.RelayButton(ui, this, action, label, 0f);
        ui.PopStyle();
    }

    private void AddInfo(Slot container, string label, string value, color tint)
    {
        InspectorUI.FixedRow(container, label.Length > 0 ? label : value, RowHeight, out var ui, Slot);
        ui.PushStyle();
        ui.TextColor(tint);
        ui.Text(label.Length > 0 ? $"{label}: {value}" : value);
        ui.PopStyle();
    }

    private static string Describe(in float3 v) => $"{v.x:0.##}, {v.y:0.##}, {v.z:0.##}";

    private ReflectionProbe? SelectedProbe()
    {
        int index = SelectedIndex.Value;
        if (index < 0 || index >= _probes.Count)
            return null;
        var probe = _probes[index];
        return probe is { IsDestroyed: false } ? probe : null;
    }

    // ACTIONS

    public void HandleInspectorAction(string action)
    {
        if (action.StartsWith("sel:", StringComparison.Ordinal))
        {
            if (int.TryParse(action[4..], out int index))
                SelectedIndex.Value = index == SelectedIndex.Value ? -1 : index;
            Dirty();
            return;
        }

        switch (action)
        {
            case "bakeall":
                foreach (var probe in _probes)
                {
                    if (probe is { IsDestroyed: false })
                        probe.Rebake();
                }
                Dirty();
                return;

            case "toggledisabled":
                IncludeDisabled.Value = !IncludeDisabled.Value;
                SelectedIndex.Value = -1;
                Dirty();
                return;

            case "volumes":
                foreach (var probe in _probes)
                {
                    if (probe is { IsDestroyed: false })
                        GizmoHelper.SpawnGizmoFor(probe.Slot);
                }
                return;

            case "novolumes":
                foreach (var probe in _probes)
                {
                    if (probe is { IsDestroyed: false })
                        GizmoHelper.DestroyGizmo(probe.Slot);
                }
                return;

            case "audit":
                RunAudit();
                Dirty();
                return;

            case "cleartest":
                ClearTestSpheres();
                return;

            case "bake":
                SelectedProbe()?.Rebake();
                return;

            case "gizmo":
            {
                var probe = SelectedProbe();
                if (probe != null)
                    GizmoHelper.ToggleGizmo(probe.Slot);
                return;
            }

            case "test":
                DropTestSphere();
                return;

            case "refit":
                RefitSelected();
                Dirty();
                return;

            case "interior":
            {
                var probe = SelectedProbe();
                if (probe != null)
                {
                    probe.Interior.Value = !probe.Interior.Value;
                    probe.Rebake();
                }
                Dirty();
                return;
            }

            case "once":
            {
                var probe = SelectedProbe();
                if (probe != null)
                    probe.UpdateMode.Value = ProbeUpdateMode.Once;
                Dirty();
                return;
            }

            case "jump":
                JumpToSelected();
                return;
        }
    }

    // A mirror ball at the probe's own capture point. This is the fastest way to answer "is this probe
    // doing anything" - a rough dielectric shows a probe at about four percent strength through the
    // blurriest mip, which is why a default material looks identical whether the probe works or not.
    private void DropTestSphere()
    {
        var probe = SelectedProbe();
        var world = World;
        if (probe == null || world?.RootSlot == null)
            return;

        var slot = world.RootSlot.AddSlot(TestSphereName);
        slot.Persistent.Value = false;
        slot.Tag.Value = "Developer";
        slot.GlobalPosition = probe.Slot.GlobalPosition + probe.OriginOffset.Value;

        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = new colorHDR(1f, 1f, 1f, 1f);
        material.Metallic.Value = 1f;
        material.Smoothness.Value = 0.95f;

        var mesh = slot.AttachComponent<Meshes.SphereMesh>();
        mesh.Radius.Value = 0.15f;
        mesh.Segments.Value = 32;
        mesh.Rings.Value = 20;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Material.Target = material;
        renderer.Mesh.Target = mesh;

        slot.AttachComponent<SphereCollider>().Radius.Value = 0.15f;
        slot.AttachComponent<Grabbable>();
    }

    private void ClearTestSpheres()
    {
        var world = World;
        if (world?.RootSlot == null)
            return;

        var doomed = new List<Slot>();
        foreach (var child in world.RootSlot.Children)
        {
            if (child.SlotName.Value == TestSphereName)
                doomed.Add(child);
        }
        foreach (var slot in doomed)
            slot.Destroy();
    }

    private void RefitSelected()
    {
        var probe = SelectedProbe();
        var parent = probe?.Slot?.Parent;
        if (probe == null || parent == null || parent.IsDestroyed)
            return;
        if (!SlotBoundsHelper.TryComputeWorldBounds(parent, out var bounds))
            return;

        var scale = probe.Slot.GlobalScale;
        var size = bounds.Size + new float3(1f, 1f, 1f);
        probe.Size.Value = new float3(
            size.x / SafeScale(scale.x),
            size.y / SafeScale(scale.y),
            size.z / SafeScale(scale.z));
        probe.Rebake();
    }

    private static float SafeScale(float value)
    {
        float magnitude = value < 0f ? -value : value;
        return magnitude < 1e-4f ? 1f : magnitude;
    }

    private void JumpToSelected()
    {
        var probe = SelectedProbe();
        var userRoot = World?.LocalUser?.Root?.Slot;
        if (probe == null || userRoot == null || userRoot.IsDestroyed)
            return;

        var target = probe.Slot.GlobalPosition + probe.OriginOffset.Value;
        userRoot.GlobalPosition = target + new float3(0f, 0f, 0.8f);
    }

    // THE AUDIT
    //
    // Every check here is a real failure mode, not generic advice. Several of them were the actual
    // reason probes in this project looked like they did not work at all.
    private void RunAudit()
    {
        _audit.Clear();

        if (!EngineSettings.ReflectionsEnabled)
            _audit.Add("Reflections are OFF in settings. No probe renders at all until that is back on.");

        if (_probes.Count == 0)
        {
            _audit.Add("No probes under this root.");
            return;
        }

        for (int i = 0; i < _probes.Count; i++)
        {
            var probe = _probes[i];
            if (probe == null || probe.IsDestroyed)
                continue;

            string name = probe.Slot.SlotName.Value;
            var size = probe.Size.Value;

            if (size.x <= 0.01f || size.y <= 0.01f || size.z <= 0.01f)
                _audit.Add($"{name}: box has a zero or near-zero axis, so it influences nothing.");

            // The mistake this whole session started with: geometry on the probe's OWN slot is
            // photographed from the inside by its own capture.
            foreach (var renderer in probe.Slot.GetComponents<MeshRenderer>())
            {
                if (renderer != null && !renderer.IsDestroyed)
                {
                    _audit.Add($"{name}: a renderer sits on the PROBE'S OWN SLOT. Its capture is shot "
                               + "from inside that geometry. Move it to a child on the temp layer.");
                    break;
                }
            }

            if (probe.UpdateMode.Value == ProbeUpdateMode.Always)
                _audit.Add($"{name}: update mode Always re-renders the scene six times every frame.");

            if (probe.MaxDistance.Value > 0f)
            {
                float half = MathF.Max(size.x, MathF.Max(size.y, size.z)) * 0.5f;
                if (probe.MaxDistance.Value < half)
                    _audit.Add($"{name}: MaxDistance {probe.MaxDistance.Value:0.#}m is shorter than the box "
                               + $"half-extent {half:0.#}m, so the far part of its own volume gets nothing.");
            }

            if (probe.BlendDistance.Value <= 0f && _probes.Count > 1)
                _audit.Add($"{name}: blend distance 0 with other probes present gives a hard seam at its edge.");

            if (probe.AmbientMode.Value == ProbeAmbientMode.Disabled && probe.Interior.Value)
                _audit.Add($"{name}: interior with ambient Disabled leaves the inside unlit by anything.");

            // Two probes on the same spot fight, and which one wins is not something the user controls.
            for (int j = i + 1; j < _probes.Count; j++)
            {
                var other = _probes[j];
                if (other == null || other.IsDestroyed)
                    continue;
                float distance = float3.Distance(probe.Slot.GlobalPosition, other.Slot.GlobalPosition);
                if (distance < 0.25f)
                {
                    _audit.Add($"{name} and {other.Slot.SlotName.Value} are {distance:0.##}m apart. "
                               + "Overlapping probes fight and the winner is not yours to choose.");
                }
            }
        }

        if (_audit.Count == 0)
            _audit.Add("No problems found.");
    }
}
