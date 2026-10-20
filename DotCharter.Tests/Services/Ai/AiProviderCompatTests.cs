using System.Net;
using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Tests.Services.Update;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The three things that decide whether a request this app builds is ACCEPTED
///     by the endpoints it claims to support — as opposed to merely being
///     well-formed C#.
///
///     <para>
///         Each of them shipped wrong in 0.55: a JSON Schema outside both strict
///         subsets, a hard-coded <c>/v1</c> append that cannot address Gemini's
///         OpenAI-compatibility layer, and no answer at all for a server that does
///         <c>json_object</c> and not <c>json_schema</c>. None of them is visible
///         without a live call, which is exactly why they need a test.
///     </para>
/// </summary>
public sealed class AiProviderCompatTests
{
    // ---- 1. the schema subset -------------------------------------------------

    /// <summary>
    ///     Every JSON Schema keyword BOTH constrained decoders document as
    ///     supported, and nothing else. A keyword outside this set is not a
    ///     stricter-than-necessary reply — it is a 400 from Anthropic and a 400
    ///     from OpenAI, before a token is generated.
    ///
    ///     <list type="bullet">
    ///         <item>
    ///             Anthropic, "Supported features" (2026-09-12):
    ///             https://platform.claude.com/docs/en/build-with-claude/structured-outputs
    ///             — all basic types, <c>enum</c>, <c>const</c>, <c>anyOf</c>/<c>allOf</c>,
    ///             <c>$ref</c>/<c>$defs</c>, <c>default</c>, <c>required</c> and
    ///             <c>additionalProperties</c> (<b>"must be set to false for
    ///             objects"</b>), string formats, and array <c>minItems</c> for the
    ///             values 0 and 1 only. UNSUPPORTED: "Numerical constraints (such as
    ///             minimum, maximum, multipleOf)", "String constraints (minLength,
    ///             maxLength)", "Array constraints beyond minItems of 0 or 1",
    ///             "additionalProperties set to anything other than false".
    ///         </item>
    ///         <item>
    ///             OpenAI, "Supported schemas" (2026-09-12):
    ///             https://developers.openai.com/api/docs/guides/structured-outputs
    ///             — mirrored on
    ///             https://learn.microsoft.com/en-us/azure/ai-foundry/openai/how-to/structured-outputs
    ///             which prints the table: unsupported are String
    ///             <c>minLength maxLength pattern format</c>, Number
    ///             <c>minimum maximum multipleOf</c>, Object
    ///             <c>patternProperties unevaluatedProperties propertyNames
    ///             minProperties maxProperties</c>, Array
    ///             <c>unevaluatedItems contains minContains maxContains minItems
    ///             maxItems uniqueItems</c>; all fields must be <c>required</c> and
    ///             every object must set <c>additionalProperties: false</c>.
    ///         </item>
    ///     </list>
    ///
    ///     <para>
    ///         <c>minItems</c> is deliberately NOT in the allow-list even though
    ///         Anthropic takes 0 and 1: the intersection is what ships, and OpenAI
    ///         takes neither. <c>format</c> is out for the same reason.
    ///     </para>
    /// </summary>
    private static readonly HashSet<string> CommonSubset = new(StringComparer.Ordinal)
    {
        "type", "description", "properties", "required", "additionalProperties",
        "items", "enum", "const", "anyOf", "allOf", "$ref", "$defs", "definitions", "default"
    };

    /// <summary>Every strict reply schema the app sends to a provider: the tone draft, the AI
    /// chart pass's four (#497) and the AI chat's sections &amp; phrases job (#561).</summary>
    public static TheoryData<string> Schemas =>
    [
        nameof(ToneDraftJson),
        nameof(DotCharter.Services.Ai.Chart.AiChartPrompt.FingeringSchema),
        nameof(DotCharter.Services.Ai.Chart.AiChartPrompt.NamesSchema),
        nameof(DotCharter.Services.Ai.Chart.AiChartPrompt.FhpSchema),
        nameof(DotCharter.Services.Ai.Chart.AiChartPrompt.FingeringOnlySchema),
        nameof(DotCharter.Services.Ai.Chart.AiStructurePrompt),
    ];

