using System.Collections.ObjectModel;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.Infrastructure;

/// <summary>In-memory <see cref="IStatusBarService" /> that records every task
/// begin/complete in order, so a test can assert a pill bracketed some work —
/// and was completed on the failure path too.</summary>
public sealed class RecordingStatusBarService : IStatusBarService
{
    /// <summary>"begin:&lt;id&gt;:&lt;label&gt;" and "complete:&lt;id&gt;", in call order.</summary>
    public List<string> Log { get; } = [];

    public ObservableCollection<StatusBarMessage> RecentMessages { get; } = [];
    public ObservableCollection<StatusBarTask> ActiveTasks { get; } = [];

    /// <summary>Every progress report, in call order.</summary>
    public List<(string Id, double Progress, string? Label)> Progress { get; } = [];

    /// <summary>The task the last <see cref="BeginTask" /> handed out — its
    /// <see cref="StatusBarTask.CancelAction" /> is the pill's cancel button.</summary>
    public StatusBarTask? LastTask { get; private set; }

    public StatusBarTask BeginTask(string id, string label, bool indeterminate = true, Action? cancel = null)
    {
        Log.Add($"begin:{id}:{label}");
        return LastTask = new StatusBarTask(id, label, indeterminate) { CancelAction = cancel };
    }

    public void ReportProgress(string id, double progress, string? newLabel = null)
    {
        lock (Progress) Progress.Add((id, progress, newLabel));
    }

    public void CompleteTask(string id) => Log.Add($"complete:{id}");

    public void ClearMessages() { }
}
