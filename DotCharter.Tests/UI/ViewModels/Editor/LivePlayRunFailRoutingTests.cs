using System.Reflection;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The rock meter's fail-out: the run that could not fail.
///     <para>
///         <see cref="RockMeter" /> latches <c>HasFailed</c> from EITHER <c>Tick</c> or <c>OnVerdict</c>,
///         and raises <c>Failed</c> from whichever crossed the debounce. The app
///         subscribed to that event nowhere: its only fail detector was
///         <c>PumpRockMeter</c>'s <c>Tick</c> return value, while
///         <c>OnJudged</c> discards what <c>OnVerdict</c> did. A verdict's
///         <c>DetectedAtMs</c> is a RAW detection timestamp and the pump's
///         playhead is LATENCY-COMPENSATED — so the verdict clock is AHEAD, and
///         crossing the debounce inside a verdict was entirely reachable. When
///         it happened the run hung UNFAILABLE: the meter latched, the event
///         fired into the void, and every later <c>Tick</c> returned false
///         forever (a failed meter is inert), so the shell never reached
///         <c>PlayModeScreen.Failed</c>.
///     </para>
///     <para>
///         <b>What these tests prove and what they do not.</b> They drive the
///         REAL <see cref="LivePlayViewModel" /> — the real
///         <c>ArmRockMeter</c> subscription, the real handler — and assert the
///         view model's own synchronous evidence that the run was failed: the
///         once-per-run latch and the log line the fail path writes. They stop
///         one line short of the <c>Dispatcher.UIThread.Post</c> that raises
///         <c>RockMeterFailed</c>, because a plain test thread's UI dispatcher
///         never pumps (and asserting it would need a headless session, which
///         puts a test outside the default gate). That last line is unchanged
///         by this fix — it is the same post the tick route has always used —
///         and the shell's half of it is covered where the shell is tested.
///     </para>
/// </summary>
public sealed class LivePlayRunFailRoutingTests
{
    // ---- the fail latch, from BOTH routes ------------------------------------

    /// <summary>The bug. A meter that crosses the debounce inside
    /// <c>OnVerdict</c> — never on a tick — must still end the run. Against the
    /// unfixed code nothing is subscribed to <c>Failed</c>, so the view model
    /// learns nothing and this fails.</summary>
    [Fact]
    public void AFailLatchedInsideAVerdict_StillEndsTheRun()
    {
        var vm = ArmedRun(out var logger);
        var meter = RockMeterOf(vm);

        DrainToZero(meter);                                  // energy 0, debounce started at t=0
        Assert.False(meter.HasFailed);                       // …and not yet failed
        meter.OnVerdict(LivePlayVerdict.Miss, 0, RockMeter.FailDebounceMs); // crosses it INSIDE the verdict

        Assert.True(meter.HasFailed, "the meter itself must have latched — otherwise this test proves nothing");
        Assert.True(RunFailPosted(vm),
            "the run was never failed: the meter latched inside OnVerdict and nothing in the app was listening, " +
            "so the player is left at zero energy on a run that can no longer fail");
        Assert.Equal(1, FailLogLines(logger));
    }

    /// <summary>…and the route that always worked still works: a debounce
    /// crossed by the session tick fails the run exactly as before. This is the
    /// regression guard on the fix, not on the bug.</summary>
    [Fact]
    public void AFailLatchedOnTheSessionTick_StillEndsTheRun()
    {
        var vm = ArmedRun(out var logger);
        var meter = RockMeterOf(vm);

        DrainToZero(meter);
        PumpRockMeter(vm, RockMeter.FailDebounceMs);

        Assert.True(meter.HasFailed);
        Assert.True(RunFailPosted(vm));
        Assert.Equal(1, FailLogLines(logger));
    }

