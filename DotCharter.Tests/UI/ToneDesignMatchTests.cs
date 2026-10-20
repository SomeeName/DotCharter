using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     "Match from audio" in the rig drawer's tone panel, against fake match
///     services. No Avalonia and no audio device: every assertion is about what
///     the view model DRIVES — the span it asks for, the prior it sends, the
///     candidate it publishes, and the drawer seams the selected candidate
///     reaches.
///
///     <para>
///         The seam assertions carry the weight. A matched rig that reached the
///         chain by its own private path would pass a "the candidate list is
///         populated" test and still be the second door onto the rig that the
///         tone drawer's whole design exists to avoid. The point of this flow is
///         that a matched tone lands in the SAME <c>Result</c> a designed one
///         does and is applied, saved and discarded by the same four commands.
///     </para>
/// </summary>
public sealed class ToneDesignMatchTests
{
    // ---- fakes ----------------------------------------------------------------

    private sealed class FakeMatcher : IToneMatcher
    {
        public List<ToneMatchRequest> Requests { get; } = [];
        public TaskCompletionSource<bool>? Block;
        public Exception? Throw;
        public ToneMatchResult? Answer;

        public async Task<ToneMatchResult> MatchAsync(
            ToneMatchRequest request, IProgress<string>? status, CancellationToken ct)
        {
            Requests.Add(request);
            status?.Report("searching");
            if (Block != null) await Block.Task.WaitAsync(ct);
            if (Throw != null) throw Throw;
            return Answer ?? ResultWith(0.2, 0.5);
        }
    }

    private sealed class FakeIsolator : IStemIsolator
    {
        public List<(string Path, TimeSpan From, TimeSpan To)> Calls { get; } = [];
        public TaskCompletionSource<bool>? Block;
        public MonoAudio? Answer;

        public async Task<MonoAudio> IsolateGuitarAsync(
            string songAudioPath, TimeSpan from, TimeSpan to, IProgress<string>? status, CancellationToken ct)
        {
            Calls.Add((songAudioPath, from, to));
            status?.Report("isolating");
            // A real isolator observes the token; one that does not would make
            // the cancel case assert nothing.
            if (Block != null) await Block.Task.WaitAsync(ct);
            return Answer ?? Playing;
        }

        /// <summary>
        ///     Four seconds of deterministic PLAYING.
        ///     <para>
        ///         <b>It was <c>new float[4800]</c> — a tenth of a second of digital
        ///         silence — until 2026-09-16</b>, and every case in this file passed
        ///         against it, which is precisely the W5a failure in the test suite:
        ///         nothing anywhere asked whether the thing being matched had a note
        ///         in it. The panel refuses a silent target now, so a fixture that is
        ///         silence would assert the guard instead of the flow.
        ///     </para>
        /// </summary>
        public static MonoAudio Playing { get; } = BuildPlaying();

        private static MonoAudio BuildPlaying()
        {
            var x = new float[4 * 48000];
            for (var i = 0; i < x.Length; i++)
            {
                var t = i / 48000.0;
                var env = 0.5 + 0.5 * Math.Cos(2 * Math.PI * 2.0 * t);
                x[i] = (float)(0.4 * env * Math.Sin(2 * Math.PI * 110.0 * t));
            }

            return new MonoAudio(x, 48000);
        }
    }

    private sealed class FakeEstimator : IRigEstimator
    {
        public bool IsAvailable { get; init; } = true;
        public int Calls;
        public Exception? Throw;

        public Task<RigEstimate> EstimateAsync(MonoAudio wet, CancellationToken ct)
        {
            Calls++;
            if (Throw != null) throw Throw;
            return Task.FromResult(new RigEstimate(
                Rig("estimated-amp"), new Dictionary<string, float> { ["amp"] = 1f }, 0.7f, "rig-net-1"));
        }
    }

    /// <summary>A fake engine whose loaded file a test can name.
    /// <c>CurrentAudioPath</c> is a DEFAULT interface member, so re-listing the
    /// interface is what makes this property the implementation rather than an
    /// unrelated member the interface never sees.</summary>
    private sealed class MatchAudio : FakeAudioPlaybackService, IAudioPlaybackService
    {
        public string LoadedPath = "";
        public string CurrentAudioPath => LoadedPath;

        /// <summary>Total samples ever captured, as the engine's sync counter
        /// reports it. A test advances this and <c>InputCaptureWindow</c>
        /// together, which is exactly the pair the recorder stitches with.</summary>
        public ulong Captured;

        public bool SyncAvailable = true;

        public override bool TryGetInputCaptureSync(out ulong capturedSamples, out long hostTicks)
        {
            capturedSamples = Captured;
            hostTicks = 0;
            return SyncAvailable;
        }

