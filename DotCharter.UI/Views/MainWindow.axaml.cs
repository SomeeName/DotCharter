// MainWindow code-behind: construction, view-model wiring/unwiring, file
// drops and the close path. The rest of the shell lives in partials beside
// this file (.Input, .WindowChrome, .Docking) and in the four collaborators it
// owns under Views/Shell/ — MainWindowPrewarm, MainWindowShellLayout,
// MainWindowCapture and MainWindowDiagnostics.

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DotCharter.UI.Controls;
using DotCharter.UI.Input;
using DotCharter.UI.Layout;
using DotCharter.UI.Views.Shell;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Session;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Views;

public partial class MainWindow : Window
{
    private Action? _chartDataChangedHandler;
    private ChartEditorViewModel? _chartDataChangedSource;
    private PropertyChangedEventHandler? _chartEditorPropertyChangedHandler;
    private ChartEditorViewModel? _chartEditorPropertyChangedSource;
    private Action? _historyChangedHandler;
    private IHistoryService? _historyChangedSource;

    // The three SHELL-level handlers DataContextChanged also registers. They
    // must not be untracked lambdas while only the render-invalidation trio is
    // detached — see DetachViewModelHandlers.
    private MainWindowViewModel? _shellHandlerSource;
    private System.ComponentModel.PropertyChangedEventHandler? _shellPropertyChangedHandler;
    private System.ComponentModel.PropertyChangingEventHandler? _shellPropertyChangingHandler;
    private Action? _layoutResetHandler;
    private Action? _timelinePopOutHandler;
    private TimelineInteractionManager? _interactionManager;

    private readonly MainWindowLayoutController _layout;
    private readonly MainWindowPrewarm _prewarm;
    private readonly MainWindowShellLayout _shellLayout;
    private readonly GameplayDockSnapshot _gameplayDock = new();
    private readonly MainWindowCapture _capture;
    private readonly MainWindowDiagnostics _diagnostics;
    private readonly MinimapDragController _minimap = new();
    private TimelineWheelAdjuster? _wheelAdjuster;
    private DispatcherTimer? _focusGuard;

    /// <summary>The startup prewarm's last drain has run (see <see cref="MainWindowPrewarm.HiddenSurfacesDrained" />).</summary>
    internal Task HiddenSurfacesDrained => _prewarm.HiddenSurfacesDrained;

