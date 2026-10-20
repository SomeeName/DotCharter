using System.Text;

namespace DotCharter.Tone.Ai;

/// <summary>
///     The "ChatGPT (copy &amp; paste)" route's prompt text: one
///     <see cref="ToneDesignPrompt" /> — the exact prompt a provider is sent —
///     flattened into a single block a player pastes into a chat window.
///
///     <para>
///         <b>It stays ENGLISH whatever the UI language</b>: it is written for the
///         model, like every other prompt in this folder, and the catalogue it
///         carries is English anyway.
///     </para>
///     <para>
///         <b>The layout follows the CLI providers'</b> (<c>CodexCliToneModel.BuildStandardInput</c>):
///         a framing line, the system prompt, the labelled conversation. What a chat
///         window lacks is a structured-output switch, so the schema goes in the
///         text and the block ENDS on the instruction to answer with the JSON
///         object only — the last thing a chat model reads is what it follows best.
///         A reply that still wraps the object in prose or a <c>```json</c> fence
///         is fine: the import extracts it (<see cref="ToneDraftJson.ExtractJsonObject" />).
///     </para>
/// </summary>
public static class ToneDesignManualPrompts
{
    /// <summary>What the chat is told it is, before anything else.</summary>
    public const string Framing =
        "You are a guitar amp and effects tone designer for DotCharter, a guitar practice app with its own " +
        "amp and effects rig. Everything below is the app's own instructions, its catalogue and the player's " +
        "request. Design the tone, then answer in the exact format described at the end.";

    /// <summary>The block's last line: answer with the object and nothing else.</summary>
    public const string FinalInstruction =
        "Answer with ONLY the JSON object — no explanation before or after it. The \"rationale\" field is " +
        "where your reasoning goes.";

    /// <summary>Flattens <paramref name="prompt" /> into the text the player copies.</summary>
    /// <param name="prompt">The prompt a provider would be sent.</param>
    /// <param name="research">True when the chat may search the web for the named
    /// song's recorded rig first (<see cref="AiProviderSettings.WebResearch" />) — a
    /// chat subscription usually can, like the CLI providers.</param>
    public static string Flatten(ToneDesignPrompt prompt, bool research)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var text = new StringBuilder(prompt.System.Length + prompt.JsonSchema.Length + 2048);
        text.Append(Framing).Append("\n\n");
        text.Append(prompt.System.TrimEnd());
        if (research) text.Append("\n\n").Append(ToneDesignResearch.Instruction);

        foreach (var turn in prompt.Messages)
        {
            text.Append("\n\n");
            text.Append(turn.Role == ToneDesignRole.Assistant ? "## Your previous answer\n" : "## Request\n");
            text.Append(turn.Text.TrimEnd());
        }

        text.Append("\n\n# Output format\n\n");
        text.Append("Reply with a single JSON object that validates against this JSON Schema (\"")
            .Append(prompt.SchemaName).Append("\"):\n\n");
        text.Append(prompt.JsonSchema.Trim()).Append("\n\n");
        text.Append(FinalInstruction).Append('\n');
        return text.ToString();
    }
}
