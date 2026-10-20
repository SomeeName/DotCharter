using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     The whole pipeline over the fake renderer: hidden-rig recovery, the
///     self-distance floor, the budget, the prior and the estimator arm.
///     <para>
///         Hidden-rig recovery is the one test that would fail if ANY stage were
///         wrong — encode/decode, retrieval, the objective or the optimiser — which
///         is why it asserts on the recovered KNOB VECTOR and not only on a
///         distance that any degenerate rig could reach.
///     </para>
/// </summary>
public sealed class ToneMatcherTests
{
    /// <summary>The chain the fixture matches over. A three-band EQ: eight
    /// coordinates, each moving the long-term spectrum smoothly and in its own
    /// region, which makes the landscape WELL POSED — a recovery failure is then
    /// the search's fault and not the chain's.
    /// <para>
    /// Getting to a well-posed chain took four attempts and each failure is a real
    /// property worth recording. <c>gain</c> → <c>overdrive</c>: a pre-gain into a
    /// tanh and a drive knob are the SAME control (recovered 0.88 for a hidden
    /// 0.70). <c>overdrive</c> at 24× pre-gain: the top half of the knob is
    /// saturated and two settings 0.12 apart render the same audio.
    /// <c>overdrive</c> → <c>delay</c> at high feedback: a short feedback delay is a
    /// comb, and its loss surface in <c>timeMs</c> is RUGGED, which is the one thing
    /// CMA-ES cannot climb. <c>overdrive</c> → <c>eq</c>: the drive's tone control
    /// is a low-pass and the EQ's high shelf undoes it — degenerate again. None of
    /// those was a bug in the matcher; all four are why the real feature must be
    /// judged on AUDIO distance against the floor, with knob recovery as a
    /// diagnostic.</para></summary>
    private static readonly string[] Chain = ["eq"];

    /// <summary>
    ///     The params this test asserts on: the ones the fake renderer models AND
    ///     the metric can see. Three coordinates of the chain are deliberately NOT
    ///     here, and each exclusion is a real property rather than a concession:
    ///     <list type="bullet">
    ///         <item><c>overdrive.level</c> and <c>eq.outputTrimDb</c> — pure
    ///         OUTPUT LEVELS, and the metric is
    ///         loudness-invariant on purpose (the spectra are normalised to unit
    ///         mean; a rig is loudness-normalised before anyone hears it, see
    ///         <c>DcRigLoudnessNormalizer</c>). A level knob is therefore
    ///         unidentifiable BY CONSTRUCTION, and a search that "recovered" it
    ///         would be fitting the one thing the ear is not being asked about.
    ///         Measured: it lands anywhere in the range while the distance keeps
    ///         falling. The matcher's answer for level is the loudness normaliser,
    ///         not the optimiser.</item>
    ///         <item><c>eq.midQ</c> — BANDWIDTH, and the descriptor's bands are
    ///         1/3-octave. A dip of a given depth at Q 1 and at Q 4.7 differ by less
    ///         than one band edge once the spectrum is folded, so Q is the one EQ
    ///         coordinate this front-end cannot resolve (measured: every other knob
    ///         lands within 0.07, midQ at 0.48). Resolving it needs finer bands, not
    ///         a better search — which is a requirement ON THE EXTRACTOR and is
    ///         recorded as such in the guide.</item>
    ///     </list>
    ///     Asserting on an unidentifiable coordinate is asserting on noise.
    /// </summary>
    private static readonly HashSet<string> Audible = new(StringComparer.Ordinal)
    {
        "lowFreq", "lowGainDb", "midFreq", "midGainDb", "highFreq", "highGainDb"
    };

    private sealed record Rig(
        ToneMatcher Matcher,
        FakeRigRenderer Renderer,
        FakeSimilarity Similarity,
        MonoAudio Di,
        RigLibrary Library);

