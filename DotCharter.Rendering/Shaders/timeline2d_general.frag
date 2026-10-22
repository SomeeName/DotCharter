#version 450

// Composed-Vulkan 2D timeline — general (uber) stream. The type branches
// mirror the OpenGL engine's General fragment shader exactly:
//   < 1.5 : filled AA circle (SDF smoothstep on |uv|)
//   < 2.5 : ring outline
//   < 3.5 : font glyph (atlas alpha modulates vertex color)
//   < 4.5 : vertical AA line (uv carries distance ramp)
//   < 5.5 : horizontal AA line
//   >= 7.5: dashed vertical AA line (type 4's across-line ramp; the dash
//           pattern is decoded by the vertex shader, see timeline2d_general.vert)
//   else  : plain vertex color

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in float vType;
layout(location = 3) in float vAlong;
layout(location = 4) flat in float vTop;
layout(location = 5) flat in vec2 vDash;

// A pixel centre that lies exactly on a dash edge is decided like the
// rasterizer's top-left rule decided it for the one-quad-per-dash geometry
// this replaces: the dash's top edge is inside, its bottom edge outside. The
// bias absorbs interpolation noise there; it is far below the 1/256 px
// sub-pixel grid, so it never moves an edge that was not a tie.
const float DashEdgeBias = 1.0 / 1024.0;

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uFont;

void main() {
    if (vType < 1.5) {
        float d = length(vUv);
        float a = 1.0 - smoothstep(0.9, 1.0, d);
        if (a <= 0.01) discard;
        outColor = vec4(vColor.rgb, vColor.a * a);
    }
    else if (vType < 2.5) {
        float d = length(vUv);
        float a = (1.0 - smoothstep(0.9, 1.0, d)) - (1.0 - smoothstep(0.7, 0.8, d));
        if (a <= 0.01) discard;
        outColor = vec4(vColor.rgb, vColor.a * a);
    }
    else if (vType < 3.5) {
        vec4 texColor = texture(uFont, vUv);
        if (texColor.a <= 0.01) discard;
        outColor = vec4(vColor.rgb, vColor.a * texColor.a);
    }
    else if (vType < 4.5) {
        float a = clamp(min(vUv.x, vUv.y - vUv.x), 0.0, 1.0);
        outColor = vec4(vColor.rgb, vColor.a * a);
    }
    else if (vType < 5.5) {
        float a = clamp(min(vUv.y, vUv.x - vUv.y), 0.0, 1.0);
        outColor = vec4(vColor.rgb, vColor.a * a);
    }
    else if (vType > 7.5) {
        // Gaps are not drawn at all (the old geometry had no quad there), so
        // blending stays exactly what the separate dash quads produced.
        if (mod(vAlong - vTop + DashEdgeBias, vDash.y) >= vDash.x) discard;
        float a = clamp(min(vUv.x, vUv.y - vUv.x), 0.0, 1.0);
        outColor = vec4(vColor.rgb, vColor.a * a);
    }
    else {
        outColor = vColor;
    }
}
