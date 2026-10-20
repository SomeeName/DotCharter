using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     #573: the Inspector's tab strip is ALL labelled or ALL icons (the active tab always keeps
///     its caption), and what does not fit even as icons goes into "More". This pins the pure
///     decision, <see cref="TabStripLayout.Decide" />; <c>Perf/InspectorTabStripUiPerfSmokeTests</c>
///     lays the real strip out at 320 / 420 / 560 / 720 / 1100 px.
///     <para>
///         The fixture is nine tabs shaped like the real ones in English: 28 px glyph segments, the
///         Issues tab 64 px for its three-digit badge, captions 32-84 px plus the 4 px between glyph
///         and caption, 2 px between segments and a 56 px More button. So all nine labelled need
///         802 + 16 = 818 px, all nine as icons with the widest caption reserved for the active
///         one 288 + 88 + 16 = 392 px.
///     </para>
/// </summary>
public sealed class InspectorTabStripLayoutTests
{
    private const double Spacing = 2;
    private const double More = 56;
    private const double CaptionGap = 4;
    private const double AllLabelled = 818;
    private const double AllIcons = 392;

    private static readonly double[] Captions = [40, 38, 32, 40, 42, 78, 52, 72, 84];

    private static readonly TabStripTab[] Tabs =
        [.. Captions.Select((caption, i) => Tab(i == 4 ? 64 : 28, caption))];

    private static TabStripTab Tab(double icon, double caption) => new(icon, icon + CaptionGap + caption);

    private static TabStripPlan Decide(double available, int active) =>
        TabStripLayout.Decide(available, Spacing, Tabs, active, More);

    [Theory]
    [InlineData(AllLabelled)]
    [InlineData(AllLabelled + 0.4)]
    [InlineData(1100)]
    public void EveryCaptionFits_EveryTabIsLabelled(double available)
    {
        var plan = Decide(available, 3);
        Assert.Equal(TabStripMode.Labelled, plan.Mode);
        Assert.Equal(Enumerable.Range(0, 9), plan.Shown);
        Assert.Empty(plan.Overflow);
    }

    [Theory]
    [InlineData(AllLabelled - 1)]
    [InlineData(720)]
    [InlineData(560)]
    [InlineData(420)]
    [InlineData(AllIcons)]
    public void OneCaptionShort_EveryTabIsAnIcon_AndNothingOverflowsWhileTheIconsFit(double available)
    {
        var plan = Decide(available, 3);
        Assert.Equal(TabStripMode.IconsOnly, plan.Mode);
        Assert.Equal(Enumerable.Range(0, 9), plan.Shown);
        Assert.Empty(plan.Overflow);
    }

    [Fact]
    public void BelowTheIcons_TheEndOfTheStripOverflows_InTabOrder()
    {
        // 313 px is what the dock's 320 px floor leaves the strip: 255 px beside More fits the
        // reserved caption, the Issues segment and three glyphs.
        var plan = Decide(313, 0);
        Assert.Equal(TabStripMode.IconsOnly, plan.Mode);
        Assert.Equal([0, 1, 2, 3], plan.Shown);
        Assert.Equal([4, 5, 6, 7, 8], plan.Overflow);

        // One pixel short of the icons: More (56 + 2) takes the room of two glyphs.
        Assert.Equal(7, Decide(AllIcons - 1, 0).Shown.Count);
    }

    [Fact]
    public void AnOverflowedActiveTab_TakesTheLastSlot_AndTheOthersStayInOrder()
    {
        var plan = Decide(313, 7);
        Assert.Equal([0, 1, 2, 7], plan.Shown);
        Assert.Equal([3, 4, 5, 6, 8], plan.Overflow);
    }

    /// <summary>Whatever is selected — the More menu's rows included — and at whatever width, the
    /// active tab stands in the strip, every tab is either shown or in More (never both, never
    /// lost), both lists keep tab order, and what is shown fits.</summary>
    [Fact]
    public void AtEveryWidth_TheActiveTabIsShown_AndEveryTabIsReachable()
    {
        for (var available = 300.0; available <= 1200; available += 1)
        {
            for (var active = -1; active < Tabs.Length; active++)
            {
                var plan = Decide(available, active);
                var at = $"at {available} px with tab {active} active";
                if (active >= 0) Assert.True(plan.Shown.Contains(active), $"{at} the active tab is not in the strip");
                Assert.Equal(Enumerable.Range(0, Tabs.Length), plan.Shown.Concat(plan.Overflow).Order());
                Assert.Equal(plan.Shown.Order(), plan.Shown);
                Assert.Equal(plan.Overflow.Order(), plan.Overflow);

                var used = plan.Shown.Sum(i => plan.Mode == TabStripMode.Labelled || i == active ? Tabs[i].LabelledWidth : Tabs[i].IconWidth)
                           + (plan.HasOverflow ? More + Spacing : 0) + (Spacing * (plan.Shown.Count - 1));
                Assert.True(used <= available + 0.5, $"{at} the strip needs {used} px");
            }
        }
    }

    /// <summary>The owner's complaint was a strip that reshuffles: choosing a tab never changes the
    /// mode or how many tabs fit, only (when the choice was in More) which ones.</summary>
    [Fact]
    public void ChoosingATab_NeverChangesTheModeOrHowManyTabsFit()
    {
        for (var available = 300.0; available <= 1200; available += 1)
        {
            var first = Decide(available, 0);
            for (var active = 1; active < Tabs.Length; active++)
            {
                var plan = Decide(available, active);
                Assert.Equal(first.Mode, plan.Mode);
                Assert.Equal(first.Shown.Count, plan.Shown.Count);
            }
        }
    }

    [Fact]
    public void ALongerLanguage_GoesToIconsAtAWiderPane()
    {
        // German captions run ~40 % longer: the same width that labels English is icons there.
        TabStripTab[] german = [.. Captions.Select((caption, i) => Tab(i == 4 ? 64 : 28, caption * 1.4))];
        Assert.Equal(TabStripMode.Labelled, Decide(900, 0).Mode);
        Assert.Equal(TabStripMode.IconsOnly, TabStripLayout.Decide(900, Spacing, german, 0, More).Mode);
    }

    [Fact]
    public void AnUnconstrainedMeasure_OrNoTabs_IsLabelledAndOverflowsNothing()
    {
        Assert.Equal(TabStripMode.Labelled, Decide(double.PositiveInfinity, 0).Mode);
        Assert.Empty(Decide(double.PositiveInfinity, 0).Overflow);
        Assert.Same(TabStripPlan.Empty, TabStripLayout.Decide(300, Spacing, [], -1, More));
    }

    [Fact]
    public void AStripTooNarrowForAnything_StillShowsTheActiveTab() =>
        Assert.Equal([5], Decide(60, 5).Shown);
}
