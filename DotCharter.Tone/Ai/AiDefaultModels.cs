namespace DotCharter.Tone.Ai;

/// <summary>
///     The model id each provider kind starts with: what the Settings tab of both
///     apps fills an empty model box with when the kind is picked, and what the
///     CLI providers answer with when the saved id is empty.
///     <list type="bullet">
///         <item>Claude Code CLI: <see cref="ClaudeCliToneModel.DefaultModelId" />,
///         Claude Haiku 5.5 — a tone design is a short structured answer, and the
///         CLI bills it to the user's own subscription.</item>
///         <item>Codex CLI: <see cref="CodexCliToneModel.DefaultModelId" />, GPT-6 Luna.</item>
///         <item>Claude API: <see cref="ClaudeToneModel.DefaultModelId" />.</item>
///     </list>
///     A local OpenAI-compatible server has no default: its model id is whatever
///     the user loaded there.
/// </summary>
public static class AiDefaultModels
{
    /// <summary>The default model id for <paramref name="kind" />, or null when the
    /// kind has none.</summary>
    public static string? For(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Claude => ClaudeToneModel.DefaultModelId,
        AiProviderKind.ClaudeCli => ClaudeCliToneModel.DefaultModelId,
        AiProviderKind.CodexCli => CodexCliToneModel.DefaultModelId,
        _ => null
    };

    /// <summary>
    ///     The model id to show after the picker moves from <paramref name="previous" />
    ///     to <paramref name="picked" />. An empty box takes the picked kind's default.
    ///     A box still holding the PREVIOUS kind's untouched default takes it too, so
    ///     moving from Claude Code to Codex never hands <c>claude-haiku-5-5</c> to
    ///     Codex. Anything else was typed or saved by the user and is kept, including
    ///     the picked kind's own older default.
    /// </summary>
    public static string Seed(AiProviderKind previous, AiProviderKind picked, string current)
    {
        var trimmed = current.Trim();
        var leftOver = previous != picked && trimmed.Length > 0
            && string.Equals(trimmed, For(previous), StringComparison.Ordinal);
        if (trimmed.Length > 0 && !leftOver) return current;
        return For(picked) ?? "";
    }
}
