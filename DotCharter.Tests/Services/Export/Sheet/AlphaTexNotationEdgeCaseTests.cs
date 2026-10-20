using AlphaTab;
using AlphaTab.Importer;
using AlphaTab.Model;
using DotCharter.Services.Export.Sheet;
using DomainChord = DotCharter.Core.Domain.Guitar.Chord;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// Notation edge-case coverage for the domain → alphaTex exporter. These are
/// golden-TEXT + managed-parse tests (deterministic, headless): a golden IMAGE
/// comparison is not reproducible headless because the notation raster is drawn
/// by AlphaSkia's native backend against machine fonts. Every case asserts both
/// the emitted alphaTex AND that AlphaTab's managed importer parses it (so the
/// on-screen strip can never blank on these inputs), and — where relevant — that
/// the parsed model carries the intended tie.
/// </summary>
[Collection("AlphaTab")]
public class AlphaTexNotationEdgeCaseTests
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

    private static GuitarNote Note(int beat, int str = 0, int fret = 5) =>
        new() { Position = new FractionalPosition(beat), StringIndex = str, Fret = fret };

    private static Score Parse(string tex)
    {
        var importer = new AlphaTexImporter();
        importer.InitFromString(tex, new Settings());
        return importer.ReadScore();
    }

    private static bool AnyTieDestination(Score score)
    {
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
                            foreach (var note in beat.Notes)
                            {
                                if (note.IsTieDestination)
                                    return true;
                            }
                        }
                    }
                }
            }
        }

        return false;
    }

    [Fact]
    public void TieAcrossBarline_SustainedNote_EmitsTieThatParses()
    {
        var (project, arr, level, beats) = Fixture(2);
        // bar 1, beat 4, SUSTAINED into bar 2 beat 2 — a real hold ties across.
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(3), StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(2)
        });
        level.Notes.Add(Note(6)); // bar 2, beat 3

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var barSegments = tex.Split('|');
        Assert.True(barSegments.Length >= 2);
        Assert.Contains("-.", barSegments[1]); // bar 2 opens with a tie, not a rest
        Assert.True(AnyTieDestination(Parse(tex)), "expected a note tied across the barline");
    }

    [Fact]
    public void NoTieAcrossBarline_ForZeroSustainNote_GapBecomesRests()
    {
        // A zero-sustain (short) hit before a gap must NOT be tied across the
        // barline — the old gap-fill rendered a short bass hit before a long
        // break as dozens of tied whole notes. The next bar opens with a rest.
        var (project, arr, level, beats) = Fixture(2);
        level.Notes.Add(Note(3)); // bar 1, beat 4, sustain 0
        level.Notes.Add(Note(5)); // bar 2, beat 2

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var barSegments = tex.Split('|');
        Assert.True(barSegments.Length >= 2);
        Assert.DoesNotContain("-.", barSegments[1]);
        Assert.Contains("r", barSegments[1]); // bar 2 opens with a rest
        Assert.False(AnyTieDestination(Parse(tex)));
    }

    [Fact]
    public void NoTie_WhenNextOnsetIsOnTheDownbeat()
    {
        var (project, arr, level, beats) = Fixture(2);
        level.Notes.Add(Note(3)); // bar 1, beat 4
        level.Notes.Add(Note(4)); // bar 2, beat 1 (downbeat) — no gap, no tie needed

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var barSegments = tex.Split('|');
        Assert.DoesNotContain("-.", barSegments[1]);
        Parse(tex); // still valid
    }

    [Fact]
    public void TieSpansEmptyMiddleBar_WhenSustainCrossesIt()
    {
        var (project, arr, level, beats) = Fixture(3);
        // bar 1, beat 4, sustained for 6 beats: through all of bar 2 into bar 3 beat 2.
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(3), StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(6)
        });
        level.Notes.Add(Note(10)); // bar 3, beat 3

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var barSegments = tex.Split('|');
        Assert.True(barSegments.Length >= 3);
        Assert.Contains("-.", barSegments[1]); // tie continues through the empty bar 2
        Assert.Contains("-.", barSegments[2]); // and into bar 3 up to where the sustain ends
        Assert.True(AnyTieDestination(Parse(tex)));
    }

    [Fact]
    public void SustainedNote_ShorterThanGap_GetsRestsAfterIt()
    {
        var (project, arr, level, beats) = Fixture(1);
        // Beat 1, sustained ONE beat; next onset at beat 4 — beats 2-3 are rests.
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(1)
        });
        level.Notes.Add(Note(3));

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var beatsParsed = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats;
        Assert.Equal(4, beatsParsed.Count); // quarter, rest, rest, quarter
        Assert.False(beatsParsed[0].IsRest);
        Assert.True(beatsParsed[1].IsRest);
        Assert.True(beatsParsed[2].IsRest);
        Assert.False(beatsParsed[3].IsRest);
    }

    [Fact]
    public void WithinBarTie_StillWorks()
    {
        var (project, arr, level, beats) = Fixture(1);
        // Beat 1, SUSTAINED 2.5 beats (5 eighths) — not a single standard value,
        // so it must render as half + tied eighth (a tie WITHIN the bar).
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(2, new Fraction(1, 2))
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(3), StringIndex = 0, Fret = 7 });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        Assert.Contains("-.", tex);
        Assert.True(AnyTieDestination(Parse(tex)));
    }

    [Fact]
    public void TripletTiming_RendersAsTrueTuplets()
    {
        // Three evenly-spaced onsets within one beat — the 48-ticks-per-quarter
        // grid expresses them exactly, and they must render as a REAL triplet
        // ({tu 3}), not as dotted/tied approximation fragments.
        var (project, arr, level, beats) = Fixture(1);
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0, new Fraction(0, 3)), StringIndex = 0, Fret = 1 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0, new Fraction(1, 3)), StringIndex = 0, Fret = 2 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0, new Fraction(2, 3)), StringIndex = 0, Fret = 3 });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var parsed = Parse(tex).Tracks[0].Staves[0].Bars[0].Voices[0].Beats;
        Assert.Equal(Duration.Eighth, parsed[0].Duration);
        Assert.Equal(3.0, parsed[0].TupletNumerator);
        Assert.Equal(Duration.Eighth, parsed[1].Duration);
        Assert.Equal(3.0, parsed[1].TupletNumerator);
        // The third (zero-sustain) note gap-fills the remainder of the bar —
        // it starts a non-rest beat; no :16/:64 approximation slivers appear.
        Assert.False(parsed[2].IsRest);
        Assert.DoesNotContain(":16", tex);
        Assert.DoesNotContain(":64", tex);
    }

    [Fact]
    public void SameTickMultipleNotes_MergeIntoOneChordBeat()
    {
        // Two notes + a chord landing on the same tick merge into ONE beat with
        // all three strings — the old exporter silently dropped all but the
        // first event on the tick.
        var (project, arr, level, beats) = Fixture(1);
        level.Notes.Add(Note(1, str: 0, fret: 5));
        level.Notes.Add(Note(1, str: 2, fret: 7));
        var chord = new DomainChord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 3 });
        level.Chords.Add(chord);

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        var score = Parse(tex);
        var beat = score.Tracks[0].Staves[0].Bars[0].Voices[0].Beats
            .First(b => !b.IsRest);
        Assert.Equal(3, beat.Notes.Count);
    }

    [Fact]
    public void LinkNextChain_RendersAsTie()
    {
        // Rocksmith "link next" (same string, same fret, no slide) is a held
        // continuation — it must render as a tie, not a re-attack.
        var (project, arr, level, beats) = Fixture(1);
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(1), LinkNext = true
        });
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1), StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(1)
        });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        Assert.True(AnyTieDestination(Parse(tex)), "link-next chain should tie");
    }

    [Fact]
    public void ChordName_EmittedOnlyOnHarmonyChange()
    {
        // Four identical chords in a row: the name annotates the FIRST beat only
        // (GP prints chord names on change, not on every strum).
        var (project, arr, level, beats) = Fixture(2);
        var arr2 = arr;
        arr2.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "Am",
            Frets = new Dictionary<int, int> { { 1, 0 }, { 2, 2 } }
        });
        arr2.ChordTemplates.Add(new ChordTemplate
        {
            Id = 1, Name = "G",
            Frets = new Dictionary<int, int> { { 1, 2 }, { 2, 0 } }
        });
        for (var i = 0; i < 4; i++)
        {
            var c = new DomainChord { Position = new FractionalPosition(i), TemplateId = i < 3 ? 0 : 1 };
            c.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = i < 3 ? 0 : 2 });
            c.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = i < 3 ? 2 : 0 });
            level.Chords.Add(c);
        }

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        Assert.Equal(1, CountOccurrences(tex, "ch \"Am\""));
        Assert.Equal(1, CountOccurrences(tex, "ch \"G\""));
    }

    [Fact]
    public void StaffMetadata_TuningInstrumentClef_EmittedForBass()
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Bass", StringCount = 4, ArrangementType = ArrangementType.Bass };
        for (var i = 0; i < 4; i++) arr.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 4; i++)
            beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 3 });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;
        var score = Parse(tex);
        var staff = score.Tracks[0].Staves[0];

        Assert.Equal(4, staff.Tuning.Count);
        Assert.Equal(43, (int)staff.Tuning[0]); // G2 — bass tuning, not guitar defaults
        Assert.Equal(28, (int)staff.Tuning[3]); // E1
        Assert.Equal(Clef.F4, staff.Bars[0].Clef);
        // \instrument makes AlphaTab apply the standard −12 display transposition.
        Assert.Equal(-12.0, staff.DisplayTranspositionPitch);
    }

    [Fact]
    public void StaffMetadata_DropTuningAndCapo_Emitted()
    {
        var project = new ChartProject();
        project.Chart.KeyAccidentals = 2; // D major
        var arr = new Arrangement { Name = "Lead", StringCount = 6, Capo = 2 };
        // Drop D: low string −2.
        arr.TuningPitches.Add(new TuningOffset(-2));
        for (var i = 1; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 4; i++)
            beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 0 });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;
        var score = Parse(tex);
        var staff = score.Tracks[0].Staves[0];

        Assert.Equal(38, (int)staff.Tuning[5]); // drop D low string = D2
        Assert.Equal(64, (int)staff.Tuning[0]); // high E untouched
        Assert.Equal(2.0, staff.Capo);
        Assert.Equal(KeySignature.D, staff.Bars[0].MasterBar.KeySignature);
    }

    [Fact]
    public void BeatMap_StaysOneToOneWithParsedBeatsPerBar()
    {
        // The sheet's click-to-note editing indexes AlphaTab's per-bar beat
        // bounds into the exporter's BeatMap by position — the two MUST stay
        // 1:1 per bar. Exercise every emission shape at once: runs, a chord, a
        // sustain that ties across a barline, mid-bar rests, triplets and a
        // trailing empty bar.
        var (project, arr, level, beats) = Fixture(4);
        level.Notes.Add(Note(0));
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1, new Fraction(0, 3)), StringIndex = 0, Fret = 1 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1, new Fraction(1, 3)), StringIndex = 0, Fret = 2 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1, new Fraction(2, 3)), StringIndex = 0, Fret = 3 });
        var chord = new DomainChord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 2 });
        level.Chords.Add(chord);
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(3), StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(2) // ties into bar 2
        });
        level.Notes.Add(Note(9)); // bar 3 beat 2 — bar 3 opens with a rest

        var export = AlphaTexExporter.Export(project, arr, level, beats);
        var score = Parse(export.AlphaTex);
        var bars = score.Tracks[0].Staves[0].Bars;

        for (var barIdx = 0; barIdx < bars.Count; barIdx++)
        {
            var parsedCount = bars[barIdx].Voices[0].Beats.Count;
            var mapCount = export.BeatMap.Count(b => b.MeasureIndex == barIdx);
            // AlphaTab represents a fully empty bar as ONE implicit whole rest;
            // the exporter emits an explicit rest for it, so counts match there
            // too. Any mismatch breaks glyph→note resolution for that bar.
            Assert.Equal(parsedCount, mapCount);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
