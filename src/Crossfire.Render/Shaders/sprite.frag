#version 450

// Signed-distance shapes: crisp at any resolution, no textures or descriptor sets needed.
layout(location = 0) in vec2 vLocal;          // -1..1 across the quad
layout(location = 1) in vec4 vColor;
layout(location = 2) flat in vec4 vParams;    // y = shape, z/w = params
layout(location = 3) flat in vec2 vSizePx;    // half extents in pixels
layout(location = 4) in vec2 vUV;

layout(set = 0, binding = 0) uniform sampler2D uAtlas;

layout(location = 0) out vec4 outColor;

const int SHAPE_BULLET  = 0; // bright core, team-coloured body, dark outline
const int SHAPE_RING    = 1; // z = thickness (fraction of radius)
const int SHAPE_RECT    = 2;
const int SHAPE_GLOW    = 3;
const int SHAPE_ARC     = 4; // z = progress 0..1 (clockwise from top), w = thickness
const int SHAPE_DIAMOND = 5; // z = outline thickness
const int SHAPE_CROSS   = 6; // z = bar thickness, w = centre gap
const int SHAPE_DISC    = 7; // filled circle
const int SHAPE_TEXTURED = 8; // atlas sprite * tint
const int SHAPE_NEBULA  = 9; // z = time (s), w = palette (0 blue, 1 red); rgb = brightness
const int SHAPE_VIGNETTE = 10; // darkens toward the edges; a = strength

// Value noise + fbm for the nebula background.
float hash21(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float vnoise(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash21(i), hash21(i + vec2(1.0, 0.0)), u.x),
               mix(hash21(i + vec2(0.0, 1.0)), hash21(i + vec2(1.0, 1.0)), u.x), u.y);
}

float fbm(vec2 p)
{
    // Rotating each octave hides the axis-aligned creases of value noise.
    const mat2 rot = mat2(0.80, 0.60, -0.60, 0.80);
    float v = 0.0, amp = 0.5;
    for (int i = 0; i < 4; i++)
    {
        v += amp * vnoise(p);
        p = rot * p * 2.03 + vec2(17.1, 9.2);
        amp *= 0.5;
    }
    return v;
}

// Antialiased coverage of "inside" for signed distance d (in -1..1 local units).
float cover(float d)
{
    float px = 1.0 / max(min(vSizePx.x, vSizePx.y), 1.0); // one pixel in local units
    return clamp(0.5 - d / px, 0.0, 1.0);
}

void main()
{
    int shape = int(vParams.y + 0.5);
    vec2 p = vLocal;
    float r = length(p);
    vec4 c = vColor;
    float a = 0.0;

    if (shape == SHAPE_TEXTURED)
    {
        outColor = texture(uAtlas, vUV) * c;
        return;
    }
    if (shape == SHAPE_NEBULA)
    {
        float t = vParams.z;
        bool red = vParams.w > 0.5;
        // Aspect-correct, resolution-independent coordinates; offset per half so the two differ.
        vec2 q = p * vec2(vSizePx.x / max(vSizePx.y, 1.0), 1.0) * 1.7 + (red ? vec2(11.3, 4.7) : vec2(0.0));
        // Domain warping: noise displaced by noise gives soft, swirling gas.
        vec2 w = vec2(fbm(q + vec2(0.0, t * 0.020)), fbm(q + vec2(5.2, 1.3) - vec2(t * 0.015, 0.0)));
        float n = fbm(q + 1.8 * w + vec2(t * 0.010, -t * 0.012));
        float n2 = fbm(q * 2.3 - w + 3.1);
        vec3 deep = red ? vec3(0.050, 0.015, 0.040) : vec3(0.015, 0.025, 0.070);
        vec3 c1   = red ? vec3(0.400, 0.060, 0.220) : vec3(0.080, 0.250, 0.600);
        vec3 c2   = red ? vec3(0.680, 0.240, 0.360) : vec3(0.420, 0.220, 0.750);
        vec3 col = deep;
        col = mix(col, c1, smoothstep(0.35, 0.85, n) * 0.70);
        col = mix(col, c2, smoothstep(0.30, 0.75, n * n2 * 1.6) * 0.55);
        col *= 0.70 + 0.30 * smoothstep(0.25, 0.70, n2); // darker dust lanes
        outColor = vec4(col * c.rgb, c.a);
        return;
    }
    if (shape == SHAPE_VIGNETTE)
    {
        float d = length(p * vec2(0.80, 1.0));
        a = smoothstep(0.55, 1.30, d);
        outColor = vec4(c.rgb, c.a * a);
        return;
    }
    if (shape == SHAPE_BULLET)
    {
        a = cover(r - 1.0);
        vec3 outline = c.rgb * 0.18;
        vec3 body = c.rgb;
        vec3 core = mix(c.rgb, vec3(1.0), 0.85);
        vec3 col = mix(outline, body, smoothstep(0.86, 0.74, r));
        col = mix(col, core, smoothstep(0.5, 0.38, r));
        outColor = vec4(col, c.a * a);
        return;
    }
    else if (shape == SHAPE_RING)
    {
        float t = vParams.z;
        a = cover(abs(r - (1.0 - t * 0.5)) - t * 0.5);
    }
    else if (shape == SHAPE_RECT)
    {
        a = 1.0;
    }
    else if (shape == SHAPE_GLOW)
    {
        float g = clamp(1.0 - r, 0.0, 1.0);
        a = g * g;
    }
    else if (shape == SHAPE_ARC)
    {
        float t = vParams.w;
        float ring = cover(abs(r - (1.0 - t * 0.5)) - t * 0.5);
        float ang = atan(p.x, -p.y) / 6.28318530718; // 0 at top, clockwise in screen space (y down)
        if (ang < 0.0) ang += 1.0;
        a = ang <= vParams.z ? ring : 0.0;
    }
    else if (shape == SHAPE_DIAMOND)
    {
        float d = abs(p.x) + abs(p.y) - 1.0;
        float t = vParams.z;
        a = cover(d);
        float inner = cover(d + t);
        outColor = vec4(mix(c.rgb * 0.15, c.rgb, inner), c.a * a);
        return;
    }
    else if (shape == SHAPE_CROSS)
    {
        float t = vParams.z, gap = vParams.w;
        float h = max(abs(p.y) - t, gap - abs(p.x));
        float v = max(abs(p.x) - t, gap - abs(p.y));
        a = max(cover(h), cover(v));
    }
    else if (shape == SHAPE_DISC)
    {
        a = cover(r - 1.0);
    }

    outColor = vec4(c.rgb, c.a * a);
}
