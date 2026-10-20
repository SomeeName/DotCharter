using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotCharter.Tone.Online;

namespace DotCharter.Tone.Ai;

/// <summary>
///     Well-known base URLs the Settings page offers as one-click presets. Both are
///     LOOPBACK and both are OUTBOUND HTTP from this app — the repo's
///     47810–47829 listener-port rule does not apply, because nothing here opens a
///     listener.
/// </summary>
public static class AiProviderPresets
{
    /// <summary>LM Studio's OpenAI-compatible server default.</summary>
    public const string LmStudioBaseUrl = "http://localhost:1234";

    /// <summary>Ollama's OpenAI-compatible shim default.</summary>
    public const string OllamaBaseUrl = "http://localhost:11434";

    /// <summary>The base-URL forms this provider accepts, in the order the Settings
    /// hint lists them. ONE list, so the hint, the doc and
    /// <c>AiProviderCompatTests</c> cannot disagree about what is supported.</summary>
    public static IReadOnlyList<string> BaseUrlExamples { get; } =
    [
        LmStudioBaseUrl,
        OllamaBaseUrl,
        "https://api.openai.com/v1",
        "https://openrouter.ai/api/v1",
        "https://api.deepseek.com",
        "https://generativelanguage.googleapis.com/v1beta/openai"
    ];
}

/// <summary>
///     One provider for every server speaking the OpenAI chat-completions shape:
///     LM Studio, Ollama's <c>/v1</c> shim, llama.cpp's server, a gateway.
///
///     <para>
///         <b>The schema is sent TWICE, on purpose.</b> It goes in
///         <c>response_format.json_schema</c> with <c>strict: true</c>, which LM
///         Studio honours and constrains generation with — and it goes in the system
///         prompt as text, because Ollama's <c>/v1</c> endpoint ACCEPTS the parameter
///         and may ignore it. A reply that came back as prose around a fenced block
///         is then still parseable: <see cref="ToneDraftJson.ExtractJsonObject" />
///         strips fences and finds the balanced object.
///     </para>
///     <para>
///         <b>No streaming and a long timeout.</b> A local model on CPU can take
///         minutes for a first token, and there is no user-visible stream to feed;
///         <see cref="RequestTimeout" /> is the bound. The client comes from
///         <see cref="OnlineHttp.CreateClient" /> for its response-size cap and the
///         shared connection pool — a local server is still a server, and a broken
///         one answering with a gigabyte is not this app's problem to buffer.
///     </para>
///     <para>
///         <b>…and when the server will not take it at all, the request is made
///         again without it.</b> DeepSeek supports <c>response_format:
///         {"type": "json_object"}</c> and no <c>json_schema</c>, and answers 400.
///         A 400 naming <c>response_format</c> or <c>json_schema</c> is retried
///         ONCE in <c>json_object</c> mode; the schema is in the system prompt
///         either way and the design service validates the reply either way, so the
///         only thing lost is the constraint. Logged at Information once per
///         process, never with the body.
///     </para>
///     <para>
///         <c>Authorization: Bearer</c> is sent only when a key exists: LM Studio and
///         Ollama need none, and sending an empty bearer makes some gateways 401.
///     </para>
///     <para>
///         <b>The endpoint is DERIVED from the base URL, not a blind <c>/v1</c>
///         append</b> — see <see cref="ChatCompletionsUrl" /> for the three forms
///         and why Gemini's <c>…/v1beta/openai/</c> breaks the naive rule.
///     </para>
///     <para>
///         <b>OpenRouter is the one host with extras, and they are keyed off its
///         HOST alone</b> (<see cref="IsOpenRouter" />): <c>provider.require_parameters</c>
///         so a <c>json_schema</c> request is never routed to a backend that would
///         ignore it, the <c>HTTP-Referer</c>/<c>X-Title</c> attribution headers,
///         and <c>usage.cost</c> read off the reply into
///         <see cref="AiUsage.CostUsd" />. Nothing of that reaches another server —
///         <c>provider</c> is a field LM Studio has never heard of, and a gateway
///         that validates its body would 400 on it.
///     </para>
///     <para>
/// <b>Reasoning effort is sent only when Settings ▸ AI names a level</b>:
///         <c>reasoning_effort</c> (OpenAI's spelling, which Ollama, Gemini's layer and
///         the OpenAI API itself take), or OpenRouter's own <c>reasoning.effort</c>. A
///         server or model without it may answer 400; a 400 NAMING <c>reasoning</c> is
///         retried ONCE without the field, logged once per process — the same shape as
///         the <c>response_format</c> fallback below, for the same reason.
///     </para>
/// </summary>
public sealed class OpenAiCompatibleToneModel : IToneDesignModel, IDisposable
{
    /// <summary>A local model on CPU is slow; two minutes is the wall.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Low but not zero: a tone wants consistency, not determinism.</summary>
    public const double Temperature = 0.3;

