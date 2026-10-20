namespace DotCharter.Tone.Ai;

/// <summary>
/// How hard the model reasons before it answers, chosen in Settings ▸ AI.
///     <see cref="Default" /> sends NOTHING, which is what every build before it did: the
///     provider's own default (adaptive on the Claude API, the CLI's own on Claude Code,
///     <c>medium</c> on Codex, nothing on an OpenAI-compatible server). The ordinals are
///     persisted by NAME (the config's string-enum converter), so a new member appends.
/// </summary>
public enum AiReasoningEffort
{
    Default = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

/// <summary>Why a provider/model cannot honour a reasoning choice: the row is then shown
/// disabled, with this reason printed under it.</summary>
public enum AiReasoningUnsupported
{
    /// <summary>It can: the choice is sent.</summary>
    None = 0,

    /// <summary>No provider is chosen.</summary>
    NoProvider = 1,

    /// <summary>A Claude model older than the effort parameter (it arrived with Claude Opus
    /// 4.5 and Sonnet 4.6; Haiku 4.5, Sonnet 4.5 and older answer it with a 400).</summary>
    ClaudeModelTooOld = 2
}

/// <summary>
///     The ONE place that decides whether, and as what, a reasoning choice reaches a provider.
///     Each provider asks <see cref="WireValue" /> with its own kind and the model it is about
///     to call; the Settings row and the chat's label ask <see cref="Support" /> with the same
///     two, so a choice the row shows as live is exactly the choice the request carries.
///     <list type="bullet">
///         <item>Claude API: <c>output_config.effort</c> (GA, no beta header).</item>
///         <item>Claude Code CLI: <c>--effort &lt;level&gt;</c> (<c>claude --help</c>, 2.1.296:
///         "Effort level for the current session (low, medium, high, xhigh, max)").</item>
///         <item>Codex CLI: <c>-c model_reasoning_effort="&lt;level&gt;"</c> — the <c>config.toml</c>
///         key, overridden per run because the provider runs with <c>--ignore-user-config</c>
///         (<c>codex exec --help</c>, 0.162.1: "-c key=value … Override a configuration value").</item>
///         <item>OpenAI-compatible: <c>reasoning_effort</c>; OpenRouter's own
///         <c>reasoning.effort</c>. A server that refuses it is asked once more without it.</item>
///     </list>
/// </summary>
public static class AiReasoning
{
    /// <summary>Claude model-id prefixes that predate the effort parameter. Matched on the
    /// PREFIX so a dated snapshot (<c>claude-sonnet-4-20250514</c>) is caught with its
    /// family; an alias the CLI resolves itself (<c>opus</c>, <c>sonnet</c>) is current.</summary>
    private static readonly string[] ClaudeIdsWithoutEffort =
    [
        "claude-3",
        "claude-haiku-4",
        "claude-sonnet-4-0",
        "claude-sonnet-4-2",
        "claude-sonnet-4-5",
        "claude-opus-4-0",
        "claude-opus-4-1",
        "claude-opus-4-2"
    ];

    /// <summary>The wire spelling, or null for <see cref="AiReasoningEffort.Default" />.</summary>
    public static string? Level(AiReasoningEffort effort) => effort switch
    {
        AiReasoningEffort.Low => "low",
        AiReasoningEffort.Medium => "medium",
        AiReasoningEffort.High => "high",
        _ => null
    };

    /// <summary>Whether <paramref name="kind" /> on <paramref name="modelId" /> (the id the
    /// request will actually name — an empty box already resolved to the provider's default)
    /// can take a reasoning choice.</summary>
    public static AiReasoningUnsupported Support(AiProviderKind kind, string? modelId) => kind switch
    {
        AiProviderKind.None or AiProviderKind.BuiltIn => AiReasoningUnsupported.NoProvider,
        AiProviderKind.Claude or AiProviderKind.ClaudeCli when IsClaudeWithoutEffort(modelId)
            => AiReasoningUnsupported.ClaudeModelTooOld,
        _ => AiReasoningUnsupported.None
    };

    /// <summary>What the request carries: the level when the choice is set AND this provider
    /// and model can honour it, else null — which every provider reads as "send nothing".</summary>
    public static string? WireValue(AiReasoningEffort effort, AiProviderKind kind, string? modelId)
        => Support(kind, modelId) == AiReasoningUnsupported.None ? Level(effort) : null;

    private static bool IsClaudeWithoutEffort(string? modelId)
    {
        var id = (modelId ?? "").Trim();
        return ClaudeIdsWithoutEffort.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
