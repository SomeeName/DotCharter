using DotCharter.Tone.Audio.Dsp.Presets;

namespace DotCharter.Tone.Ai;

// The contract between the three halves of "AI tone design" (v0.55): the
// provider layer (talks to a model), the design service (grounds the model in
// the live catalogue, validates, applies through the preset pipeline) and the
// UI (the "Design a tone" panel in the tone drawer, the Settings tab). Written
// FIRST so the three could be built in parallel against one shape; the
// implementations own everything else. A tone is pure data here — the model
// never touches audio — so this is a text-to-JSON problem with a validator the
// app already owns (TonePresetJsonFile.Sanitize, DcRigPresetPreparation).

/// <summary>Which backend answers a tone request.</summary>
public enum AiProviderKind
{
    /// <summary>No provider configured; the feature is hidden/disabled.</summary>
    None = 0,
    /// <summary>Anthropic's Claude through the official <c>Anthropic</c> SDK.</summary>
    Claude = 1,
    /// <summary>Any server speaking the OpenAI chat-completions shape: LM Studio,
    /// Ollama, Codex-style gateways. One implementation, a base URL and a model id.</summary>
    OpenAiCompatible = 2,
    /// <summary>RESERVED for a bundled, downloadable local model (not implemented in
    /// v0.55; the Settings UI may show it as "coming later"). Keeping the value
    /// pinned means a config written by a later build round-trips today.</summary>
    BuiltIn = 3,
    /// <summary>The Claude Code CLI already installed on this machine, driven
    /// headless as a child process. No API key: the binary answers under the user's
    /// OWN Claude Code login, so a request counts against that Pro/Max
    /// subscription's usage. OPT-IN and never a default — see
    /// <c>docs/agents/services/ai.md</c> § Claude Code (local CLI).</summary>
    ClaudeCli = 4,
    /// <summary>The OpenAI Codex CLI installed on this machine, driven headless
    /// (<c>codex exec</c>) under the user's OWN Codex login. No API key, OPT-IN,
    /// same terms as <see cref="ClaudeCli" />. APPENDED: the ordinal is persisted.</summary>
    CodexCli = 5,
}

/// <summary>Persisted, non-secret provider settings. The API key never lives here —
/// see <see cref="IAiSecretStore"/>.</summary>
/// <param name="Kind">Which backend answers.</param>
/// <param name="ModelId">The model id, verbatim.</param>
/// <param name="BaseUrl">Base URL for an OpenAI-compatible server; null otherwise.
/// <para><b><see cref="AiProviderKind.ClaudeCli" /> REUSES this field</b> as an
/// optional absolute path to the <c>claude</c> executable, for a machine where the
/// binary is not on <c>PATH</c>. Null there means "search PATH", which is the normal
/// case. The field is reused rather than added to because the record is persisted in
/// <c>AppConfig.Ai</c> and a fourth positional property would have to be additive
/// anyway; one optional "where is it" string per provider is the shape both users of
/// it want. It is NOT a URL for that kind and nothing may parse it as one.</para></param>
/// <param name="UseLibraryTones">Whether the designer may ground a request in the
/// Rocksmith tones of the song in the player's own library (grounding stage 2).
/// Default TRUE, and ADDITIVE with a default so a config written before it takes
/// the feature ON rather than silently losing it.</param>
/// <param name="WebResearch">Whether a provider that CAN search the web (the Claude
/// Code and Codex CLIs) may look a named song's recorded rig up before designing
/// (<see cref="ToneDesignResearch" />). Default TRUE and additive, like
/// <paramref name="UseLibraryTones" />. The API providers ignore it.</param>
/// <param name="Effort">How hard the model reasons before it answers:
/// <see cref="AiReasoningEffort.Default" /> sends nothing and leaves it to the
/// provider. ADDITIVE with a default, so a config written before it reads as
/// Default — the behaviour every earlier build had. Each provider maps it through
/// <see cref="AiReasoning" />.</param>
public sealed record AiProviderSettings(
    AiProviderKind Kind,
    string ModelId,
    string? BaseUrl,
    bool UseLibraryTones = true,
    bool WebResearch = true,
    AiReasoningEffort Effort = AiReasoningEffort.Default)
{
    public static AiProviderSettings Disabled { get; } = new(AiProviderKind.None, "", null);
    /// <summary>A provider is chosen and has what it needs to be asked. The Codex
    /// CLI is the one kind with no required model id: empty means "the CLI's own
    /// default for that login".</summary>
    public bool IsEnabled => Kind != AiProviderKind.None
                             && (Kind == AiProviderKind.CodexCli || !string.IsNullOrWhiteSpace(ModelId));
}

