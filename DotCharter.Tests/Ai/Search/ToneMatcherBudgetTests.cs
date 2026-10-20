using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     <b>The budget has to be honest about what it buys.</b>
///     <para>
///         It was <c>MaxRenders</c> 400 against a 60 s wall while one evaluation cost
///         a measured 2.55 s — so the wall expired after ~23 renders, the cap was
///         unreachable, and a reader of <see cref="ToneMatcherOptions" /> believed the
///         search was 17× deeper than it was. Worse, the refinement stage was gated on
///         a flat <c>share &lt; 8</c>: under a tight budget it did NOTHING and the
///         result said nothing about that, which is how the first end-to-end eval
///         ended up hand-tuning <c>RetrievalTopK</c> 3 / <c>RefineCandidates</c> 1 to
///         get one CMA-ES generation out of it.
///     </para>
///     <para>
///         The measurement itself is <c>RenderCostEvalTests</c> (gated, Release).
///         These are the structural consequences, and they run in the default gate.
///     </para>
/// </summary>
public sealed class ToneMatcherBudgetTests
{
    private sealed record Fixture(
        ToneMatcher Matcher, FakeRigRenderer Renderer, MonoAudio Di, MonoAudio Target, int RendersBefore)
    {
        /// <summary>Renders the MATCH spent — the fixture already paid for the library
        /// build and the target, and counting those would make every budget assertion
        /// pass for the wrong reason.</summary>
        public int MatchRenders => Renderer.Renders - RendersBefore;
    }

    private static async Task<Fixture> BuildAsync(ToneMatcherOptions options)
    {
        var di = SearchFakes.Di(1.2);
        var renderer = new FakeRigRenderer();
        var extractor = new FakeFeatureExtractor();
        var library = await RigLibrary.BuildAsync(
            di, new FakeRigSampler("eq"), renderer, extractor,
            new RigLibraryOptions(Count: 48, Seed: 4242), null, TestContext.Current.CancellationToken);

        var target = await renderer.RenderAsync(di, new DcRigPreset([
            new DcRigModule("eq", new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["lowFreq"] = 120f, ["lowGainDb"] = 8f, ["midFreq"] = 900f, ["midQ"] = 1.0f,
                ["midGainDb"] = -12f, ["highFreq"] = 5000f, ["highGainDb"] = 7f, ["outputTrimDb"] = 0f
            })
        ]), TestContext.Current.CancellationToken);

