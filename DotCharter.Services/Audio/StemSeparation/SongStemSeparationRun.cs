namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
/// The separation in flight, as any screen can follow it: the audio it
///     separates, its latest progress, how it ends, and a cancel. A screen that did
///     not start the run — Chart from audio reopened while its own run goes on —
///     attaches to this instead of starting a second one. The run belongs to the app,
///     not to the screen that started it: closing that screen neither cancels nor
///     abandons it, and the stems still land in the never-regenerated cache.
/// </summary>
public sealed class SongStemSeparationRun
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts;
    private readonly TaskCompletionSource<SongStemSeparationOutcome> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IProgress<double>[] _watchers = [];
    private double _progress;

    /// <param name="audioPath">The audio being separated, as the run resolved it.</param>
    /// <param name="callerToken">The starting caller's own cancel; it cancels this run too.</param>
    public SongStemSeparationRun(string audioPath, CancellationToken callerToken)
    {
        AudioPath = audioPath;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
    }

    public string AudioPath { get; }

    /// <summary>The run's own token: the caller's cancel, the status bar's and <see cref="Cancel" />.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>The latest 0–1 progress reported, so a screen that attaches late starts where the run is.</summary>
    public double Progress => Volatile.Read(ref _progress);

    /// <summary>How the run ended. Never faults: a run that threw completes as failed.</summary>
    public Task<SongStemSeparationOutcome> Completion => _done.Task;

    /// <summary>True when this run separates <paramref name="audioPath" /> (two spellings of one file count).</summary>
    public bool Separates(string audioPath) => IsSameFile(AudioPath, audioPath);

    /// <summary>
    ///     Sends every later progress report to <paramref name="watcher" />, on the
    ///     reporting thread, until the returned handle is disposed.
    /// </summary>
    public IDisposable Watch(IProgress<double> watcher)
    {
        lock (_gate)
        {
            _watchers = [.. _watchers, watcher];
        }

        return new Unwatch(this, watcher);
    }

    /// <summary>The backend's progress: kept for late watchers and handed to the current ones.</summary>
    public void Report(double value)
    {
        Volatile.Write(ref _progress, value);
        IProgress<double>[] watchers;
        lock (_gate)
        {
            watchers = _watchers;
        }

        foreach (var watcher in watchers) watcher.Report(value);
    }

    /// <summary>
    ///     Cancels the run; a no-op once it has ended. The source is never disposed, so a
    ///     late cancel cannot throw, and its callbacks run outside any lock of this type.
    /// </summary>
    public void Cancel()
    {
        if (!_done.Task.IsCompleted) _cts.Cancel();
    }

    /// <summary>Ends the run with <paramref name="outcome" /> and drops its watchers; the first call wins.</summary>
    public void Complete(SongStemSeparationOutcome outcome)
    {
        lock (_gate)
        {
            _watchers = [];
        }

        _done.TrySetResult(outcome);
    }

    /// <summary>Two spellings of one file: full paths, compared the way Windows compares them.</summary>
    public static bool IsSameFile(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed class Unwatch(SongStemSeparationRun run, IProgress<double> watcher) : IDisposable
    {
        public void Dispose()
        {
            lock (run._gate)
            {
                run._watchers = [.. run._watchers.Where(w => !ReferenceEquals(w, watcher))];
            }
        }
    }
}
