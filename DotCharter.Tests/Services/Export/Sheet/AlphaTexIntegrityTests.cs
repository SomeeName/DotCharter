using AlphaTab;
using AlphaTab.Importer;
using DotCharter.Services.Export.Sheet;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// Guards the three exporter invariants that are invisible until a real chart
/// hits them, all three of which were BROKEN when this file was written:
/// <list type="number">
///   <item>every bar emits EXACTLY its time signature (the beaming/duration
///         invariant — an over- or under-filled bar makes AlphaTab engrave a
///         sliver beat that breaks the beam of the run it lands in);</item>
///   <item>a capoed arrangement engraves the pitches the domain says it
///         sounds;</item>
///   <item>song metadata containing alphaTex metacharacters cannot corrupt the
///         document.</item>
/// </list>
/// Everything here is pure or goes through AlphaTab's MANAGED importer — no
/// AlphaSkia, no display, no fonts — so it belongs in the default gate.
/// </summary>
[Collection("AlphaTab")]
public class AlphaTexIntegrityTests
{
    // ------------------------------------------------------------------
    // Fixture
    // ------------------------------------------------------------------

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
                NoteDenominator = denominator
            });
        }

        return (project, arr, level, beats);
    }

    /// <summary>Beat position as an EXACT 1/192-of-a-beat fraction — the same grid
    /// an ms-imported chart quantizes onto, so off-grid cases are realistic.</summary>
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

    // ------------------------------------------------------------------
    // (1) The beaming/duration invariant
    // ------------------------------------------------------------------

    /// <summary>
    /// Every emitted beat carries its duration in ticks, and AlphaTab fills bars
    /// from exactly those durations — so "the bar map sums to the bar" IS "the
    /// engraved bar holds its time signature".
    /// </summary>
    private static void AssertBarsSumExactly(
        string because, ChartProject p, Arrangement a, ChartLevel l, IList<BeatMarker> b)
    {
        var result = AlphaTexExporter.Export(p, a, l, b);

        var perBar = new Dictionary<int, int>();
        foreach (var beat in result.BeatMap)
            perBar[beat.MeasureIndex] = perBar.GetValueOrDefault(beat.MeasureIndex) + beat.DurationTicks;

        for (var bar = 0; bar < result.MeasureSpans.Count; bar++)
        {
            var expected = result.MeasureSpans[bar].TotalTicks;
            var actual = perBar.GetValueOrDefault(bar);
            Assert.True(actual == expected,
                $"{because}: bar {bar} emitted {actual} ticks for a {expected}-tick bar " +
                $"(delta {actual - expected}).\nalphaTex: {result.AlphaTex}");
        }
    }

    [Fact]
    public void EveryBar_EmitsExactlyItsTimeSignature_ForRealChartShapes()
    {
        {
            // A sustain that outlives the next onset — the head starts where the
            // previous sound ended.
            var (p, a, l, b) = Fixture();
            l.Notes.Add(Note(0, 0, 3, 8));
            l.Notes.Add(Note(1, 1, 5));
            AssertBarsSumExactly("overlapping sustain", p, a, l, b);
        }
        {
            var (p, a, l, b) = Fixture();
            for (var i = 0; i < 12; i++) l.Notes.Add(Note(i / 3.0, 0, 3)); // eighth triplets
            AssertBarsSumExactly("triplets", p, a, l, b);
        }
        {
            var (p, a, l, b) = Fixture(7, 8, 3);
            for (var i = 0; i < 14; i++) l.Notes.Add(Note(i * 0.5, 0, 3));
            AssertBarsSumExactly("7/8", p, a, l, b);
        }
        {
            var (p, a, l, b) = Fixture();
            l.Notes.Add(Note(0, 0, 3, 1.5)); // dotted quarter
            l.Notes.Add(Note(1.5, 1, 5, 0.5));
            AssertBarsSumExactly("dotted values", p, a, l, b);
        }
        {
            // An authored notation-duration hint longer than the gap it sits in.
            var (p, a, l, b) = Fixture();
            var n = Note(0, 0, 3);
            n.NotationDuration = new Fraction(7, 2);
            l.Notes.Add(n);
            l.Notes.Add(Note(2, 1, 5));
            AssertBarsSumExactly("over-long notation hint", p, a, l, b);
        }
        {
            // THE REGRESSION CASE. An ms-imported sustain ending at 4+5/192 with
            // the next onset at 4+6/192 produced a 46-tick head, which the old
            // splitter cut 36 + 9 + 2 = 47 — one tick more than it was asked for.
            var (p, a, l, b) = Fixture();
            l.Notes.Add(Note(3, 0, 3, 1 + 5 / 192.0));
            l.Notes.Add(Note(4 + 6 / 192.0, 1, 5));
            l.Notes.Add(Note(5, 2, 7));
            AssertBarsSumExactly("off-grid tie into an off-grid onset", p, a, l, b);
        }
        {
            // A tie landing 1 tick before the next onset: the 1-tick rest that
            // used to be emitted rounded up to a 2-tick chunk.
            var (p, a, l, b) = Fixture(3, 4, 4);
            l.Notes.Add(Note(2, 0, 3, 1 + 3 / 192.0));
            l.Notes.Add(Note(3 + 4 / 192.0, 1, 5, 0.75));
            l.Notes.Add(Note(4, 2, 7));
            AssertBarsSumExactly("one-tick rest gap", p, a, l, b);
        }
        {
            // An onset landing within a 64th of the barline: the duration floor
            // used to write a 3-tick head into a 2-tick bar remainder.
            var (p, a, l, b) = Fixture(3, 4, 4);
            l.Notes.Add(Note(3 - 2 / 192.0, 0, 3));
            l.Notes.Add(Note(3 + 1, 1, 5));
            AssertBarsSumExactly("onset a 64th before the barline", p, a, l, b);
        }
    }

    /// <summary>
    /// The invariant has to hold for charts nobody hand-wrote. Onsets and
    /// sustains land anywhere on the 1/192 grid, meters vary, capos vary — the
    /// three fixed defects were all found here first, at seeds 1, 378 and 645.
    /// </summary>
    [Fact]
    public void EveryBar_EmitsExactlyItsTimeSignature_AcrossAFuzzedCorpus()
    {
        var meters = new[] { (4, 4), (3, 4), (6, 8), (7, 8), (5, 4), (12, 8) };

        for (var seed = 0; seed < 1500; seed++)
        {
            var rnd = new Random(seed);
            var (numerator, denominator) = meters[rnd.Next(meters.Length)];
            var (p, a, l, b) = Fixture(numerator, denominator, 4);
            a.Capo = rnd.Next(0, 4);

            var count = rnd.Next(1, 14);
            for (var i = 0; i < count; i++)
            {
                var beat = rnd.Next(0, numerator * 4) + rnd.Next(0, 192) / 192.0;
                var sustain = rnd.Next(0, 3) == 0 ? 0 : rnd.Next(0, 400) / 192.0;
                l.Notes.Add(Note(beat, rnd.Next(0, 6), rnd.Next(0, 20), sustain));
            }

            AssertBarsSumExactly($"fuzz seed {seed} ({numerator}/{denominator})", p, a, l, b);
        }
    }

    // ------------------------------------------------------------------
    // (2) Capo
    // ------------------------------------------------------------------

    private static List<(double Fret, double RealValue)> ParseNotes(string tex)
    {
        var notes = new List<(double, double)>();
        var score = Parse(tex);
        foreach (var track in score.Tracks)
        {
            foreach (var staff in track.Staves)
            {
                foreach (var bar in staff.Bars)
                {
                    foreach (var voice in bar.Voices)
                    {
                        foreach (var beat in voice.Beats)
                        {
                            foreach (var n in beat.Notes)
                                notes.Add((n.Fret, n.RealValue));
                        }
                    }
                }
            }
        }

        return notes;
    }

    /// <summary>
    /// The domain's rule (shared with <c>ChordNameSuggester</c>,
    /// <c>RefretPlanner.SoundingMidi</c> and <c>LivePitchAcceptance.SoundingMidi</c>):
    /// frets are absolute on the un-capoed neck and the sounding offset is
    /// <c>max(fret, capo)</c>. AlphaTab instead ADDS <c>\capo</c> to whatever fret
    /// it is given, so the exporter has to hand it capo-RELATIVE frets. Before
    /// the fix every fretted note on a capoed chart engraved <c>capo</c>
    /// semitones sharp while open strings stayed correct — the tell that made it
    /// read as "the notation is a bit off" rather than as a bug.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public void CapoedArrangement_EngravesTheSoundingPitchTheDomainDefines(int capo)
    {
        var (p, a, l, b) = Fixture();
        a.Capo = capo;
        // Low E (string index 0, open MIDI 40): one open string and two fretted.
        l.Notes.Add(Note(0, 0, 0));
        l.Notes.Add(Note(1, 0, 7));
        l.Notes.Add(Note(2, 0, 12));

        var notes = ParseNotes(AlphaTexExporter.Export(p, a, l, b).AlphaTex);

        const int openLowE = 40;
        foreach (var (domainFret, expectedOffset) in new[] { (0, 0), (7, 7), (12, 12) })
        {
            var sounding = openLowE + Math.Max(expectedOffset, capo);
            Assert.True(notes.Any(n => n.RealValue == sounding),
                $"capo {capo}: domain fret {domainFret} must sound MIDI {sounding}; " +
                $"got [{string.Join(", ", notes.Select(n => $"fret {n.Fret}=>{n.RealValue}"))}]");
        }
    }

    [Fact]
    public void Capo_EmitsTabFretsRelativeToTheCapo_SoZeroIsTheCapo()
    {
        var (p, a, l, b) = Fixture();
        a.Capo = 3;
        l.Notes.Add(Note(0, 0, 3));  // sitting ON the capo -> tab 0
        l.Notes.Add(Note(1, 0, 8));  // five above the capo -> tab 5

        var frets = ParseNotes(AlphaTexExporter.Export(p, a, l, b).AlphaTex).Select(n => n.Fret).ToList();

        Assert.Contains(0d, frets);
        Assert.Contains(5d, frets);
    }

    [Fact]
    public void NoCapo_IsAStrictNoOp_OnTheEmittedFrets()
    {
        var (p, a, l, b) = Fixture();
        l.Notes.Add(Note(0, 0, 0));
        l.Notes.Add(Note(1, 2, 9));
        l.Notes.Add(Note(2, 5, 17));

        var tex = AlphaTexExporter.Export(p, a, l, b).AlphaTex;

        Assert.DoesNotContain("\\capo", tex);
        var frets = ParseNotes(tex).Select(n => n.Fret).ToList();
        Assert.Equal(new[] { 0d, 9d, 17d }, frets);
    }

    // ------------------------------------------------------------------
    // (3) Escaping / injection
    // ------------------------------------------------------------------

    /// <summary>
    /// The bundled 1.8.3 lexer honours <c>\\</c> and <c>\"</c> inside a string
    /// literal and treats every other <c>\x</c> as a hard parse error. The
    /// exporter escaped the quote but NOT the backslash, so a title, artist or
    /// chord name carrying one (<c>AC\DC</c>, a pasted path) threw
    /// <c>UnsupportedFormatError</c> and took the entire document down — the
    /// sheet pane rendered nothing at all because of one character of metadata.
    /// </summary>
    [Theory]
    [InlineData(@"AC\DC")]
    [InlineData(@"C:\songs\take 1")]
    [InlineData(@"ends with a backslash\")]
    [InlineData("has \"quotes\"")]
    [InlineData(@"both \ and "" together")]
    [InlineData(@"\\ \" + "\"")]
    public void SongMetadata_WithAlphaTexMetacharacters_StillParses_AndRoundTrips(string nasty)
    {
        var (p, a, l, b) = Fixture();
        p.Chart.SongName = nasty;
        p.Chart.Artist = nasty;
        l.Notes.Add(Note(0, 0, 3));

        var score = Parse(AlphaTexExporter.Export(p, a, l, b).AlphaTex);

        Assert.Equal(nasty, score.Title);
        Assert.Equal(nasty, score.SubTitle);
    }

    [Fact]
    public void ChordName_WithAlphaTexMetacharacters_StillParses_AndRoundTrips()
    {
        const string nasty = @"A\5 ""add9""";
        var (p, a, l, b) = Fixture();
        a.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = nasty, Frets = new Dictionary<int, int> { { 0, 0 }, { 1, 2 } }
        });
        var chord = new Chord { Position = At(0), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 0 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        l.Chords.Add(chord);

        var score = Parse(AlphaTexExporter.Export(p, a, l, b).AlphaTex);

        var names = (from track in score.Tracks
                     from staff in track.Staves
                     from bar in staff.Bars
                     from voice in bar.Voices
                     from beat in voice.Beats
                     where beat.HasChord
                     select beat.Chord?.Name).ToList();
        Assert.Contains(nasty, names);
    }

    // ------------------------------------------------------------------
    // (4) Fidelity: articulations the domain carries and the sheet dropped
    // ------------------------------------------------------------------

    /// <summary>
    /// <see cref="HopoType.Tap"/> is a real domain value the charter can set, and
    /// it used to engrave as an ORDINARY note — the reader was shown a picked
    /// note where the chart says "tap". <c>tt</c> is parser-verified: it sets
    /// <c>Beat.Tap</c> on the bundled 1.8.3 model.
    /// </summary>
    [Fact]
    public void ATappedNote_EngravesAsATap_NotAsAnOrdinaryNote()
    {
        var (p, a, l, b) = Fixture();
        l.Notes.Add(new GuitarNote
        {
            Position = At(0), StringIndex = 0, Fret = 12, Hopo = HopoType.Tap
        });
        l.Notes.Add(Note(1, 0, 5));

        var score = Parse(AlphaTexExporter.Export(p, a, l, b).AlphaTex);

        var taps = (from track in score.Tracks
                    from staff in track.Staves
                    from bar in staff.Bars
                    from voice in bar.Voices
                    from beat in voice.Beats
                    where beat.Tap
                    select beat).ToList();
        Assert.Single(taps);
    }

    /// <summary>
    /// Chord fingering lives on the <see cref="ChordTemplate"/>, so it has to be
    /// resolved where the template index is in scope. AlphaTex numbers fingers
    /// 1..5 = thumb..little while the domain numbers them 0..4 (T,1,2,3,4).
    /// </summary>
    [Fact]
    public void ChordFingering_ReachesTheStaff_WithTheDomainToAlphaTexOffset()
    {
        var (p, a, l, b) = Fixture();
        a.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Name = "Am",
            Frets = new Dictionary<int, int> { { 2, 2 }, { 3, 2 }, { 4, 1 } },
            // domain: 1 = index, 2 = middle, 3 = ring
            Fingers = new Dictionary<int, int> { { 2, 2 }, { 3, 3 }, { 4, 1 } }
        });
        var chord = new Chord { Position = At(0), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 1 });
        l.Chords.Add(chord);

        var score = Parse(AlphaTexExporter.Export(p, a, l, b).AlphaTex);

        var fingers = (from track in score.Tracks
                       from staff in track.Staves
                       from bar in staff.Bars
                       from voice in bar.Voices
                       from beat in voice.Beats
                       from n in beat.Notes
                       select n.LeftHandFinger).ToList();

        Assert.Contains(AlphaTab.Model.Fingers.IndexFinger, fingers);   // domain 1
        Assert.Contains(AlphaTab.Model.Fingers.MiddleFinger, fingers);  // domain 2
        Assert.Contains(AlphaTab.Model.Fingers.AnnularFinger, fingers); // domain 3
    }

    /// <summary>
    /// A raw newline parses, but a multi-line title draws over the first system —
    /// control characters fold to spaces instead.
    /// </summary>
    [Fact]
    public void SongMetadata_WithControlCharacters_FoldsThemToSpaces()
    {
        var (p, a, l, b) = Fixture();
        p.Chart.SongName = "line one\r\nline two\ttabbed";
        l.Notes.Add(Note(0, 0, 3));

        var score = Parse(AlphaTexExporter.Export(p, a, l, b).AlphaTex);

        Assert.Equal("line one  line two tabbed", score.Title);
    }
}
