using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     Static perf audit 2026-08-29 P1 #12 and #17 — what the inspector does
///     with a fresh issue list, which arrives on the 150 ms debounce after every
///     history step.
///     <para>
///         #17: the issue list and <c>IssueGroups</c> were refilled Clear + N
///         Adds into nested non-virtualizing <c>ItemsControl</c>s, and the groups
///         were rebuilt whether or not the FEHLER tab was showing. Both are
///         <c>BulkObservableCollection</c>s now (one Reset), and the groups —
///         bound only by that tab — go stale while it is hidden and are rebuilt
///         when it opens. The header dot reads its severity from the issues
///         directly so it never waits for the groups.
///     </para>
///     <para>
///         #12: the five selection-finding properties each re-ran an
///         Issues × Selection iterator with <c>List.Contains</c>, and
///         <c>HasSelectionIssues</c> re-ran <c>SelectionIssueCount</c>'s
///         enumeration a second time. One materialized list per notification
///         now, over a reference set of the selection.
///     </para>
///     Source lints over the inspector and its Issues tab (<c>InspectorIssuesViewModel</c>,
///     #659 — whose recompute is also tested directly by <c>InspectorIssuesViewModelTests</c>);
///     the one pure piece (the severity overload) is tested behaviourally.
/// </summary>
public sealed class InspectorIssueRebuildTests
{
    [Fact]
    public void TheIssueList_IsReplacedInOneReset_NotClearedAndRefilled()
    {
        var src = InspectorSource();
        Assert.Contains("BulkObservableCollection<ArrangementIssue> Items { get; }", src);
        Assert.Contains("BulkObservableCollection<InspectorIssueGroup> IssueGroups { get; }", src);

        var recompute = BodyOf(src, "internal void RecomputeIssues()");
        Assert.Contains("Items.ResetWith(results)", recompute);
        Assert.DoesNotContain("Items.Clear()", recompute);
        Assert.DoesNotContain("Items.Add(", recompute);
        Assert.DoesNotContain("IssueGroups.Clear()", src);
        Assert.DoesNotContain("IssueGroups.Add(", src);
    }

    [Fact]
    public void TheSeverityGroups_WaitForTheTab_AndAreRebuiltWhenItOpens()
    {
        var src = InspectorSource();
        var rebuild = BodyOf(src, "private void RebuildIssueGroups()");
        Assert.Contains("if (_host?.IsIssuesTabSelected ?? true) SyncIssueGroups();", rebuild);
        Assert.Contains("else _issueGroupsStale = true;", rebuild);
        // The selection findings and the work queue follow the fresh list through the host.
        Assert.Contains("_host?.OnIssuesRecomputed();", rebuild);
        var recomputed = BodyOf(src, "void IInspectorIssuesHost.OnIssuesRecomputed()");
        Assert.Contains("NotifySelectionIssuesChanged();", recomputed);
        // The dot does not depend on the groups being built.
        Assert.Contains("TopIssueSeverity = IssueGrouping.TopSeverity(Items);", rebuild);

        var tabChange = BodyOf(src, "partial void OnSelectedTabIndexChanged(int value)");
        Assert.Contains("if (value == IssuesTabIndex) Issues.EnsureIssueGroupsCurrent();", tabChange);

        var ensure = BodyOf(src, "internal void EnsureIssueGroupsCurrent()");
        Assert.Contains("if (_issueGroupsStale) SyncIssueGroups();", ensure);
    }

    [Fact]
    public void TheSelectionFindings_AreMaterializedOnce_OverAReferenceSet()
    {
        var src = InspectorSource();
        var selection = BodyOf(src, "private IReadOnlyList<ArrangementIssue> SelectionIssues()");
        Assert.Contains("if (_selectionIssues != null) return _selectionIssues;", selection);
        Assert.Contains("new HashSet<object>(ReferenceEqualityComparer.Instance)", selection);
        Assert.DoesNotContain("SelectedNotes.Contains(", selection);
        Assert.DoesNotContain("yield return", selection);

        // HasSelectionIssues reads the list, not the count property that would
        // re-run it.
        Assert.Contains("public bool HasSelectionIssues => SelectionIssues().Count > 0;", src);
        // The badge counts the FOLDED rows (#117), which are themselves derived
        // from the one materialized list, keyed to its identity.
        Assert.Contains("public int SelectionIssueCount => SelectionFieldIssues.Count;", src);
        Assert.Contains("if (!ReferenceEquals(_foldedFrom, issues))", src);

        // Both invalidation points: a fresh issue list, and a selection change.
        var notify = BodyOf(src, "private void NotifySelectionIssuesChanged()");
        Assert.Contains("_selectionIssues = null;", notify);
        var selectionChanged = BodyOf(src, "private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)");
        Assert.Contains("_selectionIssues = null;", selectionChanged);
    }

