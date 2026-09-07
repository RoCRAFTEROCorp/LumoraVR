// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

public enum MaterialType
{
    PBS_Metallic,

    Unlit,

    OverlayUnlit,

    UI_Unlit,

    UI_DualColor,

    // UI mask WRITER: stamps the stencil reference where its (usually invisible) geometry rasterizes,
    // so stencil-tested content is clipped to the mask's exact SHAPE. Godot 4.5+ stencil_mode shader.
    UI_StencilWrite,

    // Stencil-TESTED UI content: like UI_Unlit but only draws where the stencil equals the mask reference
    // (written first by UI_StencilWrite). Drawn after the writer via render-priority ordering.
    UI_StencilTest,

    UI_Text,

    // Stencil-tested UI text: like UI_Text but only draws where the stencil equals the mask reference, so
    // text inside a shaped (circle/rounded) mask is clipped to the shape, not the AABB.
    UI_TextStencil,

    // Depth-tested, so world text occludes behind geometry.
    Text,

    Custom,

    Metaball,

    GridSpaceGround,

    LocalHomeRising,

    Blur,

    UI_ColorGradient,

    // Values below are appended only. The enum is serialized by ordinal, so reordering or inserting
    // would repoint every saved material at a different shader. -xlinka

    // Needs barycentric coordinates baked into the mesh vertex color channel.
    Wireframe,

    Matcap,

    // Outline is an inverted hull on the material's next pass.
    FlatToon,

    PBS_Triplanar,

    PBS_DualSided,

    PBS_VertexColor,

    FresnelLerp,

    // Overlay-band fresnel outline. Renders in the same late additive band as OverlayUnlit and
    // picks its color set from a depth-buffer occlusion test rather than the depth test.
    OverlayFresnel,

    // Ramp-shaded toon workhorse. Culling is a compile-time render_mode, so the hook swaps between
    // three shader variants for it, and the outline hull rides on the material's next pass.
    Toon,

    // World portal face: picture in an ellipse with a crystal rim (Mat_Portal.gdshader).
    Portal,

    // Denser toon model ported from Xiexe's Unity Shaders (MIT, (c) 2019 Xiexe): ramp shading with a
    // lit AND a shadow-side rim, subsurface bleed, a sharpenable specular lobe and an outline hull.
    // Separate from Toon on purpose - that one is our own simpler model and content depends on it.
    XiexeToon,

    // Specular-workflow PBS. The metallic material cannot stand in for it: a specular map carries a
    // reflectance COLOUR, not a metalness, and reading one as the other inverts the surface. Ported
    // from Unity's built-in Standard (Specular setup), MIT, (c) 2016 Unity Technologies.
    PBS_Specular,
}

// What the alpha channel of a toon surface means. Opaque throws it away, cutout tests it against the
// cutoff and keeps the surface in the opaque silhouette, blend actually blends.
public enum AlphaMode
{
    Opaque,

    Cutout,

    Blend
}

public enum BlendMode
{
    Opaque,

    Cutout,

    Alpha,

    Transparent,

    Additive,

    Multiply
}

public enum Culling
{
    Back,

    Front,

    None
}

public enum ZWrite
{
    Auto,
    Off,
    On
}

public enum ZTest
{
    Disabled = 0,
    Never = 1,
    Less = 2,
    Equal = 3,
    LessOrEqual = 4,
    Greater = 5,
    NotEqual = 6,
    GreaterOrEqual = 7,
    Always = 8
}

[System.Flags]
public enum ColorMask
{
    None = 0,
    R = 1,
    G = 2,
    B = 4,
    A = 8,
    RGB = R | G | B,
    RGBA = R | G | B | A
}

public enum StencilComparison
{
    Disabled = 0,
    Never = 1,
    Less = 2,
    Equal = 3,
    LessOrEqual = 4,
    Greater = 5,
    NotEqual = 6,
    GreaterOrEqual = 7,
    Always = 8
}

public enum StencilOperation
{
    Keep = 0,
    Zero = 1,
    Replace = 2,
    IncrementSaturate = 3,
    DecrementSaturate = 4,
    Invert = 5,
    IncrementWrap = 6,
    DecrementWrap = 7,

}
