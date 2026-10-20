using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DotCharter.Services.Editing.DraftMerge;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Services.Editing.DraftMerge;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;
using static DotCharter.Tests.Services.Editing.DraftMerge.DraftMergeFixture;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Chart from audio's merge card (#647) built for real: a draft is held against the fixture's
///     arrangement, the card is hung off the top-right corner of a timeline-sized host as
///     <c>MainWindow.axaml</c> hangs it, shown and laid out, and the card, its header and footer,
///     every phrase row and both of a row's verbs MEASURE (a view whose styles failed still
///     arranges, at zero size). A ✓ click merges the phrase as one undo step; the keyboard
///     (↑ / ↓ / A / R) drives the same verbs through real key presses. With no review the card is
///     hidden and offered to the shell's prewarm; with no view model it shows nothing.
/// </summary>
[Collection("HeadlessUI")]
public sealed class DraftMergeReviewConstructionUiPerfSmokeTests
{
    private const double TimelineWidth = 720;

    /// <summary>The timeline row at 1920x1080 (<c>TimelineAreaMinHeight.TimelineMinPx</c>).</summary>
    private const double TimelineHeight = 360;

    [Fact]
    public Task AHeldDraft_ShowsTheCard_WithEveryRowAndVerbMeasured() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var card = new Card();
            var view = new DraftMergeReviewView { DataContext = card.Vm };
            Assert.Equal([view.FindControl<Control>("DraftMergeCard")!], view.HiddenSurfaces());

            card.Fixture.Seed(Note(9, 0, 0));
            card.HoldIntroVerseChorus(replace: false);
            Assert.True(card.Vm.IsOpen);

            AttachAndLayout(view, window =>
            {
                var border = view.FindControl<Border>("DraftMergeCard")!;
                Assert.True(border.IsVisible, "the card is hidden with a draft held");
                AssertMeasured(border);
                string[] parts =
                [
                    "DraftMergeTitle", "DraftMergeCounts", "DraftMergeInfo", "DraftMergeSubtitle", "DraftMergeOccupied",
                    "DraftMergeDiscard", "DraftMergeAll"
                ];
                foreach (var name in parts) AssertInsideCard(view.FindControl<Control>(name)!, border);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(view.FindControl<Control>("DraftMergeInfo")!)));

