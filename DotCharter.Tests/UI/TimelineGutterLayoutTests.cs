using System.Text.RegularExpressions;
using DotCharter.UI.Controls;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Editing.Tracks;

namespace DotCharter.Tests.UI;

/// <summary>
///     The 2D timeline's vertical layout, now that TWO controls draw against it:
///     <c>ChartRenderer</c> (the chart) and <c>TimelineGutterControl</c> (the
///     captions naming its lanes, immediately to its left).
///     <para>
///         A caption one pixel off its lane is not a cosmetic defect — it names
///         the wrong string. So the formula lives once in
///         <see cref="TimelineGutterLayout" /> and these are the assertions that
///         keep the renderer's own <c>LaneTopFor</c> agreeing with it.
///     </para>
///     <para>
///         Pure and platform-free: <see cref="TimelineGutterLayout" /> and
///         <see cref="RibbonLayout" /> are structs and
///         <see cref="ChartRenderContext" /> is a plain object, so this runs in
///         the default gate.
///     </para>
/// </summary>
public sealed class TimelineGutterLayoutTests
{
    private const double BottomMargin = 44;

    /// <summary>
    ///     <b>The gutter and the renderer put a string in the same place.</b>
    ///     Swept over every ribbon-visibility combination, both flips, four
    ///     string counts and three heights — the reflow cases are exactly where
    ///     two copies of this arithmetic would drift, because the ribbon's height
    ///     changes with how many rows are showing.
    /// </summary>
    [Fact]
    public void EveryLane_LandsWhereTheRendererDrawsIt()
    {
        var compared = 0;

        foreach (var ribbon in AllRibbons())
        {
            foreach (var flip in new[] { false, true })
            {
                foreach (var count in new[] { 4, 5, 6, 7 })
                {
                    foreach (var height in new double[] { 420, 733.5, 1080 })
                    {
                        var layout = TimelineGutterLayout.For(height, ribbon, count, flip, BottomMargin);

                        // The renderer's own mapping, fed the layout's own answers — which
                        // is what ChartRenderer.RenderTimelineFrame does.
                        var rc = new ChartRenderContext
                        {
                            TopMargin = layout.TopMargin,
                            LaneHeight = layout.LaneHeight,
                            StringCount = layout.StringCount,
                            // The context stores the two flags separately and XORs them;
                            // the layout stores the XOR. Feeding the combined value in as
                            // LeftyFlip with InvertStrings false is the same predicate.
                            LeftyFlip = flip,
                            InvertStrings = false
                        };

                        for (var s = 0; s < count; s++)
                        {
                            Assert.Equal(rc.LaneTopFor(s), layout.LaneTopFor(s), 5);
                            compared++;
                        }
                    }
                }
            }
        }

        // Anti-vacuity: 32 ribbons × 2 densities × 2 flips × (4+5+6+7) strings × 3 heights.
        Assert.Equal(32 * 2 * 2 * 22 * 3, compared);
    }

    // ── the ribbon's vertical budget (0.51) ─────────────────────────────────

    /// <summary>
    ///     <b>Every ribbon bar FILLS its lane.</b>
    ///     <para>
    ///         The owner's report, twice: the app's ribbon rows "cost more
    ///         height than they use" beside the design's. They were 30 px lanes
    ///         holding a 20 px bar — 5 px of dead space above and below every
    ///         section, phrase, tone and lyric on the surface, five lanes deep.
    ///         The reference (<c>Chart Timeline.reference.png</c>, measured) is a
    ///         19 px bar in a 24 px lane.
    ///     </para>
    ///     <para>
    ///         Asserted as a RATIO and a per-side padding rather than as the two
    ///         literals, because what regresses is not the numbers — it is
    ///         somebody growing one of them for breathing room and reopening
    ///         exactly this report.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryRibbonBar_FillsItsLane()
    {
        var pad = (RibbonLayout.RowHeight - RibbonLayout.BarHeight) * 0.5f;

        // Centred: the same padding above the bar as below it.
        Assert.Equal(pad, RibbonLayout.BarTopIn(0f), 5);
        Assert.Equal(RibbonLayout.RowHeight - pad, RibbonLayout.BarBottomIn(0f), 5);
        Assert.True(pad is > 0f and <= 3f,
            $"a ribbon bar has {pad} px of dead space per side; the reference draws 2.5");
        Assert.True(RibbonLayout.BarHeight / RibbonLayout.RowHeight >= 0.75f,
            $"a ribbon bar fills only {RibbonLayout.BarHeight / RibbonLayout.RowHeight:P0} of its lane " +
            "— the reference fills 79%");

        // …and the same rule on the measure band, which was the worst of the
        // five: a 16 px pill drawn one pixel ABOVE a 22 px band, i.e. seven
        // empty pixels under it.
        var pillPad = (RibbonLayout.MetadataBandHeight - RibbonLayout.PillHeight) * 0.5f;
        Assert.Equal(RibbonLayout.PillTopIn(0f), pillPad, 5);
        Assert.True(pillPad is >= 0f and <= 3f,
            $"the measure pill has {pillPad} px of dead space per side");
    }