    private static string SchemaNamed(string name) => name switch
    {
        nameof(ToneDraftJson) => ToneDraftJson.Schema,
        nameof(DotCharter.Services.Ai.Chart.AiChartPrompt.FingeringSchema) => DotCharter.Services.Ai.Chart.AiChartPrompt.FingeringSchema,
        nameof(DotCharter.Services.Ai.Chart.AiChartPrompt.NamesSchema) => DotCharter.Services.Ai.Chart.AiChartPrompt.NamesSchema,
        nameof(DotCharter.Services.Ai.Chart.AiChartPrompt.FhpSchema) => DotCharter.Services.Ai.Chart.AiChartPrompt.FhpSchema,
        nameof(DotCharter.Services.Ai.Chart.AiChartPrompt.FingeringOnlySchema) => DotCharter.Services.Ai.Chart.AiChartPrompt.FingeringOnlySchema,
        // The AI chat's sections & phrases job (#561).
        nameof(DotCharter.Services.Ai.Chart.AiStructurePrompt) => DotCharter.Services.Ai.Chart.AiStructurePrompt.Schema,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    [Theory]
    [MemberData(nameof(Schemas))]
    public void TheSchema_NamesNoKeywordOutsideTheSubsetBothDecodersDocument(string schema)
    {
        using var doc = JsonDocument.Parse(SchemaNamed(schema));
        var offenders = new List<string>();
        WalkSchema(doc.RootElement, "$", offenders, _ => { });

        Assert.Equal([], offenders);
    }

    [Theory]
    [MemberData(nameof(Schemas))]
    public void EverySchemaObject_IsClosedAndRequiresEveryPropertyItDeclares(string schema)
    {
        // Both decoders demand this of EVERY object, not just the root: OpenAI
        // rejects the schema outright, and Anthropic lists any other
        // additionalProperties value as unsupported.
        using var doc = JsonDocument.Parse(SchemaNamed(schema));
        var failures = new List<string>();

        WalkSchema(doc.RootElement, "$", [], node =>
        {
            if (!node.Element.TryGetProperty("properties", out var properties)) return;

            if (!node.Element.TryGetProperty("additionalProperties", out var open) ||
                open.ValueKind != JsonValueKind.False)
            {
                failures.Add($"{node.Path}: additionalProperties must be literal false");
            }

            var declared = properties.EnumerateObject()
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var required = node.Element.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(e => e.GetString() ?? "").OrderBy(n => n, StringComparer.Ordinal).ToList()
                : [];

            if (!declared.SequenceEqual(required, StringComparer.Ordinal))
                failures.Add($"{node.Path}: required must list every declared property");
        });

        Assert.Equal([], failures);
    }

    [Fact]
    public void ParamsAreAListOfIdValuePairs_NotAnOpenMap()
    {
        // The shape itself, stated once: an open `additionalProperties` map is the
        // thing neither decoder takes, so this is the property that must not drift
        // back.
        using var doc = JsonDocument.Parse(ToneDraftJson.Schema);
        var paramsSchema = doc.RootElement
            .GetProperty("properties").GetProperty("modules")
            .GetProperty("items").GetProperty("properties").GetProperty("params");

        Assert.Equal("array", paramsSchema.GetProperty("type").GetString());
        var item = paramsSchema.GetProperty("items");
        Assert.Equal("object", item.GetProperty("type").GetString());
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("string", item.GetProperty("properties").GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("number", item.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
    }

    [Fact]
    public void ADraftRoundTrips_ThroughThePairListForm()
    {
        var draft = new ToneDraft("Test", "because", 0.8,
        [
            new ToneDraftModule("amp", new Dictionary<string, float> { ["gainDb"] = 6.5f, ["bass"] = 0.4f })
        ]);

        var json = ToneDraftJson.Serialize(draft);

        // Written as the strict-subset form — this is what the few-shot examples
        // teach, and an example that disagreed with the schema would be worse than
        // no example.
        Assert.Contains("\"id\": \"gainDb\"", json, StringComparison.Ordinal);
        Assert.Contains("\"value\": 6.5", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"gainDb\": 6.5", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<ToneDraft>(json, ToneDraftJson.Options);
        var module = Assert.Single(back!.Modules);
        Assert.Equal(6.5f, module.Params["gainDb"]);
        Assert.Equal(0.4f, module.Params["bass"]);
    }

    [Fact]
    public void TheOldOpenMapForm_IsStillRead_BecauseALocalModelKeepsEmittingIt()
    {
        // A server that ignores response_format answers in whatever shape its
        // weights learned. Losing a usable tone to the encoding it arrived in
        // would be a regression dressed as strictness.
        const string json =
            """{"name":"n","rationale":"r","confidence":0.5,"modules":[{"type":"amp","params":{"gainDb":3},"bypassed":false}]}""";

        var draft = JsonSerializer.Deserialize<ToneDraft>(json, ToneDraftJson.Options);

        Assert.Equal(3f, Assert.Single(draft!.Modules).Params["gainDb"]);
    }

    [Fact]
    public void AParamWithoutAUsableValue_BecomesNaN_SoTheValidatorReportsIt()
    {
        // NaN is the validator's "was not a number; the default N was used"
        // warning. Dropping the entry instead would apply the same default
        // silently.
        const string json =
            """{"name":"n","rationale":"r","confidence":0.5,"modules":[{"type":"amp","params":[{"id":"gainDb","value":"loud"}],"bypassed":false}]}""";

        var draft = JsonSerializer.Deserialize<ToneDraft>(json, ToneDraftJson.Options);

        Assert.True(float.IsNaN(Assert.Single(draft!.Modules).Params["gainDb"]));
    }

    // ---- 2. the URL rule ------------------------------------------------------

    [Theory]
    // LM Studio and Ollama: no version segment, so /v1 is appended — byte for byte
    // what the presets did before the rule existed.
    [InlineData("http://localhost:1234", "http://localhost:1234/v1/chat/completions")]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/", "http://localhost:11434/v1/chat/completions")]
    // Gemini's OpenAI-compatibility layer. The documented base URL already carries
    // v1beta, and the old rule produced .../v1beta/openai/v1/chat/completions.
    // https://ai.google.dev/gemini-api/docs/openai
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai",
        "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/",
        "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    // OpenRouter and OpenAI: a version segment is already there.
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    // DeepSeek documents the bare host AND the /v1 form; both must work.
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1/chat/completions")]
    // A user who pasted the whole endpoint gets it back unchanged.
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/chat/completions")]
    // The HOST is never inspected — a server at v1.example.com has no version
    // segment in its PATH and still needs /v1.
    [InlineData("https://v1.example.com", "https://v1.example.com/v1/chat/completions")]
    public void TheChatCompletionsUrl_FollowsTheBaseUrlsOwnForm(string baseUrl, string expected)
        => Assert.Equal(expected, OpenAiCompatibleToneModel.ChatCompletionsUrl(baseUrl));

    [Theory]
    [InlineData("http://localhost:1234", "http://localhost:1234/v1/models")]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1/models")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai",
        "https://generativelanguage.googleapis.com/v1beta/openai/models")]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/models")]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/v1/models")]
    // The SIBLING of the completions endpoint, so "Test connection" works for a
    // user who pasted the full URL.
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/models")]
    public void TheModelsUrl_IsTheSiblingOfChatCompletions(string baseUrl, string expected)
        => Assert.Equal(expected, OpenAiCompatibleToneModel.ModelsUrl(baseUrl));

