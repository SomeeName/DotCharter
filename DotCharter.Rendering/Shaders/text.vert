#version 450
layout (location = 0) in vec3 aPos;
layout (location = 1) in vec4 aColor;
layout (location = 2) in vec2 aTexCoord;

layout(push_constant) uniform PushConstants {
    mat4 uMVP;
} pc;

layout(location = 0) out vec4 fColor;
layout(location = 1) out vec2 fTexCoord;

void main() {
    gl_Position = pc.uMVP * vec4(aPos, 1.0);
    fColor = aColor;
    fTexCoord = aTexCoord;
}