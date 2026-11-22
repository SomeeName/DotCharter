using DotCharter.Services.Editing.Tones;

namespace DotCharter.Tests.Services.Editing.Tones;

/// <summary>
///     A tone change on the Tone row is dragged, grid-snapped, as ONE undo step. A drop never
///     stacks two changes, never swaps them and never goes before zero (the <see cref="ToneChangeNudge" />
///     rules); a multi-selection moves rigidly; a cancel puts everything back.
/// </summary>
public sealed class ToneChangeDragTests
{
    private static readonly List<BeatMarker> Beats =
        [.. Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120, NoteDenominator = 4 })];

    private static (Arrangement Arr, ToneChange A, ToneChange B, ToneChange C) Chart()
    {
        var arr = new Arrangement { ToneBase = "clean", ToneA = "lead", ToneB = "crunch" };
        var a = new ToneChange { Position = new FractionalPosition(2), ToneName = "lead" };
        var b = new ToneChange { Position = new FractionalPosition(4), ToneName = "crunch" };
        var c = new ToneChange { Position = new FractionalPosition(6), ToneName = "clean" };
        arr.ToneChanges.Add(a);
        arr.ToneChanges.Add(b);
        arr.ToneChanges.Add(c);
        return (arr, a, b, c);
    }

    [Fact]
    public void ADrag_MovesTheChange_AsOneUndoStep()
    {
        var (arr, a, _, _) = Chart();
        var drag = new ToneChangeDrag(arr.ToneChanges, [a]);

        drag.Preview(new FractionalPosition(0, new Fraction(1, 2)), Beats);
        Assert.Equal(new FractionalPosition(2, new Fraction(1, 2)), a.Position);
        drag.Preview(new FractionalPosition(1), Beats);
        Assert.Equal(new FractionalPosition(3), a.Position);

        var history = new HistoryService(new FakeAppLogger());
        Assert.True(history.Do(drag.Commit()!));
        Assert.Equal(new FractionalPosition(3), a.Position);

        history.Undo();
        Assert.Equal(new FractionalPosition(2), a.Position);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void ADropOnAnotherChange_OrPastIt_IsRefused_AndTheLastLegalSpotStays()
    {
        var (arr, a, b, _) = Chart();
        var drag = new ToneChangeDrag(arr.ToneChanges, [a]);
        drag.Preview(new FractionalPosition(1), Beats);

        Assert.Equal(new FractionalPosition(1), drag.Preview(new FractionalPosition(2), Beats));
        Assert.Equal(new FractionalPosition(3), a.Position);
        Assert.Equal(new FractionalPosition(1), drag.Preview(new FractionalPosition(3), Beats));
        Assert.Equal(new FractionalPosition(3), a.Position);
        Assert.Equal(new FractionalPosition(4), b.Position);
        Assert.False(drag.IsLegal(new FractionalPosition(2), Beats));
    }

    [Fact]
    public void ADropWithinTheValidatorsSlopOfAnotherChange_IsAStack()
    {
        var (arr, a, b, _) = Chart();
        b.Position = new FractionalPosition(3, new Fraction(1, 1000)); // 0.5 ms after beat 3
        var drag = new ToneChangeDrag(arr.ToneChanges, [a]);

        Assert.False(drag.IsLegal(new FractionalPosition(1), Beats));
    }

    [Fact]
    public void ADropBeforeZero_IsClampedToZero_ForTheWholeSet()
    {
        var (arr, a, b, _) = Chart();
        var drag = new ToneChangeDrag(arr.ToneChanges, [a, b]);

        drag.Preview(FractionalPosition.Zero - new FractionalPosition(5), Beats);

        Assert.Equal(FractionalPosition.Zero, a.Position);
        Assert.Equal(new FractionalPosition(2), b.Position);
    }

    [Fact]
    public void AMultiSelection_MovesTogether_AndUndoesInOneStep()
    {
        var (arr, a, b, c) = Chart();
        var drag = new ToneChangeDrag(arr.ToneChanges, [a, b]);

        drag.Preview(new FractionalPosition(1), Beats);
        Assert.Equal(new FractionalPosition(3), a.Position);
        Assert.Equal(new FractionalPosition(5), b.Position);
        // b would land on c: the whole set stops at the last legal delta.
        drag.Preview(new FractionalPosition(2), Beats);
        Assert.Equal(new FractionalPosition(5), b.Position);

        var history = new HistoryService(new FakeAppLogger());
        Assert.True(history.Do(drag.Commit()!));
        Assert.Equal(new FractionalPosition(6), c.Position);

        history.Undo();
        Assert.Equal(new FractionalPosition(2), a.Position);
        Assert.Equal(new FractionalPosition(4), b.Position);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void AMultiSelection_CannotCarryAMemberOverAChangeThatStays()
    {
        var (arr, a, _, c) = Chart();
        var drag = new ToneChangeDrag(arr.ToneChanges, [a, c]);

        Assert.True(drag.IsLegal(new FractionalPosition(1), Beats));
        Assert.False(drag.IsLegal(new FractionalPosition(2), Beats));
        Assert.False(drag.IsLegal(FractionalPosition.Zero - new FractionalPosition(2), Beats));
    }

    [Fact]
    public void Cancel_RestoresEveryChange_AndCommitsNothing()
    {
        var (arr, a, b, _) = Chart();
        var drag = new ToneChangeDrag(arr.ToneChanges, [a, b]);
        drag.Preview(new FractionalPosition(1), Beats);

        drag.Restore();

        Assert.Equal(new FractionalPosition(2), a.Position);
        Assert.Equal(new FractionalPosition(4), b.Position);
        Assert.Null(drag.Commit());
    }
}
