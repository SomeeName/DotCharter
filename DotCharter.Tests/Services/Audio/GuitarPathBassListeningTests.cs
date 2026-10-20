using System.Collections.ObjectModel;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Tuner;
using DotCharter.Services.Import.Audio;
using DotCharter.Tests.Core;
using DotCharter.Tone.Audio.Tuning;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     Everything that LISTENS to, names or measures a 5/6-string bass charted on a
///     guitar path at A220 (#161): live judging, the tuner, the guided tuning check
///     and the timeline gutter, the drift hint, the chart-from-audio string map, and
///     the tone half of the bass-part decision.
/// </summary>
public sealed class GuitarPathBassListeningTests
{
    [Fact]
    public void Live_judging_expects_the_bass_register_not_the_guitar_one()
    {
        var five = GuitarPathBassShapes.FiveStringBassOnBonusRhythm();
        var six = GuitarPathBassShapes.SixStringBassOnAltRhythm();

        Assert.Equal(23, LivePitchAcceptance.SoundingMidi(five, 0, 0)); // B0
        Assert.Equal(43, LivePitchAcceptance.SoundingMidi(five, 4, 0)); // G2
        Assert.Equal(23, LivePitchAcceptance.SoundingMidi(six, 0, 0));
        Assert.Equal(48, LivePitchAcceptance.SoundingMidi(six, 5, 0)); // C3
        Assert.Equal(40, LivePitchAcceptance.SoundingMidi(GuitarPathBassShapes.GuitarRhythm(-50), 0, 0));
        Assert.Equal(28, LivePitchAcceptance.SoundingMidi(GuitarPathBassShapes.BassPath(), 0, 0));
        // A Bass path at A220 tuned up a fifth is a B-E-A-D bass: B0 as well.
        Assert.Equal(23, LivePitchAcceptance.SoundingMidi(GuitarPathBassShapes.BassPathAtA220(), 0, 0));
    }

    [Fact]
    public void Chart_from_audio_maps_a_guitar_path_bass_onto_its_real_open_strings() =>
        Assert.Equal([23, 28, 33, 38, 43, 48],
            ChartFromAudioOptions.OpenStringMidiFor(GuitarPathBassShapes.SixStringBassOnAltRhythm()));

    [Fact]
    public void The_tuner_names_B0_and_aims_at_B0()
    {
        var logic = new ArrangementTunerLogic();
        logic.Load(GuitarPathBassShapes.SixStringBassOnAltRhythm());

        Assert.Equal(["B0", "E1", "A1", "D2", "G2", "C3"], logic.Targets.Select(t => t.NoteName).ToArray());
        Assert.Equal(ArrangementTunerLogic.FrequencyOf(23), logic.Targets[0].FrequencyHz, 6);
        Assert.Equal(30.87, logic.Targets[0].FrequencyHz, 2);

        // A player on that pitch is in tune, on the low string, at 0 cents.
        var frame = logic.Process(ArrangementTunerLogic.FrequencyOf(23), 10);
        Assert.Equal(0, frame.NearestString);
        Assert.Equal(0.0, frame.CentsOff, 6);
        Assert.True(frame.InTune);
    }

    [Fact]
    public void The_tuning_check_and_gutter_name_the_bass_strings()
    {
        var targets = TuningCheckTargets.Build(GuitarPathBassShapes.FiveStringBassOnBonusRhythm());

        Assert.Equal(["B0", "E1", "A1", "D2", "G2", "E3"], targets.Select(t => t.NoteName).ToArray());
        Assert.Equal(ArrangementTunerLogic.FrequencyOf(23), targets[0].FrequencyHz, 6);
    }

    [Fact]
    public void A_guitar_and_a_bass_path_keep_their_tuner_names()
    {
        Assert.Equal("E2", TuningCheckTargets.Build(GuitarPathBassShapes.GuitarRhythm())[0].NoteName);
        Assert.Equal("E1", TuningCheckTargets.Build(GuitarPathBassShapes.BassPath())[0].NoteName);
    }

    [Theory]
    [InlineData(-1200.0, 0.0)]
    [InlineData(15.0, 15.0)]
    [InlineData(-45.0, -45.0)]
    [InlineData(65.0, -35.0)]
    public void The_drift_hint_compares_only_what_a_semitone_reading_can_see(double offset, double residual) =>
        Assert.Equal(residual, LiveTuningDriftMonitor.NearestSemitoneResidual(offset), 9);

