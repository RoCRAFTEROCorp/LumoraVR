// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Helio.UI.Listing;
using Lumora.Core.Input;
using Lumora.Core.Input.Actions;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// Category rail on the left, entry list on the right, both driven by listing sources over
// EngineSettings. The entry list is VIRTUALIZED: the Controls page alone is ~50 rows of six cells
// each, which as eagerly-built rows blew past the per-chunk surface band and re-tessellated the whole
// page on every hover. Now only what the viewport can see exists.
//
// Switching category swaps the entry view's path. Nothing around it is rebuilt: the rail, the panel
// and the title are chrome that outlive the selection, and the rows themselves are recycled.
//
// A setting appears in exactly ONE place, and no category shares a name with a section in another one.
// General used to hold both an "Interface" section and a "Dashboard" section while an Interface
// category sat below it in the rail, so there was no way to guess where anything lived.
//
// Every entry here writes a setting a subsystem actually reads. If you cannot point at the consumer,
// it does not go on this screen. -xlinka
[ComponentCategory("Hidden")]
public sealed class SettingsScreen : DashboardScreen
{
    private const string KindBinding = "binding";
    private const string KindLocomotion = "locomotion";
    private const string KindNavCategory = "nav";

    // One poll drives every live row on the screen: the locomotion strip, the pad status, the last
    // input readout and the binding cells. It also catches settings changed from somewhere else (a Home
    // widget, code applying a value). Cheap because every template write is equality-gated, so a tick
    // where nothing moved costs no re-mesh.
    private const float RefreshInterval = 0.1f;

    private Dashboard? _dashboard;
    private readonly SettingsFonts _fonts = new();
    private ListingStyle _style = new ListingStyle();
    private ListingStyle _sidebarStyle = new ListingStyle();
    private readonly ListingItemSource _categories = new();
    private readonly ListingItemSource _entries = new();
    private ListingView? _sidebarView;
    private ListingView? _entryView;
    private Text? _title;
    private ListingItem? _activeItem;
    private string _activeCategory = string.Empty;
    private float _refreshAccum;
    private LocaleText _controlsStatus = "Settings.Controls.Hint".AsLocale("Click a binding to rebind it. Escape cancels.");
    private bool _localeHooked;

    // BUILD

    protected override void BuildContent(UIBuilder builder)
    {
        _dashboard = Slot.GetComponentInParents<Dashboard>();
        _fonts.Regular = _dashboard?.Font.Target;
        _fonts.Semibold = _dashboard?.FontSemibold.Target;
        _fonts.Bold = _dashboard?.FontBold.Target;

        // RoundedSprite is deliberately left null. Rows paint a procedural RoundedPanel now, so nothing
        // on this screen wants the nine-slice border sprite the old cards were built from.
        _style = new ListingStyle
        {
            Font = _fonts.Body,
            Accent = DashTheme.Accent,
            RowHeight = SettingsMetrics.RowHeight,
            RowSpacing = DashTheme.Gap,
        };
        _sidebarStyle = new ListingStyle
        {
            Font = _fonts.Medium,
            Accent = DashTheme.Accent,
            RowHeight = SettingsMetrics.NavHeight,
            RowSpacing = SettingsMetrics.NavSpacing,
        };

        var root = builder.Current;
        var split = root.AttachComponent<HorizontalLayout>();
        split.Spacing.Value = DashTheme.GapLarge;
        split.PaddingLeft.Value = DashTheme.GapLarge;
        split.PaddingRight.Value = DashTheme.GapLarge;
        split.PaddingTop.Value = DashTheme.GapLarge;
        split.PaddingBottom.Value = DashTheme.GapLarge;
        split.ForceExpandWidth.Value = false;
        split.ForceExpandHeight.Value = true;

        BuildCategories();
        BuildEntries();

        // The rail is bare: no card, no outline. The items are words, and only the current one carries
        // a wash. Its first item lines up with the title beside it.
        var railHost = AddColumn(root, "Sidebar", SettingsMetrics.SidebarWidth);
        float railTop = (SettingsMetrics.TitleHeight - SettingsMetrics.NavHeight) * 0.5f;
        var railViewport = SettingsUI.Fill(railHost, "Viewport", 0f, DashTheme.Gap, 0f, railTop);
        var railTemplates = new ListingTemplateMapper { DefaultHeight = SettingsMetrics.NavHeight };
        railTemplates.MapKind(KindNavCategory, new SettingsNavTemplate(_fonts, PickCategory));
        _sidebarView = ListingView.Attach(railViewport, _sidebarStyle, railTemplates, _categories);

        // No panel fill of its own. The dash already hands every screen a panel to sit on, and a second
        // one in the same tone inside it is a box drawn around nothing. The rows carry the surface.
        var entryHost = AddColumn(root, "Entries", 0f);
        _title = SettingsUI.Label(entryHost, "Title", _fonts.Strong, DashTheme.FontTitle, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f),
            new float2(DashTheme.Inset, -SettingsMetrics.TitleHeight), new float2(-DashTheme.Inset, 0f));
        var entryViewport = SettingsUI.Fill(entryHost, "Viewport",
            SettingsMetrics.PanelInset, SettingsMetrics.PanelInset,
            SettingsMetrics.PanelInset, SettingsMetrics.TitleHeight);
        _entryView = ListingView.Attach(entryViewport, _style, BuildEntryTemplates(), _entries);

