shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, unshaded, world_vertex_coords;

// Vertex-animated bubbles for a BubbleFieldMesh. UV0 = (bubble index, part), UV2 = shape
// parameters. Part 0 is the bubble sphere, 1 the neck it rises out of, 2 the ripple ring on the
// floor. The previous version of this file raytraced every bubble in every pixel; the look is the
// same, the cost is now the pixels a bubble actually covers. - xlinka

uniform vec4 tint_a : source_color = vec4(0.72, 1.00, 0.92, 1.0);
uniform vec4 tint_b : source_color = vec4(1.00, 0.56, 0.88, 1.0);
uniform float blob_radius : hint_range(0.03, 0.6) = 0.12;
uniform float blob_smoothness : hint_range(0.0, 0.6) = 0.075;
uniform int blob_count : hint_range(1, 256) = 72;
uniform float rise_speed : hint_range(0.05, 2.0) = 0.34;
uniform vec2 volume_extents = vec2(24.0, 24.0);
uniform float volume_height : hint_range(0.5, 12.0) = 7.0;
uniform vec3 volume_offset = vec3(0.0, -3.5, 0.0);
uniform float rim_strength : hint_range(0.0, 4.0) = 1.55;
uniform float rim_falloff : hint_range(0.2, 6.0) = 2.8;
uniform float fresnel_power : hint_range(0.5, 5.0) = 3.0;
uniform float alpha_scale : hint_range(0.0, 2.0) = 0.92;
uniform float emission_strength : hint_range(0.0, 4.0) = 1.05;
uniform float time_scale : hint_range(0.0, 4.0) = 1.0;
uniform vec4 ripple_color : source_color = vec4(0.70, 1.00, 0.92, 1.0);
uniform float ripple_strength : hint_range(0.0, 2.0) = 0.62;
uniform float ripple_radius : hint_range(0.1, 3.0) = 0.58;
uniform float ripple_width : hint_range(0.005, 0.2) = 0.032;

varying vec3 v_world;
varying vec3 v_normal;
varying vec2 v_ring;
varying float v_part;
varying float v_life;
varying float v_pop;
varying float v_ring_radius;

float hash11(float n) {
    return fract(sin(n) * 43758.5453123);
}

float blob_life(int i, float t) {
    float fi = float(i);
    float phase = hash11(fi * 8.213);
    return fract(t * rise_speed + phase);
}

// Per-bubble pop point in [0.62, 0.76]. Upper bound leaves head-room: the burst
// + vanish windows tack on another ~0.03 of life (~0.2m of rise) before the
// bubble is fully gone, and we want zero pops kissing the ceiling. Floor keeps
// pops past halfway up so none feel premature. Each index gets a stable value
// so the same bubble pops at the same height every cycle. - xlinka
float blob_pop_life(int i) {
    return mix(0.62, 0.76, hash11(float(i) * 5.197 + 2.13));
}

// Polar disk distribution so balls stay inside the cylindrical ground rather than
// poking past the rim like the old square layout did. sqrt(r) keeps density even. - xlinka
vec3 blob_center(int i, float t, vec3 origin) {
    float fi = float(i);
    float r_rand = hash11(fi * 1.137 + 7.31);
    float a_rand = hash11(fi * 3.751 + 0.91);
    float disk_radius = min(volume_extents.x, volume_extents.y);
    float r = sqrt(r_rand) * disk_radius;
    float a = a_rand * TAU;
    float rx = cos(a) * r;
    float rz = sin(a) * r;
    float life = blob_life(i, t);
    float lift = smoothstep(0.06, 0.94, life);
    float y = mix(0.025, volume_height + 0.10, lift);
    return origin + vec3(rx, y, rz);
}

// Real-bubble pop, not a balloon deflation:
//   pre-pop window  -> surface tension wobble (high-freq jitter)
//   burst window    -> rapid outward expansion (~70%), no shrink
//   vanish window   -> alpha+size cut to zero a few frames later
// Fragment shader thins the alpha to a rim-only shell during burst so it
// reads as a popping soap film rather than a growing sphere. - xlinka
float blob_size(int i, float t) {
    float fi = float(i);
    float life = blob_life(i, t);
    float pop_life = blob_pop_life(i);
    float birth = smoothstep(0.02, 0.16, life);
    float wobble = 0.94 + 0.06 * sin(t * 2.2 + fi * 1.7);
    float separation = 0.72 + 0.28 * smoothstep(0.14, 0.36, life);

    float pre_pop = smoothstep(pop_life - 0.05, pop_life, life);
    float jitter = sin(t * 22.0 + fi * 5.1) * sin(t * 31.0 + fi * 2.3);
    float tension = 1.0 + pre_pop * jitter * 0.09;

    float burst = smoothstep(pop_life, pop_life + 0.022, life);
    float expansion = 1.0 + burst * 0.70;

    float vanish = smoothstep(pop_life + 0.012, pop_life + 0.032, life);
    float visible = 1.0 - vanish;

    return blob_radius * birth * wobble * separation * tension * expansion * visible;
}

float neck_size(int i, float t) {
    float life = blob_life(i, t);
    float birth = smoothstep(0.0, 0.10, life);
    float detach = 1.0 - smoothstep(0.24, 0.48, life);
    return blob_radius * 0.82 * birth * detach;
}

float ripple_fade(float life) {
    return smoothstep(0.0, 0.08, life) * (1.0 - smoothstep(0.80, 1.0, life));
}

