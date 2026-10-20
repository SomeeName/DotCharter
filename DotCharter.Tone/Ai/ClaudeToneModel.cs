using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace DotCharter.Tone.Ai;

/// <summary>
///     The Claude provider, over the official <c>Anthropic</c> SDK.
///
///     <para>
///         <b>Structured output, not prompt-and-hope.</b> The request carries the
///         <see cref="ToneDraftJson.Schema" /> in <c>OutputConfig.Format</c>, so the
///         reply is schema-valid JSON by construction and
///         <see cref="ToneDraftJson.ExtractJsonObject" /> has nothing to do. The
///         schema still goes to the OpenAI-compatible provider's prompt as well,
///         because a local model may ignore the parameter — here it cannot.
///     </para>
///     <para>
///         <b>Streaming, even though nothing streams to the user.</b> A non-streamed
///         request holds one HTTP response open for the whole generation, and a long
///         reply on a slow link hits the client timeout with nothing to show for it;
///         the streamed form keeps bytes moving and is what the API asks for on long
///         generations. There is no <c>GetFinalMessage</c> helper in the C# SDK
///         (12.55.0), so this accumulates the text deltas itself and reads the stop
///         reason and output usage off the <c>message_delta</c> event.
///     </para>
///     <para>
///         <b>Adaptive thinking.</b> The <c>Thinking</c> parameter is OMITTED:
///         on Claude Opus 5 and 5.5 thinking is on by default and omitting it runs adaptive,
///         while <c>ThinkingConfigEnabled</c>/<c>budget_tokens</c> is removed on that
///         model and returns 400. <c>Temperature</c> is omitted for the same reason.
///         Depth is steered with <c>OutputConfig.Effort</c> instead, and only when the
/// charter picked a level in Settings ▸ AI: Default sends no effort,
///         which is the model's own default (<c>medium</c> on Opus 5.5).
///     </para>
///     <para>The API key is held in a field, is never logged, and never reaches
///     <see cref="ToneDesignException.Detail" />.</para>
/// </summary>
public sealed class ClaudeToneModel : IToneDesignModel
{
    /// <summary>The model used when the settings do not name one.</summary>
    public const string DefaultModelId = "claude-opus-5-5";

    /// <summary>A tone is a few hundred tokens of JSON; 4096 is generous headroom
    /// and still bounds a runaway reply.</summary>
    public const int MaxTokens = 4096;

    private readonly AnthropicClient _client;
    private readonly IAppLogger? _logger;
    private readonly string _modelId;
    private readonly Effort? _effort;

    /// <summary>Creates the provider. <paramref name="handler" /> is the tests'
    /// seam — when supplied, the SDK client is pointed at an
    /// <see cref="HttpClient" /> over it instead of the default transport.</summary>
    /// <param name="settings">Provider settings; <c>ModelId</c> overrides
    /// <see cref="DefaultModelId" />.</param>
    /// <param name="apiKey">The Anthropic API key. Never logged.</param>
    /// <param name="logger">Optional app logger for usage lines.</param>
    /// <param name="handler">Optional transport, for tests.</param>
    public ClaudeToneModel(
        AiProviderSettings settings,
        string apiKey,
        IAppLogger? logger = null,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _logger = logger;
        _modelId = string.IsNullOrWhiteSpace(settings.ModelId) ? DefaultModelId : settings.ModelId.Trim();
        _effort = AiReasoning.WireValue(settings.Effort, AiProviderKind.Claude, _modelId) switch
        {
            "low" => Effort.Low,
            "medium" => Effort.Medium,
            "high" => Effort.High,
            _ => null
        };
        // The handler path goes through OnlineHttp.CreateClient rather than a bare
        // `new HttpClient(handler)`: that is the app's ONE client factory, it is what
        // OnlineHttpPolicyTests enforces, and it carries the bounded connection pool
        // and the response-size cap. The cap does not touch the STREAMED body (that
        // is read with ResponseHeadersRead and never buffered), and the timeout is
        // infinite because the SDK and the CancellationToken own the deadline here.
        _client = handler is null
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient
            {
                ApiKey = apiKey,
                HttpClient = Online.OnlineHttp.CreateClient(Timeout.InfiniteTimeSpan, handler)
            };
    }

    /// <inheritdoc />
    public AiProviderKind Kind => AiProviderKind.Claude;

    /// <inheritdoc />
    public async Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        // Before the call, not just around it — see the same guard in
        // OpenAiCompatibleToneModel.
        ct.ThrowIfCancellationRequested();

        var parameters = BuildParams(prompt);
        var text = new StringBuilder();
        var inputTokens = 0;
        var outputTokens = 0;
        string? stopReason = null;

        try
        {
            await foreach (var streamEvent in _client.Messages.CreateStreaming(parameters, ct).ConfigureAwait(false))
            {
                if (streamEvent.TryPickStart(out var start))
                {
                    inputTokens = (int)start.Message.Usage.InputTokens;
                    outputTokens = (int)start.Message.Usage.OutputTokens;
                }
                else if (streamEvent.TryPickContentBlockDelta(out var blockDelta))
                {
                    if (blockDelta.Delta.TryPickText(out var textDelta)) text.Append(textDelta.Text);
                }
                else if (streamEvent.TryPickDelta(out var messageDelta))
                {
                    stopReason = messageDelta.Delta.StopReason?.Raw();
                    outputTokens = (int)messageDelta.Usage.OutputTokens;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }

        if (string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToneDesignException(
                ToneDesignFault.ModelRefused,
                "Claude declined to design this tone.",
                "stop_reason=refusal");
        }

        var json = ToneDraftJson.ExtractJsonObject(text.ToString());
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ToneDesignException(
                ToneDesignFault.InvalidOutput,
                "Claude's reply did not contain a tone.",
                $"stop_reason={stopReason ?? "?"}, {text.Length} characters");
        }

        var usage = new AiUsage(_modelId, inputTokens, outputTokens);
        _logger?.Info(AppLogCategory.Application,
            $"AI tone design (Claude {_modelId}): {usage.InputTokens} in / {usage.OutputTokens} out tokens.");

        return new ToneDesignModelReply(json, usage);
    }

