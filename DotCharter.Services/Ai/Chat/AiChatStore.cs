namespace DotCharter.Services.Ai.Chat;

/// <summary>
///     The synchronous file half of <see cref="AiChatHistory" />, for ONE chart:
///     <c>&lt;project folder&gt;/ai-chats/&lt;chart id&gt;/&lt;conversation id&gt;.json</c>.
///     <para>
///         Every read checks the file's own <c>chartId</c> against the chart it
///         was asked for, so a conversation copied, moved or edited into another
///         chart's folder is refused (<see cref="AiChatStatus.ChartMismatch" />),
///         never loaded, never overwritten and never deleted from here. The three
///         read outcomes stay apart (infrastructure-update.md § JSON stores):
///         absent is <see cref="AiChatStatus.NotFound" />; corrupt is refused and
///         moved aside only when a save for the same id replaces it; unreadable is
///         presumed intact and never written over. Writes are atomic
///         (<see cref="AtomicFile" />). Not thread-safe: the service serializes calls.
///     </para>
/// </summary>
internal sealed class AiChatStore
{
    /// <summary>The folder under the project folder that holds every chart's chats.</summary>
    internal const string FolderName = "ai-chats";

    private const string Extension = ".json";

    private readonly string _chartId;

    private AiChatStore(string chartId, string directory)
    {
        _chartId = chartId;
        FolderPath = directory;
    }

    /// <summary>This chart's chat folder (it may not exist yet).</summary>
    internal string FolderPath { get; }

    /// <summary>The store for <paramref name="chart" />, or the refusal that stops one.</summary>
    internal static AiChatStatus TryOpen(AiChatChart? chart, out AiChatStore? store)
    {
        store = null;
        if (chart is null) return AiChatStatus.NoChart;
        if (!ProjectIdentity.IsWellFormed(chart.ChartId)) return AiChatStatus.InvalidId;
        if (string.IsNullOrWhiteSpace(chart.ProjectFolder)) return AiChatStatus.ChartNotSaved;
        store = new AiChatStore(chart.ChartId, Path.Combine(chart.ProjectFolder, FolderName, chart.ChartId));
        return AiChatStatus.Ok;
    }

    internal AiChatListing List()
    {
        if (!Directory.Exists(FolderPath)) return new AiChatListing(AiChatStatus.Ok, _chartId, [], []);

        var conversations = new List<AiChatSummary>();
        var refused = new List<AiChatRefusal>();
        foreach (var id in ConversationIds().Take(AiChatLimits.MaxConversationsPerChart * 2))
        {
            var status = Read(id, out var conversation, out _);
            if (status == AiChatStatus.Ok) conversations.Add(conversation!.ToSummary());
            else refused.Add(new AiChatRefusal(id, status));
        }

        conversations.Sort((a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
        return new AiChatListing(AiChatStatus.Ok, _chartId, conversations, refused);
    }

    /// <summary>Reads conversation <paramref name="id" /> and checks it is THIS chart's.</summary>
    internal AiChatStatus Read(string id, out AiChatConversation? conversation, out string? detail)
    {
        conversation = null;
        detail = null;
        if (!ProjectIdentity.IsWellFormed(id)) return AiChatStatus.InvalidId;

        var path = PathOf(id);
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return AiChatStatus.NotFound;
            if (info.Length > AiChatLimits.MaxConversationBytes)
            {
                detail = $"{info.Length} bytes";
                return AiChatStatus.TooLarge;
            }

            bytes = AtomicFile.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = ex.Message;
            return AiChatStatus.Unreadable;
        }

        if (bytes.Length > AiChatLimits.MaxConversationBytes) return AiChatStatus.TooLarge;

        var status = AiChatJson.TryRead(bytes, out var read, out detail);
        if (status != AiChatStatus.Ok) return status;
        if (!string.Equals(read!.Id, id, StringComparison.Ordinal))
        {
            detail = $"the file says it is conversation {read.Id}";
            return AiChatStatus.Corrupt;
        }

        // THE rule: a conversation is only ever opened for its own chart.
        if (!string.Equals(read.ChartId, _chartId, StringComparison.Ordinal))
        {
            detail = $"it belongs to chart {read.ChartId}";
            return AiChatStatus.ChartMismatch;
        }

        conversation = read;
        return AiChatStatus.Ok;
    }

    internal AiChatWriteResult Write(AiChatConversation conversation)
    {
        if (!ProjectIdentity.IsWellFormed(conversation.Id)) return new AiChatWriteResult(AiChatStatus.InvalidId);
        if (!string.Equals(conversation.ChartId, _chartId, StringComparison.Ordinal))
        {
            return new AiChatWriteResult(AiChatStatus.ChartMismatch,
                $"conversation {conversation.Id} belongs to chart {conversation.ChartId}");
        }

        var bytes = AiChatJson.Serialize(conversation);
        if (bytes.Length > AiChatLimits.MaxConversationBytes)
            return new AiChatWriteResult(AiChatStatus.TooLarge, $"{bytes.Length} bytes");

        var path = PathOf(conversation.Id);
        var existing = Read(conversation.Id, out _, out var detail);
        switch (existing)
        {
            case AiChatStatus.Ok:
                break;
            case AiChatStatus.NotFound when CountConversations() >= AiChatLimits.MaxConversationsPerChart:
                return new AiChatWriteResult(AiChatStatus.LimitReached);
            case AiChatStatus.NotFound:
                break;
            case AiChatStatus.Corrupt:
                // Ours by name, unreadable as a conversation: keep the bytes aside
                // rather than destroy them, then write the good one.
                if (!TryQuarantine(path, out detail)) return new AiChatWriteResult(AiChatStatus.WriteFailed, detail);
                break;
            default:
                // Another chart's, unreadable, too large or a newer format — none
                // of them is this chart's to replace.
                return new AiChatWriteResult(existing, detail);
        }

        try
        {
            Directory.CreateDirectory(FolderPath);
            AtomicFile.WriteAllBytes(path, bytes);
            return AiChatWriteResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AiChatWriteResult(AiChatStatus.WriteFailed, ex.Message);
        }
    }

    internal AiChatWriteResult Delete(string id)
    {
        var status = Read(id, out _, out var detail);
        if (status != AiChatStatus.Ok) return new AiChatWriteResult(status, detail);
        try
        {
            File.Delete(PathOf(id));
            return AiChatWriteResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AiChatWriteResult(AiChatStatus.WriteFailed, ex.Message);
        }
    }

    private string PathOf(string id) => Path.Combine(FolderPath, id + Extension);

    /// <summary>The ids of the files in this chart's folder that are NAMED like a
    /// conversation (a quarantined <c>&lt;id&gt;.corrupt-…json</c> is not).</summary>
    private List<string> ConversationIds()
    {
        var ids = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(FolderPath, "*" + Extension))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (ProjectIdentity.IsWellFormed(id)) ids.Add(id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unlistable folder lists as what could be listed; nothing is deleted on its strength.
        }

        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    private int CountConversations() => Directory.Exists(FolderPath) ? ConversationIds().Count : 0;

    private static bool TryQuarantine(string path, out string? error)
    {
        error = null;
        var aside = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}{Extension}");
        try
        {
            File.Move(path, aside, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
