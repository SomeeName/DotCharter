using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     The ported Editor-on-Fire beat-text and marker import settings, exercised
///     through the real gpif importer:
///     <list type="bullet">
///         <item>beat text "T"/"S"/"P" → tap / slap / pop on the note;</item>
///         <item>section markers (and free-text markers) → RS phrases, plus
///         section EventPoints when the name is a valid RS section.</item>
///     </list>
/// </summary>
public sealed class GpBeatTextAndMarkerImportTests
{
    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpmarker-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("Content/score.gpif");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    private static Arrangement Import(string gpif, GpImportSettings settings)
    {
        var path = WriteGpFile(gpif);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList(), settings));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string Note(int id, int str, int fret) => $"""
        <Note id="{id}"><Properties>
          <Property name="String"><String>{str}</String></Property>
          <Property name="Fret"><Fret>{fret}</Fret></Property>
        </Properties></Note>
        """;

    // One 4/4 bar, one whole-note beat carrying the given free text + a single note.
    private static string SingleBeatText(string freeText) => $"""
        <GPIF>
          <Score><Title>Beat Text Fixture</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks>
            <Track id="0"><Name>Lead</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
          <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
          <Voices><Voice id="0"><Beats>0</Beats></Voice></Voices>
          <Beats><Beat id="0"><Rhythm ref="0" /><FreeText>{freeText}</FreeText><Notes>0</Notes></Beat></Beats>
          <Notes>{Note(0, 2, 5)}</Notes>
          <Rhythms><Rhythm id="0"><NoteValue>Whole</NoteValue></Rhythm></Rhythms>
        </GPIF>
        """;

    // Two 4/4 bars each with a section marker + a single note.
    private static string TwoSectionMarkers(string bar0Section, string bar1Section) => $"""
        <GPIF>
          <Score><Title>Marker Fixture</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks>
            <Track id="0"><Name>Lead</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars>
            <MasterBar><Time>4/4</Time><Section><Text>{bar0Section}</Text></Section><Bars>0</Bars></MasterBar>
            <MasterBar><Time>4/4</Time><Section><Text>{bar1Section}</Text></Section><Bars>1</Bars></MasterBar>
          </MasterBars>
          <Bars>
            <Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar>
            <Bar id="1"><Voices>1 -1 -1 -1</Voices></Bar>
          </Bars>
          <Voices>
            <Voice id="0"><Beats>0</Beats></Voice>
            <Voice id="1"><Beats>1</Beats></Voice>
          </Voices>
          <Beats>
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
          </Beats>
          <Notes>{Note(0, 2, 5)}{Note(1, 2, 5)}</Notes>
          <Rhythms><Rhythm id="0"><NoteValue>Whole</NoteValue></Rhythm></Rhythms>
        </GPIF>
        """;

    [Theory]
    [InlineData("T", HopoType.Tap)]
    [InlineData("t", HopoType.Tap)]
    [InlineData(" T ", HopoType.Tap)]
    public void BeatText_TapMarker_SetsTapHopo(string text, HopoType expected)
    {
        var arr = Import(SingleBeatText(text), GpImportSettings.Default);
        Assert.Equal(expected, Assert.Single(arr.Levels[0].Notes).Hopo);
    }

    [Theory]
    [InlineData("S", BassPickingType.Slap)]
    [InlineData("P", BassPickingType.Pop)]
    public void BeatText_SlapPopMarkers_SetBassPicking(string text, BassPickingType expected)
    {
        var arr = Import(SingleBeatText(text), GpImportSettings.Default);
        Assert.Equal(expected, Assert.Single(arr.Levels[0].Notes).BassPicking);
    }

    [Fact]
    public void BeatText_IsIgnored_WhenSettingOff()
    {
        var arr = Import(SingleBeatText("T"), new GpImportSettings { BeatTextTechniques = false });
        Assert.Equal(HopoType.None, Assert.Single(arr.Levels[0].Notes).Hopo);
    }

    [Fact]
    public void SectionMarkers_ImportAsPhrasesAndSections_WhenOn()
    {
        // "Verse" is a valid RS section; "Zibble" is not → phrase-only.
        var arr = Import(TwoSectionMarkers("Verse", "Zibble"),
            new GpImportSettings { MarkersToSections = true });

        Assert.Equal(2, arr.EventPoints.Count);
        var verse = arr.EventPoints.First(e => e.Phrase == "Verse");
        Assert.Equal("verse", verse.Section);
        var zibble = arr.EventPoints.First(e => e.Phrase == "Zibble");
        Assert.Equal("", zibble.Section);

        Assert.Contains("Verse", arr.Phrases);
        Assert.Contains("Zibble", arr.Phrases);
    }

    [Fact]
    public void SectionMarkers_AreNotImported_WhenOff()
    {
        var arr = Import(TwoSectionMarkers("Verse", "Chorus"), GpImportSettings.Default);
        Assert.Empty(arr.EventPoints);
    }

    [Fact]
    public void FreeTextMarker_ImportsAsPhrase_WhenNotATechniqueLetter()
    {
        // A non-technique free text is a section/phrase marker, not a technique.
        var arr = Import(SingleBeatText("Solo"), new GpImportSettings { MarkersToSections = true });
        var ev = Assert.Single(arr.EventPoints);
        Assert.Equal("Solo", ev.Phrase);
        Assert.Equal("solo", ev.Section);
        Assert.Equal(HopoType.None, Assert.Single(arr.Levels[0].Notes).Hopo);
    }
}
