shader_type spatial;
// Multiplied particles - shadows, stains, anything that only ever darkens.
render_mode blend_mul, depth_draw_never, cull_back, unshaded;

#include "res://Shaders/EngineParticle_Core.gdshaderinc"