    /// <summary>
    ///     <b>Nothing a lane draws leaves that lane.</b> (The active section chip
    ///     also wore a 2 px underline below its bar until 2026-09-24; the owner
    ///     read it as a stray purple line, so the chip's brighter fill is the
    ///     only mark now. <see cref="TheActiveSection_WearsNoUnderline" />.)
    /// </summary>
    [Fact]
    public void TheBar_StaysInsideItsOwnLane()
    {
        const float rowY = 100f;
        Assert.True(RibbonLayout.BarTopIn(rowY) >= rowY, "the bar starts above its own lane");
        Assert.True(RibbonLayout.BarBottomIn(rowY) <= rowY + RibbonLayout.RowHeight,
            "the bar ends below its own lane");
    }

    [Fact]
    public void TheActiveSection_WearsNoUnderline()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "DotCharter.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var src = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(dir!, "DotCharter.UI", "Controls", "Layers", "EventLayer.cs")));
        Assert.DoesNotContain("DrawActiveUnderline", src);
        Assert.DoesNotContain("BarBottomIn(rowY) + 0.5f", src);
    }

    /// <summary>
    ///     <b>The whole ribbon fits the budget the reference sets.</b> Four
    ///     lanes (sections / phrases / lyrics / measures — the mockup has TONES
    ///     switched off) occupy 96 px there, plus the one-pixel rule under them.
    ///     Ours came to 132 for the same content.
    ///     <para>
    ///         The cap is what makes this a guard rather than a restatement of
    ///         the constants: every one of the five numbers folded into
    ///         <c>TotalHeight</c> — the pitch, the top inset, the gap before the
    ///         band, the band, the separator — can regrow independently, and
    ///         only their sum is what the owner sees.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheRibbon_CostsNoMoreHeightThanTheDesignItWasMeasuredAgainst()
    {
        // The reference's own row set: tones off, the other four on.
        var asDesigned = new RibbonLayout(RibbonLanes.All & ~RibbonLanes.Tones);
        Assert.True(asDesigned.TotalHeight <= 100f,
            $"the ribbon spends {asDesigned.TotalHeight} px on the reference's four lanes, which it " +
            "draws in 96 + a 1 px rule");

        // With every row on it is five lanes, so one pitch more and no more.
        var everything = RibbonLayout.Default;
        Assert.Equal(asDesigned.TotalHeight + RibbonLayout.RowHeight, everything.TotalHeight, 5);

        // Hiding a row really gives its height back to the chart — the
        // auto-collapse behaviour the reflow exists for.
        var noLyrics = new RibbonLayout(RibbonLanes.All & ~RibbonLanes.Lyrics);
        Assert.Equal(everything.TotalHeight - RibbonLayout.RowHeight, noLyrics.TotalHeight, 5);
    }

    /// <summary>
    ///     <b>A caption band is exactly the lane it names.</b>
    ///     <c>TimelineGutterControl</c> draws a caption centred in
    ///     <c>RibbonLayout.RowHeight</c> at each row's own Y and a seam at its
    ///     floor, while <c>EventLayer</c> draws the bar at
    ///     <see cref="RibbonLayout.BarTopIn" /> of the same Y. So the rows tile
    ///     with no gap and no overlap, and the LAST one's floor is where the
    ///     measure band begins.
    /// </summary>
    [Fact]
    public void TheCaptionBands_TileTheRibbonExactly()
    {
        var r = RibbonLayout.Default;
        Assert.Equal(RibbonLayout.TopPadding, r.ToneY, 5);
        Assert.Equal(r.ToneY + RibbonLayout.RowHeight, r.SectionY, 5);
        Assert.Equal(r.SectionY + RibbonLayout.RowHeight, r.PhraseY, 5);
        Assert.Equal(r.PhraseY + RibbonLayout.RowHeight, r.LyricY, 5);
        Assert.Equal(r.LyricY + RibbonLayout.RowHeight, r.MetadataY, 5);
        Assert.Equal(r.MetadataY + RibbonLayout.MetadataBandHeight + RibbonLayout.SeparatorHeight,
            r.TotalHeight, 5);

        // Every row's hit band is the same band the gutter captions, so a click
        // on a chip and the caption naming it agree about which row that is.
        foreach (var (y, row) in new (float, EventRow)[]
                 {
                     (r.ToneY, EventRow.Tone), (r.SectionY, EventRow.Section),
                     (r.PhraseY, EventRow.Phrase), (r.LyricY, EventRow.Lyric)
                 })
        {
            Assert.Equal(row, r.HitTestRow(RibbonLayout.BarTopIn(y)));
            Assert.Equal(row, r.HitTestRow(RibbonLayout.BarBottomIn(y) - 0.01));
        }

        Assert.Equal(EventRow.None, r.HitTestRow(r.MetadataY));
    }

    /// <summary>
    ///     The two flags really do CANCEL. A left-handed player with the tab
    ///     string layout gets the default order back, and a gutter that took
    ///     either flag alone would print the strings upside down for exactly
    ///     that user.
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void TheTwoVerticalFlips_Xor(bool lefty, bool inverted, bool expectFlipped)
    {
        var layout = TimelineGutterLayout.For(
            600, RibbonLayout.Default, 6, lefty ^ inverted, BottomMargin);

        Assert.Equal(expectFlipped, layout.FlipStrings);
        // String 0 is the LOW string and sits at the top unflipped.
        var lowIsAtTop = layout.LaneTopFor(0) < layout.LaneTopFor(5);
        Assert.Equal(!expectFlipped, lowIsAtTop);
    }

    /// <summary>
    ///     With no labels — the stacked / preview case — the chart fills the
    ///     whole control: no ribbon above it and no ruler below it. A gutter
    ///     drawn against a labelled layout there would caption empty space.
    /// </summary>
    [Fact]
    public void WithoutLabels_TheChartFillsTheWholeControl()
    {
        var layout = TimelineGutterLayout.For(300, RibbonLayout.Default, 6, false, BottomMargin, drawLabels: false);

        Assert.Equal(0f, layout.TopMargin);
        Assert.Equal(0f, layout.BottomMargin);
        Assert.Equal(300f, layout.LaneAreaHeight);
        Assert.Equal(50f, layout.LaneHeight);
    }

    /// <summary>
    ///     <b>The repaint gate answers "would a caption move?", not "did
    ///     anything change?".</b> The renderer publishes a layout every frame,
    ///     so a gate that compared everything would repaint the gutter at the
    ///     compositor's rate forever — the exact "InvalidateVisual only on real
    ///     state change" rule every custom-drawn control here follows.
    /// </summary>
    [Fact]
    public void TheRepaintGate_IgnoresIdenticalFramesAndCatchesEveryReflow()
    {
        var a = TimelineGutterLayout.For(600, RibbonLayout.Default, 6, false, BottomMargin);
        var b = TimelineGutterLayout.For(600, RibbonLayout.Default, 6, false, BottomMargin);
        Assert.True(a.SameShapeAs(b), "two identical frames must not repaint the gutter");

        Assert.False(a.SameShapeAs(TimelineGutterLayout.For(601, RibbonLayout.Default, 6, false, BottomMargin)),
            "a height change moves every lane");
        Assert.False(a.SameShapeAs(TimelineGutterLayout.For(600, RibbonLayout.Default, 7, false, BottomMargin)),
            "a string count change adds a row");
        Assert.False(a.SameShapeAs(TimelineGutterLayout.For(600, RibbonLayout.Default, 6, true, BottomMargin)),
            "a flip reverses the captions");

        // Every ribbon row, one at a time — a gate that missed one would leave
        // that caption behind on a lane that is no longer there.
        for (var bit = 0; bit < 5; bit++)
        {
            var changed = RibbonFor(0b11111 ^ (1 << bit));
            Assert.False(a.SameShapeAs(TimelineGutterLayout.For(600, changed, 6, false, BottomMargin)),
                $"hiding ribbon row {bit} did not register as a reflow");
        }

        // Compact rows move every caption (#81).
        Assert.False(a.SameShapeAs(TimelineGutterLayout.For(600,
                new RibbonLayout(RibbonLanes.All, RibbonDensity.Compact), 6, false, BottomMargin)),
            "switching to compact rows did not register as a reflow");
    }

    /// <summary>
    ///     <b>The renderer really uses the shared helper.</b> Everything above
    ///     proves the two agree given the same inputs; this proves
    ///     <c>ChartRenderer</c> asks for them rather than keeping the copy it
    ///     had. A source lint because the frame builder is private and needs a
    ///     live GPU compositor to run at all.
    /// </summary>
    [Fact]
    public void TheRenderer_BuildsItsFrameFromTheSharedLayout()
    {
        var src = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "ChartRenderer.cs")));

        Assert.Contains("TimelineGutterLayout.For(", src, StringComparison.Ordinal);
        Assert.Contains("PublishGutterLayout(", src, StringComparison.Ordinal);

        // …and it did not keep the old inline arithmetic beside it.
        Assert.DoesNotMatch(new Regex(@"var\s+laneHeight\s*=\s*laneAreaHeight\s*/"), src);
        Assert.DoesNotMatch(new Regex(@"var\s+topMargin\s*=\s*DrawLabels\s*\?"), src);
    }

    /// <summary>
    ///     <b>The string names come from the ARRANGEMENT, and they are not
    ///     constant.</b>
    ///     <para>
    ///         The owner's reference screenshot is a chart in Eb-with-drop-Db,
    ///         where the gutter reads Eb Bb F# C# G# Eb — not one of those is the
    ///         standard name for that string. A gutter that printed "E A D G B e"
    ///         would look completely convincing and be wrong on every drop-tuned
    ///         chart in the library. The names come from
    ///         <c>TuningCheckTargets</c>, the same table the tuner and Get Ready
    ///         judge against, so the gutter cannot claim a note the tuner
    ///         disagrees with.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheStringNames_FollowTheArrangementsTuning()
    {
        var standard = new Arrangement { ArrangementType = ArrangementType.Lead };
        Assert.Equal(
            new[] { "E", "A", "D", "G", "B", "E" },
            TimelineGutterControl.StringNamesFor(standard, 6));

        // The reference's tuning: every string down a semitone, low string down
        // a further tone (Eb standard with drop Db → Db Ab Eb Ab C F on paper;
        // what matters here is that NOT ONE name matches the standard set).
        var dropped = new Arrangement { ArrangementType = ArrangementType.Lead };
        foreach (var step in new[] { -3, -1, -1, -1, -1, -1 })
            dropped.TuningPitches.Add(new TuningOffset(step));

        var names = TimelineGutterControl.StringNamesFor(dropped, 6);
        Assert.Equal(6, names.Length);
        Assert.All(names, n => Assert.DoesNotContain(TimelineGutterControl.Unknown, n, StringComparison.Ordinal));
        Assert.NotEqual(TimelineGutterControl.StringNamesFor(standard, 6), names);

        // The OCTAVE is dropped — this column is ~150 px and "Eb2" says nothing
        // "Eb" does not — but the letter and any accidental are kept.
        Assert.All(names, n => Assert.DoesNotContain(n, char.IsDigit));
        Assert.All(names, n => Assert.InRange(n.Length, 1, 2));
    }

    /// <summary>
    ///     With no arrangement the lanes say they do not know, rather than
    ///     printing a plausible standard tuning. A confident wrong label on the
    ///     one control whose whole job is naming the lanes is worse than no
    ///     label at all.
    /// </summary>
    [Fact]
    public void WithNoArrangement_TheLanesSayTheyDoNotKnow()
    {
        var names = TimelineGutterControl.StringNamesFor(null, 6);
        Assert.Equal(6, names.Length);
        Assert.All(names, n => Assert.Equal(TimelineGutterControl.Unknown, n));
    }

    // ---- helpers -----------------------------------------------------------

    private static IEnumerable<RibbonLayout> AllRibbons()
    {
        // Every lane combination, at both densities (#81): compact rows must tile too.
        foreach (var density in new[] { RibbonDensity.Normal, RibbonDensity.Compact })
        {
            for (var mask = 0; mask < 32; mask++) yield return RibbonFor(mask, density);
        }
    }

    private static RibbonLayout RibbonFor(int mask, RibbonDensity density = RibbonDensity.Normal) =>
        new((RibbonLanes)mask, density);
}
