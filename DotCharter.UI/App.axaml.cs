// The App itself: Initialize, the startup sequence in OnFrameworkInitializationCompleted, the split-install cleanup and ConfigureServices, the root the slices hang off.
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DotCharter.Rendering.Engines;
using DotCharter.Rendering.Vulkan;
using DotCharter.Services.Diagnostics;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.UI.Platform;
using DotCharter.UI.Controls.Themes;
using DotCharter.UI.Input;
using DotCharter.UI.Platform;
using DotCharter.UI.Services;
using DotCharter.UI.Theming;
using DotCharter.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using DotCharter.Services.Infrastructure.Shell;
using ChartEditorViewModel = DotCharter.UI.ViewModels.Editor.ChartEditorViewModel;
using MainWindowViewModel = DotCharter.UI.ViewModels.Shell.MainWindowViewModel;
using ProjectState = DotCharter.Services.State.ProjectState;
using TransportViewModel = DotCharter.UI.ViewModels.Editor.TransportViewModel;

namespace DotCharter.UI;

public partial class App : Application
{
    private readonly JsonConfigurationService _configService = JsonConfigurationService.ForApplication();
    private ServiceProvider? _serviceProvider;
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        // Before ANY markup is parsed: a tooltip that is opened without its
        // delay creates and destroys a popup host per hover, which is what
        // stutters the composed 2D timeline when the pointer sweeps a dense
        // rail. See Platform/ToolTipHoverPolicy.
        Platform.ToolTipHoverPolicy.Apply();
        Platform.SliderWheelPolicy.Apply();
        Platform.PopupBringIntoViewPolicy.Apply();
        // Warm appconfig.json / keybinds.json on a worker thread while the UI
        // thread parses App.axaml (~300 ms). The first LoadAppConfig call costs
        // ~90 ms (file read + System.Text.Json metadata build) and would
        // land on the UI thread in the middle of service wiring; by the time
        // wiring asks for the config it is a cached-reference read. Errors are
        // buffered by the service and replayed once the logger is attached.
        _ = Task.Run(() =>
        {
            _configService.LoadAppConfig();
            _configService.LoadKeybinds();
            // Same trick for the RS gear catalogue: a Lazy over a 144 KB
            // embedded JSON blob (613 gear entries + their knobs), measured
            // 64.9 ms on first touch. ToneDesignerDialogViewModel must not force
            // it from a FIELD INITIALIZER, i.e. inside the composition root's
            // view-model graph phase, before the window exists; it is deferred
            // to the dialog's first layout, and this warms it in parallel so
            // even that read is a cached-reference read. Thread-safe by
            // construction (Lazy with the default publication mode) and
            // throw-free (its factory returns the Empty catalogue on failure).
            _ = DotCharter.Tone.Audio.Dsp.Tones.RsGearCatalog.Shared;
        });
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Hold GC off the boot path — DI, the view-model graph, the compiled
        // shell markup, first layout — until the window is interactive. Sampled
        // on the R2R release build: ~400 ms of the boot was the UI thread
        // suspended for collections (Diagnostics/StartupGcHold). Ended in the
        // Opened → Background post below; never throws, reports on the
        // StartupPhases line.
        Diagnostics.StartupGcHold.Begin();
        // End-to-end boot timing (DI build → view-model graph → window
        // constructed). Logged once below so startup regressions are
        // measurable instead of anecdotal.
        var bootStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var collection = new ServiceCollection();
        ConfigureServices(collection, _configService, AppPlugins.PackageReaderFromEnvironment());
        _serviceProvider = collection.BuildServiceProvider();
        Services = _serviceProvider;
        // Nothing we spawn may outlive us. TerminateNow TerminateProcess-es this
        // process, so no managed cleanup ever runs — a job object is the only
        // mechanism that survives that, and children inherit it automatically, so
        // this single call also covers juce_vst_host.exe (spawned from C++) and
        // every ffmpeg/yt-dlp/demucs the app will ever start.
        global::DotCharter.Services.Infrastructure.Process.ChildProcessJob.EnsureCurrentProcessIsContained(
            Services.GetService<IAppLogger>());
        // Process-wide statics reached from the services: before any catalogue is built.
        AppStatics.Wire(Services);
        var diBuiltMs = bootStopwatch.ElapsedMilliseconds;

