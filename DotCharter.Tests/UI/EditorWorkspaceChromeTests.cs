using System.Text.RegularExpressions;
using Avalonia.Media;

namespace DotCharter.Tests.UI;

/// <summary>
///     The chart editor's WORKSPACE chrome, after the 0.51 Fire conversion: the
///     track rail beside the timeline, the fixed-pane header strip, the minimap
///     frame, the 3D preview's editor bar, and the Dock.Avalonia pane chrome
///     around all of them.
///     <para>
///         Source lints, because every failure here is silent. A view that wears
///         a grammar class without including the grammar renders in its default
///         look with no warning and no exception; a Dock brush key that drifts
///         from the Fire token it restates leaves the panes a different colour
///         from the rails above them, which nothing else in the gate can see.
///         The LAYOUT half lives in <c>Perf/ShellConstructionUiPerfSmokeTests</c>.
///     </para>
/// </summary>
public sealed class EditorWorkspaceChromeTests
{
    private const string GrammarInclude =
        "avares://DotCharter/Views/Editor/EditorWorkspaceStyles.axaml";

    /// <summary>Every class the workspace grammar dresses. A view wearing one of
    /// these and not including the file is the silent no-op this exists for.</summary>
    private static readonly string[] GrammarClasses =
        ["TrackRail", "RowsStrip", "RibbonRow", "PaneHeader", "PaneTitle", "PaneAction",
         "MinimapFrame", "SongMapCaption", "SongMapFacts",
         "HudBar", "HudLabel", "HudStat", "HudAction"];

    // ── The grammar reaches everything that wears it ────────────────────────

