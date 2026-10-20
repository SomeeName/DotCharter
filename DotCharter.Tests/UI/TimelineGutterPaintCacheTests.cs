namespace DotCharter.Tests.UI;

/// <summary>
///     P2-13: <c>TimelineGutterControl</c> allocated on every paint — three
///     theme brushes resolved + allocated per <c>Render</c>, a
///     <c>SolidColorBrush</c> per string row, and a <c>string.Create</c> cache
///     KEY per label lookup — and its shaping cache never evicted. Source
///     lints (the control needs a visual tree and a font manager to exercise
///     behaviourally; <c>FormattedText</c> shaping has no platform in the
///     default gate).
/// </summary>
public sealed class TimelineGutterPaintCacheTests
{
    [Fact]
    public void TheStringRows_UseTheThemeLayersOwnBrushArray()
    {
        var source = Source();
        var draw = Body(source, "private void DrawStringRows");
        Assert.Contains("ChartTheme.StringBrushFor(", draw);
        Assert.DoesNotContain("new SolidColorBrush", draw);
    }

    [Fact]
    public void TheThemeBrushes_AreCachedPerControl_NotResolvedPerPaint()
    {
        var render = Body(Source(), "public override void Render");
        Assert.Contains("_dim ??=", render);
        Assert.Contains("_faint ??=", render);
        Assert.Contains("_seam ??=", render);
    }

    [Fact]
    public void TheCaptionCacheKey_IsAStruct_NotAStringPerLookup()
    {
        var cached = Body(Source(), "private FormattedText Cached");
        Assert.Contains("new CaptionKey(", cached);
        Assert.DoesNotContain("var key = string.Create", cached);
    }

    [Fact]
    public void TheCaches_AreDropped_OnThemeAndOnTuningChange()
    {
        var source = Source();
        // Theme/palette: both subscription halves, and the detach unsubscribes
        // (a kept static ChartTheme.ThemeChanged subscription roots the control).
        Assert.Contains("ChartTheme.ThemeChanged += OnThemeOrPaletteChanged", source);
        Assert.Contains("ChartTheme.ThemeChanged -= OnThemeOrPaletteChanged", source);
        Assert.Contains("ActualThemeVariantChanged -= OnThemeOrPaletteChanged", source);

        // Tuning: a new song's string names are the only other source of new
        // keys; OnLayoutChanged drops the old song's.
        var layoutChanged = Body(source, "private void OnLayoutChanged");
        Assert.Contains("ClearRenderCaches()", layoutChanged);
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' has moved or been renamed — repoint this lint.");
        var next = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        var nextPublic = source.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        var nextInternal = source.IndexOf("\n    internal ", start + signature.Length, StringComparison.Ordinal);
        foreach (var candidate in new[] { nextPublic, nextInternal })
        {
            if (candidate >= 0 && (next < 0 || candidate < next))
                next = candidate;
        }

        return next < 0 ? source[start..] : source[start..next];
    }

    private static string Source() =>
        UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "Controls", "TimelineGutterControl.cs")));
}