    /// <summary>One run, one failure. The meter raises <c>Failed</c> once by
    /// construction, but the tick route reads a return value as well, so the
    /// view model's own one-shot has to hold when both look at the same
    /// latch.</summary>
    [Fact]
    public void TheRunFailsOnce_EvenWhenBothRoutesSeeTheSameLatch()
    {
        var vm = ArmedRun(out var logger);
        var meter = RockMeterOf(vm);

        DrainToZero(meter);
        meter.OnVerdict(LivePlayVerdict.Miss, 0, RockMeter.FailDebounceMs); // route A latches
        PumpRockMeter(vm, RockMeter.FailDebounceMs + 5_000);                // route B looks after it
        PumpRockMeter(vm, RockMeter.FailDebounceMs + 10_000);
        meter.OnVerdict(LivePlayVerdict.Miss, 0, RockMeter.FailDebounceMs + 12_000);

        Assert.Equal(1, FailLogLines(logger));
    }

    /// <summary>Why the missing subscription was fatal rather than merely
    /// late — pure <see cref="RockMeter" />, no view model. A meter that
    /// latched inside a verdict answers EVERY later <c>Tick</c> with false, so
    /// a tick-only detector never sees the failure at all. (This one describes
    /// the mechanism; it passes against the unfixed code too.)</summary>
    [Fact]
    public void AVerdictLatchIsPermanentlyInvisibleToTheTickRoute()
    {
        var meter = new RockMeter();
        DrainToZero(meter);
        meter.OnVerdict(LivePlayVerdict.Miss, 0, RockMeter.FailDebounceMs);

        Assert.True(meter.HasFailed);
        Assert.False(meter.Tick(RockMeter.FailDebounceMs + 1_000));
        Assert.False(meter.Tick(RockMeter.FailDebounceMs + 600_000));
    }

    // ---- the wiring itself (source lints) ------------------------------------

    /// <summary>The subscription is made where the meter is ARMED and dropped
    /// where it is torn down. A meter armed without it is the bug again, and a
    /// subscription never dropped keeps a dead run's meter attached to the view
    /// model.</summary>
    [Fact]
    public void TheMeterFailEvent_IsSubscribedWhereTheMeterIsArmed_AndDroppedOnDisarm()
    {
        var source = SourceText.StripCommentLines(LivePlayViewModelSource());

        Assert.Contains("Failed += OnRockMeterLatchedFailure", source, StringComparison.Ordinal);
        Assert.Contains("Failed -= OnRockMeterLatchedFailure", source, StringComparison.Ordinal);
    }

    /// <summary>Both fail routes — the meter and sudden death — post through
    /// ONE helper, so the once-per-run guard cannot be bypassed by adding a
    /// third caller that posts for itself.</summary>
    [Fact]
    public void TheRunFailIsPostedFromExactlyOnePlace()
    {
        var source = SourceText.StripCommentLines(LivePlayViewModelSource());

        var posts = Occurrences(source, "RockMeterFailed?.Invoke()");
        Assert.True(posts == 1,
            $"expected exactly one place to raise RockMeterFailed (PostRunFailed), found {posts}");
        Assert.Contains("Dispatcher.UIThread.Post(() => RockMeterFailed?.Invoke());", source, StringComparison.Ordinal);
    }

    // ---- fixture -------------------------------------------------------------

    private static LivePlayViewModel NewViewModel(out FakeAppLogger logger)
    {
        logger = new FakeAppLogger();
        return LivePlayTestFactory.Create(
            new FakeAudioPlaybackService { InputSampleRate = 48_000 },
            new ProjectState(),
            logger,
            new FakeConfigurationService(),
            new InMemoryPracticeStore());
    }

