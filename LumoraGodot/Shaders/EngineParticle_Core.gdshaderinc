// Shared body of the engine particle shader.
//
// Blending is a compile-time render_mode in Godot, so the one thing that actually differs between an
// additive spark and an alpha-blended splash cannot be a uniform. The body lives here and the variants
// beside it carry a single line each. Which one a system gets is decided by ParticleSystem.BlendMode,
// and for an imported effect that value comes from the material its own renderer named. -xlinka

uniform float emission_strength : hint_range(0.0, 6.0) = 1.4;

// x = columns, y = rows, z = frame count, w = 1 when a sheet is actually bound. With w at 0 this
// shader is byte for byte the one that shipped before flipbooks existed, which is the point: every
// effect in every world already out there has no sheet and must not change.
uniform vec4 flipbook_sheet = vec4(1.0, 1.0, 1.0, 0.0);
uniform sampler2D flipbook_texture : source_color, hint_default_white, filter_linear;

// Face the quad at the viewer. On for any TEXTURED system (a sprite is drawn to be seen flat-on), off
// for the untextured sphere fallback, which billboards to nothing, and off when the system drives its
// own particle rotation.
uniform bool sheet_billboard = false;

// INSTANCE_CUSTOM only exists in vertex(), so the frame has to be carried across. Flat because it is
// per particle, not per corner: interpolating it would smear one particle's frame into its own edges.
varying flat float sheet_frame;

void vertex() {
    // Normalised on the way in (frame / count) because instance custom data is not promised to survive
    // a trip outside 0..1 on every backend. Unpacking is one multiply and the count is here anyway.
    sheet_frame = INSTANCE_CUSTOM.x * max(flipbook_sheet.z, 1.0);

    if (sheet_billboard) {
        vec3 scale = vec3(
            length(MODEL_MATRIX[0].xyz),
            length(MODEL_MATRIX[1].xyz),
            length(MODEL_MATRIX[2].xyz));
        mat4 model_view = VIEW_MATRIX * MODEL_MATRIX;
        MODELVIEW_MATRIX = mat4(
            vec4(scale.x, 0.0, 0.0, 0.0),
            vec4(0.0, scale.y, 0.0, 0.0),
            vec4(0.0, 0.0, scale.z, 0.0),
            model_view[3]);
    }
}

void fragment() {
    vec3 rgb = COLOR.rgb;
    float alpha = COLOR.a;

    if (flipbook_sheet.w > 0.5) {
        float columns = max(flipbook_sheet.x, 1.0);
        float rows = max(flipbook_sheet.y, 1.0);
        float count = max(flipbook_sheet.z, 1.0);
        vec2 tile = vec2(1.0 / columns, 1.0 / rows);

        float wrapped = mod(sheet_frame, count);
        float first = floor(wrapped);
        float blend = wrapped - first;
        float second = mod(first + 1.0, count);

        // Row major, first row at the top. V runs down the image in Godot, so the row scales straight
        // in with no flip.
        vec2 offset_a = vec2(mod(first, columns), floor(first / columns)) * tile;
        vec2 offset_b = vec2(mod(second, columns), floor(second / columns)) * tile;

        // BOTH tiles, always. A stepped sheet at 12 fps in front of a 90 Hz headset strobes; the same
        // sheet crossfaded is continuous motion, and the fractional frame exists for no other reason.
        vec4 sheet = mix(
            texture(flipbook_texture, UV * tile + offset_a),
            texture(flipbook_texture, UV * tile + offset_b),
            blend);

        rgb *= sheet.rgb;
        alpha *= sheet.a;
    }

    ALBEDO = rgb;
    EMISSION = rgb * emission_strength;
    ALPHA = alpha;
}
