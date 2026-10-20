using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     #141: <see cref="SurfaceShowTiming" /> puts the cost of an Inspector tab
///     switch and a hosted dialog open into the app log, so the owner's next report
///     of a stutter carries a number. A pure sink is easy to leave unwired, so this
///     pins the no-sink / bad-sink contract and lints that both callers begin a
///     timing and the composition root listens (comments stripped).
/// </summary>
[Collection("SurfaceShowTiming")]
public sealed class SurfaceShowTimingTests
{
    [Fact]
    public void Record_WithoutASink_IsANoOp_AndABadSinkNeverThrows()
    {
        var before = SurfaceShowTiming.Sink;
        try
        {
            SurfaceShowTiming.Sink = null;
            SurfaceShowTiming.Record("Inspector tab 6", 40);

            SurfaceShowTiming.Sink = (_, _) => throw new InvalidOperationException("sink broke");
            SurfaceShowTiming.Record("Inspector tab 6", 40);

            var seen = new List<(string, double)>();
            SurfaceShowTiming.Sink = (n, ms) => seen.Add((n, ms));
            SurfaceShowTiming.Record("Dialog IToneDesignerScreen", 120);
            Assert.Equal([("Dialog IToneDesignerScreen", 120.0)], seen);
        }
        finally
        {
            SurfaceShowTiming.Sink = before;
        }
    }

    /// <summary>The log line names the surface, the keys in the burst and the rows it settled on.</summary>
    [Fact]
    public void ASearchTiming_IsNamedByItsSurfaceKeysAndRows()
    {
        Assert.Equal("Library search (1 key, 71 rows)", SearchSettleTiming.Name("Library", 1, 71));
        Assert.Equal("Song Library search (3 keys, 0 rows)", SearchSettleTiming.Name("Song Library", 3, 0));
    }

    [Fact]
    public void TheTabsAndTheDialogHostBegin_AndTheAppListens()
    {
        static string Read(params string[] parts) =>
            SourceText.StripComments(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine([TestPaths.RepoRoot(), .. parts]))));

        Assert.Contains("SurfaceShowTiming.Begin(", Read("DotCharter.UI", "Views", "Editor", "InspectorView.axaml.cs"));
        Assert.Contains("SurfaceShowTiming.BeginAt(", Read("DotCharter.UI", "Views", "Shell", "DialogHost.cs"));

        // #180: opening the Library from the shell, and a search keystroke in both song lists.
        Assert.Contains("SurfaceShowTiming.Begin(", Read("DotCharter.UI", "Views", "Shell", "MainWindowPrewarm.cs"));
        Assert.Contains("new SearchSettleTiming(\"Library\", LibrarySearchBox, LibraryRows)",
            Read("DotCharter.UI", "Views", "Library", "LibraryView.axaml.cs"));
        Assert.Contains("new SearchSettleTiming(\"Song Library\", SongSelectSearchBox, SongListBox)",
            Read("DotCharter.UI", "Views", "PlayMode", "Screens", "SongSelectView.axaml.cs"));
        Assert.Contains("SurfaceShowTiming.Record(", Read("DotCharter.UI", "Views", "Shell", "SearchSettleTiming.cs"));

        var app = string.Concat(UiSourceTree.GetFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "App*.cs")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => SourceText.StripComments(UiSourceTree.ReadAllText(f))));
        Assert.Contains("SurfaceShowTiming.Sink =", app);
    }
}
