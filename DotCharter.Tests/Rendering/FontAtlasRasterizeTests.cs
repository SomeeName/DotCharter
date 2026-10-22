using DotCharter.Rendering.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 3D glyph sheet used to be rasterized inside
///     <c>Dispatcher.UIThread.Invoke</c>, but the rebuild runs on the RENDER
///     thread — and the UI thread blocks on that same render thread whenever
///     the 3D view is detached or popped out (<c>Preview3DComposedCanvas</c> →
///     <c>WaitForDrain(1500 ms)</c>). A rebuild in flight at that moment
///     deadlocked the two against each other.
///     <para>
///         These run the rasterizer on a plain background thread with no
///         Avalonia dispatcher anywhere: if the UI-thread dependency ever comes
///         back, this hangs (and the timeout fails it) instead of shipping a
///         freeze that only reproduces on pop-out.
///     </para>
/// </summary>
public sealed class FontAtlasRasterizeTests
{
    private static readonly char[] Ascii = Enumerable.Range(' ', '~' - ' ' + 1).Select(c => (char)c).ToArray();

    [Fact]
    public async Task Rasterize_CompletesOffTheUiThread()
    {
        var work = Task.Run(() => FontAtlas.Rasterize(Ascii, "Arial", 32f));

        var finished = await Task.WhenAny(work,
            Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

        Assert.True(ReferenceEquals(finished, work),
            "FontAtlas.Rasterize did not complete off the UI thread — the dispatcher dependency is back.");
        var (pixels, glyphs, _, _) = await work;
        Assert.Equal(FontAtlas.AtlasWidth * FontAtlas.AtlasHeight * 4, pixels.Length);
        Assert.NotEmpty(glyphs);
    }

    [Fact]
    public void Rasterize_ProducesInkAndSaneMetrics()
    {
        var (pixels, glyphs, _, _) = FontAtlas.Rasterize(Ascii, "Arial", 32f);

        // Every printable ASCII char gets a cell on a 2048² sheet at 32 px.
        foreach (var c in Ascii) Assert.True(glyphs.ContainsKey(c), $"no glyph packed for '{c}'");

        var m = glyphs['W'];
        Assert.True(m.Width > 0, "'W' has no width");
        Assert.True(m.Height > 0, "'W' has no height");
        Assert.True(m.AdvanceX > 0, "'W' has no advance");
        // UVs stay inside the sheet, so sampling can't read neighbouring rows.
        Assert.InRange(m.U0, 0f, 1f);
        Assert.InRange(m.V0, 0f, 1f);
        Assert.InRange(m.U1, 0f, 1f);
        Assert.InRange(m.V1, 0f, 1f);
        Assert.True(m.U1 > m.U0 && m.V1 > m.V0, "degenerate UV rect");

        // Glyphs are white-on-transparent: some pixel must carry alpha, or the
        // sheet is blank and every 3D label would render invisible.
        var anyInk = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                anyInk = true;
                break;
            }
        }

        Assert.True(anyInk, "rasterized atlas is completely transparent");
    }

    [Fact]
    public void Rasterize_IsDeterministic()
    {
        var (_, a, _, _) = FontAtlas.Rasterize(Ascii, "Arial", 24f);
        var (_, b, _, _) = FontAtlas.Rasterize(Ascii, "Arial", 24f);

        Assert.Equal(a.Count, b.Count);
        foreach (var (c, m) in a)
        {
            Assert.Equal(m.U0, b[c].U0);
            Assert.Equal(m.AdvanceX, b[c].AdvanceX);
        }
    }
}
