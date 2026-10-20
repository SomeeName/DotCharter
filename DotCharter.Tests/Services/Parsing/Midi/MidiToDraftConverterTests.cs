using DotCharter.Services.Parsing.Midi;
using static DotCharter.Tests.Services.Parsing.Midi.MidiTestBytes;

namespace DotCharter.Tests.Services.Parsing.Midi;

public sealed class MidiToDraftConverterTests
{
    private const int Ppq = 480;

    [Fact]
    public void Convert_MapsOpenStringPitches_AndExactGridPositions()
    {
        // E2 (40) at tick 0 for one beat, A2 (45) at tick 600 for half a beat.
        var midi = FileWithNotes(
            new MidiNoteEvent(0, 40, 100, 0, Ppq),
            new MidiNoteEvent(0, 45, 90, 600, 240));

        var draft = MidiToDraftConverter.Convert(midi);

        Assert.Equal(2, draft.Notes.Count);
        var low = draft.Notes[0];
        Assert.Equal(0, low.Note.StringIndex);
        Assert.Equal(0, low.Note.Fret);
        Assert.Equal(new FractionalPosition(0), low.Note.Position);
        Assert.Equal(new FractionalPosition(1), low.Note.Sustain);
        Assert.Equal(40, low.Midi);
        Assert.Equal(100, low.Velocity);
        Assert.Equal(0.0, low.TimeSeconds, 6);

        var high = draft.Notes[1];
        Assert.Equal(1, high.Note.StringIndex);
        Assert.Equal(0, high.Note.Fret);
        // Tick 600 = beat 1 + 120/480 = 1/4 — exact rational, no rounding.
        Assert.Equal(new FractionalPosition(1, new Fraction(1, 4)), high.Note.Position);
        Assert.Equal(new FractionalPosition(0, new Fraction(1, 2)), high.Note.Sustain);
        // 120 BPM default tempo: tick 600 = 625 ms.
        Assert.Equal(0.625, high.TimeSeconds, 6);
    }

    [Fact]
    public void Convert_ChordOnset_UsesOneStringPerNote()
    {
        var midi = FileWithNotes(
            new MidiNoteEvent(0, 40, 100, 0, Ppq),
            new MidiNoteEvent(0, 45, 100, 0, Ppq),
            new MidiNoteEvent(0, 50, 100, 0, Ppq));

        var draft = MidiToDraftConverter.Convert(midi);

        Assert.Equal(3, draft.Notes.Count);
        Assert.Equal(new[] { 0, 1, 2 }, draft.Notes.Select(n => n.Note.StringIndex).OrderBy(s => s));
        Assert.All(draft.Notes, n => Assert.Equal(0, n.Note.Fret));
    }

    [Fact]
    public void Convert_PrefersFretsNearTheHandPosition()
    {
        // Fresh hand (reference fret 3): pitch 48 goes to string 1 fret 3,
        // not string 0 fret 8.
        var nearDefault = MidiToDraftConverter.Convert(FileWithNotes(
            new MidiNoteEvent(0, 48, 100, 0, Ppq)));
        Assert.Equal(1, nearDefault.Notes[0].Note.StringIndex);
        Assert.Equal(3, nearDefault.Notes[0].Note.Fret);

        // After a note at fret 6 the hand is high: pitch 57 now resolves to
        // string 2 fret 7 (cost 1) instead of string 3 fret 2 (cost 4).
        var afterHighNote = MidiToDraftConverter.Convert(FileWithNotes(
            new MidiNoteEvent(0, 70, 100, 0, Ppq), // string 5 fret 6
            new MidiNoteEvent(0, 57, 100, Ppq, Ppq)));
        Assert.Equal(5, afterHighNote.Notes[0].Note.StringIndex);
        Assert.Equal(6, afterHighNote.Notes[0].Note.Fret);
        Assert.Equal(2, afterHighNote.Notes[1].Note.StringIndex);
        Assert.Equal(7, afterHighNote.Notes[1].Note.Fret);
    }

    [Fact]
    public void Convert_SkipsUnmappablePitches()
    {
        var midi = FileWithNotes(
            new MidiNoteEvent(0, 20, 100, 0, Ppq), // below the lowest open string
            new MidiNoteEvent(0, 120, 100, 0, Ppq), // above fret 24 on every string
            new MidiNoteEvent(0, 52, 100, Ppq, Ppq)); // mappable

        var draft = MidiToDraftConverter.Convert(midi);

        var note = Assert.Single(draft.Notes);
        Assert.Equal(52, note.Midi);
    }

