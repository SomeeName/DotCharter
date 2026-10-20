using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Batching;

public class TextBatcher(IVulkanRenderService vulkanRenderer)
{
    // Sized from the renderer's PER-FRAME chunk, never a bare literal: a
    // batcher that stages more than one frame's chunk holds has an auto-flush
    // the chunk guard is guaranteed to drop. It staged 50,000 against a
    // 25,000-vertex chunk for several releases, silently.
    private readonly TextVertex[] _textVertexData = new TextVertex[TextVertexBudget.PerFrameVertexCount];

    /// <summary>Vertices this batcher stages before it auto-flushes — test seam
    /// for <c>TextVertexBudgetTests</c>.</summary>
    internal int Capacity => _textVertexData.Length;
    private Matrix4x4 _currentMvp;
    private FontAtlas? _fontAtlas;
    private int _textVertexCount;

    /// <summary>Lefty flip (v0.34): when the scene MVP mirrors X, in-world
    /// text must counter-flip its glyph-LOCAL X so the double mirror restores
    /// readable orientation and run order — positions still mirror with the
    /// board. Set once per frame by the engine; only affects
    /// <see cref="DrawText3D"/> (the ortho HUD's <see cref="DrawText2D"/> uses
    /// an unmirrored matrix and must stay untouched).</summary>
    public bool MirrorLocalX { get; set; }

    public void SetState(FontAtlas? fontAtlas, Matrix4x4 mvp)
    {
        if (_fontAtlas != fontAtlas || _currentMvp != mvp) Flush();
        _fontAtlas = fontAtlas;
        _currentMvp = mvp;
    }

    public void Flush()
    {
        if (_textVertexCount == 0) return;
        if (_fontAtlas?.Texture == null)
        {
            // DISCARD, never carry. Returning with the count intact left staged
            // glyphs alive across the next SetState, so they were drawn with a
            // DIFFERENT MVP and a different atlas — in-world quads re-emitted
            // under the ortho HUD matrix. That is exactly the "text staged
            // against the wrong matrix, silently" shape that cost three
            // releases of invisible chord names.
            _textVertexCount = 0;
            return;
        }

        vulkanRenderer.DrawTextBatch(_textVertexData, _textVertexCount, _currentMvp, _fontAtlas.Texture);
        _textVertexCount = 0;
    }

    private void EnsureCapacity(int requiredAdded)
    {
        if (_textVertexCount + requiredAdded > _textVertexData.Length) Flush();
    }

    // 4 unique vertices per glyph quad (0.15.6 / plan 5.9): the renderer
    // expands them through the shared static quad index buffer.
    private void AddTextQuad(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4, Vector4 c, float u0, float u1, float v0,
        float v1)
    {
        EnsureCapacity(4);
        _textVertexData[_textVertexCount++] = new TextVertex
        { Position = p1, Color = c, TexCoord = new Vector2(u0, v0) };
        _textVertexData[_textVertexCount++] = new TextVertex
        { Position = p2, Color = c, TexCoord = new Vector2(u1, v0) };
        _textVertexData[_textVertexCount++] = new TextVertex
        { Position = p3, Color = c, TexCoord = new Vector2(u1, v1) };
        _textVertexData[_textVertexCount++] = new TextVertex
        { Position = p4, Color = c, TexCoord = new Vector2(u0, v1) };
    }

    public void DrawText3D(string text, Matrix4x4 transform, Color color, float scale = 1.0f)
    {
        if (_fontAtlas == null) return;
        var vColor = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        float totalWidth = 0;

        foreach (var c in text)
        {
            if (_fontAtlas.TryGetGlyph(c, out var m))
                totalWidth += m.AdvanceX;
        }

        var cursorX = -(totalWidth / 2f);
        var mirror = MirrorLocalX ? -1f : 1f;
        foreach (var c in text)
        {
            if (_fontAtlas.TryGetGlyph(c, out var glyph))
            {
                var x0 = cursorX * scale * mirror;
                var x1 = (cursorX + glyph.Width) * scale * mirror;
                var y0 = glyph.Height / 2f * scale;
                var y1 = -(glyph.Height / 2f) * scale;
                var p1 = Vector3.Transform(new Vector3(x0, y0, 0), transform);
                var p2 = Vector3.Transform(new Vector3(x1, y0, 0), transform);
                var p3 = Vector3.Transform(new Vector3(x1, y1, 0), transform);
                var p4 = Vector3.Transform(new Vector3(x0, y1, 0), transform);
                AddTextQuad(p1, p2, p3, p4, vColor, glyph.U0, glyph.U1, glyph.V0, glyph.V1);
                cursorX += glyph.AdvanceX;
            }
        }
    }

    public float DrawText2D(string text, Vector2 position, Color color, float scale = 1.0f)
    {
        if (_fontAtlas == null) return position.X;
        var vColor = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        var cursorX = position.X;
        var cursorY = position.Y;
        var lineHeight = 64f * scale;

        foreach (var c in text)
        {
            if (c == '\n')
            {
                cursorX = position.X;
                cursorY += lineHeight;
                continue;
            }

            if (_fontAtlas.TryGetGlyph(c, out var glyph))
            {
                var x0 = cursorX;
                var x1 = cursorX + glyph.Width * scale;
                var y0 = cursorY;
                var y1 = cursorY + glyph.Height * scale;
                var p1 = new Vector3(x0, y0, 0);
                var p2 = new Vector3(x1, y0, 0);
                var p3 = new Vector3(x1, y1, 0);
                var p4 = new Vector3(x0, y1, 0);
                AddTextQuad(p1, p2, p3, p4, vColor, glyph.U0, glyph.U1, glyph.V0, glyph.V1);
                cursorX += glyph.AdvanceX * scale;
            }
        }

        return cursorX;
    }
}
