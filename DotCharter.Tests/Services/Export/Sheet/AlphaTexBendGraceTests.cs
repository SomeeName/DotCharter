using AlphaTab;
using AlphaTab.Importer;
using AlphaTab.Model;
using DotCharter.Services.Export.Sheet;
using Chord = DotCharter.Core.Domain.Guitar.Chord;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// Bends and grace notes reaching the notation. Bend curves map the chart's
/// <see cref="BendValue"/> list (Step in SEMITONES) onto alphaTex's
/// <c>b (v0 v1 …)</c> quarter-step values (4 = whole tone, parser-verified);
/// grace hints (<see cref="GuitarNote.GraceFret"/>) emit a 16th before-beat
/// grace beat that consumes no bar time. All verified through the bundled
/// AlphaTab 1.8.3 parser — the same one that renders the sheet.
/// </summary>
[Collection("AlphaTab")]
public class AlphaTexBendGraceTests
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

    private static GuitarNote NoteAt(int beat, int stringIndex, int fret, int sustainBeats = 1) => new()
    {
        Position = new FractionalPosition(beat),
        StringIndex = stringIndex,
        Fret = fret,
        Sustain = new FractionalPosition(sustainBeats)
    };

    [Fact]
    public void WholeToneBend_RendersAsBendCurve()
    {
        var (project, arr, level, beats) = Fixture(1);
        var note = NoteAt(0, 2, 7);
        // Struck unbent, bent a whole tone (2 semitones) by mid-sustain.
        note.BendValues.Add(new BendValue(new FractionalPosition(0, new Fraction(1, 2)), 2.0));
        level.Notes.Add(note);

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;
        Assert.Contains("b (0 4)", tex);

        var parsed = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats[0].Notes[0];
        Assert.Equal(BendType.Bend, parsed.BendType);
        Assert.Equal(4.0, parsed.MaxBendPoint!.Value);
    }

    [Fact]
    public void BendRelease_RendersBothLegs()
    {
        var (project, arr, level, beats) = Fixture(1);
        var note = NoteAt(0, 2, 7, sustainBeats: 2);
        note.BendValues.Add(new BendValue(new FractionalPosition(0, new Fraction(1, 2)), 1.0));
        note.BendValues.Add(new BendValue(new FractionalPosition(1), 0.0));
        level.Notes.Add(note);

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;
        Assert.Contains("b (0 2 0)", tex);

        var parsed = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats[0].Notes[0];
        Assert.Equal(BendType.BendRelease, parsed.BendType);
    }

    [Fact]
    public void PrebendAtOnset_RendersAsPrebend()
    {
        var (project, arr, level, beats) = Fixture(1);
        var note = NoteAt(0, 2, 7);
        note.BendValues.Add(new BendValue(new FractionalPosition(0), 2.0)); // bent BEFORE the strike
        level.Notes.Add(note);

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;
        Assert.Contains("b (4 4)", tex); // held prebend, no leading 0

        var parsed = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats[0].Notes[0];
        Assert.NotEqual(BendType.None, parsed.BendType);
        Assert.Equal(4.0, parsed.MaxBendPoint!.Value);
    }

    [Fact]
    public void ChordNoteBend_RendersOnThatNoteOnly()
    {
        var (project, arr, level, beats) = Fixture(1);
        var chord = new Chord { Position = new FractionalPosition(0) };
        var bent = new ChordNote { StringIndex = 2, Fret = 7, Sustain = new FractionalPosition(1) };
        bent.BendValues.Add(new BendValue(new FractionalPosition(0, new Fraction(1, 2)), 1.0));
        chord.ChordNotes.Add(bent);
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 5, Sustain = new FractionalPosition(1) });
        level.Chords.Add(chord);

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var notes = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats[0].Notes;
        Assert.Equal(2, notes.Count);
        var byString = notes.ToDictionary(n => (int)n.String);
        Assert.Equal(BendType.Bend, byString[3].BendType); // domain 2 = model String 3 (1 = lowest)
        Assert.Equal(BendType.None, byString[4].BendType); // domain 3 = model String 4

    }

    [Fact]
    public void GraceHint_EmitsABeforeBeatGraceBeat_WithoutConsumingBarTime()
    {
        var (project, arr, level, beats) = Fixture(2);
        for (var i = 0; i < 4; i++)
        {
            var n = new GuitarNote { Position = new FractionalPosition(i), StringIndex = 2, Fret = 5 };
            if (i == 0) n.GraceFret = 3;
            level.Notes.Add(n);
        }
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 2, Fret = 8 });

        var export = AlphaTexExporter.Export(project, arr, level, beats);
        Assert.Contains("{gr}", export.AlphaTex);

        var bars = Parse(export.AlphaTex).Tracks[0].Staves[0].Bars;
        var bar0 = bars[0].Voices[0].Beats;

        // 1 grace + 4 real quarters, no spill into bar 1.
        Assert.Equal(5, bar0.Count);
        Assert.Equal(GraceType.BeforeBeat, bar0[0].GraceType);
        Assert.Equal(3.0, bar0[0].Notes[0].Fret);
        Assert.All(bar0.Skip(1), b => Assert.Equal(GraceType.None, b.GraceType));
        Assert.Equal(8.0, bars[1].Voices[0].Beats[0].Notes[0].Fret); // bar 1 intact

        // The grace beat is NOT a click target: the beat map records only the
        // real beats (renderer filters GraceType from its bounds to match).
        Assert.Equal(4, export.BeatMap.Count(b => b.MeasureIndex == 0));
    }

    [Fact]
    public void SingleNoteGrace_KeepsItsExactUnparenthesisedForm()
    {
        // Byte-form pin for the single-note path: one hint must still emit the
        // bare `fret.string{gr}` beat, NOT a parenthesised one-note beat.
        var (project, arr, level, beats) = Fixture(1);
        for (var i = 0; i < 4; i++)
        {
            var n = new GuitarNote { Position = new FractionalPosition(i), StringIndex = 2, Fret = 5 };
            if (i == 0) n.GraceFret = 3;
            level.Notes.Add(n);
        }

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        Assert.Contains(":16 3.4{gr} :4 ", tex);
        Assert.DoesNotContain("({gr}", tex);
        Assert.DoesNotContain("(3.4{gr})", tex);
    }

    [Fact]
    public void ChordGraceHints_EmitOneGraceBeat_CarryingEveryGracedString()
    {
        // Guitar Pro graces a strum with one grace note per string; alphaTex
        // renders that as a single {gr} beat holding all of them.
        var (project, arr, level, beats) = Fixture(2);
        var chord = new Chord { Position = new FractionalPosition(0) };
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 2, Fret = 5, Sustain = new FractionalPosition(1), GraceFret = 3 });
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 3, Fret = 7, Sustain = new FractionalPosition(1), GraceFret = 5 });
        level.Chords.Add(chord);
        for (var i = 1; i < 4; i++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = 2, Fret = 5 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 2, Fret = 8 });

        var export = AlphaTexExporter.Export(project, arr, level, beats);
        // Domain string 2 → alphaTex string 4, domain 3 → alphaTex 3.
        Assert.Contains("(3.4{gr} 5.3{gr})", export.AlphaTex);

        var bars = Parse(export.AlphaTex).Tracks[0].Staves[0].Bars;
        var bar0 = bars[0].Voices[0].Beats;

        // 1 grace + 4 real beats — the grace still consumes no bar time.
        Assert.Equal(5, bar0.Count);
        Assert.Equal(GraceType.BeforeBeat, bar0[0].GraceType);
        Assert.All(bar0.Skip(1), b => Assert.Equal(GraceType.None, b.GraceType));

        var graceFrets = bar0[0].Notes.ToDictionary(n => (int)n.String, n => (int)n.Fret);
        Assert.Equal(2, graceFrets.Count);
        // NOTE the two conventions in play: the alphaTex TEXT numbers strings
        // 1 = highest (so the emitted token above is "3.4"), but AlphaTab's
        // parsed model numbers them 1 = lowest, i.e. model String = domain + 1.
        // See the byString asserts in the bend test above.
        Assert.Equal(3, graceFrets[3]); // domain 2 = model String 3
        Assert.Equal(5, graceFrets[4]); // domain 3 = model String 4

        Assert.Equal(8.0, bars[1].Voices[0].Beats[0].Notes[0].Fret); // bar 1 intact
        Assert.Equal(4, export.BeatMap.Count(b => b.MeasureIndex == 0)); // grace is not a click target
    }

    [Fact]
    public void ChordWithoutGraceHints_EmitsNoGraceBeatAtAll()
    {
        // The additive-change proof: a chord chart carrying no grace hint must
        // produce exactly the output it always did — no {gr} token anywhere and
        // no extra beat in the bar.
        var (project, arr, level, beats) = Fixture(1);
        var chord = new Chord { Position = new FractionalPosition(0) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5, Sustain = new FractionalPosition(1) });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 7, Sustain = new FractionalPosition(1) });
        level.Chords.Add(chord);
        for (var i = 1; i < 4; i++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = 2, Fret = 5 });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        Assert.DoesNotContain("{gr}", tex);
        var bar0 = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats;
        Assert.Equal(4, bar0.Count);
        Assert.All(bar0, b => Assert.Equal(GraceType.None, b.GraceType));
    }

    [Fact]
    public void NoteAndChordGraceOnOneOnset_ShareASingleGraceBeat()
    {
        // A note and a chord merged into one slot contribute to the SAME grace
        // beat, and a string may only be graced once.
        var (project, arr, level, beats) = Fixture(1);
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 3,
            Sustain = new FractionalPosition(1), GraceFret = 1
        });
        var chord = new Chord { Position = new FractionalPosition(0) };
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 0, Fret = 3, Sustain = new FractionalPosition(1), GraceFret = 2 }); // loses to the note
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 2, Fret = 5, Sustain = new FractionalPosition(1), GraceFret = 4 });
        level.Chords.Add(chord);
        for (var i = 1; i < 4; i++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = 4, Fret = 5 });

        var export = AlphaTexExporter.Export(project, arr, level, beats);
        var bar0 = Parse(export.AlphaTex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats;

        Assert.Equal(GraceType.BeforeBeat, bar0[0].GraceType);
        var graceFrets = bar0[0].Notes.ToDictionary(n => (int)n.String, n => (int)n.Fret);
        Assert.Equal(2, graceFrets.Count);
        // AlphaTab's parsed model numbers strings 1 = lowest (model = domain + 1),
        // which is the inverse of the alphaTex text numbering.
        Assert.Equal(1, graceFrets[1]); // domain string 0 = model String 1: the NOTE's hint won
        Assert.Equal(4, graceFrets[3]); // domain string 2 = model String 3
        Assert.Equal(1, bar0.Count(b => b.GraceType == GraceType.BeforeBeat));
    }

    [Fact]
    public void SidecarVoiceNotes_CarryBendsAndGraces_Too()
    {
        var (project, arr, level, beats) = Fixture(1);
        level.Notes.Add(NoteAt(0, 2, 5, sustainBeats: 0));

        var voice = new NotationVoice { VoiceIndex = 1 };
        var bent = new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 7,
            Sustain = new FractionalPosition(2), NotationDuration = new Fraction(4, 1), GraceFret = 5
        };
        bent.BendValues.Add(new BendValue(new FractionalPosition(1), 2.0));
        voice.Notes.Add(bent);
        arr.NotationVoices.Add(voice);

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var v1 = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[1].Beats;
        Assert.Equal(GraceType.BeforeBeat, v1[0].GraceType);
        Assert.Equal(BendType.Bend, v1[1].Notes[0].BendType);
    }
}
