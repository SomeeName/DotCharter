using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #798: the left-hand finger and pick-direction rows of the note Technique section and the
///     strum row of the chord one. Each view builds over the app's own <see cref="InspectorViewModel" />,
///     attaches and MEASURES non-zero; the view model shows the selected sound's value, and an edit
///     writes it through the history (one undo puts it back).
/// </summary>
[Collection("HeadlessUI")]
public sealed class NoteFingerPickStrumInspectorUiPerfSmokeTests
{
    [Fact]
    public async Task TheNoteRows_LayOut_ShowTheNote_AndEditItUndoably() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var history = services.GetRequiredService<IHistoryService>();
            var vm = services.GetRequiredService<InspectorViewModel>();
            var previousProject = state.ActiveProject;
            var previousArrangement = state.ActiveArrangement;

            var (project, arrangement, level) = Chart();
            var note = new GuitarNote
            {
                Position = new FractionalPosition(2), StringIndex = 6, Fret = 5,
                LeftHandFinger = 2, PickDirection = PickDirectionType.Up
            };
            level.Notes.Add(note);
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            state.ActiveLevel = level;

            var view = new SoundNoteTechniqueView { DataContext = vm };
            var window = Show(view);
            try
            {
                selection.ClearSelections();
                selection.SelectedNotes.Add(note);
                Settle(window);

                Assert.Equal(2, vm.NoteLeftHandFinger);
                Assert.Equal((int)PickDirectionType.Up, vm.NotePickDirection);
                foreach (var name in new[] { "NoteFingerRow", "NotePickDirectionRow" })
                {
                    var row = Named<StackPanel>(view, name);
                    Assert.True(row.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {row.Bounds}");
                }

                vm.NoteLeftHandFinger = 0; // the thumb
                vm.NotePickDirection = (int)PickDirectionType.Down;
                Settle(window);
                Assert.Equal(0, note.LeftHandFinger);
                Assert.Equal(PickDirectionType.Down, note.PickDirection);

                history.Undo();
                history.Undo();
                Settle(window);
                Assert.Equal(2, note.LeftHandFinger);
                Assert.Equal(PickDirectionType.Up, note.PickDirection);
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

    [Fact]
    public async Task TheStrumRow_LaysOut_ShowsTheChord_AndEditsItUndoably() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var history = services.GetRequiredService<IHistoryService>();
            var vm = services.GetRequiredService<InspectorViewModel>();
            var previousProject = state.ActiveProject;
            var previousArrangement = state.ActiveArrangement;

            var (project, arrangement, level) = Chart();
            var template = new ChordTemplate { Id = 0, Name = "G5", Frets = { [0] = 3, [1] = 5, [2] = 5 } };
            arrangement.ChordTemplates.Add(template);
            var chord = new Chord { Position = new FractionalPosition(2), TemplateId = 0, Strum = PickDirectionType.Up };
            foreach (var (s, f) in template.Frets)
                chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f, Sustain = new FractionalPosition(1) });
            level.Chords.Add(chord);
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            state.ActiveLevel = level;

            var view = new SoundChordTechniqueView { DataContext = vm };
            var window = Show(view);
            try
            {
                selection.ClearSelections();
                selection.SelectedChords.Add(chord);
                Settle(window);

                Assert.Equal((int)PickDirectionType.Up, vm.ChordStrum);
                var row = Named<StackPanel>(view, "ChordStrumRow");
                Assert.True(row.Bounds is { Width: > 1, Height: > 1 }, $"the strum row laid out as {row.Bounds}");

                vm.ChordStrum = (int)PickDirectionType.Down;
                Settle(window);
                Assert.Equal(PickDirectionType.Down, chord.Strum);

                history.Undo();
                Settle(window);
                Assert.Equal(PickDirectionType.Up, chord.Strum);
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

    private static (ChartProject Project, Arrangement Arrangement, ChartLevel Level) Chart()
    {
        // Seven strings, so the 7th-string thumb note of the issue is one a chart can hold.
        var arrangement = new Arrangement
        {
            Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 7,
            Tuning = ["B", "E", "A", "D", "G", "B", "E"]
        };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var project = new ChartProject();
        for (var i = 0; i < 16; i++) project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120 });
        project.Arrangements.Add(arrangement);
        return (project, arrangement, level);
    }

    private static Window Show(Control view)
    {
        var host = new UserControl { Content = view };
        host.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/"))
        {
            Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
        });
        var window = new Window { Width = 420, Height = 900, Content = host };
        window.Show();
        return window;
    }

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
