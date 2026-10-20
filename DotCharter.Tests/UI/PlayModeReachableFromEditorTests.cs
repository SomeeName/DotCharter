namespace DotCharter.Tests.UI;

/// <summary>
///     Play Mode must be reachable from the editor by something you can SEE.
///
///     <para>
///         The 0.50 icon rail used to carry it. When that rail was deleted the
///         route survived only as a File-menu entry, and the owner reported
///         "from the editor there is no way to get back to the main game menu
///         now" — the item existed and nobody could find it, which for a whole-
///         application mode switch is the same thing as it not existing. A
///         separated button beside the menu-bar switcher answered that from 0.50
///         to 0.62.
///     </para>
///
///     <para>
///         It was briefly a row of the ACTIVITY RAIL (0.62) and could not stay
///         there, for a sharper version of the reason it could never be a
///         segment of the old switcher: the rail exists only on the workbench
///         surfaces, because a run is a GAME and hides every piece of chrome.
///         A Play row would light itself and then vanish along with the control
///         it lives on.
///     </para>
///
///     <para>
///         HOME carries it now — a destination card with a live subtitle, on
///         the full-bleed screen the app is built around, which is the most
///         findable this route has ever been. The rail must NOT grow one back;
///         <see cref="TheActivityRailDoesNotCarryPlayMode" /> is the half that
///         rots, and <see cref="PlayModeAndHome_AreFullBleed" /> pins the
///         chrome rule that makes it true.
///     </para>
///
///     <para>
///         So the guard is deliberately about VISIBILITY, not reachability: a
///         menu item alone passes nothing here. Two routes, both pinned.
///     </para>
/// </summary>
public sealed class PlayModeReachableFromEditorTests
{
    [Fact]
    public void RememberedSong_PreparesGetReadyBeforeShowingPlayMode()
    {
        var source = Source("DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Destinations.cs");
        var start = source.IndexOf("public async Task PlaySongAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("public void OpenCommandPalette", start, StringComparison.Ordinal);
        var method = source[start..end];
        var prepare = method.IndexOf("await PlayMode.OpenSongAsync", StringComparison.Ordinal);
        var show = method.IndexOf("SelectedMainTabIndex = PlayModeTabIndex", StringComparison.Ordinal);
        Assert.True(prepare >= 0 && show > prepare, "Prepare Get ready before exposing Play Mode to prevent a library flash.");
    }

    /// <summary>
    ///     <b>Home is the visible route, and it has to be — the rail cannot be.</b>
    ///     <para>
    ///         Play Mode briefly WAS a rail row (0.62), and it could not stay:
    ///         the rail only exists on the workbench surfaces, because a run is
    ///         a game and hides every piece of chrome. A Play row would light
    ///         itself and then vanish along with the control it lives on, which
    ///         is a worse lie than the File-menu entry it replaced.
    ///     </para>
    ///     <para>
    ///         So Home carries it, as a destination card with a live subtitle —
    ///         and Home is the full-bleed screen the app is built around, which
    ///         makes this the most findable the route has ever been.
    ///     </para>
    /// </summary>
    [Fact]
    public void HomeCarriesAVisiblePlayModeDestination()
    {
        // The cards are DATA - the view renders `Destinations`, the view model
        // builds them - so the reachability lives in the view model and the
        // view only has to be the thing that draws them.
        Assert.Contains("Destinations", HomeMarkup(), StringComparison.Ordinal);

        var vm = Source("DotCharter.Presentation", "Screens", "Home", "HomeViewModel.cs");
        Assert.Contains("Home_Dest_Play", vm, StringComparison.Ordinal);
        Assert.Contains("ShellTarget.Play", vm, StringComparison.Ordinal);
        Assert.Contains("IconGuitar", vm, StringComparison.Ordinal);
    }

    /// <summary>The rail must NOT carry it — see above; this is the half that rots.</summary>
    [Fact]
    public void TheActivityRailDoesNotCarryPlayMode()
    {
        var rail = RailMarkup();

        Assert.DoesNotContain("x:Name=\"RailPlay\"", rail, StringComparison.Ordinal);
        Assert.DoesNotContain($"ConverterParameter={PlayModeTabIndex()}", rail, StringComparison.Ordinal);
    }

    /// <summary>The File-menu entry stays as the second route — belt and braces,
    /// and it is what the command palette and the keyboard reach.</summary>
    [Fact]
    public void TheFileMenuStillOffersItToo() => Assert.Contains("OpenPlayModeCommand", MenuBarMarkup(), StringComparison.Ordinal);

    /// <summary>
    ///     <b>Play Mode is full-bleed, and everything that is chrome hides
    ///     together.</b>
    ///     <para>
    ///         "When you normally play it should be like a game — no sidebar, no
    ///         top toolbar, just like a game" (the owner, 2026-09-20). A rail, a
    ///         title bar and a status rail around a Rocksmith-style highway is
    ///         what makes it read as a program instead. Home is full-bleed for
    ///         the same reason, and both go through ONE flag so a surface can
    ///         never end up with a rail and no title bar.
    ///     </para>
    /// </summary>
    [Fact]
    public void PlayModeAndHome_AreFullBleed()
    {
        var layout = Source("DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Layout.cs");

        // The rule itself is pure and pinned by `Presentation/ShellPresentationTests`;
        // this half checks the shell asks it.
        Assert.Contains("ShellPresentation.HasChrome(CurrentSurface, ShellContext, IsGameplayPresentation)",
            layout, StringComparison.Ordinal);
        Assert.Contains("public bool IsActivityRailVisible => IsShellChromeVisible;",
            layout, StringComparison.Ordinal);

        // The title bar's ROW collapses with it. Hiding a control inside a fixed
        // 38 px row leaves the band — a full-bleed screen would start 38 px down
        // with nothing above it, which is the bug this shape prevents.
        Assert.Contains("public GridLength TitleBarRowHeight =>", layout, StringComparison.Ordinal);

        var window = MainWindowSource.ReadAllText();
        var at = window.IndexOf("<shellViews:ActivityRailView", StringComparison.Ordinal);
        Assert.True(at > 0, "MainWindow no longer hosts the activity rail");
        Assert.Contains("IsVisible=\"{Binding IsActivityRailVisible}\"",
            window[at..(at + 400)], StringComparison.Ordinal);

        foreach (var bound in new[]
                 {
                     "Height=\"{Binding TitleBarRowHeight}\"",
                     "IsVisible=\"{Binding IsShellChromeVisible}\""
                 })
        {
            Assert.True(window.Contains(bound, StringComparison.Ordinal),
                $"MainWindow no longer carries `{bound}` — a piece of shell chrome has stopped " +
                "following the one flag, so some surface will show a rail without a title bar or worse");
        }

        // …including the status rail. Since #194 the rail is the ContentTemplate
        // body of StatusBarHost, and the gate is on that host's own start tag.
        var status = window.IndexOf("x:Name=\"StatusBarHost\"", StringComparison.Ordinal);
        Assert.True(status > 0, "MainWindow no longer hosts the status rail");
        Assert.Contains("IsVisible=\"{Binding IsShellChromeVisible}\"",
            window[status..window.IndexOf('>', status)], StringComparison.Ordinal);
        Assert.True(window.IndexOf("x:Name=\"StatusBarView\"", status, StringComparison.Ordinal) > status,
            "StatusBarHost no longer hosts the status rail view");
    }

    /// <summary>
    ///     <b>Removing chrome removes what was riding on it: a full-bleed
    ///     surface still has to be MOVABLE.</b>
    ///     <para>
    ///         <c>WindowDecorations="BorderOnly"</c> means the OS draws no
    ///         caption, so the title bar's strip is the only thing that moves
    ///         this window — and Play Mode, which hides it, is the DEFAULT
    ///         startup screen. Without a replacement the app opens pinned to the
    ///         desktop with nothing on screen explaining why. The headless suite
    ///         found this (the drag region measured zero); it is asserted here
    ///         because the layout version of it has to switch shell tabs, and
    ///         that switch runs an audio path that takes the testhost down.
    ///     </para>
    ///     <para>
    ///         Both directions matter: missing on a full-bleed surface pins the
    ///         window, and present on a chrome surface puts an invisible band
    ///         over the menu bar. The inverted binding is what gives both.
    ///     </para>
    /// </summary>
    [Fact]
    public void AFullBleedSurface_KeepsAWayToMoveTheWindow()
    {
        var window = MainWindowSource.ReadAllText();

        // The window CAPTION (owner, 2026-09-23: in a window, Home and Play Mode had no
        // minimise, maximise or close): the app's mark, a drag strip and the three buttons,
        // in the title bar's row. It replaced an invisible drag strip that did only the moving.
        var at = window.IndexOf("x:Name=\"WindowCaption\"", StringComparison.Ordinal);
        Assert.True(at > 0,
            "MainWindow has no `WindowCaption`. Home and Play Mode hide the title bar, and this " +
            "window draws no OS caption — without it neither surface can be moved, minimised or closed");
        var caption = window[at..window.IndexOf("</Grid>", at, StringComparison.Ordinal)];
        foreach (var piece in new[]
                 {
                     "IsVisible=\"{Binding IsWindowCaptionVisible}\"",
                     "PointerPressed=\"TitleBar_PointerPressed\"", "DoubleTapped=\"TitleBar_DoubleTapped\"",
                     "Click=\"MinimizeWindow\"", "Click=\"MaximizeWindow\"", "Click=\"CloseWindow\"",
                     "app-icon.png"
                 })
        {
            Assert.Contains(piece, caption, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("FullBleedDragArea", window, StringComparison.Ordinal);

        // Exactly when neither the title bar nor the back bar is there, and not in fullscreen.
        var layout = Source("DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Layout.cs");
        Assert.Contains("ShellPresentation.ShowsWindowCaption(IsShellChromeVisible, IsBackBarVisible, IsWindowFullScreen)",
            layout, StringComparison.Ordinal);
        Assert.Contains("ShellPresentation.TopRowHeight(IsShellChromeVisible, IsBackBarVisible, IsWindowFullScreen)",
            layout, StringComparison.Ordinal);

        // …and the back bar is the title bar's stand-in: it moves the window, and
        // it carries Back and the window buttons a full-bleed Library has no other way to reach.
        var bar = window.IndexOf("x:Name=\"BackBar\"", StringComparison.Ordinal);
        Assert.True(bar > 0, "MainWindow has no `BackBar` — a player's full-bleed Library or Settings has no way out");
        var barMarkup = window[bar..window.IndexOf("</Grid>", bar, StringComparison.Ordinal)];
        foreach (var piece in new[]
                 {
                     "IsVisible=\"{Binding IsBackBarVisible}\"", "Command=\"{Binding GoBackCommand}\"",
                     "PointerPressed=\"TitleBar_PointerPressed\"", "Click=\"CloseWindow\"", "Click=\"MinimizeWindow\""
                 })
        {
            Assert.Contains(piece, barMarkup, StringComparison.Ordinal);
        }

        // Fullscreen reaches the view model, and never writes the bound row directly: a local
        // value replaces the binding, which pinned the title bar over full-bleed screens.
        var chrome = Source("DotCharter.UI", "Views", "MainWindow.WindowChrome.cs");
        Assert.Contains("vm.IsWindowFullScreen = WindowState == WindowState.FullScreen;", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("titleRow.IsVisible", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("RowDefinitions[0].Height", chrome, StringComparison.Ordinal);
    }

    private static string PlayModeTabIndex()
    {
        var src = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs")));
        var m = System.Text.RegularExpressions.Regex.Match(src, @"const int PlayModeTabIndex = (?<i>\d+);");
        Assert.True(m.Success, "MainWindowViewModel no longer declares PlayModeTabIndex");
        return m.Groups["i"].Value;
    }

    private static string RailMarkup() =>
        Source("DotCharter.UI", "Views", "Shell", "ActivityRailView.axaml");

    private static string HomeMarkup() =>
        Source("DotCharter.UI", "Views", "Home", "HomeView.axaml");

    private static string Source(params string[] parts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"{parts[^1]} moved — this guard is blind");
        return UiSourceTree.ReadAllText(path);
    }

    /// <summary>The bar and every menu file (#653) — <see cref="MainMenuSource" />
    /// throws rather than hand back a bar with no menus in it.</summary>
    private static string MenuBarMarkup() => MainMenuSource.ReadAllText();
}
