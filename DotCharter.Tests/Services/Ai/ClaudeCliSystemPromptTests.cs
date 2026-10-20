using DotCharter.Tone.Ai;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     W9: the CLI provider's grounding goes in the SYSTEM prompt, in a file, so the
///     CLI's own cache breakpoint covers it across one-shot <c>--print</c> processes.
///
///     <para>
///         <b>The probe is the load-bearing half.</b> The CLI's option parser is not
///         strict — an unknown flag is accepted silently and the prompt it carries is
///         dropped on the floor — so a machine whose binary does not advertise
///         <c>--system-prompt-file</c> must keep the old stdin layout rather than
///         send a design into a void. Both layouts are pinned here, and so is the
///         decision between them.
///     </para>
/// </summary>
public sealed class ClaudeCliSystemPromptTests
{
    private const string FakeExe = @"C:\fake\claude.exe";

    private static ToneDesignPrompt Prompt()
        => ToneDesignPromptBuilder.Build(new ToneDesignRequest("a clean tone", [], null));

    private static ClaudeCliToneModel Model(IClaudeCliCapabilityProbe? probe = null)
        => new(
            new AiProviderSettings(AiProviderKind.ClaudeCli, "", null),
            FakeExe,
            new StubRunner(),
            workingDirectory: Path.GetTempPath(),
            capabilities: probe);

    // ---- the probe -------------------------------------------------------------

