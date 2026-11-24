#version 450

// One instanced quad per sprite. No vertex buffer for the quad itself: corners come from gl_VertexIndex.
layout(location = 0) in vec2 iPos;     // centre, arena units
layout(location = 1) in vec2 iSize;    // half extents, arena units
layout(location = 2) in vec4 iColor;   // straight (non-premultiplied) RGBA
layout(location = 3) in vec4 iParams;  // x = rotation (rad), y = shape id, z/w = shape params
layout(location = 4) in vec4 iUV;      // atlas rect (u0, v0, u1, v1) for textured sprites

layout(push_constant) uniform Push
{
    vec2 scale;   // arena units -> NDC
    vec2 offset;
    float pixelsPerUnit;
} pc;

layout(location = 0) out vec2 vLocal;
layout(location = 1) out vec4 vColor;
layout(location = 2) flat out vec4 vParams;
layout(location = 3) flat out vec2 vSizePx;
layout(location = 4) out vec2 vUV;

const vec2 kCorners[6] = vec2[](
    vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(1.0, 1.0),
    vec2(-1.0, -1.0), vec2(1.0, 1.0), vec2(-1.0, 1.0));

void main()
{
    vec2 c = kCorners[gl_VertexIndex];
    vec2 local = c * iSize;
    float s = sin(iParams.x), co = cos(iParams.x);
    vec2 world = iPos + vec2(co * local.x - s * local.y, s * local.x + co * local.y);
    gl_Position = vec4(world * pc.scale + pc.offset, 0.0, 1.0);
    vLocal = c;
    vColor = iColor;
    vParams = iParams;
    vSizePx = iSize * pc.pixelsPerUnit;
    vUV = mix(iUV.xy, iUV.zw, c * 0.5 + 0.5);
}