public enum ToneDesignRole { User = 0, Assistant = 1 }

/// <summary>One turn of the design conversation, so "less gain" edits the current
/// chain instead of starting over.</summary>
public sealed record ToneDesignTurn(ToneDesignRole Role, string Text);

/// <summary>What the user asked for. <paramref name="CurrentTone"/> is the chain a
/// follow-up should edit; null for a fresh design. <paramref name="Tuning"/> is the
/// tuning the player's tuner is set to ("Drop D (D A D G B E)"), or null.</summary>
public sealed record ToneDesignRequest(
    string Description,
    IReadOnlyList<ToneDesignTurn> History,
    DspTonePreset? CurrentTone,
    string? SongTitle = null,
    string? Artist = null,
    string? Tuning = null);

/// <summary>Token accounting for the status line and for evals.
/// <para><paramref name="CostUsd" /> is what the SERVER charged, when the server
/// says so — OpenRouter returns <c>usage.cost</c> on every reply, in credits, which
/// it sells at one US dollar each. It is ADDITIVE with a default so every call site
/// written before it compiles unchanged, and <c>null</c> means "the server did not
/// price this request", which is NOT the same as free: <see cref="AiModelPricing" />'s
/// estimate stays the answer for a provider that prices nothing, and a local server
/// costs nothing at all. A measured cost, where there is one, beats a table someone
/// has to keep current — which is the whole point for an eval comparing many
/// models.</para></summary>
public sealed record AiUsage(string ModelId, int InputTokens, int OutputTokens, decimal? CostUsd = null)
{
    /// <summary>The model id an IMPORTED reply carries
    /// (<see cref="IToneDesignService.ImportReplyAsync" />): the player pasted
    /// ChatGPT's answer, so this app metered nothing and priced nothing. Not a real
    /// model id on purpose — <see cref="AiModelPricing" /> finds no row for it, and
    /// the panel prints "from ChatGPT" instead of a time and a cost.</summary>
    public const string ManualModelId = "ChatGPT (manual)";

    /// <summary>Zero tokens, the manual model id: what an imported reply reports.</summary>
    public static AiUsage Manual { get; } = new(ManualModelId, 0, 0);

    /// <summary>True for <see cref="Manual" /> — a reply the player pasted in.</summary>
    public bool IsManual => string.Equals(ModelId, ManualModelId, StringComparison.Ordinal);
}

/// <summary>The validated outcome: a preset that passed the catalogue check and is
/// ready for the ordinary apply/save path, the model's rationale, its own
/// confidence (0..1), and every knob or module the validator had to clamp or drop.
/// <para><c>Elapsed</c> is wall time for the whole design call — the provider round
/// trip plus validation — and is ADDITIVE with a default, so every call site
/// written before it compiles unchanged. Zero means "not measured", which is what a
/// test double that does not fill it in is honestly saying; the UI prints nothing
/// for it rather than "0.0 s".</para>
/// <para><c>DownloadedCaptures</c> lists the neural captures this design fetched
/// from TONE3000 (see <see cref="ToneDraft.CaptureSearch" />), so the panel can
/// credit their authors and licences the way the capture browser does. Null or
/// empty means none were fetched.</para></summary>
public sealed record ToneDesignResult(
    DspTonePreset Tone,
    string Rationale,
    double Confidence,
    IReadOnlyList<string> Warnings,
    string RawJson,
    AiUsage Usage,
    TimeSpan Elapsed = default,
    IReadOnlyList<Online.Tone3000.NamCaptureManifest>? DownloadedCaptures = null);

/// <summary>The exact JSON the model must return. <c>Type</c> is a catalogue type id,
/// <c>Params</c> is keyed by param id with the value in the param's own unit. Series
/// chains only in v1 (no split lanes, no sidechain); the design service maps this
/// onto <see cref="DspTonePreset"/> and rejects anything the live catalogue does
/// not know.
/// <para><paramref name="CaptureSearch" /> is a TONE3000 search for the amp a
/// <c>namamp</c> should play when no capture on the list fits ("Fender Deluxe
/// Reverb clean"). The design service runs it, downloads the best match into the
/// capture library and points the <c>namamp</c> at it BEFORE validation. It is
/// offered only while TONE3000 is signed in, is empty otherwise, and has a default
/// so every call site written before it compiles unchanged. It is one field on the
/// DRAFT, not one per module, because a chain has at most one amp.</para></summary>
public sealed record ToneDraft(
    string Name,
    string Rationale,
    double Confidence,
    IReadOnlyList<ToneDraftModule> Modules,
    string CaptureSearch = "");