    public MainWindow()
    {
        InitializeComponent();
        // The window itself must be able to hold keyboard focus: when a
        // clicked control leaves the visual tree (Play Mode screen switches
        // do this constantly) Avalonia is left with NO focused element and
        // silently drops ALL key input — the historic "Esc does nothing" bug.
        Focusable = true;
        _layout = new MainWindowLayoutController(this);
        _prewarm = new MainWindowPrewarm(this, CompositionRoot, ResolveService<IAppLogger>);
        _shellLayout = new MainWindowShellLayout(this);
        _capture = new MainWindowCapture(this);
        _diagnostics = new MainWindowDiagnostics(this,
            ResolveService<IAppLogger>,
            ResolveService<IProjectSessionService>,
            ResolveService<IAudioPlaybackService>,
            ResolveService<IHighwayEditingService>,
            ResolveService<IHistoryService>,
            ResolveService<IProjectDocumentService>,
            ResolveService<ProjectState>);

        // Global UI scale (accessibility): apply the current value and track
        // live changes from Settings as a LayoutTransform on the shell root.
        // 1.0 leaves the transform null so it's a true no-op.
        ApplyUiScale(Services.UiScaleController.Scale);
        Services.UiScaleController.ScaleChanged += ApplyUiScale;

        // …and the AUTO arm, whose one input is this window's client height.
        // The shell is the only thing that knows it, so the controller asks and
        // this answers: once at first layout, then on every size delivery (the
        // quantisation + hysteresis in AutoUiScale is what keeps a drag-resize
        // from being a re-layout per delivered size), and once more whenever the
        // mode is switched ON — turning Auto on cannot derive a factor by itself.
        Services.UiScaleController.AutoModeChanged += OnAutoUiScaleModeChanged;

        // …and the SHAPE half, which the scale provably cannot do: a uniform
        // transform leaves every "content px / window px" ratio invariant, so
        // a wide monitor needs different LAYOUT, not bigger layout. The
        // controller puts `Compact`/`Wide` on this window's Classes and swaps
        // the Fire ladder's layout constants, keyed on the INNER (post-
        // RootScaler) width and unhooked on Closed. Nothing else here changes:
        // ClientSize is zero in a constructor, which reads as the design shape.
        Services.UiBreakpointController.Attach(this);

        // Catch-all keyboard-focus anchor: whenever a click ends with NO
        // focused element (the clicked control vanished — screen switches,
        // dialog closes — or the target wasn't focusable), give focus to the
        // window itself so key input (Esc!) keeps flowing. Runs after the
        // bubble phase so it never steals focus from a control that took it.
        AddHandler(PointerReleasedEvent, (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                AnchorFocusIfLost();
            });
        }, RoutingStrategies.Tunnel, true);

        // Last line of defense: with NO focused element Avalonia drops every
        // key press before it even routes (the historic dead-Esc). Whatever
        // exotic path clears focus — control removal without a click, popup
        // teardown, native interop — this guard re-anchors within 250 ms.
        // Cost: one null check 4×/s.
        _focusGuard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _focusGuard.Tick += (_, _) =>
        {
            if (IsActive) AnchorFocusIfLost();
        };
        _focusGuard.Start();

        // 1. Auto-focus the timeline immediately when the app finishes loading
        // The AUTO interface scale's one input. RestoreOnLoad below re-applies
        // the saved window size, so the FIRST honest client height is only
        // available from Loaded — and every later one arrives here. Both go
        // through the same guarded entry point (see ReapplyAutoUiScale).
        SizeChanged += (_, _) => ReapplyAutoUiScale();

        // 1. Auto-focus the timeline immediately when the app finishes loading
        Loaded += (_, _) =>
        {
            var timelineGrid = this.FindControl<Grid>("TimelineGrid");
            timelineGrid?.Focus();
            // The timeline grid is inside the (possibly hidden) editor tab —
            // when the app boots into Play Mode that Focus() fails and the
            // whole window starts keyboard-deaf. Anchor on the window then.
            Dispatcher.UIThread.Post(() =>
            {
                AnchorFocusIfLost();
            });
            // BEFORE RestoreOnLoad, which derives the dock split's star values
            // from UiScaleController.Scale (DockRowStars: the saved dock height
            // is in the RootScaler's INNER pixels and the window height is
            // OUTER DIPs). Deriving the auto scale after it would restore every
            // Auto user's split against the wrong scale on the first launch —
            // the exact fault that comment was written for.
            ReapplyAutoUiScale();
            _layout.RestoreOnLoad();
            if (DataContext is MainWindowViewModel vm)
            {
                RestoreDockLayout(vm);
                _shellLayout.WireGameplayLayout(vm);
                // Pause backdrop: the pause menu is another tab, so the window
                // captures itself (PrintWindow sees the composed 3D surface).
                vm.PlayMode.PauseBackdropProvider = _capture.CaptureSelfBitmap;
                // v0.32 one-key screenshot: same PrintWindow machinery,
                // scoped to the 3D highway slot.
                vm.Export.HighwayScreenshotProvider = _capture.CaptureHighwaySlotBitmap;
            }
            _prewarm.PrewarmTopLevelViews();
            _diagnostics.ArmUiAutopilot();
            _diagnostics.LogPopupHostTransparency();
        };

        // Window-level Ctrl+Space — routed via the tunneling pre-event so it
        // fires no matter which control currently has focus (the timeline,
        // the lyric textbox, a button, etc.). Without this, pressing Apply
        // in the Lyrics inspector or clicking Play steals focus and Ctrl+Space
        // never reaches Timeline_KeyDown.
        AddHandler(KeyDownEvent, GlobalKeyDownPreview, RoutingStrategies.Tunnel, true);
        // Paired KeyUp swallow: a key the tunnel consumed as a shortcut must not
        // ALSO activate a focused Button/ToggleButton on its release. Same tunnel
        // + handledEventsToo so it beats the focused control's own KeyUp handler.
        AddHandler(KeyUpEvent, GlobalKeyUpPreview, RoutingStrategies.Tunnel, true);
        // Input timestamps for the prewarm pacer: a drain step only runs after a
        // quiet window, so realizing a hidden surface never lands under a drag
        // or a keystroke in the first seconds of a session (PrewarmPacer).
        // Tunnel + handledEventsToo: one volatile write, whoever handles it.
        AddHandler(PointerMovedEvent, (_, _) => PrewarmPacer.NoteInput(), RoutingStrategies.Tunnel, true);
        AddHandler(PointerPressedEvent, (_, _) => PrewarmPacer.NoteInput(), RoutingStrategies.Tunnel, true);
        AddHandler(PointerWheelChangedEvent, (_, _) => PrewarmPacer.NoteInput(), RoutingStrategies.Tunnel, true);
        AddHandler(KeyDownEvent, (_, _) => PrewarmPacer.NoteInput(), RoutingStrategies.Tunnel, true);
        // ...and the swallow latch must not survive losing focus — see
        // WireGlobalKeyRoutes, which installs the same three on every pop-out.
        Deactivated += ClearActivationLatch;

        // Window-level keybind FALLBACK (bubble phase): clicking any toolbar/
        // inspector control moves keyboard focus off the timeline grid, which
        // would silently kill every editor shortcut — Timeline_KeyDown only
        // fires while focus sits inside the timeline. Any key press no control
        // handled bubbles up here and is offered to the keybind registry, so
        // Ctrl+Z/Ctrl+Y/H/… keep working regardless of the focused control.
        // Text inputs stay native (see GlobalKeybindFallback's guards).
        AddHandler(KeyDownEvent, GlobalKeybindFallback, RoutingStrategies.Bubble);

        // (Sheet strip seek wiring lives in SheetMusicPaneView now — the
        // view is re-created per dock host, so MainWindow can't hold it.)

        // Drag-over affordance: light up the DropOverlay while files hover
        // over the window so the user knows dropping will do something.
        AddHandler(DragDrop.DragEnterEvent, (_, e) => SetDropOverlayVisible(HasFiles(e)));
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropOverlayVisible(false));

        AddHandler(DragDrop.DropEvent, (s, e) =>
        {
            SetDropOverlayVisible(false);
            var files = e.DataTransfer.TryGetFiles()?.ToList();
            var file = files?.FirstOrDefault()?.TryGetLocalPath();

            if (file != null && DataContext is MainWindowViewModel vm)
            {
                _ = vm.Project.LoadFile(file);
                // Only the first file is opened; say so instead of silently
                // ignoring the rest of a multi-file drop.
                if (files!.Count > 1)
                {
                    vm.ProjectState.MainDisplayMessage = LocalizationService.Instance.Format(
                        "Status_DroppedMultipleFiles", files.Count);
                }
            }
        });

        DataContextChanged += (_, _) =>
        {
            DetachViewModelHandlers();

            if (DataContext is MainWindowViewModel vm)
            {
                if (this.FindControl<ChartRenderer>("MainChartRenderer") is { } renderer)
                {
                    _interactionManager = vm.InteractionFactory.CreateManager(vm, renderer);
                    _chartDataChangedHandler = RequestRendererRedraw;
                    _chartDataChangedSource = vm.ChartEditor;
                    vm.ChartEditor.ChartDataChanged += _chartDataChangedHandler;
                }

                _chartEditorPropertyChangedHandler = (_, ve) =>
                {
                    if (ve.PropertyName is (nameof(ChartEditorViewModel.PlacedNotes)) or
                        (nameof(ChartEditorViewModel.PlacedChords)))
                    {
                        RequestRendererRedraw();
                    }
                };
                _chartEditorPropertyChangedSource = vm.ChartEditor;
                vm.ChartEditor.PropertyChanged += _chartEditorPropertyChangedHandler;

                _historyChangedSource = vm.InteractionFactory.History;
                _historyChangedHandler = () => Dispatcher.UIThread.Post(RequestRendererRedraw);
                _historyChangedSource.HistoryChanged += _historyChangedHandler;

                _wheelAdjuster = vm.InteractionFactory.CreateWheelAdjuster();

                _layout.Bind(vm);
                InitializeBottomDock(vm);
                _shellLayout.UpdateSheetRowLayout(vm);
                _prewarm.EnsureStackedTracksCreated(vm);
                // Play Mode can be the BOOT surface (AppConfig.StartInPlayMode
                // — the VM ctor selects the tab before this handler runs), so
                // its deferred host must fill synchronously here, not in
                // Loaded/prewarm, or the first frame shows an empty screen.
                if (vm.IsPlayModeVisible) _prewarm.EnsurePlayModeViewCreated(vm);

                // Visibility flags drive dock pane presence: toggling a pane
                // on resurrects it (even after tab X / float-window X);
                // toggling off closes it in the layout.
                _shellHandlerSource = vm;
                _shellPropertyChangedHandler = (_, pe) =>
                {
                    switch (pe.PropertyName)
                    {
                        case nameof(MainWindowViewModel.IsInspectorVisible):
                            _dockController?.SetPaneVisible("Inspector", vm.IsInspectorVisible);
                            break;
                        // Stem Mixer / Riff Library are inspector TABS since
                        // 0.42.4, so "show it" means: make sure the inspector is
                        // up, then select the tab. The flags are kept (the View
                        // menu, the palette, the keybind and the Play-Mode
                        // presentation snapshot all read them) - only what they
                        // DO changed.
                        case nameof(MainWindowViewModel.IsStemMixerVisible):
                            if (vm.IsStemMixerVisible) vm.ShowInspectorTab(InspectorViewModel.MixerTabIndex);
                            break;
                        case nameof(MainWindowViewModel.IsPhraseCatalogVisible):
                            if (vm.IsPhraseCatalogVisible)
                                vm.ShowInspectorTab(InspectorViewModel.RiffLibraryTabIndex);
                            break;
                        case nameof(MainWindowViewModel.IsStackedTracksVisible):
                            _prewarm.EnsureStackedTracksCreated(vm);
                            break;
                        case nameof(MainWindowViewModel.IsPlayModeVisible):
                            // Creation is synchronous on the flip, so the
                            // surface is populated within the same property-
                            // change dispatch that makes the host visible.
                            if (vm.IsPlayModeVisible) _prewarm.EnsurePlayModeViewCreated(vm);
                            break;
                        case nameof(MainWindowViewModel.IsSheetMusicVisible):
                            _topDockController?.SetPaneVisible("SheetMusic", vm.IsSheetMusicVisible);
                            _shellLayout.UpdateSheetRowLayout(vm);
                            break;
                        case nameof(MainWindowViewModel.IsMinimapShown):
                            // The song map is part of what the top area cannot be smaller than.
                            _shellLayout.UpdateSheetRowLayout(vm);
                            break;
                        case nameof(MainWindowViewModel.IsBottomDockVisible):
                            // The dock row's floor: 0 while it is collapsed, the
                            // Inspector's usable height while it shows.
                            _shellLayout.ApplyEditorRowFloors(vm);
                            break;
                        case nameof(MainWindowViewModel.IsAiChatVisible):
                            // The column first (its widths), then the pane's place in the dock.
                            _layout.ApplyAiChatColumn(vm.IsAiChatVisible);
                            SyncAiChatPane(vm);
                            break;
                        case nameof(MainWindowViewModel.IsPreviewVisible):
                            // Toggling the 3D preview off closes its pop-out
                            // window too (the Closed handler re-docks the view).
                            if (!vm.IsPreviewVisible) _preview3DWindow?.Close();
                            break;
                        case nameof(MainWindowViewModel.IsGameplayPresentation):
                            // The flags are back by now; put each pane back where
                            // it lived, floating ones included.
                            if (!vm.IsGameplayPresentation)
                                _gameplayDock.AfterGameplay(_dockController, _topDockController, _sideDockController);
                            break;
                    }
                };

                // A run hides the panes from OnIsGameplayPresentationChanged, i.e.
                // before PropertyChanged; PropertyChanging is the last moment the
                // layout is still the user's.
                _shellPropertyChangingHandler = (_, pe) =>
                {
                    if (pe.PropertyName == nameof(MainWindowViewModel.IsGameplayPresentation)
                        && !vm.IsGameplayPresentation)
                    {
                        _gameplayDock.BeforeGameplay(_dockController, _topDockController, _sideDockController);
                    }
                };

                vm.PropertyChanged += _shellPropertyChangedHandler;
                vm.PropertyChanging += _shellPropertyChangingHandler;

                _layoutResetHandler = () => ResetLayoutToDefaults(vm);
                vm.LayoutResetRequested += _layoutResetHandler;

                _timelinePopOutHandler = PopOutTimeline;
                vm.TimelinePopOutRequested += _timelinePopOutHandler;

                // File ▸ Exit goes through Close() so OnClosing's
                // unsaved-changes gate sees it exactly like the caption button.
                _exitRequestedHandler = Close;
                vm.ExitRequested += _exitRequestedHandler;
            }
        };
    }

    private Action? _exitRequestedHandler;

    /// <summary>Set once the user has explicitly resolved the unsaved-changes
    /// prompt (Save succeeded or Discard) so the re-issued Close proceeds.</summary>
    private bool _closeConfirmed;
    private bool _closePromptOpen;

    /// <summary>
    ///     The window's ONE read of the composition root. <see cref="MainWindow" /> is
    ///     built with a parameterless constructor (by <c>App</c> and by the headless
    ///     tests), so it is the boundary that hands its collaborators their
    ///     dependencies: they take a <see cref="Func{TResult}" /> per service and
    ///     resolve at call time, exactly as late as before, so nothing is constructed
    ///     earlier than it was. Null when there is no composition root.
    /// </summary>
    private static IServiceProvider? CompositionRoot() => App.Services;

    /// <summary>Resolves <typeparamref name="T" /> from <see cref="CompositionRoot" />
    /// at the moment of the call; null without a composition root or registration.</summary>
    private static T? ResolveService<T>() where T : class => CompositionRoot()?.GetService<T>();

    /// <summary>
    ///     One shutdown step that must not take the close down with it — and that
    ///     must still SAY something in a Release build.
    ///     <para>
    ///         These four must not be <c>catch { Debug.WriteLine(…) }</c>, which the
    ///         compiler strips outside a DEBUG build: three of them persist state
    ///         (the dock layout, the window layout, the live tone rig — the last of
    ///         which is the ONLY copy of the last few seconds of knob tweaking,
    ///         because ToneSessionService polls rather than writing per knob), so a
    ///         silent failure here is quietly lost work with nothing anywhere to
    ///         explain it. <c>App.TerminateNow</c> flushes the logger, so a warning
    ///         written here still reaches the file.
    ///     </para>
    /// </summary>
    private static void TryOnClose(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            ResolveService<IAppLogger>()?.Warning(AppLogCategory.Application,
                $"[Shutdown] {what} failed: {ex.Message}");
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // An isolated-app teardown (ReleaseForIsolatedTeardown, test hosts only):
        // no prompt, no persistence and above all no TerminateNow — the process
        // goes on to host the next app. Pop-outs close with their owner, so latch
        // _shuttingDown first exactly as the real close does.
        if (_isolatedTeardown)
        {
            TryOnClose("Closing pop-out windows", MarkShuttingDownAndClosePopOuts);
            base.OnClosing(e);
            return;
        }

        // Data-loss gate FIRST: closing with unsaved edits pauses for a
        // Save / Discard / Cancel prompt. This must run before ANY of the
        // teardown below — the terminate path kills the process with zero
        // further dispatching, so a prompt after it could never be shown.
        if (!_closeConfirmed && DataContext is MainWindowViewModel vm && vm.IsProjectDirty)
        {
            e.Cancel = true;
            _ = ConfirmCloseWithUnsavedChangesAsync(vm);
            return;
        }

        // Terminate the process *immediately*. Anything we do before
        // TerminateNow() — base.OnClosing, layout-persist, even
        // SaveAppConfig — runs while AGPF.vst3 / BIAS FX 2 are still
        // active in their own threads, and those plugins access-violate
        // (0xC0000005) when the host starts going down through any
        // managed-side teardown. AppConfig is persisted on every change,
        // the project is on disk, and the layout-persist was a nice-to-have
        // — none of it is worth a hard crash every shutdown. TerminateNow
        // flushes the logger and then TerminateProcess(0)s the kernel-side
        // process, which the OS reclaims cleanly. The cancellable
        // WindowClosingEventArgs becomes irrelevant the instant we
        // terminate, so we never even call base.OnClosing.
        // Latch the shutdown flag BEFORE anything closes: the two pop-out
        // windows are owned by this one, so they close with it, and their
        // Closed handlers would otherwise re-parent the single 3D-preview /
        // timeline instance into a window that is tearing down. See
        // MainWindow.Docking's _shuttingDown.
        // The two process-lifetime subscriptions this window takes out in its
        // ctor. Neither survives a TerminateNow (the process is gone), but the
        // CLEAN path below leaves through the normal CRT path — and the 250 ms
        // focus guard would go on ticking into a closing window, while the
        // STATIC UiScaleController.ScaleChanged would keep this whole window
        // (and its visual tree) alive for whatever comes after.
        TryOnClose("Shell teardown", StopShellTimersAndSubscriptions);
        TryOnClose("Closing pop-out windows", MarkShuttingDownAndClosePopOuts);
        TryOnClose("Persist dock state", PersistDockLayout);
        TryOnClose("Persist layout", _layout.PersistOnClose);
        // The live tone rig is the ONE piece of state the comment above does not
        // cover. It is not "persisted on every change" — it cannot be, because a
        // knob write happens at pointer rate during a drag and hanging config I/O
        // off that would put file writes on the UI thread sixty times a second.
        // ToneSessionService polls instead, so without this the last few seconds
        // of tweaking are lost on quit. Cheap and no-ops when nothing changed.
        TryOnClose("Persist tone rig",
            () => ResolveService<DotCharter.Tone.Audio.Dsp.ToneSessionSet>()?.FlushAll());
        // A Rocksmith DotCharter started ends with it (quit, then ended); one the user started is only detached.
        TryOnClose("End Rocksmith", () => DotCharter.Tone.Audio.Tonelab.TonelabGameLauncher.EndStartedGameOnExit(CompositionRoot()));

        // When every VST runs out-of-process (AppConfig.OutOfProcessVst
        // with no in-proc plugins loaded), teardown cannot execute
        // third-party plugin code — take the clean close instead: the window
        // closes normally, desktop.Exit fires, and App.ShutdownServices
        // disposes the audio stack (sandbox quit + engine destroy). The
        // TerminateNow fast-exit above stays for the legacy in-proc mode only.
        if (App.TryBeginCleanShutdown())
        {
            base.OnClosing(e);
            return;
        }
        App.TerminateNow();
    }

    /// <summary>
    ///     Closes this window WITHOUT the exit path, for a host that boots more
    ///     than one app per process — the headless test session (one app per
    ///     dispatch). A plain <c>Close()</c> reaches <see cref="App.TerminateNow" />,
    ///     so the session would abandon every <see cref="MainWindow" /> open
    ///     instead: Avalonia's <c>MediaContext</c> kept each one registered as a
    ///     top level, the STATIC <c>UiScaleController</c> / <c>ChartTheme</c>
    ///     events kept their subscribers, and every abandoned window stayed
    ///     reachable with its whole visual tree — the headless suite grew without
    ///     bound. Releases what the ctor took out, closes for real (unregistering
    ///     the top level) and detaches the shell, so every control's
    ///     <c>OnDetachedFromVisualTree</c> drops its own static subscriptions.
    ///     The product never calls it; <c>OnClosing</c> takes this branch only
    ///     when the flag is set here.
    /// </summary>
    internal void ReleaseForIsolatedTeardown()
    {
        _isolatedTeardown = true;
        StopShellTimersAndSubscriptions();
        Close();
        DisposeScreenHosts();
        Content = null;
    }

    /// <summary>
    ///     Disposes every <see cref="ScreenHost" /> in the shell (today the
    ///     Settings and Home hosts <c>MainWindowPrewarm</c> creates), which disposes
    ///     each host's DI SCOPE and every scoped view model in it — the host's own
    ///     contract is "dispose the host with its owner", and nothing did. Scoped
    ///     settings view models (<c>SettingsShellViewModel</c>,
    ///     <c>KeybindsSettingsViewModel</c>) subscribe to the PROCESS-WIDE
    ///     <c>LocalizationService.Instance.PropertyChanged</c> and unsubscribe only
    ///     in <c>Dispose</c>, so an undisposed scope pinned its whole app — window,
    ///     container and all, ~250 MB a <see cref="MainWindow" /> — for the rest of a
    ///     headless run (measured with a heap dump; pinned by
    ///     <c>Perf/IsolatedAppReleaseUiPerfSmokeTests</c>). The product's close
    ///     terminates the process, so only an isolated teardown ever reaches this.
    /// </summary>
    private void DisposeScreenHosts()
    {
        foreach (var host in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(this).OfType<ScreenHost>().ToList())
        {
            if (host.Parent is ContentControl slot && ReferenceEquals(slot.Content, host)) slot.Content = null;
            host.Dispose();
        }
    }

    private bool _isolatedTeardown;

    /// <summary>
    ///     Releases what the ctor took out and nothing else: the 250 ms
    ///     keyboard-focus guard, and the subscription to the STATIC
    ///     <c>UiScaleController.ScaleChanged</c>. Idempotent — the close path can
    ///     be re-entered (the unsaved-changes prompt cancels and comes back).
    /// </summary>
    private void StopShellTimersAndSubscriptions()
    {
        _focusGuard?.Stop();
        _focusGuard = null;
        Services.UiScaleController.ScaleChanged -= ApplyUiScale;
        Services.UiScaleController.AutoModeChanged -= OnAutoUiScaleModeChanged;
    }

    private async Task ConfirmCloseWithUnsavedChangesAsync(MainWindowViewModel vm)
    {
        if (_closePromptOpen) return; // e.g. the user hit the X twice
        _closePromptOpen = true;
        try
        {
            switch (await vm.Project.AskAboutUnsavedChangesAsync())
            {
                case UnsavedChangesChoice.Save:
                    // A first-save routes through the Save As picker; a cancel
                    // there (or a failed write) must keep the session alive.
                    if (!await vm.Project.SaveProjectForCloseAsync()) return;
                    _closeConfirmed = true;
                    Close();
                    break;
                case UnsavedChangesChoice.Discard:
                    _closeConfirmed = true;
                    Close();
                    break;
                    // Cancel: do nothing — the window stays open.
            }
        }
        catch (Exception ex)
        {
            // This is the DATA-LOSS GATE, and it was the one path here with a
            // `finally` and no `catch`. `OnClosing` has already set
            // `e.Cancel = true` and handed this off as an un-awaited task, so a
            // throw out of ShowAsync/SaveProjectForCloseAsync faulted
            // UNOBSERVED and the user saw literally nothing happen when they
            // pressed X — no dialog, no error, no log line, and no way to tell
            // that from the app ignoring the click.
            //
            // Staying open is the right failure direction (the edits are still
            // there and `_closeConfirmed` stays false, so the next X re-runs the
            // prompt), but it has to be SAID. Nothing else in the close path
            // can report this: the terminate branch below never dispatches
            // again.
            ResolveService<IAppLogger>()?.Error(AppLogCategory.Application,
                "The unsaved-changes prompt failed, so the window stayed open with the project still dirty. " +
                "Nothing was saved and nothing was discarded; closing again re-runs the prompt.", ex);
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    /// <summary>
    ///     Unhooks EVERY handler <c>DataContextChanged</c> registers, so a
    ///     second DataContext assignment cannot leave the previous view model's
    ///     subscriptions live.
    /// </summary>
    /// <remarks>
    ///     It unhooks every handler the block registers, not only the three RENDER-invalidation
    ///     handlers: the same block also registers the pane-visibility switch,
    ///     <c>LayoutResetRequested</c> and <c>TimelinePopOutRequested</c>, and leaving those as
    ///     untracked lambdas would be a leak. MainWindow.DataContext is set exactly once today
    ///     (App.axaml.cs), so this never fired twice in production — but if it
    ///     ever does (a test harness reusing the window, a future re-bind),
    ///     View → Reset Layout would run twice and two DockLayoutControllers
    ///     would fight over one DockControl. Registering and detaching
    ///     happen in one place each; keep them in step.
    /// </remarks>
    private void DetachViewModelHandlers()
    {
        _interactionManager?.Dispose();
        _interactionManager = null;

        if (_shellHandlerSource != null)
        {
            if (_shellPropertyChangedHandler != null)
                _shellHandlerSource.PropertyChanged -= _shellPropertyChangedHandler;
            if (_shellPropertyChangingHandler != null)
                _shellHandlerSource.PropertyChanging -= _shellPropertyChangingHandler;
            if (_layoutResetHandler != null)
                _shellHandlerSource.LayoutResetRequested -= _layoutResetHandler;
            if (_timelinePopOutHandler != null)
                _shellHandlerSource.TimelinePopOutRequested -= _timelinePopOutHandler;
            if (_exitRequestedHandler != null)
                _shellHandlerSource.ExitRequested -= _exitRequestedHandler;
        }

        _shellHandlerSource = null;
        _shellPropertyChangedHandler = null;
        _shellPropertyChangingHandler = null;
        _layoutResetHandler = null;
        _timelinePopOutHandler = null;
        _exitRequestedHandler = null;

        if (_chartDataChangedSource != null && _chartDataChangedHandler != null)
            _chartDataChangedSource.ChartDataChanged -= _chartDataChangedHandler;
        _chartDataChangedSource = null;
        _chartDataChangedHandler = null;

        if (_chartEditorPropertyChangedSource != null && _chartEditorPropertyChangedHandler != null)
            _chartEditorPropertyChangedSource.PropertyChanged -= _chartEditorPropertyChangedHandler;
        _chartEditorPropertyChangedSource = null;
        _chartEditorPropertyChangedHandler = null;

        if (_historyChangedSource != null && _historyChangedHandler != null)
            _historyChangedSource.HistoryChanged -= _historyChangedHandler;
        _historyChangedSource = null;
        _historyChangedHandler = null;
    }

    private static bool HasFiles(DragEventArgs e)
    {
        try { return e.DataTransfer.TryGetFiles()?.Any() == true; }
        catch { return false; }
    }

    private void SetDropOverlayVisible(bool visible)
    {
        if (this.FindControl<Avalonia.Controls.Border>("DropOverlay") is { } overlay)
            overlay.IsVisible = visible;
    }

    private void RequestRendererRedraw()
    {
        this.FindControl<ChartRenderer>("MainChartRenderer")?.RequestNextFrameRendering();
        if (this.FindControl<MinimapOverviewControl>("MinimapOverview") is { } minimap)
        {
            minimap.InvalidateStaticLayer();
            minimap.InvalidateVisual();
        }
    }
}
