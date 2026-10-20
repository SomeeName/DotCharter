using System.Text;
using System.Text.Json;

namespace DotCharter.Tone.Ai;

/// <summary>
///     The Claude Code provider: the <c>claude</c> binary already installed on this
///     machine, driven headless as a child process.
///
///     <para>
///         <b>Why it exists.</b> The other Claude arm needs an Anthropic API key and
///         bills per token. A player who already pays for Claude Pro or Max has a
///         logged-in CLI sitting on their PATH, and this reaches it: no key, no
///         second bill. The trade is that <b>every request counts against that
///         subscription's usage</b>, so the provider is OPT-IN, is never a default,
///         and the Settings row says both things in one line.
///     </para>
///     <para>
///         <b>Three flags from the task brief are deliberately NOT used, each for a
///         measured reason</b> (verified against <c>claude -p --help</c> on
///         2.1.269, which is what is installed here):
///         <list type="bullet">
///             <item>
///                 <c>--bare</c> would be the obvious minimal mode, and it is the
///                 ONE flag that breaks this feature outright: its own help says
///                 "Anthropic auth is strictly ANTHROPIC_API_KEY or apiKeyHelper
///                 via --settings (OAuth and keychain are never read)". Subscription
///                 login IS the OAuth path, so <c>--bare</c> turns the no-key
///                 provider back into a key-only one.
///             </item>
///             <item>
///                 <c>--max-turns</c> is in no <c>--help</c> output. The CLI's
///                 option parser is NOT strict — an unknown flag is accepted
///                 silently — so "it did not error" is no evidence a flag exists.
///                 Only advertised flags are used. A single turn is instead
///                 guaranteed by giving the model no tools to take a second turn
///                 with.
///             </item>
///             <item>
///                 <c>--disallowedTools "*"</c> is replaced by <c>--tools ""</c>,
///                 which the help documents exactly as "use "" to disable all
///                 tools" — a positive empty set beats a wildcard whose globbing
///                 rules are unstated.
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>The system prompt never goes on the command LINE.</b> It is ~35,000
///         characters and a Windows command line is capped at 32,767 for the whole
///         line, schema and flags included, so <c>--system-prompt &lt;the
///         catalogue&gt;</c> is a feature that works until the catalogue grows and
///         then fails as a truncated-argument error nobody can read. There are two
///         layouts and <see cref="IClaudeCliCapabilityProbe" /> picks one per
///         process:
///         <list type="number">
///             <item>
///                 <b>Preferred (W9): <c>--system-prompt-file &lt;path&gt;</c>.</b>
///                 The framing and the grounding are written to one temp file named
///                 after a hash of its own content
///                 (<see cref="ClaudeCliSystemPrompt" />) and the piped stdin carries
///                 only the CONVERSATION. That puts the grounding in the SYSTEM
///                 prompt, where the CLI's own cache breakpoint covers it — measured
///                 on 2.1.273 with a 25k-token prompt and a DIFFERENT
///                 request each run: <b>24,334 cache READ tokens every time after
///                 the first</b>.
///             </item>
///             <item>
///                 <b>Fallback: the grounding at the head of STDIN</b>, with
///                 <c>--system-prompt</c> carrying the short framing. This is what
///                 shipped before W9 and what a CLI that does not advertise the flag
///                 still gets. Its cost is measured rather than assumed: the CLI does
///                 set a cache breakpoint on the piped user turn, so two IDENTICAL
///                 requests hit it — but a request whose text differs (every real
///                 second design) invalidates the whole turn and measured <b>0 cache
///                 reads against a full 25,114-token cache WRITE</b>.
///             </item>
///         </list>
///     </para>
///     <para>
///         Either way the invocation stays a one-shot <c>--print</c>.
///         <b><c>--resume</c> / <c>--continue</c> are deliberately NOT used</b>: a
///         resumed session replays every prior turn into every later request, which
///         for short independent tone requests costs more than it saves.
///     </para>
/// </summary>
public sealed class ClaudeCliToneModel : IToneDesignModel
{
    /// <summary>The model used when the settings do not name one: Claude Haiku 5.5
    /// (owner). A tone design is a short structured answer, and every
    /// request counts against the user's own Claude Code usage.</summary>
    public const string DefaultModelId = "claude-haiku-5-5";

