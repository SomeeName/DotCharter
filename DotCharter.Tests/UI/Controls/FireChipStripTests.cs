using System.Text.RegularExpressions;
using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The drawn arrangement-chip strip that replaced the per-row chips
///     <c>ItemsControl</c> in both song lists. What matters here:
///     <list type="number">
///         <item>the pure geometry (width, per-chip rects),</item>
///         <item>look-parity with the `FireTag Square` mark — the geometry
///         constants must EQUAL the token dictionary's values, because the
///         detail surfaces still build Border-based tags and the two must be
///         indistinguishable,</item>
///         <item>the repaint gate every Fire drawn control carries, and</item>
///         <item>that the two row templates actually use the strip (with its
///         combined tooltip + automation name) rather than quietly growing a
///         per-chip <c>ItemsControl</c> back.</item>
///     </list>
/// </summary>
public sealed class FireChipStripTests
{
    // ── geometry ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 22)]
    [InlineData(3, 22 * 3 + 6 * 2)]
    [InlineData(5, 22 * 5 + 6 * 4)]
    public void TheRowWidth_CountsChipsAndTheGapsBetweenThem(int count, double expected)
        => Assert.Equal(expected,
            FireChipStripGeometry.RowWidth(count,
                FireChipStripGeometry.DefaultChipSize, FireChipStripGeometry.DefaultGap));

    [Fact]
    public void EachChip_IsAFixedSquareSteppedByChipPlusGap()
    {
        var first = FireChipStripGeometry.ChipRect(0, 22, 6);
        var third = FireChipStripGeometry.ChipRect(2, 22, 6);

        Assert.Equal(new Avalonia.Rect(0, 0, 22, 22), first);
        Assert.Equal(new Avalonia.Rect(2 * 28, 0, 22, 22), third);
    }

    // ── token parity ──────────────────────────────────────────────────────────

    /// <summary>The constants mirror `FireTag Square`'s tokens. A token change
    /// that does not move these constants would make the drawn chip visibly
    /// diverge from the Border-built tag the detail surfaces still use.</summary>
    [Fact]
    public void TheGeometryConstants_MatchTheTokenDictionary()
    {
        var ladders = UiSourceTree.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireTokens", "Ladders.axaml"));

        Assert.Equal(FireChipStripGeometry.DefaultChipSize, TokenDouble(ladders, "FireTagSquareSize"));
        Assert.Equal(FireChipStripGeometry.DefaultGap, TokenDouble(ladders, "FireSpaceSm"));
        Assert.Equal(FireChipStripGeometry.FontSize, TokenDouble(ladders, "FireSizeTag"));
        Assert.Equal(FireChipStripGeometry.CornerRadius,
            TokenCornerRadius(ladders, "FireRadiusTag"));
    }

    // ── the repaint gate ──────────────────────────────────────────────────────

    [Fact]
    public void TheStrip_RepaintsOnAChipListChange_AndNotOnAnIdenticalOne()
    {
        var chips = new[] { "L", "R", "B" };
        var strip = new FireChipStrip { Initials = chips };
        var before = strip.RepaintRequests;

        strip.Initials = chips; // same reference — nothing rendered changes
        Assert.Equal(before, strip.RepaintRequests);

        strip.Initials = ["L", "R"];
        Assert.True(strip.RepaintRequests > before, "a new chip list did not repaint the strip");
    }

    [Fact]
    public void TheStrip_RepaintsWhenItsIdentityFlips()
    {
        var strip = new FireChipStrip { Initials = new[] { "L" } };
        var before = strip.RepaintRequests;

        strip.Accent = true;
        Assert.True(strip.RepaintRequests > before, "flipping Accent did not repaint the strip");
    }

    [Fact]
    public void TheStrip_MeasuresToItsGeometry()
    {
        var strip = new FireChipStrip { Initials = new[] { "L", "R", "B", "V" } };

        strip.Measure(Avalonia.Size.Infinity);

        Assert.Equal(FireChipStripGeometry.RowWidth(4, 22, 6), strip.DesiredSize.Width);
        Assert.Equal(22, strip.DesiredSize.Height);
    }

    // ── the wiring, which no unit test can see ────────────────────────────────

    /// <summary>Both row templates draw the strip; neither may quietly grow a
    /// per-chip <c>ItemsControl</c> back (that shape rebuilt its chip children
    /// on every recycled re-bind). The combined tooltip + automation name
    /// replace the per-chip tooltips the ItemsControl carried.</summary>
    [Fact]
    public void BothRowTemplates_DrawTheStrip_WithItsTooltipAndAutomationName()
    {
        // Play Mode binds the song browser's row; the Library binds its contract's row, which is its
        // own view since #180 (the template of the list's containers).
        foreach (var (name, markup, initials, summary) in new[]
                 {
                     ("LibraryRowView", UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
                         UiSourceTree.Resolve(Path.Combine("DotCharter.UI", "Views", "Library", "LibraryRowView.axaml")))), "Paths", "PathsSummary"),
                     // PlayModeView with its lifted screen views inlined (#294): the row is Song Select's.
                     ("PlayModeView", PlayModeMarkup.Text(), "ArrangementInitials", "ArrangementSummary")
                 })
        {
            var strip = markup.IndexOf("<fire:FireChipStrip", StringComparison.Ordinal);
            Assert.True(strip >= 0, $"{name} no longer draws the FireChipStrip");

            var element = markup[strip..markup.IndexOf("/>", strip, StringComparison.Ordinal)];
            Assert.Contains($"Initials=\"{{Binding {initials}}}\"", element, StringComparison.Ordinal);
            Assert.Contains($"ToolTip.Tip=\"{{Binding {summary}}}\"", element, StringComparison.Ordinal);
            Assert.Contains($"AutomationProperties.Name=\"{{Binding {summary}}}\"", element, StringComparison.Ordinal);

            Assert.DoesNotContain("ItemsSource=\"{Binding ArrangementChips}\"", markup, StringComparison.Ordinal);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static double TokenDouble(string markup, string key)
    {
        var match = Regex.Match(markup,
            $"""x:Key="{key}">([0-9.]+)<""");
        Assert.True(match.Success, $"token {key} not found in Ladders.axaml — repoint this parity pin.");
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double TokenCornerRadius(string markup, string key)
        => TokenDouble(markup, key);
}
