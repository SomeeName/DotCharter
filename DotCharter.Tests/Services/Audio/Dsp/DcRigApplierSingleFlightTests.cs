using System.Reflection;
using DotCharter.Services.Audio.Coordination;
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
    public async Task TheCoordinatorsOwnApplyGateStaysBecauseItOrdersAppliesAgainstTheRestore()
    {
        var chain = new FakeDspChainService();
        chain.AddNode("reverb");
        var project = new ChartProject();
        for (var i = 0; i < 8; i++) project.Beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { ToneBase = "clean" };
        project.Arrangements.Add(arrangement);
        project.ToneDefinitions.Add(new ToneDefinition
        {
            Key = "clean", Name = "clean",
            Json = """{"Key":"clean","Name":"clean","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}}}}"""
        });
        var state = new ProjectState { ActiveProject = project, ActiveArrangement = arrangement };
        var coordinator = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        coordinator.PrepareForProject(project, arrangement);
        var gate = typeof(RsTonePlaybackCoordinator).GetField("_applyGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        var applies = 0;
        chain.BeforeAddNode = () =>
        {
            Assert.True(Monitor.IsEntered(gate), "Tone applies and player restores must own the coordinator's ordering gate.");
            applies++;
        };

        Assert.True(coordinator.BeginPreview());
        Assert.True(applies > 0);
        applies = 0;
        coordinator.EndPreview();
        await coordinator.RestoreCompletion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(applies > 0);
        Assert.Equal("reverb", Assert.Single(chain.GetNodes()).ModuleTypeId);
    }

    /// <summary>Seat 1's tone must not wait behind seat 0's: the gate is
    /// per-applier, and an applier is per chain.
    /// <para>#607: seat 1 used to be parked by a 60 s spin whose result was
    /// ignored, so with ONE global gate seat 0 simply waited the spin out and the
    /// test passed. Now seat 1 stays parked until the test releases it, which
    /// happens only after seat 0 has RETURNED (60 s deadlock detector); seat 1's
    /// own hold outlives that detector, so it cannot let seat 0 through first.</para></summary>
    [Fact]
    public async Task TheSingleFlightIsPerApplierNotGlobal()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new[] { Module("amp") };
        var seat0 = new DcRigApplier(new FakeDspChainService(catalog));

        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var heldPastTheDetector = false;
        var chain1 = new FakeDspChainService(catalog)
        {
            BeforeAddNode = () =>
            {
                inside.TrySetResult();
                heldPastTheDetector = !release.Wait(SeatOneHold);
            }
        };
        var seat1 = new DcRigApplier(chain1);

        var busy = Task.Run(() => seat1.Apply(Rig("amp")), ct);
        try
        {
            await BoundedWait.ForAsync(Task.WhenAny(inside.Task, busy), "seat 1's apply to park mid-plan", ct);
            Assert.False(busy.IsCompleted, "seat 1's apply finished without parking mid-plan");

            // Seat 0 goes right through while seat 1 is parked mid-plan.
            var r = await BoundedWait.ForAsync(Task.Run(() => seat0.Apply(Rig("amp")), ct),
                "seat 0's apply while seat 1 is parked (a global gate waits here)", ct);
            Assert.False(r.NoOp);
        }
        finally
        {
            release.Set();
        }

        await BoundedWait.ForAsync(busy, "seat 1's apply after its release", ct);
        Assert.False(heldPastTheDetector, "seat 1's hold expired: seat 0 was let through by the hold, not by the gate");
    }

    /// <summary>Longer than <see cref="BoundedWait.DeadlockCap" />, so seat 0 waiting
    /// behind seat 1 fails on the detector before this hold could end it.</summary>
    private static readonly TimeSpan SeatOneHold = TimeSpan.FromMinutes(5);

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
