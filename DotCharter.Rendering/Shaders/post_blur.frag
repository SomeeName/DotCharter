#version 450
// Separable 9-tap gaussian for the bloom mip chain (plan 5.5). uDir selects
// the axis in texel units ((1,0) horizontal / (0,1) vertical). With
// uDir = (0,0) every tap lands on the same point and the weights sum to 1,
// so the SAME pipeline doubles as the plain downsample copy between mips.
layout(location = 0) in vec2 vUv;
layout(binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;

layout(push_constant) uniform PostPC {
    vec2 uTexel;   // 1 / source size
    vec2 uDir;     // blur axis (texels)
    vec4 uParams;  // unused here
} pc;

void main() {
    // Normalized 9-tap gaussian (sigma ~1.8).
    const float w[5] = float[](0.2270270, 0.1945946, 0.1216216, 0.0540541, 0.0162162);
    vec2 step = pc.uDir * pc.uTexel;
    vec3 acc = texture(uTexture, vUv).rgb * w[0];
    for (int i = 1; i < 5; i++) {
        acc += texture(uTexture, vUv + step * float(i)).rgb * w[i];
        acc += texture(uTexture, vUv - step * float(i)).rgb * w[i];
    }
    FragColor = vec4(acc, 1.0);
}
