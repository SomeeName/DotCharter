#version 450
// Composite-time textured fullscreen draw (plan 5.5): draws the rendered
// scene onto the swapchain image (opaque pipeline, intensity 1) and the bloom
// mips over it (additive pipeline, weighted intensity). uParams.y != 0
// applies the filmic tonemap (approved follow-up) to the SCENE draw only —
// bloom is added after the curve (LDR pipeline, no format change; fine for a
// subtle accent glow).
layout(location = 0) in vec2 vUv;
layout(binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;

layout(push_constant) uniform PostPC {
    vec2 uTexel;   // unused here
    vec2 uDir;     // unused here
    vec4 uParams;  // x = intensity, y = tonemap flag
} pc;

// Narkowicz ACES filmic approximation: gentle highlight roll-off + a touch
// of midtone contrast. Input is LDR (B8G8R8A8 stays the swapchain format),
// so a mild pre-gain keeps the overall brightness instead of darkening
// everything below the curve's shoulder.
vec3 acesFilm(vec3 x) {
    x *= 1.25; // exposure pre-gain — tuned so mid grey survives the curve
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main() {
    vec3 c = texture(uTexture, vUv).rgb * pc.uParams.x;
    if (pc.uParams.y > 0.5) {
        c = acesFilm(c);
    }
    FragColor = vec4(c, 1.0);
}
