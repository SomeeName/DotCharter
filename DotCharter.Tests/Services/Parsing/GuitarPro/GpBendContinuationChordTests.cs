using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     A two-string chord bent on one string and held on the other, as Guitar Pro 8
///     writes it: the bend chord, a tied hold chord, then a release chord whose bent
///     string is NOT tied (its bend starts at the held pitch) while the other string
///     is tied. Each string continues its sound, so the three chords import as one
///     linked strum. A chord links only as a whole, so a fresh pick on either string
///     keeps the release chord a new strum.
/// </summary>
public sealed class GpBendContinuationChordTests
{
    private const int BentString = 3;
    private const int HeldString = 2;

    private static string Note(int id, int stringIndex, int fret, string tie, string props) => $"""
        <Note id="{id}">{tie}<Properties>
          <Property name="String"><String>{stringIndex}</String></Property>
          <Property name="Fret"><Fret>{fret}</Fret></Property>
          {props}
        </Properties></Note>
        """;

    private static string Bend(int origin, int destination) => $"""
        <Property name="Bended"><Enable /></Property>
        <Property name="BendOriginOffset"><Float>0</Float></Property>
        <Property name="BendOriginValue"><Float>{origin}</Float></Property>
        <Property name="BendMiddleOffset1"><Float>12</Float></Property>
        <Property name="BendMiddleOffset2"><Float>12</Float></Property>
        <Property name="BendMiddleValue"><Float>{(origin + destination) / 2}</Float></Property>
        <Property name="BendDestinationOffset"><Float>25</Float></Property>
        <Property name="BendDestinationValue"><Float>{destination}</Float></Property>
        """;

    private static string Tie(bool origin, bool destination) =>
        $"""<Tie origin="{(origin ? "true" : "false")}" destination="{(destination ? "true" : "false")}" />""";

    /// <param name="heldTied">Whether the held string is tied into the release chord.</param>
    /// <param name="releaseFret">The fret the release chord's bent string is on.</param>
    private static string Score(bool heldTied = true, int releaseFret = 17) => $"""
        <GPIF>
          <Score><Title>Chord release fixture</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Lead</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
          <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
          <Voices><Voice id="0"><Beats>0 1 2 3</Beats></Voice></Voices>
          <Beats>
            <Beat id="0"><Rhythm ref="0" /><Notes>0 1</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>2 3</Notes></Beat>
            <Beat id="2"><Rhythm ref="0" /><Notes>4 5</Notes></Beat>
            <Beat id="3"><Rhythm ref="0" /></Beat>
          </Beats>
          <Rhythms><Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm></Rhythms>
          <Notes>
          {Note(0, BentString, 17, Tie(true, false), Bend(0, 100))}
          {Note(1, HeldString, 15, Tie(true, false), "")}
          {Note(2, BentString, 17, Tie(false, true), "")}
          {Note(3, HeldString, 15, Tie(heldTied, true), "")}
          {Note(4, BentString, releaseFret, "", Bend(100, 0))}
          {Note(5, HeldString, 15, heldTied ? Tie(false, true) : "", "")}
          </Notes>
        </GPIF>
        """;

    private static ChartLevel Import(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpchordbend-{Guid.NewGuid():N}.gp");
        try
        {
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
            {
                writer.Write(gpif);
            }

            var imported = new GpImporter().ImportTracksWithMetadata(path, [0]);
            return Assert.Single(imported.Arrangements).Levels[^1];
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    private static string Describe(ChartLevel level) => string.Join(" | ", level.Chords.OrderBy(c => c.Position)
        .Select(c => $"@{c.Position} " + string.Join(",", c.ChordNotes.Select(cn =>
            $"s{cn.StringIndex}f{cn.Fret} sus{cn.Sustain} link={cn.LinkNext} bends={cn.BendValues.Count}"))));

    [Fact]
    public void UntiedReleaseChord_ContinuesTheHeldChord_AsOneStrum()
    {
        var level = Import(Score());
        var chord = Assert.Single(level.Chords);
        Assert.Equal(2, chord.ChordNotes.Count);
        Assert.All(chord.ChordNotes, cn => Assert.False(cn.LinkNext, Describe(level)));
        var bent = chord.ChordNotes.Single(cn => cn.StringIndex == BentString);
        Assert.True(bent.BendValues.Any(b => b.Step > 0) && bent.BendValues.OrderBy(b => b.Position).Last().Step == 0,
            $"the release was not folded into the bend: {Describe(level)}");
    }

    [Fact]
    public void ReleaseChord_WithAFreshPickOnTheOtherString_StaysANewStrum()
    {
        var level = Import(Score(heldTied: false));
        var chords = level.Chords.OrderBy(c => c.Position).ToList();
        Assert.True(chords.Count == 2, Describe(level));
        Assert.All(chords[0].ChordNotes, cn => Assert.False(cn.LinkNext,
            $"a chord links only as a whole, never one string of it: {Describe(level)}"));
    }

    [Fact]
    public void ReleaseChord_OnAnotherFret_StaysANewStrum()
    {
        var level = Import(Score(releaseFret: 18));
        var chords = level.Chords.OrderBy(c => c.Position).ToList();
        Assert.True(chords.Count == 2, Describe(level));
        Assert.False(chords[0].ChordNotes.Single(cn => cn.StringIndex == BentString).LinkNext, Describe(level));
    }
}