                var rows = Rows(view);
                Assert.Equal(card.Vm.Rows.Count, rows.Count);
                Assert.Equal(2, rows.Count);
                foreach (var row in rows)
                {
                    AssertMeasured(row);
                    var buttons = row.GetVisualDescendants().OfType<Button>().ToList();
                    Assert.Equal(["DraftMergeRowGo", "DraftMergeRowAccept", "DraftMergeRowReject"], buttons.Select(b => b.Name));
                    Assert.True(row.Bounds.Height <= 30, $"a phrase row is {row.Bounds.Height} px tall, not one dense line");
                    foreach (var part in new[] { "DraftMergeRowName", "DraftMergeRowSpan", "DraftMergeRowDetail", "DraftMergeRowConfidence" })
                        AssertInsideCard(Named<TextBlock>(row, part), border);
                    foreach (var button in buttons)
                    {
                        AssertInsideCard(button, border);
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), $"{button.Name} has no accessible name");
                        Assert.NotNull(ToolTip.GetTip(button));
                    }
                }

                // The count columns line up down the list.
                var spanLefts = rows.Select(r => BoundsIn(Named<TextBlock>(r, "DraftMergeRowSpan"), r).Left).Distinct().ToList();
                Assert.True(spanLefts.Max() - spanLefts.Min() < 1, $"the time column is ragged: {string.Join(", ", spanLefts)}");

                Assert.Empty(view.HiddenSurfaces());
                NarrowWidthLayout.Capture(window, "draft-merge-review");

                // ✓ on the verse merges it now, as one undo step; its row leaves the card.
                Click(window, Named<Button>(rows[1], "DraftMergeRowAccept"));
                Assert.Equal(["intro"], card.Vm.Rows.Select(r => r.Name));
                Assert.Single(card.History.GetUndoSteps());
                Assert.Single(Rows(view));
            });
        }, CancellationToken.None);

    [Fact]
    public Task AReplacingRow_ReadsInTheWarningColour() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var card = new Card();
            card.Fixture.Seed(Note(5, 2, 5));
            card.HoldIntroVerseChorus(replace: true);
            var view = new DraftMergeReviewView { DataContext = card.Vm };

            AttachAndLayout(view, window =>
            {
                var rows = Rows(view);
                Assert.Equal(3, rows.Count);
                Assert.False(view.FindControl<Control>("DraftMergeOccupied")!.IsVisible);
                for (var i = 0; i < rows.Count; i++)
                    Assert.Equal(card.Vm.Rows[i].IsReplace, Named<Border>(rows[i], "DraftMergeRow").Classes.Contains("ReviewReplace"));
                Assert.True(card.Vm.Rows[1].IsReplace);
                NarrowWidthLayout.Capture(window, "draft-merge-review-replace");
            });
        }, CancellationToken.None);

    [Fact]
    public Task TheKeyboard_GoesToARow_ThenMergesOrDropsIt() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var card = new Card();
            card.HoldIntroVerseChorus(replace: false);
            var view = new DraftMergeReviewView { DataContext = card.Vm };
            AttachAndLayout(view, window =>
            {
                var rows = Rows(view);
                var go = Named<Button>(rows[1], "DraftMergeRowGo");
                Click(window, go);
                Assert.Same(card.Vm.Rows[1], card.Vm.CurrentRow);
                Assert.Equal(card.Vm.Rows[1].Phrase.StartMs, card.State.CurrentTimeMs, 3);
                Assert.Contains("ReviewCurrent", Named<Border>(rows[1], "DraftMergeRow").Classes);
                Assert.Same(go, window.FocusManager?.GetFocusedElement());

                Press(window, Key.Up, PhysicalKey.ArrowUp);
                Assert.Same(card.Vm.Rows[0], card.Vm.CurrentRow);
                Assert.Same(Named<Button>(rows[0], "DraftMergeRowGo"), window.FocusManager?.GetFocusedElement());

                // R drops the intro (nothing written); ↓ then A merges the next row as ONE undo step.
                Press(window, Key.R, PhysicalKey.R);
                Assert.Equal(["verse", "chorus"], card.Vm.Rows.Select(r => r.Name));
                Assert.False(card.History.CanUndo);
                // The dropped row took its focused button with it: the card holds focus, so the next key lands.
                Assert.Same(view.FindControl<Border>("DraftMergeCard"), window.FocusManager?.GetFocusedElement());
                Press(window, Key.Down, PhysicalKey.ArrowDown);
                Assert.Same(card.Vm.Rows[0], card.Vm.CurrentRow);
                Assert.Same(Named<Button>(Rows(view)[0], "DraftMergeRowGo"), window.FocusManager?.GetFocusedElement());
                Press(window, Key.A, PhysicalKey.A);
                Assert.Single(card.History.GetUndoSteps());
                Assert.Single(card.Vm.Rows);
            });
        }, CancellationToken.None);

    [Fact]
    public Task TheKeys_StandDown_WhenNothingIsHeld_OrAModifierIsDown() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var card = new Card();
            var view = new DraftMergeReviewView { DataContext = card.Vm };
            Assert.False(view.HandleKey(Key.A, KeyModifiers.None), "a key was claimed with no draft held");

            card.HoldIntroVerseChorus(replace: false);
            Assert.False(view.HandleKey(Key.A, KeyModifiers.Control), "Ctrl+A is the editor's, not the card's");
            Assert.True(view.HandleKey(Key.Down, KeyModifiers.None));
            Assert.Same(card.Vm.Rows[0], card.Vm.CurrentRow);
        }, CancellationToken.None);

    [Fact]
    public Task WithNoViewModel_TheCardIsHidden() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new DraftMergeReviewView();
            AttachAndLayout(view, _ =>
                Assert.False(view.FindControl<Border>("DraftMergeCard")!.IsVisible,
                    "the card shows with no view model behind it (FallbackValue lost)"));
        }, CancellationToken.None);

    private static List<Control> Rows(DraftMergeReviewView view) =>
        view.FindControl<ItemsControl>("DraftMergeRows")!.GetRealizedContainers().OfType<Control>().ToList();

    private static T Named<T>(Visual root, string name) where T : Control => NarrowWidthLayout.Named<T>(root, name);

    private static Rect BoundsIn(Visual control, Visual space) => new(control.TranslatePoint(default, space)!.Value, control.Bounds.Size);

    private static void AssertMeasured(Control control) =>
        Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1,
            $"{control.Name ?? control.GetType().Name} laid out as {control.Bounds} — it rendered nothing.");

    private static void AssertInsideCard(Control control, Border card)
    {
        AssertMeasured(control);
        var r = BoundsIn(control, card);
        Assert.True(r.X >= -0.5 && r.Right <= card.Bounds.Width + 0.5 && r.Y >= -0.5 && r.Bottom <= card.Bounds.Height + 0.5,
            $"{control.Name ?? control.GetType().Name} at {r} leaves the {card.Bounds.Size} card ({HeadlessSessionFixture.SweptCulture})");
    }

    private static void Click(Window window, Control control)
    {
        var at = BoundsIn(control, window).Center;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.UpdateLayout();
    }

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        window.UpdateLayout();
    }

    /// <summary>A panel the size of the timeline row with the card hung off its top-right corner.</summary>
    private static void AttachAndLayout(DraftMergeReviewView view, Action<Window> assert)
    {
        view.HorizontalAlignment = HorizontalAlignment.Right;
        view.VerticalAlignment = VerticalAlignment.Top;
        view.Margin = new Thickness(12, 10);
        var host = new Panel { Width = TimelineWidth, Height = TimelineHeight };
        host.Children.Add(view);
        var window = NarrowWidthLayout.NewWindow(TimelineWidth, TimelineHeight, host);
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

    /// <summary>The card's view model over <see cref="DraftMergeFixture" /> (intro 0, verse 4, chorus 8, END 12).</summary>
    private sealed class Card
    {
        public Card()
        {
            State = new ProjectState { ActiveProject = Fixture.Project, ActiveArrangement = Fixture.Arrangement, ActiveLevel = Fixture.Level };
            var selection = new SelectionService();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, new FakeConfigurationService(), FakeLocalization.KeysOnly());
            var editor = new ChartEditorViewModel(State, transport, History, selection, new TempoService(new FakeAppLogger()),
                new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());
            Host = new DraftMergeReviewHost(History, new FretAnchorGenerationService());
            Vm = new DraftMergeReviewViewModel(Host, editor, History, State, FakeLocalization.English());
        }

        public DraftMergeFixture Fixture { get; } = new();

        public HistoryService History { get; } = new(new FakeAppLogger());

        public ProjectState State { get; }

        public DraftMergeReviewHost Host { get; }

        public DraftMergeReviewViewModel Vm { get; }

        public void HoldIntroVerseChorus(bool replace) =>
            Host.Hold(Fixture.Plan([Note(0, 0, 3), Note(1, 1, 5), Note(5, 2, 7), Note(9, 3, 2)], replace, [0.2, 0.4, 0.5, 0.9]));
    }
}
