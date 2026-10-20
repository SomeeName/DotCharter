# Fire — the Play Mode screens built on the layer

Part of the Fire design system. Router: [../fire-design-system.md](../fire-design-system.md).

## What is built on it

**The Play Mode front door** (`MenuDesign6Root`, from `Main Menu Sidebar.dc.html`)
was removed on 2026-09-22: Start now leads to the shell's Home. See
[../play-mode/chrome-and-screens.md](../play-mode/chrome-and-screens.md).

**The LAUNCH screen — the app's very first surface (0.48)**, `StartScreenRoot`
in `Views/PlayMode/Screens/LaunchScreenView.axaml`, from `Launch Screen.dc.html`.
`CurrentScreen` starts at `PlayModeScreen.Start` and `AppConfig.StartupScreen`
defaults to Play Mode, so this is literally the first thing the product shows;
its button leads on to Home. It
replaced a 420 px `VerticalAlignment=Center` StackPanel with a **full-height
frame**: a scrolling body holding the lockup column over a footer pinned to the
floor. Five things it settled:

1. **The frame is the load-bearing part, not the column.** The surface it
   replaced measured perfectly well; what it lacked was a floor, a footer and
   any status at all. `StartScreenRoot` is a `*,Auto` Grid — body row, footer
   row — and `PlayModeScreensUiPerfSmokeTests
   .LaunchScreen_isAFullHeightFrame_WithEveryChipInAKnownState` asserts the
   footer really ends at the root's own bottom edge.
2. **Every chip can say "I do not know", and that is the whole design.**
   `ViewModels/PlayMode/LaunchFacts` is the one immutable snapshot, and its two
   pure mappings carry the rule: `StateFor` (library — NotScanned / Scanning /
   Empty / Ready, where **a running scan beats a populated count**) and
   `InputStateFor` (NoDevice / NoInput / Connected, where "no engine" is NOT the
   same claim as "engine up, nothing capturing"). The count only renders in
   `Ready` and the latency only when there is a measured one (the streak chip
   went with the calendar on 2026-09-19). A screen that cannot express
   ignorance lies on a fresh install.
