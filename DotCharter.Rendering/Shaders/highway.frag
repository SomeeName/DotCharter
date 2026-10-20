#version 450
layout(location = 0) in vec4 fColor;
layout(location = 1) in vec3 fNormal;
layout(location = 2) in vec2 fTexCoord;
layout(location = 3) in float fVertexType;
layout(location = 4) in vec3 fFragPos;

layout(binding = 0) uniform sampler2D uTexture;

// Must stay layout-identical to the block in highway.vert (one shared
// 92-byte push-constant range covering both stages).
layout(push_constant) uniform PushConstants {
    mat4 uMVP;
    float uCamX; float uCamY; float uCamZ;
    float uFadeStart;
    float uFadeEnd;
    float uTime;
    uint uEffects;
} pc;

// HighwayEffects bits (keep in sync with DotCharter.Rendering.Vulkan.HighwayEffects).
const uint EFFECT_LIGHTING = 1u;
const uint EFFECT_STRING_SHIMMER = 2u;

layout(location = 0) out vec4 FragColor;

void main() {
    if (fVertexType < 1.5) {
        // Mode 1: Solid Color / Flat Alpha
        FragColor = fColor;
    }
    else if (fVertexType < 2.5) {
        // Mode 2: Highway Sustains & Glow
        float distFromCenter = abs(fTexCoord.x - 0.5) * 2.0;
        float solidCore = 1.0 - smoothstep(0.5, 0.75, distFromCenter);
        float hotCenter = 1.0 - smoothstep(0.0, 0.25, distFromCenter);
        float outerGlow = 1.0 - smoothstep(0.6, 1.0, distFromCenter);

        vec3 hotWhite = vec3(1.0, 1.0, 1.0);
        vec3 baseColor = fColor.rgb * 1.3;

        vec3 finalRGB = baseColor * solidCore + hotWhite * hotCenter * 0.4;
        finalRGB += fColor.rgb * 0.3 * outerGlow;

        float finalAlpha = fColor.a * (solidCore * 0.85 + outerGlow * 0.15);
        // Highway near-fade: lanes/sustains fade to invisible as they reach
        // the chartboard, mirroring RsCharter's FadingShader behaviour.
        finalAlpha *= smoothstep(-0.5, 4.0, fFragPos.z);
        FragColor = vec4(finalRGB, finalAlpha);
    } 
    else if (fVertexType < 3.5) {
        // Mode 3: Shadow Highlight Texture (Matches Java's ShadowHighlightTextureShader)
        // R = Shadow/Multiply, G = Highlight/Add, B = Alpha Channel
        vec4 texColor = texture(uTexture, fTexCoord);
        if (texColor.b == 0.0 || fColor.a == 0.0) {
            discard;
        }
        FragColor = vec4(fColor.rgb * texColor.r + texColor.g, fColor.a * texColor.b);
    } 
    else if (fVertexType < 4.5) {
        // Mode 4: Standard Textured Model (Matches Java's BaseTextureShader)
        vec4 texColor = texture(uTexture, fTexCoord);
        FragColor = texColor * fColor;
        if (FragColor.a < 0.05) {
            discard;
        }
    }
    else if (fVertexType < 5.5) {
        // Mode 5: Chord Panes & Edges
        float distFromCenter = abs(fTexCoord.y - 0.5) * 2.0;
        float glow = 1.0 - smoothstep(0.0, 1.0, distFromCenter);
        vec3 boosted = fColor.rgb * (1.0 + glow * 0.4);
        float a = fColor.a * smoothstep(-0.5, 4.0, fFragPos.z);
        FragColor = vec4(boosted, a);
    }
    else if (fVertexType < 6.5) {
        // Mode 6: Harmonic Notes
        vec4 texColor = texture(uTexture, fTexCoord);
        float brightness = max(texColor.r, max(texColor.g, texColor.b));
        if (brightness < 0.05) {
            discard;
        }
        vec3 finalRGB = (fColor.rgb * texColor.r * 1.8) + vec3(texColor.g * 1.4);
        vec3 harmonicGlow = vec3(0.25, 0.5, 0.75) * texColor.r;
        FragColor = vec4(finalRGB + harmonicGlow, fColor.a);
    }
    else if (fVertexType < 7.5) {
        // Mode 7: Handshape Highlights
        float zFade = 1.0 - smoothstep(0.0, 1.0, fTexCoord.y * 0.7);
        float xDist = abs(fTexCoord.x - 0.5) * 2.0;
        float xFade = 1.0 - smoothstep(0.85, 1.0, xDist);
        float intensity = zFade * xFade;
        
        vec3 glowColor = fColor.rgb * intensity * 1.5;
        float glowAlpha = fColor.a * intensity;
        FragColor = vec4(glowColor, glowAlpha);
    }
    else if (fVertexType < 8.5) {
        // Mode 8: Radial Glow
        float distFromCenter = length(fTexCoord - vec2(0.5)) * 2.0;
        float glow = 1.0 - smoothstep(0.0, 1.0, distFromCenter);
        glow = glow * glow;
        
        vec3 glowColor = fColor.rgb * glow;
        float glowAlpha = fColor.a * glow;
        FragColor = vec4(glowColor, glowAlpha);
    }
    else if (fVertexType < 9.5) {
        // Mode 9: Edge Glow
        float edgeX = min(fTexCoord.x, 1.0 - fTexCoord.x) * 2.0;
        float edgeY = min(fTexCoord.y, 1.0 - fTexCoord.y) * 2.0;
        float edgeDist = min(edgeX, edgeY);
        float edgeGlow = 1.0 - smoothstep(0.0, 0.3, edgeDist);
        
        vec3 fillColor = fColor.rgb * (1.0 + edgeGlow * 0.8);
        float fillAlpha = fColor.a * (1.0 + edgeGlow * 0.5);
        fillAlpha *= smoothstep(-0.5, 4.0, fFragPos.z);
        FragColor = vec4(fillColor, fillAlpha);
    }
    else if (fVertexType < 10.5) {
        // Mode 10: Fret Lane Soft Glow
        float distFromCenter = abs(fTexCoord.x - 0.5) * 2.0;
        float glow = 1.0 - smoothstep(0.0, 1.0, distFromCenter);
        glow = glow * glow;
        
        vec3 glowColor = fColor.rgb * glow * 0.5;
        float glowAlpha = fColor.a * glow * 0.7;
        glowAlpha *= smoothstep(-0.5, 4.0, fFragPos.z);
        FragColor = vec4(glowColor, glowAlpha);
    }
    else if (fVertexType < 11.5) {
        // Mode 11: Solid color with chartboard near-fade.
        // Used by highway lane spans / handshape borders so they fade out
        // smoothly as they reach the strike-line, mirroring RsCharter.
        float a = fColor.a * smoothstep(-0.5, 4.0, fFragPos.z);
        FragColor = vec4(fColor.rgb, a);
    }
    else if (fVertexType < 12.5) {
        // Mode 12: String core (plan 5.7). Solid color exactly like mode 1
        // when the shimmer effect is off; with EFFECT_STRING_SHIMMER a slow
        // light-band travels along the wire (uTime = song ms, so it freezes
        // while paused — consistent with the idle-gated render loop) plus a
        // faint higher-frequency sparkle. Deliberately subtle (±~14%).
        vec3 rgb = fColor.rgb;
        if ((pc.uEffects & EFFECT_STRING_SHIMMER) != 0u) {
            float t = pc.uTime * 0.001; // seconds
            float band = sin(fFragPos.x * 1.7 - t * 4.2 + fFragPos.y * 6.0);
            float sparkle = sin(fFragPos.x * 11.3 + t * 7.9 + fFragPos.y * 23.0);
            float shimmer = 0.10 * band + 0.04 * sparkle;
            rgb *= 1.0 + shimmer;
        }
        FragColor = vec4(rgb, fColor.a);
    }

    // Highway lighting (plan 5.6): a simple directional key light + soft fill
    // evaluated from the streamed per-vertex normals. Applied only to the
    // "surface" modes (solid geometry, textured sprites, chord panes, edge
    // glow, faded solids) — the additive/glow modes (2, 6, 7, 8, 10) stay
    // untouched so emissive effects keep their brightness. Deliberately
    // subtle: the multiplier stays within ~[0.82, 1.20], preserving the flat
    // look while orientation differences (floor vs board vs sprites) gain
    // depth. abs(dot) = two-sided, since quad normals are not consistently
    // wound across components.
    if ((pc.uEffects & EFFECT_LIGHTING) != 0u) {
        bool litSurface =
            fVertexType < 1.5 ||
            (fVertexType >= 2.5 && fVertexType < 5.5) ||
            (fVertexType >= 8.5 && fVertexType < 9.5) ||
            fVertexType >= 10.5;
        float nLen = length(fNormal);
        if (litSurface && nLen > 0.001) {
            vec3 n = fNormal / nLen;
            vec3 keyDir = vec3(-0.2949, 0.7864, -0.5111);  // upper front-left (normalized)
            vec3 fillDir = vec3(0.5507, 0.1502, 0.8210);   // low opposite fill (normalized)
            float diff = abs(dot(n, keyDir));
            float fillL = abs(dot(n, fillDir));
            FragColor.rgb = FragColor.rgb * (0.82 + 0.26 * diff + 0.12 * fillL);
        }
    }

    // Dither as the very last step, against the 8-bit target's bands (the dark
    // sky gradient and the vignette read as stair-steps). Triangular noise
    // (the sum of two uniform hashes) spanning ±1 LSB is what fully breaks a
    // quantisation step; the ±0.5 LSB uniform noise this replaced only thinned
    // the bands. The blend then multiplies rgb by alpha, so a translucent
    // fragment's noise is scaled up by 1/alpha to still land ±1 LSB in the
    // frame (capped at 4 LSB so a faint glow never turns into grain).
    float n1 = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);
    float n2 = fract(sin(dot(gl_FragCoord.xy, vec2(39.3468, 11.1353))) * 24634.6345);
    float amp = min(1.0 / max(FragColor.a, 0.001), 4.0) / 255.0;
    FragColor.rgb += vec3((n1 + n2 - 1.0) * amp);
}