        var configService = Services.GetRequiredService<IConfigurationService>();
        var appConfig = configService.LoadAppConfig();
        var localization = Services.GetRequiredService<ILocalizationService>();
        // Community language packs live in <ConfigRoot>/languages/*.json. On a
        // portable install ConfigRoot IS the install dir, so this folder is on
        // PayloadReceiptFilter's excluded-dirs list (an update payload may never
        // carry it, so a community pack is never overwritten).
        localization.LoadCommunityLanguages(
            Path.Combine(
                WritablePaths.ConfigRoot, "languages"));
        localization.SetConfiguredCulture(appConfig.LanguageCulture);

        // The app has ONE theme (0.62). Community theme packs and the five
        // sibling presets are gone with the picker that selected them, so
        // there is nothing to resolve here any more — only the user's own
        // per-token overrides to lay over it.
        //
        // Applied before the main window is constructed so everything paints
        // correctly first time. The colour-blind-safe string preset (if any) is
        // merged OVER the free-form colour overrides so the String* tokens win
        // — the fretboard reads these.
        var themeService = Services.GetRequiredService<IThemeService>();
        var startupOverrides = new Dictionary<string, string>(appConfig.ThemeColorOverrides);
        foreach (var kv in StringColorPresets.GetOverrides(appConfig.StringColorPresetId))
            startupOverrides[kv.Key] = kv.Value;
        themeService.ApplyPreset(ThemePresets.Default, startupOverrides);

        // Global UI scale (accessibility): seed before the window is built so it
        // applies its LayoutTransform on first layout.
        //
        // The MANUAL value first, then the mode. Under Auto that number is only
        // a starting point — MainWindow re-derives from its own client height in
        // Loaded, before RestoreOnLoad — but seeding it anyway means a user who
        // switches Auto off gets the factor they last chose rather than 1.0.
        UiScaleController.Set(appConfig.UiScale);
        UiScaleController.SetAutoMode(appConfig.UiScaleAuto);

        // Wire chart-renderer palette (2D Avalonia + 3D Vulkan string colors)
        // to the same theme service so runtime preset changes propagate.
        ChartTheme.Attach(themeService);
        Action redrawOnThemeChange = () => { Services?.GetService<ChartEditorViewModel>()?.RequestRedraw(); };
        ChartTheme.ThemeChanged += redrawOnThemeChange;
        _processWideUnhooks.Add(() => ChartTheme.ThemeChanged -= redrawOnThemeChange);

        var projectState = Services.GetRequiredService<ProjectState>();
        projectState.Keybinds = configService.LoadKeybinds();

