using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotCharter.Services.Ai.Chart;
using DotCharter.UI.Views.Shell;
using static DotCharter.Tests.Perf.AiChartReviewHarness;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The AI review card (#613) where the shell puts it — over the timeline's top-right corner, in
///     a host as tall as the timeline row at 1920x1080 (360 px) and at 1280x720 (270 px) — with 3, 12
///     and 40 sections, in the swept culture (the release re-runs this class in de-DE and ja-JP).
///     <para>
///         The owner's report: a fixed box that scrolled with no visible scrollbar, ~1.5 sections in
///         view, the hint paragraph and three big buttons eating the card. So: at least SIX rows in
///         the short window and NINE at the default height (or every row, when there are fewer), a
///         scrollbar that is shown, expanded and hit-testable whenever the rows overflow, and every
///         header and footer element inside the card, the footer on one row, in every language.
///     </para>
///     Captured with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> as <c>ai-review-{size}-{sections}-{culture}.png</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class AiChartReviewSizesUiPerfSmokeTests
{
    public static TheoryData<string, double, int> Cases() => new()
    {
        { "default", DefaultTimelineHeight, 3 },
        { "default", DefaultTimelineHeight, 12 },
        { "default", DefaultTimelineHeight, 40 },
        { "short", ShortTimelineHeight, 3 },
        { "short", ShortTimelineHeight, 12 },
        { "short", ShortTimelineHeight, 40 }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public Task TheCard_OverTheTimeline_ShowsItsRows_AndAVisibleScrollbar(string size, double height, int sections) =>
        HeadlessSessionFixture.Dispatch(async () =>
        {
            var review = NewReview(SweptLocalization);
            await review.Run.RunAsync(AiChartJob.FhpAndFingering);
            Assert.True(review.Vm.IsOpen);
            Fill(review.Vm, sections, SweptLocalization);

            var view = new AiChartReviewView { DataContext = review.Vm };
            var (window, host) = NewTimelineHost(960, height, view);
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                var card = view.FindControl<Border>("AiChartReviewCard")!;
                AssertMeasured(card);
                var visible = WhollyVisibleRows(view);
                NarrowWidthLayout.Report(
                    $"ai-review {size} 960x{height} sections={sections}: card {card.Bounds.Width:0}x{card.Bounds.Height:0}, rows wholly visible {visible}");
                NarrowWidthLayout.Capture(window, $"ai-review-{size}-{sections}");

                // The card hangs inside the timeline it covers, never past its bottom edge.
                Assert.InRange(BoundsIn(card, host).Bottom, 1, host.Bounds.Height);

                var floor = Math.Min(sections, height >= DefaultTimelineHeight ? 9 : 6);
                Assert.True(visible >= floor,
                    $"{sections} sections in a {height} px timeline: only {visible} rows wholly visible, wanted {floor} " +
                    $"(card {card.Bounds.Height:0} px, {HeadlessSessionFixture.SweptCulture})");

                foreach (var name in new[]
                         {
                             "AiChartReviewTitle", "AiChartReviewCounts", "AiChartReviewInfo", "AiChartReviewApply",
                             "AiChartReviewAcceptAll", "AiChartReviewRejectAll", "AiChartReviewDiscard"
                         })
                {
                    AssertInsideCard(view.FindControl<Control>(name)!, card);
                }

                // The footer is ONE row in every shipped language: three verbs and the primary.
                var apply = view.FindControl<Button>("AiChartReviewApply")!;
                foreach (var verb in new[] { "AiChartReviewAcceptAll", "AiChartReviewRejectAll", "AiChartReviewDiscard" })
                {
                    var button = view.FindControl<Button>(verb)!;
                    Assert.True(Math.Abs(BoundsIn(button, card).Center.Y - BoundsIn(apply, card).Center.Y) < 4,
                        $"{verb} wrapped off the footer row ({HeadlessSessionFixture.SweptCulture})");
                }

                AssertTheScrollbar(view, window, overflows: visible < sections);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>With more rows than fit, the list's scrollbar is shown, never auto-hides (a hover-only
    /// bar is the owner's "hidden scrolling"), has a real width and takes the pointer.</summary>
    private static void AssertTheScrollbar(AiChartReviewView view, Window window, bool overflows)
    {
        var scroll = view.FindControl<ScrollViewer>("AiChartReviewScroll")!;
        Assert.False(scroll.AllowAutoHide, "the review list's scrollbar auto-hides");
        if (!overflows)
        {
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1, "every row is visible, yet the list scrolls");
            return;
        }

        var bar = VerticalBar(view);
        Assert.True(bar.IsEffectivelyVisible, "the rows overflow and the scrollbar is not shown");
        Assert.True(bar.IsExpanded, "the scrollbar is collapsed to a hairline until hovered");
        Assert.True(bar.Bounds.Width >= 6 && bar.Bounds.Height > 40, $"the scrollbar is {bar.Bounds.Size}");
        var center = BoundsIn(bar, window).Center;
        var hit = window.InputHitTest(center) as Visual;
        Assert.True(hit is not null && (hit == bar || bar.IsVisualAncestorOf(hit)),
            $"the scrollbar's centre {center} hit {hit?.GetType().Name ?? "nothing"}, not the scrollbar");
    }
}
