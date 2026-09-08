// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using Helio.UI;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Import;

[ComponentCategory("Assets/Import")]
public sealed class ModelImportDialog : ImportDialog
{
    public readonly Sync<float> Scale;
    public readonly Sync<bool> AutoScale;
    public readonly Sync<ModelMaterialType> Material;
    public readonly Sync<bool> Colliders;
    public readonly Sync<bool> AllowGrabbable;
    public readonly Sync<bool> Scalable;
    public readonly Sync<bool> ImportAtOrigin;
    public readonly Sync<bool> CalculateNormals;
    public readonly Sync<bool> CalculateTangents;
    public readonly Sync<bool> ImportVertexColors;
    public readonly Sync<bool> ImportAlbedoColor;
    public readonly Sync<bool> ImportEmissive;
    public readonly Sync<bool> MakeDualSided;
    public readonly Sync<bool> MakeFlatShaded;
    public readonly Sync<int> MaxTextureSize;
    public readonly Sync<bool> ForceNoMipMaps;
    public readonly Sync<bool> ImportAnimations;

    protected override string TitleText => "Model Import";
    protected override float2 CanvasSize => new float2(420f, 640f);

    public ModelImportDialog()
    {
        Scale = new Sync<float>(this, 1f);
        AutoScale = new Sync<bool>(this, false);
        Material = new Sync<ModelMaterialType>(this, ModelMaterialType.Lit);
        Colliders = new Sync<bool>(this, true);
        AllowGrabbable = new Sync<bool>(this, true);
        Scalable = new Sync<bool>(this, true);
        ImportAtOrigin = new Sync<bool>(this, false);
        CalculateNormals = new Sync<bool>(this, true);
        CalculateTangents = new Sync<bool>(this, true);
        ImportVertexColors = new Sync<bool>(this, false);
        ImportAlbedoColor = new Sync<bool>(this, true);
        ImportEmissive = new Sync<bool>(this, true);
        MakeDualSided = new Sync<bool>(this, false);
        MakeFlatShaded = new Sync<bool>(this, false);
        MaxTextureSize = new Sync<int>(this, -1);
        ForceNoMipMaps = new Sync<bool>(this, false);
        ImportAnimations = new Sync<bool>(this, true);
    }

    private void DefaultPreset()
    {
        Material.Value = ModelMaterialType.Lit;
        Scale.Value = 1f;
        AutoScale.Value = false;
        Colliders.Value = true;
        CalculateNormals.Value = true;
        CalculateTangents.Value = true;
        ImportAlbedoColor.Value = true;
        ImportEmissive.Value = true;
        AllowGrabbable.Value = true;
        Scalable.Value = true;
    }

    protected override void OpenRoot(UIBuilder ui)
    {
        var body = SetupSection(ui, "How should this model be imported?", backButton: false);
        SetupGrid(body);
        GridButton(body, "Regular", PresetRegular);
        GridButton(body, "Vertex Color", PresetVertexColor);
        GridButton(body, "Advanced", () => OpenPage(MenuCustom));
        GridButton(body, "As Raw File", AsRawFile, BackColor);
    }

    public void PresetRegular()
    {
        DefaultPreset();
        OpenPage(MenuScale);
    }

    public void PresetVertexColor()
    {
        DefaultPreset();
        ImportVertexColors.Value = true;
        OpenPage(MenuScale);
    }

    private void MenuScale(UIBuilder ui)
    {
        var body = SetupSection(ui, "Units");
        SetupGrid(body);
        GridButton(body, "Auto", () => SetScaleAndOpenFinish(1f, true));
        GridButton(body, "Humanoid", () => SetScaleAndOpenFinish(1.8f, true));
        GridButton(body, "Meters", () => SetScaleAndOpenFinish(1f, false));
        GridButton(body, "Millimeters", () => SetScaleAndOpenFinish(0.001f, false));
        GridButton(body, "Centimeters", () => SetScaleAndOpenFinish(0.01f, false));
        GridButton(body, "Inches", () => SetScaleAndOpenFinish(0.0254f, false));
    }

    private void SetScaleAndOpenFinish(float scale, bool autoScale)
    {
        Scale.Value = scale;
        AutoScale.Value = autoScale;
        OpenPage(MenuFinish);
    }

    private void MenuFinish(UIBuilder ui)
    {
        var body = SetupSection(ui, "Finalize");
        SetupGrid(body);
        GridButton(body, "Run Import", RunImport);
        GridButton(body, "Advanced", () => OpenPage(MenuCustom));
    }