        // Settings → Graphics (0.14.2): the effect/readability toggles persist
        // in AppConfig; mirror them into the in-memory UserSettings the 3D
        // snapshot reads each frame. Display settings (render scale / fps cap /
        // present mode / MSAA) are consumed by the renderer + render loop.
        projectState.EnableNoteHitExplosions = appConfig.EnableNoteHitExplosions;
        projectState.EnableHighwayNearFade = appConfig.EnableHighwayNearFade;
        projectState.EnableShowlightBeams = appConfig.EnableShowlightBeams;
        projectState.EnableShowlightFog = appConfig.EnableShowlightFog;
        projectState.EnableAnchorPreview = appConfig.EnableAnchorPreview;
        projectState.EnableAnchorSweep = appConfig.EnableAnchorSweep;
        projectState.EnableBeatPulse = appConfig.EnableBeatPulse;
        projectState.EnableDepthFog = appConfig.EnableDepthFog;
        projectState.EnableVignette = appConfig.EnableVignette;
        projectState.EnableEnhancedHitFeedback = appConfig.EnableEnhancedHitFeedback;
        projectState.EnableCrowdBackground = appConfig.EnableCrowdBackground;
        projectState.EnableHighwayLighting = appConfig.EnableHighwayLighting;
        projectState.EnableBloom = appConfig.EnableBloom;
        projectState.EnableStringShimmer = appConfig.EnableStringShimmer;
        projectState.EnableHitSparks = appConfig.EnableHitSparks;
        projectState.EnableFilmicTonemap = appConfig.EnableFilmicTonemap;
        projectState.ApplyStringOrders(appConfig);
        // v0.34 lefty flip: PROFILE-level, not AppConfig — lefties are lefty on
        // every machine they play (the flag rides profile.json, incl. the
        // roaming/export bundle). Mirrored here at startup and re-staged on
        // every profile switch or flag change (SetLeftyFlip raises the event).
        var profilesForLefty = Services.GetRequiredService<
            DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>();
        projectState.LeftyFlip = profilesForLefty.ActiveProfile.LeftyFlip;
        profilesForLefty.ActiveProfileChanged += p => projectState.LeftyFlip = p.LeftyFlip;
        projectState.AnchorPreviewLeadMs =
            Core.Graphics.GraphicsSettingsPolicy.ClampAnchorPreviewLeadMs(
                appConfig.AnchorPreviewLeadMs);
        // Highway pace + board length. The editor preview has no "playthrough",
        // so the per-run slot is null here: the global override wins if the user
        // set one, otherwise the active chart's own value is applied when a
        // project loads (Play Mode resolves it again per run).
        projectState.HighwayScrollSpeed = Core.Graphics.HighwaySpeedPolicy.ResolveScrollSpeed(
            perRun: null,
            globalOverride: appConfig.HighwayScrollSpeedOverrideEnabled
                ? (float)appConfig.HighwayScrollSpeed
                : null,
            chartValue: null);
        projectState.HighwayDrawDistanceZ =
            Core.Graphics.HighwaySpeedPolicy.ClampDrawDistanceZ((float)appConfig.HighwayDrawDistanceZ);
        projectState.Preview3DCameraFill =
            Core.Graphics.GraphicsSettingsPolicy.ClampCameraFillPercent(appConfig.Preview3DCameraFill);
        // Render-loop pacing (fps cap + paused trickle) reads these volatile
        // statics each iteration; the setters clamp.
        Controls.Preview3DPacing.MaxFps = appConfig.MaxFps;
        Controls.Preview3DPacing.IdleFps = appConfig.IdleFps;
        // Visual lead (display-latency compensation): trims the composed path's
        // measured value.
        Controls.VisualLeadTiming.LeadTrimMs =
            Math.Clamp(appConfig.VisualLeadMs, 0, Controls.VisualLeadTiming.MaxLeadMs);
        // 2D timeline visuals (0.14.3): the timeline layers read this static
        // channel once per frame.
        Controls.Timeline2DVisuals.PolishEnabled = appConfig.Timeline2DPolishEnabled;
        Controls.Timeline2DVisuals.LaneTintEnabled = appConfig.Timeline2DLaneTint;
        // Gameplay tone HUD: the 3D HUD reads this static channel per frame.
        Rendering.Data.GameplayToneHud.Enabled = appConfig.ShowGameplayToneHud;
        // Present mode + MSAA: recorded before the renderer initializes; the
        // first swapchain/pipeline creation already honours them (clamped to
        // surface/device support).
        var vulkanService = Services.GetRequiredService<IVulkanRenderService>();
        vulkanService.SetPresentMode(appConfig.PresentMode);
        vulkanService.SetMsaaSamples(appConfig.MsaaSamples);
        vulkanService.SetRenderScalePercent(appConfig.RenderScalePercent);

        // Wire the editing scope so service-layer mutations follow the
        // currently focused track (set by clicking in any timeline panel).
        // When no track is focused the resolver returns null and the scope
        // falls back to ProjectState.ActiveArrangement / ActiveLevel.
        var registry = Services.GetRequiredService<ITrackContextRegistry>();
        var scope = Services.GetRequiredService<IEditingScope>();
        scope.SetResolver(() =>
        {
            var focused = registry.FocusedTrack;
            if (focused?.Level == null) return null;
            return new EditingTarget(focused.Arrangement, focused.Level);
        });