    /// <summary>Claude Opus 5.5, offered on the Settings tab as a fill-in button.</summary>
    public const string OpusModelId = "claude-opus-5-5";

    /// <summary>Claude Sonnet 5.5, the third model the Settings tab offers for this provider.</summary>
    public const string AlternateModelId = "claude-sonnet-5-5";

    /// <summary>Wall-clock deadline for one design. Generous because the CLI pays a
    /// Node start-up cost the API provider does not, and a thinking model on a long
    /// catalogue is not fast.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>The deadline when the model may research the song first: a few
    /// searches and page reads come before the answer.</summary>
    public static readonly TimeSpan ResearchTimeout = TimeSpan.FromSeconds(300);

    /// <summary>The short prompt handed to <c>--system-prompt</c>. It REPLACES the
    /// CLI's coding-agent system prompt; the grounding is on stdin.</summary>
    public const string FramingPrompt =
        "You are a guitar amp and effects tone designer inside DotCharter. " +
        "Answer with a single JSON object matching the supplied schema and nothing else. " +
        "Do not use tools, do not read or write files, and do not ask questions.";

    private readonly IClaudeCliCapabilityProbe _capabilities;
    private readonly AiReasoningEffort _effort;
    private readonly string _executablePath;
    private readonly IAppLogger? _logger;
    private readonly string _modelId;
    private readonly bool _research;
    private readonly IClaudeCliRunner _runner;
    private readonly string _workingDirectory;

    /// <summary>Creates the provider over an already-located binary.</summary>
    /// <param name="settings">Provider settings; <c>ModelId</c> overrides
    /// <see cref="DefaultModelId" />.</param>
    /// <param name="executablePath">Absolute path to <c>claude</c>, from
    /// <see cref="IClaudeCliLocator" />.</param>
    /// <param name="runner">The process seam.</param>
    /// <param name="logger">Optional app logger for usage lines.</param>
    /// <param name="workingDirectory">The child's cwd. Defaults to a scratch folder
    /// under the temp directory, on purpose: run in the app's own cwd and the CLI
    /// auto-discovers whatever <c>CLAUDE.md</c>, settings and git state live there
    /// and prepends them to a request about guitar tones.</param>
    /// <param name="capabilities">Decides whether this binary takes
    /// <c>--system-prompt-file</c>. Optional: the default asks the real
    /// <c>claude --help</c> once per process through the same runner.</param>
    public ClaudeCliToneModel(
        AiProviderSettings settings,
        string executablePath,
        IClaudeCliRunner runner,
        IAppLogger? logger = null,
        string? workingDirectory = null,
        IClaudeCliCapabilityProbe? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        _executablePath = executablePath;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger;
        _modelId = string.IsNullOrWhiteSpace(settings.ModelId) ? DefaultModelId : settings.ModelId.Trim();
        _research = settings.WebResearch;
        _effort = settings.Effort;
        _workingDirectory = workingDirectory ?? ScratchDirectory();
        _capabilities = capabilities ?? ClaudeCliCapabilityProbe.For(_runner, logger, _workingDirectory);
    }

    /// <inheritdoc />
    public AiProviderKind Kind => AiProviderKind.ClaudeCli;

    /// <inheritdoc />
    public async Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ct.ThrowIfCancellationRequested();

        // W9: the grounding belongs in the SYSTEM prompt, where the CLI's own cache
        // breakpoint covers it across one-shot processes. Both halves can decline —
        // a CLI without the flag, or a temp folder that cannot be written — and
        // either way the answer is the old stdin layout rather than a failed design.
        string? systemPromptFile = null;
        if (await _capabilities.SupportsSystemPromptFileAsync(_executablePath, ct).ConfigureAwait(false))
        {
            systemPromptFile = ClaudeCliSystemPrompt.TryWrite(
                Path.Combine(_workingDirectory, "prompts"), prompt, _logger);
        }

        var request = new ClaudeCliRequest(
            _executablePath,
            BuildArguments(prompt, systemPromptFile),
            BuildStandardInput(prompt, systemPromptFile is not null, _research),
            _workingDirectory,
            _research ? ResearchTimeout : Timeout);

