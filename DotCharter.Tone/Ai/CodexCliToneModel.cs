using System.Text;
using System.Text.Json;

namespace DotCharter.Tone.Ai;

/// <summary>
///     Finds the OpenAI Codex CLI (<c>codex</c>). Same contract as
///     <see cref="IClaudeCliLocator" />: an explicit path wins (and a missing one is
///     null, never a silent fallback to a different binary), otherwise <c>PATH</c>
///     and then the per-user install folder the official installer uses.
/// </summary>
public sealed class CodexCliLocator : IClaudeCliLocator
{
    /// <summary>The names tried, native <c>.exe</c> first — the npm install adds a
    /// <c>.cmd</c> shim that has to go through <c>cmd.exe</c>.</summary>
    public static IReadOnlyList<string> CandidateNames { get; } = OperatingSystem.IsWindows()
        ? ["codex.exe", "codex.cmd", "codex"]
        : ["codex"];

    private static readonly Lazy<string?> Lookup = new(Search, isThreadSafe: true);

    private readonly IAppLogger? _logger;

    public CodexCliLocator(IAppLogger? logger = null) => _logger = logger;

    public string? Locate(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var trimmed = explicitPath.Trim().Trim('"');
            return File.Exists(trimmed) ? Path.GetFullPath(trimmed) : null;
        }

        var found = Lookup.Value;
        _logger?.Debug(AppLogCategory.Application, $"Codex CLI lookup: {found ?? "not found"}.");
        return found;
    }

    /// <summary>The per-user folder the Windows installer puts <c>codex.exe</c> in
    /// (<c>%LOCALAPPDATA%\Programs\OpenAI\Codex\bin</c>) — searched after PATH,
    /// because that installer does not always add itself to it.</summary>
    internal static string InstallFolder() => Path.Combine(
        UserDataFolders.OtherProgramsLocalAppData,
        "Programs", "OpenAI", "Codex", "bin");

    private static string? Search()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(InstallFolder());
        foreach (var dir in dirs)
        {
            string full;
            try { full = Path.GetFullPath(dir.Trim().Trim('"')); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            foreach (var name in CandidateNames)
            {
                try
                {
                    var candidate = Path.Combine(full, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // one bad entry must not stop the walk
                }
            }
        }

        return null;
    }
}

/// <summary>
///     The OpenAI Codex CLI provider: the installed <c>codex</c> binary, headless
///     (<c>codex exec</c>), under the user's OWN Codex login (ChatGPT sign-in or an
///     API key the CLI holds). Like <see cref="ClaudeCliToneModel" /> it is OPT-IN:
///     every design counts against that login's usage, and the app never passes a
///     key to the child.
///     <para>
///         The command line, measured against <c>codex exec --help</c> on
///         <b>0.156.1</b> and exercised end to end:
///     </para>
///     <code>
///     exec --skip-git-repo-check --ephemeral --ignore-user-config --ignore-rules
///          --sandbox read-only --color never --json
///          --output-schema &lt;file&gt; -o &lt;file&gt; [-m &lt;model&gt;] -
///     </code>
///     <list type="bullet">
///         <item><c>--ignore-user-config</c> keeps the user's <c>config.toml</c> out
///         (auth still comes from <c>CODEX_HOME</c>): without it the user's MCP
///         servers start for a question about guitar tones — measured, their
///         connection errors filled stderr and the request grew by ~2,600 tokens.</item>
///         <item><c>--sandbox read-only</c>: the agent may not write anything; the
///         answer is a JSON object, not a change to the machine.</item>
///         <item><c>--output-schema</c> takes a FILE; the reply is written to the
///         <c>-o</c> file as the schema-shaped object. <c>--json</c> prints JSONL
///         events whose <c>turn.completed</c> carries the usage.</item>
///         <item>The grounding and the conversation go on STDIN (<c>-</c>): the
///         catalogue does not fit a Windows command line.</item>
///         <item>An empty model id means <see cref="DefaultModelId" />, GPT-6 Luna
///         (owner), so <c>-m</c> is always passed.</item>
///         <item><c>-c model_reasoning_effort="…"</c> only when Settings ▸ AI names a
/// level. It is the <c>config.toml</c> key, and <c>--ignore-user-config</c>
///         means the user's own value never reaches this run — so Default is Codex's
///         built-in default, not whatever the user set for their terminal.</item>
///     </list>
/// </summary>
public sealed class CodexCliToneModel : IToneDesignModel
{
    /// <summary>GPT-6 Sol, one of the two model ids the Settings tab offers for this
    /// provider as a fill-in button.</summary>
    public const string SolModelId = "gpt-6-sol";

