using System.Diagnostics;
using System.Reflection;
using DotCharter.Tone.Audio;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// H1: an engine could be built AFTER <c>Dispose()</c>.
/// <para>
/// <c>Dispose</c> stopped and disposed the watchdog timer without waiting for a
/// tick already inside <c>WatchdogTick</c>, and <c>ReplaceEngineGated</c> —
/// the end of that tick's <c>AttemptDeviceRecovery -> ApplyAudioConfiguration</c>
/// walk — contained no <c>IsDisposing</c> check anywhere. It therefore published
/// a brand-new engine into <c>_engine</c> after <c>DisposeCore</c> destroyed the
/// old one and nulled the field. Nothing ever disposes that engine, so the
/// process exits still holding the (exclusive, on ASIO) sound card: the
/// "one survived 32 hours" mode in AGENTS.md. A settings apply racing window
/// close had the same window.
/// </para>
/// <para>
/// These are source lints plus one behavioural bail check. They deliberately
/// never let a code path reach <c>CreateEngineWithFallback</c>: a test that
/// opens a real device steals the machine's sound card.
/// </para>
/// </summary>
public sealed class EngineBuildAfterDisposeTests
{
    /// <summary>
    /// The cheap guard: an apply arriving after dispose has started returns
    /// immediately and opens nothing.
    /// </summary>
    [Fact]
    public void AnApplyThatArrivesAfterDisposeHasStartedOpensNoDevice()
    {
        var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        try
        {
            // Simulate "Dispose() has entered" without running DisposeCore, so
            // the assertion is about the guard and not about teardown order.
            typeof(JuceAudioPlaybackService)
                .GetField("_disposingFlag", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(svc, 1);

            var sw = Stopwatch.StartNew();
            svc.ApplyAudioConfiguration(new AppConfig());
            sw.Stop();

            Assert.Null(Field(svc, "_engine"));
            // …and it did not re-arm the watchdog on the way out, which would
            // hand a disposed service a recovery target.
            Assert.Null(Field(svc, "_watchdogConfig"));
            Assert.True(sw.ElapsedMilliseconds < 60_000,
                $"The disposing bail must be immediate; took {sw.ElapsedMilliseconds} ms.");
        }
        finally
        {
            svc.Dispose();
        }
    }

    /// <summary>
    /// <c>ReplaceEngineGated</c> bails on entry AND re-checks between the
    /// create and the publish — the teardown in between takes hundreds of ms,
    /// which is exactly the window Dispose lands in.
    /// </summary>
    [Fact]
    public void ReplaceEngineGatedRefusesToPublishAnEngineOnceDisposeHasStarted()
    {
        var body = MethodBody("DotCharter.Tone/Audio/JuceAudioPlaybackService.EngineLifecycle.cs",
            "private void ReplaceEngineGated(JuceEngineConfig cfg)");

        var entry = body.IndexOf("if (IsDisposing) return;", StringComparison.Ordinal);
        Assert.True(entry >= 0, "ReplaceEngineGated must stand down when dispose has started.");

        var create = body.IndexOf("CreateEngineWithFallback", StringComparison.Ordinal);
        var publish = body.IndexOf("_engine = engine;", StringComparison.Ordinal);
        Assert.True(create >= 0 && publish > create, "Expected the create-then-publish shape.");

        var recheck = body.IndexOf("if (IsDisposing)", create, StringComparison.Ordinal);
        Assert.True(recheck >= 0 && recheck < publish,
            "A fresh engine must not be published into _engine without re-checking IsDisposing "
            + "between the create and the publish — otherwise the process exits owning the device.");
    }

    /// <summary>
    /// <c>Dispose</c> waits for an in-flight watchdog tick. <c>Timer.Stop()</c>
    /// only stops FUTURE ticks.
    /// </summary>
    [Fact]
    public void DisposeWaitsForAnInFlightWatchdogTick()
    {
        var body = MethodBody("DotCharter.Tone/Audio/JuceAudioPlaybackService.cs",
            "public void Dispose()");

        var stop = body.IndexOf("_watchdogTimer.Stop();", StringComparison.Ordinal);
        var wait = body.IndexOf("WaitForWatchdogIdle(", StringComparison.Ordinal);
        Assert.True(stop >= 0, "Dispose must stop the watchdog timer.");
        Assert.True(wait > stop, "Dispose must wait for the in-flight tick AFTER stopping the timer.");

        var core = body.IndexOf("DisposeCore();", StringComparison.Ordinal);
        Assert.True(core > wait, "The wait must happen before DisposeCore destroys the engine.");
    }

    /// <summary>The wait is bounded — a wedged native probe must not hang exit.</summary>
    [Fact]
    public void TheWatchdogDrainIsBounded()
    {
        var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        try
        {
            typeof(JuceAudioPlaybackService)
                .GetField("_watchdogBusy", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(svc, 1); // pretend a tick is stuck inside the handler

            var sw = Stopwatch.StartNew();
            var idle = svc.WaitForWatchdogIdle(TimeSpan.FromMilliseconds(120));
            sw.Stop();

            Assert.False(idle, "A stuck tick must be reported, not waited on forever.");
            Assert.True(sw.ElapsedMilliseconds < 60_000,
                $"The drain must give up on its budget; took {sw.ElapsedMilliseconds} ms.");
        }
        finally
        {
            typeof(JuceAudioPlaybackService)
                .GetField("_watchdogBusy", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(svc, 0);
            svc.Dispose();
        }
    }

    /// <summary>An idle watchdog drains instantly.</summary>
    [Fact]
    public void AnIdleWatchdogDrainsImmediately()
    {
        var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        try { Assert.True(svc.WaitForWatchdogIdle(TimeSpan.FromSeconds(1))); }
        finally { svc.Dispose(); }
    }

    // ----- helpers -----------------------------------------------------------

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    private static string MethodBody(string relativeFile, string signature)
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            relativeFile.Replace('/', Path.DirectorySeparatorChar)));
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{signature}' in {relativeFile}.");
        var open = src.IndexOf('{', start + signature.Length);
        var depth = 0;
        var i = open;
        for (; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) break;
        }
        return src[open..Math.Min(i + 1, src.Length)];
    }
}