    /// <summary>
    ///     The exact shape 2.1.273 prints. The flag has NO option entry of its own on
    ///     that version — it appears only inside <c>--bare</c>'s description as
    ///     <c>--system-prompt[-file]</c> — and it demonstrably works, so a probe that
    ///     accepted only the literal spelling would leave the feature permanently off
    ///     on the machine it was written for. That is the case this asserts; do not
    ///     "tidy" the shorthand out.
    /// </summary>
    [Theory]
    [InlineData("  --system-prompt <prompt>   System prompt to use for the session", false)]
    [InlineData("Explicitly provide context via: --system-prompt[-file], --add-dir", true)]
    [InlineData("  --system-prompt-file <path>   Read the system prompt from a file", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheProbe_ReadsEitherSpellingOutOfTheHelp(string? help, bool expected)
        => Assert.Equal(expected, ClaudeCliCapabilityProbe.HelpAdvertisesSystemPromptFile(help));

    [Fact]
    public async Task TheProbe_RunsHelpOnce_AndCachesTheAnswer()
    {
        var runner = new StubRunner(new ClaudeCliOutcome(0, "--system-prompt[-file]", ""));
        var probe = new ClaudeCliCapabilityProbe(runner, workingDirectory: Path.GetTempPath());

        Assert.True(await probe.SupportsSystemPromptFileAsync(FakeExe, TestContext.Current.CancellationToken));
        Assert.True(await probe.SupportsSystemPromptFileAsync(FakeExe, TestContext.Current.CancellationToken));

        Assert.Equal(1, runner.Runs);
        Assert.Equal(["--help"], runner.LastRequest!.Arguments);
        // --help is free and local; nothing about it may spend a design request.
        Assert.Equal("", runner.LastRequest.StandardInput);
    }

    [Fact]
    public async Task AProbeThatCannotRun_AnswersFalse_RatherThanFailingTheDesign()
    {
        var probe = new ClaudeCliCapabilityProbe(
            new ThrowingRunner(new ToneDesignException(ToneDesignFault.NotConfigured, "no binary")),
            workingDirectory: Path.GetTempPath());

        Assert.False(await probe.SupportsSystemPromptFileAsync(FakeExe, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATimedOutProbe_AnswersFalse()
    {
        var probe = new ClaudeCliCapabilityProbe(
            new StubRunner(new ClaudeCliOutcome(-1, "--system-prompt-file", "", TimedOut: true)),
            workingDirectory: Path.GetTempPath());

        Assert.False(await probe.SupportsSystemPromptFileAsync(FakeExe, TestContext.Current.CancellationToken));
    }

    // ---- the file --------------------------------------------------------------

    /// <summary>The file carries the framing AND the grounding, because
    /// <c>--system-prompt-file</c> REPLACES the CLI's own coding-agent prompt exactly
    /// as the short <c>--system-prompt</c> did: a framing left behind on a flag that
    /// is no longer passed is a framing that is simply gone.</summary>
    [Fact]
    public void TheFile_CarriesTheFramingThenTheWholeGrounding()
    {
        var content = ClaudeCliSystemPrompt.Compose(Prompt());

        Assert.StartsWith(ClaudeCliToneModel.FramingPrompt, content, StringComparison.Ordinal);
        Assert.Contains("# Module catalogue", content, StringComparison.Ordinal);
        Assert.EndsWith(Prompt().System, content, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileName_IsKeyedOnTheContent()
    {
        var a = ClaudeCliSystemPrompt.FileNameFor("one grounding");
        var again = ClaudeCliSystemPrompt.FileNameFor("one grounding");
        var other = ClaudeCliSystemPrompt.FileNameFor("one grounding, plus a capture");

        Assert.Equal(a, again);
        Assert.NotEqual(a, other);
        Assert.StartsWith(ClaudeCliSystemPrompt.FileNamePrefix, a, StringComparison.Ordinal);
        Assert.EndsWith(".txt", a, StringComparison.Ordinal);
    }

    /// <summary>A changed catalogue writes a NEW file and the stale one goes: a temp
    /// folder that accumulates one 35 KB prompt per catalogue change forever is a
    /// leak, and a reused stale file is a grounding that no longer matches the
    /// build.</summary>
    [Fact]
    public void WritingSweepsTheStalePromptFiles_AndReusesAnIdenticalOne()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "dotcharter-cli-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var stale = Path.Combine(directory, ClaudeCliSystemPrompt.FileNamePrefix + "deadbeefdeadbeef.txt");
            File.WriteAllText(stale, "an older catalogue");

            ClaudeCliSystemPrompt.ResetSweepForTesting();
            var path = ClaudeCliSystemPrompt.TryWrite(directory, Prompt());

            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(stale), "the stale prompt file should have been swept");
            Assert.Equal(ClaudeCliSystemPrompt.Compose(Prompt()), File.ReadAllText(path!));

            // Same content ⇒ same path, and no rewrite a concurrent design could read
            // half of.
            var written = File.GetLastWriteTimeUtc(path!);
            Assert.Equal(path, ClaudeCliSystemPrompt.TryWrite(directory, Prompt()));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path!));
        }
        finally
        {
            BestEffortDelete.Directory(directory);
        }
    }

    [Fact]
    public void AWriteThatCannotHappen_IsNullRatherThanAThrow()
        => Assert.Null(ClaudeCliSystemPrompt.TryWrite("   ", Prompt()));

    // ---- the two layouts -------------------------------------------------------

    [Fact]
    public void TheFileLayout_SwapsTheFramingFlag_AndLeavesOnlyTheConversationOnStdin()
    {
        var prompt = Prompt();
        var args = Model().BuildArguments(prompt, @"C:\temp\dc-tone-system-0123456789abcdef.txt");

        Assert.Contains("--system-prompt-file", args);
        Assert.Contains(@"C:\temp\dc-tone-system-0123456789abcdef.txt", args);
        // Never both: the second spelling would replace the first and the framing —
        // which lives in the file's head — is the half that would win.
        Assert.DoesNotContain("--system-prompt", args);
        Assert.DoesNotContain(ClaudeCliToneModel.FramingPrompt, args);

        var stdin = ClaudeCliToneModel.BuildStandardInput(prompt, systemPromptOnFile: true);
        Assert.DoesNotContain("# Module catalogue", stdin, StringComparison.Ordinal);
        Assert.StartsWith("## Request", stdin, StringComparison.Ordinal);
        Assert.Contains("a clean tone", stdin, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStdinLayout_IsUnchanged_AndCarriesTheGroundingItself()
    {
        var prompt = Prompt();
        var args = Model().BuildArguments(prompt);

        Assert.Contains("--system-prompt", args);
        Assert.Contains(ClaudeCliToneModel.FramingPrompt, args);
        Assert.DoesNotContain("--system-prompt-file", args);

        var stdin = ClaudeCliToneModel.BuildStandardInput(prompt);
        Assert.StartsWith(prompt.System, stdin, StringComparison.Ordinal);
    }

    /// <summary>Whichever layout is chosen, the command line stays far under
    /// Windows' 32,767-character cap — a path is a few dozen characters and the
    /// grounding is ~35,000.</summary>
    [Fact]
    public void BothLayouts_StayFarUnderTheWindowsCommandLineCap()
    {
        var prompt = Prompt();
        var model = Model();
        var longPath = Path.Combine(Path.GetTempPath(), "DotCharter", "ai-cli", "prompts",
            ClaudeCliSystemPrompt.FileNameFor(ClaudeCliSystemPrompt.Compose(prompt)));

        foreach (var args in new[] { model.BuildArguments(prompt), model.BuildArguments(prompt, longPath) })
        {
            // +3 per argument for the space and the quotes CreateProcess needs.
            var length = FakeExe.Length + args.Sum(a => a.Length + 3);
            Assert.True(length < 16_000, $"command line would be {length} characters");
        }
    }

    /// <summary>The decision, end to end over a fake runner: an advertising CLI gets
    /// the file, a silent one gets stdin, and neither fails.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheProbesAnswer_DecidesWhichLayoutTheChildIsRun_With(bool supported)
    {
        const string envelope =
            """
            {"is_error":false,
             "structured_output":{"name":"T","rationale":"r","confidence":0.9,
               "modules":[{"type":"amp","params":[],"bypassed":false,"capture":""}]},
             "usage":{"input_tokens":2,"cache_read_input_tokens":0,
                      "cache_creation_input_tokens":10,"output_tokens":5}}
            """;

        var runner = new StubRunner(new ClaudeCliOutcome(0, envelope, ""));
        var model = new ClaudeCliToneModel(
            new AiProviderSettings(AiProviderKind.ClaudeCli, "", null),
            FakeExe, runner,
            workingDirectory: Path.Combine(
                Path.GetTempPath(), "dotcharter-cli-layout-" + Guid.NewGuid().ToString("N")),
            capabilities: new StubProbe(supported));

        await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        var request = runner.LastRequest!;
        Assert.Equal(supported, request.Arguments.Contains("--system-prompt-file"));
        Assert.Equal(!supported, request.StandardInput.Contains("# Module catalogue", StringComparison.Ordinal));
    }

    private sealed class StubProbe(bool supported) : IClaudeCliCapabilityProbe
    {
        public Task<bool> SupportsSystemPromptFileAsync(string executablePath, CancellationToken ct)
            => Task.FromResult(supported);
    }

    private sealed class StubRunner(ClaudeCliOutcome? outcome = null) : IClaudeCliRunner
    {
        private readonly ClaudeCliOutcome _outcome = outcome ?? new ClaudeCliOutcome(0, "{}", "");

        public int Runs { get; private set; }
        public ClaudeCliRequest? LastRequest { get; private set; }

        public Task<ClaudeCliOutcome> RunAsync(ClaudeCliRequest request, CancellationToken ct)
        {
            Runs++;
            LastRequest = request;
            return Task.FromResult(_outcome);
        }
    }

    private sealed class ThrowingRunner(Exception ex) : IClaudeCliRunner
    {
        public Task<ClaudeCliOutcome> RunAsync(ClaudeCliRequest request, CancellationToken ct)
            => Task.FromException<ClaudeCliOutcome>(ex);
    }
}
