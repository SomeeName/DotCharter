using System.Globalization;
using System.Text;
using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Prior;

/// <summary>
///     <b>Measurement back into words.</b> The matcher answers with a chain and a
///     distance; this asks the model to say what that chain IS — a name for the tone
///     and a sentence or two of why — so a player who does not read module ids still
///     knows what they were handed.
///
///     <para>
///         It owns a SMALL prompt of its own rather than reusing the designer's: the
///         design system prompt is the whole module catalogue plus four few-shots
///         (tens of thousands of tokens) and exists to make a model EMIT a chain.
///         Narration consumes one. Sending the catalogue to describe a rig that is
///         already decided is paying for grounding nobody needs — and it is charged
///         per call, per candidate the player asks about.
///     </para>
///     <para>
///         <b>Provider resolution is shared, not copied.</b> Settings come through
///         <see cref="AiProviderResolution" />, the same answer
///         <see cref="ToneDesignService" /> uses, so the narrator can never think a
///         provider is configured while the designer disagrees.
///     </para>
///     <para>
///         <b>It never fails anything.</b> No provider, a refusal, unparseable JSON,
///         a <see cref="ToneDesignException" /> — all answer null, at Warning. A
///         match the player can hear must not be lost because a sentence about it
///         could not be written. Only <see cref="OperationCanceledException" />
///         propagates.
///     </para>
/// </summary>
public sealed class ToneNarrator : IToneNarrator
{
    /// <summary>The schema's name, as the provider's structured-output config takes
    /// it.</summary>
    public const string SchemaName = "tone_narration";

    /// <summary>The most words a name may carry before it is cut. The prompt asks
    /// for this; the truncation is the net under a model that ignores it, because a
    /// twenty-word "name" is a layout bug in the panel that shows it.</summary>
    public const int MaxNameWords = 12;

    /// <summary>The longest song or artist text printed in the prompt; both come from the project file.</summary>
    private const int SongFieldChars = 120;

    /// <summary>The most words a rationale may carry before it is cut.</summary>
    public const int MaxRationaleWords = 80;

