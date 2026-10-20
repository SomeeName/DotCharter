using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Every <c>DataTemplate</c> in <c>Views/**</c> declares its type
///     (2026-08-11): an untyped template opts its whole subtree out of
///     compiled bindings, so a renamed view-model property builds clean and
///     the template silently renders blanks at runtime. Seventeen were bare
///     when this landed; typing them compiled cleanly, which is the proof the
///     check is cheap — and the next renamed property inside one becomes a
///     BUILD error instead of a blank control.
/// </summary>
public sealed class TypedDataTemplateTests
{
    /// <summary>Deliberately empty. An entry here is a subtree without
    /// compile-time binding checks; argue for it in a comment.</summary>
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal);

    [Fact]
    public void EveryDataTemplateInViews_DeclaresItsDataType()
    {
        var viewsRoot = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        var offenders = new List<string>();
        var typed = 0;

        foreach (var file in UiSourceTree.EnumerateFiles(viewsRoot, "*.axaml", SearchOption.AllDirectories))
        {
            if (Exempt.Contains(Path.GetFileName(file))) continue;
            var markup = UiSourceTree.ReadAllText(file);
            typed += Regex.Matches(markup, @"<DataTemplate\s+[^>]*DataType=").Count;
            foreach (Match m in Regex.Matches(markup, @"<DataTemplate\s*>"))
            {
                var line = markup[..m.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}: a bare <DataTemplate> — " +
                              "give it x:DataType so its bindings compile");
            }
        }

        Assert.True(offenders.Count == 0,
            "Untyped DataTemplate(s) — their bindings are unchecked at build time:\n  " +
            string.Join("\n  ", offenders));

        // The scan really saw the catalogue: 100+ typed templates exist.
        Assert.True(typed >= 100, $"only {typed} typed DataTemplates were seen — the scan is broken");
    }
}
