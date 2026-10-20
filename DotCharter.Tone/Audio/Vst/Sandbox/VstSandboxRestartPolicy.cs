namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>
/// Pure restart-backoff state machine for the VST sandbox process. All
/// decisions are driven by caller-supplied monotonic timestamps (e.g. a
/// <see cref="System.Diagnostics.Stopwatch"/> elapsed) so the policy is
/// deterministic and unit-testable.
///
/// Semantics: every crash inside the sliding window escalates the restart
/// delay one step; once the crash count inside the window exceeds the
/// budget the policy gives up (returns <c>null</c>) until a sufficiently
/// long healthy stretch — or an explicit <see cref="Reset"/> — clears the
/// history.
/// </summary>
public sealed class VstSandboxRestartPolicy
{
    private static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
    ];

    private readonly List<TimeSpan> _crashes = [];
    private readonly TimeSpan[] _delays;

    public VstSandboxRestartPolicy(
        int maxCrashesPerWindow = 5,
        TimeSpan? window = null,
        TimeSpan? healthyResetAfter = null,
        TimeSpan[]? delays = null)
    {
        MaxCrashesPerWindow = Math.Max(1, maxCrashesPerWindow);
        Window = window ?? TimeSpan.FromMinutes(2);
        HealthyResetAfter = healthyResetAfter ?? TimeSpan.FromSeconds(30);
        _delays = delays is { Length: > 0 } ? delays : DefaultDelays;
    }

    public int MaxCrashesPerWindow { get; }
    public TimeSpan Window { get; }
    public TimeSpan HealthyResetAfter { get; }

    /// <summary>True once the crash budget was exceeded and no healthy
    /// stretch / reset has happened since.</summary>
    public bool GaveUp { get; private set; }

    /// <summary>Crashes still inside the sliding window as of <paramref name="now"/>.</summary>
    public int RecentCrashCount(TimeSpan now)
    {
        Prune(now);
        return _crashes.Count;
    }

    /// <summary>
    /// Record a crash at <paramref name="now"/>. Returns the delay to wait
    /// before the next restart attempt, or <c>null</c> when the budget is
    /// exhausted (caller should stop restarting and tell the user).
    /// </summary>
    public TimeSpan? OnCrash(TimeSpan now)
    {
        if (GaveUp) return null;
        Prune(now);
        _crashes.Add(now);
        if (_crashes.Count > MaxCrashesPerWindow)
        {
            GaveUp = true;
            return null;
        }
        return _delays[Math.Min(_crashes.Count - 1, _delays.Length - 1)];
    }

    /// <summary>
    /// Report that the sandbox has been healthy up to <paramref name="now"/>.
    /// A stretch of at least <see cref="HealthyResetAfter"/> since the last
    /// crash clears the history (and un-gives-up).
    /// </summary>
    public void OnHealthy(TimeSpan now)
    {
        if (_crashes.Count == 0)
        {
            GaveUp = false;
            return;
        }
        if (now - _crashes[^1] >= HealthyResetAfter)
        {
            _crashes.Clear();
            GaveUp = false;
        }
    }

    /// <summary>Explicit reset (e.g. the user toggled the feature or applied
    /// new audio settings).</summary>
    public void Reset()
    {
        _crashes.Clear();
        GaveUp = false;
    }

    private void Prune(TimeSpan now)
        => _crashes.RemoveAll(t => now - t > Window);
}