    /// <summary>The JSON Schema a narration reply must satisfy.</summary>
    public static string Schema { get; } = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "rationale"],
          "properties": {
            "name": {
              "type": "string",
              "description": "A name for this tone, at most 12 words."
            },
            "rationale": {
              "type": "string",
              "description": "At most 80 words on what this rig sounds like and what is doing the work."
            }
          }
        }
        """;

    private readonly Func<AppConfig> _config;
    private readonly IToneDesignModelFactory _factory;
    private readonly IAppLogger? _logger;
    private readonly Func<CultureInfo>? _culture;

    /// <summary>Creates the narrator.</summary>
    /// <param name="config">Live settings source — invoked per call, never cached.</param>
    /// <param name="factory">Resolves the provider from those settings.</param>
    /// <param name="logger">Optional app logger.</param>
    /// <param name="culture">The app's display language, so the narration is in the
    /// language the rest of the panel is in. Null falls back to
    /// <see cref="CultureInfo.CurrentUICulture" />.</param>
    public ToneNarrator(
        Func<AppConfig> config,
        IToneDesignModelFactory factory,
        IAppLogger? logger = null,
        Func<CultureInfo>? culture = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger;
        _culture = culture;
    }

    /// <inheritdoc />
    public bool IsAvailable => AiProviderResolution.IsConfigured(_config, _factory);

    /// <inheritdoc />
    public async Task<ToneNarration?> NarrateAsync(
        DcRigPreset rig, ToneSection section, string? songTitle, string? artist, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(section);
        ct.ThrowIfCancellationRequested();
        if (!IsAvailable) return null;

        IToneDesignModel? model = null;
        try
        {
            model = AiProviderResolution.Create(_factory, _config, out _);
            var reply = await model.CompleteAsync(
                BuildPrompt(rig, section, songTitle, artist, Language()), ct).ConfigureAwait(false);
            var narration = Parse(reply);
            if (narration is null)
            {
                _logger?.Warning(AppLogCategory.Application,
                    "The model did not answer with a tone narration; the rig is unchanged.");
            }

            return narration;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ToneDesignException ex)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"No narration for \"{section.Label}\": {ex.Fault} — {ex.Message}", ex);
            return null;
        }
        finally
        {
            (model as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    ///     The prompt: the rig as a readable chain with its knob values in real
    ///     units, the section it is for, and the strict two-field schema.
    ///     <para>
    ///         The rig is rendered by <see cref="DcRigPresetDescriber" /> — the same
    ///         renderer the prior's own text uses — so the model is never shown two
    ///         different accounts of one board.
    ///     </para>
    /// </summary>
    /// <param name="rig">The matched rig.</param>
    /// <param name="section">The part it was matched for.</param>
    /// <param name="songTitle">The song, or null when unknown.</param>
    /// <param name="artist">The artist, or null when unknown.</param>
    /// <param name="language">The language to answer in, as an English name
    /// ("German"). Ignored when null or empty.</param>
    public static ToneDesignPrompt BuildPrompt(
        DcRigPreset rig, ToneSection section, string? songTitle, string? artist, string? language)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(section);

        var system = new StringBuilder(1024);
        system.AppendLine(
            "You name and explain guitar and bass tones. You are given a signal chain that");
        system.AppendLine(
            "an audio matcher already settled on — you are NOT designing it and must not");
        system.AppendLine("suggest changes to it.");
        system.AppendLine();
        system.AppendLine("Rules:");
        system.Append("1. \"name\": at most ").Append(MaxNameWords)
            .AppendLine(" words. Evocative, not a list of modules.");
        system.Append("2. \"rationale\": at most ").Append(MaxRationaleWords)
            .AppendLine(" words on what it sounds like and what is doing the work.");
        system.AppendLine("3. Plain language a player uses. No module ids, no param names, no numbers.");
        system.AppendLine("4. Never claim to have heard the recording; you are reading a chain.");
        if (!string.IsNullOrWhiteSpace(language))
            system.Append("5. Answer in ").Append(language.Trim()).AppendLine(".");
        system.AppendLine(AiUntrustedText.DataRule);
        system.AppendLine("Answer with the JSON object ONLY. No prose, no markdown fences.");

        var user = new StringBuilder(1024);
        user.Append("Part: the ").Append(section.Label).Append(" part, ")
            .Append(SectionClock.Format(section.From)).Append('–').Append(SectionClock.Format(section.To))
            .Append(", ").Append(section.Stem).AppendLine(".");
        if (AiUntrustedText.Clean(songTitle, SongFieldChars) is { Length: > 0 } song) user.Append("Song: ").AppendLine(song);
        if (AiUntrustedText.Clean(artist, SongFieldChars) is { Length: > 0 } by) user.Append("Artist: ").AppendLine(by);
        user.AppendLine();
        user.Append("Chain: ").AppendLine(DcRigPresetDescriber.Chain(rig));
        user.AppendLine();
        user.AppendLine(DcRigPresetDescriber.Describe(rig));

        return new ToneDesignPrompt(
            system.ToString().TrimEnd(),
            [new ToneDesignTurn(ToneDesignRole.User, user.ToString().TrimEnd())],
            Schema,
            SchemaName);
    }

    /// <summary>The reply as a narration, or null when it is not one — a refusal
    /// ("I can't help with that"), an empty name, or anything that is not a JSON
    /// object with both fields. A model that refuses must read as "no narration",
    /// never as a narration whose text is the refusal.</summary>
    /// <param name="reply">The provider's reply.</param>
    public static ToneNarration? Parse(ToneDesignModelReply? reply)
    {
        if (reply is null) return null;
        var json = ToneDraftJson.ExtractJsonObject(reply.Json);
        if (string.IsNullOrWhiteSpace(json)) return null;

        string? name, rationale;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            name = Read(doc.RootElement, "name");
            rationale = Read(doc.RootElement, "rationale");
        }
        catch (JsonException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(name)) return null;
        return new ToneNarration(
            Clip(name, MaxNameWords),
            Clip(rationale ?? "", MaxRationaleWords),
            reply.Usage);
    }

    private static string? Read(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Cuts at a WORD boundary and marks the cut with an ellipsis — a
    /// mid-word cut reads as a rendering bug, and an unmarked one reads as a model
    /// that stopped mid-sentence.</summary>
    private static string Clip(string text, int maxWords)
    {
        var words = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= maxWords
            ? string.Join(' ', words)
            : string.Join(' ', words.Take(maxWords)) + "…";
    }

    /// <summary>The app's language as an English name, which is what a prompt can
    /// use ("Answer in German"). A culture whose English name is unknown answers
    /// null and the rule is left out entirely — "Answer in ." teaches a model
    /// nothing and costs tokens.</summary>
    private string? Language()
    {
        var culture = _culture?.Invoke() ?? CultureInfo.CurrentUICulture;
        var name = culture.TwoLetterISOLanguageName is "iv" or "" ? null : culture.EnglishName;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var cut = name.IndexOf(" (", StringComparison.Ordinal);
        return cut > 0 ? name[..cut] : name;
    }
}
