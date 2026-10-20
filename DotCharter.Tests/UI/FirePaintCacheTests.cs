using Avalonia.Media;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.Converters;

namespace DotCharter.Tests.UI;

/// <summary>
///     Static perf audit 2026-08-29 P1 #18–#22 — the per-paint and per-binding
///     allocations in the Fire meters, the knob and two converters.
///     <para>
///         <see cref="FirePaintCache" /> is the pure half (colour by token, brush
///         and pen by colour, cleared on attach / theme change); the source lints
///         pin that the three controls draw THROUGH it (via
///         <see cref="FireDrawnControl" />) and that the base releases the
///         theme subscription on detach; the converter tests pin that the
///         section-accuracy brush is resolved once per key and the tuning text
///         once per (offsets, letters) per arrangement. The controls themselves
///         need a visual tree; <c>Perf/FireMeterRenderAllocationUiPerfSmokeTests</c>
///         logs the per-paint bytes.
///     </para>
/// </summary>
public sealed class FirePaintCacheTests
{
    [Fact]
    public void TheSameColour_IsTheSameBrushAndPen_UntilCleared()
    {
        var cache = new FirePaintCache();
        var red = Color.FromArgb(200, 255, 0, 0);

        var brush = cache.Brush(red);
        Assert.Same(brush, cache.Brush(Color.FromArgb(200, 255, 0, 0)));
        Assert.NotSame(brush, cache.Brush(Color.FromArgb(100, 255, 0, 0)));

        var pen = cache.Pen(red, 1.5, PenLineCap.Round);
        Assert.Same(pen, cache.Pen(red, 1.5, PenLineCap.Round));
        Assert.NotSame(pen, cache.Pen(red, 1.5));
        Assert.NotSame(pen, cache.Pen(red, 2.0, PenLineCap.Round));

        cache.Clear();
        Assert.NotSame(brush, cache.Brush(red));
    }

    [Fact]
    public void AHit_AllocatesNothing()
    {
        var cache = new FirePaintCache();
        var amber = Color.FromRgb(240, 165, 74);
        cache.Brush(amber);
        cache.Pen(amber, 1);

        DotCharter.Tests.Infrastructure.AllocationProbe.AssertAllocationFree("FirePaintCache brush + pen hits", () =>
        {
            cache.Brush(amber);
            cache.Pen(amber, 1);
        });
    }

    [Theory]
    [InlineData("FireTunerMeter.cs")]
    [InlineData("FireLevelMeter.cs")]
    [InlineData("FireKnob.cs")]
    public void TheControl_DrawsThroughTheCache_AndFollowsTheThemeVariant(string file)
    {
        var src = SourceText.StripCommentLines(UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Controls", "Fire", file)));

        // The cache, its clears and the theme subscription live in the base.
        Assert.Contains(" : FireDrawnControl", src);
        Assert.Contains(": base(followsThemeVariant: true)", src);
        Assert.Contains("Paint.", src);
        // No per-paint resolves or allocations are left in the file.
        Assert.DoesNotContain("FireUnitState.ColorFrom(this", src);
        Assert.DoesNotContain("new SolidColorBrush(", src);
        Assert.DoesNotContain("new Pen(", src);
    }

    [Fact]
    public void TheBase_ClearsTheCache_AndReleasesItsThemeSubscription()
    {
        var src = SourceText.StripCommentLines(UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Controls", "Fire", "FireDrawnControl.cs")));

        // Cleared on attach (a different host resolves differently) and on a
        // theme change; the subscription is released on detach.
        var attach = Body(src, "protected override void OnAttachedToVisualTree(");
        Assert.Contains("_paint?.Clear();", attach);
        Assert.Contains("ActualThemeVariantChanged += OnThemeVariantChanged;", attach);
        var detach = Body(src, "protected override void OnDetachedFromVisualTree(");
        Assert.Contains("ActualThemeVariantChanged -= OnThemeVariantChanged;", detach);
        var themed = Body(src, "private void OnThemeVariantChanged(");
        Assert.Contains("_paint?.Clear();", themed);
        Assert.Contains("Repaint();", themed);
    }

    [Fact]
    public void TheKnob_KeepsTheUnlitArcGeometry_AndBuildsOnlyTheLitOnePerFrame()
    {
        var src = SourceText.StripCommentLines(UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Controls", "Fire", "FireKnob.cs")));
        var render = Body(src, "public override void Render(DrawingContext context)");
        Assert.Contains("FullArc(center, radius)", render);
        var fullArc = Body(src, "private StreamGeometry FullArc(");
        Assert.Contains("_fullArcKey == (center, radius)", fullArc);
    }

    // ── the two converters ────────────────────────────────────────────────────

    [Fact]
    public void TheSectionAccuracyBrush_IsResolvedOncePerKey_NotPerBinding()
    {
        var src = SourceText.StripCommentLines(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Converters", "SectionAccuracyBrushConverter.cs"))));
        Assert.DoesNotContain("DynamicResourceExtension", src);
        var resolve = Body(src, "private static IBrush Resolve(string key)");
        Assert.Contains("Cache.TryGetValue(key, out var cached)", resolve);
        Assert.Contains("app.TryFindResource(key, app.ActualThemeVariant, out var value)", resolve);
        Assert.Contains("ActualThemeVariantChanged += (_, _) => Cache.Clear();", resolve);
        // No static ChartTheme subscription: the brushes follow the palette by
        // themselves (DynamicResource colours), and a static hook would root the
        // cache for the process (ViewSubscriptionLeakGuardTests).
        Assert.DoesNotContain("ChartTheme.ThemeChanged", src);
        // The three keys are unchanged.
        Assert.Equal("SuccessColor", SectionAccuracyBrushConverter.ResourceKeyFor(0.95));
        Assert.Equal("WarningColor", SectionAccuracyBrushConverter.ResourceKeyFor(0.8));
        Assert.Equal("DangerColor", SectionAccuracyBrushConverter.ResourceKeyFor(0.1));
    }

    [Fact]
    public void TheTuningText_IsMemoizedPerArrangement_AndFollowsAChange()
    {
        var arrangement = new Arrangement { Name = "Lead", Tuning = ["E", "A", "D", "G", "B", "e"] };
        foreach (var step in new[] { 0, 0, 0, 0, 0, 0 }) arrangement.TuningPitches.Add(new TuningOffset(step));

        var first = ArrangementTuningConverter.TextFor(arrangement);
        Assert.Same(first, ArrangementTuningConverter.TextFor(arrangement));
        Assert.Equal(first, (string)ArrangementTuningConverter.Instance.Convert(arrangement, typeof(string), null,
            System.Globalization.CultureInfo.InvariantCulture)!);

        // A repeat call on an unchanged arrangement allocates nothing.
        DotCharter.Tests.Infrastructure.AllocationProbe.AssertAllocationFree("ArrangementTuningConverter memo hit",
            () => ArrangementTuningConverter.TextFor(arrangement));

        // Drop D: the memo notices the offset change without a new instance.
        arrangement.TuningPitches[0].Step = -2;
        var dropped = ArrangementTuningConverter.TextFor(arrangement);
        Assert.NotEqual(first, dropped);
        Assert.Same(dropped, ArrangementTuningConverter.TextFor(arrangement));

        // …and a letters-only change too.
        arrangement.Tuning = ["D", "A", "D", "G", "B", "e"];
        arrangement.TuningPitches[0].Step = 0;
        var relettered = ArrangementTuningConverter.TextFor(arrangement);
        Assert.NotSame(dropped, relettered);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' has moved or been renamed — repoint this lint.");
        var open = source.IndexOf('{', start + signature.Length);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }
}