    /// <inheritdoc />
    public async Task<AiProbeResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _modelId,
                MaxTokens = 16,
                Messages = [new() { Role = Role.User, Content = "Reply with the single word: ok" }]
            }, ct).ConfigureAwait(false);

            // The model the API ANSWERED with, not the one we asked for: an alias
            // ("claude-opus-5") resolves to a dated id, and showing the resolved one
            // is what tells a user their alias reached what they expected.
            var answered = response.Model.Raw();
            return new AiProbeResult(true, $"Connected to {answered}.", answered);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var fault = Translate(ex);
            return new AiProbeResult(false, fault.Message, null);
        }
    }

    /// <summary>The request for <paramref name="prompt" />. Internal so
    /// <c>ClaudeToneModelTests</c> can assert the shape without a transport.</summary>
    internal MessageCreateParams BuildParams(ToneDesignPrompt prompt)
    {
        var messages = new List<MessageParam>();
        foreach (var turn in prompt.Messages)
        {
            messages.Add(new MessageParam
            {
                Role = turn.Role == ToneDesignRole.Assistant ? Role.Assistant : Role.User,
                Content = turn.Text
            });
        }

        return new MessageCreateParams
        {
            Model = _modelId,
            MaxTokens = MaxTokens,
            // The system prompt is the catalogue + rules + examples and is
            // byte-identical on every request, so it is marked cacheable. Ephemeral
            // caching is what makes a ~7.5k-token grounding block cost a tenth of its
            // tokens from the second request on.
            //
            // The TTL is the ONE-HOUR tier, not the 5-minute default, and that is
            // arithmetic rather than taste. A tone session is a player listening,
            // playing, then asking for "less gain" — minutes apart, which is a 5m
            // MISS, and a miss costs a 1.25x cache WRITE and then reads nothing:
            // ~16% MORE than never caching at all. The 1h write is 2x and holds for
            // the whole session, so a SECOND request inside the hour already pays
            // for it (2x + 0.1x against 2x uncached) and a session is many more.
            // Multipliers: https://platform.claude.com/docs/en/build-with-claude/prompt-caching
            System = new List<TextBlockParam>
            {
                new()
                {
                    Text = prompt.System,
                    CacheControl = new CacheControlEphemeral { Ttl = Ttl.Ttl1h }
                }
            },
            OutputConfig = OutputConfigFor(prompt),
            Messages = messages
            // Thinking omitted on purpose: adaptive is the default on Opus 5 and an
            // explicit budget is a 400 there. Temperature likewise.
        };
    }

    /// <summary>The structured-output format, plus the effort when one is chosen. Two
    /// initializers rather than a null <c>Effort</c>: an absent effort must be ABSENT on
    /// the wire, which is what "Default" promises.</summary>
    private OutputConfig OutputConfigFor(ToneDesignPrompt prompt)
    {
        var format = new JsonOutputFormat { Schema = SchemaAsDictionary(prompt.JsonSchema) };
        return _effort is { } effort
            ? new OutputConfig { Format = format, Effort = effort }
            : new OutputConfig { Format = format };
    }

    /// <summary>The schema as the SDK wants it — a property bag of
    /// <see cref="JsonElement" />, not a string.</summary>
    private static Dictionary<string, JsonElement> SchemaAsDictionary(string schemaJson)
    {
        using var doc = JsonDocument.Parse(schemaJson);
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in doc.RootElement.EnumerateObject())
            map[property.Name] = property.Value.Clone();
        return map;
    }

    /// <summary>
    ///     Maps an SDK exception onto the one exception type the UI handles.
    ///     Ordered most-specific-first, which is the whole reason the SDK defines a
    ///     class per status: a single catch-all would report a bad API key and a
    ///     dead network as the same "something went wrong".
    /// </summary>
    private static ToneDesignException Translate(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException => new ToneDesignException(
            ToneDesignFault.Authentication,
            "Claude rejected the API key. Check it under Settings.",
            Redacted(ex), ex),
        AnthropicForbiddenException => new ToneDesignException(
            ToneDesignFault.Authentication,
            "This API key is not allowed to use that model.",
            Redacted(ex), ex),
        AnthropicRateLimitException => new ToneDesignException(
            ToneDesignFault.RateLimited,
            "Claude is rate-limiting this key. Wait a moment and try again.",
            Redacted(ex), ex),
        AnthropicIOException => new ToneDesignException(
            ToneDesignFault.Network,
            "Could not reach Claude. Check the connection.",
            Redacted(ex), ex),
        HttpRequestException or TaskCanceledException or TimeoutException => new ToneDesignException(
            ToneDesignFault.Network,
            "Could not reach Claude. Check the connection.",
            Redacted(ex), ex),
        AnthropicApiException => new ToneDesignException(
            ToneDesignFault.Unknown,
            "Claude could not complete the request.",
            Redacted(ex), ex),
        _ => new ToneDesignException(
            ToneDesignFault.Unknown,
            "Claude could not complete the request.",
            Redacted(ex), ex)
    };

    /// <summary>The provider's own text for the log, with the type name in front so
    /// a log line says WHICH exception it was. The key is never part of an SDK
    /// message, and this is the one place that would carry it if it ever were.</summary>
    private static string Redacted(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
