shader_type spatial;
// Inside-out sphere around the head while the engine boots. It sits in the transparent pass with the
// depth test off so it covers whatever the shared world already holds (the local home starts arriving
// during the last phases), the same way the 2D loading Control covers the desktop window. The mesh is
// re-centred on the camera every frame, so a model-space vertex IS the view direction and the gradient
// needs no camera uniforms. TIME drives a slow pulse so a frozen frame and a live one look different,
// which is the whole point of showing anything at all during boot. -xlinka
render_mode unshaded, cull_front, depth_draw_never, depth_test_disabled, blend_mix;

uniform vec4 top_color : source_color = vec4(0.045, 0.035, 0.11, 1.0);
uniform vec4 horizon_color : source_color = vec4(0.19, 0.14, 0.42, 1.0);
uniform vec4 bottom_color : source_color = vec4(0.02, 0.015, 0.05, 1.0);
uniform vec4 accent_color : source_color = vec4(0.47, 0.37, 0.94, 1.0);
uniform float horizon_width : hint_range(0.02, 0.6) = 0.2;
uniform float pulse_speed : hint_range(0.0, 2.0) = 0.3;

varying vec3 v_dir;

void vertex() {
	v_dir = VERTEX;
}

void fragment() {
	vec3 dir = normalize(v_dir);
	float h = dir.y;

	vec3 col;
	if (h >= 0.0) {
		col = mix(horizon_color.rgb, top_color.rgb, smoothstep(0.0, 0.85, h));
	} else {
		col = mix(horizon_color.rgb, bottom_color.rgb, smoothstep(0.0, 0.6, -h));
	}

	// Glow band on the horizon that breathes, plus a slow drift around the azimuth so turning the head
	// reads as motion through something rather than a flat wall. Both are a couple of ALU ops; this runs
	// on a mobile GPU before anything else is up.
	float band = 1.0 - smoothstep(0.0, horizon_width, abs(h));
	float pulse = 0.5 + 0.5 * sin(TIME * pulse_speed * 6.2831853);
	float az = atan(dir.x, -dir.z);
	float drift = 0.5 + 0.5 * sin(az * 3.0 + TIME * 0.4);
	col += accent_color.rgb * band * (0.08 + 0.08 * pulse + 0.05 * drift);

	ALBEDO = col;
	ALPHA = 1.0;
}
