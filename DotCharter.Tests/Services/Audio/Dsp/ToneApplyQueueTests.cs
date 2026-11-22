using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using System.Threading.Channels;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
/// H11: the v0.48 bypass crossfade DID ship natively, and the managed callers
/// still treated remove/move as instant.
/// <para>
/// <c>DspChain::fadeOutAndWait</c> marks a node bypassed, publishes, and then
/// SLEEPS on the calling thread until the audio thread has ramped its weight to
/// zero — bounded at <c>kFadeSeconds (10 ms) + 3 blocks + 5 ms</c>, ≈47 ms at
/// 512 frames / 48 kHz — and every <c>RemoveNode</c> and <c>MoveNode</c> goes
/// through it. `DcRigApplier` is a minimal-diff planner, so an eight-module
/// preset swap is on the order of sixteen of those, driven synchronously from a
/// tone-card click: hundreds of milliseconds of frozen UI against a 150 ms
/// budget.
/// </para>
/// <para>
/// Structural, never wall-clock: these assert WHICH THREAD the ops ran on and
/// HOW MANY applies one gesture costs.
/// </para>
/// </summary>
public sealed class ToneApplyQueueTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupersededFailedCompletion_DoesNotMoveANewerPresetToItsOldScene(bool throws)
    {
        var chain = new FakeDspChainService([new DspModuleType("amp", "amp", [])]);
        PedalboardViewModel? board = null;
        var failed = false;
        var presets = new RecordingPresetService(preset =>
        {
            failed = preset.Id == "failed";
            if (failed)
            {
                board!.SelectSceneAt(1);
                if (throws) throw new InvalidOperationException("failed before changing the chain");
                return;
            }
            new DcRigApplier(chain).Apply(preset.Rig);
        }, resultFactory: () => new DcRigApplyResult(0, 0, 0, 0, 0, NoOp: false, FailedOps: failed ? 1 : 0));
        var dispatcher = new CompletionDispatcher();
        using var owned = board = TestRigBuilders.Pedalboard(chain, presets, FakeLocalization.English(), dispatcher: dispatcher);
        board.AddGear(chain.Catalog[0]);
        board.Browser.ApplyPreset(Preset("failed"));
        var failure = await dispatcher.Completions.Reader.ReadAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var next = new DspTonePreset("next", "next", DspToneCategory.Clean, string.Empty,
            new DcRigPreset([new("amp", new Dictionary<string, float>())]), DspToneOrigin.User);
        board.Browser.ApplyPreset(next);
        var success = await dispatcher.Completions.Reader.ReadAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        failure();
        success();

        Assert.Equal(0, board.ActiveSceneIndex);
        Assert.Null(chain.Scenes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedBrowserApply_DoesNotPassItsDeferredSceneToTheNextPreset(bool throws)
    {
        var chain = new FakeDspChainService([new DspModuleType("amp", "amp", [])]);
        PedalboardViewModel? board = null;
        var failed = false;
        var presets = new RecordingPresetService(preset =>
        {
            failed = preset.Id == "failed";
            if (failed)
            {
                Assert.True(board!.Browser.IsApplying);
                board.SelectSceneAt(1);
                if (throws) throw new InvalidOperationException("failed before changing the chain");
                return;
            }
            new DcRigApplier(chain).Apply(preset.Rig);
        }, resultFactory: () => new DcRigApplyResult(0, 0, 0, 0, 0, NoOp: false, FailedOps: failed ? 1 : 0));
        using var owned = board = TestRigBuilders.Pedalboard(chain, presets, FakeLocalization.English());
        board.AddGear(chain.Catalog[0]);

        board.Browser.ApplyPreset(Preset("failed"));
        Assert.Equal(0, board.ActiveSceneIndex);
        var next = new DspTonePreset("next", "next", DspToneCategory.Clean, string.Empty,
            new DcRigPreset([new("amp", new Dictionary<string, float>())]), DspToneOrigin.User);
        board.Browser.ApplyPreset(next);

        Assert.Equal(0, board.ActiveSceneIndex);
        Assert.Null(chain.Scenes);
        Assert.Equal("next", board.Browser.ActivePreset!.Id);
    }

    [Fact]
    public void SuccessfulBrowserApply_DoesNotSeedCompareWithTheFailedChainsOldTrim()
    {
        var chain = new FakeDspChainService([
            new DspModuleType("gain", "gain", [new("gainDb", "Gain", -24, 24, 0)])
        ]);
        var failed = false;
        var presets = new RecordingPresetService(preset =>
        {
            failed = preset.Id == "failed";
            if (!failed) new DcRigApplier(chain).Apply(preset.Rig);
        }, resultFactory: () => new DcRigApplyResult(0, 0, 0, 0, 0, NoOp: false, FailedOps: failed ? 1 : 0));
        using var board = TestRigBuilders.Pedalboard(chain, presets, FakeLocalization.English());
        board.AddGear(chain.Catalog[0]);
        Assert.True(board.CompareLevel.SetLiveTrim(6));

        board.Browser.ApplyPreset(Preset("failed"));
        Assert.True(board.CompareLevel.HasTrim);
        var next = new DspTonePreset("next", "next", DspToneCategory.Clean, string.Empty,
            new DcRigPreset([new("gain", new Dictionary<string, float> { ["gainDb"] = 2 })]), DspToneOrigin.User);
        board.Browser.ApplyPreset(next);
        board.CompareLevel.IsMatched = false;
        board.SelectCompareSlotCommand.Execute("B");

        Assert.False(board.CompareLevel.HasTrim);
        Assert.Equal(2f, chain.GetNodes().Single().Values["gainDb"], 3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedBrowserApply_PreservesTheBypassPatternWhenMasterBypassIsReleased(bool throws)
    {
        var chain = new FakeDspChainService([
            new DspModuleType("amp", "amp", []), new DspModuleType("cab", "cab", [])
        ]);
        var presets = new RecordingPresetService(_ =>
        {
            if (throws) throw new InvalidOperationException("failed before changing the chain");
        }, resultFactory: () => new DcRigApplyResult(0, 0, 0, 0, 0, NoOp: false, FailedOps: 1));
        using var board = TestRigBuilders.Pedalboard(chain, presets, FakeLocalization.English());
        board.AddGear(chain.Catalog[0]);
        board.AddGear(chain.Catalog[1]);
        board.Nodes[1].Bypassed = true;
        var intended = chain.GetNodes().Select(node => node.Bypassed).ToArray();
        var ids = chain.GetNodes().Select(node => node.NodeId).ToArray();
        board.ToggleMasterBypassCommand.Execute(null);

        board.Browser.ApplyPreset(Preset("bad"));

        Assert.True(board.IsMasterBypassed);
        Assert.All(chain.GetNodes(), node => Assert.True(node.Bypassed));
        Assert.Equal(ids, chain.GetNodes().Select(node => node.NodeId));
        board.ToggleMasterBypassCommand.Execute(null);
        Assert.Equal(intended, chain.GetNodes().Select(node => node.Bypassed));
    }

    [Fact]
    public void AFailedApplyAfterAHeldSuccessPublishesTheFinalChainSettlement()
    {
        var chain = new FakeDspChainService([
            new DspModuleType("amp", "amp", []), new DspModuleType("cab", "cab", []),
            new DspModuleType("reverb", "reverb", [])
        ]);
        var good = new DspTonePreset("good", "good", DspToneCategory.Clean, string.Empty, new DcRigPreset([
            new("amp", new Dictionary<string, float>(), false), new("reverb", new Dictionary<string, float>(), false)
        ]), DspToneOrigin.User);
        var presets = new RecordingPresetService(preset =>
        {
            if (preset.Id == "bad") throw new InvalidOperationException("failed apply");
            new DcRigApplier(chain).Apply(preset.Rig);
        });
        using var board = TestRigBuilders.Pedalboard(chain, presets, FakeLocalization.English());
        board.AddGear(chain.Catalog[0]);
        board.AddGear(chain.Catalog[1]);
        board.ToggleMasterBypassCommand.Execute(null);
        var workers = new Queue<Action>();
        var posted = new List<Action>();
        var settled = new List<DcRigApplyResult>();
        var queue = new ToneApplyQueue(presets, applied: (_, result) =>
        {
            settled.Add(result);
            board.Sync();
        }, runOnWorker: workers.Enqueue, postBack: posted.Add);

        queue.ApplyToPlayer(0, good);
        workers.Dequeue()();
        queue.ApplyToPlayer(0, Preset("bad"));
        workers.Dequeue()();
        foreach (var completion in posted) completion();

        Assert.True(Assert.Single(settled).FailedOps > 0);
        Assert.All(chain.GetNodes(), node => Assert.True(node.Bypassed));
        Assert.Equal(chain.GetNodes().Select(node => node.ModuleTypeId), board.Nodes.Select(node => node.ModuleTypeId));
    }

    [Fact]
    public async Task DelayedCompletions_PreserveTheLatestPresetsAuthoredBypassPattern()
    {
        var chain = new FakeDspChainService([
            new DspModuleType("amp", "amp", []), new DspModuleType("cab", "cab", [])
        ]);
        var first = new DspTonePreset("a", "a", DspToneCategory.Clean, string.Empty, new DcRigPreset([
            new("amp", new Dictionary<string, float>(), false), new("cab", new Dictionary<string, float>(), true)
        ]), DspToneOrigin.User);
        var last = new DspTonePreset("b", "b", DspToneCategory.Clean, string.Empty, new DcRigPreset([
            new("amp", new Dictionary<string, float>(), true), new("cab", new Dictionary<string, float>(), false)
        ]), DspToneOrigin.User);
        PedalboardViewModel? board = null;
        var presets = new RecordingPresetService(preset =>
        {
            new DcRigApplier(chain).Apply(preset.Rig);
            if (preset.Id == "a") board!.Browser.ApplyPreset(last);
        });
        var dispatcher = new CompletionDispatcher();
        using var owned = board = TestRigBuilders.Pedalboard(chain, presets, FakeLocalization.English(), dispatcher: dispatcher);
        board.AddGear(chain.Catalog[0]);
        board.AddGear(chain.Catalog[1]);
        board.ToggleMasterBypassCommand.Execute(null);

        board.Browser.ApplyPreset(first);
        var firstCompletion = await dispatcher.Completions.Reader.ReadAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var lastCompletion = await dispatcher.Completions.Reader.ReadAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "a", "b" }, presets.Completed);
        firstCompletion();
        lastCompletion();
        Assert.All(chain.GetNodes(), node => Assert.True(node.Bypassed));
        board.ToggleMasterBypassCommand.Execute(null);

        Assert.Equal(new[] { true, false }, chain.GetNodes().Select(node => node.Bypassed));
    }

    private sealed class CompletionDispatcher : IUiDispatcher
    {
        public Channel<Action> Completions { get; } = Channel.CreateUnbounded<Action>();
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => Completions.Writer.TryWrite(action);
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    [Fact]
    public void AGestureReturnsBeforeTheApplyRuns_AndTheApplyIsOffTheGesturesThread()
    {
        var release = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        var svc = new RecordingPresetService(p =>
        {
            entered.Set();
            Assert.True(SpinUntil(() => release.IsSet),
                "the apply was never released — the worker is starved, not wrong");
        });

        var gestureThread = Environment.CurrentManagedThreadId;
        var q = new ToneApplyQueue(svc, runOnWorker: action => Task.Run(action));

        q.ApplyToPlayer(0, Preset("crunch"));

        // The whole point: the gesture is already back on its own thread while
        // the native crossfade is still sleeping.
        Assert.True(SpinUntil(() => entered.IsSet), "the worker never started");
        Assert.Empty(svc.Completed);

        release.Set();
        Assert.True(SpinUntil(() => svc.Completed.Count == 1), "the apply never completed");
        Assert.NotEqual(gestureThread, svc.Threads.Single());
    }

    /// <summary>
    /// One rebuild per gesture BURST: skimming the card grid must cost one apply
    /// of the tone you stop on, not one per card. Two appliers interleaved over
    /// one chain is also how a blend of two tones gets counted as `FailedOps`.
    /// </summary>
    [Fact]
    public void ABurstOfGesturesCollapsesToOneApplyOfTheLastPreset()
    {
        var release = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        var svc = new RecordingPresetService(_ =>
        {
            entered.Set();
            SpinUntil(() => release.IsSet);
        });

        var q = new ToneApplyQueue(svc, runOnWorker: action => Task.Run(action));

        q.ApplyToPlayer(0, Preset("first"));
        Assert.True(SpinUntil(() => entered.IsSet), "the worker never started");

        // Three more clicks while the first apply is still inside the crossfade.
        q.ApplyToPlayer(0, Preset("second"));
        q.ApplyToPlayer(0, Preset("third"));
        q.ApplyToPlayer(0, Preset("fourth"));

        // Still exactly ONE worker for this seat — never four appliers racing
        // over one chain from four independent GetNodes() snapshots.
        Assert.Equal(1, q.RunningSeats);

        release.Set();
        Assert.True(SpinUntil(() => q.RunningSeats == 0), "the pump never drained");

        Assert.Equal(["first", "fourth"], svc.Completed);
    }

    /// <summary>Seats are independent: player 2's tone must not wait behind
    /// player 1's crossfade.</summary>
    [Fact]
    public void AQueuedPresetFromAnotherProfileIsDiscardedBeforeApply()
    {
        var workers = new Queue<Action>();
        var completions = 0;
        var service = new RecordingPresetService(_ => { });
        var queue = new ToneApplyQueue(service, (_, _) => completions++, runOnWorker: workers.Enqueue);
        queue.ApplyToPlayer(0, Preset("profile-a"));

        service.ProfileGeneration++;
        workers.Dequeue()();

        Assert.Empty(service.Completed);
        Assert.Equal(0, completions);
        Assert.False(queue.IsBusy(0));
        queue.ApplyToPlayer(0, Preset("profile-b"));
        workers.Dequeue()();
        Assert.Equal(["profile-b"], service.Completed);
    }

    [Fact]
    public void SwitchingAwayAndBackStillDiscardsOldWork_AndNewWorkReplacesIt()
    {
        var workers = new Queue<Action>();
        var service = new RecordingPresetService(_ => { });
        var queue = new ToneApplyQueue(service, runOnWorker: workers.Enqueue);
        queue.ApplyToPlayer(0, Preset("old-a"));
        service.ProfileGeneration += 2;
        workers.Dequeue()();
        Assert.Empty(service.Completed);

        queue.ApplyToPlayer(0, Preset("a"));
        service.ProfileGeneration++;
        queue.ApplyToPlayer(0, Preset("b"));
        workers.Dequeue()();
        Assert.Equal(["b"], service.Completed);
    }

    [Fact]
    public void ACompletionPostedByThePreviousProfileIsDiscarded()
    {
        var posted = new Queue<Action>();
        var completions = 0;
        var service = new RecordingPresetService(_ => { });
        var queue = new ToneApplyQueue(service, (_, _) => completions++, postBack: posted.Enqueue);
        queue.ApplyToPlayer(0, Preset("a"));
        service.ProfileGeneration++;
        posted.Dequeue()();
        Assert.Equal(0, completions);
    }

    [Fact]
    public void SeatsQueueIndependently()
    {
        var svc = new RecordingPresetService(_ => { });
        var q = new ToneApplyQueue(svc);

        q.ApplyToPlayer(0, Preset("p1"));
        q.ApplyToPlayer(1, Preset("p2"));

        Assert.Equal(["p1", "p2"], svc.Completed);
        Assert.Equal([0, 1], svc.Players);
    }

    /// <summary>The completion goes back through the caller's post, once per
    /// apply — that is the board re-sync, and it must not run on the worker.</summary>
    [Fact]
    public void TheCompletionIsPostedBackAndNotRunOnTheWorker()
    {
        var svc = new RecordingPresetService(_ => { });
        var posted = new List<Action>();
        var applied = 0;

        var q = new ToneApplyQueue(svc, applied: (_, _) => applied++,
            postBack: posted.Add);

        q.ApplyToPlayer(0, Preset("a"));

        Assert.Single(posted);
        Assert.Equal(0, applied);      // not invoked until the post is pumped
        posted[0]();
        Assert.Equal(1, applied);
    }

    /// <summary>A preset that throws must not wedge the seat's queue forever.</summary>
    [Fact]
    public void AnApplyThatThrowsLeavesTheSeatUsable()
    {
        var svc = new RecordingPresetService(p =>
        {
            if (p.Id == "bad") throw new InvalidOperationException("boom");
        });
        var q = new ToneApplyQueue(svc);

        q.ApplyToPlayer(0, Preset("bad"));
        q.ApplyToPlayer(0, Preset("good"));

        Assert.Equal(0, q.RunningSeats);
        Assert.Equal(["good"], svc.Completed);
    }

    /// <summary>
    /// The wiring guard. The queue only helps if the PRODUCT hands it a real
    /// worker — an inline scheduler is exactly the pre-fix behaviour and would
    /// pass every test above. This is the "registered but never driven" shape,
    /// so it is linted at the one call site that matters.
    /// </summary>
    [Fact]
    public void ThePedalboardHandsTheBrowserARealWorkerAndTheDispatcherToComeBackOn()
    {
        var src = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            Path.Combine("DotCharter.Tone.UI", "ViewModels", "PlayMode", "PedalboardViewModel.cs")));

        var ctor = src.IndexOf("new PresetBrowserViewModel(", StringComparison.Ordinal);
        Assert.True(ctor >= 0, "PedalboardViewModel must still build the preset browser.");
        var end = src.IndexOf(");", ctor, StringComparison.Ordinal);
        var call = src[ctor..end];

        Assert.Contains("applyWorker:", call, StringComparison.Ordinal);
        Assert.Contains("Task.Run", call, StringComparison.Ordinal);
        Assert.Contains("applyPostBack:", call, StringComparison.Ordinal);
        Assert.Contains("PostNormal", call, StringComparison.Ordinal);

        // …and the dispatcher the worker is keyed on really is supplied by the
        // composition root. Without this the wiring above degrades silently to
        // the inline (pre-fix) path for the whole product.
        var di = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            Path.Combine("DotCharter.UI", "App.ViewModelServices.cs"))));
        var reg = di.IndexOf("new Tone.UI.ViewModels.PlayMode.PedalboardViewModel(", StringComparison.Ordinal);
        Assert.True(reg >= 0, "the composition root must still build the pedalboard VM");
        Assert.Contains("IUiDispatcher", di[reg..di.IndexOf("));", reg, StringComparison.Ordinal)],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// End to end through the real <c>PedalboardViewModel</c>: with a UI
    /// dispatcher present — the product shape — a card click returns while the
    /// apply is still inside the crossfade, and the board re-sync comes back
    /// through the dispatcher rather than on the worker.
    /// </summary>
    [Fact]
    public void ACardClickOnTheRealBoardReturnsWhileTheApplyIsStillRunning()
    {
        var release = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        var presets = new RecordingPresetService(_ =>
        {
            entered.Set();
            SpinUntil(() => release.IsSet);
        });

        var chain = new FakeDspChainService([new DspModuleType("amp", "amp", [])]);
        var dispatcher = new DotCharter.Tests.Infrastructure.GatedInlineDispatcher();
        var board = TestRigBuilders.Pedalboard(chain, presets, FakeLocalization.English(), dispatcher: dispatcher);

        var clickThread = Environment.CurrentManagedThreadId;
        board.Browser.ApplyPreset(Preset("crunch"));

        Assert.True(SpinUntil(() => entered.IsSet), "the apply never reached the worker");
        Assert.Empty(presets.Completed);   // the click is already back

        release.Set();
        Assert.True(SpinUntil(() => presets.Completed.Count == 1), "the apply never completed");
        Assert.NotEqual(clickThread, presets.Threads.Single());
    }

    /// <summary>The browser must not apply straight onto the service any more —
    /// that is the call this whole change removes.</summary>
    [Fact]
    public void TheBrowsersApplyPathGoesThroughTheQueue()
    {
        var src = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            Path.Combine("DotCharter.Tone.UI", "ViewModels", "PlayMode", "PresetBrowserViewModel.cs")));

        Assert.DoesNotContain("_presets.ApplyToPlayer(", src, StringComparison.Ordinal);
        Assert.Contains("_applyQueue.ApplyToPlayer(", src, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedSettlementFromThePreviousProfileIsDiscarded(bool throws)
    {
        var posted = new Queue<Action>();
        var settled = new List<DcRigApplyResult>();
        var service = new RecordingPresetService(_ =>
        {
            if (throws) throw new InvalidOperationException("partial apply failed");
        }, resultFactory: () => new DcRigApplyResult(0, 0, 0, 0, 0, NoOp: false, FailedOps: 1));
        var queue = new ToneApplyQueue(service, (_, result) => settled.Add(result), postBack: posted.Enqueue);
        queue.ApplyToPlayer(0, Preset("profile-a"));
        Assert.Single(posted);

        service.ProfileGeneration++;
        posted.Dequeue()();

        Assert.Empty(settled);
    }

    [Fact]
    public void ObsoleteQueuedWorkDoesNotApply_AndTheCurrentProfilesFailureStillSettles()
    {
        var workers = new Queue<Action>();
        var posted = new Queue<Action>();
        var attempts = new List<string>();
        var settled = new List<DcRigApplyResult>();
        var service = new RecordingPresetService(preset =>
        {
            attempts.Add(preset.Id);
            throw new InvalidOperationException("partial apply failed");
        });
        var queue = new ToneApplyQueue(service, (_, result) => settled.Add(result),
            runOnWorker: workers.Enqueue, postBack: posted.Enqueue);
        queue.ApplyToPlayer(0, Preset("profile-a"));
        service.ProfileGeneration++;
        workers.Dequeue()();
        Assert.Empty(attempts);
        Assert.Empty(posted);

        queue.ApplyToPlayer(0, Preset("profile-b"));
        workers.Dequeue()();
        posted.Dequeue()();

        Assert.Equal(["profile-b"], attempts);
        Assert.True(Assert.Single(settled).FailedOps > 0);
        Assert.False(queue.IsBusy(0));
    }

    // ----- helpers -----------------------------------------------------------

    private static DspTonePreset Preset(string id) =>
        new(id, id, DspToneCategory.Clean, string.Empty, new DcRigPreset([]), DspToneOrigin.User);

    private static bool SpinUntil(Func<bool> condition)
    {
        // A 60 s DEADLOCK DETECTOR, never a budget: a green run never reaches it.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(2);
        }

        return condition();
    }

    private sealed class RecordingPresetService(Action<DspTonePreset> onApply,
        Func<DcRigApplyResult>? resultFactory = null) : IDspTonePresetService
    {
        public long ProfileGeneration { get; set; }
        private readonly object _gate = new();
        private readonly List<string> _completed = [];
        private readonly List<int> _players = [];
        private readonly List<int> _threads = [];

        public IReadOnlyList<string> Completed { get { lock (_gate) return _completed.ToList(); } }
        public IReadOnlyList<int> Players { get { lock (_gate) return _players.ToList(); } }
        public IReadOnlyList<int> Threads { get { lock (_gate) return _threads.ToList(); } }

        public IReadOnlyList<DspTonePreset> Presets => [];
        public IReadOnlyList<DspTonePreset> UserPresets => [];
        public IReadOnlyList<DspTonePreset> AllPresets => [];

        public DspTonePreset? FindById(string id) => null;

        public DcRigApplyResult Apply(DspTonePreset preset) => ApplyToPlayer(0, preset);

        public DcRigApplyResult? ApplyById(string id) => null;

        public DcRigApplyResult ApplyToPlayer(int player, DspTonePreset preset)
        {
            onApply(preset);
            lock (_gate)
            {
                _completed.Add(preset.Id);
                _players.Add(player);
                _threads.Add(Environment.CurrentManagedThreadId);
            }

            return resultFactory?.Invoke() ?? new DcRigApplyResult(0, 0, 0, 0, 0, NoOp: false);
        }

        public DcRigApplyResult? ApplyToPlayerById(int player, string id) => null;

        public DspTonePreset SaveUser(string name, DspToneCategory category, string description,
            DcRigPreset rig, string? overwriteId = null) => Preset(name);

        public bool DeleteUser(string id) => false;
    }
}
