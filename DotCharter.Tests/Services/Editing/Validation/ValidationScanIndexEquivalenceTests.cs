using System.Diagnostics;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     Pins the two <see cref="ArrangementValidationContext" /> lookups that
///     replaced per-sound linear scans on the chart-load path —
///     <c>PreviousChordOnStringBefore</c> (for <c>SlideTechniqueRule.CheckPullOff</c>)
///     and <c>HasPhraseChangeOnSustain</c> (for <c>NoteRule</c>'s Link-Next
///     check) — against those scans, kept verbatim here as the spec.
/// </summary>
/// <remarks>
///     <para>
///         The scan was <c>foreach (var chord in level.Chords)</c> with NO early
///         exit, run once per pull-off note: a guaranteed full pass over every
///         chord in the level, per pull-off. <c>Validate()</c> runs on the
///         inspector's 150 ms debounce and TWICE on a chart load (the second
///         because <c>TotalAudioLengthMs</c> arrives after the audio decode,
///         far outside that window), synchronously on the UI thread, over
///         EVERY level of a DD chart.
///     </para>
///     <para>
///         This changes a VERDICT path — "pull-off has no preceding note" and
///         "pull-off onto a higher or equal fret" are both derived from what the
///         scan settled on — so the evidence is EQUIVALENCE against the scan
///         kept verbatim below as the spec, not a benchmark. Modelled on
///         <see cref="AnchorIndexEquivalenceTests" />. Timings are LOGGED, never
///         asserted.
///     </para>
/// </remarks>
public sealed class ValidationScanIndexEquivalenceTests(ITestOutputHelper output)
{
    /// <summary>
    ///     The pre-index scan, verbatim from <c>SlideTechniqueRule.CheckPullOff</c>
    ///     (only the surrounding note lookup is factored out into the arguments).
    ///     Returns the fret the scan settled on, or null.
    /// </summary>
    private static int? ScanForPreviousFret(
        ChartLevel level, FractionalPosition notePosition, int stringIndex,
        int? noteFret, FractionalPosition notePreviousPosition)
    {
        var previousFret = noteFret;
        var previousPosition = notePreviousPosition;

        foreach (var chord in level.Chords)
        {
            if (chord.Position >= notePosition) continue;
            if (previousFret != null && chord.Position <= previousPosition) continue;
            foreach (var cn in chord.ChordNotes)
            {
                if (cn.StringIndex == stringIndex)
                {
                    previousFret = cn.Fret;
                    previousPosition = chord.Position;
                    break;
                }
            }
        }

        return previousFret;
    }

    /// <summary>What the product does now, in the same terms.</summary>
    private static int? IndexedPreviousFret(
        ArrangementValidationContext context, ChartLevel level, FractionalPosition notePosition, int stringIndex,
        int? noteFret, FractionalPosition notePreviousPosition)
    {
        var previousFret = noteFret;
        var previous = context.PreviousChordOnStringBefore(level, notePosition, stringIndex);
        if (previous is { } chord && (previousFret == null || chord.Position > notePreviousPosition))
            previousFret = chord.Fret;
        return previousFret;
    }

    private static ArrangementValidationContext ContextOver(ChartLevel level)
    {
        var arrangement = new Arrangement();
        arrangement.Levels.Add(level);
        return ArrangementValidationContext.Build(
            arrangement, [new BeatMarker { Time = 0 }, new BeatMarker { Time = 500 }], null);
    }

