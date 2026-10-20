using System.Text;

namespace DotCharter.Services.Ai.Chat;

/// <summary>Who said a turn.</summary>
public enum AiChatRole
{
    User,
    Assistant
}

/// <summary>What a provider reported for one reply; every part is optional because
/// not every provider reports it (the Claude Code CLI reports none).</summary>
public sealed record AiChatUsage(int? InputTokens = null, int? OutputTokens = null, decimal? CostUsd = null)
{
    /// <summary>True when nothing was reported.</summary>
    public bool IsEmpty => InputTokens is null && OutputTokens is null && CostUsd is null;
}

/// <summary>
///     One turn of a conversation. <paramref name="ReviewId" /> optionally links the
///     proposals/review card the turn produced (an opaque id the review owns).
/// </summary>
public sealed record AiChatTurn(
    AiChatRole Role,
    string Text,
    DateTimeOffset At,
    AiChatUsage? Usage = null,
    string? ReviewId = null);

/// <summary>What the history picker shows for a conversation, read without its turns.</summary>
public sealed record AiChatSummary(
    string Id,
    string Title,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string ModelId,
    int TurnCount);

/// <summary>
/// One saved AI chat. It belongs to exactly ONE chart, named by
///     <paramref name="ChartId" /> (the project's <see cref="ChartProject.ProjectId" />),
///     and <see cref="AiChatHistory" /> refuses to load or write it for any other.
///     Immutable: <see cref="Append" /> and <c>with</c> return a new value.
/// </summary>
public sealed record AiChatConversation(
    string Id,
    string ChartId,
    string Title,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string ModelId,
    IReadOnlyList<AiChatTurn> Turns)
{
    /// <summary>A new, empty conversation for <paramref name="chartId" />. Its title
    /// comes from the first user message (<see cref="Append" />).</summary>
    public static AiChatConversation Start(string chartId, string modelId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(chartId);
        return new AiChatConversation(ProjectIdentity.NewId(), chartId, "", now, now, modelId ?? "", []);
    }

    /// <summary>
    ///     This conversation with <paramref name="turn" /> added: the update time
    ///     follows the turn, and an untitled conversation takes its title from its
    ///     first USER message (<see cref="AiChatTitle.FromFirstMessage" />). A
    ///     renamed one keeps its name.
    /// </summary>
    public AiChatConversation Append(AiChatTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var title = Title.Length == 0 && turn.Role == AiChatRole.User
            ? AiChatTitle.FromFirstMessage(turn.Text)
            : Title;
        return this with
        {
            Title = title,
            UpdatedUtc = turn.At > UpdatedUtc ? turn.At : UpdatedUtc,
            Turns = [.. Turns, turn]
        };
    }

    /// <summary>The picker's view of this conversation.</summary>
    public AiChatSummary ToSummary() => new(Id, Title, CreatedUtc, UpdatedUtc, ModelId, Turns.Count);
}

/// <summary>How a conversation is named.</summary>
public static class AiChatTitle
{
    /// <summary>The longest title, derived or renamed; a longer one is cut at a word.</summary>
    public const int MaxLength = 80;

    /// <summary>
    ///     The title a first message gives its conversation: whitespace (line
    ///     breaks included) collapsed to single spaces, trimmed, and cut at a word
    ///     boundary to <see cref="MaxLength" /> with an ellipsis. Empty for an empty
    ///     message — the pane shows its own "New chat" label for that.
    /// </summary>
    public static string FromFirstMessage(string? text) => Clip(Collapse(text));

    /// <summary>A user-chosen title, collapsed and clipped the same way.</summary>
    public static string Normalize(string? title) => Clip(Collapse(title));

    private static string Collapse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder(Math.Min(text.Length, MaxLength * 2));
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(c);
            if (builder.Length > MaxLength * 2) break;
        }

        return builder.ToString();
    }

    private static string Clip(string text)
    {
        if (text.Length <= MaxLength) return text;
        var cut = text.LastIndexOf(' ', MaxLength - 1);
        if (cut < MaxLength / 2)
        {
            // No word boundary in the second half: a hard cut, never between the
            // two halves of a surrogate pair (an emoji, a rare kanji).
            cut = MaxLength - 1;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
        }

        var head = text[..cut];
        return head.TrimEnd() + "…";
    }
}
