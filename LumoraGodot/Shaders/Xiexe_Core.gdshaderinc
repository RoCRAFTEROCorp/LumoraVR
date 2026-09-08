// Shared body of the Xiexe toon material.
//
// Ported from Xiexe's Unity Shaders (XSToon), MIT licensed, Copyright (c) 2019 Xiexe.
// The lighting model is theirs; this is a translation of it to Godot 4's spatial shader, not a
// reimplementation from scratch - the ramp remap, the two rim terms, the subsurface wrap and the
// specular sharpening all follow XSLightingFunctions.cginc so an avatar authored against it lands
// looking the way its author intended.
//
// Kept entirely separate from the plain toon material. That one is our own simpler model and several
// worlds already use it; this is a second, denser model that happens to share a silhouette. Culling is
// a compile-time render_mode in Godot 4, so this ships as thin variant shaders that differ only in that
// line and pull the body in from here. -xlinka

// ---------------------------------------------------------------- albedo
uniform vec4 base_color : source_color = vec4(1.0);
uniform sampler2D main_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_main_texture = false;
uniform vec2 main_texture_scale = vec2(1.0);
uniform vec2 main_texture_offset = vec2(0.0);
uniform bool use_vertex_colors = false;
// Pulls the albedo toward or away from its own luminance before anything is lit.
uniform float saturation : hint_range(-1.0, 1.0) = 0.0;

// ---------------------------------------------------------------- normal
uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform float normal_scale : hint_range(0.0, 4.0) = 1.0;

// ---------------------------------------------------------------- shading ramp
// The ramp IS the light response. U is the wrapped N.L, so the texture decides where the terminator
// sits and what colour the dark side takes; V picks a row, which lets one texture carry several ramps.
uniform sampler2D shadow_ramp : source_color, filter_linear, repeat_disable;
uniform bool use_shadow_ramp = false;
uniform float ramp_row : hint_range(0.0, 1.0) = 0.0;
// Fallback when no ramp is bound: a hard-ish step with a soft edge.
uniform float shadow_sharpness : hint_range(0.0, 1.0) = 0.5;

// ---------------------------------------------------------------- rim (lit side)
uniform vec4 rim_color : source_color = vec4(1.0);
uniform float rim_intensity : hint_range(0.0, 4.0) = 0.0;
uniform float rim_range : hint_range(0.0, 1.0) = 0.7;
uniform float rim_threshold : hint_range(0.0, 1.0) = 0.1;
uniform float rim_sharpness : hint_range(0.0, 1.0) = 0.1;
uniform float rim_albedo_tint : hint_range(0.0, 1.0) = 0.0;
uniform float rim_attenuation_effect : hint_range(0.0, 1.0) = 0.0;

// ---------------------------------------------------------------- rim (shadow side)
uniform vec4 shadow_rim_color : source_color = vec4(1.0);
uniform float shadow_rim_range : hint_range(0.0, 1.0) = 0.0;
uniform float shadow_rim_threshold : hint_range(0.0, 1.0) = 0.1;
uniform float shadow_rim_sharpness : hint_range(0.0, 1.0) = 0.1;
uniform float shadow_rim_albedo_tint : hint_range(0.0, 1.0) = 0.0;

// ---------------------------------------------------------------- specular
uniform float specular_intensity : hint_range(0.0, 4.0) = 0.0;
// 0 is a mirror-hard toon dot, 1 lets the lobe spread into a normal highlight.
uniform float specular_area : hint_range(0.0, 1.0) = 0.5;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float glossiness : hint_range(0.0, 1.0) = 0.5;
uniform float reflectivity : hint_range(0.0, 1.0) = 0.0;
uniform sampler2D metallic_gloss_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_metallic_gloss_map = false;

// ---------------------------------------------------------------- matcap / emission / occlusion
uniform sampler2D matcap : source_color, filter_linear_mipmap, repeat_disable;
uniform bool use_matcap = false;
uniform vec4 matcap_tint : source_color = vec4(1.0);

uniform sampler2D emission_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_emission_map = false;
uniform vec4 emission_color : source_color = vec4(0.0);

