namespace DotCharter.Tests.Infrastructure;

/// <summary>A child's silence, rather than its total runtime, decides whether it is hung (#664).</summary>
internal sealed class ProcessIdleGuard(TimeSpan idleTimeout, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _lastActivity = (clock ?? TimeProvider.System).GetTimestamp();

    public static TimeSpan PublicExportTimeout => TimeSpan.FromMinutes(15);

    public void ReportActivity() => Interlocked.Exchange(ref _lastActivity, _clock.GetTimestamp());

    public bool Expired => _clock.GetElapsedTime(Interlocked.Read(ref _lastActivity)) >= idleTimeout;
}