    /// <summary>A one-beat chart with one empty level — enough for
    /// <c>StartCore</c> and for <c>LivePlaySessionService.Start</c>.</summary>
    private static void LoadChart(LivePlayViewModel vm)
    {
        var state = GetField<ProjectState>(vm, "_projectState");
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel());
        state.Beats.Add(new BeatMarker { Time = 0, IsMeasure = true });
        state.ActiveArrangement = arrangement; // also sets ActiveLevel
    }

    /// <summary>A view model with an ACTIVE session and an ARMED rock meter —
    /// the state a gameplay run plays in. The session is started directly so
    /// the detection worker never runs: this file is about what happens to a
    /// verdict, not about pumping audio.</summary>
    private static LivePlayViewModel ArmedRun(out FakeAppLogger logger)
    {
        var vm = NewViewModel(out logger);
        LoadChart(vm);

        var state = GetField<ProjectState>(vm, "_projectState");
        var session = GetField<LivePlaySessionService>(vm, "_session");
        session.Start(state.ActiveArrangement!, state.ActiveLevel, state.Beats, new LivePlaySessionOptions());

        vm.ArmRockMeter();
        Assert.True(vm.Meters.IsRockMeterActive, "the meter did not arm — the rest of this test would be vacuous");
        return vm;
    }

    /// <summary>Sustained missing until the meter sits below the fail
    /// threshold, all at t=0 so the debounce STARTS there and no tick is
    /// involved. Iteration-capped so a constants change fails loudly instead of
    /// hanging.</summary>
    private static void DrainToZero(RockMeter meter)
    {
        for (var i = 0; i < 200 && meter.Energy(0) >= RockMeter.FailThreshold; i++)
            meter.OnVerdict(LivePlayVerdict.Miss, 0, nowMs: 0);

        Assert.True(meter.Energy(0) < RockMeter.FailThreshold,
            "sustained missing no longer empties the meter — this fixture is stale");
    }

    // ---- reflection over the view model's worker-confined state --------------
    //
    // The fail path is private and worker-confined by design (it runs under
    // _sessionGate on the detection thread), so the test reaches it the way the
    // worker does rather than widening the type's surface for a test.

    private static RockMeter RockMeterOf(LivePlayViewModel vm) => GetField<RockMeter>(vm.Meters, "_rockMeter");

    private static bool RunFailPosted(LivePlayViewModel vm) => GetField<bool>(vm.Meters, "_runFailPosted");

    private static void PumpRockMeter(LivePlayViewModel vm, double playheadMs)
        => vm.Meters.PumpRockMeter(playheadMs, isPlaying: true);

    private static int FailLogLines(FakeAppLogger logger)
        => logger.Entries.Count(e => e.Level == "INFO" &&
                                     e.Message.Contains("failed the run", StringComparison.OrdinalIgnoreCase));

    private static T GetField<T>(object owner, string name)
    {
        var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field != null, $"{owner.GetType().Name} has no field '{name}' — this test was renamed out of its job");
        return (T)field!.GetValue(owner)!;
    }

    // ---- source lint helpers -------------------------------------------------

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The WHOLE partial set plus the collaborators split out of it
    /// (<c>LivePlay*ViewModel*.cs</c>, so <c>LivePlayMetersViewModel.cs</c> too):
    /// a guard that reads one file goes silently blind the day a member moves
    /// to a sibling.</summary>
    private static string LivePlayViewModelSource()
    {
        var folder = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor"));
        var files = UiSourceTree.GetFiles(folder, "LivePlay*ViewModel*.cs").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Contains(files, f => Path.GetFileName(f) == "LivePlayMetersViewModel.cs");
        return string.Join("\n", files.Select(UiSourceTree.ReadAllText));
    }

    /// <summary>The practice store is never reached by these tests (no run is
    /// stopped); it exists because the view model takes one.</summary>
    private sealed class InMemoryPracticeStore : IPracticeProfileService
    {
        private readonly Dictionary<string, PracticeArrangementProfile> _profiles = new(StringComparer.Ordinal);

        public PracticeArrangementProfile GetProfile(string songKey, string arrangementName)
            => _profiles.TryGetValue(songKey + "|" + arrangementName, out var p) ? p : new PracticeArrangementProfile();

        public PracticeArrangementProfile Record(string songKey, string arrangementName,
            LivePlaySessionResult result, string? displayName = null,
            IReadOnlyList<PhraseMasteryOutcome>? phraseOutcomes = null,
            IReadOnlyList<SectionResult>? sections = null,
            IReadOnlyList<TechniqueResult>? techniques = null,
            double? chartDifficulty = null, bool failed = false)
        {
            var key = songKey + "|" + arrangementName;
            if (!_profiles.TryGetValue(key, out var profile)) _profiles[key] = profile = new PracticeArrangementProfile();
            return profile;
        }

        public IReadOnlyDictionary<string, PracticeArrangementProfile> GetAll() => _profiles;
        public bool AttachTakeFile(string songKey, string arrangementName, string takeFileName) => false;
        public bool AttachReplayFile(string songKey, string arrangementName, string replayFileName) => false;
        public void InvalidateCache() { }

        public bool IsStoreAuthoritative => true;
    }
}
