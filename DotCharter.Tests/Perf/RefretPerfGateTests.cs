using System.Diagnostics;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Refret;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Dev-machine wall-clock gate for the refret engine (env-gated like
///     StartupPerfGateTests — absolute timings are meaningless on hosted CI).
///     A 3,000-sound arrangement must refret in under 2 s; the DP is
///     O(units · states²) with ≤ 4·beam states per unit (see RefretPlanner),
///     so this is a regression tripwire, not a stretch goal.
///     Run: DOTCHARTER_PERF_GATE=1 dotnet test --filter RefretPerfGate
///     <para>
///         <b>Nothing in this repo sets DOTCHARTER_PERF_GATE</b>, so both
///         wall-clock cases below run on no machine and never have — which
///         meant the quadratic-collision-sweep tripwire guarded a shipped bug
///         with an assertion that never executed. The COMPLEXITY half of that
///         claim is now a structural assertion in the DEFAULT gate
///         (<see cref="TheCollisionSweepStaysLinearInTheNumberOfSounds" />):
///         it counts the sweep's inner-loop pair visits instead of timing it,
///         which is this repo's own "assert STRUCTURE and LOG the timing" rule
///         applied to the one place it had been skipped. The wall-clock bounds
///         stay env-gated, because a millisecond budget in a parallel gate is a
///         flake. See DotCharter.Tests/AGENTS.md for the full list of opt-in
///         gate variables and what each one leaves uncovered.
///     </para>
/// </summary>
public sealed class RefretPerfGateTests
{
    private const int BudgetMs = 2000;

    [Fact]
    public void Refretting_a_3000_note_arrangement_stays_under_two_seconds()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("DOTCHARTER_PERF_GATE") != "1",
            "Dev-machine wall-clock gate — set DOTCHARTER_PERF_GATE=1 to run.");

        var rng = new Random(42);
        var arr = new Arrangement { StringCount = 6, TuningType = "E_STANDARD" };
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        arr.Levels.Add(level);

        var beats = new List<BeatMarker>();
        for (var i = 0; i < 1600; i++)
            beats.Add(new BeatMarker { Time = i * 400.0, Bpm = 150, IsMeasure = i % 4 == 0 });

        var soundCount = 0;
        for (var beat = 1; soundCount < 3000 && beat < 1550; beat++)
        {
            for (var sixteenth = 0; sixteenth < 4 && soundCount < 3000; sixteenth += rng.Next(1, 3))
            {
                var pos = new FractionalPosition(beat, new Fraction(sixteenth, 4));
                if (rng.Next(6) == 0)
                {
                    var startStr = rng.Next(0, 4);
                    var baseFret = rng.Next(1, 10);
                    var template = new ChordTemplate { Id = arr.ChordTemplates.Count, Name = $"C{soundCount}" };
                    template.Frets[startStr] = baseFret;
                    template.Frets[startStr + 1] = baseFret + 2;
                    template.Frets[startStr + 2] = baseFret + 2;
                    arr.ChordTemplates.Add(template);

                    var chord = new Chord { Position = pos, TemplateId = template.Id };
                    foreach (var kv in template.Frets.OrderBy(kv => kv.Key))
                        chord.ChordNotes.Add(new ChordNote { StringIndex = kv.Key, Fret = kv.Value });
                    level.Chords.Add(chord);
                    soundCount += 3;
                }
                else
                {
                    level.Notes.Add(new GuitarNote
                    {
                        Position = pos,
                        StringIndex = rng.Next(6),
                        Fret = rng.Next(0, 16)
                    });
                    soundCount++;
                }
            }
        }

        var service = new RefretService(new FretAnchorGenerationService(), new FingeringGenerationService());

        var noteParts = level.Notes.Count + level.Chords.Sum(c => c.ChordNotes.Count);
        Assert.True(noteParts >= 2900, $"Fixture too small: {noteParts} note parts.");

        var stopwatch = Stopwatch.StartNew();
        var result = service.RefretArrangement(arr, beats);
        stopwatch.Stop();

        Assert.Equal(level.Notes.Count + level.Chords.Count, result.SoundsConsidered);
        Assert.True(stopwatch.ElapsedMilliseconds < BudgetMs,
            $"Refret took {stopwatch.ElapsedMilliseconds} ms for {result.SoundsConsidered} sounds (budget {BudgetMs} ms).");
    }

    /// <summary>
    ///     The pathological input for <c>RefretPlanner.RevertCollisions</c>: nearly
    ///     every sound on TWO strings, with long sustains so the same-string
    ///     occupancy blocks are huge. The sweep's inner loop only ever broke on a
    ///     string CHANGE, so it rescanned the whole block per entry — quadratic per
    ///     pass, with up to units.Count passes and RefretService re-planning up to
    ///     MaxPasses times on top. Same 2 s budget as the wide fixture: this shape
    ///     must not be dramatically more expensive than a normal chart.
    /// </summary>
    /// <summary>
    ///     The STRUCTURAL half of the case below, and the only one that runs
    ///     anywhere: <c>RefretPlanner.RevertCollisions</c> must stay linear in
    ///     the number of sounds.
    ///     <para>
    ///         The shipped bug: the sweep's inner loop only ever broke on a
    ///         string CHANGE, so on a chart where most sounds live on two or
    ///         three strings it rescanned the whole same-string block for every
    ///         entry — quadratic per pass, with up to <c>units.Count</c> passes
    ///         and <c>RefretService</c> re-planning on top. The fix keeps an
    ///         ACTIVE set instead, so the sweep costs O(n) plus one visit per
    ///         genuinely overlapping pair.
    ///     </para>
    ///     <para>
    ///         Asserted as a RATIO across two fixture sizes, because a ratio is
    ///         immune to the machine: doubling the sounds roughly doubles a
    ///         linear sweep's pair visits and roughly QUADRUPLES a quadratic
    ///         one. The absolute per-sound bound underneath it catches a
    ///         regression that is quadratic at both sizes.
    ///     </para>
    ///     Wall clock is logged, never asserted — the two cases beside this one
    ///     own that, behind DOTCHARTER_PERF_GATE.
    /// </summary>
    [Fact]
    public void TheCollisionSweepStaysLinearInTheNumberOfSounds()
    {
        var small = SweepVisitsForTwoStringChart(600);
        var large = SweepVisitsForTwoStringChart(1200);

        // Non-vacuity: the fixture must actually make the sweep work. A chart
        // that produced no overlapping pairs would report 0 visits and pass any
        // complexity bound at all.
        Assert.True(small > 600,
            $"the two-string fixture produced only {small} collision-sweep pair visits — it is no longer " +
            "pathological, so this test proves nothing. Re-check the sustain overlap in " +
            "SweepVisitsForTwoStringChart.");

        var ratio = large / (double)small;
        Assert.True(ratio < 3.0,
            $"doubling the sounds multiplied the collision sweep's pair visits by {ratio:F2} " +
            $"({small} -> {large}). Linear is ~2x, quadratic is ~4x: the inner loop is rescanning the " +
            "whole same-string block again instead of keeping an active set (RefretPlanner.RevertCollisions).");

        Assert.True(large < 200L * 1200,
            $"the collision sweep made {large} pair visits over 1200 sounds ({large / 1200.0:F1} per sound). " +
            "That is not a linear sweep with a bounded overlap depth.");
    }

    /// <summary>
    ///     The pathological shape, sized: nearly every sound on TWO strings with
    ///     long overlapping ring-outs, so the same-string occupancy blocks are
    ///     huge but the genuine overlap DEPTH stays bounded. Returns the number
    ///     of inner-loop pair visits the collision sweep made.
    /// </summary>
    private static long SweepVisitsForTwoStringChart(int sounds)
    {
        var rng = new Random(1337);
        var arr = new Arrangement { StringCount = 6, TuningType = "E_STANDARD" };
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        arr.Levels.Add(level);

        var beats = new List<BeatMarker>();
        for (var i = 0; i < sounds / 2 + 64; i++)
            beats.Add(new BeatMarker { Time = i * 400.0, Bpm = 150, IsMeasure = i % 4 == 0 });

        for (var i = 0; i < sounds; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(1 + i / 2, new Fraction(i % 2 * 2, 4)),
                StringIndex = i % 2,
                Fret = rng.Next(1, 16),
                Sustain = new FractionalPosition(rng.Next(0, 4))
            });
        }

        var stopwatch = Stopwatch.StartNew();
        var plan = RefretPlanner.Plan(arr, level, beats, null, null, RefretOptions.Default);
        stopwatch.Stop();

        // Logged, not asserted — see the class doc.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{sounds} sounds: {plan.CollisionSweepVisits} sweep pair visits in {stopwatch.ElapsedMilliseconds} ms");

        return plan.CollisionSweepVisits;
    }

    [Fact]
    public void Refretting_a_two_string_heavy_arrangement_stays_under_two_seconds()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("DOTCHARTER_PERF_GATE") != "1",
            "Dev-machine wall-clock gate — set DOTCHARTER_PERF_GATE=1 to run.");

        var rng = new Random(1337);
        var arr = new Arrangement { StringCount = 6, TuningType = "E_STANDARD" };
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        arr.Levels.Add(level);

        var beats = new List<BeatMarker>();
        for (var i = 0; i < 1600; i++)
            beats.Add(new BeatMarker { Time = i * 400.0, Bpm = 150, IsMeasure = i % 4 == 0 });

        for (var i = 0; i < 3000; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(1 + i / 2, new Fraction(i % 2 * 2, 4)),
                StringIndex = i % 2,
                Fret = rng.Next(1, 16),
                // Long, deliberately overlapping ring-outs.
                Sustain = new FractionalPosition(rng.Next(0, 4))
            });
        }

        var service = new RefretService(new FretAnchorGenerationService(), new FingeringGenerationService());

        var stopwatch = Stopwatch.StartNew();
        var result = service.RefretArrangement(arr, beats);
        stopwatch.Stop();

        Assert.Equal(3000, result.SoundsConsidered);
        Assert.True(stopwatch.ElapsedMilliseconds < BudgetMs,
            $"One-string-heavy refret took {stopwatch.ElapsedMilliseconds} ms (budget {BudgetMs} ms).");
    }
}
