#version 450

// Composed-Vulkan 2D timeline — general (uber) stream: circles, ring
// outlines, AA lines and font glyphs. Same pixel→clip mapping as the solid
// shader (see timeline2d_solid.vert for the y-orientation note).

layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec4 aColor;
layout(location = 2) in vec2 aUv;
layout(location = 3) in float aType;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out float vType;
// Dashed vertical AA line (TimelineBatcher.AddDashedVerticalLineAaRaw): the
// along-line coordinate is the logical y, and the dash phase is anchored at
// the quad's TOP edge. Every quad's first vertex is its top-left corner and
// both triangles start there, so a flat varying of the provoking vertex's y
// IS the line top. vDash = (dash length, dash + gap) in logical px.
layout(location = 3) out float vAlong;
layout(location = 4) flat out float vTop;
layout(location = 5) flat out vec2 vDash;

layout(push_constant) uniform Timeline2DPush {
    vec2 uResolution;
} pc;

// Must match TimelineBatcher.DashedLineTypeBase / DashedLineUnitsPerPx /
// DashedLineDashStride: type = base + round(dash * 16) * 1024 + round(gap * 16).
const float DashedLineTypeBase = 8.0;
const float DashedLineUnitsPerPx = 16.0;
const float DashedLineDashStride = 1024.0;

void main() {
    vec2 zeroToOne = aPosition / pc.uResolution;
    vec2 clipSpace = zeroToOne * 2.0 - 1.0;
    gl_Position = vec4(clipSpace.x, clipSpace.y, 0.0, 1.0);
    vColor = aColor;
    vUv = aUv;
    vType = aType;
    vAlong = aPosition.y;
    vTop = aPosition.y;
    vDash = vec2(0.0, 1.0);
    if (aType > DashedLineTypeBase - 0.5) {
        float code = floor(aType - DashedLineTypeBase + 0.5);
        float dashUnits = floor(code / DashedLineDashStride);
        float gapUnits = code - dashUnits * DashedLineDashStride;
        float dash = dashUnits / DashedLineUnitsPerPx;
        vDash = vec2(dash, dash + gapUnits / DashedLineUnitsPerPx);
    }
}
