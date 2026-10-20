# Shell chrome — menu bar, toolbar, status rail, popovers (UI deep reference) — the chart editor chrome grammar; the status rail's readouts and its one message; the activity rail

## The chart editor CHROME grammar (`Views/Shell/ShellChromeStyles.axaml`)

- **The chart editor's CHROME is on the Fire layer (0.50), through a THIRD
  converted grammar: `Views/Shell/ShellChromeStyles.axaml`.** It is included by
  exactly three views — `MainMenuView`, `ToolbarView`, `StatusBar` — and pinned
  at three by `UI/EditorChromeTests`; a fourth includer would restyle menus on a
  surface nobody has converted yet. It is held to the same lints as
  `DialogStyles.axaml` and `SettingsStyles.axaml` (Fire ladders only, no
  `Cursor`, no `Transitions`). Regions and marks live in `Fire.axaml`:
  `Border.FireMenuBar` (38) / `Border.FireToolbar` / `Border.FireStatusBar` (30),
  and `MenuItem.FireMenuTop` for a top-level header.
  - **A DROPDOWN'S CHROME CANNOT BE SET BY A STYLE SELECTOR, and the app shipped
    that mistake for several releases.** Avalonia's `MenuItem`/`ComboBox`
    templates set their popup Border's Background / BorderBrush /
    BorderThickness / CornerRadius / Padding from a `{DynamicResource …}`
    written INSIDE the template — a LOCAL value, which beats a style setter. So
    `MenuItem /template/ Popup Border { Background … }` in `Styles/Chrome.axaml`
    and its ComboBox twin in `Styles/Inputs.axaml` were silent no-ops, and every
    menu in the app kept opening in Fluent's `#1c1d28` at radius 5 while
    everything around it moved. Only the properties the template does NOT set (a
    `BoxShadow`, a `Margin`) ever applied, which is what made the rules look
    like they worked. **Override the KEY**: `Styles/FireMenuChrome.axaml`
    restates Fluent's popup keys in Fire values and the chrome grammar merges
    it, which scopes it (a resource lookup from inside a popup walks up to the
    control that opened it). Its literals are compared against the Fire tokens
    they restate by `UI/EditorChromeTests.EveryFluentPopupKey_RestatesItsFireToken`,
    and `NoStyleRule_StillPretendsToPaintAPopupPanel` stops a dead setter coming
    back anywhere in `Views/` or `Styles/`.
  - A setter on the CONTROL'S OWN properties (a `MenuItem`'s Padding, FontSize,
    CornerRadius) works normally, and a **view-scoped type selector DOES reach
    the items inside an open submenu** — that is why the grammar can carry a
    bare `MenuItem` rule at all.
  - **The bar itself is `Views/Shell/EditorMenu` (0.52), not `Menu`** — one
    override, for one bug. Avalonia's `Menu.Open()` raises `Opened`, and
    `DefaultMenuInteractionHandler.MenuOpened` answers it with
    `MoveSelection(First, true)`; on a POINTER open the handler has already
    selected the header under the cursor, so every click ran through item 0 on
    the way (recorded: `Tools.IsSelected=True → File.IsSelected=True →
    Tools.IsSelected=True → Tools.IsSubMenuOpen=True`). That is the owner's "for
    some reason File flashes up", and Fluent's own template paints the selected
    fill as a LOCAL value, so `MenuItem.FireMenuTop`'s transparent background
    does not hide it. `EditorMenuInteractionHandler` skips that step when
    something is already selected; an accepted open with no selection still
    lands on the first header. `EditorMenu.Open` separately rejects bar opens
    with the pointer elsewhere so Alt remains the editor's view modifier.
    Avalonia 12.1.1's reference assembly marks the handler's constructor and
    virtual hooks obsolete because the type is `[Unstable]`, with no replacement
    select-first hook. Two local warning scopes document this compatibility
    boundary; recheck them on Avalonia upgrades. The app's own public handler is
    not obsolete, and all other menu interaction stays inherited.
    **`StyleKeyOverride => typeof(Menu)` is load-bearing** — control themes are
    type-keyed, so without it the subclass gets no template and the whole bar
    renders as nothing. All of it is pinned by `UI/MenuFlyoutPrewarmerTests`.
  - **Every menu is GROUPED, not flat (0.52).** File / Edit / Tempo Map / View
    joined Tools: `Export`, `Select`, `Snap & Length`, `Start & End` and
    `Panels` are the new submenus, and Arrangements / Vocals / Help stay flat
    because 4-5 items do not need a level. The rules are pinned by
    `UI/MainMenuLocalizationTests`: ≤ 14 direct entries per top-level menu, and
    **no submenu may hold fewer than three rows** (the two Tools groups that
    hold two are named exceptions — they group by TARGET, and the pair is the
    group). A new group header is a resx key in BOTH files.
  - **One file per top-level menu (#653).** `MainMenuView.axaml` is the bar
    alone: the `EditorMenu` and eight elements, `<menus:FileMenu />` …
    `<menus:HelpMenu />`. Each menu — its `FireMenuTop` header and every row —
    is `Views/Shell/Menus/<Name>Menu.axaml`, a subclass of
    `Menus/EditorTopMenu`, whose `StyleKeyOverride => typeof(MenuItem)` is
    load-bearing: type selectors match on the style key, so without it every
    header still gets a template but loses `MenuItem.FireMenuTop`'s padding,
    type size and colour (measured at #653). Each file is its own
    namescope and none declares an `x:Name`; the prewarm reaches the menus
    through `MainMenuView.TopLevelMenus()`, and the style grammar still comes
    from `MainMenuView`'s include (the menu files include nothing, so the
    three-includer pin holds). Source lints read the whole bar through
    `DotCharter.Tests/Infrastructure/MainMenuSource` (one merged document,
    shaped as the single file was); `Perf/MainMenuDropdownUiPerfSmokeTests`
    opens every menu and measures its dropdown and rows.
  - **Tools has no "Prepare for Rocksmith" any more (#952)**: every export applies
    DLC Builder's fixes and levels to its copy, and the chart's context menu
    ("Apply DLC Builder fixes to the chart", palette id `tools.applyDlcBuilderFixes`)
    puts them into the project. Improve chart and Auto-fix arrangements each carry
    a tooltip saying how they differ.

## The status rail's editing-state readouts

- **`SNAP … · SEL n · ZOOM ×n` (+ `MASK d·d` while a string-edit mask is set)
  live on `StatusBar.axaml`'s metric cluster**, backed by the pure
  `EditStateReadouts` beside `StatusBarViewModel` (P1-11, pinned by
  `EditStateReadoutsTests`). Rules that keep the rail cheap: every source
  (selection bursts, per-wheel-notch zoom) funnels through ONE single-flight
  `IUiDispatcher.Post` at Background priority — never a per-frame binding; the
  MASK readout prints the Ctrl+DIGITS that set it (index+1) and is EMPTY while
  no mask is set (an empty mask edits every string and must not read as modal
  state); the ZOOM baseline is `UserSettingsState.PixelsPerMs`'s default and a
  test pins the two together; the strings are deliberately unlocalized like the
  rail's other mono read-outs — the localized part is the cluster tooltip.
  `TransportViewModel`/`ISelectionService` arrive as OPTIONAL ctor params — a
  host composed without them hides the cluster via `HasEditReadouts`.
- **The ⚠/⛔ chips are CURRENT chart lint counts, not the log tail (0.59).**
  Until then they counted the WARN/ERROR log lines in
  `StatusBarService.RecentMessages` — a since-launch tally that climbed during
  pure library browsing (the PSARC library service alone had eleven Warning
  sites). Now the shell pushes the inspector's debounced validation result into
  `StatusBarViewModel.SetChartIssueCounts` (Important ⇒ the danger chip,
  Warning ⇒ amber), a fresh empty project shows 0/0, and clicking a chip opens
  the Issues tab through the `OpenChartIssues` delegate. The session-log flyout
  (with Clear All) moved to its own NEUTRAL icon-only chip
  (`HasRecentLogMessages`) — log noise must never wear the lint colours.
  Pinned by `StatusBarLintBadgeTests`.

## The top-level nav is the ACTIVITY RAIL

- **`Views/Shell/ActivityRailView`** — 76 px down the left edge of the whole
  shell, spanning `TitleContentGrid`'s title-bar row AND its content row, so
  the window edge reads as one column from the top down to the status rail.
  Its grammar is `Styles/Fire/FireShellRail.axaml` (`FireRailToggle`,
  `Border.FireActivityRail`, `.FireRailItem/Stack/Glyph/Caption/Rule`) — in the
  Fire tree, because `UI/FireStyleClassTests` requires every `Fire*` selector
  to live there.
- **Every DESTINATION is a `RadioButton`**, two-way through `IntEqualsConverter`
  onto `MainWindowViewModel.SelectedMainTabIndex`. Never a ToggleButton: one
  unchecks itself when the current destination is clicked again and the rail
  then lights nothing at all. The rig row IS a `ToggleButton` and is outside the
  group on purpose — it is an overlay over whichever destination is showing, and
  it is the rig's only door in BOTH directions: the rig draws no ✕ in the
  editor (`AmpFxRigViewModel.ShowCloseButton`, false only there).
  `UI/EditorChromeTests` asserts the RadioButton rule per ROW and reads the tab
  indices off the shell's own constants; `UI/RigDismissalTests` owns the rig's
  doors.
- **Chrome is ONE flag: `MainWindowViewModel.IsShellChromeVisible`**
  (`!IsGameplayPresentation && !IsHomeVisible && !IsPlayModeVisible`). The rail,
  the title bar and the status rail all bind it, because they are one decision.
  Home and Play Mode are FULL-BLEED; the chart editor, the library and settings
  carry all three. The title bar's ROW height is bound too
  (`TitleBarRowHeight`) — hiding a control inside a fixed 38 px row keeps the
  band, and a full-bleed screen would start 38 px down under nothing.
- **PLAY IS NOT A RAIL ROW.** It was one for a single commit in 0.62 and came
  straight back out: the rail does not exist on the Play surface, so the row
  would light itself and then vanish with the control it lives on. Home's
  destination card carries the route; the File menu is the second door.
- **It replaced `MainNavSwitcher`** (a three-segment strip in the 38 px menu
  bar) and the separated "back to Play Mode" button beside it, both 0.50–0.62.
  It is NOT a return of the 54 px icon rail 0.50 deleted: that one was panel
  toggles the View menu already carried; this one is destinations only.

## The status rail's ONE message carries a severity (`Views/Shell/StatusBar.axaml`)

- **The left cell is a `DockPanel`, never a horizontal `StackPanel` (#130).**
  The StackPanel measured the message with infinite width, so its ellipsis
  never engaged and the message plus the task chip painted over the metric
  cluster (1280 px always, 1920 px with a task). Now `StatusTaskChip` docks
  right and is measured first (its label trims; `MinWidth` = its clock + bar +
  cancel, so it never overprints its own parts), and `StatusMessageLine`
  (glyph + message, clipped as a unit) takes what is left and trims. At
  1280 px the metric cluster takes ~1,100 px, so with a task running the
  message has no room and is hidden until the task ends. **The cluster yields
  to a running task in a Compact window (#213):** `:is(Window).Compact
  StackPanel.StatusEditReadoutsSlot.TaskRunning` hides the Snap / selection /
  zoom read-outs (their WRAPPER — the read-outs' own `IsVisible` is a local
  binding a style cannot outrank; and `StackPanel`, not `Panel`, because a
  type selector matches the exact type), so the task's name shows instead of a
  bare bar; the chip's tooltip is the full `ActiveTaskLabel`. The chip's `ProgressBar` needs `MinWidth="0"` (Fluent's
  200 px minimum outranks `Width`). Pinned by
  `Perf/StatusRailNarrowWidthUiPerfSmokeTests` (1280 / 1920, with and without
  a task; also the Library footer's load status vs. the Load button).
- **The audio readout and the "+N more" task suffix are resx keys**
  (`StatusBar_Audio_NotReady`, `StatusBar_Audio_Fmt`, `StatusBar_Task_MoreFmt`)
  formatted through the injected `ILocalizationService`; pinned by
  `UI/ViewModels/Application/StatusBarLocalizedReadoutTests`.

- **`ProjectState.MainDisplayMessage` is the rail's left-hand line, and it is
  written from ~225 sites** (`_projectState.SetMessage(…)` and
  `MainDisplayMessage = …`, across the coordinators, the shell view model and
  every import/export dialog). Until this landed all of them set the same
  faint mono string, so a save that failed inside a `catch` read exactly like
  one that worked. **The line now carries a `StatusSeverity`**
  (`Services/State/StatusSeverity.cs`: Info / Success / Warning / Error)
  beside the text:
  - The API is ADDITIVE. `SetMessage(string)` and the `MainDisplayMessage`
    setter are **Info** and reset a previous level; a failure or completion
    path names its level — `SetMessage(text, StatusSeverity.Error)`. Never
    write the level on its own: `SetMessage` raises severity BEFORE the text
    so a listener refreshing on the message already sees the right colour.
  - `StatusBarViewModel` mirrors it as `IsMainMessageError` / `…Warning` /
    `…Success` / `HasMainMessageGlyph`; the view binds those to
    `Classes.err` / `.warn` / `.ok` on `StatusMessageText` and on the
    `StatusMessageGlyph` `PathIcon` (alert for Error/Warning, check for
    Success). Brushes are the Fire semantics — `FireDanger` / `FireWarning` /
    `FireSuccess`; Info stays `FireMono Faint`. **The glyph's geometry is a
    STYLE setter keyed on `PathIcon#StatusMessageGlyph`, never a local
    `Data`** — a local value beats every class setter and the alert could
    never replace it.
  - **Routine news leaves the rail; a problem stays** (2026-09-23). Info and
    Success fade after `StatusBarViewModel.MainMessageFadeMs` (8 s) on the rail's
    once-a-second timer; Warning and Error stay until something replaces them,
    because they still need the user. The line used to keep the last thing said
    forever ("Loaded Chart: project.rscp" for a whole session). The history is
    in the log chip. `StatusSeverityTests.RoutineNews_LeavesTheRail_ButAProblemStays`.
  - **A message may carry ONE verb** (#638): `SetMessage(text, severity,
    new StatusMessageAction(label, run))` puts `StatusMessageActionButton` right
    after the text ("Show" on a lossy Editor on Fire export opens the Chart
    quality report on its round trip). The verb belongs to that message: every
    later write, with or without a verb of its own, replaces it
    (`ProjectState.MainDisplayAction`). The text and the verb sit in a
    LEFT-aligned `*,Auto` grid, so the verb follows short text and a long
    message trims before the verb does
    (`StatusRailNarrowWidthUiPerfSmokeTests.AMessagesVerb_FollowsTheText_AndOutlastsTheTrim`,
    `StatusSeverityTests.AMessagesVerb_IsOfferedWithIt_AndGoesWithTheNextWrite`).
  - **There is no auto-clear timer.** An Error stays lit until the next writer
    replaces the line, which is the intent: a failed save must not fade among
    the neutral messages. Do not add one.
  - **…but it is ONE slot, so it is never the only route to an action.** The
    startup update check wrote "Update {0} is available — install it from
    Settings ▸ Updates" here and nowhere else, and the next writer — an
    autosave, a project open, "General settings applied." — erased it
    permanently, leaving a PRIMED update dialog four navigations deep with
    nothing pointing at it. A message that asks the user to do something needs
    a durable surface beside it: 0.61 added **Help ▸ Check for updates…** and
    the `help.checkForUpdates` palette row, both on
    `HelpMenuViewModel.CheckOrShowUpdateCommand` (`MainWindowViewModel.Help`) over
    `Services/Update/UpdateCheckFlow.CheckOrShowAsync` (re-open what the
    startup check found, else check now and put the answer on the status line —
    one destination for both entry conditions). `UI/UpdateEntryPointTests` pins
    both surfaces.
  - **Every `catch` that writes the line writes Error, and
    `UI/StatusSeverityLintTests` fails the gate otherwise** (it walks each
    `catch` body in `DotCharter.UI` + `DotCharter.Services` source and requires
    `StatusSeverity.Error` on any `SetMessage(` / `MainDisplayMessage =` inside
    it, plus Error on any write whose loc key ends in `Failed` / `Error`).
    "…saved / …exported / …imported / …built" writes are Success. Everything
    else stays Info — a precondition refusal ("No chart loaded") is not a
    failure of the operation the user asked for.
  - Guarded by `Services/State/StatusSeverityTests` (state contract + view
    model flags, default gate) and
    `Perf/ShellConstructionUiPerfSmokeTests.StatusBar_drawsErrorAndSuccessInADifferentBrushThanInfo`
    (the real view over the real view model resolves a different Foreground
    per level and a resolved glyph — run by hand after touching the rail).


## The three rails at the WIDE breakpoint

**The rails keep their height and step their CONTENT.** They are the one part of
the app pinned to the window's EDGES rather than centred, so the 2026-09-12
resolution audit's usual finding — a centred column the same pixel width at
1080p and 4K — is not what was wrong with them. Density was: a 38 / 44 / 30 px
rail of 9.5–13 px type and 14 px glyphs is a correct design at 1920 and a row of
specks at 3840. Nothing became a capped strip, and `FireMenuBarHeight` 38, the
tool rail's own 44 px floor and `FireStatusBarHeight` 30 are asserted UNCHANGED
across the switch.

Everything lives in `Views/Shell/ShellChromeStyles.axaml` (the file the three
views include) except the window chrome (`Views/Shell/WindowChromeView`: title bar, back bar,
window caption), which includes no grammar and is dressed by the themes its
controls wear (`FireMenuBarBorder`, `FireGoToAnythingButton`, `SubtleButton`,
`WindowBtn`). `MainWindow.axaml` has NO `Window.Styles` (#412): that scope also
covers Play Mode, the song browser, Settings and every prewarmed dialog, so any
rule there is attached to every control of its type in all of them
(`UI/StyleIncludeScopeTests`).

| At `:is(Window).Wide` | default | wide |
|---|---|---|
| `Border.FireToolbar PathIcon` | 14 | 17 (square, and asserted square) |
| `… PathIcon.TbCaret` | 10 | 12 (stays under the glyph it hangs off) |
| `StackPanel.TbGroup > Button / ToggleButton / ComboBox` | 28 | 32, inside the same 44 px rail |
| `StackPanel.TbGroup > TextBlock`, the rail's ComboBox | `FireSizeCaption` | `FireSizeBase` |
| `MenuItem.FireMenuTop` | `FireSizeCaption` 12.5 | `FireSizeMd` 14 |
| `Border.FireStatusBar TextBlock.FireMono` / `.Faint` | 10.5 / 9.5 | `FireSizeSm` 12 / `FireSizeXs` 11 |

**The tool rail's 24 glyphs had to LOSE their `Width="14" Height="14"` first.**
That is a LOCAL value and outranks every style setter, so the breakpoint rule
would have been dead on all 24 while reading as applied. They are declared once
in the grammar now; the four disclosure carets gained a `.TbCaret` class for the
same reason.

**Nothing here reaches into a DROPDOWN, deliberately.** A `MenuItem` row, a
`FlyoutPresenter` and a `ComboBox` popup are hosted in their own `PopupRoot`
under the native-swapchain opt-out (see the airspace rule in
`DotCharter.UI/AGENTS.md`), where a selector rooted at the shell `Window` matches
nothing. A rule that works on one presentation path and dies on the other is the
silent half-fix this layer keeps paying for, so the dropdown rows keep one size
on both paths.

**`.Faint` is a second rung and must be stated separately.** One
`Window.Wide TextBlock.FireMono` rule raised the rail's quiet read-outs to the
same size as the loud ones and deleted a hierarchy that exists at 1080p; the
variant rule follows the base one (last matching style wins).

Evidence: `Perf/UiResolutionAuditUiPerfSmokeTests
.TheShellChrome_StepsUpItsContentButNotItsRails`, at 1920x1080 and 3840x2160,
asserting the exact DEFAULT of every number above as well as the step — a case
that only checked "wide is bigger" would pass on a change that moved the laptop.