        _logger?.Debug(AppLogCategory.Application,
            $"Claude Code CLI: {request.Arguments.Count} args, {request.StandardInput.Length} chars on stdin, " +
            $"system prompt {(systemPromptFile is null ? "on stdin" : "in " + Path.GetFileName(systemPromptFile))}.");

        var outcome = await _runner.RunAsync(request, ct).ConfigureAwait(false);

        if (outcome.TimedOut)
        {
            throw new ToneDesignException(
                ToneDesignFault.Network,
                $"Claude Code did not answer within {Timeout.TotalSeconds:F0} seconds.",
                outcome.ToRedactedLogLine());
        }

        if (IsAuthenticationFailure(outcome))
        {
            throw new ToneDesignException(
                ToneDesignFault.Authentication,
                "Claude Code is not signed in. Run \"claude\" in a terminal and log in, then try again.",
                outcome.ToRedactedLogLine());
        }

        var envelope = ParseEnvelope(outcome);
        var usage = new AiUsage(_modelId, envelope.InputTokens, envelope.OutputTokens);

        // Usage and cost only — never the prompt, never the reply body. The reply
        // is a user's tone request and its answer, and Information is a level that
        // ships in the log file the app attaches to a bug report.
        _logger?.Info(AppLogCategory.Application,
            $"AI tone design (Claude Code CLI {_modelId}): {usage.InputTokens} in / " +
            $"{usage.OutputTokens} out tokens, {envelope.CostUsd:F4} USD list, " +
            $"{envelope.Turns} turn(s).");

