using DotCharter.Analysis.AudioToChart;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tone.Audio.Tuning;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

public sealed class ReviewNoteSpellingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(61)]
    public void AudioFindings_UseArrangementSpelling(int? heard)
    {
        var arrangement = new Arrangement();
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(-1));
        var audio = new ChartAudioFinding(0, 56, 0.9f, heard, 0.8f);
        var finding = new QualityFinding(QualityChecks.AudioLint, QualitySeverity.Warning, "audio",
            arrangement, null, 0, null, null, null, audio);
        var message = QualityFindingText.Message(finding, FakeLocalization.English());
        Assert.Contains("Ab3", message, StringComparison.Ordinal);
        Assert.DoesNotContain("G#3", message, StringComparison.Ordinal);
        if (heard != null) Assert.Contains("Db4", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FakeSettings_ShowTheSharedSpelling(bool preset)
    {
        var screen = FakeArrangementSettingsScreen.Default();
        if (preset)
        {
            Assert.Contains("Eb Ab Db Gb Bb Eb", screen.TuningPresets.Single(p => p.Key == "Eb Standard").Label, StringComparison.Ordinal);
            return;
        }
        foreach (var row in screen.TuningOffsets) row.Step = -1;
        Assert.Equal(new[] { "Eb", "Ab", "Db", "Gb", "Bb", "Eb" }, screen.TuningOffsets.Select(r => r.ResultingNote));
    }

    [Theory]
    [InlineData("Bb Eb Ab Db Gb Bb Eb", 34, "Bb1")]
    [InlineData("Db E A D G B E", 37, "Db2")]
    public void PitchAgreesWithOpenLabels_WhenModelOffsetsFallBackToZero(string letters, int midi, string expected)
    {
        var arrangement = new Arrangement { StringCount = 7, TuningType = "CUSTOM", Tuning = letters.Split(' ') };
        Assert.All(Enumerable.Range(0, 7), i => Assert.Equal(0, TuningOffsetResolution.For(arrangement, i)));
        Assert.Equal(expected[..^1], TuningDisplayNames.For(arrangement)![0]);
        Assert.Equal(expected, TuningDisplayNames.Pitch(midi, arrangement, 0));
        Assert.Equal(expected, TuningDisplayNames.Pitch(midi, arrangement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RigTuner_UsesFlatNamesForLoweredTuning(bool reading)
    {
        const int sampleRate = 48000;
        var frequency = ArrangementTunerLogic.FrequencyOf(39);
        var wave = Enumerable.Range(0, 4096).Select(i => 0.35f * MathF.Sin((float)(2 * Math.PI * frequency * i / sampleRate))).ToArray();
        var audio = new FakeAudioPlaybackService { InputSampleRate = sampleRate, InputCaptureWindow = wave };
        var tuner = new RigTunerViewModel(audio, FakeLocalization.English());
        var clock = 0.0;
        tuner.TickClock = () => clock += 66;
        tuner.SelectedTuning = tuner.Tunings.Single(c => c.Tuning is { IsBass: false, Name: "Eb Standard" });
        if (reading)
        {
            tuner.Tick();
            Assert.True(tuner.HasReading);
            Assert.Equal("Eb2", tuner.NoteText);
            return;
        }
        Assert.Equal("Eb Ab Db Gb Bb Eb", tuner.SelectedTuning.Notes);
        Assert.Equal(new[] { "Eb", "Ab", "Db", "Gb", "Bb", "Eb" }, tuner.Strings.Select(s => s.Note));
    }
}
