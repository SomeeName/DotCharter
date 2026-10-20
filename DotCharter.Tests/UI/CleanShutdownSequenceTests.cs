using DotCharter.UI;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.UI;

/// <summary>
///     Regression tests for UI audit 2026-07-25 finding 3.1: the DI container
///     was never disposed. <c>App._serviceProvider</c> was assigned in
///     <c>OnFrameworkInitializationCompleted</c> and read nowhere, so on the
///     CLEAN shutdown path (out-of-process VST — the one path where managed
///     teardown is safe, and the stated exit criterion for deleting the
///     <c>TerminateNow</c> hack) the process left through the normal CRT path
///     with only <c>IAudioPlaybackService</c> disposed. The SQLite psarc index,
///     the LAN sockets, the stem separators' GPU handles and a
///     fully-implemented <c>StatusBarViewModel.Dispose()</c> never ran.
///
///     <c>ShutdownServices</c> itself is only reachable from a real app exit
///     under <c>AppConfig.OutOfProcessVst</c>, which no headless test can drive
///     — so the teardown SEQUENCE is extracted into
///     <c>App.RunCleanShutdown</c> and pinned here instead. These would catch
///     the container dispose being dropped again, and the ordering constraint
///     that makes it correct.
/// </summary>
public sealed class CleanShutdownSequenceTests
{
    private sealed class RecordingDisposable(List<string> log, string name, bool throws = false)
        : IDisposable
    {
        public void Dispose()
        {
            log.Add(name);
            if (throws) throw new InvalidOperationException(name + " blew up");
        }
    }

    private static Action Flush(List<string> log, bool throws = false) => () =>
    {
        log.Add("flush");
        if (throws) throw new InvalidOperationException("flush blew up");
    };

    private static Action FlushConfig(List<string> log, bool throws = false) => () =>
    {
        log.Add("config");
        if (throws) throw new InvalidOperationException("config flush blew up");
    };

    /// <summary>The whole point of the fix: the container IS disposed.</summary>
    [Fact]
    public void CleanShutdown_DisposesTheContainer()
    {
        var log = new List<string>();

        App.RunCleanShutdown(null, null, null, new RecordingDisposable(log, "container"));

        Assert.Contains("container", log);
    }

    /// <summary>Config flush first (config saves are COALESCED — a
    /// SaveAppConfig only arms a debounced background write, so what the user
    /// changed in the last quarter second is still only in memory — and it runs
    /// ahead of the step most likely to be slow or to throw), audio second (its
    /// ordering against IDspChainService is load-bearing and must not depend on
    /// registration order), log flush third (so the log survives a service
    /// throwing on the way out), container last (AppLogger is one of its
    /// singletons and its Dispose drains the teardown's own log lines — and
    /// resolving anything from a disposed provider would throw
    /// ObjectDisposedException).</summary>
    [Fact]
    public void CleanShutdown_RunsConfigThenAudioThenFlushThenContainer()
    {
        var log = new List<string>();

        App.RunCleanShutdown(
            FlushConfig(log),
            new RecordingDisposable(log, "audio"),
            Flush(log),
            new RecordingDisposable(log, "container"));

        Assert.Equal(new[] { "config", "audio", "flush", "container" }, log);
    }

    /// <summary>The clean path is one of the two places a coalesced config write
    /// can still reach the disk (the other is <c>App.TerminateNow</c>, which
    /// TerminateProcess-es). Without this step every setting changed inside the
    /// debounce window is lost on a normal quit — and nothing else would notice,
    /// because the in-memory config was right the whole time.</summary>
    [Fact]
    public void CleanShutdown_FlushesTheCoalescedConfigWriter()
    {
        var log = new List<string>();

        App.RunCleanShutdown(FlushConfig(log), null, null, null);

        Assert.Contains("config", log);
    }

    /// <summary>One service throwing must not cost the others their teardown —
    /// an exception from the audio engine used to be the last thing that ran.</summary>
    [Fact]
    public void CleanShutdown_StillDisposesTheContainer_WhenEarlierStepsThrow()
    {
        var log = new List<string>();

        App.RunCleanShutdown(
            FlushConfig(log, throws: true),
            new RecordingDisposable(log, "audio", throws: true),
            Flush(log, throws: true),
            new RecordingDisposable(log, "container"));

        Assert.Equal(new[] { "config", "audio", "flush", "container" }, log);
    }

