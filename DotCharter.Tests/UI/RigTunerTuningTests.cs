using DotCharter.Tone.Audio.Tuning;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The rig tuner's TUNING (<c>AppConfig.TunerTuning</c>): chromatic names
///     whatever it hears; a tuning judges each note against the NEAREST of its
///     strings, so a guitar in E standard retuning to Drop D reads its low string
///     as D2, sharp, and the chips show which strings are done. Real readings off
///     a sine, like <see cref="RigTunerReferenceTests" />.
/// </summary>
public sealed class RigTunerTuningTests
{
    private const int SampleRate = 48000;
    private const int WindowSize = 4096;

    private static float[] Sine(double hz)
    {
        var buf = new float[WindowSize];
        for (var i = 0; i < WindowSize; i++)
            buf[i] = 0.35f * MathF.Sin((float)(2.0 * Math.PI * hz * i / SampleRate));
        return buf;
    }

    [Fact]
    public void TheList_NamesEveryTuning_WithAUniqueId()
    {
        Assert.All(TunerTunings.All, t => Assert.NotEqual(t.Id, t.Name));   // the resolver knew it
        Assert.Equal(TunerTunings.All.Count, TunerTunings.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal(TunerTunings.All.Count, TunerTunings.All.Select(t => (t.IsBass, t.Name)).Distinct().Count());
        var guitar = TunerTunings.All.Where(t => !t.IsBass).Select(t => t.Name).ToList();
        foreach (var name in new[] { "E Standard", "Drop D", "DADGAD", "Drop C", "D Standard", "Open G" })
            Assert.Contains(name, guitar);
        Assert.All(TunerTunings.All, t => Assert.Equal(t.IsBass ? 4 : 6, t.Steps.Count));
        Assert.Null(TunerTunings.Find(TunerTunings.ChromaticId));
        Assert.Null(TunerTunings.Find("guitar:9,9,9,9,9,9"));
    }

    [Fact]
    public void DropD_JudgesTheLowEAgainstD2_AndMarksTheStringsHeardInTune()
    {
        var audio = new FakeAudioPlaybackService { InputSampleRate = SampleRate, InputCaptureWindow = Sine(82.41) };
        var tuner = new RigTunerViewModel(audio, FakeLocalization.English());
        var nowMs = 0.0;
        tuner.TickClock = () => nowMs += 66;             // the poll's ~15 Hz

        // chromatic: an E2 is an E2
        tuner.Tick();
        Assert.Equal("E2", tuner.NoteText);
        Assert.False(tuner.HasTuning);

        tuner.SelectedTuning = tuner.Tunings.Single(c => c.Tuning is { IsBass: false, Name: "Drop D" });
        Assert.True(tuner.HasTuning);
        Assert.Equal(["D", "A", "D", "G", "B", "E"], tuner.Strings.Select(s => s.Note));
        Assert.Equal("D A D G B E", tuner.SelectedTuning.Notes);

        // the same E2, against Drop D: the low string, two semitones sharp
        tuner.Tick();
        Assert.Equal("D2", tuner.NoteText);
        Assert.True(tuner.CentsOff > 20, $"an E2 against D2 read {tuner.CentsOff} ¢");
        Assert.True(tuner.Strings[0].IsCurrent);
        Assert.False(tuner.Strings[0].IsTuned);

        // tuned down to D2: in tune, and the chip says so. The needle eases over
        // time, so the judgement starts afresh by picking the tuning again.
        var dropD = tuner.SelectedTuning;
        tuner.SelectedTuning = tuner.Tunings[0];
        tuner.SelectedTuning = dropD;
        audio.InputCaptureWindow = Sine(73.42);
        for (var i = 0; i < 30 && !tuner.IsInTune; i++) tuner.Tick();   // two seconds of plucking at most
        Assert.True(tuner.IsInTune, $"a D2 against Drop D read {tuner.CentsOff} ¢");
        Assert.True(tuner.Strings[0].IsTuned);
        Assert.False(tuner.Strings[1].IsTuned);
    }

    [Fact]
    public void TheTuning_IsPersisted_AndAnUnknownIdReadsAsChromatic()
    {
        var config = new FakeConfigurationService();
        var tuner = new RigTunerViewModel(audio: null, routing: null, config: config, localization: FakeLocalization.English());
        Assert.Null(tuner.SelectedTuning.Tuning);

        var dadgad = tuner.Tunings.Single(c => c.Tuning is { IsBass: false, Name: "DADGAD" });
        tuner.SelectedTuning = dadgad;
        Assert.Equal(dadgad.Tuning!.Id, config.AppConfig.TunerTuning);
        Assert.Equal("DADGAD", new RigTunerViewModel(null, FakeLocalization.English(), null, config).SelectedTuning.Tuning?.Name);

        config.AppConfig.TunerTuning = "guitar:not-a-tuning";
        Assert.Null(new RigTunerViewModel(null, FakeLocalization.English(), null, config).SelectedTuning.Tuning);
    }
}
