using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The Claude provider. The SDK client takes a custom <see cref="HttpClient" />
///     (<c>AnthropicClient.HttpClient</c>), so these drive the real SDK over a
///     scripted handler and assert BOTH halves: the request body the SDK put on the
///     wire, and what the provider made of a canned streamed response.
/// </summary>
public sealed class ClaudeToneModelTests
{
    private const string ToneJson =
        """{"name":"Test Tone","rationale":"because","confidence":0.9,"modules":[{"type":"amp","params":{"gain":0.5},"bypassed":false}]}""";

    private static ToneDesignPrompt Prompt()
        => ToneDesignPromptBuilder.Build(new ToneDesignRequest("a clean tone", [], null));

    private static AiProviderSettings Settings(string model = "")
        => new(AiProviderKind.Claude, model, null);

    // ---- the request the SDK builds -------------------------------------------

    [Fact]
    public void TheRequest_CarriesTheModel_MaxTokens_TheSchemaAndTheCacheableSystemPrompt()
    {
        var model = new ClaudeToneModel(Settings(), "sk-ant-test");
        var parameters = model.BuildParams(Prompt());

        Assert.Equal(ClaudeToneModel.DefaultModelId, parameters.Model.Raw());
        Assert.Equal(ClaudeToneModel.MaxTokens, parameters.MaxTokens);

        // Adaptive thinking is the DEFAULT on Opus 5 and an explicit budget is a
        // 400 there, so the parameter must be absent. (Temperature's absence is
        // asserted on the WIRE below — the property itself is [Obsolete].)
        Assert.Null(parameters.Thinking);

        var format = parameters.OutputConfig!.Format!;
        Assert.True(format.Schema.ContainsKey("properties"));
        Assert.True(format.Schema.ContainsKey("required"));

        var systemBlocks = Assert.IsAssignableFrom<IEnumerable<TextBlockParam>>(parameters.System!.Value);
        var block = Assert.Single(systemBlocks);
        Assert.Contains("# Module catalogue", block.Text, StringComparison.Ordinal);
        Assert.NotNull(block.CacheControl);

        // The ONE-HOUR tier, not the 5-minute default. A tone session is minutes
        // between turns, and a 5m MISS costs a 1.25x cache write with no read
        // against it — ~16% MORE than not caching at all. Two requests inside the
        // hour already pay for the 2x write.
        // https://platform.claude.com/docs/en/build-with-claude/prompt-caching
        Assert.Equal("1h", block.CacheControl!.Ttl?.Raw());

        var message = Assert.Single(parameters.Messages);
        Assert.Equal("user", message.Role.Raw());
    }

    [Fact]
    public void TheSettingsModelId_OverridesTheDefault()
    {
        var model = new ClaudeToneModel(Settings("claude-sonnet-5"), "sk-ant-test");
        Assert.Equal("claude-sonnet-5", model.BuildParams(Prompt()).Model.Raw());
    }

    [Fact]
    public void AFollowUpsHistory_BecomesAlternatingRoles()
    {
        var model = new ClaudeToneModel(Settings(), "sk-ant-test");
        var prompt = ToneDesignPromptBuilder.Build(new ToneDesignRequest(
            "less gain",
            [
                new ToneDesignTurn(ToneDesignRole.User, "plexi please"),
                new ToneDesignTurn(ToneDesignRole.Assistant, "here you go")
            ],
            null));

        var parameters = model.BuildParams(prompt);

        Assert.Equal(
            new[] { "user", "assistant", "user" },
            parameters.Messages.Select(m => m.Role.Raw()));
    }

    [Fact]
    public void AMissingApiKey_IsRejectedAtConstruction()
        => Assert.ThrowsAny<ArgumentException>(() => new ClaudeToneModel(Settings(), "  "));

    // ---- what it makes of a streamed reply ------------------------------------