    private void MenuCustom(UIBuilder ui)
    {
        var body = SetupSection(ui, "Advanced Settings");
        // The body lays its children out now, so the scroll area has to ask for the leftover height.
        // Without this it takes its preferred height, which for a scroll viewport is nothing. -xlinka
        body.PushStyle().FlexibleHeight(1f);
        body.ScrollRect(out _, fitVertical: false);
        body.PopStyle();
        body.VerticalLayout(4f, 4f);

        SetupCheckbox(body, AutoScale, "Auto Scale");
        SetupCheckbox(body, CalculateNormals, "Calculate Normals");
        SetupCheckbox(body, CalculateTangents, "Calculate Tangents");
        SetupCheckbox(body, ImportVertexColors, "Import Vertex Colors");
        SetupCheckbox(body, ImportAlbedoColor, "Import Albedo Color");
        SetupCheckbox(body, ImportEmissive, "Import Emissive");
        SetupCheckbox(body, Colliders, "Generate Colliders");
        SetupCheckbox(body, ImportAnimations, "Import Animations");
        SetupCheckbox(body, MakeDualSided, "Dual Sided");
        SetupCheckbox(body, MakeFlatShaded, "Flat Shaded");
        SetupCheckbox(body, ForceNoMipMaps, "No Texture Mipmaps");
        SetupCheckbox(body, AllowGrabbable, "Grabbable");
        SetupCheckbox(body, Scalable, "Scalable");
        SetupCheckbox(body, ImportAtOrigin, "Position At Origin");

        body.PushStyle().MinHeight(36f).PreferredHeight(36f);
        body.Button("Run Import", (_, _) => { if (CanInteract) RunImport(); }, ButtonFill);
        body.PopStyle();
    }

    public void RunImport()
    {
        if (!CanInteract) return;
        Logger.Log($"ModelImportDialog: scale={Scale.Value} auto={AutoScale.Value} material={Material.Value} colliders={Colliders.Value}");

        int rowSize = (int)MathF.Max(1f, MathF.Ceiling(MathF.Sqrt(Paths.Count)));
        int index = 0;
        var basePos = Slot.GlobalPosition;
        var baseRot = Slot.GlobalRotation;
        var handler = ImportHandlers.Model;
        var request = BuildRequest();
        var target = ResolveTargetWorld();

        foreach (var file in Paths)
        {
            var s = target.RootSlot.AddSlot(Path.GetFileName(file) ?? file);
            if (!ImportAtOrigin.Value)
            {
                var offset = UniversalImporter.GridOffset(ref index, rowSize);
                s.GlobalPosition = basePos + baseRot * offset;
                s.GlobalRotation = baseRot;
            }
            s.GlobalScale = float3.One * Scale.Value;

            if (handler != null)
            {
                var pathCaptured = file;
                // Owned by the slot the model imports into, not dropped on the floor. The dialog destroys
                // itself on the next line, so it cannot be the owner; the target can, and that is the
                // thing whose disappearance should stop the import. A discarded Task also swallowed every
                // exception the import threw - this one is watched and logged. -xlinka
                s.StartTask(() => handler.ImportAsync(s, pathCaptured, request));
            }
            else
            {
                var label = s.AttachComponent<TextRenderer>();
                label.Text.Value = Path.GetFileName(file) ?? file;
                label.Size.Value = 0.08f;
                if (AllowGrabbable.Value)
                {
                    var g = s.AttachComponent<Grabbable>();
                    g.AllowGrab.Value = true;
                    g.Scalable.Value = Scalable.Value;
                }
            }
        }

        Slot.Destroy();
    }

    private ModelImportRequest BuildRequest()
    {
        return new ModelImportRequest
        {
            Scale = Scale.Value,
            AutoScale = AutoScale.Value,
            Material = Material.Value,
            Colliders = Colliders.Value,
            Grabbable = AllowGrabbable.Value,
            Scalable = Scalable.Value,
            ImportAtOrigin = ImportAtOrigin.Value,
            CalculateNormals = CalculateNormals.Value,
            CalculateTangents = CalculateTangents.Value,
            ImportVertexColors = ImportVertexColors.Value,
            ImportAlbedoColor = ImportAlbedoColor.Value,
            ImportEmissive = ImportEmissive.Value,
            MakeDualSided = MakeDualSided.Value,
            MakeFlatShaded = MakeFlatShaded.Value,
            MaxTextureSize = MaxTextureSize.Value,
            ForceNoMipMaps = ForceNoMipMaps.Value,
            ImportAnimations = ImportAnimations.Value,
        };
    }
}
