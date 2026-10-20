using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Presentation.Shared;
using DotCharter.Tone.Audio;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Library;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Application;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.CommandPalette;
using DotCharter.UI.Views.Diagnostics;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Builds every SHELL view for real, ATTACHES it to a window and runs a
///     layout pass.
///     <para>
///         This repo has shipped four separate silent XAML failures, and every
///         one of them built clean: the AXAML compiler proves the markup parses
///         and says nothing about a <c>{DynamicResource}</c> that resolves to
///         nothing, a style selector that matches nothing, or a subtree that
///         measures to zero. Those only surface once something lays out — and
///         for the shell nothing does, because none of these views has a
///         view-model cheap enough for an ordinary unit test.
///     </para>
///     <para>
///         The strongest single assertion here is <see cref="AssertIconsResolved" />:
///         a <c>PathIcon</c> whose <c>Data</c> came from a mistyped
///         <c>DynamicResource</c> key is simply BLANK at runtime — no exception,
///         no log line. It is also how the 0.37 Play Mode pass caught a duplicate
///         <c>IconCog</c> key, which builds clean and then crashes the whole app
///         at <c>App.Initialize()</c>.
///     </para>
///     <para>
///         Most cases run with NO DataContext on purpose. Every binding then
///         produces no value and each property keeps its default, which is the
///         harshest layout case and needs no service graph — what is under test
///         is the LAYOUT, not the data. The one exception is the song list,
///         whose row template is the riskiest new markup in the pass: real
///         <see cref="PsarcSongListItem" />s are pushed straight into the
///         <c>ListBox</c> so the template is genuinely realized.
///     </para>
///     <para>
///         Named <c>*UiPerfSmokeTests</c> because it needs the real Avalonia app
///         session, which cannot share a testhost with the gate and is excluded from the
///         default gate. Run it by hand after touching any shell view.
///         <c>[Collection("HeadlessUI")]</c> keeps its windows from racing the
///         focus-sensitive key-routing smoke tests.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ShellConstructionUiPerfSmokeTests
{
    private const double W = 1600;
    private const double H = 1000;

    // ── The Library (wave 5) ────────────────────────────────────────────────
    // Every variant, the two empty states and caption alignment on the fake are
    // `LibraryConstructionUiPerfSmokeTests`; this case feeds the view REAL song rows.

    /// <summary>
    ///     The row template with real items behind it: the caption row and the rows share ONE
    ///     <see cref="SongTableLayout" /> (#212), so the values sit under their captions all the
    ///     way down a 2,000-song list — measured on both panels' arranged columns, because a
    ///     per-element bounds check passes while the two drift apart. Also pins that the list
    ///     still VIRTUALIZES.
    /// </summary>
    [Fact]
    public async Task Library_songRows_lineUpWithTheirCaptions_andStayVirtualized()
    {
        await WithLaidOut(() => new DotCharter.UI.Views.Library.LibraryView(), view =>
        {
            var list = Assert.IsType<ListBox>(Find(view, "LibraryRows"));
            list.ItemsSource = SampleSongs(60).Select(s => new LibraryRow(s, () => false)).ToList();
            view.Measure(new Size(W, H));
            view.Arrange(new Rect(0, 0, W, H));
            view.UpdateLayout();

            var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.True(rows.Count > 0, "the song list realized no rows at all");
            Assert.True(rows.Count < 60,
                $"the song list realized {rows.Count} of 60 rows - virtualization was lost " +
                "(an ItemsPanel override?), and a real library is thousands of rows");

            var captionPanel = ColumnPanel(Find(view, "LibraryColumnHeader"));
            Assert.NotNull(captionPanel);
            Assert.Equal(SongTableLayout.Library.Columns.Count, captionPanel!.Places.Count);
            foreach (var row in rows)
            {
                Assert.True(row.Bounds.Height > 30,
                    $"a song row laid out {row.Bounds.Height:0.#} px tall - its two-line record collapsed");
                var rowPanel = ColumnPanel(row);
                Assert.NotNull(rowPanel);
                Assert.Equal(captionPanel.Shown, rowPanel!.Shown);
                for (var column = 0; column < captionPanel.Places.Count; column++)
                {
                    var caption = captionPanel.Places[column];
                    var actual = rowPanel.Places[column];
                    Assert.True(Math.Abs(caption.Left - actual.Left) <= 1 && Math.Abs(caption.Width - actual.Width) <= 1,
                        $"column {column} is {caption} in the caption row and {actual} in a song row - " +
                        "the captions no longer sit over their columns");
                }

                // PATHS: one drawn strip, the initials of the paths the song HAS.
                var strip = Assert.Single(row.GetVisualDescendants().OfType<DotCharter.Tone.UI.Controls.Fire.FireChipStrip>());
                Assert.True(strip.Initials is { Count: >= 2 },
                    $"a song row's chip strip carries {strip.Initials?.Count ?? 0} initials");
                Assert.True(strip.Bounds.Width > 1 && strip.Bounds.Height > 1, $"the path chip strip laid out as {strip.Bounds}");
            }

            AssertIconsResolved(view, "the Library");
        });
    }

    /// <summary>The song-table panel a caption row / a song row is built on.</summary>
    private static SongTablePanel? ColumnPanel(Visual root) =>
        root.GetVisualDescendants().OfType<SongTablePanel>().FirstOrDefault();

    // ── The rest of the shell ───────────────────────────────────────────────

    [Fact]
    public async Task Toolbar_buildsAttachesAndLaysOut()
    {
        await WithLaidOut(() => new ToolbarView(), view =>
        {
            AssertLaidOut(view, "ToolbarQuickGroup");

            var groups = view.GetVisualDescendants().OfType<StackPanel>()
                .Where(p => p.Classes.Contains("TbGroup"))
                .ToList();
            // Counted from Views/Shell/ToolbarView.axaml: transport, edit, zoom
            // and the grouped flyouts, plus the right-pinned quick cluster.
            // RE-COUNT there rather than nudging this number — a stale count pin
            // over derived markup is the failure this suite keeps having. The
            // charting-verbs group went to the menu in 0.48 (owner: those three
            // are once-per-chart and do not need the quick rail).
            Assert.True(groups.Count >= 5,
                $"the toolbar laid out {groups.Count} control groups — expected the four core " +
                "groups plus the right-pinned quick cluster");
            Assert.All(groups, g => Assert.True(g.Bounds.Height > 1,
                $"a toolbar group laid out as {g.Bounds}"));

            // Every glyph on the rail now comes through {DynamicResource IconX};
            // a typo'd key is a blank button and nothing else.
            AssertIconsResolved(view, nameof(ToolbarView));
        });
    }

    /// <summary>
    ///     The tool rail's right-hand cluster took the deleted icon rail's panel
    ///     toggles, and each one has to be a real toggle you can SEE the state of
    ///     — a `ToolbarToggleBtn` whose template failed still measures, it just
    ///     stops looking pressed, which no bounds check can tell apart.
    /// </summary>
    [Fact]
    public async Task Toolbar_carriesThePanelTogglesTheIconRailUsedToOwn()
    {
        await WithLaidOut(() => new ToolbarView(), view =>
        {
            var quick = Find(view, "ToolbarQuickGroup");
            var toggles = quick.GetVisualDescendants().OfType<ToggleButton>().ToList();

            // metronome · claps · play the chart (#574) · loop · inspector · 3D ·
            // sheet · tone preview. The pedalboard toggle left in 2026-09 — the
            // activity rail's Rig row beside this toolbar is the one door onto
            // the same overlay.
            Assert.True(toggles.Count >= 8,
                $"the quick cluster laid out {toggles.Count} toggles — the three panel toggles " +
                "the icon rail used to own, or the tone-preview toggle, are missing");
            Assert.All(toggles, t => Assert.True(t.Bounds.Width > 1 && t.Bounds.Height > 1,
                $"a quick-cluster toggle laid out as {t.Bounds}"));

            // Every glyph in the cluster is distinct: two controls sharing one
            // icon in a nine-button strip is not a strip. Compared by REFERENCE
            // — a `{DynamicResource IconX}` hands out the one StreamGeometry the
            // dictionary holds, and `Geometry.ToString()` is the type name.
            var glyphs = quick.GetVisualDescendants().OfType<PathIcon>()
                .Select(i => i.Data)
                .Where(d => d != null)
                .ToList();
            Assert.Equal(glyphs.Count, glyphs.Distinct(ReferenceEqualityComparer.Instance).Count());
        });
    }

    /// <summary>
    ///     The tone-preview toggle specifically. Found by its GLYPH rather than
    ///     by position: a <c>{DynamicResource IconHeadphones}</c> that resolves to
    ///     nothing is a blank button with no exception and no log line, so
    ///     "the toggle wearing this icon exists and measured" tests both halves at
    ///     once.
    /// </summary>
    [Fact]
    public async Task Toolbar_carriesTheTonePreviewToggle()
    {
        await WithLaidOut(() => new ToolbarView(), view =>
        {
            var quick = Find(view, "ToolbarQuickGroup");
            Assert.True(Application.Current!.TryFindResource("IconHeadphones", out var headphones),
                "the IconHeadphones glyph is not in the app's icon dictionary");

            var toggle = quick.GetVisualDescendants().OfType<ToggleButton>()
                .FirstOrDefault(t => t.GetVisualDescendants().OfType<PathIcon>()
                    .Any(i => ReferenceEquals(i.Data, headphones)));

            Assert.True(toggle is not null,
                "the quick cluster has no tone-preview toggle — the owner asked for it twice");
            Assert.True(toggle!.Bounds.Width > 1 && toggle.Bounds.Height > 1,
                $"the tone-preview toggle laid out as {toggle.Bounds}");
        });
    }

    /// <summary>
    ///     The play-the-chart toggle (#574), by name and by glyph: it measured, its
    ///     <c>{DynamicResource IconPiano}</c> resolved, it carries an accessible name,
    ///     and it shows its tip on a DISABLED button too — disabled is how it says the
    ///     chart cannot sound, and the tip is where it says why.
    /// </summary>
    [Fact]
    public async Task Toolbar_carriesThePlayTheChartToggle()
    {
        await WithLaidOut(() => new ToolbarView(), view =>
        {
            var toggle = Assert.IsType<ToggleButton>(Find(view, "ChartSynthToolbarToggle"));
            Assert.True(toggle.Bounds.Width > 1 && toggle.Bounds.Height > 1,
                $"the play-the-chart toggle laid out as {toggle.Bounds}");
            Assert.Same(Find(view, "ToolbarQuickGroup"), toggle.GetVisualParent());

            Assert.True(Application.Current!.TryFindResource("IconPiano", out var piano),
                "the IconPiano glyph is not in the app's icon dictionary");
            Assert.Contains(toggle.GetVisualDescendants().OfType<PathIcon>(), i => ReferenceEquals(i.Data, piano));

            Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(toggle)),
                "the play-the-chart toggle is icon-only and has no accessible name");
            Assert.True(ToolTip.GetShowOnDisabled(toggle),
                "a disabled play-the-chart toggle would hide the reason it is disabled");
        });
    }

    /// <summary>
    ///     The loop-region toggle is disabled until a region is marked; its tip says how to
    ///     mark one, so it must show on the disabled button too.
    /// </summary>
    [Fact]
    public async Task Toolbar_loopToggleShowsItsHintWhileDisabled()
    {
        await WithLaidOut(() => new ToolbarView(), view =>
        {
            var toggle = Assert.IsType<ToggleButton>(Find(view, "LoopToolbarToggle"));
            Assert.True(ToolTip.GetShowOnDisabled(toggle),
                "a disabled loop toggle would hide how to mark a loop region");
        });
    }

    [Fact]
    public async Task StatusBar_buildsAttachesAndLaysOutItsMetricCluster()
    {
        await WithLaidOut(() => new StatusBar(), view =>
        {
            AssertLaidOut(view, "StatusMetricCluster");

            // The RAIL states its own height (`Border.FireStatusBar` → the
            // `FireStatusBarHeight` token); the UserControl around it stretches,
            // because in the shell it sits in an Auto row that sizes to this.
            var rail = view.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("FireStatusBar"));
            Assert.True(rail.Bounds.Height is > 20 and < 40,
                $"the status rail laid out {rail.Bounds.Height:0.#} px tall — it is a rail, not a panel");

            // …and every read-out on it really got the mono face. A rail of
            // proportional text twitches on every clock tick.
            var readouts = view.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("FireMono"))
                .ToList();
            Assert.True(readouts.Count >= 6,
                $"only {readouts.Count} mono read-outs on the status rail");
            Assert.All(readouts, t => Assert.Equal("JetBrains Mono", t.FontFamily.Name));

            AssertIconsResolved(view, nameof(StatusBar));
        });
    }

    /// <summary>
    ///     The rail's ONE message is drawn by SEVERITY: an Error write must
    ///     resolve a different Foreground than an Info write, and bring a
    ///     resolved glyph with it. Driven through the REAL view model over the
    ///     real <c>ProjectState</c>, because the fault this guards — every
    ///     writer setting the same faint string — lived in the wiring, not the
    ///     brushes.
    /// </summary>
    [Fact]
    public async Task StatusBar_drawsErrorAndSuccessInADifferentBrushThanInfo()
    {
        var state = new ProjectState();
        var dispatcher = new InlineUiDispatcher();
        using var vm = new StatusBarViewModel(
            new StatusBarService(dispatcher), state, new NullAudioPlaybackService(),
            dispatcher, new StubProfiles(), new FakeFileStorageService(), FakeLocalization.English());

        await WithLaidOut(() => new StatusBar { DataContext = vm }, view =>
        {
            var text = (TextBlock)Find(view, "StatusMessageText");
            var glyph = (PathIcon)Find(view, "StatusMessageGlyph");

            state.MainDisplayMessage = "Loaded chart";
            view.UpdateLayout();
            var info = text.Foreground?.ToString();
            Assert.False(glyph.IsVisible, "an Info message must not carry a glyph");

            state.SetMessage("Save failed: access denied", StatusSeverity.Error);
            view.UpdateLayout();
            var error = text.Foreground?.ToString();
            Assert.True(glyph.IsVisible && glyph.Data != null,
                "an Error message must show a resolved glyph beside the text");
            Assert.True(glyph.Bounds.Width > 1 && glyph.Bounds.Height > 1,
                $"the error glyph laid out as {glyph.Bounds}");
            var errorGlyph = glyph.Foreground?.ToString();
            var errorGeometry = glyph.Data;

            state.SetMessage("Project saved", StatusSeverity.Success);
            view.UpdateLayout();
            var success = text.Foreground?.ToString();
            var successGlyph = glyph.Foreground?.ToString();
            // The geometry is a STYLE setter (a local Data would beat it), so
            // the alert must really give way to the check.
            Assert.NotSame(errorGeometry, glyph.Data);

            state.SetMessage("Partial export", StatusSeverity.Warning);
            view.UpdateLayout();
            var warning = text.Foreground?.ToString();

            Assert.NotEqual(info, error);
            Assert.NotEqual(info, success);
            Assert.NotEqual(info, warning);
            Assert.NotEqual(error, success);
            Assert.NotEqual(errorGlyph, successGlyph);

            // …and a plain write takes the rail back to its resting face.
            state.MainDisplayMessage = "Loaded chart";
            view.UpdateLayout();
            Assert.Equal(info, text.Foreground?.ToString());
            Assert.False(glyph.IsVisible);
        });
    }

    // ── The menu bar (0.50) and the activity rail (0.62) ──────────────

    /// <summary>
    ///     The menu bar builds and lays out. Its CHART / LIBRARY / SETTINGS
    ///     switcher left in 0.62 for <c>ActivityRailView</c>, so what is left to
    ///     assert here is the menu itself — and that it still fits the 38 px bar.
    /// </summary>
    [Fact]
    public async Task MenuBar_laysOutItsMenu()
    {
        await WithLaidOut(() => new MainMenuView(), view =>
        {
            var menu = view.GetVisualDescendants().OfType<Menu>().Single();
            Assert.True(menu.Bounds.Width > 100 && menu.Bounds.Height > 1,
                $"the menu laid out as {menu.Bounds} — its eight headers rendered nothing");

            // §6.8 in reverse: a bar that states its height hands its content
            // whatever is left, and a control that needs more measures its
            // captions at 0 × 0 while every container still measures fine.
            Assert.True(menu.Bounds.Height <= 36,
                $"the menu headers are {menu.Bounds.Height:0.#} px tall — they do not fit the 38 px top bar");

            AssertIconsResolved(view, nameof(MainMenuView));
        });
    }

    /// <summary>
    ///     The window chrome (#654) builds, lays out and, for each of its three rows
    ///     shown alone in a row of its own height as the shell shows it, MEASURES:
    ///     the row, its move-drag strip and its three caption buttons with their
    ///     glyphs. <c>WindowDecorations="BorderOnly"</c> means the OS draws none of
    ///     this, so a row that styled to nothing is a window that cannot be moved,
    ///     minimised or closed. The maximize glyph swaps to the restore glyph and back.
    /// </summary>
    [Theory]
    [InlineData("TitleBarRow", "TitleBarDragArea", ShellPresentation.TitleBarHeight)]
    [InlineData("BackBar", "BackBarDragArea", ShellPresentation.BackBarHeight)]
    [InlineData("WindowCaption", "WindowCaptionDragArea", ShellPresentation.WindowCaptionHeight)]
    public async Task WindowChrome_laysOutEachRowWithItsDragStripAndCaptionButtons(string shown, string strip, double rowHeight)
    {
        string[] rows = ["TitleBarRow", "BackBar", "WindowCaption"];
        await WithLaidOut(() =>
        {
            // MainWindow's TitleContentGrid in miniature: the chrome in a row of the
            // shown row's height, the content under it.
            var chrome = new WindowChromeView();
            foreach (var row in rows)
                chrome.FindControl<Grid>(row)!.IsVisible = row == shown;
            var host = new Grid { RowDefinitions = new RowDefinitions($"{rowHeight},*") };
            host.Children.Add(chrome);
            return host;
        }, view =>
        {
            var chrome = view.GetVisualDescendants().OfType<WindowChromeView>().Single();
            Assert.True(chrome.Bounds.Width > 1 && chrome.Bounds.Height > 1, $"the chrome laid out as {chrome.Bounds}");
            Assert.Equal(rowHeight, chrome.Bounds.Height);

            var row = chrome.FindControl<Grid>(shown)!;
            Assert.True(row.Bounds.Width > 1 && row.Bounds.Height > 1, $"{shown} laid out as {row.Bounds}");

            var drag = chrome.FindControl<Border>(strip);
            Assert.True(drag is { Bounds: { Width: > 1, Height: > 1 } },
                $"`{strip}` laid out as {drag?.Bounds} — {shown} cannot move the window");
            Assert.Contains(drag, chrome.DragAreas);
            Assert.True(drag!.Background is not null, $"`{strip}` has no Background, so it is not hit-testable");

            var windowButton = Application.Current!.FindResource("WindowBtn");
            var buttons = row.GetVisualDescendants().OfType<Button>().Where(b => ReferenceEquals(b.Theme, windowButton)).ToList();
            Assert.Equal(3, buttons.Count);
            Assert.All(buttons, b =>
            {
                Assert.True(b.Bounds.Width > 1 && b.Bounds.Height > 1, $"a caption button in {shown} laid out as {b.Bounds}");
                Assert.True(b.GetVisualDescendants().OfType<PathIcon>().Single().Data is not null,
                    $"a caption button in {shown} has no glyph");
            });
            Assert.Contains("CloseBtn", buttons[2].Classes);

            var glyph = chrome.MaximizeGlyphs[Array.IndexOf(rows, shown)];
            Assert.Same(glyph, buttons[1].GetVisualDescendants().OfType<PathIcon>().Single());
            var restored = glyph.Data;
            chrome.ShowWindowState(WindowState.Maximized);
            var maximized = glyph.Data;
            Assert.NotSame(restored, maximized);
            Assert.NotEqual(restored!.Bounds, maximized!.Bounds);
            chrome.ShowWindowState(WindowState.Normal);
            Assert.NotEqual(maximized.Bounds, glyph.Data!.Bounds);

            AssertIconsResolved(view, nameof(WindowChromeView));
        });
    }

    /// <summary>
    ///     The activity rail builds, lays out, and every row MEASURES — frame,
    ///     glyph and caption.
    ///     <para>
    ///         This is the assertion the rail NEEDS. A <c>Classes="FireRailItem"</c>
    ///         that matched nothing would render as a plain transparent button
    ///         holding a bare glyph and a grey caption, which is exactly what the
    ///         library's FILTERS pill shipped as, and every per-element bounds
    ///         check still passes. So the rail's own frame is measured, its rows
    ///         are measured against it, and the glyph geometry is checked to have
    ///         resolved — an unresolvable <c>{DynamicResource Icon…}</c> is a blank
    ///         column with no exception and no log line.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task ActivityRail_laysOutEveryDestination()
    {
        await WithLaidOut(() => new ActivityRailView(), view =>
        {
            var frame = view.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("FireActivityRail"));
            Assert.True(frame.Bounds.Width > 40 && frame.Bounds.Height > 100,
                $"the rail laid out as {frame.Bounds} — its style matched nothing");

            var rows = view.GetVisualDescendants().OfType<ToggleButton>().ToList();
            Assert.Equal(5, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.True(row.Bounds.Width > 40 && row.Bounds.Height > 40,
                    $"a rail row laid out as {row.Bounds} — its contents rendered nothing");

                var glyph = row.GetVisualDescendants().OfType<PathIcon>().Single();
                Assert.True(glyph.Data != null,
                    "a rail glyph has no geometry — its icon resource key is wrong, and that renders " +
                    "as a blank space with no exception");
                Assert.True(glyph.Bounds.Width > 1 && glyph.Bounds.Height > 1,
                    $"a rail glyph laid out as {glyph.Bounds}");

                var caption = row.GetVisualDescendants().OfType<TextBlock>().Single();
                Assert.True(caption.Bounds.Width > 1 && caption.Bounds.Height > 1,
                    $"a rail caption laid out as {caption.Bounds} — the row is a glyph with no name " +
                    "under it");
            });

            // Every row is inside the rail's width. A row that overflowed would
            // be clipped against the content beside it with no warning.
            Assert.All(rows, row => Assert.True(row.Bounds.Width <= frame.Bounds.Width,
                $"a rail row is {row.Bounds.Width:0.#} px wide inside a {frame.Bounds.Width:0.#} px rail"));

            AssertIconsResolved(view, nameof(ActivityRailView));
        });
    }

    /// <summary>
    ///     The rail's SELECTED look is the one thing a construction test can
    ///     otherwise miss entirely: with no view model nothing is checked, so the
    ///     active fill and the edge bar are never exercised. Check one by hand
    ///     and assert both really changed — a <c>:checked</c> rule that stopped
    ///     matching leaves the rail looking like five inactive rows forever, on
    ///     every screen, which is the whole of the app's navigation state.
    /// </summary>
    [Fact]
    public async Task TheRailsActiveRow_ReallyLights()
    {
        await WithLaidOut(() => new ActivityRailView(), view =>
        {
            var row = view.GetVisualDescendants().OfType<ToggleButton>().First();

            var bar = row.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Bar");
            var fill = row.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Fill");
            var idleBar = bar.Background?.ToString();
            var idleFill = fill.Background?.ToString();

            row.IsChecked = true;
            view.UpdateLayout();

            Assert.NotEqual(idleBar, bar.Background?.ToString());
            Assert.NotEqual(idleFill, fill.Background?.ToString());
        });
    }

    /// <summary>
    ///     The DROPDOWNS, which no other test in this file can see: a menu's rows
    ///     live inside a closed <c>Popup</c>, so they are not in the visual tree
    ///     and <c>WithLaidOut</c>'s layout pass never touches them. Realizing them
    ///     through <see cref="MenuFlyoutPrewarmer" /> is the only way to lay out
    ///     ~126 rows of markup that otherwise first runs on a user's click — and
    ///     it is where a bad <c>{StaticResource Icon…}</c>, a zero-measuring row
    ///     or an empty group would show up.
    /// </summary>
    [Fact]
    public async Task MenuBar_realizesEveryDropdownRow_withoutOpeningAnything()
    {
        await WithLaidOut(() => new MainMenuView(), view =>
        {
            var menus = ((MainMenuView)view).TopLevelMenus();
            Assert.Equal(8, menus.Count);

            var realized = menus.Sum(m => MenuFlyoutPrewarmer.Realize(m, new Size(W, H)));
            Assert.True(realized > 100,
                $"the prewarm realized {realized} rows — the bar carries far more than that, so it " +
                "stopped early and those dropdowns still pay their first open on the user's click");

            // Nothing may be OPEN afterwards: a prewarm that opened popups would
            // be the flicker it exists to remove.
            Assert.All(Rows(menus), row => Assert.False(row.IsSubMenuOpen));

            // Every row measured. A row that failed to style still arranges, it
            // just has no size — the shape every silent XAML fault here takes.
            foreach (var row in Rows(menus))
            {
                Assert.True(row.DesiredSize is { Width: > 1, Height: > 1 },
                    $"the menu row '{row.Header}' measured {row.DesiredSize} — it renders nothing");
            }

            // …including the icon on it. These never appeared in any layout
            // before, so nothing has ever checked their geometry resolved.
            foreach (var row in Rows(menus))
            {
                if (row.Icon is PathIcon icon)
                {
                    Assert.True(icon.Data != null,
                        $"the icon on menu row '{row.Header}' has no geometry — its resource key is wrong");
                }
            }
        });
    }

    /// <summary>
    ///     The five submenus the 0.52 grouping added exist, are captioned, and
    ///     each really contains rows. A group whose localization key went missing
    ///     renders as a blank row that opens onto nothing.
    /// </summary>
    [Fact]
    public async Task TheGroupedMenus_carryCaptionedSubmenusThatContainRows()
    {
        await WithLaidOut(() => new MainMenuView(), view =>
        {
            var menus = ((MainMenuView)view).TopLevelMenus();
            foreach (var m in menus) MenuFlyoutPrewarmer.Realize(m, new Size(W, H));

            var groups = Rows(menus).Where(r => r.Items.OfType<MenuItem>().Any()).ToList();

            // Import + Export + Select + Snap & Length + Start & End + Panels +
            // Timeline Rows + Bookmarks + the six Tools groups.
            Assert.True(groups.Count >= 13,
                $"only {groups.Count} submenus in the whole bar — the grouping was undone");

            Assert.All(groups, g =>
            {
                Assert.False(string.IsNullOrWhiteSpace(g.Header?.ToString()),
                    "a submenu header rendered blank — its localization key does not resolve");
                Assert.True(g.Items.OfType<MenuItem>().Count() >= 2,
                    $"the submenu '{g.Header}' holds fewer than two rows — an extra click for nothing");
            });
        });
    }

    /// <summary>Every row under <paramref name="menus" />, at any depth.</summary>
    private static IEnumerable<MenuItem> Rows(IEnumerable<MenuItem> menus)
    {
        foreach (var menu in menus)
        {
            foreach (var row in menu.Items.OfType<MenuItem>())
            {
                yield return row;
                foreach (var nested in Rows([row])) yield return nested;
            }
        }
    }

    [Fact]
    public async Task StackedTracks_buildsAttachesAndLaysOut()
    {
        // No entries without a MainWindowViewModel, so this proves the shell
        // chrome and style scope; the following test exercises the row template.
        await WithLaidOut(() => new StackedTracksView(), view =>
            AssertIconsResolved(view, nameof(StackedTracksView)));
    }

    [Fact]
    public async Task StackedTrackRow_MarqueeBindsToItsOwnRenderer()
    {
        await WithLaidOut(() =>
        {
            var view = new StackedTracksView();
            var host = view.FindControl<ItemsControl>("StackedItemsHost")!;
            // Real row markup with an empty context: only the element-name
            // binding is under test; no app/audio session is required.
            return Assert.IsAssignableFrom<Control>(host.ItemTemplate!.Build(null));
        }, row =>
        {
            var renderer = Assert.Single(row.GetVisualDescendants().OfType<ChartRenderer>());
            var overlay = Assert.Single(row.GetVisualDescendants().OfType<MarqueeSelectionOverlay>());
            Assert.True(renderer.Bounds.Width > 0 && renderer.Bounds.Height > 0);
            Assert.Same(renderer, overlay.Renderer);
        });
    }

    // ── The chart editor's WORKSPACE grammar (0.51) ─────────────────────────

    /// <summary>
    ///     The timeline's ROWS strip and its GUTTER, laid out under the real
    ///     grammar at the width the gutter actually gets.
    ///     <para>
    ///         Assembled here rather than pulled out of <c>MainWindow.axaml</c>
    ///         because building MainWindow needs the whole DI container;
    ///         <see cref="UI.EditorWorkspaceChromeTests" /> is the source lint
    ///         that keeps the shell's markup and this shape the same, and
    ///         <see cref="UI.TimelineGutterLayoutTests" /> owns the arithmetic.
    ///         What only a real layout can prove is what is here: that the
    ///         strip's pills really pick up their checked fill (a
    ///         <c>ContentPresenter</c> rule that stopped matching leaves a
    ///         perfectly functional toggle showing no state at all), that the
    ///         longest GERMAN caption still fits the strip, and that the gutter
    ///         draws captions and string ticks against a real
    ///         <c>ChartRenderer</c> instead of rendering an empty column.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task EditorRowsStripAndGutter_layOutUnderTheWorkspaceGrammar()
    {
        const double gutterWidth = 160;

        await HeadlessSessionFixture.Dispatch(() =>
        {
            var pills = new List<ToggleButton>();
            var labels = new List<TextBlock>();
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            row.Children.Add(new TextBlock { Classes = { "FireEyebrow" }, Text = "ZEILEN" }.Themed());

            // The real de-DE captions; the longest is what this measures.
            var on = true;
            foreach (var caption in new[] { "ABSCHNITTE", "PHRASEN", "LYRICS", "TONES", "TAKT-INFO" })
            {
                var label = new TextBlock { Text = caption };
                var pill = new ToggleButton { Classes = { "RibbonRow" }, Content = label, IsChecked = on };
                on = !on;
                pills.Add(pill);
                labels.Add(label);
                row.Children.Add(pill);
            }

            var strip = new Border { Classes = { "RowsStrip" }, Child = row };

            // A real renderer, with no Editor: TopRibbon falls back to
            // RibbonLayout.Default, which is the pre-first-frame path the gutter
            // has to survive.
            var renderer = new ChartRenderer();
            var gutter = new TimelineGutterControl { Renderer = renderer };
            // The gutter is a bare Control with no intrinsic size — in the
            // shell it fills a `*` Grid row, and the height is stated here
            // for the same reason.
            var frame = new Border
            {
                Classes = { "TrackRail" }, Width = gutterWidth, Height = 520, Child = gutter
            };

            var root = new StackPanel();
            root.Children.Add(strip);
            root.Children.Add(frame);

            var window = new Window { Width = W, Height = H, Content = root };
            window.Styles.Add(WorkspaceGrammar());
            window.Show();
            try
            {
                root.Measure(new Size(W, H));
                root.Arrange(new Rect(0, 0, W, H));

                Assert.True(strip.Bounds is { Width: > 1, Height: > 1 },
                    $"the ROWS strip laid out as {strip.Bounds} — it rendered nothing");
                Assert.True(strip.Background != null,
                    "Border.RowsStrip has no fill — EditorWorkspaceStyles.axaml did not reach it");

                // A CHECKED pill must look different from an unchecked one, and
                // the difference lives on the presenter: Fluent's own :checked
                // rule writes that Background as a local value, so a rule set on
                // the control is simply painted over and the strip shows no
                // state while toggling perfectly well.
                var fills = pills.Select(p => p.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                    .FirstOrDefault()?.Background?.ToString()).ToList();
                Assert.True(fills[0] != fills[1],
                    "a shown row and a hidden row draw the same fill — the pill's :checked rule stopped " +
                    "matching the ContentPresenter, so the strip reports nothing");

                foreach (var label in labels)
                {
                    Assert.True(label.Bounds is { Width: > 1, Height: > 1 },
                        $"a ROWS pill's caption laid out as {label.Bounds}");
                }

                // The gutter: real size, and a real Render pass. A caption that
                // threw would take the frame with it.
                Assert.True(frame.Bounds is { Width: > 1, Height: > 1 },
                    $"the gutter frame laid out as {frame.Bounds}");
                Assert.True(gutter.Bounds is { Width: > 1, Height: > 1 },
                    $"the gutter laid out as {gutter.Bounds} — it rendered nothing");
                Assert.Equal(gutterWidth, frame.Bounds.Width, 1);

                // …and the captions really land on the ribbon's own lane pitch.
                // The gutter draws a hairline seam at the FLOOR of every caption
                // band; the renderer beside it draws each bar at
                // RibbonLayout.BarTopIn of the same row Y. So the seams are where
                // the two grammars meet, and a gutter that had kept its own copy
                // of the pitch would put every caption on the wrong lane while
                // still measuring, still rendering and still passing every other
                // assertion in this file.
                var group = new Avalonia.Media.DrawingGroup();
                using (var ctx = group.Open()) gutter.Render(ctx);

                var seams = group.Children
                    .OfType<Avalonia.Media.GeometryDrawing>()
                    .Select(d => d.Geometry?.Bounds ?? default)
                    .Where(b => b.Height is > 0 and <= 1.5 && b.Width > 1)
                    .Select(b => Math.Round(b.Y, 2))
                    .ToHashSet();

                Assert.True(seams.Count > 0,
                    "the gutter's render recorded no seam rectangles at all — this probe has gone " +
                    "blind and everything below it is vacuous");

                var ribbon = RibbonLayout.Default;
                foreach (var (rowY, height, name) in new (float, float, string)[]
                         {
                             (ribbon.ToneY, RibbonLayout.RowHeight, "TONES"),
                             (ribbon.SectionY, RibbonLayout.RowHeight, "SECTIONS"),
                             (ribbon.PhraseY, RibbonLayout.RowHeight, "PHRASES"),
                             (ribbon.LyricY, RibbonLayout.RowHeight, "LYRICS"),
                             (ribbon.MetadataY, RibbonLayout.MetadataBandHeight, "MEASURES")
                         })
                {
                    Assert.True(seams.Contains(Math.Round((double)(rowY + height - 1), 2)),
                        $"the {name} caption band does not end at {rowY + height - 1} — the gutter is " +
                        "captioning a different grid from the one the renderer draws bars on. " +
                        $"Seams drawn: {string.Join(", ", seams.OrderBy(s => s))}");
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The fixed-pane header and the 3D preview's editor strip, laid out
    ///     under the same grammar.
    ///     <para>
    ///         <c>Preview3DView</c> itself is deliberately NOT constructed: its
    ///         ctor builds the Vulkan viewport control (a NativeControlHost), and
    ///         attaching that in a headless session is a native-HWND lifetime
    ///         change, not a layout test. What is under test is the strip's
    ///         chrome, which is markup.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task EditorPaneHeaderAndPreviewStrip_layOutUnderTheWorkspaceGrammar()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var title = new TextBlock { Classes = { "PaneTitle" }, Text = "3D-VORSCHAU" };
            var action = new Button { Classes = { "PaneAction" }, Content = new TextBlock { Text = "↗" } };
            var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            headerRow.Children.Add(title);
            Grid.SetColumn(action, 1);
            headerRow.Children.Add(action);
            var header = new Border { Classes = { "PaneHeader" }, Child = headerRow };

            var toggle = new ToggleButton
            {
                Classes = { "HudAction" }, IsChecked = true,
                Content = new TextBlock { Text = "Live-Spiel" }
            };
            var label = new TextBlock { Classes = { "HudLabel" }, Text = "SCORE" };
            var stat = new TextBlock { Classes = { "HudStat" }, Text = "12345" };
            var barRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            barRow.Children.Add(toggle);
            barRow.Children.Add(label);
            barRow.Children.Add(stat);
            var bar = new Border { Classes = { "HudBar" }, Child = barRow };

            var root = new StackPanel();
            root.Children.Add(header);
            root.Children.Add(bar);

            var window = new Window { Width = W, Height = H, Content = root };
            window.Styles.Add(WorkspaceGrammar());
            window.Show();
            try
            {
                root.Measure(new Size(W, H));
                root.Arrange(new Rect(0, 0, W, H));

                foreach (var (control, what) in new (Control, string)[]
                         {
                             (header, "the pane header"), (title, "its title"), (action, "its icon action"),
                             (bar, "the 3D preview strip"), (toggle, "a strip toggle"),
                             (label, "a strip label"), (stat, "a strip value")
                         })
                {
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 },
                        $"{what} laid out as {control.Bounds} — it rendered nothing");
                }

                // The header keeps the compact Dock tool chrome's own height, or
                // the fixed 3D-preview pane stops lining up with the dockable
                // siblings it sits beside.
                Assert.Equal(22, header.Bounds.Height, 1);

                // A strip control has to stay dense: FireGhost is 48 px tall
                // (42 compact) and a dozen of those would push the viewport off
                // the bottom of the pane, which is why HudAction restates it.
                Assert.True(toggle.Bounds.Height < 30,
                    $"a strip toggle is {toggle.Bounds.Height:0.#} px tall — it picked up a full-size " +
                    "Fire action instead of the strip's own density");

                Assert.True(header.Background != null && bar.Background != null,
                    "the pane header or the preview strip has no fill — the workspace grammar did not " +
                    "reach it and the strip is transparent over the viewport behind it");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>The grammar, loaded exactly the way the four real views load
    /// it.</summary>
    private static StyleInclude WorkspaceGrammar()
    {
        var uri = new Uri("avares://DotCharter/Views/Editor/EditorWorkspaceStyles.axaml");
        return new StyleInclude(uri) { Source = uri };
    }

    [Fact]
    public async Task AmpFxPanel_buildsAttachesAndLaysOutItsThreeSections()
    {
        await WithLaidOut(() => new AmpFxPanel(), view =>
        {
            AssertLaidOut(view, "AmpFxRoot");

            // Three Fire cards (plugins, virtual instrument, drop tune) — the
            // rig's own language, not the retired ShellBadge / RackSectionTitle.
            foreach (var section in new[] { "AmpFxPluginsSection", "AmpFxInstrumentSection", "AmpFxDropTuneSection" })
            {
                AssertLaidOut(view, section);
                Assert.Contains("FirePanel", Find(view, section).Classes);
            }
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("ShellBadge"));

            // The chain is the SHARED rack, not a template of this panel's own.
            Assert.IsType<DotCharter.Tone.UI.Views.PlayMode.VstRackView>(Find(view, "AmpFxVstRack"));
            AssertLaidOut(view, "AmpFxVstRack");

            // The per-seat drop-tune picker (ABI v31) CONSTRUCTS and lays out.
            // Visibility is deliberately not asserted here: this harness attaches
            // the panel with no DataContext, so `IsVisible="{Binding
            // HasMultipleDropTuneSeats}"` never resolves and sits at its default.
            // The hide-with-one-seat rule is a VIEW-MODEL fact and is pinned
            // where it can actually be observed — UI/DropTuneSeatTests; the
            // two-seat layout is Perf/AmpFxRackUiPerfSmokeTests.
            var seatPicker = view.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => c.Name == "DropTuneSeatPicker");
            Assert.NotNull(seatPicker);
            Assert.True(seatPicker!.Bounds.Width > 1 && seatPicker.Bounds.Height > 1,
                $"the drop-tune seat picker laid out as {seatPicker.Bounds}");

            // ABI v38: the built-in chart synth and the instrument route sit in the
            // instrument card, with a reason line under each for when it is off.
            AssertLaidOut(view, "AmpFxChartSynthToggle");
            AssertLaidOut(view, "AmpFxInstrumentThroughRigToggle");

            AssertIconsResolved(view, nameof(AmpFxPanel));
        });
    }

    [Fact]
    public async Task CommandPalette_buildsAttachesAndLaysOut()
    {
        await WithLaidOut(() => new CommandPaletteView(), view =>
        {
            // With no view model IsOpen produces no value, so the scrim keeps
            // its default (visible) — which is exactly the state we want to
            // measure.
            AssertLaidOut(view, "PaletteCard");
            Assert.NotNull(Find(view, "SearchBox"));
            Assert.NotNull(Find(view, "ResultList"));
            AssertIconsResolved(view, nameof(CommandPaletteView));
        });
    }

    [Fact]
    public async Task DiagnosticsWindow_buildsAndLaysOutSixChannelCards()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var window = new DiagnosticsWindow();
            window.Show();
            try
            {
                window.UpdateLayout();

                // The class is DlgSection, not ShellCard: a92b1fb7 rebuilt this
                // window as Session Health status cards over the real channels
                // and renamed them. This assertion kept naming the old class and
                // so found zero cards from that commit until 0.44 — it failed
                // LOUDLY the whole time, and nobody saw it, because Perf/ is
                // excluded from the default gate. A suite outside the gate is a
                // suite whose failures rot.
                var cards = window.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("DlgSection"))
                    .ToList();
                Assert.Equal(6, cards.Count);
                Assert.All(cards, c => Assert.True(c.Bounds.Width > 1 && c.Bounds.Height > 1,
                    $"a diagnostics channel card laid out as {c.Bounds}"));

                AssertIconsResolved(window, nameof(DiagnosticsWindow));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The profile switcher is a bare <c>DataTemplate</c> in a
    ///     <c>ResourceDictionary</c>, so it carries NO styles of its own and is
    ///     realized inside whichever host opened the flyout. It may therefore
    ///     only use class names BOTH hosts define. Building it under the status
    ///     bar's style scope is what proves that contract still holds.
    /// </summary>
    [Fact]
    public async Task ProfileSwitcherFlyout_buildsUnderTheStatusBarStyleScope()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var host = new StatusBar();
            // TryFindResource, not the Resources indexer: the switcher arrives
            // through a ResourceInclude, i.e. a MERGED dictionary, which the
            // indexer does not search.
            Assert.True(host.TryFindResource("ProfileSwitcherFlyoutContent", out var found),
                "the status bar no longer merges ProfileSwitcherFlyout.axaml — the profile " +
                "flyout would open empty");
            var template = Assert.IsAssignableFrom<IDataTemplate>(found);

            var content = template.Build(null);
            Assert.NotNull(content);

            // Hosted INSIDE the status bar so the flyout's `Eyebrow` /
            // `HeaderRule` classes resolve exactly as they do at runtime.
            var probe = new ContentControl { Content = content };
            var shell = new StackPanel();
            shell.Children.Add(host);
            shell.Children.Add(probe);

            var window = new Window { Width = 640, Height = 640, Content = shell };
            window.Show();
            try
            {
                shell.Measure(new Size(640, 640));
                shell.Arrange(new Rect(0, 0, 640, 640));

                Assert.True(content!.Bounds.Width > 1 && content.Bounds.Height > 1,
                    $"the profile switcher laid out as {content.Bounds} — it rendered nothing");

                // Since 0.54 the switcher is a `FirePopover` filled with the
                // `Fire*` popover grammar, and BOTH of those are app-level —
                // which is the point: they resolve in the status bar and on the
                // Play Mode front door alike. A class defined in the editor's
                // chrome grammar would match nothing in the second host.
                var section = content.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(b => b.Classes.Contains("FirePopoverSection"));
                Assert.True(section is { Bounds.Width: > 1, BorderThickness.Top: > 0 },
                    "the switcher's sections did not pick up the Fire popover grammar — the flyout " +
                    "may only use marks EVERY host defines, and this one is realized in two");

                AssertIconsResolved(content, "ProfileSwitcherFlyoutContent");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Takes a FACTORY, never a ready-made control: the view has to be built
    ///     on the session's dispatcher thread. Constructing it on the xUnit
    ///     thread and handing it in "works" for simple views and then fails two
    ///     unrelated-looking ways — <c>Unable to locate
    ///     'Avalonia.Platform.IPlatformRenderInterface'</c> the moment any
    ///     geometry is parsed, and <c>The calling thread cannot access this
    ///     object</c> as soon as the window adopts it. Both were hit while
    ///     writing this file, and both read like product faults.
    /// </summary>
    private static async Task WithLaidOut(Func<Control> factory, Action<Control> assert)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = factory();
            // Show() is what roots the tree and raises OnAttachedToLogicalTree —
            // where a Button asks its Command for CanExecute. Layout is driven
            // through the VIEW, not the window: measuring a headless Window
            // directly reports "Invalid size returned for Measure".
            var window = new Window { Width = W, Height = H, Content = view };
            window.Show();
            try
            {
                view.Measure(new Size(W, H));
                view.Arrange(new Rect(0, 0, W, H));

                Assert.True(view.Bounds.Width > 1 && view.Bounds.Height > 1,
                    $"{view.GetType().Name} laid out as {view.Bounds} — it rendered nothing");
                assert(view);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>Every realized glyph resolved its geometry. A
    /// <c>{DynamicResource}</c> miss leaves <c>Data</c> null and draws nothing —
    /// silently, which is the whole reason this assertion exists.</summary>
    private static void AssertIconsResolved(Visual root, string what)
    {
        var icons = root.GetVisualDescendants().OfType<PathIcon>().ToList();
        Assert.All(icons, icon => Assert.True(icon.Data != null,
            $"a PathIcon in {what} has no geometry — its DynamicResource key does not exist"));
    }

    private static Control Find(Control view, string name)
    {
        var control = view.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.Name == name)
            ?? view.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
        Assert.True(control != null,
            $"'{name}' is not in the tree — the view never realized (a XAML fault or a rename)");
        return control!;
    }

    private static void AssertLaidOut(Control view, string name)
    {
        var control = Find(view, name);
        Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1,
            $"'{name}' laid out as {control.Bounds} — it rendered nothing");
    }

    /// <summary>Runs posts inline: the status-bar view model forwards every
    /// state change through the dispatcher, and the test drives it on the
    /// session's UI thread already.</summary>
    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    private sealed class StubProfiles : IPlayerProfileService
    {
        private readonly PlayerProfile _active = new() { Id = Guid.NewGuid(), Name = "Ana" };
        public IReadOnlyList<PlayerProfile> Profiles => new[] { _active };
        public PlayerProfile ActiveProfile => _active;
        public event Action<PlayerProfile>? ActiveProfileChanged { add { } remove { } }
        public string GetDataDirectory(Guid id) => Path.Combine(Path.GetTempPath(), "dotcharter-shell-smoke", id.ToString("N"));
        public void InvalidateCache() { }
        public PlayerProfile Create(string name, string? accentColor = null) => _active;
        public void Rename(Guid id, string newName) { }
        public void SetLeftyFlip(Guid id, bool lefty) { }
        public void SetTuningMatch(Guid id, string myTuningId, bool playInMyTuning) { }
        public bool SwitchTo(Guid id) => false;
        public bool Delete(Guid id) => false;
        public bool SetAvatar(Guid id, string sourceImagePath) => false;
        public string? GetAvatarPath(Guid id) => null;
        public bool ExportProfile(Guid id, string zipPath) => false;
        public PlayerProfile? ImportProfile(string zipPath) => null;
    }

    /// <summary>Plausible library rows: two arrangements (so the arrangement
    /// chip has something to say), a tuning, a capo on every third song and a
    /// real <c>.psarc</c> file name.</summary>
    private static List<PsarcSongListItem> SampleSongs(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i => new PsarcSongListItem(new LibrarySongEntry
        {
            Title = $"A Reasonably Long Song Title {i}",
            Artist = $"Artist {i}",
            Album = $"Album {i}",
            SongKey = $"Song{i}",
            PackagePath = Path.Combine(Path.GetTempPath(), $"pack_{i}_p.psarc"),
            SongLength = 180 + i,
            TuningSummary = "E Standard",
            CapoSummary = i % 3 == 0 ? 2 : 0,
            Arrangements =
            [
                new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = "songs/bin/lead.sng", RouteMask = 1 },
                new LibraryArrangementEntry { ArrangementName = "Rhythm", SngPath = "songs/bin/rhythm.sng", RouteMask = 2 }
            ]
        }, LocalizationService.Instance))
    ];
}