/// <summary>One module of a drafted chain.
/// <para><paramref name="Capture" /> is the <c>namamp</c> module's chosen NEURAL
/// CAPTURE, named by its <see cref="NamCaptureOption.Id" /> from the closed capture
/// list the prompt carries — never a path, never a title. It is ADDITIVE with a
/// parameter default so every call site written before it compiles unchanged, and
/// empty everywhere else: a non-<c>namamp</c> module that carries one has its
/// capture ignored with a warning, and a <c>namamp</c> that carries none is dropped
/// (<see cref="ToneDraftValidator" />). The validator resolves the id to the
/// capture's FILE NAME and <c>DcRigPresetPreparation.Prepare</c> turns that into the
/// absolute path the native module requires — the same two steps a factory capture
/// preset goes through.</para></summary>
public sealed record ToneDraftModule(
    string Type,
    IReadOnlyDictionary<string, float> Params,
    bool Bypassed = false,
    string Capture = "");

/// <summary>What a provider receives: a system prompt (catalogue, rules, examples),
/// the conversation, and the JSON schema the reply must validate against.
/// Providers that support structured output pass the schema natively; the rest put
/// it in the prompt and the service validates the reply either way.</summary>
public sealed record ToneDesignPrompt(
    string System,
    IReadOnlyList<ToneDesignTurn> Messages,
    string JsonSchema,
    string SchemaName);

/// <summary>
///     The prompt for the "ChatGPT (copy &amp; paste)" route: EXACTLY what
///     <see cref="IToneDesignService.DesignAsync" /> would send for the same request
///     (<see cref="Prompt" />), flattened into ONE plain-text block (<see cref="Text" />)
///     the player pastes into a chat — the system prompt, the conversation, the JSON
///     schema and a closing "answer with only the JSON object".
/// </summary>
/// <param name="Text">The block to put on the clipboard.</param>
/// <param name="Prompt">The structured prompt it was flattened from.</param>
public sealed record ToneDesignManualPrompt(string Text, ToneDesignPrompt Prompt)
{
    /// <summary>The rough size of <see cref="Text" /> in tokens (four characters a
    /// token, <see cref="ToneDesignPromptBuilder.EstimateTokens" />) — shown to the
    /// player, who pastes it into a chat with its own limits.</summary>
    public int EstimatedTokens => ToneDesignPromptBuilder.EstimateTokens(Text);
}

/// <summary>What a provider returns: the reply's JSON text (already stripped of any
/// prose or code fences) and the usage.</summary>
public sealed record ToneDesignModelReply(string Json, AiUsage Usage);

/// <summary>Result of "Test connection".</summary>
public sealed record AiProbeResult(bool Ok, string Message, string? ModelId);

/// <summary>A model backend. Implementations are thin: marshal the prompt, call the
/// API, return JSON. No catalogue knowledge, no validation, no UI.</summary>
public interface IToneDesignModel
{
    AiProviderKind Kind { get; }
    Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct);
    Task<AiProbeResult> ProbeAsync(CancellationToken ct);
}

/// <summary>Resolves a model from settings plus the secret store; throws
/// <see cref="ToneDesignException"/> with <see cref="ToneDesignFault.NotConfigured"/>
/// when the settings are incomplete.</summary>
public interface IToneDesignModelFactory
{
    IToneDesignModel Create(AiProviderSettings settings);

    /// <summary>True when <paramref name="kind" /> is a provider this build can
    /// actually mint — Claude and OpenAI-compatible today, never
    /// <see cref="AiProviderKind.None" /> (the off switch, not a provider) and
    /// never <see cref="AiProviderKind.BuiltIn" /> (a reserved slot with no model
    /// behind it). The Settings picker drives its enabled rows off this, so filling
    /// the reserved slot in takes one arm in the factory rather than a second,
    /// hand-kept list in the UI that can disagree with it.</summary>
    bool Supports(AiProviderKind kind);
}

