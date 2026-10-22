using System.Reflection;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #329 in the view model: a CQT hit is JUDGED once, when the CQT confirms it
///     (<c>OnJudged</c>: "Hit", streak, rock and crowd meters, a 3D marker), and GRADED later
///     when its attack onset is reported (<c>OnGraded</c>). The grade is not a second judgment:
///     it feeds only the timing consumers — the timing collector the latency calibrator reads,
///     the tier flash and the EARLY/LATE cue. Fed to the judgment funnel it would count the hit
///     twice on every meter.
///     <para>
///         Reaches the worker-confined handlers by reflection, the way
///         <c>LivePlayRunFailRoutingTests</c> does, rather than widening the type for a test.
///     </para>
/// </summary>
public sealed class LivePlayGradeRoutingTests
{
    [Fact]
    public void AGrade_FeedsTheTimingCues_AndNeverCountsTheHitAgain()
    {
        var vm = ArmedRun();
        var sound = new ExpectedSound { TimeMs = 1000, MidiNotes = [43], StringIndex = 0, Fret = 3 };
        var credit = new LivePlayEvent(LivePlayVerdict.Hit, sound, 1100, 0.9f);
        var grade = new LivePlayGrade(sound, LivePlayVerdict.Hit, 940, LiveTimingTier.Great, LiveTimingDirection.Early);
        var meter = GetField<RockMeter>(vm.Meters, "_rockMeter");
        var timing = GetField<LiveTimingCollector>(vm, "_timing");

        var judgedUi = NewTickUiState();
        Invoke(vm, "OnJudged", credit, judgedUi);
        var energyAfterCredit = meter.Energy(1200);

        Assert.Equal(0, timing.Count);                                    // an untimed credit times nothing
        Assert.True(GetField<bool>(judgedUi, "HasVerdict"));

        var gradedUi = NewTickUiState();
        Invoke(vm, "OnGraded", grade, gradedUi);

        Assert.Equal(1, timing.Count);                                    // the calibrator's evidence
        Assert.Equal(energyAfterCredit, meter.Energy(1200));              // the meter heard ONE hit
        Assert.False(GetField<bool>(gradedUi, "HasVerdict"));             // "Hit" is not shown again
        Assert.True(GetField<bool>(gradedUi, "HasTiming"));
        Assert.True(GetField<bool>(gradedUi, "TimingEarly"));
        Assert.False(string.IsNullOrEmpty(GetField<string>(gradedUi, "TimingLabel")));

        var feedback = vm.FeedbackEvents.ToArray();
        Assert.Equal(2, feedback.Length);
        Assert.False(feedback[0].IsGrade);
        Assert.Equal(LiveTimingTier.None, feedback[0].Tier);
        Assert.True(feedback[1].IsGrade);                                 // the 3D driver adds no marker for it
        Assert.Equal(LiveTimingTier.Great, feedback[1].Tier);
        Assert.Equal(LiveTimingDirection.Early, feedback[1].Direction);
        Assert.Equal(940, feedback[1].JudgedAtMs);                        // stamped with the attack
    }

