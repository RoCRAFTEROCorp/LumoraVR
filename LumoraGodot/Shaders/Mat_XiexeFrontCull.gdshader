shader_type spatial;
// Xiexe toon material, front-face culling. See Mat_Xiexe.gdshader; only the render_mode differs.
render_mode blend_mix, depth_prepass_alpha, cull_front;

#include "res://Shaders/Xiexe_Core.gdshaderinc"