        // The keyboard target ladder's bottom rung. The pen already snaps the
        // playhead exactly the way the pointer does; the grid divisor it needs
        // lives on the transport view model, which the service layer must not
        // reach up to — hence the same installed-resolver idiom the editing
        // scope uses one block above.
        var editTargets = Services.GetRequiredService<DotCharter.Services.Editing.Targets.IEditTargetService>();
        var penService = Services.GetRequiredService<DotCharter.Services.Editing.Pen.IChartPenService>();
        var transportVm = Services.GetRequiredService<TransportViewModel>();
        editTargets.SetPenPositionProvider(() => penService.PenPosition(transportVm.GridDivisor));

        // Undo / redo refocuses the panel that owned the action so the user
        // sees the change happen on the same timeline they originally edited.
        var history = Services.GetRequiredService<IHistoryService>();
        history.FocusRequested += target =>
        {
            if (target != null) registry.Focus(target);
        };

        var logger = Services.GetRequiredService<IAppLogger>();
        logger.Info(AppLogCategory.Application, "DotCharter application services initialized.");
        // A refused plugin is a WARNING, which the status bar surfaces: resolve it first
        // so it is subscribed. The app runs on without the plugin.
        _ = Services.GetRequiredService<IStatusBarService>();
        AppPlugins.ReportPlugins(Services, logger, localization);
        AppPlugins.Activate(Services);

        // What a popover open costs on THIS box, per open: the owner reports a
        // ~200 ms freeze on Tempo / Mix / Tools / Tracks that no headless
        // measurement can see (the per-open popup HWND — see PopoverOpenTiming).
        Views.Shell.PopoverOpenTiming.Sink = (name, ms) =>
            logger.Info(AppLogCategory.Application,
                $"Popover '{name}' opened in {ms:F0} ms (Opening → Opened; popup host included).");

        // The same for an Inspector tab switch and a hosted dialog open.
        Views.Shell.SurfaceShowTiming.Sink = (name, ms) =>
            logger.Info(AppLogCategory.Application, $"Surface '{name}' shown in {ms:F0} ms (gesture → first idle tick).");

        // Flush any buffered early-startup errors that were captured before the logger existed.
        if (configService is JsonConfigurationService jsonConfig) jsonConfig.Logger = logger;

        // Opt-in local crash bundle (redacted; never auto-uploaded). Built next
        // to the unhandled-exception logging below so the flushed log tail is
        // captured; a no-op unless AppConfig.CrashReportOptIn is on.
        var crashReporter = new DotCharter.Services.Infrastructure.CrashReporting.CrashReportService(
            () => configService.LoadAppConfig(), logger);

