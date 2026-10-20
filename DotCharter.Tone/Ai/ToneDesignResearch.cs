namespace DotCharter.Tone.Ai;

/// <summary>
///     Online research before a design (<see cref="AiProviderSettings.WebResearch" />).
///     Only the CLI providers can do it — they bring their own web tools (Claude
///     Code's <c>WebSearch</c> / <c>WebFetch</c>, Codex's live <c>web_search</c>);
///     the API providers ask a model with no tools and ignore the setting.
///     <para>
///         The instruction goes on the CONVERSATION side, never into the system
///         prompt: the grounding is cached per capture library, and a setting that
///         changed it would throw that cache away for every design.
///     </para>
/// </summary>
public static class ToneDesignResearch
{
    /// <summary>The Claude Code tools research needs, and the only ones given.</summary>
    public const string ClaudeCliTools = "WebSearch,WebFetch";

    /// <summary>What the model is told it may do first.</summary>
    public const string Instruction =
        "## Research\n" +
        "You may search the web before you answer. When the request names a song, an album, an artist or a " +
        "player, first look up the gear used ON THAT RECORDING: amp (and channel), cab and speakers, mic, " +
        "guitar and pickups, pedals and boosts, and the tuning. Prefer rig rundowns, interviews and gear " +
        "forums over guesses, and keep it to a few searches. Then build the tone from what you found with the " +
        "modules in the catalogue, and say in the explanation what the recording used and how the chain " +
        "stands in for it. A request that names no recording needs no research.";
}
