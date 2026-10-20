using System.Diagnostics;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     The compact-repeat decision (<see cref="RsChordDensity" />) answers three
///     positional questions per chord — the last sound before it, whether its
///     hand shape holds an earlier sound, and the nearest earlier CHORD in that
///     shape. All three used to be linear scans of the whole level; they are
///     binary searches over the same sorted list now.
/// </summary>
/// <remarks>
///     <para>
///         <b>This runs everywhere</b> — no <c>TestData/</c>, so it is the half of
///         the evidence that is not skipped in a worktree or on CI.
///         <see cref="ExportBytePinTests" /> pins the same change against real
///         charts when the corpus is present.
///     </para>
///     <para>
///         The fixture is built to be the WORST case for the old code and the
///         most likely to expose an off-by-one in the new: many chords, dense
///         overlapping hand shapes, notes interleaved between chords (a single
///         note between two identical chords breaks the repeat), and repeated
///         shapes so the decision actually comes out true for a large fraction.
///         A degenerate fixture where nothing compacts would pass with the
///         function stubbed to <c>false</c>, so the test asserts the decision is
///         non-trivial in BOTH directions before comparing.
///     </para>
/// </remarks>
public sealed class RsChordDensityLookupEquivalenceTests
{
    private static List<BeatMarker> Beats(int count)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
            beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });
        return beats;
    }

    /// <summary>
    ///     A level whose chords repeat a small pool of shapes inside overlapping
    ///     hand shapes, with single notes sprinkled between them.
    /// </summary>
    private static ChartLevel BuildDenseLevel(int seed, int chordCount, int shapeSpanBeats)
    {
        var rng = new Random(seed);
        var level = new ChartLevel();

        // A handful of shapes, so consecutive chords frequently repeat one.
        var shapePool = new List<(int StringIndex, int Fret)[]>
        {
            new[] { (0, 3), (1, 5), (2, 5) },
            new[] { (1, 2), (2, 4), (3, 4) },
            new[] { (0, 7), (1, 9), (2, 9), (3, 8) },
            new[] { (2, 10), (3, 12) },
            new[] { (0, 0), (1, 2), (2, 2) }
        };

        var beat = 1;
        for (var i = 0; i < chordCount; i++)
        {
            var shape = shapePool[rng.Next(shapePool.Count)];
            var position = new FractionalPosition(beat, new Fraction(rng.Next(0, 4), 4));

            var chord = new Chord { Position = position, TemplateId = 0 };
            foreach (var (stringIndex, fret) in shape)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            level.Chords.Add(chord);

            // A single note between chords sometimes — it BREAKS the repeat, so
            // it is the case a wrong "previous sound" answer gets wrong.
            if (rng.Next(3) == 0)
            {
                level.Notes.Add(new GuitarNote
                {
                    Position = position + new FractionalPosition(0, new Fraction(1, 8)),
                    StringIndex = rng.Next(6),
                    Fret = rng.Next(0, 12)
                });
            }

            beat += rng.Next(1, 3);
        }

        // Overlapping hand shapes across the whole span, so a chord often sits
        // in one and the shape ranges are not disjoint.
        for (var start = 1; start < beat; start += Math.Max(1, shapeSpanBeats / 2))
        {
            level.HandShapes.Add(new HandShape
            {
                Position = new FractionalPosition(start),
                EndPosition = new FractionalPosition(start + shapeSpanBeats)
            });
        }

        return level;
    }

    /// <summary>
    ///     A brute-force restatement of the three lookups the optimisation
    ///     replaced, over the same sorted sound list <see cref="RsChordDensity" />
    ///     builds. Used only to prove the fixture reaches the hand-shape branch.
    /// </summary>
    private static (int InShape, int WithEarlierSoundInShape) ShapeCoverage(ChartLevel level)
    {
        var positions = level.Notes.Select(n => n.Position)
            .Concat(level.Chords.Select(c => c.Position))
            .OrderBy(p => p)
            .ToList();

        var inShape = 0;
        var withEarlier = 0;
        foreach (var chord in level.Chords)
        {
            var shape = level.HandShapes.FirstOrDefault(
                h => chord.Position >= h.Position && chord.Position < h.EndPosition);
            if (shape == null) continue;
            inShape++;
            if (positions.Any(p => p >= shape.Position && p < shape.EndPosition && p < chord.Position))
                withEarlier++;
        }

        return (inShape, withEarlier);
    }

    [Theory]
    [InlineData(1, 400, 4)]
    [InlineData(2, 900, 2)]
    [InlineData(3, 1500, 8)]
    // One shape covering nearly everything: the pathological range for the
    // "nearest earlier chord in this shape" scan.
    [InlineData(4, 1200, 400)]
    public void TheCompactRepeatDecision_IsNonTrivialAndStableOnADenseLevel(
        int seed, int chordCount, int shapeSpanBeats)
    {
        var level = BuildDenseLevel(seed, chordCount, shapeSpanBeats);
        var beats = Beats(level.Chords[^1].Position.BeatId + 8);

        var compact = RsChordDensity.Compute(level, beats, _ => 0);

        // Not vacuous: the fixture must reach the hand-shape branch...
        var (inShape, withEarlier) = ShapeCoverage(level);
        Assert.True(inShape > chordCount / 4,
            $"Only {inShape}/{chordCount} chords are inside a hand shape — the fixture does not " +
            "exercise the hand-shape branch the binary search replaced.");
        Assert.True(withEarlier > chordCount / 4,
            $"Only {withEarlier}/{chordCount} chords have an earlier sound in their hand shape.");

        // ...and the decision must come out BOTH ways, or a stubbed `false`
        // (or `true`) would pass.
        Assert.True(compact.Count > 0, "No chord compacted — the decision is stuck at false.");
        Assert.True(compact.Count < level.Chords.Count,
            "Every chord compacted — the decision is stuck at true.");
    }

    /// <summary>
    ///     Determinism at scale: the same level must produce the same set every
    ///     time, and the result must not depend on the chord list's insertion
    ///     ORDER (<c>Compute</c> sorts by position first). Shuffling the input
    ///     is what would catch a binary search that assumed an ordering the
    ///     caller does not guarantee.
    /// </summary>
    [Fact]
    public void TheDecision_DoesNotDependOnTheOrderChordsWereAddedIn()
    {
        var level = BuildDenseLevel(7, 800, 6);
        var beats = Beats(level.Chords[^1].Position.BeatId + 8);
        var expected = RsChordDensity.Compute(level, beats, _ => 0);

        var shuffled = new ChartLevel();
        var rng = new Random(99);
        foreach (var chord in level.Chords.OrderBy(_ => rng.Next())) shuffled.Chords.Add(chord);
        foreach (var note in level.Notes.OrderBy(_ => rng.Next())) shuffled.Notes.Add(note);
        foreach (var shape in level.HandShapes) shuffled.HandShapes.Add(shape);

        var actual = RsChordDensity.Compute(shuffled, beats, _ => 0);

        Assert.Equal(expected.Count, actual.Count);
        Assert.True(expected.SetEquals(actual),
            $"{expected.Except(actual).Count()} chord(s) compacted only in the sorted order and " +
            $"{actual.Except(expected).Count()} only in the shuffled order.");
    }

    /// <summary>
    ///     The decision itself, pinned. Each expected value is a SHA-256 over the
    ///     positions of the chords that compacted, captured by running this exact
    ///     fixture against the code as it stood BEFORE the binary search
    ///     (<c>9cc50f87</c>). It is the fixture-free counterpart of
    ///     <see cref="ExportBytePinTests" />: no <c>TestData/</c>, so it runs in
    ///     every checkout, and it fails on any chord whose decision moved.
    /// </summary>
    [Theory]
    [InlineData(1, 400, 4, "43863962a78793ed")]
    [InlineData(2, 900, 2, "4ea566668d4f876e")]
    [InlineData(3, 1500, 8, "34b2a5a49158d7b7")]
    [InlineData(4, 1200, 400, "7746950838540f4d")]
    public void TheCompactRepeatDecision_MatchesThePreOptimisationImplementation(
        int seed, int chordCount, int shapeSpanBeats, string expected)
    {
        var level = BuildDenseLevel(seed, chordCount, shapeSpanBeats);
        var beats = Beats(level.Chords[^1].Position.BeatId + 8);

        Assert.Equal(expected, DecisionFingerprint(RsChordDensity.Compute(level, beats, _ => 0), level));
    }

    /// <summary>
    ///     A stable hash of WHICH chords compacted — by index into the
    ///     position-sorted chord list, so it does not depend on object identity
    ///     or on the order the fixture happened to add them in.
    /// </summary>
    internal static string DecisionFingerprint(HashSet<Chord> compact, ChartLevel level)
    {
        var sorted = level.Chords.OrderBy(c => c.Position).ToList();
        var flags = new byte[sorted.Count];
        for (var i = 0; i < sorted.Count; i++) flags[i] = compact.Contains(sorted[i]) ? (byte)1 : (byte)0;
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(flags))[..16];
    }

    /// <summary>
    ///     The scans this replaced were O(chords x sounds): 4x the chords cost
    ///     ~16x the time. Wall-clock, so it is env-gated out of the default gate
    ///     exactly like <c>RefretPerfGateTests</c> — a timing assertion in the
    ///     gate flakes the moment suites run in parallel.
    ///     Run: <c>DOTCHARTER_PERF_GATE=1 dotnet test --filter RsChordDensityLookup</c>
    /// </summary>
    [Fact]
    public void TheDecision_ScalesNearLinearlyInLevelSize()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("DOTCHARTER_PERF_GATE") != "1",
            "Dev-machine wall-clock gate — set DOTCHARTER_PERF_GATE=1 to run.");

        var beatsSmall = Beats(4000);
        var beatsLarge = Beats(8000);
        var small = BuildDenseLevel(11, 1000, 6);
        var large = BuildDenseLevel(11, 4000, 6);

        RsChordDensity.Compute(small, beatsSmall, _ => 0);
        RsChordDensity.Compute(large, beatsLarge, _ => 0);

        static double Best(ChartLevel level, List<BeatMarker> beats)
        {
            var best = double.MaxValue;
            for (var i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                RsChordDensity.Compute(level, beats, _ => 0);
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }

            return best;
        }

        var smallMs = Best(small, beatsSmall);
        var largeMs = Best(large, beatsLarge);
        var ratio = largeMs / Math.Max(0.001, smallMs);

        // Linear -> ~4x; quadratic -> ~16x. The bar sits between them.
        Assert.True(ratio < 9.0,
            $"4x the chords cost {ratio:F1}x the time ({smallMs:F1} ms -> {largeMs:F1} ms). " +
            "That is the quadratic scan shape the binary search replaced.");
    }
}