uniform sampler2D occlusion_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_occlusion_map = false;
uniform vec4 occlusion_color : source_color = vec4(1.0);

// ---------------------------------------------------------------- subsurface
// Light bleeding through thin geometry - ears, fins, membranes. Skipped entirely when the colour is
// black, exactly as the original does, because it is not free.
uniform sampler2D thickness_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_thickness_map = false;
uniform vec4 subsurface_color : source_color = vec4(0.0);
uniform float subsurface_distortion : hint_range(0.0, 1.0) = 0.2;
uniform float subsurface_power : hint_range(0.0, 16.0) = 1.0;
uniform float subsurface_scale : hint_range(0.0, 8.0) = 1.0;

// 0 opaque, 1 cutout, 2 blend.
uniform int alpha_mode : hint_range(0, 2) = 0;
uniform float alpha_clip : hint_range(0.0, 1.0) = 0.5;

varying vec3 vertex_color;

void vertex() {
	UV = UV * main_texture_scale + main_texture_offset;
	vertex_color = COLOR.rgb;
}

void fragment() {
	vec4 base = base_color;
	if (use_main_texture) {
		base *= texture(main_texture, UV);
	}
	if (use_vertex_colors) {
		base.rgb *= vertex_color;
	}

	// Saturation around the surface's own luminance, before lighting - the original does this to the
	// albedo, so a desaturated ramp still reads against the original hue.
	if (abs(saturation) > 0.001) {
		float luma = dot(base.rgb, vec3(0.2126, 0.7152, 0.0722));
		base.rgb = clamp(mix(vec3(luma), base.rgb, 1.0 + saturation), 0.0, 8.0);
	}

	if (alpha_mode == 1 && base.a < alpha_clip) {
		discard;
	}

	// NORMAL, TANGENT and BINORMAL are view-space here, which is what both the matcap lookup and the
	// rim fresnel want, so nothing needs transforming back.
	if (use_normal_map) {
		vec3 tn = texture(normal_map, UV).xyz * 2.0 - 1.0;
		tn.xy *= normal_scale;
		NORMAL = normalize(TANGENT * tn.x + BINORMAL * tn.y + NORMAL * tn.z);
	}

	float occlusion = 1.0;
	if (use_occlusion_map) {
		occlusion = texture(occlusion_map, UV).r;
		base.rgb *= mix(occlusion_color.rgb, vec3(1.0), occlusion);
	}

	vec3 emission = emission_color.rgb;
	if (use_emission_map) {
		emission *= texture(emission_map, UV).rgb;
	}

	// Matcap is a view-space normal lookup, added on top so it survives the shadow side.
	if (use_matcap) {
		vec3 cap = texture(matcap, NORMAL.xy * 0.5 + 0.5).rgb;
		emission += cap * matcap_tint.rgb * matcap_tint.a;
	}

	float m = metallic;
	float g = glossiness;
	if (use_metallic_gloss_map) {
		vec4 packed = texture(metallic_gloss_map, UV);
		// Their map is metallic in R, smoothness in A - the Unity convention, not glTF's.
		m = packed.r;
		g = packed.a;
	}

	ALBEDO = base.rgb;
	ALPHA = alpha_mode == 2 ? base.a : 1.0;
	EMISSION = emission;
	METALLIC = m;
	ROUGHNESS = clamp(1.0 - g, 0.0, 1.0);
	SPECULAR = reflectivity;
	AO = occlusion;
}

