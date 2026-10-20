namespace DotCharter.Tests.Services.Editing.Tempo;

/// <summary>
/// #282: a tempo scale put two beats in every beat's place (double-time) or removed
/// every other one (half-time) but left <c>BeatsInMeasure</c> / <c>NoteDenominator</c>
/// alone, so a doubled 4/4 bar held 8 beats while saying 4/4. The Guitar Pro export
/// groups a bar as <c>BeatsInMeasure</c> consecutive beats, so its bars split wrong.
/// The invariant pinned here: a bar's <c>BeatsInMeasure</c> is the number of beats
/// it actually has.
/// </summary>
public sealed class BeatScaleTimeSignatureTests
{
    private static ChartProject Grid(int beatsPerBar, int denominator, int bars)
    {
        var project = new ChartProject();
        for (var i = 0; i < beatsPerBar * bars + 1; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500, Bpm = 120, IsMeasure = i % beatsPerBar == 0,
                BeatsInMeasure = beatsPerBar, NoteDenominator = denominator
            });
        }

        return project;
    }

    private static List<(int Count, int Numerator, int Denominator)> Bars(IList<BeatMarker> beats)
    {
        var bars = new List<(int, int, int)>();
        var start = -1;
        for (var i = 0; i < beats.Count; i++)
        {
            if (!beats[i].IsMeasure) continue;
            if (start >= 0) bars.Add((i - start, beats[start].BeatsInMeasure, beats[start].NoteDenominator));
            start = i;
        }

        return bars;
    }

    private static void Scale(ChartProject project, double factor)
    {
        var action = new BeatScaleService(new FakeAppLogger())
            .BuildScaleAction(project, 0, project.Beats[^1].Time, factor);
        Assert.NotNull(action);
        action!.Execute();
    }

    [Fact]
    public void DoubleTime_FourFour_BecomesEightEight()
    {
        var project = Grid(4, 4, bars: 4);
        Scale(project, 2.0);

        Assert.All(Bars(project.Beats), bar => Assert.Equal((8, 8, 8), bar));
    }

    [Fact]
    public void DoubleTime_ThreeFour_BecomesSixEight()
    {
        var project = Grid(3, 4, bars: 4);
        Scale(project, 2.0);

        Assert.All(Bars(project.Beats), bar => Assert.Equal((6, 6, 8), bar));
    }

    [Fact]
    public void HalfTime_FourFour_BecomesTwoTwo()
    {
        var project = Grid(4, 4, bars: 4);
        Scale(project, 0.5);

        Assert.All(Bars(project.Beats), bar => Assert.Equal((2, 2, 2), bar));
    }

    [Fact]
    public void HalfTime_ThreeFour_SaysWhatTheGridReallyIs()
    {
        var project = Grid(3, 4, bars: 4);
        Scale(project, 0.5);

        // Every bar's numerator is its real beat count, and never fractional.
        var bars = Bars(project.Beats);
        Assert.NotEmpty(bars);
        Assert.All(bars, bar =>
        {
            Assert.Equal(bar.Count, bar.Numerator);
            Assert.Equal(2, bar.Denominator);
        });
    }

    [Fact]
    public void BarsOutsideTheRange_AreUntouched()
    {
        var project = Grid(4, 4, bars: 4);
        var action = new BeatScaleService(new FakeAppLogger()).BuildScaleAction(project, 0, 4 * 500, 2.0);
        action!.Execute();

        var bars = Bars(project.Beats);
        Assert.Equal((8, 8, 8), bars[0]);
        Assert.All(bars.Skip(1), bar => Assert.Equal((4, 4, 4), bar));
    }

    [Fact]
    public void Undo_RestoresTheOriginalSignature()
    {
        var project = Grid(4, 4, bars: 2);
        var action = new BeatScaleService(new FakeAppLogger())
            .BuildScaleAction(project, 0, project.Beats[^1].Time, 2.0)!;
        action.Execute();
        action.Undo();

        Assert.All(Bars(project.Beats), bar => Assert.Equal((4, 4, 4), bar));
    }
}
