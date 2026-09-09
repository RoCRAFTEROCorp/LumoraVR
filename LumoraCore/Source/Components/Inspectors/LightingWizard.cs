// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// What the lights in a world are costing, and how to stop them costing it.
//
// The premise this was built for is "we are murdering the computer with realtime lights", and the
// honest finding is that most of the fix is not baking. A lightmap does not make a frame faster on its
// own: the renderer does not stop rendering a shadow map because lightmap data exists, so every
// millisecond comes from turning shadows OFF and the bake only buys the look back afterwards.
//
// So this panel is about SHADOWS, which are the expensive half. An unshadowed light is close to free;
// its shadow is six cube-face renders for a point light and one pass per cascade for a sun. Every
// audit check and every one-click fix here targets that. -xlinka
[ComponentCategory("Utility/Inspectors")]
public sealed class LightingWizard : Component, IInspectorActionHandler
{
    public readonly SyncRef<Slot> Root;
    public readonly Sync<bool> IncludeDisabled;
    public readonly Sync<int> SelectedIndex;

    private readonly SyncRef<Slot> _listContent;
    private readonly Sync<int> _revision;
    private readonly Sync<int> _builtRevision;

    private const float RowHeight = 28f;

    private readonly List<Light> _lights = new();
    private readonly List<string> _audit = new();

    public LightingWizard()
    {
        Root = new SyncRef<Slot>(this);
        IncludeDisabled = new Sync<bool>(this, false);
        SelectedIndex = new Sync<int>(this, -1);
        _listContent = new SyncRef<Slot>(this);
        _revision = new Sync<int>(this, 0);
        _builtRevision = new Sync<int>(this, -1);
    }

