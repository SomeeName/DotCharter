using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Generate;

/// <summary>
///     The shipping <see cref="IRsToneMapper" />: the app's configured tone-design
///     provider (Claude API, an OpenAI-compatible server, or the Claude Code CLI —
///     whichever the user chose under Settings ▸ AI), given the Rocksmith catalogue
///     as its system prompt and a strict JSON schema as its reply shape.
///     <para>
///         <b>It reuses <see cref="IToneDesignModel" /> and nothing else from the
///         design lane.</b> Not <c>IToneDesignService</c>: that service's system
///         prompt is the app's OWN DSP module catalogue plus four few-shot rigs,
///         which is grounding for emitting a <c>DcRigPreset</c> and pure cost — and
///         active misdirection — for emitting Rocksmith gear. Own prompt, own schema,
///         own validator; same provider resolution, same transport, same key store.
///     </para>
///     <para>
///         <b>The model never sees audio.</b> It sees eleven measured numbers and the
///         charter's text. That is what makes a disagreement with its answer
///         actionable: the features are printed in the run report and a reader can
///         say "a tilt of −3 dB/octave is not a closed-back 4x12".
///     </para>
/// </summary>
public sealed class LanguageModelRsToneMapper : IRsToneMapper
{
    private readonly Func<IToneDesignModel> _model;
    private readonly RsGearCatalog _catalog;
    private readonly string _describe;

    /// <summary>Builds the mapper over an already-resolved model.</summary>
    /// <param name="model">Resolved per call, so a provider change in Settings takes
    /// effect without rebuilding the container.</param>
    /// <param name="describe">One line naming provider + model, for the report.</param>
    /// <param name="catalog">The gear catalogue; null for
    /// <see cref="RsGearCatalog.Shared" />.</param>
    public LanguageModelRsToneMapper(
        Func<IToneDesignModel> model, string describe, RsGearCatalog? catalog = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _describe = describe;
        _catalog = catalog ?? RsGearCatalog.Shared;
    }

    /// <inheritdoc />
    public string Describe() => _describe;

    /// <inheritdoc />
    public async Task<RsToneMapping> MapAsync(RsToneMapRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt = RsToneGeneratePrompt.Build(request);
        var model = _model();
        var reply = await model.CompleteAsync(prompt, ct).ConfigureAwait(false);

        var tone = RsToneReplyReader.Read(
            reply.Json, request.ToneKey, request.ToneName, _catalog, out var rationale);

        var sources = new List<string>(2);
        if (request.Features is not null) sources.Add(ToneGenerationSources.Audio);
        if (!string.IsNullOrWhiteSpace(request.Context) || !string.IsNullOrWhiteSpace(request.Instruction))
            sources.Add(ToneGenerationSources.Context);

        return new RsToneMapping(tone, reply.Usage.ModelId, reply.Usage, sources, rationale);
    }
}
