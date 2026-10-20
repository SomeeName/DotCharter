namespace DotCharter.Tests.UI;

/// <summary>
///     No two <c>.axaml</c> files under <c>DotCharter.UI/Views/</c> may share a
///     file name.
///     <para>
///         <c>Perf/UiSweep.AxamlFor</c> pairs a view TYPE with its markup by file
///         name alone, and the markup's <c>x:DataType</c> decides which view model
///         the sweep binds. When the redesigned lobby shipped as a second
///         <c>MultiplayerLobbyView.axaml</c> (2026-09-17) the sweep read the
///         SHIPPED file's <c>x:DataType</c> for the redesign type, bound the live
///         <c>MultiplayerViewModel</c> to a view compiled against
///         <c>IMultiplayerLobby</c>, and every command binding died as a
///         <c>dead-command-binding</c> finding. Renaming fixed it; this keeps the
///         next redesign from repeating it.
///     </para>
/// </summary>
public sealed class ViewFileNamesAreUniqueTests
{
    [Fact]
    public void NoTwoViewAxamlFiles_ShareAFileName()
    {
        var viewsRoot = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        var duplicates = UiSourceTree.EnumerateFiles(viewsRoot, "*.axaml", SearchOption.AllDirectories)
            .GroupBy(Path.GetFileName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(p => Path.GetRelativePath(viewsRoot, p)))}")
            .ToList();

        Assert.True(duplicates.Count == 0,
            "duplicate view file names under DotCharter.UI/Views (the UI sweep pairs a view type " +
            "with its .axaml by file name, so a duplicate binds the wrong view model):\n  " +
            string.Join("\n  ", duplicates));
    }
}
