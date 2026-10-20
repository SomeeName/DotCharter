using System.Diagnostics;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

public sealed class LatencyCalibrationSessionTests
{
    private static long Ms(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    [Fact]
    public void CollectsClicks_AndReportsMedianRoundTrip()
    {
        var session = new LatencyCalibrationSession { ClicksTarget = 3, SettleMs = 100 };
        var now = 0L;
        session.Start(now);

        var roundTrips = new[] { 20.0, 24.0, 22.0 };
        foreach (var rt in roundTrips)
        {
            Assert.True(session.ShouldEmitClick(now));
            session.NotifyClickEmitted(now);
            Assert.Equal(LatencyCalibrationState.WaitingForEcho, session.State);

            Assert.True(session.OfferOnset(now + Ms(rt)));
            now += Ms(rt) + Ms(200); // past the settle window
        }

        Assert.Equal(LatencyCalibrationState.Complete, session.State);
        Assert.Equal(22.0, session.ResultMs!.Value, 1);
    }

    [Fact]
    public void ImplausiblyFastOnset_IsRejected()
    {
        var session = new LatencyCalibrationSession();
        session.Start(0);
        session.NotifyClickEmitted(0);

        Assert.False(session.OfferOnset(Ms(0.2))); // 0.2 ms: not a real echo
        Assert.Equal(LatencyCalibrationState.WaitingForEcho, session.State);
    }

    [Fact]
    public void OnsetWhileSettling_IsIgnored()
    {
        var session = new LatencyCalibrationSession { SettleMs = 500 };
        session.Start(0);
        Assert.False(session.OfferOnset(Ms(10))); // no click emitted yet
    }

    [Fact]
    public void RepeatedTimeouts_FailTheRun()
    {
        var session = new LatencyCalibrationSession
        {
            EchoTimeoutMs = 100, SettleMs = 50, MaxMissedEchoes = 2
        };
        var now = 0L;
        session.Start(now);

        // Pump like the UI timer would; the echo never arrives.
        for (var tick = 0; tick < 40 && session.State != LatencyCalibrationState.Failed; tick++)
        {
            if (session.ShouldEmitClick(now)) session.NotifyClickEmitted(now);
            now += Ms(50);
            session.Advance(now);
        }

        Assert.Equal(LatencyCalibrationState.Failed, session.State);
        Assert.Null(session.ResultMs);
    }

    [Fact]
    public void SingleTimeout_RetriesInsteadOfFailing()
    {
        var session = new LatencyCalibrationSession
        {
            ClicksTarget = 1, EchoTimeoutMs = 100, SettleMs = 50
        };
        var now = 0L;
        session.Start(now);
        session.NotifyClickEmitted(now);

        now += Ms(200);
        session.Advance(now); // first echo missed -> settle for a retry
        Assert.Equal(LatencyCalibrationState.Settling, session.State);

        now += Ms(100);
        Assert.True(session.ShouldEmitClick(now));
        session.NotifyClickEmitted(now);
        Assert.True(session.OfferOnset(now + Ms(15)));
        Assert.Equal(LatencyCalibrationState.Complete, session.State);
        Assert.Equal(15.0, session.ResultMs!.Value, 1);
    }

    [Fact]
    public void Cancel_ReturnsToIdle()
    {
        var session = new LatencyCalibrationSession();
        session.Start(0);
        session.Cancel();
        Assert.Equal(LatencyCalibrationState.Idle, session.State);
        Assert.False(session.ShouldEmitClick(long.MaxValue));
    }
}