    /// <summary>
    ///     …and the THIRD input has its own invalidation, because the two above
    ///     do not reach it.
    ///     <para>
    ///         <c>SelectionIssues()</c> folds <c>SelectedHandShapes</c> into its
    ///         reference set — an <c>ArrangementIssue</c> can back-reference a
    ///         hand shape, and the Shape/Span cards are where such a finding
    ///         belongs. But the memo was cleared from exactly two places: a fresh
    ///         issue list, and <c>ISelectionService.PropertyChanged</c>. That
    ///         service raises <c>PropertyChanged</c> for the SCALARS
    ///         <c>SelectedNote</c>/<c>SelectedChord</c> only
    ///         (<c>SelectionService</c>'s ctor: every other collection raises
    ///         <c>SelectionChanged</c>, an event, and nothing else), so selecting
    ///         a hand shape left the memo holding whatever it computed for the
    ///         PREVIOUS selection: the header badge read 0 over a shape carrying
    ///         a finding, and stayed 0 until something else invalidated it.
    ///     </para>
    ///     <para>
    ///         Four links, so a repoint of any one of them fails rather than
    ///         going quiet: the memo READS the collection, the ctor subscribes
    ///         the collection to <c>HandShapeEditorViewModel.SyncHandShapeProperties</c>,
    ///         that sync raises <c>Synced</c>, and the inspector's handler for it
    ///         invalidates.
    ///     </para>
    /// </summary>
    [Fact]
    public void AHandShapeSelection_InvalidatesTheSelectionFindings()
    {
        var src = InspectorSource();

        var selection = BodyOf(src, "private IReadOnlyList<ArrangementIssue> SelectionIssues()");
        Assert.Contains("SelectedHandShapes", selection);

        Assert.Contains(
            "_chartEditor.Selection.SelectedHandShapes.CollectionChanged += (_, _) => HandShapeEditor.SyncHandShapeProperties();",
            src);
        Assert.Contains("HandShapeEditor.Synced += OnHandShapeEditorSynced;", src);

        var editor = SourceText.StripCommentLines(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor", "Inspector", "HandShapeEditorViewModel.cs"))));
        Assert.Contains("Synced?.Invoke();", BodyOf(editor, "internal void SyncHandShapeProperties()"));

        var handler = BodyOf(src, "private void OnHandShapeEditorSynced()");
        Assert.Contains("NotifySelectionIssuesChanged();", handler);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(0, 2)]
    [InlineData(4, 5)]
    public void TheIssueOverload_AgreesWithTheGroupsOverload(int warnings, int importants)
    {
        var issues = new List<ArrangementIssue>();
        for (var i = 0; i < warnings; i++) issues.Add(new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "w"));
        for (var i = 0; i < importants; i++) issues.Add(new ArrangementIssue(ArrangementIssueType.FirstPhraseNotEmpty, "i"));
        // Interleave so "first wins" cannot pass by accident.
        issues = issues.OrderBy(i => i.Message).ThenBy(i => i.GetHashCode()).ToList();

        Assert.Equal(
            IssueGrouping.TopSeverity(IssueGrouping.Build(issues)),
            IssueGrouping.TopSeverity(issues));
    }

    private static string InspectorSource()
    {
        var dir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor"));
        var files = UiSourceTree.GetFiles(dir, "InspectorViewModel*.cs").ToList();
        Assert.NotEmpty(files);
        // #659: the Issues tab is its own view model, composed by the inspector.
        var issues = UiSourceTree.Resolve(Path.Combine(dir, "Inspector", "InspectorIssuesViewModel.cs"));
        Assert.True(File.Exists(issues), $"{issues} does not exist — repoint this lint.");
        files.Add(issues);
        return Regex.Replace(string.Join("\n", files.Select(UiSourceTree.ReadAllText)), @"^\s*//.*$", "", RegexOptions.Multiline);
    }

    private static string BodyOf(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"InspectorViewModel no longer declares '{signature}' — repoint this lint.");

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
