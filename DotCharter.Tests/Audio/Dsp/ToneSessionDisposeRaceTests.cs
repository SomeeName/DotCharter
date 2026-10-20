using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Audio.Dsp;

/// <summary>
///     Concurrency audit #6: <c>ToneSessionService.Dispose</c> called the
///     parameterless <c>Timer.Dispose()</c>, which does not wait for a callback
///     already executing, and then ran its own final <c>PersistIfChanged</c> —
///     so the shutdown flush could run CONCURRENTLY with a timer-thread persist:
///     two <c>SetRig</c> writes of one file, two unsynchronized signature
///     writes. The timer callback is held inside its chain snapshot by the fake
///     while Dispose is called; Dispose must not return until it has left.
/// </summary>
public sealed class ToneSessionDisposeRaceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public async Task DisposeWaitsForAPersistTheTimerAlreadyStarted()
    {
        var ct = TestContext.Current.CancellationToken;
        var chain = new FakeDspChainService();
        var config = new FakeConfigurationService();
        var store = new ProfileToneRigStore(_dir);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var callbackExited = 0;
        var timerCalls = 0;
        // xunit may run this body on a pool thread itself, so "a timer-thread
        // call" is identified by NOT being the test thread — the restore's own
        // reads run inline here and must not be the ones held.
        var testThread = Environment.CurrentManagedThreadId;
        chain.OnGetNodes = () =>
        {
            // Only the FIRST off-thread snapshot is held; Dispose's final flush
            // (which comes later, on its own task) goes straight through.
            if (Environment.CurrentManagedThreadId != testThread && Interlocked.Increment(ref timerCalls) == 1)
            {
                entered.Set();
                release.Wait(ct);
                Volatile.Write(ref callbackExited, 1);
            }
        };

        var session = new ToneSessionService(chain, config, new DspTonePresetService(chain, config), store,
            logger: null, pollInterval: TimeSpan.FromMilliseconds(20));
        session.Start();
        chain.AttachEngine(0x1000);
        chain.AddNode("overdrive");

        Assert.True(entered.Wait(TimeSpan.FromSeconds(60), ct), "the persist timer never fired");

        // Dispose while the timer callback is parked inside its snapshot.
        using var disposeStarted = new ManualResetEventSlim(false);
        var disposeTask = Task.Run(() =>
        {
            disposeStarted.Set();
            session.Dispose();
            // What Dispose saw when it returned: had the in-flight persist finished?
            return Volatile.Read(ref callbackExited) == 1;
        }, ct);

        disposeStarted.Wait(ct);
        // Give an unguarded Dispose every chance to return early before the
        // callback is released — a delay, not an assertion; with the wait in
        // place the result below cannot depend on it.
        await Task.Delay(100, ct);
        release.Set();

        Assert.True(await disposeTask.WaitAsync(TimeSpan.FromSeconds(60), ct),
            "Dispose returned while a timer-thread persist was still inside its snapshot");
    }
}
