using System.IO.Compression;
using System.Xml.Linq;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Guitar Pro carries a per-note LEFT-hand finger in both of its formats and
///     both of our readers used to throw it away — the binary path
///     <c>Skip(2)</c>'d the fingering bytes, the gpif path never looked for the
///     <c>LeftFingering</c> property. Every imported chord template was then
///     fingered by <c>ChordTemplateFingerSetter</c>'s guess (measured 74.6% exact
///     against a hard 88.8% ceiling) while the file had the answer.
/// </summary>
public sealed class GpAuthoredFingeringTests
{
    // ---- The mapping itself -------------------------------------------------

    [Theory]
    [InlineData("P", 0)] // pouce / thumb
    [InlineData("I", 1)]
    [InlineData("M", 2)]
    [InlineData("A", 3)] // annular / ring
    [InlineData("C", 4)] // little
    [InlineData("i", 1)] // case-insensitive
    [InlineData(" M ", 2)] // trimmed
    [InlineData("3", 3)] // numeric spelling
    public void MapGpifFinger_MapsTheClassicalLetters(string value, int expected)
        => Assert.Equal(expected, GpAuthoredFingering.MapGpifFinger(value));

    [Theory]
    [InlineData("Open")]
    [InlineData("None")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("9")]
    [InlineData(null)]
    public void MapGpifFinger_UnfingeredOrGarbage_IsNull(string? value)
        => Assert.Null(GpAuthoredFingering.MapGpifFinger(value));

    [Theory]
    [InlineData(-1, null)] // GP's "no fingering"
    [InlineData(0, 0)] // thumb — already our id
    [InlineData(4, 4)]
    [InlineData(5, null)]
    [InlineData(-2, null)]
    public void MapBinaryFinger_IsIdentityForLegalValuesOnly(int gp, int? expected)
        => Assert.Equal(expected, GpAuthoredFingering.MapBinaryFinger(gp));

    // ---- End to end: the GP3-5 binary reader --------------------------------

    /// <summary>The shape both fixtures author, and a fingering no suggester
    /// would produce for it (that is the point — see the control test).</summary>
    private static readonly (int String, int Fret, int Finger)[] Shape =
    [
        (0, 5, 4),
        (1, 7, 1),
        (2, 7, 3)
    ];

    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void LegacyBinary_AuthoredFingeringBeatsTheSuggester(string version)
    {
        var chord = FxBeat.Chord(0,
            Shape.Select(s => new FxNote(s.String, s.Fret, LeftFinger: s.Finger)).ToArray());
        var measure = new FxMeasure().AddVoice(chord, FxBeat.RestBeat(-1), FxBeat.RestBeat(0));

        var arr = ImportLegacy(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));
        var template = Assert.Single(arr.ChordTemplates);

