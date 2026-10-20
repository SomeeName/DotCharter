namespace DotCharter.Services.Ai.Chat;

/// <summary>
/// The saved AI chats of the OPEN chart. Every call asks
///     <see cref="IAiChatChartSource" /> which chart is open, on the caller's thread,
///     and then works on that chart alone: another chart's conversation is
///     refused (<see cref="AiChatStatus.ChartMismatch" />) — never listed, loaded,
///     overwritten or deleted.
/// </summary>
public interface IAiChatHistory
{
    /// <summary>The open chart's id, or null when none is open.</summary>
    string? OpenChartId { get; }

    /// <summary>A new, unsaved conversation for the open chart; null when none is open.
    /// It is written by the first <see cref="SaveAsync" />.</summary>
    AiChatConversation? StartNew(string modelId);

    /// <summary>The open chart's conversations, newest first, plus the files that
    /// were refused and why.</summary>
    Task<AiChatListing> ListAsync(CancellationToken cancellationToken = default);

    Task<AiChatLoadResult> LoadAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces <paramref name="conversation" />; refused unless it
    /// belongs to the open chart.</summary>
    Task<AiChatWriteResult> SaveAsync(AiChatConversation conversation, CancellationToken cancellationToken = default);

    /// <summary>Renames a conversation; a blank title goes back to the first message's.</summary>
    Task<AiChatWriteResult> RenameAsync(string conversationId, string title,
        CancellationToken cancellationToken = default);

    Task<AiChatWriteResult> DeleteAsync(string conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
///     <see cref="IAiChatHistory" /> over files in the project folder
///     (<see cref="AiChatStore" />). File work runs on the thread pool, one call at
///     a time; the open chart is captured BEFORE it starts, so a chart switch
///     mid-call cannot redirect it.
///     <para>
///         <b>Not registered in DI yet</b>: nothing resolves it until the chat pane
/// is wired, and a registration nobody resolves is a dead feature
///         (<c>ServiceRegistrationReachabilityTests</c>). The pane registers it
///         with <see cref="ProjectStateAiChatChartSource" />.
///     </para>
/// </summary>
public sealed class AiChatHistory : IAiChatHistory
{
    private readonly IAiChatChartSource _chart;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAppLogger? _logger;
    private readonly TimeProvider _time;

    public AiChatHistory(IAiChatChartSource chart, TimeProvider? time = null, IAppLogger? logger = null)
    {
        _chart = chart ?? throw new ArgumentNullException(nameof(chart));
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    public string? OpenChartId => _chart.Current?.ChartId;

    public AiChatConversation? StartNew(string modelId)
    {
        var chartId = OpenChartId;
        return chartId is null ? null : AiChatConversation.Start(chartId, modelId, _time.GetUtcNow());
    }

    public Task<AiChatListing> ListAsync(CancellationToken cancellationToken = default)
    {
        var chart = _chart.Current;
        var opened = AiChatStore.TryOpen(chart, out var store);
        if (opened != AiChatStatus.Ok) return Task.FromResult(AiChatListing.Refusal(opened, chart?.ChartId));
        return RunAsync(() =>
        {
            var listing = store!.List();
            foreach (var refusal in listing.Refused)
            {
                _logger?.Warning(AppLogCategory.Project,
                    $"AI chat {refusal.ConversationId} in '{store.FolderPath}' was not listed: {refusal.Reason}.");
            }

            return listing;
        }, cancellationToken);
    }

    public Task<AiChatLoadResult> LoadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var opened = AiChatStore.TryOpen(_chart.Current, out var store);
        if (opened != AiChatStatus.Ok) return Task.FromResult(AiChatLoadResult.Refusal(opened));
        return RunAsync(() =>
        {
            var status = store!.Read(conversationId, out var conversation, out var detail);
            if (status != AiChatStatus.Ok) Refused("load", conversationId, status, detail);
            return new AiChatLoadResult(status, conversation, detail);
        }, cancellationToken);
    }

    public Task<AiChatWriteResult> SaveAsync(AiChatConversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var opened = AiChatStore.TryOpen(_chart.Current, out var store);
        if (opened != AiChatStatus.Ok) return Task.FromResult(new AiChatWriteResult(opened));
        return RunAsync(() => Logged("save", conversation.Id, store!.Write(conversation)), cancellationToken);
    }

    public Task<AiChatWriteResult> RenameAsync(string conversationId, string title,
        CancellationToken cancellationToken = default)
    {
        var opened = AiChatStore.TryOpen(_chart.Current, out var store);
        if (opened != AiChatStatus.Ok) return Task.FromResult(new AiChatWriteResult(opened));
        return RunAsync(() =>
        {
            var status = store!.Read(conversationId, out var conversation, out var detail);
            if (status != AiChatStatus.Ok) return Logged("rename", conversationId, new AiChatWriteResult(status, detail));

            var renamed = AiChatTitle.Normalize(title);
            if (renamed.Length == 0)
            {
                var first = conversation!.Turns.FirstOrDefault(t => t.Role == AiChatRole.User);
                renamed = AiChatTitle.FromFirstMessage(first?.Text);
            }

            return Logged("rename", conversationId, store.Write(conversation! with { Title = renamed }));
        }, cancellationToken);
    }

    public Task<AiChatWriteResult> DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var opened = AiChatStore.TryOpen(_chart.Current, out var store);
        if (opened != AiChatStatus.Ok) return Task.FromResult(new AiChatWriteResult(opened));
        return RunAsync(() =>
        {
            var result = store!.Delete(conversationId);
            // Logged, so a later "NotFound" for the same id reads as a second delete, not a lost file.
            if (result.Status == AiChatStatus.Ok)
            {
                _logger?.Info(AppLogCategory.Project, $"AI chat {conversationId} deleted from '{store.FolderPath}'.");
            }

            return Logged("delete", conversationId, result);
        }, cancellationToken);
    }

    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(work, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AiChatWriteResult Logged(string verb, string conversationId, AiChatWriteResult result)
    {
        if (result.Status != AiChatStatus.Ok) Refused(verb, conversationId, result.Status, result.Detail);
        return result;
    }

    private void Refused(string verb, string conversationId, AiChatStatus status, string? detail) =>
        _logger?.Warning(AppLogCategory.Project,
            $"AI chat {verb} refused for conversation '{conversationId}': {status}{(detail is null ? "" : $" ({detail})")}.");
}
