# Rendering / perf — startup work and hidden-surface prewarm — hidden-surface prewarm: the four cases and the drain order

## Hidden-surface prewarm — the four cases, and the drain order

- **Every drain step waits for QUIET input (0.49.4, `Views/Shell/PrewarmPacer`).**
  On the owner's release build the window was interactive at 1.5 s and the
  three drains then ran for ~9 s more (chrome 0.4 s, menus 0.5 s, popovers
  0.5 s, 79 hidden surfaces over 6.7 s). A step is one surface realized on the
  UI thread and cannot be preempted once started; `Background` priority only
  keeps it from STARTING while input is queued, so mouse movement in those
  seconds landed between steps and read as "laggy right after launch". A
  loading screen was considered and rejected — it hides the lag by taking the
  9 s away from the user. The pacer posts a step only once the last
  pointer/keyboard input (noted by `MainWindow`'s tunnel handlers) is 250 ms
  old, re-arming on a one-shot timer otherwise, and caps the deferral at 8 s
  from the request so a continuous drag cannot starve the drain. The rule is
  the pure `ShouldRunNow`; `UI/PrewarmPacerTests` pins it and lints that all
  three drains post through `PrewarmPacer.Post`, that the window feeds it, and
  that `HeadlessSessionFixture.Get()` sets `QuietWindowMs = 0` — its bounded
  drain runs queued jobs, not timers. `DOTCHARTER_NO_PREWARM_IDLE_GATE=1`
  is the product-side A/B. **Not measured on a real desktop by the session
  that wrote it** — the owner's next launch is the test.
  - **The pacer asks TWICE: at post time and when the step's turn comes (#234,
    `PrewarmPacer.TryRun`).** A step posted while the input was quiet then
    waits in the `Background` queue; a click lands, is handled at `Input`, and
    the queue is idle until the render tick — so the waiting step ran BETWEEN
    the click and the frame that shows it. On the real window (Release R2R,
    `ShellSwitchProbe`) that one step was the whole of the "slow first entry":
    Home → Chart 109-148 ms, Library → Play 74-278 ms, where the switch itself
    is 3-6 ms. Pinned by `UI/PrewarmPacerTests` (the rule, and a lint that the
    pacer posts the second look and never the bare step).
- **The drain's LIST is produced inside the drain, one item per turn (#234).**
  `HiddenSubtreePrewarmer.ScheduleDrain` takes a lazy `IEnumerable<Control?>`
  (`null` = a turn that only built something), and
  `MainWindowPrewarm.TheRestOfTheHiddenSurfaces` yields: one turn per top-level
  view it creates (Home, Library, Settings, Play Mode — one `Ensure*Created` each,
  so a switch also builds only the view it shows), the hosts, then per owner one
  turn per `IPrewarmConstruction.ConstructionSteps()` step, one for its
  `HiddenSurfaces()` listing, then its surfaces. Up front, that listing was ONE
  turn of 0.7-1.9 s (Play Mode's deferred bodies and the rig view, 34 dialog
  views, the three top-level views) that a click could not interrupt.
  `PlayModeView` and `DialogContainerView` are the two `IPrewarmConstruction`
  owners; an owner whose `HiddenSurfaces()` BUILDS views must be one
  (`UI/HiddenSubtreePrewarmerTests.AnOwnerThatBuildsToList_HandsTheBuildingToTheDrain`).
  Behaviour pinned headless by `Perf/LazyPrewarmDrainUiPerfSmokeTests`, the
  structure by `TheRestOfTheDrain_IsProducedInsideIt_OneTurnPerBuildAndPerListing`.
  - **The owners are listed AFTER the hosts are realized, and that fixed a hole.**
    `ScreenTabHost.HiddenSurfaces()` lists nothing until a layout pass has
    attached it under its `ScreenHost`; the up-front list ran before the
    settings host was ever measured, so every settings tab but the first stayed
    COLD (Keybinds ~600 ms, Appearance ~470 ms of first-click realization). The
    owner list is taken once, at its turn — never walked lazily, because the
    drain changes the visual tree between two yields.
  - **Owner order:** Play Mode and the Library before the dialogs — both are one
    click from Home; a dialog needs the editor and a menu first.
  - **A Play Mode boot drains Home and its destinations FIRST, ahead of the shell
    chrome** (`MainWindowPrewarm.HomeAndItsDestinations`: Home, Song Select, the
    Library, Settings — built and realized one per turn): the Start screen leads
    to Home, the editor chrome is not even on screen, and a user pressing Start in
    the first seconds otherwise paid Home's construction and first layout
    (~300 ms) behind a drain still busy with the editor, and a first Settings
    350-1,200 ms.
