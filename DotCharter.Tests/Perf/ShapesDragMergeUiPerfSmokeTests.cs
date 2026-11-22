using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Drag-to-merge on the Shapes panel over the app's own inspector view model and a real arrangement, driven
///     with real pointer presses, moves and releases at design D's 530 px width: a list line dragged from its frets lights
///     the line that can take it, the release waits in the Checked page's one confirmation bar (the page comes on
///     screen), one yes merges as one undo step, and a release on
///     other frets, over nothing or with Esc changes nothing. Dragging near the list's edge scrolls it, and a
///     press that never moves begins no drag. Captures <c>shapes-drag-*</c> with
///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ShapesDragMergeUiPerfSmokeTests
{
    private const double Width = 530;
    private const double Height = 900;

    [Fact]
    public Task ACardDraggedOntoItsCopy_LightsTheTarget_WaitsInTheBar_AndOneYesMergesAsOneUndoStep() =>
        WithPanel(Height, p =>
        {
            var source = 1;
            var target = 0;
            p.Press(source);
            p.MoveTo(source, 14, 0);
            Assert.True(p.Screen.IsChoosingMergeTarget, "the drag did not begin");
            Assert.True(LocalizedText.Matches("Shapes_DragHint", p.Screen.MergeHintText), $"the drop hint '{p.Screen.MergeHintText}' is not Shapes_DragHint's format");
            Assert.True(p.Tab.FindControl<Grid>("ShapesMergeHint")!.IsEffectivelyVisible, "the drop hint is not showing");
            Assert.Contains("Unavailable", p.Card(source).Classes);
            Assert.False(p.Card(target).Classes.Contains("selected"), "the target lit before the pointer reached it");

            p.MoveTo(target, 0, 0);
            Assert.Contains("selected", p.Card(target).Classes);
            Assert.Equal(1, p.RealizedCards.Count(c => c.Classes.Contains("selected")));
            Assert.Equal(6, p.Arrangement.ChordTemplates.Count);
            NarrowWidthLayout.Capture(p.Window, "shapes-drag-over");

            p.Release(target);
            var bar = p.Tab.FindControl<StackPanel>("ShapesPendingBar")!;
            Assert.Contains(p.Tab.FindControl<Control>("ShapesCheckedCard")!, p.Tab.Cards.ShownCards);
            Assert.True(bar.IsEffectivelyVisible && bar.Bounds is { Width: > 1, Height: > 1 }, $"the merge bar laid out {bar.Bounds}");
            Assert.False(p.Tab.FindControl<Grid>("ShapesMergeHint")!.IsEffectivelyVisible, "the drop hint stayed after the drop");
            Assert.True(LocalizedText.Matches("Shapes_Pending_Merge", p.Screen.PendingText, (1, "Am")),
                $"the pending text '{p.Screen.PendingText}' is not Shapes_Pending_Merge's format, merging into Am");
            Assert.Equal(6, p.Arrangement.ChordTemplates.Count);
            Assert.False(p.History.CanUndo, "the drop changed the chart before its yes");
            NarrowWidthLayout.Capture(p.Window, "shapes-drag-merge");

            p.Click(p.Tab.FindControl<Button>("ShapesConfirm")!);
            Assert.Equal(5, p.Arrangement.ChordTemplates.Count);
            Assert.False(bar.IsEffectivelyVisible, "the bar stayed after the merge");

            p.History.Undo();
            p.Settle();
            Assert.Equal(6, p.Arrangement.ChordTemplates.Count);
            Assert.Equal(6, p.Screen.Rows.Count);
        });

    [Fact]
    public Task ADropOnOtherFrets_OrOverNothing_OrWithEsc_ChangesNothing() =>
        WithPanel(Height, p =>
        {
            var source = 1;

            // Other frets: the reason is said on the status rail, the pick is dropped, nothing waits in the bar.
            p.Press(source);
            p.MoveTo(source, 14, 0);
            p.MoveTo(3, 0, 0);
            Assert.DoesNotContain("selected", p.Card(3).Classes);
            p.Release(3);
            Assert.Equal(LocalizedText.Of("ChordShapes_Refused_DifferentFrets"), p.State.MainDisplayMessage);
            Assert.False(p.Screen.HasPending);
            Assert.False(p.Screen.IsChoosingMergeTarget);
            Assert.Equal(6, p.Arrangement.ChordTemplates.Count);

            // Released over the search box: no card, so the pick is dropped.
            var search = p.Tab.FindControl<TextBox>("ShapesSearch")!;
            p.Press(source);
            p.MoveTo(source, 14, 0);
            Assert.True(p.Screen.IsChoosingMergeTarget);
            var away = search.TranslatePoint(new Point(4, 4), p.Window)!.Value;
            p.Window.MouseMove(away, RawInputModifiers.LeftMouseButton);
            p.Settle();
            p.Window.MouseUp(away, MouseButton.Left);
            p.Settle();
            Assert.False(p.Screen.IsChoosingMergeTarget);
            Assert.False(p.Screen.HasPending);

            // Esc while dragging, then the release lands on a card that could have taken it: nothing happens.
            p.Press(source);
            p.MoveTo(source, 14, 0);
            p.MoveTo(0, 0, 0);
            p.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            p.Settle();
            Assert.False(p.Screen.IsChoosingMergeTarget);
            p.Release(0);
            Assert.False(p.Screen.HasPending);
            Assert.Equal(6, p.Arrangement.ChordTemplates.Count);
            Assert.False(p.History.CanUndo);
        });

    [Fact]
    public Task ADragNearTheEdge_ScrollsTheList_AndAPressWithoutAMove_BeginsNoDrag() =>
        WithPanel(330, p =>
        {
            var scroll = p.Tab.Scroll;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "the fixture list does not scroll");
            var source = 0;
            p.Press(source);
            p.MoveTo(source, 14, 0);
            var edge = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height - 6), p.Window)!.Value;
            for (var step = 0; step < 6; step++)
            {
                p.Window.MouseMove(new Point(edge.X + step, edge.Y), RawInputModifiers.LeftMouseButton);
                p.Settle();
            }

            Assert.True(scroll.Offset.Y > 1, $"the list did not follow the drag ({scroll.Offset.Y})");
            p.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            p.Settle();
            Assert.False(p.Screen.IsChoosingMergeTarget);
            p.Window.MouseUp(edge, MouseButton.Left);
            p.Settle();

            // No movement: no drag, no pick.
            scroll.Offset = default;
            p.Settle();
            p.Press(0);
            p.Release(0);
            Assert.False(p.Screen.IsChoosingMergeTarget);
            Assert.False(p.Screen.HasPending);
        });

    [Fact]
    public Task ADistantDropKeepsTheOriginalSourceAfterItsCardIsRecycled() =>
        WithPanel(520, p =>
        {
            p.Tab.RowsList.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel { CacheLength = 0 });
            for (var id = 6; id < 66; id++)
            {
                p.Arrangement.ChordTemplates.Add(new ChordTemplate
                {
                    Id = id, Name = $"Filler {id}", Frets = new() { [0] = id % 24, [1] = id / 24 + 1 }
                });
            }
            var source = p.Arrangement.ChordTemplates[1];
            var keeper = p.Arrangement.ChordTemplates[0];
            var distant = new ChordTemplate
            {
                Id = 1000, Name = "Distant", Frets = new(keeper.Frets), Fingers = new(keeper.Fingers)
            };
            p.Arrangement.ChordTemplates.Add(distant);
            p.Screen.Refresh();
            p.Settle();
            var sourceRow = p.Screen.AllRows.Single(r => ReferenceEquals(r.Template, source));
            var moved = p.Arrangement.Levels[0].Chords.Single(c => c.TemplateId == source.Id);
            var target = p.Arrangement.ChordTemplates.Count - 1;
            p.Press(1);
            p.MoveTo(1, 14, 0);
            p.Tab.FindControl<TextBox>("ShapesSearch")!.Focus();
            p.MoveTo(target, 0, 0);

            Assert.DoesNotContain(p.RealizedCards, c => ReferenceEquals(c.DataContext, sourceRow));
            Assert.True(p.Screen.IsChoosingMergeTarget);
            Assert.True(sourceRow.IsMergeSource);
            p.Release(target);
            Assert.True(p.Screen.HasPending);
            p.Click(p.Tab.FindControl<Button>("ShapesConfirm")!);

            Assert.Equal(distant.Id, moved.TemplateId);
            Assert.DoesNotContain(source, p.Arrangement.ChordTemplates);
        });

    private static Task WithPanel(double height, Action<Rig> body) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var history = services.GetRequiredService<IHistoryService>();
            var selection = services.GetRequiredService<ISelectionService>();
            var inspector = services.GetRequiredService<InspectorViewModel>();
            var screen = Assert.IsType<ShapesPanelViewModel>(inspector.ChordShapes);
            var previous = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, inspector.SelectedTabIndex);
            Window? window = null;
            try
            {
                var arrangement = Seed(state);
                selection.ClearSelections();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Assert.Equal(arrangement.ChordTemplates.Count, screen.Rows.Count);

                inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex;
                var view = new InspectorView { DataContext = inspector };
                window = NarrowWidthLayout.NewWindow(Width, height, view);
                window.Show();
                var rig = new Rig(window, view.GetVisualDescendants().OfType<ShapesPanelView>().Single(), screen, arrangement, state, history);
                rig.Settle();
                body(rig);
            }
            finally
            {
                window?.Close();
                inspector.SelectedTabIndex = previous.SelectedTabIndex;
                state.ActiveProject = previous.ActiveProject;
                state.ActiveArrangement = previous.ActiveArrangement;
                state.ActiveLevel = previous.ActiveLevel;
            }
        }, CancellationToken.None);

    /// <summary>The window, the panel under test and the pointer gestures of one case.</summary>
    private sealed class Rig(Window window, ShapesPanelView tab, ShapesPanelViewModel screen, Arrangement arrangement,
        ProjectState state, IHistoryService history)
    {
        internal Window Window { get; } = window;

        internal ShapesPanelView Tab { get; } = tab;

        internal ShapesPanelViewModel Screen { get; } = screen;

        internal Arrangement Arrangement { get; } = arrangement;

        internal ProjectState State { get; } = state;

        internal IHistoryService History { get; } = history;

        /// <summary>The currently realized card visuals.</summary>
        internal IReadOnlyList<Border> RealizedCards => ShapesPanelCards.Realized(Tab).ToList();

        internal Border Card(int index)
        {
            var template = Arrangement.ChordTemplates[index];
            var row = Screen.AllRows.Single(r => ReferenceEquals(r.Template, template));
            return ShapesPanelCards.Card(Tab, row);
        }

        internal void Settle()
        {
            for (var pass = 0; pass < 3; pass++)
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Window.UpdateLayout();
            }
        }

        /// <summary>A point on the line's frets stamp: blank card, so a press there arms a drag.</summary>
        private Point DiagramOf(Border card, double dx, double dy)
        {
            var grip = card.GetVisualDescendants().OfType<TextBlock>().First(b => b.Name == "ShapeFrets");
            return grip.TranslatePoint(new Point(grip.Bounds.Width / 2 + dx, grip.Bounds.Height / 2 + dy), Window)!.Value;
        }

        internal void Press(int index)
        {
            var card = Card(index);
            Settle();
            var at = DiagramOf(card, 0, 0);
            Window.MouseMove(at);
            Window.MouseDown(at, MouseButton.Left);
            Settle();
        }

        /// <summary>Moves the held pointer to <paramref name="index" />'s frets, offset by the given pixels, and lets the layout settle.</summary>
        internal void MoveTo(int index, double dx, double dy)
        {
            var card = Card(index);
            Window.MouseMove(DiagramOf(card, dx, dy), RawInputModifiers.LeftMouseButton);
            Settle();
        }

        internal void Release(int index)
        {
            var card = Card(index);
            Window.MouseUp(DiagramOf(card, 0, 0), MouseButton.Left);
            Settle();
        }

        internal void Click(Control control)
        {
            var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)!.Value;
            Window.MouseMove(at);
            Window.MouseDown(at, MouseButton.Left);
            Window.MouseUp(at, MouseButton.Left);
            Settle();
        }
    }

    /// <summary>
    ///     Three copies of one grip (2, 1 and 2 chords, fingered three ways; the first is called Am), a C, and two
    ///     shapes nothing uses.
    /// </summary>
    private static Arrangement Seed(ProjectState state)
    {
        var project = new ChartProject { Chart = { SongName = "Shapes drag fixture" } };
        for (var i = 0; i < 64; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel { DifficultyIndex = 0 };
        arrangement.Levels.Add(level);
        var amFrets = new Dictionary<int, int> { [1] = 0, [2] = 2, [3] = 2, [4] = 1, [5] = 0 };
        for (var id = 0; id < 6; id++)
        {
            var template = new ChordTemplate { Id = id, Name = id == 0 ? "Am" : "" };
            var frets = id < 3 ? amFrets : id == 3 ? new Dictionary<int, int> { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 } : new Dictionary<int, int> { [0] = id, [1] = id };
            template.Frets = new Dictionary<int, int>(frets);
            if (id < 4) ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
            if (id == 1) template.Fingers[4] = template.Fingers[4] == 4 ? 3 : 4;
            if (id == 2) template.Fingers[3] = template.Fingers[3] == 3 ? 2 : 3;
            arrangement.ChordTemplates.Add(template);
            var chords = id switch { 0 => 2, 1 => 1, 2 => 2, 3 => 1, _ => 0 };
            for (var n = 0; n < chords; n++)
            {
                var chord = new Chord { Position = new FractionalPosition(id * 8 + n * 2), TemplateId = id };
                foreach (var (s, f) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
                level.Chords.Add(chord);
            }
        }

        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = level;
        return arrangement;
    }
}