        /// <summary>Publishes the newest <paramref name="windowSamples" /> of a
        /// stream whose every sample equals its own ABSOLUTE index, so a test can
        /// say which samples the recorder kept simply by reading them.</summary>
        public void Advance(int samples, int windowSamples)
        {
            Captured += (ulong)samples;
            var start = Captured >= (ulong)windowSamples ? Captured - (ulong)windowSamples : 0;
            var window = new float[(int)Math.Min((ulong)windowSamples, Captured)];
            for (var i = 0; i < window.Length; i++) window[i] = start + (ulong)i;
            InputCaptureWindow = window;
        }
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>How many samples the recorder's own buffer holds at the 100 Hz
    /// rate these tests run the engine at — three poll intervals' worth, which is
    /// what the view model sizes it to. The fake publishes exactly this many, so
    /// its window means what the engine's does: the NEWEST n samples.</summary>
    private const int RecorderWindow = 60;

    private static DcRigPreset Rig(params string[] modules) =>
        new(modules.Select(m => new DcRigModule(m, new Dictionary<string, float>())).ToList());

    /// <summary>
    ///     A finished result whose candidates carry a REAL
    ///     <see cref="MatchScore" /> (W5b) — built from synthetic feature vectors
    ///     through the real <see cref="MatchScale" />, not hand-written numbers.
    ///     <para>
    ///         <b>Scoring is the matcher's job now.</b> A fake result whose candidates
    ///         had no score would make every panel case here assert the UNRATED path,
    ///         which is the opposite of what they are about. The distance a caller asks
    ///         for is honoured exactly; the vectors are chosen so the scale produces it.
    ///     </para>
    /// </summary>
    private static ToneMatchResult ResultWith(params double[] distances)
    {
        var target = Flat(0f);
        var reference = Flat(ReferenceOffset);
        var similarity = new ToneSimilarity();
        var referenceDistance = similarity.Distance(target, reference);

        var candidates = distances.Select((d, i) =>
        {
            // The metric is an L2 over per-element differences, so a flat offset of
            // `d / referenceDistance * ReferenceOffset` is a candidate at distance d.
            var candidate = Flat((float)(ReferenceOffset * d / referenceDistance));
            var score = MatchScale.Score(target, candidate, reference, similarity, SelfDistance);
            return new ToneMatchCandidate(Rig($"amp{i}"), score.Distance, $"origin{i}")
            {
                Features = candidate.Vector,
                Score = score
            };
        }).ToList();

        return new ToneMatchResult(candidates, SelfDistance, TimeSpan.FromSeconds(3), ["rendered 400 rigs"])
        {
            TargetFeatures = target.Vector,
            ReferenceFeatures = reference.Vector,
            ReferenceDistance = referenceDistance
        };
    }

    /// <summary>The 0 % anchor's offset. Big enough that the scale has room for
    /// every distance these cases use.</summary>
    private const float ReferenceOffset = 1.0f;

    /// <summary>The reported self-distance. A caveat, not part of the scale.</summary>
    private const double SelfDistance = 0.2;

    /// <summary>A non-degenerate flat feature vector — see
    /// <c>MatchScaleTests.Flat</c> for why the base is not zero.</summary>
    private static ToneFeatures Flat(float offset)
    {
        var x = new float[ToneFeatureLayout.Length];
        for (var i = 0; i < x.Length; i++) x[i] = 0.2f + offset;
        return new ToneFeatures(x, ToneFeatureExtractor.ExtractorId);
    }

    private sealed class Harness
    {
        public readonly FakeMatcher Matcher = new();
        public readonly FakeIsolator Isolator = new();
        public readonly MatchAudio Audio = new();
        public readonly List<DspTonePreset> Applied = [];
        public readonly List<(string Name, DspToneCategory Category, DcRigPreset Rig)> Saved = [];
        public readonly List<DspTonePreset> Snapshots = [];
        public readonly ToneDesignViewModel Vm;

        /// <summary>Every delay the recorder asked for, so the poll loop costs a
        /// test nothing and a test can drive the capture between polls.</summary>
        public Action? OnDelay;

        public Harness(bool withMatcher = true, bool withEstimator = false, FakeEstimator? estimator = null)
        {
            Vm = new ToneDesignViewModel(
                static () => null,
                preset => Applied.Add(preset),
                () =>
                {
                    var snapshot = new DspTonePreset(
                        "live", $"What I was playing #{Snapshots.Count}", DspToneCategory.Clean, "",
                        Rig("amp"), DspToneOrigin.User);
                    Snapshots.Add(snapshot);
                    return snapshot;
                },
                (name, category, _, rig) =>
                {
                    Saved.Add((name, category, rig));
                    return new DspTonePreset("saved-1", name, category, "", rig, DspToneOrigin.User);
                }, FakeLocalization.English(),
                id => id == "amp0" ? "Amp" : id);

            Vm.ConfigureMatch(
                () => withMatcher ? Matcher : null,
                () => Isolator,
                () => withEstimator ? estimator ?? new FakeEstimator() : null,
                () => Audio,
                (_, _) =>
                {
                    OnDelay?.Invoke();
                    return Task.CompletedTask;
                },
                // No bundled clip on disk in a test tree: the reference DI is
                // whatever the test records or seeds.
                static () => null);

            Vm.MatchSourceProvider = () => Source;
            Source = new ToneMatchSource("C:/songs/song.wav", Array.Empty<ToneSpan>(), 0, 0);
        }