    public static LightingWizard Spawn(World world)
    {
        if (world?.RootSlot == null)
            return null!;

        var panelSlot = world.RootSlot.AddSlot("Lighting");
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

        var wizard = panelSlot.AttachComponent<LightingWizard>();
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
        shell.Title.Value = "Lighting";
        shell.Size.Value = new float2(680f, 1000f);
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

    private void Rebuild()
    {
        var container = _listContent.Target;
        if (container == null || container.IsDestroyed)
            return;
        container.DestroyChildren();

        Collect();
        BuildGlobals(container);
        BuildBudget(container);
        BuildActions(container);
        BuildList(container);
        BuildDetail(container);
        BuildAudit(container);
    }

    private void Collect()
    {
        _lights.Clear();
        var root = Root.Target ?? World?.RootSlot;
        if (root == null || root.IsDestroyed)
            return;

        foreach (var light in root.GetComponentsInChildren<Light>())
        {
            if (light == null || light.IsDestroyed)
                continue;
            if (!IncludeDisabled.Value && !light.Enabled.Value)
                continue;
            _lights.Add(light);
        }
    }

    private void BuildGlobals(Slot container)
    {
        InspectorUI.SectionHeader(container, "Global", Slot);
        AddInfo(container, "Shadow quality", EngineSettings.DescribeShadowQuality(EngineSettings.ShadowQuality), InspectorUI.TextColor);
        AddInfo(container, "Shadow distance", $"{EngineSettings.ShadowDistanceScale:0.##}x", InspectorUI.TextColor);
        AddButton(container, "Shadow quality: cycle", "quality", InspectorUI.TextColor);
    }

    // The number the user actually asked about. Counted rather than modelled: a shadow casting light is
    // six cube-face renders for a point, one per cascade for a sun, one for a spot.
    private void BuildBudget(Slot container)
    {
        int casters = 0;
        int passes = 0;
        int unshadowed = 0;

        foreach (var light in _lights)
        {
            if (light.IsDestroyed)
                continue;
            bool casts = light.Shadows.Value != ShadowType.None && light.ShadowStrength.Value > 0.001f;
            if (!casts)
            {
                unshadowed++;
                continue;
            }
            casters++;
            passes += light.Type.Value switch
            {
                LightType.Point => 6,
                LightType.Directional => light.ShadowSplits.Value switch
                {
                    ShadowSplitMode.Four => 4,
                    ShadowSplitMode.Two => 2,
                    _ => 1,
                },
                _ => 1,
            };
        }

        InspectorUI.SectionHeader(container, "Cost", Slot);
        AddInfo(container, "Lights", $"{_lights.Count}  ({unshadowed} free, {casters} casting)", InspectorUI.TextColor);
        AddInfo(container, "Shadow passes / frame", passes.ToString(),
            passes > 24 ? InspectorUI.DangerColor : passes > 8 ? InspectorUI.CyanColor : InspectorUI.TextColor);
        AddInfo(container, "", "An unshadowed light is nearly free. The passes above are the cost.", InspectorUI.MutedColor);
    }

    private void BuildActions(Slot container)
    {
        InspectorUI.SectionHeader(container, "Actions", Slot);
        AddButton(container, "Run audit", "audit", InspectorUI.CyanColor);
        AddButton(container, "Shadows off, everything (A/B the cost)", "alloff", InspectorUI.DangerColor);
        AddButton(container, "Shadows on, everything", "allon", InspectorUI.TextColor);
        AddButton(container, "Fade distant shadows (8m) on all lamps", "fadeall", InspectorUI.AccentColor);
        AddButton(container, IncludeDisabled.Value ? "Hiding nothing" : "Include disabled", "toggledisabled", InspectorUI.MutedColor);
    }

    private void BuildList(Slot container)
    {
        InspectorUI.SectionHeader(container, "Lights", Slot);
        if (_lights.Count == 0)
        {
            AddInfo(container, "", "No lights under this root.", InspectorUI.MutedColor);
            return;
        }

        for (int i = 0; i < _lights.Count; i++)
        {
            var light = _lights[i];
            bool casts = light.Shadows.Value != ShadowType.None && light.ShadowStrength.Value > 0.001f;
            string cost = casts
                ? light.Type.Value switch
                {
                    LightType.Point => "6 passes",
                    LightType.Directional => $"{(light.ShadowSplits.Value == ShadowSplitMode.Four ? 4 : light.ShadowSplits.Value == ShadowSplitMode.Two ? 2 : 1)} cascades",
                    _ => "1 pass",
                }
                : "free";

            string label = $"{(i == SelectedIndex.Value ? "> " : "  ")}{light.Slot.SlotName.Value}"
                           + $"   {light.Type.Value}"
                           + $"   {cost}"
                           + (light.ShadowFadeDistance.Value > 0f ? $"   fade {light.ShadowFadeDistance.Value:0.#}m" : "")
                           + (light.Enabled.Value ? "" : "   DISABLED");

            AddButton(container, label, "sel:" + i,
                i == SelectedIndex.Value ? InspectorUI.AccentColor
                : casts ? InspectorUI.TextColor : InspectorUI.MutedColor);
        }
    }

    private void BuildDetail(Slot container)
    {
        var light = Selected();
        if (light == null)
            return;

        InspectorUI.SectionHeader(container, "Selected: " + light.Slot.SlotName.Value, Slot);
        AddInfo(container, "Shadows", light.Shadows.Value.ToString(), InspectorUI.TextColor);
        AddInfo(container, "Strength", $"{light.ShadowStrength.Value:0.##}", InspectorUI.TextColor);
        AddInfo(container, "Range", $"{light.Range.Value:0.#}m", InspectorUI.TextColor);
        if (light.Type.Value == LightType.Directional)
            AddInfo(container, "Cascades", $"{light.ShadowSplits.Value} over {light.ShadowMaxDistance.Value:0.#}m", InspectorUI.TextColor);

        AddButton(container, "Toggle its shadow", "toggle", InspectorUI.TextColor);
        AddButton(container, "Cycle Hard / Soft / None", "cycle", InspectorUI.TextColor);
        AddButton(container, "Fade its shadow at 8m", "fade", InspectorUI.TextColor);
        if (light.Type.Value == LightType.Point)
            AddButton(container, "Convert to spot (half the shadow cost)", "tospot", InspectorUI.AccentColor);
        if (light.Type.Value == LightType.Directional)
            AddButton(container, "Halve its cascade distance", "halve", InspectorUI.TextColor);
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

    private Light? Selected()
    {
        int index = SelectedIndex.Value;
        if (index < 0 || index >= _lights.Count)
            return null;
        var light = _lights[index];
        return light is { IsDestroyed: false } ? light : null;
    }

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
            case "quality":
                EngineSettings.ShadowQuality = (EngineSettings.ShadowQuality + 1) % EngineSettings.ShadowQualityOptions.Length;
                Dirty();
                return;

            case "toggledisabled":
                IncludeDisabled.Value = !IncludeDisabled.Value;
                SelectedIndex.Value = -1;
                Dirty();
                return;

            case "audit":
                RunAudit();
                Dirty();
                return;

            case "alloff":
                foreach (var light in _lights)
                {
                    if (light is { IsDestroyed: false })
                        light.Shadows.Value = ShadowType.None;
                }
                Dirty();
                return;

            case "allon":
                foreach (var light in _lights)
                {
                    if (light is { IsDestroyed: false })
                        light.Shadows.Value = ShadowType.Hard;
                }
                Dirty();
                return;

            case "fadeall":
                foreach (var light in _lights)
                {
                    if (light is { IsDestroyed: false } && light.Type.Value != LightType.Directional)
                        light.ShadowFadeDistance.Value = 8f;
                }
                Dirty();
                return;

            case "toggle":
            {
                var light = Selected();
                if (light != null)
                    light.Shadows.Value = light.Shadows.Value == ShadowType.None ? ShadowType.Hard : ShadowType.None;
                Dirty();
                return;
            }

            case "cycle":
            {
                var light = Selected();
                if (light != null)
                {
                    light.Shadows.Value = light.Shadows.Value switch
                    {
                        ShadowType.None => ShadowType.Hard,
                        ShadowType.Hard => ShadowType.Soft,
                        _ => ShadowType.None,
                    };
                }
                Dirty();
                return;
            }

            case "fade":
            {
                var light = Selected();
                if (light != null)
                    light.ShadowFadeDistance.Value = light.ShadowFadeDistance.Value > 0f ? 0f : 8f;
                Dirty();
                return;
            }

            case "tospot":
            {
                var light = Selected();
                if (light != null && light.Type.Value == LightType.Point)
                    light.Type.Value = LightType.Spot;
                Dirty();
                return;
            }

            case "halve":
            {
                var light = Selected();
                if (light != null)
                    light.ShadowMaxDistance.Value = MathF.Max(5f, light.ShadowMaxDistance.Value * 0.5f);
                Dirty();
                return;
            }

            case "jump":
            {
                var light = Selected();
                var userRoot = World?.LocalUser?.Root?.Slot;
                if (light != null && userRoot != null && !userRoot.IsDestroyed)
                    userRoot.GlobalPosition = light.Slot.GlobalPosition + new float3(0f, 0f, 1.2f);
                return;
            }
        }
    }

