#version 450
// Bloom threshold extract (plan 5.5): keeps only the bright emissive part of
// the scene (note gems, string shimmer highlights, HUD accents) with a soft
// knee so the cutoff never bands. Renders at half resolution.
layout(location = 0) in vec2 vUv;
layout(binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;

layout(push_constant) uniform PostPC {
    vec2 uTexel;   // 1 / source size
    vec2 uDir;     // unused here
    vec4 uParams;  // x = threshold, y = soft knee width
} pc;

void main() {
    vec3 c = texture(uTexture, vUv).rgb;
    float brightness = max(c.r, max(c.g, c.b));
    float threshold = pc.uParams.x;
    float knee = max(pc.uParams.y, 1e-4);
    // 0 below (threshold - knee), 1 above threshold, smooth in between.
    float w = smoothstep(threshold - knee, threshold, brightness);
    FragColor = vec4(c * w, 1.0);
}
