using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Border families are <c>ControlTheme</c>s (#240), the second step after the
///     buttons (<see cref="ButtonControlThemeTests" />), and these are the ways that move
///     silently undoes or breaks itself.
///     <para>
///         Avalonia attaches EVERY global <c>Selector="Border.X"</c> style to EVERY
///         <c>Border</c> in the app — an activator and its <c>DynamicResource</c> setters,
///         whether or not the class is ever set. Before this step each of the ~4,000
///         Borders of the prewarmed shell carried ~216 value frames (#239). A theme's
///         nested styles attach only to the Borders that wear it, so each family lives in
///         one: <c>FireTagBorder</c> holds the <c>^.FireTag…</c> rules, gated on the class
///         exactly as the global style was, and a Border wears both,
///         <c>Theme="{StaticResource FireTagBorder}" Classes="FireTag Accent"</c>.
///     </para>
///     <list type="number">
///         <item>A new global <c>Border.X</c> style brings the per-Border cost back for
///         every Border in the app — no error, no warning.</item>
///         <item>A family class worn WITHOUT the theme that carries its rules is a silent
///         no-op: the Border draws nothing at all.</item>
///         <item>A Border has ONE theme, so a rule two families share would be COPIED
///         into each; a copy that drifts is two looks for one class.</item>
///         <item>A Border theme worn inside a control template is outranked by the
///         templated parent's theme (<c>StyleTriggerTemplatedParentTheme</c> beats the
///         Border's own <c>StyleTriggerTheme</c>, where the old global style beat both),
///         and a theme that reaches a control directly AND through its templated
///         parent's theme is attached twice (Avalonia PR #22276, open).</item>
///     </list>
/// </summary>
public sealed class BorderControlThemeTests
{
    /// <summary>
    ///     Style files whose global <c>Border.X</c> rules are not converted, with the
    ///     reason. Shrink-only: a stale entry fails.
    /// </summary>
    /// <remarks>Empty since #242: the rig's families are ControlThemes in
    ///     <c>Fire/FireRig*.axaml</c>, and its descendant rules are scoped to the rig views.</remarks>
    private static readonly Dictionary<string, string> GlobalFiles = new(StringComparer.Ordinal);

    /// <summary>
    ///     Border classes styled by a global DESCENDANT or CHILD rule
    ///     (<c>ListBox.FireList &gt; ListBoxItem:selected Border.FireListRow</c>), which a
    ///     ControlTheme may not contain ("ControlTheme style may not directly contain a
    ///     child or descendent selector"). Shrink-only: a stale entry fails.
    /// </summary>
    private static readonly Dictionary<string, string> DescendantFamilies = new(StringComparer.Ordinal)
    {
        ["FireListRow"] = "a row's list state comes from its ListBox (`ListBox.DenseRows`, `ListBox.FireList > " +
                          "ListBoxItem:pointerover/:focus-visible/:selected`); the row's own rules are FireListRowBorder",
        ["FirePopoverDotGlow"] = "lit by its button's state (`Button.FirePopoverHistoryRow.Current Border.FirePopoverDotGlow`)",
        ["MdCodeBlock"] = "scoped to the release-notes view (`md|MarkdownView Border.MdCodeBlock`)",
        ["MdRule"] = "scoped to the release-notes view (`md|MarkdownView Border.MdRule`)",
    };

    private static readonly string[] ThemeRoots =
    [
        "DotCharter.UI/Styles/Theme.axaml",
        "DotCharter.Tone.UI/Styles/RigTheme.axaml"
    ];

    private static readonly string[] MarkupRoots =
        ["DotCharter.UI", "DotCharter.Tone.UI", "DotCharter.RigLab", "DotCharter.UI.Gallery"];

    private static readonly Regex Subject = new(
        @"^(?:Border|:is\(Border\))(?<rest>(?:\.[A-Za-z0-9_\-]+|:[A-Za-z\-]+(?:\([^)]*\))?)+)$", RegexOptions.Compiled);

    private static readonly Regex ThemeReference = new(
        @"^\{(?:Static|Dynamic)Resource\s+(?<key>[A-Za-z0-9_]+)\}$", RegexOptions.Compiled);

    [Fact]
    public void NoGlobalStyle_TargetsABorderByClass()
    {
        var files = ThemeRoots.SelectMany(IncludeChain).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(files.Count > 20, $"the theme include chains reached only {files.Count} files — the walk is broken");

        var offenders = new List<string>();
        var keptFiles = new HashSet<string>(StringComparer.Ordinal);
        var keptFamilies = new HashSet<string>(StringComparer.Ordinal);
        var seen = 0;
        var parts = 0;
        foreach (var file in files)
        {
            var relative = Relative(file);
            foreach (var style in GlobalStyles(file))
            {
                foreach (var part in style.Split(','))
                {
                    parts++;
                    var (classes, descendant) = BorderSubject(part);
                    if (classes.Count == 0) continue;
                    seen++;
                    if (GlobalFiles.ContainsKey(relative))
                    {
                        keptFiles.Add(relative);
                        continue;
                    }

                    var family = classes.FirstOrDefault(DescendantFamilies.ContainsKey);
                    if (descendant && family is not null)
                    {
                        keptFamilies.Add(family);
                        continue;
                    }

                    offenders.Add($"{relative}: Selector=\"{part.Trim()}\"");
                }
            }
        }

        // Only the descendant rules are left (#242), so the walk is proven by every
        // global selector it read, and by each DescendantFamilies entry being seen.
        Assert.True(parts > 150, $"only {parts} global selector parts were read — the walk is broken");
        Assert.True(seen >= DescendantFamilies.Count, $"only {seen} global Border-by-class rules were seen — the walk is broken");
        Assert.True(offenders.Count == 0,
            "A global style that targets a Border by class is attached to EVERY Border in the app, set or not (#239). " +
            "Put the rule in that family's ControlTheme as a nested `^.Class` style (see fire-design-system.md):\n  " +
            string.Join("\n  ", offenders));
        Assert.True(keptFiles.SetEquals(GlobalFiles.Keys),
            "GlobalFiles lists a file with no global Border rule any more — delete the entry: " +
            string.Join(", ", GlobalFiles.Keys.Except(keptFiles)));
        Assert.True(keptFamilies.SetEquals(DescendantFamilies.Keys),
            "DescendantFamilies lists a family no descendant rule styles any more — delete the entry: " +
            string.Join(", ", DescendantFamilies.Keys.Except(keptFamilies)));
    }

    /// <summary>
    ///     Every rule of a Border theme was a global style once, so wherever a rule's
    ///     classes are all on a Border, that rule used to style it — and must still. The
    ///     Border's own <c>Theme</c> has to carry a rule for each such class set.
    /// </summary>
    [Fact]
    public void EveryBorder_WearsATheme_ThatCarriesTheRulesItsClassesNeed()
    {
        var themes = Themes();
        Assert.True(themes.Count > 30, $"only {themes.Count} Border ControlThemes were found");

        var borders = 0;
        var themed = 0;
        var offenders = new List<string>();
        foreach (var file in MarkupFiles())
        {
            foreach (var border in XDocument.Parse(File.ReadAllText(file)).Descendants().Where(e => e.Name.LocalName == "Border"))
            {
                borders++;
                var classes = ClassesOf(border);
                var needed = RuleSets(themes, classes);
                var where = $"{Relative(file)}: <Border Classes=\"{string.Join(' ', classes)}\">";
                var key = border.Attribute("Theme") is { } local && ThemeReference.Match(local.Value) is { Success: true } m
                    ? m.Groups["key"].Value
                    : null;
                if (needed.Count == 0)
                {
                    if (key is not null && themes.ContainsKey(key))
                        offenders.Add($"{where} wears {key} but none of its rules: the theme is the wrong one or the class is missing");
                    continue;
                }

                themed++;
                if (key is null)
                {
                    offenders.Add($"{where} wears no theme, so its '^.{string.Join("', '^.", needed)}' rules style nothing");
                    continue;
                }

                foreach (var set in needed.Where(s => !themes.TryGetValue(key, out var rules) || !rules.Contains(s)))
                    offenders.Add($"{where} wears {key}, which has no '^.{set}' rules");
            }
        }

        Assert.True(borders > 800, $"only {borders} Borders were read — the walk is broken");
        Assert.True(themed > 300, $"only {themed} Borders wear a themed class — the walk is broken");
        Assert.True(offenders.Count == 0,
            "A class styled inside Border ControlThemes does nothing on a Border that does not wear one that carries it " +
            "(#240) — add Theme=\"{StaticResource <Family>Border}\" (DynamicResource in a dialog view):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>The same check for Borders built in code: the file that adds the
    /// classes must also name a theme that carries their rules.</summary>
    [Fact]
    public void EveryBorderClassAddedInCode_NamesItsTheme()
    {
        var themes = Themes();
        var offenders = new List<string>();
        var reached = 0;
        foreach (var root in MarkupRoots)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), root), "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                var source = File.ReadAllText(file);
                var added = Regex.Matches(source, @"Classes\.Add\(""(?<c>[A-Za-z0-9_]+)""\)")
                    .Select(m => m.Groups["c"].Value).ToHashSet(StringComparer.Ordinal);
                foreach (var set in RuleSets(themes, added))
                {
                    reached++;
                    if (!themes.Any(t => t.Value.Contains(set) && source.Contains($"\"{t.Key}\"", StringComparison.Ordinal)))
                        offenders.Add($"{Relative(file)}: adds '{set.Replace('.', ' ')}' but names no theme that carries '^.{set}'");
                }
            }
        }

        Assert.True(reached > 0, "no code-built Border wears a themed class — DialogHost's scrim should");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    /// <summary>No modifier is shared between two Border families today: every one is
    /// stated per family (<c>^.FireTag.Warn</c>, <c>^.FireDot.Warn</c>). A rule that is
    /// ever copied into a second theme must stay the same in each.</summary>
    [Fact]
    public void ARuleCopiedIntoSeveralBorderThemes_IsTheSameInEach()
    {
        var bySelector = new Dictionary<string, List<(string Theme, string Body)>>(StringComparer.Ordinal);
        var rules = 0;
        foreach (var (key, theme) in ThemeElements())
        {
            foreach (var style in theme.Elements().Where(e => e.Name.LocalName == "Style"))
            {
                rules++;
                var selector = style.Attribute("Selector")!.Value;
                var body = string.Join("\n", style.Elements().Select(e =>
                    Regex.Replace(e.ToString(SaveOptions.DisableFormatting), @"\s+", " ")));
                if (!bySelector.TryGetValue(selector, out var list)) bySelector[selector] = list = [];
                var mine = list.FindIndex(v => v.Theme == key);
                if (mine >= 0) list[mine] = (key, list[mine].Body + "\n" + body);
                else list.Add((key, body));
            }
        }

        Assert.True(rules > 60, $"only {rules} rules were read from the Border themes — the walk is broken");
        var drifted = bySelector.Where(kv => kv.Value.Count > 1)
            .Where(kv => kv.Value.Select(v => v.Body).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(kv => $"'{kv.Key}' differs between {string.Join(", ", kv.Value.Select(v => v.Theme))}")
            .ToList();
        Assert.True(drifted.Count == 0,
            "A rule copied into several Border themes must stay the same in each, or one class has two looks:\n  " +
            string.Join("\n  ", drifted));
    }

    /// <summary>
    ///     Avalonia PR #22276 (open): a ControlTheme's shared StyleInstance is attached
    ///     twice when the same theme reaches a control directly AND through its templated
    ///     parent's theme. A Border has no template, so a Border theme can only meet that
    ///     by being worn inside some control's template — and there it would also lose to
    ///     the parent theme's <c>/template/</c> rules, which the old global style beat.
    ///     So a Border theme is never worn inside a <c>ControlTemplate</c>, and a Border
    ///     theme never carries a template of its own.
    /// </summary>
    [Fact]
    public void NoBorderTheme_IsWornInsideATemplate()
    {
        var themes = Themes();
        var offenders = new List<string>();
        var templates = 0;
        foreach (var file in MarkupFiles())
        {
            var doc = XDocument.Parse(File.ReadAllText(file));
            foreach (var template in doc.Descendants().Where(e => e.Name.LocalName == "ControlTemplate"))
            {
                templates++;
                foreach (var border in template.Descendants().Where(e => e.Name.LocalName == "Border"))
                {
                    if (border.Attribute("Theme") is { } t && ThemeReference.Match(t.Value) is { Success: true } m
                        && themes.ContainsKey(m.Groups["key"].Value))
                    {
                        offenders.Add($"{Relative(file)}: a ControlTemplate holds a Border wearing {m.Groups["key"].Value}");
                    }
                }
            }
        }

        foreach (var (key, theme) in ThemeElements())
        {
            if (theme.Descendants().Any(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == "Template"))
                offenders.Add($"{key} sets a Template — a Border theme must stay template-free");
        }

        Assert.True(templates > 20, $"only {templates} ControlTemplates were read — the walk is broken");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    // ── model ──────────────────────────────────────────────────────────────

    /// <summary>Each Border ControlTheme's key and the class sets of its class-gated
    /// rules, spelled <c>A.B</c> in ordinal order.</summary>
    private static Dictionary<string, HashSet<string>> Themes()
    {
        var themes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (key, theme) in ThemeElements())
        {
            if (!themes.TryGetValue(key, out var sets)) themes[key] = sets = new(StringComparer.Ordinal);
            foreach (var style in theme.Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                var classes = style.AncestorsAndSelf().TakeWhile(e => e != theme)
                    .Where(e => e.Name.LocalName == "Style")
                    .SelectMany(e => Regex.Matches(e.Attribute("Selector")?.Value ?? "", @"\^((?:\.[A-Za-z0-9_\-]+)+)")
                        .SelectMany(m => m.Groups[1].Value.Split('.', StringSplitOptions.RemoveEmptyEntries)))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (classes.Count > 0) sets.Add(string.Join('.', classes));
            }
        }

        return themes;
    }

    private static IEnumerable<(string Key, XElement Theme)> ThemeElements()
    {
        foreach (var file in MarkupFiles())
        {
            foreach (var theme in XDocument.Parse(File.ReadAllText(file)).Descendants()
                         .Where(e => e.Name.LocalName == "ControlTheme" && e.Attribute("TargetType")?.Value.Split(':')[^1] == "Border"))
            {
                var key = theme.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
                if (key is not null) yield return (key, theme);
            }
        }
    }

    /// <summary>The rule class sets, in any theme, that a Border with these classes satisfies.</summary>
    private static List<string> RuleSets(Dictionary<string, HashSet<string>> themes, HashSet<string> classes) =>
        themes.Values.SelectMany(s => s).Distinct(StringComparer.Ordinal)
            .Where(s => s.Split('.').All(classes.Contains))
            .ToList();

    private static HashSet<string> ClassesOf(XElement element) =>
        (element.Attribute("Classes")?.Value ?? "")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Concat(element.Attributes().Where(a => a.Name.LocalName.StartsWith("Classes.", StringComparison.Ordinal))
            .Select(a => a.Name.LocalName["Classes.".Length..]))
        .ToHashSet(StringComparer.Ordinal);

    // ── markup plumbing ────────────────────────────────────────────────────

    /// <summary>The selector of every <c>Style</c> in a file that sits outside a ControlTheme.</summary>
    private static IEnumerable<string> GlobalStyles(string file) =>
        XDocument.Parse(File.ReadAllText(file)).Descendants()
            .Where(e => e.Name.LocalName == "Style" && e.Ancestors().All(a => a.Name.LocalName != "ControlTheme"))
            .Select(e => e.Attribute("Selector")?.Value)
            .OfType<string>();

    /// <summary>The classes of a selector part whose STYLED control is a Border (the step
    /// before any <c>/template/</c>), and whether it has ancestor steps.</summary>
    private static (List<string> Classes, bool Descendant) BorderSubject(string part)
    {
        var subject = part.Split("/template/")[0].Trim();
        var steps = Regex.Split(subject, @"\s*>\s*|\s+").Where(s => s.Length > 0).ToList();
        if (steps.Count == 0 || part.Contains("/template/", StringComparison.Ordinal)) return ([], false);
        var m = Subject.Match(steps[^1]);
        return m.Success
            ? (Regex.Matches(m.Groups["rest"].Value, @"\.([A-Za-z0-9_\-]+)").Select(c => c.Groups[1].Value).ToList(), steps.Count > 1)
            : ([], false);
    }

    private static IEnumerable<string> MarkupFiles() => MarkupRoots
        .SelectMany(r => Directory.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), r), "*.axaml", SearchOption.AllDirectories))
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>A theme file and every file its <c>StyleInclude</c>s reach, in order.</summary>
    private static IEnumerable<string> IncludeChain(string relative)
    {
        var root = TestPaths.RepoRoot();
        var seen = new List<string>();
        void Walk(string path)
        {
            if (seen.Contains(path, StringComparer.Ordinal)) return;
            seen.Add(path);
            foreach (Match m in Regex.Matches(File.ReadAllText(path), @"<StyleInclude\s+Source=""avares://(?<asm>[^/]+)/(?<path>[^""]+)"""))
            {
                var project = m.Groups["asm"].Value switch
                {
                    "DotCharter" => "DotCharter.UI",
                    "DotCharter.Tone.UI" => "DotCharter.Tone.UI",
                    _ => null
                };
                if (project is not null) Walk(Path.Combine(root, project, m.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        Walk(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return seen;
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(TestPaths.RepoRoot(), file).Replace('\\', '/');
}
