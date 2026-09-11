shader_type spatial;

// Void-grid floor. Uniform names match GridSpaceGroundMaterial.cs (PascalCase member -> snake_case).
//
// specular_disabled: the directional light contributes diffuse and shadows only, so it can never
// bloom a highlight on the floor no matter how low the sun sits. ambient_light_disabled: the floor
// samples the sky for neither irradiance nor reflections. Its tint comes from EMISSION, so a warm
// sky cannot mud a cool floor and there is no reflection to smear across it. Shadows still land
// because the diffuse path is intact. -xlinka
render_mode cull_back, diffuse_lambert, specular_disabled, ambient_light_disabled;

// ---------------------------------------------------------------- surface
uniform vec4 base_near_color : source_color = vec4(0.110, 0.120, 0.145, 1.0);
uniform vec4 base_far_color : source_color = vec4(0.090, 0.100, 0.125, 1.0);
// self_lit scales the emission that carries the palette; light_response scales how much the
// directional light (and therefore shadow contrast) is allowed on top of it.
uniform float self_lit : hint_range(0.0, 2.0) = 1.0;
uniform float light_response : hint_range(0.0, 4.0) = 1.0;

// ---------------------------------------------------------------- grid
// World-space XZ by default so the grid is anchored to the world origin and independent of mesh
// UV scale; UV mode is kept for meshes that are not a floor.
uniform bool world_space_grid = true;
uniform vec2 grid_offset = vec2(0.0);
uniform float minor_scale : hint_range(0.1, 10.0) = 1.0;
uniform float major_scale : hint_range(1.0, 100.0) = 5.0;
uniform vec4 minor_line_color : source_color = vec4(0.200, 0.215, 0.250, 1.0);
uniform vec4 major_line_color : source_color = vec4(0.330, 0.370, 0.460, 1.0);
// Widths and softness are screen pixels, held constant at every distance.
uniform float minor_line_width : hint_range(0.5, 4.0) = 1.0;
uniform float major_line_width : hint_range(0.5, 6.0) = 1.5;
uniform float line_softness : hint_range(0.25, 2.0) = 1.0;
// A level fades out per axis once its cell is fewer than this many pixels across (fully gone at
// half that). Minor fades first; major persists major_scale/minor_scale times further.
uniform float minor_fade_pixels : hint_range(2.0, 40.0) = 10.0;
uniform float major_fade_pixels : hint_range(2.0, 40.0) = 8.0;

// ---------------------------------------------------------------- depth
// Grazing-angle lift in place of a specular lobe.
uniform vec4 sheen_color : source_color = vec4(0.55, 0.62, 0.75, 1.0);
uniform float sheen_strength : hint_range(0.0, 1.0) = 0.05;
uniform float sheen_power : hint_range(1.0, 8.0) = 3.0;
// Distance haze. Set horizon_color to the sky's horizon so the floor's far edge dissolves into it.
uniform vec4 horizon_color : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform float horizon_start : hint_range(0.0, 500.0) = 12.0;
uniform float horizon_end : hint_range(1.0, 1000.0) = 90.0;

varying vec2 grid_uv;

void vertex()
{
    vec3 world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    grid_uv = (world_space_grid ? world_pos.xz : UV) + grid_offset;
}

// Coverage of one grid level in screen space. The half-width is width_px scaled by the per-axis
// derivative, so a line is the same number of pixels wide at 2 m and at 40 m, with a soft_px band of
// smoothstep on its edge for the antialiasing. Each axis then fades on its OWN derivative: a floor
// seen at a grazing angle compresses the along-view axis far sooner than the across-view one, so
// the lines running across the view drop out first while the ones converging on the vanishing
// point stay crisp. The fade lands while a cell is still several times wider than the line, which
// is before the lines pack closer than their AA band and start to moire. -xlinka
float grid_level(vec2 coord, float width_px, float soft_px, float fade_px)
{
    vec2 deriv = max(fwidth(coord), vec2(0.000001));
    vec2 dist = abs(fract(coord + 0.5) - 0.5);
    vec2 half_w = min(width_px * 0.5 * deriv, vec2(0.5));
    vec2 soft = soft_px * deriv;
    vec2 line = vec2(1.0) - smoothstep(half_w, half_w + soft, dist);
    vec2 cell_px = vec2(1.0) / deriv;
    vec2 vis = smoothstep(vec2(fade_px * 0.5), vec2(fade_px), cell_px);
    line *= vis;
    return max(line.x, line.y);
}

void fragment()
{
    float cam_dist = length(VERTEX);
    float haze_end = max(horizon_end, horizon_start + 0.01);
    float haze = smoothstep(horizon_start, haze_end, cam_dist);
    float depth_t = smoothstep(0.0, haze_end, cam_dist);

    float minor = grid_level(grid_uv / minor_scale, minor_line_width, line_softness, minor_fade_pixels);
    float major = grid_level(grid_uv / major_scale, major_line_width, line_softness, major_fade_pixels);

    vec3 surface = mix(base_near_color.rgb, base_far_color.rgb, depth_t);
    surface = mix(surface, minor_line_color.rgb, minor);
    surface = mix(surface, major_line_color.rgb, major);

    float ndv = clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0);
    float sheen = pow(1.0 - ndv, sheen_power) * sheen_strength;
    vec3 lit = surface * self_lit + sheen_color.rgb * sheen;

    // The haze takes the light response down with it: a fully hazed floor is atmosphere, and
    // atmosphere does not show a shadow. -xlinka
    ALBEDO = surface * light_response * (1.0 - haze);
    EMISSION = mix(lit, horizon_color.rgb, haze);
    ROUGHNESS = 1.0;
    METALLIC = 0.0;
    SPECULAR = 0.0;
}
