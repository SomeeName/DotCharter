#version 450
layout (location = 0) in vec3 aPos;
layout (location = 1) in vec4 aColor;
layout (location = 2) in vec3 aNormal;
layout (location = 3) in vec2 aTexCoord;
layout (location = 4) in float aVertexType;

layout(push_constant) uniform PushConstants {
    mat4 uMVP;
    float uCamX; float uCamY; float uCamZ;
    float uFadeStart;
    float uFadeEnd;
    float uTime;
    uint uEffects; // HighwayEffects bit set (lighting/shimmer toggles)
} pc;

layout(location = 0) out vec4 fColor;
layout(location = 1) out vec3 fNormal;
layout(location = 2) out vec2 fTexCoord;
layout(location = 3) out float fVertexType;
layout(location = 4) out vec3 fFragPos;

void main() {
    gl_Position = pc.uMVP * vec4(aPos, 1.0);
    fFragPos = aPos;
    fColor = aColor;
    fNormal = aNormal;
    fTexCoord = aTexCoord;
    fVertexType = aVertexType;
}