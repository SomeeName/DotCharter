using DotCharter.Services.Editing.Pen;

namespace DotCharter.Tests.Services.Editing.Pen;

/// <summary>
///     The multi-digit fret typist. Pure by construction (the clock is a
///     parameter), which is the whole reason it was lifted out of
///     <c>SheetMusicView.OnKeyDown</c> — the sheet's copy could only be
///     exercised by driving a real control.
/// </summary>
public class FretDigitAccumulatorTests
{
    private const int Window = FretDigitAccumulator.PenWindowMs;
    private const int Max = FretDigitAccumulator.MaxTypedFret;

    [Fact]
    public void AFirstDigitIsThatFret()
    {
        var acc = FretDigitAccumulator.Empty.Push(7, 1000, Window, Max, out var fret);

        Assert.Equal(7, fret);
        Assert.True(acc.IsAccumulating);
    }

    [Fact]
    public void ASecondDigitInsideTheWindowExtendsTheValue()
    {
        var acc = FretDigitAccumulator.Empty.Push(1, 1000, Window, Max, out _);
        acc.Push(2, 1000 + Window, Window, Max, out var fret);

        Assert.Equal(12, fret);
    }

    [Fact]
    public void ASecondDigitPastTheWindowStartsOver()
    {
        var acc = FretDigitAccumulator.Empty.Push(1, 1000, Window, Max, out _);
        acc.Push(2, 1000 + Window + 1, Window, Max, out var fret);

        Assert.Equal(2, fret);
    }

    [Fact]
    public void AValueThatWouldOvershootTheNeckStartsOverOnTheNewDigit()
    {
        // 2,4 = fret 24. A third digit must mean "fret 7", never a clamp back
        // onto 24 — clamping would make the key silently do nothing.
        var acc = FretDigitAccumulator.Empty.Push(2, 1000, Window, Max, out _);
        acc = acc.Push(4, 1100, Window, Max, out var twentyFour);
        acc.Push(7, 1200, Window, Max, out var seven);

        Assert.Equal(24, twentyFour);
        Assert.Equal(7, seven);
    }

    [Fact]
    public void FretZeroIsARealFretAndKeepsAccumulating()
    {
        // -1, not 0, is "nothing typed" — 0 is the open string / the capo.
        Assert.False(FretDigitAccumulator.Empty.IsAccumulating);

        var acc = FretDigitAccumulator.Empty.Push(0, 1000, Window, Max, out var zero);
        Assert.Equal(0, zero);
        Assert.True(acc.IsAccumulating);

        acc.Push(5, 1100, Window, Max, out var five);
        Assert.Equal(5, five);
    }

    [Fact]
    public void ResetEndsTheRun()
    {
        var acc = FretDigitAccumulator.Empty.Push(1, 1000, Window, Max, out _).Reset();
        acc.Push(2, 1050, Window, Max, out var fret);

        Assert.Equal(2, fret);
    }

    [Fact]
    public void TheSheetPaneWindowIsUnchangedFromWhatItShipped()
    {
        // Lifting the arithmetic out of SheetMusicView must be a strict no-op.
        Assert.Equal(800, FretDigitAccumulator.SheetWindowMs);
        Assert.Equal(2000, FretDigitAccumulator.PenWindowMs);
    }
}
