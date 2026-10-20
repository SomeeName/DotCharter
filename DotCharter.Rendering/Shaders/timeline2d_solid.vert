#version 450

// Composed-Vulkan 2D timeline — solid (flat-shaded) stream.
// Mirrors the OpenGL engine's Solid shader byte-for-byte in math: vertices
// arrive in LOGICAL pixel units, uResolution is the logical viewport size.
// Vulkan clip space is y-down, so +clipSpace.y (where the GL shader negates)
// stores layer row 0 at image row 0 — a top-down image, counter-flipped once
// by the composed host visual (Scale(1,-1,1)), exactly like composed 3D.

layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec4 aColor;

layout(location = 0) out vec4 vColor;

layout(push_constant) uniform Timeline2DPush {
    vec2 uResolution;
} pc;

void main() {
    vec2 zeroToOne = aPosition / pc.uResolution;
    vec2 clipSpace = zeroToOne * 2.0 - 1.0;
    gl_Position = vec4(clipSpace.x, clipSpace.y, 0.0, 1.0);
    vColor = aColor;
}
