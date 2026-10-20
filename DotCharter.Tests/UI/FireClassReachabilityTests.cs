using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     Source-structure guards for the "Fire" design layer as it is used by the
///     Play Mode screens. Three of the four things that go wrong when a screen
///     is converted to it are INVISIBLE at build time and only one of them is a
///     crash:
///
///     <list type="number">
///         <item>
///             A <c>Fire*</c> class that matches no selector in
///             <c>Styles/Fire.axaml</c> is a silent no-op — no warning, no
///             exception, the control just renders in its default look. The
///             type-aware half matters as much as the name: <c>FireListRow</c>
///             exists for a <c>Border</c> AND for a <c>Button</c>, but
///             <c>FireBack</c> only for a <c>Button</c>, so a class put on the
///             wrong element is the same silent no-op.
///         </item>
///         <item>
///             A screen that paints its own ground HIDES
///             <c>MenuBackdropControl</c> — the animated glows / string lines /
///             audio-reactive EQ bars that render behind every Play Mode
///             screen. The mockups ship a flat <c>#07060c</c> because a web page
///             has nothing behind it; a converted screen that copies it loses a
///             designed part of the product, and that shipped once and was
///             rejected on sight.
///         </item>
///         <item>
///             A control that looks like an action and does nothing. The first
///             Fire front door shipped its practice queue as a bare
///             <c>Border</c> that looked exactly right and did nothing when
///             clicked; it was found in under a minute.
///         </item>
///     </list>
///
///     Asserted against the real XAML parsed as XML rather than a live headless
///     view: booting the app session for a visual-tree walk is the
///     <c>HeadlessSessionFixture</c> teardown trap the suite excludes from the
///     default gate. Same approach as <see cref="SongSelectLayoutStructureTests" />.
/// </summary>
public sealed class FireClassReachabilityTests
{
    /// <summary>The screens converted to the Fire layer from the owner's
    /// mockups. Each is looked up by <c>x:Name</c>, so a rename fails here
    /// rather than quietly skipping.</summary>
    private static readonly string[] ConvertedScreens =
    [
        // `ProgressHubRoot` (History + Trends) and `MenuDesign6Root` (the Play
        // Mode menu) left with their screens, 2026-09-19 and 2026-09-22.
        "StartScreenRoot", "SongSelectScreenRoot"
    ];

    /// <summary>An element+class pair declared anywhere in a Fire selector,
    /// e.g. <c>Border.FireListRow</c> or <c>ProgressBar.FireMeter.Warn</c>.</summary>
    private static readonly Regex SelectorPart = new(
        @"(?<el>[A-Za-z][A-Za-z0-9]*)(?<classes>(?:\.[A-Za-z0-9_]+)+)", RegexOptions.Compiled);

    /// <summary><c>:is(ToggleButton).FireSegmentItem.Tab</c> declares that pair
    /// for a <c>ToggleButton</c> as surely as writing the type would, but
    /// <see cref="SelectorPart" /> sees a <c>)</c> where it needs a letter and
    /// finds nothing. Unwrapping first is what keeps the widened scan from
    /// reporting the Online hub's tab strip as a typo.</summary>
    private static readonly Regex IsSelector = new(
        @":is\((?<el>[A-Za-z][A-Za-z0-9]*)\)", RegexOptions.Compiled);

    /// <summary>
    ///     <b>Scope is all of <c>Views/</c>, not Play Mode.</b> It was
    ///     <c>PlayModeView.axaml</c> alone until 0.60 — which is how four
    ///     <c>TextBlock Classes="FirePopoverKicker"</c> captions shipped in
    ///     <c>OverflowPopoverView</c>: <c>FirePopoverKicker</c> is declared once,
    ///     as <c>Grid.FirePopoverKicker</c>, so a TextBlock wearing it rendered
    ///     in the inherited font instead of the 9 px mono kicker step. Nothing
    ///     warned; the lint simply never looked at that file.
    /// </summary>
    [Fact]
    public void EveryFireClassUnderViews_MatchesASelectorForItsElementType()
    {
        var declared = DeclaredFireClasses();
        Assert.NotEmpty(declared);

        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        Assert.True(Directory.Exists(views), $"Views not found at {views}");

        var scanned = 0;
        var inspected = 0;
        var offenders = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            scanned++;
            foreach (var element in XDocument.Load(file).Descendants())
            {
                var el = element.Name.LocalName;
                foreach (var attribute in element.Attributes())
                {
                    // `Classes="FireGhost Compact"` — a whole class LIST. If any
                    // token is a Fire class the WHOLE list has to resolve for this
                    // element, because that is how a variant is declared
                    // (`Button.FireGhost.Compact`) and a stray token is exactly the
                    // silent no-op this test exists for.
                    if (attribute.Name.LocalName != "Classes" || attribute.Name.NamespaceName.Length > 0) continue;
                    var tokens = attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (!tokens.Any(t => t.StartsWith("Fire", StringComparison.Ordinal))) continue;
                    inspected++;
                    foreach (var token in tokens.Where(token => !declared.Contains((el, token))))
                    {
                        offenders.Add($"{Path.GetFileName(file)}: <{el} Classes=\"{attribute.Value}\"> — " +
                                      $"'{token}' matches no Fire selector for {el}");
                    }
                }
            }
        }

