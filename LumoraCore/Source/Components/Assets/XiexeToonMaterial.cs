// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
//
// The shading model this drives is ported from Xiexe's Unity Shaders (XSToon),
// MIT licensed, Copyright (c) 2019 Xiexe. See LumoraGodot/Shaders/Xiexe_Core.gdshaderinc.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// A denser toon model than the plain ToonMaterial: ramp shading plus a lit rim AND a shadow-side rim,
// subsurface bleed through thin geometry, a sharpenable specular lobe, matcap, occlusion tinting and an
// inverted-hull outline.
//
// Deliberately a SEPARATE component rather than more knobs on ToonMaterial. That one is our own simpler
// model and content already depends on how it looks; widening it to cover this would change existing
// worlds. Avatars authored against the original shader family land here instead and keep their
// intended look. -xlinka
[ComponentCategory("Assets/Materials")]
public class XiexeToonMaterial : MaterialProvider, ICommonMaterial
{
    // ALBEDO
    public readonly Sync<colorHDR> AlbedoColor;
    public readonly AssetRef<TextureAsset> AlbedoTexture;
    public readonly Sync<float2> MainTextureScale;
    public readonly Sync<float2> MainTextureOffset;
    public readonly Sync<bool> UseVertexColors;
    // Pulls the albedo toward or away from its own luminance before any lighting runs.
    public readonly Sync<float> Saturation;

    // NORMAL
    public readonly AssetRef<TextureAsset> NormalMap;
    public readonly Sync<float> NormalScale;

    // SHADING RAMP - the ramp IS the light response: U is the wrapped N.L, V picks a row.
    public readonly AssetRef<TextureAsset> ShadowRamp;
    public readonly Sync<float> ShadowRampRow;
    // Only used when no ramp is bound.
    public readonly Sync<float> ShadowSharpness;

    // RIM, lit side. Gated on N.L, so it appears only where light reaches - that is what separates it
    // from a plain fresnel.
    public readonly Sync<colorHDR> RimColor;
    public readonly Sync<float> RimIntensity;
    public readonly Sync<float> RimRange;
    public readonly Sync<float> RimThreshold;
    public readonly Sync<float> RimSharpness;
    public readonly Sync<float> RimAlbedoTint;
    public readonly Sync<float> RimAttenuationEffect;

    // RIM, shadow side. Tints the dark side rather than adding light, so it reads as bounce.
    public readonly Sync<colorHDR> ShadowRim;
    public readonly Sync<float> ShadowRimRange;
    public readonly Sync<float> ShadowRimThreshold;
    public readonly Sync<float> ShadowRimSharpness;
    public readonly Sync<float> ShadowRimAlbedoTint;

    // SPECULAR
    public readonly Sync<float> SpecularIntensity;
    // 0 is a hard toon dot, 1 lets the lobe spread into an ordinary highlight.
    public readonly Sync<float> SpecularArea;
    public readonly Sync<float> Metallic;
    public readonly Sync<float> Glossiness;
    public readonly Sync<float> Reflectivity;
    // Their packing, not glTF's: metallic in R, smoothness in A.
    public readonly AssetRef<TextureAsset> MetallicGlossMap;

    // MATCAP / EMISSION / OCCLUSION
    public readonly AssetRef<TextureAsset> Matcap;
    public readonly Sync<colorHDR> MatcapTint;
    public readonly AssetRef<TextureAsset> EmissionMap;
    public readonly Sync<colorHDR> EmissionColor;
    public readonly AssetRef<TextureAsset> OcclusionMap;
    public readonly Sync<colorHDR> OcclusionColor;

    // SUBSURFACE - light bleeding through thin geometry: ears, fins, membranes. Costs nothing while
    // SubsurfaceColor is black, which the shader branches on.
    public readonly AssetRef<TextureAsset> ThicknessMap;
    public readonly Sync<colorHDR> SubsurfaceColor;
    public readonly Sync<float> SubsurfaceDistortion;
    public readonly Sync<float> SubsurfacePower;
    public readonly Sync<float> SubsurfaceScale;

    // OUTLINE - an inverted hull on the material's next pass.
    // Model-space metres. A toon outline on a character reads at about one to three millimetres; five
    // is already heavy. This is NOT the flat toon material's 0.1, which is a depth-scaled factor.
    private const float MaxOutlineMetres = 0.005f;
    private bool _warnedOutlineWidth;

    public readonly Sync<colorHDR> OutlineColor;
    public readonly Sync<float> OutlineWidth;
    public readonly AssetRef<TextureAsset> OutlineMask;