void vertex() {
    int i = int(UV.x + 0.5);
    float part = UV.y;
    float t = TIME * time_scale;
    vec3 origin = MODEL_MATRIX[3].xyz + volume_offset;

    float life = blob_life(i, t);
    vec3 center = blob_center(i, t, origin);

    // A bubble past the authored count collapses to a point and rasterises nothing, so the mesh's
    // Count and the material's blob_count can disagree without one of them drawing garbage.
    float enabled = i < blob_count ? 1.0 : 0.0;

    vec3 world = center;
    vec3 n = vec3(0.0, 1.0, 0.0);
    v_ring = vec2(0.0);
    v_ring_radius = 0.0;

    if (part < 0.5) {
        float radius = blob_size(i, t) * enabled;
        radius = radius < 0.004 ? 0.0 : radius;
        float phi = UV2.x * TAU;
        float theta = UV2.y * 3.14159265359;
        vec3 unit = vec3(sin(theta) * cos(phi), cos(theta), sin(theta) * sin(phi));
        world = center + unit * radius;
        n = unit;
    } else if (part < 1.5) {
        float nr = neck_size(i, t) * enabled;
        // Cap the thread short so a tall volume can't stretch it into a flagpole, and stop it inside
        // the bubble's lower half so the two surfaces don't double up. - xlinka
        float radius = blob_size(i, t);
        float neck_top = min(min(center.y - radius * 0.35, origin.y + volume_height), origin.y + 0.9);
        float base = origin.y - 0.02;
        float h = max(neck_top - base, 0.0);
        if (nr < 0.004) {
            nr = 0.0;
            h = 0.0;
        }
        // Tapers toward the bubble like a drip letting go, instead of a pole. - xlinka
        float taper = mix(1.0, 0.30, UV2.y);
        float phi = UV2.x * TAU;
        vec2 c = vec2(cos(phi), sin(phi));
        world = vec3(center.x + c.x * nr * taper, base + UV2.y * h, center.z + c.y * nr * taper);
        n = normalize(vec3(c.x, 0.22, c.y));
    } else {
        float lift = smoothstep(0.04, 0.42, life);
        v_ring_radius = mix(blob_radius * 1.35, ripple_radius, lift);
        float extent = (v_ring_radius + ripple_width * 3.0) * enabled;
        extent *= ripple_fade(life) > 0.001 ? 1.0 : 0.0;
        v_ring = UV2 * extent;
        world = vec3(center.x + v_ring.x, origin.y + 0.006, center.z + v_ring.y);
    }

    v_world = world;
    v_normal = n;
    v_part = part;
    v_life = life;
    v_pop = blob_pop_life(i);

    VERTEX = world;
    NORMAL = n;
}

vec3 blob_color(vec3 p, float t) {
    float h = sin(p.y * 1.8 + t * 0.7) * 0.5 + 0.5;
    return mix(tint_a.rgb, tint_b.rgb, h);
}

void shade_bubble(float t, vec3 cam, out vec3 albedo, out vec3 emission, out float alpha) {
    vec3 n = normalize(v_normal);
    vec3 v = normalize(cam - v_world);
    float ndv = clamp(dot(n, v), 0.0, 1.0);
    float fres = pow(1.0 - ndv, fresnel_power);
    float rim = pow(1.0 - ndv, rim_falloff) * rim_strength;
    bool neck = v_part > 0.5;

    // During the burst, alpha collapses to a rim-only shell so the bubble reads
    // as a stretching soap film instead of a glowing sphere. Once vanish kicks
    // in, the whole thing dies. - xlinka
    float burst_t = smoothstep(v_pop, v_pop + 0.022, v_life);
    float vanish_t = smoothstep(v_pop + 0.012, v_pop + 0.032, v_life);
    float born = smoothstep(0.02, 0.14, v_life);
    float shell = mix(1.0, clamp(fres * 1.9 + 0.12, 0.0, 1.0), burst_t);
    float life_alpha = born * (1.0 - vanish_t) * shell;
    float neck_alpha = neck ? 0.70 : 1.0;
    // Sharp flash right at the rupture moment, peaks in a couple frames. - xlinka
    float pop_flash = neck ? 0.0 : exp(-pow((v_life - (v_pop + 0.003)) * 38.0, 2.0));

    vec3 base = blob_color(v_world, t);
    albedo = base * (0.74 + n.y * 0.26) + vec3(rim);
    emission = base * (0.24 + fres + pop_flash * 2.4) * emission_strength;
    alpha = clamp(alpha_scale * life_alpha * neck_alpha * (0.58 + fres * 0.42) + pop_flash * 0.45, 0.0, 1.0);
}

void fragment() {
    float t = TIME * time_scale;

    if (v_part > 1.5) {
        // Floor ripple ring, the same shape the floor shader used to evaluate for every bubble in
        // every pixel. Only the ring's own quad pays for it now. - xlinka
        float d = length(v_ring);
        float width = ripple_width + blob_smoothness * 0.025;
        float ring = 1.0 - smoothstep(width * 0.35, width, abs(d - v_ring_radius));
        float contact = 1.0 - smoothstep(0.30, 0.72, v_life);
        float center_pull = 1.0 - smoothstep(0.0, blob_radius * 1.8, d);
        float ripple = clamp(ring * ripple_fade(v_life) + center_pull * contact * 0.50, 0.0, 1.0) * ripple_strength;
        if (ripple < 0.002) {
            discard;
        }
        vec3 rgb = mix(ripple_color.rgb, tint_b.rgb, ripple * 0.35);
        ALBEDO = rgb;
        EMISSION = rgb * ripple * 0.95 * emission_strength;
        ALPHA = clamp(ripple * 0.62, 0.0, 0.82) * alpha_scale;
    } else {
        vec3 albedo;
        vec3 emission;
        float alpha;
        shade_bubble(t, INV_VIEW_MATRIX[3].xyz, albedo, emission, alpha);
        ALBEDO = albedo;
        EMISSION = emission;
        ALPHA = alpha;
    }
}

