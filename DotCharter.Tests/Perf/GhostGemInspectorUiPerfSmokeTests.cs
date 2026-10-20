using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #785: the Sound tab's ghost-gem block for a selected hand shape. It builds over the app's own
///     <see cref="InspectorViewModel" />, attaches, MEASURES non-zero, shows one checkbox per string
///     of the grip with the ghost string ticked and the pin box ticked-and-locked, and a click on a
///     ghost box writes the grip through the history (one undo puts it back).
/// </summary>
[Collection("HeadlessUI")]
public sealed class GhostGemInspectorUiPerfSmokeTests
{
    [Fact]
    public async Task TheGhostBlock_LaysOut_ShowsTheGrip_AndItsBoxEditsTheGripUndoably() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var history = services.GetRequiredService<IHistoryService>();
            var vm = services.GetRequiredService<InspectorViewModel>();
            var previousProject = state.ActiveProject;
            var previousArrangement = state.ActiveArrangement;

            // A G5 + octave with its octave string ghosted: the hand shape holds 3-5-5, plays 3-5.
            var arrangement = new Arrangement
            {
                Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6,
                Tuning = ["E", "A", "D", "G", "B", "E"]
            };
            var level = new ChartLevel();
            arrangement.Levels.Add(level);
            var template = new ChordTemplate { Id = 0, Name = "G5", Frets = { [0] = 3, [1] = 5, [2] = 5 } };
            arrangement.ChordTemplates.Add(template);
            var chord = new Chord { Position = new FractionalPosition(2), TemplateId = 0 };
            foreach (var (s, f) in template.Frets)
                chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f, Sustain = new FractionalPosition(1) });
            level.Chords.Add(chord);
            var project = new ChartProject();
            for (var i = 0; i < 16; i++) project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120 });
            project.Arrangements.Add(arrangement);
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            state.ActiveLevel = level;
            GhostGems.Ghost(arrangement, level, [], [chord], s => s == 2).Action!.Execute();
            var shape = Assert.Single(level.HandShapes);

            var view = new SoundShapeView { DataContext = vm };
            var host = new UserControl { Content = view };
            host.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/"))
            {
                Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
            });
            var window = new Window { Width = 420, Height = 900, Content = host };
            window.Show();
            try
            {
                selection.ClearSelections();
                selection.SelectedHandShapes.Add(shape);
                Settle(window);

                var block = Named<StackPanel>(view, "SoundGhostBlock");
                Assert.True(block.IsEffectivelyVisible, "a selected hand shape shows its ghost-gem block");
                Assert.True(block.Bounds is { Width: > 1, Height: > 1 }, $"the ghost block laid out as {block.Bounds}");

                var boxes = Named<ItemsControl>(view, "SoundGhostRows").GetLogicalDescendants().OfType<CheckBox>().ToList();
                Assert.Equal(3, boxes.Count);
                Assert.Single(boxes, b => b.IsChecked == true);
                var pin = Named<CheckBox>(view, "SoundGripPinned");
                Assert.True(pin.IsChecked);
                Assert.False(pin.IsEnabled); // ghost gems pin the grip whatever the flag says

                // Untick the ghost: the bar is repointed at the un-ghosted grip, in one undo node.
                boxes.Single(b => b.IsChecked == true).IsChecked = false;
                Settle(window);
                Assert.Equal(0, arrangement.ChordTemplates.Single(t => t.Id == shape.TemplateId).GhostMask);

                history.Undo();
                Settle(window);
                Assert.True(arrangement.ChordTemplates.Single(t => t.Id == shape.TemplateId).IsGhost(2));
            }
            finally
            {
                selection.ClearSelections();
                window.Close();
                state.ActiveProject = previousProject;
                state.ActiveArrangement = previousArrangement;
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no {typeof(T).Name} named '{name}' in {root.GetType().Name}");

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
