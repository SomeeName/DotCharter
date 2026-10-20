using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The FEHLER header's severity dot.
///     <para>
///         It used to bind <c>IssueGroups[0].Severity</c>, and
///         <c>InspectorViewModel.RebuildIssueGroups</c> <c>Clear()</c>s that
///         collection on every recompute. So in the all-clear state — and in the
///         window between the clear and the first add on every single refresh —
///         the indexer resolved against an empty list and Avalonia logged "Index
///         was out of range" instead of pushing a value. The headless sweep sees
///         it on both <c>InspectorView</c> and <c>IssuesInspectorView</c> and
///         classes it <c>binding-error</c>, explicitly not the benign
///         <c>null-source-binding</c> class it drops.
///     </para>
///     <para>
///         The dot's <c>Background</c> was then left UNSET, which is not the same
///         as the converter's grey fallback: a binding that never delivers a value
///         never reaches the converter at all.
///     </para>
/// </summary>
public sealed class IssueDotSeverityTests
{
    [Fact]
    public void TheAllClearState_HasASeverityAndItIsNotTheDangerOne()
    {
        Assert.Equal(ArrangementIssueSeverity.Warning, IssueGrouping.TopSeverity(Array.Empty<InspectorIssueGroup>()));
        Assert.Equal(ArrangementIssueSeverity.Warning, IssueGrouping.TopSeverity(Array.Empty<ArrangementIssue>()));
    }

    [Fact]
    public void TheWorstBucketWins()
    {
        var groups = IssueGrouping.Build([Warning(), Important()]);

        // Build orders Important first, so this is also a guard that the two
        // stay consistent: the dot must show the severity of the group the user
        // reads at the top of the list.
        Assert.Equal(ArrangementIssueSeverity.Important, groups[0].Severity);
        Assert.Equal(ArrangementIssueSeverity.Important, IssueGrouping.TopSeverity(groups));
    }

    [Fact]
    public void AWarningOnlyChart_ReportsTheWarningSeverity()
    {
        Assert.Equal(ArrangementIssueSeverity.Warning,
            IssueGrouping.TopSeverity(IssueGrouping.Build([Warning()])));
    }

    /// <summary>
    ///     The wiring half. <c>TopSeverity</c> being right buys nothing if the
    ///     view model does not recompute it where the collection is rebuilt, or
    ///     if the markup goes back to indexing the collection.
    /// </summary>
    [Fact]
    public void TheDotBindsTheProperty_AndTheProperty_IsRecomputedWhereTheGroupsAre()
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor",
            "Inspector", "IssuesInspectorView.axaml")));
        var noComments = Regex.Replace(markup, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        var dot = Regex.Match(noComments, @"<Border[^>]*Classes=""IssueDot""[\s\S]*?/>");
        Assert.True(dot.Success, "IssuesInspectorView no longer has a Border.IssueDot — this guard is blind");
        Assert.Contains("Binding Issues.TopIssueSeverity", dot.Value, StringComparison.Ordinal);

        var rebuild = BodyOf(InspectorSource(), "private void RebuildIssueGroups()");
        Assert.Contains("TopIssueSeverity = IssueGrouping.TopSeverity", rebuild, StringComparison.Ordinal);
    }

    /// <summary>
    ///     …and the general form of the fault, because this is the SECOND time it
    ///     has shipped (the v0.43 Song-inspector bug was the first). A
    ///     <c>{Binding Collection[0].Something}</c> is only ever correct while the
    ///     collection is non-empty, and every one of these collections is rebuilt
    ///     by clearing it.
    /// </summary>
    [Fact]
    public void NoViewBindsIntoACollectionByIndex()
    {
        var offenders = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(
                     UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views")), "*.axaml", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(UiSourceTree.ReadAllText(file),
                     @"\{\s*(?:Reflection)?Binding\s+(?<path>[A-Za-z_][A-Za-z0-9_.]*\[\d+\][A-Za-z0-9_.]*)"))
            {
                offenders.Add($"{Path.GetFileName(file)}: {{Binding {m.Groups["path"].Value}}}");
            }
        }

        Assert.True(offenders.Count == 0,
            "views bind into a collection by index. Every such collection is refilled by Clear() + Add(), so " +
            "the binding resolves against an empty list on every refresh and in the empty state — Avalonia " +
            "logs \"Index was out of range\" and the target property is left UNSET (the converter is never " +
            "reached, so its fallback does not apply). Expose the value as a property instead:\n  " +
            string.Join("\n  ", offenders));
    }

    // Severity is DERIVED from the issue type (ArrangementIssueType.GetSeverity),
    // so these are the same two representatives IssueGroupingTests uses.
    private static ArrangementIssue Important() => new(ArrangementIssueType.FirstPhraseNotEmpty, "i");

    private static ArrangementIssue Warning() => new(ArrangementIssueType.NoEndPhrase, "w");

    /// <summary>The whole <c>InspectorViewModel</c>, which is a set of topic
    /// partials, plus its Issues tab — reading only the root would make this guard
    /// blind the day a member moves between them (<c>RebuildIssueGroups</c> lives in
    /// <c>Inspector/InspectorIssuesViewModel.cs</c> since #659).</summary>
    private static string InspectorSource()
    {
        var dir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor"));
        var files = UiSourceTree.GetFiles(dir, "InspectorViewModel*.cs").ToList();
        Assert.NotEmpty(files);
        // #659: the Issues tab is its own view model, composed by the inspector.
        var issues = UiSourceTree.Resolve(Path.Combine(dir, "Inspector", "InspectorIssuesViewModel.cs"));
        Assert.True(File.Exists(issues), $"{issues} does not exist — repoint this lint.");
        files.Add(issues);
        return string.Join("\n", files.Select(UiSourceTree.ReadAllText));
    }

    private static string BodyOf(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"InspectorViewModel no longer declares '{signature}'.");

        var open = src.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[open..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }
}
