using System.Globalization;
using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     The opt-in gate for the LIVE eval.
///     <para>
///         A research harness is opt-in or it is part of the gate — there is no
///         third state. <c>GND_IDMT_ROOT</c> is the model this follows: without
///         <see cref="EnableVariable" /> set to <c>1</c> the live test
///         <c>Assert.Skip</c>s with the exact variables it wanted, so a default
///         gate never touches the network, never spends money, and never passes
///         vacuously while pretending it ran.
///     </para>
/// </summary>
/// <param name="Provider">Which backend to drive.</param>
/// <param name="ModelId">The model id, verbatim.</param>
/// <param name="BaseUrl">Base URL; required for <see cref="AiProviderKind.OpenAiCompatible" />.</param>
/// <param name="ApiKey">The key, or null for a local OpenAI-compatible server that
/// wants none. Never logged, never written to an artifact.</param>
/// <param name="MaxSongs">Cap on songs, from <see cref="MaxVariable" />.</param>
/// <param name="IncludeShortPrompt">Also send the sparse prompt, from <see cref="ShortVariable" />.</param>
/// <param name="UseAnchor">Run in ANCHORED mode, from <see cref="AnchorVariable" />.
/// Default FALSE, and it must stay false for any number that is quoted as a
/// score — see <see cref="AnchorVariable" />.</param>
/// <param name="TranscriptDir">Non-null for the TRANSCRIPT provider only: the
/// directory prompts are written to and replies read from. See
/// <see cref="TranscriptDirVariable" />.</param>
/// <param name="InputUsdPerMTok">Input list price for THIS model, USD per million
/// tokens, from <see cref="PriceInVariable" />. Null keeps
/// <see cref="ToneEvalOptions" />' Opus default.</param>
/// <param name="OutputUsdPerMTok">Output list price, from
/// <see cref="PriceOutVariable" />.</param>
public sealed record ToneEvalEnvironment(
    AiProviderKind Provider,
    string ModelId,
    string? BaseUrl,
    string? ApiKey,
    int MaxSongs,
    bool IncludeShortPrompt,
    bool UseAnchor = false,
    string? TranscriptDir = null,
    double? InputUsdPerMTok = null,
    double? OutputUsdPerMTok = null)
{
    /// <summary>Master switch. Must be exactly <c>1</c>.</summary>
    public const string EnableVariable = "DOTCHARTER_AI_EVAL";

    /// <summary><c>claude</c>, <c>openai</c> or <c>transcript</c>.</summary>
    public const string ProviderVariable = "DOTCHARTER_AI_PROVIDER";

    /// <summary>
    ///     The transcript directory. REQUIRED for — and read only by —
    ///     <c>DOTCHARTER_AI_PROVIDER=transcript</c>, the provider that writes each
    ///     prompt to a file and reads the reply back from one instead of making an
    ///     HTTP call. See <see cref="TranscriptToneDesignModel" />: it replaces the
    ///     TRANSPORT and nothing else, which is what makes a number produced that
    ///     way comparable with a wire run — and is the only way to grade a model on
    ///     a machine with no key and no local server.
    /// </summary>
    public const string TranscriptDirVariable = "DOTCHARTER_AI_TRANSCRIPT_DIR";

    /// <summary>The model id.</summary>
    public const string ModelVariable = "DOTCHARTER_AI_MODEL";

    /// <summary>Base URL. Required for <c>openai</c>, optional for <c>claude</c>.</summary>
    public const string BaseUrlVariable = "DOTCHARTER_AI_BASE_URL";

    /// <summary>The key, checked first. Required for <c>claude</c>; OPTIONAL for an
    /// OpenAI-compatible server, because LM Studio and Ollama want none — which is
    /// the factory's own rule, mirrored here rather than re-decided.</summary>
    public const string KeyVariable = "DOTCHARTER_AI_KEY";

    /// <summary>The key, checked second — the name the Anthropic SDK already uses.</summary>
    public const string AnthropicKeyVariable = "ANTHROPIC_API_KEY";

    /// <summary>Song cap. Defaults to <see cref="DefaultMaxSongs" />.</summary>
    public const string MaxVariable = "DOTCHARTER_AI_EVAL_MAX";

    /// <summary>Set to <c>1</c> to send the sparse prompt as well, doubling cost.</summary>
    public const string ShortVariable = "DOTCHARTER_AI_EVAL_SHORT";

    /// <summary>
    ///     Set to <c>1</c> for the ANCHORED run. **Off by default, and that is not
    ///     a convenience default — it is the only correct one.**
    ///     <para>
    ///         The scorer grades a designed chain against the song's own converted
    ///         Rocksmith tone, and grounding stage 2 hands the model THAT VERY
    ///         TONE. An anchored run is therefore scoring the model on how well it
    ///         copies the answer it was given: a number that goes up and means
    ///         nothing about designing a tone for a song the library does not have,
    ///         which is the case the feature exists for. A default run passes
    ///         <c>UseLibraryTones = false</c> to the real service, so the leak
    ///         cannot happen by forgetting.
    ///     </para>
    ///     <para>
    ///         The anchored run is still worth having, as an UPPER BOUND: it says
    ///         what the pipeline can reach when retrieval is perfect, which bounds
    ///         how much of the remaining gap is the model's and how much is the
    ///         converter's and the scorer's. The report header says which mode ran
    ///         and labels an anchored number as "not a generalisation score".
    ///     </para>
    /// </summary>
    public const string AnchorVariable = "DOTCHARTER_AI_EVAL_ANCHOR";

    /// <summary>
    ///     This model's INPUT list price, USD per million tokens. Optional, and it
    ///     exists for one reason: <c>ToneEvalOptions</c> prices every run at Opus
    ///     list ($5/$25), which is right for Opus and wrong by two orders of
    ///     magnitude for the cheap models a comparison sweep is mostly made of —
    ///     `deepseek-v4-flash` is $0.07/$0.13. Unset or unparseable keeps the Opus
    ///     default, and the report always prints the price it used, so a stale
    ///     number is visible rather than silently wrong.
    ///     <c>tools/ai-eval/Compare-Models.ps1</c> fills both from the OpenRouter
    ///     models endpoint.
    /// </summary>
    public const string PriceInVariable = "DOTCHARTER_AI_EVAL_PRICE_IN";

    /// <summary>This model's OUTPUT list price, USD per million tokens. See
    /// <see cref="PriceInVariable" />.</summary>
    public const string PriceOutVariable = "DOTCHARTER_AI_EVAL_PRICE_OUT";

    /// <summary>How many songs the live eval runs when <see cref="MaxVariable" />
    /// is unset. Twenty is a few dollars at Opus list price.</summary>
    public const int DefaultMaxSongs = 20;

    /// <summary>The skip message, naming every variable, so a reader of a skipped
    /// run knows exactly what to set.</summary>
    public const string SkipMessage =
        "live AI tone eval is opt-in: set " + EnableVariable + "=1, " +
        ProviderVariable + "=claude|openai|transcript, " + ModelVariable + "=<model id>, " +
        TranscriptDirVariable + "=<dir> (transcript only), " +
        BaseUrlVariable + "=<url> (openai only), and — for claude — a key in " +
        KeyVariable + " or " + AnthropicKeyVariable + " (an OpenAI-compatible " +
        "server may need none). Optional: " +
        MaxVariable + "=<songs, default 20>, " + ShortVariable + "=1, " +
        AnchorVariable + "=1 (anchored upper bound; NOT a generalisation score).";

    /// <summary>
    ///     Reads the environment. Returns false with a reason whenever the live run
    ///     is not fully configured — a half-configured run is a skip, never a
    ///     failure, because "I forgot to export the key" is not a regression.
    /// </summary>
    public static bool TryResolve(out ToneEvalEnvironment? environment, out string reason)
    {
        environment = null;

        if (Read(EnableVariable) != "1")
        {
            reason = SkipMessage;
            return false;
        }

        var providerText = (Read(ProviderVariable) ?? "").ToLowerInvariant();

        // The transcript provider answers AS Claude — it is the transport that was
        // replaced, not the model — so it resolves to the same kind and differs only
        // by carrying a directory. That keeps every downstream switch on
        // AiProviderKind untouched; ToOptions() labels the REPORT "transcript" so an
        // artifact can never be mistaken for a wire run.
        var isTranscript = providerText == "transcript";
        var provider = providerText switch
        {
            "claude" or "anthropic" or "transcript" => AiProviderKind.Claude,
            "openai" or "openaicompatible" => AiProviderKind.OpenAiCompatible,
            _ => AiProviderKind.None
        };

        if (provider == AiProviderKind.None)
        {
            reason = $"{ProviderVariable} must be 'claude' or 'openai' — or 'transcript' " +
                     $"for the offline transcript provider (was '{providerText}'). {SkipMessage}";
            return false;
        }

        var model = Read(ModelVariable);
        if (string.IsNullOrWhiteSpace(model))
        {
            reason = $"{ModelVariable} is not set. {SkipMessage}";
            return false;
        }

        var baseUrl = Read(BaseUrlVariable);
        if (provider == AiProviderKind.OpenAiCompatible && string.IsNullOrWhiteSpace(baseUrl))
        {
            reason = $"{BaseUrlVariable} is required for an OpenAI-compatible provider. {SkipMessage}";
            return false;
        }

        var transcriptDir = isTranscript ? Read(TranscriptDirVariable) : null;
        if (isTranscript && string.IsNullOrWhiteSpace(transcriptDir))
        {
            reason = $"{TranscriptDirVariable} is required for the transcript provider. {SkipMessage}";
            return false;
        }

        var key = Read(KeyVariable);
        if (string.IsNullOrWhiteSpace(key)) key = Read(AnthropicKeyVariable);

        // Claude cannot be reached without one; a local OpenAI-compatible server
        // usually wants none, and demanding one here would block the FREE way to
        // run this eval. Same split ToneDesignModelFactory.Create makes.
        // …and a TRANSCRIPT never needs one either: there is no endpoint to
        // authenticate against, only a directory.
        if (provider == AiProviderKind.Claude && !isTranscript && string.IsNullOrWhiteSpace(key))
        {
            reason = $"no key in {KeyVariable} or {AnthropicKeyVariable}. {SkipMessage}";
            return false;
        }

        var max = DefaultMaxSongs;
        var maxText = Read(MaxVariable);
        if (!string.IsNullOrWhiteSpace(maxText) &&
            int.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            max = parsed;
        }

        environment = new ToneEvalEnvironment(
            provider, model.Trim(), string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim(),
            string.IsNullOrWhiteSpace(key) ? null : key,
            max, Read(ShortVariable) == "1", Read(AnchorVariable) == "1",
            string.IsNullOrWhiteSpace(transcriptDir) ? null : transcriptDir.Trim(),
            ReadPrice(PriceInVariable), ReadPrice(PriceOutVariable));
        reason = "";
        return true;
    }

    /// <summary>
    ///     The non-secret half, ready for a model factory — with
    ///     <see cref="AiProviderSettings.UseLibraryTones" /> pinned to
    ///     <see cref="UseAnchor" />. This is where the leak is closed: the DEFAULT
    ///     eval run tells the real service not to ground itself in the library,
    ///     which is the same library the ground truth came out of.
    /// </summary>
    public AiProviderSettings Settings => new(Provider, ModelId, BaseUrl, UseAnchor);

    /// <summary>The run options this environment implies.</summary>
    public ToneEvalOptions ToOptions()
    {
        var options = new ToneEvalOptions
        {
            MaxSongs = MaxSongs,
            IncludeShortPrompt = IncludeShortPrompt,
            Label = ModelId,
            Provider = TranscriptDir is null ? Provider.ToString() : "transcript",
            Anchored = UseAnchor
        };

        // Additive and one-sided-safe: half a price pair would make the report's
        // dollars a mix of two models' prices, which is worse than the honest Opus
        // default, so BOTH have to be present.
        if (InputUsdPerMTok is { } input && OutputUsdPerMTok is { } output)
            options = options with { InputUsdPerMTok = input, OutputUsdPerMTok = output };

        return options;
    }

    private static string? Read(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>A price, or null when it is unset, unparseable or not positive —
    /// never a throw, because a research harness that refuses to start over a
    /// cosmetic column is worse than one that prints the default price it used.</summary>
    private static double? ReadPrice(string name)
    {
        var text = Read(name);
        if (string.IsNullOrWhiteSpace(text)) return null;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
               && value > 0 && double.IsFinite(value)
            ? value
            : null;
    }
}
