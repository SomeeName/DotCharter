using System.IO.Compression;
using System.Text;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     #928: a fresh Guitar Pro import leaves every hand shape the spacing gap before the next
///     attack, so the D15 check starts with nothing to report; a hand shape wider than four frets
///     and a source note the import drops (a negative fret) are counted for the import report
///     instead of passing silently.
/// </summary>
public sealed class GpImportHandShapeReportTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-gpshapes-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    [Fact]
    public void FreshImport_LeavesTheGapBeforeTheNextAttack()
    {
        var (result, arr) = Import();
        var beats = GpBeatMapBuilder.Build(result.Metadata, GpBeatMapBuilder.InitialBpm(result.Metadata, 120), 0);

        var issues = new List<ArrangementIssue>();
        new HandShapeGapRule().Validate(ArrangementValidationContext.Build(arr, beats, null), issues);

        Assert.True(arr.Levels[0].HandShapes.Count >= 2);
        Assert.Empty(issues);
    }

    [Fact]
    public void WideShape_AndNegativeFret_AreCountedForTheReport()
    {
        var (result, arr) = Import();

        Assert.Equal(1, result.WideHandShapes[arr]);
        Assert.Equal(1, result.DroppedNotes[arr]);
        Assert.DoesNotContain(arr.Levels[0].Notes, n => n.StringIndex == 3 && n.Fret == 0);
    }

    /// <summary>Bar 1: a whole-note chord 3/8 (a five-fret shape); bar 2: a whole-note chord 5/7;
    /// bar 3: a half note with a negative fret, then a half note at fret 5.</summary>
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
                <Voice id="0"><Beats>0</Beats></Voice>
                <Voice id="1"><Beats>1</Beats></Voice>
                <Voice id="2"><Beats>2 3</Beats></Voice>
              </Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0 1</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /><Notes>2 3</Notes></Beat>
                <Beat id="2"><Rhythm ref="1" /><Notes>4</Notes></Beat>
                <Beat id="3"><Rhythm ref="1" /><Notes>5</Notes></Beat>
              </Beats>
              <Notes>
                <Note id="0"><Properties><Property name="String"><String>1</String></Property><Property name="Fret"><Fret>3</Fret></Property></Properties></Note>
                <Note id="1"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>8</Fret></Property></Properties></Note>
                <Note id="2"><Properties><Property name="String"><String>1</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
                <Note id="3"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>7</Fret></Property></Properties></Note>
                <Note id="4"><Properties><Property name="String"><String>3</String></Property><Property name="Fret"><Fret>-1</Fret></Property></Properties></Note>
                <Note id="5"><Properties><Property name="String"><String>3</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
              </Notes>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Whole</NoteValue></Rhythm>
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
