shader_type spatial;
// Specular-workflow PBS, both sides drawn. Godot flips NORMAL for back faces under cull_disabled, so
// the far side lights correctly on its own. Same body as Mat_PBS_Specular.
render_mode blend_mix, depth_prepass_alpha, cull_disabled;

#include "res://Shaders/PBS_Specular_Core.gdshaderinc"
