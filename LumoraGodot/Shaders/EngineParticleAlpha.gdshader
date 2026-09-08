shader_type spatial;
// Alpha-blended particles - smoke, splashes, drips, anything with a texture whose dark parts are meant
// to stay dark. Under blend_add those blacks vanish entirely, which is why an imported alpha effect read
// as half missing.
render_mode blend_mix, depth_draw_never, cull_back, unshaded;

#include "res://Shaders/EngineParticle_Core.gdshaderinc"
