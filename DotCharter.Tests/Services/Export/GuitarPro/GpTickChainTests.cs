using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Export.GuitarPro;

/// <summary>
///     Sustains that fit no single note value are written as an EXACT chain of tied values.
///     Longest-first alone drops the tick it cannot spell, so the bar comes out
///     short and every later onset in it moves.
/// </summary>
public sealed class GpTickChainTests
{
    /// <summary>The tick lengths of a x/4 bar's note values on the 48-per-beat grid, longest first.</summary>
    private static readonly int[] Quarter4 = [288, 192, 144, 128, 96, 72, 64, 48, 36, 32, 24, 18, 16, 12, 9, 8, 6, 4, 3, 2];

    [Theory]
    [InlineData(7)]
    [InlineData(41)]
    [InlineData(5)]
    [InlineData(47)]
    public void ALengthLongestFirstCannotSpell_IsSpelledExactly(int ticks)
    {
        var chain = new GpTickChain(Quarter4);

        Assert.True(chain.TrySpell(ticks, out var values));
        Assert.Equal(ticks, values.Sum(i => Quarter4[i]));
    }

    [Fact]
    public void ALengthLongestFirstCanSpell_KeepsThatSpelling()
    {
        Assert.True(new GpTickChain(Quarter4).TrySpell(60, out var values));
        Assert.Equal([48, 12], values.Select(i => Quarter4[i]));
    }

    [Fact]
    public void OneTick_IsNotWritable_SoASoundIsHeldOverIt()
    {
        var chain = new GpTickChain(Quarter4);

        Assert.False(chain.IsWritable(1));
        // 47 of a 48-tick gap would leave a one-tick rest: hold to the onset instead.
        Assert.Equal(48, chain.Fit(47, 48));
        // A one-tick sound cannot be written: the nearest writable length is two.
        Assert.Equal(2, chain.Fit(1, 48));
        Assert.Equal(7, chain.Fit(7, 48));
    }

    /// <summary>The bar-level proof: longest-first, a staccato sustain of 7/48 beat loses a tick of
    /// the note and another of the rest after it, so the next onsets come back early.</summary>
    [Fact]
    public void ASustainNoNoteValueSpells_KeepsEveryLaterOnsetInPlace()
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0, Fraction.Zero), StringIndex = 1, Fret = 5, Sustain = new FractionalPosition(0, new Fraction(7, 48)) });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1, Fraction.Zero), StringIndex = 1, Fret = 7, Sustain = new FractionalPosition(0, new Fraction(7, 48)) });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2, Fraction.Zero), StringIndex = 1, Fret = 8 });
        arrangement.Levels.Add(level);
        List<BeatMarker> beats = [.. Enumerable.Range(0, 4).Select(i => new BeatMarker
        {
            Time = i * 500.0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i == 0
        })];

        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-tickchain-{Guid.NewGuid():N}.gp");
        try
        {
            File.WriteAllBytes(path, GpExporter.ExportToBytes(arrangement, level, beats));
            var importer = new GpImporter();
            var back = Assert.Single(importer.ImportTracks(path, [.. importer.GetAvailableTracks(path).Select(t => t.Id)]));

            Assert.Equal([0, 1, 2], back.Levels[0].Notes.OrderBy(n => n.Position).Select(n => n.Position.DoubleValue));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
