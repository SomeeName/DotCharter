# Docking (Dock.Avalonia) — UI deep reference

Extracted verbatim from `DotCharter.UI/AGENTS.md`. **Read this before adding a
dock pane, touching `Docking/`, or changing anything about float / pin / redock
or the pop-out windows.** The hard rule that `Dockable.Context` must be a VIEW
MODEL stays in `DotCharter.UI/AGENTS.md`; the reasoning and everything else is
below.

- `Docking/DockPaneDefinition` is the pane registry entry; `Docking/DockLayoutController` owns one `DockControl`'s layout (build, incremental show/hide, close-watching, float/pin helpers, snapshot capture/restore). Three hosts exist: `BottomDock` (Inspector — and only the Inspector since 0.42.4, when the Stem Mixer and Riff Library became inspector TABS rather than a second tab strip), `TopDock` (Sheet Music) and `SideDock` (the AI chat, #559 — below). Adding a pane = one `DockPaneDefinition` in `MainWindow.InitializeBottomDock` + a visibility flag on `MainWindowViewModel` (folded into `IsBottomToolAreaVisible`, and into the Play-Mode presentation save/restore block) + the matching `SetPaneVisible` case in the shell's `PropertyChanged` switch (without it the View-menu checkbox cannot resurrect a closed pane) + a View-menu item. `PhraseCatalogReachabilityTests` is the worked example of pinning all of those hops.
- **`Dockable.Context` must be a VIEW MODEL, never a control.** Views are materialized per dock host by the app `ViewLocator` (VM name → View name, so the VM must derive from `ViewModelBase`). A control instance in Context crashes on float/redock ("already has a visual parent"). Same reason the `DockableView` DataTemplate lives in `App.axaml`, not MainWindow — floated `HostWindow`s never see window-scoped templates.
  **Since 0.58 the TYPE is the guard**: `DockPaneDefinition.Context` is declared
  `ViewModelBase`, not `object`, so a control there no longer compiles — the
  rule used to be enforced by nothing but this bullet, and its only symptom was
  a crash on the user's first drag. `DockPaneContractTests` pins the narrowing
  (never widen it back), and additionally resolves every registered pane's
  `Context = vm.X` through `ViewLocator.ResolveViewType` — a VM with no matching
  view is not a crash, it is a pane that renders the words `Not Found: …`.