    /// <summary>Process-wide, because "once per process" is what the log line
    /// promises and a provider instance is minted per REQUEST.</summary>
    private static int _schemaFallbackLogged;
    private static int _effortFallbackLogged;

    /// <summary>The <c>HTTP-Referer</c> OpenRouter attributes requests to.</summary>
    private const string OpenRouterReferer = "https://github.com/SomeeName/DotCharter";

    /// <summary>The <c>X-Title</c> OpenRouter shows for this app.</summary>
    private const string OpenRouterTitle = "DotCharter";

    private readonly string _baseUrl;
    private readonly bool _isOpenRouter;
    private readonly HttpClient _http;
    private readonly IAppLogger? _logger;
    private readonly string _modelId;
    private readonly string? _effort;

    /// <summary>Creates the provider over <paramref name="settings" />' base URL and
    /// model id.</summary>
    /// <param name="settings">Provider settings; <c>BaseUrl</c> is required.</param>
    /// <param name="apiKey">Optional bearer token; null/blank sends no header.</param>
    /// <param name="logger">Optional app logger for usage lines.</param>
    /// <param name="handler">Optional transport, for tests.</param>
    public OpenAiCompatibleToneModel(
        AiProviderSettings settings,
        string? apiKey = null,
        IAppLogger? logger = null,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.BaseUrl);

        _logger = logger;
        _baseUrl = settings.BaseUrl.TrimEnd('/');
        _modelId = settings.ModelId?.Trim() ?? "";
        _isOpenRouter = IsOpenRouter(_baseUrl);
        _effort = AiReasoning.WireValue(settings.Effort, AiProviderKind.OpenAiCompatible, _modelId);
        _http = OnlineHttp.CreateClient(RequestTimeout, handler);
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        if (!_isOpenRouter) return;