    [Fact]
    public async Task AStreamedReply_IsAccumulatedWithItsUsage()
    {
        var handler = new SseHandler(Stream(ToneJson, "end_turn", 9012, 214));
        var model = new ClaudeToneModel(Settings(), "sk-ant-test", handler: handler);

        var reply = await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        Assert.Equal(ToneJson, reply.Json);
        Assert.Equal(ClaudeToneModel.DefaultModelId, reply.Usage.ModelId);
        Assert.Equal(9012, reply.Usage.InputTokens);
        Assert.Equal(214, reply.Usage.OutputTokens);
    }

    [Fact]
    public async Task TheBodyOnTheWire_NamesTheModel_TheSchemaAndTheSystemPrompt()
    {
        var handler = new SseHandler(Stream(ToneJson, "end_turn", 1, 1));
        var model = new ClaudeToneModel(Settings(), "sk-ant-test", handler: handler);

        await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var root = body.RootElement;

        Assert.Equal(ClaudeToneModel.DefaultModelId, root.GetProperty("model").GetString());
        Assert.Equal(ClaudeToneModel.MaxTokens, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("temperature", out _));

        var format = root.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("schema").TryGetProperty("properties", out _));

        var system = root.GetProperty("system")[0];
        Assert.Contains("# Module catalogue", system.GetProperty("text").GetString()!, StringComparison.Ordinal);
        Assert.Equal("ephemeral", system.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("1h", system.GetProperty("cache_control").GetProperty("ttl").GetString());

        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    /// <summary>Settings ▸ AI ▸ Reasoning (#603): <c>output_config.effort</c> on the wire exactly
    /// when a level is set — beside the format, never replacing it — and ABSENT on Default.</summary>
    [Theory]
    [InlineData(AiReasoningEffort.Default, null)]
    [InlineData(AiReasoningEffort.Low, "low")]
    [InlineData(AiReasoningEffort.Medium, "medium")]
    [InlineData(AiReasoningEffort.High, "high")]
    public async Task TheEffort_IsOnTheWire_ExactlyWhenALevelIsSet(AiReasoningEffort effort, string? expected)
    {
        var handler = new SseHandler(Stream(ToneJson, "end_turn", 1, 1));
        var model = new ClaudeToneModel(Settings() with { Effort = effort }, "sk-ant-test", handler: handler);

        await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var output = body.RootElement.GetProperty("output_config");
        Assert.Equal("json_schema", output.GetProperty("format").GetProperty("type").GetString());
        var has = output.TryGetProperty("effort", out var value);
        Assert.Equal(expected is not null, has);
        if (expected is not null) Assert.Equal(expected, value.GetString());
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
    }

    /// <summary>A model older than the parameter answers it with a 400, so it is never sent one.</summary>
    [Fact]
    public void AClaudeModelOlderThanEffort_IsSentNone()
    {
        var model = new ClaudeToneModel(Settings("claude-haiku-4-5") with { Effort = AiReasoningEffort.High }, "sk-ant-test");

        Assert.Null(model.BuildParams(Prompt()).OutputConfig!.Effort);
    }

    [Fact]
    public async Task TheApiKey_NeverReachesTheRequestBody()
    {
        var handler = new SseHandler(Stream(ToneJson, "end_turn", 1, 1));
        var model = new ClaudeToneModel(Settings(), "sk-ant-a-recognisable-secret", handler: handler);

        await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("sk-ant-a-recognisable-secret", handler.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusal_IsItsOwnFault()
    {
        var handler = new SseHandler(Stream("", "refusal", 10, 0));
        var model = new ClaudeToneModel(Settings(), "sk-ant-test", handler: handler);

        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken));

        Assert.Equal(ToneDesignFault.ModelRefused, ex.Fault);
        Assert.Equal("stop_reason=refusal", ex.Detail);
    }

    [Fact]
    public async Task AReplyWithNoJson_IsInvalidOutput()
    {
        var handler = new SseHandler(Stream("Sorry, no.", "end_turn", 10, 5));
        var model = new ClaudeToneModel(Settings(), "sk-ant-test", handler: handler);

        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken));

