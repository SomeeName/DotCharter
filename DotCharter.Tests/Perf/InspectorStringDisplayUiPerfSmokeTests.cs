using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class InspectorStringDisplayUiPerfSmokeTests
{
    [Fact]
    public Task SoundGrid_FollowsBothOrdersLive_AndCapturesEFlat() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services!;
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var vm = services.GetRequiredService<InspectorViewModel>();
            var previousProject = state.ActiveProject;
            var previousArrangement = state.ActiveArrangement;
            var previousLevel = state.ActiveLevel;
            var previousInvert = state.InvertStrings;
            var arrangement = new Arrangement { StringCount = 6, TuningType = "E_FLAT_STANDARD", Tuning = ["Eb", "G#", "C#", "F#", "Bb", "Eb"] };
            for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset { Step = -1 });
            var level = new ChartLevel();
            arrangement.Levels.Add(level);
            var template = new ChordTemplate { Id = 0, Name = "Ab", Frets = { [0] = 5, [1] = 7, [2] = 7, [3] = 6, [4] = 5, [5] = 5 } };
            arrangement.ChordTemplates.Add(template);
            var chord = new Chord { TemplateId = 0, Position = new FractionalPosition(2) };
            foreach (var (s, fret) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
            level.Chords.Add(chord);
            var project = new ChartProject();
            project.Arrangements.Add(arrangement);
            for (var i = 0; i < 8; i++) project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120 });
            var view = new SoundShapeView { DataContext = vm };
            var window = new Window { Width = 520, Height = 1000, Content = view };
            window.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/")) { Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml") });
            try
            {
                state.ActiveProject = project;
                state.ActiveArrangement = arrangement;
                state.ActiveLevel = level;
                selection.ClearSelections();
                selection.SelectedChords.Add(chord);
                window.Show();
                vm.Sections.ChooseCommand.Execute(SoundSection.Shape);
                var arrowsFollow = true;
                foreach (var invert in new[] { false, true, false })
                {
                    state.InvertStrings = invert;
                    Settle(window);
                    // The Shape page's grid (#751 design D): the chord's fret box (Note) of each string row.
                    var rows = view.GetLogicalDescendants().OfType<ItemsControl>().Single(c => c.Name == "SoundShapeRowList");
                    Assert.True(rows.Bounds is { Width: > 1, Height: > 1 });
                    var boxes = rows.GetLogicalDescendants().OfType<TextBox>().Where(b => Grid.GetColumn(b) == 1).ToArray();
                    Assert.Equal(6, boxes.Length);
                    var expected = invert ? Enumerable.Range(0, 6).Reverse() : Enumerable.Range(0, 6);
                    Assert.Equal(expected, boxes.Select(b => ((GripRow)b.DataContext!).StringIndex));
                    Assert.Equal("Eb", ((GripRow)boxes[invert ? 5 : 0].DataContext!).Label.Split(' ')[1]);
                    vm.GripInspector.ShowPageCommand.Execute("1");
                    Settle(window);
                    var suggested = view.GetLogicalDescendants().OfType<Button>()
                        .Where(button => ReferenceEquals(button.Command, vm.GripInspector.UseNameCommand))
                        .Select(button => ((GripNameSuggestion)button.CommandParameter!).Name);
                    Assert.Equal(new[] { "Ab" }, suggested);
                    Assert.Equal("Ab", template.Name);
                    vm.GripInspector.ShowPageCommand.Execute("0");
                    Settle(window);
                    var dir = Environment.GetEnvironmentVariable("DOTCHARTER_729_CAPTURE_DIR");
                    if (dir != null)
                    {
                        Directory.CreateDirectory(dir);
                        using var frame = window.CaptureRenderedFrame();
                        Assert.NotNull(frame);
                        frame.Save(Path.Combine(dir, invert ? "inspector-tab.png" : "inspector-rocksmith.png"), new PngBitmapEncoderOptions());
                    }
                    boxes[0].Focus();
                    window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                    window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                    arrowsFollow &= boxes[1].IsFocused;
                    boxes[1].Focus();
                    window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                    window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                    arrowsFollow &= boxes[0].IsFocused;
                    boxes[0].Focus();
                    for (var step = 0; step < 20 && !boxes[1].IsFocused; step++)
                    {
                        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                        window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                    }
                    Assert.True(boxes[1].IsFocused, "Tab reaches the next fret box in visual order");
                    Assert.Equal(expected, boxes.OrderBy(b => b.TranslatePoint(default, rows)!.Value.Y)
                        .Select(b => ((GripRow)b.DataContext!).StringIndex));
                }
                Assert.True(arrowsFollow, "Up/Down must follow the visually displayed string order");
            }
            finally
            {
                selection.ClearSelections();
                window.Close();
                state.InvertStrings = previousInvert;
                state.ActiveProject = previousProject;
                state.ActiveArrangement = previousArrangement;
                state.ActiveLevel = previousLevel;
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);

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
