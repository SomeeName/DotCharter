using System.Text.RegularExpressions;
using Avalonia.Media;
using DotCharter.Tone.UI.Converters;

namespace DotCharter.Tests.UI;

/// <summary>
///     The shell's CHROME, after the 0.50 Fire conversion and the 0.62 Graphite
///     one: the menu bar, the ACTIVITY RAIL that carries the app's
///     destinations, the tool rail, the status rail and the dropdowns they open.
///     <para>
///         Source lints, because every failure this file guards is silent. A
///         deleted rail whose commands went with it is a feature that is
///         simply unreachable; a rail row bound to the wrong index
///         navigates to the wrong screen and looks perfect; a Fluent popup key
///         that drifts from the Fire token it restates leaves one dropdown a
///         different colour from the rest, which nothing else in the gate can
///         see. The LAYOUT half lives in
///         <c>Perf/ShellConstructionUiPerfSmokeTests</c>.
///     </para>
/// </summary>
public sealed class EditorChromeTests
{
    // ── The rail is gone, and nothing went with it ──────────────────────────

    /// <summary>
    ///     The 54 px icon rail and the `IconBtn` theme that only it wore are
    ///     deleted, and they must STAY deleted — a shell markup is easy to
    ///     re-grow by copy-paste from an older branch.
    /// </summary>
    [Fact]
    public void TheIconRail_AndTheThemeThatOnlyItWore_AreGone()
    {
        Assert.DoesNotContain("x:Name=\"SideRail\"", MainWindowMarkup(), StringComparison.Ordinal);
        Assert.DoesNotContain("SideRail", MainWindowCodeBehind(), StringComparison.Ordinal);

        var themes = UiSourceTree.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "ControlThemes.axaml"));
        Assert.DoesNotContain("x:Key=\"IconBtn\"", themes, StringComparison.Ordinal);

        // …and no view still asks for it. A `Theme="{StaticResource IconBtn}"`
        // against a missing key throws at parse time in some Avalonia builds and
        // renders an unthemed button in others; neither is a thing to find out
        // from a screenshot.
        foreach (var file in UiSourceTree.EnumerateFiles(
                     Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.axaml", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("IconBtn", UiSourceTree.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     <b>Every destination the rail owned is still reachable.</b> Three of
    ///     its eight buttons were the only route to their surface, and Play Mode
    ///     was the worst: with <c>AppConfig.StartInPlayMode</c> off, deleting the
    ///     rail without a replacement would have made the whole game shell
    ///     unreachable from a running editor. This is the reachability half of
    ///     the removal and it is worth more than the styling half.
    /// </summary>
    [Fact]
    public void EveryDestinationTheRailOwned_IsStillReachable()
    {
        var menu = MainMenuSource.ReadAllText();
        var toolbar = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "ToolbarView.axaml")));

        // Navigation → the ACTIVITY RAIL (the tab indices, asserted below) plus
        // the File menu, which keeps its Play Mode row as a second door. The
        // song browser's OWN File-menu row left in 0.59 (the menu sat AT the
        // 14-entry cap when Open Recent arrived, and the row duplicated the
        // always-visible LIBRARY destination); the rail assertion below is what
        // holds its reachability now, and the palette keeps `file.songBrowser`.
        Assert.Contains("OpenPlayModeCommand", menu, StringComparison.Ordinal);
        Assert.Contains("file.songBrowser", UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
                TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs"))),
            StringComparison.Ordinal);

        // Panels → the tool rail's right-hand cluster. The View menu carries
        // them too, but a menu is not a toggle you can see the state of.
        foreach (var flag in new[] { "IsInspectorVisible", "IsPreviewVisible", "IsSheetMusicVisible" })
        {
            Assert.True(toolbar.Contains(flag, StringComparison.Ordinal),
                $"the tool rail no longer toggles {flag} — the icon rail was the other place it lived, " +
                "and the icon rail is gone");
        }

        // The rig is the activity rail's Rig row, which sits beside the toolbar in the
        // editor; a second toggle on the toolbar was one verb in two places (2026-09-23).
        Assert.DoesNotContain("TogglePedalboardCommand", toolbar, StringComparison.Ordinal);
        var rail = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "ActivityRailView.axaml")));
        Assert.Contains("IsChecked=\"{Binding ToneRig.IsPedalboardOpen, Mode=TwoWay}\"", rail, StringComparison.Ordinal);

        // …and that door writes the property, so opening must sync the cards in the property's
        // own hook: the sync lived in a command only the removed toolbar toggle called.
        var rig = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell",
            "ToneRigViewModel.cs")));
        var hook = rig[rig.IndexOf("partial void OnIsPedalboardOpenChanged", StringComparison.Ordinal)..];
        Assert.Contains("Pedalboard?.Sync();", hook[..hook.IndexOf("SyncRigCloseButton();", StringComparison.Ordinal)],
            StringComparison.Ordinal);
    }

    // ── The activity rail ─────────────────────────────────────────

    /// <summary>
    ///     The rail drives <c>SelectedMainTabIndex</c> — the SAME property the
    ///     0.50 icon rail and the 0.50–0.62 menu-bar switcher drove — with the
    ///     indices the shell's own constants name, and it does it with
    ///     RadioButtons.
    ///     <para>
    ///         A ToggleButton unchecks itself when the already-current row is
    ///         clicked again, so the rail would report no destination at all
    ///         until the next real switch. That is the documented §6.4 trap and
    ///         it is invisible until someone clicks the tab they are on. The rig
    ///         row IS a ToggleButton and that is correct — it is an overlay with
    ///         two genuine states, not a destination — so the rule this asserts
    ///         is per-row: anything carrying a tab index is a RadioButton.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheActivityRail_DrivesTheShellsOwnTabIndices_WithRadioButtons()
    {
        var rail = RailMarkup();

        Assert.Contains("Classes=\"FireActivityRail\"", rail, StringComparison.Ordinal);

        foreach (Match row in Regex.Matches(rail, @"<(?<tag>RadioButton|ToggleButton)(?<body>.*?)>",
                     RegexOptions.Singleline))
        {
            var body = row.Groups["body"].Value;
            if (!body.Contains("ConverterParameter=", StringComparison.Ordinal)) continue;
            Assert.True(row.Groups["tag"].Value == "RadioButton",
                "a rail row bound to a tab index is a ToggleButton — it will unlight itself when the " +
                "destination it already shows is clicked again");
        }

        // Chart 0 / Song browser 1 / Settings 2 — read off MainWindowViewModel
        // rather than restated, so renumbering the tabs fails HERE and not in a
        // bug report about the Settings row opening the song browser.
        foreach (var (field, expected) in ShellTabIndices())
        {
            var parameter = $"ConverterParameter={expected}";
            Assert.True(rail.Contains(parameter, StringComparison.Ordinal),
                $"no rail row carries {parameter}, which is {field} — one of the destinations is " +
                "bound to the wrong tab");
        }

        // Play Mode is deliberately NOT a row - the rail only exists on the
        // workbench surfaces, and a run hides every piece of chrome.
        // `UI/PlayModeReachableFromEditorTests` owns that rule; here it is only
        // the arithmetic below that would otherwise drift silently.

        // Two-way, or clicking a row lights it and navigates nowhere. Four
        // destinations plus the rig's own two-way IsPedalboardOpen.
        Assert.Equal(5, Regex.Matches(rail, @"Mode=TwoWay").Count);
        Assert.Equal(4, Regex.Matches(rail, @"IntEqualsConverter\.Instance").Count);
    }

    /// <summary>
    ///     Every rail row carries a localized caption and an automation name. An
    ///     11 px caption under a glyph with no name is a mystery control for a
    ///     screen reader, and a tooltip is not a name — it is announced on hover,
    ///     the one gesture the reader who needs it does not have. A tooltip is
    ///     optional: it was required while the rail was icons only, and once every
    ///     row printed its caption three of them only repeated it (2026-09-24,
    ///     <c>RedundantTooltipLintTests</c>).
    /// </summary>
    [Fact]
    public void EveryRailRow_IsCaptionedAndNamed()
    {
        var rail = RailMarkup();

        foreach (var key in new[] { "Nav_Home", "Nav_Chart", "Nav_Library", "Nav_Settings", "Nav_Rig" })
        {
            Assert.True(rail.Contains($"localization:Loc {key}", StringComparison.Ordinal),
                $"the rail does not use {key} — a row caption is not localized");
        }

        var rows = Regex.Matches(rail, @"<(?:RadioButton|ToggleButton)\s+x:Name=""(?<n>Rail\w+)""").Count;
        Assert.Equal(5, rows);
        Assert.Equal(rows, Regex.Matches(rail, @"AutomationProperties\.Name=").Count);
    }

    /// <summary>
    ///     The rail is the shell's navigation and the menu bar no longer
    ///     duplicates it. Two controls writing one property is how a surface
    ///     ends up half-converted, and the segmented strip was the thing the
    ///     rail replaced — not a second door onto the same three tabs.
    /// </summary>
    [Fact]
    public void TheMenuBar_NoLongerCarriesASecondNavigationControl()
    {
        var menu = MainMenuSource.ReadAllText();

        Assert.DoesNotContain("x:Name=\"MainNavSwitcher\"", menu, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"MainNavPlayMode\"", menu, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedMainTabIndex", menu, StringComparison.Ordinal);

        // …and the window hosts the rail across BOTH rows. A rail under the
        // title bar is a sidebar; spanning the window edge is what makes the
        // layout a workbench.
        var window = MainWindowMarkup();
        Assert.Contains("<shellViews:ActivityRailView", window, StringComparison.Ordinal);
        Assert.Contains("Grid.RowSpan=\"2\" Grid.Column=\"0\"", window, StringComparison.Ordinal);
    }

    private static string RailMarkup() => UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "ActivityRailView.axaml")));

    // ── The dropdown chrome ─────────────────────────────────────────────────

    /// <summary>
    ///     <b>No style rule still pretends to paint a popup panel.</b>
    ///     <c>Styles/Chrome.axaml</c> and <c>Styles/Inputs.axaml</c> carried
    ///     <c>… /template/ Popup Border</c> rules whose Background, BorderBrush,
    ///     BorderThickness, CornerRadius and Padding setters never applied —
    ///     Avalonia's templates set those from a <c>{DynamicResource}</c> INSIDE
    ///     the template, which is a local value and beats a style. Only the
    ///     properties the template does NOT set (a BoxShadow, a Margin) ever did
    ///     anything, which is exactly what made the rules look like they worked.
    ///     They keep those; a dead setter creeping back would be a second,
    ///     plausible-looking source for a dropdown's colour that does nothing,
    ///     and the next person to change a menu colour would change that one.
    /// </summary>
    [Fact]
    public void NoStyleRule_StillPretendsToPaintAPopupPanel()
    {
        string[] dead = ["Background", "BorderBrush", "BorderThickness", "CornerRadius", "Padding"];

        foreach (var file in UiSourceTree.EnumerateFiles(
                     Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.axaml", SearchOption.AllDirectories))
        {
            var markup = UiSourceTree.ReadAllText(file);
            foreach (Match rule in Regex.Matches(markup,
                         @"<Style Selector=""[^""]*/template/ Popup Border""[^>]*>(?<body>.*?)</Style>",
                         RegexOptions.Singleline))
            {
                foreach (var property in dead)
                {
                    Assert.False(rule.Groups["body"].Value
                            .Contains($"Property=\"{property}\"", StringComparison.Ordinal),
                        $"{Path.GetFileName(file)} sets {property} on a popup panel Border. The control's " +
                        "own template sets that from a DynamicResource, which is a local value and wins — " +
                        "the setter does nothing. Override the KEY instead (Styles/FireMenuChrome.axaml).");
                }
            }
        }
    }

    /// <summary>
    ///     <b>A docked pane's header reads like the 3D preview's.</b> The owner: the bar "reminds of
    ///     the title bar of like imgui" (2026-09-24) — a focused pane filled its whole header with an
    ///     accent tint and turned the title and every icon bright white. The header now carries the
    ///     3D preview's type (the bar surface, a small dim title) and focus only lifts the title a step.
    /// </summary>
    [Fact]
    public void TheDockPaneHeader_IsTheQuietPaneHeader()
    {
        var dock = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Styles", "Dock.axaml")));
        var flat = string.Join(" ", dock.Split(['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains("Selector=\"dock|ToolChromeControl /template/ TextBlock#PART_Title\"", dock);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"{StaticResource FireSizeEyebrow}\" />", flat);
        Assert.Contains("Selector=\"dock|ToolChromeControl:active /template/ Grid#PART_Grip\"> <Setter Property=\"Background\" Value=\"Transparent\" />", flat);
        Assert.Contains("Selector=\"dock|ToolChromeControl:active /template/ Button Path\"", dock);
    }

    /// <summary>
    ///     <b>A menu or dropdown casts no drop shadow.</b> Its popup window is the frame's own size
    ///     (plus any margin), so a shadow is cut off at the window's square edge — under the frame's
    ///     rounded corners the menus showed a dark square "border" (owner screenshot, 2026-09-24).
    ///     Measured on the View menu: the dark patch stopped at a hard vertical line exactly where
    ///     the popup window ends.
    /// </summary>
    [Fact]
    public void APopupFrame_CastsNoShadowItsWindowWouldClip()
    {
        var offenders = new List<string>();
        foreach (var file in UiSourceTree.EnumerateFiles(
                     Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.axaml", SearchOption.AllDirectories))
        {
            var markup = UiSourceTree.ReadAllText(file);
            foreach (Match rule in Regex.Matches(markup,
                         @"<Style Selector=""(?<sel>[^""]*/template/ Popup[^""]*)""[^>]*>(?<body>.*?)</Style>",
                         RegexOptions.Singleline))
            {
                if (rule.Groups["body"].Value.Contains("Property=\"BoxShadow\"", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: {rule.Groups["sel"].Value}");
            }
        }

        Assert.True(offenders.Count == 0, "popup frames with a clipped shadow: " + string.Join(", ", offenders));
    }

    /// <summary>
    ///     The chrome grammar reaches the dropdowns the ONLY way that works —
    ///     by overriding Fluent's resource keys — and it is scoped to the three
    ///     editor chrome views. A fourth includer would restyle menus on a
    ///     surface that has not been converted.
    /// </summary>
    [Fact]
    public void TheChromeGrammar_IsIncludedByExactlyTheThreeChromeViews()
    {
        const string include = "avares://DotCharter/Views/Shell/ShellChromeStyles.axaml";

        var includers = UiSourceTree
            .EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.axaml", SearchOption.AllDirectories)
            .Where(f => UiSourceTree.ReadAllText(f).Contains(include, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["MainMenuView.axaml", "StatusBar.axaml", "ToolbarView.axaml"], includers);

        var grammar = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "ShellChromeStyles.axaml")));
        Assert.Contains("avares://DotCharter.Tone.UI/Styles/FireMenuChrome.axaml", grammar, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <b>Every Fluent popup key restates a Fire token, and the two sides
    ///     cannot drift.</b> <c>FireMenuChrome.axaml</c> has to carry literal
    ///     values (a Brush resource has no way to defer to another brush, and the
    ///     Fire tokens live in a <c>&lt;Styles&gt;</c> that a
    ///     <c>ResourceDictionary</c> cannot merge), so this is the guard that
    ///     makes the duplication safe: it parses both files and compares them
    ///     colour by colour.
    /// </summary>
    [Theory]
    [InlineData("MenuFlyoutPresenterBackground", "FireSurfaceMenu")]
    [InlineData("ComboBoxDropDownBackground", "FireSurfaceMenu")]
    [InlineData("FlyoutPresenterBackground", "FireSurfaceMenu")]
    [InlineData("MenuFlyoutPresenterBorderBrush", "FireBorderStrong")]
    [InlineData("ComboBoxDropDownBorderBrush", "FireBorderStrong")]
    [InlineData("FlyoutBorderThemeBrush", "FireBorderStrong")]
    [InlineData("MenuFlyoutItemBackgroundPointerOver", "FireAccentTint")]
    [InlineData("MenuFlyoutSubItemBackgroundPointerOver", "FireAccentTint")]
    [InlineData("ComboBoxItemBackgroundPointerOver", "FireAccentTint")]
    [InlineData("MenuFlyoutItemBackgroundPressed", "FireAccentTintStrong")]
    [InlineData("MenuFlyoutItemForeground", "FireTextBody")]
    [InlineData("MenuFlyoutSubItemForeground", "FireTextBody")]
    [InlineData("ComboBoxItemForeground", "FireTextBody")]
    [InlineData("MenuFlyoutItemForegroundPointerOver", "FireTextBright")]
    [InlineData("MenuFlyoutItemForegroundPressed", "FireTextBright")]
    [InlineData("MenuFlyoutSubItemForegroundPointerOver", "FireTextBright")]
    [InlineData("MenuFlyoutSubItemChevronPointerOver", "FireTextBright")]
    [InlineData("MenuFlyoutItemForegroundDisabled", "FireTextFaint")]
    [InlineData("MenuFlyoutItemKeyboardAcceleratorTextForeground", "FireTextFaint")]
    [InlineData("MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "FireTextFaint")]
    [InlineData("MenuFlyoutSubItemChevronDisabled", "FireTextFaint")]
    [InlineData("MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "FireTextMuted")]
    [InlineData("MenuFlyoutSubItemChevron", "FireTextMuted")]
    [InlineData("MenuFlyoutSeparatorBackground", "FireBorder")]
    public void EveryFluentPopupKey_RestatesItsFireToken(string fluentKey, string fireToken)
    {
        var aliases = BrushesIn(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireMenuChrome.axaml"));
        var tokens = BrushesIn(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireTokens.axaml"));

        Assert.True(aliases.TryGetValue(fluentKey, out var alias),
            $"FireMenuChrome.axaml no longer declares {fluentKey} — that dropdown reverted to Fluent's own look");
        Assert.True(tokens.TryGetValue(fireToken, out var token),
            $"FireTokens.axaml no longer declares {fireToken}");

        Assert.True(alias == token,
            $"{fluentKey} is {alias} but {fireToken} is {token}. The alias RESTATES the token because a " +
            "ResourceDictionary cannot defer to a <Styles> — so when the token moves, this file has to " +
            "move with it, and this is the only thing that notices.");
    }

    /// <summary>
    ///     The dropdown GROUND is fully opaque (2026-08-11). A menu popup is its
    ///     own HWND drawn over arbitrary chrome, and at the mockups' .98 the
    ///     toolbar icons ghosted through the Tempo Map / View dropdown panels
    ///     (owner report). The panel brush is the sanctioned fix — the Win32
    ///     composition mode must stay transparency-capable
    ///     (<c>Win32CompositionModeSelectorTests</c>), so opacity has to live
    ///     HERE, not there.
    /// </summary>
    [Theory]
    [InlineData("MenuFlyoutPresenterBackground")]
    [InlineData("ComboBoxDropDownBackground")]
    [InlineData("FlyoutPresenterBackground")]
    public void TheDropdownGround_IsFullyOpaque(string key)
    {
        var aliases = BrushesIn(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireMenuChrome.axaml"));
        Assert.True(aliases.TryGetValue(key, out var value), $"{key} is no longer declared");
        Assert.True(value.A == 0xFF,
            $"{key} is {value} — a translucent dropdown ground lets the chrome underneath " +
            "ghost through every menu");
    }

    /// <summary>…and the pinned list is not vacuous: it covers essentially the
    /// whole alias file, so a key added there without a row here is caught.</summary>
    [Fact]
    public void ThePinnedList_CoversEveryColourAliasInTheFile()
    {
        var aliases = BrushesIn(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireMenuChrome.axaml"));
        // Read the attribute's literal DATA rather than asking it to expand:
        // xunit v3's GetData is async and takes a DisposalTracker, and all this
        // needs is the first constructor argument of each row.
        var pinned = typeof(EditorChromeTests)
            .GetMethod(nameof(EveryFluentPopupKey_RestatesItsFireToken))!
            .GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(Xunit.InlineDataAttribute))
            .Select(a => (string)((System.Collections.ObjectModel
                    .ReadOnlyCollection<System.Reflection.CustomAttributeTypedArgument>)
                a.ConstructorArguments[0].Value!)[0].Value!)
            .ToHashSet(StringComparer.Ordinal);

        // Transparent is not a token — it is the absence of one.
        var unpinned = aliases.Keys
            .Where(k => !pinned.Contains(k) && aliases[k] != Colors.Transparent)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(unpinned.Count == 0,
            "FireMenuChrome.axaml declares colour alias(es) nothing pins against a Fire token: " +
            string.Join(", ", unpinned));
    }

    // ── The mono caps transform ─────────────────────────────────────────────

    /// <summary>
    ///     The top bar's context is IBM Plex Mono at the ladder's quietest step,
    ///     and that face only works in caps — but the strings it prints are a
    ///     project name and a tempo read-out, i.e. bound data in whatever case
    ///     the user wrote it. Avalonia has no <c>text-transform</c>, so the
    ///     converter IS the rule.
    /// </summary>
    [Fact]
    public void TheMonoCapsConverter_UppercasesAndSurvivesNull()
    {
        Assert.Equal("SAKASAMA", TextConverters.ToUpper.Convert(
            "Sakasama", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(TextConverters.ToUpper.Convert(
            null, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     The title bar repeats nothing the editor already shows (owner, 2026-09-23): the
    ///     chart's name, its unsaved dot, the position and the tempo live on the status rail,
    ///     and play / pause / stop on the toolbar. What stays is the menu, "Go to anything"
    ///     and the window buttons.
    /// </summary>
    [Fact]
    public void TheTitleBar_RepeatsNothingTheToolbarOrStatusRailShows()
    {
        var markup = MainWindowMarkup();
        var start = markup.IndexOf("x:Name=\"TitleBarRow\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "MainWindow no longer declares the title bar");
        var row = markup[start..markup.IndexOf("x:Name=\"BackBar\"", start, StringComparison.Ordinal)];

        Assert.Contains("x:Name=\"TitleGoToAnything\"", row, StringComparison.Ordinal);
        foreach (var repeat in new[] { "Transport.", "StatusBar.ProjectName", "StatusBar.IsProjectDirty",
                     "StatusBar.PositionLabel", "StatusBar.BpmLabel" })
        {
            Assert.DoesNotContain(repeat, row, StringComparison.Ordinal);
        }

        // …and the status rail really carries them.
        var rail = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "StatusBar.axaml")));
        foreach (var kept in new[] { "ProjectName", "IsProjectDirty", "PositionLabel", "BpmLabel" })
            Assert.Contains("{Binding " + kept + "}", rail, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The back bar is Back and the window buttons. It also printed the screen's name,
    ///     and both screens it serves print their own title right under it, so a full-bleed
    ///     Library read "Library" twice (2026-09-24).
    /// </summary>
    [Fact]
    public void TheBackBar_DoesNotRepeatTheScreensOwnTitle()
    {
        var markup = MainWindowMarkup();
        var start = markup.IndexOf("x:Name=\"BackBar\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "MainWindow no longer declares the back bar");
        var bar = markup[start..markup.IndexOf("</Grid>", markup.IndexOf("BackBarMaximizeGlyph", start, StringComparison.Ordinal), StringComparison.Ordinal)];

        Assert.Contains("GoBackCommand", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("BackBarTitle", bar, StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>The shell's four tab-index constants, read off the source so a
    /// renumbering fails the switcher test rather than the app.</summary>
    private static IEnumerable<(string Field, int Index)> ShellTabIndices()
    {
        var src = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs")));

        foreach (var name in new[]
                 { "ChartEditorTabIndex", "SongBrowserTabIndex", "SettingsTabIndex", "HomeTabIndex" })
        {
            var m = Regex.Match(src, $@"const int {name} = (?<i>\d+);");
            Assert.True(m.Success, $"MainWindowViewModel no longer declares {name}");
            yield return (name, int.Parse(m.Groups["i"].Value));
        }
    }

    /// <summary>
    ///     Every <c>SolidColorBrush</c> in an axaml file, by key — through the
    ///     shared resolver, which reads the file WITH what it merges (a
    ///     container like <c>Styles/FireTokens.axaml</c> declares nothing by
    ///     itself) and follows a brush that names a <c>Color</c> token instead of
    ///     a literal. The <c>Assert.NotEmpty</c> below is what keeps either of
    ///     those from turning this lint into a comparison against nothing.
    /// </summary>
    private static Dictionary<string, Color> BrushesIn(string path)
    {
        var found = StyleFileText.BrushesWithIncludes(path);

        Assert.NotEmpty(found);
        return found;
    }

    private static string MainWindowMarkup() =>
        MainWindowSource.ReadAllText();

    /// <summary>The shell code-behind is SPLIT across <c>MainWindow.axaml.cs</c>,
    /// its <c>MainWindow.*.cs</c> partials AND the <c>MainWindow*</c> collaborators
    /// extracted out of them (<c>Views/Shell/MainWindowPrewarm.cs</c> and friends) — read them all, or a lint
    /// silently stops looking at the half it cares about.</summary>
    private static string MainWindowCodeBehind() =>
        string.Join('\n', UiSourceTree
            .GetFiles(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views")), "MainWindow*.cs",
                SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(UiSourceTree.ReadAllText));

    /// <summary>
    ///     A filter popover's body is a bare list of checkboxes with no sections
    ///     of its own, so without the body inset its rows and group captions sat
    ///     on the popover's left edge (owner, 2026-09-24: "could use some
    ///     margin/padding").
    /// </summary>
    [Fact]
    public void EveryFilterPopover_InsetsItsBody()
    {
        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        var tags = UiSourceTree.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(UiSourceTree.ReadAllText(f), @"<controls:FirePopover\b[^>]*>")
                .Select(m => (File: Path.GetFileName(f), Tag: m.Value)))
            .Where(t => t.Tag.Contains("IconFilter"))
            .ToList();

        Assert.True(tags.Count >= 7, $"found {tags.Count} filter popovers; the lint no longer sees them");
        var bare = tags.Where(t => !t.Tag.Contains("Padding=\"{StaticResource FirePadPopoverBody}\"")).ToList();
        Assert.True(bare.Count == 0,
            "filter popovers with no body inset: " + string.Join(", ", bare.Select(t => t.File)));
    }
}
