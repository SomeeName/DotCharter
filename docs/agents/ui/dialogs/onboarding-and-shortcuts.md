# Dialogs (`Views/Dialogs/`) — UI deep reference — the first-run onboarding wizard; Help > Keyboard Shortcuts

## The first-run onboarding wizard

`Views/Dialogs/OnboardingWizardView` over
`ViewModels/Onboarding/OnboardingWizardViewModel`, navigation in the pure
`ViewModels/Onboarding/OnboardingStepMachine` (eight pages: Welcome/role →
Library → Audio → Input → Tone → Latency → Profile → Done). Entry points —
every one of them linted by `UI/Onboarding/OnboardingReachabilityTests`:

- **Startup**: `MainWindowViewModel`'s constructor posts
  `Dialogs.Onboarding.OpenAtStartupIfNeeded()` on the same deferred hook as
  crash recovery. It opens exactly while `AppConfig.OnboardingCompleted` is
  false; **Finish AND dismissal (Esc / "Skip setup") both set it**, with
  `OnboardingCompletedVersion`, so nobody is nagged twice. **The gate is the
  BOOLEAN; `OnboardingCompletedVersion` has two writers and no product reader
  ON PURPOSE** — it is support data, read by a human out of the diagnostics
  bundle (`appconfig.json` travels verbatim; `UI/Onboarding/
  OnboardingCompletedVersionTests` pins that path and the two writers agreeing).
  The "new since you set up" prompt it also anticipates is a FUTURE feature and
  a product decision: do not invent a reader to make the field look used, and do
  not delete it as dead.
  `Perf/HeadlessAppFixture` seeds its isolated config with the flag ON — an
  empty config is a first run by definition and every headless boot would
  otherwise overlay the shell with it.
- **Existing-install detection** uses library/calibration and project/picker
  history. Window geometry and dock layout are written even when the first-run
  wizard is unfinished; they must not mark setup complete. Regression:
  `OnboardingGrandfatheringTests.ClosingAnUnfinishedFirstRun_DoesNotCountAsCompletingSetup`.
