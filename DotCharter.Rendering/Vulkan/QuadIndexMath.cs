namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Pure math for the shared static quad index buffer (0.15.6 / plan 5.9).
///     Every quad batch streams 4 unique vertices per quad; the GPU expands them
///     through one immutable index buffer holding the repeating pattern
///     <c>[4q, 4q+1, 4q+2, 4q, 4q+2, 4q+3]</c>. The buffer is written once at
///     pipeline init and shared by the highway and text vertex streams (both are
///     pure quad lists) — per-draw addressing happens via
///     <c>vkCmdDrawIndexed</c>'s <c>vertexOffset</c>, so indices always start at 0.
///     Unit-tested in <c>QuadIndexMathTests</c>.
/// </summary>
public static class QuadIndexMath
{
    public const int VerticesPerQuad = 4;
    public const int IndicesPerQuad = 6;

    /// <summary>Builds the index pattern for <paramref name="quadCount" /> quads.</summary>
    public static uint[] BuildPattern(int quadCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quadCount);
        var indices = new uint[quadCount * IndicesPerQuad];
        for (var q = 0; q < quadCount; q++)
        {
            var v = (uint)(q * VerticesPerQuad);
            var i = q * IndicesPerQuad;
            indices[i + 0] = v;
            indices[i + 1] = v + 1;
            indices[i + 2] = v + 2;
            indices[i + 3] = v;
            indices[i + 4] = v + 2;
            indices[i + 5] = v + 3;
        }

        return indices;
    }

    /// <summary>Number of whole quads contained in a vertex batch, given the
    /// per-vertex stride in array elements. Trailing partial quads are excluded
    /// (the batchers only ever emit whole quads).</summary>
    public static int QuadCount(int elementCount, int elementsPerVertex)
    {
        if (elementCount <= 0 || elementsPerVertex <= 0) return 0;
        return elementCount / (elementsPerVertex * VerticesPerQuad);
    }
}
