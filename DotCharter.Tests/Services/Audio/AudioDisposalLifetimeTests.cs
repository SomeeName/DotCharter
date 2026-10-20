using System.Reflection;
using DotCharter.Tone.Audio;
using DotCharter.Services.Audio.LivePlay.Takes;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// D1–D5: the disposal/lifetime gaps in the audio layer. Each is small on its
/// own; together they are the difference between a teardown that is correct by
/// design and one that happens to survive.
/// </summary>
public sealed class AudioDisposalLifetimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "dc-audiodispose-" + Guid.NewGuid().ToString("N"));

    public AudioDisposalLifetimeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>
    /// D2. `Dispose()` disposes the recorder's CancellationTokenSource and
    /// `StopAsync()` called `Cancel()` on it unguarded, so Dispose-then-Stop
    /// threw ObjectDisposedException out of StopAsync. Latent only because
    /// LiveTakeStore.FinalizeTakeAsync happens to order them the other way and
    /// wraps everything in a catch.
    /// </summary>
    [Fact]
    public async Task ARecorderStoppedAfterDisposeDoesNotThrow()
    {
        var path = Path.Combine(_dir, "take.wav");
        var recorder = new LiveTakeRecorder(path, sampleRate: 48_000, maxDuration: TimeSpan.FromSeconds(10));
        recorder.Write([0.1f, -0.1f, 0.2f]);

        recorder.Dispose();

        var result = await recorder.StopAsync();
        Assert.Equal(path, result.FilePath);
    }

    /// <summary>The normal order still works and still writes the take.</summary>
    [Fact]
    public async Task ARecorderStoppedThenDisposedStillFinalizesTheTake()
    {
        var path = Path.Combine(_dir, "take2.wav");
        var recorder = new LiveTakeRecorder(path, sampleRate: 48_000, maxDuration: TimeSpan.FromSeconds(10));
        recorder.Write([0.1f, -0.1f, 0.2f, 0.3f]);

        var result = await recorder.StopAsync();
        recorder.Dispose();

        Assert.True(result.SamplesWritten > 0);
        Assert.True(new FileInfo(path).Length > 44);
    }

    /// <summary>
    /// D1. `_vstGate` (SemaphoreSlim) and `_healthStop` (AutoResetEvent) were
    /// never disposed — one kernel event handle and one semaphore stranded per
    /// service instance. Harmless for the DI singleton, real for any test that
    /// constructs many. Constructing and disposing a batch must not accumulate
    /// handles, and disposing twice must stay a no-op.
    /// </summary>
    [Fact]
    public void DisposingTheServiceReleasesItsSynchronizationPrimitives()
    {
        for (var i = 0; i < 8; i++)
        {
            var svc = new JuceAudioPlaybackService(FakeLocalization.English());
            svc.Dispose();
            svc.Dispose(); // idempotent

            var vstGate = (SemaphoreSlim)Field(svc, "_vstGate")!;
            Assert.Throws<ObjectDisposedException>(() => vstGate.Release());

            var healthStop = (AutoResetEvent)Field(Field(svc, "_health")!, "_healthStop")!;
            Assert.Throws<ObjectDisposedException>(() => healthStop.WaitOne(0));
        }
    }

    /// <summary>
    /// D3. `Timer.Dispose()` (without a wait handle) is not a barrier — a tick
    /// already inside the handler runs concurrently with DisposeCore, touching a
    /// `_source` being disposed and an `_engine` being destroyed. It survived
    /// only incidentally (the handler catches ObjectDisposedException and
    /// swallows everything else). Standing down on `IsDisposing` makes that
    /// designed rather than lucky.
    /// </summary>
    [Fact]
    public void ThePositionTickStandsDownOnceDisposeHasStarted()
    {
        var handler = MethodBody("DotCharter.Tone/Audio/JuceAudioPlaybackService.cs",
            "private void PositionTick()");
        var guard = handler.IndexOf("if (IsDisposing) return;", StringComparison.Ordinal);
        var firstTry = handler.IndexOf("try", StringComparison.Ordinal);

        Assert.True(guard >= 0, "The position-timer handler must bail once dispose has started.");
        Assert.True(guard < firstTry, "The guard must be the FIRST thing the handler does.");
    }

    /// <summary>
    /// D5. `NotifySlotsUnloadedAfterCrash` raised the unload events but left
    /// `_sandboxSlots` populated, so until the restart's backoff expired
    /// `IsGuitarVstSlotLoaded(id)` reported TRUE for a plugin whose process was
    /// gone — and a chain apply landing in that window saw an `existing` entry
    /// and skipped the load.
    /// </summary>
    [Fact]
    public void ASandboxCrashClearsTheSlotTableImmediately()
    {
        var body = MethodBody("DotCharter.Tone/Audio/JuceAudioPlaybackService.Sandbox.cs",
            "private void NotifySlotsUnloadedAfterCrash()");

        Assert.Contains("_sandboxSlots.Clear();", body, StringComparison.Ordinal);
        Assert.Contains("_sandboxInstrumentLoaded = false;", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// D4. The sandbox health timer was created lazily but only stopped in
    /// `DisposeSandbox`, so switching back to in-process hosting (or exhausting
    /// the crash budget) left a 3 s no-op timer waking a pool thread twenty
    /// times a minute for the rest of the session.
    /// </summary>
    [Fact]
    public void TheSandboxHealthTimerStopsWithTheSandbox()
    {
        var body = MethodBody("DotCharter.Tone/Audio/JuceAudioPlaybackService.Sandbox.cs",
            "private async Task ShutdownSandboxLockedAsync(bool disarmBridge)");

        Assert.Contains("StopSandboxHealthTimer();", body, StringComparison.Ordinal);
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
