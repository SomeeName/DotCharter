using System.Globalization;
using System.Text;
using DotCharter.Services.Import.GuitarPro.Report.AnswerKey;

namespace DotCharter.Tests.Services.Import.GuitarPro.Report.AnswerKey;

/// <summary>
///     Faults of the comparison harness itself, each as a SYNTHETIC pair that differs from its
///     twin only in the way the harness used to misread: the comparison must find nothing.
/// </summary>
public sealed class TabChartHarnessFaultTests
{
    [Fact]
    public void AShortLegatoSlide_IsANote_NotAGrace_WhenItLeavesTheSlideInRange()
    {
        // A sixteenth-note legato slide 5 -> 10 (five frets, so no slide-in ornament), landing on a linked note.
        // The tab holds it for exactly a sixteenth; the chart holds it a little longer.
        var tab = Guitar(Note(0, 5, sustain: 0.25, slideTo: 10, link: true), Note(0.25, 10, sustain: 0.75), Filler(4));
        var chart = Guitar(Note(0, 5, sustain: 0.3, slideTo: 10, link: true), Note(0.3, 10, sustain: 0.7), Filler(4));

        var result = TabChartComparer.Compare(tab, Grid((4, 4), (4, 4)), chart, Grid((4, 4), (4, 4)));

        AssertNothingFound(result);
        Assert.Equal(5, result.Counts.TabSounds);
        Assert.Equal(5, result.Counts.ChartSounds);
    }

    [Fact]
    public void ASlideInOrnament_IsDroppedOnBothSides_AndItsLandingNoteIsKept()
    {
        // The importer's slide-in (origin two frets below, a sixteenth, linked) against a chart whose origin
        // sits one fret below: the landing note counts once on each side, flagged SlideIn.
        var tab = Guitar(Note(0, 8, sustain: 0.25, slideTo: 10, link: true), Note(0.25, 10, sustain: 0.75), Filler(4));
        var chart = Guitar(Note(0, 9, sustain: 0.25, slideTo: 10, link: true), Note(0.25, 10, sustain: 0.75), Filler(4));

        var result = TabChartComparer.Compare(tab, Grid((4, 4), (4, 4)), chart, Grid((4, 4), (4, 4)));

        AssertNothingFound(result);
        Assert.Equal(5, result.Counts.TabSounds);
        Assert.Equal(1, result.Counts.Technique(SoundTechnique.SlideIn).Both);
    }

    [Fact]
    public void ALegatoSlideTheChartLeavesOut_IsNotASlideOut()
    {
        var tab = Guitar(Note(0, 5, sustain: 1, slideTo: 7, link: true), Note(1, 7, sustain: 1), Filler(4));
        var chart = Guitar(Note(0, 5, sustain: 1, link: true), Note(1, 7, sustain: 1), Filler(4));

        var result = TabChartComparer.Compare(tab, Grid((4, 4), (4, 4)), chart, Grid((4, 4), (4, 4)));

        AssertNothingFound(result);
    }

    [Fact]
    public void PitchedSlidesAndSlideOuts_AreSeparateClasses()
    {
        var grid = Grid((4, 4), (4, 4));
        static GuitarNote Slide(bool unpitched) => Note(0, 5, sustain: 1, slideTo: 9, unpitched: unpitched);

        AssertNothingFound(TabChartComparer.Compare(Guitar(Slide(false), Filler(4)), grid, Guitar(Slide(false), Filler(4)), grid));
        AssertNothingFound(TabChartComparer.Compare(Guitar(Slide(true), Filler(4)), grid, Guitar(Slide(true), Filler(4)), grid));

        var mixed = TabChartComparer.Compare(Guitar(Slide(false), Filler(4)), grid, Guitar(Slide(true), Filler(4)), grid);

        Assert.Equal(1, mixed.Differences.Count(d => d.Class == DifferenceClasses.Technique(SoundTechnique.Slide, missing: false)));
        Assert.Equal(1, mixed.Differences.Count(d => d.Class == DifferenceClasses.Technique(SoundTechnique.SlideOut, missing: true)));
    }

    [Fact]
    public void AFiveStringBass_AgainstAFourStringChart_IsComparedByPitch()
    {
        // B E A D G on the tab (indices 0-4) against E A D G on the chart (0-3): the same notes.
        var tab = Part(ArrangementType.Bass, [23, 28, 33, 38, 43], Cycle(1, 2, 3, 4));
        var chart = Part(ArrangementType.Bass, [28, 33, 38, 43], Cycle(0, 1, 2, 3));

        var result = TabChartComparer.Compare(tab, Grid((4, 4), (4, 4)), chart, Grid((4, 4), (4, 4)));

        AssertNothingFound(result);
        Assert.Equal(8, result.Counts.ExactSounds);
    }

