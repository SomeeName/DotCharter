namespace DotCharter.Tests.UI.Input.Tools;

/// <summary>
///     A drag ends where the pointer was RELEASED, not one throttle window behind
///     it — the same fault the minimap drag carried
///     (<c>fix(minimap): a drag ends where the pointer was released</c>).
///     <para>
///         <c>ToolCoordinator.HandlePointerMoved</c> drops every pointer move
///         inside 16 ms, so the previews built by the last surviving move can be up
///         to a frame old — and <c>OnPointerReleased</c> is what COMMITS those
///         previews. Because the drag delta is grid-snapped, a flick whose final
///         frame crossed a grid line dropped the whole selection on the previous
///         line, silently and as a real undo node.
///     </para>
///     <para>
///         <c>HandShapeInteractionTool</c> already recomputed its delta from the
///         release context; <c>HighwayInteractionTool</c> (every note/chord/hand
///         shape/anchor drag on the highway) and <c>FretAnchorInteractionTool</c>
///         did not. A source lint because all three take a <c>ToolContext</c>,
///         which takes a <c>MainWindowViewModel</c> — the
///         <c>HighwayDragCloneDeferralTests</c> / <c>PointerGestureBoundaryTests</c>
///         precedent.
///     </para>
/// </summary>
public sealed class DragEndsAtTheReleaseTests
{
    [Fact]
    public void TheHighwayDrag_RecomputesFromTheReleasePosition()
    {
        var released = MethodBody(
            Read("DotCharter.UI/Input/Tools/HighwayInteractionTool.cs"),
            "public void OnPointerReleased(PointerReleasedEventArgs e, ToolContext context)");

        Assert.Contains("_dragState.GetDelta(context)", released);
        Assert.Contains("RefreshDragPreviews(", released);

        // …and it must happen BEFORE the previews are read into the commit.
        var recompute = released.IndexOf("RefreshDragPreviews(", StringComparison.Ordinal);
        var read = released.IndexOf("selection.PreviewNotes.ToList()", StringComparison.Ordinal);
        Assert.True(read > recompute,
            "the commit reads the previews before recomputing them — the drop is still a frame behind");
    }

    [Fact]
    public void TheFhpAnchorDrag_RecomputesFromTheReleasePosition()
    {
        var source = Read("DotCharter.UI/Input/Tools/FretAnchorInteractionTool.cs");
        var released = MethodBody(source,
            "public void OnPointerReleased(PointerReleasedEventArgs e, ToolContext context)");

        Assert.Contains("RefreshDragPreviews(context,", released);

        // ONE preview builder, shared with the move — a second copy is how the two
        // paths drift apart again.
        Assert.Single(AllIndexesOf(source, "private void RefreshDragPreviews("));
    }

    /// <summary>The path that was already right, so a refactor cannot quietly
    /// take it back out.</summary>
    [Fact]
    public void TheHandShapeDrag_StillRecomputesFromTheReleasePosition()
    {
        var released = MethodBody(
            Read("DotCharter.UI/Input/Tools/HandShapeInteractionTool.cs"),
            "public void OnPointerReleased(PointerReleasedEventArgs e, ToolContext context)");

        Assert.Contains("GetClampedDelta(context)", released);
    }

    private static List<int> AllIndexesOf(string source, string needle)
    {
        var hits = new List<int>();
        for (var i = source.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            hits.Add(i);
        }

        return hits;
    }

    private static string MethodBody(string source, string signature)
    {
        source = source.Replace("\r\n", "\n");
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' has moved or been renamed — repoint this lint.");
        var next = source.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        var nextPrivate = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        if (nextPrivate >= 0 && (next < 0 || nextPrivate < next)) next = nextPrivate;
        return next < 0 ? source[start..] : source[start..next];
    }

    private static string Read(string relative) =>
        UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
}
