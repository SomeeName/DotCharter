using System.Net;
using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Tests.Services.Update;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     Settings ▸ AI ▸ Reasoning (#603) as each provider puts it on the wire — pure builders and a
///     fake transport, no binary, no network, no model. Every provider carries the level EXACTLY
///     when one is set and the model can take it, and carries nothing on Default (the behaviour
///     every build before the setting had). The Claude API's wire body is pinned in
///     <see cref="ClaudeToneModelTests" />, beside the stream fake it needs.
/// </summary>
public sealed class AiReasoningEffortTests
{
    private const string ToneJson =
        """{"name":"Test Tone","rationale":"because","confidence":0.9,"modules":[{"type":"amp","params":{"gain":0.5},"bypassed":false}]}""";

    public static TheoryData<AiReasoningEffort, string?> Levels => new()
    {
        { AiReasoningEffort.Default, null },
        { AiReasoningEffort.Low, "low" },
        { AiReasoningEffort.Medium, "medium" },
        { AiReasoningEffort.High, "high" }
    };

    private static ToneDesignPrompt Prompt()
        => ToneDesignPromptBuilder.Build(new ToneDesignRequest("a clean tone", [], null));

    // ---- the one rule ------------------------------------------------------------

    [Theory]
    [InlineData(AiProviderKind.Claude, "claude-opus-5-5", AiReasoningUnsupported.None)]
    [InlineData(AiProviderKind.Claude, "claude-opus-4-5", AiReasoningUnsupported.None)]
    [InlineData(AiProviderKind.Claude, "claude-sonnet-4-6", AiReasoningUnsupported.None)]
    [InlineData(AiProviderKind.Claude, "claude-haiku-4-5", AiReasoningUnsupported.ClaudeModelTooOld)]
    [InlineData(AiProviderKind.Claude, "claude-sonnet-4-5", AiReasoningUnsupported.ClaudeModelTooOld)]
    [InlineData(AiProviderKind.ClaudeCli, "claude-sonnet-4-20250514", AiReasoningUnsupported.ClaudeModelTooOld)]
    [InlineData(AiProviderKind.ClaudeCli, "claude-3-7-sonnet-latest", AiReasoningUnsupported.ClaudeModelTooOld)]
    [InlineData(AiProviderKind.ClaudeCli, "opus", AiReasoningUnsupported.None)]
    [InlineData(AiProviderKind.CodexCli, "", AiReasoningUnsupported.None)]
    [InlineData(AiProviderKind.OpenAiCompatible, "claude-haiku-4-5", AiReasoningUnsupported.None)]
    [InlineData(AiProviderKind.None, "", AiReasoningUnsupported.NoProvider)]
    public void Support_IsDecidedByTheProviderAndTheModelItWillCall(AiProviderKind kind, string model, AiReasoningUnsupported expected)
        => Assert.Equal(expected, AiReasoning.Support(kind, model));

    [Fact]
    public void AModelThatCannotTakeALevel_IsSentNone_WhateverWasChosen()
    {
        Assert.Null(AiReasoning.WireValue(AiReasoningEffort.High, AiProviderKind.Claude, "claude-haiku-4-5"));
        Assert.Equal("high", AiReasoning.WireValue(AiReasoningEffort.High, AiProviderKind.Claude, "claude-haiku-5-5"));
    }

    // ---- Claude Code CLI: --effort <level> -----------------------------------------

    [Theory]
    [MemberData(nameof(Levels))]
    public void ClaudeCli_PassesEffort_ExactlyWhenSet_RightAfterTheModel(AiReasoningEffort effort, string? expected)
    {
        var model = new ClaudeCliToneModel(
            new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-sonnet-5-5", null, Effort: effort),
            @"C:\fake\claude.exe", new NullRunner(), workingDirectory: Path.GetTempPath());

        var args = model.BuildArguments(Prompt()).ToList();

        var at = args.IndexOf("--effort");
        if (expected is null)
        {
            Assert.Equal(-1, at);
            return;
        }

        Assert.Equal(args.IndexOf("--model") + 2, at);
        Assert.Equal(expected, args[at + 1]);
        Assert.Single(args, a => a == "--effort");
    }

    [Fact]
    public void ClaudeCli_OnAModelOlderThanEffort_PassesNone()
    {
        var model = new ClaudeCliToneModel(
            new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-haiku-4-5", null, Effort: AiReasoningEffort.High),
            @"C:\fake\claude.exe", new NullRunner(), workingDirectory: Path.GetTempPath());

        Assert.DoesNotContain("--effort", model.BuildArguments(Prompt()));
    }

    // ---- Codex CLI: -c model_reasoning_effort="<level>" ------------------------------

    [Theory]
    [MemberData(nameof(Levels))]
    public void CodexCli_OverridesTheConfigKey_ExactlyWhenSet(AiReasoningEffort effort, string? expected)
    {
        var model = new CodexCliToneModel(
            new AiProviderSettings(AiProviderKind.CodexCli, "gpt-6-sol", null, Effort: effort),
            @"C:\bin\codex.exe", new NullRunner(), workingDirectory: Path.GetTempPath());

        var args = model.BuildArguments("s", "r").ToList();

        var overrides = args.Where(a => a.StartsWith("model_reasoning_effort", StringComparison.Ordinal)).ToList();
        if (expected is null)
        {
            Assert.Empty(overrides);
            return;
        }

        var value = Assert.Single(overrides);
        Assert.Equal($"model_reasoning_effort=\"{expected}\"", value);
        Assert.Equal("-c", args[args.IndexOf(value) - 1]);
        // The user's config.toml is still ignored: the override is the ONLY source of the level.
        Assert.Contains("--ignore-user-config", args);
    }

    // ---- OpenAI-compatible: reasoning_effort, OpenRouter's reasoning.effort ------------

    [Theory]
    [MemberData(nameof(Levels))]
    public void OpenAiCompatible_SendsReasoningEffort_ExactlyWhenSet(AiReasoningEffort effort, string? expected)
    {
        using var model = new OpenAiCompatibleToneModel(
            new AiProviderSettings(AiProviderKind.OpenAiCompatible, "qwen3", AiProviderPresets.OllamaBaseUrl, Effort: effort));

        using var body = JsonDocument.Parse(model.BuildRequestBody(Prompt()));

        var has = body.RootElement.TryGetProperty("reasoning_effort", out var value);
        Assert.Equal(expected is not null, has);
        if (expected is not null) Assert.Equal(expected, value.GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public void OpenRouter_GetsItsOwnReasoningObject_NotTheTopLevelField()
    {
        using var model = new OpenAiCompatibleToneModel(new AiProviderSettings(
            AiProviderKind.OpenAiCompatible, "openai/gpt-6", "https://openrouter.ai/api/v1", Effort: AiReasoningEffort.Medium));

        using var body = JsonDocument.Parse(model.BuildRequestBody(Prompt()));

        Assert.Equal("medium", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    /// <summary>A server or model without reasoning answers 400 naming the field; the design is
    /// asked for once more WITHOUT it rather than failing on a setting it could ignore.</summary>
    [Fact]
    public async Task OpenAiCompatible_ARefusedEffort_IsRetriedOnceWithoutIt()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"error":{"message":"Unrecognized request argument supplied: reasoning_effort"}}""",
            HttpStatusCode.BadRequest);
        handler.EnqueueJson(Reply(ToneJson));
        using var model = new OpenAiCompatibleToneModel(new AiProviderSettings(
            AiProviderKind.OpenAiCompatible, "gpt-4o", AiProviderPresets.LmStudioBaseUrl, Effort: AiReasoningEffort.High), handler: handler);

        var reply = await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        Assert.Equal(ToneJson, reply.Json);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("\"reasoning_effort\":\"high\"", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning", handler.RequestBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiCompatible_OnDefault_AnUnrelated400IsNotRetriedAsAnEffortRefusal()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"error":{"message":"reasoning is not available on this plan"}}""", HttpStatusCode.BadRequest);
        using var model = new OpenAiCompatibleToneModel(new AiProviderSettings(
            AiProviderKind.OpenAiCompatible, "m", AiProviderPresets.LmStudioBaseUrl), handler: handler);

        await Assert.ThrowsAsync<ToneDesignException>(() => model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken));
        Assert.Single(handler.RequestBodies);
    }

    private static string Reply(string content) =>
        """{"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":""" +
        JsonSerializer.Serialize(content) +
        """},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";

    private sealed class NullRunner : IClaudeCliRunner
    {
        public Task<ClaudeCliOutcome> RunAsync(ClaudeCliRequest request, CancellationToken ct)
            => Task.FromResult(new ClaudeCliOutcome(0, "", ""));
    }
}