        public ToneMatchSource? Source;

        /// <summary>Seeds a reference DI without recording one — the flow needs a
        /// dry guitar and most of these cases are not about where it came
        /// from.</summary>
        public void GiveItAReferenceDi()
        {
            // The engine's window is whatever buffer the recorder asks for —
            // 3 polls' worth — so the fake publishes exactly that many samples and
            // advances by less than one window per poll.
            Audio.InputSampleRate = 100;
            Audio.Advance(1_000, RecorderWindow);
            var polls = 0;
            OnDelay = () =>
            {
                polls++;
                Audio.Advance(40, RecorderWindow);
            };

            Vm.RecordDiCommand.Execute(null);

            Assert.True(Vm.HasReferenceDi, $"the recorder produced no DI after {polls} polls");
            OnDelay = null;
        }
    }

    private static string Loc(string key) => FakeLocalization.English()[key];

    private static string Loc(string key, params object[] args) =>
        FakeLocalization.English().Format(key, args);

    // ---- the flow -------------------------------------------------------------

    [Fact]
    public async Task Match_IsolatesTheCurrentSpan_ThenSearches_ThenPublishesRankedCandidates()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Source = new ToneMatchSource(
            "C:/songs/song.wav",
            [new ToneSpan(0, "Clean", 0, 30_000), new ToneSpan(1, "Lead", 30_000, 90_000)],
            45_000,
            180_000);
        h.Matcher.Answer = ResultWith(0.8, 0.2, 0.4);

        await h.Vm.MatchCommand.ExecuteAsync(null);

        // The span the PLAYHEAD is standing in, not the first one and not the
        // whole song.
        var call = Assert.Single(h.Isolator.Calls);
        Assert.Equal("C:/songs/song.wav", call.Path);
        Assert.Equal(TimeSpan.FromSeconds(30), call.From);
        Assert.Equal(TimeSpan.FromSeconds(90), call.To);

        // …and the isolated guitar is what the matcher was asked about.
        var request = Assert.Single(h.Matcher.Requests);
        Assert.Equal(FakeIsolator.Playing.Samples.Length, request.Target.Samples.Length);

        Assert.Equal(3, h.Vm.MatchCandidates.Count);
        // To 4 places, not exactly: since W5b the fixture's distances come from real
        // feature vectors through the real metric, so they carry one float's worth of
        // rounding. The ORDER and the magnitudes are the claim.
        Assert.Equal([0.2, 0.4, 0.8], h.Vm.MatchCandidates.Select(c => Math.Round(c.Distance, 4)).ToArray());
        Assert.Same(h.Vm.MatchCandidates[0], h.Vm.SelectedMatchCandidate);
        Assert.Equal(string.Empty, h.Vm.MatchError);

        // The selected candidate IS the panel's result — that is the whole
        // integration.
        Assert.True(h.Vm.HasResult);
        Assert.Same(h.Vm.MatchCandidates[0].Tone, h.Vm.Result!.Tone);
    }

    [Fact]
    public async Task TheSelectedCandidate_DrivesHearItThroughTheDrawersOwnApplySeam()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        await h.Vm.MatchCommand.ExecuteAsync(null);

        h.Vm.HearItCommand.Execute(null);

