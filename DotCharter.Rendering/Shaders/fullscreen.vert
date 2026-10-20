#version 450
// Fullscreen triangle from gl_VertexIndex (no vertex buffer): one triangle
// covering the whole viewport, uv 0..1 across the visible area. Used by every
// post-process pass (bloom extract/blur, composite).
layout(location = 0) out vec2 vUv;

void main() {
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vUv = uv;
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
}
