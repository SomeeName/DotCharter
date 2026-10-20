# Rendering / perf — startup work and hidden-surface prewarm

Part of the rendering/perf reference. Router: [../rendering-perf-detail.md](../../rendering-perf-detail.md).

## Startup: `MainWindowViewModel`'s constructor runs before the window exists

- **`MainWindowViewModel`'s constructor runs before the window exists — nothing
  on it may touch the audio engine or start a library scan.** It is not an
  ordinary view-model ctor: the composition root resolves it (via
  `AppKeybindRegistrar`, which takes it) *before* `new MainWindow()`, and with
  `AppConfig.StartInPlayMode` it also sets `SelectedMainTabIndex` itself, so
  `OnSelectedMainTabIndexChanged` and `PlayModeViewModel.OnCurrentScreenChanged`
  are on the pre-first-paint path too. Two costs were found there and are now
  pinned by `StartupWorkDeferralTests`: the tab switch called
  `Transport.PauseCommand` (a native JUCE round-trip issued while
  `ProjectSessionService.Initialize` was still opening the device on a worker —
  48 ms warm, ~265 ms mid-open, for a transport that cannot be playing), and the
  Menu's plan card kicked the FULL PSARC library scan inline (7.9 s / 2,160 songs
  cold, with per-batch `IProgress` callbacks marshalled onto the UI thread ahead
  of the first frame). Anything similar goes behind a `Dispatcher.UIThread.Post`
  at `DispatcherPriority.Background`, which starts it once the window is up.
  **That rule reaches every view model it TAKES, not just its own body**:
  `PhraseCatalogViewModel` (the Riff Library pane) read the whole library in its
  ctor — a full `.rscp` deserialize per stored riff, growing with use, for a pane
  nothing shows at boot. It now posts the load at `Background`, and
  `PhraseCatalogView`'s attach calls `EnsureLoaded()` as the open-it-early path
  (`UI/PhraseCatalogDeferralTests`, behavioural — the fake catalog counts reads).
  **`StemModelsSettingsViewModel` was the same shape one level further out
  (2026-08-22):** it is reached as `MainWindowViewModel` <- `IAppDialogService`
  <- `SettingsViewModel` <- this, and its ctor started three probes of real
  machine state for a Settings tab nobody had opened — `GpuRuntimeInstaller
  .GetReport` (a `NativeLibrary.TryLoad("nvcuda.dll")`, i.e. the NVIDIA driver
  DLL loaded into the process, plus a disk scan), `DescribeBackendAsync` (the
  native ONNX Runtime environment init) and `StemCacheStore.MeasureUsage` (every
  file in the durable stem cache). None of them blocks the UI thread, which is
  why they were invisible: the cost is working set, three pool threads and the
  disk head, concurrently with the audio-device open and the PSARC scan. They
  moved into an idempotent probe run. **Its opener is the screen becoming
  VISIBLE, not attaching** — every settings surface is built with the settings
  shell (the cross-tab search walks them all), so an attach hook would fire on
  the boot path this fixes. `StemModelsSettingsViewModel` implements
  `IScreenLifecycle` and the settings shell calls `OnShown`; a tab still drawn
  by the legacy slot would use an `EffectiveVisibilityWatcher` instead (never an
  `IsEffectivelyVisible` property-changed branch, which never fires).
  `Presentation/Settings/StemModelsProbeDeferralTests` is behavioural (the fakes
  count the probes) plus a source lint that the shell really opens it.
  Profile the graph with `StartupProfileUiPerfSmokeTests` before adding work here.

- **GC is held off the boot path (0.49.6, `Diagnostics/StartupGcHold`).**
  A 12 s `dotnet-trace` sample of the R2R v0.49.4 release build put
  `Thread.PollGC` — the UI thread suspended for collections — at 209 ms of
  `MainWindow.InitializeComponent`'s 785 ms and another 202 ms inside the
  deferred-view construction after first paint; the rest is genuine tree work
  (`SetLogicalParent` / `ApplyStyling`, i.e. markup size). `App
  .OnFrameworkInitializationCompleted` opens `GC.TryStartNoGCRegion` (240 →
  128 → 64 MB, first the runtime accepts) as its first statement and ends it
  in the Opened → Background post right before the `StartupPhases:` line,
  which now ends in `gc=held|blown|unavailable|off gc0= gc1= gc2=` (the
  `1/1/1` a held boot reports is the blocking collection `TryStartNoGCRegion`
  performs to establish the region). Measured on an R2R publish with a fresh
  config, warm disk, three runs: `windowCtor` 411 → 325–328 ms, `bind` 351 →
  227–231, `windowConstructed` 934 → 715–718, `interactive` 1413 →
  1113–1171. `DOTCHARTER_NO_STARTUP_GC_HOLD=1` is the A/B; `blown` on the
  phase line means the boot allocated past the budget and a GC ran anyway —
  raise the first budget, do not remove the hold. Pinned by
  `UI/StartupGcHoldTests` (contract + placement lint).
  **Do not profile startup without a sampling profiler again** — the recipe
  that produced these numbers is: `dotnet-trace collect --duration 00:00:12
  -- <staged R2R exe>` with a portable `{}` config, `dotnet-trace convert
  --format Speedscope`, then aggregate self-time on the nearest MANAGED frame
  (the `CPU_TIME` / `UNMANAGED_CODE_TIME` pseudo-leaves absorb everything
  otherwise), scoped to the frame you care about.