        // Global unhandled-exception catchers â€” ensure that every crash path
        // produces a log entry that is flushed before the process exits.
        //
        // These three, the theme hook above and the console router below are
        // PROCESS-wide: each one roots this App's logger (and through its
        // subscribers the whole graph) until it is removed. Each is recorded in
        // _processWideUnhooks so ReleaseForIsolatedTeardown can undo it.
        UnhandledExceptionEventHandler onUnhandled = (_, e) =>
        {
            var ex = e.ExceptionObject as Exception
                     ?? new Exception(e.ExceptionObject.ToString() ?? "Unknown non-Exception throwable");
            logger.LogUnhandledException("AppDomain", ex);
            crashReporter.TryCapture("AppDomain", ex);
        };
        AppDomain.CurrentDomain.UnhandledException += onUnhandled;
        _processWideUnhooks.Add(() => AppDomain.CurrentDomain.UnhandledException -= onUnhandled);
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            logger.LogUnhandledException("TaskScheduler", e.Exception);
            crashReporter.TryCapture("TaskScheduler", e.Exception);
            e.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;
        _processWideUnhooks.Add(() => TaskScheduler.UnobservedTaskException -= onUnobserved);
        // Emit a marker on every process exit so we can distinguish a graceful
        // shutdown (logged here) from a hard native crash (no marker, just
        // "exit code -1" in the IDE / parent terminal). Native access
        // violations in third-party VST or audio libraries do NOT raise the
        // managed AppDomain.UnhandledException event, so the absence of any
        // crash log combined with absence of this marker == native fault.
        EventHandler onProcessExit = (_, _) =>
        {
            try
            {
                // The session tally goes FIRST: it is the line most likely to be
                // read, and the exit path below can still fail on a native
                // teardown. See ChartingSessionLog for why counts are the
                // summary and the per-event lines are the evidence.
                logger.Info(AppLogCategory.Application,
                    Services?.GetService<ChartingSessionLog>()?.Summarize()
                    ?? "ChartingSession: unavailable.");
                logger.Info(AppLogCategory.Application,
                    $"Process exit handler invoked. ManagedThreadId={Environment.CurrentManagedThreadId}, hasShutdownStarted={Environment.HasShutdownStarted}.");
                if (logger is IDisposable d) d.Dispose();
            }
            catch
            {
                // Never throw from ProcessExit.
            }
        };
        AppDomain.CurrentDomain.ProcessExit += onProcessExit;
        _processWideUnhooks.Add(() => AppDomain.CurrentDomain.ProcessExit -= onProcessExit);

        // Reinstall the smart console router (it filters native audio stdout).
        var consoleRouter = new ConsoleLogRouter(logger);
        consoleRouter.Install();
        _processWideUnhooks.Add(consoleRouter.Uninstall);

        // AppKeybindRegistrar takes MainWindowViewModel as a constructor
        // argument, so RESOLVING THE REGISTRAR builds the entire view-model
        // graph. Stamping "services wired" after that and then resolving the
        // shell (by then a cached reference) would report vmGraph as ~0 ms and
        // hide the ~250 ms shell construction inside
        // "services". Resolve the shell explicitly first and stamp there; the
        // registrar gets the same singleton one line later, so construction
        // order is unchanged.
        var servicesReadyMs = bootStopwatch.ElapsedMilliseconds;
        var viewModel = Services.GetRequiredService<MainWindowViewModel>();
        var viewModelMs = bootStopwatch.ElapsedMilliseconds;

        Services.GetRequiredService<AppKeybindRegistrar>().RegisterAll();
        Services.GetRequiredService<IKeybindRegistry>().RefreshBinds();
        // The MIDI controller's editor binds ride the same verbs; resolving
        // the dispatch is what starts it listening.
        var midiBindings = Services.GetRequiredService<MidiBindingDispatch>();
        // Non-modal, fire-and-forget; the test host disables it by switch.
        Services.GetRequiredService<DotCharter.Services.Update.UpdateCheckFlow>().StartStartupCheck();
        StartSplitInstallCleanup();
        // One line when a test or measurement launch redirects the user folders.
        if (DotCharter.Tone.Infrastructure.Storage.UserDataFolders.DescribeOverride() is { } dataRoot)
            Services.GetService<IAppLogger>()?.Info(AppLogCategory.Application, dataRoot);
        var keybindsMs = bootStopwatch.ElapsedMilliseconds;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => ShutdownServices();
            var window = new MainWindow();
            var windowCtorMs = bootStopwatch.ElapsedMilliseconds;
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            midiBindings.Dispatched += window.RequestChartFrame;