        return new ToneDesignModelReply(envelope.Json, usage);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <c>claude --version</c>, not a design request: it costs nothing, needs no
    ///     network and answers the question the button is really asking — is the
    ///     binary there and runnable. A "Test" that spent subscription usage every
    ///     time it was pressed would be its own support request.
    /// </remarks>
    public async Task<AiProbeResult> ProbeAsync(CancellationToken ct)
    {
        ClaudeCliOutcome outcome;
        try
        {
            outcome = await _runner.RunAsync(
                new ClaudeCliRequest(
                    _executablePath, ["--version"], string.Empty, _workingDirectory,
                    TimeSpan.FromSeconds(30)),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ToneDesignException ex)
        {
            return new AiProbeResult(false, ex.Message, null);
        }

        if (outcome.TimedOut)
            return new AiProbeResult(false, "The Claude Code command did not answer.", null);

        if (outcome.ExitCode != 0)
        {
            return new AiProbeResult(
                false,
                "The Claude Code command failed to run. Install Claude Code and sign in, then try again.",
                null);
        }

        // "2.1.269 (Claude Code)" — reported verbatim, trimmed. The version is what
        // tells a user WHICH binary answered when two are installed.
        var version = outcome.StandardOutput.Trim();
        if (version.Length == 0) version = "unknown version";
        return new AiProbeResult(true, $"Claude Code {version} at {_executablePath}, model {_modelId}.", _modelId);
    }

    /// <summary>
    ///     The argument list for <paramref name="prompt" />. Internal so
    ///     <c>ClaudeCliToneModelTests</c> can assert it exactly without spawning
    ///     anything — this list IS the contract with the CLI, and every entry below
    ///     is documented in <c>claude -p --help</c> on 2.1.269.
    /// </summary>
    /// <param name="prompt">The assembled prompt.</param>
    /// <param name="systemPromptFile">The file the grounding was written to, or null
    /// for the stdin layout. Non-null swaps <c>--system-prompt &lt;framing&gt;</c>
    /// for <c>--system-prompt-file &lt;path&gt;</c> — never both: the second call
    /// would replace the first and the framing (which lives in the file's head) would
    /// be the half that won.</param>
    internal IReadOnlyList<string> BuildArguments(ToneDesignPrompt prompt, string? systemPromptFile = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var effort = AiReasoning.WireValue(_effort, AiProviderKind.ClaudeCli, _modelId);
        return
        [
            // Headless: print the answer and exit. The prompt arrives on stdin.
            "--print",
            // The machine-readable envelope: is_error, result, structured_output, usage.
            "--output-format", "json",
            "--model", _modelId,
            // Reasoning effort, only when Settings ▸ AI names a level: Default
            // passes nothing and leaves the CLI's own. `--effort <level>` is in
            // `claude --help` on 2.1.296 ("low, medium, high, xhigh, max").
            .. effort is null ? (string[])[] : ["--effort", effort],
            // Structured output. The CLI validates the reply against it, so
            // ToneDraftJson.ExtractJsonObject has nothing left to strip.
            "--json-schema", prompt.JsonSchema,
            // Both spellings REPLACE the CLI's coding-agent prompt. The file form
            // carries the framing AND the grounding (so the whole thing sits behind
            // the CLI's system-block cache breakpoint); the inline form carries the
            // framing alone, because the grounding does not fit on a Windows command
            // line — see the class remarks.
            .. systemPromptFile is null
                ? (string[])["--system-prompt", FramingPrompt]
                : ["--system-prompt-file", systemPromptFile],
            // No tools at all, which is also what makes this a single turn: there is
            // nothing the model can call to take a second one. With research on, the
            // web tools and nothing else — read-only lookups, pre-approved so no
            // permission prompt can wait on a user who cannot see it.
            .. _research
                ? (string[])["--tools", ToneDesignResearch.ClaudeCliTools, "--allowedTools", ToneDesignResearch.ClaudeCliTools]
                : ["--tools", ""],
            // A tone request has no business reading the user's MCP servers, their
            // skills, or leaving a resumable session on disk.
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--no-session-persistence",
            // Nothing may block on a permission prompt no one can see: with no tools
            // this should never fire, and if it ever does the answer is "no".
            "--permission-prompts", "none"
        ];
    }

    /// <summary>
    ///     What is piped to the child: the grounding prompt, then the conversation.
    ///     Internal for the same reason as <see cref="BuildArguments" />.
    ///     <para>
    ///         The turns are labelled rather than sent as a real message array — the
    ///         CLI's text input format is one prompt — so a follow-up's history is
    ///         preserved as text. That is weaker than the API providers' structured
    ///         turns and is the honest cost of driving a CLI.
    ///     </para>
    /// </summary>
    /// <param name="prompt">The assembled prompt.</param>
    /// <param name="systemPromptOnFile">True when the grounding already went to
    /// <c>--system-prompt-file</c>, in which case stdin carries the CONVERSATION
    /// alone. Sending it twice would double the input tokens of every request and
    /// put the cached copy and an uncached copy in the same call.</param>
    /// <param name="research">True when the model may search the web first: the
    /// <see cref="ToneDesignResearch.Instruction" /> opens the conversation.</param>
    internal static string BuildStandardInput(ToneDesignPrompt prompt, bool systemPromptOnFile = false, bool research = false)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var text = new StringBuilder(prompt.System.Length + 1024);
        if (!systemPromptOnFile) text.Append(prompt.System);

        var first = systemPromptOnFile;
        if (research)
        {
            if (!first) text.Append("\n\n");
            text.Append(ToneDesignResearch.Instruction);
            first = false;
        }
        foreach (var turn in prompt.Messages)
        {
            if (first) first = false;
            else text.Append("\n\n");
            text.Append(turn.Role == ToneDesignRole.Assistant ? "## Your previous answer\n" : "## Request\n");
            text.Append(turn.Text);
        }

        return text.ToString();
    }

    /// <summary>The envelope `--output-format json` prints, reduced to what this
    /// provider needs.</summary>
    internal sealed record CliEnvelope(string Json, int InputTokens, int OutputTokens, double CostUsd, int Turns);

