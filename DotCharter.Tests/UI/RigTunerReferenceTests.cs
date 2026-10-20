using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The tuner's CONCERT PITCH (<c>AppConfig.TunerReferenceHz</c>): A4 = 440 by
///     default, 415..466 clamped, persisted per install — and load-bearing all the
///     way through the reading, because a needle that only knows 440 tells anyone
///     playing at A442 that every string is eight cents sharp.
///     <para>
///         The pure half asserts the math (a 442 reference reports 442 Hz as
///         exactly in tune), the view-model half asserts the READING the panel
///         prints and the value that reached the config — never the property alone.
///     </para>
/// </summary>
public sealed class RigTunerReferenceTests
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

    // ── the pure math ────────────────────────────────────────────────────────

    [Fact]
    public void At442_TheAAt442Hz_IsExactlyInTune()
    {
        const double reference = 442.0;

        var midi = RigTunerMath.NearestMidi(442.0, reference);
        Assert.Equal(69, midi);                                    // still an A4
        Assert.Equal(0.0, RigTunerMath.CentsOff(442.0, midi, reference), 6);
        Assert.Equal(442.0, RigTunerMath.FrequencyOfMidi(69, reference), 6);

        // …and the SAME pitch at the standard reference is sharp by the interval
        // between the two references, which is the whole reason this exists.
        var standardCents = RigTunerMath.CentsOff(442.0, 69);
        Assert.True(standardCents is > 7 and < 8, $"expected ~+7.85 cents, got {standardCents}");
    }

    [Fact]
    public void TheDefaultOverloads_AreThe440OnesUnchanged()
    {
        Assert.Equal(RigTunerMath.NearestMidi(440.0, 440.0), RigTunerMath.NearestMidi(440.0));
        Assert.Equal(RigTunerMath.FrequencyOfMidi(64, 440.0), RigTunerMath.FrequencyOfMidi(64), 9);
        Assert.Equal(RigTunerMath.CentsOff(330.0, 64, 440.0), RigTunerMath.CentsOff(330.0, 64), 9);
        Assert.Equal(440.0, RigTunerMath.DefaultReferenceHz);
        Assert.Equal(-1, RigTunerMath.NearestMidi(0.0, 442.0));
    }

    [Fact]
    public void TheReference_IsClampedAndNeverNonFinite()
    {
        Assert.Equal(RigTunerMath.MinReferenceHz, RigTunerMath.ClampReference(300.0));
        Assert.Equal(RigTunerMath.MaxReferenceHz, RigTunerMath.ClampReference(500.0));
        Assert.Equal(442.0, RigTunerMath.ClampReference(442.0));
        Assert.Equal(440.0, RigTunerMath.ClampReference(double.NaN));
        Assert.Equal(440.0, RigTunerMath.ClampReference(double.PositiveInfinity));

        // …and a caller that reaches past the clamp still cannot move the grid.
        Assert.Equal(
            RigTunerMath.FrequencyOfMidi(69, RigTunerMath.MaxReferenceHz),
            RigTunerMath.FrequencyOfMidi(69, 900.0), 9);
    }

    // ── the panel ────────────────────────────────────────────────────────────

    [Fact]
    public void TheNeedle_JudgesAgainstThePlayersReference()
    {
        var audio = new FakeAudioPlaybackService
        {
            InputSampleRate = SampleRate,
            InputCaptureWindow = Sine(442.0)
        };
        var tuner = new RigTunerViewModel(audio, FakeLocalization.English());

        // A440: an A string sitting at 442 Hz reads sharp.
        tuner.Tick();
        Assert.Equal("A4", tuner.NoteText);
        Assert.True(tuner.CentsOff > 5, $"expected a sharp reading at A440, got {tuner.CentsOff}");
        Assert.False(tuner.IsInTune);

        // A442: the same string is in tune, and the note is unchanged.
        tuner.ReferenceHz = 442.0;
        tuner.Tick();
        Assert.Equal("A4", tuner.NoteText);
        Assert.True(Math.Abs(tuner.CentsOff) < 2, $"expected ~0 cents at A442, got {tuner.CentsOff}");
    }

    [Fact]
    public void ChangingTheReference_DropsTheStaleReading()
    {
        var audio = new FakeAudioPlaybackService
        {
            InputSampleRate = SampleRate,
            InputCaptureWindow = Sine(440.0)
        };
        var tuner = new RigTunerViewModel(audio, FakeLocalization.English());
        tuner.Tick();
        Assert.True(tuner.HasReading);

        tuner.ReferenceHz = 444.0;

        // A different target is a different judgement: an "in tune" needle left
        // over from the previous reference is the same lie as one left over from
        // another seat's guitar.
        Assert.False(tuner.HasReading);
        Assert.False(tuner.IsInTune);
        Assert.Equal("–", tuner.NoteText);
    }

    [Fact]
    public void TheReference_IsClampedPersistedAndRestored()
    {
        var config = new FakeConfigurationService();
        var tuner = new RigTunerViewModel(audio: null, routing: null, config: config, localization: FakeLocalization.English());

        Assert.Equal(440.0, tuner.ReferenceHz);
        Assert.True(tuner.IsStandardReference);

        tuner.ReferenceHz = 442.0;
        Assert.Equal(442.0, config.AppConfig.TunerReferenceHz);
        Assert.False(tuner.IsStandardReference);

        // A hand-edited config, or a control that overshoots, is clamped both ways.
        tuner.ReferenceHz = 900.0;
        Assert.Equal(RigTunerMath.MaxReferenceHz, tuner.ReferenceHz);
        tuner.ReferenceHz = 100.0;
        Assert.Equal(RigTunerMath.MinReferenceHz, tuner.ReferenceHz);

        config.AppConfig.TunerReferenceHz = 443.0;
        Assert.Equal(443.0, new RigTunerViewModel(null, FakeLocalization.English(), null, config).ReferenceHz);

        // …including on the way IN: a config holding nonsense opens on standard
        // pitch rather than on a needle nothing tunes to.
        config.AppConfig.TunerReferenceHz = 12.0;
        Assert.Equal(RigTunerMath.MinReferenceHz, new RigTunerViewModel(null, FakeLocalization.English(), null, config).ReferenceHz);
    }

    [Fact]
    public void TheLabel_NamesThePitch_AndFollowsIt()
    {
        var tuner = new RigTunerViewModel(audio: null, localization: FakeLocalization.English());
        Assert.Contains("440", tuner.ReferenceLabel);

        var raised = new List<string?>();
        tuner.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        tuner.ReferenceHz = 441.0;

        Assert.Contains("441", tuner.ReferenceLabel);
        Assert.Contains(nameof(RigTunerViewModel.ReferenceLabel), raised);
        Assert.Contains(nameof(RigTunerViewModel.ReferenceHz), raised);
    }

    [Fact]
    public void ATunerWithNoConfig_TunesToStandardPitch_AndRemembersNothing()
    {
        var tuner = new RigTunerViewModel(audio: null, localization: FakeLocalization.English());
        Assert.Equal(440.0, tuner.ReferenceHz);
        tuner.ReferenceHz = 442.0;   // must not throw with no service behind it
        Assert.Equal(442.0, tuner.ReferenceHz);
    }
}