/// <summary>API keys, encrypted at rest with DPAPI (CurrentUser scope + app entropy,
/// the same shape as <c>Tone3000TokenStore</c>). Never in AppConfig, never logged.</summary>
public interface IAiSecretStore
{
    string? Read(AiProviderKind kind);
    void Write(AiProviderKind kind, string? secret);

    /// <summary>True when this store SEALS a secret at rest and it survives the
    /// process (DPAPI on Windows). False means the key lives for the session and is
    /// forgotten on exit, which the Settings page says out loud — a user who
    /// believes a key was saved and finds it gone next launch reads that as the
    /// feature being broken. On the INTERFACE so the Settings page asks the store
    /// it was handed instead of naming a concrete implementation, which is what
    /// made a test double silently answer for the real one.</summary>
    bool IsPersistent { get; }
}

/// <summary>The feature's one entry point for the UI.</summary>
public interface IToneDesignService
{
    /// <summary>True when a provider is configured; the UI hides the panel otherwise.</summary>
    bool IsConfigured { get; }

    /// <summary>Raised when the provider configuration CHANGED — a different kind,
    /// model id or base URL. The panel subscribes to this instead of re-reading
    /// <see cref="IsConfigured" /> on every expand: the read goes through
    /// <c>IConfigurationService.LoadAppConfig</c>, and a property a XAML binding
    /// touches must not be a settings load. Raised on whatever thread saved the
    /// settings; a UI subscriber marshals.</summary>
    event EventHandler? ConfiguredChanged;

    /// <summary>Tells the service its settings were just written, so it can compare
    /// them against what it last published and raise
    /// <see cref="ConfiguredChanged" /> if they moved. Called by the Settings page's
    /// save path. Idempotent: a save that changed nothing raises nothing, which is
    /// what keeps a Save button press from re-rendering a panel that did not
    /// change.</summary>
    void NotifyConfigurationChanged();

    /// <summary>Designs (or, with history + a current tone, edits) a tone. Reports
    /// human-readable progress ("Asking Claude…", "Checking 7 modules…") through
    /// <paramref name="status"/>. Throws <see cref="ToneDesignException"/> for every
    /// failure the UI should show, and <see cref="OperationCanceledException"/> on
    /// cancel — never a raw HTTP or SDK exception.</summary>
    Task<ToneDesignResult> DesignAsync(ToneDesignRequest request, IProgress<string>? status, CancellationToken ct);

    /// <summary>The "ChatGPT (copy &amp; paste)" route, step 1: the prompt
    /// <see cref="DesignAsync" /> would send for <paramref name="request" /> — same
    /// grounding (gear catalogue, the player's captures, library anchors, the
    /// capture-search offer) — as one block of text for the player to paste into a
    /// chat. Needs NO provider: it works while <see cref="IsConfigured" /> is false,
    /// which is the whole point. Throws <see cref="OperationCanceledException" /> on
    /// cancel and nothing else a caller must handle.</summary>
    Task<ToneDesignManualPrompt> BuildManualPromptAsync(ToneDesignRequest request, CancellationToken ct);

    /// <summary>Step 2: turns the chat's pasted answer — prose around a fenced JSON
    /// block is fine — into a tone through the SAME post-reply pipeline a provider's
    /// reply takes (extract → capture sourcing → validate → prepare).
    /// <paramref name="request" /> is the one the prompt was built for; the capture
    /// catalogue is re-read the same way, so a capture the copied prompt listed
    /// validates here. The result's usage is <see cref="AiUsage.Manual" />. Throws
    /// <see cref="ToneDesignException" /> (<see cref="ToneDesignFault.InvalidOutput" />
    /// for an answer that is not a usable tone) and
    /// <see cref="OperationCanceledException" />; needs no provider either.</summary>
    Task<ToneDesignResult> ImportReplyAsync(
        ToneDesignRequest request, string reply, IProgress<string>? status, CancellationToken ct);
}

public enum ToneDesignFault
{
    NotConfigured,
    Network,
    Authentication,
    RateLimited,
    ModelRefused,
    InvalidOutput,
    Unknown,
}

/// <summary>The one exception type the UI handles. The message is user-facing
/// (localizable via the fault), <c>Detail</c> is the provider's own text for the log.</summary>
public sealed class ToneDesignException : Exception
{
    public ToneDesignFault Fault { get; }
    public string? Detail { get; }

    public ToneDesignException(ToneDesignFault fault, string message, string? detail = null, Exception? inner = null)
        : base(message, inner)
    {
        Fault = fault;
        Detail = detail;
    }
}
