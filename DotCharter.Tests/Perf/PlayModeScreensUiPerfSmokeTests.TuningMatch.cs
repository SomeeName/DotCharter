// Get Ready's "Play in my tuning" row (#312): constructed, attached and laid out on a real chart, the switch live on a path it can shift and disabled with its reason on one it cannot, and an optional PNG of each.

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

public sealed partial class PlayModeScreensUiPerfSmokeTests
{
    /// <summary>
    ///     The row must MEASURE, not just exist: a style that failed to resolve still arranges a
    ///     zero-size switch. On the E♭ path the switch is live and says how far the song moves;
    ///     on the Drop D path beside it the switch is off and disabled, and the reason is printed.
    ///     With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set, both
    ///     states are written as PNGs.
    /// </summary>
    [Fact]
    public async Task GetReady_PlayInMyTuning_IsLiveOnAUniformPath_AndDisabledWithItsReasonOnAnother()
    {
        await WithView((view, vm) =>
        {
            EnterGetReady(vm, view);
            var row = Find(view, "GetReadyTuningMatch");
            row.BringIntoView();
            view.UpdateLayout();

            AssertLaidOut(view, "GetReadyTuningMatch");
            AssertLaidOut(view, "GetReadyTuningMatchToggle");
            AssertLaidOut(view, "GetReadyMyTuning");
            AssertLaidOut(view, "GetReadyTuningMatchStatus");
            AssertContains(Find(view, "GetReadyPathPanel"), row, "Play in my tuning row");

            var toggle = Find<ToggleSwitch>(view, "GetReadyTuningMatchToggle");
            Assert.True(toggle.IsEffectivelyEnabled, "the E♭ path can be shifted, so the switch must be live");
            Assert.Contains("+1", Find<TextBlock>(view, "GetReadyTuningMatchLabel").Text);

            vm.TuningMatch.IsOn = true;
            view.UpdateLayout();
            Assert.True(toggle.IsChecked);
            Assert.Equal(vm.TuningMatch.MyTuningName, Find<TextBlock>(view, "GetReadyTuningLockup").Text);
            Assert.Equal(vm.TuningMatch.MyTuningName, Find<TextBlock>(view, "GetReadyExpectedTuning").Text);
            AssertLaidOut(view, "GetReadyExpectedTuningCaption");
            Capture(view, "getready-tuningmatch-on.png");

            vm.SelectedFlowArrangement = vm.FlowArrangements[1];
            view.UpdateLayout();

            Assert.False(toggle.IsEffectivelyEnabled, "Drop D against E standard cannot be shifted");
            Assert.False(toggle.IsChecked);
            AssertLaidOut(view, "GetReadyTuningMatchStatus");
            Assert.Equal(vm.TuningMatch.StatusText, Find<TextBlock>(view, "GetReadyTuningMatchStatus").Text);
            Assert.False(string.IsNullOrWhiteSpace(vm.TuningMatch.StatusText));
            Capture(view, "getready-tuningmatch-refused.png");
        }, seedProject: SeedTuningMatchChart);
    }

    private static void Capture(PlayModeView view, string fileName)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        if (TopLevel.GetTopLevel(view) is not { } top) return;
        Directory.CreateDirectory(dir);
        using var frame = top.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(dir, fileName), new PngBitmapEncoderOptions());
    }

    /// <summary>An E♭-standard lead and a Drop D rhythm, so one path can be shifted to an
    /// E-standard player and the other cannot.</summary>
    private static void SeedTuningMatchChart(ProjectState state)
    {
        var project = new ChartProject();
        foreach (var (name, type, steps) in new[]
                 {
                     ("Lead", ArrangementType.Lead, new[] { -1, -1, -1, -1, -1, -1 }),
                     ("Rhythm", ArrangementType.Rhythm, new[] { -2, 0, 0, 0, 0, 0 })
                 })
        {
            var arrangement = new Arrangement { Name = name, ArrangementType = type, StringCount = 6 };
            foreach (var step in steps) arrangement.TuningPitches.Add(new TuningOffset(step));
            var level = new ChartLevel { DifficultyIndex = 0 };
            for (var i = 0; i < 12; i++)
                level.Notes.Add(new GuitarNote { StringIndex = i % 6, Fret = i % 5 });
            arrangement.Levels.Add(level);
            project.Arrangements.Add(arrangement);
        }

        state.ActiveProject = project;
        state.ActiveArrangement = project.Arrangements[0];
    }
}
