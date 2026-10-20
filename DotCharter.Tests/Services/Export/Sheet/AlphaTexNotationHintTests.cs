using AlphaTab;
using AlphaTab.Importer;
using AlphaTab.Model;
using DotCharter.Services.Export.Sheet;
using Chord = DotCharter.Core.Domain.Guitar.Chord;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// Authored notation hints (score-import provenance) driving the sheet: when a
/// note/chord carries <c>NotationDuration</c>, the exporter renders THAT rhythm
/// — staccato + rests, dotted values, true tuplets — instead of deriving a
/// duration from sustain/onset gaps. Hint-less sounds keep the derived behavior.
/// </summary>
[Collection("AlphaTab")]
public class AlphaTexNotationHintTests
{
    private static (ChartProject project, Arrangement arr, ChartLevel level, List<BeatMarker> beats) Fixture(int bars)
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);

        var beats = new List<BeatMarker>();
        for (var i = 0; i < bars * 4; i++)
            beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });

        return (project, arr, level, beats);
    }

    private static Score Parse(string tex)
    {
        var importer = new AlphaTexImporter();
        importer.InitFromString(tex, new Settings());
        return importer.ReadScore();
    }

    [Fact]
    public void AuthoredEighth_BeforeAGap_RendersEighthPlusRest_NotGapFill()
    {
        var (project, arr, level, beats) = Fixture(1);
        // Authored staccato eighth on beat 1; next onset a full beat later.
        // WITHOUT the hint this gap-fills to a quarter — the hint must win.
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 5,
            NotationDuration = new Fraction(1, 2)
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 7 });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var parsed = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats;
        Assert.Equal(Duration.Eighth, parsed[0].Duration);
        Assert.False(parsed[0].IsRest);
        Assert.True(parsed[1].IsRest);                    // the authored rest
        Assert.Equal(Duration.Eighth, parsed[1].Duration);
    }

    [Fact]
    public void SameInputWithoutHint_GapFillsToAQuarter()
    {
        var (project, arr, level, beats) = Fixture(1);
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 5 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 7 });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var parsed = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats;
        Assert.Equal(Duration.Quarter, parsed[0].Duration);
        Assert.False(parsed[1].IsRest); // next note directly, no rest inserted
    }

    [Fact]
    public void AuthoredDottedQuarter_RendersDotted()
    {
        var (project, arr, level, beats) = Fixture(1);
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 5,
            NotationDuration = new Fraction(3, 2)
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 7 });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var parsed = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats;
        Assert.Equal(Duration.Quarter, parsed[0].Duration);
        Assert.Equal(1.0, parsed[0].Dots);
        Assert.True(parsed[1].IsRest); // the remaining eighth before beat 3
    }

    [Fact]
    public void AuthoredTripletChord_RendersTuplet()
    {
        var (project, arr, level, beats) = Fixture(1);
        for (var k = 0; k < 3; k++)
        {
            var chord = new Chord
            {
                Position = new FractionalPosition(0, new Fraction(k, 3)),
                NotationDuration = new Fraction(1, 3)
            };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 2 });
            level.Chords.Add(chord);
        }

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var parsed = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats;
        Assert.Equal(3.0, parsed[0].TupletNumerator);
        Assert.Equal(3.0, parsed[1].TupletNumerator);
        Assert.Equal(3.0, parsed[2].TupletNumerator);
    }

    [Fact]
    public void HintCappedByNextOnset_WhenEditsMoveNotesCloser()
    {
        // A stale hint (note moved after import) must never overlap the next
        // onset: authored half note, but the next note now sits one beat away.
        var (project, arr, level, beats) = Fixture(1);
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 5,
            NotationDuration = new Fraction(2, 1)
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 7 });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var parsed = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats;
        Assert.Equal(Duration.Quarter, parsed[0].Duration); // clamped to the gap
        Assert.False(parsed[1].IsRest);
    }
}