        // The queue seam a preset CARD click uses, with the matched rig — not a
        // private apply, and not the raw DcRigPreset.
        var applied = Assert.Single(h.Applied);
        Assert.Same(h.Vm.MatchCandidates[0].Tone, applied);
        // USER origin: the factory apply path loudness-normalizes, and a matched
        // tone re-levelled on its way to the chain is not the tone that scored.
        Assert.Equal(DspToneOrigin.User, applied.Origin);
        Assert.Single(h.Snapshots);
    }

    [Fact]
    public async Task SelectingASecondCandidate_RepublishesTheResult_AndHearItFollowsIt()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Matcher.Answer = ResultWith(0.2, 0.5);
        await h.Vm.MatchCommand.ExecuteAsync(null);

        h.Vm.SelectedMatchCandidate = h.Vm.MatchCandidates[1];
        h.Vm.HearItCommand.Execute(null);

        Assert.Same(h.Vm.MatchCandidates[1].Tone, Assert.Single(h.Applied));
    }

    [Fact]
    public async Task SavingAMatchedTone_GoesThroughTheDrawersSaveSeam_WithTheMatchedRig()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        await h.Vm.MatchCommand.ExecuteAsync(null);

        h.Vm.SaveToneCommand.Execute(null);

        var saved = Assert.Single(h.Saved);
        Assert.Same(h.Vm.MatchCandidates[0].Tone.Rig, saved.Rig);
    }

    [Fact]
    public async Task Discard_ClearsTheCandidateListToo()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        await h.Vm.MatchCommand.ExecuteAsync(null);
        Assert.NotEmpty(h.Vm.MatchCandidates);

        h.Vm.DiscardCommand.Execute(null);

        // A ranked list left under a discarded result offers a row whose tone the
        // panel has just put back; the next click would silently re-apply it.
        Assert.Empty(h.Vm.MatchCandidates);
        Assert.Null(h.Vm.SelectedMatchCandidate);
        Assert.False(h.Vm.HasResult);
    }

    // ---- the prior ------------------------------------------------------------

    [Fact]
    public async Task AnExistingDesign_IsSentAsThePrior()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        var designed = new ToneDesignResult(
            new DspTonePreset("ai", "Designed", DspToneCategory.Lead, "", Rig("rat", "amp"), DspToneOrigin.User),
            "", 0.8, [], "{}", new AiUsage("m", 1, 1));
        h.Vm.SetResultForTests(designed, "Rat → Amp");

        await h.Vm.MatchCommand.ExecuteAsync(null);

        var prior = Assert.Single(h.Matcher.Requests).Prior;
        Assert.NotNull(prior);
        Assert.Equal("design", prior!.Source);
        Assert.Same(designed.Tone.Rig, prior.Rig);
    }

    [Fact]
    public async Task WithNoDesign_TheEstimatorIsThePrior()
    {
        var estimator = new FakeEstimator();
        var h = new Harness(withEstimator: true, estimator: estimator);
        h.GiveItAReferenceDi();

        await h.Vm.MatchCommand.ExecuteAsync(null);

        var prior = Assert.Single(h.Matcher.Requests).Prior;
        Assert.NotNull(prior);
        Assert.Equal("estimator:rig-net-1", prior!.Source);
        Assert.Equal(1, estimator.Calls);
    }

    [Fact]
    public async Task AnEstimatorThatThrows_DoesNotTakeTheSearchDown()
    {
        var estimator = new FakeEstimator { Throw = new InvalidOperationException("onnx said no") };
        var h = new Harness(withEstimator: true, estimator: estimator);
        h.GiveItAReferenceDi();

        await h.Vm.MatchCommand.ExecuteAsync(null);

        // A prior is an optimisation; the half of the flow that is allowed to be
        // absent must not be able to fail the half that is not.
        Assert.Null(Assert.Single(h.Matcher.Requests).Prior);
        Assert.NotEmpty(h.Vm.MatchCandidates);
        Assert.Equal(string.Empty, h.Vm.MatchError);
    }

    // ---- cancel ---------------------------------------------------------------

    /// <summary>
    ///     The panel's progress bar is driven by the library-build line and by
    ///     nothing else: a build line sets the fraction and shows the bar, any
    ///     other line hides it. (The owner watched "1/4000" with no bar.)
    /// </summary>
    [Fact]
    public void ALibraryBuildLine_DrivesTheProgressBar_AndAnyOtherLineHidesIt()
    {
        var h = new Harness();

        h.Vm.MatchStatus = "Building tone library: 12/96 · ~3 min left";
        Assert.True(h.Vm.HasMatchProgress);
        Assert.Equal(0.125, h.Vm.MatchProgressFraction, 6);

        h.Vm.MatchStatus = "Building tone library: 96/96";
        Assert.True(h.Vm.HasMatchProgress);
        Assert.Equal(1.0, h.Vm.MatchProgressFraction, 6);

        h.Vm.MatchStatus = Loc("ToneMatch_Searching");
        Assert.False(h.Vm.HasMatchProgress);
        Assert.Equal(0.0, h.Vm.MatchProgressFraction, 6);

        h.Vm.MatchStatus = string.Empty;
        Assert.False(h.Vm.HasMatchProgress);
    }

    [Fact]
    public async Task CancelDuringIsolation_ReportsCancelled_AndPublishesNothing()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Isolator.Block = new TaskCompletionSource<bool>();

        var run = h.Vm.MatchCommand.ExecuteAsync(null);
        Assert.True(h.Vm.IsMatching);
        h.Vm.CancelMatchCommand.Execute(null);
        await run;

        Assert.Empty(h.Matcher.Requests);
        Assert.Empty(h.Vm.MatchCandidates);
        // A cancel is a cancel, not a fault: no error banner opens.
        Assert.Equal(Loc("ToneMatch_Cancelled"), h.Vm.MatchStatus);
        Assert.Equal(string.Empty, h.Vm.MatchError);
        Assert.False(h.Vm.IsMatching);
    }

    [Fact]
    public async Task CancelDuringTheSearch_ReportsCancelled()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Matcher.Block = new TaskCompletionSource<bool>();

        var run = h.Vm.MatchCommand.ExecuteAsync(null);
        h.Vm.CancelMatchCommand.Execute(null);
        await run;

        Assert.Empty(h.Vm.MatchCandidates);
        Assert.Equal(Loc("ToneMatch_Cancelled"), h.Vm.MatchStatus);
    }

    // ---- the faults -----------------------------------------------------------

    [Fact]
    public void WithNoMatcherRegistered_TheWholeFeatureIsOffScreen()
    {
        var h = new Harness(withMatcher: false);

        Assert.False(h.Vm.IsMatchAvailable);
        Assert.False(h.Vm.MatchCommand.CanExecute(null));
    }

    [Fact]
    public async Task WithNoSongAudio_ItSaysSo()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Source = null;
        h.Audio.LoadedPath = "";

        await h.Vm.MatchCommand.ExecuteAsync(null);

        Assert.Equal(Loc("ToneMatch_Fault_NoAudio"), h.Vm.MatchError);
        Assert.Empty(h.Isolator.Calls);
    }

    [Fact]
    public async Task WithNoHostProvider_TheTransportsOwnFileIsUsed()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Vm.MatchSourceProvider = null;
        h.Audio.LoadedPath = "C:/psarc/cache/song.wav";

        await h.Vm.MatchCommand.ExecuteAsync(null);

        // No host wiring at all still matches — the chart editor's rig host wires
        // no flow song either, and a dead button there would be worse than the
        // first-30-seconds fallback.
        var call = Assert.Single(h.Isolator.Calls);
        Assert.Equal("C:/psarc/cache/song.wav", call.Path);
        Assert.Equal(TimeSpan.Zero, call.From);
        Assert.Equal(TimeSpan.FromSeconds(30), call.To);
    }

    [Fact]
    public async Task WhenTheIsolatorFindsNoGuitar_ItSaysSo_AndDoesNotSearch()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Isolator.Answer = new MonoAudio([], 48000);

        await h.Vm.MatchCommand.ExecuteAsync(null);

        Assert.Equal(Loc("ToneMatch_Fault_NoStems"), h.Vm.MatchError);
        Assert.Empty(h.Matcher.Requests);
    }

    /// <summary>
    ///     W5a: "no guitar" is not only an EMPTY buffer. A separated stem for a
    ///     stretch of song with no guitar in it is a full-length buffer of digital
    ///     silence — which is not <c>Length == 0</c> and went straight through the
    ///     guard above into a match that reported a confident percentage about an
    ///     all-zero feature vector. Measured on the owner's own project: the first
    ///     ten seconds of <c>stems/guitar.wav</c> are −180 dBFS.
    /// </summary>
    [Fact]
    public async Task AStemThatIsSilence_IsRefusedLikeAMissingOne_AndDoesNotSearch()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Isolator.Answer = new MonoAudio(new float[30 * 48000], 48000);

        await h.Vm.MatchCommand.ExecuteAsync(null);

        Assert.Equal(Loc("ToneMatch_Fault_NoStems"), h.Vm.MatchError);
        Assert.Empty(h.Matcher.Requests);
    }

    /// <summary>The guard against the guard: a window whose playing sits AFTER a
    /// long silent lead-in is matched, not refused — the trim is what makes it
    /// usable, and refusing it would lose a legitimate match.</summary>
    [Fact]
    public async Task AStemWithALongSilentLeadIn_IsStillMatched()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        var lead = new float[15 * 48000];
        var playing = FakeIsolator.Playing.Samples;
        var buffer = new float[lead.Length + playing.Length];
        Array.Copy(playing, 0, buffer, lead.Length, playing.Length);
        h.Isolator.Answer = new MonoAudio(buffer, 48000);

        await h.Vm.MatchCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, h.Vm.MatchError);
        Assert.Single(h.Matcher.Requests);
    }

    [Fact]
    public async Task WithNoReferenceDi_ItSaysWhatIsMissing_AndDoesNotIsolate()
    {
        var h = new Harness();

        await h.Vm.MatchCommand.ExecuteAsync(null);

        Assert.Equal(Loc("ToneMatch_Fault_NoReferenceDi"), h.Vm.MatchError);
        Assert.Empty(h.Isolator.Calls);
    }

    [Fact]
    public async Task AMatcherThatThrows_SurfacesAsAMessage()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Matcher.Throw = new InvalidOperationException("the library is missing");

        await h.Vm.MatchCommand.ExecuteAsync(null);

        // The banner starts with the localised sentence and ENDS with the
        // reason, so "see the log" is no longer the only way to learn what
        // failed (4febb3190).
        Assert.StartsWith(Loc("ToneMatch_Fault_Failed"), h.Vm.MatchError);
        Assert.Contains("the library is missing", h.Vm.MatchError);
        Assert.False(h.Vm.IsMatching);
    }

    [Fact]
    public async Task ASearchThatAnswersWithNothing_SaysSoRatherThanShowingAnEmptyList()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Matcher.Answer = new ToneMatchResult([], 0.2, TimeSpan.Zero, []);

        await h.Vm.MatchCommand.ExecuteAsync(null);

        Assert.Equal(Loc("ToneMatch_Fault_NoCandidates"), h.Vm.MatchError);
        Assert.False(h.Vm.HasMatchCandidates);
    }

    // ---- recording the player's own DI ----------------------------------------

    [Fact]
    public void TheRecorder_KeepsOnlyAudioCapturedAfterTheButtonWasPressed()
    {
        var h = new Harness();
        h.Audio.InputSampleRate = 100;
        // A ring already full of someone else's playing. A windowed reader cannot
        // drain it — it anchors instead, and that is what this asserts.
        h.Audio.Advance(5_000, RecorderWindow);
        h.OnDelay = () => h.Audio.Advance(20, RecorderWindow);

        h.Vm.RecordDiCommand.Execute(null);

        Assert.True(h.Vm.HasReferenceDi);
        var di = CapturedDi(h);
        Assert.Equal(800, di.Samples.Length);  // 8 s at 100 Hz
        // Every sample equals its own absolute stream index, so the first one
        // names the moment the recording began: after the 5,000 already captured.
        Assert.Equal(5_000f, di.Samples[0]);
        Assert.Equal(5_799f, di.Samples[^1]);
        // Contiguous: no duplicated overlap and no skipped samples.
        for (var i = 1; i < di.Samples.Length; i++)
            Assert.Equal(di.Samples[i - 1] + 1, di.Samples[i]);
    }

    [Fact]
    public void ARingThatWrappedBetweenPolls_IsReportedAsAGap_NotSpliced()
    {
        var h = new Harness();
        h.Audio.InputSampleRate = 100;
        h.Audio.Advance(1_000, RecorderWindow);
        // More new audio per poll than the window holds: the samples in between
        // are gone for good.
        h.OnDelay = () => h.Audio.Advance(500, RecorderWindow);

        h.Vm.RecordDiCommand.Execute(null);

        Assert.Equal(Loc("ToneMatch_Fault_CaptureGap"), h.Vm.MatchError);
        Assert.False(h.Vm.HasReferenceDi);
    }

    [Fact]
    public void ACaptureThatStopsAdvancing_EndsOnAPollBudget_RatherThanSpinningForever()
    {
        var h = new Harness();
        h.Audio.InputSampleRate = 100;
        // The engine answers its sync counter but the ring never moves. Every
        // other exit from the poll loop depends on the DEVICE making progress, so
        // without a budget this is an unbounded loop with the cancel button as
        // the only way out of it — which is what the first version of this loop
        // did, and it hung a test run to prove it.
        h.Audio.Advance(1_000, RecorderWindow);
        var polls = 0;
        h.OnDelay = () =>
        {
            polls++;
            Assert.True(polls < 1_000, "the recorder never gave up — the poll budget is gone");
        };

        h.Vm.RecordDiCommand.Execute(null);

        Assert.Equal(Loc("ToneMatch_Fault_CaptureStalled"), h.Vm.MatchError);
        Assert.False(h.Vm.HasReferenceDi);
    }

    [Fact]
    public void WithNoInputRunning_TheRecorderSaysSo()
    {
        var h = new Harness();
        h.Audio.InputSampleRate = 0;

        h.Vm.RecordDiCommand.Execute(null);

        Assert.Equal(Loc("ToneMatch_Fault_NoInput"), h.Vm.MatchError);
        Assert.False(h.Vm.HasReferenceDi);
    }

    [Fact]
    public async Task CancelDuringTheRecording_ReportsCancelled()
    {
        var h = new Harness();
        h.Audio.InputSampleRate = 100;
        h.Audio.Advance(100, RecorderWindow);
        h.OnDelay = () =>
        {
            h.Audio.Advance(20, RecorderWindow);
            h.Vm.CancelMatchCommand.Execute(null);
        };

        await h.Vm.RecordDiCommand.ExecuteAsync(null);

        Assert.False(h.Vm.HasReferenceDi);
        Assert.Equal(Loc("ToneMatch_Cancelled"), h.Vm.MatchStatus);
    }

    private static MonoAudio CapturedDi(Harness h)
    {
        // The DI is private state by design — it is read back through the one
        // thing that consumes it, which is also the thing that would break if it
        // were wrong.
        h.Source = new ToneMatchSource("C:/songs/song.wav", Array.Empty<ToneSpan>(), 0, 0);
        h.Vm.MatchCommand.Execute(null);
        return h.Matcher.Requests[^1].ReferenceDi;
    }

    // ---- the two pure decisions -----------------------------------------------

    /// <summary>
    ///     <b>The replacement for <c>MatchPercent_IsARatioAgainstTheFloor</c></b>, which
    ///     pinned <c>100 × floor / max(distance, floor)</c> — the formula W5b removed
    ///     because it reports 100 % for every candidate whose distance is below the
    ///     target's self-distance, which on real material is most of them. The scale
    ///     itself is pinned by <c>Ai/Search/MatchScaleTests</c> and calibrated against
    ///     real feature vectors by <c>Ai/Search/MatchScoreCalibrationTests</c>; what is
    ///     left for the PANEL is the three states and the breakdown.
    /// </summary>
    [Fact]
    public void ARatedCandidate_ShowsItsPercentage_AndTheBreakdownBehindIt()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Matcher.Answer = ResultWith(0.5);

        h.Vm.MatchCommand.Execute(null);

        var row = Assert.Single(h.Vm.MatchCandidates);
        Assert.True(row.IsRated);
        Assert.NotNull(row.MatchPercent);
        Assert.Equal(Loc("ToneMatch_Percent", row.MatchPercent!.Value), row.MatchText);
        Assert.Equal(string.Empty, row.UnratedText);
        Assert.NotEmpty(row.Breakdown);
        Assert.NotEqual(string.Empty, row.BreakdownText);
        Assert.NotEqual(string.Empty, row.ScaleText);
        // The rationale the saved tone carries names the blocks, not just a number.
        Assert.Contains(row.BreakdownText, row.Tone.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnratedCandidate_SaysSo_AndIsNotShownAsZeroPercent()
    {
        // The third state. A candidate the run could not put a number on must not
        // render as a measured 0 %.
        var h = new Harness();
        h.GiveItAReferenceDi();
        var target = Flat(0f);
        var candidate = Flat(0.5f);
        var score = MatchScale.Score(target, candidate, null, new ToneSimilarity(), SelfDistance);
        h.Matcher.Answer = new ToneMatchResult(
            [new ToneMatchCandidate(Rig("amp"), score.Distance, "origin0") { Score = score }],
            SelfDistance, TimeSpan.Zero, []);

        h.Vm.MatchCommand.Execute(null);

        var row = Assert.Single(h.Vm.MatchCandidates);
        Assert.False(row.IsRated);
        Assert.Null(row.MatchPercent);
        Assert.Equal(MatchUnrated.NoReference, row.UnratedReason);
        Assert.Equal(Loc("ToneMatch_Unrated_NoReference"), row.UnratedText);
        Assert.Equal(Loc("ToneMatch_UnratedWhy", row.UnratedText), row.MatchText);
        Assert.Contains(row.UnratedText, row.Tone.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ACandidateWithNoScoreAtAll_IsUnratedRatherThanZero()
    {
        // A producer that did not score (a fake matcher, a hand-built candidate) is a
        // DIFFERENT thing from a run that tried and could not — but both are unrated,
        // and neither is 0 %.
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Matcher.Answer = new ToneMatchResult(
            [new ToneMatchCandidate(Rig("amp"), 0.4, "origin0")], 0.2, TimeSpan.Zero, []);

        h.Vm.MatchCommand.Execute(null);

        var row = Assert.Single(h.Vm.MatchCandidates);
        Assert.False(row.IsRated);
        Assert.Null(row.MatchPercent);
        Assert.Equal(Loc("ToneMatch_Unrated_NotScored"), row.UnratedText);
        Assert.Empty(row.Breakdown);
    }

    [Fact]
    public void ACandidateInsideThePassagesOwnVariation_CarriesTheCaveat()
    {
        // The old 100 % anchor, demoted: a candidate closer than the target's own two
        // windows is flagged rather than scored 100.
        var h = new Harness();
        h.GiveItAReferenceDi();
        var target = Flat(0f);
        var reference = Flat(ReferenceOffset);
        var similarity = new ToneSimilarity();
        var referenceDistance = similarity.Distance(target, reference);
        var candidate = Flat(ReferenceOffset * 0.25f);
        var score = MatchScale.Score(target, candidate, reference, similarity, referenceDistance * 0.5);
        h.Matcher.Answer = new ToneMatchResult(
            [new ToneMatchCandidate(Rig("amp"), score.Distance, "origin0") { Score = score }],
            referenceDistance * 0.5, TimeSpan.Zero, []);

        h.Vm.MatchCommand.Execute(null);

        var row = Assert.Single(h.Vm.MatchCandidates);
        Assert.True(row.WithinTargetNoise);
        Assert.Equal(50, row.SelfDistancePercent);
        Assert.NotEqual(string.Empty, row.NoiseCaveatText);
    }

    [Fact]
    public void TheBreakdownRowsAreOrderedWorstFirst_AndNameTheirBlock()
    {
        var h = new Harness();
        h.GiveItAReferenceDi();
        h.Matcher.Answer = ResultWith(0.5);

        h.Vm.MatchCommand.Execute(null);

        var row = h.Vm.MatchCandidates[0];
        Assert.True(row.Breakdown.Count <= 4);
        for (var i = 1; i < row.Breakdown.Count; i++)
            Assert.True(row.Breakdown[i - 1].SharePercent >= row.Breakdown[i].SharePercent);
        // Every block resolves to a player-facing name, never a resource key.
        Assert.All(row.Breakdown, b => Assert.DoesNotContain("ToneMatch_Block_", b.Label, StringComparison.Ordinal));
    }

    [Fact]
    public void PickSpan_TakesTheSpanThePlayheadIsStandingIn()
    {
        var source = new ToneMatchSource(
            "s.wav",
            [new ToneSpan(0, "Clean", 0, 20_000), new ToneSpan(1, "Lead", 20_000, 60_000)],
            25_000,
            120_000);

        var (from, to) = ToneDesignViewModel.PickSpan(source, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(20), from);
        Assert.Equal(TimeSpan.FromSeconds(60), to);
    }

    [Fact]
    public void PickSpan_SkipsASpanTooShortToSayAnythingAbout()
    {
        // A stab, a stinger or a mis-drop: averaging a spectrum over it produces a
        // confident answer from one chord. The chart layer already decides this.
        var source = new ToneMatchSource(
            "s.wav",
            [new ToneSpan(0, "Stab", 0, 200), new ToneSpan(1, "Lead", 200, 60_000)],
            100,
            120_000);

        var (from, to) = ToneDesignViewModel.PickSpan(source, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromMilliseconds(200), from);
        Assert.Equal(TimeSpan.FromSeconds(60), to);
    }

    /// <summary>
    ///     W5a, 2026-09-16: this asserted <c>(0, 30 s)</c> for a playhead at 1:30 —
    ///     the fallback ignored <c>PositionMs</c> entirely, so a chart with no tone
    ///     changes (the owner's own project, and every song not in the library)
    ///     matched the OPENING of the song wherever the player was standing. On
    ///     Sakasma the separated guitar for that window is digital silence for ten
    ///     seconds and −70 dBFS for five more, so the tone was built out of half an
    ///     intro. The window follows the playhead now; at playhead 0 the answer is
    ///     unchanged.
    /// </summary>
    [Fact]
    public void PickSpan_WithNoToneChanges_TakesTheFallbackFromThePlayhead_AndNeverTheWholeSong()
    {
        var source = new ToneMatchSource("s.wav", Array.Empty<ToneSpan>(), 90_000, 240_000);

        var (from, to) = ToneDesignViewModel.PickSpan(source, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(90), from);
        Assert.Equal(TimeSpan.FromSeconds(120), to);
    }

    [Fact]
    public void PickSpan_WithNoToneChanges_AtTheStart_IsStillTheOpening()
    {
        var source = new ToneMatchSource("s.wav", Array.Empty<ToneSpan>(), 0, 240_000);

        var (from, to) = ToneDesignViewModel.PickSpan(source, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.Zero, from);
        Assert.Equal(TimeSpan.FromSeconds(30), to);
    }

    [Fact]
    public void PickSpan_WithThePlayheadNearTheEnd_SlidesBackForAFullWindow()
    {
        // A sliver at the end is the same fault as an intro at the start: a window
        // shorter than the analysis needs produces a confident answer about one bar.
        var source = new ToneMatchSource("s.wav", Array.Empty<ToneSpan>(), 235_000, 240_000);

        var (from, to) = ToneDesignViewModel.PickSpan(source, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(210), from);
        Assert.Equal(TimeSpan.FromSeconds(240), to);
    }

    [Fact]
    public void PickSpan_OnASongShorterThanTheFallback_StopsAtTheEnd()
    {
        var source = new ToneMatchSource("s.wav", Array.Empty<ToneSpan>(), 0, 12_000);

        var (_, to) = ToneDesignViewModel.PickSpan(source, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(12), to);
    }

    // ---- the bundled reference clip -------------------------------------------

    [Fact]
    public async Task ABundledReferenceClip_IsReadAndUsedWhenNothingWasRecorded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-di-{Guid.NewGuid():N}.wav");
        WriteWav16(path, [0f, 0.5f, -0.5f, 1f], 44_100);
        try
        {
            var h = new Harness();
            h.Vm.ConfigureMatch(() => h.Matcher, () => h.Isolator, bundledReferenceDi: () => path);

            await h.Vm.MatchCommand.ExecuteAsync(null);

            var di = Assert.Single(h.Matcher.Requests).ReferenceDi;
            Assert.Equal(44_100, di.SampleRate);
            Assert.Equal(4, di.Samples.Length);
            Assert.True(Math.Abs(di.Samples[1] - 0.5f) < 0.001f, $"the 16-bit sample decoded as {di.Samples[1]}");
            Assert.Equal(Loc("ToneMatch_DiBundled"), h.Vm.ReferenceDiLabel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AMissingBundledClip_IsAMessage_NotAnException()
    {
        var h = new Harness();
        h.Vm.ConfigureMatch(
            () => h.Matcher, () => h.Isolator,
            bundledReferenceDi: () => Path.Combine(Path.GetTempPath(), "dc-no-such-di.wav"));

        await h.Vm.MatchCommand.ExecuteAsync(null);

        Assert.Equal(Loc("ToneMatch_Fault_NoReferenceDi"), h.Vm.MatchError);
    }

    [Fact]
    public async Task WithNothingRecordedAndNoClipOnDisk_TheBuiltInClipIsUsed()
    {
        // `Assets/Ai/reference-di.wav` is EMBEDDED and never deployed, so the
        // file branch answers nothing in a real build. Before the generated clip
        // was wired in, the first press of Match on a fresh install therefore
        // said "no reference DI" and the whole feature read as broken.
        var h = new Harness();
        h.Vm.ConfigureMatch(
            () => h.Matcher,
            () => h.Isolator,
            bundledReferenceDi: static () => null,
            builtInReferenceDi: DotCharter.Services.Ai.Match.Similarity.ReferenceDi.Medley);

        await h.Vm.MatchCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, h.Vm.MatchError);
        var di = Assert.Single(h.Matcher.Requests).ReferenceDi;
        Assert.True(di.Samples.Length > 0, "the built-in clip produced no audio");
        Assert.Equal(Loc("ToneMatch_DiBundled"), h.Vm.ReferenceDiLabel);
    }

    private static void WriteWav16(string path, float[] samples, int rate)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        var dataBytes = samples.Length * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in samples) w.Write((short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue));
    }
}
