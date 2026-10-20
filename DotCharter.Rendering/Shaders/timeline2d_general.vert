#version 450

// Composed-Vulkan 2D timeline — general (uber) stream: circles, ring
// outlines, AA lines and font glyphs. Same pixel→clip mapping as the solid
// shader (see timeline2d_solid.vert for the y-orientation note).

layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec4 aColor;
layout(location = 2) in vec2 aUv;
layout(location = 3) in float aType;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out float vType;

layout(push_constant) uniform Timeline2DPush {
    vec2 uResolution;
} pc;

void main() {
    vec2 zeroToOne = aPosition / pc.uResolution;
    vec2 clipSpace = zeroToOne * 2.0 - 1.0;
    gl_Position = vec4(clipSpace.x, clipSpace.y, 0.0, 1.0);
    vColor = aColor;
    vUv = aUv;
    vType = aType;
}
