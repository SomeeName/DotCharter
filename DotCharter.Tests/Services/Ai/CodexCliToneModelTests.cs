using DotCharter.Tone.Ai;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The Codex CLI provider against a scripted runner. The argument list is the
///     contract with <c>codex exec</c> (0.156.1) and is asserted exactly; the reply
///     comes back through the <c>-o</c> FILE and the usage through the JSONL
///     events, which is how the real CLI answered on 2026-09-26.
/// </summary>
public sealed class CodexCliToneModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codex-cli-" + Guid.NewGuid().ToString("N"));

    public CodexCliToneModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static ToneDesignPrompt Prompt() => new(
        "GROUNDING: the catalogue.",
        [new ToneDesignTurn(ToneDesignRole.User, "a crunchy tone")],
        """{"type":"object"}""",
        "tone");

    // The shape `codex exec --json` printed for a real run.
    private const string Events = """
        {"type":"thread.started","thread_id":"01a0"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"type":"agent_message"}}
        {"type":"turn.completed","usage":{"input_tokens":16086,"cached_input_tokens":0,"output_tokens":16,"reasoning_output_tokens":4}}
        """;

    private CodexCliToneModel Model(ScriptedRunner runner, string modelId = "") =>
        new(new AiProviderSettings(AiProviderKind.CodexCli, modelId, null), @"C:\bin\codex.exe", runner,
            workingDirectory: _dir);

    [Fact]
    public void TheArguments_AreExactlyTheHeadlessContract()
    {
        var model = Model(new ScriptedRunner(), "gpt-5.5");

        var args = model.BuildArguments(@"C:\t\schema.json", @"C:\t\reply.json");

        Assert.Equal(
        [
            "exec", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules",
            "--sandbox", "read-only", "--color", "never", "--json",
            "--output-schema", @"C:\t\schema.json", "-o", @"C:\t\reply.json",
            // research is on by default: Codex's live web search
            "-c", "web_search=\"live\"",
            "-m", "gpt-5.5", "-"
        ], args);
    }

    [Fact]
    public void AnEmptyModelId_AsksForGpt6Luna()
    {
        var args = Model(new ScriptedRunner()).BuildArguments("s", "r");

        var flag = Array.IndexOf(args.ToArray(), "-m");
        Assert.True(flag >= 0, "an empty Codex model id must still pass -m");
        Assert.Equal("gpt-6-luna", args[flag + 1]);
        Assert.True(new AiProviderSettings(AiProviderKind.CodexCli, "", null).IsEnabled,
            "an empty Codex model id is the default model, not 'not configured'");
    }

    [Fact]
    public async Task TheReply_IsReadFromTheOutputFile_AndUsageFromTheEvents()
    {
        var runner = new ScriptedRunner(new ClaudeCliOutcome(0, Events, ""), reply: """{"modules":[]}""");

        var reply = await Model(runner).CompleteAsync(Prompt(), CancellationToken.None);

        Assert.Equal("""{"modules":[]}""", reply.Json);
        Assert.Equal(16086, reply.Usage.InputTokens);
        Assert.Equal(20, reply.Usage.OutputTokens);
        // The grounding and the request travel on stdin, never on the command line.
        Assert.Contains("GROUNDING: the catalogue.", runner.LastRequest!.StandardInput);
        Assert.Contains("## Request\na crunchy tone", runner.LastRequest.StandardInput);
        Assert.Equal("-", runner.LastRequest.Arguments[^1]);
        // …and the two scratch files are gone afterwards.
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ASignedOutCli_IsAnAuthenticationFault()
    {
        var runner = new ScriptedRunner(new ClaudeCliOutcome(1, "", "Error: not logged in. Run `codex login`."));

        var ex = await Assert.ThrowsAsync<ToneDesignException>(() => Model(runner).CompleteAsync(Prompt(), CancellationToken.None));

        Assert.Equal(ToneDesignFault.Authentication, ex.Fault);
    }

    [Fact]
    public async Task AFailedTurn_IsReportedWithTheCliMessage()
    {
        var failed = """{"type":"turn.failed","error":{"message":"model not available"}}""";
        var runner = new ScriptedRunner(new ClaudeCliOutcome(0, failed, ""));

        var ex = await Assert.ThrowsAsync<ToneDesignException>(() => Model(runner).CompleteAsync(Prompt(), CancellationToken.None));

        Assert.Equal(ToneDesignFault.Unknown, ex.Fault);
        Assert.Contains("model not available", ex.Detail);
    }

    [Fact]
    public async Task NoReplyFile_IsInvalidOutput_NotASilentEmptyTone()
    {
        var runner = new ScriptedRunner(new ClaudeCliOutcome(0, Events, ""), reply: null);

        var ex = await Assert.ThrowsAsync<ToneDesignException>(() => Model(runner).CompleteAsync(Prompt(), CancellationToken.None));

        Assert.Equal(ToneDesignFault.InvalidOutput, ex.Fault);
    }

    [Fact]
    public async Task TheProbe_RunsVersion_AndSpendsNothing()
    {
        var runner = new ScriptedRunner(new ClaudeCliOutcome(0, "codex-cli 0.156.1\n", ""));

        var probe = await Model(runner).ProbeAsync(CancellationToken.None);

        Assert.True(probe.Ok);
        Assert.Contains("codex-cli 0.156.1", probe.Message);
        Assert.Equal(["--version"], runner.LastRequest!.Arguments);
    }

    [Fact]
    public void TheFactory_MintsCodex_WhenTheBinaryIsFound_AndSaysSoWhenNot()
    {
        var found = new ToneDesignModelFactory(new InMemoryAiSecretStore(),
            codexLocator: new FixedLocator(@"C:\bin\codex.exe"));
        var missing = new ToneDesignModelFactory(new InMemoryAiSecretStore(), codexLocator: new FixedLocator(null));
        var settings = new AiProviderSettings(AiProviderKind.CodexCli, "", null);

        Assert.True(found.Supports(AiProviderKind.CodexCli));
        Assert.IsType<CodexCliToneModel>(found.Create(settings));
        Assert.False(missing.Supports(AiProviderKind.CodexCli));
        Assert.Equal(ToneDesignFault.NotConfigured,
            Assert.Throws<ToneDesignException>(() => missing.Create(settings)).Fault);
    }

    private sealed class FixedLocator(string? path) : IClaudeCliLocator
    {
        public string? Locate(string? explicitPath) => path;
    }

    /// <summary>Answers with a fixed outcome, and — like the real CLI — writes the
    /// reply into the file named after <c>-o</c>.</summary>
    private sealed class ScriptedRunner(ClaudeCliOutcome? outcome = null, string? reply = null) : IClaudeCliRunner
    {
        public ClaudeCliRequest? LastRequest { get; private set; }

        public Task<ClaudeCliOutcome> RunAsync(ClaudeCliRequest request, CancellationToken ct)
        {
            LastRequest = request;
            var args = request.Arguments.ToList();
            var at = args.IndexOf("-o");
            if (reply is not null && at >= 0) File.WriteAllText(args[at + 1], reply);
            return Task.FromResult(outcome ?? new ClaudeCliOutcome(0, "", ""));
        }
    }
}
