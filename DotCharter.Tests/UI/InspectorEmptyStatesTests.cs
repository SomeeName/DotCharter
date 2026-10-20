using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     What the inspector's tabs say when they have nothing to show (inspector review, 2026-09-24).
///     The Issues tab with nothing to report said "No issues detected." twice, one line apart: the
///     header's summary and the empty state under it. The header names the tab when there is
///     nothing to count, and Auto-Fix only shows when there is something to fix (the Edit menu's
///     "Auto-fix chart" stays for a whole-chart pass). Inspector review, 2026-09-24.
/// </summary>
public sealed class InspectorEmptyStatesTests
{
    [Fact]
    public void WithNoIssues_TheHeaderDoesNotRepeatTheEmptyState()
    {
        var vm = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor", "Inspector", "InspectorIssuesViewModel.cs")));
        var summary = Regex.Match(vm, @"public string IssueSummary => IssueCount == 0\s*\?\s*_localization\[""(?<key>\w+)""\]");
        Assert.True(summary.Success, "IssueSummary's zero branch is no longer one resource key; re-read this test");

        var view = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "Inspector", "IssuesInspectorView.axaml")));
        var empty = Regex.Match(view, @"Classes=""InspectorEmpty""[\s\S]*?Loc (?<key>\w+)\}");
        Assert.True(empty.Success, "the Issues tab has no empty state any more");
        Assert.NotEqual(empty.Groups["key"].Value, summary.Groups["key"].Value);
    }

    [Fact]
    public void AutoFix_ShowsOnlyWithSomethingToFix()
    {
        var view = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "Inspector", "IssuesInspectorView.axaml")));
        var button = Regex.Match(view, @"<Button[^>]*Command=""\{Binding Issues\.AutoFixIssuesCommand\}""[^>]*>");
        Assert.True(button.Success, "the Issues header has no Auto-Fix button");
        Assert.Contains(@"IsVisible=""{Binding Issues.IssueCount}""", button.Value, StringComparison.Ordinal);
    }

    /// <summary>The Song tab's Tones card was an empty box on an arrangement without tone changes.</summary>
    [Fact]
    public void TheSongTabsTonesCard_SaysWhenItIsEmpty()
    {
        var view = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "Inspector", "SongInspectorView.axaml")));
        // #601: the Tones SECTION of the tab's rail, up to its list.
        var card = Regex.Match(view, @"x:Name=""SongTonesBody""[\s\S]*?<ItemsControl ItemsSource=""\{Binding Tones\}""");
        Assert.True(card.Success, "the Tones section is gone");
        Assert.Contains(@"IsVisible=""{Binding !Tones.Count}""", card.Value, StringComparison.Ordinal);
        Assert.Contains("Loc Inspector_Tones_None", card.Value, StringComparison.Ordinal);
    }
}
