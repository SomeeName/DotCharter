namespace DotCharter.Tests.Services.Editing.Improvement;

/// <summary>
///     #295: which sounds a generated easy level keeps, and how many, follow what official ladders
///     keep (measured by <c>LocalLevelLadderScoreTest</c>): bar starts and long notes first, and the
///     official density curve rather than a straight line.
/// </summary>
public sealed class LevelGenerationRankingTests
{
    private const int Levels = 20;

    /// <summary>16 bars of 4/4 at 120 BPM.</summary>
    private static List<BeatMarker> Beats() =>
    [
        .. Enumerable.Range(0, 64).Select(i => new BeatMarker
        {
            Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0
        })
    ];

    private static ChartLevel Generated(Arrangement arrangement, int level, List<BeatMarker> beats)
    {
        Assert.True(new LevelGenerationService().TryGenerate(arrangement, Levels, beats));
        return arrangement.Levels.OrderBy(l => l.DifficultyIndex).ElementAt(level);
    }

    private static Arrangement WithLevel(ChartLevel level)
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(level);
        return arrangement;
    }

    [Fact]
    public void AHeldNote_OutranksARunOfShortOnesOnTheSameKindOfBeat()
    {
        var beats = Beats();
        var source = new ChartLevel();
        // Beat 1 of bar 3 (beat 9) holds four beats; every other beat 2/3/4 is a quarter.
        for (var beat = 1; beat < 64; beat++)
        {
            if (beat % 4 == 0) continue;
            source.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(beat), StringIndex = 1, Fret = 5,
                Sustain = beat == 9 ? new FractionalPosition(3) : FractionalPosition.Zero
            });
        }

        var easiest = Generated(WithLevel(source), 0, beats);

        Assert.Contains(easiest.Notes, n => n.Position == new FractionalPosition(9));
    }

    [Fact]
    public void ABarStart_OutranksAPlainBeat()
    {
        var beats = Beats();
        var source = new ChartLevel();
        for (var beat = 0; beat < 64; beat++)
            source.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = 1, Fret = 5 });

        var easiest = Generated(WithLevel(source), 0, beats);

        Assert.NotEmpty(easiest.Notes);
        Assert.All(easiest.Notes, n => Assert.True(beats[n.Position.BeatId].IsMeasure, $"kept beat {n.Position.BeatId}, not a bar start"));
    }

    [Fact]
    public void TheEasiestLevel_KeepsAboutTheOfficialShare_NotOneLevelsWorth()
    {
        var beats = Beats();
        var source = new ChartLevel();
        for (var eighth = 0; eighth < 128; eighth++)
            source.Notes.Add(new GuitarNote { Position = new FractionalPosition(eighth / 2, new Fraction(eighth % 2, 2)), StringIndex = 1, Fret = 5 });

        var easiest = Generated(WithLevel(source), 0, beats);

        // Official ladders keep ~5.6 % at the easiest level and ~60 % at the middle one; a
        // straight line from 1/N kept 5 % and 55 % (71 of these 128).
        Assert.InRange(easiest.Notes.Count, 6, 9);
        var middle = Generated(WithLevel(source), Levels / 2, beats);
        Assert.InRange(middle.Notes.Count, 76, 80);
    }
}
