using DotCharter.Services.Import.GuitarPro.Report.AnswerKey;

namespace DotCharter.Tests.Services.Import.GuitarPro.Report.AnswerKey;

/// <summary>Measure alignment of <c>tools/tab-vs-chart</c> on synthetic measures.</summary>
public sealed class TabChartAlignerTests
{
    /// <summary>A chart with a two-bar count-in, and a bar of the tab the charter cut.</summary>
    [Fact]
    public void ACountInAndACutBar_AreSkipped_AndEveryOtherBarPairsWithItsTwin()
    {
        var tab = new[] { Bar(0, 3), Bar(1, 5), Bar(2, 7), Bar(3, 9), Bar(4, 11), Bar(5, 13) };
        // The chart: two silent bars, then the tab without its bar 3.
        var chart = new[] { Silent(0), Silent(1), Bar(2, 3), Bar(3, 5), Bar(4, 7), Bar(5, 11), Bar(6, 13) };

        var alignment = TabChartAligner.Align(tab, chart);

        Assert.Equal("dp", alignment.Method);
        Assert.Equal(2, alignment.FirstNoteOffset);
        Assert.Equal([(0, 2), (1, 3), (2, 4), (4, 5), (5, 6)], alignment.Pairs);
        Assert.True(alignment.MeanSimilarity > 0.99, alignment.MeanSimilarity.ToString());
    }

    [Fact]
    public void ContentThatAgreesNowhere_FallsBackToTheFirstNoteOffset()
    {
        var tab = new[] { Silent(0), Bar(1, 3, 0), Bar(2, 3, 0) };
        var chart = new[] { Silent(0), Silent(1), Silent(2), OffBeats(3), OffBeats(4) };

        var alignment = TabChartAligner.Align(tab, chart);

        Assert.Equal("first-notes", alignment.Method);
        Assert.Equal(2, alignment.FirstNoteOffset);
        Assert.Equal([(0, 2), (1, 3), (2, 4)], alignment.Pairs);
    }

    /// <summary>Four quarter notes on string 1 from <paramref name="fret" /> up; a string shifts the pitch.</summary>
    private static MeasureSounds Bar(int index, int fret, int stringIndex = 1) => new(index, 4,
        [.. Enumerable.Range(0, 4).Select(b => new StruckSound(index, b, new FractionalPosition(index * 4 + b), stringIndex, fret + b,
            40 + stringIndex * 5 + fret + b, SoundTechnique.None, null))]);

    /// <summary>Three eighths off the beat on the top string: no onset, pitch or fret in common with <see cref="Bar" />.</summary>
    private static MeasureSounds OffBeats(int index) => new(index, 4,
        [.. Enumerable.Range(0, 3).Select(b => new StruckSound(index, b + 0.5, new FractionalPosition(index * 4 + b), 5, 20, 89, SoundTechnique.None, null))]);

    private static MeasureSounds Silent(int index) => new(index, 4, []);
}