        var first = _categories.ItemsAt(string.Empty);
        PickCategory(first.Count > 0 ? first[0] : null);

        LocaleManager.Changed += OnLocaleChanged;
        _localeHooked = true;
    }

    public override void OnDestroy()
    {
        if (_localeHooked)
        {
            LocaleManager.Changed -= OnLocaleChanged;
            _localeHooked = false;
        }
        base.OnDestroy();
    }

    // Every label on this screen is read out of a listing item at bind time, so the only thing a
    // language switch has to do here is ask both views to rebind. Nothing is rebuilt: the rail keeps
    // its selection, the entry list keeps its scroll, and the template writes are equality-gated so the
    // rows whose text did not change do not re-mesh. -xlinka
    private void OnLocaleChanged()
    {
        if (IsDestroyed)
            return;
        _sidebarView?.RefreshValues();
        _entryView?.RefreshValues();
        if (_title != null && _activeItem != null)
            ListingStyle.SetText(_title, _activeItem.Label);
        MarkDirty();
    }

    private ListingTemplateMapper BuildEntryTemplates()
    {
        var labels = new SettingsLabelTemplate(_fonts);
        var mapper = new ListingTemplateMapper { DefaultHeight = SettingsMetrics.RowHeight };
        mapper.Map<ListingHeader>(new SettingsSectionTemplate(_fonts));
        mapper.Map<ListingToggle>(new SettingsToggleTemplate(_fonts));
        mapper.Map<ListingSlider>(new SettingsSliderTemplate(_fonts));
        mapper.Map<ListingChoice>(new SettingsChoiceTemplate(_fonts));
        mapper.Map<ListingAction>(new SettingsActionTemplate(_fonts));
        mapper.Map<ListingLabel>(labels);
        mapper.Fallback(labels);
        mapper.MapKind(KindBinding, new SettingsBindingTemplate(_fonts, this));
        mapper.MapKind(KindLocomotion, new SettingsLocomotionTemplate(_fonts, this));
        return mapper;
    }

    private static Slot AddColumn(Slot root, string name, float fixedWidth)
    {
        var column = root.AddSlot(name);
        column.AttachComponent<RectTransform>();
        var element = column.AttachComponent<LayoutElement>();
        if (fixedWidth > 0f)
        {
            element.MinWidth.Value = fixedWidth;
            element.PreferredWidth.Value = fixedWidth;
            element.FlexibleWidth.Value = 0f;
        }
        else
        {
            element.FlexibleWidth.Value = 1f;
        }
        element.FlexibleHeight.Value = 1f;
        return column;
    }

    private void BuildCategories()
    {
        _categories.ClearAll();
        AddCategory("general", "Settings.Category.General".AsLocale("General"));
        AddCategory("graphics", "Settings.Category.Graphics".AsLocale("Graphics"));
        AddCategory("movement", "Settings.Category.Movement".AsLocale("Movement"));
        AddCategory("controls", "Settings.Category.Controls".AsLocale("Controls"));
        AddCategory("interface", "Settings.Category.Interface".AsLocale("Interface"));
    }

    private void AddCategory(string key, LocaleText label)
        => _categories.AddRoot(new ListingCustom(key, KindNavCategory, label));

    private void PickCategory(ListingItem? item)
    {
        if (item == null)
            return;
        // Leaving Controls mid-rebind must not strand the listener: while it waits every action set is
        // gated off, so an abandoned capture reads as input having died.
        CancelPendingRebind();
        _activeItem = item;
        _activeCategory = item.Key;
        if (_sidebarView != null)
            _sidebarView.SelectedKey = item.Key;
        // Swaps the listing's items only. The rail, the panel and the title are chrome and stay put.
        _entryView?.SetPath(item.Key);
        if (_title != null)
            ListingStyle.SetText(_title, item.Label);
        MarkDirty();
    }

    // ENTRIES
    //
    // Everything below is wired to a real consumer. Where a value is only meaningful in one mode
    // (snap angle vs smooth speed), the other row goes non-interactive rather than disappearing, so
    // the page does not reflow under your hand.

    private void BuildEntries()
    {
        _entries.ClearAll();
        BuildGeneral();
        BuildGraphics();
        BuildMovement();
        BuildControls();
        BuildInterface();
    }

    private void BuildGeneral()
    {
        const string path = "general";
        Header(path, "You", "Settings.General.Section.You".AsLocale("You"));
        // Drives the avatar auto-rescale: SettingsApplier feeds it to InputInterface.UserHeight and
        // AvatarIK re-scales so the eyes land at this height.
        Slider(path, "height", "Settings.General.Height".AsLocale("Height"), SettingsCatalog.UserHeight,
            () => EngineSettings.UserHeight, v => EngineSettings.UserHeight = v, v => $"{v:0.00} m");

        Header(path, "Audio", "Settings.General.Section.Audio".AsLocale("Audio"));
        Slider(path, "volume", "Settings.General.MasterVolume".AsLocale("Master Volume"), SettingsCatalog.MasterVolume,
            () => EngineSettings.MasterVolume, v => EngineSettings.MasterVolume = v, v => $"{v * 100f:0}%");
    }

    private void BuildGraphics()
    {
        const string path = "graphics";
        Header(path, "Display", "Settings.Graphics.Section.Display".AsLocale("Display"));
        Toggle(path, "vsync", "Settings.Graphics.VSync".AsLocale("VSync"), () => EngineSettings.VSync, v => EngineSettings.VSync = v);
        Toggle(path, "fullscreen", "Settings.Graphics.Fullscreen".AsLocale("Fullscreen"), () => EngineSettings.Fullscreen, v => EngineSettings.Fullscreen = v);
        Slider(path, "fps", "Settings.Graphics.FpsLimit".AsLocale("FPS Limit"), SettingsCatalog.MaxFps,
            () => EngineSettings.MaxFps, v => EngineSettings.MaxFps = (int)v,
            v => v <= 0f ? "Off" : $"{(int)v}");
        // Caps the loop while the window is unfocused or minimized (vsync stops throttling there).
        Slider(path, "bgfps", "Settings.Graphics.BackgroundFps".AsLocale("Background FPS"), SettingsCatalog.BackgroundFps,
            () => EngineSettings.BackgroundFps, v => EngineSettings.BackgroundFps = (int)v,
            v => v <= 0f ? "Off" : $"{(int)v}");
        // Windowed this resizes the window; fullscreen it drops the 3D buffer to that height and leaves
        // the interface at native. A picker, not a slider: nobody drags for a resolution.
        _entries.Add(path, new ListingChoice("resolution", "Settings.Graphics.Resolution".AsLocale("Resolution"))
        {
            Options = ResolutionOptionLabels(),
            Read = () => ResolutionIndex(EngineSettings.ResolutionHeight),
            Write = index =>
            {
                var options = EngineSettings.ResolutionHeightOptions;
                if (index >= 0 && index < options.Length)
                    EngineSettings.ResolutionHeight = options[index];
            },
        });

        // 5% steps so the viewport is not re-allocated per pixel of drag. With dynamic resolution on
        // this is the CEILING rather than the fixed value.
        Slider(path, "renderscale", "Settings.Graphics.RenderScale".AsLocale("Render Scale"), SettingsCatalog.RenderScale,
            () => EngineSettings.RenderScale, v => EngineSettings.RenderScale = v, v => $"{v * 100f:0}%");
        Toggle(path, "dynres", "Settings.Graphics.DynamicResolution".AsLocale("Dynamic Resolution"),
            () => EngineSettings.DynamicResolution, v => EngineSettings.DynamicResolution = v);
        // Desktop only, and only does anything below 100% scale. Not offered in a headset: see
        // EngineSettings.Upscaler for why.
        _entries.Add(path, new ListingChoice("upscaler", "Settings.Graphics.Upscaler".AsLocale("Upscaler"))
        {
            Options = new[] { "Bilinear", "FSR 1", "FSR 2" },
            Read = () => EngineSettings.Upscaler,
            Write = index => EngineSettings.Upscaler = index,
        });

        Header(path, "Image", "Settings.Graphics.Section.Image".AsLocale("Image"));
        // The renderer shipped on the platform's untouched defaults: a LINEAR tonemapper with a white
        // point of 1.0, which clips every value above 1 - exactly the range colorHDR exists to carry.
        // AgX holds saturated brights where ACES skews them toward white, and saturated brights are
        // what emissives are made of.
        _entries.Add(path, new ListingChoice("tonemap", "Settings.Graphics.Tonemap".AsLocale("Tonemap"))
        {
            Options = EngineSettings.TonemapOptions,
            Read = () => EngineSettings.Tonemap,
            Write = index => EngineSettings.Tonemap = index,
        });
        // The luminance that maps to full white. At 1 everything brighter clips, which is the default
        // that made every emissive flat.
        Slider(path, "exposure", "Settings.Graphics.WhitePoint".AsLocale("White Point"), SettingsCatalog.Exposure,
            () => EngineSettings.Exposure, v => EngineSettings.Exposure = v, v => $"{v:0.#}");
        Slider(path, "bloom", "Settings.Graphics.Bloom".AsLocale("Bloom"), SettingsCatalog.Bloom,
            () => EngineSettings.Bloom, v => EngineSettings.Bloom = v,
            v => v <= 0.001f ? "Off" : $"{v:0.##}");
        // Costs fill rate rather than frame logic. Off is a real option on a weak GPU, but in a
        // headset the first step off zero is the difference between crawling edges and a clean image.
        Slider(path, "aa", "Settings.Graphics.AntiAliasing".AsLocale("Anti-Aliasing"), 0f, EngineSettings.AntiAliasingOptions.Length - 1, 1f,
            () => AntiAliasingIndex(EngineSettings.AntiAliasing),
            v =>
            {
                int index = System.Math.Clamp((int)MathF.Round(v), 0, EngineSettings.AntiAliasingOptions.Length - 1);
                EngineSettings.AntiAliasing = EngineSettings.AntiAliasingOptions[index];
            },
            v => EngineSettings.DescribeAntiAliasing(
                EngineSettings.AntiAliasingOptions[System.Math.Clamp((int)MathF.Round(v), 0, EngineSettings.AntiAliasingOptions.Length - 1)]));

        Header(path, "Quality", "Settings.Graphics.Section.Quality".AsLocale("Quality"));
        // Never set by anything until now, so every world ran on the renderer default. One of the
        // largest single levers on shadow cost.
        _entries.Add(path, new ListingChoice("shadowquality", "Settings.Graphics.ShadowQuality".AsLocale("Shadow Quality"))
        {
            Options = EngineSettings.ShadowQualityOptions,
            Read = () => EngineSettings.ShadowQuality,
            Write = index => EngineSettings.ShadowQuality = index,
        });
        // Driven as an index over the generated buckets, not pixels: a drag can only land on a size
        // that actually has a variant behind it. Providers re-resolve live onto the new cap.
        Slider(path, "texturesize", "Settings.Graphics.MaxTextureSize".AsLocale("Max Texture Size"), 0f, EngineSettings.TextureSizeOptions.Length - 1, 1f,
            () => TextureSizeIndex(EngineSettings.MaxTextureSize),
            v =>
            {
                int index = System.Math.Clamp((int)MathF.Round(v), 0, EngineSettings.TextureSizeOptions.Length - 1);
                EngineSettings.MaxTextureSize = EngineSettings.TextureSizeOptions[index];
            },
            v => EngineSettings.DescribeTextureSize(
                EngineSettings.TextureSizeOptions[System.Math.Clamp((int)MathF.Round(v), 0, EngineSettings.TextureSizeOptions.Length - 1)]));
        // Off makes every probe stop rendering, which removes its cost rather than dimming it: a probe
        // in Always mode is six scene renders per bake.
        Toggle(path, "reflections", "Settings.Graphics.Reflections".AsLocale("Reflections"),
            () => EngineSettings.ReflectionsEnabled, v => EngineSettings.ReflectionsEnabled = v);
        // Multiplier on every LOD switch distance. Above 1 holds detailed levels further out.
        Slider(path, "lodbias", "Settings.Graphics.LodBias".AsLocale("LOD Bias"), SettingsCatalog.LodBias,
            () => EngineSettings.LodBias, v => EngineSettings.LodBias = v, v => $"{v:0.00}x");
        // Screen-space error an imported mesh is allowed to show before the renderer drops it to a
        // cheaper level of itself. This never removes an object, only triangles.
        Slider(path, "meshlod", "Settings.Graphics.MeshDetail".AsLocale("Mesh Detail"), SettingsCatalog.MeshLodThreshold,
            () => EngineSettings.MeshLodThreshold, v => EngineSettings.MeshLodThreshold = v,
            EngineSettings.DescribeMeshLodThreshold);
        // Multiplier on every directional light's cascade range - the cheapest real cut on the shadow
        // pass, and it works on worlds whose lights someone else authored.
        Slider(path, "shadowdistance", "Settings.Graphics.ShadowDistance".AsLocale("Shadow Distance"), SettingsCatalog.ShadowDistanceScale,
            () => EngineSettings.ShadowDistanceScale, v => EngineSettings.ShadowDistanceScale = v, v => $"{v:0.00}x");
    }

    private void BuildMovement()
    {
        const string path = "movement";
        Header(path, "Look", "Settings.Movement.Section.Look".AsLocale("Look"));
        Slider(path, "mousesens", "Settings.Movement.MouseSensitivity".AsLocale("Mouse Sensitivity"), SettingsCatalog.MouseSensitivity,
            () => EngineSettings.MouseSensitivity, v => EngineSettings.MouseSensitivity = v, v => $"{v:0.00}x");
        Slider(path, "mousesmooth", "Settings.Movement.MouseSmoothing".AsLocale("Mouse Smoothing"), SettingsCatalog.MouseSmoothing,
            () => EngineSettings.MouseSmoothing, v => EngineSettings.MouseSmoothing = v,
            v => v <= 0.001f ? "Off" : $"{v:0.00}");

        Header(path, "Locomotion", "Settings.Movement.Section.Locomotion".AsLocale("Locomotion"));
        _entries.Add(path, new ListingCustom("locomotion", KindLocomotion, "Settings.Movement.Mode".AsLocale("Mode")));
        Slider(path, "noclip", "Settings.Movement.NoclipSpeed".AsLocale("Noclip Speed"), SettingsCatalog.NoclipSpeed,
            () => EngineSettings.NoclipSpeed, v => EngineSettings.NoclipSpeed = v, v => $"{v:0.#} m/s");

        Header(path, "Turning", "Settings.Movement.Section.Turning".AsLocale("Turning"));
        _entries.Add(path, ListingChoice.FromEnum<EngineSettings.TurnStyle>("turnmode", "Settings.Movement.TurnStyle".AsLocale("Turn Style"),
            () => EngineSettings.TurnMode, v => EngineSettings.TurnMode = v));
        // Only one of these two does anything at a time; the idle one greys out instead of vanishing so
        // the list does not reflow while you are reading it.
        var snap = Slider(path, "snapangle", "Settings.Movement.SnapAngle".AsLocale("Snap Angle"), SettingsCatalog.SnapTurnAngle,
            () => EngineSettings.SnapTurnAngle, v => EngineSettings.SnapTurnAngle = v, v => $"{v:0}°");
        snap.Tag = EngineSettings.TurnStyle.Snap;
        var smooth = Slider(path, "smoothspeed", "Settings.Movement.SmoothTurnSpeed".AsLocale("Smooth Turn Speed"), SettingsCatalog.SmoothTurnSpeed,
            () => EngineSettings.SmoothTurnSpeed, v => EngineSettings.SmoothTurnSpeed = v, v => $"{v:0}°/s");
        smooth.Tag = EngineSettings.TurnStyle.Smooth;
    }

    // Everything that shapes what you look at, in one place: the language the interface speaks, the
    // desktop cursor, and the dashboard's own placement. Language and the dashboard toggles used to sit
    // under General, which left this category holding a single reticle group and no reason to exist.
    private void BuildInterface()
    {
        const string path = "interface";
        // The one setting in this category with nothing to group it with, so it leads the page rather
        // than sitting under a section header repeating its own name.
        _entries.Add(path, BuildLanguageChoice());

        Header(path, "Reticle", "Settings.Interface.Section.Reticle".AsLocale("Reticle"));
        // The desktop cursor drawer reads these through InterfaceSettings, which forwards to the same
        // fields, so a change repaints the cursor on the next frame.
        _entries.Add(path, ListingChoice.FromEnum<EngineSettings.ReticleShape>("reticlestyle", "Settings.Interface.ReticleStyle".AsLocale("Style"),
            () => EngineSettings.ReticleStyle, v => EngineSettings.ReticleStyle = v));
        var size = Slider(path, "reticlesize", "Settings.Interface.ReticleSize".AsLocale("Size"), SettingsCatalog.ReticleSize,
            () => EngineSettings.ReticleSize, v => EngineSettings.ReticleSize = v, v => $"{v:0} px");
        size.Tag = "reticle";
        var thickness = Slider(path, "reticlethickness", "Settings.Interface.ReticleThickness".AsLocale("Thickness"), SettingsCatalog.ReticleThickness,
            () => EngineSettings.ReticleThickness, v => EngineSettings.ReticleThickness = v, v => $"{v:0.#} px");
        thickness.Tag = "reticle";

        Header(path, "Dashboard", "Settings.Interface.Section.Dashboard".AsLocale("Dashboard"));
        // Freeform leaves the panel where you put it instead of pinning it in front of your view. VR
        // only; desktop is window-projected.
        Toggle(path, "freeform", "Settings.Interface.Freeform".AsLocale("Freeform"),
            () => UserspaceDashboard.LocalInstance?.Freeform.Value ?? false,
            v => UserspaceDashboard.LocalInstance?.SetFreeform(v),
            "Settings.Interface.Freeform.Hint".AsLocale("Stays where you put it"));
        // Edit mode boosts every spawned widget panel's grab above its canvas so you can pick it up;
        // off, the canvas takes clicks again.
        Toggle(path, "editwidgets", "Settings.Interface.EditWidgets".AsLocale("Edit Widgets"),
            () => WidgetPanel.EditMode, v => WidgetPanel.EditMode = v,
            "Settings.Interface.EditWidgets.Hint".AsLocale("Grab widgets to move them"));
    }

    // CONTROLS
    //
    // Generated from the action map rather than hand-listed: a set added in code shows up here with its
    // bindings and its rebind buttons, and nothing has to be kept in step by hand.
    //
    // Keyboard and mouse share one column because they share one desk - a rebind there takes whichever
    // of the two you reach for, and clearing it clears both. Bindings save the moment they change
    // rather than on exit; a remap you cannot undo because you remapped the key that reaches this
    // screen is a trap. -xlinka
    private void BuildControls()
    {
        const string path = "controls";
        var map = Engine.Current?.InputInterface?.Actions;

        Header(path, "Devices", "Settings.Controls.Section.Devices".AsLocale("Devices"));
        _entries.Add(path, new ListingLabel("pad", "Settings.Controls.Gamepad".AsLocale("Gamepad"), DescribePad));
        _entries.Add(path, new ListingLabel("lastinput", "Settings.Controls.LastInput".AsLocale("Last input"), DescribeLastInput));
        _entries.Add(path, new ListingAction("resetall", "Settings.Controls.ResetAll".AsLocale("Every control back to stock"))
        {
            Invoke = ResetAllBindings,
            ButtonLabel = "Settings.Controls.ResetAllButton".AsLocale("Reset All"),
            Destructive = true,
        });
        _entries.Add(path, new ListingLabel("status", "Settings.Controls.Status".AsLocale("Status"), () => _controlsStatus.Resolve()));

        if (map == null)
            return;

        foreach (var set in map.Sets)
        {
            bool wroteHeader = false;
            foreach (var action in set.Actions)
            {
                if (!action.Rebindable)
                    continue;
                if (!wroteHeader)
                {
                    // Set labels come out of the action map, which is code-owned rather than translated.
                    Header(path, set.Label, set.Label);
                    wroteHeader = true;
                }
                _entries.Add(path, new ListingCustom("bind." + set.Label + "." + action.Name, KindBinding, action.Label)
                {
                    Tag = action,
                });
            }
        }
    }

    // ENTRY HELPERS

    // Keyed on an id, not on the label: an item key has to hold still when the interface language
    // changes, or every row in the list reads as brand new to the reconcile the moment somebody
    // switches. -xlinka
    private void Header(string path, string id, LocaleText label)
        => _entries.Add(path, new ListingHeader(path + ".h." + id, label));

    // hint is the second line under the label, for a setting whose name alone does not say what it
    // does. It replaces the parenthetical that used to be stuffed into the label itself and wrapped it
    // onto two lines.
    private ListingToggle Toggle(string path, string key, LocaleText label, Func<bool> read, Action<bool> write,
        LocaleText hint = default)
        => _entries.Add(path, new ListingToggle(key, label) { Read = read, Write = write, DetailText = hint });

    // Range-taking overload. Every slider whose setting has bounds goes through this one, so the row can
    // no longer offer a narrower span than the engine accepts. Three of them already did. -xlinka
    private ListingSlider Slider(string path, string key, LocaleText label, in SettingRange range,
        Func<float> read, Action<float> write, Func<float, string> format)
        => Slider(path, key, label, range.Min, range.Max, range.Step, read, write, format);

    private ListingSlider Slider(string path, string key, LocaleText label, float min, float max, float step,
        Func<float> read, Action<float> write, Func<float, string> format)
        => _entries.Add(path, new ListingSlider(key, label)
        {
            Min = min,
            Max = max,
            Step = step,
            Read = read,
            Write = write,
            Format = format,
        });

    // The strip lists whatever locale tables actually loaded, in their own language, plus the generated
    // pseudo locale. Nothing is offered that has no table behind it: a language you can pick and that
    // then changes nothing is the exact kind of placebo setting this screen does not carry. -xlinka
    private static ListingChoice BuildLanguageChoice()
    {
        var available = LocaleManager.Available;
        var codes = new List<string>(available.Count);
        var names = new List<string>(available.Count);
        for (int i = 0; i < available.Count; i++)
        {
            codes.Add(available[i].Code);
            names.Add(available[i].NativeName);
        }
        return new ListingChoice("language", "Settings.Interface.Language".AsLocale("Language"))
        {
            Options = names,
            Read = () => LocaleManager.IndexOf(LocaleManager.CurrentLocale),
            Write = index =>
            {
                if (index >= 0 && index < codes.Count)
                    LocaleManager.SetLocale(codes[index]);
            },
        };
    }

    private static string[] ResolutionOptionLabels()
    {
        var options = EngineSettings.ResolutionHeightOptions;
        var labels = new string[options.Length];
        for (int i = 0; i < options.Length; i++)
            labels[i] = EngineSettings.DescribeResolutionHeight(options[i]);
        return labels;
    }

    private static int ResolutionIndex(int height)
    {
        var options = EngineSettings.ResolutionHeightOptions;
        for (int i = 0; i < options.Length; i++)
        {
            if (options[i] == height)
                return i;
        }
        return 0;
    }

    private static int TextureSizeIndex(int size)
    {
        var options = EngineSettings.TextureSizeOptions;
        for (int i = 0; i < options.Length; i++)
        {
            if (options[i] == size)
                return i;
        }
        return 0;
    }

    private static int AntiAliasingIndex(int samples)
    {
        var options = EngineSettings.AntiAliasingOptions;
        for (int i = 0; i < options.Length; i++)
        {
            if (options[i] == samples)
                return i;
        }
        return 0;
    }

    // LIVE STATE

    protected override void OnShow()
    {
        base.OnShow();
        // A pad plugged in, a rebind made elsewhere, or an avatar that finished spawning while this
        // screen sat closed.
        _entryView?.RefreshValues();
        MarkDirty();
    }

    protected override void OnHide()
    {
        CancelPendingRebind();
        base.OnHide();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (!Slot.ActiveSelf.Value)
            return;

        PumpRebindCapture();

        _refreshAccum += delta;
        if (_refreshAccum < RefreshInterval)
            return;
        _refreshAccum = 0f;

        ApplyTurnModeGating();
        _entryView?.RefreshValues();
    }

    // Snap angle and smooth speed each only mean something in their own mode. Flipping Interactable
    // here (rather than rebuilding the page) keeps the row where it is and just greys it.
    private void ApplyTurnModeGating()
    {
        var items = _entries.ItemsAt("movement");
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Tag is EngineSettings.TurnStyle style)
                items[i].Interactable = EngineSettings.TurnMode == style;
        }
    }

    // Polls the map's listener rather than driving it: the capture itself happens inside the input
    // pass, where the raw devices live, so all this has to do is notice when it finished.
    private void PumpRebindCapture()
    {
        if (_activeCategory != "controls")
            return;
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null)
            return;

        switch (map.CaptureStatus)
        {
            case InputBindingMap.CaptureState.Captured:
                var bound = map.CaptureConflicts;
                if (bound.Count > 0)
                {
                    var names = new List<string>(bound.Count);
                    foreach (var conflict in bound)
                        names.Add(conflict.Label);
                    _controlsStatus = "Settings.Controls.BoundConflicts"
                        .AsLocale("Bound. Also used by: {0}.", string.Join(", ", names));
                }
                else
                {
                    _controlsStatus = "Settings.Controls.Bound".AsLocale("Bound.");
                }
                map.ClearCapture();
                PersistBindings();
                _entryView?.RefreshValues();
                break;

            case InputBindingMap.CaptureState.Cancelled:
                map.ClearCapture();
                _controlsStatus = "Settings.Controls.RebindCancelled".AsLocale("Rebind cancelled.");
                _entryView?.RefreshValues();
                break;
        }
    }

    internal void BeginRebind(InputAction action, InputDeviceKind[] devices)
    {
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null)
            return;
        map.ClearCapture();
        map.BeginCapture(action, devices);
        _controlsStatus = "Settings.Controls.PressControl"
            .AsLocale("Press a control for \"{0}\"... (Escape cancels)", action.Label);
        _entryView?.RefreshValues();
    }

    internal void ClearAllBindings(InputAction action)
    {
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null)
            return;
        map.ClearBindings(action, InputDeviceKind.Keyboard, InputDeviceKind.Mouse, InputDeviceKind.Gamepad, InputDeviceKind.VRController);
        PersistBindings();
        _controlsStatus = "Settings.Controls.Cleared".AsLocale("Cleared every binding for \"{0}\".", action.Label);
        _entryView?.RefreshValues();
    }

    internal void ResetBinding(InputAction action)
    {
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null)
            return;
        map.ResetToDefaults(action);
        PersistBindings();
        _controlsStatus = "Settings.Controls.Restored".AsLocale("Restored the stock binding for \"{0}\".", action.Label);
        _entryView?.RefreshValues();
    }

    private void ResetAllBindings()
    {
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null)
            return;
        map.ResetAllToDefaults();
        PersistBindings();
        _controlsStatus = "Settings.Controls.AllReset".AsLocale("Every control is back to stock.");
        _entryView?.RefreshValues();
    }

    private static void PersistBindings() => Engine.Current?.InputInterface?.SaveBindingOverrides();

    private void CancelPendingRebind()
    {
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null || map.CaptureStatus != InputBindingMap.CaptureState.Listening)
            return;
        map.ClearCapture();
        _controlsStatus = "Settings.Controls.RebindCancelled".AsLocale("Rebind cancelled.");
    }

    private static string DescribePad()
    {
        var input = Engine.Current?.InputInterface;
        var pad = input?.Gamepad;
        if (pad == null || !pad.IsConnected)
            return "none connected";
        int count = input?.GetGamepadDriver()?.ConnectedPadCount ?? 1;
        return count > 1 ? $"{pad.DeviceName} (+{count - 1} idle)" : pad.DeviceName;
    }

    private static string DescribeLastInput()
    {
        var map = Engine.Current?.InputInterface?.Actions;
        var last = map?.LastActivatedControl;
        return last.HasValue && last.Value.IsValid ? last.Value.Describe() : "-";
    }

    internal LocomotionController? GetLocalLocomotionController()
    {
        var userRoot = World?.LocalUser?.Root;
        if (userRoot == null)
            return null;
        return userRoot.GetRegisteredComponent<LocomotionController>() ?? userRoot.Slot?.GetComponent<LocomotionController>();
    }

    internal void SelectLocomotionModule(LocomotionModule module)
    {
        var locomotion = GetLocalLocomotionController();
        if (locomotion == null || !locomotion.IsModuleUsable(module))
            return;
        locomotion.ActivateModule(module);
        // A deliberate pick from Settings is a standing preference for future spawns, not just this
        // session - unlike the radial menu, which only ever changes the live module.
        EngineSettings.PreferredLocomotion = module.DisplayName;
    }

    private void MarkDirty() => _dashboard?.Slot.GetComponent<Canvas>()?.MarkDirty();
}
