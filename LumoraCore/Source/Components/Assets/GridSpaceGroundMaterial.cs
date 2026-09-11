// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Void-grid floor, backed by res://Shaders/GridSpaceGround.gdshader. Member names map 1:1 onto the
// shader uniforms through the hook's snake_case conversion. No BlendMode or Culling members: both
// are compile-time render_modes in that shader, so a toggle here would be a lying checkbox. -xlinka
[ComponentCategory("Assets/Materials")]
public class GridSpaceGroundMaterial : MaterialProvider
{
    public readonly Sync<colorHDR> BaseNearColor;
    public readonly Sync<colorHDR> BaseFarColor;
    public readonly Sync<float> SelfLit;
    public readonly Sync<float> LightResponse;

    public readonly Sync<bool> WorldSpaceGrid;
    public readonly Sync<float2> GridOffset;
    public readonly Sync<float> MinorScale;
    public readonly Sync<float> MajorScale;
    public readonly Sync<colorHDR> MinorLineColor;
    public readonly Sync<colorHDR> MajorLineColor;
    public readonly Sync<float> MinorLineWidth;
    public readonly Sync<float> MajorLineWidth;
    public readonly Sync<float> LineSoftness;
    public readonly Sync<float> MinorFadePixels;
    public readonly Sync<float> MajorFadePixels;

    public readonly Sync<colorHDR> SheenColor;
    public readonly Sync<float> SheenStrength;
    public readonly Sync<float> SheenPower;
    public readonly Sync<colorHDR> HorizonColor;
    public readonly Sync<float> HorizonStart;
    public readonly Sync<float> HorizonEnd;

    public readonly Sync<int> RenderQueue;

    protected override MaterialType MaterialType => MaterialType.GridSpaceGround;

    public GridSpaceGroundMaterial()
    {
        BaseNearColor = new Sync<colorHDR>(this, new colorHDR(0.110f, 0.120f, 0.145f, 1f));
        BaseFarColor = new Sync<colorHDR>(this, new colorHDR(0.090f, 0.100f, 0.125f, 1f));
        SelfLit = new Sync<float>(this, 1.0f);
        LightResponse = new Sync<float>(this, 1.0f);

        WorldSpaceGrid = new Sync<bool>(this, true);
        GridOffset = new Sync<float2>(this, float2.Zero);
        MinorScale = new Sync<float>(this, 1.0f);
        MajorScale = new Sync<float>(this, 5.0f);
        MinorLineColor = new Sync<colorHDR>(this, new colorHDR(0.200f, 0.215f, 0.250f, 1f));
        MajorLineColor = new Sync<colorHDR>(this, new colorHDR(0.330f, 0.370f, 0.460f, 1f));
        MinorLineWidth = new Sync<float>(this, 1.0f);
        MajorLineWidth = new Sync<float>(this, 1.5f);
        LineSoftness = new Sync<float>(this, 1.0f);
        MinorFadePixels = new Sync<float>(this, 10.0f);
        MajorFadePixels = new Sync<float>(this, 8.0f);

        SheenColor = new Sync<colorHDR>(this, new colorHDR(0.55f, 0.62f, 0.75f, 1f));
        SheenStrength = new Sync<float>(this, 0.05f);
        SheenPower = new Sync<float>(this, 3.0f);
        // Neutral grey on purpose: every world that uses this floor sets HorizonColor to its own
        // sky's horizon, and a default that happened to match one sky would silently mismatch the rest.
        HorizonColor = new Sync<colorHDR>(this, new colorHDR(0.5f, 0.5f, 0.5f, 1f));
        HorizonStart = new Sync<float>(this, 12.0f);
        HorizonEnd = new Sync<float>(this, 90.0f);

        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetFloat("RenderQueue", RenderQueue.Value);

        asset.SetColor("BaseNearColor", BaseNearColor.Value);
        asset.SetColor("BaseFarColor", BaseFarColor.Value);
        asset.SetFloat("SelfLit", SelfLit.Value);
        asset.SetFloat("LightResponse", LightResponse.Value);

        asset.SetBool("WorldSpaceGrid", WorldSpaceGrid.Value);
        asset.SetFloat2("GridOffset", GridOffset.Value);
        asset.SetFloat("MinorScale", MinorScale.Value);
        asset.SetFloat("MajorScale", MajorScale.Value);
        asset.SetColor("MinorLineColor", MinorLineColor.Value);
        asset.SetColor("MajorLineColor", MajorLineColor.Value);
        asset.SetFloat("MinorLineWidth", MinorLineWidth.Value);
        asset.SetFloat("MajorLineWidth", MajorLineWidth.Value);
        asset.SetFloat("LineSoftness", LineSoftness.Value);
        asset.SetFloat("MinorFadePixels", MinorFadePixels.Value);
        asset.SetFloat("MajorFadePixels", MajorFadePixels.Value);

        asset.SetColor("SheenColor", SheenColor.Value);
        asset.SetFloat("SheenStrength", SheenStrength.Value);
        asset.SetFloat("SheenPower", SheenPower.Value);
        asset.SetColor("HorizonColor", HorizonColor.Value);
        asset.SetFloat("HorizonStart", HorizonStart.Value);
        asset.SetFloat("HorizonEnd", HorizonEnd.Value);
    }

    // The floor under your feet is the near base colour; the lines are detail on top of it.
    public override bool TryGetPrimaryColor(out colorHDR color)
    {
        color = BaseNearColor.Value;
        return true;
    }
}
