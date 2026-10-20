using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

public sealed class RsToneScannerTests
{
    private static RsToneScanner Scanner(string baseKey, params (double, string)[] b) =>
        new(new RsToneSchedule(baseKey, b.Select(x => (x.Item1, x.Item2)).ToList()));

    [Fact]
    public void InBaseRegion_DoesNotFire()
    {
        var s = Scanner("clean", (5000, "chorus"));
        Assert.False(s.TryAdvance(0, out _));
        Assert.False(s.TryAdvance(2000, out _));
        Assert.Equal("clean", s.ActiveKey);
    }

    [Fact]
    public void CrossingABoundary_FiresOnceWithTheNewKey()
    {
        var s = Scanner("clean", (5000, "chorus"), (9000, "solo"));

        Assert.False(s.TryAdvance(4990, out _));
        Assert.True(s.TryAdvance(5010, out var k1));
        Assert.Equal("chorus", k1);
        // Same region again → no re-fire.
        Assert.False(s.TryAdvance(6000, out _));
        Assert.False(s.TryAdvance(8000, out _));
        // Next boundary fires.
        Assert.True(s.TryAdvance(9500, out var k2));
        Assert.Equal("solo", k2);
    }

    [Fact]
    public void SeekingBackward_ReResolves_AndFires()
    {
        var s = Scanner("clean", (5000, "chorus"));
        Assert.True(s.TryAdvance(6000, out _)); // now on chorus
        // Rewind to the base region — must report the change back to clean.
        Assert.True(s.TryAdvance(1000, out var k));
        Assert.Equal("clean", k);
    }

    [Fact]
    public void Reset_ForcesTheNextCallToReReportTheActiveTone()
    {
        var s = Scanner("clean", (5000, "chorus"));
        Assert.True(s.TryAdvance(6000, out _)); // chorus
        Assert.False(s.TryAdvance(6500, out _)); // no change

        s.Reset();
        Assert.True(s.TryAdvance(6500, out var k)); // re-armed → re-reports chorus
        Assert.Equal("chorus", k);
    }
}
