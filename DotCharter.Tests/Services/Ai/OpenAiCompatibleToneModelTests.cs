using System.Net;
using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Tests.Services.Update;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The local-server provider, driven by a scripted <c>HttpMessageHandler</c>.
///     Two of these cases are the difference between "works on LM Studio" and
///     "works": Ollama's <c>/v1</c> accepts <c>response_format</c> and may ignore
///     it, so the reply arrives fenced and surrounded by prose.
/// </summary>
public sealed class OpenAiCompatibleToneModelTests
{
    private const string ToneJson =
        """{"name":"Test Tone","rationale":"because","confidence":0.9,"modules":[{"type":"amp","params":{"gain":0.5},"bypassed":false}]}""";

    private static AiProviderSettings Settings(string? model = "local-model")
        => new(AiProviderKind.OpenAiCompatible, model ?? "", AiProviderPresets.LmStudioBaseUrl);

    private static ToneDesignPrompt Prompt()
        => ToneDesignPromptBuilder.Build(new ToneDesignRequest("a clean tone", [], null));

    private static string LmStudioReply(string content) =>
        $$"""
          {
            "id": "chatcmpl-1",
            "model": "local-model",
            "choices": [ { "index": 0, "message": { "role": "assistant", "content": {{JsonSerializer.Serialize(content)}} }, "finish_reason": "stop" } ],
            "usage": { "prompt_tokens": 9123, "completion_tokens": 214, "total_tokens": 9337 }
          }
          """;

    [Fact]
    public async Task ALmStudioShapedReply_IsParsedAndAccounted()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(LmStudioReply(ToneJson));
        using var model = new OpenAiCompatibleToneModel(Settings(), handler: handler);

