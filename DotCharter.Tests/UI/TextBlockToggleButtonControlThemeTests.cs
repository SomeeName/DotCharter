using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The TextBlock and ToggleButton families are <c>ControlTheme</c>s (#240), the
///     step after the buttons (<see cref="ButtonControlThemeTests" />) and the Borders
///     (<see cref="BorderControlThemeTests" />), and these are the ways that move
///     silently undoes or breaks itself.
///     <para>
///         Avalonia attaches EVERY global <c>Selector="TextBlock.X"</c> style to EVERY
///         <c>TextBlock</c> in the app — an activator and its <c>DynamicResource</c>
///         setters, whether or not the class is ever set; the same for a
///         <c>ToggleButton.X</c> style and every ToggleButton. A theme's nested styles
///         attach only to the controls that wear it, so each family lives in one:
///         <c>FireMonoText</c> holds the <c>^.FireMono…</c> rules, gated on the class
///         exactly as the global style was, and a TextBlock wears both,
///         <c>Theme="{StaticResource FireMonoText}" Classes="FireMono Faint"</c>.
///         A ToggleButton family is <c>&lt;Family&gt;Toggle</c> (<c>ChipToggle</c>),
///         based on Fluent's ToggleButton theme.
///     </para>
///     <list type="number">
///         <item>A new global class style brings the per-control cost back for every
///         TextBlock (or ToggleButton) in the app — no error, no warning.</item>
///         <item>A family class worn WITHOUT the theme that carries its rules is a silent
///         no-op: the text falls back to the inherited font, the toggle to bare Fluent.</item>
///         <item>A control has ONE theme, so a rule two families share (the ToggleButton
///         <c>^.Compact</c>, in <c>CompactToggle</c> and <c>FireGhostToggle</c>) is COPIED
///         into each; a copy that drifts is two looks for one class.</item>
///         <item>A theme worn inside a control template is outranked by the templated
///         parent's theme (<c>StyleTriggerTemplatedParentTheme</c> beats the control's
///         own <c>StyleTriggerTheme</c>, where the old global style beat both), and a
///         theme that reaches a control directly AND through its templated parent's
///         theme is attached twice (Avalonia PR #22276, open).</item>
///     </list>
/// </summary>
public sealed class TextBlockToggleButtonControlThemeTests
{
    /// <summary>
    ///     Style files whose global class rules are not converted, per element type,
    ///     with the reason. Shrink-only: a stale entry fails. Empty since #242, which
    ///     made the rig's families ControlThemes and scoped its descendant rules to the
    ///     rig views.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> GlobalFiles = new(StringComparer.Ordinal)
    {
        ["TextBlock"] = new(StringComparer.Ordinal),
        ["ToggleButton"] = new(StringComparer.Ordinal),
    };

    /// <summary>
    ///     Classes styled by a global DESCENDANT rule (<c>Button.FireCta TextBlock.FireCtaLabel</c>),
    ///     which a ControlTheme may not contain ("ControlTheme style may not directly contain
    ///     a child or descendent selector"), per element type. Shrink-only: a stale entry fails.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> DescendantFamilies = new(StringComparer.Ordinal)
    {
        ["TextBlock"] = new(StringComparer.Ordinal)
        {
            ["FireCtaLabel"] = "the lit action's label, styled by its button (`Button.FireCta TextBlock.FireCtaLabel`, `Button:disabled …`)",
            ["FirePopoverHistoryName"] = "lit by its row button's `.Current` (`Button.FirePopoverHistoryRow…`)",
            ["FirePopoverMeta"] = "the picked option's meta (`Button.FirePopoverOption.Picked TextBlock.FirePopoverMeta`); " +
                                  "its own rules are FirePopoverMetaText",
            ["FirePopoverOptionName"] = "lit by its option button's `.Picked` (`Button.FirePopoverOption…`)",
            ["FirePopoverProfileMeta"] = "lit by its row button's `.Current` (`Button.FirePopoverProfileRow…`)",
            ["FirePopoverProfileName"] = "lit by its row button's `.Current` (`Button.FirePopoverProfileRow…`)",
            ["FirePopoverSelectValue"] = "the select's value, inside its toggle (`ToggleButton.FirePopoverSelect TextBlock…`)",
            ["MdBulletGlyph"] = "scoped to the release-notes view (`md|MarkdownView TextBlock.MdBulletGlyph`)",
        },
        ["ToggleButton"] = new(StringComparer.Ordinal),
    };

    /// <summary>
    ///     ToggleButton families whose theme is a whole TEMPLATE with ungated setters,
    ///     reached by class until #240 through a <c>ToggleButton.X { Theme }</c> style: a
    ///     toggle wearing the class must wear the theme, gated rules or not.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> NamedByClass = new(StringComparer.Ordinal)
    {
        ["TextBlock"] = new(StringComparer.Ordinal),
        ["ToggleButton"] = new(StringComparer.Ordinal)
        {
            ["FireChoice"] = "FireChoiceToggle",
            ["FireSegmentItem"] = "FireSegmentItemToggle",
        },
    };

    private static readonly string[] ThemeRoots =
    [
        "DotCharter.UI/Styles/Theme.axaml",
        "DotCharter.Tone.UI/Styles/RigTheme.axaml"
    ];

    private static readonly string[] MarkupRoots =
        ["DotCharter.UI", "DotCharter.Tone.UI", "DotCharter.RigLab", "DotCharter.UI.Gallery"];

    private static readonly Regex ThemeReference = new(
        @"^\{(?:Static|Dynamic)Resource\s+(?<key>[A-Za-z0-9_]+)\}$", RegexOptions.Compiled);

    public static TheoryData<string> Types => ["TextBlock", "ToggleButton"];

    [Theory]
    [MemberData(nameof(Types))]
    public void NoGlobalStyle_TargetsTheTypeByClass(string type)
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
                    var (classes, descendant) = SubjectClasses(part, type);
                    if (classes.Count == 0) continue;
                    seen++;
                    if (GlobalFiles[type].ContainsKey(relative))
                    {
                        keptFiles.Add(relative);
                        continue;
                    }

                    var family = classes.FirstOrDefault(DescendantFamilies[type].ContainsKey);
                    if (descendant && family is not null)
                    {
                        keptFamilies.Add(family);
                        continue;
                    }

                    offenders.Add($"{relative}: Selector=\"{part.Trim()}\"");
                }
            }
        }

        // Only descendant rules are left (none at all for ToggleButton since #242), so
        // the walk is proven by every global selector it read and by each
        // DescendantFamilies entry being seen.
        Assert.True(parts > 150, $"only {parts} global selector parts were read — the walk is broken");
        Assert.True(seen >= DescendantFamilies[type].Count, $"only {seen} global {type}-by-class rules were seen — the walk is broken");
        Assert.True(offenders.Count == 0,
            $"A global style that targets a {type} by class is attached to EVERY {type} in the app, set or not (#239). " +
            "Put the rule in that family's ControlTheme as a nested `^.Class` style (see fire-design-system.md):\n  " +
            string.Join("\n  ", offenders));
        Assert.True(keptFiles.SetEquals(GlobalFiles[type].Keys),
            $"GlobalFiles[{type}] lists a file with no global {type} rule any more — delete the entry: " +
            string.Join(", ", GlobalFiles[type].Keys.Except(keptFiles)));
        Assert.True(keptFamilies.SetEquals(DescendantFamilies[type].Keys),
            $"DescendantFamilies[{type}] lists a family no descendant rule styles any more — delete the entry: " +
            string.Join(", ", DescendantFamilies[type].Keys.Except(keptFamilies)));
    }

    /// <summary>
    ///     Every rule of a theme was a global style once, so wherever a rule's classes
    ///     are all on a control, that rule used to style it — and must still. The
    ///     control's own <c>Theme</c> has to carry a rule for each such class set, and a
    ///     class named by its theme (<see cref="NamedByClass" />) needs that theme.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void EveryControl_WearsATheme_ThatCarriesTheRulesItsClassesNeed(string type)
    {
        var themes = Themes(type);
        Assert.True(themes.Count > (type == "TextBlock" ? 40 : 8), $"only {themes.Count} {type} ControlThemes were found");

        var controls = 0;
        var themed = 0;
        var offenders = new List<string>();
        foreach (var file in MarkupFiles())
        {
            foreach (var control in XDocument.Parse(File.ReadAllText(file)).Descendants().Where(e => e.Name.LocalName == type))
            {
                controls++;
                var classes = ClassesOf(control);
                var needed = RuleSets(themes, classes);
                var named = classes.Where(NamedByClass[type].ContainsKey).Select(c => NamedByClass[type][c]).Distinct().ToList();
                var where = $"{Relative(file)}: <{type} Classes=\"{string.Join(' ', classes)}\">";
                var key = control.Attribute("Theme") is { } local && ThemeReference.Match(local.Value) is { Success: true } m
                    ? m.Groups["key"].Value
                    : null;
                if (needed.Count == 0 && named.Count == 0)
                {
                    if (key is not null && themes.TryGetValue(key, out var sets) && sets.Count > 0)
                        offenders.Add($"{where} wears {key} but none of its rules: the theme is the wrong one or the class is missing");
                    continue;
                }

                themed++;
                if (key is null)
                {
                    offenders.Add($"{where} wears no theme, so its '^.{string.Join("', '^.", needed.Concat(named))}' rules style nothing");
                    continue;
                }

                foreach (var set in needed.Where(s => !themes.TryGetValue(key, out var rules) || !rules.Contains(s)))
                    offenders.Add($"{where} wears {key}, which has no '^.{set}' rules");
                foreach (var theme in named.Where(n => n != key))
                    offenders.Add($"{where} wears {key}, but its class names {theme}");
            }
        }

        Assert.True(controls > (type == "TextBlock" ? 2000 : 150), $"only {controls} {type}s were read — the walk is broken");
        Assert.True(themed > (type == "TextBlock" ? 800 : 80), $"only {themed} {type}s wear a themed class — the walk is broken");
        Assert.True(offenders.Count == 0,
            $"A class styled inside {type} ControlThemes does nothing on a {type} that does not wear one that carries it " +
            "(#240) — add Theme=\"{StaticResource <Family>" + (type == "TextBlock" ? "Text" : "Toggle") +
            "}\" (DynamicResource in a dialog view):\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The same check for controls built in product code: a file that adds a
    /// themed class must also name a theme that carries its rules.</summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void EveryClassAddedInCode_NamesItsTheme(string type)
    {
        var themes = Themes(type);
        var offenders = new List<string>();
        var files = 0;
        foreach (var root in MarkupRoots)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), root), "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                files++;
                var source = File.ReadAllText(file);
                // `x.Classes.Add("X")` and a `new TextBlock { Classes = { "X" } }` initializer
                var added = Regex.Matches(source, @"Classes\.Add\(""(?<c>[A-Za-z0-9_]+)""\)")
                    .Select(m => m.Groups["c"].Value)
                    .Concat(Regex.Matches(source, $@"new {type}\b[^;]*?Classes\s*=\s*\{{(?<list>[^}}]*)\}}", RegexOptions.Singleline)
                        .SelectMany(m => Regex.Matches(m.Groups["list"].Value, @"""(?<c>[A-Za-z0-9_]+)""").Select(c => c.Groups["c"].Value)))
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var set in RuleSets(themes, added))
                {
                    if (!themes.Any(t => t.Value.Contains(set) && source.Contains($"\"{t.Key}\"", StringComparison.Ordinal)))
                        offenders.Add($"{Relative(file)}: adds '{set.Replace('.', ' ')}' but names no theme that carries '^.{set}'");
                }
            }
        }

        Assert.True(files > 500, $"only {files} source files were read — the walk is broken");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    /// <summary>A rule copied into several themes of one type (the ToggleButton
    /// <c>^.Compact</c>) must stay the same in each.</summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void ARuleCopiedIntoSeveralThemes_IsTheSameInEach(string type)
    {
        var bySelector = new Dictionary<string, List<(string Theme, string Body)>>(StringComparer.Ordinal);
        var rules = 0;
        foreach (var (key, theme) in ThemeElements(type))
        {
            foreach (var style in theme.Elements().Where(e => e.Name.LocalName == "Style"))
            {
                rules++;
                var selector = Regex.Replace(style.Attribute("Selector")!.Value, @"\s+", " ");
                // the class-gated rules: a whole-template theme's own `^:checked` is not a copy
                if (!selector.StartsWith("^.", StringComparison.Ordinal)) continue;
                var body = string.Join("\n", style.Elements().Select(e =>
                    Regex.Replace(e.ToString(SaveOptions.DisableFormatting), @"\s+", " ")));
                if (!bySelector.TryGetValue(selector, out var list)) bySelector[selector] = list = [];
                var mine = list.FindIndex(v => v.Theme == key);
                if (mine >= 0) list[mine] = (key, list[mine].Body + "\n" + body);
                else list.Add((key, body));
            }
        }

        Assert.True(rules > (type == "TextBlock" ? 80 : 50), $"only {rules} rules were read from the {type} themes — the walk is broken");
        var shared = bySelector.Where(kv => kv.Value.Count > 1).ToList();
        if (type == "ToggleButton")
            Assert.Contains(shared, kv => kv.Key == "^.Compact");
        var drifted = shared
            .Where(kv => kv.Value.Select(v => v.Body).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(kv => $"'{kv.Key}' differs between {string.Join(", ", kv.Value.Select(v => v.Theme))}")
            .ToList();
        Assert.True(drifted.Count == 0,
            $"A rule copied into several {type} themes must stay the same in each, or one class has two looks:\n  " +
            string.Join("\n  ", drifted));
    }

    /// <summary>
    ///     Avalonia PR #22276 (open): a ControlTheme's shared StyleInstance is attached
    ///     twice when the same theme reaches a control directly AND through its templated
    ///     parent's theme; and inside a template the parent theme's <c>/template/</c>
    ///     rules outrank the control's own theme, which the old global style beat. So a
    ///     family theme is never worn inside a <c>ControlTemplate</c>, and a TextBlock
    ///     theme carries no template.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void NoFamilyTheme_IsWornInsideATemplate(string type)
    {
        var themes = Themes(type);
        var offenders = new List<string>();
        var templates = 0;
        foreach (var file in MarkupFiles())
        {
            var doc = XDocument.Parse(File.ReadAllText(file));
            foreach (var template in doc.Descendants().Where(e => e.Name.LocalName == "ControlTemplate"))
            {
                templates++;
                foreach (var control in template.Descendants().Where(e => e.Name.LocalName == type))
                {
                    if (control.Attribute("Theme") is { } t && ThemeReference.Match(t.Value) is { Success: true } m
                        && themes.ContainsKey(m.Groups["key"].Value))
                    {
                        offenders.Add($"{Relative(file)}: a ControlTemplate holds a {type} wearing {m.Groups["key"].Value}");
                    }
                }
            }
        }

        if (type == "TextBlock")
        {
            foreach (var (key, theme) in ThemeElements(type))
            {
                if (theme.Descendants().Any(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == "Template"))
                    offenders.Add($"{key} sets a Template — a TextBlock theme must stay template-free");
            }
        }

        Assert.True(templates > 20, $"only {templates} ControlTemplates were read — the walk is broken");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    // ── model ──────────────────────────────────────────────────────────────

    /// <summary>Each ControlTheme of the type: its key and the class sets of its
    /// class-gated rules, spelled <c>A.B</c> in ordinal order.</summary>
    private static Dictionary<string, HashSet<string>> Themes(string type)
    {
        var themes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (key, theme) in ThemeElements(type))
        {
            if (!themes.TryGetValue(key, out var sets)) themes[key] = sets = new(StringComparer.Ordinal);
            foreach (var style in theme.Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                // a nested rule is gated on its own `^` step AND on every enclosing one;
                // each comma part is gated alike in the families converted so far
                var classes = style.AncestorsAndSelf().TakeWhile(e => e != theme)
                    .Where(e => e.Name.LocalName == "Style")
                    .SelectMany(e => Regex.Matches((e.Attribute("Selector")?.Value ?? "").Split(',')[0].Split("/template/")[0],
                            @"\^((?:\.[A-Za-z0-9_\-]+|:[A-Za-z\-]+(?:\([^)]*\))?)+)")
                        .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"\.([A-Za-z0-9_\-]+)").Select(c => c.Groups[1].Value)))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (classes.Count > 0) sets.Add(string.Join('.', classes));
            }
        }

        // A theme BASED ON another family theme carries that theme's rules too: the rig's
        // `FireMicCardToggle` is BasedOn `ChipToggle` for its one `FireMicCard Chip` toggle,
        // rather than copying ChipToggle's rules (and its Transitions setter) into a Fire part.
        var bases = ThemeElements(type).ToDictionary(t => t.Key, t => t.Theme.Attribute("BasedOn")?.Value, StringComparer.Ordinal);
        foreach (var key in themes.Keys.ToList())
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { key };
            var basedOn = bases[key];
            while (basedOn is not null && ThemeReference.Match(basedOn) is { Success: true } b
                   && themes.TryGetValue(b.Groups["key"].Value, out var inherited) && visited.Add(b.Groups["key"].Value))
            {
                themes[key].UnionWith(inherited);
                basedOn = bases[b.Groups["key"].Value];
            }
        }

        return themes;
    }

    private static IEnumerable<(string Key, XElement Theme)> ThemeElements(string type)
    {
        foreach (var file in MarkupFiles())
        {
            foreach (var theme in XDocument.Parse(File.ReadAllText(file)).Descendants()
                         .Where(e => e.Name.LocalName == "ControlTheme" && e.Attribute("TargetType")?.Value.Split(':')[^1] == type))
            {
                var key = theme.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
                if (key is not null) yield return (key, theme);
            }
        }
    }

    /// <summary>The rule class sets, in any theme, that a control with these classes satisfies.</summary>
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

    /// <summary>The classes of a selector part whose STYLED control is of the type (the
    /// step before any <c>/template/</c>, so a ToggleButton's template rules count), and
    /// whether it has ancestor steps.</summary>
    private static (List<string> Classes, bool Descendant) SubjectClasses(string part, string type)
    {
        var subject = part.Split("/template/")[0].Trim();
        var steps = Regex.Split(subject, @"\s*>\s*|\s+").Where(s => s.Length > 0).ToList();
        if (steps.Count == 0) return ([], false);
        var m = Regex.Match(steps[^1], $@"^(?:{type}|:is\({type}\))(?<rest>(?:\.[A-Za-z0-9_\-]+|:[A-Za-z\-]+(?:\([^)]*\))?)+)$");
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
