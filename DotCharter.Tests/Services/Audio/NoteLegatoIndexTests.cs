using System.Reflection;
using System.Runtime.CompilerServices;
using DotCharter.Tone.Audio;

namespace DotCharter.Tests.Services.Audio;

public class NoteLegatoIndexTests
{
    [Theory]
    [InlineData(false, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData(true, new[] { 5, 4, 3, 2, 1, 0, 6, 7, 8 })]
    public void Instrument_channels_keep_all_supported_strings_independent(bool ample, int[] expected)
    {
        // This leaf reads only the Ample flag; no constructor, engine or device is needed.
        var service = (JuceAudioPlaybackService)RuntimeHelpers.GetUninitializedObject(typeof(JuceAudioPlaybackService));
        typeof(JuceAudioPlaybackService).GetField("_instrumentIsAmpleGuitar", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, ample);
        var channelFor = typeof(JuceAudioPlaybackService).GetMethod("StringChannelForInstrument",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var channels = Enumerable.Range(0, StandardOpenStrings.MaxStringCount)
            .Select(s => (int)channelFor.Invoke(service, [s])!).ToArray();

        Assert.Equal(expected, channels);
        Assert.Equal(StandardOpenStrings.MaxStringCount, channels.Distinct().Count());
    }

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

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Extra_strings_keep_their_own_note_and_chord_legato_attacks(int stringIndex)
    {
        var extra = NoteAt(stringIndex, 2);
        extra.Hopo = HopoType.HammerOn;
        var chord = new Chord
        {
            Position = new FractionalPosition(3),
            ChordNotes = { new ChordNote { StringIndex = stringIndex, Hopo = HopoType.PullOff } }
        };
        var index = NoteLegatoIndex.Build([NoteAt(stringIndex - 6, 1), extra], [chord], BuildBeats(8));

        Assert.Equal(500.0, index.NextStartOnString(stringIndex - 6, 0));
        Assert.Null(index.NextStartOnString(stringIndex - 6, 500));
        Assert.Equal((1000.0, true), index.NextOnString(stringIndex, 0)!.Value);
        Assert.Equal((1500.0, true), index.NextOnString(stringIndex, 1000)!.Value);
    }

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

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Invalid_strings_are_ignored_without_aliasing_a_valid_string(int stringIndex)
    {
        var chord = new Chord
        {
            Position = new FractionalPosition(2),
            ChordNotes = { new ChordNote { StringIndex = stringIndex } }
        };
        var index = NoteLegatoIndex.Build([NoteAt(0, 1), NoteAt(stringIndex, 3)], [chord], BuildBeats(8));

        Assert.Null(index.NextOnString(stringIndex, 0));
        Assert.Equal(500.0, index.NextStartOnString(0, 0));
        Assert.Null(index.NextStartOnString(0, 500));
    }

    [Fact]
    public void Build_with_null_beats_returns_empty_index()
    {
        var idx = NoteLegatoIndex.Build(new[] { NoteAt(0, 1) }, Array.Empty<Chord>(), beats: null);
        Assert.Null(idx.NextStartOnString(0, 0));
    }
}
