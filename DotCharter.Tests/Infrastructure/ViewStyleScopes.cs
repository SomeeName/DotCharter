using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The VIEW-SCOPED style grammars and the views each one reaches, derived from the
///     markup (#242).
///     <para>
///         A style in a view's own <c>&lt;UserControl.Styles&gt;</c> — inline, or through
///         a <c>StyleInclude</c> there — applies only to controls inside that view: its
///         markup, the views it declares as elements, the views a type it declares
///         builds in code (<c>RigModuleEditorView</c> declares
///         <c>RigEditorTemplateSelector</c>, which builds the family editors), the
///         template dictionaries it merges (<c>RigGearTileTemplate</c>), and markup that
///         another file nests INSIDE an instance of the view (<c>ToneDesignPanel</c>'s
///         blocks in <c>ToneDesignCorePanel.ExtrasTemplate</c>). A class declared only
///         there and worn anywhere else is a silent no-op, so the lints ask this type
///         instead of guessing.
///     </para>
/// </summary>
internal static class ViewStyleScopes
{
    private static readonly Regex NewView = new(@"\bnew\s+(?<t>[A-Z][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

    private static readonly Lazy<Model> Instance = new(Build);

    /// <summary>View name → its .axaml (views, windows and resource dictionaries).</summary>
    public static IReadOnlyDictionary<string, string> Views => Instance.Value.Views;

    /// <summary>
    ///     View name → the (element, class) pairs its own Styles declare, in its markup
    ///     and in every file it <c>StyleInclude</c>s there. Views without own styles are absent.
    /// </summary>
    public static IReadOnlyDictionary<string, HashSet<(string Element, string Class)>> Declared => Instance.Value.Declared;

    /// <summary>Whether some OTHER view's scope reaches <paramref name="file" /> — i.e. its host is
    /// derivable from the markup. A view hosted only by code this type does not read
    /// (<c>DialogHost</c>, a <c>ViewLocator</c> data template) is not.</summary>
    public static bool HasKnownHost(string file)
    {
        var full = Path.GetFullPath(file);
        return ScopeFiles.Any(s => !string.Equals(Views.GetValueOrDefault(s.Key), full, StringComparison.OrdinalIgnoreCase) && s.Value.Contains(full));
    }

    /// <summary>View name → the files its styles reach (itself included).</summary>
    public static IReadOnlyDictionary<string, HashSet<string>> ScopeFiles => Instance.Value.Scope;

    /// <summary>Every <c>StyleInclude</c>d grammar file, by the views that include it from their own Styles.</summary>
    public static IReadOnlyDictionary<string, List<string>> IncludedBy => Instance.Value.IncludedBy;

    /// <summary>
    ///     Whether an element of <paramref name="file" /> sits inside <paramref name="view" />:
    ///     the file is in the view's scope, or the element is nested inside an instance of
    ///     the view in that file.
    /// </summary>
    public static bool Reaches(string view, string file, XElement element) =>
        ScopeFiles.TryGetValue(view, out var files) && files.Contains(Path.GetFullPath(file))
        || element.Ancestors().Any(a => a.Name.LocalName == view);

    /// <summary>
    ///     Whether <paramref name="hostAxaml" /> hosts <paramref name="viewName" /> through
    ///     code: a type it declares as an element constructs the view.
    /// </summary>
    public static bool BuildsInCode(string hostAxaml, string viewName) =>
        Instance.Value.CodeChildren.TryGetValue(Path.GetFullPath(hostAxaml), out var built) && built.Contains(viewName);

    private sealed record Model(
        Dictionary<string, string> Views,
        Dictionary<string, HashSet<(string Element, string Class)>> Declared,
        Dictionary<string, HashSet<string>> Scope,
        Dictionary<string, List<string>> IncludedBy,
        Dictionary<string, HashSet<string>> CodeChildren);

    private static Model Build()
    {
        var root = TestPaths.RepoRoot();
        var views = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in UiSourceTree.EnumerateFiles(Path.Combine(root, "DotCharter.UI", "Views"), "*.axaml", SearchOption.AllDirectories))
        {
            var rootName = XDocument.Parse(File.ReadAllText(file)).Root!.Name.LocalName;
            if (rootName is "UserControl" or "Window" or "ResourceDictionary")
                views[Path.GetFileNameWithoutExtension(file)] = Path.GetFullPath(file);
        }

        var sources = Directory.EnumerateFiles(Path.Combine(root, "DotCharter.UI"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "DotCharter.Tone.UI"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal)
            .ToDictionary(g => g.Key!, g => g.First(), StringComparer.Ordinal);

        var children = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var codeChildren = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var declared = new Dictionary<string, HashSet<(string, string)>>(StringComparer.Ordinal);
        var includedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var docs = views.ToDictionary(kv => kv.Key, kv => XDocument.Parse(File.ReadAllText(kv.Value)), StringComparer.Ordinal);
        foreach (var (name, file) in views)
        {
            var doc = docs[name];
            var names = doc.Descendants().Select(e => e.Name.LocalName).ToHashSet(StringComparer.Ordinal);
            var built = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in names.Where(n => !views.ContainsKey(n) && sources.ContainsKey(n)))
                built.UnionWith(NewView.Matches(File.ReadAllText(sources[type])).Select(m => m.Groups["t"].Value).Where(views.ContainsKey));

            built.Remove(name);
            codeChildren[file] = built;
            var merged = Regex.Matches(File.ReadAllText(file), @"<ResourceInclude\s+Source=""avares://[^/]+/(?<p>[^""]+)""")
                .Select(m => Path.GetFileNameWithoutExtension(m.Groups["p"].Value)).Where(views.ContainsKey);
            children[name] = names.Where(n => n != name && views.ContainsKey(n)).Concat(built).Concat(merged)
                .ToHashSet(StringComparer.Ordinal);

            var own = doc.Root!.Elements().Where(e => e.Name.LocalName.EndsWith(".Styles", StringComparison.Ordinal)).ToList();
            if (own.Count == 0) continue;
            var pairs = new HashSet<(string, string)>();
            foreach (var styles in own)
            {
                AddPairs(styles, pairs);
                foreach (var include in styles.Descendants().Where(e => e.Name.LocalName == "StyleInclude"))
                {
                    var path = AvaresPath(root, include.Attribute("Source")?.Value);
                    if (path is null) continue;
                    if (!includedBy.TryGetValue(path, out var list)) includedBy[path] = list = [];
                    list.Add(name);
                    // …and everything that grammar merges in turn (DialogStyles → its parts)
                    foreach (var part in StyleFileText.FilesOf(path))
                        AddPairs(XDocument.Parse(File.ReadAllText(part)).Root!, pairs);
                }
            }

            declared[name] = pairs;
        }

        var nestedIn = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var instance in docs.Values.SelectMany(d => d.Descendants()).Where(e => views.ContainsKey(e.Name.LocalName)))
        {
            if (!nestedIn.TryGetValue(instance.Name.LocalName, out var set)) nestedIn[instance.Name.LocalName] = set = new(StringComparer.Ordinal);
            set.UnionWith(instance.Descendants().Select(e => e.Name.LocalName).Where(views.ContainsKey));
        }

