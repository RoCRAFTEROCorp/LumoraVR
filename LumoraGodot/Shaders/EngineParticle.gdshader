shader_type spatial;
// Additive particles - sparks, glows, anything that should brighten what is behind it. The default, and
// what every effect authored before blend modes existed still gets.
render_mode blend_add, depth_draw_never, cull_back, unshaded;

#include "res://Shaders/EngineParticle_Core.gdshaderinc"
