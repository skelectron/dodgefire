#version 450

// Signed-distance shapes: crisp at any resolution, no textures or descriptor sets needed.
layout(location = 0) in vec2 vLocal;          // -1..1 across the quad
layout(location = 1) in vec4 vColor;
layout(location = 2) flat in vec4 vParams;    // y = shape, z/w = params
layout(location = 3) flat in vec2 vSizePx;    // half extents in pixels

layout(location = 0) out vec4 outColor;

const int SHAPE_BULLET  = 0; // bright core, team-coloured body, dark outline
const int SHAPE_RING    = 1; // z = thickness (fraction of radius)
const int SHAPE_RECT    = 2;
const int SHAPE_GLOW    = 3;
const int SHAPE_ARC     = 4; // z = progress 0..1 (clockwise from top), w = thickness
const int SHAPE_DIAMOND = 5; // z = outline thickness
const int SHAPE_CROSS   = 6; // z = bar thickness, w = centre gap
const int SHAPE_DISC    = 7; // filled circle

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