        // The scan must not pass by finding nothing to read.
        Assert.True(scanned > 80, $"only {scanned} views were scanned — the lint is looking in the wrong place");
        Assert.True(inspected > 500, $"only {inspected} Fire class lists were reached — the walk is broken");

        Assert.True(offenders.Count == 0,
            "Fire style classes that match no selector for the element they are worn on (a silent no-op — the " +
            "control renders in its default look with no warning and no exception):\n" + string.Join("\n", offenders));
    }

    /// <summary>
    ///     <b>A class declared ONLY in a view's own Styles works only inside that view</b>
    ///     (#242). The rig's descendant rules and its Path / Panel / Grid / Slider marks
    ///     moved out of the global Fire chain into <c>AmpFxRigStyles.axaml</c> and
    ///     <c>RigModuleEditorStyles.axaml</c> (each <c>StyleInclude</c>d by its view) and
    ///     into <c>ToneDesignCorePanel</c>'s Styles, so they attach to the rig's controls
    ///     and not to every control of their type in the app. The case above counts a
    ///     selector anywhere as "declared"; this one models the SCOPE: wherever such a
    ///     pair is worn, a view that declares it must reach the element
    ///     (<see cref="ViewStyleScopes" />: the view, the views it declares or builds in
    ///     code, the template dictionaries it merges, markup nested inside an instance of
    ///     it). Worn anywhere else — a dialog, a popover hosted by another screen, the
    ///     gallery — it is a silent no-op. A view whose host the markup does not name
    ///     (a dialog <c>DialogHost</c> builds, a <c>ViewLocator</c> data template) cannot be
    ///     placed and is counted, not judged. The rig's two grammar files have one owner
    ///     each and are in no theme chain, or they would be global again.
    /// </summary>
    [Fact]
    public void AClassDeclaredOnlyInAViewsOwnStyles_IsWornOnlyInsideThatView()
    {
        var root = TestPaths.RepoRoot();
        var themeChains = new[] { "DotCharter.UI/Styles/Theme.axaml", "DotCharter.Tone.UI/Styles/RigTheme.axaml" }
            .SelectMany(t => StyleFileText.FilesOf(Path.Combine(root, t.Replace('/', Path.DirectorySeparatorChar))))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var global = themeChains.SelectMany(f => ViewStyleScopes.SelectorPairs(XDocument.Load(f).Root!)).ToHashSet();
        foreach (var theme in MarkupRoots(root).SelectMany(f => XDocument.Load(f).Descendants())
                     .Where(e => e.Name.LocalName == "ControlTheme"))
        {
            global.UnionWith(ViewStyleScopes.SelectorPairs(theme));
        }

        var declaredBy = new Dictionary<(string, string), List<string>>();
        foreach (var (view, pairs) in ViewStyleScopes.Declared)
        {
            foreach (var pair in pairs.Where(p => !global.Contains(p)))
            {
                if (!declaredBy.TryGetValue(pair, out var views)) declaredBy[pair] = views = [];
                views.Add(view);
            }
        }

        var offenders = new List<string>();
        var worn = 0;
        var unplaced = 0;
        foreach (var file in MarkupRoots(root))
        {
            foreach (var element in XDocument.Load(file).Descendants())
            {
                var classes = (element.Attribute("Classes")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Concat(element.Attributes().Where(a => a.Name.LocalName.StartsWith("Classes.", StringComparison.Ordinal))
                        .Select(a => a.Name.LocalName["Classes.".Length..]));
                foreach (var cls in classes)
                {
                    if (!declaredBy.TryGetValue((element.Name.LocalName, cls), out var views)) continue;
                    worn++;
                    if (views.Any(v => ViewStyleScopes.Reaches(v, file, element))) continue;
                    // A wearer the markup cannot place is judged only for a grammar of the
                    // RIG (a DotCharter.Tone.UI view, every one of them derivably hosted): such
                    // a class worn anywhere unplaced is necessarily outside the rig.
                    if (!ViewStyleScopes.HasKnownHost(file) && !views.Any(IsRigView))
                    {
                        unplaced++;
                        continue;
                    }

                    offenders.Add($"{Path.GetFileName(file)}: <{element.Name.LocalName} Classes=\"…{cls}…\"> — " +
                                  $"declared only in {string.Join(", ", views)}'s own Styles, which do not reach it");
                }
            }
        }

        foreach (var (grammar, owner) in new[] { ("AmpFxRigStyles.axaml", "AmpFxRigView"), ("RigModuleEditorStyles.axaml", "RigModuleEditorView") })
        {
            var included = ViewStyleScopes.IncludedBy.Single(kv => Path.GetFileName(kv.Key) == grammar);
            Assert.Equal(new[] { owner }, included.Value);
            Assert.DoesNotContain(included.Key, themeChains);
        }

        // The rig's scoped grammar is what this guards; it must be found and worn.
        Assert.True(declaredBy.Count > 20, $"only {declaredBy.Count} view-scoped (element, class) pairs found — the scan is broken");
        Assert.True(worn > 40, $"only {worn} elements wear a view-scoped class — the walk is broken");
        Assert.True(unplaced < worn / 2, $"{unplaced} of {worn} wearers have no host the markup names — the host walk is broken");
        Assert.True(offenders.Count == 0,
            "A class whose only selector is in a view's own Styles is a silent no-op outside that view (#242):\n  " +
            string.Join("\n  ", offenders));
    }

    private static bool IsRigView(string view) =>
        ViewStyleScopes.Views[view].Contains($"{Path.DirectorySeparatorChar}DotCharter.Tone.UI{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static IEnumerable<string> MarkupRoots(string root) =>
        new[] { "DotCharter.UI", "DotCharter.Tone.UI", "DotCharter.RigLab", "DotCharter.UI.Gallery" }
            .SelectMany(r => Directory.EnumerateFiles(Path.Combine(root, r), "*.axaml", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    ///     No converted screen may paint an opaque ground over
    ///     <c>MenuBackdropControl</c>. The translucent Fire washes are fine —
    ///     they TINT the backdrop, which is what they are for.
    /// </summary>
    [Fact]
    public void TheConvertedScreens_LetTheAnimatedBackdropThrough()
    {
        string[] grounds = ["FireGround", "FireGroundScreen", "FireInk", "AppBackground", "CardBackground"];

        foreach (var name in ConvertedScreens)
        {
            var root = ScreenRoot(name);
            Assert.Null(root.Attribute("Background"));

            var painted = root.DescendantsAndSelf()
                .SelectMany(e => e.Attributes())
                .Where(a => a.Name.LocalName is "Background")
                .Where(a => grounds.Any(g => a.Value.Contains(g, StringComparison.Ordinal)))
                .Select(a => a.Value)
                .ToList();

            Assert.True(painted.Count == 0,
                $"'{name}' paints an opaque ground ({string.Join(", ", painted)}) — that hides the animated " +
                "MenuBackdropControl behind every Play Mode screen. Use a translucent Border.FireWash instead.");
        }

        // …and each one must actually HAVE the wash, or the mockup's purple
        // light is simply missing rather than being deferred to the backdrop.
        foreach (var name in ConvertedScreens)
        {
            Assert.Contains(ScreenRoot(name).Descendants(),
                e => e.Name.LocalName == "Border"
                     && (e.Attribute("Classes")?.Value.Contains("FireWash", StringComparison.Ordinal) ?? false));
        }
    }

    /// <summary>
    ///     Every button on a converted screen must DO something. A
    ///     <c>ToggleButton</c> counts as wired through a two-way
    ///     <c>IsChecked</c> (that is how the sort-direction toggle works), and a
    ///     button that owns a <c>Flyout</c> counts through the flyout.
    /// </summary>
    [Fact]
    public void EveryButtonOnTheConvertedScreens_IsWired()
    {
        var offenders = new List<string>();
        foreach (var name in ConvertedScreens)
        {
            foreach (var button in ScreenRoot(name).Descendants()
                     .Where(e => e.Name.LocalName is "Button" or "ToggleButton" or "RepeatButton"))
            {
                var wired = button.Attribute("Command") != null
                            || button.Attribute("IsChecked") != null
                            || button.Elements().Any(c => c.Name.LocalName.EndsWith(".Flyout", StringComparison.Ordinal));
                if (!wired)
                    offenders.Add($"{name}: <{button.Name.LocalName} Content=\"{button.Attribute("Content")?.Value}\">");
            }
        }

        Assert.True(offenders.Count == 0,
            "Buttons with no Command, no IsChecked and no Flyout — decoration that looks like an action:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>
    ///     Every element+class pair a selector declares, anywhere in
    ///     <c>DotCharter.UI</c>. A selector may name several
    ///     (`Button.FireCta TextBlock.FireCtaLabel`,
    ///     `ListBox.FireList &gt; ListBoxItem:selected Border.FireListRow`), and
    ///     every one of them is a legal way to wear that class.
    ///     <para>
    ///         <b>The whole project, not <c>Styles/Fire.axaml</c>.</b> Once the
    ///         scan covers all of <c>Views/</c> the declarers do too: Fire
    ///         selectors also live in <c>Styles/FireControls.axaml</c>,
    ///         <c>Styles/FireMenuChrome.axaml</c>, the editor's
    ///         <c>Views/Editor/InspectorStyles*</c> and
    ///         <c>Views/Shell/ShellChromeStyles.axaml</c>, and a view may declare
    ///         its own in <c>&lt;UserControl.Styles&gt;</c>. Reading one grammar
    ///         would report every one of those as a typo.
    ///     </para>
    ///     <para>
    ///         The trade is that SCOPE is not modelled: a class declared inside
    ///         one view's local styles counts as declared for every view. What
    ///         survives is the failure this lint exists for — a name/element pair
    ///         that is declared NOWHERE, which is what a mistyped or
    ///         wrong-element class always is.
    ///     </para>
    /// </summary>
    private static HashSet<(string Element, string Class)> DeclaredFireClasses()
    {
        var declared = new HashSet<(string, string)>();
        var ui = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");

        foreach (var file in UiSourceTree.EnumerateFiles(ui, "*.axaml", SearchOption.AllDirectories))
        {
            foreach (var selector in Selectors(file).Select(s => IsSelector.Replace(s, "${el}")))
            {
                // `^:pointerover /template/ Border#PART_Track` and friends carry no
                // class at all; SelectorPart simply finds nothing in them.
                foreach (Match part in SelectorPart.Matches(selector))
                {
                    var el = part.Groups["el"].Value;
                    foreach (var cls in part.Groups["classes"].Value
                                 .Split('.', StringSplitOptions.RemoveEmptyEntries))
                    {
                        declared.Add((el, cls));
                    }
                }
            }
        }

        // `Theme="{DynamicResource FireNavRowToggle}"` reaches the same look
        // through a ControlTheme rather than a class, so a screen that wears
        // the class is covered by the `ToggleButton.FireNavRow` style itself —
        // nothing extra to register here.
        return declared;
    }

    /// <summary>
    ///     Every selector in <paramref name="file" />, with the nesting <c>^</c> of a
    ///     style inside a <c>ControlTheme</c> spelled as the theme's target type: the
    ///     button families are ControlThemes (#240), so <c>^.FireGhost.Compact</c> in
    ///     <c>FireGhostButton</c> declares the pair for a <c>Button</c>. Which theme a
    ///     button must wear for its class to apply is
    ///     <see cref="ButtonControlThemeTests" />'s question, not this one's.
    /// </summary>
    private static IEnumerable<string> Selectors(string file)
    {
        foreach (var style in XDocument.Parse(UiSourceTree.ReadAllText(file)).Descendants()
                     .Where(e => e.Name.LocalName == "Style"))
        {
            var selector = style.Attribute("Selector")?.Value;
            if (selector is null) continue;
            var theme = style.Ancestors().FirstOrDefault(a => a.Name.LocalName == "ControlTheme");
            var target = theme?.Attribute("TargetType")?.Value.Split(':')[^1];
            yield return target is null ? selector : selector.Replace("^", target, StringComparison.Ordinal);
        }
    }

    private static XElement ScreenRoot(string name)
    {
        var root = PlayModeMarkup.Document().Descendants().SingleOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        Assert.True(root != null,
            $"'{name}' is not in the Play Mode markup — a renamed screen root silently skips every guard here");
        return root!;
    }
}
