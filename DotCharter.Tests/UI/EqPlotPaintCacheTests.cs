using System.Text.RegularExpressions;
using DotCharter.Tone.Audio.Equalizer;

namespace DotCharter.Tests.UI;

/// <summary>
///     Static perf audit 2026-08-29, P0 #1–#4: <c>EqPlotControl</c> repaints at
///     ~15 Hz for as long as the EQ module is attached and used to resolve ~26
///     theme brushes by full visual-tree walk, build 7 interpolated resource
///     keys, shape ~15 <c>FormattedText</c> runs, and allocate three
///     <c>double[160]</c>, ~10 pens and a LINQ'd band list — per frame.
///     Measured 105,054 → 30,277 B/frame
///     (<c>Perf/EqPlotRenderAllocationUiPerfSmokeTests</c>, logged).
///     <para>
///         These are SOURCE lints in the shape of <c>TimelineGutterPaintCacheTests</c>,
///         because the control needs a visual tree and a font manager to exercise
///         behaviourally and <c>FormattedText</c> has no platform in the default
///         gate. The two pure halves it leans on — the span overloads and the
///         non-allocating audible-band copy — are tested behaviourally below.
///     </para>
/// </summary>
public sealed class EqPlotPaintCacheTests
{
    [Fact]
    public void TheThemeBrushes_AreResolvedOncePerTheme_NotPerPaint()
    {
        var brush = Body(Source(), "private IBrush Brush(string key, Color fallback)");
        Assert.Contains("_brushes.TryGetValue(key", brush);
        Assert.Contains("_brushes[key] = brush", brush);

        // The one TryFindResource is inside the cache miss; no draw method walks
        // the tree itself.
        var source = Source();
        Assert.Single(Regex.Matches(SourceText.StripCommentLines(source), @"TryFindResource\("));
    }

    [Fact]
    public void TheBandKeys_AreAStaticArray_NotAnInterpolatedStringPerBand()
    {
        var source = SourceText.StripCommentLines(Source());
        Assert.DoesNotContain("$\"FireEqBand", source);
        Assert.Contains("BandKeys[index % BandKeys.Length]", source);
    }

    [Fact]
    public void TheLabels_AreShapedOncePerThemeAndFont_NotPerPaint()
    {
        var source = SourceText.StripCommentLines(Source());
        // Exactly one FormattedText constructor, and it is inside the cache.
        Assert.Single(Regex.Matches(source, @"new FormattedText\("));
        var text = Body(Source(), "private FormattedText Text(");
        Assert.Contains("_texts.TryGetValue(key", text);
        Assert.Contains("new FormattedText(", text);

        // One Typeface, lazily, dropped when the font family changes.
        Assert.Single(Regex.Matches(source, @"new Typeface\("));
        Assert.Contains("_typeface ??= new Typeface(", source);
        var fontChange = Body(Source(), "protected override void OnPropertyChanged");
        Assert.Contains("TextElement.FontFamilyProperty", fontChange);
        Assert.Contains("_texts.Clear()", fontChange);
    }

    [Fact]
    public void ThePensAndDashes_AreCached_NotBuiltPerPaint()
    {
        var source = SourceText.StripCommentLines(Source());
        // The only `new Pen(` is inside the Pen() cache.
        Assert.Single(Regex.Matches(source, @"new Pen\("));
        var pen = Body(Source(), "private Pen Pen(string brushKey");
        Assert.Contains("_pens.TryGetValue(key", pen);
        Assert.Contains("new Pen(", pen);
        // The three dash patterns are static fields, not per-paint arrays.
        Assert.Equal(3, Regex.Matches(source, @"static readonly DashStyle").Count);
        Assert.DoesNotContain("DashStyle = new DashStyle(", source);
    }

