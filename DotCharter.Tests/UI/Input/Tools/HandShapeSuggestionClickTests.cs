namespace DotCharter.Tests.UI.Input.Tools;

/// <summary>
///     A click on a dashed suggested bar accepts THAT bar (#491). The renderer and the hand-shape tool
///     each read the editor's ONE shared <c>HandShapeSuggestionLatch</c> over the same inputs — the level, the View toggle and
///     the history revision — so what a click hits is what was drawn; the tool hit-tests it with the
///     committed bars' own <c>ChartHitTester.HitTestHandShape</c>; Alt picks the arpeggio. A source lint
///     because the tool takes a <c>ToolContext</c>, which takes a <c>MainWindowViewModel</c> (the
///     <see cref="DragEndsAtTheReleaseTests" /> precedent); the acceptance itself is
///     <c>HandShapeSuggestionAcceptorTests</c>.
/// </summary>
public sealed class HandShapeSuggestionClickTests
{
    [Fact]
    public void TheToolAndTheRenderer_ReadTheSuggestionsTheSameWay()
    {
        var tool = Read("DotCharter.UI/Input/Tools/HandShapeInteractionTool.cs");
        var renderer = Read("DotCharter.UI/Controls/ChartRenderer.cs");

        // One latch, the editor's: a latch per reader ran the detector once per reader per edit.
        Assert.DoesNotContain("new HandShapeSuggestionLatch", tool);
        Assert.DoesNotContain("new HandShapeSuggestionLatch", renderer);
        Assert.Contains("ChartEditor.SharedHandShapeSuggestions.Get(", tool);
        Assert.Contains("editor.SharedHandShapeSuggestions.Get(", renderer);
        foreach (var source in new[] { tool, renderer })
            Assert.Contains("HistoryRevision, Environment.TickCount64)", source);
        Assert.Contains("vm.ProjectState.ShowSuggestedHandShapes", tool);
        Assert.Contains("editor.ShowSuggestedHandShapes", renderer);
    }

    /// <summary>The song map's bands (#639) read the suggestions the lane's way too — the shared latch
    /// over the level on screen, the View toggle and the history revision — so a band on the map is
    /// a dashed bar on the lane. The bands' geometry is <c>UI/Controls/SongMapSuggestionMarksTests</c>.</summary>
    [Fact]
    public void TheSongMap_ReadsTheSuggestionsTheLanesWay()
    {
        var map = Read("DotCharter.UI/Controls/MinimapOverviewControl.cs");

        Assert.DoesNotContain("new HandShapeSuggestionLatch", map);
        Assert.Contains("editor.SharedHandShapeSuggestions.Get(", map);
        Assert.Contains("editor.ShowSuggestedHandShapes, editor.HistoryRevision, Environment.TickCount64)", map);
        Assert.Contains("SongMapMarks.CollectSuggestions(", map);
    }

    [Fact]
    public void ALeftClickOnEmptyLane_AcceptsTheHitSuggestion_PlainOrArpeggioWithAlt()
    {
        var pressed = MethodBody(Read("DotCharter.UI/Input/Tools/HandShapeInteractionTool.cs"),
            "public void OnPointerPressed(PointerPressedEventArgs e, ToolContext context)");

        var hit = pressed.IndexOf("HitSuggestion(context)", StringComparison.Ordinal);
        var accept = pressed.IndexOf("_acceptor.Accept(", StringComparison.Ordinal);
        var cleared = pressed.LastIndexOf("selection.SelectedHandShapes.Clear();", StringComparison.Ordinal);
        Assert.True(hit >= 0 && accept > hit, "the press accepts the suggestion it hit");
        Assert.True(cleared > accept, "a click that hits no suggestion still clears the bar selection");
        Assert.Contains("[hit.Suggestion], context.Modifiers.HasFlag(KeyModifiers.Alt))", pressed);
    }

    [Fact]
    public void TheHitTest_IsTheCommittedBarsHitTest()
    {
        var hit = MethodBody(Read("DotCharter.UI/Input/Tools/HandShapeInteractionTool.cs"),
            "private (HandShape Bar, SuggestedHandShape Suggestion)? HitSuggestion(ToolContext context)");

        Assert.Contains("ChartHitTester.HitTestHandShape(context.Position, bars,", hit);
        Assert.Contains("set.Suggestions[bars.IndexOf(hit)]", hit);
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