- **A long list inside a surface is realized a CHUNK per turn (#253).** One
  surface was still one turn, however big: the rig's tone browser (every preset
  card, non-virtualized on the owner's call — a virtualized grid lags on scroll)
  was a single 1.1-1.9 s turn PER RIG INSTANCE (Release R2R), Keybinds ~600 ms,
  Appearance ~400 ms, the inspector's Mixer ~250 ms. Their item panels are
  `IChunkedPanel`s (`DotCharter.Tone.UI/Controls/`): `ChunkedWrapPanel` (the
  tone cards, `ChunkSize="3"`), `ChunkedStackPanel` (the Keybinds and Appearance
  groups, one a turn) and `MasonryPanel` with a `ChunkSize` (the Mixer's
  sections; 0, the default, does not chunk). Their shared state is
  `HiddenChunkGate`: while the panel is NOT effectively visible and has never
  been shown, a measure lays out only the granted children, each with the exact
  constraint the base panel gives it (so the real layout finds their measures
  valid), and reports the WHOLE list's estimated size (a scroller sizes its bar
  from it). `ScheduleDrain` follows a realized part's settle turn with
  `HiddenSubtreePrewarmer.RealizeNextChunk` turns — grant one chunk, then
  `RealizeStale` the part — until no panel under it holds anything back;
  `CollectStale` skips the held-back children (`IChunkedPanel.Withholds`). The
  first SHOW lifts the limit for good and re-measures the panel (an
  `EffectiveVisibilityWatcher`), so a user faster than the drain gets every card,
  laid out by that show as before. Never writes `IsVisible`, never changes a
  look. Pinned by `UI/ChunkedPanelTests` (per-turn counts, one measure per card,
  the size estimate, the one-chunk-per-turn lint, which markup uses the panels)
  and `Perf/ToneBrowserChunkedPrewarmUiPerfSmokeTests` (the real rig drained
  turn by turn, a show before the last chunk, the show-time re-measure).
  `ScheduleDrain`'s `onStep` reports every turn's cost; `MainWindowPrewarm` logs
  each one of 50 ms or more as `Prewarm: long step <build|realize|settle|chunk>
  <part> <ms> ms.` — the list to read before the next "a click during startup
  waits" report.