    // SURFACE
    public readonly Sync<AlphaMode> AlphaMode;
    public readonly Sync<float> AlphaClip;
    public readonly Sync<Culling> Culling;
    public readonly Sync<int> RenderQueue;

    protected override MaterialType MaterialType => MaterialType.XiexeToon;

    // ICommonMaterial: the shared handle anything generic (the colour picker, the eyedropper) reaches
    // for, mapped onto this material's own fields.
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

    public XiexeToonMaterial()
    {
        AlbedoColor = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture = new AssetRef<TextureAsset>(this);
        MainTextureScale = new Sync<float2>(this, float2.One);
        MainTextureOffset = new Sync<float2>(this, float2.Zero);
        UseVertexColors = new Sync<bool>(this, false);
        Saturation = new Sync<float>(this, 0f);

        NormalMap = new AssetRef<TextureAsset>(this);
        NormalScale = new Sync<float>(this, 1f);

        ShadowRamp = new AssetRef<TextureAsset>(this);
        ShadowRampRow = new Sync<float>(this, 0f);
        ShadowSharpness = new Sync<float>(this, 0.5f);

        RimColor = new Sync<colorHDR>(this, colorHDR.White);
        RimIntensity = new Sync<float>(this, 0f);
        RimRange = new Sync<float>(this, 0.7f);
        RimThreshold = new Sync<float>(this, 0.1f);
        RimSharpness = new Sync<float>(this, 0.1f);
        RimAlbedoTint = new Sync<float>(this, 0f);
        RimAttenuationEffect = new Sync<float>(this, 0f);

        ShadowRim = new Sync<colorHDR>(this, colorHDR.White);
        ShadowRimRange = new Sync<float>(this, 0f);
        ShadowRimThreshold = new Sync<float>(this, 0.1f);
        ShadowRimSharpness = new Sync<float>(this, 0.1f);
        ShadowRimAlbedoTint = new Sync<float>(this, 0f);

        SpecularIntensity = new Sync<float>(this, 0f);
        SpecularArea = new Sync<float>(this, 0.5f);
        Metallic = new Sync<float>(this, 0f);
        Glossiness = new Sync<float>(this, 0.5f);
        Reflectivity = new Sync<float>(this, 0f);
        MetallicGlossMap = new AssetRef<TextureAsset>(this);

        Matcap = new AssetRef<TextureAsset>(this);
        MatcapTint = new Sync<colorHDR>(this, colorHDR.White);
        EmissionMap = new AssetRef<TextureAsset>(this);
        EmissionColor = new Sync<colorHDR>(this, colorHDR.Black);
        OcclusionMap = new AssetRef<TextureAsset>(this);
        OcclusionColor = new Sync<colorHDR>(this, colorHDR.White);

        ThicknessMap = new AssetRef<TextureAsset>(this);
        SubsurfaceColor = new Sync<colorHDR>(this, colorHDR.Black);
        SubsurfaceDistortion = new Sync<float>(this, 0.2f);
        SubsurfacePower = new Sync<float>(this, 1f);
        SubsurfaceScale = new Sync<float>(this, 1f);

        OutlineColor = new Sync<colorHDR>(this, colorHDR.Black);
        OutlineWidth = new Sync<float>(this, 0f);
        OutlineMask = new AssetRef<TextureAsset>(this);

        AlphaMode = new Sync<AlphaMode>(this, Assets.AlphaMode.Opaque);
        AlphaClip = new Sync<float>(this, 0.5f);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        // Culling is a compile-time render_mode, so this swaps the shader variant rather than setting a
        // uniform. It runs FIRST: the swap keeps every uniform on the material, but the property pushes
        // below have to land on whatever shader ends up bound.
        asset.SetCulling(Culling.Value);

        // Names are PascalCase here and the hook snake-cases them onto the shader uniform.
        asset.SetColor("BaseColor", AlbedoColor.Value);
        asset.SetTexture("MainTexture", AlbedoTexture.Asset);
        asset.SetBool("UseMainTexture", AlbedoTexture.Asset != null);
        asset.SetFloat2("MainTextureScale", MainTextureScale.Value);
        asset.SetFloat2("MainTextureOffset", MainTextureOffset.Value);
        asset.SetBool("UseVertexColors", UseVertexColors.Value);
        asset.SetFloat("Saturation", Saturation.Value);

        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetBool("UseNormalMap", NormalMap.Asset != null);
        asset.SetFloat("NormalScale", NormalScale.Value);

        asset.SetTexture("ShadowRamp", ShadowRamp.Asset);
        asset.SetBool("UseShadowRamp", ShadowRamp.Asset != null);
        asset.SetFloat("RampRow", ShadowRampRow.Value);
        asset.SetFloat("ShadowSharpness", ShadowSharpness.Value);

        asset.SetColor("RimColor", RimColor.Value);
        asset.SetFloat("RimIntensity", RimIntensity.Value);
        asset.SetFloat("RimRange", RimRange.Value);
        asset.SetFloat("RimThreshold", RimThreshold.Value);
        asset.SetFloat("RimSharpness", RimSharpness.Value);
        asset.SetFloat("RimAlbedoTint", RimAlbedoTint.Value);
        asset.SetFloat("RimAttenuationEffect", RimAttenuationEffect.Value);

        asset.SetColor("ShadowRimColor", ShadowRim.Value);
        asset.SetFloat("ShadowRimRange", ShadowRimRange.Value);
        asset.SetFloat("ShadowRimThreshold", ShadowRimThreshold.Value);
        asset.SetFloat("ShadowRimSharpness", ShadowRimSharpness.Value);
        asset.SetFloat("ShadowRimAlbedoTint", ShadowRimAlbedoTint.Value);

        asset.SetFloat("SpecularIntensity", SpecularIntensity.Value);
        asset.SetFloat("SpecularArea", SpecularArea.Value);
        asset.SetFloat("Metallic", Metallic.Value);
        asset.SetFloat("Glossiness", Glossiness.Value);
        asset.SetFloat("Reflectivity", Reflectivity.Value);
        asset.SetTexture("MetallicGlossMap", MetallicGlossMap.Asset);
        asset.SetBool("UseMetallicGlossMap", MetallicGlossMap.Asset != null);

        asset.SetTexture("Matcap", Matcap.Asset);
        asset.SetBool("UseMatcap", Matcap.Asset != null);
        asset.SetColor("MatcapTint", MatcapTint.Value);

        asset.SetTexture("EmissionMap", EmissionMap.Asset);
        asset.SetBool("UseEmissionMap", EmissionMap.Asset != null);
        asset.SetColor("EmissionColor", EmissionColor.Value);

        asset.SetTexture("OcclusionMap", OcclusionMap.Asset);
        asset.SetBool("UseOcclusionMap", OcclusionMap.Asset != null);
        asset.SetColor("OcclusionColor", OcclusionColor.Value);

        asset.SetTexture("ThicknessMap", ThicknessMap.Asset);
        asset.SetBool("UseThicknessMap", ThicknessMap.Asset != null);
        asset.SetColor("SubsurfaceColor", SubsurfaceColor.Value);
        asset.SetFloat("SubsurfaceDistortion", SubsurfaceDistortion.Value);
        asset.SetFloat("SubsurfacePower", SubsurfacePower.Value);
        asset.SetFloat("SubsurfaceScale", SubsurfaceScale.Value);

        // The hook routes these four to the chained hull material instead of the lit surface.
        asset.SetColor("OutlineColor", OutlineColor.Value);
        // 0.1 HERE IS NOT 0.1 THERE. The flat toon outline multiplies its width by view depth, so its
        // number is a screen-space factor and 0.1 is a reasonable ceiling. This one extrudes the hull in
        // MODEL SPACE METRES (Mat_XiexeOutline: VERTEX += normal * outline_width), so the same 0.1 is a
        // TEN CENTIMETRE shell. Copied from the other material, it wrapped an imported fox in a black
        // block that reads as a fat outline rather than as a line.
        //
        // A drawn character outline is a millimetre or two, so that is the ceiling. Anything over it is
        // reported once with the authored number, because a value this far out means the source scale is
        // different from ours and the log is what tells us by how much. -xlinka
        float authoredOutline = OutlineWidth.Value;
        float outline = System.Math.Clamp(authoredOutline, 0f, MaxOutlineMetres);
        if (authoredOutline > MaxOutlineMetres && !_warnedOutlineWidth)
        {
            _warnedOutlineWidth = true;
            Logging.Logger.Warn(
                $"XiexeToonMaterial on '{Slot?.SlotName.Value}': outline width {authoredOutline} is model-space "
                + $"metres here, clamped to {MaxOutlineMetres}. An authored value this large usually means the "
                + "source measured it in different units.");
        }
        asset.SetFloat("OutlineWidth", outline);
        asset.SetTexture("OutlineMask", OutlineMask.Asset);
        asset.SetBool("UseOutlineMask", OutlineMask.Asset != null);

        asset.SetInt("AlphaMode", (int)AlphaMode.Value);
        asset.SetFloat("AlphaClip", AlphaClip.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);
    }
}