        Assert.Equal(ToneDesignFault.InvalidOutput, ex.Fault);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ToneDesignFault.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, ToneDesignFault.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, ToneDesignFault.RateLimited)]
    public async Task AnErrorStatus_BecomesItsOwnFault(HttpStatusCode status, ToneDesignFault expected)
    {
        var handler = new SseHandler(status);
        var model = new ClaudeToneModel(Settings(), "sk-ant-test", handler: handler);

        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken));

        Assert.Equal(expected, ex.Fault);
    }

    [Fact]
    public async Task AConnectionFailure_IsANetworkFault()
    {
        var model = new ClaudeToneModel(
            Settings(), "sk-ant-test", handler: new SseHandler(new HttpRequestException("refused")));

        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken));

        Assert.Equal(ToneDesignFault.Network, ex.Fault);
    }

    [Fact]
    public async Task Probe_AsksForSixteenTokensAndReportsTheModelThatAnswered()
    {
        var handler = new SseHandler("""
            {
              "id": "msg_1", "type": "message", "role": "assistant",
              "model": "claude-opus-5-20260401",
              "content": [ { "type": "text", "text": "ok" } ],
              "stop_reason": "end_turn", "stop_sequence": null,
              "usage": { "input_tokens": 12, "output_tokens": 2 }
            }
            """, json: true);
        var model = new ClaudeToneModel(Settings(), "sk-ant-test", handler: handler);

        var probe = await model.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(probe.Ok);
        Assert.Equal("claude-opus-5-20260401", probe.ModelId);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(16, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Probe_ReportsABadKeyWithoutThrowing()
    {
        var handler = new SseHandler(HttpStatusCode.Unauthorized);
        var model = new ClaudeToneModel(Settings(), "sk-ant-bad", handler: handler);

        var probe = await model.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.False(probe.Ok);
        Assert.Contains("API key", probe.Message, StringComparison.Ordinal);
    }

    // ---- scripted transport ----------------------------------------------------

    /// <summary>A canned <c>text/event-stream</c> Messages response.</summary>
    private static string Stream(string text, string stopReason, int inputTokens, int outputTokens)
    {
        var sb = new StringBuilder();
        Event(sb, "message_start",
            "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\"," +
            "\"role\":\"assistant\",\"model\":\"claude-opus-5\",\"content\":[]," +
            "\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":" +
            inputTokens + ",\"output_tokens\":1}}}");
        Event(sb, "content_block_start",
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");

        // Two deltas, so the accumulation is actually exercised.
        var half = text.Length / 2;
        foreach (var chunk in new[] { text[..half], text[half..] })
        {
            Event(sb, "content_block_delta",
                "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" +
                JsonSerializer.Serialize(chunk) + "}}");
        }

        Event(sb, "content_block_stop", """{"type":"content_block_stop","index":0}""");
        Event(sb, "message_delta",
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":" +
            JsonSerializer.Serialize(stopReason) + ",\"stop_sequence\":null}," +
            "\"usage\":{\"output_tokens\":" + outputTokens + "}}");
        Event(sb, "message_stop", """{"type":"message_stop"}""");
        return sb.ToString();
    }

    private static void Event(StringBuilder sb, string name, string data)
    {
        sb.Append("event: ").Append(name).Append('\n');
        sb.Append("data: ").Append(Compact(data)).Append("\n\n");
    }

    private static string Compact(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    private sealed class SseHandler : HttpMessageHandler
    {
        private readonly string? _body;
        private readonly Exception? _exception;
        private readonly bool _json;
        private readonly HttpStatusCode _status;

        public SseHandler(string body, bool json = false)
        {
            _body = body;
            _json = json;
            _status = HttpStatusCode.OK;
        }

        public SseHandler(HttpStatusCode status)
        {
            _status = status;
            _json = true;
            _body = """{"type":"error","error":{"type":"invalid_request_error","message":"scripted"}}""";
        }

        public SseHandler(Exception exception)
        {
            _exception = exception;
            _status = HttpStatusCode.OK;
        }

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));

            if (_exception is not null) throw _exception;

            var content = new StringContent(_body!, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                _json ? "application/json" : "text/event-stream");
            return new HttpResponseMessage(_status) { Content = content };
        }
    }
}
