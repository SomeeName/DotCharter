using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>InspectorViewModel.SelectionIssues()</c> is MEMOIZED — five bound
///     properties (the header's ●N badge, <c>HasSelectionIssues</c>,
///     <c>SelectionBlocksExport</c>, the field lines and the chord box's rail
///     pips) all read the one materialized list. Every path that changes the
///     SELECTION therefore has to drop it.
///     <para>
///         Two of the three did not. <c>ISelectionService</c> raises
///         <c>PropertyChanged</c> only for the SCALARS <c>SelectedNote</c> /
///         <c>SelectedChord</c>, and it keeps those at
///         <c>collection.FirstOrDefault()</c> — so a Ctrl+click that APPENDS a
///         second note or chord behind the first changes no scalar, raises no
///         property change, and left the memo holding the answer computed for
///         the previous selection. The three <c>Sync*Properties</c> methods ARE
///         the <c>CollectionChanged</c> handlers, and only the hand-shape one
///         invalidated. (The hand-shape sync lives on
///         <c>HandShapeEditorViewModel</c> now; it reaches the inspector as
///         <c>OnHandShapeEditorSynced</c>, the handler of its <c>Synced</c>.)
///     </para>
///     <para>
///         <b>Why this is a source lint and not a behavioural test.</b>
///         <c>InspectorViewModel</c>'s constructor takes twelve required
///         collaborators (three of them view models with their own graphs) and
///         builds a <c>DispatcherTimer</c>, so it cannot be constructed outside
///         a headless Avalonia session — nothing in the default gate builds one,
///         and the headless suite is a separate process that does not run on
///         commit. The invalidation is one call in a known method; this asserts
///         the call is there, with a floor so it cannot pass by finding nothing.
///     </para>
/// </summary>
public class InspectorSelectionIssueMemoTests
{
    private static readonly (string File, string Method)[] Handlers =
    [
        ("InspectorViewModel.Notes.cs", "SyncNoteProperties"),
        ("InspectorViewModel.Chords.cs", "SyncChordProperties"),
        ("InspectorViewModel.cs", "OnHandShapeEditorSynced")
    ];

    [Fact]
    public void EverySelectionSyncHandler_DropsTheSelectionIssueMemo()
    {
        var found = 0;
        var offenders = new List<string>();

        foreach (var (file, method) in Handlers)
        {
            var body = MethodBody(file, method);
            Assert.False(body == null, $"{method} was not found in {file} — repoint this lint.");
            found++;
            if (!body!.Contains("NotifySelectionIssuesChanged()", StringComparison.Ordinal))
                offenders.Add($"{file}:{method}");
        }

        // A floor, so a moved or renamed method turns this red instead of
        // silently checking nothing.
        Assert.Equal(Handlers.Length, found);
        Assert.True(offenders.Count == 0,
            "These selection-change handlers do not invalidate the memoized selection-issue list, "
            + "so the header badge and the chord-box pips keep describing the PREVIOUS selection: "
            + string.Join(", ", offenders));
    }

    /// <summary>The memo is only safe to keep because exactly one field holds
    /// it and exactly one method clears it — if a second clear site appears,
    /// this lint is looking at the wrong thing.</summary>
    [Fact]
    public void TheMemoIsClearedInOnePlaceOnly()
    {
        var source = ReadUiSource("InspectorViewModel.SelectionIssues.cs");
        var clears = Regex.Matches(source, @"_selectionIssues\s*=\s*null").Count;
        Assert.Equal(1, clears);
        Assert.Contains("private void NotifySelectionIssuesChanged()", source, StringComparison.Ordinal);
    }

    private static string? MethodBody(string file, string method)
    {
        var source = ReadUiSource(file);
        var start = source.IndexOf($"private void {method}()", StringComparison.Ordinal);
        if (start < 0) return null;

        var open = source.IndexOf('{', start);
        if (open < 0) return null;

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        return null;
    }

    private static string ReadUiSource(string file)
    {
        var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor", file));
        Assert.True(File.Exists(path), $"{path} does not exist — repoint this lint.");
        return UiSourceTree.ReadAllText(path);
    }
}