    private static async Task<Rig> BuildAsync(
        ToneMatcherOptions? options = null,
        IRigEstimator? estimator = null,
        int libraryCount = 48)
    {
        // 1.2 s and 48 library rigs are the measured minimum for the recovery
        // test: at 1.0 s / 32 the retrieval start point is far enough off that the
        // refinement lands on the wrong side of a band edge. The whole fixture is
        // deterministic, so this is a reproducible bound and not a flake.
        var di = SearchFakes.Di(1.2);
        var renderer = new FakeRigRenderer();
        var extractor = new FakeFeatureExtractor();
        var sim = new FakeSimilarity(extractor);
        var library = await RigLibrary.BuildAsync(
            di, new FakeRigSampler(Chain), renderer, extractor,
            new RigLibraryOptions(Count: libraryCount, Seed: 4242), null, CancellationToken.None);

        var matcher = new ToneMatcher(
            renderer, sim, new FixedRigLibraryProvider(library), FakeLocalization.English(), estimator,
            options ?? new ToneMatcherOptions(
                MaxRenders: 1000,
                RefineCandidates: 1,
                SelfDistanceWindow: TimeSpan.FromSeconds(0.5)));
        return new Rig(matcher, renderer, sim, di, library);
    }

    private static DcRigPreset HiddenRig() => new([
        new DcRigModule("eq", new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["lowFreq"] = 120f, ["lowGainDb"] = 8f,
            ["midFreq"] = 900f, ["midQ"] = 1.0f, ["midGainDb"] = -12f,
            ["highFreq"] = 5000f, ["highGainDb"] = 7f, ["outputTrimDb"] = 0f
        })
    ]);

    /// <summary>A full 1000-render match on its own: a nested class is its own xunit
    /// collection, so the four full matches run in parallel instead of back to
    /// back (they were a ~175 s serial chain, the longest pole of the gate).</summary>
    public sealed class HiddenRigRecovery
    {
        [Fact]
        public async Task RecoversAHiddenRig_ToWithinATenthOfEveryKnob()
        {
            var rig = await BuildAsync();
            var hidden = HiddenRig();
            var target = await rig.Renderer.RenderAsync(rig.Di, hidden, CancellationToken.None);

            var result = await rig.Matcher.MatchAsync(
                new ToneMatchRequest(target, rig.Di, null) { TargetIsRender = true }, null, CancellationToken.None);

            var best = result.Candidates[0];
            var layout = KnobLayout.For(hidden);
            var want = layout.Encode(hidden);
            var got = layout.Encode(best.Rig);

            var errors = new List<(string Param, float Error)>();
            for (var i = 0; i < want.Length; i++)
            {
                if (Audible.Contains(layout.Coordinates[i].ParamId))
                    errors.Add((layout.Coordinates[i].ParamId, Math.Abs(want[i] - got[i])));
            }

            var report = string.Join(", ", errors.Select(e => $"{e.Param} {e.Error:0.000}")) +
                         $" | distance {best.Distance:E3}, floor {result.TargetSelfDistanceFloor:E3}, " +
                         string.Join("; ", result.Notes);

            // Three bars, in the order they matter.
            // 1. AUDIO: the match must reach the target's own noise floor. This is the
            //    bar the product is judged on — knob exactness is a diagnostic.
            Assert.True(best.Distance <= result.TargetSelfDistanceFloor,
                "did not reach the self-distance floor: " + report);

            // 2. KNOBS, in aggregate: mean error under 0.1 of full travel — twice as
            //    tight as Comunità 2021's < 0.05 MAE yardstick doubled, and reached
            //    here with no labels at all.
            Assert.True(errors.Average(e => e.Error) < 0.10f, "mean knob error too high: " + report);

            // 3. KNOBS, worst case: no single knob may be wildly wrong. A narrow
            //    mid-peak's CENTRE FREQUENCY is the loosest of them — moving it changes
            //    which single band dips, so the loss carries information about it only
            //    locally — which is why this bound is 0.3 and not 0.1.
            Assert.True(errors.Max(e => e.Error) < 0.30f, "a knob is wildly wrong: " + report);

            // 4. Refinement must have EARNED its renders: the top answer is a refined
            //    one and it beats every rig retrieval offered on its own.
            Assert.EndsWith("+cmaes", best.Origin);
            var bestUnrefined = result.Candidates
                .Where(c => !c.Origin.EndsWith("+cmaes", StringComparison.Ordinal))
                .Select(c => c.Distance).DefaultIfEmpty(double.PositiveInfinity).Min();
            Assert.True(best.Distance < bestUnrefined, "refinement did not beat retrieval: " + report);
        }
    }

    /// <summary>A full 1000-render match on its own: a nested class is its own xunit
    /// collection, so the four full matches run in parallel instead of back to
    /// back (they were a ~175 s serial chain, the longest pole of the gate).</summary>
    public sealed class SelfDistanceFloor
    {
        [Fact]
        public async Task TheSelfDistanceFloor_IsMeasuredFromTheTargetItself()
        {
            var rig = await BuildAsync();
            var target = await rig.Renderer.RenderAsync(rig.Di, HiddenRig(), CancellationToken.None);

            var result = await rig.Matcher.MatchAsync(
                new ToneMatchRequest(target, rig.Di, null) { TargetIsRender = true }, null, CancellationToken.None);

            // Non-zero (two windows of real content are never identical) and small
            // (they are the same tone).
            Assert.InRange(result.TargetSelfDistanceFloor, 1e-6, 1.0);
        }
    }

    /// <summary>
    ///     <b>A span too short to hold two DISJOINT windows has NO floor, and the
    ///     matcher says NaN rather than 0.</b>
    ///     <para>
    ///         0 is a legal floor value — it means "this target is perfectly
    ///         self-similar", i.e. the easiest possible target — so reporting it for
    ///         "I could not measure one" made a consumer divide by the wrong answer
    ///         for a right-looking reason. The first end-to-end eval hit the softer
    ///         half of this on every row: on a 12 s span with a 5 s window the two
    ///         windows are disjoint but cover 10 of the 12 seconds, and on eight of
    ///         twelve rows the reported "floor" sat ABOVE every candidate's distance.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task ASpanShorterThanTwoWindows_ReportsNoFloorRatherThanAZeroOne()
    {
        // The fixture DI is 1.2 s and the window here is 1.0 s: two 1 s windows do
        // not fit, so there is nothing to measure.
        var rig = await BuildAsync(new ToneMatcherOptions(
            MaxRenders: 8, RefineCandidates: 0, SelfDistanceWindow: TimeSpan.FromSeconds(1.0)));
        var target = await rig.Renderer.RenderAsync(rig.Di, HiddenRig(), CancellationToken.None);

        var result = await rig.Matcher.MatchAsync(
            new ToneMatchRequest(target, rig.Di, null) { TargetIsRender = true },
            null, CancellationToken.None);

        Assert.True(double.IsNaN(result.TargetSelfDistanceFloor),
            $"reported a floor of {result.TargetSelfDistanceFloor:E3} for a span that cannot hold two " +
            "disjoint windows — a number a consumer will divide by.");
        Assert.NotEmpty(result.Candidates);
    }

    /// <summary>A full 1000-render match on its own: a nested class is its own xunit
    /// collection, so the four full matches run in parallel instead of back to
    /// back (they were a ~175 s serial chain, the longest pole of the gate).</summary>
    public sealed class ExactPrior
    {
        [Fact]
        public async Task APriorThatIsAlreadyRight_IsNotMadeWorse()
        {
            var rig = await BuildAsync();
            var hidden = HiddenRig();
            var target = await rig.Renderer.RenderAsync(rig.Di, hidden, CancellationToken.None);

            var result = await rig.Matcher.MatchAsync(
                new ToneMatchRequest(target, rig.Di, new ToneMatchPrior(hidden, "text-model")) { TargetIsRender = true },
                null, CancellationToken.None);

            var best = result.Candidates[0];
            var layout = KnobLayout.For(hidden);
            var want = layout.Encode(hidden);
            var got = layout.Encode(best.Rig);
            for (var i = 0; i < want.Length; i++)
            {
                if (!Audible.Contains(layout.Coordinates[i].ParamId)) continue;
                Assert.True(Math.Abs(want[i] - got[i]) < 0.05f,
                    $"the exact prior was degraded on {layout.Coordinates[i].ParamId}: " +
                    $"{want[i]:0.000} -> {got[i]:0.000}");
            }
            Assert.StartsWith("prior", best.Origin);
        }
    }

    [Fact]
    public async Task RespectsTheRenderBudget()
    {
        var rig = await BuildAsync(new ToneMatcherOptions(
            MaxRenders: 60, SelfDistanceWindow: TimeSpan.FromSeconds(0.5)));
        var target = await rig.Renderer.RenderAsync(rig.Di, HiddenRig(), CancellationToken.None);
        var before = rig.Renderer.Renders;

        await rig.Matcher.MatchAsync(new ToneMatchRequest(target, rig.Di, null) { TargetIsRender = true }, null, CancellationToken.None);

        Assert.True(rig.Renderer.Renders - before <= 60,
            $"spent {rig.Renderer.Renders - before} renders for a budget of 60");
    }

    [Fact]
    public async Task TheRenderCapIsExact_NotApproximate()
    {
        // Four retrieval seeds, a cap of three, no refinement: the third render is
        // the last one. This pins the CAP itself rather than the budget-sharing
        // arithmetic that usually keeps the total down.
        //
        // **The SCALE's anchor render is inside the budget (W5b)**, and deliberately:
        // `MatchBudget.TryTake` is the one place a render is authorised, so an
        // overrun stays impossible by construction. It is taken FIRST, so a cap of
        // three buys the anchor plus TWO candidates. Do not "fix" a count here by
        // moving the anchor outside the budget — that would make the cap a number
        // about some renders.
        var rig = await BuildAsync(new ToneMatcherOptions(
            RetrievalTopK: 4, RefineCandidates: 0, MaxRenders: 3,
            SelfDistanceWindow: TimeSpan.FromSeconds(0.5)));
        var target = await rig.Renderer.RenderAsync(rig.Di, HiddenRig(), CancellationToken.None);
        var before = rig.Renderer.Renders;

        var result = await rig.Matcher.MatchAsync(
            new ToneMatchRequest(target, rig.Di, null) { TargetIsRender = true }, null, CancellationToken.None);

        Assert.Equal(3, rig.Renderer.Renders - before);
        Assert.Equal(2, result.Candidates.Count);
        // …and the one render that is not a candidate bought the SCALE: both anchors
        // are measured numbers, and the run says so. (These fake targets are 1.2 s,
        // under `MatchTargetAudio.MinContent`, so the candidates are legitimately
        // UNRATED — the scale is still measured, which is the claim here.)
        Assert.Contains(result.Notes, n => n.StartsWith("scale:", StringComparison.Ordinal));
        Assert.All(result.Candidates, c => Assert.False(double.IsNaN(c.Score!.ReferenceDistance)));
    }

    [Fact]
    public async Task AnExpiredWallClockBudget_StopsBeforeTheFirstRender()
    {
        var rig = await BuildAsync(new ToneMatcherOptions(
            MaxRenders: 400, SelfDistanceWindow: TimeSpan.FromSeconds(0.5)));
        var target = await rig.Renderer.RenderAsync(rig.Di, HiddenRig(), CancellationToken.None);
        var before = rig.Renderer.Renders;

        var result = await rig.Matcher.MatchAsync(
            new ToneMatchRequest(target, rig.Di, null, Budget: TimeSpan.Zero) { TargetIsRender = true }, null, CancellationToken.None);

        // No renders, no candidates, no throw: an exhausted budget is an ANSWER
        // ("nothing was tried"), not an error.
        Assert.Equal(0, rig.Renderer.Renders - before);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task ReportsProgressWithCandidateAndRenderCounts()
    {
        var rig = await BuildAsync(new ToneMatcherOptions(
            MaxRenders: 60, SelfDistanceWindow: TimeSpan.FromSeconds(0.5)));
        var target = await rig.Renderer.RenderAsync(rig.Di, HiddenRig(), CancellationToken.None);
        var lines = new List<string>();

        await rig.Matcher.MatchAsync(new ToneMatchRequest(target, rig.Di, null) { TargetIsRender = true },
            new SyncProgress(lines.Add), CancellationToken.None);

        Assert.Contains(lines, l => l.Contains("Rendering candidate", StringComparison.Ordinal) &&
                                    l.Contains("/60", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Refining candidate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HonoursCancellationBetweenRenders()
    {
        var rig = await BuildAsync();
        var target = await rig.Renderer.RenderAsync(rig.Di, HiddenRig(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var before = rig.Renderer.Renders;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Matcher.MatchAsync(
            new ToneMatchRequest(target, rig.Di, null) { TargetIsRender = true },
            new SyncProgress(_ => cts.Cancel()), cts.Token));

        Assert.True(rig.Renderer.Renders - before < 60,
            $"kept rendering after cancellation: {rig.Renderer.Renders - before}");
    }

    [Fact]
    public async Task AnAvailableEstimator_AddsACandidateTaggedEstimator()
    {
        var hidden = HiddenRig();
        var rig = await BuildAsync(
            new ToneMatcherOptions(MaxRenders: 60, RefineCandidates: 0,
                SelfDistanceWindow: TimeSpan.FromSeconds(0.5)),
            new StubEstimator(hidden, available: true));
        var target = await rig.Renderer.RenderAsync(rig.Di, hidden, CancellationToken.None);

        var result = await rig.Matcher.MatchAsync(
            new ToneMatchRequest(target, rig.Di, null, Candidates: 8) { TargetIsRender = true }, null, CancellationToken.None);

        Assert.Contains(result.Candidates, c => c.Origin.StartsWith("estimator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnavailableEstimator_IsNeverCalled()
    {
        var stub = new StubEstimator(HiddenRig(), available: false);
        var rig = await BuildAsync(new ToneMatcherOptions(MaxRenders: 40, RefineCandidates: 0,
            SelfDistanceWindow: TimeSpan.FromSeconds(0.5)), stub);
        var target = await rig.Renderer.RenderAsync(rig.Di, HiddenRig(), CancellationToken.None);

        var result = await rig.Matcher.MatchAsync(
            new ToneMatchRequest(target, rig.Di, null) { TargetIsRender = true }, null, CancellationToken.None);

        Assert.Equal(0, stub.Calls);
        Assert.DoesNotContain(result.Candidates, c => c.Origin.StartsWith("estimator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AThrowingEstimator_DoesNotLoseTheRetrievalResult()
    {
        var rig = await BuildAsync(new ToneMatcherOptions(MaxRenders: 40, RefineCandidates: 0,
            SelfDistanceWindow: TimeSpan.FromSeconds(0.5)), new ThrowingEstimator());
        var target = await rig.Renderer.RenderAsync(rig.Di, HiddenRig(), CancellationToken.None);

        var result = await rig.Matcher.MatchAsync(
            new ToneMatchRequest(target, rig.Di, null) { TargetIsRender = true }, null, CancellationToken.None);

        Assert.NotEmpty(result.Candidates);
        Assert.Contains(result.Notes, n => n.Contains("estimator unavailable", StringComparison.Ordinal));
    }

    /// <summary>A full 1000-render match on its own: a nested class is its own xunit
    /// collection, so the four full matches run in parallel instead of back to
    /// back (they were a ~175 s serial chain, the longest pole of the gate).</summary>
    public sealed class CandidateCap
    {
        [Fact]
        public async Task CandidatesComeBackSortedAndCapped()
        {
            var rig = await BuildAsync();
            var target = await rig.Renderer.RenderAsync(rig.Di, HiddenRig(), CancellationToken.None);

            var result = await rig.Matcher.MatchAsync(
                new ToneMatchRequest(target, rig.Di, null, Candidates: 3) { TargetIsRender = true }, null, CancellationToken.None);

            Assert.True(result.Candidates.Count <= 3);
            for (var i = 1; i < result.Candidates.Count; i++)
                Assert.True(result.Candidates[i - 1].Distance <= result.Candidates[i].Distance);
        }
    }

    /// <summary><see cref="Progress{T}" /> posts to the synchronization context, so
    /// a callback that cancels the run would not be seen until after the run. This
    /// one reports on the calling thread, which is what the assertion needs.</summary>
    private sealed class SyncProgress(Action<string> onReport) : IProgress<string>
    {
        public void Report(string value) => onReport(value);
    }

    private sealed class StubEstimator(DcRigPreset rig, bool available) : IRigEstimator
    {
        public int Calls { get; private set; }
        public bool IsAvailable => available;

        public Task<RigEstimate> EstimateAsync(MonoAudio wet, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new RigEstimate(
                rig, new Dictionary<string, float> { ["drive"] = 1f }, 0.8f, "stub-v1"));
        }
    }

    private sealed class ThrowingEstimator : IRigEstimator
    {
        public bool IsAvailable => true;

        public Task<RigEstimate> EstimateAsync(MonoAudio wet, CancellationToken ct)
            => throw new InvalidOperationException("no model");
    }
}
