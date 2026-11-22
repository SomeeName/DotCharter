using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     #376: <see cref="SoundingPitch" /> is the ONE pitch rule; live judging, the audio lint and
///     note names ask it. Pitch-to-fret round-trips it.
/// </summary>
public sealed class SoundingPitchAgreementTests
{
    private static Arrangement Make(ArrangementType type, int strings, double cents = 0, int capo = 0)
    {
        var a = new Arrangement { ArrangementType = type, StringCount = strings, CentOffset = cents, Capo = capo };
        for (var i = 0; i < strings; i++) a.TuningPitches.Add(new TuningOffset(0));
        return a;
    }

    public static TheoryData<ArrangementType, int, double> Instruments => new()
    {
        { ArrangementType.Lead, 6, 0 },
        { ArrangementType.Lead, 7, 0 },
        { ArrangementType.Lead, 8, 0 },
        { ArrangementType.Bass, 4, 0 },
        { ArrangementType.Bass, 5, 0 },
        { ArrangementType.Lead, 5, -1200 },
        { ArrangementType.Lead, 6, -1200 },
    };

    [Theory]
    [MemberData(nameof(Instruments))]
    public void Live_judging_and_SoundingPitch_name_the_same_pitch_for_every_string_fret_and_capo(ArrangementType type, int strings, double cents)
    {
        foreach (var capo in new[] { 0, 2 })
        {
            var a = Make(type, strings, cents, capo);
            for (var s = 0; s < strings; s++)
            {
                foreach (var fret in new[] { 0, 1, 3, 5 })
                {
                    var live = LivePitchAcceptance.SoundingMidi(a, s, fret);
                    var expected = SoundingPitch.MidiFor(a, s, fret) is { } m
                        ? m + ArrangementInstrument.ReferenceOctaveSemitones(cents)
                        : (int?)null;
                    Assert.True(expected == live, $"{type} {strings}-string cents {cents} capo {capo} string {s} fret {fret}: live {live} vs SoundingPitch {expected}");
                    Assert.Equal(expected, SoundingPitch.SoundingMidiFor(a, s, fret));
                    if (expected is { } sounding) Assert.Equal(SoundingPitch.NameFor(sounding), SoundingPitch.NameFor(a, s, fret));
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Instruments))]
    public void FretForPitch_round_trips_MidiFor(ArrangementType type, int strings, double cents)
    {
        foreach (var capo in new[] { 0, 2 })
        {
            var a = Make(type, strings, cents, capo);
            for (var s = 0; s < strings; s++)
            {
                foreach (var fret in new[] { 0, 3, 5 })
                {
                    if (SoundingPitch.MidiFor(a, s, fret) is not { } midi) continue;
                    Assert.Equal(fret, SoundingPitch.FretForPitch(a, s, midi));
                }
            }
        }
    }

    [Fact]
    public void A_fret_behind_the_capo_is_malformed_for_live_judging_too()
    {
        var a = Make(ArrangementType.Lead, 6, 0, capo: 3);

        Assert.Null(SoundingPitch.MidiFor(a, 0, 1));
        Assert.Null(LivePitchAcceptance.SoundingMidi(a, 0, 1));
        Assert.NotNull(LivePitchAcceptance.SoundingMidi(a, 0, 0));
    }

    [Fact]
    public void The_open_sounding_pitch_carries_the_reference_octave()
    {
        var a = Make(ArrangementType.Lead, 6, -1200);

        Assert.Equal(SoundingPitch.OpenStringMidi(a, 0) - 12, SoundingPitch.OpenSoundingMidi(a, 0));
    }
}
