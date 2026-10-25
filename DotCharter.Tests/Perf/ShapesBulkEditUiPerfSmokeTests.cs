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
///     The Shapes panel's bulk edit over the app's own inspector view model and a real arrangement, driven
///     with real pointer clicks: the tick shows on a hovered card and keeps its card open once ticked, the bulk
///     bar names what is ticked, Merge into one and Delete wait in the one confirmation bar (its buttons laid
///     out, the delete one in the danger tier) and one yes applies each as one undo step. Captures
///     <c>shapes-bulk-*</c> with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ShapesBulkEditUiPerfSmokeTests
{
    private const double Width = 600;
    private const double Height = 524;

    [Fact]
    public Task TickedCards_OfferTheBulkBar_AndMergeAndDeleteWaitInTheBarForTheirYes() =>
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
                window = NarrowWidthLayout.NewWindow(Width, Height, view);
                window.Show();
                Settle(window);
                var tab = view.GetVisualDescendants().OfType<ShapesPanelView>().Single();
                var selectionBar = tab.FindControl<Border>("ShapesSelectionBar")!;
                Assert.False(selectionBar.IsEffectivelyVisible, "the bulk bar shows with nothing ticked");

                // The three copies of the Am grip, ticked by real clicks on the tick of each hovered card.
                foreach (var template in arrangement.ChordTemplates.Take(3)) TickByClick(window, tab, screen, template);
                Assert.Equal("3 shapes selected · 5 chords", screen.SelectionText);
                Assert.True(selectionBar.IsEffectivelyVisible && selectionBar.Bounds is { Width: > 1, Height: > 1 }, $"the bulk bar laid out {selectionBar.Bounds}");
                foreach (var name in new[] { "ShapesEditSelected", "ShapesMergeSelected", "ShapesDeleteSelected", "ShapesClearSelection" })
                {
                    var button = tab.FindControl<Button>(name)!;
                    Assert.True(button.IsEffectivelyVisible && button.IsEnabled && button.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out {button.Bounds}");
                }

                var tickedCards = Cards(tab).Where(c => c.Classes.Contains("Record")).ToList();
                Assert.Equal(3, tickedCards.Count);
                var away = tab.FindControl<TextBox>("ShapesSearch")!.TranslatePoint(new Point(4, 4), window)!.Value;
                window.MouseMove(away);
                Settle(window);
                Assert.All(tickedCards, c =>
                {
                    var tick = c.GetVisualDescendants().OfType<CheckBox>().First(b => b.Name == "ShapeTick");
                    Assert.True(tick.IsEffectivelyVisible && tick.Bounds is { Width: > 1, Height: > 1 }, "a ticked card hid its tick");
                });
                NarrowWidthLayout.Capture(window, "shapes-bulk-ticked");

                // Merge into one: the bar says what moves where, nothing changes until its yes.
                Click(window, tab.FindControl<Button>("ShapesMergeSelected")!);
                Settle(window);
                var bar = tab.FindControl<Border>("ShapesPendingBar")!;
                Assert.True(bar.IsEffectivelyVisible && bar.Bounds is { Width: > 1, Height: > 1 }, $"the confirmation bar laid out {bar.Bounds}");
                Assert.StartsWith("Merge 3 shapes into", screen.PendingText, StringComparison.Ordinal);
                Assert.Equal(6, arrangement.ChordTemplates.Count);
                NarrowWidthLayout.Capture(window, "shapes-bulk-merge");
                var confirm = tab.FindControl<Button>("ShapesConfirm")!;
                Assert.True(confirm.IsEffectivelyVisible && confirm.Bounds is { Width: > 1, Height: > 1 }, $"the Merge button laid out {confirm.Bounds}");
                Click(window, confirm);
                Settle(window);
                Assert.Equal(4, arrangement.ChordTemplates.Count);
                Assert.False(bar.IsEffectivelyVisible, "the bar stayed after the merge");
                Assert.False(selectionBar.IsEffectivelyVisible, "the bulk bar stayed after the merge");
                Assert.Equal(4, screen.Rows.Count);

                history.Undo();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Settle(window);
                Assert.Equal(6, screen.Rows.Count);

                // Delete: the two copies of the Am grip outside the first card move onto it; the danger button asks once.
                foreach (var template in arrangement.ChordTemplates.Skip(1).Take(2)) TickByClick(window, tab, screen, template);
                Click(window, tab.FindControl<Button>("ShapesDeleteSelected")!);
                Settle(window);
                Assert.True(bar.IsEffectivelyVisible && screen.IsPendingDestructive, "the delete did not wait in the bar");
                var danger = tab.FindControl<Button>("ShapesConfirmDanger")!;
                Assert.True(danger.IsEffectivelyVisible && danger.Bounds is { Width: > 1, Height: > 1 }, $"the danger button laid out {danger.Bounds}");
                NarrowWidthLayout.Capture(window, "shapes-bulk-delete");
                Click(window, danger);
                Settle(window);
                Assert.Equal(4, arrangement.ChordTemplates.Count);
                Assert.False(bar.IsEffectivelyVisible, "the bar stayed after the delete");
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

    private static void TickByClick(Window window, ShapesPanelView tab, ShapesPanelViewModel screen, ChordTemplate template)
    {
        var row = screen.AllRows.Single(r => ReferenceEquals(r.Template, template));
        var list = tab.FindControl<ItemsControl>("ShapesRows")!;
        var card = list.ContainerFromIndex(list.Items.Cast<object>().ToList().IndexOf(row))!
            .GetVisualDescendants().OfType<Border>().First(b => b.Name == "ShapeCard");
        card.BringIntoView();
        Settle(window);
        window.MouseMove(card.TranslatePoint(new Point(card.Bounds.Width - 6, 6), window)!.Value);
        Settle(window);
        var tick = card.GetVisualDescendants().OfType<CheckBox>().First(b => b.Name == "ShapeTick");
        Assert.True(tick.IsEffectivelyVisible && tick.Bounds is { Width: > 1, Height: > 1 }, $"the tick laid out {tick.Bounds} on a hovered card");
        Click(window, tick);
        Settle(window);
        Assert.True(row.IsSelected, "the click did not tick the card");
    }

    private static IEnumerable<Border> Cards(Control view) =>
        view.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "ShapeCard");

    private static void Click(Window window, Control control)
    {
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }

    /// <summary>
    ///     Three copies of one grip (2, 1 and 2 chords, fingered three ways), a C, and two shapes nothing uses.
    /// </summary>
    private static Arrangement Seed(ProjectState state)
    {
        var project = new ChartProject { Chart = { SongName = "Shapes bulk fixture" } };
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
