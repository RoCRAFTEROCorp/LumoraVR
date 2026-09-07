// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

/// <summary>
/// Physically-Based Rendering material using the specular workflow.
/// Maps to StandardMaterial3D in Godot with specular color controls.
/// </summary>
[ComponentCategory("Assets/Materials")]
public class PBS_Specular : MaterialProvider, ICommonMaterial
{
    // TEXTURE TRANSFORM
    public readonly Sync<float2> TextureScale;
    public readonly Sync<float2> TextureOffset;

    // ALBEDO
    public readonly Sync<colorHDR> AlbedoColor;
    public readonly AssetRef<TextureAsset> AlbedoTexture;

    // SPECULAR
    // Smoothness is the ALPHA of SpecularColor, not a member of its own - the specular workflow packs
    // the two together everywhere else too (the map's RGBA carries exactly the same pair), and a
    // separate field would be a second source of truth that imported material data has no way to fill.
    public readonly Sync<colorHDR> SpecularColor;
    public readonly AssetRef<TextureAsset> SpecularMap;

    // NORMAL MAP
    public readonly AssetRef<TextureAsset> NormalMap;
    public readonly Sync<float> NormalScale;

    // EMISSION
    public readonly Sync<colorHDR> EmissiveColor;
    public readonly AssetRef<TextureAsset> EmissiveMap;

    // OCCLUSION
    public readonly AssetRef<TextureAsset> OcclusionMap;

    // BLEND SETTINGS
    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<float> AlphaCutoff;
    public readonly Sync<Culling> Culling;
    public readonly Sync<int> RenderQueue;

    protected override MaterialType MaterialType => MaterialType.PBS_Specular;

    public float Smoothness
    {
        get => SpecularColor.Value.a;
        set
        {
            var c = SpecularColor.Value;
            SpecularColor.Value = new colorHDR(c.r, c.g, c.b, value);
        }
    }

    // ICommonMaterial
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

    public PBS_Specular()
    {
        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);

        AlbedoColor = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture = new AssetRef<TextureAsset>(this);

        SpecularColor = new Sync<colorHDR>(this, new colorHDR(0.5f, 0.5f, 0.5f, 0.5f));
        SpecularMap = new AssetRef<TextureAsset>(this);

        NormalMap = new AssetRef<TextureAsset>(this);
        NormalScale = new Sync<float>(this, 1.0f);

        EmissiveColor = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveMap = new AssetRef<TextureAsset>(this);

        OcclusionMap = new AssetRef<TextureAsset>(this);

        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Opaque);
        AlphaCutoff = new Sync<float>(this, 0.5f);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetFloat("AlphaCutoff", AlphaCutoff.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);

        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);

        asset.SetColor("AlbedoColor", AlbedoColor.Value);
        asset.SetTexture("AlbedoTexture", AlbedoTexture.Asset);

        // The specular workflow goes to a shader of its own. It used to be squeezed into the metallic
        // material, which meant the specular map landed in the metallic slot and was then read as a
        // glTF metallic-roughness pack - blue channel as metalness, with Metallic pinned to 1. A skin
        // or fur surface authored with a coloured specular tint came out fully metal and near-black.
        asset.SetColor("SpecularColor", SpecularColor.Value);
        asset.SetTexture("SpecularMap", SpecularMap.Asset);
        asset.SetBool("UseSpecularMap", SpecularMap.Asset != null);

        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetBool("UseNormalMap", NormalMap.Asset != null);
        asset.SetFloat("NormalScale", NormalScale.Value);

        asset.SetColor("EmissiveColor", EmissiveColor.Value);
        asset.SetTexture("EmissiveMap", EmissiveMap.Asset);
        asset.SetBool("UseEmissiveMap", EmissiveMap.Asset != null);

        asset.SetTexture("OcclusionMap", OcclusionMap.Asset);
        asset.SetBool("UseOcclusionMap", OcclusionMap.Asset != null);
    }
}
