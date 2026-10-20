# DotCharter.UI Agent Guide

Avalonia 12 app project and composition root (`App.axaml.cs` builds DI).

This file holds the hard rules, the folder map and ONE routing table. The
per-area reference lives under `docs/agents/ui/` as plain links (not imports, so
it costs nothing until opened). **Open the routing row for the area you are
editing before you edit it** — those files carry each known trap with its guard.
How each rule was learned is in `git log -p DotCharter.UI/AGENTS.md`.

## Hard rules (read before editing any UI file)

- **Shell chrome is ONE flag.** The activity rail, the title bar and the status
  rail all follow `MainWindowViewModel.IsShellChromeVisible`, which asks the pure
  `Presentation/Shared/ShellPresentation`. Home and Play Mode are FULL-BLEED; the
  chart editor always carries all three; the Library and Settings take the
  CONTEXT they were reached from (`ShellContext`: Play Mode and a run set
  Playing, the editor sets Charting, Home keeps it) — a player's Library is
  full-bleed under the `BackBar` (Back and the window buttons in the 44 px title
  row; the screen prints its own title), a charter's is a workbench. The rig
  overlay's ✕ follows the same flag (`SyncRigCloseButton`). Never add a second
  flag; change the rule.
  - **The title bar's ROW height is BOUND** (`TitleBarRowHeight`, a
    `GridLength`): `IsVisible=false` in a fixed row keeps the band. Pinned by
    `UI/PlayModeReachableFromEditorTests.PlayModeAndHome_AreFullBleed`. **A
    headless case may NOT switch shell tabs to check this** — the switch runs
    real per-destination work and takes the testhost down without failing.
  - **Removing chrome removes what rides on it.** With
    `WindowDecorations="BorderOnly"` the title bar is the only thing that moves
    the window and holds its buttons, so `Views/Shell/WindowChromeView`'s `WindowCaption` (a
    32 px row: mark, drag strip, three buttons) shows when neither the title bar
    nor the back bar does and the window is not fullscreen
    (`ShellPresentation.ShowsWindowCaption` / `TopRowHeight`). A new full-bleed
    surface answers the same question.
- **Play Mode is reached from HOME, never from the rail** (the rail does not
  exist on the Play surface). The File menu keeps its entry as the second door.
- **The Amp & FX rig's ✕ is HOST-DEPENDENT** (`AmpFxRigViewModel.ShowCloseButton`,
  default true). The shell overlay turns it off exactly where the rail is
  (`!IsShellChromeVisible`), because the rail's rig row already toggles
  `IsPedalboardOpen` both ways; Home, a full-bleed Library and Play Mode keep it.
  `UI/RigDismissalTests`.
- **No airspace: the 3D preview is composed-only.** `Preview3DComposedCanvas`
  is the one 3D host — no child HWND, no appconfig switch, no fallback (a
  compositor without GPU interop leaves the panel blank and says so on the status
  rail). Dialogs, popups and the rig layer above it like any Avalonia
  content, so **never add a NativeControlHost, a panel-hide or a
  freeze-frame for an overlay** — `UI/Preview3DComposedOnlyTests` fails on a
  `NativeControlHost` in this assembly or a path switch in `AppConfig`.
  `Views/Shell/MainWindowCapture`'s `PrintWindow` capture stays for the pause/fail
  backdrop and the highway screenshot (it reads the composed surface; Avalonia's
  render-to-bitmap does not).
