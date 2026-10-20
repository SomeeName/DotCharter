using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     The runner and the report writer, end to end, with a deterministic
///     <see cref="FakeToneDesignService" /> in place of a provider. No network, no
///     key, no corpus — the default gate runs all of it.
///     <para>
///         This is what makes the LIVE eval trustworthy: by the time the owner
///         spends money, the only untested thing in the path is the model itself.
///     </para>
/// </summary>
public sealed class ToneDesignEvalOfflineTests : IDisposable
{
    /// <summary>
    ///     The score <see cref="FakeToneDesignBehaviour.Mediocre" /> earns over the
    ///     committed fixture corpus. Pinned, not computed: a change to the weights,
    ///     to the canonical form or to a fixture moves it, and a silent move is how
    ///     an eval starts reporting two different numbers for the same model. When
    ///     this fails, decide WHICH number is right before editing it.
    ///     <para>
    ///         <b>Read it as the FLOOR, not as a mediocre score.</b> 67.3 is what a
    ///         fixed <c>overdrive -> amp -> cab</c> earns while knowing nothing at
    ///         all about the song: most guitar tones contain those three, and the
    ///         knob component rewards catalogue defaults for sitting in the middle
    ///         of every range. A model that scores 70 has told you nothing; the
    ///         interesting range starts above it. <c>tools/ai-eval/README.md</c>
    ///         says the same where the owner will read it.
    ///     </para>
    ///     <para>
    ///         Was 69.82 until 2026-10, when the TRUTH grew: a converted RS tone now
    ///         carries the cabinet's mic layer (<c>micpos</c>) and a four-band studio EQ
    ///         as two <c>eq</c> nodes, which a fixed three-module answer does not have.
    ///         The floor moving down because the truth got more faithful is right.
    ///     </para>
    ///     <para>
    ///         67.28 -> 67.31 (2026-10, #70 / #72), two moves measured apart: the
    ///         TRUTH changed (amps on the circuit voicings, the bass heads' mid /
    ///         shift controls set), -0.12 — with main's catalogue mirror swapped
    ///         back in the floor reads 67.16 — and the catalogue gained knobs
    ///         (amp Bright / Resonance, bassamp mid / octave / shift) that both
    ///         sides leave at their defaults, +0.15.
    ///     </para>
    ///     <para>
    ///         67.31 -> 67.57 (2026-10, #74 / #75): the truth's cabinets
    ///         moved onto the IRs of their own speaker layouts (the `cab.model`
    ///         range grew 0..11 -> 0..25, so one roster step is a smaller knob
    ///         distance) and its mic layers gained the tube mic and the off-axis
    ///         angle (a new `micpos` knob both sides otherwise leave at 0).
    ///     </para>
    ///     <para>
    ///         67.57 -> 68.43 (2026-10, #88; +0.86, the same move it made on a
    ///         base without #74 / #75): every converted amp is now Rocksmith's
    ///         own chain (`rsamp`), which a design cannot write, so the scorer judges
    ///         it as the designable archetype the prompt's idiom names for its slot
    ///         (`amp` / `bassamp` / `tubepre`) at its defaults
    ///         (ToneDesignScorer.RsAmpStandIn). The answer's `amp` now matches a guitar
    ///         truth's amp family and its default knobs exactly.
    ///     </para>
    ///     <para>
    ///         68.43 -> 68.54 (#755): the game's chains and curves are read from the
    ///         player's install, and the test process loads none, so the truth is the
    ///         converter WITHOUT them — every amp on its generic roster model at its
    ///         own knobs, the curve gears on their label-reading mapping.
    ///     </para>
    /// </summary>
    private const double MediocreMean = 68.54;

