using System.Diagnostics;
using System.Text.Json;
using DotCharter.Localization;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tone.Ai;

/// <summary>
///     The feature's one entry point: assemble the prompt, ask the model, validate
///     the reply against THIS build's catalogue, and hand back a preset that is
///     byte-for-byte what the ordinary save/apply path would have produced.
///
///     <para>
///         <b>It reads settings LIVE.</b> <see cref="IsConfigured" /> and every
///         design call go through the <c>Func&lt;AppConfig&gt;</c> handed to the
///         constructor, so switching provider in Settings takes effect on the next
///         request rather than the next launch. Nothing is cached across calls
///         except the system prompt, which is build-constant.
///     </para>
///     <para>
///         <b>The result goes through <see cref="DcRigPresetPreparation" />.</b>
///         That is the ONE preset→rig pipeline the live apply and the offline re-amp
///         share, so an AI tone and a saved tone are prepared identically: every
///         omitted param pinned to its catalogue default (without which a sparse
///         chain inherits the values of whatever was applied before it). It does NOT
///         loudness-normalize, and that is correct and deliberate: the normalizer
///         runs on <see cref="DspToneOrigin.BuiltIn" /> presets only, and an AI tone
///         is a USER preset. Applying it here as well would be the double-apply the
///         pipeline's own contract exists to prevent.
///     </para>
///     <para>
///         <b>Two routes, one pipeline</b>. A provider design is
///         <c>PrepareAsync</c> → <see cref="IToneDesignModel.CompleteAsync" /> →
///         <c>ProcessReplyAsync</c>. The "ChatGPT (copy &amp; paste)" route is the
///         same two halves with the player in the middle:
///         <see cref="BuildManualPromptAsync" /> is <c>PrepareAsync</c> flattened
///         to text (<see cref="ToneDesignManualPrompts" />), and
///         <see cref="ImportReplyAsync" /> extracts the pasted answer's JSON and
///         hands it to <c>ProcessReplyAsync</c>. Neither needs a provider — a
///         player with a chat subscription and no API key is who it is for.
///     </para>
///     <para>
///         <b>No raw SDK or HTTP exception escapes.</b> Every provider fault is
///         already a <see cref="ToneDesignException" /> by the time it gets here;
///         anything else is wrapped as <see cref="ToneDesignFault.Unknown" />.
///         <see cref="OperationCanceledException" /> passes through untouched — a
///         user Cancel is not a failure, and a service that reported it as one would
///         put an error on screen for a button the user pressed.
///     </para>
/// </summary>
public sealed class ToneDesignService : IToneDesignService
{
    private readonly Func<RigConfig> _config;
    private readonly IToneDesignModelFactory _factory;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger? _logger;
    private readonly Func<string?>? _namCaptureDirectory;
    private readonly IToneAnchorLibrary? _anchors;
    private readonly Func<NamCaptureCatalog>? _captures;
    private readonly INamCaptureSource? _captureSource;

