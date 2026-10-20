using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     Japanese lyrics in the 3D highway rendered as tofu — every kana
///     and kanji the same empty <c>.notdef</c> box, because the 3D glyph sheet
///     drew each character with its one primary typeface and no fallback.
///     <para>
///         The frame-level proof needs no reference picture of tofu: render the
///         HUD lyric line with two DIFFERENT strings of the same length and
///         script. Tofu boxes are identical for every character, so the two
///         frames differ only by sampling noise (the boxes sit at different
///         UVs: 91–154 px on lavapipe with the fallback forced off), while real
///         glyphs differ by 850–1,030 px; 400 sits between. Each string
///         must also differ from the frame with no lyric at all, or nothing
///         rendered.
///     </para>
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessCjkLyricTests
{
    private const int Width = 1280;
    private const int Height = 720;

    private static FrameSnapshot BuildWithLyric(string? lyric)
    {
        var s = new FrameSnapshot
        {
            StringCount = 6, HighwayScrollSpeed = 1.3f,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 20000, Fret = 1, Width = 4 }], AnchorCount = 1
        };

        if (lyric is null) return s;

        s.LyricWords = [new RenderLyricWord { Time = 0, EndTime = 4000, Text = lyric }];
        s.LyricWordCount = 1;
        s.LyricPhrases = [new RenderLyricPhrase { StartTime = 0, EndTime = 4000, WordOffset = 0, WordCount = 1 }];
        s.LyricPhraseCount = 1;
        return s;
    }

    [Theory]
    [InlineData("ja", "こんにちは世界", "さようなら歌詞")]
    [InlineData("ko", "안녕하세요", "감사합니다")]
    [InlineData("zh", "你好世界", "谢谢朋友")]
    public void CjkLyricsRenderAsGlyphsNotIdenticalMissingGlyphBoxes(string script, string a, string b)
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 (or =sw for lavapipe) to run.");

        using var r = new HeadlessHighwayRenderer();

        var none = r.Render(BuildWithLyric(null), Width, Height, timeMs: 500);
        var first = r.Render(BuildWithLyric(a), Width, Height, timeMs: 500);
        var second = r.Render(BuildWithLyric(b), Width, Height, timeMs: 500);
        GoldenFrame.Dump(first, Width, Height, $"vk_cjk_lyric_{script}_a");
        GoldenFrame.Dump(second, Width, Height, $"vk_cjk_lyric_{script}_b");

        var drawn = GoldenFrame.DifferenceBounds(none, first, Width, Height);
        Assert.True(drawn is { Pixels: > 400 },
            $"The {script} lyric '{a}' changed {drawn?.Pixels ?? 0} pixels — the line did not render.");

        var distinct = GoldenFrame.DifferenceBounds(first, second, Width, Height);
        Assert.True(distinct is { Pixels: > 400 },
            $"'{a}' and '{b}' rendered {(distinct is null ? "IDENTICALLY" : $"only {distinct.Value.Pixels} px apart")} — " +
            "every character is the same missing-glyph box (tofu).");

        // The difference is the lyric line, in the HUD's upper band — not some
        // other part of the frame that happened to change.
        var (_, y0, _, y1, _) = distinct!.Value;
        Assert.True(y0 > Height * 0.1 && y1 < Height * 0.3,
            $"The {script} difference spans y {y0}..{y1} of {Height}, outside the lyric band.");
    }
}