3. **It is the BOOT surface, so its work is Background-deferred and latched.**
   `PlayModeViewModel.EnterLaunchScreen` publishes nothing inline on first
   entry — it posts `WarmLaunchScreenAsync` at `DispatcherPriority.Background`,
   exactly like the Menu's plan card, because `EnsureLibrary()` is a
   seconds-long scan that marshals a progress callback per batch onto the UI
   thread. **It is reached from the VIEW'S ATTACH**, not from
   `OnCurrentScreenChanged`: `CurrentScreen` STARTS at `Start`, so no screen
   change ever fires for it (`PhraseCatalogView.EnsureLoaded`'s shape).
4. **The three printed key hints are all real.** `ENTER START` and `S SETTINGS`
   are window-tunnel branches behind `GlobalChordGate.AllowsLaunchStart` /
   `AllowsLaunchShortcut` (both stand down for a focused `TextBox` — the
   screen's own name fields; **Enter additionally stands down for a focused
   `Button`**, which is what keeps the screen arrow-key navigable). `ESC QUIT`
   stays on the app's ONE Esc route: `TryCloseOverlay` dismisses an open launch
   panel FIRST, and only then does `HandleEscapeKey` invoke
   `MainWindowViewModel.RequestApplicationExit` (assigned by the window to
   `Close`, so the unsaved-changes prompt still runs). That ordering is what
   makes "Esc quits" safe while the player is typing a profile name.
5. **The profiles panel is the SAME profile model as the shared switcher
   flyout** — `StatusBarViewModel`, with its real list, `SwitchProfile`,
   `BeginRenameProfile`, `ChooseProfilePicture`, `ExportProfile`,
   `ImportProfile`, `CreateProfile` and the two-way `IsActiveLefty`. A second
   profile model would drift the moment either side changed.

Both inline panels are appended to `PlayModeView.HiddenSurfaces()` via
`PlayModeView.LaunchPanelNames` — the sibling sweep cannot reach them for the
OPPOSITE reason to the settings modal's panes: the screen root is VISIBLE at
boot, so `HiddenChildrenOf` never sees it, while its gated children have still
never been measured.

**Readability pass (2026-09-08, owner: "this text is really hard to read", "the
start button text not properly centered"):** every status chip is dot + a
`FireRowTitle` line + an optional `FireMono` caption; a chip with no figure (no
songs yet, no streak yet) shows its state text AS the title — as the only line
in the `Faint` ink it could not be read. The three chips stretch to one height.
▶ START is one centred group on a `Panel` with the ENTER badge at the right
edge; the label used to carry `FireGapRowLg` (a BOTTOM gap) beside the icon at
the left. `Perf/LaunchScreenCaptureUiPerfSmokeTests` is the layout harness
(writes `launch.png` with the capture env vars) and asserts both: no visible
chip line in the faint ink, and the START group centred on its face.

Guarded by the default-gate `UI/LaunchScreenTests` (the tri-states, the chord
gate, the profile detail line, the full-height frame, the shipped logo, the
no-ground rule, every bound figure resolving on `ILaunchFacts`, every profile
action resolving on `StatusBarViewModel`, the Esc ordering, the scan deferral
and the prewarm exposure) plus `Perf/PlayModeScreensUiPerfSmokeTests
.{LaunchScreen_isAFullHeightFrame_WithEveryChipInAKnownState,
LaunchScreen_profilesPanelExpandsAndMeasures}`.

**Song Library** (`SongSelectScreenRoot` in
`Views/PlayMode/Screens/SongSelectView.axaml`, its detail pane `SongSelectDetailRailView`). Built from `Song Library.dc.html` (Session
History, Practice Trends and the Practice Calendar, each built from its own
mockup, were removed on 2026-09-19; restore from tag `pre-cut-2026-09`). The
screen root carries a comment listing every mockup panel that was CUT and the
missing data that cut it — read that before adding one back. Three things it
established for every screen after it:

1. **They paint no ground.** `MenuBackdropControl` (animated glows, string
   lines, audio-reactive EQ bars) renders behind every Play Mode screen; the
   mockups' flat `#07060c` exists only because a web page has nothing behind
   it, and design 6 copying it was rejected on sight. A translucent
   `Border.FireWash UpperLeft` is the only layer under the panels.
2. **A row that represents an action is a `Button` with a real `Command`.**
   A queue row is a `Button.FireListRow` on its start command; design 6
   shipped a queue as a bare `Border` that looked exactly right and did
   nothing.
3. **Selection-driven lists use `ListBox.FireList`, not a command per row.**
   Song Library models "pick a row" as `ListBox.SelectedItem`
   (it fills a detail rail), so the 3 px accent bar is reached through
   `ListBox.FireList > ListBoxItem:selected Border.FireListRow` rather than a
   `Classes.selected` binding on the row's view model.

Song Library additionally moves the per-row Play / LAN-board / download cluster
into the right-hand `SongSelectDetailRail`, which is the mockup's arrangement:
the row is information, the rail is what you DO, and all four commands simply
take `SelectedSong` instead of the row item.

**Song Library, rebuilt to the mockup (0.47).** Header + filter bar over a
flexible list and a fixed 420 px detail pane, FULL BLEED — the 1360 px centred
column it carried since 0.32 is gone, because the design's columns meet the
window's edges ("no centred islands"). What is load-bearing and what changed:

- **ONE Play button, in the detail pane.** Pinned twice: a source lint
  (`UI/FrontDoorAndLibraryTests.TheSongLibrary_HasOnePlayButton_AndItIsInTheDetailPane`,
  which also refuses ANY button in the row template) and a runtime one that
  walks the laid-out tree for controls bound to `PlaySongCommand`.
- **The tuning column is FIXED at 150 px and holds the tuning WITH its capo**
  ("E STANDARD · CAPO 2"), amber when either means you have to do something to
  the guitar before you can play. That alignment is §6.2's stated acceptance
  criterion, and an `Auto` column fails it while every per-element bounds check
  still passes — so it is asserted on the column DEFINITION (in
  `SongTableLayout.PlayModeSongList`) and on the cells' measured left edges.
- **The caption row and the rows are ONE table (#231).** Both lay out through
  `Controls/SongTablePanel` over `SongTableLayout.PlayModeSongList` at the same
  inset (`FirePadListRow`), so the song column keeps its 260 px floor (the
  length/difficulty cell gives way first: at 1280 it goes and the song column
  is ~300 px), the title trims with an ellipsis in its cell, and every caption
  starts where its values start. Details and guards:
  [../song-browser.md](../song-browser.md) § Columns at narrow widths.
- **The filters are the caption row's facet popovers + removable chips** (the
  unified browser, 2026-08-15 — the 0.47 FILTERS toggle and its combo row are
  DELETED). The chips are `BrowserFilterChip`s rebuilt by every filter pass
  (`RefreshSongListChrome`), so a chip can never claim a filter the list is
  not applying; the facet detail is in
  [../play-mode/song-select.md](../play-mode/song-select.md).
- **`SongSelectLayoutStructureTests` kept its point and lost its cap.** The
  1360 px `MaxWidth` assertion is gone; `Stretch` (never `Center`) was always the
  half that kept the surface stable while the library scanned.

Guarded by `UI/FireClassReachabilityTests` (default gate — the element-TYPE-aware
class check, the no-ground rule and "every button is wired") plus `Perf/PlayModeScreensUiPerfSmokeTests` (construction + attach +
layout, including the rail's two states).

### Persisted section bar ranges

The per-section BAR ranges are captured and printed — the results screen's
section breakdown reads them.

**Contract for the persisted bar range**: `StartBar` /
`EndBar` are `int?` and ABSENT from older stores and from any
chart with no measure markers. `null` means **not recorded** and must never be
read as bar 0 — consumers fall back to the section's real time range. The
technique members are APPENDED to the enum and persist by NAME
(`TechniquePlayRecord.Technique` is a string), so an old store simply has no
rows for them. Nothing in the store is rewritten, and the best-score MAX
invariant is untouched — none of this touches `PracticeProfileService.Record`'s
best-score path.

