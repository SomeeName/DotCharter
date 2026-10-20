using DotCharter.Services.Gameplay;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
/// Pure schedule for the ADDITIONAL count-in placed on the beat grid immediately
/// before the first expected sound (a long noteless intro would otherwise dump
/// the player into silence and then unannounced notes). Beat selection comes from
/// the tempo map's actual beat times (so a tempo change inside the window is
/// honoured), gated on the first note sitting ≥ N + ClearanceBeats past the run
/// start. Injected data / no audio / no clock.
/// </summary>
public class FirstNoteCountInPlanTests
{
    // A steady 4/4 grid at 120 bpm (500 ms/beat) starting at t=0.
    private static IReadOnlyList<double> Grid(int count, double startMs = 0, double msPerBeat = 500)
    {
        var beats = new double[count];
        for (var i = 0; i < count; i++) beats[i] = startMs + i * msPerBeat;
        return beats;
    }

    [Fact]
    public void Off_ReturnsEmpty()
        => Assert.Empty(FirstNoteCountInPlan.Build(Grid(40), firstNoteMs: 10_000, runStartMs: 0, beats: 0, beatsPerMeasure: 4));

    [Fact]
    public void NoBeats_ReturnsEmpty()
    {
        Assert.Empty(FirstNoteCountInPlan.Build(System.Array.Empty<double>(), 10_000, 0, 4, 4));
        Assert.Empty(FirstNoteCountInPlan.Build(null, 10_000, 0, 4, 4));
    }

    [Fact]
    public void LongIntro_PlacesClicksOnTheFourBeatsBeforeTheFirstNote()
    {
        // First note on beat 20 (10 s); run starts at 0. The last 4 beats before
        // the note are beats 16..19 (8000, 8500, 9000, 9500).
        var plan = FirstNoteCountInPlan.Build(Grid(40), firstNoteMs: 10_000, runStartMs: 0, beats: 4, beatsPerMeasure: 4);

        Assert.Equal(4, plan.Length);
        Assert.Equal(new[] { 8000.0, 8500.0, 9000.0, 9500.0 }, plan.Select(c => c.TimeMs));
    }

    [Fact]
    public void EightBeats_TakesTheEightBeatsBeforeTheNote_FirstOfEachMeasureAccented()
    {
        var plan = FirstNoteCountInPlan.Build(Grid(40), firstNoteMs: 10_000, runStartMs: 0, beats: 8, beatsPerMeasure: 4);

        Assert.Equal(8, plan.Length);
        // Beats 12..19; earliest first.
        Assert.Equal(6000.0, plan[0].TimeMs);
        Assert.Equal(9500.0, plan[7].TimeMs);
        // Accent mirrors the run-start count-in: index 0 and index 4.
        Assert.True(plan[0].Accent);
        Assert.True(plan[4].Accent);
        for (var i = 0; i < plan.Length; i++)
        {
            if (i is not (0 or 4))
                Assert.False(plan[i].Accent, $"click {i} should not be accented");
        }
    }

    [Fact]
    public void FirstNoteWithinReach_ReturnsEmpty()
    {
        // Only 5 beats between run start (0) and the first note (beat 5 = 2500).
        // For N=4 the gate needs N + 2 = 6, so nothing is added.
        var plan = FirstNoteCountInPlan.Build(Grid(40), firstNoteMs: 2500, runStartMs: 0, beats: 4, beatsPerMeasure: 4);
        Assert.Empty(plan);
    }

    [Fact]
    public void ExactlyAtGate_Applies()
    {
        // Exactly N + ClearanceBeats = 6 beats before the note (beats 1..5 after
        // run start plus the note on beat 6). Beats in (0, firstNote) are 1..5? No —
        // first note on beat 6 = 3000; beats strictly inside are 500..2500 = 5.
        // Bump the note one beat so the window holds exactly 6.
        var plan = FirstNoteCountInPlan.Build(Grid(40), firstNoteMs: 3500, runStartMs: 0, beats: 4, beatsPerMeasure: 4);
        Assert.Equal(4, plan.Length);
        // Last 4 of beats {500,1000,1500,2000,2500,3000} → 1500..3000.
        Assert.Equal(new[] { 1500.0, 2000.0, 2500.0, 3000.0 }, plan.Select(c => c.TimeMs));
    }

