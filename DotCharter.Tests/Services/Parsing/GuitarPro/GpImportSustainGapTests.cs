using System.IO.Compression;
using System.Text;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     #1009: a fresh Guitar Pro import of back-to-back sustained sounds opens without a D90
///     finding (a sustain ending closer than a 32nd note to the next sound), and a plain strum
///     keeps no sustain: the hand shape carries how long it is held.
/// </summary>
public sealed class GpImportSustainGapTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-gpsustain-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    [Fact]
    public void FreshImport_HasNoSustainTooCloseToTheNextSound()
    {
        var (result, arr) = Import();
        var beats = GpBeatMapBuilder.Build(result.Metadata, GpBeatMapBuilder.InitialBpm(result.Metadata, 120), 0);

        var issues = new List<ArrangementIssue>();
        new SustainSpacingRule().Validate(ArrangementValidationContext.Build(arr, beats, null), issues);

        var level = arr.Levels[0];
        Assert.Equal(4, level.Notes.Count(n => n.Sustain > FractionalPosition.Zero));
        Assert.Equal(2, level.Chords.Count(c => c.ChordNotes.All(cn => cn.Sustain > FractionalPosition.Zero)));
        Assert.Empty(issues);
    }

    [Fact]
    public void FreshImport_APlainStrumKeepsNoSustain()
    {
        var (_, arr) = Import();

        var plain = arr.Levels[0].Chords.Where(c => c.ChordNotes.All(cn => !cn.Vibrato)).ToList();
        Assert.Equal(2, plain.Count);
        Assert.All(plain.SelectMany(c => c.ChordNotes), cn => Assert.Equal(FractionalPosition.Zero, cn.Sustain));
    }

    /// <summary>Bar 1: four quarter notes on one string; bar 2: two half-note chords with vibrato;
    /// bar 3: two plain half-note chords.</summary>
    private (GpImportResult Result, Arrangement Arrangement) Import()
    {
        const string gpif = """
            <GPIF>
              <Score><Title>Fixture</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0"><Name>Lead</Name><Staves><Staff><Properties>
                  <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                </Properties></Staff></Staves></Track>
              </Tracks>
              <MasterBars>
                <MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar>
                <MasterBar><Time>4/4</Time><Bars>1</Bars></MasterBar>
                <MasterBar><Time>4/4</Time><Bars>2</Bars></MasterBar>
              </MasterBars>
              <Bars>
                <Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar>
                <Bar id="1"><Voices>1 -1 -1 -1</Voices></Bar>
                <Bar id="2"><Voices>2 -1 -1 -1</Voices></Bar>
              </Bars>
              <Voices>
                <Voice id="0"><Beats>0 1 2 3</Beats></Voice>
                <Voice id="1"><Beats>4 5</Beats></Voice>
                <Voice id="2"><Beats>6 7</Beats></Voice>
              </Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
                <Beat id="2"><Rhythm ref="0" /><Notes>2</Notes></Beat>
                <Beat id="3"><Rhythm ref="0" /><Notes>3</Notes></Beat>
                <Beat id="4"><Rhythm ref="1" /><Notes>4 5</Notes></Beat>
                <Beat id="5"><Rhythm ref="1" /><Notes>6 7</Notes></Beat>
                <Beat id="6"><Rhythm ref="1" /><Notes>8 9</Notes></Beat>
                <Beat id="7"><Rhythm ref="1" /><Notes>10 11</Notes></Beat>
              </Beats>
              <Notes>
                <Note id="0"><Properties><Property name="String"><String>3</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
                <Note id="1"><Properties><Property name="String"><String>3</String></Property><Property name="Fret"><Fret>7</Fret></Property></Properties></Note>
                <Note id="2"><Properties><Property name="String"><String>3</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
                <Note id="3"><Properties><Property name="String"><String>3</String></Property><Property name="Fret"><Fret>7</Fret></Property></Properties></Note>
                <Note id="4"><Vibrato>Slight</Vibrato><Properties><Property name="String"><String>1</String></Property><Property name="Fret"><Fret>3</Fret></Property></Properties></Note>
                <Note id="5"><Vibrato>Slight</Vibrato><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
                <Note id="6"><Vibrato>Slight</Vibrato><Properties><Property name="String"><String>1</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
                <Note id="7"><Vibrato>Slight</Vibrato><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>7</Fret></Property></Properties></Note>
                <Note id="8"><Properties><Property name="String"><String>1</String></Property><Property name="Fret"><Fret>3</Fret></Property></Properties></Note>
                <Note id="9"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
                <Note id="10"><Properties><Property name="String"><String>1</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
                <Note id="11"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>7</Fret></Property></Properties></Note>
              </Notes>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm>
                <Rhythm id="1"><NoteValue>Half</NoteValue></Rhythm>
              </Rhythms>
            </GPIF>
            """;
        var path = Path.Combine(_tempDir, "fixture.gp");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
        {
            writer.Write(gpif);
        }

        var importer = new GpImporter();
        var result = importer.ImportTracksWithMetadata(path, [.. importer.GetAvailableTracks(path).Select(t => t.Id)]);
        return (result, Assert.Single(result.Arrangements));
    }
}