    /// <summary>
    ///     #643: a PROVISIONAL grade shows the cue (tier flash, EARLY/LATE) but stays out of the
    ///     timing collector — the latency calibrator reads finalized attacks only. The finalized
    ///     grade then feeds the collector once; it shows the cue again only when it CORRECTS it.
    /// </summary>
    [Theory]
    [InlineData(LivePlayGradeStage.Confirmed, false)]
    [InlineData(LivePlayGradeStage.Corrected, true)]
    public void AProvisionalGrade_ShowsTheCueOnly_AndTheFinalizedOneCollects(LivePlayGradeStage finalStage,
        bool shownAgain)
    {
        var vm = ArmedRun();
        var sound = new ExpectedSound { TimeMs = 1000, MidiNotes = [43], StringIndex = 0, Fret = 3 };
        var timing = GetField<LiveTimingCollector>(vm, "_timing");
        var provisional = new LivePlayGrade(sound, LivePlayVerdict.Hit, 1030, LiveTimingTier.Perfect,
            LiveTimingDirection.Late)
        {
            Stage = LivePlayGradeStage.Provisional
        };
        var finalized = new LivePlayGrade(sound, LivePlayVerdict.Hit, 1080, LiveTimingTier.Great,
            LiveTimingDirection.Late)
        {
            Stage = finalStage
        };

        var cueUi = NewTickUiState();
        Invoke(vm, "OnGraded", provisional, cueUi);

        Assert.Equal(0, timing.Count);                                    // never calibrate from a provisional onset
        Assert.True(GetField<bool>(cueUi, "HasTiming"));                  // ...but the cue is shown now
        Assert.True(GetField<bool>(cueUi, "TimingLate"));
        var cue = Assert.Single(vm.FeedbackEvents.ToArray());
        Assert.True(cue.IsGrade);
        Assert.Equal(LiveTimingTier.Perfect, cue.Tier);

        var finalUi = NewTickUiState();
        Invoke(vm, "OnGraded", finalized, finalUi);

        Assert.Equal(1, timing.Count);                                    // the finalized attack is collected once
        Assert.Equal(shownAgain, GetField<bool>(finalUi, "HasTiming"));
        Assert.Equal(shownAgain ? 2 : 1, vm.FeedbackEvents.Count);
    }

    // ---- fixture -------------------------------------------------------------

    private static LivePlayViewModel ArmedRun()
    {
        var vm = LivePlayTestFactory.Create(
            new FakeAudioPlaybackService { InputSampleRate = 48_000 },
            new ProjectState(),
            new FakeAppLogger(),
            new FakeConfigurationService(),
            new UnusedPracticeStore());

        var state = GetField<ProjectState>(vm, "_projectState");
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel());
        state.Beats.Add(new BeatMarker { Time = 0, IsMeasure = true });
        state.ActiveArrangement = arrangement;
        GetField<LivePlaySessionService>(vm, "_session")
            .Start(arrangement, state.ActiveLevel, state.Beats, new LivePlaySessionOptions());

        vm.ArmRockMeter();
        Assert.True(vm.Meters.IsRockMeterActive, "the meter did not arm — the meter assertion would be vacuous");
        return vm;
    }

    private static object NewTickUiState()
    {
        var type = typeof(LivePlayViewModel).GetNestedType("TickUiState", BindingFlags.NonPublic);
        Assert.True(type != null, "LivePlayViewModel.TickUiState was renamed — update this test");
        return Activator.CreateInstance(type!, nonPublic: true)!;
    }

    private static void Invoke(LivePlayViewModel vm, string name, params object[] args)
    {
        var method = typeof(LivePlayViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(method != null, $"LivePlayViewModel has no method '{name}' — this test was renamed out of its job");
        method!.Invoke(vm, args);
    }

    private static T GetField<T>(object owner, string name)
    {
        var field = owner.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(field != null, $"{owner.GetType().Name} has no field '{name}' — this test was renamed out of its job");
        return (T)field!.GetValue(owner)!;
    }

    /// <summary>Never reached (no run is stopped); the view model takes one.</summary>
    private sealed class UnusedPracticeStore : IPracticeProfileService
    {
        public PracticeArrangementProfile GetProfile(string songKey, string arrangementName) => new();

        public PracticeArrangementProfile Record(string songKey, string arrangementName,
            LivePlaySessionResult result, string? displayName = null,
            IReadOnlyList<PhraseMasteryOutcome>? phraseOutcomes = null,
            IReadOnlyList<SectionResult>? sections = null,
            IReadOnlyList<TechniqueResult>? techniques = null,
            double? chartDifficulty = null, bool failed = false) => new();

        public IReadOnlyDictionary<string, PracticeArrangementProfile> GetAll() =>
            new Dictionary<string, PracticeArrangementProfile>();

        public bool AttachTakeFile(string songKey, string arrangementName, string takeFileName) => false;
        public bool AttachReplayFile(string songKey, string arrangementName, string replayFileName) => false;
        public void InvalidateCache() { }

        public bool IsStoreAuthoritative => true;

        public int RekeySong(string fromSongKey, string toSongKey) => 0;
    }
}
