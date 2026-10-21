using System.Text.Json;
using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The Claude Code CLI provider. Every case here drives the real model over a
///     FAKE runner, so the argument list, the stdin payload and the envelope parser
///     are pinned without a binary, a login or a token spent — the one real
///     invocation this feature has is
///     <see cref="ClaudeCliSmokeTests" />, which is env-gated out of the gate.
/// </summary>
public sealed class ClaudeCliToneModelTests
{
    private const string ToneJson =
        """{"name":"Test Tone","rationale":"because","confidence":0.9,"modules":[{"type":"amp","params":{"gain":0.5},"bypassed":false}]}""";

    private static ToneDesignPrompt Prompt()
        => ToneDesignPromptBuilder.Build(new ToneDesignRequest("a clean tone", [], null));

    private static AiProviderSettings Settings(string model = "")
        => new(AiProviderKind.ClaudeCli, model, null, WebResearch: true);

    private static ClaudeCliToneModel Model(FakeRunner runner, string model = "")
        => new(Settings(model), FakeExe, runner, workingDirectory: Path.GetTempPath());

    private const string FakeExe = @"C:\fake\claude.exe";

    /// <summary>A successful envelope in the shape the real CLI prints — captured
    /// from a real 2.1.269 run and reduced to the fields this provider reads.</summary>
    private static string Envelope(
        string? structured = ToneJson,
        string? result = null,
        bool isError = false,
        int input = 2,
        int cacheRead = 0,
        int cacheCreate = 1558,
        int output = 71,
        double cost = 0.0079,
        int turns = 2)
    {
        var structuredPart = structured is null ? "" : $",\"structured_output\":{structured}";
        var resultPart = result is null ? "" : $",\"result\":{JsonSerializer.Serialize(result)}";
        return $$"""
            {"type":"result","subtype":"success","is_error":{{(isError ? "true" : "false")}},
             "num_turns":{{turns}},"total_cost_usd":{{cost.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
             "usage":{"input_tokens":{{input}},"cache_read_input_tokens":{{cacheRead}},
                      "cache_creation_input_tokens":{{cacheCreate}},"output_tokens":{{output}}}
             {{structuredPart}}{{resultPart}}}
            """;
    }

    // ---- the command line ------------------------------------------------------

    /// <summary>
    ///     The argument list IS the contract with the CLI, so it is pinned by value.
    ///     Three absences matter as much as the presences and are asserted below:
    ///     <c>--bare</c> (it disables the OAuth login this whole provider exists to
    ///     use), and the two flags the brief suggested that do not exist in 2.1.269.
    /// </summary>
    [Fact]
    public void TheArgumentList_IsTheExactHeadlessSingleTurnInvocation()
    {
        var prompt = Prompt();
        var args = Model(new FakeRunner()).BuildArguments(prompt);

        Assert.Equal(
        [
            "--print",
            "--output-format", "json",
            "--model", ClaudeCliToneModel.DefaultModelId,
            "--json-schema", prompt.JsonSchema,
            "--system-prompt", ClaudeCliToneModel.FramingPrompt,
            // research on: the web tools, pre-approved, and nothing else
            "--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--no-session-persistence",
            "--permission-prompts", "none"
        ], args);
    }

    /// <summary>
    ///     <c>--bare</c>'s own help says "Anthropic auth is strictly
    ///     ANTHROPIC_API_KEY or apiKeyHelper via --settings (OAuth and keychain are
    ///     never read)". Subscription login IS the OAuth path, so passing it would
    ///     turn the no-key provider into a key-only one that fails for every user it
    ///     was built for. This is the guard that stops it coming back as a tidy-up.
    /// </summary>
    [Fact]
    public void TheArgumentList_NeverPassesBare_NorAFlagThisCliVersionDoesNotHave()
    {
        var args = Model(new FakeRunner()).BuildArguments(Prompt());

        Assert.DoesNotContain("--bare", args);
        // Not in any `claude --help` output. The CLI's parser accepts unknown flags
        // SILENTLY, so passing one is not an error — it is a no-op that reads like a
        // working limit.
        Assert.DoesNotContain("--max-turns", args);
        Assert.DoesNotContain("--append-system-prompt-file", args);

        // `--system-prompt-file` DOES exist (W9, verified on 2.1.273) and is used —
        // but only when the probe found it, which this default overload did not ask.
        // The layouts are pinned in ClaudeCliSystemPromptTests.
        Assert.DoesNotContain("--system-prompt-file", args);

        // …and a RESUMED session is never asked for: replaying every prior turn into
        // each later request costs more than it saves for short, independent tone
        // requests, which is why W9 caches the SYSTEM prompt instead.
        Assert.DoesNotContain("--resume", args);
        Assert.DoesNotContain("--continue", args);
    }