        return new Fixture(
            new ToneMatcher(renderer, new FakeSimilarity(extractor), new FixedRigLibraryProvider(library),
                FakeLocalization.English(),
                null, options),
            renderer, di, target, renderer.Renders);
    }

    private static ToneMatcherOptions Defaults() =>
        // Only the floor window is overridden: the fake DI is 1.2 s, so a 5 s window
        // could not hold two disjoint halves and there would be no floor to report.
        new(SelfDistanceWindow: TimeSpan.FromSeconds(0.5));

    /// <summary>
    ///     <b>The pin the whole item exists for: with the DEFAULT options, refinement
    ///     RUNS.</b> Not "may run if the box is fast" — the budget is counted in
    ///     renders, so this is deterministic.
    ///     <para>
    ///         "Runs" is asserted as what it is: CMA-ES spent renders past the seeds,
    ///         and every planned refinement ended in a refined candidate OR the
    ///         "not improved" note. Until 2026-10 this asserted a refined candidate
    ///         SURVIVED, which is a property of the search landscape, not of the
    ///         budget: widening the <c>eq</c>'s frequency ranges (#68) gave the
    ///         log-scaled search more octaves, and on this fixture two or three
    ///         generations stopped beating the best of 48 retrieval seeds while the
    ///         refinements themselves ran exactly as before.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task WithTheDefaultOptions_RefinementRuns()
    {
        var f = await BuildAsync(Defaults());

        var result = await f.Matcher.MatchAsync(
            new ToneMatchRequest(f.Target, f.Di, null, Candidates: 16) { TargetIsRender = true },
            null, TestContext.Current.CancellationToken);

        var plan = Assert.Single(result.Notes, n => n.StartsWith("plan:", StringComparison.Ordinal));
        Assert.Contains("2 refinement(s)", plan, StringComparison.Ordinal);
        // Seeding costs one render per seed plus the 0 % anchor; the rest is CMA-ES.
        Assert.True(f.MatchRenders >= 4 + 1 + 2 * 8,
            $"the match spent {f.MatchRenders} renders: not even one λ8 generation per refinement");
        var refined = result.Candidates.Count(c => ToneMatchOrigins.WasRefined(c.Origin));
        var notImproved = result.Notes.Count(n => n.Contains("not improved by refinement", StringComparison.Ordinal));
        Assert.Equal(2, refined + notImproved);
    }

    /// <summary>The effective plan is REPORTED, so a run that refined nothing says so
    /// instead of leaving a reader to infer it from a render count.</summary>
    [Fact]
    public async Task TheNotes_CarryTheEffectivePlan()
    {
        var f = await BuildAsync(Defaults());

        var result = await f.Matcher.MatchAsync(
            new ToneMatchRequest(f.Target, f.Di, null) { TargetIsRender = true },
            null, TestContext.Current.CancellationToken);

        var plan = Assert.Single(result.Notes, n => n.StartsWith("plan:", StringComparison.Ordinal));
        Assert.Contains("4 seeds", plan, StringComparison.Ordinal);
        Assert.Contains("2 refinement(s)", plan, StringComparison.Ordinal);
        Assert.Contains("λ8", plan, StringComparison.Ordinal);
        Assert.Contains("cap 48 renders / 150 s", plan, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <b>A tight budget runs a SMALLER population, it does not skip.</b> Five
    ///     renders past the scale's anchor and the four seeds is below the default λ
    ///     of 8, and the old code skipped refinement entirely at anything under 8. Now
    ///     λ shrinks to what the share affords and CMA-ES still runs.
    ///     <para>
    ///         It read λ6 until W5b (2026-09-16) put the 0 % anchor render inside the
    ///         same budget: one fewer render is left to share, so the same cap buys a
    ///         narrower population. The CLAIM — shrink rather than skip — is unchanged.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task ATightBudget_ShrinksThePopulationInsteadOfSkippingRefinement()
    {
        var f = await BuildAsync(Defaults() with { MaxRenders = 10, RefineCandidates = 1 });

        var result = await f.Matcher.MatchAsync(
            new ToneMatchRequest(f.Target, f.Di, null, Candidates: 16) { TargetIsRender = true },
            null, TestContext.Current.CancellationToken);

        var plan = Assert.Single(result.Notes, n => n.StartsWith("plan:", StringComparison.Ordinal));
        Assert.Contains("1 refinement(s)", plan, StringComparison.Ordinal);
        Assert.Contains("λ5", plan, StringComparison.Ordinal);
        Assert.True(f.MatchRenders <= 10, $"spent {f.MatchRenders} renders for a budget of 10");
    }

    /// <summary>Below CMA-ES's own minimum λ of 4 there is genuinely nothing to run,
    /// and that must be SAID rather than looked like a refinement that found
    /// nothing.</summary>
    [Fact]
    public async Task ABudgetThatCannotAffordAGeneration_SaysSo()
    {
        var f = await BuildAsync(Defaults() with { MaxRenders = 4, RefineCandidates = 1 });

        var result = await f.Matcher.MatchAsync(
            new ToneMatchRequest(f.Target, f.Di, null) { TargetIsRender = true },
            null, TestContext.Current.CancellationToken);

        var plan = Assert.Single(result.Notes, n => n.StartsWith("plan:", StringComparison.Ordinal));
        Assert.Contains("budget exhausted by the seeds", plan, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Candidates, c => ToneMatchOrigins.WasRefined(c.Origin));
    }

    /// <summary>
    ///     The two defaults must AGREE. This is the structural half of
    ///     <c>RenderCostEvalTests</c>: that one measures the seconds, this one pins
    ///     that nobody raised the render cap without moving the wall, which is the
    ///     exact drift that produced the 400-vs-60 s pair.
    /// </summary>
    [Fact]
    public void TheRenderCapAndTheWallBudget_AgreeAtTheMeasuredCost()
    {
        // Measured 2026-09-12 in Release on the owner's box: render + extract of the
        // 20 s medley. Re-measure with RenderCostEvalTests rather than trusting this.
        const double secondsPerEvaluation = 2.55;
        var defaults = new ToneMatcherOptions();
        var wall = (defaults.Budget ?? ToneMatcher.DefaultWallBudget).TotalSeconds;

        Assert.True(wall >= defaults.MaxRenders * secondsPerEvaluation,
            $"a {wall:0} s wall cannot spend {defaults.MaxRenders} renders at {secondsPerEvaluation:0.00} s " +
            "each, so MaxRenders is decoration and the search is shallower than the options claim.");

        // …and not absurdly generous either, or the wall stops being a bound at all.
        Assert.True(wall <= defaults.MaxRenders * secondsPerEvaluation * 2.0,
            $"a {wall:0} s wall is more than twice what {defaults.MaxRenders} renders cost — the render " +
            "cap is then the only real bound and a slow rig can run for minutes past it.");
    }
}
