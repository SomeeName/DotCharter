using AlphaTab;
using AlphaTab.Importer;
using DotCharter.Services.Export.Sheet;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// Deterministic, headless-safe tests for the domain → alphaTex exporter. They
/// exercise the alphaTex string AND round-trip it through AlphaTab's *managed*
/// importer (no AlphaSkia / native raster, so no display or fonts needed) to
/// prove the emitted syntax actually parses and carries the intended meaning.
/// </summary>
[Collection("AlphaTab")]
public class AlphaTexExporterTests
{
    private static (ChartProject project, Arrangement arr, ChartLevel level, List<BeatMarker> beats) Fixture()
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);

        var beats = new List<BeatMarker>();
        for (var i = 0; i < 8; i++)
            beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });

        return (project, arr, level, beats);
    }

    private static Chord ChordAt(int beat, int templateId, params (int str, int fret)[] notes)
    {
        var c = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
        foreach (var (s, f) in notes)
            c.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        return c;
    }

    private static string? ParseFirstChordName(string tex)
    {
        var importer = new AlphaTexImporter();
        importer.InitFromString(tex, new Settings());
        var score = importer.ReadScore();
        foreach (var track in score.Tracks)
        {
            foreach (var staff in track.Staves)
            {
                foreach (var bar in staff.Bars)
                {
                    foreach (var voice in bar.Voices)
                    {
                        foreach (var b in voice.Beats)
                        {
                            if (b.HasChord)
                                return b.Chord?.Name;
                        }
                    }
                }
            }
        }

        return null;
    }

    [Fact]
    public void Export_EmitsChordName_FromTemplateName_AndParses()
    {
        var (project, arr, level, beats) = Fixture();
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Name = "Am",
            Frets = new Dictionary<int, int> { { 1, 0 }, { 2, 2 }, { 3, 2 }, { 4, 1 }, { 5, 0 } }
        });
        level.Chords.Add(ChordAt(0, 0, (1, 0), (2, 2), (3, 2), (4, 1), (5, 0)));

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        Assert.Contains("ch \"Am\"", tex);
        Assert.Equal("Am", ParseFirstChordName(tex));
    }

    [Fact]
    public void Export_EscapesChordNameWithSpecialChars()
    {
        var (project, arr, level, beats) = Fixture();
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 3,
            Name = "C#m7",
            Frets = new Dictionary<int, int> { { 5, 4 }, { 4, 6 } }
        });
        level.Chords.Add(ChordAt(0, 3, (5, 4), (4, 6)));

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        Assert.Contains("ch \"C#m7\"", tex);
        Assert.Equal("C#m7", ParseFirstChordName(tex));
    }

    [Fact]
    public void Export_OmitsChordName_WhenUntemplatedAndAllMuted()
    {
        var (project, arr, level, beats) = Fixture();
        var c = new Chord { Position = new FractionalPosition(0), TemplateId = -1 };
        c.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5, Mute = MuteType.Full });
        c.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Mute = MuteType.Full });
        level.Chords.Add(c);

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        Assert.DoesNotContain("ch \"", tex);
        // Still a valid score (renders as a muted chord, no name annotation).
        var importer = new AlphaTexImporter();
        importer.InitFromString(tex, new Settings());
        importer.ReadScore();
    }

    [Fact]
    public void Export_OutputAlwaysParses_ForMixedNotesAndChords()
    {
        var (project, arr, level, beats) = Fixture();
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 1, Name = "E5", Frets = new Dictionary<int, int> { { 0, 0 }, { 1, 2 } }
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 3 });
        level.Chords.Add(ChordAt(2, 1, (0, 0), (1, 2)));
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 2, Fret = 7 });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        // Should not throw — the exporter's output is always valid alphaTex.
        var importer = new AlphaTexImporter();
        importer.InitFromString(tex, new Settings());
        var score = importer.ReadScore();
        Assert.NotEmpty(score.Tracks);
    }
}
