# The UI rewrite — target architecture

**Status: approved by the owner on 2026-09-19, with every recommended default
in § 9 accepted.** Agents execute it screen by screen.
It fixes the shape of the new UI layer, the rules that hold it there and the
order the old one is replaced in. Names marked **(proposed)** do not exist yet;
everything else is in the tree today. Read the root `AGENTS.md`, the UI router
and [MAP.md](../MAP.md) first — this file assumes them.

Baseline it is written against (measured 2026-09-19, `main` @ `118b421c7`):
`DotCharter.UI` is 129.5k C# lines (37.5k of them comment lines, 29 %) and
46.7k XAML lines; 27 constructors take ≥ 10 dependencies (`AppDialogService`
36, `MainWindowViewModel` 33, `PlayModeViewModel` 22); 28 UI files do
`File`/`Directory` I/O and 9 start processes; 34 view-model files `using
Avalonia`; 192 of the 269 test files under `DotCharter.Tests/UI/` are source-text
lints. The gallery-first redesign has 10 contracts, 10 fakes and 9 views under
`ViewModels/PlayMode/Contracts/`, `DesignData/PlayMode/` and
`Views/PlayMode/Redesign/`, all bound to nothing but `INotifyPropertyChanged`
and `ICommand`. That shape is the seed of this design, not something it replaces.
Wave 0 moved the contracts to `DotCharter.Presentation/Contracts/PlayMode/` and
the fakes to `DotCharter.Presentation.Fakes/PlayMode/`; the views stayed.

## 1. Goals and non-goals

**Clean** — a screen's view model depends on Services abstractions only; the UI
assemblies contain no file, process, network or database access (mechanically:
zero banned-API diagnostics outside the composition root). **Split** — no type
above 600 lines, no file above 400 lines of C# or 600 of XAML, no constructor
above 7 dependencies, one screen per folder. **Testable** — every screen has a
fake with named variants, a view-model unit test against fake services in the
default gate, and a golden screenshot compared on every headless run; a view
change that moves pixels the owner did not approve fails.

Non-goals: no new visual language (the Fire tokens and `Styles/ColorTokens.axaml`
stay the theme); no changes to `DotCharter.Core`, `Analysis`, `Rendering` or
the JUCE ABI; the custom-drawn controls (`Controls/ChartRenderer`,
`Controls/Preview3DComposedCanvas`, `Controls/Sheet/SheetMusicView`, the
`Controls/Dsp/` and `Controls/Fire/` families) are kept and re-hosted, not
rewritten; no HTML prototypes; nothing for co-op, LAN, practice plans or
History/Trends (tag `pre-cut-2026-09`).

## 2. Target structure