    /// <summary>Creates the service.</summary>
    /// <param name="config">Live settings source — invoked per call, never cached.</param>
    /// <param name="factory">Resolves a provider from the settings.</param>
    /// <param name="localization">Resolves the status text the user reads.</param>
    /// <param name="logger">Optional app logger.</param>
    /// <param name="namCaptureDirectory">Optional NAM capture root, for the shared
    /// preparation step. Null is fine: the designer never emits a capture module.</param>
    /// <param name="anchors">Optional grounding stage 2 — the player's own Rocksmith
    /// library. Null means the feature is not in this build; it is also skipped
    /// whenever <see cref="AiProviderSettings.UseLibraryTones" /> is off or the
    /// request names no song.</param>
    /// <param name="captures">Optional: the CLOSED list of neural captures a design
    /// may choose from — the bundled set plus the player's own downloads, read live
    /// so a capture downloaded mid-session is offered on the next request. Null (the
    /// default) means no captures, which is the same prompt a player with an empty
    /// library gets: no capture block, and <c>namamp</c> off the table. Invoked once
    /// per design, never cached, for the same reason <paramref name="config" /> is.</param>
    /// <param name="captureSource">Optional: where a capture that is NOT in the
    /// library comes from. While it is available (TONE3000 signed in) the prompt
    /// offers <see cref="ToneDraft.CaptureSearch" />, and a reply that uses it has the
    /// capture downloaded and its <c>namamp</c> pointed at it before validation.
    /// Null means designs choose from the library only, as before.</param>
    public ToneDesignService(
        Func<RigConfig> config,
        IToneDesignModelFactory factory,
        ILocalizationService localization,
        IAppLogger? logger = null,
        Func<string?>? namCaptureDirectory = null,
        IToneAnchorLibrary? anchors = null,
        Func<NamCaptureCatalog>? captures = null,
        INamCaptureSource? captureSource = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _localization = localization;
        _logger = logger;
        _namCaptureDirectory = namCaptureDirectory;
        _anchors = anchors;
        _captures = captures;
        _captureSource = captureSource;
    }

    /// <summary>The settings this service last PUBLISHED — what
    /// <see cref="NotifyConfigurationChanged" /> compares against. Seeded lazily on
    /// the first read so constructing the service never loads the config.</summary>
    private AiProviderSettings? _published;

    /// <inheritdoc />
    public event EventHandler? ConfiguredChanged;

    /// <summary>The settings currently in force. Resolved through
    /// <see cref="AiProviderResolution" />, which is the ONE answer to "which
    /// provider is in force" — the narrator resolves the same way rather than
    /// keeping a second copy of this line.</summary>
    public AiProviderSettings Settings => AiProviderResolution.SettingsOf(_config);

