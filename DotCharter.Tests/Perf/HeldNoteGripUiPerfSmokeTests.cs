using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Services.Editing.HandShapes;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.Editor.Inspector;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class HeldNoteGripUiPerfSmokeTests
{
    [Fact]
    public Task TheInspectorShowsTheReusedFirstGripOverItsOpeningChord() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var chart = new HeldNoteGripsTests.Fixture();
            var run = chart.AddRun(0);
            var grip = new ChordTemplate
            {
                Id = 38, Frets = { [1] = 0, [2] = 7, [3] = 5, [4] = 5 },
                Fingers = { [2] = 3, [3] = 1, [4] = 1 }
            };
            chart.Arrangement.ChordTemplates.Add(grip);
            run[0].TemplateId = grip.Id;
            var vm = CreateEditor(chart);
            try
            {
                chart.Apply(run);
                Dispatcher.UIThread.RunJobs();

                Assert.Same(grip, vm.Template);
                Assert.Equal("5", vm.StringInputs[3].FretText);
                Assert.Equal("5", vm.StringInputs[4].FretText);

                foreach (var guitarString in new[] { 3, 4 })
                {
                    var edit = GhostGems.SetGhost(chart.Arrangement, chart.Level, run[0], guitarString, false);
                    Assert.True(chart.History.Do(edit.Action!));
                }
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(0, chart.Mask(run[0]));
                Assert.Equal("x0755x", chart.Frets(run[0]));
                Assert.Equal("5", vm.StringInputs[3].FretText);
                Assert.Equal("5", vm.StringInputs[4].FretText);
            }
            finally
            {
                chart.Selection.ClearSelections();
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);

    [Fact]
    public Task TheInspectorFollowsTheFilledRunAndSavesItsGhostFingers() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var chart = new HeldNoteGripsTests.Fixture();
            var run = chart.AddRun(0);
            var vm = CreateEditor(chart);
            var view = new SoundLibraryTemplateView { DataContext = vm };
            var window = new Window { Width = 460, Height = 900, Content = view };
            window.Show();
            try
            {
                chart.Selection.SelectedHandShapes.Add(run[2]);
                Capture(window, "inspector-before.png");
                chart.Apply([run[2], run[0], run[1], run[3], run[4]]);
                Capture(window, "inspector-after.png");
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 });
                Assert.Same(chart.Grip(run[2]), vm.Template);
                Assert.Equal("7", vm.StringInputs[2].FretText);
                var original = chart.Grip(run[0]);
                var finger = original.GetFinger(2) == 4 ? 3 : 4;

                // The fill leaves the whole run selected; "This chord" keeps the save on the inspected bar
                // (the Selected rung would reach every selected bar, #751 phase 6).
                vm.ApplyRung = ChordApplyRung.ThisChord;
                vm.StringInputs[2].FingerText = finger.ToString();
                vm.SaveFingerEditsCommand.Execute(null);

                Assert.Equal("x0755x", chart.Frets(run[2]));
                Assert.Equal(0b01110, chart.Mask(run[2]));
                Assert.Equal(finger, chart.Grip(run[2]).GetFinger(2));
                Assert.Same(original, chart.Grip(run[0]));
                Assert.NotSame(original, chart.Grip(run[2]));
            }
            finally
            {
                chart.Selection.ClearSelections();
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);

    private static ChordTemplateEditorViewModel CreateEditor(HeldNoteGripsTests.Fixture chart)
    {
        var state = new ProjectState
        {
            ActiveProject = chart.Project, ActiveArrangement = chart.Arrangement, ActiveLevel = chart.Level
        };
        var logger = new FakeAppLogger();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, new FakeConfigurationService(),
            FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, chart.History, chart.Selection,
            new TempoService(logger), new TrackContextRegistry(state, chart.Selection), FakeLocalization.KeysOnly());
        return new ChordTemplateEditorViewModel(editor, chart.History, new FingeringGenerationService(),
            FakeLocalization.English());
    }

    private static void Capture(Window window, string file)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_HELD_GRIPS_CAPTURE");
        if (string.IsNullOrWhiteSpace(dir)) return;

        Directory.CreateDirectory(dir);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
