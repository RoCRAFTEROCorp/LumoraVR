// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// The toon workhorse imported avatar content expects: ramp shading over a wrapped N.L, a rim band, an
// inverted-hull outline, a matcap, emission and a metallic/smoothness pair, all on one material.
//
// It is LIT, not unshaded. The ramp only decides how the light it receives is quantised, so a world
// with no lights leaves the surface on ambient alone and a bright world drives it up the ramp. That is
// the whole reason an avatar reads as part of the room instead of a sticker on it.
//
// FlatToonMaterial stays as it is: a small banded cel material with nothing on it but bands, a rim and
// an outline. This one supersedes it in capability, not in place, and saved FlatToon materials keep
// loading as what they were. -xlinka
[ComponentCategory("Assets/Materials")]
public class ToonMaterial : MaterialProvider, ICommonMaterial
{
    [Group("Albedo")]
    public readonly Sync<colorHDR> AlbedoColor;

    [Group("Albedo")]
    public readonly AssetRef<TextureAsset> AlbedoTexture;

    [Group("Albedo")]
    public readonly Sync<float2> TextureScale;

    [Group("Albedo")]
    public readonly Sync<float2> TextureOffset;

    [Group("Normal")]
    public readonly AssetRef<TextureAsset> NormalMap;

    [Group("Normal")]
    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> NormalScale;

    // Replaces the two-tone fallback outright when set: wrapped N.L drives U and the texel IS the light
    // response, which is how a creator-authored ramp (a hard break with a warm bounce under it) survives
    // being brought over.
    [Group("Shading")]
    public readonly AssetRef<TextureAsset> ShadowRamp;

    // Multiplies the light where it lands.
    [Group("Shading")]
    public readonly Sync<colorHDR> LightTint;

    // And where it does not. A cool grey here is the usual anime shadow; black kills the shape.
    [Group("Shading")]
    public readonly Sync<colorHDR> ShadowTint;