- Docks are `IsCollapsable=false` so an emptied dock stays alive as the redock drop target.
- **The AI chat is the editor's right-hand column** (`MainSplitGrid`'s columns `*,0,0`: the editor, `AiChatSplitter`, `AiChatDockHost` with `SideDock`), the full height beside the timeline and the bottom dock. `MainWindowViewModel.IsAiChatVisible` (toolbar toggle `ToolbarAiChatToggle`, `KeybindConfig.ToggleAiChat` = Ctrl+Shift+A, palette `view.toggleAiChat`, View ▸ Panels) shows the COLUMN; `MainWindowLayoutController.ApplyAiChatColumn` writes its widths (0 while hidden, `Layout/AiChatColumnPolicy`), and the flag and width persist as `AppConfig.LayoutAiChatVisible` / `LayoutAiChatWidth`, not in the dock snapshot. **Hiding does not close the pane**: it stays in `SideDock`'s layout (`MainWindow._aiChatInLayout`), so `HiddenShellSurfaces` prewarms `AiChatDockHost` and a toggle is an `IsVisible` flip. The pane's own ✕ removes it (flag off); the next toggle puts it back (`SyncAiChatPane`); hiding a FLOATED pane closes its window. Its Context is `AiChatDockViewModel` (hangs off `ShellDialogsViewModel.AiChat`, the shell being at its constructor budget), whose view `AiChatDockView` hosts the target-architecture `AiChatPaneView` over `IAiChatScreen`. A run hides it and puts it back like the others (`_preGameAiChatVisible`, `GameplayDockSnapshot` over all three controllers). Pinned by `Perf/AiChatDockUiPerfSmokeTests`.
- **Every editor splitter is a DRAWN seam** (`Background="{DynamicResource BorderDefault}"`: `DockRowSplitter`, `InspectorPreviewSplitter`, `AiChatSplitter`, the sheet strip's): on the transparent default the panels read as loose boxes with nothing to grab (owner, #559). `Perf/AiChatDockUiPerfSmokeTests.EveryEditorSplitter_IsADrawnSeam_ThatThePointerFinds`.
- In `MainWindow.axaml`'s bottom `InspectorSplitGrid` (`*,6,2*`; the Inspector column has `MinWidth="320"`, the dock's own, so a narrow editor — 1280 px with the AI chat open — never arranges the dock past its left edge) the `BottomDock` and the 3D-preview `Border` **stretch to the full width when the sibling is empty** — bind their `Grid.Column`/`Grid.ColumnSpan` to `MainWindowViewModel.InspectorColumnSpan`/`PreviewColumnStart`/`PreviewColumnSpan` (derived from `IsInspectorOnlyVisible`/`IsPreviewOnlyVisible`), never hard-code `Grid.Column`, or an emptied dock leaves a dead-black quadrant. The `DockControl` stays alive underneath (covered, not width-collapsed) so it's still a hit-testable redock target once a pane returns.
- **The bottom dock's HEIGHT has a floor, and the timeline pays for it on a short window (#211).**
  `MainSplitGrid`'s row MinHeights (and `SheetSplitGrid`'s timeline row's) are re-derived on every
  size change of the split by `Views/Shell/MainWindowShellLayout.ApplyEditorRowFloors` from the pure
  `Views/Shell/EditorRowFloors` — the ONE writer of those three outside the gameplay presentation
  (`MainWindowLayoutController.ApplyBottomDockRowSizing` only writes the rows' `Height`). The dock is
  guaranteed 300 px; the timeline row gives way from 360 to 270 px to pay for it, and only below that
  does the dock shrink again (never under the markup's 80). On 1920x1080 and 2560x1440 nothing binds.
  The floors are not persisted, so a saved split gets them too. Pinned by `UI/EditorRowFloorsTests`
  and `Perf/EditorShortWindowUiPerfSmokeTests`.
- Layout persists as a versioned `DockLayoutState` snapshot inside `AppConfig` (per-pane mode + float bounds + active tab); View → Reset Layout clears it. Don't serialize the dock object graph.
- **A float window's bounds are the PLATFORM's, so `CaptureState` calls
  `IDockWindow.Save()` before reading them.** `X/Y/Width/Height` on the model are
  plain fields; Dock.Avalonia refreshes them from the host at a drag-END and from
  the host window's own close. The app snapshots in `MainWindow.OnClosing` —
  before any host window closes — and a RESIZE raises no drag at all, so without
  the explicit `Save()` the persisted bounds were the ones the window was CREATED
  at: a float dragged to the second monitor came home to the middle of the first
  one at its opening size. Regression: `DockLayoutControllerTests
  .CaptureState_ReadsTheFloatWindowsLiveBounds`.
- **A gameplay run must not re-dock a floated pane (#285).** The run hides panes
  through their visibility FLAGS, and a flag turned back on adds its pane to the
  tool dock. `Views/Shell/GameplayDockSnapshot` captures both controllers on
  `PropertyChanging(IsGameplayPresentation)` (before `OnIsGameplayPresentationChanged`
  hides anything) and restores them on the `PropertyChanged` that ends the run.
  Regression: `DockLayoutControllerTests.AGameplayRun_LeavesAFloatedPaneFloatingWhereItWas`.
- **`MainWindowViewModel.IsBottomToolAreaVisible` must name exactly the
  visibility flags of the panes registered in `InitializeBottomDock`** — i.e.
  `IsInspectorVisible`, and only that, since 0.42.4. It kept `|| IsStemMixerVisible
  || IsPhraseCatalogVisible` after those two became inspector TABS, and a tab flag
  cannot hold the dock open: open the Riff Library (which turns both flags on),
  close the Inspector, and an EMPTY `DockControl` held its third of the bottom row
  with `IsPreviewOnlyVisible` false, so the 3D preview could not widen into it
  either. Closing the Inspector now also clears both tab flags
  (`OnIsInspectorVisibleChanged`) — they are what the View menu checks and what the
  keybinds TOGGLE, so a stale `true` showed "Riff Library ✓" with no riff library
  and made the keybind's first press a no-op. Both pinned by
  `DockPaneContractTests`, which derives the flag set from the registry itself.
- **A saved layout is UNTRUSTED INPUT and must never brick startup.** It
  round-trips through JSON, and `MainWindow.RestoreDockLayout` runs inside the
  `Loaded` handler — where an escaping exception silently takes
  `MainWindowPrewarm.PrewarmTopLevelViews`, the focus anchor, the gameplay-layout wiring and the
  autopilot arm down with it, i.e. costs the user working keyboard focus and the
  entire responsiveness budget without a single visible error. Four shapes JSON
  produces that `CaptureState` never writes are handled explicitly in
  `DockLayoutController.RestoreState`, each falling back to the DEFAULT layout
  with a logged reason rather than aborting the restore: a **null pane entry**
  (`"Inspector": null` — the dictionary value is non-nullable in C#, which is
  exactly why nobody checked, and the old code went straight to `s.Mode`), an
  **unrecognized `Mode`** (previously anything `!= "Closed"` counted as "show
  it"), a **pane id this build no longer registers** (Stem Mixer / Riff Library
  were panes until 0.42.4), and **float bounds that are NaN, infinite or
  1e12** — a window restored at NaN is nowhere and the pane simply reads as
  lost. A version mismatch also LOGS now instead of returning silently.
  Regression: the `RestoreState_*` / `SanitizeBound_*` cases in
  `DockLayoutControllerTests`.
- The 3D Preview and the chart timeline stay outside the Dock.Avalonia system, but both support a **pop-out window** instead: the 3D panel header's pop-out button moves the single `Preview3DView` instance into a plain `Window` (composed-only: a plain re-parent — the detach tears down the compositor surface + pump, the attach rebuilds them; the Vulkan device/pipelines never notice, and there is no Vulkan surface to re-target), and View → Float Chart Timeline does the same for the `TimelineGrid` composite (composed-Vulkan backend: detach tears down pump + per-viewport renderer, attach rebuilds them against the new TopLevel's compositor — the shared 2D device survives). Never create a second instance of either view. `DOTCHARTER_UI_AUTOPILOT=float-timeline` scripts this pop-out/redock cycle for verification (Avalonia menus are not UIA-drivable).
  **"Never a second instance" is now linted, not just asserted in prose**:
  `DockPaneContractTests` counts `<…:Preview3DView` elements across every
  `.axaml` (exactly one), `new Preview3DView(` in every `.cs` (zero), and
  `x:Name="TimelineGrid"` / `"MainChartRenderer"` / `"Preview3DPane"` (one
  each). `ChartRenderer` itself is deliberately NOT unique — `StackedTracksView`
  draws one per arrangement — so the NAMED instances are what is pinned.
- **The two pop-outs are not dock panes, so `Rebuild()` cannot see them — every
  "bring it home" path has to name them separately.** Two consequences, both
  fixed in 0.58:
  - **View → Reset Layout** brought the dock panes home and left a popped-out
    timeline or 3D preview floating over a main window still showing the
    "popped out" placeholder — the same orphan shape the checklist below already
    tested for on the dock side. `ResetLayoutToDefaults` now closes both, via
    `MainWindow.ClosePopOutWindows()`.
  - **Entering the GAMEPLAY PRESENTATION is the second such path, and it was
    missed until 0.60.** The run is presented in the 3D preview PANEL —
    `MainWindowShellLayout.ApplyGameplayLayout` collapses the timeline row and
    the tool dock so that panel owns the window — so starting a run with the
    panel popped out counted the user in against a main window showing the
    "popped out" placeholder, with the highway drawing in a small window off to
    one side. The timeline pop-out is the mirror case: that method's restore
    branch turns `TimelineSlot` visible again unconditionally, which uncollapses
    an empty slot beside its own placeholder. The enter branch calls
    `ClosePopOutWindows()` first. Pinned by `DockPaneContractTests`
    (`EnteringTheGameplayPresentationClosesThePopOutWindowsToo`, which reads the
    ENTER branch only, plus a shared assertion that the helper closes BOTH).
  - **Closing the main window** closes them too (they are owned via
    `Show(this)`), which runs their `Closed` handler, which re-parents the
    single instance back into the dying window: for the 3D panel that is a
    native child HWND re-attached — and its render loop restarted from
    `OnAttachedToVisualTree` — against a top-level mid-teardown, and for the
    timeline it is a `Focus()` on a closing window. `OnClosing` latches
    `MainWindow._shuttingDown` and closes both BEFORE the persist steps; both
    handlers bail on that flag. Pinned by `DockPaneContractTests`
    (`BothPopOutCloseHandlersBailOutOnTheShutdownPath`,
    `OnClosingLatchesTheShutdownFlagBeforePersistingTheLayout`,
    `ResetLayoutClosesThePopOutWindowsToo`).
  - A **popped-out pane keeps its singleton subscriptions** because the moved
    subtree is DETACHED then RE-ATTACHED, and every control in it re-subscribes
    from `OnAttachedToVisualTree` — which is the same rule as the leak guard
    above, read from the other end. A control that unsubscribes on detach but
    subscribes only in its constructor would go dead on the first pop-out; that
    is why the guard demands the pair, not merely the cleanup.
- **A floated pane has TWO ways home that do not need a drag (2026-09-24).** Dock.Avalonia's
  tool-chrome menu enables "Dock" only for a tool with an `OriginalOwner` and runs
  `PinDockable` — and it sets `OriginalOwner` only when a tool is AUTO-HIDDEN, never when it
  floats, so in a float window "Dock" was always greyed out and a drag was the only way back
  (the owner found none). `DotCharterDockFactory` now records the dock a float left as its
  `OriginalOwner` (`RememberHome`, from `FloatDockable` and `SplitToWindow`), a `PinDockable` on
  such a tool takes it home (`DockHome`), and the flag is cleared again on any re-dock — a
  DOCKED tool with an `OriginalOwner` has "Auto Hide" disabled. The float window is titled with
  the pane's name (the menu float titled it "IToolDock"). Second way: the spot a floated pane
  left shows a card ("The inspector is in its own window" · "Dock it back here") bound to
  `IsInspectorFloating` / `IsSheetMusicFloating`, which `DockLayoutController` sets on every
  layout change (`DockPaneDefinition.SetFloating`) and whose button calls `DockPaneHome`. The
  card is centred, not a cover, so the rest of the dock stays a drop target. Pinned by
  `DockLayoutControllerTests` (`DockPaneHome_*`, `ThePaneMenusDock_*`) and `DockHomeCardTests`.
- `DotCharterDockFactory` overrides `CloseDockable`/`FloatDockable`/`PinDockable` to work around verified Dock.Avalonia 12.0.0.2 model bugs (null close target on emptied chrome; pinned-flyout drag-out floating the preview dock and duplicating the pane; stale `ActiveDockable` after pin). Do not remove these when upgrading the package without re-verifying upstream (issue #1053).
- Custom-drawn controls hosted in panes must unsubscribe from singleton services in `OnDetachedFromVisualTree` — pane views are re-created on float/redock (see `SheetMusicView`). Enforced in the default gate by `ViewSubscriptionLeakGuardTests` (every class that declares its own `OnAttachedToVisualTree` must declare its own `OnDetachedFromVisualTree` — a base class's detach does not cover a subclass's attach; allowlist requires a review comment).
  **That reflective sweep had two holes, both closed in 0.58.** (1) It never
  asserted it had *found* anything — a change to its `Control` filter, its
  hierarchy walk or its assembly would have made it inspect zero types and still
  report success; it now floors the scan (measured 2026-08 by flooring at 9999
  and reading the failure: 18 controls, all pairing).
  (2) It only ever looked at method OVERRIDES, so the five controls
  that wire the same attach-time work from their CONSTRUCTOR
  (`AttachedToVisualTree += …` — `BendEditorControl`, `AmpFxRigView`,
  `VstRackView` (the shared plugin rack; `AmpFxPanel` wired it until 2026-09-27),
  `MixPopoverView`, `ToolsPopoverView`) were inspected by nothing
  at all. `SourceLint_FilesWiringAttachAlsoWireDetach` covers that form, and
  `SourceLint_EveryChartThemeSubscriberUnsubscribes` covers the sharpest case of
  the rule — `ChartTheme.ThemeChanged` is a STATIC event, so a subscriber is
  rooted for the PROCESS, not merely for a service's lifetime (the composition
  root's own subscription is the one reviewed exception).

- `DockLayoutController.Rebuild()` (View → Reset Layout, and the fallback in `SetPaneVisible`) **tears the previous layout down first**: it detaches the old factory's `DockableClosed`/`WindowClosed` handlers, then `Exit()`s and clears the old root's float windows. Skipping either half left a stranded `HostWindow` over the same view model the rebuilt docked pane presents — invisible to `CaptureState`, so lost on restart — whose eventual close ran the OLD factory's handler against the NEW layout. That is why those two handlers are named methods, not lambdas. Regression: `DockLayoutControllerTests` (model-level; a real float needs a `HostWindow`, so the host is stubbed).

Manual dock checklist after touching this area (dock drags can't be tested headless): float each pane → content renders (not black) → drag back over its dock → redocks; close a floating window → View-menu checkbox flips off → re-enable → pane returns docked; pin a pane → collapses to edge tab, flyout works, View menu still shows it checked; arrange panes, restart → layout restored; View → Reset Layout → defaults; **float a pane, THEN View → Reset Layout → the float window closes with it** (no orphan left on screen).

…and the same three for the two POP-OUTS, which the dock model does not cover:
pop the 3D preview out → it renders in its own window and the panel shows the
placeholder → close it → it comes home and renders again; pop the timeline out →
editing/keys still work there → View → Reset Layout → the window closes and the
timeline comes home; pop either out and **close the MAIN window → the app exits
with no stranded window and no crash on the way down** (the 3D one is a native
child HWND, so a re-parent into a closing top-level is a real swapchain race).
