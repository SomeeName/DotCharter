using System.Globalization;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     The LIVE eval: the real design service, a real provider, real money.
///     <para>
///         <b>Opt-in or part of the gate — there is no third state.</b> Without
///         <c>DOTCHARTER_AI_EVAL=1</c> and a full provider configuration this class
///         <c>Assert.Skip</c>s with the exact variables it wanted. It is the same
///         discipline <c>GND_IDMT_ROOT</c> enforces on the CQT evals, and for the
///         same reason: a hard-coded fallback once made a ten-minute research
///         harness run on every <c>dotnet test</c> on exactly one machine.
///     </para>
///     <para>How to run it is in <c>tools/ai-eval/README.md</c>.</para>
/// </summary>
public sealed class ToneDesignLiveEvalTests
{
    [Fact]
    public async Task DesignsTheCorpusAndWritesAReport()
    {
        if (!ToneEvalEnvironment.TryResolve(out var environment, out var reason))
            Assert.Skip(reason);

        var corpus = ToneEvalCorpus.LoadLocal(environment!.MaxSongs);
        if (corpus.Count == 0)
        {
            // Better a small honest run than none: the committed fixtures are five
            // real RS tones and cost about a nickel.
            corpus = ToneEvalCorpus.Fixtures;
            Console.WriteLine("[ai-eval] " + ToneEvalCorpus.MissingCorpus +
                              " Falling back to the 5 committed fixtures.");
        }

        // Anchored mode only: an unanchored run gets NO library, so the leak
        // cannot happen by forgetting a flag in one of the two places.
        var anchors = environment.UseAnchor ? new ToneEvalAnchorLibrary(corpus) : null;
        // The corpus goes in as well, and for ONE reason: the transcript provider
        // names its files by corpus position. Every other provider ignores it.
        if (!ToneDesignServiceLocator.TryCreate(environment, anchors, out var service, out var why, corpus))
        {
            Assert.Skip("DOTCHARTER_AI_EVAL is set but the live service refused the " +
                        "configuration: " + why);
        }

        var options = environment.ToOptions();
        var calls = options.CallCount(corpus.Count);

        // The estimate goes out BEFORE the first call, so a mis-set MAX is caught
        // by a human reading the console rather than by an invoice.
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[ai-eval] {environment.Provider} / {environment.ModelId}: {corpus.Count} songs, " +
            $"{calls} requests, estimated ${options.EstimateUsd(calls):F2} " +
            $"(~{options.AssumedInputTokensPerCall} in / {options.AssumedOutputTokensPerCall} out " +
            $"per call at ${options.InputUsdPerMTok:F2}/${options.OutputUsdPerMTok:F2} per MTok). " +
            $"Cap with {ToneEvalEnvironment.MaxVariable}."));

        Console.WriteLine(environment.UseAnchor
            ? "[ai-eval] ANCHORED run: the model is handed each song's own converted "
              + "Rocksmith tone, which is the chain the scorer grades against. The result "
              + "is an UPPER BOUND, not a generalisation score."
            : "[ai-eval] unanchored run (UseLibraryTones=false): the model never sees the "
              + $"ground truth. Set {ToneEvalEnvironment.AnchorVariable}=1 for the "
              + "anchored upper bound.");

        var run = await ToneDesignEval.RunAsync(service!, corpus, options, TestContext.Current.CancellationToken);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[ai-eval] mean {run.Mean:F1} of a reachable {run.MeanCeiling:F1} · " +
            $"median {run.Median:F1} · p10 {run.P10:F1} · " +
            $"{run.Failures} failed · {run.InputTokens:N0} in / {run.OutputTokens:N0} out · " +
            $"billed ${run.CostUsd:F2}"));
        // The machine-readable half: tools/ai-eval/Compare-Models.ps1 greps this ONE
        // line to find the run it just paid for. Keep it a bare directory on a line
        // of its own — see ToneEvalConsoleLines.
        Console.WriteLine(ToneEvalConsoleLines.Artifacts(run.ArtifactDirectory));
        Console.WriteLine("[ai-eval] report: " + Path.Combine(run.ArtifactDirectory, "report.md"));

        // The transcript provider's DUMP pass: an empty transcript directory fails
        // every row for a missing reply, and every prompt file has just been
        // written. That is the intended first half of a two-pass run, not a
        // regression — so it is a SKIP naming the directory, and the assertion
        // below stays a real assertion for the scored pass.
        if (environment.TranscriptDir is { } transcriptDir && run.Failures == run.Results.Count)
        {
            Assert.Skip(string.Create(CultureInfo.InvariantCulture,
                $"[ai-eval] transcript DUMP pass: {run.Results.Count} prompt(s) written to " +
                $"\"{transcriptDir}\" and no reply answered yet. Write each <n>.reply.json " +
                $"beside its <n>.prompt.json and run again to score."));
        }

        // The assertions are deliberately thin. There is NO live baseline yet, so a
        // score threshold here would be a number invented rather than measured —
        // and a gate that fails on a model being mediocre teaches nobody anything.
        // What must hold is that the harness RAN: rows exist, artifacts exist, and
        // the provider answered at least once.
        Assert.NotEmpty(run.Results);
        Assert.True(File.Exists(Path.Combine(run.ArtifactDirectory, "results.json")));
        Assert.True(File.Exists(Path.Combine(run.ArtifactDirectory, "report.md")));
        Assert.True(run.Failures < run.Results.Count,
            "every single request failed: " +
            (run.Results.FirstOrDefault(r => r.Error is not null)?.Error ?? "(no detail)"));
    }

    // ------------------------------------------------ the gate itself, in the gate

    [Fact]
    public void TheGate_IsClosedInAnUnconfiguredEnvironment()
    {
        // Proves the skip is real rather than a comment: with the master switch
        // absent, TryResolve must refuse and name every variable it wanted.
        var previous = Environment.GetEnvironmentVariable(ToneEvalEnvironment.EnableVariable);
        try
        {
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.EnableVariable, null);

            Assert.False(ToneEvalEnvironment.TryResolve(out var environment, out var reason));
            Assert.Null(environment);
            Assert.Contains(ToneEvalEnvironment.EnableVariable, reason, StringComparison.Ordinal);
            Assert.Contains(ToneEvalEnvironment.ProviderVariable, reason, StringComparison.Ordinal);
            Assert.Contains(ToneEvalEnvironment.ModelVariable, reason, StringComparison.Ordinal);
            Assert.Contains(ToneEvalEnvironment.BaseUrlVariable, reason, StringComparison.Ordinal);
            Assert.Contains(ToneEvalEnvironment.KeyVariable, reason, StringComparison.Ordinal);
            Assert.Contains(ToneEvalEnvironment.AnthropicKeyVariable, reason, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.EnableVariable, previous);
        }
    }

    [Fact]
    public void TheGate_RefusesEachHalfConfiguration()
    {
        var names = new[]
        {
            ToneEvalEnvironment.EnableVariable, ToneEvalEnvironment.ProviderVariable,
            ToneEvalEnvironment.ModelVariable, ToneEvalEnvironment.BaseUrlVariable,
            ToneEvalEnvironment.KeyVariable, ToneEvalEnvironment.AnthropicKeyVariable,
            ToneEvalEnvironment.MaxVariable, ToneEvalEnvironment.ShortVariable
        };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);

        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);

            Environment.SetEnvironmentVariable(ToneEvalEnvironment.EnableVariable, "1");
            Assert.False(ToneEvalEnvironment.TryResolve(out _, out var noProvider));
            Assert.Contains("must be 'claude' or 'openai'", noProvider, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable(ToneEvalEnvironment.ProviderVariable, "claude");
            Assert.False(ToneEvalEnvironment.TryResolve(out _, out var noModel));
            Assert.Contains(ToneEvalEnvironment.ModelVariable + " is not set", noModel,
                StringComparison.Ordinal);

            Environment.SetEnvironmentVariable(ToneEvalEnvironment.ModelVariable, "claude-opus-5");
            Assert.False(ToneEvalEnvironment.TryResolve(out _, out var noKey));
            Assert.Contains("no key in", noKey, StringComparison.Ordinal);

            // An OpenAI-compatible provider additionally needs a base URL.
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.ProviderVariable, "openai");
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.KeyVariable, "sk-not-a-real-key");
            Assert.False(ToneEvalEnvironment.TryResolve(out _, out var noUrl));
            Assert.Contains(ToneEvalEnvironment.BaseUrlVariable, noUrl, StringComparison.Ordinal);

            // …but it does NOT need a key: LM Studio and Ollama want none, which is
            // ToneDesignModelFactory.Create's own rule, mirrored rather than redecided.
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.KeyVariable, null);
            Environment.SetEnvironmentVariable(
                ToneEvalEnvironment.BaseUrlVariable, "http://127.0.0.1:1234/v1");
            Assert.True(ToneEvalEnvironment.TryResolve(out var local, out _));
            Assert.Null(local!.ApiKey);
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.BaseUrlVariable, null);

            // Fully configured: claude needs no base URL, and the cap defaults to 20.
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.ProviderVariable, "claude");
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.KeyVariable, "sk-not-a-real-key");
            Assert.True(ToneEvalEnvironment.TryResolve(out var resolved, out _));
            Assert.NotNull(resolved);
            Assert.Equal(DotCharter.Tone.Ai.AiProviderKind.Claude, resolved!.Provider);
            Assert.Equal("claude-opus-5", resolved.ModelId);
            Assert.Equal(ToneEvalEnvironment.DefaultMaxSongs, resolved.MaxSongs);
            Assert.False(resolved.IncludeShortPrompt);

            // …and the ANTHROPIC_API_KEY fallback really is a fallback.
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.KeyVariable, null);
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.AnthropicKeyVariable, "sk-env");
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.MaxVariable, "7");
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.ShortVariable, "1");
            Assert.True(ToneEvalEnvironment.TryResolve(out var viaAnthropic, out _));
            Assert.Equal("sk-env", viaAnthropic!.ApiKey);
            Assert.Equal(7, viaAnthropic.MaxSongs);
            Assert.True(viaAnthropic.IncludeShortPrompt);
        }
        finally
        {
            // Restored on the FAILURE path too — these are process-wide.
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    // ------------------------------------------------- constructing the real thing

    [Fact]
    public void TheLocator_BuildsTheRealServiceFromTheEnvironment()
    {
        var environment = new ToneEvalEnvironment(
            DotCharter.Tone.Ai.AiProviderKind.Claude, "claude-opus-5", null,
            "sk-not-a-real-key", 1, false);

        Assert.True(ToneDesignServiceLocator.TryCreate(environment, out var service, out var reason));
        Assert.Equal("", reason);

        // The REAL type, not a stand-in — a locator that quietly handed back a fake
        // would report a perfect live run for free.
        var real = Assert.IsType<DotCharter.Tone.Ai.ToneDesignService>(service);
        Assert.True(real.IsConfigured);

        // …and its settings came from the ENVIRONMENT, not from the owner's saved
        // AppConfig. An eval must never read or write the configured provider.
        Assert.Equal(environment.Settings, real.Settings);
    }

    [Fact]
    public void TheLocator_RefusesAnEnvironmentTheServiceWouldNotActOn()
    {
        // No model id => AiProviderSettings.IsEnabled is false => IsConfigured is
        // false. TryResolve already refuses this, so the only way here is by hand —
        // but the guard must exist, or the eval spends a call to learn it.
        var environment = new ToneEvalEnvironment(
            DotCharter.Tone.Ai.AiProviderKind.Claude, "", null, "sk-not-a-real-key", 1, false);

        Assert.False(ToneDesignServiceLocator.TryCreate(environment, out var service, out var reason));
        Assert.Null(service);
        Assert.Contains("IsConfigured is false", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLocator_DoesNotNeedAKeyForALocalServer()
    {
        // LM Studio and Ollama want none; ToneDesignModelFactory.Create says so, and
        // demanding one here would block the free way to run this eval.
        var environment = new ToneEvalEnvironment(
            DotCharter.Tone.Ai.AiProviderKind.OpenAiCompatible, "qwen3-coder",
            "http://127.0.0.1:1234/v1", null, 1, false);

        Assert.True(ToneDesignServiceLocator.TryCreate(environment, out var service, out _));
        Assert.NotNull(service);
    }
}