    [Fact]
    public void EveryDocumentedBaseUrlForm_ResolvesToAChatCompletionsEndpoint()
    {
        // The hint's list and the rule cannot drift apart: a form offered in
        // Settings that this rule cannot address is a support ticket.
        foreach (var example in AiProviderPresets.BaseUrlExamples)
        {
            Assert.EndsWith("/chat/completions", OpenAiCompatibleToneModel.ChatCompletionsUrl(example),
                StringComparison.Ordinal);
        }
    }

    // ---- 3. the json_object fallback ------------------------------------------

    [Fact]
    public async Task A400NamingResponseFormat_IsRetriedOnceInJsonObjectMode()
    {
        // DeepSeek supports response_format json_object and no json_schema:
        // https://api-docs.deepseek.com/guides/json_mode
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(
            """{"error":{"message":"response_format json_schema is not supported","type":"invalid_request_error"}}""",
            HttpStatusCode.BadRequest);
        handler.EnqueueJson(Reply(ToneJson));
        using var model = new OpenAiCompatibleToneModel(DeepSeekSettings(), handler: handler);

        var reply = await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        Assert.Equal(ToneJson, reply.Json);
        Assert.Equal(2, handler.RequestBodies.Count);

        using var first = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal("json_schema",
            first.RootElement.GetProperty("response_format").GetProperty("type").GetString());

        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        var format = second.RootElement.GetProperty("response_format");
        Assert.Equal("json_object", format.GetProperty("type").GetString());
        Assert.False(format.TryGetProperty("json_schema", out _));

        // The schema is still in the system prompt, which is the whole reason the
        // fallback is safe — and DeepSeek additionally REQUIRES the word "json"
        // there for json_object mode.
        var system = second.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("JSON Schema", system, StringComparison.Ordinal);
        Assert.Contains("\"modules\"", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A400AboutSomethingElse_IsNotRetried()
    {
        // The retry is for ONE cause. Retrying every 400 would double the cost of
        // a bad model id and hide the real reason behind the second answer.
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"error":{"message":"model 'nope' not found"}}""", HttpStatusCode.BadRequest);
        using var model = new OpenAiCompatibleToneModel(DeepSeekSettings(), handler: handler);

        var fault = await Assert.ThrowsAsync<ToneDesignException>(
            () => model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken));

        Assert.Equal(ToneDesignFault.Unknown, fault.Fault);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public void TheFallbackTrigger_ReadsTheParameterNames_NotAMessage()
    {
        // Every gateway words it differently; the parameter names are what the
        // request actually sent.
        Assert.True(OpenAiCompatibleToneModel.RejectedTheResponseFormat(
            """{"error":{"message":"Unsupported parameter: 'response_format.json_schema'"}}"""));
        Assert.True(OpenAiCompatibleToneModel.RejectedTheResponseFormat(
            """{"detail":"json_schema mode requires a newer model"}"""));
        Assert.False(OpenAiCompatibleToneModel.RejectedTheResponseFormat(
            """{"error":{"message":"context length exceeded"}}"""));
    }

    [Fact]
    public async Task TheFallbackBody_IsOtherwiseTheSameRequest()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"error":{"message":"response_format not supported"}}""", HttpStatusCode.BadRequest);
        handler.EnqueueJson(Reply(ToneJson));
        using var model = new OpenAiCompatibleToneModel(DeepSeekSettings(), handler: handler);

        await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        using var first = JsonDocument.Parse(handler.RequestBodies[0]);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal(first.RootElement.GetProperty("model").GetString(),
            second.RootElement.GetProperty("model").GetString());
        Assert.Equal(first.RootElement.GetProperty("messages").GetArrayLength(),
            second.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal(OpenAiCompatibleToneModel.Temperature,
            second.RootElement.GetProperty("temperature").GetDouble(), 6);
    }

    // ---- 4. the OpenRouter extras ---------------------------------------------

    [Fact]
    public async Task AnOpenRouterRequest_RequiresParameterSupport_AndIsAttributed()
    {
        // require_parameters: "Only use providers that support all parameters in
        // your request" — https://openrouter.ai/docs/features/provider-routing
        // Headers: https://openrouter.ai/docs/api-reference/overview
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(OpenRouterReply(ToneJson, cost: "0.00421"));
        using var model = new OpenAiCompatibleToneModel(OpenRouterSettings(), handler: handler);

        await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.True(body.RootElement.GetProperty("provider").GetProperty("require_parameters").GetBoolean());

        var request = handler.Requests[0];
        Assert.Equal("DotCharter", Assert.Single(request.Headers.GetValues("X-Title")));
        Assert.Contains("DotCharter", Assert.Single(request.Headers.GetValues("HTTP-Referer")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheServersOwnCost_IsCarriedOnTheUsage()
    {
        // usage.cost is returned automatically; the `usage: {include: true}`
        // request parameter is documented as deprecated and having no effect
        // (https://openrouter.ai/docs/use-cases/usage-accounting), so it is NOT
        // sent — this asserts both halves.
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(OpenRouterReply(ToneJson, cost: "0.00421"));
        using var model = new OpenAiCompatibleToneModel(OpenRouterSettings(), handler: handler);

        var reply = await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        Assert.Equal(0.00421m, reply.Usage.CostUsd);
        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.False(body.RootElement.TryGetProperty("usage", out _));
    }

    [Fact]
    public async Task AServerThatPricesNothing_LeavesTheCostNull_NotZero()
    {
        // Null is "nobody priced this"; zero would read as a measured free call.
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(Reply(ToneJson));
        using var model = new OpenAiCompatibleToneModel(
            new AiProviderSettings(AiProviderKind.OpenAiCompatible, "local-model",
                AiProviderPresets.LmStudioBaseUrl),
            handler: handler);

        var reply = await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        Assert.Null(reply.Usage.CostUsd);
    }

    [Fact]
    public async Task NoneOfTheOpenRouterExtras_ReachAnotherServer()
    {
        // `provider` is a field LM Studio has never heard of, and a gateway that
        // validates its body would 400 on it.
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(Reply(ToneJson));
        using var model = new OpenAiCompatibleToneModel(
            new AiProviderSettings(AiProviderKind.OpenAiCompatible, "local-model",
                AiProviderPresets.LmStudioBaseUrl),
            handler: handler);

        await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.False(body.RootElement.TryGetProperty("provider", out _));
        Assert.False(handler.Requests[0].Headers.Contains("HTTP-Referer"));
        Assert.False(handler.Requests[0].Headers.Contains("X-Title"));
    }

    [Fact]
    public async Task TheFallbackBody_DropsRequireParameters_BecauseItNoLongerNeedsTheCapability()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"error":{"message":"response_format not supported"}}""", HttpStatusCode.BadRequest);
        handler.EnqueueJson(OpenRouterReply(ToneJson, cost: "0.001"));
        using var model = new OpenAiCompatibleToneModel(OpenRouterSettings(), handler: handler);

        await model.CompleteAsync(Prompt(), TestContext.Current.CancellationToken);

        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.False(second.RootElement.TryGetProperty("provider", out _));
    }

    [Theory]
    [InlineData("https://openrouter.ai/api", true)]
    [InlineData("https://openrouter.ai/api/v1", true)]
    [InlineData("https://api.openrouter.ai/v1", true)]
    [InlineData("http://localhost:1234", false)]
    [InlineData("https://api.deepseek.com", false)]
    // The HOST, not the string: a proxy that merely mentions it is not OpenRouter.
    [InlineData("http://localhost:8080/proxy?upstream=openrouter.ai", false)]
    public void OpenRouterIsRecognisedByHost(string baseUrl, bool expected)
        => Assert.Equal(expected, OpenAiCompatibleToneModel.IsOpenRouter(baseUrl));

    // ---- fixtures -------------------------------------------------------------

    private static AiProviderSettings OpenRouterSettings()
        => new(AiProviderKind.OpenAiCompatible, "anthropic/claude-opus-5", "https://openrouter.ai/api");

    private static string OpenRouterReply(string content, string cost) =>
        $$"""
          {
            "id": "gen-1",
            "model": "anthropic/claude-opus-5",
            "choices": [ { "index": 0, "message": { "role": "assistant", "content": {{JsonSerializer.Serialize(content)}} }, "finish_reason": "stop" } ],
            "usage": { "prompt_tokens": 7500, "completion_tokens": 300, "total_tokens": 7800, "cost": {{cost}},
                       "cost_details": { "upstream_inference_cost": null } }
          }
          """;

    private const string ToneJson =
        """{"name":"Test Tone","rationale":"because","confidence":0.9,"modules":[{"type":"amp","params":[{"id":"gainDb","value":3}],"bypassed":false}]}""";

    private static AiProviderSettings DeepSeekSettings()
        => new(AiProviderKind.OpenAiCompatible, "deepseek-chat", "https://api.deepseek.com");

    private static ToneDesignPrompt Prompt()
        => ToneDesignPromptBuilder.Build(new ToneDesignRequest("a clean tone", [], null));

    private static string Reply(string content) =>
        $$"""
          {
            "id": "chatcmpl-1",
            "model": "deepseek-chat",
            "choices": [ { "index": 0, "message": { "role": "assistant", "content": {{JsonSerializer.Serialize(content)}} }, "finish_reason": "stop" } ],
            "usage": { "prompt_tokens": 9123, "completion_tokens": 214 }
          }
          """;

    private readonly record struct SchemaNode(JsonElement Element, string Path);

    /// <summary>
    ///     Walks every SCHEMA object in the document, collecting keywords outside
    ///     <see cref="CommonSubset" /> into <paramref name="offenders" /> and
    ///     handing each node to <paramref name="visit" />.
    ///     <para>
    ///         The recursion has to know which keys are KEYWORDS and which are
    ///         user-chosen names: the values under <c>properties</c> and
    ///         <c>$defs</c> are schemas keyed by arbitrary names, and treating
    ///         those names as keywords would make the test fail on the word
    ///         "confidence".
    ///     </para>
    /// </summary>
    private static void WalkSchema(
        JsonElement element, string path, List<string> offenders, Action<SchemaNode> visit)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        visit(new SchemaNode(element, path));

        foreach (var member in element.EnumerateObject())
        {
            switch (member.Name)
            {
                case "properties":
                case "$defs":
                case "definitions":
                    foreach (var child in member.Value.EnumerateObject())
                        WalkSchema(child.Value, $"{path}.{member.Name}.{child.Name}", offenders, visit);
                    break;
                case "items":
                    WalkSchema(member.Value, $"{path}.items", offenders, visit);
                    break;
                case "anyOf":
                case "allOf":
                    var index = 0;
                    foreach (var child in member.Value.EnumerateArray())
                        WalkSchema(child, $"{path}.{member.Name}[{index++}]", offenders, visit);
                    break;
                default:
                    if (!CommonSubset.Contains(member.Name)) offenders.Add($"{path}.{member.Name}");
                    break;
            }
        }
    }
}
