using Avalonia;
using Avalonia.Controls;
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
///     with real pointer presses, moves and releases: a card dragged from its diagram lights the card that can
///     take it, the release waits in the one confirmation bar, one yes merges as one undo step, and a release on
///     other frets, over nothing or with Esc changes nothing. Dragging near the list's edge scrolls it, and a
///     press that never moves begins no drag. Captures <c>shapes-drag-*</c> with
///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ShapesDragMergeUiPerfSmokeTests
{
    private const double Width = 600;
    private const double Height = 900;

    [Fact]
    public Task ACardDraggedOntoItsCopy_LightsTheTarget_WaitsInTheBar_AndOneYesMergesAsOneUndoStep() =>
        WithPanel(Height, p =>
        {
            var source = p.Cards[1];
            var target = p.Cards[0];
            p.Press(source);
            p.MoveTo(source, 14, 0);
            Assert.True(p.Screen.IsChoosingMergeTarget, "the drag did not begin");
            Assert.StartsWith("Drop “", p.Screen.MergeHintText, StringComparison.Ordinal);
            Assert.True(p.Tab.FindControl<Border>("ShapesMergeHint")!.IsEffectivelyVisible, "the drop hint is not showing");
            Assert.Contains("Unavailable", source.Classes);
            Assert.False(target.Classes.Contains("selected"), "the target lit before the pointer reached it");

            p.MoveTo(target, 0, 0);
            Assert.Contains("selected", target.Classes);
            Assert.Equal(1, p.Cards.Count(c => c.Classes.Contains("selected")));
            Assert.Equal(6, p.Arrangement.ChordTemplates.Count);
            NarrowWidthLayout.Capture(p.Window, "shapes-drag-over");

            p.Release(target);
            var bar = p.Tab.FindControl<Border>("ShapesPendingBar")!;
            Assert.True(bar.IsEffectivelyVisible && bar.Bounds is { Width: > 1, Height: > 1 }, $"the merge bar laid out {bar.Bounds}");
            Assert.False(p.Tab.FindControl<Border>("ShapesMergeHint")!.IsEffectivelyVisible, "the drop hint stayed after the drop");
            Assert.StartsWith("Merge “", p.Screen.PendingText, StringComparison.Ordinal);
            Assert.Contains("into “Am”", p.Screen.PendingText, StringComparison.Ordinal);
            Assert.EndsWith("One undo step.", p.Screen.PendingText, StringComparison.Ordinal);
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
            var source = p.Cards[1];

            // Other frets: the reason is said on the status rail, the pick is dropped, nothing waits in the bar.
            p.Press(source);
            p.MoveTo(source, 14, 0);
            p.MoveTo(p.Cards[3], 0, 0);
            Assert.DoesNotContain("selected", p.Cards[3].Classes);
            p.Release(p.Cards[3]);
            Assert.Equal("Not merged: the two shapes have different frets.", p.State.MainDisplayMessage);
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
            p.MoveTo(p.Cards[0], 0, 0);
            p.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            p.Settle();
            Assert.False(p.Screen.IsChoosingMergeTarget);
            p.Release(p.Cards[0]);
            Assert.False(p.Screen.HasPending);
            Assert.Equal(6, p.Arrangement.ChordTemplates.Count);
            Assert.False(p.History.CanUndo);
        });

    [Fact]
    public Task ADragNearTheEdge_ScrollsTheList_AndAPressWithoutAMove_BeginsNoDrag() =>
        WithPanel(330, p =>
        {
            var scroll = p.Tab.FindControl<ScrollViewer>("ShapesScroll")!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "the fixture list does not scroll");
            var source = p.Cards[0];
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
            p.Press(p.Cards[0]);
            p.Release(p.Cards[0]);
            Assert.False(p.Screen.IsChoosingMergeTarget);
            Assert.False(p.Screen.HasPending);
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

        /// <summary>The cards in the order of the arrangement's templates (the first three are copies of one grip).</summary>
        internal IReadOnlyList<Border> Cards =>
            Arrangement.ChordTemplates.Select(t =>
            {
                var row = Screen.AllRows.Single(r => ReferenceEquals(r.Template, t));
                var list = Tab.FindControl<ItemsControl>("ShapesRows")!;
                return list.ContainerFromIndex(list.Items.Cast<object>().ToList().IndexOf(row))!
                    .GetVisualDescendants().OfType<Border>().First(b => b.Name == "ShapeCard");
            }).ToList();

        internal void Settle()
        {
            for (var pass = 0; pass < 3; pass++)
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Window.UpdateLayout();
            }
        }

        private Point DiagramOf(Border card, double dx, double dy)
        {
            var button = card.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ShapePlace");
            return button.TranslatePoint(new Point(button.Bounds.Width / 2 + dx, button.Bounds.Height / 2 + dy), Window)!.Value;
        }

        internal void Press(Border card)
        {
            card.BringIntoView();
            Settle();
            var at = DiagramOf(card, 0, 0);
            Window.MouseMove(at);
            Window.MouseDown(at, MouseButton.Left);
            Settle();
        }

        /// <summary>Moves the held pointer to <paramref name="card" />'s diagram, offset by the given pixels, and lets the layout settle.</summary>
        internal void MoveTo(Border card, double dx, double dy)
        {
            Window.MouseMove(DiagramOf(card, dx, dy), RawInputModifiers.LeftMouseButton);
            Settle();
        }

        internal void Release(Border card)
        {
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