    /// <summary>No API key, no key-bearing environment, is ever handed to the child:
    /// the CLI answers under the user's own login and a key here would silently bill
    /// a different account.</summary>
    [Fact]
    public void TheArgumentList_CarriesNoApiKey()
    {
        var args = Model(new FakeRunner()).BuildArguments(Prompt());
        Assert.DoesNotContain(args, a => a.Contains("sk-ant", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, a => a.Contains("ANTHROPIC_API_KEY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheSettingsModelId_OverridesTheDefault()
        => Assert.Contains(
            ClaudeCliToneModel.AlternateModelId,
            Model(new FakeRunner(), ClaudeCliToneModel.AlternateModelId).BuildArguments(Prompt()));

    // ---- stdin -----------------------------------------------------------------

    /// <summary>
    ///     The grounding goes on STDIN and the command line stays far under
    ///     Windows' 32,767-character cap. This is the case that fails the day
    ///     someone moves the system prompt back onto <c>--system-prompt</c>.
    /// </summary>
    [Fact]
    public void TheSystemPrompt_RidesOnStdin_AndTheCommandLineStaysUnderTheWindowsCap()
    {
        var prompt = Prompt();
        var model = Model(new FakeRunner());

        var stdin = ClaudeCliToneModel.BuildStandardInput(prompt);
        Assert.Contains("# Module catalogue", stdin, StringComparison.Ordinal);
        Assert.Contains("a clean tone", stdin, StringComparison.Ordinal);

        // +3 per argument for the space and the quotes CreateProcess needs.
        var args = model.BuildArguments(prompt);
        var commandLineLength = FakeExe.Length + args.Sum(a => a.Length + 3);
        Assert.True(commandLineLength < 16_000,
            $"command line would be {commandLineLength} characters — half the cap is the budget");

        // …and not merely under it by luck. Putting the system prompt back on
        // `--system-prompt` would land within a few hundred characters of the
        // 32,767 cap TODAY (measured 2026-09-12: ~29,930 + ~2,800 = ~32,730, some
        // 37 characters of headroom) and over it with the next module added to the
        // catalogue. "It still fits" is the reading this guard exists to refuse.
        var ifItWereAnArgument = commandLineLength + prompt.System.Length;
        Assert.True(ifItWereAnArgument > 32_767 - 2_000,
            $"the system prompt on the command line would be {ifItWereAnArgument} characters, " +
            "which no longer crowds the 32,767 cap — re-measure before trusting this guard");
    }

    [Fact]
    public void AFollowUp_LabelsEveryTurnOnStdin()
    {
        var prompt = new ToneDesignPrompt(
            "SYSTEM",
            [
                new ToneDesignTurn(ToneDesignRole.User, "a clean tone"),
                new ToneDesignTurn(ToneDesignRole.Assistant, "{\"name\":\"prev\"}"),
                new ToneDesignTurn(ToneDesignRole.User, "less gain")
            ],
            ToneDraftJson.Schema,
            ToneDraftJson.SchemaName);

        var stdin = ClaudeCliToneModel.BuildStandardInput(prompt);

        Assert.StartsWith("SYSTEM", stdin, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(stdin, "## Request"));
        Assert.Equal(1, Occurrences(stdin, "## Your previous answer"));
        Assert.True(stdin.IndexOf("less gain", StringComparison.Ordinal)
                    > stdin.IndexOf("prev", StringComparison.Ordinal),
            "the turns must stay in order");
    }

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

    // ---- the envelope ----------------------------------------------------------

    [Fact]
    public async Task AGoodRun_ReturnsTheStructuredOutputAndSumsTheInputTokens()
    {
        var runner = new FakeRunner(new ClaudeCliOutcome(0, Envelope(), ""));
        var reply = await Model(runner).CompleteAsync(Prompt(), CancellationToken.None);

        Assert.Contains("\"name\"", reply.Json, StringComparison.Ordinal);
        Assert.Equal("Test Tone", JsonSerializer.Deserialize<ToneDraft>(reply.Json, ToneDraftJson.Options)!.Name);

        // 2 fresh + 0 cache reads + 1,558 cache writes. Reporting `input_tokens`
        // alone would show a ~7,500-token request as 2, which is what makes the
        // panel's cost estimate meaningless for this provider.
        Assert.Equal(1560, reply.Usage.InputTokens);
        Assert.Equal(71, reply.Usage.OutputTokens);
        Assert.Equal(ClaudeCliToneModel.DefaultModelId, reply.Usage.ModelId);
    }

    /// <summary>A run where the schema was not honoured still answers: the tone is
    /// dug out of the `result` STRING, fences and prose and all.</summary>
    [Fact]
    public async Task WithNoStructuredOutput_TheToneIsExtractedFromTheResultString()
    {
        var runner = new FakeRunner(new ClaudeCliOutcome(
            0, Envelope(structured: null, result: $"Here you go!\n```json\n{ToneJson}\n```\nHope that helps."), ""));

        var reply = await Model(runner).CompleteAsync(Prompt(), CancellationToken.None);
        Assert.Equal("Test Tone", JsonSerializer.Deserialize<ToneDraft>(reply.Json, ToneDraftJson.Options)!.Name);
    }

    [Fact]
    public async Task AnEnvelopeCarryingNoTone_IsInvalidOutput()
    {
        var runner = new FakeRunner(new ClaudeCliOutcome(0, Envelope(structured: null), ""));
        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => Model(runner).CompleteAsync(Prompt(), CancellationToken.None));
        Assert.Equal(ToneDesignFault.InvalidOutput, ex.Fault);
    }

    [Fact]
    public async Task ANonZeroExitWithGarbageOnStdout_IsInvalidOutput()
    {
        var runner = new FakeRunner(new ClaudeCliOutcome(1, "Segmentation fault", "node: bad things"));
        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => Model(runner).CompleteAsync(Prompt(), CancellationToken.None));

        Assert.Equal(ToneDesignFault.InvalidOutput, ex.Fault);
        Assert.Contains("exit=1", ex.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorEnvelope_IsUnknown_AndNamesItsSubtype()
    {
        var runner = new FakeRunner(new ClaudeCliOutcome(0, Envelope(isError: true), ""));
        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => Model(runner).CompleteAsync(Prompt(), CancellationToken.None));
        Assert.Equal(ToneDesignFault.Unknown, ex.Fault);
    }

    /// <summary>The design reply must never reach the log through
    /// <see cref="ToneDesignException.Detail" />: stdout is quoted as a LENGTH.</summary>
    [Fact]
    public async Task TheDetailLine_NeverQuotesStdout()
    {
        var runner = new FakeRunner(new ClaudeCliOutcome(1, "a very private tone description", "boom"));
        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => Model(runner).CompleteAsync(Prompt(), CancellationToken.None));

        Assert.DoesNotContain("private tone", ex.Detail!, StringComparison.Ordinal);
        Assert.Contains("chars", ex.Detail!, StringComparison.Ordinal);
    }

    // ---- faults ----------------------------------------------------------------

    [Theory]
    [InlineData("Invalid API key · Please run /login")]
    [InlineData("You are not logged in.")]
    [InlineData("OAuth token has expired")]
    [InlineData("Credit balance is too low")]
    public async Task ASignedOutCli_IsAuthentication(string stderr)
    {
        var runner = new FakeRunner(new ClaudeCliOutcome(1, "", stderr));
        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => Model(runner).CompleteAsync(Prompt(), CancellationToken.None));

        Assert.Equal(ToneDesignFault.Authentication, ex.Fault);
        Assert.Contains("log in", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A ZERO exit is never an auth failure however the reply is worded —
    /// a tone whose rationale says "sign in" must not fail the design.</summary>
    [Fact]
    public void ASuccessfulRunIsNeverAnAuthFailure()
        => Assert.False(ClaudeCliToneModel.IsAuthenticationFailure(
            new ClaudeCliOutcome(0, "please log in", "")));

    [Fact]
    public async Task ATimeout_IsNetwork()
    {
        var runner = new FakeRunner(new ClaudeCliOutcome(-1, "", "", TimedOut: true));
        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => Model(runner).CompleteAsync(Prompt(), CancellationToken.None));
        Assert.Equal(ToneDesignFault.Network, ex.Fault);
    }

    [Fact]
    public async Task Cancellation_ReachesTheRunner_AndPropagates()
    {
        using var cts = new CancellationTokenSource();
        var runner = new FakeRunner { OnRun = _ => cts.Cancel() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Model(runner).CompleteAsync(Prompt(), cts.Token));
        Assert.True(runner.SawCancellation);
    }

    // ---- the probe -------------------------------------------------------------

    [Fact]
    public async Task TheProbe_RunsVersionOnly_AndReportsIt()
    {
        var runner = new FakeRunner(new ClaudeCliOutcome(0, "2.1.269 (Claude Code)\n", ""));
        var probe = await Model(runner).ProbeAsync(CancellationToken.None);

        Assert.True(probe.Ok);
        Assert.Contains("2.1.269", probe.Message, StringComparison.Ordinal);
        Assert.Contains(FakeExe, probe.Message, StringComparison.Ordinal);
        // Free: no design request, so pressing Test never spends subscription usage.
        Assert.Equal(["--version"], runner.LastRequest!.Arguments);
    }

    [Fact]
    public async Task TheProbe_FailsWhenTheBinaryWillNotRun()
    {
        var runner = new FakeRunner(new ClaudeCliOutcome(9009, "", "not recognized"));
        var probe = await Model(runner).ProbeAsync(CancellationToken.None);
        Assert.False(probe.Ok);
        Assert.Null(probe.ModelId);
    }

    // ---- the locator -----------------------------------------------------------

    [Fact]
    public void AnExplicitPathWins_AndAnExplicitPathThatDoesNotExistIsNotAFallback()
    {
        var locator = new ClaudeCliLocator();
        var real = Path.Combine(Path.GetTempPath(), $"dc-claude-{Guid.NewGuid():N}.exe");
        File.WriteAllText(real, "");
        try
        {
            Assert.Equal(Path.GetFullPath(real), locator.Locate(real));
            // Silently searching PATH here is how a user debugs the wrong binary.
            Assert.Null(locator.Locate(Path.Combine(Path.GetTempPath(), "no-such-claude-xyz.exe")));
        }
        finally
        {
            File.Delete(real);
        }
    }

    // ---- the factory -----------------------------------------------------------

    [Fact]
    public void TheFactory_MintsTheCliProvider_WhenTheBinaryIsFound()
    {
        var factory = new ToneDesignModelFactory(
            new InMemoryAiSecretStore(), cliLocator: new StubLocator(FakeExe), cliRunner: new FakeRunner());

        Assert.True(factory.Supports(AiProviderKind.ClaudeCli));
        var model = factory.Create(new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-opus-5", null));
        Assert.Equal(AiProviderKind.ClaudeCli, model.Kind);
    }

    [Fact]
    public void TheFactory_SaysNotConfigured_WhenThereIsNoBinary()
    {
        var factory = new ToneDesignModelFactory(
            new InMemoryAiSecretStore(), cliLocator: new StubLocator(null), cliRunner: new FakeRunner());

        Assert.False(factory.Supports(AiProviderKind.ClaudeCli));
        var ex = Assert.Throws<ToneDesignException>(
            () => factory.Create(new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-opus-5", null)));

        Assert.Equal(ToneDesignFault.NotConfigured, ex.Fault);
        Assert.Contains("Install Claude Code", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFactory_NamesABadExplicitPath_RatherThanFallingBackToPath()
    {
        var factory = new ToneDesignModelFactory(
            new InMemoryAiSecretStore(), cliLocator: new StubLocator(null), cliRunner: new FakeRunner());
        var ex = Assert.Throws<ToneDesignException>(() => factory.Create(
            new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-opus-5", @"D:\nope\claude.exe")));

        Assert.Equal(ToneDesignFault.NotConfigured, ex.Fault);
        Assert.Contains(@"D:\nope\claude.exe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFactory_ReadsNoSecretForTheCliProvider()
    {
        var secrets = new ThrowingSecretStore();
        var factory = new ToneDesignModelFactory(
            secrets, cliLocator: new StubLocator(FakeExe), cliRunner: new FakeRunner());

        // ThrowingSecretStore fails the test if the factory asks it for a key: this
        // provider has none, and reaching for one would be the bug that makes a
        // user's Anthropic key bill for a subscription request.
        factory.Create(new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-opus-5", null));
    }

    // ---- the enum and its persistence ------------------------------------------

    /// <summary>
    ///     The ordinal is persisted as an INT in <c>AppConfig.Ai</c>, so it is
    ///     pinned: renumbering silently repoints every saved configuration at a
    ///     different provider. <c>BuiltIn = 3</c> stays reserved and unused.
    /// </summary>
    [Fact]
    public void TheProviderOrdinals_ArePinned()
    {
        Assert.Equal(0, (int)AiProviderKind.None);
        Assert.Equal(1, (int)AiProviderKind.Claude);
        Assert.Equal(2, (int)AiProviderKind.OpenAiCompatible);
        Assert.Equal(3, (int)AiProviderKind.BuiltIn);
        Assert.Equal(4, (int)AiProviderKind.ClaudeCli);
        Assert.Equal(5, (int)AiProviderKind.CodexCli);
    }

    [Fact]
    public void TheCliProvider_RoundTripsThroughAppConfig()
    {
        var settings = new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-opus-5", @"C:\bin\claude.exe");
        var json = JsonSerializer.Serialize(new AppConfig { Ai = settings });

        // As an int, which is the whole reason the ordinals above are pinned.
        Assert.Contains("\"Kind\":4", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<AppConfig>(json)!;
        Assert.Equal(settings, back.Ai);
        Assert.True(back.Ai.IsEnabled);
    }

    // ---- fakes -----------------------------------------------------------------

    private sealed class FakeRunner : IClaudeCliRunner
    {
        private readonly ClaudeCliOutcome _outcome;

        public FakeRunner(ClaudeCliOutcome? outcome = null)
            => _outcome = outcome ?? new ClaudeCliOutcome(0, "{}", "");

        public Action<ClaudeCliRequest>? OnRun { get; init; }
        public ClaudeCliRequest? LastRequest { get; private set; }
        public bool SawCancellation { get; private set; }

        public Task<ClaudeCliOutcome> RunAsync(ClaudeCliRequest request, CancellationToken ct)
        {
            LastRequest = request;
            OnRun?.Invoke(request);
            if (ct.IsCancellationRequested)
            {
                SawCancellation = true;
                return Task.FromCanceled<ClaudeCliOutcome>(ct);
            }

            return Task.FromResult(_outcome);
        }
    }

    private sealed class StubLocator(string? path) : IClaudeCliLocator
    {
        public string? Locate(string? explicitPath) => path;
    }

    private sealed class ThrowingSecretStore : IAiSecretStore
    {
        public bool IsPersistent => true;
        public string? Read(AiProviderKind kind)
            => throw new InvalidOperationException($"the CLI provider must not read a secret (asked for {kind})");
        public void Write(AiProviderKind kind, string? secret)
            => throw new InvalidOperationException("the CLI provider must not write a secret");
    }
}
