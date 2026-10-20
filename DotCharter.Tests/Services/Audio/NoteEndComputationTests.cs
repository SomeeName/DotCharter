using DotCharter.Tone.Audio;

namespace DotCharter.Tests.Services.Audio;

public class NoteEndComputationTests
{
    private const double Bpm = 120.0;
    private const double Eighth = 30_000.0 / Bpm; // 250 ms
    private const double Pad = 8.0;
    private const double Stac = 90.0;

    private static List<BeatMarker> BuildBeats(int count)
    {
        var beats = new List<BeatMarker>();
        var msPerBeat = 60000.0 / Bpm;
        for (var i = 0; i < count; i++)
            beats.Add(new BeatMarker { Time = i * msPerBeat, Bpm = Bpm });
        return beats;
    }

    private static GuitarNote NoteAt(int stringIndex, int beat) => new()
    {
        StringIndex = stringIndex,
        Position = new FractionalPosition(beat),
        Sustain = FractionalPosition.Zero,
    };

    [Fact]
    public void Non_accent_zero_sustain_note_holds_for_at_least_an_eighth()
    {
        var beats = BuildBeats(16);
        var idx = NoteLegatoIndex.Build(Array.Empty<GuitarNote>(), Array.Empty<Chord>(), beats);
        var end = JuceAudioPlaybackService.ComputeNoteEnd(
            startMs: 0, baseEndMs: 0, stringIndex: 0, accent: false,
            handShapes: null, beats: beats, legato: idx,
            eighthMs: Eighth, padMs: Pad, staccatoMs: Stac);
        Assert.Equal(Eighth, end, 1);
    }

    [Fact]
    public void Accent_note_stays_staccato_short()
    {
        var beats = BuildBeats(16);
        var idx = NoteLegatoIndex.Build(Array.Empty<GuitarNote>(), Array.Empty<Chord>(), beats);
        var end = JuceAudioPlaybackService.ComputeNoteEnd(
            startMs: 0, baseEndMs: 0, stringIndex: 0, accent: true,
            handShapes: null, beats: beats, legato: idx,
            eighthMs: Eighth, padMs: Pad, staccatoMs: Stac);
        Assert.Equal(Stac, end, 1);
    }

    [Fact]
    public void Accent_with_authored_sustain_keeps_authored_release()
    {
        var beats = BuildBeats(16);
        var idx = NoteLegatoIndex.Build(Array.Empty<GuitarNote>(), Array.Empty<Chord>(), beats);
        var end = JuceAudioPlaybackService.ComputeNoteEnd(
            startMs: 0, baseEndMs: 700, stringIndex: 0, accent: true,
            handShapes: null, beats: beats, legato: idx,
            eighthMs: Eighth, padMs: Pad, staccatoMs: Stac);
        Assert.Equal(700.0, end, 1);
    }

    [Fact]
    public void Next_note_on_same_string_clamps_release_before_attack()
    {
        var beats = BuildBeats(16);
        // Two notes on string 0: at beat 0 (0 ms) and beat 1 (500 ms).
        var n0 = NoteAt(0, 0);
        var n1 = NoteAt(0, 1);
        var idx = NoteLegatoIndex.Build(new[] { n0, n1 }, Array.Empty<Chord>(), beats);

        var end = JuceAudioPlaybackService.ComputeNoteEnd(
            startMs: 0, baseEndMs: 0, stringIndex: 0, accent: false,
            handShapes: null, beats: beats, legato: idx,
            eighthMs: Eighth, padMs: Pad, staccatoMs: Stac);

        // Eighth-note minimum is 250 ms; next attack is at 500 ms — eighth wins.
        Assert.Equal(Eighth, end, 1);
    }

    [Fact]
    public void Authored_long_sustain_is_clamped_to_next_attack()
    {
        var beats = BuildBeats(16);
        var n0 = NoteAt(0, 0);
        var n1 = NoteAt(0, 1); // 500 ms
        var idx = NoteLegatoIndex.Build(new[] { n0, n1 }, Array.Empty<Chord>(), beats);

        var end = JuceAudioPlaybackService.ComputeNoteEnd(
            startMs: 0, baseEndMs: 2000, stringIndex: 0, accent: false,
            handShapes: null, beats: beats, legato: idx,
            eighthMs: Eighth, padMs: Pad, staccatoMs: Stac);

        Assert.Equal(500.0 - Pad, end, 1);
    }
}
