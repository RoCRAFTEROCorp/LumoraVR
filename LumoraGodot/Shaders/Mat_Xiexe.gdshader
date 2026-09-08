shader_type spatial;
// Xiexe toon material, back-face culling (the default). Ported from Xiexe's Unity Shaders (XSToon),
// MIT licensed, Copyright (c) 2019 Xiexe.
//
// The material hook swaps this for Mat_XiexeFrontCull or Mat_XiexeDoubleSided when Culling changes;
// uniform values live on the material and survive the swap.
render_mode blend_mix, depth_prepass_alpha, cull_back;

#include "res://Shaders/Xiexe_Core.gdshaderinc"