        // Attribution, documented as OPTIONAL and sent because the alternative is
        // appearing on a public leaderboard as an anonymous key:
        // https://openrouter.ai/docs/api-reference/overview
        _http.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", OpenRouterReferer);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", OpenRouterTitle);
    }

    /// <summary>True when <paramref name="baseUrl" /> addresses OpenRouter, which
    /// takes three fields nothing else does. Matched on the HOST — an
    /// <c>openrouter.ai</c> anywhere in the string would also match a local proxy
    /// with it in a query.</summary>
    internal static bool IsOpenRouter(string? baseUrl)
        => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
           (string.Equals(uri.Host, "openrouter.ai", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;

    /// <summary>Releases the HTTP client. The shared handler underneath is NOT
    /// disposed — see <see cref="OnlineHttp" />.</summary>
    public void Dispose() => _http.Dispose();

    /// <inheritdoc />
    public async Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        // Before the POST, not just around it: a token already cancelled must not
        // cost a request, and a handler that ignores the token would otherwise turn
        // a user Cancel into whatever the server happened to answer.
        ct.ThrowIfCancellationRequested();

        var withEffort = _effort is not null;
        var (status, payload) = await PostAsync(BuildRequestBody(prompt, withEffort: withEffort), ct).ConfigureAwait(false);

        if (status == HttpStatusCode.BadRequest && withEffort && RejectedTheReasoningEffort(payload))
        {
            LogEffortFallbackOnce();
            withEffort = false;
            (status, payload) = await PostAsync(BuildRequestBody(prompt, withEffort: false), ct).ConfigureAwait(false);
        }

        if (status == HttpStatusCode.BadRequest && RejectedTheResponseFormat(payload))
        {
            LogSchemaFallbackOnce();
            (status, payload) = await PostAsync(BuildRequestBody(prompt, jsonObjectMode: true, withEffort), ct)
                .ConfigureAwait(false);
        }

        if ((int)status is < 200 or > 299) throw ForStatus(status, payload);

        var (text, usage, finishReason) = ParseReply(payload);

        if (string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(finishReason, "refusal", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToneDesignException(
                ToneDesignFault.ModelRefused,
                "The model declined to design this tone.",
                $"finish_reason={finishReason}");
        }

        var json = ToneDraftJson.ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ToneDesignException(
                ToneDesignFault.InvalidOutput,
                "The model's reply did not contain a tone.",
                $"finish_reason={finishReason ?? "?"}, {text?.Length ?? 0} characters");
        }

        var accounted = usage with { ModelId = string.IsNullOrWhiteSpace(usage.ModelId) ? _modelId : usage.ModelId };
        // The cost clause appears only when the SERVER priced the request; a
        // printed "$0.00" for a server that priced nothing reads as a measured
        // zero.
        var costClause = accounted.CostUsd is { } usd
            ? $" ${usd.ToString("0.######", CultureInfo.InvariantCulture)}."
            : "";
        _logger?.Info(AppLogCategory.Application,
            $"AI tone design ({accounted.ModelId} at {_baseUrl}): " +
            $"{accounted.InputTokens} in / {accounted.OutputTokens} out tokens.{costClause}");

        return new ToneDesignModelReply(json, accounted);
    }

    /// <summary>POSTs one body and reads the whole reply. Returns the status rather
    /// than throwing on it, because the caller has one status — 400 naming
    /// <c>response_format</c> — it answers with a retry instead of a failure.</summary>
    private async Task<(HttpStatusCode Status, string Payload)> PostAsync(string body, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(ChatCompletionsUrl(_baseUrl), content, ct)
                .ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (response.StatusCode, payload);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    /// <summary>True when a 400 is the server saying it does not do
    /// <c>json_schema</c> structured output. DeepSeek is the shipping example: it
    /// supports <c>response_format: {"type": "json_object"}</c> and nothing else
    /// (<see href="https://api-docs.deepseek.com/guides/json_mode" />).
    /// <para>Matched on the PARAMETER NAMES rather than on a message, because every
    /// gateway words it differently and the names are what the request actually
    /// sent.</para></summary>
    internal static bool RejectedTheResponseFormat(string payload)
        => payload.Contains("response_format", StringComparison.OrdinalIgnoreCase) ||
           payload.Contains("json_schema", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a 400 is the server (or the model behind it) refusing the
    /// reasoning field this request sent — matched on the parameter name, like
    /// <see cref="RejectedTheResponseFormat" />.</summary>
    internal static bool RejectedTheReasoningEffort(string payload)
        => payload.Contains("reasoning", StringComparison.OrdinalIgnoreCase);

    private void LogEffortFallbackOnce()
    {
        if (Interlocked.Exchange(ref _effortFallbackLogged, 1) != 0) return;
        _logger?.Info(AppLogCategory.Application,
            "AI: the server rejected the reasoning-effort field; retrying without it. Reported once per process.");
    }

    /// <summary>Logs the fallback at Information ONCE per process. A server that
    /// does not do structured output does not do it on every request either, and a
    /// line per design would be noise. <b>The body is never logged</b> — an error
    /// payload from a gateway can echo the request back, key header included.</summary>
    private void LogSchemaFallbackOnce()
    {
        if (Interlocked.Exchange(ref _schemaFallbackLogged, 1) != 0) return;
        _logger?.Info(AppLogCategory.Application,
            "AI tone design: the server rejected response_format=json_schema; retrying with " +
            "json_object and the schema in the system prompt. Reported once per process.");
    }

    /// <inheritdoc />
    public async Task<AiProbeResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(ModelsUrl(_baseUrl), ct).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var fault = ForStatus(response.StatusCode, payload);
                return new AiProbeResult(false, fault.Message, null);
            }

            var ids = ParseModelIds(payload);
            if (ids.Count == 0)
            {
                return new AiProbeResult(true,
                    $"Connected to {_baseUrl}, but it lists no models.", null);
            }

            // Ollama lists PULLED models here and LM Studio lists LOADED ones, so a
            // model that is absent is a real warning and not a failure: the user may
            // be about to load it.
            var listed = ids.Contains(_modelId, StringComparer.OrdinalIgnoreCase);
            return new AiProbeResult(
                true,
                listed
                    ? $"Connected to {_baseUrl}; \"{_modelId}\" is available."
                    : $"Connected to {_baseUrl}, but \"{_modelId}\" is not in its list of " +
                      $"{ids.Count} model(s). Available: {string.Join(", ", ids.Take(8))}.",
                listed ? _modelId : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AiProbeResult(false, Translate(ex).Message, null);
        }
    }

    /// <summary>
    ///     Where chat completions live under <paramref name="baseUrl" />.
    ///
    ///     <para>
    ///         <b>A bare <c>/v1</c> append is wrong for half the servers this
    ///         provider claims to support.</b> Gemini's OpenAI-compatibility layer
    ///         documents its base URL as
    ///         <c>https://generativelanguage.googleapis.com/v1beta/openai/</c>
    ///         (<see href="https://ai.google.dev/gemini-api/docs/openai" />), and
    ///         appending <c>/v1</c> to that asks for
    ///         <c>…/v1beta/openai/v1/chat/completions</c>, which does not exist. So
    ///         the rule is:
    ///     </para>
    ///     <list type="number">
    ///         <item>a URL that already names <c>chat/completions</c> is used as
    ///         given (and <see cref="ModelsUrl" /> takes its sibling);</item>
    ///         <item>a PATH that already carries a version segment — <c>v1</c>,
    ///         <c>v1beta</c>, <c>v2</c> — gets the leaf only;</item>
    ///         <item>anything else gets <c>/v1</c> plus the leaf, which is what
    ///         keeps the LM Studio and Ollama presets working byte for byte.</item>
    ///     </list>
    ///     <para>
    ///         Only the path is inspected, never the host: a server at
    ///         <c>v1.example.com</c> has no version segment in its path and must
    ///         still get <c>/v1</c>.
    ///     </para>
    /// </summary>
    internal static string ChatCompletionsUrl(string baseUrl) => Endpoint(baseUrl, "chat/completions");

    /// <summary>Where the model listing lives under <paramref name="baseUrl" />, by
    /// the same rule — the SIBLING of chat completions, so a user who pasted the
    /// full completions URL still gets a working "Test connection".</summary>
    internal static string ModelsUrl(string baseUrl) => Endpoint(baseUrl, "models");

    private static string Endpoint(string baseUrl, string leaf)
    {
        const string chat = "/chat/completions";
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        if (trimmed.EndsWith(chat, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^chat.Length];

        return HasVersionSegment(trimmed) ? $"{trimmed}/{leaf}" : $"{trimmed}/v1/{leaf}";
    }

    private static bool HasVersionSegment(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsVersionSegment(segment))
                return true;
        }

        return false;
    }

    /// <summary><c>v</c> + digits, optionally followed by a channel word and more
    /// digits: <c>v1</c>, <c>v2</c>, <c>v1beta</c>, <c>v1alpha2</c>. Not
    /// <c>vocals</c>, and not <c>v1.example.com</c> — the whole segment must
    /// match.</summary>
    private static bool IsVersionSegment(ReadOnlySpan<char> segment)
    {
        if (segment.Length < 2 || (segment[0] != 'v' && segment[0] != 'V')) return false;

        var i = 1;
        while (i < segment.Length && char.IsAsciiDigit(segment[i])) i++;
        if (i == 1) return false;
        while (i < segment.Length && char.IsAsciiLetter(segment[i])) i++;
        while (i < segment.Length && char.IsAsciiDigit(segment[i])) i++;
        return i == segment.Length;
    }

    /// <summary>The POST body. Internal so the tests can assert the wire shape
    /// without a transport.</summary>
    /// <param name="prompt">The prompt to marshal.</param>
    /// <param name="jsonObjectMode">True for the fallback body: plain
    /// <c>response_format: json_object</c> instead of the schema, for a server
    /// that answered 400 to <c>json_schema</c>. The schema is in the system prompt
    /// either way, and the design service validates the reply either way.</param>
    /// <param name="withEffort">False for the body re-sent after a server refused the
    /// reasoning field; true sends it when Settings ▸ AI names a level.</param>
    internal string BuildRequestBody(ToneDesignPrompt prompt, bool jsonObjectMode = false, bool withEffort = true)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", _modelId);
            writer.WriteNumber("temperature", Temperature);
            writer.WriteBoolean("stream", false);

            if (withEffort && _effort is { } effort)
            {
                if (_isOpenRouter)
                {
                    // https://openrouter.ai/docs/use-cases/reasoning-tokens
                    writer.WritePropertyName("reasoning");
                    writer.WriteStartObject();
                    writer.WriteString("effort", effort);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteString("reasoning_effort", effort);
                }
            }

            if (_isOpenRouter && !jsonObjectMode)
            {
                // "Only use providers that support all parameters in your request."
                // https://openrouter.ai/docs/features/provider-routing
                //
                // Without it OpenRouter is free to route a json_schema request to a
                // backend that silently IGNORES response_format, which is the worst
                // of the three outcomes: not an error, just a worse tone, and on a
                // model-comparison eval it would be scored as the model's fault.
                // Omitted in the fallback body on purpose — there the whole point
                // is that the request does not need the capability.
                writer.WritePropertyName("provider");
                writer.WriteStartObject();
                writer.WriteBoolean("require_parameters", true);
                writer.WriteEndObject();
            }

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            WriteMessage(writer, "system", SystemPromptWithSchema(prompt));
            foreach (var turn in prompt.Messages)
                WriteMessage(writer, turn.Role == ToneDesignRole.Assistant ? "assistant" : "user", turn.Text);
            writer.WriteEndArray();

            writer.WritePropertyName("response_format");
            writer.WriteStartObject();
            if (jsonObjectMode)
            {
                writer.WriteString("type", "json_object");
            }
            else
            {
                writer.WriteString("type", "json_schema");
                writer.WritePropertyName("json_schema");
                writer.WriteStartObject();
                writer.WriteString("name", prompt.SchemaName);
                writer.WriteBoolean("strict", true);
                writer.WritePropertyName("schema");
                using (var schema = JsonDocument.Parse(prompt.JsonSchema))
                {
                    schema.RootElement.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The system prompt with the schema and a "JSON only" instruction
    /// appended — the half that survives a server which ignores
    /// <c>response_format</c>.</summary>
    internal static string SystemPromptWithSchema(ToneDesignPrompt prompt)
    {
        var sb = new StringBuilder(prompt.System.Length + prompt.JsonSchema.Length + 256);
        sb.AppendLine(prompt.System);
        sb.AppendLine();
        sb.AppendLine("# Output format");
        sb.AppendLine();
        sb.AppendLine("Reply with a single JSON object and NOTHING else — no prose before or after it,");
        sb.AppendLine("no markdown code fence. It must validate against this JSON Schema:");
        sb.AppendLine();
        sb.AppendLine(prompt.JsonSchema);
        return sb.ToString();
    }

    private static void WriteMessage(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    /// <summary>Pulls the assistant text, the usage and the finish reason out of a
    /// chat-completions response. Tolerant by design: a local server may omit
    /// <c>usage</c> entirely, and one that does must not cost the user their
    /// tone.</summary>
    internal static (string? Text, AiUsage Usage, string? FinishReason) ParseReply(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        string? text = null;
        string? finish = null;
        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var contentElement))
            {
                text = contentElement.ValueKind == JsonValueKind.String ? contentElement.GetString() : null;
            }

            if (choice.TryGetProperty("finish_reason", out var finishElement) &&
                finishElement.ValueKind == JsonValueKind.String)
            {
                finish = finishElement.GetString();
            }
        }

        var model = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString() ?? ""
            : "";

        var input = 0;
        var output = 0;
        decimal? cost = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = ReadInt(usage, "prompt_tokens");
            output = ReadInt(usage, "completion_tokens");
            cost = ReadCost(usage);
        }

        return (text, new AiUsage(model, input, output, cost), finish);
    }

    /// <summary>
    ///     What the server says this request cost, or null when it says nothing.
    ///
    ///     <para>
    ///         OpenRouter returns <c>usage.cost</c> on every reply — in CREDITS,
    ///         which it sells at one US dollar each — alongside
    ///         <c>cost_details.upstream_inference_cost</c>. <b>Its
    ///         <c>usage: { include: true }</c> request parameter is documented as
    ///         DEPRECATED and having no effect</b> ("Full usage details are now
    ///         always included automatically in every response",
    ///         https://openrouter.ai/docs/use-cases/usage-accounting), so this
    ///         reads the field and sends nothing to ask for it: a deprecated
    ///         no-op parameter on the wire is a thing a later reader has to
    ///         re-research to delete.
    ///     </para>
    ///     <para>
    ///         Read for EVERY server, not just OpenRouter. The field is absent
    ///         everywhere else, which is exactly what null means, and a gateway
    ///         that adopts the same field gets priced for free.
    ///     </para>
    /// </summary>
    private static decimal? ReadCost(JsonElement usage)
    {
        if (!usage.TryGetProperty("cost", out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static int ReadInt(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var i)
            ? i
            : 0;

    /// <summary>The model ids a <c>/v1/models</c> payload lists.</summary>
    internal static List<string> ParseModelIds(string payload)
    {
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return ids;

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                    id.GetString() is { Length: > 0 } value)
                {
                    ids.Add(value);
                }
            }
        }
        catch (JsonException)
        {
            // A server that answers /v1/models with something else is reachable but
            // not usable for the listing; the probe still reports the connection.
        }

        return ids;
    }

    private static ToneDesignException ForStatus(HttpStatusCode status, string payload) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ToneDesignException(
            ToneDesignFault.Authentication,
            "The server rejected the API key.",
            Detail(status, payload)),
        HttpStatusCode.TooManyRequests => new ToneDesignException(
            ToneDesignFault.RateLimited,
            "The server is rate-limiting this key. Wait a moment and try again.",
            Detail(status, payload)),
        HttpStatusCode.NotFound => new ToneDesignException(
            ToneDesignFault.Unknown,
            "The server has no chat-completions endpoint at that address.",
            Detail(status, payload)),
        _ => new ToneDesignException(
            ToneDesignFault.Unknown,
            $"The server answered {(int)status}.",
            Detail(status, payload))
    };

    private static string Detail(HttpStatusCode status, string payload)
        => $"HTTP {(int)status}: {payload[..Math.Min(payload.Length, 512)]}";

    private static ToneDesignException Translate(Exception ex) => ex switch
    {
        HttpRequestException or TaskCanceledException or TimeoutException or IOException
            or System.Net.Sockets.SocketException => new ToneDesignException(
                ToneDesignFault.Network,
                "Could not reach the model server. Is it running?",
                OnlineHttp.Describe(ex), ex),
        JsonException => new ToneDesignException(
            ToneDesignFault.InvalidOutput,
            "The model server's reply was not valid JSON.",
            ex.Message, ex),
        _ => new ToneDesignException(
            ToneDesignFault.Unknown,
            "The model server could not complete the request.",
            $"{ex.GetType().Name}: {ex.Message}", ex)
    };
}
