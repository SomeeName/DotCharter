using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DotCharter.Tone.Diagnostics;

/// <summary>
/// Records a warning or error surfaced from <see cref="IAppLogger"/> so the
/// status bar can show it without having to crack open the log file.
/// </summary>
public sealed class StatusBarMessage
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Level { get; init; }
    public required AppLogCategory Category { get; init; }
    public required string Message { get; init; }
    public string? ExceptionText { get; init; }

    public bool IsError => Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase);
    public bool IsWarning => Level.Equals("WARN", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Long-running operation indicator (audio engine restart, stem separation,
/// VST load, PSARC import, etc.) for the bottom status bar.
/// </summary>
public sealed class StatusBarTask : INotifyPropertyChanged
{
    public string Id { get; }
    public DateTimeOffset StartedAt { get; }

    public StatusBarTask(string id, string label, bool indeterminate)
    {
        Id = id;
        StartedAt = DateTimeOffset.Now;
        _label = label;
        _indeterminate = indeterminate;
    }

    private string _label;
    public string Label
    {
        get => _label;
        set { if (_label != value) { _label = value; OnPropertyChanged(); } }
    }

    private double _progress;
    /// <summary>0..1. Ignored when <see cref="IsIndeterminate"/> is true.</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            var clamped = double.IsNaN(value) ? 0 : Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_progress - clamped) > 1e-4)
            {
                _progress = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercent));
            }
        }
    }

    public int ProgressPercent => (int)Math.Round(Progress * 100.0);

    private bool _indeterminate;
    public bool IsIndeterminate
    {
        get => _indeterminate;
        set { if (_indeterminate != value) { _indeterminate = value; OnPropertyChanged(); } }
    }

    private Action? _cancelAction;
    /// <summary>
    /// Optional cancel hook supplied by the operation's owner (e.g. a CTS
    /// Cancel). When set, the status bar shows a cancel affordance on the
    /// task pill.
    /// </summary>
    public Action? CancelAction
    {
        get => _cancelAction;
        set
        {
            if (_cancelAction != value)
            {
                _cancelAction = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public bool CanCancel => _cancelAction != null;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Aggregates progress, warnings and errors for an IntelliJ-style bottom
/// status bar. Thread-safe at the producer side; consumer (UI) must use the
/// dispatcher to observe collection changes.
/// </summary>
public interface IStatusBarService
{
    /// <summary>Recent warnings + errors, capped (newest first).</summary>
    ObservableCollection<StatusBarMessage> RecentMessages { get; }

    /// <summary>Active long-running tasks (newest first).</summary>
    ObservableCollection<StatusBarTask> ActiveTasks { get; }

    /// <summary>
    /// Begins (or updates) a long-running task indicator. A non-null
    /// <paramref name="cancel"/> gives the task pill a cancel affordance.
    /// </summary>
    StatusBarTask BeginTask(string id, string label, bool indeterminate = true, Action? cancel = null);

    /// <summary>Updates progress for an existing task. No-op if not found.</summary>
    void ReportProgress(string id, double progress, string? newLabel = null);

    /// <summary>Marks a task as complete and removes it from the active list.</summary>
    void CompleteTask(string id);

    /// <summary>Clears all stored warnings + errors.</summary>
    void ClearMessages();
}

/// <summary>
/// Thin abstraction over the UI dispatcher so non-UI projects can post
/// callbacks without referencing Avalonia directly. Implementation lives
/// in DotCharter.Tone.UI.
/// </summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    /// <summary>
    /// Fire-and-forget post at background priority — for non-urgent UI
    /// refreshes (status text, counters) that must not compete with input.
    /// </summary>
    void Post(Action action);

    /// <summary>
    /// Fire-and-forget post at normal dispatcher priority — matches the
    /// default of Avalonia's <c>Dispatcher.UIThread.Post</c>.
    /// </summary>
    void PostNormal(Action action);

    /// <summary>Runs the action on the UI thread and completes when it ran.</summary>
    Task InvokeAsync(Action action);

    /// <summary>Runs the function on the UI thread and returns its result.</summary>
    Task<T> InvokeAsync<T>(Func<T> func);

    /// <summary>Runs an async function on the UI thread and unwraps its result.</summary>
    Task<T> InvokeAsync<T>(Func<Task<T>> func);

    /// <summary>
    ///     A periodic tick ON the UI thread — for the 1 Hz-class refreshes a
    ///     view model polls with. Dispose to stop.
    ///     <para>
    ///         The default implementation is a thread-pool timer that
    ///         <see cref="Post" />s each tick, which is what every caller would otherwise
    ///         hand-roll; the Avalonia implementation overrides it with a
    ///         <c>DispatcherTimer</c>, so the tick never leaves the UI thread and
    ///         nothing posts from a pool thread after the dispatcher it was
    ///         posting to is gone. That last point is why this exists: under
    ///         <c>Avalonia.Headless</c> every booted app leaked one such pool
    ///         timer for the rest of the run, and a post landing between one
    ///         test's dispatcher reset and the next test's platform init is
    ///         the shape of the once-per-run <c>ServerCompositor..ctor</c> /
    ///         <c>VerifyAccess</c> failure.
    ///     </para>
    /// </summary>
    IDisposable SchedulePeriodic(TimeSpan period, Action tick)
    {
        var timer = new System.Threading.Timer(_ => Post(tick), null, period, period);
        return timer;
    }
}

/// <summary>
/// Default implementation. Listens to <see cref="IAppLogger.LogEmitted"/>
/// to populate <see cref="RecentMessages"/>. Mutations go through the
/// supplied <see cref="IUiDispatcher"/> so observable collection events
/// reach the UI thread without the consumer having to marshal manually.
/// </summary>
public sealed class StatusBarService : IStatusBarService, IDisposable
{
    private const int MaxMessages = 200;

    private readonly IAppLogger? _logger;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;

    // Producers (audio sandbox restart, engine replace, VST chain load) call
    // BeginTask/ReportProgress from thread-pool threads, but ActiveTasks is an
    // ObservableCollection mutated on the UI thread for binding — and
    // ToArray() on one is Count-then-CopyTo, NOT a snapshot, so it tears when
    // the UI thread inserts/removes underneath. Lookups therefore go through
    // this dictionary, which is maintained in the same Post bodies (and is the
    // authority for task identity).
    private readonly ConcurrentDictionary<string, StatusBarTask> _tasksById = new(StringComparer.Ordinal);

    public ObservableCollection<StatusBarMessage> RecentMessages { get; } = [];
    public ObservableCollection<StatusBarTask> ActiveTasks { get; } = [];

    public StatusBarService(IUiDispatcher dispatcher, IAppLogger? logger = null)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _logger?.LogEmitted += OnLogEmitted;
    }

    private void OnLogEmitted(LogEmittedArgs ev)
    {
        if (!ev.Level.Equals("WARN", StringComparison.OrdinalIgnoreCase)
            && !ev.Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var msg = new StatusBarMessage
        {
            Timestamp = ev.Timestamp,
            Level = ev.Level,
            Category = ev.Category,
            Message = ev.Message,
            ExceptionText = ev.ExceptionText
        };

        Post(() =>
        {
            RecentMessages.Insert(0, msg);
            while (RecentMessages.Count > MaxMessages)
                RecentMessages.RemoveAt(RecentMessages.Count - 1);
        });
    }

    public StatusBarTask BeginTask(string id, string label, bool indeterminate, Action? cancel = null)
    {
        while (true)
        {
            if (_tasksById.TryGetValue(id, out var existing))
            {
                Post(() =>
                {
                    existing.Label = label;
                    existing.IsIndeterminate = indeterminate;
                    existing.CancelAction = cancel;
                });
                return existing;
            }

            var fresh = new StatusBarTask(id, label, indeterminate) { CancelAction = cancel };

            // Claiming the id in the dictionary is what makes "begin the same task
            // twice" idempotent — two producers racing here must not both Insert.
            if (!_tasksById.TryAdd(id, fresh)) continue;

            Post(() => ActiveTasks.Insert(0, fresh));
            return fresh;
        }
    }

    public void ReportProgress(string id, double progress, string? newLabel = null)
    {
        if (!_tasksById.TryGetValue(id, out var task)) return;
        Post(() =>
        {
            task.IsIndeterminate = false;
            task.Progress = progress;
            if (!string.IsNullOrEmpty(newLabel)) task.Label = newLabel;
        });
    }

    public void CompleteTask(string id)
    {
        // Drop the id first so a BeginTask with the same id right after this is a
        // fresh task; the posted removal below still runs before that task's Insert.
        _tasksById.TryRemove(id, out _);

        Post(() =>
        {
            for (var i = ActiveTasks.Count - 1; i >= 0; i--)
            {
                if (string.Equals(ActiveTasks[i].Id, id, StringComparison.Ordinal))
                    ActiveTasks.RemoveAt(i);
            }
        });
    }

    public void ClearMessages() => Post(() => RecentMessages.Clear());

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.Post(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _logger?.LogEmitted -= OnLogEmitted;
    }
}