    [Fact]
    public void An_A220_chart_played_in_tune_is_not_drift()
    {
        var strings = FakeLocalization.English();
        var monitor = new LiveTuningDriftMonitor(strings, new FakeAppLogger());
        monitor.Reset(centOffset: -1200);

        var hints = new List<string>();
        for (var i = 0; i < 24; i++)
        {
            var window = monitor.BeginCheck(i * LiveTuningDriftMonitor.DriftCheckIntervalMs, 48_000);
            Assert.NotNull(window);
            for (var n = 0; n < window!.Length; n++) window[n] = (float)(0.5 * Math.Sin(2.0 * Math.PI * 110.0 * n / 48_000));
            if (monitor.CompleteCheck(window.Length) is { } hint) hints.Add(hint);
        }

        // The one verdict change is to "in tune", whose hint is empty — the same
        // answer an A440 chart gets (LiveTuningDriftMonitorTests).
        Assert.Equal([string.Empty], hints);
    }

    private static ToneDefinition Tone(string key, string amp, params string[] descriptors) => new()
    {
        Key = key,
        Name = key,
        Json = "{\"Key\":\"" + key + "\",\"Name\":\"" + key + "\",\"ToneDescriptors\":[" +
               string.Join(",", descriptors.Select(d => "\"" + d + "\"")) +
               "],\"GearList\":{\"Amp\":{\"Type\":\"Amps\",\"Key\":\"" + amp + "\",\"KnobValues\":{}}}}",
    };

    private static (ChartProject Project, Arrangement Arrangement) WithTones(params ToneDefinition[] tones)
    {
        var arrangement = GuitarPathBassShapes.GuitarRhythm();
        arrangement.ToneBase = tones[0].Key;
        arrangement.ToneNames = new ObservableCollection<string>(tones.Select(t => t.Key));
        var project = new ChartProject();
        foreach (var tone in tones) project.ToneDefinitions.Add(tone);
        project.Arrangements.Add(arrangement);
        return (project, arrangement);
    }

    [Fact]
    public void A_guitar_path_at_A440_whose_tones_are_all_bass_tones_is_a_bass_part()
    {
        var (project, arrangement) = WithTones(
            Tone("bass_clean", "Bass_Amp_CH350B", ArrangementBassTones.BassDescriptor),
            Tone("bass_drive", "Bass_Amp_HT400B", ArrangementBassTones.BassDescriptor, "$[35716]OVERDRIVE"));

        Assert.True(ArrangementBassTones.AreBassGear(project, arrangement));
        Assert.True(ArrangementBassTones.IsBassPart(project, arrangement));
        Assert.False(ArrangementBassTones.IsBassPart(null, arrangement));
    }

    [Fact]
    public void One_bass_tone_among_guitar_tones_stays_guitar()
    {
        var (project, arrangement) = WithTones(
            Tone("crunch", "Amp_MarshallJCM800", "$[35722]DISTORTION"),
            Tone("synth_bass", "Bass_Amp_LT25B", ArrangementBassTones.BassDescriptor, "$[35750]SPECIAL EFFECT"));

        Assert.False(ArrangementBassTones.IsBassPart(project, arrangement));
    }

    [Fact]
    public void A_bass_head_without_the_bass_descriptor_or_the_descriptor_on_a_guitar_amp_stays_guitar()
    {
        var (soloProject, solo) = WithTones(Tone("solo", "Bass_Amp_LT85B", "$[35720]CLEAN"));
        Assert.False(ArrangementBassTones.IsBassPart(soloProject, solo));
        var (project, arrangement) = WithTones(Tone("b_std", "Amp_MarshallJCM800", ArrangementBassTones.BassDescriptor));
        Assert.False(ArrangementBassTones.IsBassPart(project, arrangement));
    }

    [Fact]
    public void A_tone_is_measured_off_the_bass_stem_for_a_bass_part()
    {
        Assert.Equal(ToneStems.Bass, AiToneGenerationRun.StemFor(GuitarPathBassShapes.SixStringBassOnAltRhythm()));
        Assert.Equal(ToneStems.Guitar, AiToneGenerationRun.StemFor(GuitarPathBassShapes.GuitarRhythm()));

        var (project, arrangement) = WithTones(Tone("bass", "Bass_Amp_CH350B", ArrangementBassTones.BassDescriptor));
        Assert.Equal(ToneStems.Bass, AiToneGenerationRun.StemFor(arrangement, project));
    }
}
