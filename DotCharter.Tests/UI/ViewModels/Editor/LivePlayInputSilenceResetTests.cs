using System.Reflection;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The dead-input warning is PER RUN, and it used to outlive one.
///     <para>
///         <c>IsInputSilent</c> ("another app holds your input") and the counters
///         behind it — <c>_silentSamples</c>, <c>_sawAnySignal</c> and the
///         worker's own <c>_workerInputSilent</c> shadow — were reset by nothing.
///         <c>TearDownAnalysis</c> cleared its sibling <c>IsInputStarved</c> and
///         left this one up, so the banner could stay on screen after the run
///         that raised it had ended and with nothing reading the input at all;
///         and because the counter survived, the NEXT run tripped the ~4 s
///         warning a fraction of a second in, on the previous run's total.
///     </para>
///     <para>
///         The shadow matters as much as the banner: a false shadow under a true
///         banner can never flip the banner back down, because the worker only
///         publishes CHANGES.
///     </para>
/// </summary>
public sealed class LivePlayInputSilenceResetTests
{
    /// <summary>~12 s of exact zeros — three times the warning's ~4 s
    /// threshold, so a surviving counter is unmistakable.</summary>
    private const int LastRunsSilence = 12 * 48_000;

    [Fact]
    public void StartingARun_ResetsTheInputSilenceWatchAndItsBanner()
    {
        var vm = NewViewModel();
        LoadChart(vm);
        // An analyzer already exists, so EnsureAnalyzer is a no-op and no
        // detection worker is started: this test is about StartCore's resets,
        // not about pumping audio on a thread.
        SetField(vm, "_analyzer", new GuitarInputAnalyzer(48_000));
        MarkPreviousRunSilent(vm);

        Assert.True((bool)Invoke(vm, "StartCore")!,
            "the session refused to start — the resets under test never ran");

        AssertWatchIsClean(vm);
    }

    [Fact]
    public void TearingDownAnalysis_ClearsBothInputBanners()
    {
        var vm = NewViewModel();
        MarkPreviousRunSilent(vm);
        vm.IsInputStarved = true;

        Invoke(vm, "TearDownAnalysis");

        AssertWatchIsClean(vm);
        Assert.False(vm.IsInputStarved); // the half that already worked
    }

    // ---- fixture -------------------------------------------------------------

    private static void MarkPreviousRunSilent(LivePlayViewModel vm)
    {
        SetField(vm, "_silentSamples", LastRunsSilence);
        SetField(vm, "_sawAnySignal", true);
        SetField(vm, "_workerInputSilent", true);
        vm.IsInputSilent = true;
    }

    private static void AssertWatchIsClean(LivePlayViewModel vm)
    {
        Assert.False(vm.IsInputSilent,
            "the dead-input banner outlived the run that raised it");
        Assert.Equal(0, GetField<int>(vm, "_silentSamples"));
        Assert.False(GetField<bool>(vm, "_sawAnySignal"));
        Assert.False(GetField<bool>(vm, "_workerInputSilent"),
            "the worker's shadow must be reset with the banner — a false shadow under a true banner can never " +
            "flip it back down, because only CHANGES are published");
    }

    private static LivePlayViewModel NewViewModel()
        => LivePlayTestFactory.Create(new FakeAudioPlaybackService { InputSampleRate = 48_000 },
            new ProjectState(),
            new FakeAppLogger(),
            new FakeConfigurationService(),
            new InMemoryPracticeStore());

    /// <summary>A one-beat chart with one empty level — enough for
    /// <c>StartCore</c> to reach its per-run resets.</summary>
    private static void LoadChart(LivePlayViewModel vm)
    {
        var state = GetField<ProjectState>(vm, "_projectState");
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel());
        state.Beats.Add(new BeatMarker { Time = 0, IsMeasure = true });
        state.ActiveArrangement = arrangement; // also sets ActiveLevel
    }

    // The silence watch is worker-confined by design, so the test reaches it the
    // way the worker does rather than widening the type's surface for a test.

    private static FieldInfo Field(string name)
    {
        var field = typeof(LivePlayViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field != null, $"LivePlayViewModel has no field '{name}' — this test was renamed out of its job");
        return field!;
    }

    private static T GetField<T>(LivePlayViewModel vm, string name) => (T)Field(name).GetValue(vm)!;

    private static void SetField(LivePlayViewModel vm, string name, object? value) => Field(name).SetValue(vm, value);

    private static object? Invoke(LivePlayViewModel vm, string name, params object?[] args)
    {
        var method = typeof(LivePlayViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(method != null, $"LivePlayViewModel has no method '{name}' — this test was renamed out of its job");
        return method!.Invoke(vm, args);
    }

    /// <summary>Never reached by these tests (no run is stopped); it exists
    /// because the view model takes one.</summary>
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

        public int RekeySong(string fromSongKey, string toSongKey) => 0;
    }
}
