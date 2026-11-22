using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #328: the chord inspector's whole-chord row. "No notes" is visibly disabled, with its reason, while
///     "Split notes" is on (the stored flag is untouched), and the repeat line says what the player sees in
///     game. The view builds over the app's own <see cref="InspectorViewModel" />, attaches and MEASURES
///     non-zero (only non-zero: the text is localized, nothing here compares a width).
/// </summary>
[Collection("HeadlessUI")]
public sealed class ChordWholeChordRowUiPerfSmokeTests
{
    [Fact]
    public async Task SplitNotes_DisablesNoNotes_WithItsReason_AndKeepsTheStoredFlag() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var vm = services.GetRequiredService<InspectorViewModel>();
            var previousProject = state.ActiveProject;
            var previousArrangement = state.ActiveArrangement;

            var arrangement = new Arrangement
            {
                Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6,
                Tuning = ["E", "A", "D", "G", "B", "E"]
            };
            var level = new ChartLevel();
            arrangement.Levels.Add(level);
            var project = new ChartProject();
            for (var i = 0; i < 16; i++) project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120 });
            project.Arrangements.Add(arrangement);
            var template = new ChordTemplate { Id = 0, Name = "G5", Frets = { [0] = 3, [1] = 5, [2] = 5 } };
            arrangement.ChordTemplates.Add(template);
            var chord = new Chord { Position = new FractionalPosition(2), TemplateId = 0, ForceNoNotes = true };
            foreach (var (s, f) in template.Frets)
                chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f, Sustain = new FractionalPosition(1) });
            level.Chords.Add(chord);
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            state.ActiveLevel = level;
            var restoreApplyStep = state.InspectorApplyStep;
            state.InspectorApplyStep = true; // #805: the staged-until-Apply behaviour is opt-in

            var view = new SoundChordWholeView { DataContext = vm };
            var window = Show(view);
            try
            {
                selection.ClearSelections();
                selection.SelectedChords.Add(chord);
                Settle(window);

                var noNotes = Named<ToggleButton>(view, "ChordNoNotesChip");
                var split = Named<ToggleButton>(view, "ChordSplitNotesChip");
                var hint = Named<TextBlock>(view, "ChordNoNotesDisabledHint");
                var line = Named<TextBlock>(view, "ChordRepeatVerdictLine");
                Assert.True(noNotes.IsEnabled);
                Assert.True(noNotes.IsChecked);
                Assert.False(hint.IsVisible);
                Assert.True(line.Bounds is { Width: > 1, Height: > 1 }, $"the repeat line laid out as {line.Bounds}");
                Assert.Equal(vm.ChordRepeatVerdict, line.Text);

                // Staged (#751 P4): the click disables No notes at once, the footer's Apply writes it.
                split.IsChecked = true;
                Settle(window);
                Assert.False(noNotes.IsEnabled);
                Assert.True(hint.IsVisible);
                Assert.False(string.IsNullOrWhiteSpace(hint.Text));
                Assert.True(hint.Bounds is { Width: > 1, Height: > 1 }, $"the reason laid out as {hint.Bounds}");
                Assert.False(chord.SplitIntoNotes, "a staged toggle wrote before Apply");

                Assert.True(vm.Staged.Engine.Apply(StagedPage.Technique));
                Settle(window);

                Assert.True(chord.ForceNoNotes, "the stored No notes flag must stay as it was");
                Assert.True(chord.SplitIntoNotes);
                Assert.False(vm.IsChordNoNotesEnabled);
                Assert.False(noNotes.IsEnabled);
                Assert.Equal(vm.ChordRepeatVerdict, line.Text);
                Assert.False(string.IsNullOrWhiteSpace(vm.ChordRepeatVerdict));
            }
            finally
            {
                selection.ClearSelections();
                window.Close();
                state.ActiveProject = previousProject;
                state.ActiveArrangement = previousArrangement;
                state.InspectorApplyStep = restoreApplyStep;
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);

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
