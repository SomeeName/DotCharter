using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     #246: a place of a phrase, section or tone names its bar as well as its time, counted the
///     way the rest of the app counts bars (<c>LiveBarNumbers.BarAt</c>).
/// </summary>
public sealed class MarkerOccurrenceLabelTests
{
    private static string Label(IList<BeatMarker> beats, double ms) =>
        MarkerOccurrenceLabel.Format(FakeLocalization.English(), 2, beats, ms);

    /// <summary>Two bars of 4/4 then 3/4: the bar count follows the measure markers, not a fixed meter.</summary>
    [Fact]
    public void TheBarFollowsAMeterChange()
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 8; i++) beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0 });
        for (var i = 0; i < 9; i++) beats.Add(new BeatMarker { Time = 4000 + i * 500.0, IsMeasure = i % 3 == 0, BeatsInMeasure = 3 });

        Assert.Equal("#2 · bar 1 · 0:01.5", Label(beats, 1500));
        Assert.Equal("#2 · bar 3 · 0:04.0", Label(beats, 4000));
        // 4/4 would put 5.6 s in bar 3; in 3/4 it is the second 3/4 bar, bar 4.
        Assert.Equal("#2 · bar 4 · 0:05.6", Label(beats, 5600));
    }

    /// <summary>A chart with no measure markers cannot be barred: the time stands alone.</summary>
    [Fact]
    public void WithoutMeasureMarkers_TheTimeStandsAlone()
    {
        var beats = Enumerable.Range(0, 8).Select(i => new BeatMarker { Time = i * 500.0 }).ToList();

        Assert.Equal("#2 · 0:02.0", Label(beats, 2000));
    }
}
