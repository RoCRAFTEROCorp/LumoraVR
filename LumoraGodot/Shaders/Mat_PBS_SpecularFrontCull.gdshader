shader_type spatial;
// Specular-workflow PBS, front faces culled. Same body as Mat_PBS_Specular.
render_mode blend_mix, depth_prepass_alpha, cull_front;

#include "res://Shaders/PBS_Specular_Core.gdshaderinc"