- **`windowCtor=` and `bind=` are STYLING, so they are paid per element built
  (#194, 2026-10-05).** A `dotnet-trace` of the R2R boot put almost all of both
  phases in `StyledElement.ApplyStyling` as elements joined the window's logical
  tree — `new MainWindow()` styles everything `InitializeComponent` builds, and
  the bind styles `PlayModeView` as it is set into `PlayModeHost`. So the lever is
  the ELEMENT COUNT in front of the first frame, not any one slow call. Five
  subtrees the default boot (Play Mode, full-bleed) never shows are now the
  `ContentTemplate` body of a `ContentControl` host — built only when a layout
  pass reaches the presenter: the Amp & FX rig in BOTH hosts
  (`MainWindow.ShellRigScreenHost`, `PlayModeView.RigScreenHostName`; ~450
  elements each with its sub-views; the content is `ToneRig.AmpFxRig`, which
  `DeferredRigScreen` keeps null until wanted, and a null content builds
  nothing), the menu bar (`MainMenuBarHost`), the editor toolbar
  (`EditorToolbarHost`) and the status rail (`StatusBarHost`). The gate sits on
  the host (or above it: the menu bar's is `TitleBarRow`, both in
  `Views/Shell/WindowChromeView` since #654), never on the body.
  Measured on a published `r2r=yes` build with the null audio service and a
  portable config booting Play Mode, six interleaved pairs, medians:
  **`windowCtor` 444 → 237 ms, `bind` 255 → 163, `windowConstructed` 838 → 543,
  `opened` 935 → 640, `interactive` 1176 → 851**. A charter boot (editor first)
  shows those rails in the first frame, so for it the cost moves from the ctor
  into the first layout instead of disappearing.
  - **Each body is still prewarmed after first paint.** `HiddenShellSurfaces()`
    lists the HOSTS (`EditorToolbarHost`, `TitleBarRow`, `StatusBarHost`, the rig
    overlay), and realizing a host is what builds its body. Code that walks a
    rail goes through `MainWindowPrewarm.DeferredChrome<T>(host)`, which
    builds the body first (`HiddenSubtreePrewarmer.BuildDeferredBody`:
    `ApplyTemplate` + one `UpdateChild`, construction only); the rig's tone-drawer
    names are listed only AFTER `EnsureRigScreen()` + `BuildDeferredBody`, or
    they resolve to nothing and drop out of the drain silently.
  - **Pinned by** `UI/StartupWindowDeferralTests` (default gate: each host's
    shape, no inline copy anywhere in the shell or Play Mode, the prewarm's
    build-before-list order) and `Perf/StartupWindowDeferralUiPerfSmokeTests`
    (a real Play Mode boot builds none of them through ctor, bind and first
    layout; the drain builds AND measures every one; opening the rig before any
    prewarm builds and lays it out). A headless Debug A/B of the first switches
    after the drain showed the same shape on both sides (editor median
    ~130 → ~101 ms, first rig open ~85 → ~65 ms; JIT-dominated, `r2r=no`).
  - **What is left** in `bind` is `PlayModeView`'s inline markup, mostly Song
    Select and Results, which cannot be template bodies while the code-behind
    needs their names (see [deferred-screen-bodies.md](deferred-screen-bodies.md)); in `windowCtor` the
    window itself (`ExtendClientArea` is a native call) and the editor's own
    elements, `Preview3DView` among them (a single instance by rule).

- **`vmGraph=` must not touch a disk (#194).** One R2R boot read `vmGraph=336`
  against 45 ms for every other run; the whole gap sat between the log lines
  "Initializing audio engine" and "Main window view model initialized", where
  the only I/O was `ProjectSessionService.Initialize`'s
  `File.Exists(LastProjectPath)` — on the owner's machine a path on a SPINNING
  HDD, cold after a night idle. The probe now runs on a worker inside the queued
  startup reload (`StartupProjectExists`, a seam), so a parked disk delays the
  reload and never the first frame. An EMPTY path still gets the default project
  synchronously; a missing file gets it from the reload, which leaves the first
  frame in the same not-yet-loaded state an existing project always had.
  Pinned behaviourally by `Services/Session/StartupProjectProbeDeferralTests`.

- **A startup assembly stays ReadyToRun; an off-startup one may not be (#410).**
  The release publish leaves the AI SDK, Guitar Pro, PSARC/Wwise, ONNX, GDI+ and
  the non-Windows Avalonia backends as IL (`PublishReadyToRunExclude` in
  `DotCharter.UI.csproj`): none of them loads before `StartupPhases:`. Code that
  moves one of them onto the boot path makes every launch JIT it, and `r2r=yes`
  will not show it (the probe reads `DotCharter.dll`). Take the assembly off the
  list in the same change; `Infrastructure/PublishPayloadSettingsTests` pins the
  names that must stay precompiled. See `tools/release/README.md` § "Payload size".
