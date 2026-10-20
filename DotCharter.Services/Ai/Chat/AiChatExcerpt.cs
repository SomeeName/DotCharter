using System.Text;

namespace DotCharter.Services.Ai.Chat;

/// <summary>
/// What of a chat goes into the AI chart pass's prompt: the instruction and the
///     LAST few turns before it, each collapsed to one line and clipped, the whole excerpt
///     capped. Every chunk's prompt carries it, so a long chat must not grow every request.
/// </summary>
public static class AiChatExcerpt
{
    /// <summary>The most turns quoted.</summary>
    public const int MaxTurns = 8;

    /// <summary>The longest single quoted turn, in characters.</summary>
    public const int MaxTurnChars = 600;

    /// <summary>The longest excerpt, in characters (the newest turns win).</summary>
    public const int MaxChars = 3000;

    /// <summary>The longest instruction sent, in characters.</summary>
    public const int MaxInstructionChars = 2000;

    /// <summary>The instruction as sent: one line, clipped to <see cref="MaxInstructionChars" />.</summary>
    public static string Instruction(string? text) => Clip(Collapse(text), MaxInstructionChars);

    /// <summary>
    ///     The turns before <paramref name="instruction" />, oldest first, one per line
    ///     (<c>charter:</c> / <c>assistant:</c>), or null when there are none. A last USER
    ///     turn that is the instruction itself (the pane appends it before asking) is not
    ///     quoted twice.
    /// </summary>
    /// <param name="conversation">The chat, or null for none.</param>
    /// <param name="instruction">The instruction being run.</param>
    public static string? Of(AiChatConversation? conversation, string? instruction)
    {
        if (conversation is null || conversation.Turns.Count == 0) return null;
        var turns = conversation.Turns;
        var end = turns.Count;
        if (turns[^1].Role == AiChatRole.User && Collapse(turns[^1].Text) == Collapse(instruction)) end--;

        var lines = new List<string>();
        var total = 0;
        for (var i = end - 1; i >= 0 && lines.Count < MaxTurns; i--)
        {
            var text = Clip(Collapse(turns[i].Text), MaxTurnChars);
            if (text.Length == 0) continue;
            var line = (turns[i].Role == AiChatRole.User ? "charter: " : "assistant: ") + text;
            if (total + line.Length + 1 > MaxChars) break;
            lines.Add(line);
            total += line.Length + 1;
        }

        if (lines.Count == 0) return null;
        lines.Reverse();
        return string.Join("\n", lines);
    }

    /// <summary>Every run of whitespace (line breaks included) as one space, trimmed: a turn
    /// cannot fake the prompt's own section lines.</summary>
    private static string Collapse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Clip(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }
}
