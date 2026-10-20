using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.Services.Ai.Chart;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;
using static DotCharter.Tests.Perf.AiChartReviewHarness;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The AI chart pass review card (#506, dense since #613) built for real: a pass on a FAKE model
///     holds its proposals, the card is attached to a window over a timeline-sized host, shown and
///     laid out, and the card, its header and footer, every section row and both of a row's toggles
///     MEASURE (a view whose styles failed still arranges, at zero size). A row click is Go to; the
///     keyboard (↑ / ↓ / A / R / Enter) drives the same decisions through real key presses. With no
///     review the card is hidden and offered to the shell's prewarm; with no view model it shows nothing.
/// </summary>
[Collection("HeadlessUI")]
public sealed class AiChartReviewConstructionUiPerfSmokeTests
{
    [Fact]
    public Task AHeldReview_ShowsTheCard_WithEveryRowAndVerbMeasured() =>
        HeadlessSessionFixture.Dispatch(async () =>
        {
            var review = NewReview();
            var vm = review.Vm;
            var view = new AiChartReviewView { DataContext = vm };
            Assert.Equal([view.FindControl<Control>("AiChartReviewCard")!], view.HiddenSurfaces());

            await review.Run.RunAsync(AiChartJob.FhpAndFingering);
            Assert.True(vm.IsOpen);

            AttachAndLayout(view, DefaultTimelineHeight, window =>
            {
                var card = view.FindControl<Border>("AiChartReviewCard")!;
                Assert.True(card.IsVisible, "the card is hidden with a review held");
                AssertMeasured(card);
                foreach (var name in new[] { "AiChartReviewTitle", "AiChartReviewCounts", "AiChartReviewSubtitle", "AiChartReviewInfo" })
                    AssertInsideCard(view.FindControl<Control>(name)!, card);
                foreach (var name in new[] { "AiChartReviewApply", "AiChartReviewAcceptAll", "AiChartReviewRejectAll", "AiChartReviewDiscard" })
                    AssertInsideCard(view.FindControl<Button>(name)!, card);
                // The explanation is the ⓘ's tooltip now, never a paragraph on the card.
                Assert.Contains(FakeLocalization.English()["AiChartReview_Hint"], vm.HintText, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(view.FindControl<Control>("AiChartReviewInfo")!)));

                var rows = Rows(view);
                Assert.Equal(vm.Rows.Count, rows.Count);
                Assert.Equal(2, rows.Count);
                foreach (var row in rows)
                {
                    AssertMeasured(row);
                    // ONE dense row: the Go to button and the two toggles, nothing else to press.
                    var buttons = row.GetVisualDescendants().OfType<Button>().ToList();
                    Assert.Equal(["AiChartReviewRowGo", "AiChartReviewRowAccept", "AiChartReviewRowReject"],
                        buttons.Select(b => b.Name));
                    Assert.True(row.Bounds.Height <= 30, $"a section row is {row.Bounds.Height} px tall, not one dense line");
                    foreach (var button in buttons)
                    {
                        AssertInsideCard(button, card);
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)),
                            $"{button.Name} has no accessible name");
                        Assert.NotNull(ToolTip.GetTip(button));
                    }
                }

                Assert.Empty(view.HiddenSurfaces());
                NarrowWidthLayout.Capture(window, "ai-chart-review-pending");

                // A decision restyles its row in place: the chosen toggle is lit, nothing else is.
                vm.Rows[0].RejectCommand.Execute(null);
                vm.Rows[1].AcceptCommand.Execute(null);
                window.UpdateLayout();
                Assert.True(vm.ApplyCommand.CanExecute(null));
                AssertDecisionsLit(vm, view);
                NarrowWidthLayout.Capture(window, "ai-chart-review");

                // The toggles TOGGLE: a lit ✓ clicked again puts the section back to deciding.
                vm.Rows[1].AcceptCommand.Execute(null);
                window.UpdateLayout();
                Assert.False(vm.Rows[1].IsAccepted || vm.Rows[1].IsRejected);
                AssertDecisionsLit(vm, view);
            });
        }, CancellationToken.None);

    [Fact]
    public Task ManySections_WithLongNames_ScrollInsideTheCard_AndTrim() =>
        HeadlessSessionFixture.Dispatch(async () =>
        {
            var review = NewReview();
            await review.Run.RunAsync(AiChartJob.FhpAndFingering);
            Fill(review.Vm, 12, FakeLocalization.English());

            var view = new AiChartReviewView { DataContext = review.Vm };
            AttachAndLayout(view, ShortTimelineHeight, window =>
            {
                var card = view.FindControl<Border>("AiChartReviewCard")!;
                AssertMeasured(card);

                // The rows scroll; the header and the footer stay on the card.
                var scroll = view.FindControl<ScrollViewer>("AiChartReviewScroll")!;
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 1,
                    $"12 sections did not scroll in a 270 px timeline: extent {scroll.Extent}, viewport {scroll.Viewport}");
                Assert.True(WhollyVisibleRows(view) >= 6, $"only {WhollyVisibleRows(view)} of 12 rows wholly visible");
                AssertInsideCard(view.FindControl<Button>("AiChartReviewApply")!, card);

                // The long name trims before its time column instead of pushing the toggles off the card.
                var rows = Rows(view);
                var longRow = rows[1];
                var name = Named<TextBlock>(longRow, "AiChartReviewRowName");
                var span = Named<TextBlock>(longRow, "AiChartReviewRowSpan");
                AssertMeasured(span);
                Assert.True(BoundsIn(name, longRow).Right <= BoundsIn(span, longRow).Left,
                    $"the long section name ({name.Bounds}) runs into its time column ({span.Bounds})");
                Assert.Equal(TextTrimming.CharacterEllipsis, name.TextTrimming);
                foreach (var part in new[] { "AiChartReviewRowSpan", "AiChartReviewRowDetail", "AiChartReviewRowAccept", "AiChartReviewRowReject" })
                    AssertInsideCard(Named<Control>(longRow, part), card);

                // The time and change columns line up down the list.
                var spanLefts = rows.Select(r => BoundsIn(Named<TextBlock>(r, "AiChartReviewRowSpan"), r).Left).Distinct().ToList();
                Assert.True(spanLefts.Max() - spanLefts.Min() < 1, $"the time column is ragged: {string.Join(", ", spanLefts)}");

                AssertDecisionsLit(review.Vm, view);
                NarrowWidthLayout.Capture(window, "ai-chart-review-many");
                scroll.Offset = new Vector(0, scroll.Extent.Height);
                window.UpdateLayout();
                NarrowWidthLayout.Capture(window, "ai-chart-review-many-end");
            });
        }, CancellationToken.None);

    [Fact]
    public Task ARowClick_GoesToTheSection_AndTheKeyboardDecidesAndApplies() =>
        HeadlessSessionFixture.Dispatch(async () =>
        {
            var review = NewReview();
            var vm = review.Vm;
            await review.Run.RunAsync(AiChartJob.FhpAndFingering);
            var view = new AiChartReviewView { DataContext = vm };
            AttachAndLayout(view, DefaultTimelineHeight, window =>
            {
                var rows = Rows(view);
                review.State.CurrentTimeMs = 30_000;

                // Clicking the verse's row moves the playhead there and makes it the current row.
                var go = Named<Button>(rows[1], "AiChartReviewRowGo");
                var at = BoundsIn(go, window).Center;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                window.UpdateLayout();
                Assert.Equal(vm.Rows[1].StartMs, review.State.CurrentTimeMs, 3);
                Assert.Same(vm.Rows[1], vm.CurrentRow);
                Assert.Contains("ReviewCurrent", Named<Border>(rows[1], "AiChartReviewRow").Classes);
                Assert.DoesNotContain("ReviewCurrent", Named<Border>(rows[0], "AiChartReviewRow").Classes);
                Assert.Same(go, window.FocusManager?.GetFocusedElement());
                NarrowWidthLayout.Capture(window, "ai-chart-review-current");

                // ↑ goes to the intro; ↑ again stays (the list does not wrap).
                Press(window, Key.Up, PhysicalKey.ArrowUp);
                Assert.Same(vm.Rows[0], vm.CurrentRow);
                Assert.Equal(vm.Rows[0].StartMs, review.State.CurrentTimeMs, 3);
                Assert.Same(Named<Button>(rows[0], "AiChartReviewRowGo"), window.FocusManager?.GetFocusedElement());
                Press(window, Key.Up, PhysicalKey.ArrowUp);
                Assert.Same(vm.Rows[0], vm.CurrentRow);

                // R rejects the current row, A accepts it — the key, not a toggle: A twice stays accepted.
                Press(window, Key.R, PhysicalKey.R);
                Assert.True(vm.Rows[0].IsRejected);
                Press(window, Key.A, PhysicalKey.A);
                Press(window, Key.A, PhysicalKey.A);
                Assert.True(vm.Rows[0].IsAccepted);

                // ↓ to the verse, R; Enter on a row applies the one accepted section as ONE undo step.
                Press(window, Key.Down, PhysicalKey.ArrowDown);
                Assert.Same(vm.Rows[1], vm.CurrentRow);
                Press(window, Key.R, PhysicalKey.R);
                Assert.True(vm.Rows[1].IsRejected);
                Assert.False(review.History.CanUndo);
                Press(window, Key.Enter, PhysicalKey.Enter);
                Assert.False(vm.IsOpen);
                Assert.True(review.History.CanUndo);
                review.History.Undo();
                Assert.False(review.History.CanUndo);
            });
        }, CancellationToken.None);

    [Fact]
    public Task TheKeys_StandDown_WhenNothingIsHeld_OrAModifierIsDown() =>
        HeadlessSessionFixture.Dispatch(async () =>
        {
            var review = NewReview();
            var view = new AiChartReviewView { DataContext = review.Vm };
            Assert.False(view.HandleKey(Key.A, KeyModifiers.None, null), "a key was claimed with no review held");

            await review.Run.RunAsync(AiChartJob.FhpAndFingering);
            Assert.False(view.HandleKey(Key.A, KeyModifiers.Control, null), "Ctrl+A is the editor's, not the card's");
            Assert.True(view.HandleKey(Key.Down, KeyModifiers.None, null));
            Assert.Same(review.Vm.Rows[0], review.Vm.CurrentRow);
            // Enter is Apply only on a row: on a footer button it stays that button's own key.
            Assert.False(view.HandleKey(Key.Enter, KeyModifiers.None, null));
        }, CancellationToken.None);

    [Fact]
    public Task WithNoViewModel_TheCardIsHidden() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new AiChartReviewView();
            AttachAndLayout(view, DefaultTimelineHeight, _ =>
                Assert.False(view.FindControl<Border>("AiChartReviewCard")!.IsVisible,
                    "the card shows with no view model behind it (FallbackValue lost)"));
        }, CancellationToken.None);

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        window.UpdateLayout();
    }

    /// <summary>✓ is lit exactly on an accepted row, ✕ exactly on a rejected one.</summary>
    private static void AssertDecisionsLit(AiChartReviewViewModel vm, AiChartReviewView view)
    {
        var rows = Rows(view);
        Assert.Equal(vm.Rows.Count, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(vm.Rows[i].IsAccepted, Named<Button>(rows[i], "AiChartReviewRowAccept").Classes.Contains("ReviewLit"));
            Assert.Equal(vm.Rows[i].IsRejected, Named<Button>(rows[i], "AiChartReviewRowReject").Classes.Contains("ReviewLit"));
        }
    }

    private static void AttachAndLayout(AiChartReviewView view, double height, Action<Window> assert)
    {
        var (window, _) = NewTimelineHost(720, height, view);
        window.Show();
        try
        {
            NarrowWidthLayout.Settle(window);
            assert(window);
        }
        finally
        {
            window.Close();
        }
    }
}
