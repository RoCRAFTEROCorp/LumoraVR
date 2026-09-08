// Shared body of the specular-workflow PBS material.
//
// Ported from Unity's built-in Standard (Specular setup) shader, MIT licensed, Copyright (c) 2016
// Unity Technologies - UnityStandardCore.cginc / UnityStandardBRDF.cginc. This is a translation of
// that BRDF to Godot 4's spatial shader.
//
// The distinction from the metallic material is not cosmetic. In the metallic workflow the reflectance
// at normal incidence is derived: a dielectric gets a fixed grey 4% and a metal reflects its own albedo.
// In the specular workflow the author SUPPLIES that reflectance directly, as a colour, and the diffuse
// colour is whatever energy is left over. There is no metallic value to recover, so pushing a specular
// map through a metallic slot does not approximate the look, it inverts it: the map's blue channel gets
// read as "how metal is this", and a surface authored with a coloured specular tint comes out fully
// metallic and near-black in the diffuse.
//
// Godot's own PBR path has no coloured F0, so direct lighting runs through a light() of our own.
// Ambient and reflections still go through Godot, fed the closest metallic-workflow equivalent. -xlinka

// ---------------------------------------------------------------- albedo
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_albedo_texture = false;
uniform vec2 texture_scale = vec2(1.0);
uniform vec2 texture_offset = vec2(0.0);

// ---------------------------------------------------------------- specular / gloss
// RGB is the reflectance colour at normal incidence, A is smoothness - Unity's _SpecGlossMap layout,
// and the same packing the specular COLOUR uses, which is why there is no separate smoothness value.
//
// The map REPLACES the colour rather than tinting it, matching SpecularGloss() in UnityStandardInput:
// when _SPECGLOSSMAP is defined the sampled texel is the whole answer and _SpecColor is not consulted.
// Multiplying instead would darken every mapped surface by whatever the flat colour happened to be.
uniform vec4 specular_color : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform sampler2D specular_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_specular_map = false;

// ---------------------------------------------------------------- normal
uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform float normal_scale : hint_range(0.0, 4.0) = 1.0;

// ---------------------------------------------------------------- emission / occlusion
uniform vec4 emissive_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform sampler2D emissive_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_emissive_map = false;

uniform sampler2D occlusion_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_occlusion_map = false;

uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;
// Set by the material hook from the BlendMode member. 0 Opaque, 1 Cutout, 2 Alpha, 3 Transparent,
// 4 Additive, 5 Multiply. Only the cutout case changes anything here: blending itself is a compile-time
// render_mode, so the rest is handled by which variant the hook loaded.
uniform int blend_mode = 0;

// The one thing Godot's light stage cannot carry for us: a per-channel F0.
varying vec3 spec_f0;

void vertex() {
	UV = UV * texture_scale + texture_offset;
}

void fragment() {
	vec4 base = albedo_color;
	if (use_albedo_texture) {
		base *= texture(albedo_texture, UV);
	}

	vec4 spec = specular_color;
	if (use_specular_map) {
		spec = texture(specular_map, UV);
	}
	float gloss = spec.a;

	// Energy conservation, Unity's EnergyConservationBetweenDiffuseAndSpecular: whatever the specular
	// lobe reflects is not available to the diffuse one, and the strongest channel sets that budget.
	float reflectivity = max(max(spec.r, spec.g), spec.b);

	spec_f0 = spec.rgb;

	if (blend_mode == 1 && base.a < alpha_cutoff) {
		discard;
	}

	ALBEDO = base.rgb * (1.0 - reflectivity);
	ALPHA = blend_mode == 0 || blend_mode == 1 ? 1.0 : base.a;
	ROUGHNESS = clamp(1.0 - gloss, 0.0, 1.0);

	// Ambient and reflection probes still run Godot's metallic path, which reconstructs F0 as
	// 0.16 * specular^2 for a dielectric. Solving that for the reflectivity we actually have keeps the
	// indirect term at the same brightness as the direct one instead of defaulting to a flat 4%.
	METALLIC = 0.0;
	SPECULAR = clamp(sqrt(reflectivity / 0.16), 0.0, 1.0);

	vec3 emission = emissive_color.rgb * emissive_color.a;
	if (use_emissive_map) {
		emission *= texture(emissive_map, UV).rgb;
	}
	EMISSION = emission;

	if (use_occlusion_map) {
		AO = texture(occlusion_map, UV).r;
		AO_LIGHT_AFFECT = 1.0;
	}

	if (use_normal_map) {
		vec3 tn = texture(normal_map, UV).xyz * 2.0 - 1.0;
		tn.xy *= normal_scale;
		NORMAL = normalize(TANGENT * tn.x + BINORMAL * tn.y + NORMAL * tn.z);
	}
}

// Direct lighting. Cook-Torrance with a GGX distribution, Unity's Smith-joint visibility approximation
// and a Schlick fresnel that runs on the COLOURED F0 - which is the whole reason this cannot be left to
// Godot's built-in light path. -xlinka
void light() {
	vec3 N = normalize(NORMAL);
	vec3 V = normalize(VIEW);
	vec3 L = normalize(LIGHT);
	vec3 H = normalize(L + V);

	// One branch around the whole body rather than an early out: 'return' is rejected outright in a
	// light() processor.
	float n_dot_l = clamp(dot(N, L), 0.0, 1.0);
	if (n_dot_l > 0.0) {
		float n_dot_v = abs(dot(N, V)) + 1e-5;
		float n_dot_h = clamp(dot(N, H), 0.0, 1.0);
		float l_dot_h = clamp(dot(L, H), 0.0, 1.0);

		// Clamped away from a perfect mirror: at zero roughness the lobe collapses to a subpixel spike
		// that aliases into fireflies on anything animated.
		float rough = clamp(ROUGHNESS, 0.045, 1.0);
		float a = rough * rough;
		float a2 = a * a;

		float denom = (n_dot_h * a2 - n_dot_h) * n_dot_h + 1.0;
		float d_term = a2 / max(PI * denom * denom, 1e-7);

		float lambda_v = n_dot_l * (n_dot_v * (1.0 - a) + a);
		float lambda_l = n_dot_v * (n_dot_l * (1.0 - a) + a);
		float vis = 0.5 / max(lambda_v + lambda_l, 1e-5);

		vec3 fresnel = spec_f0 + (vec3(1.0) - spec_f0) * pow(1.0 - l_dot_h, 5.0);

		vec3 radiance = LIGHT_COLOR * ATTENUATION * n_dot_l;

		// DIFFUSE_LIGHT is multiplied by ALBEDO when the pass resolves, so the lambert 1/PI goes here
		// and the albedo does not.
		DIFFUSE_LIGHT += radiance / PI;
		SPECULAR_LIGHT += d_term * vis * fresnel * radiance;
	}
}