        foreach (var (str, fret, finger) in Shape)
        {
            Assert.Equal(fret, template.Frets[str]);
            Assert.True(template.Fingers.TryGetValue(str, out var actual),
                $"string {str} lost its authored finger");
            Assert.Equal(finger, actual);
        }
    }

    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void LegacyBinary_WithoutAuthoredFingering_TheSuggesterStillRuns(string version)
    {
        // The control that makes the test above discriminating: the SAME shape
        // with no fingering block must NOT come out with the authored fingers.
        var chord = FxBeat.Chord(0, Shape.Select(s => new FxNote(s.String, s.Fret)).ToArray());
        var measure = new FxMeasure().AddVoice(chord, FxBeat.RestBeat(-1), FxBeat.RestBeat(0));

        var arr = ImportLegacy(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));
        var template = Assert.Single(arr.ChordTemplates);

        Assert.NotEmpty(template.Fingers); // still charting-ready
        Assert.False(Shape.All(s => template.Fingers.TryGetValue(s.String, out var f) && f == s.Finger),
            "the suggester happens to reproduce the authored fingering — pick a different fixture shape");
    }

    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void LegacyBinary_AuthoredFollowerFingersSurviveTheImportFhpPass(string version)
    {
        var suggestion = new ChordTemplate { Frets = new() { [3] = 9, [4] = 10 } };
        ChordTemplateFingerSetter.SetSuggestedFingers(suggestion, 6);
        Assert.Equal(1, suggestion.GetFinger(3));
        var first = FxBeat.Chord(1, new FxNote(3, 9), new FxNote(4, 10));
        var follower = FxBeat.Chord(1,
            new FxNote(3, 9, LeftFinger: suggestion.GetFinger(3)),
            new FxNote(4, 10, LeftFinger: suggestion.GetFinger(4)));
        var measure = new FxMeasure().AddVoice(
            FxBeat.Chord(0, new FxNote(3, 8)), first, follower,
            FxBeat.Chord(0, new FxNote(3, 8)), FxBeat.Chord(0, new FxNote(4, 8)));

        var arrangement = ImportLegacy(LegacyGpFixtureBuilder.WriteFile(version, new[] { measure }));

        var level = Assert.Single(arrangement.Levels);
        Assert.NotEmpty(level.Anchors);
        Assert.Equal(2, level.Chords.Count);
        var bar = Assert.Single(level.HandShapes);
        Assert.All(level.Chords, chord =>
        {
            Assert.Equal(bar.TemplateId, chord.TemplateId);
            var template = arrangement.FindChordTemplate(chord.TemplateId)!;
            Assert.Equal(suggestion.Fingers, template.Fingers);
        });
    }

    // ---- End to end: the modern gpif reader ---------------------------------

    [Fact]
    public void Gpif_AuthoredFingeringBeatsTheSuggester()
    {
        var arr = ImportGpif(GpifScore(withFingering: true));
        var template = Assert.Single(arr.ChordTemplates);

        foreach (var (str, fret, finger) in Shape)
        {
            Assert.Equal(fret, template.Frets[str]);
            Assert.True(template.Fingers.TryGetValue(str, out var actual),
                $"string {str} lost its authored finger");
            Assert.Equal(finger, actual);
        }
    }

    [Fact]
    public void Gpif_WithoutAuthoredFingering_TheSuggesterStillRuns()
    {
        var arr = ImportGpif(GpifScore(withFingering: false));
        var template = Assert.Single(arr.ChordTemplates);

        Assert.NotEmpty(template.Fingers);
        Assert.False(Shape.All(s => template.Fingers.TryGetValue(s.String, out var f) && f == s.Finger),
            "the suggester happens to reproduce the authored fingering — pick a different fixture shape");
    }

    [Fact]
    public void GpNoteParser_ReadsTheLeftFingeringProperty()
    {
        var parser = new GpNoteParser();
        var noteNode = XElement.Parse("""
            <Note>
              <Properties>
                <Property name="String"><String>1</String></Property>
                <Property name="Fret"><Fret>7</Fret></Property>
                <Property name="LeftFingering"><Fingering>A</Fingering></Property>
              </Properties>
            </Note>
            """);

        parser.ParseChordNote(noteNode, FractionalPosition.Zero, new FractionalPosition(1),
            stringCount: 6, hasTremolo: false);
        Assert.Equal(3, parser.LastLeftFingering);

        // ...and it must not leak onto the NEXT note.
        var bare = XElement.Parse("""
            <Note><Properties>
              <Property name="String"><String>2</String></Property>
              <Property name="Fret"><Fret>5</Fret></Property>
            </Properties></Note>
            """);
        parser.ParseChordNote(bare, FractionalPosition.Zero, new FractionalPosition(1),
            stringCount: 6, hasTremolo: false);
        Assert.Null(parser.LastLeftFingering);
    }

    // A track with no power-chord style of its own (here the rhythm part's only
    // power chord, unfingered) plays it the way the file's other guitar track
    // fingers it, whichever track comes first in the file.
    [Fact]
    public void Gpif_ARhythmPartFollowsTheLeadPartsPowerChordFingering()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpsong-{Guid.NewGuid():N}.gp");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("Content/score.gpif").Open());
                writer.Write(TwoTrackPowerChordScore());
            }

            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var arrangements = importer.ImportTracks(path, tracks.Select(t => t.Id).ToList());
            var rhythm = Assert.Single(arrangements, a => a.Name == "Rhythm");
            var chord = Assert.Single(rhythm.Levels[0].Chords);
            var template = rhythm.ChordTemplates.Single(t => t.Id == chord.TemplateId);

            Assert.Equal(1, template.Fingers[0]);
            Assert.Equal(4, template.Fingers[1]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string TwoTrackPowerChordScore() => """
        <GPIF>
          <Score><Title>Song Style Fixture</Title></Score>
          <MasterTrack><Tracks>0 1</Tracks></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Rhythm</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                <Property name="CapoFret"><Fret>0</Fret></Property>
              </Properties></Staff></Staves>
            </Track>
            <Track id="1">
              <Name>Lead</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                <Property name="CapoFret"><Fret>0</Fret></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars><MasterBar><Time>4/4</Time><Bars>0 1</Bars></MasterBar></MasterBars>
          <Bars>
            <Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar>
            <Bar id="1"><Voices>1 -1 -1 -1</Voices></Bar>
          </Bars>
          <Voices>
            <Voice id="0"><Beats>0</Beats></Voice>
            <Voice id="1"><Beats>1</Beats></Voice>
          </Voices>
          <Beats>
            <Beat id="0"><Rhythm ref="0" /><Notes>0 1</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>2 3</Notes></Beat>
          </Beats>
          <Notes>
            <Note id="0"><Properties>
              <Property name="String"><String>0</String></Property>
              <Property name="Fret"><Fret>3</Fret></Property>
            </Properties></Note>
            <Note id="1"><Properties>
              <Property name="String"><String>1</String></Property>
              <Property name="Fret"><Fret>5</Fret></Property>
            </Properties></Note>
            <Note id="2"><Properties>
              <Property name="String"><String>0</String></Property>
              <Property name="Fret"><Fret>5</Fret></Property>
              <Property name="LeftFingering"><Fingering>I</Fingering></Property>
            </Properties></Note>
            <Note id="3"><Properties>
              <Property name="String"><String>1</String></Property>
              <Property name="Fret"><Fret>7</Fret></Property>
              <Property name="LeftFingering"><Fingering>C</Fingering></Property>
            </Properties></Note>
          </Notes>
          <Rhythms><Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm></Rhythms>
        </GPIF>
        """;

    // ---- helpers ------------------------------------------------------------

    private static Arrangement ImportLegacy(string path)
    {
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static Arrangement ImportGpif(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpfinger-{Guid.NewGuid():N}.gp");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("Content/score.gpif");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(gpif);
            }

            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string GpifScore(bool withFingering)
    {
        string[] letters = ["P", "I", "M", "A", "C"];
        var notes = string.Join("\n    ", Shape.Select((s, i) => $"""
            <Note id="{i}"><Properties>
                  <Property name="String"><String>{s.String}</String></Property>
                  <Property name="Fret"><Fret>{s.Fret}</Fret></Property>
                  {(withFingering
                      ? $"<Property name=\"LeftFingering\"><Fingering>{letters[s.Finger]}</Fingering></Property>"
                      : "")}
                </Properties></Note>
            """));

        return $"""
            <GPIF>
              <Score><Title>Fingering Fixture</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Guitar</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                    <Property name="CapoFret"><Fret>0</Fret></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
              <Voices><Voice id="0"><Beats>0</Beats></Voice></Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0 1 2</Notes></Beat>
              </Beats>
              <Notes>
                {notes}
              </Notes>
              <Rhythms><Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm></Rhythms>
            </GPIF>
            """;
    }
}
