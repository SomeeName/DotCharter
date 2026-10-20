using AlphaTab;
using AlphaTab.Importer;
using DotCharter.Services.Export.Sheet;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
///     Asserts the exporter's output through AlphaTab's own MANAGED importer —
///     the ENGRAVED score, not the exporter's self-report.
///     <para>
///         <c>AlphaTexIntegrityTests</c> proves "every bar sums to its time
///         signature" from <c>ExportResult.BeatMap</c>, which is the right proxy
///         for the emit paths that fill it. It is blind to anything appended
///         OUTSIDE those paths and to anything the parser rejects — and both
///         happened: a level with no sounds got a stray quarter rest after its
///         last bar (5 beats of content in a 4/4 bar, invisible to the beat map
///         because the fallback writes no map entry), and a bar whose time
///         signature had a non-power-of-two denominator threw
///         <c>AlphaTexErrorWithDiagnostics</c> and took the WHOLE document down.
///         Parsing is the only assertion that sees either.
///     </para>
///     Pure + managed importer: no AlphaSkia, no display, no fonts.
/// </summary>
[Collection("AlphaTab")]
public class AlphaTexEngravedScoreTests
{
    private static (ChartProject Project, Arrangement Arr, ChartLevel Level, List<BeatMarker> Beats) Fixture(
        int numerator = 4, int denominator = 4, int bars = 4)
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);

        var beats = new List<BeatMarker>();
        var msPerBeat = 60_000.0 / 120.0 * 4.0 / denominator;
        for (var i = 0; i < numerator * bars; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * msPerBeat,
                Bpm = 120,
                BeatsInMeasure = numerator,
                NoteDenominator = denominator,
                IsMeasure = i % numerator == 0
            });
        }

        return (project, arr, level, beats);
    }

    private static FractionalPosition At(double beats)
    {
        var whole = (int)Math.Floor(beats);
        return new FractionalPosition(whole, new Fraction((int)Math.Round((beats - whole) * 192), 192));
    }

    private static GuitarNote Note(double beat, int stringIndex, int fret, double sustainBeats = 0) => new()
    {
        Position = At(beat),
        StringIndex = stringIndex,
        Fret = fret,
        Sustain = At(sustainBeats)
    };

    private static AlphaTab.Model.Score Parse(string tex)
    {
        var importer = new AlphaTexImporter();
        importer.InitFromString(tex, new Settings());
        return importer.ReadScore();
    }

    /// <summary>
    ///     Every engraved bar holds exactly its own master-bar duration. AlphaTab
    ///     fills a bar from the durations it was handed, so a surplus is a beat
    ///     spilling past the barline and a deficit is a hole.
    /// </summary>
    private static void AssertEngravedBarsHoldTheirMeter(string because, string tex)
    {
        var score = Parse(tex);
        var staff = score.Tracks[0].Staves[0];
        for (var bar = 0; bar < staff.Bars.Count; bar++)
        {
            var sum = 0.0;
            foreach (var beat in staff.Bars[bar].Voices[0].Beats) sum += beat.PlaybackDuration;
            var expected = score.MasterBars[bar].CalculateDuration();
            Assert.True(Math.Abs(sum - expected) < 0.5,
                $"{because}: engraved bar {bar} holds {sum} ticks of a {expected}-tick bar.\nalphaTex: {tex}");
        }
    }

    // ------------------------------------------------------------------
    // A level with no sounds
    // ------------------------------------------------------------------

    /// <summary>
    ///     A chart whose ACTIVE level is still empty (every new arrangement, and
    ///     every higher difficulty before it is filled in) is all rests — and the
    ///     "nothing was emitted, give the importer a beat" fallback fired anyway,
    ///     appending <c>:4 r</c> after the final bar's whole rest. That quarter
    ///     rest lands INSIDE the last bar: measured 4800 ticks in a 3840-tick bar.
    /// </summary>
    [Fact]
    public void ALevelWithNoSounds_EngravesWholeRests_AndTheLastBarIsNotOverfilled()
    {
        var (p, a, l, b) = Fixture(bars: 2);
        var result = AlphaTexExporter.Export(p, a, l, b);

        AssertEngravedBarsHoldTheirMeter("a level with no sounds", result.AlphaTex);

        var score = Parse(result.AlphaTex);
        Assert.Equal(2, score.Tracks[0].Staves[0].Bars.Count);
        // One whole rest per bar, not a whole rest plus a stray quarter.
        Assert.Single(score.Tracks[0].Staves[0].Bars[^1].Voices[0].Beats);
    }

    /// <summary>
    ///     The fallback itself still exists for the case it was written for: no
    ///     beat grid at all means no bars, and the document needs one beat to be
    ///     renderable.
    /// </summary>
    [Fact]
    public void AChartWithNoBeatGrid_StillEmitsARenderableBeat()
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);
        level.Notes.Add(Note(0, 0, 3));

        var result = AlphaTexExporter.Export(project, arr, level, []);

        Assert.Contains(":4 r", result.AlphaTex);
        var score = Parse(result.AlphaTex);
        Assert.NotEmpty(score.Tracks[0].Staves[0].Bars[0].Voices[0].Beats);
    }

    // ------------------------------------------------------------------
    // Time-signature denominators
    // ------------------------------------------------------------------

    /// <summary>
    ///     <see cref="BeatMarker.NoteDenominator" /> is unvalidated everywhere and
    ///     the beat dialog offers 1..64 in steps of ONE, so <c>4/3</c>, <c>4/5</c>,
    ///     <c>4/6</c> and <c>4/7</c> are each one keystroke away. Every one of them
    ///     emitted <c>\ts (4 d)</c>, which the bundled parser rejects outright —
    ///     the whole pane fell to its error state and the PDF export threw, over
    ///     one beat's meter. The document must survive, and the bar must still
    ///     hold what it declares.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(64)]
    public void ATimeSignatureDenominatorNotationCannotExpress_StillParses(int denominator)
    {
        var (p, a, l, b) = Fixture(4, denominator, 2);
        l.Notes.Add(Note(0, 0, 3));
        l.Notes.Add(Note(1, 1, 5));

        var result = AlphaTexExporter.Export(p, a, l, b);

        // Parses at all — this is the half that used to throw.
        AssertEngravedBarsHoldTheirMeter($"denominator {denominator}", result.AlphaTex);
        var score = Parse(result.AlphaTex);
        Assert.All(score.MasterBars, mb =>
            Assert.True(mb.TimeSignatureDenominator is 1 or 2 or 4 or 8 or 16 or 32,
                $"engraved denominator {mb.TimeSignatureDenominator} is not a note value."));
    }

    /// <summary>A meter notation CAN express is passed through untouched.</summary>
    [Theory]
    [InlineData(4, 4)]
    [InlineData(7, 8)]
    [InlineData(5, 16)]
    [InlineData(2, 2)]
    public void ARealMeter_IsEngravedExactlyAsAuthored(int numerator, int denominator)
    {
        var (p, a, l, b) = Fixture(numerator, denominator, 2);
        l.Notes.Add(Note(0, 0, 3));

        var result = AlphaTexExporter.Export(p, a, l, b);

        AssertEngravedBarsHoldTheirMeter($"{numerator}/{denominator}", result.AlphaTex);
        var score = Parse(result.AlphaTex);
        Assert.Equal(numerator, score.MasterBars[0].TimeSignatureNumerator);
        Assert.Equal(denominator, score.MasterBars[0].TimeSignatureDenominator);
    }

    // ------------------------------------------------------------------
    // The tuning header
    // ------------------------------------------------------------------

    /// <summary>
    ///     A chart states its tuning twice — per-string OFFSETS and note LETTERS
    ///     — and an arrangement that carries only the letters has an EMPTY offsets
    ///     vector. The exporter read `TuningPitches` directly with a 0 default, so
    ///     such a chart engraved as E STANDARD: every fretted pitch on the dropped
    ///     string a tone sharp, while the PDF's own tuning legend (which reads the
    ///     letters) printed "Tuning: D A D G B E" on the same page.
    ///     `TuningOffsetResolution` is the one answer every other writer already
    ///     goes through.
    /// </summary>
    [Fact]
    public void ATuningStatedOnlyAsNoteLetters_IsEngravedAsThatTuning()
    {
        var (p, a, l, b) = Fixture(bars: 1);
        a.Tuning = ["D", "A", "D", "G", "B", "E"];
        a.TuningPitches.Clear();
        l.Notes.Add(Note(0, 0, 0));

        var score = Parse(AlphaTexExporter.Export(p, a, l, b).AlphaTex);

        // AlphaTab lists staff tuning HIGHEST string first, so the low string is last.
        var tuning = score.Tracks[0].Staves[0].Tuning;
        Assert.Equal(38, tuning[^1]); // D2, not E2 (40)
        Assert.Equal(64, tuning[0]);  // the rest of the neck is untouched
    }

    /// <summary>
    ///     Authored offsets still win over the letters — the resolution is
    ///     deliberately conservative, and every chart that has them must export
    ///     exactly as before.
    /// </summary>
    [Fact]
    public void AuthoredOffsets_StillDecideTheEngravedTuning()
    {
        var (p, a, l, b) = Fixture(bars: 1);
        a.Tuning = ["E", "A", "D", "G", "B", "E"]; // stale letters
        a.TuningPitches.Clear();
        foreach (var step in new[] { -2, 0, 0, 0, 0, 0 }) a.TuningPitches.Add(new TuningOffset(step));
        l.Notes.Add(Note(0, 0, 0));

        var score = Parse(AlphaTexExporter.Export(p, a, l, b).AlphaTex);

        Assert.Equal(38, score.Tracks[0].Staves[0].Tuning[^1]);
    }

    // ------------------------------------------------------------------
    // Where the barlines are
    // ------------------------------------------------------------------

    /// <summary>
    ///     `BeatMarker.IsMeasure` is what every other consumer counts bars by —
    ///     the bar numbers the user reads, the measure snap, the SNG export. The
    ///     exporter counted `BeatsInMeasure` beats at a time instead, so a chart
    ///     with a two-beat PICKUP had its first barline two beats early and every
    ///     barline after it: the note on the chart's first real downbeat engraved
    ///     as beat 3 of bar 1.
    /// </summary>
    [Fact]
    public void APickupBar_PutsTheFirstRealDownbeatOnABarline()
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);

        // Two pickup beats, then three full 4/4 bars — barlines at beats 2, 6, 10.
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 14; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * 500.0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4,
                IsMeasure = i >= 2 && (i - 2) % 4 == 0
            });
        }

        level.Notes.Add(Note(0, 0, 1));  // in the pickup
        level.Notes.Add(Note(2, 0, 2));  // the first real downbeat
        level.Notes.Add(Note(6, 0, 3));  // the next one

        var result = AlphaTexExporter.Export(project, arr, level, beats);

        AssertEngravedBarsHoldTheirMeter("a two-beat pickup", result.AlphaTex);

        // The pickup is its own short bar; the real bars start where the chart says.
        Assert.Equal(0, result.MeasureSpans[0].FirstBeatIndex);
        Assert.Equal(2, result.MeasureSpans[1].FirstBeatIndex);
        Assert.Equal(6, result.MeasureSpans[2].FirstBeatIndex);
        Assert.Equal(10, result.MeasureSpans[3].FirstBeatIndex);

        // …and each real downbeat is the FIRST thing in its bar, not a beat inside it.
        foreach (var barIndex in new[] { 1, 2 })
        {
            var head = result.BeatMap.First(sb => sb.MeasureIndex == barIndex);
            Assert.Equal(AlphaTexExporter.SheetBeatKind.Head, head.Kind);
            Assert.Equal(0, head.StartTick);
        }
    }

    /// <summary>
    ///     A well-formed grid's flags agree with counting <c>BeatsInMeasure</c>, so
    ///     honouring them may not move a single barline there.
    /// </summary>
    [Fact]
    public void ARegularGrid_KeepsExactlyTheBarlinesItAlwaysHad()
    {
        var (p, a, l, b) = Fixture(4, 4, 4);
        for (var i = 0; i < 16; i++) l.Notes.Add(Note(i, i % 6, 3));

        var result = AlphaTexExporter.Export(p, a, l, b);

        Assert.Equal(4, result.MeasureSpans.Count);
        for (var bar = 0; bar < 4; bar++)
        {
            Assert.Equal(bar * 4, result.MeasureSpans[bar].FirstBeatIndex);
            Assert.Equal(4, result.MeasureSpans[bar].BeatCount);
        }
    }

    /// <summary>
    ///     A grid whose flags are NOT a barline set must fall back to the declared
    ///     meter, or the song engraves as a handful of bars hundreds of beats long.
    ///     Two shapes, and they are rejected by DIFFERENT halves of the check:
    ///     `TempoService` marks beat 0 and nothing else (too few), and a
    ///     half-finished set leaves gaps far longer than any bar.
    /// </summary>
    [Theory]
    [InlineData(32)] // only beat 0 is flagged
    [InlineData(16)] // beats 0 and 16 — two flags, neither a barline
    public void AGridWithNoRealBarlineFlags_FallsBackToTheDeclaredMeter(int flagStride)
    {
        var (p, a, l, b) = Fixture(4, 4, 8);
        for (var i = 0; i < b.Count; i++) b[i].IsMeasure = i % flagStride == 0;
        l.Notes.Add(Note(0, 0, 3));

        var result = AlphaTexExporter.Export(p, a, l, b);

        Assert.Equal(8, result.MeasureSpans.Count);
        Assert.All(result.MeasureSpans, s => Assert.Equal(4, s.BeatCount));
    }

    // ------------------------------------------------------------------
    // Sounds that are not on the neck
    // ------------------------------------------------------------------

    /// <summary>
    ///     Narrowing an arrangement from seven strings to six changes `StringCount`
    ///     and rewrites no notes, so a sound can sit on a string the staff has not
    ///     got. `StaffMap.String` CLAMPS, which put it on the same alphaTex string
    ///     as the real high-E note beside it: the beat emitted `(3.1 4.1)` — two
    ///     notes on one line, two digits stacked in the tab. There is no honest
    ///     place to draw it, so it is dropped.
    /// </summary>
    [Fact]
    public void ASoundOffTheNeck_IsNotStackedOntoTheOutermostString()
    {
        var (p, a, l, b) = Fixture(bars: 1);
        l.Notes.Add(Note(0, 5, 3)); // the real high-E note
        l.Notes.Add(Note(0, 6, 4)); // left behind on a string this staff has not got

        var score = Parse(AlphaTexExporter.Export(p, a, l, b).AlphaTex);

        var beat = score.Tracks[0].Staves[0].Bars[0].Voices[0].Beats[0];
        Assert.Single(beat.Notes);
        Assert.Equal(3, beat.Notes[0].Fret);
        // One note per string, always — the shape the clamp broke.
        Assert.Equal(beat.Notes.Count, beat.Notes.Select(n => n.String).Distinct().Count());
    }
}
