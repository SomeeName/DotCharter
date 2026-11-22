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
        config.Update(c => c.DefaultTonePresetId = "");
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
        Assert.NotNull(chain.AddNode("overdrive"));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(60), ct), "the persist timer never fired");

        // Dispose while the timer callback is parked inside its snapshot. #607:
        // Dispose runs on a thread of its own and must be SEEN parked before the
        // callback is released; a fixed delay did not prove it had even started.
        var exitedWhenDisposeReturned = false;
        var dispose = ParkedCall.Start(() =>
        {
            session.Dispose();
            exitedWhenDisposeReturned = Volatile.Read(ref callbackExited) == 1;
        });

        try
        {
            Assert.True(await dispose.WaitUntilParkedOrReturnedAsync("Dispose()", TimeSpan.FromSeconds(60), ct),
                "Dispose returned while a timer-thread persist was still inside its snapshot");
        }
        finally
        {
            release.Set();
        }

        await dispose.Returned.WaitAsync(TimeSpan.FromSeconds(60), ct);
        Assert.True(exitedWhenDisposeReturned, "Dispose returned before the in-flight persist had left its snapshot");
    }
}
