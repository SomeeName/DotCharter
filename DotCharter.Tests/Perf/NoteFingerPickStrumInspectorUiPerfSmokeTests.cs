using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using DotCharter.Localization;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #798: the left-hand finger and pick-direction rows of the note Technique section and the
///     strum row of the chord one (#751 P4: the Technique page's detail and whole-chord cards). Each view builds over
///     the app's own <see cref="InspectorViewModel" />,
///     attaches and MEASURES non-zero; the picker shows the selected sound's value, and an edit is STAGED until
///     Apply writes it through the history (one undo puts it back).
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
            var restoreApplyStep = state.InspectorApplyStep;
            state.InspectorApplyStep = true; // #805: the staged-until-Apply behaviour is opt-in

            var view = new SoundNoteDetailView { DataContext = vm };
            var window = Show(view);
            try
            {
                selection.ClearSelections();
                selection.SelectedNotes.Add(note);
                Settle(window);

                // The finger strip is "none", thumb, 1-4: finger N is segment N + 1 (#879).
                var finger = Named<WrapPanel>(view, "NoteFingerRow");
                var pick = Named<WrapPanel>(view, "NotePickDirectionRow");
                Assert.Equal(3, CheckedIndex(finger));
                Assert.Equal((int)PickDirectionType.Up, CheckedIndex(pick));
                foreach (var row in new Control[] { finger, pick })
                    Assert.True(row.Bounds is { Width: > 1, Height: > 1 }, $"{row.Name} laid out as {row.Bounds}");

                Click(window, finger, 1); // the thumb
                Click(window, pick, (int)PickDirectionType.Down);
                Settle(window);
                Assert.Equal(1, CheckedIndex(finger));
                Assert.Equal(2, note.LeftHandFinger);
                Assert.True(vm.Staged.Engine.Apply(DotCharter.UI.ViewModels.Editor.Inspector.StagedPage.Technique));
                Settle(window);
                Assert.Equal(0, note.LeftHandFinger);
                Assert.Equal(PickDirectionType.Down, note.PickDirection);

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
                state.InspectorApplyStep = restoreApplyStep;
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);

    [Fact]
    public async Task TheTechniqueStrips_LayOut_OneClickStagesEachValue_AndAMixedSelectionHighlightsNone() =>
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
            var note = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 3, Fret = 5 };
            var other = new GuitarNote { Position = new FractionalPosition(3), StringIndex = 3, Fret = 7, Mute = MuteType.Palm };
            level.Notes.Add(note);
            level.Notes.Add(other);
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            state.ActiveLevel = level;
            var restoreApplyStep = state.InspectorApplyStep;
            state.InspectorApplyStep = true;

            var view = new SoundNoteTechniqueView { DataContext = vm };
            var window = Show(view);
            try
            {
                selection.ClearSelections();
                selection.SelectedNotes.Add(note);
                Settle(window);

                var rows = new (string Name, int Segments)[]
                {
                    ("NoteMuteRow", 3), ("NoteHopoRow", 4), ("NoteHarmonicRow", 3), ("NotePickingRow", 3)
                };
                foreach (var (name, segments) in rows)
                {
                    var row = Named<WrapPanel>(view, name);
                    Assert.True(row.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {row.Bounds}");
                    Assert.Equal(segments, row.Children.OfType<ToggleButton>().Count());
                    Assert.Equal(0, CheckedIndex(row));
                }

                var staged = vm.Staged.NoteTechnique;
                // A selection that disagrees on a choice lights none of its segments.
                selection.SelectedNotes.Add(other);
                Settle(window);
                Assert.Equal(-1, staged.Mute);
                Assert.Equal(-1, CheckedIndex(Named<WrapPanel>(view, "NoteMuteRow")));
                Assert.Equal(0, CheckedIndex(Named<WrapPanel>(view, "NoteHopoRow")));

                selection.ClearSelections();
                selection.SelectedNotes.Add(note);
                Settle(window);

                // One click per value: each segment stages its own value on the model.
                for (var i = 0; i < 3; i++)
                {
                    Click(window, Named<WrapPanel>(view, "NoteMuteRow"), i);
                    Assert.Equal(i, staged.Mute);
                }

                for (var i = 0; i < 4; i++)
                {
                    Click(window, Named<WrapPanel>(view, "NoteHopoRow"), i);
                    Assert.Equal(i, staged.Hopo);
                }

                for (var i = 0; i < 3; i++)
                {
                    Click(window, Named<WrapPanel>(view, "NoteHarmonicRow"), i);
                    Assert.Equal(i, staged.Harmonic);
                    Click(window, Named<WrapPanel>(view, "NotePickingRow"), i);
                    Assert.Equal(i, staged.BassPicking);
                }

                // The highlight follows the value, and a second click on the lit segment keeps it lit.
                Click(window, Named<WrapPanel>(view, "NoteMuteRow"), 1);
                Click(window, Named<WrapPanel>(view, "NoteMuteRow"), 1);
                Settle(window);
                Assert.Equal(1, CheckedIndex(Named<WrapPanel>(view, "NoteMuteRow")));
                Click(window, Named<WrapPanel>(view, "NoteMuteRow"), 0);
                Click(window, Named<WrapPanel>(view, "NoteHopoRow"), 0);
                Click(window, Named<WrapPanel>(view, "NoteHarmonicRow"), 0);
                Click(window, Named<WrapPanel>(view, "NotePickingRow"), 0);

                // One Apply is one undo step.
                Click(window, Named<WrapPanel>(view, "NoteMuteRow"), 2);
                Assert.True(vm.Staged.Engine.Apply(DotCharter.UI.ViewModels.Editor.Inspector.StagedPage.Technique));
                Settle(window);
                Assert.Equal(MuteType.Palm, note.Mute);
                history.Undo();
                Settle(window);
                Assert.Equal(MuteType.None, note.Mute);
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

    /// <summary>#879: both note cards at the Inspector column's floor (320 px less the card's padding) in every shipped
    ///     culture: every segment is inside the pane (a row wraps rather than clips), and with
    ///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> the Technique page is captured per culture.</summary>
    [Fact]
    public async Task BothNoteCards_FitTheInspectorFloor_InEveryShippedCulture() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var vm = services.GetRequiredService<InspectorViewModel>();
            var previousProject = state.ActiveProject;
            var previousArrangement = state.ActiveArrangement;
            var restoreCulture = LocalizationService.Instance.ConfiguredCulture;

            var (project, arrangement, level) = Chart();
            var note = new GuitarNote
            {
                Position = new FractionalPosition(2), StringIndex = 3, Fret = 5, LeftHandFinger = 2,
                PickDirection = PickDirectionType.Down, Hopo = HopoType.HammerOn
            };
            level.Notes.Add(note);
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            state.ActiveLevel = level;
            try
            {
                foreach (var culture in new[] { "en-US", "de-DE", "ja-JP" })
                {
                    LocalizationService.Instance.SetConfiguredCulture(culture);
                    var techniques = new SoundNoteTechniqueView { DataContext = vm };
                    var detail = new SoundNoteDetailView { DataContext = vm };
                    var column = new StackPanel { Spacing = 8, Children = { techniques, detail } };
                    const double floor = 296;
                    var window = Show(column, floor);
                    try
                    {
                        selection.ClearSelections();
                        selection.SelectedNotes.Add(note);
                        Settle(window);
                        NarrowWidthLayout.Capture(window, $"inspector-technique-{culture}");
                        foreach (var name in new[] { "NoteMuteRow", "NoteHopoRow", "NoteHarmonicRow", "NotePickingRow" })
                            AssertInside(Named<WrapPanel>(techniques, name), window, floor, $"{name} in {culture}");
                        foreach (var name in new[] { "NoteFingerRow", "NotePickDirectionRow" })
                            AssertInside(Named<WrapPanel>(detail, name), window, floor, $"{name} in {culture}");
                    }
                    finally
                    {
                        selection.ClearSelections();
                        window.Close();
                    }
                }
            }
            finally
            {
                LocalizationService.Instance.SetConfiguredCulture(restoreCulture);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
                state.ActiveProject = previousProject;
                state.ActiveArrangement = previousArrangement;
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);

    private static void AssertInside(Panel row, Window window, double width, string at)
    {
        Assert.True(row.Bounds is { Width: > 1, Height: > 1 }, $"{at}: laid out as {row.Bounds}");
        foreach (var segment in row.Children.OfType<ToggleButton>())
        {
            var origin = segment.TranslatePoint(default, window) ?? throw new Xunit.Sdk.XunitException($"{at}: detached");
            Assert.True(segment.Bounds is { Width: > 1, Height: > 1 }, $"{at}: a segment laid out as {segment.Bounds}");
            Assert.True(origin.X >= 0 && origin.X + segment.Bounds.Width <= width + 0.5, $"{at}: a segment spans {origin.X}..{origin.X + segment.Bounds.Width} of {width}");
        }
    }

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
            var restoreApplyStep = state.InspectorApplyStep;
            state.InspectorApplyStep = true; // #805: the staged-until-Apply behaviour is opt-in

            var view = new SoundChordWholeView { DataContext = vm };
            var window = Show(view);
            try
            {
                selection.ClearSelections();
                selection.SelectedChords.Add(chord);
                Settle(window);

                var strum = Named<ComboBox>(view, "ChordStrumPicker");
                Assert.Equal((int)PickDirectionType.Up, strum.SelectedIndex);
                Assert.True(strum.Bounds is { Width: > 1, Height: > 1 }, $"the strum picker laid out as {strum.Bounds}");

                strum.SelectedIndex = (int)PickDirectionType.Down;
                Settle(window);
                Assert.Equal(PickDirectionType.Up, chord.Strum);
                Assert.True(vm.Staged.Engine.Apply(DotCharter.UI.ViewModels.Editor.Inspector.StagedPage.Technique));
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
                state.InspectorApplyStep = restoreApplyStep;
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);

    private static int CheckedIndex(Panel row) =>
        row.Children.OfType<ToggleButton>().ToList().FindIndex(b => b.IsChecked == true);

    // A real pointer press and release on the segment, so the toggle runs its own click path (and its command).
    private static void Click(Window window, Panel row, int segment)
    {
        var button = row.Children.OfType<ToggleButton>().ElementAt(segment);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
                    ?? throw new Xunit.Sdk.XunitException("the segment is not attached");
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

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

    private static Window Show(Control view, double width = 420)
    {
        var host = new UserControl { Content = view };
        host.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/"))
        {
            Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
        });
        var window = new Window { Width = width, Height = 900, Content = host };
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