- **Re-run**: Settings ▸ General ▸ "Run setup again"
  (`SettingsViewModel.RunSetupAgainCommand` → `Open()`, which resets the step
  machine so a second run never inherits the first one's skips).
- **The role choice IS the startup setting.** "I'm a Charter" / "I'm a Player"
  writes `AppConfig.StartupScreen` (`Editor` / `PlayMode`) through
  `StartupRouting.ScreenForRole`; the shell's constructor reads it back through
  `StartupRouting.BootsIntoPlayMode` (source-linted by
  `UI/Onboarding/StartupScreenRoutingTests`); Settings ▸ General ▸ "Open at
  startup" changes it later; the old `AppConfig.StartInPlayMode` boolean is a
  serialized ALIAS over the same field, so pre-0.49 configs and the Play Mode
  menu's own "show at startup" toggle keep working.
- **It owns no new audio/library/tone/calibration/profile code.** The Audio and
  Input pages bind straight to the shared `AudioSettingsViewModel` and commit
  through ITS `SaveCommand` (the one writer of the live seat layout —
  `ApplyAudioConfiguration` is deliberately never called here, and the lint
  says so); "Play test click" is `IAudioPlaybackService.PlayClap` after that
  Save; the input meter is `ReadInputCaptureWindow` (the tuner's non-starving
  tap) polled by a `DispatcherTimer` that lives in the VIEW and runs only while
  the Input page shows (`OnboardingWizardView.IsMeterRunning`, measured by the
  smoke test); Tone goes through the pedalboard's own preset browser
  (`PedalboardViewModel.Browser.ApplyPreset`, the card-click path, resolved at
  pick time so the wizard never constructs the rig at startup) so an OPEN
  Amp & FX board re-syncs — until 0.61 it was a bare
  `IDspTonePresetService.ApplyToPlayer(0, …)` and the board on screen kept its
  old cards; with no board it still is that call; Latency
  opens the calibration wizard ON TOP through `ILatencyCalibrationPrompt.OpenAsync`
  — a hosted dialog since 2026-09-22, so the dialog host (above every legacy
  dialog, and first to answer Esc) is what puts it over this one — and re-reads its
  summary when that task completes, however the wizard closed; Profile is
  `IPlayerProfileService.Create` / `SwitchTo`.
- **The Input page asks WHICH JACK (2026-08-30).** It opened the device and lit
  a meter without asking which input the guitar is on, so a guitar on the
  second jack read as silence. `OnboardingInputJack` binds
  `Audio.InputJackOptions` / `Audio.SelectedInputJackIndex` — Settings' own
  rows — and shows on `Audio.AreInputChannelIndexesUsed` (ASIO, the engine
  driver). Smoke: `DialogConstructionUiPerfSmokeTests
  .OnboardingWizard_InputPage_OffersTheGuitarJack_OnAsio`.
- **The latency wizard's judging step says which channel it listens on, and
  lets the player change it** (`LatencyListeningChannel`, one row per OPEN
  channel labelled by jack, default = the guitar's seat; locked while
  measuring). Nothing back on that channel ⇒ it tries the next open one and
  the result names the channel it heard the click on. The mechanism and the
  reason it does not add a seat-layout writer are in
  `docs/agents/services/audio-engine.md`; smoke: `DialogConstructionUiPerfSmokeTests
  .LatencyWizard_JudgingStep_ShowsTheListeningChannelPicker`.
- **The first page says what this computer cannot run (#288).** The Welcome
  page hosts `Views/Onboarding/SystemCheckView` over the Presentation screen
  `Screens/Onboarding/SystemCheckViewModel` (`Contracts/Onboarding/
  ISystemCheckScreen`, a catalog screen so the gallery and the lints derive
  from it; the wizard takes the concrete singleton and exposes it as
  `SystemCheck`). `Open()` starts it ONCE per process (`Start()` is
  idempotent — the answers cannot change while the process runs), the two
  probes run on the pool and the answer is applied through `IUiDispatcher`:
  - **engine** — `Tone/Audio/Engine/JuceNativeEngineProbe`
    (`INativeEngineProbe`): the wrapper's OWN ABI gate without an engine or a
    device. `JuceWemDecoder.OpenMemory` runs `EnsureAbi` first, so four bytes
    that are not a .wem load the DLL and compare `DCA_ABI_VERSION`; the native
    side then refusing the bytes (`JuceAudioException`) is the READY answer.
    `JuceAbiMismatchException` → ABI mismatch with both numbers; a load failure
    (`DllNotFound`/`BadImageFormat`/`EntryPointNotFound`) → missing.
  - **graphics** — `IGpuRenderProbe` over the renderer's own device rule
    (`Rendering/Vulkan/Composed/ComposedDeviceProbe`, see
    `docs/agents/rendering/vulkan-device-lifetime.md`): no Vulkan, no device,
    or no device with the D3D11 interop.
  A capability that cannot run is drawn DISABLED — name dimmed and struck,
  red "Off" tag, the reason (what is off, why, what to do) under it, the
  panel in the warning tint; all fine is two quiet "Ready" rows. A probe that
  THROWS reads as off, never as ready. The page does not disable the later
  audio pages — they still go through the shared audio settings. Pinned by
  `Presentation/Onboarding/SystemCheckViewModelTests` (one case per state),
  `OnboardingWizardViewModelTests.Open_StartsTheSystemCheck_Once_AndThePageShowsItsAnswer`,
  the probes' pure halves (`Services/Audio/JuceNativeEngineProbeTests`,
  `Rendering/ComposedDeviceProbeTests`) and, headless,
  `Perf/OnboardingSystemCheckUiPerfSmokeTests` (every fake variant laid out
  and contained, and the real view model on the wizard's first page updated
  in place through the real UI dispatcher; in the release's de-DE / ja-JP
  layout pass).
- **ONE shared footer, ONE primary whose face changes** (`PrimaryCaption` /
  `PrimaryCommand`: Next until the last page, then Finish). Two `IsVisible`-
  toggled primaries would both show in the DataContext-less grammar layout and
  fail `AssertFooter`; and `Next`/`Finish`/`Open` are plain methods, not
  `[RelayCommand]`s, because a generated command nothing binds fails
  `RelayCommandReachabilityTests`.
- Pinned in the default gate by `UI/Onboarding/OnboardingStepMachineTests`,
  `OnboardingWizardViewModelTests` (over `Tests/Infrastructure/OnboardingWizardRig`
  — the REAL audio/latency view models over the fake engine) and the two lints
  above; in the headless suite by `Perf/DialogConstructionUiPerfSmokeTests
  .OnboardingWizard_EveryPageMeasures_AndTheMeterClockRunsOnlyOnInput` plus the
  shared grammar cases (it is in `Converted`).

## Help ▸ Keyboard Shortcuts

- **`KeyboardShortcutsDialogView` is READ-ONLY and GENERATED** — its rows come
  from `CommandCatalog.Build` on every open (localized titles + categories,
  shortcuts as LOOKUPS into the live `KeybindConfig`), so a rebind shows
  without a restart and nothing hand-written can drift.
  `CommandPaletteCoverageTests` failing any rebindable action the catalog
  cannot find is what makes the dialog complete by the same guard. Since wave 2
  it is a hosted dialog (`IKeyboardShortcutsScreen`); the catalog arrives
  through the `Contracts/Shell/IShortcutCatalog` seam the shell implements
  (`CommandCatalog.Build` needs the whole `MainWindowViewModel`), and
  `ViewModels/CommandPalette/ShortcutEntries` keeps only BOUND chords, printed
  localized. Filtering is synchronous by design (~100
  plain rows, not realized containers). Rebinding stays in Settings ▸ Keybinds
  — the footer meta line says so. Pinned by
  `Presentation/Dialogs/KeyboardShortcutsViewModelTests`.

