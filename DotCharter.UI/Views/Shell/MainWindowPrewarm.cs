// The shell's deferred view hosts (Play Mode, song browser, settings,
// stacked tracks) and every startup prewarm pass that realizes hidden
// surfaces, menu dropdowns and rail popovers off the first-switch path.
//
// Extracted from the MainWindow.Prewarm partial: it shared NO private field
// with any sibling partial, so it was a collaborator in hiding rather than a
// page break. Owned by MainWindow, constructed in its ctor.

using System.Globalization;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.CommandPalette;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.PlayMode;
using DotCharter.UI.Views.Settings;

namespace DotCharter.UI.Views.Shell;

/// <summary>
/// Realize the visual trees for the settings shell and SongBrowser at app
/// startup so the very first switch into either of those top-level
/// views is instant. The hosts start with IsVisible=False (so the
/// compositor skips them); this pass flips them visible at Opacity=0,
/// forces template realization + first layout, then hands IsVisible
/// over to the view-model bindings so hidden views cost nothing per
/// frame afterwards. See <c>MainWindowLayoutController</c> for the
/// layout-row sizing rules.
/// </summary>
/// <param name="window">The window whose hosts this fills and prewarms.</param>
/// <param name="services">The composition root the Settings and Home surfaces build
/// their screen hosts from; read when those hosts are first built.</param>
/// <param name="logger">The app logger, resolved when a prewarm pass reports.</param>
internal sealed class MainWindowPrewarm(
    MainWindow window,
    Func<IServiceProvider?> services,
    Func<IAppLogger?> logger)
{
    private readonly MainWindow _window = window;
    private readonly Func<IServiceProvider?> _services = services;
    private readonly Func<IAppLogger?> _logger = logger;

    // The SongBrowser / Settings subtrees are deliberately NOT in the XAML —
    // constructing them inside InitializeComponent was a measurable chunk of
    // the startup "window constructed" phase. Built once here, right before
    // the prewarm needs them (still off the user's critical path because the
    // prewarm itself runs Background-priority after the first frame).
    private bool _libraryViewCreated;

    /// <summary>Both prewarm exit paths call <c>SchedulePrewarmOfHiddenSurfaces</c>;
    /// only the first one may queue the drain.</summary>
    private bool _hiddenSurfacePrewarmScheduled;
    private bool _libraryShown;

    private readonly TaskCompletionSource _hiddenSurfacesDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Completes when the last drain (the hidden surfaces) has run, or at once
    ///     when the prewarm is switched off. The shell-switch probe waits on it, so a
    ///     "first entry" it times is the user's first entry and not a collision with
    ///     a drain step still in the queue.
    /// </summary>
    internal Task HiddenSurfacesDrained => _hiddenSurfacesDrained.Task;

    /// <summary>
    /// Builds the stacked-timelines composite on the first
    /// IsStackedTracksVisible=true (the pane is off by default and its
    /// eager construction was a measurable slice of window construction).
    /// The host's IsVisible binding handles show/hide from then on.
    /// </summary>
    internal void EnsureStackedTracksCreated(MainWindowViewModel vm)
    {
        if (!vm.IsStackedTracksVisible) return;
        _window.StackedTracksHost.Content ??= new StackedTracksView { DataContext = vm };
    }

    private void EnsureTopLevelViewsCreated(MainWindowViewModel vm)
    {
        EnsureHomeCreated();
        EnsureLibraryCreated(vm);
        EnsureSettingsCreated();
    }

    // One per host, so a switch builds only the view it shows (building all
    // three on the first Home show was ~105 ms of it) and the drain can pace them.
    private void EnsureLibraryCreated(MainWindowViewModel vm)
    {
        if (_libraryViewCreated) return;
        _libraryViewCreated = true;
        _window.FindControl<Grid>("SongBrowserHost")?.Children.Add(
            new DotCharter.UI.Views.Library.LibraryView { DataContext = new LibraryScreen(vm.SongBrowser) });
    }

    // The Settings tab is the first surface on the target architecture:
    // a ScreenHost showing the settings shell screen (SettingsSurface).
    private void EnsureSettingsCreated()
    {
        if (_window.FindControl<ContentControl>("SettingsHost") is not { Content: null } settingsHost) return;
        if (_services() is { } services) settingsHost.Content = SettingsSurface.Create(services);
    }

    // Home is a screen on the target architecture too, so it is its own
    // ScreenHost over the catalog rather than a ViewLocator lookup.
    private void EnsureHomeCreated()
    {
        if (_window.FindControl<ContentControl>("HomeHost") is not { Content: null } homeHost) return;
        if (_services() is { } services) homeHost.Content = Views.Home.HomeSurface.Create(services);
    }

    /// <summary>
    /// Fills the deferred Play Mode host (same pattern as
    /// <see cref="EnsureStackedTracksCreated"/>): the ~1000-line multi-screen
    /// game shell does not inflate inside InitializeComponent. Called from
    /// DataContextChanged when Play Mode is the boot surface, from the
    /// IsPlayModeVisible flip, and from the Background-priority prewarm so
    /// the first switch stays instant. The view wires itself through its own
    /// DataContextChanged, so setting the context at construction is enough.
    /// </summary>
    internal void EnsurePlayModeViewCreated(MainWindowViewModel vm) => _window.PlayModeHost.Content ??= new PlayModeView { DataContext = vm.PlayMode };

    internal void PrewarmTopLevelViews()
    {
        // Rocksmith's own amp chains and knob curves, read from the player's
        // install or its cache: a pool thread, after first paint like the rest. Only
        // in a build that reads the game install (IPackageFeatures).
        if (_services()?.GetService(typeof(DotCharter.Services.Plugins.IPackageFeatures)) is
                DotCharter.Services.Plugins.IPackageFeatures { ReadsGameInstall: true }
            && _services()?.GetService(typeof(DotCharter.Services.Plugins.IGameInstallReader)) is DotCharter.Services.Plugins.IGameInstallReader reader)
        {
            _ = DotCharter.Services.Plugins.GameInstall.StartInBackground(reader, _logger());
        }
        if (_window.DataContext is not MainWindowViewModel vm) return;

        var songBrowserHost = _window.FindControl<Grid>("SongBrowserHost");
        var settingsHost = _window.FindControl<ContentControl>("SettingsHost");
        var homeHost = _window.FindControl<ContentControl>("HomeHost");

        // Direct PropertyChanged wiring instead of string-based reflection
        // bindings (the project compiles bindings by default; these two were
        // the only reflection stragglers). The subscription is created once
        // per prewarm pass and lives as long as the window.
        //
        // It also UNPARKS: the realize pass below holds both hosts visible at
        // Opacity 0, and its revert is a second Background post. A user switch
        // that lands inside that window must take the host out of that state
        // itself — see UnparkHost.
        void BindHostVisibility()
        {
            if (vm.IsSongBrowserVisible) EnsureLibraryCreated(vm);
            if (vm.IsSettingsVisible) EnsureSettingsCreated();
            if (vm.IsHomeVisible) EnsureHomeCreated();
            songBrowserHost?.IsVisible = vm.IsSongBrowserVisible;
            settingsHost?.IsVisible = vm.IsSettingsVisible;
            homeHost?.IsVisible = vm.IsHomeVisible;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.IsSongBrowserVisible) && songBrowserHost != null)
                {
                    if (vm.IsSongBrowserVisible)
                    {
                        // What opening the Library costs, first time and every time after.
                        SurfaceShowTiming.Begin(_libraryShown ? "Library" : "Library (first show)");
                        _libraryShown = true;
                        EnsureLibraryCreated(vm);
                    }

                    UnparkHost(songBrowserHost, vm.IsSongBrowserVisible);
                }
                else if (e.PropertyName == nameof(MainWindowViewModel.IsSettingsVisible) && settingsHost != null)
                {
                    if (vm.IsSettingsVisible) EnsureSettingsCreated();
                    UnparkHost(settingsHost, vm.IsSettingsVisible);
                }
                else if (e.PropertyName == nameof(MainWindowViewModel.IsHomeVisible) && homeHost != null)
                {
                    if (vm.IsHomeVisible) EnsureHomeCreated();
                    UnparkHost(homeHost, vm.IsHomeVisible);
                }
            };
        }

        Dispatcher.UIThread.Post(() =>
        {
            // Play Mode inflates here too if nothing created it yet —
            // content only: its host stays hidden via the IsVisible binding,
            // so no layout/composition runs until the first switch, which
            // then only pays the visible screen's first arrange.
            EnsurePlayModeViewCreated(vm);

            if (vm.SelectedMainTabIndex != 0)
            {
                BindHostVisibility();
                SchedulePrewarmOfHiddenSurfaces();
                return;
            }

            // Build the deferred subtrees now — Background priority, first
            // frame already presented — then realize their templates below.
            EnsureTopLevelViewsCreated(vm);

            // Wire the hosts BEFORE parking them, not in the revert below.
            // The revert is a SECOND Background post, and Background sits
            // under the boot-time PSARC library scan's Normal-priority
            // IProgress callbacks — measured 7.9 s / 2,160 songs, cold. A user
            // who clicks LIBRARY or SETTINGS inside that window would switch
            // to a host that was visible, transparent and hit-test disabled:
            // a BLANK window, for as long as the scan kept the revert waiting.
            // With the subscription live, that switch unparks the host itself.
            BindHostVisibility();

            ParkHost(songBrowserHost);
            ParkHost(settingsHost);

            try
            {
                _window.UpdateLayout();
                PrewarmVisibleComboBoxes();
            }
            finally
            {
                // Always schedule the revert — if it never ran, the hosts
                // would stay parked at Opacity 0.
                Dispatcher.UIThread.Post(() =>
                {
                    UnparkHost(songBrowserHost, vm.IsSongBrowserVisible);
                    UnparkHost(settingsHost, vm.IsSettingsVisible);
                    _window.UpdateLayout();
                    SchedulePrewarmOfHiddenSurfaces();
                }, DispatcherPriority.Background);
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    ///     Holds a deferred host in the one state that realizes its templates
    ///     without painting a pixel: visible (so <c>MeasureCore</c> runs at all),
    ///     fully transparent and not hit-testable.
    /// </summary>
    /// <remarks>
    ///     The transparency is not cosmetic. The editor is still the active view
    ///     during this pass and its native 3D-preview HWND ignores Avalonia
    ///     z-order, so a host that really painted here would show as the
    ///     "settings bleeds into the 3D view" glitch. Every parked host must be
    ///     handed back through <see cref="UnparkHost" /> — this is the ONE place
    ///     in the prewarm that writes <c>IsVisible</c>, and it owes the binding
    ///     that property back.
    /// </remarks>
    internal static void ParkHost(Control? host)
    {
        if (host is null) return;

        host.Opacity = 0;
        host.IsHitTestVisible = false;
        host.IsVisible = true;
    }

    /// <summary>
    ///     Takes a host back out of <see cref="ParkHost" />'s state and re-applies
    ///     the view model's own answer for <paramref name="visible" />.
    /// </summary>
    /// <remarks>
    ///     <b>The opacity is restored whether the host is being shown or hidden.</b>
    ///     A host left at <c>Opacity 0</c> is not "hidden" — it is a fully laid-out
    ///     surface that paints nothing, so the NEXT switch into it shows a blank
    ///     window with no error and no log line. That is the failure this exists
    ///     to make impossible: the revert runs at <c>Background</c>, behind a
    ///     boot-time library scan that can hold the queue for seconds, and the
    ///     user can reach either host with one click long before it lands.
    /// </remarks>
    internal static void UnparkHost(Control? host, bool visible)
    {
        if (host is null) return;

        host.Opacity = 1;
        host.IsHitTestVisible = true;
        host.IsVisible = visible;
    }

    /// <summary>
    ///     Second half of the prewarm: the surfaces the pass above CANNOT reach.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The <c>UpdateLayout()</c> above realizes what is VISIBLE — the song
    ///         browser, and whichever settings tab happens to be selected. It can
    ///         do nothing for the seven other tabs, the ~19 Play Mode screens or
    ///         the ~32 closed dialogs, because Avalonia skips <c>MeasureCore</c>
    ///         for an invisible element. So each of those still charged the user
    ///         its whole first layout on their first switch into it — measured at
    ///         20-137 ms per settings tab, 1-41 ms per Play Mode screen and 5-110
    ///         ms per dialog. That is the lag in "switching views, opening dialogs,
    ///         switching tabs always lags a little".
    ///     </para>
    ///     <para>
    ///         <see cref="HiddenSubtreePrewarmer" /> realizes them without ever
    ///         touching <c>IsVisible</c>, one per <c>Background</c> dispatcher turn
    ///         so the ~1 s total never lands as a freeze and real input preempts
    ///         it. The hidden HOSTS go first: realizing <c>PlayModeHost</c> gives
    ///         the Play Mode root panel real bounds, and every screen under it is
    ///         then measured against the slot it will really occupy rather than
    ///         against the window (which leaves the cached measure wrong by the
    ///         chrome and costs a re-measure on the real show).
    ///     </para>
    /// </remarks>
    private void SchedulePrewarmOfHiddenSurfaces()
    {
        if (_hiddenSurfacePrewarmScheduled) return;
        _hiddenSurfacePrewarmScheduled = true;

        // Verification seam (same family as DOTCHARTER_TIMELINE2D_FORCE_FAIL):
        // the only honest way to A/B the prewarm is to boot the real shell twice,
        // so keep a switch that turns it off without touching the code.
        if (Environment.GetEnvironmentVariable("DOTCHARTER_NO_SURFACE_PREWARM") == "1" ||
            _window.DataContext is not MainWindowViewModel)
        {
            _hiddenSurfacesDrained.TrySetResult();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            // The SHELL's own gated surfaces first — the chart editor is the
            // largest one in the window. A Play Mode boot lands on the Start
            // screen, whose next surface is HOME, and Home's destinations are
            // Song Select, the Library and Settings — while the editor
            // chrome is not even on screen. So that boot builds and realizes
            // those ahead of it: a user who presses Start in the first seconds
            // otherwise paid Home's construction and first layout (~300 ms,
            // Release R2R) behind a drain still busy with the editor, and the
            // first Settings ~350-1,200 ms.
            IEnumerable<Control?> Chrome()
            {
                if (_window.DataContext is MainWindowViewModel { IsPlayModeVisible: true } vm)
                {
                    foreach (var part in HomeAndItsDestinations(vm))
                        yield return part;
                }

                foreach (var part in HiddenShellSurfaces().ToList())
                    yield return part;
            }

            HiddenSubtreePrewarmer.ScheduleDrain(Chrome(), () => _window.Bounds.Size, (realized, total) =>
            {
                _logger()?.Info(AppLogCategory.Performance,
                    $"Prewarm: realized {realized}/{total} shell chrome surfaces off the first-switch path.");

                // …and the dropdowns that hang off that chrome IMMEDIATELY after
                // it, ahead of every dialog, settings tab and Play Mode screen.
                // See PrewarmTheRestOfTheHiddenSurfaces for why that order is the
                // whole point.
                ScheduleMenuFlyoutPrewarm(() =>
                    ScheduleTransportFlyoutPrewarm(PrewarmTheRestOfTheHiddenSurfaces));
            }, onStep: LogLongStep);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    ///     Home and the surfaces it opens, built and realized one per drain turn —
    ///     what a Play Mode boot drains before the editor chrome.
    /// </summary>
    private IEnumerable<Control?> HomeAndItsDestinations(MainWindowViewModel vm)
    {
        EnsureHomeCreated();
        yield return null;
        if (_window.FindControl<ContentControl>("HomeHost") is { IsVisible: false } home)
            yield return home;

        // Song Select is a DEFERRED body: built in a turn of its own, then
        // its gated host is realized — the host, because the screen root inside
        // it carries no gate of its own and a visible part realizes nothing.
        if (_window.FindControl<ContentControl>("PlayModeHost")?.Content is PlayModeView playMode)
        {
            playMode.BuildDeferredScreenBody(PlayModeView.SongSelectHostName);
            yield return null;
            if (playMode.FindControl<ContentControl>(PlayModeView.SongSelectHostName) is { IsVisible: false } songSelect)
                yield return songSelect;
        }

        EnsureLibraryCreated(vm);
        yield return null;
        if (_window.FindControl<Grid>("SongBrowserHost") is { IsVisible: false } library)
            yield return library;

        EnsureSettingsCreated();
        yield return null;
        if (_window.FindControl<ContentControl>("SettingsHost") is { IsVisible: false } settings)
            yield return settings;
    }

    /// <summary>
    ///     Everything the shell's own chrome is not: the three deferred hosts and
    ///     every <see cref="IPrewarmableSurfaces" /> under them — ~38 dialogs, the
    ///     eight settings tabs, the sixteen Play Mode screens, the song browser.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>It runs AFTER the menu bar's dropdowns and the two rails'
    ///         popovers, and the order is the fix.</b> The drain pays one surface
    ///         per <c>Background</c> dispatcher turn, and <c>Background</c> is
    ///         below <c>Normal</c> — so the PSARC library scan
    ///         <c>PlayModeViewModel</c> kicks at boot, whose <c>IProgress</c>
    ///         callbacks marshal every found batch onto the UI thread at
    ///         <c>Normal</c>, sits in front of every step of it for as long as it
    ///         runs (measured 7.9 s / 2,160 songs, cold). With the dropdowns drained
    ///         LAST they were behind ~70 surfaces of that queue, so a user who
    ///         clicked Tools or Mix in the first seconds of a session still paid
    ///         the whole cold open — 46-175 ms per popover, 8-88 ms per menu,
    ///         measured headless (Debug, <c>r2r=no</c>). Nothing about the earlier
    ///         mechanisms was broken; they simply had not arrived yet.
    ///     </para>
    ///     <para>
    ///         The chrome dropdowns are also the only surfaces in this whole list
    ///         that are ONE CLICK from the window the instant it appears. A dialog
    ///         cannot be reached before the user has navigated to whatever opens
    ///         it, and a settings tab needs Settings first.
    ///     </para>
    ///     <para>
    ///         Listing these is not free either — <c>DialogContainerView
    ///         .HiddenSurfaces()</c> MATERIALIZES every deferred dialog view — so
    ///         the listing happens here too rather than up front.
    ///     </para>
    /// </remarks>
    private void PrewarmTheRestOfTheHiddenSurfaces()
    {
        if (_window.DataContext is not MainWindowViewModel vm) return;

        // LAZY: the list is produced inside the drain, one paced turn per
        // item, because producing it is itself the expensive part — building the
        // three top-level views, Play Mode's deferred bodies and the rig view, and
        // 34 dialog views. Listed up front, that was ONE 0.7-1.9 s turn (Release
        // R2R), and a click inside it waited for all of it.
        HiddenSubtreePrewarmer.ScheduleDrain(TheRestOfTheHiddenSurfaces(vm, () => PrewarmableSurfaceOwners().ToList()),
            () => _window.Bounds.Size, (realized, total) =>
            {
                _logger()?.Info(AppLogCategory.Performance,
                    $"Prewarm: realized {realized}/{total} hidden surfaces off the first-switch path.");
                _hiddenSurfacesDrained.TrySetResult();
            }, ex => _logger()?.Warning(AppLogCategory.Performance,
                $"Prewarm: the hidden-surface listing failed and the drain stopped early: {ex.Message}"), LogLongStep);
    }

    /// <summary>The drain turns at or over this are logged: a click that lands
    /// during one waits for all of it.</summary>
    private const double LongStepMs = 50;

    private void LogLongStep(HiddenSubtreePrewarmer.DrainStep step)
    {
        if (step.Ms < LongStepMs) return;
        _logger()?.Info(AppLogCategory.Performance, string.Create(CultureInfo.InvariantCulture,
            $"Prewarm: long step {step.Kind} {step.PartName} {step.Ms:F0} ms."));
    }

    /// <summary>
    ///     What <see cref="PrewarmTheRestOfTheHiddenSurfaces" /> drains, produced
    ///     lazily: every <c>yield</c> is one dispatcher turn, and a <c>null</c> is
    ///     a turn that only BUILT something (<see cref="IPrewarmConstruction" />).
    /// </summary>
    /// <remarks>
    ///     The owners are listed in ONE go, once the hosts above have been built and
    ///     realized (so the screen hosts inside them are found too), never walked
    ///     lazily: the drain realizes parts between two yields, and a visual-tree
    ///     walk suspended across that would be enumerating children that change
    ///     under it.
    /// </remarks>
    private IEnumerable<Control?> TheRestOfTheHiddenSurfaces(
        MainWindowViewModel vm, Func<IReadOnlyList<IPrewarmableSurfaces>> listOwners)
    {
        // The deferred hosts must be FILLED before their surfaces can be listed.
        // Booting into any tab but the editor takes the early exit in
        // SchedulePrewarmOfHiddenSurfaces's callers, where SongBrowserHost and
        // SettingsHost are still empty — and an empty host has no surfaces, so
        // the whole settings page silently prewarmed nothing and the first
        // Settings switch still cost ~650 ms. Home first: it is the Start
        // screen's next surface.
        foreach (var create in new Action[]
                 {
                     EnsureHomeCreated, () => EnsureLibraryCreated(vm), EnsureSettingsCreated,
                     () => EnsurePlayModeViewCreated(vm)
                 })
        {
            create();
            yield return null;
        }

        // Hosts first — they establish the slots below them.
        foreach (var host in new Control?[]
                 {
                     _window.FindControl<ContentControl>("HomeHost"),
                     _window.FindControl<ContentControl>("PlayModeHost"),
                     _window.FindControl<Grid>("SongBrowserHost"),
                     _window.FindControl<ContentControl>("SettingsHost")
                 })
        {
            if (host is { IsVisible: false })
                yield return host;
        }

        foreach (var owner in listOwners())
        {
            if (owner is IPrewarmConstruction staged)
            {
                foreach (var build in staged.ConstructionSteps())
                {
                    Try(owner, build);
                    yield return null;
                }
            }

            IReadOnlyList<Control> listed = [];
            Try(owner, () => listed = owner.HiddenSurfaces());
            yield return null;
            foreach (var part in listed)
                yield return part;
        }

        void Try(IPrewarmableSurfaces owner, Action work)
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                _logger()?.Warning(AppLogCategory.Performance,
                    $"Prewarm: {owner.GetType().Name} could not list its hidden surfaces: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Second part of the prewarm: the MENU BAR's dropdowns.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Realizing <c>TitleBarRow</c> (see <see cref="HiddenShellSurfaces" />)
    ///         lays out the BAR — eight headers in a row. Every row UNDER a header
    ///         lives in a closed <c>Popup</c>, i.e. outside the visual tree, so it
    ///         is untouched by that pass and by every other prewarm in the shell:
    ///         the first click on a menu paid its whole realization, measured at
    ///         33-337 ms per menu against 2-8 ms for every open after it.
    ///     </para>
    ///     <para>
    ///         <see cref="MenuFlyoutPrewarmer" /> pays it here instead, one menu
    ///         per <c>Background</c> turn and without opening anything. It runs
    ///         inside <see cref="SchedulePrewarmOfHiddenSurfaces" />'s drain
    ///         callback, so <c>DOTCHARTER_NO_SURFACE_PREWARM=1</c> still A/Bs the
    ///         whole mechanism.
    ///     </para>
    /// </remarks>
    private void ScheduleMenuFlyoutPrewarm(Action onDone)
    {
        if (DeferredChrome<MainMenuView>(_window.WindowChrome.MainMenuBarHost) is not { } menu)
        {
            onDone();
            return;
        }

        MenuFlyoutPrewarmer.ScheduleDrain(menu.TopLevelMenus(), () => _window.Bounds.Size, (rows, menus) =>
        {
            _logger()?.Info(AppLogCategory.Performance,
                $"Prewarm: realized {rows} menu rows across {menus} dropdowns off the first-open path.");

            onDone();
        });
    }

    /// <summary>
    ///     Third part of the prewarm: the TRANSPORT bar's and STATUS rail's
    ///     popovers — Mix, Tempo, Tools, Tracks, the two history previews, the two
    ///     message panels and the profile switcher — and the timeline's lane menu.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Same fault as the menu bar's dropdowns and reached by neither of the
    ///         other passes: a <c>Button.Flyout</c> has no <c>Popup</c> and no
    ///         <c>FlyoutPresenter</c> at all until it is first shown, so its content
    ///         has no parent, no style scope and therefore no templates — the whole
    ///         panel is realized on the click. That is the owner's "the dropdowns
    ///         still have a slight lag on first open" after 0.52 fixed the menus.
    ///     </para>
    ///     <para>
    ///         <see cref="PrewarmableFlyout" /> is what makes it reachable without
    ///         opening anything; the list is DERIVED from the two rails' markup, so
    ///         a popover added to either is prewarmed the day it compiles. This
    ///         pass buys the FIRST open; every later open is bought back by
    ///         <see cref="PrewarmableFlyout" />'s close, which re-parents the popup
    /// into its target's style scope before re-realizing it.
    ///     </para>
    /// </remarks>
    private void ScheduleTransportFlyoutPrewarm(Action onDone)
    {
        var popovers = new List<MenuFlyoutPrewarmer.FlyoutTarget>();

        foreach (var rail in new Control?[]
                 {
                     DeferredChrome<ToolbarView>(_window.FindControl<ContentControl>("EditorToolbarHost")),
                     DeferredChrome<StatusBar>(_window.FindControl<ContentControl>("StatusBarHost")),
                     // The timeline's right-click lane menu.
                     _window.FindControl<Border>("TimelineGutterHost")
                 })
        {
            if (rail is not null)
                popovers.AddRange(MenuFlyoutPrewarmer.FlyoutsUnder(rail));
        }

        MenuFlyoutPrewarmer.ScheduleDrain(popovers, () => _window.Bounds.Size, (realized, total) =>
        {
            _logger()?.Info(AppLogCategory.Performance,
                $"Prewarm: realized {realized}/{total} transport popovers off the first-open path.");

            onDone();
        });
    }

    /// <summary>
    ///     The shell's OWN <c>IsVisible</c>-gated surfaces — the ones declared
    ///     directly in <c>MainWindow.axaml</c>, which therefore have no view to
    ///     hang <see cref="IPrewarmableSurfaces" /> on (that interface only
    ///     reaches the CONTENT of the three deferred hosts).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The chart editor is the one that matters.</b> With
    ///         <c>AppConfig.StartInPlayMode</c> — the default — the whole editor
    ///         subtree (toolbar, ribbon header, timeline, minimap, sheet split,
    ///         bottom dock) is <c>IsVisible=false</c> for the entire boot, so
    ///         Avalonia never runs <c>MeasureCore</c> on it and the user's first
    ///         switch into the editor pays every style, template, resource lookup
    ///         and text layout in it at once. Measured in the real shell at
    ///         130 ms, against a ≤150 ms budget and a &lt;20 ms target — and it is
    ///         exactly the "time switching to chart editor on first start up" the
    ///         owner reports. The second switch costs 1.2 ms, which is the proof
    ///         that the cost is realization and that paying it once, early and
    ///         off the critical path, removes it for good.
    ///     </para>
    ///     <para>
    ///         The editor chrome is listed separately because it is NOT under
    ///         <c>MainSplitGrid</c>: <c>MainWindowShellLayout.ApplyShellChrome</c>
    ///         hides the side rail, menu bar and status bar for the game shell, and
    ///         the toolbar has its own <c>IsToolbarVisible</c> binding.
    ///     </para>
    /// </remarks>
    private IEnumerable<Control> HiddenShellSurfaces()
    {
        // Editor root first — largest surface, most likely first switch.
        if (_window.FindControl<Grid>("MainSplitGrid") is { IsVisible: false } editor)
            yield return editor;
        // …then its own gated CHILDREN, which realizing the editor does not
        // reach: HiddenSubtreePrewarmer deliberately stops at the first hidden
        // root and never recurses. The Sheet Music dock is one View-menu toggle
        // away and hosts a whole DockControl + the AlphaTab notation strip.
        // Parent before child, or the child measures against a slot that has
        // never been arranged and re-measures on the real show.
        if (_window.FindControl<Border>("SheetMusicDock") is { IsVisible: false } sheet)
            yield return sheet;
        // The AI chat column: its pane stays in the side dock while the column is
        // hidden, so realizing the column here makes the toolbar toggle a flip, not a build.
        if (_window.FindControl<Panel>("AiChatDockHost") is { IsVisible: false } chat)
            yield return chat;
        // The editor chrome. Each rail is the ContentTemplate body of a
        // host that carries its gate, so these are the HOSTS: realizing one runs
        // the layout pass that builds its body, after first paint. The menu bar's
        // gate is the whole title bar (IsShellChromeVisible), so that row is the
        // entry — it must be listed or the bar's own layout lands on
        // the first switch into the workbench. The row is WindowChromeView's,
        // in that view's namescope, so it is reached through the view.
        if (_window.FindControl<ContentControl>("EditorToolbarHost") is { IsVisible: false } toolbar)
            yield return toolbar;
        if (_window.WindowChrome.TitleBarRow is { IsVisible: false } titleBar)
            yield return titleBar;
        if (_window.FindControl<ContentControl>("StatusBarHost") is { IsVisible: false } status)
            yield return status;
        // The chart editor's Amp & FX rig overlay. It is a SECOND
        // AmpFxRigView instance — Play Mode's own copy is prewarmed through
        // PlayModeView.HiddenSurfaces(), and prewarming one view does nothing
        // for another instance of it: the cost is the first LAYOUT of that
        // subtree, not the type's first use. This overlay gets its own entry
        // for that reason.
        // Its screen is GATED off the boot path (DeferredRigScreen) — the
        // DataContext binding answers null until something wants it — so ask
        // for it here, after the first frame, and the overlay is prewarmed over
        // a real screen rather than over null.
        if (_window.FindControl<Panel>("PedalboardOverlay") is { IsVisible: false } rig)
        {
            (_window.DataContext as MainWindowViewModel)?.ToneRig.EnsureRigScreen();
            // The rig VIEW is a ContentTemplate body that no layout pass has
            // reached yet: build it now, or the drawer names below resolve to
            // nothing and drop out of the drain without a word.
            HiddenSubtreePrewarmer.BuildDeferredBody(_window.ShellRigScreenHost);
            yield return rig;
            // …and the tone drawer inside it, a second hidden root the overlay's
            // own prewarm stops short of (the same entry PlayModeView lists in
            // NestedScreenBodyNames; listed AFTER its gated parent, as the rule
            // requires). Sixty preset cards, laid out on the first click otherwise.
            // The Tones VIEW first (its template puts the drawer in the visual
            // tree), then the drawer, then the designer's column.
            foreach (var drawer in HiddenSubtreePrewarmer.HiddenNamedDescendants(rig,
                         ["RigTonesMode", "RigToneBrowser", "RigToneDesignPanel"]))
            {
                yield return drawer;
            }
        }
        // The command palette. Unlike every other entry here the GATE is not on
        // the element this window can name — CommandPaletteView is always
        // visible and its own root `Grid x:Name="Scrim"` carries the
        // IsVisible="{Binding IsOpen}", in a namescope FindControl cannot cross.
        // So name the view and take its hidden CONTENT, the way the dialog
        // container does. Without a name neither
        // mechanism can reach it: the first Ctrl+Shift+P would realize a
        // ListBox-backed overlay from cold.
        if (_window.FindControl<CommandPaletteView>("CommandPaletteOverlay") is { } palette)
        {
            foreach (var scrim in HiddenSubtreePrewarmer.HiddenContentOf(palette))
                yield return scrim;
        }
        // (There is no entry for a 54 px icon rail — its navigation
        // is the menu bar's CHART / LIBRARY / SETTINGS switcher, which realizes
        // with MainMenuBar.)
    }

    /// <summary>
    ///     A shell rail that is the <c>ContentTemplate</c> body of
    /// <paramref name="host" /> — the menu bar (its host is in
    ///     <see cref="WindowChromeView" />), the editor toolbar, the status rail. Builds the
    ///     body first if no layout pass has reached the host yet (construction
    ///     only: <see cref="HiddenSubtreePrewarmer.BuildDeferredBody" />), so a
    ///     caller that walks the rail's logical tree finds it on a full-bleed boot
    ///     too. Null when the host is missing or its body is not a
    ///     <typeparamref name="T" />.
    /// </summary>
    private static T? DeferredChrome<T>(ContentControl? host) where T : Control
    {
        HiddenSubtreePrewarmer.BuildDeferredBody(host);
        return host?.Presenter?.Child as T;
    }

    /// <summary>
    ///     The views that own <c>IsVisible</c>-gated sub-surfaces, in the order
    ///     the user is most likely to reach them. Resolved through the deferred
    ///     hosts rather than a visual-tree scan: a host whose content has never
    ///     been measured has no content presenter child to find.
    /// </summary>
    private IEnumerable<IPrewarmableSurfaces> PrewarmableSurfaceOwners()
    {
        // Play Mode's screens and the Library are one click from Home; a dialog
        // needs the editor and a menu first.
        if (_window.FindControl<ContentControl>("PlayModeHost")?.Content is IPrewarmableSurfaces playMode)
            yield return playMode;
        if (_window.FindControl<Grid>("SongBrowserHost")?.Children.OfType<IPrewarmableSurfaces>()
                .FirstOrDefault() is { } browser)
        {
            yield return browser;
        }

        if (_window.FindControl<DialogContainerView>("DialogContainer") is { } dialogs)
            yield return dialogs;

        // The AI chart pass review card over the timeline: hidden until a pass
        // holds proposals, then shown with no gesture of the user's to pay for it.
        if (_window.FindControl<AiChartReviewView>("AiChartReviewOverlay") is { } aiReview)
            yield return aiReview;

        // …and Chart from audio's merge card beside it, hidden until a draft is held.
        if (_window.FindControl<DraftMergeReviewView>("DraftMergeOverlay") is { } draftMerge)
            yield return draftMerge;

        // The inspector is a DOCK pane — the app ViewLocator materializes it
        // inside BottomDock, so it is named in no markup this window can
        // FindControl and HiddenShellSurfaces() cannot list it. A visual-tree
        // walk of the dock is the only handle there is; it finds nothing while
        // the pane is closed, which is correct (the view does not exist yet and
        // is built by the click that opens it).
        var inspector = _window.FindControl<DockControl>("BottomDock")
            ?.GetVisualDescendants().OfType<InspectorView>().FirstOrDefault();
        if (inspector is not null)
        {
            yield return inspector;
            // …and the Sound tab's own cards, AFTER the tab itself: the
            // prewarmer stops at the first hidden root on purpose, and a card
            // measured before its tab has been arranged measures against a
            // fallback slot and re-measures on the real show.
            if (inspector.SoundTab is { } sound) yield return sound;
        }

        // The screen and dialog hosts of the UI rewrite (target-architecture.md
        // § 3) can sit anywhere in the tree, so they are found by type; each
        // lists only its own hidden children. The Settings tab's host is read
        // through its ContentControl instead: a deferred host that has never
        // been measured has no visual children to find, and it must come first
        // because the two owners nested INSIDE it — the tab host and the tab views with
        // hidden sections of their own — measure against a
        // fallback slot unless the shell around them is arranged first.
        var settingsScreens = _window.FindControl<ContentControl>("SettingsHost")?.Content as ScreenHost;
        var otherScreenHosts = _window.GetVisualDescendants().OfType<ScreenHost>()
            .Where(host => !ReferenceEquals(host, settingsScreens));
        foreach (var screenHost in settingsScreens is null ? otherScreenHosts : otherScreenHosts.Prepend(settingsScreens))
            yield return screenHost;
        foreach (var tabs in settingsScreens?.GetLogicalDescendants().OfType<ScreenTabHost>() ?? [])
            yield return tabs;
        foreach (var general in settingsScreens?.GetLogicalDescendants().OfType<GeneralSettingsView>() ?? [])
            yield return general;
        foreach (var graphics in settingsScreens?.GetLogicalDescendants().OfType<GraphicsSettingsView>() ?? [])
            yield return graphics;
        foreach (var audio in settingsScreens?.GetLogicalDescendants().OfType<AudioSettingsView>() ?? [])
            yield return audio;
        foreach (var keybinds in settingsScreens?.GetLogicalDescendants().OfType<KeybindsSettingsView>() ?? [])
            yield return keybinds;
        foreach (var dialogHost in _window.GetVisualDescendants().OfType<DialogHost>())
            yield return dialogHost;
    }

    private void PrewarmVisibleComboBoxes()
    {
        // Apply ComboBox templates while the startup prewarm is already doing
        // background layout work. We intentionally do not open popups here:
        // that can flash in the desktop app and can deadlock Avalonia.Headless.
        foreach (var combo in _window.GetVisualDescendants().OfType<ComboBox>())
        {
            combo.ApplyTemplate();
        }
    }
}
