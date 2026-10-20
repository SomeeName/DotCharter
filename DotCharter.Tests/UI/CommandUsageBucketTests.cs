using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Diagnostics;
using DotCharter.UI.ViewModels.CommandPalette;

namespace DotCharter.Tests.UI;

/// <summary>
///     One verb, one bucket. The menu's click hook counts a <c>[RelayCommand]</c>
///     under its method name (<c>SaveProject</c>); the palette used to count the
///     same command under its catalogue id (<c>file.saveProject</c>), so a verb
///     reached both ways read as two half-used features — and the report had no
///     command denominator at all, so every command ever clicked was listed as
///     "no longer offered" and none as never used. The owner's real report
///     (2026-08-03) had 43 such rows and zero "never used" commands.
/// </summary>
public sealed class CommandUsageBucketTests
{
    private sealed class RecordingTracker : IUsageTracker
    {
        public List<string> Ids { get; } = [];
        public void Record(string kind, string id) => Ids.Add(kind + "/" + id);
        public void RecordDuration(string kind, string id, long milliseconds) { }
        public UsageStatistics Snapshot { get; } = new();
        public void Flush() { }
    }

    [Fact]
    public void APaletteEntryCountsUnderItsUsageIdNotItsCatalogueId()
    {
        var tracker = new RecordingTracker();
        var previous = AppCommand.Tracker;
        AppCommand.Tracker = tracker;
        try
        {
            var command = new AppCommand("file.saveProject", "Save", "File", () => { }, usageId: "SaveProject");
            command.Execute();

            Assert.Equal("SaveProject", command.UsageId);
            Assert.Equal(["command/SaveProject"], tracker.Ids);
        }
        finally
        {
            AppCommand.Tracker = previous;
        }
    }

    [Fact]
    public void WithoutAUsageIdTheCatalogueIdIsTheBucket()
    {
        var command = new AppCommand("transport.playPause", "Play", "Transport", () => { });
        Assert.Equal("transport.playPause", command.UsageId);
    }

    /// <summary>
    ///     The catalogue's <c>FromCommand</c> must derive the usage id from the
    ///     RelayCommand exactly as the click hook does — through
    ///     <c>CommandIdResolver</c> — so the two routes agree by construction.
    /// </summary>
    [Fact]
    public void TheCatalogueResolvesARelayCommandsBucketTheWayTheClickHookDoes()
    {
        var catalog = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs")));
        // The helper body runs up to the next helper's declaration.
        var fromCommand = Regex.Match(catalog, @"AppCommand FromCommand\(.*?AppCommand FromAction\(", RegexOptions.Singleline);

        Assert.True(fromCommand.Success, "CommandCatalog.FromCommand not found.");
        Assert.Contains("CommandIdResolver.Resolve(command", fromCommand.Value);

        // And the resolver really does read the method name off a RelayCommand.
        var relay = new RelayCommand(SaveProject);
        Assert.Equal(nameof(SaveProject), DotCharter.UI.Diagnostics.CommandIdResolver.Resolve(relay, null, null));
    }

    private static void SaveProject() { }

    /// <summary>
    ///     …and a palette row may not RE-IMPLEMENT a shell verb inline, because
    ///     then it cannot resolve that verb's bucket. Three panel toggles —
    ///     Inspector, 3D Preview and Riff Library — were built with
    ///     <c>FromAction(() =&gt; vm.IsXVisible = !vm.IsXVisible)</c> beside four
    ///     neighbours that went through <c>FromCommand</c>, so the same family of
    ///     rows counted half under the verb (<c>ToggleStemMixer</c>) and half
    ///     under a synthetic catalogue id (<c>view.toggleInspector</c>) — and the
    ///     shell's own <c>ToggleInspectorCommand</c> / <c>TogglePreviewCommand</c>
    ///     / <c>TogglePhraseCatalogCommand</c> each had exactly one caller, the
    ///     keybind registrar, while the palette ran a second copy of the same
    ///     one-liner.
    ///     <para>
    ///         The rule is checkable because panel visibility is the shell's own
    ///         toggle-verb family and every member of it HAS a command: no
    ///         <c>Is…Visible</c> flag may be flipped inline in the catalogue.
    ///         (Flags on child view models — <c>vm.Transport.MetronomeEnabled</c>,
    ///         <c>vm.ProjectState.ShowWaveform</c> — are deliberately out of
    ///         scope; those types expose no toggle command.)
    ///     </para>
    /// </summary>
    [Fact]
    public void ThePaletteDoesNotReImplementAPanelToggleInline()
    {
        var catalog = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs")));

        var flips = Regex.Matches(catalog, @"vm\.(Is\w+Visible)\s*=\s*!\s*vm\.\1")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(flips.Count == 0,
            "CommandCatalog flips " + string.Join(", ", flips) + " inline. Route the row through the " +
            "shell's own Toggle…Command so the palette and the keybind run ONE implementation and " +
            "count in ONE bucket.");

        // Non-vacuous: the pattern really does find this shape when it is there.
        Assert.Single(Regex.Matches(
            "list.Add(FromAction(\"x\", \"y\", vw, () => vm.IsInspectorVisible = !vm.IsInspectorVisible));",
            @"vm\.(Is\w+Visible)\s*=\s*!\s*vm\.\1"));

        // …and the four commands those rows must now name are really named.
        foreach (var command in new[]
                 {
                     "vm.ToggleInspectorCommand", "vm.TogglePreviewCommand",
                     "vm.TogglePhraseCatalogCommand", "vm.ToggleSheetMusicCommand"
                 })
        {
            Assert.Contains(command, catalog, StringComparison.Ordinal);
        }
    }

    /// <summary>The report must carry a command denominator, or it cannot name an unused command.</summary>
    [Fact]
    public void TheUsageReportIsBuiltAgainstTheCommandCatalogue()
    {
        var help = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "ViewModels", "Shell", "HelpMenuViewModel.cs")));
        var shell = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs")));
        var known = Regex.Match(help, @"var known = new Dictionary<string, IReadOnlyList<string>>\s*\{.*?\};",
            RegexOptions.Singleline);

        Assert.True(known.Success, "The known-catalogue dictionary in ShowUsageStatistics was not found.");
        Assert.Contains("[UsageKind.Command]", known.Value);
        Assert.Contains("[UsageKind.Dialog]", known.Value);
        Assert.Contains("[UsageKind.Keybind]", known.Value);
        Assert.Contains("CommandUsageIds", known.Value);
        Assert.Contains("Help.CommandUsageIds = () => CommandCatalog.Build(this, _localization).Select(c => c.UsageId)", shell);
    }
}