    private static Chord ChordAt(int beat, params (int StringIndex, int Fret)[] notes)
    {
        var chord = new Chord { Position = new FractionalPosition(beat) };
        foreach (var (stringIndex, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        return chord;
    }

    /// <summary>
    ///     The whole claim: over randomly PERMUTED chord lists carrying duplicate
    ///     positions and partial string coverage, queried with and without a
    ///     competing previous NOTE, the index answers exactly what the scan
    ///     answered.
    /// </summary>
    [Fact]
    public void TheIndexedLookup_ReturnsWhatTheLinearScanReturned()
    {
        var rng = new Random(20260905);
        var comparisons = 0;
        var stopwatch = Stopwatch.StartNew();

        for (var trial = 0; trial < 400; trial++)
        {
            var level = new ChartLevel();
            var count = rng.Next(0, 20);
            for (var i = 0; i < count; i++)
            {
                // Duplicate positions are ordinary chart state and are exactly
                // what a binary search plus a run walk-back is most likely to
                // get wrong; partial string coverage is the other half.
                var beat = rng.Next(0, 8);
                var strings = Enumerable.Range(0, 6)
                    .Where(_ => rng.Next(0, 2) == 0)
                    .Select(s => (s, rng.Next(0, 22)))
                    .ToArray();
                level.Chords.Add(strings.Length == 0
                    ? ChordAt(beat, (rng.Next(0, 6), rng.Next(0, 22)))
                    : ChordAt(beat, strings));
            }

            // PERMUTED, not sorted: an import nobody has re-sorted is a real
            // state, and the scan's answer over it is the spec.
            for (var i = level.Chords.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (level.Chords[i], level.Chords[j]) = (level.Chords[j], level.Chords[i]);
            }

            var context = ContextOver(level);

            for (var beat = 0; beat <= 9; beat++)
            {
                for (var stringIndex = 0; stringIndex < 6; stringIndex++)
                {
                    foreach (var (noteFret, notePrevious) in new (int?, FractionalPosition)[]
                     {
                         (null, FractionalPosition.Zero),
                         (3, FractionalPosition.Zero),
                         (7, new FractionalPosition(Math.Max(0, beat - 1))),
                         (11, new FractionalPosition(beat))
                     })
                    {
                        var position = new FractionalPosition(beat);
                        var expected = ScanForPreviousFret(level, position, stringIndex, noteFret, notePrevious);
                        var actual = IndexedPreviousFret(context, level, position, stringIndex, noteFret, notePrevious);
                        comparisons++;
                        Assert.Equal(expected, actual);
                    }
                }
            }
        }

        stopwatch.Stop();
        output.WriteLine(
            $"{comparisons} indexed-vs-scan comparisons agreed in {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.True(comparisons > 50_000, $"only {comparisons} comparisons ran — the sweep shrank.");
    }

    /// <summary>
    ///     The index is built ONCE per level per context, so the second and
    ///     later queries cost nothing — the property the scan could never have.
    ///     Structural: same instance out, and the build does not repeat.
    /// </summary>
    [Fact]
    public void TheIndexIsBuiltOncePerLevel_AndAnswersRepeatedQueriesFromIt()
    {
        var level = new ChartLevel();
        for (var beat = 0; beat < 200; beat++) level.Chords.Add(ChordAt(beat, (0, beat % 20)));
        var context = ContextOver(level);

        var first = context.PreviousChordOnStringBefore(level, new FractionalPosition(150), 0);
        var warm = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++) context.PreviousChordOnStringBefore(level, new FractionalPosition(150), 0);
        warm.Stop();

        output.WriteLine($"10,000 warm lookups over 200 chords: {warm.Elapsed.TotalMilliseconds:F1} ms " +
                         "(the scan was 200 chord visits each).");
        Assert.Equal(new FractionalPosition(149), first!.Value.Position);
        Assert.Equal(149 % 20, first.Value.Fret);
    }

    /// <summary>
    ///     …and the rule has to keep using it. Nothing but a guard stops a future
    ///     edit walking <c>level.Chords</c> from inside the per-note loop again —
    ///     which is exactly how this one survived the P1 #10/#11 index sweep that
    ///     fixed its two siblings in <c>LinkNextRule</c>.
    /// </summary>
    [Fact]
    public void ThePullOffCheck_DoesNotWalkTheChordList()
    {
        var file = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Editing", "Validation", "Rules",
            "SlideTechniqueRule.cs");
        Assert.True(File.Exists(file), $"missing {file}");
        var source = File.ReadAllText(file);

        var start = source.IndexOf("private static void CheckPullOff(", StringComparison.Ordinal);
        Assert.True(start >= 0, "CheckPullOff is the pull-off verdict; it must still exist.");
        var end = source.IndexOf("private static void CheckBends(", start, StringComparison.Ordinal);
        Assert.True(end > start, "CheckBends follows CheckPullOff; the body slice needs both anchors.");
        var body = source[start..end];

        Assert.DoesNotContain("level.Chords", body, StringComparison.Ordinal);
        Assert.Contains("PreviousChordOnStringBefore", body, StringComparison.Ordinal);
    }

    // ------------------------------- the sibling scan on the same load path

    /// <summary>
    ///     The pre-index scan of <c>HasPhraseChangeOnSustain</c>, verbatim.
    /// </summary>
    private static bool ScanForPhraseChange(
        Arrangement arrangement, IList<BeatMarker> beats, double startMs, double endMs)
    {
        foreach (var ep in arrangement.EventPoints)
        {
            if (string.IsNullOrEmpty(ep.Phrase)) continue;
            if (ep.Phrase.StartsWith("mover", StringComparison.OrdinalIgnoreCase)) continue;
            var t = ep.Position.GetPositionMs(beats);
            if (t > startMs && t <= endMs) return true;
        }

        return false;
    }

    /// <summary>
    ///     `HasPhraseChangeOnSustain` walked every event point with a
    ///     `GetPositionMs` per point, per Link-Next note, with no early break on
    ///     the common negative answer. Same claim as above: the binary search
    ///     over the once-built sorted array answers what the scan answered, over
    ///     permuted event lists with duplicate positions, mover phrases,
    ///     section-only points and sustains that start ON a boundary, end ON one,
    ///     straddle one or touch none.
    /// </summary>
    [Fact]
    public void ThePhraseChangeLookup_ReturnsWhatTheLinearScanReturned()
    {
        var rng = new Random(20260906);
        var comparisons = 0;

        for (var trial = 0; trial < 300; trial++)
        {
            var arrangement = new Arrangement();
            arrangement.Levels.Add(new ChartLevel());
            var beats = Enumerable.Range(0, 17)
                .Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 }).ToList();

            var points = rng.Next(0, 12);
            for (var i = 0; i < points; i++)
            {
                var beat = rng.Next(0, 16);
                // The three shapes the scan discriminates on, plus duplicates.
                var phrase = rng.Next(0, 4) switch
                {
                    0 => "",
                    1 => "MOVER" + rng.Next(0, 3),
                    2 => "moverUp",
                    _ => "verse" + rng.Next(0, 3)
                };
                arrangement.EventPoints.Add(new EventPoint
                {
                    Position = new FractionalPosition(beat),
                    Phrase = phrase,
                    Section = rng.Next(0, 2) == 0 ? "intro" : ""
                });
            }

            for (var i = arrangement.EventPoints.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (arrangement.EventPoints[i], arrangement.EventPoints[j]) =
                    (arrangement.EventPoints[j], arrangement.EventPoints[i]);
            }

            var context = ArrangementValidationContext.Build(arrangement, beats, null);

            for (var beat = 0; beat < 14; beat++)
            {
                foreach (var sustainBeats in new[] { 0, 1, 2, 4 })
                {
                    var note = new GuitarNote
                    {
                        Position = new FractionalPosition(beat),
                        Sustain = sustainBeats == 0
                            ? FractionalPosition.Zero
                            : new FractionalPosition(sustainBeats),
                        Fret = 3
                    };

                    var expected = sustainBeats != 0 && ScanForPhraseChange(
                        arrangement, beats,
                        note.Position.GetPositionMs(beats),
                        note.EndPosition.GetPositionMs(beats));

                    comparisons++;
                    Assert.Equal(expected, context.HasPhraseChangeOnSustain(note));
                }
            }
        }

        output.WriteLine($"{comparisons} phrase-change comparisons agreed.");
        Assert.True(comparisons > 10_000, $"only {comparisons} comparisons ran — the sweep shrank.");
    }

    /// <summary>The array is built once, so the scan cannot come back through a
    /// rebuild per call.</summary>
    [Fact]
    public void ThePhraseTimeArray_IsBuiltOncePerContext()
    {
        var file = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Editing", "Validation",
            "ArrangementValidationContext.cs");
        var source = File.ReadAllText(file);

        var start = source.IndexOf("public bool HasPhraseChangeOnSustain(", StringComparison.Ordinal);
        Assert.True(start >= 0, "HasPhraseChangeOnSustain is the verdict; it must still exist.");
        var end = source.IndexOf('}', source.IndexOf("return lo < times.Length", start, StringComparison.Ordinal));
        var body = source[start..end];

        Assert.DoesNotContain("Arrangement.EventPoints", body, StringComparison.Ordinal);
        Assert.Contains("NonMoverPhraseTimesMs", body, StringComparison.Ordinal);
    }
}
