using DotCharter.Tests.Core;
using DotCharter.Tone.Audio.ChartSynth;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.ChartSynth;

/// <summary>
///     A 5/6-string bass charted on a guitar path at A220 (#161) is played by the
///     BASS voice, at the bass's own pitch — and the octave the A220 reference
///     already carries is not applied a second time.
/// </summary>
public sealed class GuitarPathBassSynthTests
{
    private static ChartNoteUpload Build(Arrangement arrangement, params GuitarNote[] notes) =>
        ChartNoteEventBuilder.Build(arrangement, notes, [], ChartNoteEventBuilderTests.Beats(), null, 120);

    private static GuitarNote[] OpenStrings(int count) =>
        Enumerable.Range(0, count).Select(s => ChartNoteEventBuilderTests.Note(1 + s, s, 0)).ToArray();

    [Fact]
    public void A_five_string_bass_on_Bonus_Rhythm_is_played_by_the_bass_voice()
    {
        var upload = Build(GuitarPathBassShapes.FiveStringBassOnBonusRhythm(), OpenStrings(5));

        Assert.True(ChartSynthVoiceSelection.IsBass(upload));
        Assert.All(upload.Notes, n => Assert.True((n.Flags & JuceChartNoteFlags.Bass) != 0));
        Assert.Equal(ChartSynthVoice.Bass, ChartSynthVoiceSelection.Effective(ChartSynthVoiceSelection.Default, upload));
    }

    [Fact]
    public void A_six_string_bass_on_a_guitar_path_is_played_by_the_bass_voice()
    {
        var upload = Build(GuitarPathBassShapes.SixStringBassOnAltRhythm(), OpenStrings(6));
        Assert.Equal(ChartSynthVoice.Bass, ChartSynthVoiceSelection.Effective(ChartSynthVoiceSelection.Default, upload));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    public void A_guitar_Rhythm_path_stays_on_the_guitar_voice(double centOffset)
    {
        var upload = Build(GuitarPathBassShapes.GuitarRhythm(centOffset), OpenStrings(6));
        Assert.Equal(ChartSynthVoice.Guitar, ChartSynthVoiceSelection.Effective(ChartSynthVoiceSelection.Default, upload));
        Assert.Equal(ChartSynthVoice.Guitar,
            ChartSynthVoiceSelection.Effective(ChartSynthVoiceSelection.Default, Build(GuitarPathBassShapes.GuitarDropC(), OpenStrings(6))));
    }

    [Fact]
    public void The_five_string_bass_low_B_sounds_B0_once_not_two_octaves_down()
    {
        var upload = Build(GuitarPathBassShapes.FiveStringBassOnBonusRhythm(), OpenStrings(5));

        // B0 E1 A1 D2 G2 — the guitar's B1..G3 moved down by the -1200 cents, once.
        Assert.Equal([23f, 28f, 33f, 38f, 43f], upload.Notes.Select(n => n.Pitch).ToArray());
    }

    [Fact]
    public void The_six_string_bass_spans_B0_to_C3_and_its_extra_strings_keep_their_index()
    {
        var upload = Build(GuitarPathBassShapes.SixStringBassOnAltRhythm(), OpenStrings(6));

        Assert.Equal([23f, 28f, 33f, 38f, 43f, 48f], upload.Notes.Select(n => n.Pitch).ToArray());
        // Strings 4 and 5 (G2 and C3) are real strings of the bass, past a 4-string
        // bass's range: they go out on their own string index for the voice to place.
        Assert.Equal([0, 1, 2, 3, 4, 5], upload.Notes.Select(n => n.StringIndex).ToArray());
        Assert.Equal(48f, upload.Notes[^1].Pitch);
    }

    [Fact]
    public void Retuning_to_A220_changes_the_fingerprint_so_the_voice_follows_on_the_next_tick()
    {
        var arrangement = GuitarPathBassShapes.GuitarRhythm();
        var notes = OpenStrings(6);
        var beats = ChartNoteEventBuilderTests.Beats();
        var before = ChartNoteEventBuilder.Fingerprint(arrangement, notes, [], beats, null, 120);

        arrangement.CentOffset = -1200;

        Assert.NotEqual(before, ChartNoteEventBuilder.Fingerprint(arrangement, notes, [], beats, null, 120));
    }
}