    [Fact]
    public void ASevenStringGuitar_AgainstASixStringChart_IsComparedByPitch()
    {
        var tab = Part(ArrangementType.Lead, [35, 40, 45, 50, 55, 59, 64], Cycle(1, 2, 3, 4));
        var chart = Part(ArrangementType.Lead, [40, 45, 50, 55, 59, 64], Cycle(0, 1, 2, 3));

        var result = TabChartComparer.Compare(tab, Grid((4, 4), (4, 4)), chart, Grid((4, 4), (4, 4)));

        AssertNothingFound(result);
        Assert.Equal(8, result.Counts.ExactSounds);
    }

    [Fact]
    public void ABassOnTheGuitarPath_AgainstOneOnTheBassPath_KeepsItsStrings()
    {
        // The same five-string bass written an octave apart (guitar path against bass path): same strings.
        var tab = Part(ArrangementType.Lead, [35, 40, 45, 50, 55], Cycle(0, 1, 2, 3));
        var chart = Part(ArrangementType.Bass, [23, 28, 33, 38, 43, 52], Cycle(0, 1, 2, 3));

        var result = TabChartComparer.Compare(tab, Grid((4, 4), (4, 4)), chart, Grid((4, 4), (4, 4)));

        AssertNothingFound(result);
        Assert.Equal(8, result.Counts.ExactSounds);
    }

    [Fact]
    public void ThePassageBarredDifferently_IsComparedOnAbsoluteBeats()
    {
        // Twelve quarter notes: three bars of 4/4 on the tab, four bars of 3/4 on the chart.
        var result = TabChartComparer.Compare(Guitar(Twelve()), Grid((4, 4), (4, 4), (4, 4)), Guitar(Twelve()), Grid((3, 4), (3, 4), (3, 4), (3, 4)));

        AssertNothingFound(result);
        Assert.Equal(12, result.Counts.ExactSounds);
        Assert.Equal(1.0, result.Counts.OnsetF1);
    }

    [Fact]
    public void SixEighthsAgainstThreeQuarters_AreTheSameBar()
    {
        // 6/8 counts six eighth-note beats, 3/4 three quarter-note beats: the same musical time.
        var tab = Guitar(Enumerable.Range(0, 3).Select(b => Note(2 * b, 3 + 2 * b, sustain: 0.5)).ToArray());
        var chart = Guitar(Enumerable.Range(0, 3).Select(b => Note(b, 3 + 2 * b, sustain: 1)).ToArray());

        var result = TabChartComparer.Compare(tab, Grid((6, 8), (6, 8)), chart, Grid((3, 4), (3, 4)));

        AssertNothingFound(result);
        Assert.Equal(3, result.Counts.ExactSounds);
    }

    [Fact]
    public void APickupBar_StillAlignsAtTheBarLineThatEndsIt()
    {
        // A one-beat pickup on the tab; the chart carries it as the last beat of a full bar.
        var tab = Guitar(Note(0, 3, sustain: 1), Note(1, 5, sustain: 1), Note(5, 7, sustain: 1));
        var chart = Guitar(Note(3, 3, sustain: 1), Note(4, 5, sustain: 1), Note(8, 7, sustain: 1));

        var result = TabChartComparer.Compare(tab, Grid((1, 4), (4, 4), (4, 4)), chart, Grid((4, 4), (4, 4), (4, 4)));

        AssertNothingFound(result);
        Assert.Equal(3, result.Counts.ExactSounds);
    }

    [Fact]
    public void AMoveRPhrase_IsResolvedByTheImporter_NotByTheHarness()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-tab-vs-chart-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(path, MoveRSong(), new UTF8Encoding(false));

            var (project, moved) = TabChartSections.LoadChart(path);
            var sections = TabChartSections.Resolve(project.Arrangements[0]);