    /// <inheritdoc />
    public bool IsConfigured
    {
        get
        {
            var settings = Settings;
            _published ??= settings;
            return settings.IsEnabled;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The comparison is on the whole settings RECORD, not on
    ///     <see cref="IsConfigured" />: switching from Claude to a local server is
    ///     configured→configured, and a panel that only watched the boolean would
    ///     keep showing "Asking Claude…" for a model that is not the one
    ///     answering.
    /// </remarks>
    public void NotifyConfigurationChanged()
    {
        var settings = Settings;
        var previous = _published;
        _published = settings;
        if (previous is not null && previous == settings) return;

        _logger?.Debug(AppLogCategory.Application,
            $"AI tone design reconfigured: {settings.Kind} / '{settings.ModelId}' (enabled={settings.IsEnabled}).");
        ConfiguredChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task<ToneDesignResult> DesignAsync(
        ToneDesignRequest request, IProgress<string>? status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var settings = Settings;
        var model = _factory.Create(settings); // throws NotConfigured with a specific reason
        try
        {
            return await DesignWithAsync(model, settings, request, status, ct).ConfigureAwait(false);
        }
        finally
        {
            // The OpenAI-compatible provider owns an HttpClient per instance and the
            // factory mints one per request; a provider that owns nothing ignores this.
            (model as IDisposable)?.Dispose();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The SAME preparation <see cref="DesignAsync" /> runs (<see cref="PrepareAsync" />),
    ///     minus the provider: no <see cref="IToneDesignModelFactory.Create" />, so it
    ///     works with no provider configured at all.
    /// </remarks>
    public async Task<ToneDesignManualPrompt> BuildManualPromptAsync(ToneDesignRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var settings = Settings;
        var prepared = await PrepareAsync(settings, request, status: null, ct).ConfigureAwait(false);
        var manual = new ToneDesignManualPrompt(
            ToneDesignManualPrompts.Flatten(prepared.Prompt, settings.WebResearch), prepared.Prompt);
        _logger?.Info(AppLogCategory.Application,
            $"AI tone prompt copied for a chat: {manual.Text.Length} chars (~{manual.EstimatedTokens} tokens), " +
            $"{prepared.Prompt.Messages.Count} message(s), research {(settings.WebResearch ? "on" : "off")}.");
        return manual;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Only the EXTRACTION is its own: a provider hands back JSON it already
    ///     extracted, a player pastes a whole chat answer. Everything after it is
    ///     <see cref="ProcessReplyAsync" />, the provider path's own tail.
    /// </remarks>
    public async Task<ToneDesignResult> ImportReplyAsync(
        ToneDesignRequest request, string reply, IProgress<string>? status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var total = Stopwatch.StartNew();
        status?.Report(_localization["ToneDesign_Status_ReadingReply"]);

        var json = ToneDraftJson.ExtractJsonObject(reply) ?? throw new ToneDesignException(
                ToneDesignFault.InvalidOutput,
                "The pasted answer has no JSON object in it.",
                $"{reply?.Length ?? 0} chars pasted, no JSON object found.");

        // The capture list the copied prompt offered, read the SAME way (live, from
        // the library) — so a capture the chat picked from that list validates here.
        var captures = _captures?.Invoke() ?? NamCaptureCatalog.Empty;
        return await ProcessReplyAsync(
            json, AiUsage.Manual, captures, status, total, providerMs: 0, ct).ConfigureAwait(false);
    }

    /// <summary>What a design is asked with: the prompt, and the capture list it
    /// offered (which the reply is then validated against).</summary>
    private sealed record PreparedDesign(ToneDesignPrompt Prompt, NamCaptureCatalog Captures);

    private async Task<ToneDesignResult> DesignWithAsync(
        IToneDesignModel model,
        AiProviderSettings settings,
        ToneDesignRequest request,
        IProgress<string>? status,
        CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var prepared = await PrepareAsync(settings, request, status, ct).ConfigureAwait(false);

        // Provider-specific on purpose: the local CLI says so, because it is the one
        // provider whose answer costs the user's own subscription usage rather than
        // a metered key, and a status line that read "Asking Claude…" for both would
        // hide which of the two is being spent.
        status?.Report(_localization.Format("ToneDesign_Status_Asking", model.Kind switch
        {
            AiProviderKind.Claude => "Claude",
            AiProviderKind.ClaudeCli => "Claude Code",
            AiProviderKind.CodexCli => "Codex",
            _ => settings.ModelId
        }));

        var clock = Stopwatch.StartNew();
        ToneDesignModelReply reply;
        try
        {
            reply = await model.CompleteAsync(prepared.Prompt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ToneDesignException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A provider is supposed to translate its own faults; this is the net
            // under the one that forgets, so the UI never sees an SDK type.
            throw new ToneDesignException(
                ToneDesignFault.Unknown,
                "The AI provider failed unexpectedly.",
                $"{ex.GetType().Name}: {ex.Message}", ex);
        }

        clock.Stop();
        ct.ThrowIfCancellationRequested();

        return await ProcessReplyAsync(
            reply.Json, reply.Usage, prepared.Captures, status, total, clock.ElapsedMilliseconds, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Everything BEFORE the model is asked — shared by the provider path and
    ///     the copy &amp; paste route, so the prompt a player copies is the prompt a
    ///     provider would have been sent: library anchors, the capture list, the
    ///     capture-search offer, the prompt itself, and its size in the log.
    /// </summary>
    private async Task<PreparedDesign> PrepareAsync(
        AiProviderSettings settings, ToneDesignRequest request, IProgress<string>? status, CancellationToken ct)
    {
        status?.Report(_localization["ToneDesign_Status_ReadingGear"]);

        // Grounding stage 2, BEFORE the prompt is built because it decides which
        // system prompt is used. Skipped entirely when the player switched it off,
        // when the build has no library, or when the request names no song — and it
        // cannot fail the design either way: RetrieveAsync answers Empty.
        var anchorSet = ToneAnchorSet.Empty;
        if (settings.UseLibraryTones && _anchors is not null)
        {
            status?.Report(_localization["ToneDesign_Status_CheckingLibrary"]);
            anchorSet = await ToneAnchorRetriever
                .RetrieveAsync(_anchors, request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        // The capture library, read LIVE: a capture downloaded while the panel was
        // open is offerable on the next request without a restart. Throw-free, and
        // an empty answer is a real one — it is the prompt a player with no captures
        // is supposed to get.
        var captures = _captures?.Invoke() ?? NamCaptureCatalog.Empty;

        // Read once per design, like the captures: signing in mid-session offers the
        // search on the next request.
        var captureSearch = _captureSource?.IsAvailable == true;

        var prompt = ToneDesignPromptBuilder.Build(request, anchorSet, captures, captureSearch);
        var promptTokens = ToneDesignPromptBuilder.EstimateTokens(prompt.System);
        _logger?.Debug(AppLogCategory.Application,
            $"AI tone prompt: {prompt.System.Length} chars (~{promptTokens} tokens) system, " +
            $"{prompt.Messages.Count} message(s), " +
            $"anchor={(anchorSet.Anchor is { } a ? $"\"{a.Title}\" / \"{a.ToneName}\"" : "none")}, " +
            $"{anchorSet.Analogies.Count} analogy/-ies, " +
            $"{captures.Options.Count} capture(s) offered, capture search {(captureSearch ? "on" : "off")}.");

        return new PreparedDesign(prompt, captures);
    }

    /// <summary>
    ///     Everything AFTER the model answered — shared by the provider path and an
    ///     imported chat answer: capture sourcing, validation against this build's
    ///     catalogue, the shared preset preparation. <paramref name="json" /> is the
    ///     reply's JSON object, already extracted.
    /// </summary>
    private async Task<ToneDesignResult> ProcessReplyAsync(
        string json,
        AiUsage usage,
        NamCaptureCatalog captures,
        IProgress<string>? status,
        Stopwatch total,
        long providerMs,
        CancellationToken ct)
    {
        var sourcingWarnings = new List<string>();
        var downloaded = new List<NamCaptureManifest>();

        status?.Report(_localization["ToneDesign_Status_CheckingChain"]);
        var validation = ToneDraftValidator.Validate(json, captures);

        // The TONE3000 search is a network call whose query a model wrote: it runs only for a draft
        // that already holds a usable chain, never for one that is going to be refused anyway.
        if (_captureSource is not null && validation.Preset is not null)
        {
            var before = json;
            (json, captures) = await SourceCaptureAsync(
                _captureSource, json, captures, sourcingWarnings, downloaded, status, ct).ConfigureAwait(false);
            if (!ReferenceEquals(json, before)) validation = ToneDraftValidator.Validate(json, captures);
        }

        var warnings = sourcingWarnings.Count == 0
            ? validation.Warnings
            : [.. sourcingWarnings, .. validation.Warnings];
        if (validation.Preset is null)
        {
            throw new ToneDesignException(
                ToneDesignFault.InvalidOutput,
                warnings.Count > 0
                    ? warnings[0]
                    : "The model's reply was not a tone this build can use.",
                string.Join(" | ", warnings));
        }

        var moduleCount = validation.Preset.Rig.Modules.Count;
        status?.Report(_localization.Plural(
            "ToneDesign_Status_CheckingModulesOne", "ToneDesign_Status_CheckingModules", moduleCount));

        var prepared = validation.Preset with
        {
            Rig = DcRigPresetPreparation.Prepare(validation.Preset, _namCaptureDirectory?.Invoke())
        };

        _logger?.Info(AppLogCategory.Application,
            $"AI tone \"{prepared.Name}\"{(usage.IsManual ? " (imported from a pasted chat answer)" : "")}: " +
            $"{moduleCount} modules, " +
            $"{warnings.Count} warning(s), {downloaded.Count} capture(s) downloaded, {usage.InputTokens} in / " +
            $"{usage.OutputTokens} out tokens, {providerMs} ms.");

        status?.Report(_localization["ToneDesign_Status_Done"]);

        return new ToneDesignResult(
            prepared,
            prepared.Description,
            Math.Clamp(validation.Draft?.Confidence ?? 0.5, 0d, 1d),
            warnings,
            json,
            usage,
            total.Elapsed,
            downloaded);
    }

    /// <summary>
    ///     Answers the reply's <see cref="ToneDraft.CaptureSearch" />: when a
    ///     <c>namamp</c> names no capture on the list, the search is run, the capture
    ///     downloaded (or found again in the library), and that <c>namamp</c> pointed
    ///     at it — so the validator that follows sees an ordinary listed capture.
    ///
    ///     <para>
    ///         Everything that goes wrong here is a WARNING, never a failed design:
    ///         the <c>namamp</c> is then dropped by the validator with its own warning,
    ///         and the rest of the chain still comes back. A reply that does not parse
    ///         is left alone for the validator to report, which it does better.
    ///     </para>
    ///     <para>
    ///         The rewritten JSON is what the result carries as its raw reply, so a
    ///         refinement turn shows the model the capture it actually got — by the
    ///         id it will find on the list next time.
    ///     </para>
    /// </summary>
    private async Task<(string Json, NamCaptureCatalog Captures)> SourceCaptureAsync(
        INamCaptureSource source,
        string json,
        NamCaptureCatalog captures,
        List<string> warnings,
        List<NamCaptureManifest> downloaded,
        IProgress<string>? status,
        CancellationToken ct)
    {
        ToneDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<ToneDraft>(json, ToneDraftJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (json, captures);
        }

        if (draft?.Modules is not { } modules || string.IsNullOrWhiteSpace(draft.CaptureSearch))
            return (json, captures);

        var index = -1;
        for (var i = 0; i < modules.Count; i++)
        {
            if (modules[i] is { } m
                && string.Equals(m.Type?.Trim(), AiGearNotes.NamAmpId, StringComparison.Ordinal)
                && captures.Find(m.Capture) is null)
            {
                index = i;
                break;
            }
        }

        // A listed capture was already chosen, or there is no namamp: the search is
        // not needed, and running it anyway would download a file nothing plays.
        if (index < 0) return (json, captures);

        if (source.IsAvailable)
        {
            status?.Report(_localization.Format(
                "ToneDesign_Status_FetchingCapture", Tone3000CaptureSource.NormalizeQuery(draft.CaptureSearch)));
        }

        var found = await source.FindAsync(draft.CaptureSearch, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (found.Entry is null)
        {
            warnings.Add(found.Problem ?? "No capture was found for the design's search.");
            return (json, captures);
        }

        if (found.Downloaded) downloaded.Add(found.Entry.Manifest);

        // Re-read the library so the new capture is on the list the validator checks;
        // a service built without one checks against the capture it just got.
        var refreshed = _captures?.Invoke() ?? NamCaptureCatalog.Empty;
        var option = refreshed.Options.FirstOrDefault(o =>
            string.Equals(o.FileName, found.Entry.Manifest.FileName, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            refreshed = NamCaptureCatalog.FromEntries([found.Entry]);
            option = refreshed.Options.FirstOrDefault();
        }

        if (option is null)
        {
            warnings.Add($"\"{found.Entry.Manifest.Title}\" is not a capture namamp can play.");
            return (json, captures);
        }

        var rewritten = modules.ToList();
        rewritten[index] = rewritten[index] with { Capture = option.Id };
        _logger?.Info(AppLogCategory.Application,
            $"AI tone: capture search \"{draft.CaptureSearch}\" → \"{option.Id}\" ({option.DisplayName}).");
        return (ToneDraftJson.Compact(ToneDraftJson.Serialize(draft with { Modules = rewritten, CaptureSearch = "" })),
            refreshed);
    }
}
