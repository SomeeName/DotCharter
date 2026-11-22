using DotCharter.Tone.Audio.Engine;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     #571 row 1: the output latency and the user's audio delay are wall time, but the
///     playhead subtracted them from SONG time as they were. At 2x the audio queued in
///     40 ms of device latency covers 80 ms of the song, so the playhead led the sound.
/// </summary>
public sealed class LatencyCompensationSpeedTests
{
    [Theory]
    [InlineData(1.0, 9_960.0)]
    [InlineData(2.0, 9_920.0)]
    [InlineData(0.5, 9_980.0)]
    public void WallLatency_IsScaledByThePlaybackSpeed(double speed, double expected) =>
        Assert.Equal(expected, PlayheadReadback.CompensateLatency(10_000, 40, speed), 6);

    [Fact]
    public void ACompensationPastTheStart_ClampsToZero() =>
        Assert.Equal(0.0, PlayheadReadback.CompensateLatency(50, 40, 2.0));

    [Fact]
    public void ANonPositiveSpeed_CountsAsNormalSpeed() =>
        Assert.Equal(9_960.0, PlayheadReadback.CompensateLatency(10_000, 40, 0.0), 6);
}