            // Install the app's bundled freely-licensed NAM captures into the
            // local library so a fresh install has a starter capture set in the
            // pedalboard's "NAM Captures → Library" view without a TONE3000
            // sign-in. Idempotent + throw-free; off the UI thread (file copy +
            // hashing). No-op when the module/library is unavailable.
            _ = Task.Run(() =>
            {
                try
                {
                    var library = Services.GetService<DotCharter.Tone.Online.Tone3000.INamCaptureLibrary>();
                    if (library is null) return;
                    // Repair any captures a pre-fix download stored with a wrong
                    // extension (.bin / wrong-case) — the neural loader dispatches
                    // by lowercase extension, so those play silently otherwise.
                    var repaired = library.MigrateFileExtensions();
                    if (repaired > 0)
                    {
                        logger.Info(AppLogCategory.Application,
                            $"Repaired {repaired} NAM capture file extension(s) in the library.");
                    }

                    var dir = Path.Combine(AppContext.BaseDirectory, "NamCaptures");
                    DotCharter.Tone.Online.Tone3000.BundledNamCaptureInstaller.InstallFromDirectory(
                        library, dir, logger);
                }
                catch (Exception ex)
                {
                    logger.Warning(AppLogCategory.Application, "Bundled NAM capture install failed.", ex);
                }

                // STRICTLY AFTER the install above, in the same task, and that
                // ordering is the whole point: a capture-backed preset names its
                // .nam by bare file name and resolves it against the capture
                // LIBRARY directory. On a fresh install those files only exist
                // there once InstallFromDirectory has copied them, so restoring
                // the tone first would hand the engine a path to nothing — a
                // modelless namamp, which is silence, on the one run where the
                // player's first impression is formed.
                // The coordinator, not the set: it pins the rig store to the
                // active profile, subscribes to profile switches and THEN calls
                // StartAll. Starting the set directly would restore a rig from
                // whichever profile the store happened to resolve, and would
                // never follow a switch.
                try { Services.GetRequiredService<DotCharter.Services.Audio.Dsp.ProfileToneRigCoordinator>().Start(); }
                catch (Exception ex)
                {
                    logger.Warning(AppLogCategory.Audio, "Tone session service failed to start.", ex);
                }

                // The chart synth's instrument: moves the app-wide choice into
                // the active profile once, then follows profile switches and song tones.
                try
                {
                    _chartSynthInstruments = Services.GetRequiredService<DotCharter.Services.Audio.ChartSynth.ChartSynthInstrumentCoordinator>();
                    _chartSynthInstruments.Start();
                }
                catch (Exception ex)
                {
                    logger.Warning(AppLogCategory.Audio, "Chart synth instrument coordinator failed to start.", ex);
                }
            });

            // stem archives keyed on a library song's decoded WAV move to its WEM
            // key once, before a native re-decode of that WAV could orphan them. On the
            // pool; the headless host disables it by switch.
            _ = DotCharter.Services.Audio.StemSeparation.Cache.WemKeyAdoption.StartAtStartup(
                Services.GetRequiredService<DotCharter.Services.Audio.StemSeparation.Cache.StemCacheStore>,
                () => Services.GetRequiredService<DotCharter.Services.Library.Cache.LibraryAudioCacheStore>().Root,
                logger);

            var windowConstructedMs = bootStopwatch.ElapsedMilliseconds;
            logger.Info(AppLogCategory.Application,
                $"Startup: DI build {diBuiltMs} ms, services wired {servicesReadyMs} ms, " +
                $"view-model graph {viewModelMs - servicesReadyMs} ms, keybinds {keybindsMs - viewModelMs} ms, " +
                $"window ctor {windowCtorMs - keybindsMs} ms, " +
                $"data-context bind {windowConstructedMs - windowCtorMs} ms, window constructed at {windowConstructedMs} ms.");

