using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing;

public sealed class GpLegacyNoteEffectsHelpersTests
{
    [Fact]
    public void PreviousPowerOfTwo_ReturnsFloorPow2()
    {
        Assert.Equal(1, GpLegacyNoteEffectsHelpers.PreviousPowerOfTwo(0));
        Assert.Equal(1, GpLegacyNoteEffectsHelpers.PreviousPowerOfTwo(1));
        Assert.Equal(2, GpLegacyNoteEffectsHelpers.PreviousPowerOfTwo(3));
        Assert.Equal(4, GpLegacyNoteEffectsHelpers.PreviousPowerOfTwo(7));
        Assert.Equal(8, GpLegacyNoteEffectsHelpers.PreviousPowerOfTwo(8));
        Assert.Equal(64, GpLegacyNoteEffectsHelpers.PreviousPowerOfTwo(127));
    }

    /// <summary>
    ///     The value at which <c>while (p * 2 &lt;= n) p *= 2;</c> stopped
    ///     terminating: at <c>p == 2^30</c> the unchecked doubling wraps to
    ///     <see cref="int.MinValue" />, which is still <c>&lt;= n</c>, then to
    ///     <c>0</c>, which is also still <c>&lt;= n</c> — and <c>0 * 2 == 0</c>
    ///     forever.
    ///     <para>
    ///         Run on a worker with a watchdog ON PURPOSE. Asserting the value
    ///         alone would make a regression HANG the whole gate instead of
    ///         failing it, and a hang is the very failure mode being guarded
    ///         against: no exception, no timeout, 100% of one core for as long as
    ///         the app is open. The window is enormous relative to the ~30
    ///         iterations the fixed loop runs, so it cannot flake on a loaded
    ///         machine.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1 << 30)]
    [InlineData((1 << 30) + 1)]
    [InlineData(int.MaxValue)]
    public async Task PreviousPowerOfTwo_AtOrPastTwoPow30_Terminates(int n)
    {
        var work = Task.Run(() => GpLegacyNoteEffectsHelpers.PreviousPowerOfTwo(n));

        // A TimeoutException here IS the regression: the loop is still spinning.
        var result = await work.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1 << 30, result);
    }

    [Fact]
    public void ApplySlide_Gp4_LegatoFlag_SetsSlideToMinusOne()
    {
        var note = new GuitarNote { Fret = 7 };
        GpLegacyNoteEffectsHelpers.ApplySlide(1, note, isVersion5: false);
        Assert.Equal(-1, note.SlideTo);
        Assert.False(note.LinkNext);
    }

    [Fact]
    public void ApplySlide_Gp4_LinkNextFlag_SetsLinkNext()
    {
        var note = new GuitarNote { Fret = 7 };
        GpLegacyNoteEffectsHelpers.ApplySlide(2, note, isVersion5: false);
        Assert.Equal(-1, note.SlideTo);
        Assert.True(note.LinkNext);
    }

    [Theory]
    [InlineData(3, 7, 2)]   // down 5fr from 7 → 2
    [InlineData(3, 4, 1)]   // clamp at 1
    [InlineData(4, 7, 12)]  // up 5fr from 7 → 12
    [InlineData(4, 22, 24)] // clamp at 24
    public void ApplySlide_Gp4_UnpitchedSlide_ClampsTarget(int flag, int fret, int expectedTarget)
    {
        var note = new GuitarNote { Fret = fret };
        GpLegacyNoteEffectsHelpers.ApplySlide(flag, note, isVersion5: false);
        Assert.True(note.UnpitchedSlide);
        Assert.Equal(expectedTarget, note.SlideTo);
    }

    [Fact]
    public void ApplySlide_Gp5_TreatsValueAsBitmask_AllFlagsCombined()
    {
        var note = new GuitarNote { Fret = 7 };
        // 0x01 legato + 0x02 link-next combined.
        GpLegacyNoteEffectsHelpers.ApplySlide(0x03, note, isVersion5: true);
        Assert.Equal(-1, note.SlideTo);
        Assert.True(note.LinkNext);
    }

    [Fact]
    public void ApplySlide_Gp5_DownAndUpUnpitched_LastFlagWins()
    {
        var note = new GuitarNote { Fret = 7 };
        GpLegacyNoteEffectsHelpers.ApplySlide(0x0C, note, isVersion5: true);
        Assert.True(note.UnpitchedSlide);
        // 0x04 sets to fret-5=2, then 0x08 overrides to fret+5=12.
        Assert.Equal(12, note.SlideTo);
    }

    [Fact]
    public void ApplySlide_Gp4_UnknownFlag_DoesNothing()
    {
        var note = new GuitarNote { Fret = 7 };
        GpLegacyNoteEffectsHelpers.ApplySlide(99, note, isVersion5: false);
        Assert.Null(note.SlideTo);
        Assert.False(note.LinkNext);
        Assert.False(note.UnpitchedSlide);
    }

    [Fact]
    public void MaterializeBends_EmptyPoints_NoOp()
    {
        var note = new GuitarNote { Fret = 5 };
        GpLegacyNoteEffectsHelpers.MaterializeBends(note,
            Array.Empty<(int, double)>(),
            new FractionalPosition(0), new Fraction(1, 4));
        Assert.Empty(note.BendValues);
    }

    [Fact]
    public void MaterializeBends_AddsAllPoints_AndClampsOffset()
    {
        var note = new GuitarNote { Fret = 5 };
        var points = new[] { (0, 0.0), (30, 1.0), (90, 1.0) }; // 90 clamps to 60
        GpLegacyNoteEffectsHelpers.MaterializeBends(note, points,
            new FractionalPosition(0), new Fraction(1, 4));
        Assert.Equal(3, note.BendValues.Count);
        Assert.All(note.BendValues, b => Assert.True(b.Step >= 0));
    }
}