    [Fact]
    public void RunStartOffsetZero_OnlyBeatsAfterTheStartCount()
    {
        // Run starts at 5000 (beat 10). First note at 10_000 (beat 20). Only beats
        // strictly after 5000 and before 10_000 are eligible → beats 11..19.
        var plan = FirstNoteCountInPlan.Build(Grid(40), firstNoteMs: 10_000, runStartMs: 5000, beats: 4, beatsPerMeasure: 4);
        Assert.Equal(4, plan.Length);
        Assert.All(plan, c => Assert.True(c.TimeMs is > 5000 and < 10_000));
        Assert.Equal(9500.0, plan[^1].TimeMs); // beat 19, immediately before the note
    }

    [Fact]
    public void FirstNoteAtZero_ReturnsEmpty()
        => Assert.Empty(FirstNoteCountInPlan.Build(Grid(40), firstNoteMs: 0, runStartMs: 0, beats: 4, beatsPerMeasure: 4));

    [Fact]
    public void PickupBar_NoteBeforeFirstDownbeat_StillGatesOnAvailableBeats()
    {
        // A pickup chart: grid starts at 2000 with the note landing on the very
        // first beat region. Only 2 beats precede a note at 3200 → below the gate.
        var grid = Grid(40, startMs: 2000);
        var plan = FirstNoteCountInPlan.Build(grid, firstNoteMs: 3200, runStartMs: 0, beats: 4, beatsPerMeasure: 4);
        Assert.Empty(plan);
    }

    [Fact]
    public void TempoChangeInsideWindow_UsesActualBeatTimes()
    {
        // Beats accelerate: 0, 1000, 2000, 3000 (slow), then 3500, 4000, 4500,
        // 5000, 5500 (fast). First note at 6000. The last 4 beats before it are
        // the FAST ones (4000, 4500, 5000, 5500) — proving the plan reads actual
        // beat times, not a single BPM.
        var beats = new double[] { 0, 1000, 2000, 3000, 3500, 4000, 4500, 5000, 5500 };
        var plan = FirstNoteCountInPlan.Build(beats, firstNoteMs: 6000, runStartMs: 0, beats: 4, beatsPerMeasure: 4);
        Assert.Equal(new[] { 4000.0, 4500.0, 5000.0, 5500.0 }, plan.Select(c => c.TimeMs));
    }

    [Fact]
    public void AbsurdBpm_VeryCloseBeats_StillSelectsLastN()
    {
        // 1000 bpm-ish: 1 ms apart. The plan does no BPM math, so it still picks
        // the last N beats before the note.
        var beats = new double[64];
        for (var i = 0; i < beats.Length; i++) beats[i] = i; // 0..63 ms
        var plan = FirstNoteCountInPlan.Build(beats, firstNoteMs: 40, runStartMs: 0, beats: 8, beatsPerMeasure: 4);
        Assert.Equal(8, plan.Length);
        Assert.Equal(39.0, plan[^1].TimeMs); // immediately before the note
        Assert.Equal(32.0, plan[0].TimeMs);
    }

    [Fact]
    public void NonFiniteEntries_AreIgnored()
    {
        var beats = new[] { 0, double.NaN, 500, double.PositiveInfinity, 1000, 1500, 2000, 2500, 3000 };
        var plan = FirstNoteCountInPlan.Build(beats, firstNoteMs: 3500, runStartMs: 0, beats: 4, beatsPerMeasure: 4);
        Assert.Equal(4, plan.Length);
        Assert.All(plan, c => Assert.True(double.IsFinite(c.TimeMs)));
    }
}
