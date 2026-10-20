using DotCharter.Rendering.Vulkan;

namespace DotCharter.Tests.Rendering;

public class QuadIndexMathTests
{
    [Fact]
    public void BuildPattern_EmitsTwoTrianglesPerQuad()
    {
        var indices = QuadIndexMath.BuildPattern(3);

        Assert.Equal(3 * QuadIndexMath.IndicesPerQuad, indices.Length);
        for (var q = 0; q < 3; q++)
        {
            var v = (uint)(q * QuadIndexMath.VerticesPerQuad);
            var i = q * QuadIndexMath.IndicesPerQuad;
            Assert.Equal(new[] { v, v + 1, v + 2, v, v + 2, v + 3 },
                indices[i..(i + QuadIndexMath.IndicesPerQuad)]);
        }
    }

    [Fact]
    public void BuildPattern_ZeroQuads_IsEmpty() => Assert.Empty(QuadIndexMath.BuildPattern(0));

    [Fact]
    public void BuildPattern_NegativeQuads_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => QuadIndexMath.BuildPattern(-1));

    [Theory]
    // Highway stream: 13 floats per vertex, 52 per quad.
    [InlineData(52, 13, 1)]
    [InlineData(104, 13, 2)]
    [InlineData(51, 13, 0)] // partial quad excluded
    [InlineData(103, 13, 1)] // trailing partial quad excluded
    [InlineData(0, 13, 0)]
    [InlineData(-52, 13, 0)]
    // Text stream: counted in whole vertices (elementsPerVertex = 1).
    [InlineData(4, 1, 1)]
    [InlineData(7, 1, 1)]
    [InlineData(8, 1, 2)]
    public void QuadCount_CountsWholeQuadsOnly(int elementCount, int elementsPerVertex, int expected) => Assert.Equal(expected, QuadIndexMath.QuadCount(elementCount, elementsPerVertex));

    [Fact]
    public void Pattern_NeverReferencesVerticesOutsideItsQuadRange()
    {
        var indices = QuadIndexMath.BuildPattern(100);
        for (var i = 0; i < indices.Length; i++)
        {
            var quad = i / QuadIndexMath.IndicesPerQuad;
            Assert.InRange(indices[i],
                (uint)(quad * QuadIndexMath.VerticesPerQuad),
                (uint)(quad * QuadIndexMath.VerticesPerQuad + QuadIndexMath.VerticesPerQuad - 1));
        }
    }
}