void light() {
	vec3 n = normalize(NORMAL);
	vec3 l = normalize(LIGHT);
	vec3 v = normalize(VIEW);

	float ndl = dot(n, l);
	// Wrapped N.L: the ramp needs the whole sphere mapped into 0..1, not just the lit hemisphere,
	// or the dark side has no range left for the ramp to say anything about.
	float wrapped = ndl * 0.5 + 0.5;

	vec3 shade;
	if (use_shadow_ramp) {
		shade = texture(shadow_ramp, vec2(clamp(wrapped * ATTENUATION, 0.0, 1.0), ramp_row)).rgb;
	} else {
		float edge = mix(0.35, 0.005, shadow_sharpness);
		shade = vec3(smoothstep(0.5 - edge, 0.5 + edge, wrapped));
	}

	// One minus the saturated view-dot-normal: the silhouette-facing rim factor both rim terms use.
	float svdn = 1.0 - clamp(dot(n, v), 0.0, 1.0);

	// LIT-SIDE RIM. Gated on N.L so it only appears where the light actually reaches, which is what
	// separates it from a plain fresnel.
	vec3 rim = vec3(0.0);
	if (rim_intensity > 0.0) {
		float ri = svdn * pow(clamp(ndl, 0.0, 1.0), max(rim_threshold, 0.001) * 8.0);
		ri = smoothstep(rim_range - rim_sharpness, rim_range + rim_sharpness, ri);
		rim = ri * rim_intensity * rim_color.rgb * mix(vec3(1.0), ALBEDO, rim_albedo_tint);
		rim *= mix(1.0, ATTENUATION, rim_attenuation_effect);
	}

	// SHADOW-SIDE RIM. The mirror of the above - it rides the DARK side and tints it rather than
	// adding light, so it reads as bounce rather than a second lamp.
	vec3 shadow_rim = vec3(1.0);
	if (shadow_rim_range > 0.0) {
		float sri = svdn * pow(clamp(1.0 - ndl, 0.0, 1.0), max(shadow_rim_threshold, 0.001) * 2.0);
		sri = smoothstep(shadow_rim_range - shadow_rim_sharpness, shadow_rim_range + shadow_rim_sharpness, sri);
		shadow_rim = mix(vec3(1.0), shadow_rim_color.rgb * mix(vec3(1.0), ALBEDO, shadow_rim_albedo_tint), sri);
	}

	// SUBSURFACE. Light arriving from behind, wrapped around the surface by distortion and focused by
	// power. Skipped when the colour is black - the original branches here for the same reason.
	vec3 sss = vec3(0.0);
	if (dot(subsurface_color.rgb, vec3(1.0)) > 0.0) {
		float thickness = use_thickness_map ? texture(thickness_map, UV).r : 1.0;
		vec3 h = normalize(l + n * subsurface_distortion);
		float vdh = pow(clamp(dot(v, -h), 0.0, 1.0), max(subsurface_power, 0.001));
		sss = subsurface_color.rgb * vdh * thickness * subsurface_scale
		    * clamp(ATTENUATION * wrapped, 0.0, 1.0) * ALBEDO;
	}

	// ATTENUATION scales the whole term so a shadowed or out-of-range light adds nothing at all.
	//
	// No ALBEDO multiply and a divide by PI, both deliberate: Godot multiplies DIFFUSE_LIGHT by ALBEDO
	// after this returns and hands LIGHT_COLOR in pre-scaled by PI, so writing ALBEDO here squares it
	// and skipping the divide comes out PI times hot. Same convention as the plain toon material.
	DIFFUSE_LIGHT += LIGHT_COLOR * shade * shadow_rim * ATTENUATION * (1.0 - METALLIC) / PI;
	DIFFUSE_LIGHT += LIGHT_COLOR * (rim + sss) / PI;

	// SPECULAR. A GGX lobe sharpened toward a hard toon dot by specular_area, which is what gives the
	// family its flat highlight instead of a photographic one.
	if (specular_intensity > 0.0) {
		float rough = max(ROUGHNESS * ROUGHNESS, 0.0045);
		vec3 h = normalize(l + v);
		float ndh = clamp(dot(n, h), 0.0, 1.0);
		float d = rough * rough;
		float denom = ndh * ndh * (d - 1.0) + 1.0;
		float ggx = d / max(PI * denom * denom, 0.0001);
		float lobe = mix(smoothstep(0.5, 0.51, ggx), ggx, clamp(specular_area, 0.0, 1.0));
		SPECULAR_LIGHT += LIGHT_COLOR * lobe * specular_intensity
		                * mix(vec3(1.0), ALBEDO, METALLIC) * ATTENUATION / PI;
	}
}
