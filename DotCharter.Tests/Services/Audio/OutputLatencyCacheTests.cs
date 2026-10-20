using System.Diagnostics;
using System.Text.RegularExpressions;
using DotCharter.Tone.Audio.Engine;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     Static perf audit 2026-08-29 P0 #8 and #9 — the two per-tick costs on
///     the 250 Hz position clock.
///     <para>
///         #8: <c>EstimatedOutputLatencyMs</c> made two P/Invoke round trips per
///         read, on every position tick and on every renderer frame, for two
///         device-static numbers. <see cref="OutputLatencyCache" /> is the pure
///         half; the lints below pin that the service reads through it and that
///         an in-place <c>Reconfigure</c> invalidates it.
///     </para>
///     <para>
///         #9: the clock was a <c>System.Timers.Timer</c>, which constructs an
///         <c>ElapsedEventArgs</c> stamped with <c>DateTime.Now</c> per raise;
///         it is a <c>System.Threading.Timer</c> now, which passes a state
///         object and allocates nothing per tick.
///     </para>
/// </summary>
public sealed class OutputLatencyCacheTests
{
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void ASecondReadForTheSameEngine_IsServedFromTheCache()
    {
        var cache = new OutputLatencyCache();
        var engine = new object();

        Assert.False(cache.TryGet(engine, Ticks(0), out _));
        cache.Store(engine, Ticks(0), 12.5);

        Assert.True(cache.TryGet(engine, Ticks(0.5), out var ms));
        Assert.Equal(12.5, ms);
    }

    [Fact]
    public void ARebuiltEngine_MissesOnItsReference()
    {
        var cache = new OutputLatencyCache();
        var first = new object();
        var rebuilt = new object();
        cache.Store(first, Ticks(0), 12.5);

        Assert.False(cache.TryGet(rebuilt, Ticks(0.1), out _));
    }

    [Fact]
    public void TheEntryExpires_AfterTheRefreshInterval_SoADriverSideChangeIsPickedUp()
    {
        var cache = new OutputLatencyCache();
        var engine = new object();
        cache.Store(engine, Ticks(0), 12.5);

        Assert.True(cache.TryGet(engine, Ticks(0.99), out _));
        Assert.False(cache.TryGet(engine, Ticks(1.0), out _));
    }

    [Fact]
    public void Invalidate_ForcesTheNextReadToTheEngine()
    {
        var cache = new OutputLatencyCache();
        var engine = new object();
        cache.Store(engine, Ticks(0), 12.5);
        cache.Invalidate();

        Assert.False(cache.TryGet(engine, Ticks(0.1), out _));
    }

    [Fact]
    public void TheHit_AllocatesNothing()
    {
        var cache = new OutputLatencyCache();
        var engine = new object();
        cache.Store(engine, Ticks(0), 12.5);
        var now = Ticks(0.1);

        DotCharter.Tests.Infrastructure.AllocationProbe.AssertAllocationFree("OutputLatencyCache.TryGet hit",
            () => cache.TryGet(engine, now, out _));
    }

    // ── the service reads through it ─────────────────────────────────────────

    [Fact]
    public void EstimatedOutputLatencyMs_ReadsThroughTheCache_AndReconfigureInvalidatesIt()
    {
        var playhead = Source("JuceAudioPlaybackService.Playhead.cs");
        var getter = Body(playhead, "public double EstimatedOutputLatencyMs");
        Assert.Contains("_latencyCache.TryGet(eng, now, out var cached)", getter);
        Assert.Contains("_latencyCache.Store(eng, now, ms)", getter);

        var config = Source("JuceAudioPlaybackService.EngineConfig.cs");
        var reconfigure = config.IndexOf("engine.Reconfigure(sr, buf, outDev, inDev);", StringComparison.Ordinal);
        Assert.True(reconfigure >= 0, "the in-place Reconfigure call has moved — repoint this lint");
        var after = config.Substring(reconfigure, 400);
        Assert.Contains("_latencyCache.Invalidate();", after);
    }

    [Fact]
    public void ThePositionClock_IsAThreadingTimer_NotATimersTimer()
    {
        var root = Source("JuceAudioPlaybackService.cs");
        Assert.Contains("private readonly System.Threading.Timer _positionTimer;", root);
        Assert.Contains("new System.Threading.Timer(static state => ((JuceAudioPlaybackService)state!).PositionTick()", root);
        Assert.DoesNotContain("_positionTimer.Elapsed", root);

        // Start/stop go through Change(), from the transport verbs.
        var transport = Source("JuceAudioPlaybackService.Transport.cs");
        Assert.Single(Regex.Matches(transport, @"StartPositionTimer\(\);"));
        Assert.Equal(2, Regex.Matches(transport, @"StopPositionTimer\(\);").Count);
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' has moved or been renamed — repoint this lint.");
        var open = source.IndexOf('{', start + signature.Length);
        var depth = 0;
        var i = open;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) break;
        }

        return source[open..Math.Min(i + 1, source.Length)];
    }

    private static string Source(string file) =>
        File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone", "Audio", file));
}
