using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
/// H12: <c>DcRigApplier</c>'s "callers must serialize Apply" was stated and
/// unenforced.
/// <para>
/// The applier plans its WHOLE diff from one <c>GetNodes()</c> snapshot, while
/// <c>JuceDspChainService._gate</c> serializes individual ops rather than plans.
/// Several call sites reach seat 0's applier — <c>ToneSessionService.RestoreOnce</c>
/// (engine-attach thread), the preset browser (UI),
/// <c>ProfileToneRigCoordinator</c> and <c>RsTonePlaybackCoordinator</c> — and
/// only the last single-flighted it, under a PRIVATE gate no other caller could
/// take. An interleave removes nodes the other planned to reuse and shifts every
/// <c>MoveNode</c> index, leaving a blend of two tones with the damage counted
/// as <c>FailedOps</c> and misread as native rejection.
/// </para>
/// </summary>
public sealed class DcRigApplierSingleFlightTests
{
    /// <summary>
    /// The behavioural one, deterministic rather than timed: the fake chain
    /// hands control to a SECOND full apply from inside the first one's
    /// <c>RemoveNode</c> — the exact interleave point. With the applier's own
    /// single-flight the second plan waits, so the chain ends as exactly one of
    /// the two presets instead of a blend.
    /// </summary>
    [Fact]
    public async Task AnApplyThatIsReEnteredMidPlanStillLeavesExactlyOneOfTheTwoPresets()
    {
        var catalog = new[]
        {
            Module("amp"), Module("cab"), Module("delay"), Module("reverb"),
        };
        var chain = new FakeDspChainService(catalog);
        var applier = new DcRigApplier(chain);

        // Start from a four-module rig so the "clean" plan below has real
        // removals and moves to interleave with.
        applier.Apply(Rig("amp", "cab", "delay", "reverb"));

        var clean = Rig("amp", "cab");
        var heavy = Rig("delay", "amp", "reverb");

        // A plan begins with exactly one GetNodes(), so this counts plan STARTS.
        var plans = 0;
        chain.OnGetNodes = () => Interlocked.Increment(ref plans);

        Task? second = null;
        Thread? secondThread = null;
        var started = new ManualResetEventSlim(false);

        chain.BeforeRemoveNode = () =>
        {
            if (second != null) return;
            var before = Volatile.Read(ref plans);
            second = Task.Run(() =>
            {
                secondThread = Thread.CurrentThread;
                started.Set();
                applier.Apply(heavy);
            });

            // Wait for the second thread to reach a DECIDABLE state, never for a
            // fixed time: either it is parked on the applier's gate (correct —
            // Monitor.Enter blocks in WaitSleepJoin) or it has already started a
            // second plan (the bug). Both are observable, so this cannot deadlock
            // and cannot pass by simply not scheduling the other thread.
            Assert.True(SpinUntil(() => started.IsSet), "the second apply never started");
            Assert.True(
                SpinUntil(() => Volatile.Read(ref plans) > before
                                || secondThread!.ThreadState.HasFlag(ThreadState.WaitSleepJoin)),
                "the second apply neither began nor parked");
        };

        applier.Apply(clean);
        chain.BeforeRemoveNode = null;
        Assert.NotNull(second);
        await second!;

        var final = chain.GetNodes().Select(n => n.ModuleTypeId).ToArray();
        Assert.True(
            final.SequenceEqual(["amp", "cab"]) ||
            final.SequenceEqual(["delay", "amp", "reverb"]),
            "The chain must equal exactly ONE of the two presets, never a blend of both. Got: "
            + string.Join(", ", final));
    }

    /// <summary>
    /// The claim in the class doc must match the code. It said callers were
    /// responsible; four of the five never were.
    /// </summary>
    [Fact]
    public void TheApplierSerializesItsOwnPlan()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            Path.Combine("DotCharter.Tone", "Audio", "Dsp", "RsToneConversion", "DcRigApplier.cs")));

        Assert.Contains("lock (_planGate) return ApplyLocked(preset);", src, StringComparison.Ordinal);
        Assert.DoesNotContain("must serialize <see cref=\"Apply\"/> (the coordinator single-flights it)",
            src, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The coordinator's own <c>_applyGate</c> is NOT redundant and must
    /// stay</b> — this case exists because removing it looked obviously right and
    /// was not. The applier's lock makes each PLAN atomic; the coordinator's gate
    /// decides WHICH PLAN HAPPENS FIRST, ordering a run's tone applies against
    /// <c>StopDriving</c>'s generation check + restore.
    /// Dropping it from the two bare applies made an outgoing run's restore
    /// evaluate its check mid-apply and land afterwards, so the incoming run wore
    /// the outgoing run's tone. Recorded here so the next reader does not repeat the tidy-up.
    /// </summary>
    [Fact]
    public void TheCoordinatorsOwnApplyGateStaysBecauseItOrdersAppliesAgainstTheRestore()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            Path.Combine("DotCharter.Services", "Audio", "Coordination", "RsTonePlaybackCoordinator.cs")));
        Assert.Contains("lock (_applyGate) result = _applier.Apply(", src, StringComparison.Ordinal);
    }

    /// <summary>Seat 1's tone must not wait behind seat 0's: the gate is
    /// per-applier, and an applier is per chain.</summary>
    [Fact]
    public async Task TheSingleFlightIsPerApplierNotGlobal()
    {
        var catalog = new[] { Module("amp") };
        var seat0 = new DcRigApplier(new FakeDspChainService(catalog));

        var inside = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var chain1 = new FakeDspChainService(catalog) { BeforeAddNode = () => { inside.Set(); SpinUntil(() => release.IsSet); } };
        var seat1 = new DcRigApplier(chain1);

        var busy = Task.Run(() => seat1.Apply(Rig("amp")));
        Assert.True(SpinUntil(() => inside.IsSet), "seat 1's apply never started");

        // Seat 0 goes right through while seat 1 is parked mid-plan.
        var r = seat0.Apply(Rig("amp"));
        Assert.False(r.NoOp);

        release.Set();
        await busy;
    }

    // ----- helpers -----------------------------------------------------------

    private static DspModuleType Module(string id) => new(id, id, []);

    private static DcRigPreset Rig(params string[] types) =>
        new([.. types.Select(t => new DcRigModule(t, new Dictionary<string, float>()))]);

    private static bool SpinUntil(Func<bool> condition)
    {
        // A 60 s DEADLOCK DETECTOR, never a budget.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(2);
        }

        return condition();
    }
}
