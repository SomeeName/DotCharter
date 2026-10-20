using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

public sealed class RsToneScheduleTests
{
    private static RsToneSchedule Sched(string baseKey, params (double, string)[] b) =>
        new(baseKey, b.Select(x => (x.Item1, x.Item2)).ToList());

    [Fact]
    public void EmptySchedule_AlwaysResolvesToBase()
    {
        var s = Sched("clean");
        Assert.True(s.IsEmpty);
        Assert.Equal("clean", s.ResolveAt(0));
        Assert.Equal("clean", s.ResolveAt(999_999));
    }

    [Fact]
    public void ResolvesLastBoundaryAtOrBeforeTime()
    {
        var s = Sched("clean", (1000, "verse"), (5000, "chorus"), (9000, "solo"));

        Assert.Equal("clean", s.ResolveAt(0));       // before first boundary → base
        Assert.Equal("clean", s.ResolveAt(999));
        Assert.Equal("verse", s.ResolveAt(1000));    // exactly on the boundary
        Assert.Equal("verse", s.ResolveAt(4999));
        Assert.Equal("chorus", s.ResolveAt(5000));
        Assert.Equal("solo", s.ResolveAt(9000));
        Assert.Equal("solo", s.ResolveAt(1_000_000)); // past the last → sticks
    }

    [Fact]
    public void UnsortedBoundaries_AreSorted()
    {
        var s = Sched("base", (9000, "solo"), (1000, "verse"), (5000, "chorus"));

        Assert.Equal("verse", s.ResolveAt(1500));
        Assert.Equal("chorus", s.ResolveAt(6000));
        Assert.Equal("solo", s.ResolveAt(9500));
    }
}