    /// <summary>
    ///     <b>Every view that wears a workspace class also includes the grammar.</b>
    ///     <para>
    ///         This is not a style preference — it is the difference between a
    ///         docked pane and a FLOATED one. `Border.PaneHeader` used to live in
    ///         the app-scoped <c>Styles/Containers.axaml</c>, which reaches every
    ///         window in the process for free. The Fire grammar is a
    ///         <c>StyleInclude</c>, so it reaches exactly the scopes that ask for
    ///         it, and a floated pane is re-hosted in a Dock.Avalonia
    ///         <c>HostWindow</c> that sees nothing MainWindow declares. A pane
    ///         that forgot the include renders correctly until someone drags it
    ///         out, and then its header is unstyled — which is the kind of fault
    ///         nobody finds from a diff.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryViewWearingAWorkspaceClass_AlsoIncludesTheGrammar()
    {
        var wearers = new List<string>();
        var offenders = new List<string>();

        foreach (var file in AxamlFiles(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"))))
        {
            var markup = UiSourceTree.ReadAllText(file);
            var worn = LiteralClassesIn(markup).Intersect(GrammarClasses, StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal).ToList();
            if (worn.Count == 0) continue;

            wearers.Add(Path.GetFileName(file));
            if (!markup.Contains(GrammarInclude, StringComparison.Ordinal))
            {
                offenders.Add($"{Path.GetFileName(file)} wears {string.Join("/", worn)} but does not " +
                              "include EditorWorkspaceStyles.axaml");
            }
        }

        // Anti-vacuity: MainWindow, Preview3DView and StemMixerView are the
        // surfaces this grammar exists for (PhraseCatalogView left with its pane
        // header in #601: it is an Inspector tab and the tab strip names it).
        Assert.True(wearers.Count >= 3,
            $"only {wearers.Count} view(s) wear a workspace class — the scan stopped seeing them: " +
            string.Join(", ", wearers));

        Assert.True(offenders.Count == 0,
            "workspace class(es) that silently do nothing — worst case only once the pane is FLOATED:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     …and nothing dresses them a SECOND time on the app ladder. The two
    ///     places that used to are named explicitly, because a re-added rule
    ///     there would win or lose depending on style order and look like the
    ///     conversion half-applying.
    /// </summary>
    [Fact]
    public void NoAppLadderFile_StillDressesTheWorkspaceClasses()
    {
        foreach (var relative in new[] { "Styles/Containers.axaml", "Styles/Buttons.axaml" })
        {
            var markup = SourceText.StripMarkupComments(UiSourceTree.ReadAllText(
                UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", relative.Replace('/', Path.DirectorySeparatorChar)))));

            foreach (Match m in Regex.Matches(markup, "Selector=\"(?<s>[^\"]+)\""))
            {
                foreach (var cls in Regex.Matches(m.Groups["s"].Value, @"\.(?<c>[A-Za-z_][A-Za-z0-9_]*)")
                         .Select(c => c.Groups["c"].Value))
                {
                    Assert.False(GrammarClasses.Contains(cls, StringComparer.Ordinal),
                    $"{relative} still declares a selector for '{cls}' — it moved to " +
                    "EditorWorkspaceStyles.axaml in 0.51, and two grammars dressing one control is " +
                    "how a surface ends up half-converted");
                }
            }
        }
    }

    // ── The track rail ──────────────────────────────────────────────────────

    /// <summary>
    ///     <b>The ROWS strip drives the REAL ribbon flags, and there are five of
    ///     them.</b>
    ///     <para>
    ///         The five toggles moved out of the track rail and across the top of
    ///         the timeline so the left column could label the lanes instead. The
    ///         thing worth pinning is not where they sit but WHAT they write: a
    ///         parallel visibility flag would render a perfectly convincing strip
    ///         that toggles nothing the renderer reads. Every one of them must
    ///         bind a property of <c>ChartEditor.ActiveRibbon</c> — the same
    ///         <c>RibbonVisibility</c> instance <c>ChartRenderer.TopRibbon</c>
    ///         builds its layout from.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheRowToggles_DriveTheRealRibbonFlags_FromTheViewMenu()
    {
        // They started in the track rail, moved to a pill strip across the top
        // of the timeline, and are now VIEW-MENU items — the strip cost a whole
        // horizontal band of the one screen where vertical space is the product,
        // to show five toggles you set once. What is pinned is not where they
        // sit but WHAT they write: a parallel visibility flag would render a
        // perfectly convincing control that toggles nothing the renderer reads.
        var menu = MainMenuSource.ReadAllText();

        foreach (var flag in new[]
                 {
                     "ShowSections", "ShowPhrases", "ShowLyrics", "ShowTones", "ShowMeasureMetadata"
                 })
        {
            Assert.Contains($"ChartEditor.ActiveRibbon.{flag}", menu, StringComparison.Ordinal);
        }

        // …and the strip they replaced is gone, so there is only one place to
        // set them.
        Assert.DoesNotContain("Classes=\"RowsStrip\"", MainWindowMarkup(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     <b>The gutter is a SIBLING of the renderer in column 0, and its frame
    ///     carries no inset.</b>
    ///     <para>
    ///         Both halves are load-bearing. Drawing the gutter inside
    ///         <c>ChartRenderer</c> would put a fixed offset on the renderer's x
    ///         axis, which every hit-tester, the marquee, the minimap drag and the
    ///         one time↔screen anchor map through. And a <c>Padding</c> on the
    ///         frame shifts every caption off the lane it names — the gutter's
    ///         captions are positioned in the RENDERER's coordinate space.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheGutter_SitsBesideTheRenderer_AndItsFrameHasNoInset()
    {
        var markup = MainWindowMarkup();

        var gutter = Regex.Match(markup,
            @"<controls:TimelineGutterControl[^>]*>", RegexOptions.Singleline);
        Assert.True(gutter.Success, "the timeline gutter is not in MainWindow.axaml");
        Assert.Contains("Renderer=\"{Binding #MainChartRenderer}\"", gutter.Value, StringComparison.Ordinal);

        // …inside the column-0 frame, which is what makes it a SIBLING of the
        // renderer rather than an offset inside it.
        var frame = Regex.Match(markup, @"<Border[^>]*Classes=""TrackRail""[^>]*>", RegexOptions.Singleline);
        Assert.True(frame.Success, "the gutter's frame is gone from MainWindow.axaml");
        Assert.Contains("Grid.Column=\"0\"", frame.Value, StringComparison.Ordinal);
        Assert.True(frame.Index < gutter.Index, "the gutter is not inside its column-0 frame");

        var grammar = SourceText.StripMarkupComments(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "EditorWorkspaceStyles.axaml"))));
        var rail = grammar[grammar.IndexOf("Selector=\"Border.TrackRail\"", StringComparison.Ordinal)..];
        rail = rail[..rail.IndexOf("</Style>", StringComparison.Ordinal)];
        Assert.Contains("Value=\"{StaticResource FirePadNone}\"", rail, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The gutter's column, the SONG MAP caption cell under it and
    ///     <c>StackedTracksView</c>'s per-row header are all the SAME width. For
    ///     the stacked row that is a correctness rule — a different width and its
    ///     chart shows a different slice of the song than the main one at the
    ///     same screen x. For the song map it is the design's single unbroken
    ///     left edge down the whole timeline. Three places, one number.
    /// </summary>
    [Fact]
    public void TheGutterColumn_TheSongMapCaption_AndTheStackedRowHeader_AreTheSameWidth()
    {
        var main = MainWindowMarkup();
        var timeline = main[main.IndexOf("x:Name=\"TimelineGrid\"", StringComparison.Ordinal)..];
        var columns = Regex.Match(timeline, @"ColumnDefinitions=""(?<c>[^""]+)""");
        Assert.True(columns.Success, "the TimelineGrid no longer declares its columns");
        var gutterWidth = columns.Groups["c"].Value.Split(',')[0];

        // The minimap strip is declared BEFORE the TimelineGrid (it is the
        // DockPanel's bottom child), so its columns are outside the slice above.
        var minimap = main[main.IndexOf("Classes=\"MinimapFrame\"", StringComparison.Ordinal)..];
        var minimapColumns = Regex.Match(minimap, @"ColumnDefinitions=""(?<c>[^""]+)""");
        Assert.True(minimapColumns.Success, "the minimap strip no longer declares its caption column");
        Assert.Equal(gutterWidth, minimapColumns.Groups["c"].Value.Split(',')[0]);

        var stacked = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "StackedTracksView.axaml")));
        var stackedColumns = Regex.Match(stacked, @"ColumnDefinitions=""(?<c>[^""]+)""");
        Assert.True(stackedColumns.Success, "StackedTracksView no longer declares its row columns");

        Assert.Equal(gutterWidth, stackedColumns.Groups["c"].Value.Split(',')[0]);
    }

    /// <summary>
    ///     <b>The minimap's seek handlers live on the MAP's own border, never on
    ///     the strip that now also contains a 160 px caption.</b>
    ///     <para>
    ///         <c>MinimapDragController</c> maps a seek as
    ///         <c>e.GetPosition(sender).X / sender.Bounds.Width</c> across the
    ///         WHOLE song. Left on the outer frame, the caption's width is part
    ///         of that denominator and every seek lands early by 160/W of the
    ///         song — with the viewport rectangle drawn 160 px to the right of
    ///         where the click went. It is silent, it is off by a few seconds on
    ///         a normal window, and it is exactly the class of fault the timeline
    ///         gutter itself is a sibling column to avoid.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheMinimapSeekHandlers_AreOnTheMapNotOnTheCaptionedStrip()
    {
        var main = MainWindowMarkup();
        var strip = main[main.IndexOf("Classes=\"MinimapFrame\"", StringComparison.Ordinal)..];

        var caption = strip.IndexOf("Classes=\"SongMapCaption\"", StringComparison.Ordinal);
        var handler = strip.IndexOf("Minimap_PointerPressed", StringComparison.Ordinal);
        Assert.True(caption > 0, "the minimap strip no longer carries the SONG MAP caption");
        Assert.True(handler > 0, "the minimap strip no longer wires Minimap_PointerPressed");

        // The frame element ends at its first '>'; a handler before that is on
        // the frame itself, which is the fault.
        var frameEnd = strip.IndexOf('>');
        Assert.True(handler > frameEnd,
            "Minimap_PointerPressed is on the MinimapFrame — the 160 px caption is " +
            "then part of the seek denominator and every seek lands early");
        Assert.True(handler > caption,
            "the seek handler is declared ahead of the caption cell — it is no longer " +
            "on the map's own border");
    }

    // ── The dock chrome bridge ──────────────────────────────────────────────

    /// <summary>
    ///     <b>Every Dock.Avalonia chrome key restates a Fire token, and the two
    ///     sides cannot drift.</b> Same shape as
    ///     <c>EditorChromeTests.EveryFluentPopupKey_RestatesItsFireToken</c> and
    ///     for the same reason: <c>DockFluentTheme</c>'s templates read these keys
    ///     from inside the template (a local value, which beats any style setter),
    ///     so overriding the KEY is the only thing that works — and a
    ///     <c>SolidColorBrush</c> cannot defer to another brush, so
    ///     <c>Styles/Dock.axaml</c> has to carry literals. This is the guard that
    ///     makes the duplication safe.
    /// </summary>
    [Theory]
    [InlineData("DockApplicationAccentBrushLow", "FireAccent")]
    [InlineData("DockApplicationAccentBrushMed", "FireAccentBright")]
    [InlineData("DockApplicationAccentBrushHigh", "FireAccentPale")]
    [InlineData("DockApplicationAccentForegroundBrush", "FireTextOnAccent")]
    [InlineData("DockApplicationAccentBrushIndicator", "FireAccent")]
    [InlineData("DockTargetIndicatorBrush", "FireAccent")]
    [InlineData("DockThemeBorderLowBrush", "FireBorderSeam")]
    [InlineData("DockThemeBackgroundBrush", "FireSurfaceChrome")]
    [InlineData("DockThemeAccentBrush", "FireAccent")]
    [InlineData("DockThemeForegroundBrush", "FireTextMuted")]
    [InlineData("DockThemeControlBackgroundBrush", "FireSurfaceChromeAlt")]
    [InlineData("DockSurfaceWorkbenchBrush", "FireGroundScreen")]
    [InlineData("DockSurfaceSidebarBrush", "FireSurfaceChrome")]
    [InlineData("DockSurfaceEditorBrush", "FireSurfaceChromeAlt")]
    [InlineData("DockSurfacePanelBrush", "FireSurfaceChrome")]
    [InlineData("DockSurfaceHeaderBrush", "FireSurfaceChromeAlt")]
    [InlineData("DockSurfaceHeaderActiveBrush", "FireAccentTintSoft")]
    [InlineData("DockBorderSubtleBrush", "FireBorderSeam")]
    [InlineData("DockBorderStrongBrush", "FireBorderStrong")]
    [InlineData("DockSeparatorBrush", "FireBorderRow")]
    [InlineData("DockSplitterIdleBrush", "FireBorderHair")]
    [InlineData("DockSplitterHoverBrush", "FireAccentTintSoft")]
    [InlineData("DockSplitterDragBrush", "FireAccent")]
    [InlineData("DockDocumentContentBorderBrush", "FireBorderSeam")]
    [InlineData("DockTabHoverBackgroundBrush", "FireSurfaceHover")]
    [InlineData("DockTabActiveBackgroundBrush", "FireAccentTint")]
    [InlineData("DockTabActiveIndicatorBrush", "FireAccent")]
    [InlineData("DockTabForegroundBrush", "FireTextDim")]
    [InlineData("DockTabSelectedForegroundBrush", "FireTextPrimary")]
    [InlineData("DockTabActiveForegroundBrush", "FireTextBright")]
    [InlineData("DockTabCloseHoverBackgroundBrush", "FireAccentTintStrong")]
    [InlineData("DockDocumentTabStripBackgroundBrush", "FireSurfaceChrome")]
    [InlineData("DockChromeButtonHoverBackgroundBrush", "FireSurfaceHover")]
    [InlineData("DockChromeButtonPressedBackgroundBrush", "FireAccentTintSoft")]
    [InlineData("DockChromeButtonDangerHoverBrush", "FireDanger")]
    [InlineData("DockToolChromeIconBrush", "FireTextDim")]
    [InlineData("DockChromeButtonForegroundBrush", "FireTextDim")]
    public void EveryDockChromeKey_RestatesItsFireToken(string dockKey, string fireToken)
    {
        var aliases = BrushesIn(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Styles", "Dock.axaml")));
        var tokens = BrushesIn(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireTokens.axaml"));

        Assert.True(aliases.TryGetValue(dockKey, out var alias),
            $"Styles/Dock.axaml no longer declares {dockKey} — that piece of pane chrome reverted to " +
            "DockFluentTheme's own VS-blue look");
        Assert.True(tokens.TryGetValue(fireToken, out var token),
            $"FireTokens.axaml no longer declares {fireToken}");

        Assert.True(alias == token,
            $"{dockKey} is {alias} but {fireToken} is {token}. The alias RESTATES the token because a " +
            "SolidColorBrush cannot defer to another brush and a ResourceDictionary cannot merge a " +
            "<Styles> — so when the token moves, this file has to move with it, and this is the only " +
            "thing that notices.");
    }

    /// <summary>…and the pinned list covers the whole file, so a key added to the
    /// bridge without a row here is caught rather than left unpinned.</summary>
    [Fact]
    public void ThePinnedDockList_CoversEveryColourAliasInTheFile()
    {
        var aliases = BrushesIn(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Styles", "Dock.axaml")));
        Assert.True(aliases.Count > 20,
            $"only {aliases.Count} dock aliases parsed — the file's shape changed and this guard is blind");

        var pinned = typeof(EditorWorkspaceChromeTests)
            .GetMethod(nameof(EveryDockChromeKey_RestatesItsFireToken))!
            .GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(Xunit.InlineDataAttribute))
            .Select(a => (string)((System.Collections.ObjectModel
                    .ReadOnlyCollection<System.Reflection.CustomAttributeTypedArgument>)
                a.ConstructorArguments[0].Value!)[0].Value!)
            .ToHashSet(StringComparer.Ordinal);

        var unpinned = aliases.Keys
            .Where(k => !pinned.Contains(k) && aliases[k] != Colors.Transparent)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(unpinned.Count == 0,
            "Styles/Dock.axaml declares colour alias(es) nothing pins against a Fire token: " +
            string.Join(", ", unpinned));
    }

    // ── The timeline's own palette ──────────────────────────────────────────

    /// <summary>
    ///     <b>The timeline chrome's boot defaults and the "Dark Slate" preset are
    ///     the same five colours.</b> <c>Styles/ColorTokens.axaml</c> (merged by
    ///     <c>App.axaml</c>) is what the app starts on
    ///     and <c>ThemePresets.DarkSlate</c> is what picking the default theme
    ///     RESTORES; a drift between them shows up only as the timeline changing
    ///     colour when the user selects the theme it is already using, which is
    ///     the least likely thing anyone would try. They moved together off the
    ///     zinc greys in 0.51 and have to keep moving together.
    /// </summary>
    [Fact]
    public void TheTimelineChromeDefaults_MatchTheDarkSlatePreset()
    {
        // The tokens moved out of App.axaml into Styles/ColorTokens.axaml when
        // the gallery started merging the same dictionary; read BOTH so this
        // lint cannot pass vacuously against whichever file no longer holds them.
        var app = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.axaml"))) +
                  UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "ColorTokens.axaml"));
        var presets = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Theming", "ThemePresets.cs")));

        string[] tokens =
        [
            "ChartTopMarginBg", "ChartBottomMarginBg", "ChartBorder", "ChartMinimapBorder", "ChartMeasure"
        ];

        foreach (var token in tokens)
        {
            var inApp = Regex.Match(app, $@"<Color x:Key=""{token}Color"">(?<v>#[0-9A-Fa-f]+)</Color>");
            Assert.True(inApp.Success, $"Styles/ColorTokens.axaml no longer declares {token}Color");

            var inPreset = Regex.Match(presets,
                $@"\[ThemeTokens\.{token}\]\s*=\s*C\(""(?<v>#[0-9A-Fa-f]+)""\)");
            Assert.True(inPreset.Success, $"ThemePresets no longer declares {token}");

            Assert.Equal(inApp.Groups["v"].Value.ToUpperInvariant(),
                inPreset.Groups["v"].Value.ToUpperInvariant());
        }

        // …and they really left the zinc ramp. Without this the test above is
        // satisfied by two files agreeing on the OLD values. Comments are
        // stripped first: the token dictionary's own prose NAMES the greys it moved off,
        // which is a scan that would flag its own documentation.
        var appMarkup = SourceText.StripMarkupComments(app);
        Assert.DoesNotContain("#18181B", appMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#71717A", appMarkup, StringComparison.OrdinalIgnoreCase);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string MainWindowMarkup() =>
        MainWindowSource.ReadAllText();

    /// <summary>Every literal <c>Classes="A B"</c> token in this markup. A bound
    /// <c>Classes.Foo="{Binding}"</c> is a different construct and none of the
    /// workspace classes is ever written that way.</summary>
    private static IEnumerable<string> LiteralClassesIn(string markup) =>
        Regex.Matches(markup, @"\bClasses=""(?<classes>[^""{]+)""")
            .SelectMany(m => m.Groups["classes"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    ///     <c>&lt;SolidColorBrush x:Key="X" Color="#AARRGGBB" /&gt;</c>, by key —
    ///     and since the Fire palette was tokenised (2026-09-20) also the
    ///     indirect form <c>Color="{DynamicResource XColor}"</c> over a
    ///     <c>&lt;Color x:Key="XColor"&gt;#AARRGGBB&lt;/Color&gt;</c> in the same
    ///     file set. Reading only the literal form would report every Fire token
    ///     as "no longer declared".
    /// </summary>
    private static Dictionary<string, Color> BrushesIn(string path) =>
        StyleFileText.BrushesWithIncludes(path);

    private static IEnumerable<string> AxamlFiles(string root) =>
        UiSourceTree.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories);
}
