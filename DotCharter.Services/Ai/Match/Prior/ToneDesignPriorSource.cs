using DotCharter.Localization;
using System.Text;
using DotCharter.Tone.Ai;

namespace DotCharter.Services.Ai.Match.Prior;

/// <summary>
///     <b>Knowledge before measurement.</b> The language model is asked what the rig
///     for a section probably is, and its answer becomes the matcher's starting point
///     — retrieval seed and CMA-ES initial mean — instead of the matcher starting from
///     whatever the library happens to contain.
///
///     <para>
///         It is a thin adapter over <see cref="IToneDesignService" /> and owns NO
///         prompt of its own: the design service's system prompt already carries this
///         build's module catalogue, the wiring rules and the four few-shots
///         (including the bass one, which is what stops a model reaching for
///         <c>amp</c> on a bass request). A second prompt here would be a second
///         place those rules can rot.
///     </para>
///     <para>
///         <b>It never fails a match.</b> A missing provider answers null before a
///         request is built; a <see cref="ToneDesignException" /> is logged at Warning
///         and answers null, so the pure-audio path runs exactly as it would have.
///         The ONE exception that propagates is
///         <see cref="OperationCanceledException" /> — a user Cancel is not a prior
///         that could not be obtained, it is a match that is over.
///     </para>
/// </summary>
public sealed class ToneDesignPriorSource : IToneMatchPriorSource
{
    private readonly IToneDesignService _design;
    private readonly ILocalizationService _localization;
    private readonly Func<AiProviderSettings>? _settings;
    private readonly IAppLogger? _logger;

    /// <summary>Creates the prior source.</summary>
    /// <param name="design">The design service the guess is asked of.</param>
    /// <param name="localization">Resolves the status and progress text the user reads.</param>
    /// <param name="settings">Optional live provider settings, used ONLY to tag the
    /// prior's <see cref="ToneMatchPrior.Source" /> with which model answered
    /// ("ai:claude"). Null tags it "ai" — honest about not knowing, rather than
    /// naming a provider that may not be the one that answered.</param>
    /// <param name="logger">Optional app logger.</param>
    public ToneDesignPriorSource(
        IToneDesignService design,
        ILocalizationService localization,
        Func<AiProviderSettings>? settings = null,
        IAppLogger? logger = null)
    {
        _design = design ?? throw new ArgumentNullException(nameof(design));
        _localization = localization;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable => _design.IsConfigured;

    /// <inheritdoc />
    public async Task<ToneMatchPrior?> GetPriorAsync(
        ToneSection section,
        string? songTitle,
        string? artist,
        IProgress<string>? status,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(section);
        ct.ThrowIfCancellationRequested();
        if (!IsAvailable) return null;

        var request = BuildRequest(section, songTitle, artist);
        status?.Report(_localization["Progress_AskingStartingPoint"]);
        try
        {
            var result = await _design.DesignAsync(request, status, ct).ConfigureAwait(false);
            var source = _settings is null
                ? "ai"
                : AiProviderResolution.SourceTag(_settings().Kind);
            _logger?.Info(AppLogCategory.Application,
                $"Tone prior for \"{section.Label}\" ({section.Stem}): " +
                $"{DcRigPresetDescriber.Chain(result.Tone.Rig)} [{source}].");
            return new ToneMatchPrior(result.Tone.Rig, source);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ToneDesignException ex)
        {
            // A prior is an OPTIMISATION. Losing it costs the match its head start
            // and nothing else, so it is a Warning and a null — never a failed match.
            _logger?.Warning(AppLogCategory.Application,
                $"No tone prior for \"{section.Label}\": {ex.Fault} — {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    ///     The request the model is asked. The description NAMES THE PART — "the
    ///     chorus part, 1:05–1:32, guitar" — because a section is the whole context
    ///     the model gets: it never hears the audio, so the label, the span and the
    ///     STEM are the entire difference between a guitar answer and a bass one.
    ///     <para>
    ///         The stem is stated twice on purpose: once inside the sentence and once
    ///         as its own line. Rule 6 of the design prompt already forbids a guitar
    ///         amp on a bass request, and the bass few-shot teaches the shape — but
    ///         both of those only fire if the request SAYS it is bass.
    ///     </para>
    /// </summary>
    /// <param name="section">The part to design for.</param>
    /// <param name="songTitle">The song, or null when unknown.</param>
    /// <param name="artist">The artist, or null when unknown.</param>
    public static ToneDesignRequest BuildRequest(ToneSection section, string? songTitle, string? artist)
    {
        ArgumentNullException.ThrowIfNull(section);
        var sb = new StringBuilder(256);
        sb.Append("A rig for the ").Append(Label(section))
            .Append(" part, ").Append(SectionClock.Format(section.From)).Append('–').Append(SectionClock.Format(section.To))
            .Append(", ").Append(section.Stem).AppendLine(".");
        sb.Append("Instrument: ").Append(section.Stem).AppendLine(".");
        sb.AppendLine(
            "You cannot hear it; give the rig this part most likely used, as a starting " +
            "point an audio matcher will then refine.");

        return new ToneDesignRequest(
            sb.ToString().TrimEnd(),
            [],
            CurrentTone: null,
            songTitle,
            artist);
    }

    /// <summary>A section with no label of its own is described by its stem rather
    /// than by an empty string — "the  part, 0:10–0:30" reads as a bug and a model
    /// asked about nothing answers about nothing.</summary>
    private static string Label(ToneSection section)
        => string.IsNullOrWhiteSpace(section.Label) ? section.Stem : section.Label.Trim();
}
