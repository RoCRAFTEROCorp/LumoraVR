// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Core.Templates;

internal sealed class LocalHomeWorldTemplate : WorldTemplateDefinition
{
    public LocalHomeWorldTemplate() : base("LocalHome") { }

    protected override void Build(World world)
    {
        var spawnSlot = world.RootSlot.AddSlot("SpawnArea");
        spawnSlot.LocalPosition.Value = new float3(0f, 0.01f, 0f);
        spawnSlot.Tag.Value = "spawn";
        spawnSlot.AttachComponent<SimpleUserSpawn>();
        var spawnArea = spawnSlot.AttachComponent<CommonSpawnArea>();
        var spawnPoints = spawnSlot.AttachComponent<CirclePointGenerator>();
        spawnPoints.Radius.Value = 2f;
        spawnArea.SpawnPointGenerator.Target = spawnPoints;
        spawnSlot.AddSlot("Visual").AttachComponent<GlowCircle>()
            .Setup(2f, 0.15f,
                new colorHDR(0.85f, 0.83f, 0.78f, 0.3f), // faint white pool (alpha scales additive strength)
                new colorHDR(1.0f, 0.72f, 0.28f, 1f));   // gold ring

        var lightSlot = world.RootSlot.AddSlot("DirectionalLight");
        lightSlot.LocalPosition.Value = new float3(0f, 10f, 0f);
        lightSlot.LocalRotation.Value = floatQ.Euler(0.785f, -0.785f, 0f);
        var dirLight = lightSlot.AttachComponent<Light>();
        dirLight.Type.Value = LightType.Directional;
        dirLight.LightColor.Value = new color(1f, 0.96f, 0.84f, 1f);
        dirLight.Intensity.Value = 1.2f;
        dirLight.Shadows.Value = ShadowType.Soft;

        var skySlot = world.RootSlot.AddSlot("GradientSkybox");
        var skybox = skySlot.AttachComponent<GradientSkybox>();
        skybox.TopColor.Value = new color(0.070f, 0.060f, 0.150f, 1f);
        skybox.HorizonColor.Value = new color(0.46f, 0.38f, 0.58f, 1f);
        skybox.BottomColor.Value = new color(0.040f, 0.032f, 0.065f, 1f);
        skybox.SunColor.Value = new color(1.0f, 0.72f, 0.54f, 1f);
        skybox.SunDirection.Value = new float3(-0.38f, 0.58f, -0.72f);
        skybox.SunSize.Value = 0.034f;
        skybox.SunIntensity.Value = 2.4f;
        skybox.AmbientEnergy.Value = 0.48f;

        const float groundRadius = 25f;
        var groundSlot = world.RootSlot.AddSlot("Ground");
        groundSlot.LocalPosition.Value = new float3(0f, 0f, 0f);
        groundSlot.Tag.Value = "floor";

        var groundMesh = groundSlot.AttachComponent<CylinderMesh>();
        groundMesh.Radius.Value = groundRadius;
        float groundHeight = 0.04f;
        groundMesh.Height.Value = groundHeight;
        groundMesh.Segments.Value = 64;
        groundMesh.UVScale.Value = new float2(25f, 25f);

        // Sink the cylinder so its top face lands exactly at y=0 (otherwise spawned
        // users sink half-height into the floor). - xlinka
        groundSlot.LocalPosition.Value = new float3(0f, -groundHeight * 0.5f, 0f);

        var groundMaterial = groundSlot.AttachComponent<MetaballMaterial>();
        groundMaterial.BlendMode.Value = BlendMode.Transparent;
        groundMaterial.Culling.Value = Culling.Back;
        groundMaterial.TintA.Value = new colorHDR(0.90f, 1.00f, 0.94f, 1f);
        groundMaterial.TintB.Value = new colorHDR(1.00f, 0.58f, 0.88f, 1f);
        groundMaterial.WaterDeepColor.Value = new colorHDR(0.040f, 0.030f, 0.060f, 1f);
        groundMaterial.WaterSurfaceColor.Value = new colorHDR(0.10f, 0.085f, 0.13f, 1f);
        groundMaterial.RippleColor.Value = new colorHDR(0.72f, 1.00f, 0.92f, 1f);
        groundMaterial.BlobRadius.Value = 0.12f;
        groundMaterial.BlobSmoothness.Value = 0.075f;
        groundMaterial.BlobCount.Value = 72;
        groundMaterial.RiseSpeed.Value = 0.34f;
        // Match risingDiskRadius below so the ripple shader places its centers at
        // the same xz as the rising orbs. Off-by-half-a-meter is enough to make
        // ripples drift visibly from the bubbles. - xlinka
        groundMaterial.VolumeExtents.Value = new float2(24.0f, 24.0f);
        groundMaterial.VolumeHeight.Value = 3.4f;
        groundMaterial.VolumeOffset.Value = new float3(0f, groundHeight * 0.5f, 0f);
        groundMaterial.RimStrength.Value = 1.55f;
        groundMaterial.RimFalloff.Value = 2.8f;
        groundMaterial.FresnelPower.Value = 3.0f;
        groundMaterial.EmissionStrength.Value = 0.82f;
        groundMaterial.WaveStrength.Value = 0.28f;
        groundMaterial.RippleStrength.Value = 0.62f;
        groundMaterial.RippleRadius.Value = 0.58f;
        groundMaterial.RippleWidth.Value = 0.032f;
        groundMaterial.LineStrength.Value = 0.34f;
        groundMaterial.RenderQueue.Value = 10;

        var groundRenderer = groundSlot.AttachComponent<MeshRenderer>();
        groundRenderer.Mesh.Target = groundMesh;
        groundRenderer.Material.Target = groundMaterial;
        groundRenderer.ShadowCastMode.Value = ShadowCastMode.Off;

        // Decoupled from groundMaterial.VolumeHeight: that one positions the floor's
        // ripple-emitter blobs (invisible), this one is how high the visible orbs
        // actually float. Bumping VolumeHeight here without touching the ground
        // shader keeps the floor ripple cadence the same. - xlinka
        const float risingVolumeHeight = 7.0f;
        const float risingVolumeLift = 0.035f;
        // Stay inside groundRadius so polar-distributed blob centers never punch
        // past the cylinder rim. - xlinka
        const float risingDiskRadius = 24.0f;

        var risingBallsSlot = groundSlot.AddSlot("RisingBalls");
        risingBallsSlot.LocalPosition.Value = new float3(0f, groundHeight * 0.5f + risingVolumeHeight * 0.5f + risingVolumeLift, 0f);

        // Real geometry, one surface: a sphere, a neck and a floor ring per bubble, placed by the
        // material's vertex shader. The volume here has to match the material's or the culling box
        // is wrong. -xlinka
        var risingVolumeOffset = new float3(0f, -risingVolumeHeight * 0.5f - risingVolumeLift, 0f);
        var risingBallsMesh = risingBallsSlot.AttachComponent<BubbleFieldMesh>();
        risingBallsMesh.Count.Value = groundMaterial.BlobCount.Value;
        risingBallsMesh.VolumeExtents.Value = new float2(risingDiskRadius, risingDiskRadius);
        risingBallsMesh.VolumeHeight.Value = risingVolumeHeight;
        risingBallsMesh.VolumeOffset.Value = risingVolumeOffset;

        var risingBallsMaterial = risingBallsSlot.AttachComponent<LocalHomeRisingMaterial>();
        risingBallsMaterial.TintA.Value = groundMaterial.TintA.Value;
        risingBallsMaterial.TintB.Value = groundMaterial.TintB.Value;
        risingBallsMaterial.BlobRadius.Value = groundMaterial.BlobRadius.Value;
        risingBallsMaterial.BlobSmoothness.Value = groundMaterial.BlobSmoothness.Value;
        risingBallsMaterial.BlobCount.Value = groundMaterial.BlobCount.Value;
        risingBallsMaterial.RiseSpeed.Value = groundMaterial.RiseSpeed.Value;
        risingBallsMaterial.VolumeExtents.Value = new float2(risingDiskRadius, risingDiskRadius);
        risingBallsMaterial.VolumeHeight.Value = risingVolumeHeight;
        risingBallsMaterial.VolumeOffset.Value = risingVolumeOffset;
        risingBallsMaterial.RimStrength.Value = groundMaterial.RimStrength.Value;
        risingBallsMaterial.RimFalloff.Value = groundMaterial.RimFalloff.Value;
        risingBallsMaterial.FresnelPower.Value = groundMaterial.FresnelPower.Value;
        risingBallsMaterial.AlphaScale.Value = 0.92f;
        risingBallsMaterial.EmissionStrength.Value = 1.05f;
        risingBallsMaterial.TimeScale.Value = groundMaterial.TimeScale.Value;
        risingBallsMaterial.RippleColor.Value = groundMaterial.RippleColor.Value;
        risingBallsMaterial.RippleStrength.Value = groundMaterial.RippleStrength.Value;
        risingBallsMaterial.RippleRadius.Value = groundMaterial.RippleRadius.Value;
        risingBallsMaterial.RippleWidth.Value = groundMaterial.RippleWidth.Value;
        risingBallsMaterial.RenderQueue.Value = 35;

        var risingBallsRenderer = risingBallsSlot.AttachComponent<MeshRenderer>();
        risingBallsRenderer.Mesh.Target = risingBallsMesh;
        risingBallsRenderer.Material.Target = risingBallsMaterial;
        risingBallsRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
        risingBallsRenderer.SortingOrder.Value = 8;

        var dropletParticlesSlot = groundSlot.AddSlot("DropletParticles");
        dropletParticlesSlot.LocalPosition.Value = new float3(0f, groundHeight * 0.5f + 0.01f, 0f);
        var dropletParticles = dropletParticlesSlot.AttachComponent<ParticleSystem>();
        dropletParticles.MaxParticles.Value = 420;
        dropletParticles.EmissionRate.Value = 20f;
        dropletParticles.BurstCount.Value = 7;
        dropletParticles.BurstInterval.Value = 0.38f;
        dropletParticles.EmitterExtents.Value = new float3(24.2f, 0f, 24.2f);
        dropletParticles.SpawnHeight.Value = 0.035f;
        dropletParticles.Lifetime.Value = 0.74f;
        dropletParticles.LifetimeVariance.Value = 0.20f;
        dropletParticles.StartSize.Value = 0.070f;
        dropletParticles.EndSize.Value = 0.014f;
        dropletParticles.InitialSpeed.Value = 1.35f;
        dropletParticles.SpeedVariance.Value = 0.48f;
        dropletParticles.Spread.Value = 0.30f;
        dropletParticles.Gravity.Value = -1.18f;
        dropletParticles.StartColor.Value = new colorHDR(0.90f, 1.00f, 0.94f, 0.90f);
        dropletParticles.EndColor.Value = new colorHDR(1.00f, 0.58f, 0.88f, 0.0f);
        dropletParticles.EmissionStrength.Value = 1.65f;
        dropletParticles.RenderQueue.Value = 60;
        dropletParticles.Seed.Value = 1771;

        var groundCollider = groundSlot.AttachComponent<CylinderCollider>();
        groundCollider.Type.Value = ColliderType.Static;
        groundCollider.Radius.Value = groundMesh.Radius.Value;
        groundCollider.Height.Value = groundMesh.Height.Value;
        groundCollider.Offset.Value = float3.Zero;

        var ambientLightSlot = world.RootSlot.AddSlot("AmbientLight");
        ambientLightSlot.LocalPosition.Value = new float3(0f, 5f, 0f);
        var ambientLight = ambientLightSlot.AttachComponent<Light>();
        ambientLight.Type.Value = LightType.Point;
        ambientLight.LightColor.Value = new color(0.4f, 0.45f, 0.55f, 1f);
        ambientLight.Intensity.Value = 0.4f;
        ambientLight.Range.Value = 100f;
        ambientLight.Shadows.Value = ShadowType.None;

        var clipboardSlot = world.RootSlot.AddSlot("ClipboardImporter");
        clipboardSlot.AttachComponent<ClipboardImporter>();

        try
        {
            CreateHelioTestPanel(world);
        }
        catch (Exception ex)
        {
            // This is a local validation panel, not core world content. Do not let
            // it prevent LocalHome/userspace from starting.
            Lumora.Core.Logging.Logger.Warn($"LocalHome: Helio validation panel skipped: {ex}");
        }


        // Catch-all under the world. Bigger than ground so a user yeeted off-edge
        // still triggers respawn instead of falling forever. - xlinka
        var respawnSlot = world.RootSlot.AddSlot("RespawnPlane");
        var respawnPlane = respawnSlot.AttachComponent<RespawnPlane>();
        respawnPlane.Size.Value = new float2(100f, 100f);
        respawnPlane.Height.Value = -20f;
        respawnPlane.UseBounds.Value = false;
        respawnPlane.ShowVisual.Value = false;
        respawnPlane.ShowDebug.Value = false;
        respawnPlane.UserRespawnPosition.Value = new float3(0f, 1f, 0f);
    }