    /// <summary>GPT-6 Luna, the other fill-in model id — the cheaper one.</summary>
    public const string LunaModelId = "gpt-6-luna";

    /// <summary>The model used when the settings do not name one.</summary>
    public const string DefaultModelId = LunaModelId;

    /// <summary>A generous deadline: Codex runs its own agent loop around the answer.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(240);

    /// <summary>The deadline when the model may research the song first.</summary>
    public static readonly TimeSpan ResearchTimeout = TimeSpan.FromSeconds(360);

    /// <summary>The framing the stdin prompt opens with.</summary>
    public const string FramingPrompt =
        "You are a guitar amp and effects tone designer inside DotCharter. " +
        "Answer with a single JSON object matching the supplied output schema and nothing else. " +
        "Do not run commands, do not read or write files, and do not ask questions.";

    private readonly AiReasoningEffort _effort;
    private readonly string _executablePath;
    private readonly IAppLogger? _logger;
    private readonly string _modelId;
    private readonly bool _research;
    private readonly IClaudeCliRunner _runner;
    private readonly string _workingDirectory;

    public CodexCliToneModel(
        AiProviderSettings settings, string executablePath, IClaudeCliRunner runner,
        IAppLogger? logger = null, string? workingDirectory = null)
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
    }

    public AiProviderKind Kind => AiProviderKind.CodexCli;

    public async Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ct.ThrowIfCancellationRequested();

        var id = Guid.NewGuid().ToString("N");
        var schemaFile = Path.Combine(_workingDirectory, $"schema-{id}.json");
        var replyFile = Path.Combine(_workingDirectory, $"reply-{id}.json");
        try
        {
            try
            {
                await File.WriteAllTextAsync(schemaFile, prompt.JsonSchema, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ToneDesignException(ToneDesignFault.Unknown,
                    "Could not write the Codex output schema to the temp folder.", ex.Message, ex);
            }

            var request = new ClaudeCliRequest(
                _executablePath,
                BuildArguments(schemaFile, replyFile),
                BuildStandardInput(prompt, _research),
                _workingDirectory,
                _research ? ResearchTimeout : Timeout);
            var outcome = await _runner.RunAsync(request, ct).ConfigureAwait(false);

            if (outcome.TimedOut)
            {
                throw new ToneDesignException(ToneDesignFault.Network,
                    $"Codex did not answer within {Timeout.TotalSeconds:F0} seconds.", outcome.ToRedactedLogLine());
            }

            if (ClaudeCliToneModel.IsAuthenticationFailure(outcome) || IsCodexAuthFailure(outcome))
            {
                throw new ToneDesignException(ToneDesignFault.Authentication,
                    "Codex is not signed in. Run \"codex login\" in a terminal, then try again.",
                    outcome.ToRedactedLogLine());
            }

            var events = ParseEvents(outcome.StandardOutput);
            if (outcome.ExitCode != 0 || events.Failure is not null)
            {
                throw new ToneDesignException(ToneDesignFault.Unknown,
                    "Codex could not complete the request.", events.Failure ?? outcome.ToRedactedLogLine());
            }

            string reply;
            try { reply = File.Exists(replyFile) ? await File.ReadAllTextAsync(replyFile, ct).ConfigureAwait(false) : string.Empty; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reply = string.Empty;
            }

            var json = ToneDraftJson.ExtractJsonObject(reply);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ToneDesignException(ToneDesignFault.InvalidOutput,
                    "Codex's reply did not contain a tone.", outcome.ToRedactedLogLine());
            }

            var usage = new AiUsage(events.Model ?? _modelId,
                events.InputTokens, events.OutputTokens);
            _logger?.Info(AppLogCategory.Application,
                $"AI tone design (Codex CLI {usage.ModelId}): {usage.InputTokens} in / {usage.OutputTokens} out tokens.");
            return new ToneDesignModelReply(json, usage);
        }
        finally
        {
            BestEffortDelete.File(schemaFile);
            BestEffortDelete.File(replyFile);
        }
    }

    /// <summary><c>codex --version</c>: free, offline, and answers whether the
    /// binary is there and runnable.</summary>
    public async Task<AiProbeResult> ProbeAsync(CancellationToken ct)
    {
        ClaudeCliOutcome outcome;
        try
        {
            outcome = await _runner.RunAsync(
                new ClaudeCliRequest(_executablePath, ["--version"], string.Empty, _workingDirectory,
                    TimeSpan.FromSeconds(30)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ToneDesignException ex)
        {
            return new AiProbeResult(false, ex.Message, null);
        }

        if (outcome.TimedOut || outcome.ExitCode != 0)
        {
            return new AiProbeResult(false,
                "The Codex command failed to run. Install the Codex CLI and run \"codex login\", then try again.", null);
        }

        var version = outcome.StandardOutput.Trim();
        if (version.Length == 0) version = "unknown version";
        return new AiProbeResult(true, $"{version} at {_executablePath}, {_modelId}.", _modelId);
    }

    /// <summary>The argument list — the contract with the CLI, asserted exactly by
    /// the tests.</summary>
    internal IReadOnlyList<string> BuildArguments(string schemaFile, string replyFile) =>
    [
        "exec",
        "--skip-git-repo-check",
        "--ephemeral",
        "--ignore-user-config",
        "--ignore-rules",
        "--sandbox", "read-only",
        "--color", "never",
        "--json",
        "--output-schema", schemaFile,
        "-o", replyFile,
        // Codex's own web search: LIVE when the model may research the song, and
        // explicitly OFF otherwise (its default is a cached index, which would
        // search anyway).
        "-c", _research ? "web_search=\"live\"" : "web_search=\"disabled\"",
        // Reasoning effort: a TOML string, overriding the config key for this run.
        .. AiReasoning.WireValue(_effort, AiProviderKind.CodexCli, _modelId) is { } effort
            ? (string[])["-c", $"model_reasoning_effort=\"{effort}\""]
            : [],
        "-m", _modelId,
        "-"
    ];

    /// <summary>What is piped in: the framing, the grounding, then the labelled
    /// conversation — the same layout <see cref="ClaudeCliToneModel" /> uses on
    /// its stdin path.</summary>
    internal static string BuildStandardInput(ToneDesignPrompt prompt, bool research = false)
    {
        var text = new StringBuilder(prompt.System.Length + 1024);
        text.Append(FramingPrompt).Append("\n\n").Append(prompt.System);
        if (research) text.Append("\n\n").Append(ToneDesignResearch.Instruction);
        foreach (var turn in prompt.Messages)
        {
            text.Append("\n\n");
            text.Append(turn.Role == ToneDesignRole.Assistant ? "## Your previous answer\n" : "## Request\n");
            text.Append(turn.Text);
        }
        return text.ToString();
    }

    internal sealed record CodexEvents(int InputTokens, int OutputTokens, string? Model, string? Failure);

    /// <summary>Reads the JSONL event stream: usage off <c>turn.completed</c>, a
    /// failure off <c>error</c> / <c>turn.failed</c>. Lines that are not JSON are
    /// skipped — the CLI may print notices.</summary>
    internal static CodexEvents ParseEvents(string stdout)
    {
        int input = 0, output = 0;
        string? model = null, failure = null;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (type == "turn.completed" && root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    input += Int(usage, "input_tokens");
                    output += Int(usage, "output_tokens") + Int(usage, "reasoning_output_tokens");
                }
                else if (type is "error" or "turn.failed")
                {
                    failure = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString()
                        : root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                          && e.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String
                            ? em.GetString()
                            : type;
                }

                if (root.TryGetProperty("model", out var mdl) && mdl.ValueKind == JsonValueKind.String)
                    model = mdl.GetString();
            }
            catch (JsonException)
            {
                // a notice line, not an event
            }
        }
        return new CodexEvents(input, output, model, failure);
    }

    private static bool IsCodexAuthFailure(ClaudeCliOutcome outcome)
    {
        if (outcome.ExitCode == 0) return false;
        var text = outcome.StandardError + "\n" + outcome.StandardOutput;
        return text.Contains("codex login", StringComparison.OrdinalIgnoreCase)
               || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
               || text.Contains("401", StringComparison.Ordinal);
    }

    private static int Int(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>An empty folder under TEMP, so the agent's workspace is nothing of
    /// the user's.</summary>
    private static string ScratchDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DotCharter", "ai-codex");
        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetTempPath();
        }
    }
}
