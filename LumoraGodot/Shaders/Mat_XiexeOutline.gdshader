shader_type spatial;
// Inverted-hull outline for the Xiexe toon material: the shell is pushed out along the normal and only
// its BACK faces survive, so it shows as a border wherever the real surface does not cover it. Godot 4
// has no multi-pass shaders, so the material hook attaches this as the material's next pass.
//
// Opaque and depth-writing on purpose - the shell's inward-facing fragments sit behind the model, so
// the depth the main pass already laid down rejects them and only the border is left.
render_mode unshaded, cull_front;

uniform vec4 outline_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
// Metres of extrusion in MODEL space, so the border keeps a fixed thickness on the object and thins
// with distance the way a drawn line does. On a scaled slot it scales with the slot.
// Metres, so the useful range is millimetres. The flat toon outline's 0.1 ceiling belongs to a
// depth-scaled factor and means nothing here: at 0.1 this extrudes a ten centimetre shell.
uniform float outline_width : hint_range(0.0, 0.005) = 0.0;
// Their outline can take its colour from the surface underneath instead of a flat one.
uniform sampler2D outline_mask : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_outline_mask = false;

void vertex() {
	VERTEX += normalize(NORMAL) * outline_width;
}

void fragment() {
	if (outline_width <= 0.0) {
		discard;
	}
	// The mask gates WHERE the outline appears, so an author can keep it off the face and on the body.
	if (use_outline_mask && texture(outline_mask, UV).r < 0.5) {
		discard;
	}
	ALBEDO = outline_color.rgb;
}
