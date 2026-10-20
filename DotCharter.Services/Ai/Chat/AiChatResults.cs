namespace DotCharter.Services.Ai.Chat;

/// <summary>
///     The outcome of an <see cref="IAiChatHistory" /> call. Everything except
///     <see cref="Ok" /> is a REFUSAL the pane shows with its reason; nothing is
///     loaded, written or deleted for it.
/// </summary>
public enum AiChatStatus
{
    Ok,

    /// <summary>No chart is open.</summary>
    NoChart,

    /// <summary>The open chart has no project folder yet (a library song, an import
    /// not saved as a project): there is nowhere for its chats to live.</summary>
    ChartNotSaved,

    /// <summary>The conversation belongs to ANOTHER chart — the issue's hard rule.
    /// Never loaded, never overwritten, never deleted from this chart.</summary>
    ChartMismatch,

    /// <summary>No conversation with that id for this chart.</summary>
    NotFound,

    /// <summary>The file is not a conversation (bad JSON, a missing field, an id
    /// that does not match its file name).</summary>
    Corrupt,

    /// <summary>A newer build wrote it; this build does not read that format.</summary>
    UnsupportedFormat,

    /// <summary>The file exists but could not be read (a lock, a permission, an AV
    /// hold). Presumed intact, so it is never written over.</summary>
    Unreadable,

    /// <summary>Over <see cref="AiChatLimits.MaxConversationBytes" />.</summary>
    TooLarge,

    /// <summary>The chart already has <see cref="AiChatLimits.MaxConversationsPerChart" />.</summary>
    LimitReached,

    /// <summary>An id that is not 32 lowercase hex digits.</summary>
    InvalidId,

    /// <summary>The write or delete itself failed.</summary>
    WriteFailed
}

/// <summary>The size bounds of the chat history. Chats are not chart content, but
/// they live beside the project and must not grow without limit.</summary>
public static class AiChatLimits
{
    /// <summary>The largest conversation file read or written (1 MiB — some hundreds
    /// of long replies). A save over it is refused (start a new chat); a file over it
    /// is not read.</summary>
    public const int MaxConversationBytes = 1024 * 1024;

    /// <summary>The most conversations one chart keeps; a NEW one past it is refused.</summary>
    public const int MaxConversationsPerChart = 200;
}

/// <summary>A file in the chart's chat folder that was not listed, and why.</summary>
public sealed record AiChatRefusal(string ConversationId, AiChatStatus Reason);

/// <summary>
///     The open chart's conversations, newest first. <paramref name="ChartId" /> is
///     the chart they were listed FOR (null when none is open): the pane compares it
///     with the chart open when the listing arrives, because the I/O runs off the UI
///     thread and the user may have switched charts meanwhile.
/// </summary>
public sealed record AiChatListing(
    AiChatStatus Status,
    string? ChartId,
    IReadOnlyList<AiChatSummary> Conversations,
    IReadOnlyList<AiChatRefusal> Refused)
{
    internal static AiChatListing Refusal(AiChatStatus status, string? chartId) => new(status, chartId, [], []);
}

/// <summary>A loaded conversation, or why not; <paramref name="Detail" /> is English for the log.</summary>
public sealed record AiChatLoadResult(AiChatStatus Status, AiChatConversation? Conversation, string? Detail = null)
{
    internal static AiChatLoadResult Refusal(AiChatStatus status, string? detail = null) => new(status, null, detail);
}

/// <summary>The outcome of a save, rename or delete; <paramref name="Detail" /> is English for the log.</summary>
public sealed record AiChatWriteResult(AiChatStatus Status, string? Detail = null)
{
    internal static readonly AiChatWriteResult Ok = new(AiChatStatus.Ok);
}
