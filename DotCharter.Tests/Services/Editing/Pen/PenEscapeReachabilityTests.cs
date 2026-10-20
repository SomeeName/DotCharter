namespace DotCharter.Tests.Services.Editing.Pen;

/// <summary>
///     <c>Esc</c> reverts the pen's in-progress fret entry — but ALL Esc handling
///     lives in one place: the window tunnel claims the key and calls
///     <c>MainWindowViewModel.HandleEscapeKey</c>, whose priority chain ends in
///     "clear the chart selection". The rebindable <c>ClearSelection</c>
///     registration never runs (the tunnel wins), so there is no second route to
///     add and the pen HAS to be a branch of that chain.
///     <para>
///         Source lint, because <c>MainWindowViewModel</c> needs the whole app
///         booted — its own routing tests are <c>*UiPerfSmokeTests</c> and sit
///         outside the default gate. This pins the two things that can silently
///         break: the branch existing at all, and it sitting AHEAD of
///         <c>ClearSelections</c> (behind it, reverting would also drop the
///         target you were typing onto and the pen's entry would survive).
///     </para>
/// </summary>
public sealed class PenEscapeReachabilityTests
{
    [Fact]
    public void TheEscapeChainAsksThePenBeforeItClearsTheSelection()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs")));

        var chainStart = source.IndexOf("public void HandleEscapeKey()", StringComparison.Ordinal);
        Assert.True(chainStart >= 0, "HandleEscapeKey is the only Esc route; it must still exist.");

        var chain = source[chainStart..];
        var penIndex = chain.IndexOf("_pen.RevertPendingEntry()", StringComparison.Ordinal);
        var clearIndex = chain.IndexOf("ChartEditor.Selection.ClearSelections()", StringComparison.Ordinal);

        Assert.True(penIndex >= 0,
            "Esc must revert the keyboard pen's in-progress fret entry — the branch is gone, so the " +
            "digits you just typed can only be taken back with Ctrl+Z.");
        Assert.True(clearIndex >= 0, "The Esc chain must still end in clearing the selection.");
        Assert.True(penIndex < clearIndex,
            "The pen branch must come BEFORE 'clear the selection': behind it, Esc drops the target " +
            "the pen was typing onto and never reaches the revert.");
    }
}