        var reply = await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        Assert.Equal(ToneJson, reply.Json);
        Assert.Equal("local-model", reply.Usage.ModelId);
        Assert.Equal(9123, reply.Usage.InputTokens);
        Assert.Equal(214, reply.Usage.OutputTokens);
    }

    [Fact]
    public async Task AnOllamaShapedReply_WithFencesAndProse_IsStillParsed()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(LmStudioReply(
            $"Sure! Here's a clean tone for you:\n\n```json\n{ToneJson}\n```\n\nTweak the gain to taste."));
        using var model = new OpenAiCompatibleToneModel(Settings(), handler: handler);

        var reply = await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        var validation = ToneDraftValidator.Validate(reply.Json);
        Assert.NotNull(validation.Preset);
        Assert.Equal("amp", Assert.Single(validation.Preset!.Rig.Modules).ModuleTypeId);
    }

    [Fact]
    public async Task TheRequestBody_CarriesTheModel_TheSchema_TheSystemPromptAndTheTurns()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(LmStudioReply(ToneJson));
        using var model = new OpenAiCompatibleToneModel(Settings(), handler: handler);

        await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var root = body.RootElement;

        Assert.Equal("local-model", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.3, root.GetProperty("temperature").GetDouble(), 3);

        var format = root.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("json_schema");
        Assert.Equal(ToneDraftJson.SchemaName, schema.GetProperty("name").GetString());
        Assert.True(schema.GetProperty("strict").GetBoolean());
        Assert.True(schema.GetProperty("schema").TryGetProperty("properties", out _));

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        var system = messages[0].GetProperty("content").GetString()!;
        // The schema goes in the PROMPT as well, for a server that ignores the
        // parameter. This assertion is the whole Ollama story.
        Assert.Contains("# Module catalogue", system, StringComparison.Ordinal);
        // A marker from the SCHEMA rather than from the catalogue. It used to be
        // "maxItems": 12, which the schema no longer carries: neither strict
        // decoder accepts an array bound — see AiProviderCompatTests.
        Assert.Contains("\"required\": [\"id\", \"value\"]", system, StringComparison.Ordinal);
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Contains("a clean tone", messages[1].GetProperty("content").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEndpoint_IsChatCompletionsUnderTheConfiguredBaseUrl()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(LmStudioReply(ToneJson));
        using var model = new OpenAiCompatibleToneModel(
            new AiProviderSettings(AiProviderKind.OpenAiCompatible, "m", AiProviderPresets.OllamaBaseUrl + "/"),
            handler: handler);

        await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task NoKey_SendsNoAuthorizationHeader()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(LmStudioReply(ToneJson));
        using var model = new OpenAiCompatibleToneModel(Settings(), apiKey: null, handler: handler);

        await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task AKey_IsSentAsABearer()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(LmStudioReply(ToneJson));
        using var model = new OpenAiCompatibleToneModel(Settings(), "gateway-key", handler: handler);

        await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("gateway-key", handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ToneDesignFault.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, ToneDesignFault.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, ToneDesignFault.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ToneDesignFault.Unknown)]
    public async Task AnErrorStatus_BecomesItsOwnFault(HttpStatusCode status, ToneDesignFault expected)
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"error":"nope"}""", status);
        using var model = new OpenAiCompatibleToneModel(Settings(), handler: handler);

        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken));

        Assert.Equal(expected, ex.Fault);
        Assert.Contains($"HTTP {(int)status}", ex.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConnectionFailure_IsANetworkFault()
    {
        using var model = new OpenAiCompatibleToneModel(
            Settings(), handler: new ThrowingHandler(new HttpRequestException("connection refused")));

        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken));

        Assert.Equal(ToneDesignFault.Network, ex.Fault);
        Assert.Contains("Is it running?", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyWithNoJsonAtAll_IsInvalidOutput()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(LmStudioReply("I'd rather not."));
        using var model = new OpenAiCompatibleToneModel(Settings(), handler: handler);

        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken));

        Assert.Equal(ToneDesignFault.InvalidOutput, ex.Fault);
    }

    [Fact]
    public async Task AContentFilterFinishReason_IsARefusal()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""
            { "model": "m", "choices": [ { "message": { "content": "" }, "finish_reason": "content_filter" } ] }
            """);
        using var model = new OpenAiCompatibleToneModel(Settings(), handler: handler);

        var ex = await Assert.ThrowsAsync<ToneDesignException>(
            () => model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken));

        Assert.Equal(ToneDesignFault.ModelRefused, ex.Fault);
    }

    [Fact]
    public async Task AServerThatOmitsUsage_StillYieldsATone()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson($$"""
            { "model": "m", "choices": [ { "message": { "content": {{JsonSerializer.Serialize(ToneJson)}} }, "finish_reason": "stop" } ] }
            """);
        using var model = new OpenAiCompatibleToneModel(Settings(), handler: handler);

        var reply = await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        Assert.Equal(ToneJson, reply.Json);
        Assert.Equal(0, reply.Usage.InputTokens);
    }

    [Fact]
    public async Task Probe_ReportsWhetherTheConfiguredModelIsListed()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{ "data": [ { "id": "local-model" }, { "id": "qwen3:8b" } ] }""");
        using var model = new OpenAiCompatibleToneModel(Settings(), handler: handler);

        var probe = await model.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(probe.Ok);
        Assert.Equal("local-model", probe.ModelId);
        Assert.Equal("http://localhost:1234/v1/models", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task Probe_ConnectsButWarnsWhenTheModelIsNotListed()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{ "data": [ { "id": "qwen3:8b" } ] }""");
        using var model = new OpenAiCompatibleToneModel(Settings(), handler: handler);

        var probe = await model.ProbeAsync(TestContext.Current.CancellationToken);

        // Reachable is not the same as ready — Ollama lists PULLED models, so a
        // missing one is a warning the user can act on, not a failure.
        Assert.True(probe.Ok);
        Assert.Null(probe.ModelId);
        Assert.Contains("not in its list", probe.Message, StringComparison.Ordinal);
        Assert.Contains("qwen3:8b", probe.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_ReportsAServerThatIsNotThere()
    {
        using var model = new OpenAiCompatibleToneModel(
            Settings(), handler: new ThrowingHandler(new HttpRequestException("refused")));

        var probe = await model.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.False(probe.Ok);
        Assert.Contains("Is it running?", probe.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var model = new OpenAiCompatibleToneModel(Settings(), handler: new FakeHttpMessageHandler());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => model.CompleteAsync(Prompt(), cts.Token));
    }

    [Fact]
    public void AMissingBaseUrl_IsRejectedAtConstruction()
        => Assert.ThrowsAny<ArgumentException>(() =>
            new OpenAiCompatibleToneModel(new AiProviderSettings(AiProviderKind.OpenAiCompatible, "m", null)));

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
