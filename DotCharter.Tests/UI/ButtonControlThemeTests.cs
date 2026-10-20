using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The button families are <c>ControlTheme</c>s (#240), and these are the
///     three ways that move silently undoes or breaks itself.
///     <para>
///         Avalonia attaches EVERY global <c>Selector="Button.X"</c> style to EVERY
///         <c>Button</c> — an activator and its <c>DynamicResource</c> setters, whether
///         or not the class is ever set. Before #240 each Button paid for ~170 of
///         them (#239). A theme's nested styles attach only to the buttons that wear
///         that theme, so each family lives in one: <c>FireGhostButton</c> holds the
///         <c>^.FireGhost</c> rules, gated on the class exactly as the global style
///         was, and a button wears both,
///         <c>Theme="{StaticResource FireGhostButton}" Classes="FireGhost Compact"</c>.
///     </para>
///     <list type="number">
///         <item>A new global <c>Button.X</c> style brings the per-button cost back
///         for every button in the app — no error, no warning.</item>
///         <item>A family class worn WITHOUT the theme that styles it is a silent
///         no-op: the button renders as a bare Fluent button. So is a class the
///         button's theme does not carry (a <c>Danger</c> on a theme without the
///         <c>^.Danger</c> rules).</item>
///         <item>A Button has ONE theme, so a rule two families share
///         (<c>^.Compact</c>, <c>^.Danger</c>, the plain <c>^.Primary</c> under the
///         dialog footer's <c>FirePrimaryButton</c>) is COPIED into each theme. A
///         copy that drifts is two looks for one class.</item>
///     </list>
/// </summary>
public sealed class ButtonControlThemeTests
{
    /// <summary>
    ///     Families still styled by global <c>Button.X</c> rules, with the reason.
    ///     Shrink-only: delete an entry when its family moves into a theme.
    /// </summary>
    /// <remarks>Empty since #242: <c>FireInsertDot</c>, whose hover rules are descendant
    ///     selectors, is scoped to <c>AmpFxRigView</c> (<c>AmpFxRigStyles.axaml</c>) instead
    ///     of being global.</remarks>
    private static readonly Dictionary<string, string> GlobalFamilies = new(StringComparer.Ordinal);

    private static readonly string[] ThemeRoots =
    [
        "DotCharter.UI/Styles/Theme.axaml",
        "DotCharter.Tone.UI/Styles/RigTheme.axaml"
    ];

    private static readonly string[] MarkupRoots =
        ["DotCharter.UI", "DotCharter.Tone.UI", "DotCharter.RigLab", "DotCharter.UI.Gallery"];

    private static readonly Regex Step = new(
        @"(?<type>:is\([A-Za-z]+\)|\^|[A-Za-z_][A-Za-z0-9_|]*)?(?<classes>(?:\.[A-Za-z_][A-Za-z0-9_\-]*)+)",
        RegexOptions.Compiled);

    private static readonly Regex ThemeReference = new(
        @"^\{(?:Static|Dynamic)Resource\s+(?<key>[A-Za-z0-9_]+)\}$", RegexOptions.Compiled);

    [Fact]
    public void NoGlobalStyle_TargetsAButtonByClass()
    {
        var files = ThemeRoots.SelectMany(IncludeChain).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(files.Count > 20, $"the theme include chains reached only {files.Count} files — the walk is broken");

        var offenders = new List<string>();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var style in Styles(file).Where(s => s.Theme is null))
            {
                foreach (var part in style.Selector.Split(','))
                {
                    var classes = ButtonTargetClasses(part);
                    if (classes.Count == 0) continue;
                    var exempt = classes.FirstOrDefault(GlobalFamilies.ContainsKey);
                    if (exempt is not null)
                    {
                        kept.Add(exempt);
                        continue;
                    }

                    offenders.Add($"{Relative(file)}: Selector=\"{part.Trim()}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A global style that targets a Button by class is attached to EVERY Button in the app, set or not (#239). " +
            "Put the rule in that family's ControlTheme as a nested `^.Class` style (see fire-design-system.md):\n  " +
            string.Join("\n  ", offenders));
        Assert.True(kept.SetEquals(GlobalFamilies.Keys),
            "GlobalFamilies lists a family that no global style uses any more — delete the entry: " +
            string.Join(", ", GlobalFamilies.Keys.Except(kept)));
    }

    /// <summary>
    ///     Every rule of a Button theme was a global style once, so wherever a rule's
    ///     classes are all on a button, that rule used to style it — and must still.
    ///     The button's theme (its own <c>Theme</c>, or one a style in its scope sets
    ///     by class) has to carry a rule for each such class set.
    /// </summary>
    [Fact]
    public void EveryButton_WearsATheme_ThatCarriesTheRulesItsClassesNeed()
    {
        var model = Model();
        Assert.True(model.Themes.Count > 25, $"only {model.Themes.Count} Button ControlThemes were found");

        var buttons = 0;
        var themed = 0;
        var offenders = new List<string>();
        foreach (var file in MarkupFiles())
        {
            var doc = XDocument.Parse(File.ReadAllText(file));
            foreach (var button in doc.Descendants().Where(e => e.Name.LocalName == "Button"))
            {
                buttons++;
                var classes = (button.Attribute("Classes")?.Value ?? "")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Concat(button.Attributes().Where(a => a.Name.LocalName.StartsWith("Classes.", StringComparison.Ordinal))
                        .Select(a => a.Name.LocalName["Classes.".Length..]))
                    .ToHashSet(StringComparer.Ordinal);
                var needed = model.RuleSets(classes);
                if (needed.Count == 0) continue;
                themed++;

                var candidates = ThemesOf(button, classes, model).ToList();
                var where = $"{Relative(file)}: <Button Classes=\"{string.Join(' ', classes)}\">";
                if (candidates.Count == 0)
                {
                    offenders.Add($"{where} wears no theme, so its '^.{string.Join("', '^.", needed)}' rules style nothing");
                    continue;
                }

                foreach (var set in needed.Where(s => !candidates.Any(c => model.Themes.TryGetValue(c, out var r) && r.Contains(s))))
                    offenders.Add($"{where} wears {string.Join("/", candidates)}, which has no '^.{set}' rules");
            }
        }

        Assert.True(buttons > 500, $"only {buttons} Buttons were read — the walk is broken");
        Assert.True(themed > 400, $"only {themed} Buttons wear a themed class — the walk is broken");
        Assert.True(offenders.Count == 0,
            "A class styled inside ControlThemes does nothing on a Button that does not wear one that carries it (#240) — " +
            "add Theme=\"{StaticResource <Family>Button}\" (DynamicResource in a dialog view):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>The same check for buttons built in code: the file that adds the
    /// classes must also name a theme that carries their rules.</summary>
    [Fact]
    public void EveryButtonClassAddedInCode_NamesItsTheme()
    {
        var model = Model();
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
                foreach (var set in model.RuleSets(added))
                {
                    reached++;
                    if (!model.Themes.Any(t => t.Value.Contains(set) && source.Contains($"\"{t.Key}\"", StringComparison.Ordinal)))
                        offenders.Add($"{Relative(file)}: adds '{set.Replace('.', ' ')}' but names no theme that carries '^.{set}'");
                }
            }
        }

        Assert.True(reached > 0, "no code-built button wears a themed class — RigFaceplateView's FireQuiet should");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Fact]
    public void ARuleCopiedIntoSeveralButtonThemes_IsTheSameInEach()
    {
        var bySelector = new Dictionary<string, List<(string Theme, string Body)>>(StringComparer.Ordinal);
        foreach (var file in StyleFiles())
        {
            foreach (var theme in XDocument.Parse(File.ReadAllText(file)).Descendants()
                         .Where(e => e.Name.LocalName == "ControlTheme" && TargetType(e) == "Button"))
            {
                var key = theme.Attributes().First(a => a.Name.LocalName == "Key").Value;
                foreach (var style in theme.Elements().Where(e => e.Name.LocalName == "Style"))
                {
                    var selector = style.Attribute("Selector")!.Value;
                    if (!selector.StartsWith("^.", StringComparison.Ordinal)) continue;
                    var body = string.Join("\n", style.Elements().Select(e =>
                        Regex.Replace(e.ToString(SaveOptions.DisableFormatting), @"\s+", " ")));
                    if (!bySelector.TryGetValue(selector, out var list)) bySelector[selector] = list = [];
                    // one theme may state a selector twice (FireQuiet does, in global order): compare the sequence
                    var mine = list.FindIndex(v => v.Theme == key);
                    if (mine >= 0) list[mine] = (key, list[mine].Body + "\n" + body);
                    else list.Add((key, body));
                }
            }
        }

        var shared = bySelector.Where(kv => kv.Value.Count > 1).ToList();
        Assert.True(shared.Count >= 10, $"only {shared.Count} rules are shared between themes — expected the Compact, " +
                                        "Danger and Primary copies; the walk is broken");
        var drifted = shared
            .Where(kv => kv.Value.Select(v => v.Body).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(kv => $"'{kv.Key}' differs between {string.Join(", ", kv.Value.Select(v => v.Theme))}")
            .ToList();
        Assert.True(drifted.Count == 0,
            "A rule copied into several Button themes must stay the same in each, or one class has two looks:\n  " +
            string.Join("\n  ", drifted));
    }

    // ── model ──────────────────────────────────────────────────────────────

    /// <summary><c>Themes</c>: each Button ControlTheme's key and the class sets
    /// of its class-gated rules, spelled <c>A.B</c> in ordinal order.</summary>
    private sealed record ThemeModel(
        Dictionary<string, HashSet<string>> Themes,
        List<(string[] Ancestors, HashSet<string> Classes, string Theme)> ThemeSetters)
    {
        /// <summary>The rule class sets, in any theme, that a button with these classes satisfies.</summary>
        public List<string> RuleSets(HashSet<string> classes) => Themes.Values.SelectMany(s => s)
            .Distinct(StringComparer.Ordinal)
            .Where(s => s.Split('.').All(classes.Contains))
            .ToList();
    }

    private static ThemeModel Model()
    {
        var themes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var setters = new List<(string[], HashSet<string>, string)>();

        foreach (var file in StyleFiles())
        {
            var doc = XDocument.Parse(File.ReadAllText(file));
            foreach (var theme in doc.Descendants().Where(e => e.Name.LocalName == "ControlTheme" && TargetType(e) == "Button"))
            {
                var key = theme.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
                if (key is null) continue;
                if (!themes.TryGetValue(key, out var sets)) themes[key] = sets = new(StringComparer.Ordinal);
                foreach (var style in theme.Descendants().Where(e => e.Name.LocalName == "Style"))
                {
                    // a nested rule is gated on its own `^` step AND on every enclosing one
                    var classes = style.AncestorsAndSelf().TakeWhile(e => e != theme)
                        .Where(e => e.Name.LocalName == "Style")
                        .SelectMany(e => Step.Matches(e.Attribute("Selector")?.Value.Split("/template/")[0] ?? "")
                            .Where(s => s.Groups["type"].Value == "^").SelectMany(ClassesOf))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                    if (classes.Count > 0) sets.Add(string.Join('.', classes));
                }
            }

            // a button's OWN styles (`<Button.Styles>`) reach that button alone: ThemesOf reads them there
            foreach (var style in Styles(file).Where(s => s.Theme is null
                                                          && !s.Element.Ancestors().Any(a => a.Name.LocalName == "Button.Styles")))
            {
                if (style.Element.Elements().FirstOrDefault(e => e.Attribute("Property")?.Value == "Theme") is not { } setter
                    || ThemeReference.Match(setter.Attribute("Value")?.Value ?? "") is not { Success: true } reference)
                {
                    continue;
                }

                foreach (var part in style.Selector.Split(','))
                {
                    var classes = ButtonTargetClasses(part, allowAncestors: true);
                    if (classes.Count == 0) continue;
                    var steps = Regex.Split(part.Split("/template/")[0].Trim(), @"\s*>\s*|\s+");
                    var ancestors = steps[..^1].SelectMany(s => Step.Matches(s).SelectMany(ClassesOf)).ToArray();
                    setters.Add((ancestors, classes.ToHashSet(StringComparer.Ordinal), reference.Groups["key"].Value));
                }
            }
        }

        return new ThemeModel(themes, setters);
    }

    /// <summary>The themes a button can wear: its own <c>Theme</c>, or one a style
    /// in its scope sets by class (the dialog footer's primary, a button whose
    /// family follows a binding).</summary>
    private static IEnumerable<string> ThemesOf(XElement button, HashSet<string> classes, ThemeModel model)
    {
        if (button.Attribute("Theme") is { } local)
        {
            if (ThemeReference.Match(local.Value) is { Success: true } m) yield return m.Groups["key"].Value;
            yield break;
        }

        var ancestorClasses = button.Ancestors()
            .SelectMany(a => (a.Attribute("Classes")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (ancestors, needed, theme) in model.ThemeSetters)
        {
            if (needed.IsSubsetOf(classes) && ancestors.All(ancestorClasses.Contains))
                yield return theme;
        }

        foreach (var style in button.Elements().Where(e => e.Name.LocalName == "Button.Styles").Descendants()
                     .Where(e => e.Name.LocalName == "Style"))
        {
            if (style.Elements().FirstOrDefault(e => e.Attribute("Property")?.Value == "Theme") is { } setter
                && ThemeReference.Match(setter.Attribute("Value")?.Value ?? "") is { Success: true } m)
            {
                yield return m.Groups["key"].Value;
            }
        }
    }

    // ── markup plumbing ────────────────────────────────────────────────────

    private sealed record StyleNode(XElement Element, string Selector, string? Theme);

    /// <summary>Every <c>Style</c> in a file, with the key of the Button
    /// ControlTheme it sits in (null outside one).</summary>
    private static IEnumerable<StyleNode> Styles(string file)
    {
        foreach (var style in XDocument.Parse(File.ReadAllText(file)).Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            var selector = style.Attribute("Selector")?.Value;
            if (selector is null) continue;
            var theme = style.Ancestors().FirstOrDefault(a => a.Name.LocalName == "ControlTheme");
            if (theme is not null && TargetType(theme) != "Button") continue;
            yield return new StyleNode(style, selector,
                theme?.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value ?? (theme is null ? null : "?"));
        }
    }

    /// <summary>The classes of a selector part whose STYLED control is a Button
    /// (the step before any <c>/template/</c>), or none.</summary>
    private static List<string> ButtonTargetClasses(string part, bool allowAncestors = false)
    {
        var subject = part.Split("/template/")[0].Trim();
        var steps = Regex.Split(subject, @"\s*>\s*|\s+").Where(s => s.Length > 0).ToList();
        if (steps.Count == 0 || (!allowAncestors && steps.Count > 1)) return [];
        var m = Regex.Match(steps[^1], @"^(?:Button|:is\(Button\))((?:\.[A-Za-z0-9_\-]+|:[A-Za-z\-]+(?:\([^)]*\))?)+)$");
        return m.Success ? Regex.Matches(m.Groups[1].Value, @"\.([A-Za-z0-9_\-]+)").Select(c => c.Groups[1].Value).ToList() : [];
    }

    private static IEnumerable<string> ClassesOf(Match step) =>
        step.Groups["classes"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries);

    private static string? TargetType(XElement theme) => theme.Attribute("TargetType")?.Value.Split(':')[^1];

    private static IEnumerable<string> StyleFiles() => MarkupFiles();

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
