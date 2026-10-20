using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Source lints for the "Fire" design layer
///     (<c>DotCharter.UI/Styles/Fire.axaml</c> + <c>FireTokens.axaml</c>,
///     written up in <c>docs/agents/ui/fire-design-system.md</c>).
///     <para>
///         Both cases guard a failure with NO runtime signal, which is why they
///         are source lints rather than assertions on a live control.
///         <see cref="ButtonStyleClassTests" /> covers the same class-typo trap
///         for <c>&lt;Button&gt;</c> only; the Fire vocabulary hangs off
///         Borders, TextBlocks, ToggleButtons, Sliders, ComboBoxes and
///         ProgressBars, so almost none of it is in that net — and a
///         <c>Classes="FirePanel"</c> that matches nothing renders as a plain
///         transparent Border that still measures, which is precisely the shape
///         every construction smoke test passes straight through.
///     </para>
/// </summary>
public sealed class FireStyleClassTests
{
    /// <summary>
    ///     Deliberately EMPTY. An entry here is a hole in the lint. A genuine
    ///     case would be a Fire class only ever attached from code-behind; add
    ///     it WITH the reason if one appears.
    /// </summary>
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal);

    /// <summary>
    ///     Every <c>Fire*</c> class in markup — whether written as a literal
    ///     <c>Classes="FirePanel Warn"</c> or bound as
    ///     <c>Classes.FireX="{Binding …}"</c> — must have a selector that can
    ///     match it, and that selector must live in <c>Fire.axaml</c>. The
    ///     second half is the part that keeps the layer a LAYER: a Fire mark
    ///     re-invented inline in one view is how nine screens stop being one
    ///     screen.
    ///     <para>
    ///         The one other home is the Styles of a view whose scope REACHES the
    ///         file (#242): the rig's descendant rules and its Path / Panel / Grid
    ///         marks live in <c>AmpFxRigStyles.axaml</c>, <c>RigModuleEditorStyles.axaml</c>
    ///         and <c>ToneDesignCorePanel</c>'s own Styles, so they attach to the rig's
    ///         controls only. A class declared there and worn outside that view is
    ///         reported like any other no-op (<see cref="ViewStyleScopes" />;
    ///         <c>FireClassReachabilityTests</c> checks the same per element).
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryFireStyleClassInMarkup_HasASelectorInFireAxaml()
    {
        var uiRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
        var fireAxaml = UiSourceTree.Resolve(Path.Combine(uiRoot, "Styles", "Fire.axaml"));
        Assert.True(File.Exists(fireAxaml), $"Fire.axaml not found at {fireAxaml}");

        // Fire.axaml is a CONTAINER of `<StyleInclude>`s over `Styles/Fire/`;
        // reading it alone would find seven include lines and no selectors at
        // all, and this lint would pass by having nothing to check.
        var declarers = StyleFileText.FilesOf(fireAxaml)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(declarers.Count > 1,
            "Fire.axaml merges nothing — either the split was undone or the include scan is broken");

        var defined = ClassesBoundBySelectorsIn(StyleFileText.ReadWithIncludes(fireAxaml));
        Assert.Contains("FirePanel", defined); // the scan must not pass by finding nothing

        var offenders = new List<string>();
        foreach (var file in AxamlFiles(uiRoot))
        {
            // Fire.axaml and its parts DECLARE the selectors; they are not consumers.
            if (declarers.Contains(Path.GetFullPath(file))) continue;
            var markup = UiSourceTree.ReadAllText(file);
            var scoped = ScopedClassesReaching(file, markup);

            foreach (var (cls, line) in FireClassesUsedIn(markup))
            {
                if (defined.Contains(cls) || scoped.Contains(cls) || Exempt.Contains(cls)) continue;
                offenders.Add($"{Path.GetFileName(file)}:{line}  Classes=\"…{cls}…\" — " +
                              "no Fire.axaml selector can match this; the control renders in its " +
                              "default look, with no warning and no exception");
            }
        }

        Assert.True(offenders.Count == 0,
            "Fire style class(es) that silently do nothing:\n  " +
            string.Join("\n  ", offenders.Distinct()));
    }

    /// <summary>
    ///     A Play Mode screen may NOT paint a Fire ground over the animated
    ///     <c>MenuBackdropControl</c>.
    ///     <para>
    ///         The mockups ship a flat <c>#07060c</c> because a web page has
    ///         nothing behind it. Play Mode does: glows, string lines and
    ///         audio-reactive EQ bars, behind every screen, and a screen that
    ///         fills over them reads as a rendering fault — it shipped once on
    ///         the front door and was rejected on sight. The translucent
    ///         <c>FireWash*</c> brushes are the sanctioned way to tint it, so
    ///         only the two opaque GROUND brushes are banned here.
    ///     </para>
    /// </summary>
    [Fact]
    public void NoPlayModeSurface_PaintsAFireGroundOverTheBackdrop()
    {
        var playModeDir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "PlayMode"));
        Assert.True(Directory.Exists(playModeDir), $"Play Mode views not found at {playModeDir}");

        var offenders = new List<string>();
        foreach (var file in AxamlFiles(playModeDir))
        {
            var markup = UiSourceTree.ReadAllText(file);
            foreach (Match m in Regex.Matches(markup,
                         @"Background=""\{(?:Dynamic|Static)Resource\s+(?<key>FireGround|FireGroundScreen|FireInk)\}"""))
            {
                var line = markup.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}  Background=\"{{…{m.Groups["key"].Value}}}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "An opaque Fire ground is painted over MenuBackdropControl — use a translucent " +
            "Border.FireWash instead, which tints the backdrop rather than hiding it:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     The five SHARED grammar files — the ~38 dialogs', the eight settings
    ///     tabs', the chart editor's chrome RAILS, its WORKSPACE (the track
    ///     rail, the pane headers, the minimap frame and the 3D preview's editor
    ///     bar) and the INSPECTOR's — were converted onto
    ///     the Fire layer, and the layer's
    ///     third rule is that its ladders are SELF-CONTAINED: a Fire surface
    ///     reaches for <c>FireRadius*</c> / <c>FireSize*</c> / <c>FirePad*</c> /
    ///     <c>FireGap*</c> / <c>FireSpace*</c> and nothing else. Several steps
    ///     of the app's own scale simply do not exist on the Fire one (radius
    ///     6 / 14, size 14 / 18, spacing 8 / 28), so ONE app token left behind
    ///     in a converted file is a row that is 2 px off or a corner that is
    ///     4 px rounder than its neighbour — invisible in a diff, and exactly
    ///     the "half-converted screen" the rule exists to prevent.
    ///     <para>
    ///         A source lint because there is no runtime signal: both ladders
    ///         resolve, so the surface renders perfectly and merely looks like
    ///         two designs.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("Views/Dialogs/DialogStyles.axaml")]
    [InlineData("Views/Settings/SettingsStyles.axaml")]
    [InlineData("Views/Shell/ShellChromeStyles.axaml")]
    [InlineData("Views/Editor/InspectorStyles.axaml")]
    [InlineData("Views/Editor/EditorWorkspaceStyles.axaml")]
    [InlineData("Views/Shell/ShellStyles.axaml")]
    public void EachConvertedGrammar_ReachesOnlyForFireLadderTokens(string relativePath)
    {
        // The app's own ladders, by key PREFIX. A Fire key starts with "Fire",
        // so `FireSpaceXl` is not caught by `Space` — the anchors matter.
        var appLadder = new Regex(
            @"\{(?:Dynamic|Static)Resource\s+(?<key>(?:Space|Padding|Gap|Radius|FontSize)[A-Za-z]*)\s*\}",
            RegexOptions.Compiled);

        var markup = SourceText.StripMarkupComments(GrammarText(relativePath));

        var offenders = appLadder.Matches(markup)
            .Select(m => m.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{relativePath} still reaches for the APP ladder after the Fire conversion: " +
            string.Join(", ", offenders) + ". The Fire ladders are self-contained — several app " +
            "steps do not exist on them, so mixing the two is how a surface ends up half-converted.");
    }

    /// <summary>
    ///     …and the same two files must actually SPEAK the layer: a conversion
    ///     that reaches for no app token because it reaches for no token at all
    ///     would pass the lint above while asserting nothing.
    /// </summary>
    [Theory]
    [InlineData("Views/Dialogs/DialogStyles.axaml")]
    [InlineData("Views/Settings/SettingsStyles.axaml")]
    [InlineData("Views/Shell/ShellChromeStyles.axaml")]
    [InlineData("Views/Editor/InspectorStyles.axaml")]
    [InlineData("Views/Editor/EditorWorkspaceStyles.axaml")]
    [InlineData("Views/Shell/ShellStyles.axaml")]
    public void EachConvertedGrammar_IsWrittenInTheFireVocabulary(string relativePath)
    {
        var markup = SourceText.StripMarkupComments(GrammarText(relativePath));

        var fireKeys = Regex.Matches(markup, @"\{(?:Dynamic|Static)Resource\s+(?<key>Fire[A-Za-z0-9]*)\s*\}")
            .Select(m => m.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(fireKeys.Count >= 20,
            $"{relativePath} names only {fireKeys.Count} Fire tokens — it is not on the layer.");

        // The three ladders the shapes, the type and the rhythm come from.
        Assert.Contains(fireKeys, k => k.StartsWith("FireRadius", StringComparison.Ordinal));
        Assert.Contains(fireKeys, k => k.StartsWith("FireSize", StringComparison.Ordinal));
        Assert.Contains(fireKeys, k => k.StartsWith("FirePad", StringComparison.Ordinal)
                                       || k.StartsWith("FireGap", StringComparison.Ordinal));

        // …and a real type HIERARCHY: the mono step, which every Fire surface
        // has (the eyebrow is the layer's most repeated mark), plus at least one
        // more face beside it.
        //
        // It used to demand `FireFontDisplay` specifically, and the workspace
        // grammar is the case that showed why that is the wrong test: the track
        // gutter, the pane headers and the 3D strip are CHROME — mono captions
        // over Space Grotesk labels, with no screen title anywhere. Adding a
        // Chakra Petch element to satisfy a lint would have been the lint
        // choosing the design.
        //
        // Graphite (2026-09-22) moved every LABEL to the UI face — mono is for
        // figures — so a chrome grammar with no figures legitimately names one
        // face. What still proves conversion is that it names a Fire face at all.
        Assert.Contains(fireKeys, k => k.StartsWith("FireFont", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Neither shared grammar file may carry a <c>Cursor</c> or a
    ///     <c>Transitions</c> setter.
    ///     <para>
    ///         This is the one guard for <c>SettingsStyles.axaml</c>: a
    ///         <c>Cursor</c> setter needs a platform and a <c>Transitions</c>
    ///         setter the dispatcher's thread, and every settings tab includes the
    ///         file. <c>DialogStyles.axaml</c> carries the same ban in its own
    ///         header and in docs/agents/ui/settings.md but had nothing enforcing
    ///         it, so the Fire conversion is where it gets a guard: the Fire
    ///         button CLASSES both set <c>Cursor="Hand"</c>, and copying one of
    ///         them into a shared style file instead of referencing it is the
    ///         obvious next mistake.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("Views/Dialogs/DialogStyles.axaml")]
    [InlineData("Views/Settings/SettingsStyles.axaml")]
    [InlineData("Views/Shell/ShellChromeStyles.axaml")]
    [InlineData("Views/Editor/EditorWorkspaceStyles.axaml")]
    [InlineData("Views/Shell/ShellStyles.axaml")]
    public void NeitherSharedGrammar_CarriesACursorOrTransitionsSetter(string relativePath)
    {
        var markup = GrammarText(relativePath);

        Assert.DoesNotContain("Property=\"Cursor\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<Transitions", markup, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The INSPECTOR grammar is the fourth converted file and is held to
    ///     HALF of the rule above — and the half it keeps is the one that
    ///     matters here.
    ///     <para>
    ///         <b>No <c>Transitions</c>, ever.</b> All eight inspector tabs are
    ///         <c>IsVisible</c>-gated and a hidden control's style clocks keep
    ///         ticking (DotCharter.UI/AGENTS.md), and instant state changes are
    ///         the Fire layer's own rule — every other converted surface is
    ///         already instant, so one animated pane reads as the odd one out.
    ///         Two setters were removed by the conversion (the M/S buttons and
    ///         the technique chip) and a third from the tab strip; this is what
    ///         stops them coming back.
    ///     </para>
    ///     <para>
    ///         <b>A <c>Cursor</c> setter IS allowed</b>, unlike in the other
    ///         three. That ban exists because <c>SettingsSearchTests</c>
    ///         constructs settings tabs with NO Avalonia platform, where
    ///         building a <c>Cursor</c> resolves <c>ICursorFactory</c> and
    ///         throws; nothing constructs an inspector view that way — every
    ///         one of them is built inside the real headless session by
    ///         <c>Perf/InspectorConstructionUiPerfSmokeTests</c>. Removing the
    ///         setters would cost 42 <c>Button.Subtle</c> actions and every
    ///         chip and disclosure their hand cursor for no gain. Asserting the
    ///         difference (rather than leaving the file off the theory) is what
    ///         makes it a decision instead of an omission.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheInspectorGrammar_CarriesNoTransitionsSetter_ButMayCarryACursor()
    {
        var markup = GrammarText("Views/Editor/InspectorStyles.axaml");

        Assert.DoesNotContain("<Transitions", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Property=\"Transitions\"", markup, StringComparison.Ordinal);

        // The other half of the claim: this file really does reach for a
        // Cursor, so "no Transitions because no setters at all" cannot pass and
        // a future removal of every Cursor is a deliberate act, not a drift.
        Assert.Contains("Property=\"Cursor\"", markup, StringComparison.Ordinal);
    }

    /// <summary>
    ///     …and the same ban reaches the three views that include the inspector
    ///     grammar and carry style blocks of their own. Two of them
    ///     (<c>StemMixerView</c>, the old tab strip in <c>InspectorView</c>)
    ///     held a <c>Transitions</c> setter until the 0.50 conversion, and a
    ///     lint on the grammar file alone would not have seen either.
    /// </summary>
    [Theory]
    [InlineData("Views/Editor/InspectorView.axaml")]
    [InlineData("Views/Editor/StemMixerView.axaml")]
    [InlineData("Views/Editor/Inspector/IssuesInspectorView.axaml")]
    public void NoInspectorSurface_AnimatesItsOwnChrome(string relativePath)
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", relativePath)));
        Assert.DoesNotContain("<Transitions", markup, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The class names the cross-tab settings search keys on are LOAD-BEARING
    ///     and a restyle must not rename or drop them: every tab's rows are
    ///     <c>Grid.FormRow</c>, its groups <c>Border.SettingsGroup</c> +
    ///     <c>TextBlock.GroupTitle</c>/<c>GroupSubtitle</c>, and the Advanced
    ///     sections a <c>ToggleButton.AdvancedHeader</c>.
    ///     <para>
    ///         This proves the STYLE FILE still dresses them, which is the
    ///         half a restyle breaks — a row that keeps its class and loses its
    ///         style still measures and still searches, it just stops looking
    ///         like a row.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheSettingsGrammar_StillDressesEveryRowAndGroupClass()
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings", "SettingsStyles.axaml")));
        // A family that became a ControlTheme (#428) states its class as `^.FormRow`
        // inside `<ControlTheme TargetType="Grid">`: read that as `Grid.FormRow`.
        var selectors = System.Xml.Linq.XDocument.Parse(markup).Descendants()
            .Where(e => e.Name.LocalName == "Style" && e.Attribute("Selector") is not null)
            .Select(e => e.Ancestors().FirstOrDefault(a => a.Name.LocalName == "ControlTheme")?.Attribute("TargetType")?.Value is { } type
                ? e.Attribute("Selector")!.Value.Replace("^", type, StringComparison.Ordinal)
                : e.Attribute("Selector")!.Value)
            .ToList();

        string[] required =
        [
            "Grid.FormRow", "Border.SettingsGroup", "TextBlock.GroupTitle", "TextBlock.GroupSubtitle",
            "ToggleButton.AdvancedHeader"
        ];

        foreach (var part in required)
        {
            // A WHOLE-token match, not a substring: `TextBlock.GroupSubtitleX`
            // contains `TextBlock.GroupSubtitle`, so a plain Contains would sail
            // straight through the rename this exists to catch (it did, once).
            var whole = new Regex($@"(?<![A-Za-z0-9_.]){Regex.Escape(part)}(?![A-Za-z0-9_])");
            Assert.True(selectors.Any(s => whole.IsMatch(s)),
                $"no selector in SettingsStyles.axaml mentions '{part}' — the cross-tab search keys on it, " +
                "so a restyle that drops it breaks search silently");
        }
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>A shared grammar AS THE APP SEES IT — the file plus everything
    /// it merges. Several of these are containers of <c>&lt;StyleInclude&gt;</c>s
    /// now, and a lint that read only the container would assert over a handful
    /// of include lines.</summary>
    private static string GrammarText(string relativePath) =>
        StyleFileText.ReadWithIncludes(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", relativePath.Replace('/', Path.DirectorySeparatorChar))));

    /// <summary>Class names any selector in the given markup can bind to. Both
    /// spellings matter: a top-level <c>Selector="Border.FirePanel"</c> and the
    /// nested <c>^:checked</c>/<c>^.Class</c> form used inside a
    /// ControlTheme.</summary>
    private static HashSet<string> ClassesBoundBySelectorsIn(string markup)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(markup, @"Selector=""[^""]*"""))
        {
            foreach (Match c in Regex.Matches(m.Value, @"\.([A-Za-z_][A-Za-z0-9_]*)"))
                defined.Add(c.Groups[1].Value);
        }

        return defined;
    }

    /// <summary>Every class token in this markup that belongs to the Fire
    /// vocabulary, with its line number. A literal <c>Classes="A B"</c>
    /// contributes each token; a <c>Classes.Foo="{Binding}"</c> contributes
    /// <c>Foo</c>. Only tokens starting with <c>Fire</c> are returned — the
    /// modifier classes beside them (<c>Warn</c>, <c>selected</c>, …) are shared
    /// with the app's three older grammars and are ButtonStyleClassTests'
    /// territory, not this one's.</summary>
    private static IEnumerable<(string Class, int Line)> FireClassesUsedIn(string markup)
    {
        foreach (Match m in Regex.Matches(markup, @"\bClasses=""(?<classes>[^""{]+)"""))
        {
            var line = markup.Take(m.Index).Count(c => c == '\n') + 1;
            foreach (var cls in m.Groups["classes"].Value
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (cls.StartsWith("Fire", StringComparison.Ordinal))
                    yield return (cls, line);
            }
        }

        foreach (Match m in Regex.Matches(markup, @"\bClasses\.(?<class>Fire[A-Za-z0-9_]*)="))
        {
            yield return (m.Groups["class"].Value,
                markup.Take(m.Index).Count(c => c == '\n') + 1);
        }
    }

    /// <summary>The classes declared by the own Styles of every view that reaches this
    /// file: it is in the view's scope, or it nests markup inside an instance of the view.</summary>
    private static HashSet<string> ScopedClassesReaching(string file, string markup) =>
        ViewStyleScopes.Declared
            .Where(v => ViewStyleScopes.ScopeFiles[v.Key].Contains(Path.GetFullPath(file))
                        || Regex.IsMatch(markup, $@"<(?:[A-Za-z0-9_]+:)?{v.Key}[\s/>]"))
            .SelectMany(v => v.Value.Select(p => p.Class))
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> AxamlFiles(string root) =>
        UiSourceTree.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories);
}
