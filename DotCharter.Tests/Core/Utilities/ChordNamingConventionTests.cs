namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     Charting conventions for generated chord names: a major seventh keeps its capital M in
///     every style, and an estimated key in a down-tuned arrangement takes the flat side of an
///     enharmonic pair, as the tuning's own names do.
/// </summary>
public sealed class ChordNamingConventionTests
{
    private static readonly string[] Standard = ["E", "A", "D", "G", "B", "E"];

    [Theory]
    [InlineData(ChordNamingStyle.Eof)]
    [InlineData(ChordNamingStyle.Musical)]
    public void AMajorSeventh_IsSpelledWithACapitalM(ChordNamingStyle style)
    {
        var names = ChordNameSuggester.SuggestChordNames(Standard, ChordNameAnswerKeyTests.ParseFrets("x,3,2,0,0,0"), 0,
            new ChordNamingOptions(style, null));

        Assert.Equal("CMaj7", names[0]);
        Assert.DoesNotContain(names, name => name.Contains("maj", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEstimatedGSharpMinor_InEbStandard_IsAbMinor() =>
        Assert.Equal(new ChordKeySignature(-7, true), ChordKeyEstimator.Resolve(GSharpMinorSong(-1), null));

    [Fact]
    public void TheSameSong_InEStandard_StaysGSharpMinor() =>
        Assert.Equal(new ChordKeySignature(5, true), ChordKeyEstimator.Resolve(GSharpMinorSong(0), null));

    [Fact]
    public void AnAuthoredKey_IsNeverRespelled()
    {
        var chart = new ChartData { KeyAccidentals = 5, KeyIsMinor = true, KeyLocked = true };
        Assert.Equal(new ChordKeySignature(5, true), ChordKeyEstimator.Resolve(GSharpMinorSong(-1), chart));
    }

    /// <summary>Single notes on the A string that sound a G# minor tune, in a tuning <paramref name="offset" /> from E Standard.</summary>
    private static Arrangement GSharpMinorSong(int offset)
    {
        var arrangement = new Arrangement();
        arrangement.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(offset));
        arrangement.Tuning = TuningDisplayNames.For(6, Enumerable.Repeat(offset, 6).ToArray()) ?? arrangement.Tuning;
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var openA = 9 + offset;
        int[] tune = [8, 8, 8, 8, 8, 8, 11, 11, 11, 11, 3, 3, 3, 3, 1, 1, 6, 6, 10, 10, 4, 4];
        for (var i = 0; i < tune.Length; i++)
            level.Notes.Add(new GuitarNote { StringIndex = 1, Fret = ((tune[i] - openA) % 12 + 12) % 12, Position = new FractionalPosition(i) });
        return arrangement;
    }
}