            Assert.Equal(1, moved);
            var riff = Assert.Single(sections, s => s.Name == "riff");
            // moveR2 on the beat at 1 s: the second note from there is the one at 1.5 s, beat 3 of this 0.5 s grid.
            Assert.True(Math.Abs(riff.Position.DoubleValue - 3.0) < 0.01, riff.Position.DoubleValue.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string MoveRSong()
    {
        var ebeats = string.Concat(Enumerable.Range(0, 8).Select(i =>
            $"<ebeat time=\"{(i * 0.5).ToString("0.000", CultureInfo.InvariantCulture)}\"{(i % 4 == 0 ? $" measure=\"{i / 4 + 1}\"" : "")} />"));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?><song version=\"7\"><arrangement>Lead</arrangement><offset>0</offset>"
               + "<songLength>4</songLength><tuning string0=\"0\" string1=\"0\" string2=\"0\" string3=\"0\" string4=\"0\" string5=\"0\" />"
               + $"<ebeats count=\"8\">{ebeats}</ebeats>"
               + "<phrases count=\"2\"><phrase disparity=\"0\" ignore=\"0\" maxDifficulty=\"0\" name=\"COUNT\" solo=\"0\" />"
               + "<phrase disparity=\"0\" ignore=\"0\" maxDifficulty=\"0\" name=\"moveR2\" solo=\"0\" /></phrases>"
               + "<phraseIterations count=\"2\"><phraseIteration time=\"0.000\" phraseId=\"0\" variation=\"\" />"
               + "<phraseIteration time=\"1.000\" phraseId=\"1\" variation=\"\" /></phraseIterations>"
               + "<sections count=\"1\"><section name=\"riff\" number=\"1\" startTime=\"1.000\" /></sections>"
               + "<levels count=\"1\"><level difficulty=\"0\"><notes count=\"3\">"
               + "<note time=\"1.250\" string=\"2\" fret=\"3\" sustain=\"0\" /><note time=\"1.500\" string=\"2\" fret=\"5\" sustain=\"0\" />"
               + "<note time=\"1.750\" string=\"2\" fret=\"7\" sustain=\"0\" /></notes><chords count=\"0\" />"
               + "<anchors count=\"1\"><anchor time=\"1.250\" fret=\"3\" width=\"4.000\" /></anchors>"
               + "<handShapes count=\"0\" /></level></levels></song>";
    }

    private static void AssertNothingFound(ArrangementComparison result)
    {
        Assert.True(result.Differences.Count == 0,
            string.Join("; ", result.Differences.Select(d => $"{d.Class} tab {d.TabMeasure} chart {d.ChartMeasure} @{d.Beat}: {d.Tab} / {d.Chart}")));
        Assert.Equal(1.0, result.Counts.ExactF1);
    }

    /// <summary>A standard six-string guitar part with the given notes on its hardest level.</summary>
    private static Arrangement Guitar(params object[] notes) => Part(ArrangementType.Lead, [40, 45, 50, 55, 59, 64], notes);

    /// <summary>A part whose strings open on the given MIDI pitches (low first).</summary>
    private static Arrangement Part(ArrangementType type, int[] open, params object[] notes)
    {
        var bass = type == ArrangementType.Bass;
        var part = new Arrangement { Name = "Part", ArrangementType = type, StringCount = open.Length };
        for (var i = 0; i < open.Length; i++)
        {
            var standard = StandardOpenStrings.GuitarLowEMidi + StandardOpenStrings.SemitonesAboveLowE(open.Length, i) + (bass ? -12 : 0);
            part.TuningPitches.Add(new TuningOffset(open[i] - standard));
        }

        var level = new ChartLevel();
        foreach (var note in notes.SelectMany(n => n is IEnumerable<GuitarNote> many ? many : [(GuitarNote)n]))
            level.Notes.Add(note);
        part.Levels.Add(level);
        return part;
    }

    /// <summary>A note on string 2 at grid beat <paramref name="at" />.</summary>
    private static GuitarNote Note(double at, int fret, double sustain, bool link = false, int? slideTo = null, bool unpitched = false) => new()
    {
        Position = Pos(at),
        Sustain = Pos(sustain),
        StringIndex = 2,
        Fret = fret,
        SlideTo = slideTo,
        UnpitchedSlide = unpitched,
        LinkNext = link
    };

    /// <summary>Four quarter notes on string 3 from <paramref name="firstBeat" />, so the case under test sits in front of a steady bar.</summary>
    private static GuitarNote[] Filler(int firstBeat) =>
    [
        .. Enumerable.Range(0, 4).Select(i => new GuitarNote { Position = new FractionalPosition(firstBeat + i), Sustain = Pos(0.5), StringIndex = 3, Fret = 3 + i })
    ];

    /// <summary>Eight quarter notes over two bars, cycling through the four given strings.</summary>
    private static GuitarNote[] Cycle(int s0, int s1, int s2, int s3) =>
    [
        .. Enumerable.Range(0, 8).Select(b => new GuitarNote
        {
            Position = new FractionalPosition(b),
            Sustain = Pos(0.5),
            StringIndex = new[] { s0, s1, s2, s3 }[b % 4],
            Fret = 2 + b
        })
    ];

    /// <summary>Twelve quarter notes, each a different fret, so a stretched bar cannot match by accident.</summary>
    private static GuitarNote[] Twelve() =>
    [
        .. Enumerable.Range(0, 12).Select(b => new GuitarNote { Position = new FractionalPosition(b), Sustain = Pos(0.5), StringIndex = 2, Fret = 1 + b })
    ];

    private static FractionalPosition Pos(double beats)
    {
        var whole = (int)Math.Floor(beats + 1e-9);
        var rest = beats - whole;
        return new FractionalPosition(whole, rest < 1e-9 ? Fraction.Zero : new Fraction((int)Math.Round(rest * 64), 64));
    }

    /// <summary>A beat map of bars <c>(beats, denominator)</c>, 600 ms per beat.</summary>
    private static List<BeatMarker> Grid(params (int Beats, int Denominator)[] bars)
    {
        var beats = new List<BeatMarker>();
        foreach (var (count, denominator) in bars)
        {
            for (var i = 0; i < count; i++)
                beats.Add(new BeatMarker { Time = beats.Count * 600.0, Bpm = 100, BeatsInMeasure = count, NoteDenominator = denominator, IsMeasure = i == 0 });
        }

        return beats;
    }
}