- **`IsVisible=false` is free per frame and expensive ONCE.** Avalonia skips
  `MeasureCore` for an invisible element, so styles, `ApplyTemplate`, resource
  resolution and text layout are all deferred to the user's first switch into
  that surface — measured at 20-137 ms per settings tab, 1-41 ms per Play Mode
  screen and 2-102 ms per dialog, i.e. the whole of "switching views, opening
  dialogs and switching tabs always lags a little". **The fix is
  `Views/Shell/HiddenSubtreePrewarmer`**, drained after the first frame by
  `MainWindow.SchedulePrewarmOfHiddenSurfaces` at `DispatcherPriority.Background`,
  one surface per dispatcher turn. Real-shell A/B (`DOTCHARTER_NO_SURFACE_PREWARM=1`
  turns it off): editor→Settings 1245 → 8 ms, per-tab 84-434 → 2-40 ms, Play Mode
  screens 9-286 → 0.6-125 ms, dialog open 11-137 → 0.5 ms.
  - **The drain realizes each surface ONCE; content that arrives later is cold
    again (#141).** A hidden surface whose lists fill after the drain (the
    project opens behind the Inspector's Song tab) has its new rows laid out by
    the click, and `Realize` cannot fix that after the fact: the stale nodes are
    deep and the part's children still hold a valid measure, so it returns at
    once. `HiddenSubtreePrewarmer.RealizeStale` opens the path down to every
    stale node first (bounded passes, visible-reachable nodes only), and
    `Views/Shell/HiddenSurfaceRewarmer` calls it through `PrewarmPacer` when any
    `ItemsControl` in its surface changes while the surface is hidden, or when its
    owner calls `Request()` for a change no list announces (the Inspector does on
    every project / arrangement switch, #175). **A
    `VirtualizingStackPanel` cannot be prewarmed at all** — it realizes rows only
    inside its effective viewport, and a hidden control has none — so a list
    that should be warm stacks while short (`Converters/ItemsPanelConverters`).
  - **A new `IsVisible`-gated surface opts in by implementing
    `IPrewarmableSurfaces` on its view** and being reachable from
    `MainWindowPrewarm.PrewarmableSurfaceOwners()`. Both are pinned by
    `UI/HiddenSubtreePrewarmerTests` (default gate, platform-free).
    **Both halves are needed and neither fails loudly.** The shell had a
    `SongBrowserHost` branch for three releases while `SongBrowserView`
    implemented nothing: the branch matched no child, the browser's gated
    surfaces — including its **Library Doctor**, one of only four dialogs
    declared OUTSIDE `DialogContainerView` and therefore invisible to the dialog
    drain — were reached by no mechanism at all, and the reachability guard
    passed anyway because its escape hatch ("the method mentions
    `IPrewarmableSurfaces`") is satisfied by the method's own signature. It
    counts `yield return` branches against implementors now, which fails in both
    directions.
  - **A surface declared in `MainWindow.axaml` itself has no view to hang that
    interface on — it goes in `MainWindow.HiddenShellSurfaces()`**, which the
    drain consumes FIRST. That list is the **chart editor** (`MainSplitGrid`),
    its own gated child the **Sheet Music dock** (`SheetMusicDock` — the
    prewarmer does NOT recurse, so realizing the editor stops at its edge;
    parent before child, or the child measures against a slot nothing has
    arranged), the editor chrome — since #194 the HOSTS whose `ContentTemplate` body
    each rail is (`EditorToolbarHost`, the title bar `TitleBarRow` around
    `MainMenuBarHost` — both in `Views/Shell/WindowChromeView` since #654 and
    reached as `WindowChrome.TitleBarRow`, not by name — `StatusBarHost`;
    realizing a host is what BUILDS its body,
    see [startup.md](startup.md)), the rig overlay (`PedalboardOverlay`) and
    the **command palette**. The palette is the odd one out and worth reading
    before adding anything like it: the VIEW is always visible and the gate sits
    on its own root `Grid x:Name="Scrim"`, in a namescope `FindControl` cannot
    cross — so it is named in `MainWindow.axaml` and handed through
    `HiddenSubtreePrewarmer.HiddenContentOf`. Passing the view itself to
    `Realize` hits the "already visible" early return and prewarms nothing,
    silently. It carried no `x:Name` at all until 0.53, so the first
    `Ctrl+Shift+P` realized a ListBox-backed overlay from cold. With
    `StartInPlayMode` — the
    default — the editor subtree is never measured during boot, so the user's
    first switch into it paid its whole realization: **130 ms before, 37 ms
    after** (the second switch is 1.2 ms either way, which is what proves the
    cost is realization). The lookups are by `x:Name`, so a rename in the markup
    is a silent no-op; `HiddenSubtreePrewarmerTests` cross-checks every name
    (a `FindControl` string or a `WindowChrome.` member) against the window's
    markup (`MainWindow.axaml` + `WindowChromeView.axaml`, through
    `DotCharter.Tests/Infrastructure/MainWindowSource.cs`) and pins the editor as the first entry.
  - **A closed menu DROPDOWN is the third case, and neither mechanism above
    reaches it.** A `MenuItem`'s rows live inside a closed `Popup`, i.e. outside
    the visual tree entirely — realizing the menu bar lays out the eight
    headers and nothing under them, so the first click on each menu paid its
    whole realization: measured headless (Debug, JIT warm) at **File 81-104 ms,
    Edit 59-82, View 62-64, Tools 56-60, Tempo Map 30-40, Vocals 30-34, Help 29,
    Arrangements 26-31**, against 2-6 ms for every open after it. That is the
    owner's "50-100 ms lag when opening top toolbar menus".
    **`Views/Shell/MenuFlyoutPrewarmer`** pays it after the first frame
    (`MainWindow.ScheduleMenuFlyoutPrewarm`, drained from the hidden-surface
    drain's callback so the A/B switch still covers it), one MENU per
    `Background` turn: first open drops to **7-18 ms** (Tools, 33 rows over six
    groups, 25-50). Two rules make it work and both are pinned by
    `UI/MenuFlyoutPrewarmerTests`:
    - **Realize the ROWS, never the popup's child.** Measured detached, the
      popup's `ItemsPresenter` builds no containers at all (0 of 20 rows) and
      the first open still cost 120 ms. The `MenuItem`s are declared in the
      markup, so they already exist and are logical children of their parent
      item — `ApplyTemplate` + `Measure` + `Arrange` on each one styles and lays
      out the whole row, submenus included (depth-first).
    - **Never open anything to warm it.** That is the flicker this removes, it
      steals focus, and it deadlocks `Avalonia.Headless`. Source-linted for
      `IsSubMenuOpen`/`IsOpen`/`.Open()`/`Focus(`/`IsVisible`.
  - **A closed FLYOUT is the fourth case, and it is worse than a menu.** The
    tool rail's Mix / Tempo / Tools / Tracks and undo/redo previews, and the
    status rail's two message panels + profile switcher, are `Button.Flyout` /
    `FlyoutBase.AttachedFlyout`. Until the first open Avalonia has built **no
    `Popup` and no `FlyoutPresenter` at all**, so the declared content sits in
    the object graph with NO parent — hence no styles, no control themes, no
    templates — and measuring it where it stands realizes nothing (the same dead
    end as the menu popup's child). Measured headless (Debug, `r2r=no`): the six
    toolbar popovers cost **535 ms** of first opens cold (43-141 ms each) and the
    three status ones **68 ms**.
    **`Views/Shell/PrewarmableFlyout`** is what makes them reachable: it is a
    `Flyout` subclass that builds the popup by calling its own `ShowAt` and
    **cancelling the open in `OnOpening`** — Avalonia constructs the popup,
    presenter and logically-parented content *before* it asks whether the open
    may proceed, so `IsOpen` never becomes true and no popup window is created.
    The realization then STICKS, because the popup is created once per flyout
    and keeps the content parented. After the prewarm: **78 ms** (4.8-7.3 ms
    each once the JIT is warm) and **8 ms**. Rules:
    - **Measure the CONTENT, not just the presenter.** A `ContentPresenter`
      only builds its child from inside a layout pass it is part of, and a
      closed popup never runs one: measuring the presenter alone realizes 57
      visuals of scrollbar chrome and none of the popover.
    - **Every popover on those two rails must be declared
      `<shell:PrewarmableFlyout>`.** A bare `<Flyout>` is invisible to
      `MenuFlyoutPrewarmer.FlyoutsUnder` and silently keeps its lag. Linted by
      `UI/PrewarmableFlyoutTests`; the list itself is DERIVED from the markup
      (a logical-tree walk), so a new popover is prewarmed the day it compiles.
    - `MainWindow.ScheduleTransportFlyoutPrewarm` runs from the MENU drain's
      callback, which keeps it after the first frame and inside the
      `DOTCHARTER_NO_SURFACE_PREWARM=1` A/B switch.
    - **A CLOSE takes the popup out of the logical tree; the close puts it back
      (#176).** Avalonia's `HideCore` un-parents the `Popup`, and the next
      `ShowAt` re-parents it BEFORE `OnOpening` — so every repeat open re-styled
      and re-templated the whole popover inside the click, outside the
      Opening → Opened window the `Popover … opened in` log line measures. On
      the real window (perf autopilot, `DOTCHARTER_AUTOPILOT_POPOVERS=1`) that
      was **25-90 ms per repeat open against 2-5 ms logged**; headless Debug,
      140-215 ms. `PrewarmableFlyout.OnClosed` now posts, at `Background`,
      `RestoreClosedStructure`: re-parent the popup into its target's LIVE
      style scope, then `RealizeBuiltContent` — the state the startup prewarm
      leaves it in. Repeat opens: **~4-7 ms headless**.
      **The order is the whole fix.** A close-time re-measure WITHOUT the
      re-parent shipped once and drew every dropdown UNSTYLED: a detached
      subtree resolves every `DynamicResource` against nothing and caches the
      FALLBACK, and the layout-validity guard it shipped with stayed green
      because a view whose styles failed still arranges.
      `UI/PrewarmableFlyoutTests.A_close_re_parents_before_it_re_realizes` pins
      the order in the source, `Perf/PopoverReopenUiPerfSmokeTests` the parent
      and the realized body, and `Perf/PopoverRestyleUiPerfSmokeTests` compares
      the resolved BRUSHES across a close. The menus never had the residual — a
      repeat menu open is 0.6-1.4 ms.
  - **The 3D-preview panel is deliberately NOT in that list.** Its `Border` is
    gated on `Is3DPreviewActive`, and realizing it would run `Preview3DSlot`'s
    `ContentPresenter` — which is what ATTACHES `Preview3DView` and builds its
    Vulkan surface. Doing that behind the game shell trades a first-switch cost
    for a native-HWND/swapchain lifetime change; it is the residual in the 37 ms
    and must stay an explicit decision, not a side effect of the prewarm.
  - **The prewarm reads `IsVisible` and never writes it** (source-linted). On
    every real surface that property is owned by a binding, and `SetBpm`, `Beat`,
    `Fhp`, `TempoMapOperation` and `LatencyCalibrationWizard` all grab keyboard
    focus the moment their scrim turns visible and `SheetMusicView` schedules a rebuild. It
    realizes a hidden root by applying that root's own template and measuring its
    *visible* children — `Measure` only consults an element's OWN `IsVisible`.
  - **Order matters: a gated PARENT must be realized before its gated children.**
    `MainWindowPrewarm.PrewarmableSurfaceOwners` yields the settings
    `ScreenHost` before the owners nested inside it (the `ScreenTabHost` with
    every tab, and the tab views with an Advanced body of their own — each
    found through the LOGICAL tree, one `yield` per type, because
    `HiddenSubtreePrewarmerTests` counts one branch per implementor), because
    a tab measured before its slot has been arranged measures against the
    window-sized fallback and re-measures on the real show (4.3 ms median /
    23.6 ms worst per tab, versus 0.2 ms with the real slot).
    Same reason `PrewarmTheRestOfTheHiddenSurfaces` drains the hidden HOSTS first.
  - **…and the CHROME's dropdowns come before the dialogs, the settings tabs and
    the Play Mode screens.** The drain pays one surface per `Background` turn,
    and `Background` is below `Normal` — so the boot-time PSARC library scan,
    whose `IProgress` callbacks marshal every found batch onto the UI thread,
    sits in front of every step of it for as long as it runs (7.9 s / 2,160
    songs, cold). Until v0.55 the menu dropdowns and the two rails' popovers
    drained LAST, behind ~70 surfaces of that queue, so a user who clicked Tools
    or Mix in the first seconds still paid the full cold open — **46-175 ms per
    popover, 8-88 ms per menu** (`r2r=no`). They are also the only surfaces in
    the list that are ONE CLICK from the window the instant it appears; a dialog
    cannot be reached before the user has navigated to whatever opens it. The
    chain is `SchedulePrewarmOfHiddenSurfaces` (shell chrome) →
    `ScheduleMenuFlyoutPrewarm` → `ScheduleTransportFlyoutPrewarm` →
    `PrewarmTheRestOfTheHiddenSurfaces`, and the LISTING of that last block
    happens there too — `DialogContainerView.HiddenSurfaces()` MATERIALIZES 34
    dialog views merely to be listed, so building the list up front put that
    construction in front of the dropdowns as well. Pinned by
    `UI/HiddenSubtreePrewarmerTests.TheChromesDropdowns_DrainBeforeTheDialogsAndScreens`.
  - **A realized surface is SETTLED one turn later, or its virtualized lists
    stay one row deep** (#234). `Realize`'s direct `Measure` is not a layout
    pass, so a `VirtualizingStackPanel` inside the part has no viewport yet: it
    builds one row, and the viewport the next pass hands it invalidates the panel
    under a hidden root that nothing measures again. `ScheduleDrain` therefore
    follows every realized part with `HiddenSubtreePrewarmer.Settle` (the window's
    layout pass, then `RealizeStale`) on the next turn. Measured on the Amp & FX
    rig's gear list (51 rows): 1 row realized by the drain, the five visible ones
    (185 controls, ten faceplates, ~60 ms) built on the first open; now 6 rows in
    the drain and nothing on the click.
  - **A hidden control's `Render` still RUNS — so nothing it draws may decode on
    the UI thread.** The compositor records `Render` for every realized control,
    including those under an `IsVisible=false` root the drain has just laid out.
    The rig's faceplates and knob caps decoded their WebP art inside `Render`
    (`RigArtworkStore.Get`): settling the gear list moved 13 decodes, ~190 ms,
    from the first open into one render tick of the drain. Render overrides call
    `RigArtworkStore.GetForRender(file, width, this)` instead, which for a control
    that is not effectively visible queues the decode on the pool (two at a time),
    draws nothing and invalidates the requester when the bitmap lands; a VISIBLE
    render decodes synchronously as before. With both, the rig's first open went
    from 322 ms to 3.7 ms (medians of 3 and 5 runs, Release R2R, null audio,
    `[SwitchProbe] rig open`, 2026-10-05); repeats stayed ~2 ms. Pinned by
    `Perf/RigFirstOpenPrewarmUiPerfSmokeTests` (the drain leaves the open nothing
    to build; a hidden render queues, a visible one decodes).
  - **The drain paces one surface per `Background` turn on the assumption that a
    turn is a LAYOUT pass — so nothing a prewarmed view CONSTRUCTS may be a CPU
    job.** The rule was learned on the retired rack (deleted in v0.43, along with
    `RigTextures` and the procedural-texture machinery — the Fire rig draws no
    baked tiles), and it applies to whatever is prewarmed next.
    `PedalboardView` — a direct child of `PlayModeView`'s root, hence in
    `PlayModeView.HiddenSurfaces()` — baked 13 × 256² procedural texture tiles in
    its ctor: ~25 M noise evaluations of straight UI-thread CPU inside the
    boot drain. The fix was to bake PLAIN BGRA BYTES on a `Task.Run` worker
    and post only the `WriteableBitmap` wrap + `Resources[key] =` assignment
    back at `Background` priority; a `Task` field makes a second caller await the
    first bake. `{DynamicResource}` re-resolves a late assignment for free.
    The load-bearing half was that the bake touched NO Avalonia type — that is
    what makes the worker legal, and the default gate boots no platform, so a
    bitmap creeping
    back into it would have thrown.
  - **The two DEFERRED HOSTS are realized by being PARKED, and an unparked host
    must come back OPAQUE.** `MainWindowPrewarm.PrewarmTopLevelViews` is the one prewarm
    that really writes `IsVisible`: the song browser and the settings page are
    held visible at `Opacity 0` + `IsHitTestVisible=false` so their templates run
    without painting over the editor's native 3D HWND (a host that really painted
    here is the "settings bleeds into the 3D view" glitch). `MainWindow.ParkHost`
    / `UnparkHost` are that state, and `UnparkHost` restores the opacity whether
    the host is being shown or HIDDEN — **a host left at `Opacity 0` is not
    "hidden", it is a fully laid-out surface that paints nothing**, so the next
    switch into it is a blank window with no error and no log line. Two things
    make that reachable rather than theoretical, and both are now pinned by
    `UI/HiddenSubtreePrewarmerTests`:
    - The revert is a SECOND `Background` post, and `Background` sits under the
      boot-time PSARC scan's `Normal`-priority `IProgress` callbacks (7.9 s /
      2,160 songs). So `BindHostVisibility()` runs BEFORE the park, and its
      branches unpark — a LIBRARY or SETTINGS click inside that window takes the
      host out of the parked state itself. The guard reads the LAST
      `BindHostVisibility()` in the method, not the first: the boot-into-another-
      tab exit path binds and returns before any of this, and a first-occurrence
      search was satisfied by that call whatever the realize path did.
    - The pass writes no view-model flag to realize the settings page: the
      settings shell is the `SettingsHost`'s content from the first frame
      (`SettingsSurface.Create`, a `ScreenHost` showing `ISettingsShellScreen`),
      and the park/unpark alone lays it out. `SettingsSession.IsOpen` — what the
      shell and the app shell agree on — is written only by
      `OnSelectedMainTabIndexChanged` and by Save / Cancel.
  - **A gated surface that no mechanism was ever OFFERED is invisible to every
    guard above** — they check that the mechanisms are wired, not that anything
    reaches a given surface. `UI/PrewarmCoverageTests` closes that: it derives
    every `IsVisible`-gated element from `DotCharter.UI/Views/**/*.axaml` and
    requires each one of **35+ own elements** (40 until 0.49.3) to be either
    named in a prewarm list, swept by one of the three structural rules (dialog
    drain, Play Mode sibling sweep, legacy settings tab sweep), or listed
    in `KnownColdSurfaces` **with a reason**. Cost is counted in ELEMENTS, never
    milliseconds, and a surface's own count EXCLUDES the gated subtrees inside
    it — double-counting those made a container owning nothing but a tab strip
    read as the largest surface in the app. The list only ever SHRINKS: a stale
    exemption fails the suite too, and each structural rule is separately
    asserted to still match a real surface, so a renamed sweep target fails
    loudly instead of taking its surfaces cold with it (the old settings
    page's `LegacyTabGrid` rule went with that page on 2026-09-22). **One** entry is
    left on it — the rig board's `HasChain` subtree, gated by DATA so that a
    prewarm of the hidden state would realize nothing.
    - **A gated body NESTED inside a surface a mechanism already reaches is a
      one-line fix, not an excuse** (0.49.3). The prewarmer stops at the first
      hidden root on purpose, so "the parent is prewarmed" buys the body
      nothing — and five of the six exemptions used to say exactly that. Each
      owner now carries a nested-body name table that
      `HiddenSubtreePrewarmer.HiddenNamedDescendants` resolves AFTER the owner's
      structural sweep, so the gated parent is always realized first:
      `PlayModeView.NestedScreenBodyNames` (the Online tab body, the hub's
      Friends and Account sub-tabs, the Account tab's profile page, the results
      timing panel; the Trends sections and the Progress hub's run detail went
      with the hub on 2026-09-19), `DialogContainerView.NestedDialogBodyNames` (the Tone Designer's
      and Pattern Builder's selection-gated editors),
      the settings tabs' Advanced bodies (each migrated view offers its own through
      `IPrewarmableSurfaces` — `GeneralSettingsView`, `GraphicsSettingsView`;
      `LegacySettingsTabsView.NestedTabBodyNames` went with the old General tab on
      2026-09-22) and
      `InspectorView.NestedPaneNames` (the rail sections of the Mixer, Song and Lyrics tabs, #601). Resolution
      goes namescope → LOGICAL tree, never the visual tree: the drain lists every
      surface up front, before any parent is realized, and a nested
      `UserControl`'s markup is not a visual descendant until its own template
      applies. A name that resolves to nothing is skipped silently, which is why
      `UI/HiddenSubtreePrewarmerTests.Every_nested_body_name_is_declared_once_on_a_gated_element`
      pins each table's names to exactly one `IsVisible`-gated `x:Name` in the
      markup (proven: a misspelt entry fails it AND the coverage gate).
    - **The threshold is 35, measured.** On 2026-08-30 the bar at each candidate
      would have demanded `>=40 → 6`, `>=35 → 10`, `>=30 → 15`, `>=20 → 38`
      answers; the name tables above covered nine of the ten plus three from the
      30-34 band, so the bar dropped to 35 with the list SHRINKING (6 → 1). The
      order stays **empty the list, then lower the bar.** Between 35 and 30
      stand two surfaces no mechanism CAN reach — the 3D HUD's practice strip
      (the preview panel is deliberately never prewarmed) and
      the per-row LAN board body (removed with the LAN features on 2026-09-19)
      — so 30 costs two new excuses; the
      20-29 band is fifteen more, none over 28 elements. Not worth it yet.
  - **A DOCK pane can be an `IPrewarmableSurfaces` owner, and since 0.61 the
    inspector is one.** This file used to say the opposite ("the inspector is a
    DOCK pane, so NO `IPrewarmableSurfaces` owner reaches it at all"), which was
    true of the mechanism and false as a limit: the pane is materialized by the
    app `ViewLocator` inside `BottomDock`, so it is named in no markup
    `MainWindow` can `FindControl` — but a visual-tree walk of the dock finds it.
    `PrewarmableSurfaceOwners()` does exactly that, and finds nothing while the
    pane is closed, which is correct (the view does not exist until the click
    that opens it builds it). Two owners come out of that walk, **parent before
    child**: `InspectorView` (its seven inactive tab panels, swept from the
    `InspectorTabPanels` Grid) and then `SoundInspectorView` (its gated card
    bodies, eight since #572 — the technique cards were the two largest cold surfaces in the app at
    80 and 58 elements). Measured by `Perf/InspectorPrewarmUiPerfSmokeTests`
    (headless, Debug, `r2r=no`, so directional): the seven tabs' first shows cost
    **544 ms cold** (7-287 ms each) against **2.4 ms** prewarmed, and the five
    Sound cards **731 ms** (31-376 ms each) against **11 ms**. The prewarm itself
    is 688 ms, drained one surface per `Background` turn after first paint like
    everything else. (The Mixer, Song and Lyrics tabs' rail sections are the same
    shape one tab over; they ride `InspectorView.NestedPaneNames` rather than a
    third owner, and `HiddenSurfaceRewarmer` takes them as SECTIONS so a list that
    changes behind a hidden section is laid out before its rail entry is clicked.)
  - **The bare `<Flyout>`s that are left are a deliberate list, not a backlog.**
    16 of them under `Views/` as of 2026-08-29 (a 0.48 note said 17 — re-count),
    against 18 `PrewarmableFlyout`s, and `UI/PrewarmableFlyoutTests
    .Every_bare_flyout_outside_the_chrome_rails_is_accounted_for` requires each
    FILE to carry a reason. **Eleven of them would be actively wrong to
    convert**: the song browser's and Song Select's column facet funnels all
    carry `Opening="OnFacetFlyoutOpening"`, and that handler is what BUILDS the
    facet list from the loaded library — `PrewarmableFlyout` works by calling its
    own `ShowAt` and cancelling in `OnOpening`, which raises that handler, so a
    prewarm would run the facet build at BOOT against a half-scanned library. The
    other five: two in the 3D HUD (the preview panel is deliberately never
    prewarmed at all, so nothing can reach a flyout inside it), one nested inside
    the Tools popover (itself a `PrewarmableFlyout`, so unreachable until that
    one opens), and Play Mode's two profile chips.
  - Residual cost after the prewarm is **data-driven**, not template-driven: the
    Song Select list, the song browser and the StemModels tab still pay for
    realizing rows whose collections only fill later. Don't chase it with more
    prewarm — it needs the data, and the lists virtualize.
- Avalonia headless tests: never open real popups in prewarm paths (deadlocks `Avalonia.Headless`).


