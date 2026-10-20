using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The ONE end-to-end case that really runs the installed <c>claude</c> binary
///     and really spends the signed-in user's subscription usage.
///
///     <para>
///         <b>Opt-in, and it must stay opt-in.</b> It is gated on
///         <c>DOTCHARTER_AI_CLI_SMOKE=1</c> and SKIPS otherwise, so it is never part
///         of the default gate and never part of CI. Running it costs a design
///         request against whoever is logged in on the machine — the exact thing
///         this repo's "a research harness is opt-in or it is part of the gate" rule
///         exists to prevent, after a hard-coded corpus path made an eval run on
///         every <c>dotnet test</c> for ten minutes on one machine.
///     </para>
///     <para>
///         It asserts the pipeline end to end: a real binary, a real login, a real
///         model, and a reply that survives <see cref="ToneDraftValidator" /> — so
///         it catches the failures a fake runner structurally cannot, above all a
///         CLI flag that stopped meaning what it meant.
///     </para>
/// </summary>
public sealed class ClaudeCliSmokeTests
{
    private const string Gate = "DOTCHARTER_AI_CLI_SMOKE";

    [Fact]
    public async Task ARealDesignRequest_ComesBackAsAValidatedChain()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(Gate) == "1",
            $"set {Gate}=1 to spend one real Claude Code request");

        var path = new ClaudeCliLocator().Locate(null);
        Assert.SkipWhen(path is null, "no claude binary on PATH");

        var settings = new AiProviderSettings(
            AiProviderKind.ClaudeCli, ClaudeCliToneModel.DefaultModelId, null);
        var model = new ClaudeCliToneModel(settings, path!, new ClaudeCliRunner());

        var probe = await model.ProbeAsync(TestContext.Current.CancellationToken);
        Assert.True(probe.Ok, probe.Message);

        var prompt = ToneDesignPromptBuilder.Build(new ToneDesignRequest(
            "Master of Puppets clean tone", [], null,
            SongTitle: "Master of Puppets", Artist: "Metallica"));

        var reply = await model.CompleteAsync(prompt, TestContext.Current.CancellationToken);

        var validated = ToneDraftValidator.Validate(reply.Json);
        Assert.NotNull(validated.Preset);
        Assert.NotEmpty(validated.Preset!.Rig.Modules);

        // Printed rather than asserted: the numbers are the point of running it and
        // an assertion on them would be an assertion on a model's taste.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{probe.Message}\nusage: {reply.Usage.InputTokens} in / {reply.Usage.OutputTokens} out\n" +
            $"chain: {string.Join(" -> ", validated.Preset.Rig.Modules.Select(m => m.ModuleTypeId))}\n" +
            $"warnings: {(validated.Warnings.Count == 0 ? "(none)" : string.Join("; ", validated.Warnings))}\n" +
            $"json: {reply.Json}");
    }

    /// <summary>
    ///     W9's whole claim, measured: two designs back to back, and the SECOND reads
    ///     the grounding out of the provider's prompt cache.
    ///
    ///     <para>
    ///         The two requests deliberately carry DIFFERENT text. That is the case
    ///         the old stdin layout cannot serve — the CLI's cache breakpoint sits on
    ///         the piped user turn, so a changed request invalidates the grounding
    ///         with it — and it is every real second design.
    ///     </para>
    ///     <para>
    ///         It spends TWO real requests against whoever is signed in, so it is
    ///         behind the same gate as the case above.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TwoDesignsInARow_ReadTheGroundingFromTheProvidersCache()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(Gate) == "1",
            $"set {Gate}=1 to spend two real Claude Code requests");

        var path = new ClaudeCliLocator().Locate(null);
        Assert.SkipWhen(path is null, "no claude binary on PATH");

        var runner = new RecordingRunner();
        var probe = new ClaudeCliCapabilityProbe(runner, workingDirectory: Path.GetTempPath());
        Assert.SkipUnless(
            await probe.SupportsSystemPromptFileAsync(path!, TestContext.Current.CancellationToken),
            "this claude does not advertise --system-prompt-file; the cached layout is not in use");

        var settings = new AiProviderSettings(
            AiProviderKind.ClaudeCli, ClaudeCliToneModel.DefaultModelId, null);
        var model = new ClaudeCliToneModel(settings, path!, runner, capabilities: probe);

        var usages = new List<(int Fresh, int CacheRead, int CacheCreate)>();
        foreach (var description in new[] { "a glassy blackface clean", "a scooped thrash rhythm tone" })
        {
            var prompt = ToneDesignPromptBuilder.Build(new ToneDesignRequest(description, [], null));
            var reply = await model.CompleteAsync(prompt, TestContext.Current.CancellationToken);
            Assert.NotNull(ToneDraftValidator.Validate(reply.Json).Preset);
            usages.Add(ReadUsage(runner.LastStandardOutput));
        }

        foreach (var (fresh, read, create) in usages)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"usage: input_tokens={fresh} cache_read_input_tokens={read} cache_creation_input_tokens={create}");
        }

        Assert.True(usages[1].CacheRead > 0,
            "the second design read nothing from the prompt cache — the grounding is not in the system block");
    }

    private static (int Fresh, int CacheRead, int CacheCreate) ReadUsage(string stdout)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            ToneDraftJson.ExtractJsonObject(stdout) ?? "{}");
        var usage = doc.RootElement.GetProperty("usage");
        return (
            usage.GetProperty("input_tokens").GetInt32(),
            usage.GetProperty("cache_read_input_tokens").GetInt32(),
            usage.GetProperty("cache_creation_input_tokens").GetInt32());
    }

    /// <summary>The real runner, keeping the raw envelope so the test can read the
    /// cache counters the provider folds into one number.</summary>
    private sealed class RecordingRunner : IClaudeCliRunner
    {
        private readonly ClaudeCliRunner _inner = new();

        public string LastStandardOutput { get; private set; } = "";

        public async Task<ClaudeCliOutcome> RunAsync(ClaudeCliRequest request, CancellationToken ct)
        {
            var outcome = await _inner.RunAsync(request, ct);
            if (request.Arguments.Contains("--print")) LastStandardOutput = outcome.StandardOutput;
            return outcome;
        }
    }
}