        var scope = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var name in views.Keys)
        {
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>([name]);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                if (!reached.Add(next)) continue;
                foreach (var kid in children[next]) stack.Push(kid);
            }

            // views nested INSIDE an instance of this view elsewhere (ExtrasTemplate blocks)
            var more = new Stack<string>(nestedIn.TryGetValue(name, out var inside) ? inside : []);
            while (more.Count > 0)
            {
                var n = more.Pop();
                if (!reached.Add(n)) continue;
                foreach (var kid in children[n]) more.Push(kid);
            }

            scope[name] = reached.Select(v => views[v]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return new Model(views, declared, scope, includedBy, codeChildren);
    }

    /// <summary>Every (element, class) pair the selectors under <paramref name="container" />
    /// name; a ControlTheme's <c>^</c> reads as its target type.</summary>
    public static HashSet<(string Element, string Class)> SelectorPairs(XElement container)
    {
        var pairs = new HashSet<(string, string)>();
        AddPairs(container, pairs);
        return pairs;
    }

    private static void AddPairs(XElement styles, HashSet<(string, string)> pairs)
    {
        foreach (var style in styles.Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            var target = style.Ancestors().FirstOrDefault(a => a.Name.LocalName == "ControlTheme")?.Attribute("TargetType")?.Value.Split(':')[^1];
            var selector = style.Attribute("Selector")?.Value;
            if (selector is null) continue;
            if (target is not null) selector = selector.Replace("^", target, StringComparison.Ordinal);
            foreach (Match part in Regex.Matches(Regex.Replace(selector, @":is\((?<el>[A-Za-z|]+)\)", "${el}"),
                         @"(?:[A-Za-z]+\|)?(?<el>[A-Za-z][A-Za-z0-9]*)(?<classes>(?:\.[A-Za-z0-9_]+)+)"))
            {
                foreach (var cls in part.Groups["classes"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries))
                    pairs.Add((part.Groups["el"].Value, cls));
            }
        }
    }

    private static string? AvaresPath(string root, string? source)
    {
        var m = Regex.Match(source ?? "", @"^avares://(?<asm>[^/]+)/(?<p>.+)$");
        if (!m.Success) return null;
        var project = m.Groups["asm"].Value == "DotCharter" ? "DotCharter.UI" : m.Groups["asm"].Value;
        var path = Path.GetFullPath(Path.Combine(root, project, m.Groups["p"].Value.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(path) ? path : null;
    }
}
