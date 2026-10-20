using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Reads an Avalonia style dictionary AS THE APP SEES IT — the file itself
///     plus everything it merges, in merge order.
/// </summary>
/// <remarks>
///     <para>
///         The oversized grammar files were split into a container of
///         <c>&lt;StyleInclude&gt;</c>s over a folder of parts. A source lint
///         that reads <c>Styles/Fire.axaml</c> alone now sees seven include
///         lines and NO selectors, so it would pass by finding nothing — the
///         vacuous-guard failure this repo keeps paying for. Every lint that
///         asks "what does this grammar declare?" goes through here instead.
///     </para>
///     <para>
///         <b>Order is preserved</b>: the container's own text first, then each
///         include in declaration order, depth first. Avalonia resolves in that
///         order and a later dictionary wins, so a lint that reasons about
///         precedence gets the same sequence the app does.
///     </para>
/// </remarks>
public static class StyleFileText
{
    private static readonly Regex Include = new(
        "<(?:StyleInclude|ResourceInclude)\\s+Source=\"avares://(?<assembly>DotCharter(?:\\.Tone\\.UI)?)/(?<path>[^\"]+)\"",
        RegexOptions.Compiled);

    /// <summary>The file's own text followed by every file it merges, in merge order.</summary>
    public static string ReadWithIncludes(string absolutePath) =>
        string.Concat(FilesOf(absolutePath).Select(File.ReadAllText));

    private static readonly Regex ColourToken = new(
        @"<Color\s+x:Key=""(?<key>[^""]+)""\s*>\s*(?<colour>#[0-9A-Fa-f]+)\s*</Color>", RegexOptions.Compiled);

    private static readonly Regex Brush = new(
        @"<SolidColorBrush\s+x:Key=""(?<key>[^""]+)""\s+Color=""(?<colour>[^""]+)""\s*/>", RegexOptions.Compiled);

    private static readonly Regex Indirect = new(
        @"^\{(?:Dynamic|Static)Resource\s+(?<key>[^}]+)\}$", RegexOptions.Compiled);

    /// <summary>
    ///     Every <c>SolidColorBrush</c> the file and its merges declare, by key,
    ///     resolved to a literal colour.
    ///     <para>
    ///         A brush states its colour one of two ways, and a lint that knows
    ///         only the first reports every token of the second as "no longer
    ///         declared": inline (<c>Color="#AARRGGBB"</c>), or through a
    ///         <c>Color</c> token of its own
    ///         (<c>Color="{DynamicResource FireGroundColor}"</c>), which is what
    ///         makes a layer themeable — <c>ThemeService</c> writes <c>*Color</c>
    ///         keys. The app's palette has always used the second; the Fire layer
    ///         moved to it on 2026-09-20. Brushes whose token is declared
    ///         elsewhere are omitted rather than guessed at.
    ///     </para>
    /// </summary>
    public static Dictionary<string, Avalonia.Media.Color> BrushesWithIncludes(string absolutePath)
    {
        var markup = Regex.Replace(
            ReadWithIncludes(absolutePath), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        var colours = new Dictionary<string, Avalonia.Media.Color>(StringComparer.Ordinal);
        foreach (Match m in ColourToken.Matches(markup))
        {
            if (Avalonia.Media.Color.TryParse(m.Groups["colour"].Value, out var c))
                colours[m.Groups["key"].Value] = c;
        }

        var found = new Dictionary<string, Avalonia.Media.Color>(StringComparer.Ordinal);
        foreach (Match m in Brush.Matches(markup))
        {
            var value = m.Groups["colour"].Value;
            var indirect = Indirect.Match(value);
            if (indirect.Success)
            {
                if (colours.TryGetValue(indirect.Groups["key"].Value.Trim(), out var resolved))
                    found[m.Groups["key"].Value] = resolved;
            }
            else if (Avalonia.Media.Color.TryParse(value, out var literal))
            {
                found[m.Groups["key"].Value] = literal;
            }
        }

        return found;
    }

    /// <summary>
    ///     Every element of the file and of everything it merges, in merge order
    ///     — the XML counterpart of <see cref="ReadWithIncludes" />.
    ///     <para>
    ///         A container of <c>&lt;StyleInclude&gt;</c>s is not ONE document,
    ///         so <c>XDocument.Load</c> on it parses perfectly and finds nothing
    ///         the grammar declares. Matches <c>XDocument.Descendants()</c>
    ///         exactly, root element included.
    ///     </para>
    /// </summary>
    public static IEnumerable<XElement> ElementsWithIncludes(string absolutePath) =>
        FilesOf(absolutePath).SelectMany(f => XDocument.Load(f).Descendants());

    /// <summary>
    ///     The file, then each file it merges, depth first in declaration order.
    ///     Absolute paths, so a caller can exclude the whole set (a grammar's own
    ///     parts are DECLARERS, never consumers of themselves).
    /// </summary>
    public static IReadOnlyList<string> FilesOf(string absolutePath)
    {
        var repoRoot = RepoRootOf(absolutePath);
        var seen = new List<string>();
        Walk(Path.GetFullPath(absolutePath));
        return seen;

        void Walk(string path)
        {
            if (seen.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
            seen.Add(path);

            foreach (Match m in Include.Matches(File.ReadAllText(path)))
            {
                var child = Path.GetFullPath(Path.Combine(
                    repoRoot, ProjectOf(m.Groups["assembly"].Value),
                    m.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar)));
                // A view may include a grammar that lives outside this project's
                // reach in some future layout; a missing file is not this
                // helper's fault to report, and the lints below all assert on
                // content they DID find.
                if (File.Exists(child)) Walk(child);
            }
        }
    }

    /// <summary>
    ///     The project folder an <c>avares://</c> assembly name is relative to. The
    ///     app assembly is named <c>DotCharter</c> (project DotCharter.UI); the design
    ///     system and the rig moved to <c>DotCharter.Tone.UI</c>, whose assembly and
    ///     project share the name.
    /// </summary>
    private static string ProjectOf(string assembly) =>
        assembly == "DotCharter" ? "DotCharter.UI" : assembly;

    private static string RepoRootOf(string absolutePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(absolutePath));
        while (dir is not null && !File.Exists(Path.Combine(dir, "DotCharter.sln")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException(
            $"'{absolutePath}' is not inside the repository, so its avares:// includes cannot be resolved");
    }
}
