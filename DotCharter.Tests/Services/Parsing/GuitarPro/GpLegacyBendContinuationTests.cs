using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Guitar Pro 3-5 binary files write "bend, hold, release" and its ghost-note
///     tail the same way GP 8 does: an untied release whose first bend point is the
///     held value, and a ghost note (note header bit 0x04) right after a bent sound.
///     Both continue the sound on the same string and fret, so the phrase imports as
///     one linked sound; a ghost after an unbent note, or a release on another fret,
///     stays a new pick.
/// </summary>
public sealed class GpLegacyBendContinuationTests
{
    private const int Fret = 7;

    private static readonly (int Offset, int Value)[] BendUp = [(0, 0), (30, 100), (60, 100)];
    private static readonly (int Offset, int Value)[] Release = [(0, 100), (60, 0)];

    private static List<GuitarNote> Import(string version, params FxBeat[] beats)
    {
        var bar = new FxMeasure().AddVoice([.. beats, FxBeat.RestBeat(0)]);
        var path = LegacyGpFixtureBuilder.WriteFile(version, [bar]);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var arr = Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
            return [.. Assert.Single(arr.Levels).Notes.OrderBy(n => n.Position)];
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    private static FxBeat Beat(FxNote note) => new() { Duration = 1, Notes = { note } };

    private static string Describe(List<GuitarNote> notes) => string.Join(" | ", notes.Select(n =>
        $"@{n.Position} f{n.Fret} sus{n.Sustain} link={n.LinkNext} bends={n.BendValues.Count}"));

    [Theory]
    [InlineData("3.00")]
    [InlineData("4.06")]
    [InlineData("5.00")]
    public void UntiedReleaseFromTheHeldPitch_ContinuesTheBend(string version)
    {
        var notes = Import(version, Beat(new FxNote(0, Fret, Bend: BendUp)), Beat(new FxNote(0, Fret, Tied: true)),
            Beat(new FxNote(0, Fret, Bend: Release)));

        var note = Assert.Single(notes);
        Assert.True(note.BendValues.OrderBy(b => b.Position).Last().Step == 0 && note.BendValues.Any(b => b.Step > 0),
            $"the release was not folded into the bend: {Describe(notes)}");
    }

    [Theory]
    [InlineData("3.00")]
    [InlineData("5.00")]
    public void GhostNoteAfterABendRelease_ContinuesIt(string version)
    {
        // The ghost sounds the pitch the release came back to, so it continues it.
        var notes = Import(version, Beat(new FxNote(0, Fret, Bend: BendUp)), Beat(new FxNote(0, Fret, Bend: Release)),
            Beat(new FxNote(0, Fret, Ghost: true)));

        Assert.True(notes.Count == 1, Describe(notes));
    }

    [Fact]
    public void GhostNoteAfterAnUnbentNote_IsASoftRePick()
    {
        var notes = Import("5.00", Beat(new FxNote(0, Fret)), Beat(new FxNote(0, Fret, Ghost: true)));

        Assert.True(notes.Count == 2, Describe(notes));
        Assert.All(notes, n => Assert.False(n.LinkNext, Describe(notes)));
    }

    [Fact]
    public void ReleaseOnAnotherFret_IsANewPick()
    {
        var notes = Import("5.00", Beat(new FxNote(0, Fret, Bend: BendUp)), Beat(new FxNote(0, Fret + 1, Bend: Release)));

        Assert.True(notes.Count == 2, Describe(notes));
        Assert.All(notes, n => Assert.False(n.LinkNext, Describe(notes)));
    }

    [Fact]
    public void UntiedReleaseChord_ContinuesTheBentChord_AsOneStrum()
    {
        var bar = new FxMeasure().AddVoice(
            FxBeat.Chord(1, new FxNote(3, Fret, Bend: BendUp), new FxNote(2, Fret - 2)),
            FxBeat.Chord(1, new FxNote(3, Fret, Bend: Release), new FxNote(2, Fret - 2, Tied: true)),
            FxBeat.RestBeat(0));
        var path = LegacyGpFixtureBuilder.WriteFile("5.00", [bar]);
        try
        {
            var importer = new GpImporter();
            var arr = Assert.Single(importer.ImportTracks(path, importer.GetAvailableTracks(path).Select(t => t.Id).ToList()));
            var chord = Assert.Single(Assert.Single(arr.Levels).Chords);
            Assert.All(chord.ChordNotes, cn => Assert.False(cn.LinkNext));
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }
}
