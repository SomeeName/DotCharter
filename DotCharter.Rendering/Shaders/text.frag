#version 450
layout(location = 0) in vec4 fColor;
layout(location = 1) in vec2 fTexCoord;

layout(binding = 0) uniform sampler2D uTexture;

layout(location = 0) out vec4 FragColor;

void main() {
    vec4 sampled = texture(uTexture, fTexCoord);
    FragColor = fColor * sampled;
    
    // Discard transparent pixels to prevent z-buffer fighting
    if (FragColor.a < 0.05) {
        discard;
    }
}