- **Popups live IN the window** (`OverlayPopups = true` in `Program.cs`).
  **The default Win32 composition mode must still stay transparency-capable**
  (`Platform/Win32CompositionModeSelector`, `Win32CompositionModeSelectorTests`)
  or every transparent popup pixel renders BLACK; the DXGI pacing mode is
  appconfig opt-in. See
  [docs/agents/ui/rendering-perf/presentation-paths.md](../docs/agents/ui/rendering-perf/presentation-paths.md).
  Because they are in-window, a bring-into-view raised INSIDE a popup could
  scroll the page behind it; `Platform/PopupBringIntoViewPolicy` stops it at the
  `Popup` (#48).
- **A bare wheel notch over a scrolling pane scrolls it — never edits a value.**
  `Platform/SliderWheelPolicy` (tunnel, on `ScrollViewer`) covers `Slider` and
  every `Platform/IWheelAdjustable`. A custom control that edits on the wheel
  implements `IWheelAdjustable` and takes only a modifier notch (the EQ plot:
  Ctrl + wheel, #47). Pinned by `MixerScrollHostUiPerfSmokeTests`.
- **Hidden subtrees use `IsVisible=false`, never `Opacity=0`** (an `Opacity=0`
  tree is still laid out and composed every frame).
- **The playhead fires at up to 250 Hz — bindings only ever see it coalesced
  through `Dispatcher.UIThread.Post` at ≤60 Hz.** Never raise `PropertyChanged`
  for bound state from a non-UI thread.
- **No style animations on hideable surfaces** — a hidden control's style clocks
  keep ticking. Extend a custom control's render pass, or use a `DispatcherTimer`
  that provably STOPS when idle/detached (`Controls/MenuBackdropControl` parks on
  detach and counts its own `InvalidateVisual` calls).
- **`Dockable.Context` must be a VIEW MODEL, never a control** (a control crashes
  on float/redock). `DockPaneDefinition.Context` is typed `ViewModelBase`; don't
  widen it (`DockPaneContractTests`).
- **`Preview3DView` and the chart timeline composite stay SINGLE instances**
  across layout toggles and pop-out (native swapchain race) — counted by
  `DockPaneContractTests`.
- **Custom-drawn controls hosted in panes unsubscribe from singleton services in
  `OnDetachedFromVisualTree`** — pane views are re-created on float/redock.
  `ViewSubscriptionLeakGuardTests` (sweeps this assembly AND `DotCharter.Tone.UI`;
  a class that declares its own attach must declare its own detach — a base
  class's detach does not count for a subclass's attach).
- **Theme: tokens only** — no hard-coded colors, no literal `FontSize="NN"`
  (use the `FontSize*` scale and the `Space*`/`Gap*`/`Padding*` tokens). Colours,
  brushes, icons and the 17 `Ladders.Wide.axaml` tokens are `DynamicResource`
  (they change at runtime); the FIXED size tokens — every `x:Double`/`Thickness`/
  `CornerRadius`/`FontWeight`/`TimeSpan`/`FontFamily` key in `Metrics`,
  `Typography`, `Palette`, `Ladders` and `PopoverTokens` — are `StaticResource`,
  because a dynamic use costs a live expression per control (#241). Both
  directions and the theme's include order are linted by
  `UI/FixedTokenReferenceTests`; dialog views keep their all-dynamic rule.
- **A Button, Border, TextBlock or ToggleButton family is a `ControlTheme`,
  never a global `Button.X` / `Border.X` / `TextBlock.X` / `ToggleButton.X` style**
  (#240): a global class style is attached to every control of that type, set or
  not. Wear both `Theme="{StaticResource FireGhostButton}"` and
  `Classes="FireGhost …"` (`FireTagBorder` + `FireTag`, `FireMonoText` +
  `FireMono`, `ChipToggle` + `Chip`) — the theme's rules are gated on the class.
  How, and the traps: [fire-design-system.md](../docs/agents/ui/fire-design-system.md)
  § "A button is a ControlTheme"; guarded by `UI/ButtonControlThemeTests`,
  `UI/BorderControlThemeTests` and `UI/TextBlockToggleButtonControlThemeTests`.
  **A rule a theme cannot hold (a descendant selector, a Path / Panel / Grid mark)
  and that one view uses lives in THAT view's Styles, not the global chain** (#242:
  the rig's `AmpFxRigStyles.axaml` / `RigModuleEditorStyles.axaml`). Scoping raises
  its rank above every global style and theme, so re-check what it used to lose to;
  `UI/FireClassReachabilityTests` fails a scoped class worn outside its view.
  **A grammar is included at ONE level, and `MainWindow` has no `Window.Styles`**
  (#412): window scope is every screen and dialog, and a grammar a view AND its
  host include is attached twice to every control inside — `UI/StyleIncludeScopeTests`,
  `Perf/StyleIncludeScopeUiPerfSmokeTests`. **A VIEW grammar's families are
  ControlThemes too (#428)**: a view-scoped `Border.X` / `TextBlock.X` /
  `Grid.X` / `StackPanel.X` rule attaches to every control of that type the view
  hosts, prewarmed or not — Play Mode, the dialogs, the settings tabs and the
  app's Fire panels and the settings shell cut ~114,000 frames; the same two test classes pin
  each grammar (a markup lint and per-scope frame ceilings on the real shell). A
  child rule (`Grid.X > TextBlock`) becomes a local value on the children; a
  view's rules for one subtree live on THAT subtree's `Styles`
  (`SettingsShellView`'s rail).
- **`ChartProjectCloner` is the single source of truth for snapshotting** (see
  `DotCharter.Core/AGENTS.md`) — never hand-roll a project copy in a view model.
- **`ViewLocator` maps VM → View by name** (below); break it and the pane renders
  "Not Found: …".
- **`App.Services` is the composition root's** (`App*.cs`, `Program.cs`). New code
  takes dependencies through its constructor or from its owner — a `Func<T?>` when
  it must stay lazy (`Views/Shell/MainWindowPrewarm` and `MainWindowDiagnostics`
  get theirs from `MainWindow`, whose `CompositionRoot()` is its one documented
  read). `Infrastructure/ServiceLocatorRatchetTests` lists today's exceptions —
  XAML-constructed controls and render-path code — and only shrinks; no view
  model may appear on it.
- **A new constructor dependency is REQUIRED, never `IFoo? foo = null`.** DI
  always passes it, so the null branch is dead in the app and a dropped
  registration fails silently instead of in `UI/AppServiceGraphTests` (#661).
  `ILocalizationService` is required everywhere; tests pass
  `FakeLocalization.English()` / `KeysOnly()`. `Infrastructure/OptionalDependencyRatchetTests`
  counts the remaining optional interface-typed constructor parameters per
  project (Services, Tone, Tone.UI, Presentation, UI) and fails when one rises;
  lower its row when a count falls.

## View-model ↔ view registration convention

- `ViewLocator` (app-scoped in `App.axaml`) maps a VM to its view by **replacing
  `ViewModel` with `View` in the full type name** — globally, so the `ViewModels`
  namespace segment becomes `Views`
  (`…ViewModels.Editor.SheetMusicPaneViewModel` → `…Views.Editor.SheetMusicPaneView`).
- The VM derives from `ViewModels/Shell/ViewModelBase` (what `ViewLocator.Match`
  keys on) and the view has a public parameterless ctor.
- Resolution is cached and never throws: a miss renders a "Not Found: …"
  `TextBlock`. The pure resolver is `ViewLocator.ResolveViewType(Type)`
  (`ViewLocatorTests`).
- Selection is app-wide: `ISelectionService` (singleton) is the one selection
  model for the 2D timeline, 3D highway and sheet strip. Views read its
  `Selected*` collections and subscribe to `SelectionChanged`; they never own
  selection. The 2D/3D renderers poll it per frame from their snapshot.

## New screens: `ScreenCatalog`, `ScreenHost`, `DialogHost`

- A target-architecture screen is NOT found by `ViewLocator`. It is one line in
  `Views/Shell/ScreenCatalog.cs` (`Screen<TContract, TView>(area, name)` or
  `Dialog<…>`); its view model comes from DI through the contract.
  `Views/Shell/ScreenHost` (one cached view per contract, `IsVisible` flip, scoped
  view models, neighbour prewarm, Esc = close top dialog then `Back()`) and
  `Views/Shell/DialogHost` (a scope per open, one shared
  `Border.FireOverlayScrim`, context-free prewarm) host them;
  `ScreenShellModule.Register` wires both.
- **Dialogs move onto the host one at a time**: `Views/Shell/DialogContainerView`
  builds the one `DialogHost` above the legacy dialogs and under the generic
  message dialog; recipe and status in
  [dialogs/hosted-dialogs.md](../docs/agents/ui/dialogs/hosted-dialogs.md).
- **Settings and Home are hosted surfaces**: `Views/Settings/SettingsSurface` and
  `Views/Home/HomeSurface` each build a `ScreenHost` for their tab's
  `ContentControl` (`SettingsHost` / `HomeHost` in `MainWindow.axaml`, filled by
  `MainWindowPrewarm.EnsureTopLevelViewsCreated`); the settings tabs sit in a
  `Views/Shell/ScreenTabHost` (borrows the enclosing host's scope, inert without
  one). Every Settings tab is a screen. Detail: §§ 3.4–3.5 of
  [target-architecture.md](../docs/agents/ui/target-architecture.md) and
  [settings.md](../docs/agents/ui/settings.md).
- The catalog DERIVES the gallery pages, the golden screenshots
  (`Invoke-Gate.ps1 -Goldens`) and three default-gate lints
  (`Presentation/ScreenCatalogTests`, `ContractBindingLintTests`,
  `ContractConformanceTests`). A catalog view must bind every contract member.

## Every new or edited view ships a smoke test

XAML faults that build clean and fail only at runtime are listed in the root
`AGENTS.md`. Here:

- **New/edited VIEW ⇒ a construction + attach + layout smoke test.** Attach to a
  real `Window`, `Show()`, lay out, assert the subtree **measured non-zero** (a
  view whose styles failed still arranges, with no size). Model on
  `DotCharter.Tests/Perf/*ConstructionUiPerfSmokeTests.cs`.
- **`Padding`/`Margin`/`BorderThickness` take `Padding*`/`Gap*`, NEVER `Space*`**
  (`x:Double`, for `Spacing`/`ColumnSpacing`/`RowSpacing`) — `DynamicResource`
  does not convert, so it throws `InvalidCastException` on attach. Pinned both
  ways by `DotCharter.Tests/UI/ResourceTokenTypeTests.cs`.
- **A style class that matches no selector is a SILENT no-op.** Pinned by
  `DotCharter.Tests/UI/ButtonStyleClassTests.cs` and, for the Fire layer, by
  `UI/FireClassReachabilityTests.EveryFireClassUnderViews_MatchesASelectorForItsElementType`
  — type-aware, because **the ELEMENT matters as much as the name**
  (`Grid.FirePopoverKicker` is the caption ROW; the caption text is
  `TextBlock.FirePopoverKick`).
- **Every control a user acts on has an accessible name** — `UI/AccessibleNameTests`:
  - Words inside a PANEL do not name a control (an icon + caption in a
    `StackPanel` announces as "Avalonia.Controls.StackPanel"). Set
    `AutomationProperties.Name` off the caption's key or binding; a list item is
    named by its data's `ToString()`, so row types override it (`LibraryRow`,
    `PlaySongItem`); Dock's pane-header buttons are named in `Styles/Dock.axaml`.
    Ground truth: `Perf/ButtonAutomationNameUiPerfSmokeTests`.
  - **A RECORD row is not named by its ToString** — that reads every member
    aloud. Bind the name instead: `<ListBox.Styles><Style Selector="ListBoxItem"
    x:DataType="…">` with an `AutomationProperties.Name` setter (the export
    findings, Showlights and Library path lists do).
  - **A template part that takes focus is named by its templated parent** —
    the TextBox in a `NumericUpDown` / editable `ComboBox` (Tone.UI
    `Styles/Inputs.axaml`) and a `ColorPicker`'s drop-down (`Styles/AutomationNames.axaml`)
    forward the control's own name; the spinner arrows say "Increase"/"Decrease";
    a Slider's pointer-only track halves are `AccessibilityView="Raw"`.
  - **The runtime half is `Perf/AccessibilityAuditUiPerfSmokeTests` (#311)**: the
    UI sweep walks every surface's AUTOMATION tree (what Narrator/NVDA see) for
    unnamed interactive controls, keyboard-unreachable ones and unnamed
    non-decorative images. Unnamed counts per surface are a RATCHET
    (`UnnamedBaseline`, only ever lowered); the other two must stay zero. A
    decorative image (cover art beside its title, the app mark) is
    `AutomationProperties.AccessibilityView="Raw"`.
  - **A custom-drawn surface publishes its own peer** (it has no children to
    walk). `MainChartRenderer` is focusable (Tab lands on it, not on
    `TimelineGrid`) and speaks through `Controls/ChartRendererAutomationPeer`:
    name = arrangement + playhead, value = the selection, help = the user's
    select/seek bindings, change events coalesced per burst; the peer owns
    its selection subscription and follows the renderer's attach/detach. The words come
    from the pure `Controls/ChartTimelineDescription` (`UI/ChartTimelineDescriptionTests`;
    wiring in `Perf/ChartRendererAutomationPeerUiPerfSmokeTests`).
  - **An icon-only control needs a name, and a `ToolTip.Tip` is not one**
    (announced on hover only) — carry BOTH off the same key. A bare glyph or a
    lone LETTER is not readable text (`IsReadable` wants two letters); a lone
    DIGIT is, deliberately.
  - **Every value control** — the lint walks `RowInputControls` (`ComboBox`,
    `Slider`, `ToggleSwitch`, `TextBox`, `NumericUpDown`, `MaskedTextBox`,
    `AutoCompleteBox`, `ListBox`, the date/time pickers, `ColorPicker`,
    `ColorView`) plus `CheckBox`/`ListBoxItem`. The name is the row's label off
    the SAME Loc key; a label-less search box takes its `PlaceholderText` key.
    **Add a new input control type to `RowInputControls` when you first use it.**
    Both exemption lists are empty and shrink-only.
  - Sibling `UI/ModalEscapeHatchTests`: every `*Scrim` surface holds a FOCUSABLE
    dismiss (a Close/Cancel command, `Button.FireOverlayClose`, or
    `IsCancel="True"`) — clicking the scrim does not count. Detail in
    [misc-surfaces/diagnostics-rtl-scale-palette.md](../docs/agents/ui/misc-surfaces/diagnostics-rtl-scale-palette.md).
- **A `[RelayCommand]` nothing binds is a button that cannot be pressed.**
  `UI/RelayCommandReachabilityTests` fails when a generated `…Command` is named by
  no `.axaml` and no other `.cs`; its PRECISION half
  (`EveryReachableCommand_IsReachedByAFileThatNamesItsViewModel`) refuses a match
  that is only a same-named command on another view model. Its `KnownUnreachable`
  and `KnownAmbiguous` lists **only shrink**: never add to them, and delete an
  entry that becomes reachable. Three verdicts for a dead command: wire it,
  delete it, or — when C# in the same file calls the METHOD — drop the attribute
  (the wrapper was dead, the method is live). The commonest real verdict is "a
  two-way binding already does it": a `ToggleButton`/`CheckBox` writing the
  property IS the gesture. Never excuse an entry with prose — excuses rot.
- **`catch (Exception ex) { Debug.WriteLine(…); }` is a SWALLOW** — the call is
  `[Conditional("DEBUG")]`, so Release has an empty block. Log through the real
  logger. `Infrastructure/SilentCatchLintTests` scans `DotCharter.UI` and
  `DotCharter.Services` (comments and strings masked); every remaining one is an
  `Allowed` entry with a REASON, and a STALE entry fails too.
- **A `public event` nothing subscribes to is the same fault.**
  `UI/PublicEventReachabilityTests` over `DotCharter.UI` and `DotCharter.Services`:
  the lenient fact needs some PRODUCT `+=` (test subscriptions do not count), the
  strict one a subscriber outside the declaring file. Both pin lists only shrink.
- **Every shell view has a smoke case** in
  `DotCharter.Tests/Perf/ShellConstructionUiPerfSmokeTests.cs`; its
  `AssertIconsResolved` is the only catch for a mistyped
  `{DynamicResource IconX}` (a blank glyph, no exception, no log). Add a case
  with every new shell view.
- **Every other fault of this class is in
  [docs/agents/ui/layout-traps.md](../docs/agents/ui/layout-traps.md) — read it
  before writing markup.**

## Responsiveness rules that bite on EVERY surface

- **A view/tab/screen/dialog switch: ≤150 ms, target <20 ms** (root `AGENTS.md`).
  The cost is the first layout of a hidden subtree, so **any new hidden surface
  is exposed through `Views/Shell/IPrewarmableSurfaces`**, realized by measuring
  VISIBLE CHILDREN — never by writing `IsVisible`.
- **A search box may not re-filter a big list in its property setter.** Route it
  through `ViewModels/Shell/CoalescedFilterPass`: `Request()` for typing (one pass
  in flight, posted at `Background`), `RunImmediately()` for a discrete gesture.
  Both song-library surfaces and the rig's tone-preset browser do; details in
  [song-browser.md](../docs/agents/ui/song-browser.md).
- **No synchronous file I/O in a command or a property setter.** File work goes
  in a `Task.Run`; UI-thread-only work (a `PrintWindow` capture, status and
  `ObservableProperty` writes) stays on the dispatcher. Settings need no
  `Task.Run`: `IConfigurationService` coalesces its writes off-thread. Pinned by
  `UiThreadIoDeferralTests` and `ConfigWriteDeferralTests`.
- **Custom controls:** `InvalidateVisual()` only when the rendered state changed
  (e.g. `MiniFretboardControl`'s hover cell); throttle time-driven repaints
  (`MinimapOverviewControl` ~33 ms). **A hidden surface is not a detached one** —
  Avalonia keeps its last `Bounds`, so route repaints through ONE funnel gated on
  `Controls/EffectiveVisibilityWatcher` (as `ChartRenderer` and
  `MinimapOverviewControl` do) and request a frame on the false→true flip. Never
  branch on `IsEffectivelyVisible` — Avalonia 12 raises no change for it.
- ComboBoxes are virtualized globally with a capped popup height (`Styles/Inputs.axaml`).
- Seeking from pointer input is throttled to ~60 Hz with an immediate seek on
  press (`MinimapDragController`, the sheet's `SheetMusicViewInput` scrubbing) — every seek flushes
  native transports.
- Nothing may create a POPUP on a bare pointer-enter — a hover earns it with
  dwell (`Platform/ToolTipHoverPolicy`). See
  [docs/agents/ui/rendering-perf/tooltips-and-popups.md](../docs/agents/ui/rendering-perf/tooltips-and-popups.md)
  and [docs/agents/ui/rendering-perf/startup-and-prewarm.md](../docs/agents/ui/rendering-perf/startup-and-prewarm.md).

## Layout

| Folder | What lives there |
|---|---|
| `Views/` | XAML views. `MainWindow.axaml` hosts the top-level "tabs" (Chart editor / Song browser / Settings / Play Mode) as stacked panels, switched by `MainWindowViewModel.SelectedMainTabIndex`. **Navigation is `Views/Shell/ActivityRailView`** — a 76 px rail down the left edge, spanning the title bar and content rows: Home / Library / Chart / Settings plus the rig toggle. Every row is a destination. **PLAY IS NOT A ROW.** |
| `ViewModels/` | CommunityToolkit.Mvvm. `Shell/MainWindowViewModel` is the root. |
| `Controls/` | Custom-drawn: `ChartRenderer` (the 2D timeline — a Decorator owning frame CONTENT, presenting through the composed-Vulkan `Controls/Timeline2D/VulkanTimelineBackend`, the only 2D backend; see `DotCharter.Rendering/AGENTS.md` § `Vulkan2D/`), `MinimapOverviewControl`, `MiniFretboardControl`, `TimelineGutterControl`, `Preview3DComposedCanvas` (the 3D preview host, Avalonia-composed), `Sheet/SheetMusicView` (AlphaTab notation strip), `Markdown/MarkdownView` (release notes over the pure `MarkdownParser`, which must never throw), `Fire/`, `FirePopover`, `Dsp/` (the rig). |
| `Docking/` | Dock.Avalonia pane registry + layout controller. |
| `Platform/` | OS adapters behind Services interfaces: `AvaloniaKeyGestureSource` (`ScreenKey` mapping), `RenderingGameplayToneHud` and the app-only policies. The adapters the standalone rig shares — `AvaloniaUiDispatcher`, `DesktopFileStorageService`, `DesktopShellLauncher`, `AvaloniaSystemClipboard` — are in `DotCharter.Tone.UI/Platform/` under the same namespace. With `App*.cs` and `Program.cs`, the only UI code that may touch files, processes, the network or SQLite (RS0030 exemptions in `.editorconfig`). |
| (no `Sheet/`, `Coordinators/`) | The sheet exporters are `DotCharter.Services/Export/Sheet/`; the coordinators are in `DotCharter.Services/Session/`, `Editing/`, `Audio/Coordination/` and `Infrastructure/Recovery/`. The UI implements their seams: `IChartEditorSurface` on `ChartEditorViewModel`, `IEditorTransport` on `TransportViewModel`, `IMessagePrompt` on `MessageDialogViewModel`. |
| `Styles/` | The theme (`Resources` → `ControlThemes` → `Typography` → `Buttons` → `Inputs` → `Containers` → `Chrome`, aggregated by `Theme.axaml`), plus `FireTokens`/`FireControls`/`Fire`/`FireMenuChrome` — a parallel `Fire`-prefixed layer that redefines nothing. |
| `Theming/` | **ONE theme** (`ThemePresets.Default`, "Graphite", complete at every token), runtime palette switching (`ChartTheme`), colour-blind string presets and per-token user overrides. No picker, no `AppConfig.ThemePresetId`. |

## Routing table — touching X ⇒ read `docs/agents/ui/Y.md` FIRST

| You are touching… | Read |
|---|---|
| **The UI rewrite** — a NEW screen on the target architecture (contract → fake → view model → view, the Presentation project, `ScreenHost`, golden screenshots, migration order, what a screen PR must contain) | [target-architecture.md](../docs/agents/ui/target-architecture.md) |
| **Any markup at all** — a row, a column, a wrapping label, a `ControlTheme`, a resource dictionary | [layout-traps.md](../docs/agents/ui/layout-traps.md) |
| Menu bar (one file per top-level menu in `Views/Shell/Menus/`), toolbar, status rail, any **popover** on those rails, `Views/Shell/`, the activity rail, `ShellStyles`/`ShellChromeStyles`/`FireMenuChrome`/`EditorMenu` | [shell-chrome.md](../docs/agents/ui/shell-chrome.md) |
| Timeline **gutter**, the `ROWS` ribbon toggles, `RibbonLayout`, the measure band, the playhead chip, the minimap strip, **dock pane chrome**, `EditorWorkspaceStyles.axaml` | [editor-workspace.md](../docs/agents/ui/editor-workspace.md) |
| A **dialog** in `Views/Dialogs/` — grammar, footer, the shared control column — the **arrangement dialog's** Rocksmith-facing fields, or the **first-run onboarding wizard** (`OnboardingWizardView` + `ViewModels/Onboarding/`; opened at startup until `AppConfig.OnboardingCompleted` and by Settings ▸ General ▸ "Run setup again"; its role choice writes `AppConfig.StartupScreen`, which `StartupRouting` boots on) | [dialogs.md](../docs/agents/ui/dialogs.md) |
| A **settings** row / group / tab, the rail, the change tracker, the **cross-tab search** | [settings.md](../docs/agents/ui/settings.md) |
| A **dock pane**, float / pin / redock, a **pop-out window** | [docking.md](../docs/agents/ui/docking.md) |
| The **2D renderer**, the 3D preview loop, the **view anchor** (`ProjectState.TimelineViewAnchorEffectiveMs` — **the ONE time↔screen anchor; a consumer that does not map through it puts clicks on the wrong chart time when panned**), LOD, ribbons, the marquee, the sheet strip; the **prewarm** mechanism; **tooltips**; startup work on `MainWindowViewModel` | [rendering-perf-detail.md](../docs/agents/ui/rendering-perf-detail.md) |
| Anything that **opens, replaces, imports or restores a project**; `MainWindow.OnClosing`; **attaching audio**; the three exhaustive chart traversals (in `DotCharter.Services`) | [project-lifecycle.md](../docs/agents/ui/project-lifecycle.md) |
| A `Fire*` token, class, control or **menu screen**; the mockups; the AVLN2000 `BoxShadow` trap; the data the mockups show that the app has not got | [fire-design-system.md](../docs/agents/ui/fire-design-system.md) |
| The **Amp & FX rig** (`Controls/Dsp/`, `AmpFxRigView`, `PedalboardViewModel`) — cards, knobs, gear rail, junctions, board drag, param binding | [pedalboard.md](../docs/agents/ui/pedalboard.md) |
| **Play Mode** (`ViewModels/PlayMode/*`, `Views/PlayMode/*`) — screens vs. scrimmed overlays, Song Select, count-in, the pause-menu freeze-frame | [play-mode.md](../docs/agents/ui/play-mode.md) |
| The **TONE3000** settings screen (`Views/Settings/Tone3000SettingsView.axaml`) | [network-surfaces.md](../docs/agents/ui/network-surfaces.md) |
| The **song browser** or Play Mode's **Song Library** (one screen in two places) | [song-browser.md](../docs/agents/ui/song-browser.md) |
| The **Inspector** pane (incl. the EQ module and `InspectorStyles.axaml`), the **Lyrics/vocals** tab, **Session Health**, **RTL**, **UI scale**, the **command palette** | [misc-surfaces.md](../docs/agents/ui/misc-surfaces.md) |
| A **keybind** or a default chord, the string-edit mask, the keyboard **pen**, what a verb means on a CHORD, the `MainWindow` **key routes** | [editor-input.md](../docs/agents/ui/editor-input.md) |
| The **Riff Library** pane (`PhraseCatalog*` in code, "riff" in anything a user reads) | [phrase-catalog.md](../docs/agents/ui/phrase-catalog.md) |
| The **Tone Designer** dialog | [tone-designer.md](../docs/agents/ui/tone-designer.md) |
| The Tone Designer's **Preview in: Rocksmith** (tonelab) target | [tonelab-bridge.md](../docs/agents/services/tonelab-bridge.md) |
| `Controls/Sheet/` and `Services/Export/Sheet/`, the coordinators in `Services/Audio/Coordination/` + `Services/Session/`, `Styles/`, `Theming/` — the beaming/duration invariant, the token scales, the ONE tone-rig owner rule, palette switching | [sheet-coordinators-theming.md](../docs/agents/ui/sheet-coordinators-theming.md) |

**When you change one of those areas, update its topic file in the same change.**
