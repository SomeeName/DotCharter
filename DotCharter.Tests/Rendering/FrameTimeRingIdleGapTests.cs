using DotCharter.Rendering.Diagnostics;
using DotCharter.Services.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Session Health regressions (owner report 2026-07-18): (1) the 2D
///     timeline's damage-driven renderer recorded idle pauses as frame
///     intervals — a 124-second "max frame" poisoned the panel; (2) the
///     Recent Log tail read the configured base name while the rolling sink
///     writes numbered siblings, so the panel always said "empty".
/// </summary>
public sealed class FrameTimeRingIdleGapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AddFrameInterval_DropsIdleGaps_KeepsRealStalls()
    {
        var ring = new FrameTimeRing();

        Assert.True(ring.AddFrameInterval(6.9));            // normal frame
        Assert.True(ring.AddFrameInterval(180.0));          // real stall — keep
        Assert.False(ring.AddFrameInterval(123_912.5));     // idle gap — drop
        Assert.False(ring.AddFrameInterval(FrameTimeRing.IdleGapThresholdMs)); // boundary is idle

        var stats = ring.Summarize();
        Assert.Equal(2, stats.Count);
        Assert.True(stats.MaxMs <= 180.0 + 0.001,
            $"idle gap leaked into max: {stats.MaxMs} ms");
    }

    [Fact]
    public void ResolveActiveLogFile_PicksNewestRolledSibling()
    {
        Directory.CreateDirectory(_dir);
        var configured = Path.Combine(_dir, "dotcharter.log"); // never written — the rolling sink's base name
        var older = Path.Combine(_dir, "dotcharter_160.log");
        var newest = Path.Combine(_dir, "dotcharter_161.log");
        File.WriteAllText(older, "old");
        File.WriteAllText(newest, "new");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow);

        Assert.Equal(newest, LogTailReader.ResolveActiveLogFile(configured));
    }

    [Fact]
    public void ResolveActiveLogFile_FallsBackToConfigured_WhenItExists()
    {
        Directory.CreateDirectory(_dir);
        var configured = Path.Combine(_dir, "dotcharter.log");
        File.WriteAllText(configured, "direct");

        Assert.Equal(configured, LogTailReader.ResolveActiveLogFile(configured));
    }

    [Fact]
    public void ResolveActiveLogFile_NullWhenNothingExists()
    {
        Assert.Null(LogTailReader.ResolveActiveLogFile(
            Path.Combine(_dir, "missing", "dotcharter.log")));
        Assert.Null(LogTailReader.ResolveActiveLogFile(null));
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
