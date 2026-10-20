using DotCharter.Tone.Audio;

namespace DotCharter.Tests.Services.Audio;

public class NoteLegatoIndexTests
{
    private static List<BeatMarker> BuildBeats(int count, double bpm = 120.0)
    {
        var beats = new List<BeatMarker>();
        var msPerBeat = 60000.0 / bpm;
        for (var i = 0; i < count; i++)
            beats.Add(new BeatMarker { Time = i * msPerBeat, Bpm = bpm });
        return beats;
    }

    private static GuitarNote NoteAt(int stringIndex, int beat) => new()
    {
        StringIndex = stringIndex,
        Position = new FractionalPosition(beat),
        Sustain = FractionalPosition.Zero,
    };

    [Fact]
    public void NextStartOnString_returns_null_when_no_notes()
    {
        var idx = NoteLegatoIndex.Build(Array.Empty<GuitarNote>(), Array.Empty<Chord>(), BuildBeats(16));
        Assert.Null(idx.NextStartOnString(0, 100));
    }

    [Fact]
    public void NextStartOnString_finds_following_note_on_same_string()
    {
        var beats = BuildBeats(16);
        var n0 = NoteAt(0, 0);
        var n1 = NoteAt(0, 1);
        var n2 = NoteAt(1, 2);

        var idx = NoteLegatoIndex.Build(new[] { n0, n1, n2 }, Array.Empty<Chord>(), beats);

        Assert.Equal(500.0, idx.NextStartOnString(0, 0)!.Value, 1);
        Assert.Null(idx.NextStartOnString(0, 500));
        Assert.Equal(1000.0, idx.NextStartOnString(1, 0)!.Value, 1);
    }

    [Fact]
    public void Build_includes_chord_notes()
    {
        var beats = BuildBeats(16);
        var chord = new Chord
        {
            Position = new FractionalPosition(1),
            ChordNotes =
            {
                new ChordNote { StringIndex = 0 },
                new ChordNote { StringIndex = 2 },
            },
        };

        var idx = NoteLegatoIndex.Build(Array.Empty<GuitarNote>(), new[] { chord }, beats);

        Assert.Equal(500.0, idx.NextStartOnString(0, 0)!.Value, 1);
        Assert.Equal(500.0, idx.NextStartOnString(2, 0)!.Value, 1);
        Assert.Null(idx.NextStartOnString(1, 0));
    }

    [Fact]
    public void NextStartOnString_normalises_out_of_range_string_index()
    {
        var beats = BuildBeats(16);
        var n = NoteAt(0, 1);
        var idx = NoteLegatoIndex.Build(new[] { n }, Array.Empty<Chord>(), beats);
        Assert.Equal(500.0, idx.NextStartOnString(-6, 0)!.Value, 1);
        Assert.Equal(500.0, idx.NextStartOnString(6, 0)!.Value, 1);
    }

    [Fact]
    public void Build_with_null_beats_returns_empty_index()
    {
        var idx = NoteLegatoIndex.Build(new[] { NoteAt(0, 1) }, Array.Empty<Chord>(), beats: null);
        Assert.Null(idx.NextStartOnString(0, 0));
    }
}
