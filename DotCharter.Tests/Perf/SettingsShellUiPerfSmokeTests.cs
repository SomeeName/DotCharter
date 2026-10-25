using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Presentation.Shared;
using DotCharter.UI;
using DotCharter.UI.Views.Settings;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Settings tab exactly as the shell hosts it, over the REAL service
///     graph: the root <see cref="ScreenHost" /> showing the settings shell
///     screen and every tab in its <see cref="ScreenTabHost" /> (<see cref="SettingsSurface" />).
///     <para>
///         What the shell's own fake cannot show: the rail lists every
///         registered tab (with its icon resolved), selecting each shows exactly
///         one body, the search finds a hit on a tab that has never been
///         visited, a rebind moves the footer counter, and the keyboard walks
///         the results without leaving the search box.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class SettingsShellUiPerfSmokeTests
{
    private sealed class Surface : IDisposable
    {
        public Surface()
        {
            Services = App.Services ?? throw new InvalidOperationException("App.Services is null — the headless app session did not boot.");
            Host = SettingsSurface.Create(Services);
            Window = new Window { Width = 1600, Height = 900, Content = Host };
            Window.Show();
            Settle();
            Shell = (SettingsShellViewModel)Host.ViewOf(typeof(ISettingsShellScreen))!.DataContext!;
            ShellView = (SettingsShellView)Host.ViewOf(typeof(ISettingsShellScreen))!;
        }

        public IServiceProvider Services { get; }

        public ScreenHost Host { get; }

        public Window Window { get; }

        public SettingsShellViewModel Shell { get; }

        public SettingsShellView ShellView { get; }

        public SettingsSession Session => Services.GetRequiredService<SettingsSession>();

        public IReadOnlyList<ISettingsRailItem> Rail => [.. Shell.Sections.SelectMany(s => s.Items)];

        public ScreenTabHost TabHost => ShellView.GetVisualDescendants().OfType<ScreenTabHost>().Single();

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            for (var pass = 0; pass < 3; pass++) Window.UpdateLayout();
        }

        /// <summary>Every tab body under the tab host that is on screen.</summary>
        public List<UserControl> VisibleBodies() =>
            TabHost.Children.OfType<UserControl>()
                .Where(u => u.IsVisible && u.IsEffectivelyVisible && u.Bounds is { Width: > 1, Height: > 1 })
                .ToList();

        public void Dispose()
        {
            if (Session.IsOpen) Session.IsOpen = false;
            Settle();
            Window.Close();
            Host.Dispose();
        }
    }

    [Fact]
    public async Task TheShell_ListsEveryRegisteredTab_WithItsGlyph_AndWearsTheGraphiteRail()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var s = new Surface();

            Assert.Equal(SettingsRail.Entries.Select(e => e.Key), s.Rail.Cast<SettingsRailItemViewModel>().Select(i => i.Tab.Key));

            var buttons = s.ShellView.GetLogicalDescendants().OfType<ToggleButton>()
                .Where(t => t.Classes.Contains("SideTabButton")).ToList();
            Assert.Equal(s.Rail.Count, buttons.Count);
            foreach (var button in buttons)
            {
                Assert.True(button.Bounds.Height > 1, $"a rail row laid out {button.Bounds} — its template rendered nothing");
                var icon = button.GetLogicalDescendants().OfType<PathIcon>().Single(p => p.Classes.Contains("SideTabIcon"));
                Assert.NotNull(icon.Data);
                // Graphite: the bar is lit ONLY on the selected row, and the
                // one-line description moved to the row's tooltip.
                var bar = button.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "Bar");
                var lit = bar?.Background is Avalonia.Media.ISolidColorBrush { Color.A: > 0 };
                Assert.True(lit == (button.IsChecked == true),
                    $"a rail row's bar is {(lit ? "lit" : "dark")} while the row is {(button.IsChecked == true ? "selected" : "not selected")}");
                Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(button) as string), "a rail entry's description (its tooltip) is empty");
                Assert.DoesNotContain(button.GetLogicalDescendants().OfType<TextBlock>(), t => t.Classes.Contains("SideTabDesc"));
            }

            // Section captions are quiet text in the reading face — not a
            // tracked mono eyebrow.
            var captions = s.ShellView.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("RailSection")).ToList();
            Assert.Equal(3, captions.Count);
            Assert.All(captions, e => Assert.True(e.FontFamily.Name == "Atkinson Hyperlegible" && e.LetterSpacing == 0,
                $"a rail caption renders in '{e.FontFamily.Name}' tracked {e.LetterSpacing} — not Graphite's quiet caption"));

            // The save block: Save over Cancel, Save the one
            // accent fill and NOT chamfered, and Reset section as a text link.
            var actions = s.ShellView.GetLogicalDescendants().OfType<Grid>().Single(g => g.Classes.Contains("SettingsActions"));
            var buttonsInBlock = actions.GetLogicalDescendants().OfType<Button>().ToList();
            Assert.Equal(2, buttonsInBlock.Count);
            Assert.All(buttonsInBlock, b => Assert.False(string.IsNullOrWhiteSpace(
                b.Content as string ?? b.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(tb => tb.IsVisible)?.Text),
                "a save-block action has no caption"));
            Assert.True(buttonsInBlock[^1].Classes.Contains("Save"), "the save block's last action is not Save");
            Assert.Equal(s.Shell.HasUnsavedChanges, buttonsInBlock[^1].Classes.Contains("Primary"));
            Assert.Null(buttonsInBlock[^1].GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "Cut"));
            Assert.Single(s.ShellView.GetLogicalDescendants().OfType<Button>(), b => b.Classes.Contains("ResetLink"));
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SelectingEachTab_ShowsExactlyItsBody_CappedAndLeftAligned_WithinTheBudget()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var s = new Surface();
            var worst = 0.0;

            foreach (var item in s.Rail)
            {
                var started = Stopwatch.GetTimestamp();
                item.SelectCommand.Execute(null);
                s.Settle();
                worst = Math.Max(worst, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                var body = Assert.Single(s.VisibleBodies());
                var key = ((SettingsRailItemViewModel)item).Tab.Key;
                // Graphite reads as ONE column from the rail's edge, not a
                // centred card: capped, and left-aligned in its pane.
                Assert.True(body.MaxWidth == 1040 && body.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Stretch,
                    $"{key}: the tab body is not capped at the column token and stretched ({body.GetType().Name})");
                var pane = s.ShellView.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TabContentGrid");
                var left = body.TranslatePoint(new Point(0, 0), pane)!.Value.X;
                Assert.True(Math.Abs(left) <= 1, $"{key}: the body starts {left:0} px into its pane, not at its edge");
                Assert.True(body.GetLogicalDescendants().OfType<Border>().Any(b => b.Classes.Contains("SettingsGroup")),
                    $"{key}: the body shows no Border.SettingsGroup");
            }

            // Both migrated screens were realized by the switches above; the
            // legacy view keeps the old page's index in step.
            Assert.NotNull(s.TabHost.ViewOf(typeof(IKeybindsSettingsScreen)));
            Assert.NotNull(s.TabHost.ViewOf(typeof(IUpdatesSettingsScreen)));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[settings shell] slowest tab switch {worst:0.0} ms (budget 150; LOGGED, never asserted)"));
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     Writes the whole Settings page — rail, save block and each tab's
    ///     body — to <c>DOTCHARTER_RIG_CAPTURE_DIR</c> as
    ///     <c>settings-page-&lt;tab&gt;.png</c>, for design review. A no-op
    ///     without the capture variables; the layout assertions live above.
    /// </summary>
    [Fact]
    public async Task EveryTab_OnThePage_CapturedForReview()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
            if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;

            using var s = new Surface();
            // Opened like the app opens it, so every tab has LOADED its values —
            // an unopened page shows empty choice lists no user ever sees.
            s.Session.IsOpen = true;
            s.Settle();
            Directory.CreateDirectory(dir);
            foreach (var item in s.Rail)
            {
                item.SelectCommand.Execute(null);
                s.Settle();
                var key = ((SettingsRailItemViewModel)item).Tab.Key;
                // Top, middle and bottom of the tab's own scroll, so a long tab
                // is reviewed whole rather than its first screen only.
                var body = s.VisibleBodies().Single();
                var scroller = body.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                var steps = scroller is { Extent.Height: var h, Viewport.Height: var v } && h > v + 1 ? 3 : 1;
                for (var step = 0; step < steps; step++)
                {
                    if (scroller is not null && steps > 1)
                    {
                        scroller.Offset = new Vector(0, (scroller.Extent.Height - scroller.Viewport.Height) * step / (steps - 1));
                        s.Settle();
                    }

                    using var frame = s.Window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    var suffix = steps == 1 ? "" : $"-{step + 1}";
                    frame.Save(Path.Combine(dir, $"settings-page-{key}{suffix}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
            }
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     AI &amp; models over the real graph: stem separation and the lyric models
    ///     have no rail rows, and their views sit on the AI page as sections, bound
    ///     to the same tab instances the shell loads and saves, measured, and
    ///     scrolling with the page rather than in a scroller of their own.
    /// </summary>
    [Fact]
    public async Task TheAiPage_HostsTheStemAndLyricSections()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var s = new Surface();
            var keys = s.Rail.Cast<SettingsRailItemViewModel>().Select(i => i.Tab.Key).ToList();
            Assert.DoesNotContain(SettingsTabKeys.StemModels, keys);
            Assert.DoesNotContain(SettingsTabKeys.LyricModels, keys);

            s.Shell.Select(SettingsTabKeys.StemModels);
            s.Settle();
            var page = Assert.Single(s.VisibleBodies());
            Assert.IsType<AiSettingsView>(page);

            var stems = page.GetVisualDescendants().OfType<StemModelsSettingsView>().Single();
            var lyrics = page.GetVisualDescendants().OfType<LyricModelsSettingsView>().Single();
            var tabs = s.Shell.TabScreens;
            Assert.DoesNotContain(typeof(IStemModelsSettingsScreen), tabs);
            foreach (var (view, contract) in new (UserControl, Type)[]
                     { (stems, typeof(IStemModelsSettingsScreen)), (lyrics, typeof(ILyricModelsSettingsScreen)) })
            {
                Assert.True(view.IsEffectivelyVisible && view.Bounds is { Width: > 1, Height: > 1 }, $"{view.GetType().Name} laid out {view.Bounds}");
                Assert.IsNotType<ScrollViewer>(view.Content);
                Assert.Same(s.Host.Services.GetRequiredService(contract), view.DataContext);
            }

            Assert.True(lyrics.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "LyricModelRows").ItemCount > 0, "no lyric model rows");
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ThePrewarm_RealizesEveryHiddenTab_OldAndNew()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var s = new Surface();

            // The host, then the owners nested inside it (the tab host and the
            // legacy adapter) — the order MainWindowPrewarm.PrewarmableSurfaceOwners keeps.
            var owners = new IPrewarmableSurfaces[] { s.Host }
                .Concat(s.Host.GetLogicalDescendants().OfType<IPrewarmableSurfaces>());
            var hidden = owners.SelectMany(o => o.HiddenSurfaces()).ToList();

            // The eight tabs not on screen (six old, two new) plus the three
            // Advanced bodies nested inside the old ones.
            Assert.True(hidden.Count >= 11, $"the settings host lists {hidden.Count} hidden surfaces");
            Assert.Contains(hidden, c => c is KeybindsSettingsView);
            Assert.Contains(hidden, c => c is UpdatesSettingsView);
            Assert.Contains(hidden, c => c.Name == "GeneralAdvancedBody");
            Assert.All(hidden, c => Assert.False(c.IsVisible, $"{c.GetType().Name} is on screen and was listed as hidden"));
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>The cross-tab guarantee, end to end and with REAL localized
    /// strings: for every tab, a query taken from that tab's own declared rows
    /// finds it while the tab has never been shown.</summary>
    [Fact]
    public async Task EveryTab_IsSearchable_WhileUnvisited()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var s = new Surface();

            foreach (var item in s.Rail.Cast<SettingsRailItemViewModel>())
            {
                var groups = item.Tab.SearchGroups;
                var probe = groups.SelectMany(g => g.Rows).Select(r => r.Label).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
                            ?? groups.Select(g => g.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                Assert.False(string.IsNullOrWhiteSpace(probe), $"{item.Tab.Key} declares no searchable row or group");

                s.Shell.SearchQuery = probe!;
                s.Shell.RunSearchNow();

                Assert.True(s.Shell.IsSearchOpen);
                Assert.Contains(s.Shell.SearchResults, h => h.TabName == item.Title);
            }

            s.Shell.ClearSearchCommand.Execute(null);
            s.Shell.RunSearchNow();
            Assert.False(s.Shell.IsSearchOpen);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheKeyboard_WalksTheHits_AndEnterJumpsToTheTab_WithoutLeavingTheBox()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var s = new Surface();
            var box = s.ShellView.FindControl<TextBox>("SettingsSearchBox")!;
            var keybinds = s.Rail.Cast<SettingsRailItemViewModel>().Single(i => i.Tab.Key == SettingsTabKeys.Keybinds);
            var probe = keybinds.Tab.SearchGroups.SelectMany(g => g.Rows).Select(r => r.Label).First(l => l.Length > 3);

            // A key press routes from the FOCUSED element, and the page's Ctrl+F
            // handler tunnels on the page: it sees a press only while focus is
            // somewhere inside the page (SongBrowserView documents the same
            // rule). Focus lands there the way it does for a user — on the rail
            // row they clicked — then the chord has to move it into the box.
            var railRow = s.ShellView.GetVisualDescendants().OfType<ToggleButton>()
                .First(b => b.Classes.Contains("SideTabButton"));
            Assert.True(railRow.Focus(), "a rail row refused focus — the page is not keyboard reachable");

            s.Window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
            Assert.True(box.IsFocused, "Ctrl+F did not focus the search box");

            s.Window.KeyTextInput(probe);
            s.Settle();
            Assert.Equal(probe, s.Shell.SearchQuery);
            Assert.True(s.Shell.HasSearchResults, "typing a real row label found nothing");

            s.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Assert.True(s.Shell.SearchResults[0].IsHighlighted, "Down did not highlight the first hit");
            Assert.True(box.IsFocused, "Down took focus out of the search box");

            s.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            s.Settle();
            Assert.Equal(SettingsTabKeys.Keybinds, s.Shell.SelectedTab?.Key);
            Assert.False(s.Shell.IsSearchOpen);
            Assert.Single(s.VisibleBodies());
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A charter's config (the editor at startup, an English UI) reads back on the General tab
    ///     as it is saved, and opening the page writes nothing: otherwise Save on any other row
    ///     switches the startup screen and the language without being asked.
    /// </summary>
    [Fact]
    public async Task TheGeneralTab_ShowsWhatTheConfigSays_OverTheRealGraph()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var s = new Surface();
            var config = s.Services.GetRequiredService<IConfigurationService>().LoadAppConfig();
            var (startup, culture) = (config.StartupScreen, config.LanguageCulture);
            try
            {
                config.StartupScreen = StartupScreen.Editor;
                config.LanguageCulture = "en-US";
                s.Session.IsOpen = true;
                s.Settle();
                s.Shell.Select(SettingsTabKeys.General);
                s.Settle();

                var general = (GeneralSettingsViewModel)s.Shell.SelectedTab!;
                Assert.Equal(1, general.SelectedStartupScreenIndex);
                Assert.True(general.SelectedLanguageIndex > 0, $"en-US read back as language row {general.SelectedLanguageIndex}");
                Assert.False(s.Shell.HasUnsavedChanges, "opening the page changed a setting");

                var view = s.TabHost.ViewOf(typeof(IGeneralSettingsScreen))!;
                Assert.Same(general, view.DataContext);
                var combos = view.GetLogicalDescendants().OfType<ComboBox>().ToList();
                Assert.Contains(combos, c => c.SelectedIndex == 1);
                Assert.Contains(combos, c => c.SelectedIndex == general.SelectedLanguageIndex);

                // …and the closed box SHOWS it: the selection box is what the user reads.
                var startupBox = combos.Single(c => c.SelectedIndex == 1 && c.ItemsSource is null);
                var shown = startupBox.SelectionBoxItem is ContentControl item ? item.Content : startupBox.SelectionBoxItem;
                Assert.Equal(LocalizationService.Instance["Settings_StartupScreen_Editor"], shown?.ToString());
                var languageBox = combos.Single(c => c.ItemsSource is not null && c.SelectedIndex == general.SelectedLanguageIndex);
                Assert.Equal(general.LanguageOptions[general.SelectedLanguageIndex], languageBox.SelectionBoxItem?.ToString());
            }
            finally
            {
                config.StartupScreen = startup;
                config.LanguageCulture = culture;
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     …and the same when the page was PREWARMED hidden first, as the app does after its
    ///     first frame: the tabs are built and measured while hidden and unloaded, then shown.
    /// </summary>
    [Fact]
    public async Task TheGeneralTab_ShowsWhatTheConfigSays_AfterAHiddenPrewarm()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services!;
            var config = services.GetRequiredService<IConfigurationService>().LoadAppConfig();
            var (startup, culture) = (config.StartupScreen, config.LanguageCulture);
            var host = SettingsSurface.Create(services);
            host.IsVisible = false;
            var window = new Window { Width = 1600, Height = 900, Content = new Panel { Children = { host } } };
            window.Show();
            try
            {
                config.StartupScreen = StartupScreen.Editor;
                config.LanguageCulture = "en-US";
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                window.UpdateLayout();
                var size = new Size(1600, 900);
                HiddenSubtreePrewarmer.Realize(host, size);
                foreach (var owner in host.GetLogicalDescendants().OfType<IPrewarmableSurfaces>().ToList())
                {
                    foreach (var part in owner.HiddenSurfaces())
                        HiddenSubtreePrewarmer.Realize(part, size);
                }

                host.IsVisible = true;
                services.GetRequiredService<SettingsSession>().IsOpen = true;
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();

                var shell = (SettingsShellViewModel)host.ViewOf(typeof(ISettingsShellScreen))!.DataContext!;
                shell.Select(SettingsTabKeys.General);
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                window.UpdateLayout();
                var general = (GeneralSettingsViewModel)shell.SelectedTab!;
                Assert.Equal(1, general.SelectedStartupScreenIndex);
                var view = host.GetLogicalDescendants().OfType<GeneralSettingsView>().Single();
                var startupBox = view.GetLogicalDescendants().OfType<ComboBox>()
                    .Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == LocalizationService.Instance["Settings_StartupScreen"]);
                var shown = startupBox.SelectionBoxItem is ContentControl item ? item.Content : startupBox.SelectionBoxItem;
                Assert.Equal(1, startupBox.SelectedIndex);
                Assert.Equal(LocalizationService.Instance["Settings_StartupScreen_Editor"], shown?.ToString());
            }
            finally
            {
                services.GetRequiredService<SettingsSession>().IsOpen = false;
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                config.StartupScreen = startup;
                config.LanguageCulture = culture;
                window.Close();
                host.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ARebind_MovesTheFooterCounter_AndResetSectionClearsIt()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var s = new Surface();
            s.Session.IsOpen = true;
            s.Settle();
            Assert.False(s.Shell.HasUnsavedChanges, "the page opened with unsaved changes before any edit");
            // With nothing to revert the save block's first button only leaves.
            Assert.Equal(LocalizationService.Instance["Action_Close"], CancelCaption(s));
            // …and the footer says nothing about unsaved changes when there are none.
            var cleanNote = s.ShellView.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("SettingsDirtyNote"));
            Assert.False(cleanNote.IsVisible, "the footer still prints a note with nothing unsaved");

            s.Shell.Select(SettingsTabKeys.Keybinds);
            s.Settle();
            var keybinds = (KeybindsSettingsViewModel)s.Shell.SelectedTab!;
            var row = keybinds.Groups[0].Rows[0];
            var original = row.Chord;
            row.Chord = new KeyChord(Key.F24, KeyModifiers.Control | KeyModifiers.Alt);

            Assert.True(s.Shell.HasUnsavedChanges);
            var note = s.ShellView.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("SettingsDirtyNote"));
            Assert.True(note.Classes.Contains("dirty"), "the footer note did not turn amber");
            Assert.True(note.IsVisible, "the footer note did not appear with an unsaved edit");
            Assert.Equal(LocalizationService.Instance["Action_Cancel"], CancelCaption(s));

            // Reset section asks first; the confirmation is the default gate's
            // (SettingsShellViewModelTests). Here: enabled on an edited tab, and the
            // tab's reset reaches the real rows.
            Assert.True(s.Shell.ResetSectionCommand.CanExecute(null), "Reset section is disabled on a tab with an unsaved edit");
            s.Shell.SelectedTab!.Reset();
            s.Settle();
            Assert.Equal(original, row.Chord);
            Assert.False(s.Shell.HasUnsavedChanges);

            // The rows materialized on the real table, and the search filter reaches them.
            var view = (KeybindsSettingsView)s.TabHost.ViewOf(typeof(IKeybindsSettingsScreen))!;
            var rows = view.GetLogicalDescendants().OfType<Grid>().Where(g => g.Classes.Contains("FormRow")).ToList();
            Assert.True(rows.Count > 100, $"only {rows.Count} keybind rows materialized");
            keybinds.ApplySearch("zzz-no-such-chord");
            s.Settle();
            Assert.DoesNotContain(rows, r => r.IsEffectivelyVisible);
            keybinds.ApplySearch("");
        }, TestContext.Current.CancellationToken);
    }

    private static string? CancelCaption(Surface s)
    {
        s.Settle();
        var actions = s.ShellView.GetLogicalDescendants().OfType<Grid>().Single(g => g.Classes.Contains("SettingsActions"));
        var cancel = actions.Children.OfType<Button>().First();
        return cancel.GetLogicalDescendants().OfType<TextBlock>().Single(tb => tb.IsVisible).Text;
    }
}