            // Machine-readable phase line, extended to "interactive": Opened
            // fires when the OS window is up; a Background-priority post from
            // there only runs once the render/layout/input queues have
            // drained — the closest proxy for time-to-interactive without
            // instrumenting the compositor. One line, key=value, stable
            // contract for the env-gated startup perf gate — extend, don't
            // reformat.
            //
            // r2r= is NOT decoration. PublishReadyToRun only applies to
            // `dotnet publish -r win-x64`, so bin/Release/net10.0/DotCharter.exe
            // JIT-compiles its entire startup path and reads roughly TWICE as
            // slow as the build we ship. Without this key every phase number
            // above is ambiguous, and a dev-build measurement gets diagnosed as
            // a product regression (see ReadyToRunProbe).
            window.Opened += (_, _) =>
            {
                // Restore a configured controller after first paint, even if
                // neither rig editor is opened during this session.
                if (!string.IsNullOrWhiteSpace(appConfig.RigMidiInputDevice))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        try { _ = Services.GetRequiredService<Tone.UI.ViewModels.PlayMode.PedalboardViewModel>().MidiControls; }
                        catch (Exception ex) { logger.Warning(AppLogCategory.Audio, "Rig MIDI controller failed to start.", ex); }
                    }, Avalonia.Threading.DispatcherPriority.Background);
                }

                var openedMs = bootStopwatch.ElapsedMilliseconds;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () =>
                    {
                        Diagnostics.StartupGcHold.End();
                        logger.Info(AppLogCategory.Application,
                        $"StartupPhases: di={diBuiltMs} services={servicesReadyMs} " +
                        $"vmGraph={viewModelMs - servicesReadyMs} keybinds={keybindsMs - viewModelMs} " +
                        $"windowCtor={windowCtorMs - keybindsMs} " +
                        $"bind={windowConstructedMs - windowCtorMs} windowConstructed={windowConstructedMs} " +
                        $"opened={openedMs} interactive={bootStopwatch.ElapsedMilliseconds} " +
                        $"r2r={Diagnostics.ReadyToRunProbe.Describe()} {Diagnostics.StartupGcHold.Describe()}");
                    },
                    Avalonia.Threading.DispatcherPriority.Background);
                // Mirror the same phase numbers to the Session Health dashboard.
                Diagnostics.StartupDiagnostics.SetPhases(
                    diBuiltMs, servicesReadyMs, viewModelMs, keybindsMs, windowCtorMs,
                    windowConstructedMs, openedMs, bootStopwatch.ElapsedMilliseconds);
            };
        }

        // No desktop lifetime (headless boots, tests): nothing will post the
        // interactive end, so the hold ends here.
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime) Diagnostics.StartupGcHold.End();
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Install-level chores at every start, off the UI thread: the split-install cleanup and a tonelab RS_ASIO.ini a crash left swapped.</summary>
    private void StartSplitInstallCleanup()
    {
        var logger = Services?.GetService<IAppLogger>();
        _ = DotCharter.Tone.Audio.Tonelab.TonelabGameLauncher.RestoreLeftIniAtStartup(logger);
        DotCharter.Services.Update.SplitInstallCleanup.StartAtStartup(logger);
    }

    /// <summary>
    ///     The composition root. Static and internal on purpose: it is the one
    ///     description of the whole object graph, and
    ///     <c>AppServiceGraphTests</c> builds it with
    ///     <c>ValidateOnBuild</c> so a missing registration, a cycle or a
    ///     captive dependency fails a test rather than a launch.
    /// </summary>
    /// <remarks>
    ///     <paramref name="configService" /> is the pre-created configuration
    ///     service whose first load was kicked off in <see cref="Initialize" />
    ///     — registering a fresh <see cref="JsonConfigurationService" /> here
    ///     would throw the warmup away.
    /// </remarks>
    internal static void ConfigureServices(
        IServiceCollection services, JsonConfigurationService configService, bool plugins = true)
    {
        // Rendering
        services.AddSingleton<IVulkanRenderService, VulkanRenderer>();
        // The five members a settings screen needs, without the rendering assembly.
        services.AddSingleton<DotCharter.Services.Infrastructure.Graphics.IGraphicsDeviceSettings>(sp =>
            new Platform.VulkanGraphicsDeviceSettings(sp.GetRequiredService<IVulkanRenderService>()));
        // The theme and the interface scale as Settings ▸ Appearance drives them, colours as hex strings.
        services.AddSingleton<DotCharter.Services.Infrastructure.Appearance.IAppearanceTheme>(sp =>
            new Platform.AppearanceTheme(sp.GetRequiredService<IThemeService>()));
        services.AddSingleton<DotCharter.Services.Infrastructure.Appearance.IInterfaceScale, Platform.InterfaceScale>();
        services.AddSingleton<Fretboard3DEngine>();
        // The first-run wizard's check of this computer: the renderer's device rule
        // and the audio wrapper's ABI gate, each asked without opening a device.
        services.AddSingleton<DotCharter.Services.Infrastructure.Graphics.IGpuRenderProbe, Platform.VulkanGpuRenderProbe>();
        services.AddSingleton<DotCharter.Tone.Audio.Abstractions.INativeEngineProbe, DotCharter.Tone.Audio.Engine.JuceNativeEngineProbe>();

        // Infrastructure.
        services.AddSingleton<IConfigurationService>(configService);
        // The same instance, seen through the slice DotCharter.Tone reads (the
        // logger, the tone rig, the audio host). Never a second config.
        services.AddSingleton<IRigConfigService>(configService);
        services.AddSingleton<IFileStorageService, DesktopFileStorageService>();
        // The OS shell and clipboard, and the documents shipped beside the exe:
        // the view models open URLs, folders and files through these instead of
        // starting processes themselves.
        services.AddSingleton<IShellLauncher, DesktopShellLauncher>();
        services.AddSingleton<ISystemClipboard, AvaloniaSystemClipboard>();
        services.AddSingleton<IApplicationCloser, DesktopApplicationCloser>();
        services.AddSingleton<IMainWindowHandle, DesktopMainWindowHandle>();
        services.AddSingleton<IBundledDocuments, BundledDocuments>();
        // Where the export/import dialogs read and write around their services.
        services.AddSingleton<IExportTargetWriter, ExportTargetWriter>();
        services.AddSingleton<DotCharter.Services.Infrastructure.Backup.IConfigBackupService>(
            _ => new DotCharter.Services.Infrastructure.Backup.ConfigBackupService());
        services.AddSingleton<IDiagnosticsBundleExporter>(
            _ => new DiagnosticsBundleExporter());
        services.AddSingleton<IProjectDocumentService, ProjectDocumentService>();
        services.AddSingleton<IAppLogger, AppLogger>();
        services.AddSingleton<ILocalizationService>(_ => LocalizationService.Instance);

        // Status-bar / diagnostics — must come *after* IAppLogger because it
        // subscribes to LogEmitted to surface warnings + errors in the UI.
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IStatusBarService>(sp =>
            new StatusBarService(
                sp.GetRequiredService<IUiDispatcher>(),
                sp.GetService<IAppLogger>()));

        // Plugins first: a package source must precede .dcsong in the scan order,
        // and IPackageFeatures defaults to none when nothing registered it.
        AppPlugins.AddPlugins(
            services, plugins ? DotCharter.Services.Plugins.PluginLoader.Declared(typeof(App).Assembly) : []);

        // Everything else, in registration ORDER - each block is one partial of
        // this class in its own App.*.cs file, named for what it holds. The order
        // is part of the contract: IArrangementRule and IEditorTool are injected
        // as one IEnumerable<T> and the container hands them over as registered.
        AddAudioServices(services);
        AddEditingServices(services);
        AddValidationServices(services);
        AddGameplayServices(services);
        AddOnlineServices(services);
        AddAiServices(services);
        AddMatchServices(services);
        AddStateServices(services);
        AddViewModelServices(services);
        Views.Shell.ScreenShellModule.Register(services);
        Presentation.Screens.Settings.SettingsModule.Register(services);
        Presentation.Screens.Home.HomeModule.Register(services);
        Presentation.Screens.Onboarding.OnboardingModule.Register(services);
        Presentation.Screens.Editor.EditorModule.Register(services);

        // The shell IS the navigation seam Home's cards reach through. One
        // instance, resolved lazily so the window's view model is not built
        // twice — see MainWindowViewModel.Destinations.cs.
        services.AddSingleton<Presentation.Contracts.Shell.IShellDestinations>(
            sp => sp.GetRequiredService<ViewModels.Shell.MainWindowViewModel>());
        // …and the keyboard-shortcuts seam: the palette catalog needs the shell.
        services.AddSingleton<Presentation.Contracts.Shell.IShortcutCatalog>(
            sp => sp.GetRequiredService<ViewModels.Shell.MainWindowViewModel>());
    }
}