    [Fact]
    public void Convert_SkipsDrumChannel_UnlessIncluded()
    {
        var midi = FileWithNotes(
            new MidiNoteEvent(9, 42, 100, 0, Ppq), // GM percussion channel
            new MidiNoteEvent(0, 45, 100, 0, Ppq));

        var withoutDrums = MidiToDraftConverter.Convert(midi);
        Assert.Equal(45, Assert.Single(withoutDrums.Notes).Midi);

        var withDrums = MidiToDraftConverter.Convert(midi, new MidiDraftOptions { IncludeDrumChannel = true });
        Assert.Equal(2, withDrums.Notes.Count);
    }

    [Fact]
    public void Convert_TrackIndex_FiltersToOneTrack()
    {
        var track0 = new MidiTrackData("Bass", new[] { new MidiNoteEvent(0, 40, 100, 0, Ppq) });
        var track1 = new MidiTrackData("Lead", new[] { new MidiNoteEvent(1, 52, 100, 0, Ppq) });
        var midi = new MidiFileData(1, Ppq, new[] { track0, track1 },
            Array.Empty<MidiTempoEvent>(), Array.Empty<MidiTimeSignatureEvent>());

        var draft = MidiToDraftConverter.Convert(midi, new MidiDraftOptions { TrackIndex = 1 });

        Assert.Equal(52, Assert.Single(draft.Notes).Midi);
    }

    [Fact]
    public void Convert_RespectsCustomTuning()
    {
        // Drop D: lowest string 38.
        var midi = FileWithNotes(new MidiNoteEvent(0, 38, 100, 0, Ppq));

        var draft = MidiToDraftConverter.Convert(midi, new MidiDraftOptions
        {
            OpenStringMidi = new[] { 38, 45, 50, 55, 59, 64 }
        });

        var note = Assert.Single(draft.Notes);
        Assert.Equal(0, note.Note.StringIndex);
        Assert.Equal(0, note.Note.Fret);
    }

    [Fact]
    public void Convert_SuggestsBeatGridFromTempoMap()
    {
        var midi = new MidiFileData(0, Ppq,
            new[] { new MidiTrackData("", new[] { new MidiNoteEvent(0, 40, 100, 0, Ppq * 2) }) },
            new[] { new MidiTempoEvent(0, 400_000) }, // 150 BPM
            Array.Empty<MidiTimeSignatureEvent>());

        var draft = MidiToDraftConverter.Convert(midi);

        Assert.True(draft.SuggestedBeats.Count >= 3);
        Assert.Equal(0.0, draft.SuggestedBeats[0].Time, 6);
        Assert.Equal(400.0, draft.SuggestedBeats[1].Time, 6);
        Assert.Equal(150.0, draft.SuggestedBeats[0].Bpm, 3);
    }

    [Fact]
    public void Convert_NotesComeOutPositionSorted_AndDeterministic()
    {
        var midi = FileWithNotes(
            new MidiNoteEvent(0, 45, 100, 960, Ppq),
            new MidiNoteEvent(0, 40, 100, 0, Ppq),
            new MidiNoteEvent(0, 50, 100, 480, Ppq));

        var first = MidiToDraftConverter.Convert(midi);
        var second = MidiToDraftConverter.Convert(midi);

        var positions = first.Notes.Select(n => n.Note.Position).ToList();
        for (var i = 1; i < positions.Count; i++)
            Assert.True(positions[i - 1] <= positions[i]);

        Assert.Equal(
            first.Notes.Select(n => (n.Midi, n.Note.StringIndex, n.Note.Fret, n.Note.Position)),
            second.Notes.Select(n => (n.Midi, n.Note.StringIndex, n.Note.Fret, n.Note.Position)));
    }

    [Fact]
    public void Convert_EndToEnd_FromRawBytes()
    {
        var bytes = SmfFile(0, Ppq, Track(
            SetTempo(0, 500_000),
            NoteOn(0, 0, 40, 100),
            NoteOff(Ppq, 0, 40),
            NoteOn(0, 0, 45, 90),
            NoteOff(Ppq, 0, 45),
            EndOfTrack()));

        var draft = MidiToDraftConverter.Convert(MidiFileParser.Parse(bytes));

        Assert.Equal(2, draft.Notes.Count);
        Assert.Equal(new FractionalPosition(0), draft.Notes[0].Note.Position);
        Assert.Equal(new FractionalPosition(1), draft.Notes[1].Note.Position);
        Assert.Equal(0.5, draft.Notes[1].TimeSeconds, 6);
        Assert.NotEmpty(draft.SuggestedBeats);
    }

    private static MidiFileData FileWithNotes(params MidiNoteEvent[] notes)
    {
        return new MidiFileData(0, Ppq,
            new[] { new MidiTrackData("", notes) },
            Array.Empty<MidiTempoEvent>(),
            Array.Empty<MidiTimeSignatureEvent>());
    }
}