    private readonly string _artifactRoot = Path.Combine(
        Path.GetTempPath(), "dotcharter-ai-eval-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ToneEvalOptions Options => new()
    {
        ArtifactRoot = _artifactRoot,
        MaxSongs = 100,
        Label = "offline fake",
        Provider = "none"
    };

    public void Dispose() => BestEffortDelete.Directory(_artifactRoot);

    // ------------------------------------------------------------ the three anchors

    [Fact]
    public async Task APerfectService_ScoresOneHundredOnEveryRow()
    {
        var corpus = ToneEvalCorpus.Fixtures;
        var service = new FakeToneDesignService(FakeToneDesignBehaviour.Perfect, corpus);

        var run = await ToneDesignEval.RunAsync(service, corpus, Options, Ct);

        Assert.Equal(corpus.Count, service.Calls);
        Assert.Equal(0, run.Failures);
        Assert.Equal(100.0, run.Mean, 6);
        Assert.Equal(100.0, run.Median, 6);
        Assert.Equal(100.0, run.P10, 6);
        Assert.All(run.Results, r => Assert.Equal(100.0, r.Total, 6));
    }

    [Fact]
    public async Task AnEmptyService_ScoresBelowTen()
    {
        var corpus = ToneEvalCorpus.Fixtures;
        var service = new FakeToneDesignService(FakeToneDesignBehaviour.Empty, corpus);

        var run = await ToneDesignEval.RunAsync(service, corpus, Options, Ct);

        Assert.True(run.Mean < 10.0, $"the empty service scored {run.Mean:F2}.");
        Assert.All(run.Results, r => Assert.Equal(0.0, r.Total, 6));
        Assert.All(run.Results, r => Assert.Empty(r.DesignedModules));
        Assert.All(run.Results, r => Assert.NotEmpty(r.TruthModules));
    }

    [Fact]
    public async Task AMediocreService_ScoresTheNumberThisTestPins()
    {
        var corpus = ToneEvalCorpus.Fixtures;
        var service = new FakeToneDesignService(FakeToneDesignBehaviour.Mediocre, corpus);

        var run = await ToneDesignEval.RunAsync(service, corpus, Options, Ct);

        Assert.Equal(MediocreMean, run.Mean, 2);
        Assert.True(run.Mean is > 10.0 and < 100.0,
            "the generic overdrive → amp → cab answer must land between the two anchors; " +
            "see FakeToneDesignService.MediocreRig.");

        // And it must be REPEATABLE — a runner that shuffles its corpus produces a
        // number nobody can diff.
        var again = await ToneDesignEval.RunAsync(
            new FakeToneDesignService(FakeToneDesignBehaviour.Mediocre, corpus), corpus, Options, Ct);
        Assert.Equal(run.Mean, again.Mean, 9);
        Assert.Equal(run.Results.Select(r => r.Id), again.Results.Select(r => r.Id));
    }

    // ------------------------------------------------------------------- artifacts

    [Fact]
    public async Task TheRunner_WritesResultsJsonAndReportMarkdown()
    {
        var corpus = ToneEvalCorpus.Fixtures;
        var run = await ToneDesignEval.RunAsync(
            new FakeToneDesignService(FakeToneDesignBehaviour.Mediocre, corpus), corpus, Options, Ct);

        var results = Path.Combine(run.ArtifactDirectory, "results.json");
        var report = Path.Combine(run.ArtifactDirectory, "report.md");

        Assert.True(File.Exists(results), results + " was not written.");
        Assert.True(File.Exists(report), report + " was not written.");
        Assert.StartsWith(_artifactRoot, run.ArtifactDirectory, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("artifacts", "ai-eval"), run.ArtifactDirectory,
            StringComparison.Ordinal);

        // results.json round-trips into the same record, so a consumer can read it.
        var reloaded = JsonSerializer.Deserialize<ToneEvalRun>(
            await File.ReadAllTextAsync(results, Ct),
            new JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });
        Assert.NotNull(reloaded);
        Assert.Equal(run.Results.Count, reloaded!.Results.Count);
        Assert.Equal(run.Mean, reloaded.Mean, 9);

        var markdown = await File.ReadAllTextAsync(report, Ct);
        Assert.Contains("# AI tone design eval", markdown, StringComparison.Ordinal);
        Assert.Contains("Chains, side by side", markdown, StringComparison.Ordinal);
        foreach (var entry in corpus)
            Assert.Contains(entry.SongTitle, markdown, StringComparison.Ordinal);
        // Both chains printed, so a human can see WHY a row scored what it did.
        Assert.Contains("- truth:", markdown, StringComparison.Ordinal);
        Assert.Contains("- designed:", markdown, StringComparison.Ordinal);
        // The weights are in the artifact: a report whose weights are unknown
        // cannot be compared with one written after they changed.
        Assert.Contains("**Weights**", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReport_EscapesAPipeInASongTitle()
    {
        // A markdown table is one bad title away from silently losing a column.
        var entry = ToneEvalCorpus.Fixtures[0] with { SongTitle = "Pipe | Dream" };
        var corpus = new[] { entry };

        var run = await ToneDesignEval.RunAsync(
            new FakeToneDesignService(FakeToneDesignBehaviour.Perfect, corpus), corpus, Options, Ct);

        var markdown = await File.ReadAllTextAsync(Path.Combine(run.ArtifactDirectory, "report.md"), Ct);
        Assert.Contains(@"Pipe \| Dream", markdown, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- token + cost

    [Fact]
    public async Task TokensAndCost_AreSummedAtTheConfiguredListPrice()
    {
        var corpus = ToneEvalCorpus.Fixtures;
        var run = await ToneDesignEval.RunAsync(
            new FakeToneDesignService(FakeToneDesignBehaviour.Mediocre, corpus), corpus, Options, Ct);

        var expectedIn = corpus.Count * FakeToneDesignService.FakeInputTokens;
        var expectedOut = corpus.Count * FakeToneDesignService.FakeOutputTokens;
        Assert.Equal(expectedIn, run.InputTokens);
        Assert.Equal(expectedOut, run.OutputTokens);

        var expectedCost = expectedIn / 1e6 * ToneEvalOptions.OpusInputUsdPerMTok +
                           expectedOut / 1e6 * ToneEvalOptions.OpusOutputUsdPerMTok;
        Assert.Equal(expectedCost, run.CostUsd, 9);
    }

    [Fact]
    public void ThePreFlightEstimate_ScalesWithTheCallCount()
    {
        var options = new ToneEvalOptions { MaxSongs = 20 };

        Assert.Equal(20, options.CallCount(35));
        Assert.Equal(12, options.CallCount(12));
        Assert.Equal(40, (options with { IncludeShortPrompt = true }).CallCount(35));

        var one = options.EstimateUsd(1);
        Assert.Equal(
            ToneEvalOptions.OpusInputUsdPerMTok * 6_000 / 1e6 +
            ToneEvalOptions.OpusOutputUsdPerMTok * 900 / 1e6, one, 9);
        Assert.Equal(20 * one, options.EstimateUsd(20), 9);
    }

    // -------------------------------------------------------------- the two prompts

    [Fact]
    public async Task TheShortPromptVariant_DoublesTheRowsAndIsLabelled()
    {
        var corpus = ToneEvalCorpus.Fixtures;
        var service = new FakeToneDesignService(FakeToneDesignBehaviour.Perfect, corpus);

        var run = await ToneDesignEval.RunAsync(
            service, corpus, Options with { IncludeShortPrompt = true }, Ct);

        Assert.Equal(corpus.Count * 2, run.Results.Count);
        Assert.Equal(corpus.Count * 2, service.Calls);
        Assert.Equal(corpus.Count, run.Results.Count(r => r.PromptKind == ToneEvalPromptKind.Full));
        Assert.Equal(corpus.Count, run.Results.Count(r => r.PromptKind == ToneEvalPromptKind.Short));
        Assert.Equal(corpus.Count, run.Songs);
    }

    [Fact]
    public async Task MaxSongs_CapsTheRun()
    {
        var corpus = ToneEvalCorpus.Fixtures;
        var service = new FakeToneDesignService(FakeToneDesignBehaviour.Perfect, corpus);

        var run = await ToneDesignEval.RunAsync(service, corpus, Options with { MaxSongs = 2 }, Ct);

        Assert.Equal(2, run.Songs);
        Assert.Equal(2, run.Results.Count);
        Assert.Equal(2, service.Calls);
    }

    // --------------------------------------------------------------- failure paths

    [Fact]
    public async Task AFailingService_IsRecordedRowByRowAndDoesNotAbortTheRun()
    {
        var corpus = ToneEvalCorpus.Fixtures;
        var run = await ToneDesignEval.RunAsync(
            new FakeToneDesignService(FakeToneDesignBehaviour.Failing, corpus), corpus, Options, Ct);

        Assert.Equal(corpus.Count, run.Results.Count);
        Assert.Equal(corpus.Count, run.Failures);
        Assert.All(run.Results, r =>
        {
            Assert.NotNull(r.Error);
            Assert.Contains("ModelRefused", r.Error!, StringComparison.Ordinal);
            Assert.Equal(0.0, r.Total);
            // The truth chain is still reported, so the report shows what was MISSED.
            Assert.NotEmpty(r.TruthModules);
        });

        // The aggregate averages the SUCCESSFUL rows; with none it must be 0, not NaN.
        Assert.Equal(0.0, run.Mean);
        Assert.False(double.IsNaN(run.Median));

        var markdown = await File.ReadAllTextAsync(Path.Combine(run.ArtifactDirectory, "report.md"), Ct);
        Assert.Contains("**FAILED**", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var corpus = ToneEvalCorpus.Fixtures;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ToneDesignEval.RunAsync(
                new FakeToneDesignService(FakeToneDesignBehaviour.Perfect, corpus),
                corpus, Options, cts.Token));
    }

    [Fact]
    public void TheDefaultArtifactRoot_IsTheRepositoryRoot()
    {
        var root = ToneDesignEval.ResolveArtifactRoot(new ToneEvalOptions());

        // Either it found DotCharter.sln, or it fell back to the binaries — never
        // empty, and never a throw, because an artifact must always land somewhere.
        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.True(Directory.Exists(root));
    }

    // ------------------------------------------------------------------ percentiles

    [Fact]
    public async Task MedianAndP10_ReadTheLowerTail()
    {
        // Two rows: one perfect, one empty. The mean hides the failure; p10 does not.
        var corpus = ToneEvalCorpus.Fixtures.Take(2).ToList();
        var run = await ToneDesignEval.RunAsync(new AlternatingService(corpus), corpus, Options, Ct);

        Assert.Equal(2, run.Results.Count);
        Assert.Equal(50.0, run.Mean, 6);
        Assert.Equal(50.0, run.Median, 6);
        Assert.Equal(10.0, run.P10, 6); // linear interpolation between 0 and 100
        Assert.True(run.P10 < run.Mean, "p10 must expose a tail the mean smooths over.");
    }

    /// <summary>Perfect on the first call, empty on the next — the smallest service
    /// that produces a spread for the percentile assertions.</summary>
    private sealed class AlternatingService : DotCharter.Tone.Ai.IToneDesignService
    {
        /// <summary>The copy &amp; paste route is not exercised by this fake.</summary>
        public Task<DotCharter.Tone.Ai.ToneDesignManualPrompt> BuildManualPromptAsync(
            DotCharter.Tone.Ai.ToneDesignRequest request, CancellationToken ct) =>
            throw new NotSupportedException("this fake never builds a copy & paste prompt");

        /// <summary>The copy &amp; paste route is not exercised by this fake.</summary>
        public Task<DotCharter.Tone.Ai.ToneDesignResult> ImportReplyAsync(
            DotCharter.Tone.Ai.ToneDesignRequest request, string reply, IProgress<string>? status,
            CancellationToken ct) =>
            throw new NotSupportedException("this fake never imports a pasted answer");

        private readonly FakeToneDesignService _perfect;
        private readonly FakeToneDesignService _empty;
        private int _calls;

        public AlternatingService(IReadOnlyList<ToneEvalEntry> corpus)
        {
            _perfect = new FakeToneDesignService(FakeToneDesignBehaviour.Perfect, corpus);
            _empty = new FakeToneDesignService(FakeToneDesignBehaviour.Empty, corpus);
        }

        public bool IsConfigured => true;

        public event EventHandler? ConfiguredChanged;

        public void NotifyConfigurationChanged() => ConfiguredChanged?.Invoke(this, EventArgs.Empty);

        public Task<DotCharter.Tone.Ai.ToneDesignResult> DesignAsync(
            DotCharter.Tone.Ai.ToneDesignRequest request, IProgress<string>? status, CancellationToken ct)
            => (_calls++ % 2 == 0 ? _perfect : _empty).DesignAsync(request, status, ct);
    }
}
