using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI.Rendering;

/// <summary>
///     <para>
///         **Never <c>foreach</c> an <see cref="System.Collections.ObjectModel.ObservableCollection{T}" />
///         from a per-frame 2D layer — walk it INDEXED.**
///         <c>ObservableCollection&lt;T&gt;</c> derives from <c>Collection&lt;T&gt;</c>,
///         which exposes no public struct enumerator, so a <c>foreach</c> binds to
///         <c>IEnumerable&lt;T&gt;.GetEnumerator()</c> and BOXES a
///         <c>List&lt;T&gt;.Enumerator</c> — one allocation per collection per layer
///         per frame, at up to 60 Hz, even when the collection is empty.
///     </para>
///     <para>
///         The rule was already written down twice on the snapshot side — see the
///         comment above <c>PreviewNotes</c> in
///         <c>DotCharter.Rendering/Data/FrameSnapshot.Build.cs</c> (~106-110) and
///         the one on <c>SelectedAnchors</c> in
///         <c>DotCharter.Rendering/Data/SnapshotUpdateContext.cs</c> (~93-96) —
///         and broken anyway, in both places and then again in the UI's own
///         layers: <c>BackgroundLayer.DrawBookmarks</c> and
///         <c>.DrawAnnotations</c> each boxed one per frame over
///         <c>rc.Editor.Bookmarks</c> / <c>.Annotations</c>. Prose did not hold
///         the line, so this is the mechanical half.
///     </para>
///     <para>
///         It resolves LOCAL ALIASES on purpose (<c>var bookmarks =
///         rc.Editor.Bookmarks;</c> followed by <c>foreach (… in bookmarks)</c>),
///         because that is the exact shape both real offenders had — a lint that
///         only matched <c>in rc.Bookmarks</c> would have passed over both of
///         them.
///     </para>
/// </summary>
public sealed class LayerCollectionEnumerationLintTests
{
    /// <summary>
    ///     Files that DECLARE the collections a layer can reach. A member named
    ///     here as <c>ObservableCollection&lt;T&gt;</c> may not be walked with a
    ///     <c>foreach</c> anywhere under <c>Controls/Layers/</c>.
    /// </summary>
    private static IReadOnlyList<string> DeclarationSources(string root)
    {
        var files = new List<string>
        {
            UiSourceTree.Resolve(Path.Combine(root, "DotCharter.UI", "Controls", "Layers", "ChartRenderContext.cs")),
            Path.Combine(root, "DotCharter.Services", "Editing", "Tracks", "TrackEditorContext.cs")
        };
        files.AddRange(UiSourceTree.GetFiles(Path.Combine(root, "DotCharter.Rendering", "Data"), "*.cs"));
        files.AddRange(UiSourceTree.GetFiles(UiSourceTree.Resolve(Path.Combine(root, "DotCharter.UI", "ViewModels", "Editor")),
            "ChartEditorViewModel*.cs"));
        foreach (var f in files) Assert.True(File.Exists(f), $"Declaration source moved: {f}");
        return files;
    }

    private static string LayerRoot(string root) =>
        UiSourceTree.Resolve(Path.Combine(root, "DotCharter.UI", "Controls", "Layers"));

    private static readonly Regex ObservableMember = new(
        @"ObservableCollection\s*<[^>]*>\s*(\w+)\s*(?:=>|=|\{|;)", RegexOptions.Compiled);

    private static readonly Regex ForeachIn = new(
        @"foreach\s*\(\s*[^)]*?\bin\s+([A-Za-z_][\w\.]*)\s*\)", RegexOptions.Compiled);

    [Fact]
    public void NoPerFrameLayerForeachesAnObservableCollection()
    {
        var root = TestPaths.RepoRoot();

        var observableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in DeclarationSources(root))
        {
            foreach (Match m in ObservableMember.Matches(Strip(UiSourceTree.ReadAllText(file))))
                observableNames.Add(m.Groups[1].Value);
        }

        // Non-vacuity floor: if the declarations move or get renamed, the name
        // set collapses and this lint would silently stop looking.
        Assert.True(observableNames.Count >= 8,
            $"Expected the declaration sources to yield >= 8 ObservableCollection members, got " +
            $"{observableNames.Count}: {string.Join(", ", observableNames.OrderBy(n => n))}");
        Assert.Contains("Bookmarks", observableNames);
        Assert.Contains("Annotations", observableNames);

        var layerFiles = UiSourceTree.GetFiles(LayerRoot(root), "*.cs", SearchOption.AllDirectories);
        Assert.True(layerFiles.Length >= 10,
            $"Expected >= 10 layer files under Controls/Layers, found {layerFiles.Length}.");

        var offenders = new List<string>();
        foreach (var file in layerFiles)
        {
            var code = Strip(UiSourceTree.ReadAllText(file));

            // `var bookmarks = rc.Editor.Bookmarks;` — the local now stands for
            // the ObservableCollection member, so foreaching it is the same bug.
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(code,
                         @"\b(?:var|[\w<>,\[\]\.]+)\s+(\w+)\s*=\s*([A-Za-z_][\w\.]*)\s*;"))
            {
                var tail = m.Groups[2].Value.Split('.').Last();
                if (observableNames.Contains(tail)) aliases.Add(m.Groups[1].Value);
            }

            foreach (Match m in ForeachIn.Matches(code))
            {
                var expr = m.Groups[1].Value;
                var tail = expr.Split('.').Last();
                if (!observableNames.Contains(tail) && !aliases.Contains(expr)) continue;
                offenders.Add($"{Path.GetFileName(file)}: foreach (… in {expr})");
            }
        }

        Assert.True(offenders.Count == 0,
            "A per-frame 2D layer walks an ObservableCollection with foreach, which boxes its " +
            "enumerator once per frame. Use an indexed for-loop:\n  " +
            string.Join("\n  ", offenders));
    }

    private static string Strip(string text)
    {
        // Comments first, then string literals — a rule written in prose beside
        // the loop must not read as a use, and neither must a name in a message.
        var noComments = Regex.Replace(
            Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\r\n]*", "");
        return Regex.Replace(noComments, "\"(?:[^\"\\\\\r\n]|\\\\.)*\"", "\"\"");
    }
}