    [Fact]
    public void TheCurveBuffers_AreReused_AndTheGeometriesOnlyRebuildOnAnEdit()
    {
        var source = SourceText.StripCommentLines(Source());
        // The span overload into two fixed buffers, never the array overload.
        Assert.DoesNotContain("EqCurve.Response(bands, CurveSamples)", source);
        Assert.Contains("EqCurve.Response(bands, response)", source);
        Assert.Contains("new double[CurveSamples]", source);
        Assert.DoesNotContain("EqSpectrum.Levels(Spectrum!)", source);
        Assert.Contains("EqSpectrum.Levels(frame, _levels)", source);
        // The audible bands are copied into a reusable list, not LINQ'd per frame.
        Assert.DoesNotContain("section.AudibleBands", source);
        Assert.Contains("section.CopyAudibleBandsTo(_audible)", source);

        // Both curve geometries are signature-gated.
        var curve = Body(Source(), "private void DrawCurve(");
        Assert.Contains("hash != _curveSignature", curve);
        var band = Body(Source(), "private void DrawSelectedBandContribution(");
        Assert.Contains("signature != _bandSignature", band);
    }

    [Fact]
    public void TheCaches_AreDropped_OnThemeChange_AndTheStaticSubscriptionIsReleased()
    {
        var source = Source();
        Assert.Contains("ChartTheme.ThemeChanged += OnThemeOrPaletteChanged", source);
        Assert.Contains("ChartTheme.ThemeChanged -= OnThemeOrPaletteChanged", source);
        Assert.Contains("ActualThemeVariantChanged += OnThemeOrPaletteChanged", source);
        Assert.Contains("ActualThemeVariantChanged -= OnThemeOrPaletteChanged", source);

        var clear = Body(source, "internal void ClearRenderCaches()");
        Assert.Contains("_brushes.Clear()", clear);
        Assert.Contains("_pens.Clear()", clear);
        Assert.Contains("_texts.Clear()", clear);

        var onTheme = Body(source, "internal void OnThemeOrPaletteChanged(object? sender");
        Assert.Contains("ClearRenderCaches()", onTheme);
        Assert.Contains("InvalidateVisual()", onTheme);
    }

    // ── the pure halves ──────────────────────────────────────────────────────

    [Fact]
    public void CopyAudibleBandsTo_IsTheSamePredicateAsAudibleBands()
    {
        var section = new EqSection();
        section.SetBandEnabled(1, true);
        section.SetBandEnabled(4, true);
        var into = new List<EqBand> { default, default };

        section.CopyAudibleBandsTo(into);
        Assert.Equal(section.AudibleBands, into);
        Assert.Equal(2, into.Count);

        section.ToggleSolo(4);
        section.CopyAudibleBandsTo(into);
        Assert.Equal(section.AudibleBands, into);
        Assert.Single(into);

        section.IsEnabled = false;
        section.CopyAudibleBandsTo(into);
        Assert.Equal(section.AudibleBands, into);
        Assert.Empty(into);
    }

    [Fact]
    public void ResponseIntoSpan_MatchesTheArrayOverload_Exactly()
    {
        var section = new EqSection();
        section.SetBandEnabled(0, true);
        section.SetBand(0, section.Bands[0] with { GainDb = 6.0 });
        section.SetBandEnabled(3, true);
        section.SetBand(3, section.Bands[3] with { GainDb = -4.0 });

        var expected = EqCurve.Response(section.AudibleBands, 160);
        var into = new double[160];
        EqCurve.Response(section.AudibleBands, into);
        Assert.Equal(expected, into);
    }

    [Fact]
    public void LevelsIntoSpan_MatchesTheArrayOverload_Exactly()
    {
        var frame = new float[64];
        for (var i = 0; i < frame.Length; i++) frame[i] = i % 7 == 0 ? float.NaN : -60f + i;

        var expected = EqSpectrum.Levels(frame);
        var into = new double[80];
        EqSpectrum.Levels(frame, into);
        Assert.Equal(expected, into[..frame.Length]);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' has moved or been renamed — repoint this lint.");
        var next = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        foreach (var marker in new[] { "\n    public ", "\n    internal ", "\n    protected ", "\n    // ── " })
        {
            var candidate = source.IndexOf(marker, start + signature.Length, StringComparison.Ordinal);
            if (candidate >= 0 && (next < 0 || candidate < next)) next = candidate;
        }

        return next < 0 ? source[start..] : source[start..next];
    }

    private static string Source() =>
        UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "EqPlotControl.cs")));
}