    // Where the terminator sits in wrapped N.L, and how wide the crossfade at it is. Both are in
    // wrapped N.L units, not pixels, so the edge does not crawl with distance.
    [Group("Shading")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> ShadowBoundary;

    [Group("Shading")]
    [Range(0.001f, 0.5f, "0.000")]
    public readonly Sync<float> ShadowSoftness;

    [Group("Rim")]
    public readonly Sync<colorHDR> RimColor;

    [Group("Rim")]
    [Range(0.5f, 16f, "0.0")]
    public readonly Sync<float> RimPower;

    // Cuts everything below it off the fresnel, which is what turns a soft glow into a drawn line.
    [Group("Rim")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> RimBoundary;

    [Group("Outline")]
    public readonly Sync<colorHDR> OutlineColor;

    // Metres of hull extrusion at unit scale. 0 removes the outline pass.
    [Group("Outline")]
    [Range(0f, 0.1f, "0.000")]
    public readonly Sync<float> OutlineWidth;

    [Group("Matcap")]
    public readonly AssetRef<TextureAsset> MatcapTexture;

    [Group("Matcap")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> MatcapStrength;

    // Additive rides over the shading as a sheen; multiply bakes into albedo, which is what a
    // baked-metal or skin matcap wants.
    [Group("Matcap")]
    public readonly Sync<bool> MatcapAdditive;

    [Group("Emission")]
    public readonly Sync<colorHDR> EmissionColor;

    [Group("Emission")]
    public readonly AssetRef<TextureAsset> EmissionMap;

    [Group("Emission")]
    [Range(0f, 16f, "0.00")]
    public readonly Sync<float> EmissionStrength;

    [Group("Specular")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Metallic;

    [Group("Specular")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Smoothness;

    // glTF packing: metallic in B, roughness in G. When one is set it drives both channels outright and
    // the two scalars above are ignored, because a 0 metallic scalar would otherwise multiply the whole
    // map away. -xlinka
    [Group("Specular")]
    public readonly AssetRef<TextureAsset> MetallicMap;

    [Group("Rendering")]
    public readonly Sync<AlphaMode> AlphaMode;

    [Group("Rendering")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> AlphaCutoff;

    [Group("Rendering")]
    public readonly Sync<Culling> Culling;

    [Group("Rendering")]
    public readonly Sync<int> RenderQueue;

    protected override MaterialType MaterialType => MaterialType.Toon;

    public colorHDR Color
    {
        get => AlbedoColor.Value;
        set => AlbedoColor.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => AlbedoTexture.Target;
        set => AlbedoTexture.Target = value;
    }

    public ToonMaterial()
    {
        AlbedoColor = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture = new AssetRef<TextureAsset>(this);
        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);

        NormalMap = new AssetRef<TextureAsset>(this);
        NormalScale = new Sync<float>(this, 1f);

        ShadowRamp = new AssetRef<TextureAsset>(this);
        LightTint = new Sync<colorHDR>(this, colorHDR.White);
        ShadowTint = new Sync<colorHDR>(this, new colorHDR(0.42f, 0.45f, 0.58f, 1f));
        ShadowBoundary = new Sync<float>(this, 0.5f);
        ShadowSoftness = new Sync<float>(this, 0.05f);

        RimColor = new Sync<colorHDR>(this, colorHDR.Black);
        RimPower = new Sync<float>(this, 4f);
        RimBoundary = new Sync<float>(this, 0f);

        OutlineColor = new Sync<colorHDR>(this, colorHDR.Black);
        OutlineWidth = new Sync<float>(this, 0f);

        MatcapTexture = new AssetRef<TextureAsset>(this);
        MatcapStrength = new Sync<float>(this, 1f);
        MatcapAdditive = new Sync<bool>(this, true);

        EmissionColor = new Sync<colorHDR>(this, colorHDR.Black);
        EmissionMap = new AssetRef<TextureAsset>(this);
        EmissionStrength = new Sync<float>(this, 1f);

        Metallic = new Sync<float>(this, 0f);
        // Zero, not the PBS set default of 0.25: a banded highlight at low gloss is a wide flat white
        // disc across the whole lit side, and every imported avatar would arrive wearing one. Turning
        // this up gives a tighter, deliberate toon highlight. -xlinka
        Smoothness = new Sync<float>(this, 0f);
        MetallicMap = new AssetRef<TextureAsset>(this);

        AlphaMode = new Sync<AlphaMode>(this, Assets.AlphaMode.Opaque);
        AlphaCutoff = new Sync<float>(this, 0.5f);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        // Culling is a compile-time render_mode, so this swaps the shader variant rather than setting a
        // uniform. It runs first: the swap keeps every uniform on the material, but the pending property
        // pushes below have to land on whatever shader ends up bound.
        asset.SetCulling(Culling.Value);

        asset.SetColor("AlbedoColor", AlbedoColor.Value);
        asset.SetTexture("AlbedoTexture", AlbedoTexture.Asset);
        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);

        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetBool("UseNormalMap", NormalMap.Asset != null);
        asset.SetFloat("NormalScale", NormalScale.Value);

        asset.SetTexture("ShadowRamp", ShadowRamp.Asset);
        asset.SetBool("UseShadowRamp", ShadowRamp.Asset != null);
        asset.SetColor("LightTint", LightTint.Value);
        asset.SetColor("ShadowTint", ShadowTint.Value);
        asset.SetFloat("ShadowBoundary", ShadowBoundary.Value);
        asset.SetFloat("ShadowSoftness", System.Math.Max(ShadowSoftness.Value, 0.001f));

        asset.SetColor("RimColor", RimColor.Value);
        asset.SetFloat("RimPower", RimPower.Value);
        asset.SetFloat("RimBoundary", RimBoundary.Value);

        asset.SetColor("OutlineColor", OutlineColor.Value);
        // Mat_ToonOutline extrudes the hull in MODEL SPACE METRES, so 0.1 is a ten centimetre shell,
        // not a thick line. Millimetres is the useful range. Same unit trap as XiexeToonMaterial. -xlinka
        asset.SetFloat("OutlineWidth", System.Math.Clamp(OutlineWidth.Value, 0f, 0.005f));

        asset.SetTexture("MatcapTexture", MatcapTexture.Asset);
        asset.SetBool("UseMatcapTexture", MatcapTexture.Asset != null);
        asset.SetFloat("MatcapStrength", MatcapStrength.Value);
        asset.SetBool("MatcapAdditive", MatcapAdditive.Value);

        asset.SetColor("EmissionColor", EmissionColor.Value);
        asset.SetTexture("EmissionMap", EmissionMap.Asset);
        asset.SetBool("UseEmissionMap", EmissionMap.Asset != null);
        asset.SetFloat("EmissionStrength", EmissionStrength.Value);

        asset.SetFloat("Metallic", Metallic.Value);
        asset.SetFloat("Smoothness", Smoothness.Value);
        asset.SetTexture("MetallicMap", MetallicMap.Asset);
        asset.SetBool("UseMetallicMap", MetallicMap.Asset != null);

        asset.SetInt("AlphaMode", (int)AlphaMode.Value);
        asset.SetFloat("AlphaCutoff", AlphaCutoff.Value);
        asset.SetInt("RenderQueue", RenderQueue.Value);
    }
}
