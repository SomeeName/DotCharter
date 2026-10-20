using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>No view carries a literal colour.</b> Every brush a screen paints with
///     resolves from the token dictionary.
///     <para>
///         The spec says it once and means it — "define these once as a
///         ResourceDictionary … nothing in the UI should carry a literal hex" —
///         and the reason is not tidiness. A hex typed into a view is invisible
///         to every gate this repo has: it builds, it renders, it looks right on
///         the day, and it is then the one pixel that does not move when a token
///         is corrected. The Fire layer's own reconciliation moved eleven token
///         values at once; every literal in a view would have silently stayed
///         behind.
///     </para>
///     <para>
///         Scope is <c>DotCharter.UI/Views/**</c>. The theme dictionaries under
///         <c>Styles/</c> are where colour is DECLARED and are deliberately not
///         scanned; the rack's own material lint (retired with it in v0.43) is the same lint
///         aimed at the one view that is allowed no literals at all.
///     </para>
/// </summary>
public sealed class ViewHardCodedColourTests
{
    /// <summary>Properties that take a brush or a colour.</summary>
    private const string BrushProperties =
        "Background|Foreground|BorderBrush|BoxShadow|Fill|Stroke|Color|" +
        "TextElement.Foreground|SelectionBrush|CaretBrush|PlaceholderForeground";

    private static readonly Regex LiteralColour = new(
        $@"\b(?:{BrushProperties})=""[^""]*#[0-9A-Fa-f]{{3,8}}\b",
        RegexOptions.Compiled);

    /// <summary>XML comments carry hex on purpose — several screen roots quote
    /// the mockup's ground (`#07060c`) to explain why it is NOT painted. A scan
    /// that cannot tell prose from markup flags its own documentation.</summary>
    private static readonly Regex XmlComment = new("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    ///     Every exemption states WHY. An entry with no reason is a hole in the
    ///     lint, not an exception to it — the point of the list is that adding to
    ///     it costs an argument.
    /// </summary>
    /// <summary>
    ///     Empty, and that is the healthy state. The one entry that ever lived
    ///     here was <c>RigWebView.axaml</c>'s browser-slot placeholder, whose
    ///     fill had to equal the hosted HTML page's own background byte for
    ///     byte. The WebView pedalboard experiment has been deleted, so the
    ///     exemption went with it — this guard's sibling case is what noticed.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void NoView_PaintsWithALiteralColour()
    {
        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        Assert.True(Directory.Exists(views), $"Views not found at {views}");

        var scanned = 0;
        var offenders = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (Exempt.ContainsKey(name)) continue;

            scanned++;
            var markup = XmlComment.Replace(UiSourceTree.ReadAllText(file), string.Empty);

            foreach (Match m in LiteralColour.Matches(markup))
                offenders.Add($"{name}:{markup.Take(m.Index).Count(c => c == '\n') + 1}  {m.Value}\"");
        }

        // The scan must not pass by finding no files to read.
        Assert.True(scanned > 20, $"only {scanned} views were scanned — the lint is looking in the wrong place");

        Assert.True(offenders.Count == 0,
            "These views paint with a literal colour instead of a token. A hex in a view is invisible to " +
            "every other gate and is the one pixel that will not move when the token dictionary is " +
            "corrected:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     …and the exemption list itself cannot rot: an entry naming a file that
    ///     no longer exists, or one that no longer needs the exemption, is a hole
    ///     that would silently re-open the whole file to literals.
    /// </summary>
    [Fact]
    public void EveryExemption_NamesAFileThatStillNeedsIt()
    {
        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));

        foreach (var (name, reason) in Exempt)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{name} is exempted with no stated reason");

            var file = UiSourceTree
                .EnumerateFiles(views, name, SearchOption.AllDirectories)
                .FirstOrDefault();

            Assert.True(file != null, $"{name} is exempted but no longer exists — drop the entry");

            var markup = XmlComment.Replace(UiSourceTree.ReadAllText(file!), string.Empty);
            Assert.True(LiteralColour.IsMatch(markup),
                $"{name} is exempted but now carries no literal colour at all — drop the entry, or the " +
                "exemption silently covers whatever is added to that file next.");
        }
    }
}