    private static void CreateHelioTestPanel(World world)
    {
        var panelSlot = world.RootSlot.AddSlot("HelioTestPanel");
        panelSlot.LocalPosition.Value = new float3(0f, 1.58f, -1.68f);
        panelSlot.LocalScale.Value = new float3(0.00120f, 0.00120f, 0.00120f);

        var fontSlot = panelSlot.AddSlot("UIFont");
        var font = fontSlot.AttachComponent<FontProvider>();
        font.URL.Value = new Uri("res://Assets/Fonts/FiraCode/FiraCode-SemiBold.ttf");
        font.FallbackURLs.Add(new Uri("res://Assets/Fonts/FiraCode/FiraCode-SemiBold.ttf"));

        var checkerSlot = panelSlot.AddSlot("UIValidationChecker");
        var checker = checkerSlot.AttachComponent<CheckerTextureProvider>();
        checker.Width.Value = 64;
        checker.Height.Value = 64;
        checker.CellSize.Value = 8;
        checker.ColorA.Value = new color(0.12f, 0.62f, 0.90f, 1f);
        checker.ColorB.Value = new color(0.95f, 0.30f, 0.56f, 1f);

        var checkerMaterialSlot = panelSlot.AddSlot("UIValidationCheckerMaterial");
        var checkerMaterial = checkerMaterialSlot.AttachComponent<UIUnlitMaterial>();
        checkerMaterial.Texture.Target = checker;
        checkerMaterial.Culling.Value = Culling.None;
        checkerMaterial.ZWrite.Value = ZWrite.Off;
        checkerMaterial.RenderQueue.Value = 3005;

        var panel = panelSlot.AttachComponent<PanelShell>();
        panel.Title.Value = "Helio Validation";
        panel.Size.Value = new float2(760f, 660f);
        panel.HeaderHeight.Value = 42f;
        panel.Padding.Value = 14f;
        panel.Font.Target = font;
        panel.BackgroundColor.Value = new color(0.018f, 0.022f, 0.030f, 0.88f);
        panel.HeaderColor.Value = new color(0.105f, 0.130f, 0.165f, 0.98f);

        panel.RebuildContent(ui =>
        {
            ui.Font(font);
            var layout = ui.VerticalLayout(8f, 10f);
            layout.ForceExpandHeight.Value = false;
            Fill(layout.RectTransform!);

            var status = ui.Text("Validation ready: hover/press, scroll clip, raw/tiled images, font fallback.", 15f, new color(0.92f, 0.97f, 1f, 1f));
            status.WordWrap.Value = true;
            status.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            Fill(status.RectTransform!);
            SetLayoutHeight(status.RectTransform!, 34f, 40f);

            // Handlers live on a component (not closures) so the panel's controls
            // survive duplication - a cloned panel drives its own labels.
            var actions = panel.Slot.GetComponent<HelioTestActions>() ?? panel.Slot.AttachComponent<HelioTestActions>();
            actions.Panel.Target = panel;
            actions.Status.Target = status;

            var shellButton = ui.Button("Shell color update", actions.OnShellPressed, new color(0.18f, 0.34f, 0.48f, 0.96f));
            Fill(shellButton.RectTransform!);
            SetLayoutHeight(shellButton.RectTransform!, 34f, 36f);

            var button = ui.Button("Laser click test", actions.OnLaserPressed, new color(0.16f, 0.30f, 0.44f, 0.96f));
            Fill(button.RectTransform!);
            SetLayoutHeight(button.RectTransform!, 34f, 36f);

            var checkboxRow = ui.Panel(new color(0.052f, 0.060f, 0.074f, 0.92f));
            Fill(checkboxRow.RectTransform!);
            SetLayoutHeight(checkboxRow.RectTransform!, 32f, 34f);
            ui.Nest();
            var checkboxSplits = ui.SplitHorizontally(0.58f, 0.04f, 0.38f);
            ui.NestInto(checkboxSplits[0]);
            var checkboxLabel = ui.Text("Checkbox", 14f, new color(0.94f, 0.96f, 1f, 1f));
            checkboxLabel.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            Fill(checkboxLabel.RectTransform!);
            ui.NestOut();
            ui.NestInto(checkboxSplits[2]);
            var checkbox = ui.Checkbox(false, actions.OnCheckboxChanged, new color(0.78f, 0.82f, 0.88f, 1f));
            FixedRect(checkbox.RectTransform!, new float2(0.5f, 0.5f), new float2(24f, 24f));
            ui.NestOut();
            ui.NestOut();

            var sliderRow = ui.Panel(new color(0.052f, 0.060f, 0.074f, 0.92f));
            Fill(sliderRow.RectTransform!);
            SetLayoutHeight(sliderRow.RectTransform!, 32f, 34f);
            ui.Nest();
            var sliderSplits = ui.SplitHorizontally(0.42f, 0.04f, 0.54f);
            ui.NestInto(sliderSplits[0]);
            var sliderLabel = ui.Text("Slider", 14f, new color(0.94f, 0.96f, 1f, 1f));
            sliderLabel.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            Fill(sliderLabel.RectTransform!);
            ui.NestOut();
            ui.NestInto(sliderSplits[2]);
            var slider = ui.Slider(0.35f, 0f, 1f, actions.OnSliderChanged, new color(0.18f, 0.24f, 0.30f, 0.96f));
            Fill(slider.RectTransform!);
            ui.NestOut();
            ui.NestOut();

            // Opt out of auto-fit: this validation panel sets an explicit oversized content height below
            // (ConfigureScrollContent) to demonstrate clipping, so it manages its own content rect. -xlinka
            var scroll = ui.ScrollRect(out var scrollContent, new float2(1f, 1f), new color(0.045f, 0.060f, 0.080f, 0.90f), fitVertical: false);
            Fill(scroll.RectTransform!);
            SetLayoutHeight(scroll.RectTransform!, 150f, 190f, 1f);
            const float scrollContentHeight = 260f;
            ConfigureScrollContent(scrollContent, scrollContentHeight);
            scroll.NormalizedPosition = new float2(0f, 0.2f); // start ~20% scrolled to show the clip
            scroll.ScrollChanged += (_, value) =>
            {
                status.Content.Value = $"Scroll clip y={value.y:0}";
            };

            var scrollUi = new UIBuilder(scrollContent.Slot);
            scrollUi.Font(font);
            var scrollLayout = scrollUi.VerticalLayout(4f, 6f);
            scrollLayout.ForceExpandHeight.Value = false;
            Fill(scrollLayout.RectTransform!);
            for (int i = 1; i <= 9; i++)
            {
                var row = scrollUi.Text($"clipped scroll row {i:00} - content should stay inside the mask", 14f, new color(0.92f, 0.95f, 1f, 1f));
                row.VerticalAlignment.Value = TextVerticalAlignment.Middle;
                Fill(row.RectTransform!);
                SetLayoutHeight(row.RectTransform!, 22f, 24f);
            }
            scrollUi.NestOut();

            var imagePanel = ui.Panel(new color(0.045f, 0.052f, 0.066f, 0.92f));
            Fill(imagePanel.RectTransform!);
            SetLayoutHeight(imagePanel.RectTransform!, 80f, 88f);
            ui.Nest();
            var imageLayout = ui.HorizontalLayout(8f, 8f);
            Fill(imageLayout.RectTransform!);
            var raw = ui.RawImage(null, color.White, Rect.UnitRect, false);
            raw.Material.Target = checkerMaterial;
            Fill(raw.RectTransform!);
            var tiled = ui.TiledRawImage(null, color.White, new float2(24f, 24f), new float2(8f, 6f));
            tiled.Material.Target = checkerMaterial;
            Fill(tiled.RectTransform!);
            ui.NestOut();
            ui.NestOut();

            // STENCIL MASK VALIDATION: a Mask with StencilMasking on, shaped by a fully-rounded texture, over a
            // checker fill. If the checker renders with ROUNDED ends (not a hard square), GPU stencil masking
            // works end-to-end in the dashboard SubViewport. A square rect-clip cannot produce this. -xlinka
            var stencilNote = ui.Text("Stencil mask: the checker below should clip to a ROUNDED shape (not a square).", 13f, new color(0.82f, 0.96f, 0.88f, 1f));
            stencilNote.WordWrap.Value = true;
            stencilNote.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            Fill(stencilNote.RectTransform!);
            SetLayoutHeight(stencilNote.RectTransform!, 30f, 36f);

            var shapeSlot = panelSlot.AddSlot("StencilMaskShape");
            var shapeTex = shapeSlot.AttachComponent<RoundedRectTextureProvider>();
            shapeTex.Size.Value = 96;
            shapeTex.Radius.Value = 48; // radius = size/2 -> fully rounded

            var stencilPanel = ui.Panel(new color(0.045f, 0.052f, 0.066f, 0.92f));
            Fill(stencilPanel.RectTransform!);
            SetLayoutHeight(stencilPanel.RectTransform!, 96f, 104f);
            ui.Nest();
            var maskHost = ui.HorizontalLayout(8f, 8f);
            Fill(maskHost.RectTransform!);
            var stencilMask = ui.Mask(color.White, showMaskGraphic: false);
            stencilMask.StencilMasking.Value = true;
            var maskShapeImage = stencilMask.Slot.GetComponent<Image>();
            if (maskShapeImage != null) maskShapeImage.Texture.Target = shapeTex; // the SHAPE stamped into the stencil
            Fill(stencilMask.RectTransform!);
            ui.Nest();
            var maskedFill = ui.RawImage(checker, color.White, Rect.UnitRect, false); // checker, clipped to the rounded shape
            Fill(maskedFill.RectTransform!);
            ui.NestOut(); // out of mask
            ui.NestOut(); // out of stencilPanel

            var fallback = ui.Text("FontSet fallback path: ASCII + symbols <> [] {} + missing glyph fallback", 14f, new color(0.88f, 0.82f, 0.96f, 1f));
            fallback.WordWrap.Value = true;
            fallback.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            Fill(fallback.RectTransform!);
            SetLayoutHeight(fallback.RectTransform!, 34f, 42f);

            ui.NestOut();
        });
    }

    private static void Fill(RectTransform rect)
    {
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;
    }

    private static void FixedRect(RectTransform rect, float2 anchor, float2 size)
    {
        rect.AnchorMin.Value = anchor;
        rect.AnchorMax.Value = anchor;
        rect.OffsetMin.Value = size * -0.5f;
        rect.OffsetMax.Value = size * 0.5f;
    }

    private static void ConfigureScrollContent(RectTransform rect, float contentHeight)
    {
        rect.AnchorMin.Value = new float2(0f, 1f);
        rect.AnchorMax.Value = new float2(1f, 1f);
        rect.OffsetMin.Value = new float2(0f, -contentHeight);
        rect.OffsetMax.Value = float2.Zero;
    }

    private static void SetLayoutHeight(RectTransform rect, float minHeight, float preferredHeight, float flexibleHeight = 0f)
    {
        var element = rect.Slot.GetComponent<LayoutElement>() ?? rect.Slot.AttachComponent<LayoutElement>();
        element.MinHeight.Value = minHeight;
        element.PreferredHeight.Value = preferredHeight;
        element.FlexibleHeight.Value = flexibleHeight;
    }
}