| Project | Assembly | Holds | May reference |
|---|---|---|---|
| `DotCharter.Presentation` | `DotCharter.Presentation` | `Contracts/<Area>/I<Screen>Screen`, `Screens/<Area>/<Screen>ViewModel` + item VMs, `Navigation/` (`INavigator`, `ScreenRequest`), `Dialogs/` (`IDialogs`), `Shared/` (area-state objects, `IThemeApplier`, formatting helpers), one `<Area>Module.Register(IServiceCollection)` per area | Core, Localization, Services, CommunityToolkit.Mvvm. **No Avalonia, no Dock, no SkiaSharp, no JuceSharp** |
| `DotCharter.Presentation.Fakes` | `DotCharter.Presentation.Fakes` | `Fake<Screen>Screen` per contract, each with a static `Variants` list (today's `DesignData/PlayMode/*`) | Presentation only |
| `DotCharter.UI` (exists) | `DotCharter` | `Views/<Area>/<Screen>View` (ctor-only code-behind), `Controls/`, `Styles/`, `Platform/` adapters (`AvaloniaUiDispatcher`, `DesktopFileStorageService`, `ThemeService`), `Views/Shell/ScreenCatalog`, `Views/Shell/ScreenHost` + `DialogHost` (§ 3.4), the composition root `App*.cs` | everything above, Avalonia, Dock, Rendering, JuceSharp. **Never `Presentation.Fakes`** |
| `DotCharter.UI.Gallery` (exists) | `DotCharter.Gallery` | pages DERIVED from `ScreenCatalog` × `Variants` (the hand-written `Pages/<Area>/<Area>Pages.cs` lists shrink to overrides) | UI, Presentation, Presentation.Fakes |
| `DotCharter.Tests` (exists) | — | unit tests over Presentation with fake Services; headless goldens over the catalog | everything |

`AssemblyLayeringTests` gets two rows — `("DotCharter.Presentation", [Core,
Localization, Services])` and `("DotCharter.Presentation.Fakes",
[Presentation, Core, Localization, Services])` — plus two new facts: the
Presentation assemblies reference no assembly whose name starts with `Avalonia`,
`Dock`, `SkiaSharp` or `JuceSharp` (the ABI stays behind Services), and the UI
assembly does not reference
`DotCharter.Presentation.Fakes`. Reflection over `GetReferencedAssemblies`, same
as the existing rows, so a stray `using Avalonia` in a view model is a red gate,
not a review comment. *Alternative:* keep view models in `DotCharter.UI` and
lint `using Avalonia` by text — weaker (a fully qualified name slips through)
and it leaves the 34 offenders looking normal.

Why the fakes are their own project: a fake in the product assembly is one
`DataContext` typo away from shipping, and "UI never references Fakes" is one
layering row.

### 2.1 What moves OUT of `DotCharter.UI` into Services first

**Done in wave 0 (2026-09-19).** `DotCharter.UI/Coordinators/` and
`DotCharter.UI/Sheet/` no longer exist. The plan said only `ProjectSessionService`
and `CrashRecoveryService` touched an Avalonia type; that held, but most of the
coordinators also named the editor and transport VIEW MODELS, so the move needed
seams as well as the `IUiDispatcher` swap. Each seam is implemented by the one
existing view-model instance and registered as a forwarding singleton:

- `Services/Editing/IChartEditorSurface` — what the coordinators read and drive
  on `ChartEditorViewModel` (beats, placed notes, lyrics, selection,
  `ApplyProject`, `RequestRedraw`).
- `Services/Audio/Coordination/IEditorTransport` — the transport's toggles, BPM
  and seek/pause/sync verbs (`TransportViewModel`, explicit `Pause()`).
- `Services/Diagnostics/IMessagePrompt` (+ `MessageDialogKind`) — the app-wide
  message box (`MessageDialogViewModel`), for the session's failed-save and
  replace-audio prompts.
- `Services/Audio/Coordination/IGameplayToneHud` — the tone coordinator's HUD
  channel; `Platform/RenderingGameplayToneHud` forwards to
  `DotCharter.Rendering.Data.GameplayToneHud`, which Services may not reference.

| Moved | Now at (Services) | Note |
|---|---|---|
| `ProjectSessionService` (`IProjectSessionService` in its own file) | `Session/` | every `Dispatcher.UIThread` call → `IUiDispatcher` (same priorities: `Post` = Background, `PostNormal` = Normal); the auto-backup `DispatcherTimer` → `IUiDispatcher.SchedulePeriodic` (Background, as before). `IUiDispatcher` is now a REQUIRED constructor argument; tests pass `Tests/Infrastructure/TestUiDispatcher` |
| `ChartMaintenanceService` + 5 partials (`IChartMaintenanceService`) | `Editing/Maintenance/` | the chord-template undo actions (`ChordTemplateActions.cs`, from `ViewModels/Panels/Actions/`) moved with it to `State/` |
| `CrashRecoveryService` | `Infrastructure/Recovery/` | its snapshot tick → `IUiDispatcher.SchedulePeriodic`; the Background priority is pinned on `AvaloniaUiDispatcher` (`UI/Platform/AvaloniaUiDispatcherTests`) |
| `GlobalEditingCoordinator` (+ `IGlobalEditingCoordinator`), `EditingClipboardCoordinator`, `HandShapeFromSelectionGenerator`, `HandShapeSplitter`, `HistoryStepGuard`, `TempoScaleCoordinator`, `DraftArrangementFactory`, `AutoBackupGate` | `Editing/` | needed the track context too: `TrackEditorContext`, `ITrackContextRegistry`, `TrackContextRegistry`, `RibbonVisibility` and `EditorFocusArea` moved to `Editing/Tracks/`; `RibbonLayout` (renderer geometry) stays in UI and `BuildLayout()` is its extension method |
| `AudioPlaybackCoordinator`, `RsTonePlaybackCoordinator`, `ChartTonePreviewController` (+ `IGuitarMonitorMixer`), `StemWaveformCoordinator` | `Audio/Coordination/` | the 250 Hz → ≤ 60 Hz coalescing is unchanged: the same 16 ms throttle and single queued publish, posted with `IUiDispatcher.PostNormal` (the Normal priority `Dispatcher.UIThread.Post` used) |
| `Sheet/AlphaTexExporter`, `SheetMusicRenderer`, `SheetPdfExporter` (+ `SheetPdfPagination`, `SheetRenderKey`) | `Export/Sheet/` | the AlphaSkia package moved to Services beside AlphaTab; the UI no longer references either directly. `Controls/Sheet/SheetMusicView` stays in UI |
| `Theming/CommunityThemePacks` (the file half) | `Theming/CommunityThemePackFiles` | lists and reads the pack files; parsing stays in the UI because a theme colour is `Avalonia.Media.Color`. **No `IThemeApplier` yet** — `Theming/IThemeService` is still the UI's resource writer and its API is Avalonia-typed; the applier lands with Presentation's `Shared/`, where it needs a colour type of its own |

Stays in UI as a platform adapter: `Platform/DesktopFileStorageService.cs`
(implements `DotCharter.Tone/Infrastructure/Storage/IFileStorageService.cs`
— the pattern every adapter below copies), `ThemeService`, the `RigArtworkStore`
in `Controls/Fire/RigArtworkImage.cs` (bitmaps), `AvaloniaUiDispatcher`.

### 2.2 Small interfaces the UI needs instead of reaching out

**Landed in wave 0 (2026-09-19)**, all in Services, each implemented once in
`DotCharter.UI/Platform/` or in Services itself and registered in `App.axaml.cs`
beside `IFileStorageService`. A view model takes each as an optional constructor
argument; where a test host passes none it falls back to the Services disk
implementation (or does nothing, for the shell and clipboard).

- `Infrastructure/Shell/IShellLauncher` — `OpenUrl` (`OpenFile` went on 2026-09-24: an extension with no default app raised Windows' "choose an app" box, so documents show in the app),
  `OpenFolder(path, create)`, `RevealInFileManager`; adapter
  `Platform/DesktopShellLauncher` (the start infos are pure and pinned by
  `UI/Platform/DesktopShellLauncherTests`). All nine `Process.Start` sites went:
  `HelpMenuViewModel` (usage report, bundled docs), `ProjectMenuViewModel` (reveal),
  `UpdatesSettingsViewModel` (since replaced by `Presentation/Screens/Settings/GitHubSignInViewModel`), `NamCaptureBrowserViewModel` (through
  `PedalboardViewModel`), `DiagnosticsViewModel`, `AiSettingsViewModel.MatchModel`,
  `RocksmithExportDialogViewModel`, `MarkdownView` and `AboutWindow`.
- `Infrastructure/Shell/ISystemClipboard` — text in/out; adapter
  `Platform/AvaloniaSystemClipboard`. Both halves moved to `DotCharter.Tone` /
  `DotCharter.Tone.UI` on 2026-09-28 (same namespaces) so the rig can use them:
  the GitHub device code, the text documents and the AI designer's copy & paste
  route.
- `Infrastructure/Storage/IArtworkSource` + `ArtworkKey` — cover art and avatars
  as a `Stream` by key (`File(path)`, `ProfileAvatar(id)`), disk implementation
  `ArtworkSource` in Services; it also stages the temp PNG an avatar import is
  re-encoded into. `SongCoverImage` and `ProfileAvatarImage` now only DECODE.
  **Deviation:** view models still expose `Bitmap`s, and there is no cached
  key→bitmap converter yet — both come with the Song Select / profile screens.
  Rig artwork is embedded assets, not files, and was left alone.
- **Deviation:** `IDiagnosticsBundleWriter` already existed as
  `Diagnostics/IDiagnosticsBundleExporter`; what was left in `DiagnosticsViewModel`
  was a log-file lookup (now `LogTailReader.ResolveActiveLogFile`), the log-folder
  open (`IShellLauncher`) and a working-set read (`Environment.WorkingSet`).
- **Deviation:** `ILicenseText` became `Infrastructure/Shell/IBundledDocuments`
  (`BundledDocuments`): the About window and the Help menu OPEN a shipped file in
  its default app rather than show its text, and both needed the same
  beside-the-exe-or-up-the-tree lookup.
- `Infrastructure/Storage/IExportTargetWriter` (`ExportTargetWriter`) — exists /
  copy / read / write / create-with-folder / find for the export and import
  dialogs (Rocksmith export, EOF, MIDI, chart-from-audio, new project,
  batch lint, the tone designer), the tone-file and lyrics imports, the highway
  screenshot and the recent-project check. The dialogs keep the choice of path.
- `Infrastructure/Input/IKeyGestureSource` + `ScreenKey` — keys without a UI
  framework type; adapter `Platform/AvaloniaKeyGestureSource` (`Map`, `Publish`).
  `GetReadyKeys` now speaks `ScreenKey`, so its view-model file no longer uses
  `Avalonia.Input`, and the Get Ready keys are routed through it: `PlayModeView`
  publishes into its own `AvaloniaKeyGestureSource` and `PlayModeViewModel`
  subscribes (`PlayModeViewModel.Keys.cs`, `AttachKeySource`/`DetachKeySource`),
  so the VM, not the view, decides what ENTER and T do. One source per hosting
  view, so it is not a DI singleton. **Left for the Play Mode wave:** the
  gameplay/pause keys and Song Select's keyboard flow still live in
  `PlayModeView`'s handlers.

**The banned-API switch is on for the whole UI with no per-file exemption**:
`.editorconfig` turns RS0030 off for the composition root and the two
`Platform/` folders only (`PresentationSourceRulesTests` pins the section list).
A view model that needs the disk takes a Services seam: stem discovery is
`Services/Audio/StemSeparation/StemFolderProbe` (beside `StemFolderBackup`), the
tone designer's bundled DI clip reads through `Ai/Match/AudioClipLoader`
(`AudioClipLoading.TryLoad`), and Play Mode's existence checks (own charts,
listen-back takes, the song a take borrowed the transport from) go through the
injected `IExportTargetWriter.FileExists`, as `ProjectMenuViewModel`'s do.

### 2.3 `JuceAudioPlaybackService` — tidy in place, not rewritten

295 KB over 17 partials stays one class; what changes is what view models see.
Presentation depends on focused interfaces the class implements, each
registered as a forwarding registration to the one singleton.
`IAudioPlaybackService` remains for Services-internal callers. A Presentation
type naming `JuceAudioPlaybackService` or `IAudioPlaybackService` fails a
banned-API diagnostic.

**Drafted in wave 0 (2026-09-19), PENDING OWNER APPROVAL before wave 3 builds on
them** — the member table, the threading notes and the reasons are in
[services/audio-engine.md](../services/audio-engine.md), "Focused interfaces for
view models". Five seams in `Services/Audio/Abstractions/`, which
`IAudioPlaybackService` now extends: `IPlaybackTransport`, `IAudioDeviceControl`,
`IStemMixer`, `IVstChainHost`, `ILiveInputCapture`. Four are registered and
already have a consumer (`ChartRenderer` + `Preview3DRenderDriver`,
`LiveInputChannelViewModel`, the stem mixer, `Audio/Tuning/LiveTuner`);
`IVstChainHost` is not registered until something other than the editor
transport needs it. **Deviations from the draft names:** no `IPlaybackHealth`
(device-lost state is in `IAudioDeviceControl` — both of its readers also read
the device), no `IMidiInput` (the service has no MIDI input; the rig's MIDI
input seam is the existing `Audio/Midi/IRigMidiInputSource`), and a new
`ILiveInputCapture` for the capture ring seven callers share. **The banned-API
rule needed a second half:** RS0030 does not fire on a field or parameter
TYPED as the service, nor on a call to a member it inherits from a seam, so
`UI/AudioSeamRegistrationTests` also fails if any Presentation type holds the
whole service.

## 3. Screen anatomy

Every screen — a Play Mode screen, a settings tab, a dialog, a shell popover, an
editor pane — is the same five things, in one folder per layer:

1. **Contract** — `Contracts/<Area>/I<Screen>Screen : INotifyPropertyChanged`.
   Properties are already-formatted strings, booleans, numbers, `IReadOnlyList<T>`
   of item contracts, and `ICommand` verbs. No Avalonia type, no domain type
   the view would have to format (see `IResultsScreen.cs`: `GradeLabel`,
   `HasSections`, `RetryCommand`). A dialog contract adds `Task<TResult> Result`.
2. **Fake** — `Fake<Screen>Screen` in `Presentation.Fakes` with
   `static IReadOnlyList<FakeVariant<TContract>> Variants`, names fixed by lint:
   `Default`, `Empty`, `LongText` always; `Full` when the contract has a list
   (2,160 songs, 63 NAM captures, 40-character names); `Busy`/`Error` when it has
   a progress or failure state. `LongText` carries German and Japanese strings.
3. **View model** — `Screens/<Area>/<Screen>ViewModel : ObservableObject,
   I<Screen>Screen`. At most 7 constructor parameters, `IUiDispatcher`,
   `INavigator` and `IDialogs` included. It never references another screen's view
   model; shared state goes through Services (`ProjectState`, `ISelectionService`,
   `IPlaybackTransport`) or through ONE area-state object (`PlayModeSession`
   **(proposed)**: selected song, arrangement, difficulty, the run in flight —
   observable properties, no commands, no services). Optional
   `IScreenLifecycle { OnShown(); OnHidden(); }` — timers start and provably stop
   there, never in the constructor.
4. **View** — `Views/<Area>/<Screen>View.axaml`, `x:DataType` = the CONTRACT,
   compiled bindings, code-behind is `InitializeComponent()` only (as
   `Views/PlayMode/Redesign/ResultsScreenView.axaml` is). File name unique across
   `Views/**` (`ViewFileNamesAreUniqueTests`). Styles come from `Styles/` and
   `DynamicResource` tokens only; a screen-local style file is allowed and must be
   `StyleInclude`d by the view itself.
5. **Registration** — one line in `ScreenCatalog` (UI): `Screen<IResultsScreen>(
   () => new ResultsScreenView())`; one line in the area's module (Presentation):
   `services.AddScoped<IResultsScreen, ResultsScreenViewModel>()` (one per host
   scope — § 3.1). The gallery
   page, the smoke test, the golden and the contract-conformance test are all
   DERIVED from those two lines — nothing else to register.

`App.ConfigureServices` keeps its signature and calls each `<Area>Module.Register`,
so `ServiceRegistrationReachabilityTests` and `AppServiceGraphTests` keep
enumerating the real graph. `ViewLocator` is not used by new screens (name-based
mapping cannot cross assemblies and cannot map an interface); it is deleted with
the last old pane.

### 3.1 Navigation

Today: `PlayModeScreen` enum + eight `Is*Screen` predicates in
`ViewModels/PlayMode/PlayModeViewModel.Screens.cs` gate the screens that
`PlayModeView.axaml` declares (each its own view under `Views/PlayMode/Screens/`
since #294, which took the host from 421 KB to ~12 KB); the shell does the same with
`MainWindowViewModel.SelectedMainTabIndex` over four hosts in `Views/MainWindow.axaml`
(`MainSplitGrid`, `PlayModeHost`, `SongBrowserHost`, `SettingsHost`).

Target: **`INavigator` (Presentation) + `ScreenHost` (UI)**.

- `INavigator.Show<TContract>(Action<TContract>? init = null)`, `Back()`,
  `Current`, `CanGoBack`, a bounded stack per host (`Start → Menu → SongSelect →
  GetReady → Countdown → Playing → Paused/Failed → Results` is the Play Mode
  host's stack; the shell host holds Editor / Song browser / Settings / Play Mode).
- `ScreenHost` is a `Panel` that keeps ONE realized child per contract it has
  shown or prewarmed and flips `IsVisible` — the same ~0.1 ms re-show today's
  prewarm relies on (`Views/Shell/HiddenSubtreePrewarmer.cs`). It creates a view
  from `ScreenCatalog` on first need, resolves the view model from its own
  `IServiceScope`, and disposes both with the host.
- `ScreenHost` implements `IPrewarmableSurfaces`
  (`Views/Shell/IPrewarmableSurfaces.cs`) and returns its hidden children, so
  `Views/Shell/MainWindowPrewarm.cs` and the headless sweep keep working
  unchanged. After first paint it pre-creates the flow-order neighbours of the
  current screen at `DispatcherPriority.Background`, one per dispatcher turn.
  Realization never writes `IsVisible` and never runs a command.
- View-model lifetime: **cached per host** (created on first `Show`, kept while the
  host lives, `OnHidden` when covered). *Alternative:* per-visit transients —
  cleaner, but Song Select would reload 2,160 rows on every return.
- Esc: the shell asks `IDialogs.CloseTop()` first, then the focused host's
  `Back()`; `MainWindowViewModel.HandleEscapeKey` becomes that two-liner.
- Budget check: the switch itself is a visibility flip; the golden harness logs
  first-layout time per screen so a screen whose realization exceeds 150 ms is
  visible in the run output (logged, never asserted — root `AGENTS.md`).

### 3.2 Dialogs

`AppDialogService` holds 35 singleton dialog view models and
`Views/Shell/DialogContainerView.axaml.cs` a `RegisterLazy` per view. Target:
`IDialogs.ShowAsync<TContract, TResult>(Action<TContract>? init)` in Presentation;
`DialogHost` (UI) resolves the dialog view model from a scope per open, builds
the view from `ScreenCatalog`, shows it on the shared scrim
(`Border.FireOverlayScrim`, translucent — the scrim rule stands), awaits
`Result`, disposes the scope. `HasOpenDialog` derives from the host's stack.
`MessageDialogViewModel` becomes `IDialogs.MessageAsync(...)` over one message
contract. Dialog views are prewarmed by `DialogHost` through `IPrewarmableSurfaces`
with a null `DataContext` — template and style application is the cost, and that
is cached; the first real open pays only binding.

### 3.3 Cross-screen state, without god objects

| State | Owner (exists unless marked) | Who may read/write |
|---|---|---|
| open project, timeline anchor, `CurrentTimeMs` | `Services/State/ProjectState` | any screen; writes through `IProjectSessionService` / editing services |
| selection | `Services/Editing/Selection/ISelectionService` | editor screens + renderers |
| playback position (≤ 60 Hz) | `AudioPlaybackCoordinator` (moved to Services) | transport, HUD, sheet — subscribe, never poll `PositionChanged` |
| the run in flight, selected song/arrangement | `PlayModeSession` **(proposed)**, Presentation `Shared/` | Play Mode screens only |
| active profile | `Services/Gameplay/Profiles/` | any screen |
| settings | `IConfigurationService` (`AppConfig`) | settings screens write; others read |

Rule: a screen view model holds no reference to another screen's view model and
no `event` another screen subscribes to; it publishes through the owner above.

### 3.4 As built in wave 0 — where it differs from §§ 3.1–3.2

Nothing in the app hosts these yet; the first screen moves in wave 1.

- **Presentation.** `Navigation/`: `INavigator`, `ScreenRequest` (contract +
  one-shot `Init`), `Navigator` over a pure `BackStack` (capacity 16; showing a
  contract already on the stack UNWINDS to it, so Results → Song Select does not
  leave Results under Song Select), `IScreenPresenter` (the host side),
  `IScreenLifecycle`, and `ScreenPresenterSlot`, the scoped seam a host plugs
  itself into right after creating its scope. `Dialogs/`: `IDialogs`
  (`HasOpenDialog`, `ShowAsync<TContract, TResult>` returning `TResult?`, the
  default when closed from outside, `MessageAsync(title, message, isConfirmation)`
  returning `bool`, `CloseTop`), `IDialogScreen<TResult>` (the `Task<TResult>
  Result` a dialog contract adds), a pure `DialogStack`, and `DialogsSlot`, the
  app-wide `IDialogs` that forwards to whichever `DialogHost` attached itself.
  The one message contract is `Contracts/Dialogs/IMessagePromptScreen` with
  `Screens/Dialogs/MessagePromptViewModel`. `NavigationModule` and
  `DialogsModule` register them. Tests: `Presentation/NavigatorTests`,
  `Presentation/DialogStackTests`.
- **UI** (`Views/Shell/`). `ScreenCatalog.Default` is the one-line-per-screen
  list, `Screen<IResultsScreen, ResultsScreenView>(PlayMode, "Results")`: a
  generic TYPE argument rather than the `() => new ResultsScreenView()` factory
  above, because the lints need the view type without constructing it. It holds
  the nine redesign views and `Dialog<IMessagePromptScreen, MessagePromptView>`
  (`Views/Prompts/`), in flow order (`FlowOf(area)`), and derives each golden's
  path. `ScreenHost` and `DialogHost` are `Panel`s built in code with
  `(IServiceScopeFactory, ScreenCatalog[, DialogsSlot])`; there is no
  parameterless constructor yet, so neither can be declared in XAML.
  `ScreenHost` exposes its `Navigator`, takes the neighbour order as `Flow`, and
  handles Esc itself (`IDialogs.CloseTop()`, else `Back()`). `DialogHost` keeps
  closed dialog views hidden with a null `DataContext` for reuse, and
  `DialogHost.Preview` frames a dialog view for the gallery and the goldens.
  `ScreenShellModule.Register` is the one line in `App.ConfigureServices`.
  `MainWindowPrewarm` finds both hosts by a visual-tree walk (they can sit
  anywhere) and asks them for their hidden children. Headless proof:
  `Perf/ScreenHostUiPerfSmokeTests`.
- **Contract conformance** (`Presentation/ContractConformanceTests`, § 6) is
  observed, not declared: every command that can run with no parameter is
  executed, and a contract property whose value moved without a
  `PropertyChanged` fails; the immutable fakes have nothing to notify until a
  command runs. It covers every fake variant and every catalog contract the
  Presentation modules register (today the message prompt). Its first run found
  `FakeSongSelectScreen` clearing its filter chips without notifying.
- **No variant name is pinned.** `Full` is required on every fake whose contract
  has a list (the countdown's pips count as one), `Busy` / `Error` when it has
  `IsBusy`/`IsLoading` or `HasError`/`IsError` — checked per catalog entry by
  `ScreenCatalogTests` and per fake by `Presentation/FakeVariantsTests`, with no
  exemption list. The one shrink-only pin left is the members no view binds yet
  (`ContractBindingLintTests.KnownUnbound`, per view).
- `UI/ServiceRegistrationReachabilityTests` now also scans
  `DotCharter.Presentation` and skips `*Module.cs` the way it skips `App*.cs`:
  a registration is not a use.

### 3.5 As built in wave 1 step A — the Settings shell and its first two tabs

The first live surface on the architecture. What it settled, so the next
screens copy it rather than re-decide it:

- **The strangler seam is the OLD host's content.** `MainWindowPrewarm`
  fills `SettingsHost` with `Views/Settings/SettingsSurface.Create(services)`:
  a root `ScreenHost` that shows `ISettingsShellScreen`. Until the last old tab
  migrated it also placed the old page's tabs into a legacy slot in the shell;
  both went with Appearance (2026-09-22). Nothing else in the shell changed;
  `MainWindowViewModel` no longer owns a settings dialog.
- **A shell screen hosts its tabs in a `Views/Shell/ScreenTabHost`** — a
  `Panel` declarable in XAML (`Screens="{Binding TabScreens}"`
  `Current="{Binding CurrentScreen}"`), one realized child per contract with an
  `IsVisible` flip and no back stack. It BORROWS the scope of the `ScreenHost`
  above it when it attaches, so a tab view model is the same instance the
  shell's view model receives (`IEnumerable<ISettingsTab>` from the same scope);
  with no host above it — the gallery, the goldens — it is inert. It is an
  `IPrewarmableSurfaces` of its own, and `MainWindowPrewarm.PrewarmableSurfaceOwners`
  lists it AFTER the settings `ScreenHost`, found
  through the logical tree — one branch per implementor, which is what
  `HiddenSubtreePrewarmerTests` counts.
- **Not-yet-migrated tabs were an adapter, not a compromise in the shell.**
  An `ISettingsTab` per old tab and one `ISettingsChangeSource` over the old
  reflection tracker sat on top of the old page's per-tab verbs; each migration
  deleted one line of it and Appearance, the last, deleted the folder
  (2026-09-22). **Wave 1 is done:** every Settings tab is a screen. The shell
  view model never knew the adapter existed.
- **The area-state object is `Shared/SettingsSession`** (`IsOpen`,
  `RequestReload`): the app shell sets `IsOpen` as its Settings tab is entered
  or left, the settings shell loads or reverts every tab in answer (posted, so
  the switch paints first), and Save / Cancel set it false so the app shell
  leaves the tab. When the top-level `ScreenHost` arrives in wave 5, `IsOpen`
  becomes `IScreenLifecycle` and the session shrinks to the reload request.
- **Deviations, each deliberate:** `SettingsShellView.axaml.cs` has one handler
  past the constructor (Ctrl+F puts focus in the search box; focus is a view
  concern). The Updates contract carries a boolean per row for the search
  filter; a tab with more rows makes them items with `IsVisible`, as Keybinds
  does. The update flow (`Services/Update/UpdateCheckFlow`, `IUpdatePrompt`,
  `IApplicationCloser`, `IKeybindDispatch`) is new Services surface, extracted
  so the two tab view models fit in 7 dependencies; `GitHubSignInViewModel` is a
  same-folder part view model for the same reason. The rows of the keybind
  table are a catalogue (`KeybindRowCatalog`) rather than 140 lines of markup.
  `ISettingsTab` / `ISettingsChangeSource` live in `Screens/Settings/`, not
  `Contracts/`: they are seams between screens, not what a view binds.

### 3.6 As built in wave 2, slices 1 and 2 — fifteen dialogs on `DialogHost`

Moved (2026-09-22): Unsaved changes, Set BPM, Tempo-map operation, Update
available, Fret-hand position, Generate levels, Beat. **Left:** the generic
message dialog — `IMessagePrompt` carries severity, details, any-thread queueing
and pre-window availability that `IDialogs.MessageAsync` does not (reasons and
the plan in [dialogs/hosted-dialogs.md](dialogs/hosted-dialogs.md)).

- **The strangler seam is `DialogContainerView`'s panel.** It builds ONE
  `DialogHost` from `App.Services` and places it above every legacy dialog and
  under the eager `MessageDialogView`; the host inherits the one
  `DialogStyles.axaml` include. The host attaches to `DialogsSlot` while it is in
  a visual tree (not in its constructor), so a window built but never shown
  never answers for the app.
- **One shell answer for both halves.** `IAppDialogService.HasOpenDialog` is
  legacy OR hosted (re-raised on the new `IDialogs.Changed`), and
  `CloseTopDialog()` is the Esc order: legacy message, host's top, first open
  legacy dialog. `DialogChange.Id` keeps the legacy usage-statistics id.
- **Three small additions to § 3.4's `Dialogs/`:** `ICancelableDialog`
  (`TryCancel` — the dialog's say in Esc; false refuses, as Generate levels does
  mid-run), `IDialogRequest<TRequest, TContract, TResult>` + `IDialogSetup<TRequest>`
  (a typed request record configures the dialog it opens, and
  `dialogs.ShowAsync(new FhpRequest(…))` infers the contract and result), and
  `DialogRequests.Observe` (a dialog nobody awaits logs its fault).
- **The pattern to copy**, one dialog at a time: contract (+ request) →
  `DialogViewModel<TResult>` subclass, scoped, one instance per open → fake with a
  factory per variant → the existing view rebound (`Grid.DialogFrame` root, no
  `IsVisible` / `ZIndex`) → one `ScreenCatalog` line → one `DialogsModule` line →
  callers on `IDialogs` → delete the legacy VM, its `AppDialogService` slot, its
  `RegisterLazy` and its singleton. The table is in
  [dialogs/hosted-dialogs.md](dialogs/hosted-dialogs.md).
- **Deviations:** `FhpDialogView.axaml.cs` keeps its digits-only `KeyDown`
  filter (an input concern of the view). A hosted dialog's backdrop is the
  host's `FireScrimFill`, a shade off the legacy `OverlayScrim`. `UpdatePrompt`
  (the `IUpdatePrompt` implementation, which remembers the last find) is a
  Presentation singleton registered in `App.ViewModelServices`, not in the
  module, because it needs the app's logger.

**Slice 2 (2026-09-22):** Paste special, Arrangement sync, Keyboard shortcuts,
Vocal track settings, Chart from audio, and the three file importers (Rocksmith
XML, MIDI, Editor on Fire). Two shapes the first slice did not have:

- **Work before a dialog opens lives in a LAUNCHER** — `EofImportLauncher`,
  `MidiImportLauncher`, `RsXmlImportLauncher` in `Screens/Dialogs/`, singletons
  the shell holds: picker (or dropped path) → read off the UI thread under a
  status-bar pill → `ShowAsync(request)`. Work AFTER one closes goes to Services
  on the dialog's ANSWER (`EofImportSelection` → `Import/Eof/EofProjectCreator`),
  never to the closed dialog's scope.
- **State kept across opens** moves to the `DialogMemory` singleton, since a
  scoped view model starts clean every time.
- Seams: `IEditorTransport.GridDivisor`, `IChartEditorSurface.ExecuteSeek`,
  `Contracts/Shell/IShortcutCatalog` (shell-implemented) and
  `Services/Import/Audio/IChartFromAudioAnalyzer` (the Analysis assembly stays
  out of Presentation). Detail in [dialogs/hosted-dialogs.md](dialogs/hosted-dialogs.md).

## 4. Enforced rules

Every rule has a mechanism that fails a build or the default gate; a rule with
only a sentence behind it is not on this list. Existing lints named here stay.

| Rule | Limit | Mechanism |
|---|---|---|
| Presentation has no UI framework and no ABI | 0 references to `Avalonia*`, `Dock*`, `SkiaSharp`, `JuceSharp*` | `AssemblyLayeringTests` (new rows + facts) |
| No I/O, process, network, SQLite in UI or Presentation | 0 diagnostics outside `App*.cs`, `Program.cs`, `Platform/` | `Microsoft.CodeAnalysis.BannedApiAnalyzers` on both projects (on for the two Presentation projects; the UI flips on once its I/O sites have moved — see § 4.1) with a `BannedSymbols.txt` (`System.IO.File`, `System.IO.Directory`, `System.Diagnostics.Process`, `Microsoft.Data.Sqlite`, `System.Net.Http.HttpClient`, `System.Environment.GetFolderPath`); `TreatWarningsAsErrors` already makes RS0030 an error (`Directory.Build.props`). `Presentation/PresentationSourceRulesTests` counts `#pragma warning disable RS0030` outside the three exempt locations and requires zero |
| Constructor size | ≤ 7 parameters | reflection over every type in Presentation (`Presentation/PresentationTypeRulesTests`), and over `DotCharter.UI` types under `Views/` and `Platform/` **(proposed)** — new code only, so the 27 existing ≥ 10 ctors (view models this plan replaces, coordinators that move to Services) need no allow-list |
| Type / file size | ≤ 400 C# lines per file, ≤ 600 per type, ≤ 2 files per type; ≤ 600 lines per `.axaml` | source lint over both Presentation projects (`Presentation/PresentationSourceRulesTests`) and new `Views/` folders **(proposed)**. Sizes come from the redesign views shipped so far (150–620 lines) |
| No narrative-history comments | no comment line matching a date (`20\d\d-\d\d`), a version (`0\.\d\d`) or `until`/`used to`/`no longer`/`shipped`; comment lines ≤ 20 % of a file | source lint over both Presentation projects (`Presentation/PresentationSourceRulesTests`; `Contracts/` exempt from the 20 % only — § 4.1) and new `Views/` **(proposed)**. History lives in git and in `docs/agents/**` |
| A screen VM references no other screen VM | 0 | reflection: constructor parameter and field types of `*ViewModel` in Presentation must not be another `*ViewModel` outside its own folder or `Shared/` (`Presentation/PresentationTypeRulesTests`) |
| Every contract has exactly one view and one fake with the required variants | 1 / 1 / names present | reflection over `ScreenCatalog` × `Presentation.Fakes` (`Presentation/ScreenCatalogTests`); a contract in the catalog with no fake, or a fake without `Default`, `Empty`, `LongText` (+ `Full` when a list property exists), fails the default gate |
| Every contract member is bound by its view | 100 % | XAML lint (`Presentation/ContractBindingLintTests`): each property/command on the contract appears in a `{Binding}`/`{CompiledBinding}` of the view (or of a `DataTemplate` typed to an item contract). Replaces `RelayCommandReachabilityTests` for new screens |
| Every view lays out and measures non-zero with every fake variant | all | `GalleryPagesUiPerfSmokeTests` (exists; pages derived from the catalog) |
| Every screen has an approved golden and matches it | ≤ 0.05 % pixels differ | § 5 |
| Localized text fits | 0 `clipped-text`, containment in de-DE / ja-JP | `UiSweepUiPerfSmokeTests` + the culture re-runs the release script already does (`DOTCHARTER_HEADLESS_CULTURE`) |
| Accessible names, token types, style classes, unique file names, theme parity | as today | `AccessibleNameTests`, `ResourceTokenTypeTests`, `FireClassReachabilityTests`, `ViewFileNamesAreUniqueTests`, `ThemeTokenDictionaryTests` |
| Registered ⇒ reached | as today | `ServiceRegistrationReachabilityTests`, `AppServiceGraphTests` |
| No swallowed exceptions | as today | `SilentCatchLintTests` extended to Presentation |

Why 7 dependencies: the nine redesigned screens need 3–6 services each (Results:
profiles, stats, take store, transport, navigator, session); 7 leaves one for a
logger. 5 fails Settings tabs honestly; 10 does not bite anything that exists.

### 4.1 As built in wave 0 — where it differs from the table

- **Banned APIs are opt-in per project**, wired once in `Directory.Build.props`:
  `<DotCharterBannedApis>true</DotCharterBannedApis>` adds the analyzer (5.6.0)
  and the root `BannedSymbols.txt`; `<DotCharterBannedPresentationApis>` adds
  `BannedSymbols.Presentation.txt` (`JuceAudioPlaybackService`,
  `IAudioPlaybackService` — § 2.3). Both Presentation projects set both. The UI
  takes the first one line once § 2.1 lands; a trial build with it on reported
  108 RS0030 errors in 36 files on 2026-09-19, none in the exempt locations.
  `Microsoft.Data.Sqlite` is banned as a namespace (`N:`), `GetFolderPath` as
  both overloads.
- **The exemptions are `.editorconfig` sections**, `[DotCharter.UI/{App*.cs,Program.cs}]`
  and `[DotCharter.UI/Platform/**.cs]` setting RS0030 to `none` — the analyzer has
  no path filter of its own. A test pins the section list; the `#pragma` counter
  also counts `SuppressMessage("…", "RS0030…")`.
- **Comment share** is comment lines over NON-BLANK lines, doc comments included.
  `Contracts/` is exempt from the 20 % (a contract's member summaries are its
  specification; the ten moved in at 40–58 %) but not from the history pattern.
- **The constructor cap** is checked over every class in `DotCharter.Presentation`,
  not Presentation.Fakes — a fake's private constructor carries fixture data, not
  dependencies.
- **Each rule is a pure function with a probe fact** that runs it on a known-bad
  input in every gate run (the Presentation assembly has no class yet, so the
  reflection rules would otherwise pass over nothing). The fixtures are in
  `DotCharter.Tests/Presentation/Fixtures/`.
- **Fakes publish `Variants` today**, ahead of `ScreenCatalog`:
  `FakeVariant<TContract>(Name, Create)` and the `FakeVariantNames` constants in
  `DotCharter.Presentation.Fakes`, one entry per existing factory, required names
  mapped onto the nearest existing state (Menu's `Loaded` is `Default`, Paused's
  `NotStarted` is `Empty`, Playing's `Extreme` is `LongText`, …).
  `Presentation/FakeVariantsTests` requires every name the fake's contract calls
  for (any non-string `IEnumerable` property is a list, so `Full`), with no pin.
  Song Select's `Full` is the 2,160-row library (`FakeSongLibraryFull`); its
  `Default` stays the 41 hand-written rows. See § 3.4.

## 5. Golden screenshots — what stops "fixed here, broke there"

- **Harness** — `Perf/ScreenGoldenUiPerfSmokeTests`, `[Collection("HeadlessUI")]`,
  one case per `ScreenCatalog` entry × fake variant, rendered at 1600×900 in
  the Skia headless mode the capture path already uses
  (`DotCharter.Tests/Perf/HeadlessAppFixture.cs`, `UseHeadlessDrawing = false`).
  That mode is fixed when the session starts, so the goldens are their OWN
  `dotnet test` invocation with `DOTCHARTER_UI_SWEEP_DUMP=1` set — a `-Goldens`
  switch on `tools/agent/Invoke-Gate.ps1`, and a third step in the
  release script and CI beside the headless job (TODO — § 5.1). Culture pinned to en-US,
  `PrewarmPacer.QuietWindowMs = 0` as the fixture sets it. Same PNG file name as
  `DotCharter.UI.Gallery/GalleryCapture.cs` so the exe, the sweep dump and the
  goldens agree.
- **Compare** — per pixel, any channel differing by more than 8/255 counts (the
  `DifferenceRatio` shape in `DotCharter.Tests/Rendering/Headless/GoldenFrame.cs`);
  the case fails when more than 0.05 % of pixels differ (≈ 720 of 1.44 M — absorbs
  anti-aliasing jitter, fails on a 12 px label moving). On failure it writes
  `<name>.actual.png`, `<name>.diff.png` and an `index.html` under
  `TestResults/ui-goldens/`. A screen with no baseline fails with "no golden —
  run Update-Goldens", never passes silently.
- **Store** — `DotCharter.Tests/Goldens/ui/<area>/<screen>--<variant>.png`,
  plain git, en-US only, one size. ~85 screens × ~4 variants
  ≈ 340 PNGs at 100–200 KB ≈ 50 MB. de-DE and ja-JP are NOT pixel goldens
  (CJK fallback fonts differ per machine); they run the structural pass (§ 4
  localized-text row). Compact-window variants are structural too. A sidecar
  `<name>.json` records `commit`, `acceptedOn`, `sha256`; a lint
  (`UI/Goldens/ScreenGoldenStoreTests`) fails on a PNG whose sidecar is missing or whose hash disagrees.
- **Update** — `tools/agent/Update-Goldens.ps1`: `-Stage` runs the
  harness against a staging folder and writes a before / after / diff contact
  sheet per changed screen; `-Accept` copies staged PNGs into `Goldens/` and
  writes the sidecars, and it asks for confirmation with `Read-Host` first — the
  agent tool shells run with stdin on the null device, so an agent cannot
  accept a baseline even by mistake. Only the owner accepts. A PR whose diff touches
  `Goldens/**` is reviewed by opening its contact sheet, nothing else.

### 5.1 As built in wave 0

- **Run it:** `powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Goldens`
  (sets `DOTCHARTER_UI_SWEEP_DUMP=1` and `DOTCHARTER_HEADLESS_CULTURE=en-US` for
  that one run). `-Headless` passes `--filter-not-class
  DotCharter.Tests.Perf.ScreenGoldenUiPerfSmokeTests`, the exact name with no
  wildcard, and so do `tools/ci/Invoke-HeadlessUiCi.ps1` and the release
  script's `$HeadlessUiFilter`, so the headless suite never goes red for a
  missing baseline. Checked with `--list-tests`: the exclusion removes exactly
  the 35 golden cases and nothing else. `UI/Goldens/ScreenGoldenStoreTests`
  pins all three filters to the class's real `FullName`.
- **TODO:** a `-Goldens` step in `tools/release/New-Release.ps1` and a CI job
  beside `headless-ui`. Both wait for the first accepted baselines, because
  until then every case fails by design.
- **Names.** Goldens use `ScreenCatalog.Slug` (`NoMatch` → `no-match`) and the
  gallery's PNG names use the same slugs, so `gallery-play-mode-song-select-no-match.png`
  and `play-mode/song-select--no-match.png` name the same render; one shared
  file name, which § 5 asked for, does not fit a store with area folders. The
  render is `ScreenPages.Build`, shared with the gallery.
- **Staging.** `DOTCHARTER_GOLDEN_STAGE_DIR` makes every case also write its
  PNG, a `.stage.json` (`new` / `changed` / `same`) and, when changed, a diff;
  `Update-Goldens.ps1 -Stage` sets it to `artifacts/ui-goldens/staged/` and
  writes `index.html` there. `-Accept` refuses outright when
  `[Console]::IsInputRedirected`, and otherwise needs `ACCEPT` typed at the
  `Read-Host` prompt; with stdin on the null device `Read-Host` returns an
  empty string, so either guard alone stops an agent.
- **Measured** 2026-09-19, first staging: 34 cases (10 screens), 2.2 MB of PNG
  (about 64 KB each, not 100–200 KB), 13 s of test time for the whole class.
  The logged first layouts run 10–370 ms; Song Select and Get Ready are the
  slow ones.

## 6. Testing strategy

| Layer | Where it runs | What it proves |
|---|---|---|
| View-model unit tests: real `<Screen>ViewModel` against fake Services (`DotCharter.Tone/Audio/NullAudioPlaybackService.cs`, in-memory stores, `GatedInlineDispatcher` for `IUiDispatcher`) | default gate | verbs, formatting, empty states, lifecycle start/stop; no Avalonia in the process |
| Contract conformance: every `Fake<Screen>` and every `<Screen>ViewModel` raise `PropertyChanged` for each contract property and expose non-null commands | default gate, reflection, derived from the catalog | the fake and the product cannot drift apart |
| Structure lints of § 4 | default gate | the rules stay rules |
| `GalleryPagesUiPerfSmokeTests`, `UiSweepUiPerfSmokeTests`, the culture re-runs; `ScreenGoldenUiPerfSmokeTests` | headless suite (`tools/agent/Invoke-Gate.ps1 -Headless`) and the goldens invocation (`-Goldens`), each its own process | it lays out, it looks as approved, nothing clips in three languages |
| Existing 13k-case gate | default gate | Core / Services / Analysis unchanged — the rewrite does not touch them |

Cost (estimate — measure in wave 0): ~340 Skia renders in their own process,
≈ 3–4 min on an idle box, beside a headless suite that takes 7–8 min today; the
default gate grows by a few hundred fast cases. Nothing timed is asserted anywhere.

Lints the new UI does NOT need (they answer questions the anatomy answers by
construction, and they are the source-text kind that goes blind on a file
move): the per-screen hand-written cases in `PlayModeScreensUiPerfSmokeTests`,
`DialogConstructionUiPerfSmokeTests` and `SettingsConstructionUiPerfSmokeTests`
(derived goldens + gallery smoke replace them screen by screen);
`RelayCommandReachabilityTests` and `PublicEventReachabilityTests` for
Presentation types (the contract-binding lint and the no-event rule replace
them); `PowerToolReachabilityTests`-style per-feature greps (the module +
catalog + gallery derivation is the reachability). Each is deleted or narrowed
in the PR that removes the last old screen it covered — never before.

## 7. Migration — the strangler

**Old shell hosts new screens.** `Views/MainWindow.axaml` already has
`ContentControl` hosts; a `ScreenHost` is dropped into `PlayModeHost`, then
`SettingsHost`, and old `IsVisible` blocks are removed one screen at a time.
Wave 1 step A did this for `SettingsHost` (§ 3.5): the settings SHELL migrated
first and hosted the old tabs through an adapter, so each remaining tab was one
self-contained PR — the checklist is in [settings.md](settings.md). **Wave 1 is
DONE (2026-09-22)**: Appearance, the last old tab, migrated over two Services
seams that carry colours as hex strings, and the adapter, the old page, its
change tracker and its markup search walker are deleted.
The shell itself is rewritten only when everything it hosts is new. *Alternative:*
a new shell hosting old screens first — it forces the shell rewrite before any
screen is proven and re-hosts the dock workspace twice.

**Done, per screen (all in ONE PR):** contract, fake with variants, view model,
view, unit tests, catalog + module lines, golden accepted by the owner, the old
markup block AND the old view-model members deleted, orphaned resx keys deleted
(an unused-key lint **(proposed)** — `LocalizationSweepTests` checks the other
direction today), the area's guide under `docs/agents/ui/` updated, and both
suites green. A screen whose old code is still there is not done.

### 7.1 Order and rough size (lines today, `.axaml` + view-model `.cs`)

| # | Wave | Size | Why here |
|---|---|---|---|
| 0 | **Prep, no visible change**: create the two Presentation projects and move the existing 10 contracts + 10 fakes in; layering rows; banned-API analyzer with the composition-root exemption; move § 2.1 out of the UI; add § 2.2 interfaces; the `JuceAudioPlaybackService` interfaces; the golden harness with the 9 redesign views as the first accepted baselines; `ScreenHost`, `DialogHost`, `INavigator`, `IDialogs` | ~9k moved, ~2k new | everything after this is one shape repeated |
| 1 | **Settings** — 8 tabs as 8 screens under one settings shell screen. **DONE 2026-09-22** — twelve tabs (General split in four), Appearance last | 1.7k XAML / 5.0k VM | small, self-contained, exercises `IConfigurationService` only |
| 2 | **Dialogs** — 37 under `Views/Dialogs/`, in two halves (project / import / export / onboarding, then chart tools / wizards / tone designer); Diagnostics and About windows; the command palette | 6.4k / 13.6k | proves `IDialogs`; kills `AppDialogService` (36 deps) |
| 3 | **Play Mode flow** — Start, Menu, Song Select, Get Ready, Countdown, Playing HUD, Paused, Failed, Results; the 2.3k lines of views under `Views/PlayMode/Redesign/` exist | 5.4k / 7.6k | `PlayModeViewModel` (5.8k lines, 19 partials) and `PlayModeView.axaml` (5.4k lines) go to zero; `PlayModeSession` replaces the shared fields |
| 4 | **The rig** — `Views/PlayMode/AmpFxRigView.axaml` (187 KB), `PedalboardViewModel` (13 partials), `RigDockUnit`, the `RigEditors/` family, the tone designer / match / sections panels | 5.0k / 13.6k | hosted by both Play Mode and the editor overlay; needs the audio interfaces from wave 0 |
| 5 | **Shell chrome** — menu bar, toolbar, status bar, popovers, launch card, stacked tracks; then `MainWindow` → a `ShellView` with the top-level `ScreenHost`; `MainWindowViewModel` (33 deps) dissolves into per-surface view models | 5.9k / 3.7k | last of the non-editor surfaces; deletes `ViewLocator` |
| 6 | **Chart editor** — song browser, inspector (8 tabs), sheet strip, stem mixer, transport, phrase catalog, then the timeline + 3D hosts and the dock layout | 4.5k / 13.2k + `Controls/` 23.7k kept | hardest: docking, two renderers, 60 Hz bindings, the view anchor. Custom controls are re-hosted behind contracts for their INPUTS; their drawing code is untouched |

**Out of order, and deliberately: HOME (0.62).** It is not in the table because
it did not exist — there was no screen to migrate. The Graphite rework asked for
a landing screen, and a NEW screen on this architecture is the cheapest possible
proof that the architecture works: contract → fake with six states → view →
gallery page → golden → `ScreenCatalog`, then a real view model over services
and a `ScreenHost` in the shell. `Contracts/Home/IHomeScreen`,
`Screens/Home/{HomeSnapshot,HomeSnapshotSource,HomeViewModel,HomeModule}`,
`Views/Home/{HomeView,HomeSurface}`. It also added the first non-screen contract,
`Contracts/Shell/IShellDestinations` — the seam a screen uses to send the user
to another top-level surface, which wave 5 will need for every screen it moves.
Home re-reads its figures on every show (`IScreenLifecycle.OnShown`, announced by
`ScreenHost.NotifyReshown`), so that read is split (#234):
`HomeSnapshotSource.CaptureLive` takes what only the UI thread may touch (the
config's live recent-project list, the open chart, the device), `Read(live, now)`
does the library index, play history and covers on a worker, and
`HomeViewModel.RefreshAsync` publishes only a snapshot that `SameAs` reports
changed. A return that finds nothing new costs 2-5 ms instead of 19-31 (Release R2R, contended box)
(`Presentation/HomeScreenTests`).

**The Library moves in wave 5, not 6 (owner, 2026-09-22).** The song browser was
listed with the chart editor, but the owner's wave-5 brief makes Library, Rig and
Settings top-level surfaces with a charting presentation (sidebar + editor chrome) AND
a play-mode presentation (full-bleed, no editor chrome) over the SAME data — so the
Library becomes one screen contract with two hosts in wave 5, and wave 6 keeps only the
editor's own panes. Its acceptance list is the external screenshot review's Library
section ("Library review items go to wave 5"); none of it is done in place first, because every item is a property of
the columns and header the move rebuilds.

Waves 1–2 are the calibration: if two agents cannot land a settings tab a day
against the rules in § 4, the rules are wrong and get fixed there, not in wave 6.

## 8. How agents execute it

- **Unit of work: one screen, one worktree, one branch** (`feat/screen-<area>-<name>`),
  cut from current `main` (check `git merge-base --is-ancestor`). The brief names
  the contract file, the required variants, the Services interfaces the view model
  may take, the old block to delete, and nothing else. Sonnet for a screen
  whose contract exists; Opus for a screen that needs a new Services interface or
  touches audio; never a brief that says "improve".
- **A screen PR contains** the files in § 7 "done", the gate line and the
  headless-class line from `Invoke-Gate.ps1`, the golden contact sheet path
  (`TestResults/ui-goldens/index.html` from the `-Stage` run), and a diff stat
  showing the deletion. It touches at most: its own folder in each layer, one
  line in `ScreenCatalog`, one line in its module, the resx trio, one guide.
  `tools/agent/Assert-ScreenScope.ps1` **(proposed)** fails the PR when the diff
  leaves that set; a shared-style change (`Styles/**`) is its own PR type and
  must attach the FULL gallery contact sheet, because it moves every golden.
- **Review gate:** the owner opens the contact sheet and accepts or rejects the
  baseline; nothing merges with a stale or missing golden. Code review is the
  lints' job first and the owner's second.
- **Guardrails against the churn that cost a week:** baselines change only
  through `-Accept` (§ 5); no PR may touch two screens; no PR may edit
  `Styles/**` and a screen together; a red golden is fixed in the screen's own
  markup, never by re-accepting; a brief without a named contract is refused by
  the orchestrator; agents run at most two at a time (memory rule) and never
  `git stash`.

## 9. Risks and decisions

The owner accepted every default below on 2026-09-19. Revisit one only with a
measured reason, and record the change here.

| # | Question | Decision |
|---|---|---|
| 1 | Golden determinism across machines: Skia/HarfBuzz are pinned by NuGet, Inter is bundled, but CJK fallback fonts are the machine's | en-US pixel goldens only; de-DE / ja-JP structural. The owner's box is the machine of record; CI compares with the same threshold and is expected to match on `windows-latest` (Inter bundled) — verify on the first cloud run |
| 2 | Baseline storage growth (~50 MB now, more with history) | plain git; move `Goldens/**` to Git LFS when the folder passes 150 MB |
| 3 | Fakes as their own project vs a folder in Presentation | own project (`UI` may never reference it — one layering row) |
| 4 | Screen view-model lifetime | cached per host with `OnShown`/`OnHidden`; revisit if memory shows |
| 5 | Constructor cap | 7, no allow-list; raise to 8 only if two real screens hit it for the same reason |
| 6 | Images in view models | keys through `IArtworkSource`; one cached converter in UI |
| 7 | Do the chart editor's view models get rewritten, or only re-hosted? | decide after wave 3 with measured numbers; the plan assumes rewritten last |
| 8 | `LivePlayViewModel` (128 KB) straddles editor and Play Mode | the judging/session half goes to Services beside `LivePlaySessionService`; a thin HUD view model per host stays |
| 9 | Which `JuceAudioPlaybackService` interfaces, exactly | drafted in wave 0 as FIVE (§ 2.3 has the deviations; the table is in `services/audio-engine.md`), **pending owner approval** before wave 3 |
| 10 | Dock.Avalonia stays for the editor | yes; a dock-pane contract per pane, `Dockable.Context` stays a view model (`DockPaneContractTests`) |
| 11 | Delete `ViewLocator` and the `ViewModelBase` convention at the end | yes, in wave 5 |
| 12 | Does the gallery become the ONLY place screens are designed | yes — a screen not in the catalog does not exist; hand-written gallery pages remain only for the design-tokens page |
