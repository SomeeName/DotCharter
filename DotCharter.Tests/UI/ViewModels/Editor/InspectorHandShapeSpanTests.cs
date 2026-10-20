using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Committing ONE box of the Span card silently moved the OTHER end of the
///     hand shape.
///     <para>
///         Both boxes bind <c>UpdateSourceTrigger=LostFocus</c> and both change
///         handlers ran the same commit, which re-parsed BOTH texts. The texts
///         are printed through <c>FractionalLengthFormat.Format</c>, and that
///         spells anything from one beat up with <c>"0.##"</c> — two decimals. A
///         shape sitting on a triplet at beat 137⅓ therefore printed "137.33",
///         and editing only the END rewrote the START from that rounding: the
///         bar crept off the grid by 1/300 of a beat per edit, on a card whose
///         whole purpose is exact numeric authoring.
///     </para>
///     <para>
///         The endpoint the user did not type now comes from the MODEL, which
///         has no round trip to lose.
///     </para>
/// </summary>
public sealed class InspectorHandShapeSpanTests
{
    /// <summary>Beat 137⅓ — exactly the value "0.##" cannot spell.</summary>
    private static FractionalPosition OffGridStart => new(new Fraction(412, 3));

    [Fact]
    public void EditingTheEnd_LeavesTheStartExactlyWhereItWas()
    {
        var span = HandShapeEditorViewModel.ResolveHandShapeSpan(
            OffGridStart, new FractionalPosition(140), "141", startEdited: false);

        Assert.NotNull(span);
        Assert.Equal(OffGridStart, span!.Value.Start);
        Assert.Equal(new FractionalPosition(141), span.Value.End);
    }

    [Fact]
    public void EditingTheStart_LeavesTheEndExactlyWhereItWas()
    {
        var end = new FractionalPosition(new Fraction(500, 3));

        var span = HandShapeEditorViewModel.ResolveHandShapeSpan(
            new FractionalPosition(100), end, "104", startEdited: true);

        Assert.NotNull(span);
        Assert.Equal(new FractionalPosition(104), span!.Value.Start);
        Assert.Equal(end, span.Value.End);
    }

    /// <summary>The whole point: the rounded text the box SHOWS for an off-grid
    /// endpoint is not what gets written back.</summary>
    [Fact]
    public void TheRoundedTextOfTheUntouchedBox_IsNeverWhatIsCommitted()
    {
        var printed = DotCharter.UI.Controls.FractionalLengthFormat.Format(OffGridStart.DoubleValue);
        Assert.Equal("137.33", printed);

        var span = HandShapeEditorViewModel.ResolveHandShapeSpan(
            OffGridStart, new FractionalPosition(140), "141", startEdited: false);

        Assert.NotEqual(137.33, span!.Value.Start.DoubleValue, 6);
    }

    [Fact]
    public void AZeroOrBackwardsSpan_IsRefused_NotClamped()
    {
        // End at the start: EndPosition is EXCLUSIVE, so that is a broken shape,
        // not a short one.
        Assert.Null(HandShapeEditorViewModel.ResolveHandShapeSpan(
            new FractionalPosition(8), new FractionalPosition(12), "8", startEdited: false));
        Assert.Null(HandShapeEditorViewModel.ResolveHandShapeSpan(
            new FractionalPosition(8), new FractionalPosition(12), "4", startEdited: false));
        Assert.Null(HandShapeEditorViewModel.ResolveHandShapeSpan(
            new FractionalPosition(8), new FractionalPosition(12), "16", startEdited: true));
    }

    [Fact]
    public void GarbageIsRefused()
    {
        Assert.Null(HandShapeEditorViewModel.ResolveHandShapeSpan(
            new FractionalPosition(8), new FractionalPosition(12), "nope", startEdited: false));
        Assert.Null(HandShapeEditorViewModel.ResolveHandShapeSpan(
            new FractionalPosition(8), new FractionalPosition(12), "-4", startEdited: false));
    }
}
