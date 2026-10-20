using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tests.UI.ViewModels.Editor;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     #627: while "Play in my tuning" (#312) shifts the song, the highway's string gutter prints
///     the tuning the GUITAR is in (the chart's plus the shift), and goes back to the chart's when
///     the shift ends. The shift state is <c>LivePlayViewModel.TransposeSemitones</c>, bound to
///     <c>TimelineGutterControl.TransposeSemitones</c> in <c>MainWindow.axaml</c>.
/// </summary>
public sealed class GutterTuningShiftLabelTests
{
    private static Arrangement EStandardGuitar()
    {
        int[] steps = [0, 0, 0, 0, 0, 0];
        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            StringCount = 6,
            TuningType = "E_STANDARD",
            Tuning = TuningUtilities.GetTuningNames(6, steps)
        };
        foreach (var step in steps) arrangement.TuningPitches.Add(new TuningOffset(step));
        return arrangement;
    }

    [Fact]
    public void TheNames_FollowTheShiftOnAndOff()
    {
        var chart = EStandardGuitar();

        Assert.Equal(["E", "A", "D", "G", "B", "E"], TimelineGutterControl.StringNamesFor(chart, 6));
        Assert.Equal(["F", "A#", "D#", "G#", "C", "F"], TimelineGutterControl.StringNamesFor(chart, 6, 1));
        Assert.Equal(["F#", "B", "E", "A", "C#", "F#"], TimelineGutterControl.StringNamesFor(chart, 6, 2));
        Assert.Equal(["E", "A", "D", "G", "B", "E"], TimelineGutterControl.StringNamesFor(chart, 6, 0));
    }

    [Fact]
    public void TheStamp_MovesWithTheShift_SoAShiftRepaintsTheNames()
    {
        var chart = EStandardGuitar();
        var off = TimelineGutterControl.TuningStampFor(chart);

        Assert.NotEqual(off, TimelineGutterControl.TuningStampFor(chart, 1));
        Assert.Equal(off, TimelineGutterControl.TuningStampFor(chart, 0));
    }

    [Fact]
    public void TheShiftState_RaisesPropertyChanged_SoTheBindingFollowsIt()
    {
        var vm = LivePlayTestFactory.Create(new FakeAudioPlaybackService { InputSampleRate = 48_000 },
            new ProjectState(), new FakeAppLogger(), new FakeConfigurationService(),
            new PracticeProfileService(new FakeAppLogger(),
                Path.Combine(Path.GetTempPath(), "dc-gutshift-" + Guid.NewGuid().ToString("N"), "never-written.json")));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.TransposeSemitones = 2;
        vm.TransposeSemitones = 0;

        Assert.Equal(["TransposeSemitones", "TransposeSemitones"], raised);
    }

    /// <summary>Anti-vacuity lint: the gutter on the main window must be wired to the shift state,
    /// and must hand it to the name table.</summary>
    [Fact]
    public void TheMainWindowGutter_IsBoundToTheShift_AndTheControlPassesItOn()
    {
        var root = TestPaths.RepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "DotCharter.UI", "Views", "MainWindow.axaml"));
        Assert.Contains("TransposeSemitones=\"{Binding LivePlay.TransposeSemitones}\"", xaml, StringComparison.Ordinal);

        var control = File.ReadAllText(Path.Combine(root, "DotCharter.UI", "Controls", "TimelineGutterControl.cs"));
        Assert.Contains("StringNamesFor(Editor?.ActiveArrangement, count, TransposeSemitones)", control,
            StringComparison.Ordinal);
    }
}