    /// <summary>And a throwing container must not escape into Avalonia's Exit
    /// handler, where it would surface as a crash on quit.</summary>
    [Fact]
    public void CleanShutdown_SwallowsAThrowingContainer()
    {
        var log = new List<string>();

        App.RunCleanShutdown(null, null, null, new RecordingDisposable(log, "container", throws: true));

        Assert.Contains("container", log);
    }

    [Fact]
    public void CleanShutdown_ToleratesEverythingBeingNull() => App.RunCleanShutdown(null, null, null, null);

    // ---- the truncated-chain class of fault ------------------------------------
    //
    // The tests above prove RunCleanShutdown REACHES the container. They said
    // nothing about what the container then does, and that is where the real
    // 2026-08-29 defect lived: the container's own disposal chain stopped part
    // way through and the guard above turned that into silence.

    private sealed class AsyncOnlyDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Two distinct types so the CREATION order (which is what the container
    // walks backwards, not the registration order) is set by this test, not by
    // how GetServices happens to enumerate.
    private sealed class CreatedFirst(List<string> log) : IDisposable
    {
        public void Dispose() => log.Add("created-first");
    }

    private sealed class CreatedLast(List<string> log) : IDisposable
    {
        public void Dispose() => log.Add("created-last");
    }

    /// <summary>
    ///     The framework behaviour the composition-root lint
    ///     (<c>AppServiceGraphTests.No_registered_type_is_async_disposable_only</c>)
    ///     exists for, pinned here so it is a stated contract rather than
    ///     folklore: <c>ServiceProvider.Dispose()</c> disposes singletons in
    ///     REVERSE creation order and THROWS when it reaches one that implements
    ///     <see cref="IAsyncDisposable" /> without <see cref="IDisposable" /> —
    ///     leaving every singleton created BEFORE it undisposed.
    ///     <c>MultiplayerViewModel</c> was that singleton, created early, so the
    ///     chain was cut near its start.
    /// </summary>
    [Fact]
    public void AnAsyncOnlyDisposableSingleton_TruncatesTheContainerDisposalChain()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        // Registered by TYPE, not as instances: the container only tracks what
        // it CONSTRUCTED, and an externally-created instance handed to
        // AddSingleton(instance) is left alone (measured — it does not throw).
        services.AddSingleton(log);
        services.AddSingleton<CreatedFirst>();
        services.AddSingleton<AsyncOnlyDisposable>();
        services.AddSingleton<CreatedLast>();

        var provider = services.BuildServiceProvider();
        // RESOLUTION is what puts an instance on the container's disposal list,
        // so these three lines — not the registration order — fix the order the
        // container will walk BACKWARDS.
        provider.GetRequiredService<CreatedFirst>();
        provider.GetRequiredService<AsyncOnlyDisposable>();
        provider.GetRequiredService<CreatedLast>();

        Assert.Throws<InvalidOperationException>(() => provider.Dispose());

        // The reverse walk reaches "created-last", then the async-only one and
        // throws. Everything created BEFORE the offender — here, one singleton;
        // in the real app, most of them — never gets disposed at all.
        Assert.Contains("created-last", log);
        Assert.DoesNotContain("created-first", log);
    }

    /// <summary>
    ///     And the shutdown must not make that invisible. Since the container
    ///     step is guarded, a throw from inside it is swallowed — so the ONLY
    ///     protection is that no registration has the shape above. This asserts
    ///     the negative directly on the real composition root's container:
    ///     resolve a singleton, dispose the provider, and require that the
    ///     dispose does not throw at all.
    /// </summary>
    [Fact]
    public void TheRealContainer_DisposesWithoutThrowing()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services, new JsonConfigurationService());
        var provider = services.BuildServiceProvider();

        // Put a disposable singleton on the disposal list exactly as a real
        // session would. (The original offender, the LAN multiplayer view
        // model, was removed with LAN multiplayer on 2026-09-19.)
        _ = provider.GetService<IConfigurationService>();

        var thrown = Record.Exception(() => provider.Dispose());

        Assert.True(thrown is null,
            $"ServiceProvider.Dispose() threw — the disposal chain is TRUNCATED at whatever it " +
            $"reached, and every singleton created earlier leaks: {thrown}");
    }
}