    /// <summary>
    ///     Reads the CLI's JSON envelope. Internal so the parser is tested against
    ///     captured real output rather than only through a spawn.
    /// </summary>
    /// <param name="outcome">What the child printed.</param>
    /// <returns>The tone JSON and the usage.</returns>
    /// <exception cref="ToneDesignException">The envelope was missing, unparseable,
    /// or reported a failure.</exception>
    internal static CliEnvelope ParseEnvelope(ClaudeCliOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        JsonDocument document;
        try
        {
            // The envelope is one object on stdout. ExtractJsonObject rather than a
            // bare Parse because the CLI is free to print a warning line first, and
            // a startup notice must not read as a failed design.
            var span = ToneDraftJson.ExtractJsonObject(outcome.StandardOutput);
            if (string.IsNullOrWhiteSpace(span)) throw new JsonException("no JSON object on stdout");
            document = JsonDocument.Parse(span);
        }
        catch (JsonException ex)
        {
            throw new ToneDesignException(
                ToneDesignFault.InvalidOutput,
                "Claude Code did not return a tone.",
                outcome.ToRedactedLogLine(), ex);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.TryGetProperty("is_error", out var isError)
                && isError.ValueKind == JsonValueKind.True)
            {
                throw new ToneDesignException(
                    ToneDesignFault.Unknown,
                    "Claude Code could not complete the request.",
                    Subtype(root) ?? outcome.ToRedactedLogLine());
            }

            // `structured_output` is the parsed object the --json-schema run
            // produced and is preferred; `result` is the same thing as a string and
            // is the fallback for a run where the schema was not honoured.
            string? json = null;
            if (root.TryGetProperty("structured_output", out var structured)
                && structured.ValueKind == JsonValueKind.Object)
            {
                json = structured.GetRawText();
            }
            else if (root.TryGetProperty("result", out var result)
                     && result.ValueKind == JsonValueKind.String)
            {
                json = ToneDraftJson.ExtractJsonObject(result.GetString() ?? string.Empty);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ToneDesignException(
                    ToneDesignFault.InvalidOutput,
                    "Claude Code's reply did not contain a tone.",
                    Subtype(root) ?? outcome.ToRedactedLogLine());
            }

            var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
                ? u
                : default;

            // Cache reads and cache writes are input tokens the user paid for in
            // one way or another; reporting only `input_tokens` would show a ~7,500
            // token request as 2. The CLI splits them; the app's one number adds
            // them back up.
            var input = Int(usage, "input_tokens")
                        + Int(usage, "cache_read_input_tokens")
                        + Int(usage, "cache_creation_input_tokens");

            return new CliEnvelope(
                json,
                input,
                Int(usage, "output_tokens"),
                root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number
                    ? cost.GetDouble()
                    : 0d,
                root.TryGetProperty("num_turns", out var turns) && turns.ValueKind == JsonValueKind.Number
                    ? turns.GetInt32()
                    : 0);
        }
    }

    private static string? Subtype(JsonElement root)
        => root.TryGetProperty("subtype", out var s) && s.ValueKind == JsonValueKind.String
            ? $"subtype={s.GetString()}"
            : null;

    private static int Int(JsonElement usage, string name)
        => usage.ValueKind == JsonValueKind.Object
           && usage.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    /// <summary>
    ///     Whether the run failed because nobody is logged in. Matched on stderr AND
    ///     stdout because the CLI reports an auth failure on either depending on how
    ///     far it got, and matched on PHRASES because there is no exit code that
    ///     distinguishes it — a signed-out CLI and a crashed one both exit non-zero,
    ///     and telling the user to check their tone request when the real answer is
    ///     "log in" is the whole reason <see cref="ToneDesignFault.Authentication" />
    ///     is a separate fault.
    /// </summary>
    internal static bool IsAuthenticationFailure(ClaudeCliOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.ExitCode == 0) return false;

        var text = outcome.StandardError + "\n" + outcome.StandardOutput;
        foreach (var marker in AuthMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static readonly string[] AuthMarkers =
    [
        "not logged in", "please log in", "please run /login", "run `claude /login`",
        "/login", "authentication", "unauthorized", "invalid api key",
        "oauth", "credit balance", "expired token", "sign in"
    ];

    /// <summary>An empty folder under TEMP, so the CLI's project auto-discovery finds
    /// nothing to prepend. Created on demand; the path is stable per machine.</summary>
    private static string ScratchDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DotCharter", "ai-cli");
        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder we cannot create is not worth failing a design over;
            // the CLI just gets a cwd with more in it.
            return Path.GetTempPath();
        }
    }
}
