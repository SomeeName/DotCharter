using Avalonia.Media;
using DotCharter.Rendering.Data;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The resume-rewind "dead note" predicate: a note AHEAD of the playhead that is
///     ALREADY resolved can only exist while a pause rewind replays a judged stretch.
///     The renderer keeps no mode state — this predicate is the whole mechanism, so it
///     must be false in every normal-play situation.
/// </summary>
public sealed class DeadNoteMathTests
{
    [Theory]
    // note, playhead, resolvedThrough, expected
    // --- normal play: the horizon always TRAILS the playhead, so nothing is dead.
    [InlineData(3000, 2000, 1750, false)] // upcoming note, judged history behind us
    [InlineData(1900, 2000, 1750, false)] // note already passed (behind the playhead)
    [InlineData(3000, 2000, double.NegativeInfinity, false)] // nothing judged yet
    // --- a long sustain still ringing UNDER the playhead: its head is behind the
    //     horizon by definition. Without the note >= playhead half it would go dark.
    [InlineData(1700, 2000, 1750, false)]
    // --- resume rewind: the playhead sits BEHIND the resolved horizon.
    [InlineData(2000, 1500, 2500, true)]  // replayed, already judged
    [InlineData(1500, 1500, 2500, true)]  // exactly at the rewound playhead
    [InlineData(2500, 1500, 2500, true)]  // exactly on the horizon
    [InlineData(2501, 1500, 2500, false)] // first still-judgeable note past the horizon
    [InlineData(3000, 1500, 2500, false)] // the rest of the song is live
    public void IsDead_OnlyInsideAReplay(double noteTimeMs, double playheadMs, double resolvedThroughMs,
        bool expected) => Assert.Equal(expected, DeadNoteMath.IsDead(noteTimeMs, playheadMs, resolvedThroughMs));

    [Fact]
    public void Dim_DarkensAndFades_ButKeepsTheHue()
    {
        var lit = Color.FromArgb(200, 200, 100, 40);

        var dim = DeadNoteMath.Dim(lit);

        Assert.True(dim.A < lit.A);
        Assert.True(dim.R < lit.R);
        Assert.True(dim.G < lit.G);
        Assert.True(dim.B < lit.B);
        // Same ratios between the channels: a dead note is the SAME string, just inert.
        Assert.Equal(lit.R / (double)lit.G, dim.R / (double)dim.G, 1);
    }
}
