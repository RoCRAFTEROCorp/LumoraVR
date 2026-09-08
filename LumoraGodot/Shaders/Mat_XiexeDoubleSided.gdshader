shader_type spatial;
// Xiexe toon material, no culling - both faces drawn. See Mat_Xiexe.gdshader; only the render_mode
// differs. This is what a Sidedness of Double or a Culling of Off lands on.
render_mode blend_mix, depth_prepass_alpha, cull_disabled;

#include "res://Shaders/Xiexe_Core.gdshaderinc"