    // Every check is a cost mistake that is real in this engine, not general advice.
    private void RunAudit()
    {
        _audit.Clear();

        if (_lights.Count == 0)
        {
            _audit.Add("No lights under this root.");
            return;
        }

        int suns = 0;
        foreach (var light in _lights)
        {
            if (light == null || light.IsDestroyed)
                continue;

            string name = light.Slot.SlotName.Value;
            bool casts = light.Shadows.Value != ShadowType.None && light.ShadowStrength.Value > 0.001f;

            if (light.Type.Value == LightType.Directional)
            {
                suns++;
                if (casts && light.ShadowMaxDistance.Value > 80f)
                    _audit.Add($"{name}: sun casts out to {light.ShadowMaxDistance.Value:0}m. Distance is the "
                               + "knob that matters - halving it halves the cost.");
                if (casts && light.ShadowSplits.Value == ShadowSplitMode.Four && light.ShadowMaxDistance.Value <= 60f)
                    _audit.Add($"{name}: four cascades over a short distance. Two is usually indistinguishable here.");
            }

            if (light.Type.Value == LightType.Point && casts && light.ShadowFadeDistance.Value <= 0f)
                _audit.Add($"{name}: point light casting with no shadow fade. Six cube faces re-rendered "
                           + "from any distance, forever.");

            if (light.Type.Value == LightType.Point && casts && light.Range.Value <= 6f)
                _audit.Add($"{name}: a short-range point light paying six passes. A spot costs one.");

            if (casts && !light.Enabled.Value)
                _audit.Add($"{name}: disabled but still configured to cast. Harmless now, a cost the moment it is enabled.");

            if (light.ShadowStrength.Value <= 0.001f && light.Shadows.Value != ShadowType.None)
                _audit.Add($"{name}: shadow strength is zero, so its shadow is invisible. It no longer renders one, "
                           + "but the setting reads as if it does.");
        }

        if (suns > 1)
            _audit.Add($"{suns} directional lights. Each one is a full cascade set over the whole world.");

        if (_audit.Count == 0)
            _audit.Add("Nothing costly found.");
    }
}
