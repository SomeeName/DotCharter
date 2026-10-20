# Play Mode — screen chrome, the front door and the gameplay-settings overlay

Part of the Play Mode reference. Router: [../play-mode.md](../play-mode.md).

- **Play Mode chrome (0.16 visual pass).** Screens and overlays share one
  "game UI" treatment over the whole-surface backdrop: translucent glass
  surfaces (`PanelGlass`/`CardGlass`/`SurfaceGlass` in `Styles/Resources.axaml`
  — brush-level alpha over the EXISTING colour tokens, so no new Color tokens
  and no theme-completeness-gate impact), plus a `HeaderRule` accent underline
  (the shared `HeaderAccentUnderline` gradient) under every screen/overlay
  title. Prefer these shared styles/tokens for new Play Mode surfaces.
- **Content-screen chrome (0.35 menu redesign).** Every non-gameplay screen
  (Menu, Song Select, Results) now shares one
  vocabulary, declared in `Views/PlayMode/PlayModeStyles.axaml` (included by
  `PlayModeView` alone; it reaches the screen views through the style cascade).
  Use these instead of hand-rolling a header or a card.
  **Every Border and TextBlock family is a `ControlTheme` (#428), in
  `Views/PlayMode/PlayModeThemes.axaml`**, a resource dictionary each screen view
  that wears one merges itself (#294 — a lifted view's StaticResources resolve
  while ITS XAML loads, outside `PlayModeView`'s scope): a
  control wears the theme AND the class, `Theme="{StaticResource EyebrowText}"
  Classes="Eyebrow"`, `Theme="{StaticResource PlayStatTileBorder}"
  Classes="PlayStatTile Hero"`. A view-scoped `Border.X`/`TextBlock.X` rule was
  attached to all 1,311 Borders and 1,024 TextBlocks the view hosts (~22,000
  value frames, prewarmed); `UI/StyleIncludeScopeTests` fails one put back. The
  figure themes (`StatFigureText`, `StatValueText`, `StatUnitText`) are BASED ON
  `MonoText`, so a `Mono` figure keeps both rules with the family's last:
  - **Header block**: a back rail + `TextBlock.Eyebrow` kicker
    (`PlayMode_Nav_Practice` / `PlayMode_Nav_Network`) + `.ScreenTitle` +
    `HeaderRule` + `.ScreenSubtitle`, LEFT-aligned at the top of a
    centered `MaxWidth` column that **stretches** (the Song Select rule — a
    `Center`-aligned grid sizes to its content, so the column would move as data
    loads).
  - **Surfaces**: `GlassPanel` (a screen's content slab),
    `PlayStatTile` (one measured figure), `.PlayStatTile.Hero` (the ONE
    accent-tinted figure a screen is about — R4, exactly one per screen),
    `PlayTopLightEdge` (the 1 px lit top edge; it is drawn with a negative
    margin equal to the panel's padding, so it only lines up on the
    `PaddingCardLg` panels/tiles).
  - **Type**: `.Eyebrow` / `.SectionTitle` / `.ScreenSubtitle` / `.StatFigure` /
    `.StatUnit`. **A big figure paired with `Classes="… Mono"` MUST carry an
    explicit `LineHeight`** — `^.Mono` (`MonoText`, `Styles/Typography.axaml`) pins
    `LineHeight` to 16 for 12 px value text, and a 28 px glyph in a 16 px line
    box is clipped to a middle band. Every figure on the (since removed) Stats
    and Calendar screens shipped as a sliver until `StatFigure` set
    `LineHeight="34"`.
  - **Menu entries**: `Button.MenuTile`, `MinHeight` 78, filling its grid
    cell. `Button.PlayMenu` stays the primary CTA.
  - **A card whose data is empty keeps its slot and says why.** Hiding an
    empty card leaves a dead column and a lopsided screen; render it with an
    empty state instead (the Menu's plan card does).
  - **Chart tracks are sized around the VM's bar constants** — a bar that can
    never reach the top of its track reads as a permanently bad value. (The
    Trends screen this rule was written for was removed on 2026-09-19.)
  - The light lives in four brushes in `Styles/Resources.axaml`
    (`PlayPanelFill` / `PlayTileFill` / `PlayHeroFill` / `PlayTopLight`), built
    from gradient stops over the EXISTING colour tokens exactly like the
    `*Glass` brushes — no new `Color` token, so the theme-switch completeness
    gate is untouched. This is the pedalboard's R1/R3 (one key light from above,
    depth as edges) applied to menu chrome. Its rulebook,
    `docs/agents/ui/pedalboard-visual-language.md`, was retired with the rack in
    v0.43 — see the note at the top of [pedalboard.md](../pedalboard.md).
- **The Play Mode menu (the old front door, `MenuScreenRoot` / `MenuDesign6Root`) is
  GONE (2026-09-22, owner).** Start's button and its Enter key hand the player to
  the shell's HOME (`PlayModeViewModel.EnterHome` → `HomeRequested`), and so do Esc
  on Song Select and the pause menu's Home (`AbortToHome`); Esc on Home goes back
  to Start, and Home's Play opens Song Select. Random went with the menu; the
  menu's "show at startup" toggle went too (Settings ▸ General ▸ Open at startup
  remains). `IFrontDoorFacts` stays: Song Select reads the verified tuning from it,
  refreshed on every Song Select entry. Restore the screen from git (the commit
  that removed it names it) — `UI/FrontDoorAndLibraryTests` fails while the name
  is back.

- **The 0.37.1 pass extended that SAME vocabulary to the rest of Play Mode** —
  Song Select, Results, Start, Tuning check, Countdown, Playing, the pause and
  fail overlays, and the overlay cards (today: gameplay settings). **There is no second vocabulary**: a new Play Mode surface uses
  the header block, `GlassPanel` + `PlayTopLightEdge`, `PlayStatTile`
  (+ exactly ONE `.Hero`), `Button.MenuTile` and the `.Eyebrow` /
  `.SectionTitle` / `.ScreenSubtitle` / `.StatFigure` type classes, or it does
  not belong. What that pass changed structurally, and must not be undone:
  - **Song Select and Session History are on the "Fire"
    layer since 0.43** ([fire/play-mode-screens.md](../fire/play-mode-screens.md)) and no
    longer speak the vocabulary above — they are the owner's commissioned
    mockups. What that changed structurally, per screen, is recorded in the
    comment block at each screen root; the two rules that reach every future
    Fire surface are: **no screen may paint a ground** (it would hide
    `MenuBackdropControl` — a translucent `Border.FireWash` is the only layer
    under the panels) and **every row that represents an action is a `Button`
    with a real `Command`**. Both are pinned in the default gate by
    `UI/FireClassReachabilityTests`, which also fails on a `Fire*` class that
    matches no selector FOR ITS ELEMENT TYPE.
  - **Song Select owns a browse-bar panel** (`SongSelectBrowseBar`): search
    (in the header, in a `Border.FireField`, with an in-field clear), sort +
    direction, the library entries and the chips row in ONE seamed strip —
    the filters themselves live in the caption row's facet popovers since the
    unified browser (2026-08-15; the `SongSelectFilterRow` WrapPanel is
    DELETED — see [song-select.md](song-select.md)).
    Since the Fire conversion the **row is information and the right-hand
    `SongSelectDetailRail` owns the ACTIONS** — Play moved there and takes `SelectedSong`
    instead of the row item.
  - **Two empty states, not one.** `ShowSongSelectFirstRun` (library empty ⇒ the
    "choose your song folder" invitation) and `ShowSongSelectNoMatch` (filters
    hid everything ⇒ "clear filters"), both derived in `ApplyFilter` alongside
    the umbrella `ShowSongSelectEmptyState`, and all three **default TRUE/TRUE/
    false** because a freshly-constructed VM genuinely IS the first-run state —
    nothing recomputes them before the first filter pass or scan.
    `OnIsLibraryLoadingChanged` must keep all three in step, and
    `SongSelectLayoutStructureTests` pins that both panels live inside the one
    centered column.
  - **`LibraryFilter.IsNarrowing`** is the pure "is a filter doing
    anything" answer (`HasActiveSongFilters` on the VM). Never re-derive it from
    a pile of index comparisons in XAML. `ClearSongFilters` resets all nine
    controls behind the `_suspendFilter` guard, then runs ONE `ApplyFilter`.
  - **The song row is three lines** — title, artist,
    then the chip row — and the best cluster leads with the grade medal captioned
    by the score.
  - **Results keeps its reveal contract**: `ResultScoreText` stays UNBOUND and
    `GradeStamp` / `FcStamp` / `NewRecordFlash` keep their names and
    `RenderTransformOrigin` — `Screens/ResultsView.axaml.cs` animates all four,
    and `PlayModeView` decides when (screen on/off, any key or click skips).
  - **The pause menu is one primary + a 2x3 `Button.MenuTile` tool grid
    (`PauseToolGrid`) + a quiet leave rail**, not nine equal word buttons. The
    fail overlay stays deliberately short (three choices, no grid).
- **Gameplay settings overlay (0.15.1; comprehensive redesign 0.16)**
  (`Settings.IsGameplaySettingsOpen` — the modal is its own view model since
  #658, `PlayModeSettingsViewModel`, exposed as `PlayModeViewModel.Settings`; its
  views bind `{Binding Settings.…}`): a game-styled options screen opened from BOTH the
  main menu and the pause menu so ALL gameplay-relevant settings adjust mid-song
  WITHOUT leaving the paused run or dropping into the editor's tabbed Settings tab
  (Esc closes it back to wherever it opened). A **left section rail**
  (`ListBox.SettingsRail`, `GameplaySettingsSection` 0-3 → `IsGameSettings{Audio,
  Gameplay,Graphics,Appearance}`) switches four scrollable panels; rows are a
  `Grid.SettingRow` (label left / control right) scoped-styled on the overlay Grid;
  spacing/type via `Space*`/`Gap*`/`FontSize*` tokens only. It's a front-end over
  EXISTING state — **Audio**: master/backing/input/monitor volumes bind the shared
  `Transport`, metronome + count-in on `Settings`; **Gameplay**: hit-window/chord
  strictness/CQT verifier/detection monitor bind the shared `LivePlay`, speed
  `PracticeSpeedPercent`, tones `UseInGameTones` (both stay on Play Mode); **Graphics**: fps cap
  (`Preview3DPacing.MaxFps` via the pure `GameplayFpsIndexToValue`/`ValueToIndex`
  maps) + render scale (`IVulkanRenderService.SetRenderScalePercent`, injected) +
  camera distance (`ProjectState.Preview3DCameraFill`, inverted to a
  distance slider — see `DotCharter.Rendering/AGENTS.md`) + all highway effect
  toggles on the shared `ProjectState`; **Appearance**:
  string-colour preset (`IThemeService`, injected — applied + persisted
  immediately, preserving custom theme colours) + UI scale (`UiScaleController`).
  Everything live-applies through the SAME statics/services Settings uses.
  Overlay-owned values (metronome, count-in, rock meter, fps, render scale, UI scale, the
  `ProjectState` toggles) are loaded from `AppConfig` on open (behind a
  `_loadingGameplaySettings` guard so loads don't re-fire live-apply) and mirrored
  to the SAME `AppConfig` fields Settings → General/Graphics/Appearance persist on
  close. "Full settings…" still reaches the full editor tab: the modal raises
  `OpenFullSettingsRequested` and Play Mode forwards it as `OpenSettingsRequested`. Disjoint from Amp/FX + Pedalboard.
  **Persisting is not the same as APPLYING, and two of these values are armed
  per RUN.** The gameplay metronome and the note claps are read from `AppConfig`
  once, in `BeginGameplay`, onto `TransportViewModel.GameplayMetronomeActive` /
  `GameplayClapsActive` — so while this overlay opens from the PAUSE menu
  precisely so settings can be changed mid-song, a flip of either one used to do
  nothing at all until the NEXT run. `ApplyGameplaySettingsToLiveRun` re-arms
  them on close, gated on Play Mode's `_runSideEffectsApplied` (a run's side
  effects are live), which the modal reads through `IPlayModeSettingsHost`
  rather than keeping a copy — never outside a run, because `AudioPlaybackCoordinator` ORs these with
  the EDITOR's own metronome toggle. Pinned by
  `UI/ViewModels/PlayMode/PlayModeRunChainStateTests` in both directions.

## The START screen's hero FILLS its viewport

`StartScreenRoot`'s body — logo, wordmark, tagline, START, the profile panel,
the three status pills — is authored at a fixed natural size and centred, so it
was a small island in any window bigger than the mockup. **Measured headlessly
2026-09-12: the hero's natural size is 680 x 537 DIPs, i.e. 17.6 % of a
1920 x 1080 client area and 9.9 % of a 2560 x 1440 one.** The owner's report was
"roughly 350 x 300 px in the centre of a 2560 x 1440 window", then "it's even
worse on 1920x1080".

- `LaunchColumn` is wrapped in **`Controls/Fire/HeroScaleToFit`**
  (`x:Name="LaunchHeroScaler"`), whose policy is the pure static
  **`Controls/Fire/LaunchHeroScale`**: grow until the hero fills **58 % of the
  viewport height or 40 % of its width, whichever binds first**, floor **1.0**,
  cap **3.0**, quantised DOWN to 0.05. Measured result — 1080p **1.10x /
  54.7 %** of window height, 1440p **1.50x / 56.0 %**, 4K **2.25x / 56.0 %**.
  Width binds on all three 16:9 sizes; height binds on an ultrawide, which is
  why the rule is a `Min` of two and not a height rule with a guard.
- **This is NOT the global interface scale and must not be folded into it.**
  1080p keeps a global scale of exactly 1.0 by policy (`AutoUiScale`), so at
  the resolution the owner called worst, the hero's own fill is the only thing
  that can help.
- **`Viewport` is bound to `LaunchBodyScroll.Bounds` and has to be.** A
  `ScrollViewer` hands its content infinite height, so a fill derived from the
  measure's `availableSize` would never engage vertically — the same note
  `RigScaleToFit` carries. Binding the scroll viewer's bounds is also what makes
  this COMPOSE with the global scale instead of multiplying against it: that
  viewport is measured inside `RootScaler`, so at 150 % it reports 1/1.5 of the
  window height and the hero grows 1/1.5 less. The hero covers the same share of
  the SCREEN at every global scale.
- **A `LayoutTransformControl`, not a `Viewbox`.** A Viewbox is a RENDER
  transform: text would be laid out at one size and painted at another (soft at
  2x — the hero's own complaint restated) and the CTA's chamfer would be
  hit-tested where it was laid out, not where it is drawn.
- **`LaunchFooter` is OUTSIDE it** — it is `Grid.Row="1"`, outside the scroll
  viewer — so the version stamp and the key hints stay anchored in their
  corners and follow the GLOBAL scale only. Pinned by
  `Perf/InterfaceScaleUiPerfSmokeTests.TheFooter_IsOutsideTheHeroScaler`.
- The cap is 3.0 because `Assets/app-icon.png` is a **256 x 256 raster**;
  everything else in the hero is vector text and chrome.

**Other Play Mode screens were NOT changed, and one of them has the same
shape.** Measured at 1920 x 1080 on 2026-09-12: `SongSelectScreenRoot` and
`ResultsScreenRoot` both fill 1920 x 1080 at the root and are not islands by this
measure (the menu screen that WAS one is gone since 2026-09-22). That is a
root-level measurement, not a content-density one.

## The GAME MOMENTS fill their viewport too

The four screens between picking a song and reading your score were the
resolution audit's worst numbers, and they were worst in the same way: their
layout is FROZEN at one pixel size, so the share of the screen they use halves
with every step up in resolution. Measured (primary box ÷ client area, before →
after):

| Screen | box | @1080p | @4K | `ratio` 4K/1080p |
|---|---|---|---|---|
| Countdown (`CountdownScreenRoot`) | 114 x 282 | 1.6 % → **8.8 %** | 0.4 % → **8.8 %** | 0.25 → **1.00** |
| Failed (`FailedScreenRoot`) | 460 x 240 | 5.3 % → **14.8 %** | 1.3 % → **14.8 %** | 0.25 → **1.00** |
| Paused (`PauseScreenRoot`) | 620 x 565 | 16.9 % → **28.6 %** | 4.2 % → **28.6 %** | 0.25 → **1.00** |
| Results (`ResultsColumn` cap) | 1120 literal | — | 27.1 % → **36.5 %** of the width | — |
| Get Ready (`TuningCheckScreenRoot`) | full-bleed | **100 %** | **100 %** | 1.00 — *unchanged, see below* |

### The figure screens: a `Viewbox` with a FRACTIONAL ceiling

Countdown, Paused and Failed are *figures* — a numeral, a card of three choices,
a card of six tool tiles — centred in an otherwise empty window. Each one's root
now sits inside a `Viewbox Stretch="Uniform" StretchDirection="UpOnly"` named
`<Screen>Frame`, and **the ceiling is a SHARE of the surface, not a number**:

```xml
MaxWidth="{Binding $parent[Panel].Bounds,
    Converter={x:Static conv:ViewportFractionConverter.Instance},
    ConverterParameter=Width:0.42}"
```

`Converters/ViewportFractionConverter` is the whole mechanism (Width/Height ×
a fraction; `+inf` on a zero-sized container so a first measure cannot pin the
figure at 0). It is deliberately not `DialogCardClampConverter` — that one
answers "available minus a gutter", which is right for a card that should FILL
the window and wrong for a figure, which should not.

- **The fraction is what makes `ratio` 1.00.** A literal ceiling — even a
  generous one — restores the audit's 0.25 signature immediately: swapping
  Paused's fractional pair for the same numbers it resolves to at 1080p
  (`807`/`864`) measured 28.6 % @1080p and **7.2 %** @4K.
- `StretchDirection="UpOnly"` means the design size is a FLOOR: a small window
  never shrinks the card, it just stops growing it.
- **The card keeps its design width** (`PauseScreenRoot` is still `Width="620"`,
  `FailedScreenRoot` still `460`). Every column, wrap point and German string in
  them is tuned to that; the Viewbox scales the picture, it does not re-flow it.
- **Corner chrome stays OUTSIDE the frame.** The count-in's keys hint is now
  `CountdownKeysHint`, anchored bottom-centre on the surface — a keyboard hint
  scaled 4.8x is a billboard. The pause card's own footer hint stays inside,
  because it is part of the card rather than screen chrome.
- **`PauseScreenRoot` is a DEFERRED body** (`PlayModeView.DeferredScreenHostNames`),
  so the Viewbox goes INSIDE the `DataTemplate` and carries no `IsVisible` — the
  gate stays on `PauseScreenRootHost` or the prewarm realizes nothing. Tests
  reach it by walking visual descendants, never `FindControl`.

**What is and is not known about a `Viewbox` here.** The launch hero above
rejects `Viewbox` for itself on two grounds. One of them does not apply to these
three screens: it carries a 256 x 256 RASTER app icon and they carry no raster
asset at all, so there is nothing here to resample. The other — "hit-tested
where it was laid out, not where it is drawn" — **could not be measured
headlessly and is not settled either way**: `GetVisualsAt` returns only the root
`Panel` for every button in this view, inside a Viewbox and outside one alike.
What `Perf/UiResolutionAuditUiPerfSmokeTests
.AScaledPauseMenu_PlacesAndFocusesItsToolsAtTheDrawnPositions` does pin is that
at ~2.6x every tool tile is PLACED at the scaled position (`TranslatePoint`
composes the transform) and still takes keyboard focus. **If a real-window
report ever says a scaled pause tile clicks in the wrong place, that is the
claim to reopen, and `HeroScaleToFit` is the alternative already in the repo.**

### Results: the token cap, and two columns at `Window.Wide`

`ResultsColumn`'s cap is `FireColumnMaxWidthLg` (1040, and 1400 above the wide
breakpoint) instead of the literal 1120 it carried, and above the breakpoint the
body becomes two columns — `ResultsPrimaryColumn` (figures, the three verbs,
listen-back) beside `ResultsDetailColumn` (section breakdown, timing,
leaderboard, library action) instead of a screen-height scroll below it. Both
columns exist at every width; only `Grid.Row`/`Column`/`ColumnSpan` move, from a
selector, so the default shape is the old single column.

**Two traps, both of which this shipped broken once:**

- **A local value in markup outranks every style setter.** `Grid.ColumnSpan="2"`
  written on the `StackPanel` silently beat the `Wide` rule and the screen stayed
  one column — while a `Tag` setter in the *same style* applied, which is exactly
  how it reads as "the selector does not match". The default cell is therefore a
  STYLE too, declared before the `Wide` one (last matching style wins). Same
  fault, same fix for the grade stamp: its `FontSize` is now
  `TextBlock.GradeStampFigure`, not a local attribute.
- **`FontSize` and `LineHeight` move together or neither moves.**
  `TextBlock.StatFigure` pins a line box; raising the figure inside a frozen one
  renders a clipped middle band. The wide rungs are ordinary entries in
  `Styles/Typography.axaml` — `FontSizeStampLg` 66, `FontSizeHeroLg` 40,
  `LineHeightHeroLg` 48 — **never in `Ladders.Wide.axaml`**, which is layout
  constants only because a font token ramped there compounds with `RootScaler`.
  The new 48 px line box is a deliberate entry in `UI/LineHeightTokenTests`.

Selectors here say `:is(Window).Wide`. A bare `Window.Wide` was MEASURED to
reach a `Window` subclass in this Avalonia too
(`ABreakpointSelector_ReachesAWindowSubclass`), so the breakpoint guide's
example is correct as written; `:is()` is used because this rule's failure mode
is silent and ships, and that case names which form went dead if one ever does.

### Get Ready keeps NO Viewbox, and that is a measurement

It is grouped with the other three as a pre-run moment, but it is not the same
shape: since 0.48 it is a full-bleed four-region layout (header / 404 px rail /
`*` centre / footer) because it is the densest screen in the app. Measured, it
covers **100 %** of the window at 1080p and at 4K, with the flex centre taking
75.1 % / 87.6 % of the width — scaling it would be a zoom, not a fix.
`GetReady_AlreadyFillsTheWindow_AndNeedsNoViewbox` pins that rather than leaving
it as a claim: if the screen ever regresses to a centred capped column (what it
was before 0.48) it fails and the question reopens with a number behind it.

### Get Ready's tone chips EDIT the song's tones

Each chip in `GetReadyToneChips` is a button (`SongToneChip`,
`PlayModeViewModel.EditSongToneCommand`): it puts that tone on the rig through
`RsTonePlaybackCoordinator.BeginToneEdit` and opens the rig with
`Views/PlayMode/SongToneEditBar` above it (row 0 of `RigScreenRoot`, over
`SongToneEditViewModel`). The bar saves what the rig holds for that song
(`SongToneEditStore` under `<ConfigRoot>/song-tones/`, off the UI thread — the
song's package is never written), resets a tone to its conversion, or
discards. **Closing the rig by any route ends the edit**
(`OnIsPedalboardOpenChanged` → `SongToneEdit.End()`), which hands the player's
board back. The chips are disabled without a native rig or a library song (an
own chart edits its tones in the Tone Designer); `SongToneEditNote` prints the
how-to line only when they are live. The bar's
`IsVisible` carries `FallbackValue=False`: without it a null view model made the
bar take 47 px over every rig. **`RigScreenRoot` carries its own opaque
`FireOverlayFill`**: the rig view paints only its own row, so without it the Get
Ready header showed through beside the bar. The bar is a `FireCard Accent` with
the rig card's 18 px inset, so the two line up edge to edge. All three are pinned
by `Perf/SongToneEditBarUiPerfSmokeTests`, which also writes
`song-tone-edit.png` under the capture env vars.

### The evidence

All of it lives in `Perf/UiResolutionAuditUiPerfSmokeTests`, which grew from an
opt-in measurement harness into one that also GATES the fixes it produced (the
new cases take no `DOTCHARTER_UI_AUDIT` gate, for the reason the settings case
already gives). Its `MeasureSurface` now composes bounds THROUGH render
transforms and counts `Viewbox` as a primary-panel candidate — without that it
would report every scaled figure at its design size and this whole change as a
no-op.

### The LAN board screen and the two HUD strips

Removed on 2026-09-19 with the LAN features; restore from tag `pre-cut-2026-09`.

## Redesign (the gallery wave) — eight flow screens on contracts and fakes

**These do not replace the shipped screens yet.** Each is a self-contained view
bound to a small CONTRACT interface with a services-free FAKE behind it, so it
is designed, captured and tested without `PlayModeViewModel`, audio or DI — the
`SongSelectScreenView` shape, one level wider. The swap into the app is a
separate step that adapts the real view model onto the contract rather than
moving markup.

- **Contracts** — `DotCharter.Presentation/Contracts/PlayMode/` (namespace
  `DotCharter.Presentation.Contracts.PlayMode`):
  `IStartScreen`, `ITuningCheckScreen` (the redesigned menu, `IMenuScreen`, went
  with the menu screen on 2026-09-22),
  `ICountdownScreen`, `IPlayingScreen`, `IPausedScreen`, `IFailedScreen`,
  `IResultsScreen`, plus the shared `IPlayModeFlowParts.cs` (`IChoiceOption`,
  `IFactTile`) and the setlist parts in `ISetlistParts.cs` (#646): Song Select's
  `Setlists` (`ISetlistPanel`) and the `Setlist` of Get Ready, Pause and Results
  (`IGetReadySetlist`, `IPausedSetlist`, `IResultsSetlist`). The app's
  `PlaySetlistViewModel` implements all four; the fakes are `FakeSetlistPanel` /
  `FakeSetlistRun` in `FakeSetlistParts.cs`, behind the `Setlist*` variants
  (`InSetlist`, `SetlistSkipped`, `SetlistNext`, `SetlistDone`, `Setlist`,
  `SetlistEmpty`, `SetlistEnded`; `LongText` and Song Select's `Full` carry a long
  setlist). In the redesigns, Results puts "Next: <song>" on a row of its own with
  Retry and End setlist under it, Pause swaps Song select for Skip song / End
  setlist in its leave rail, and Song Select's rail scrolls the setlist panel UNDER
  the song's details (pinned, it took the room the record must keep at rest).
- **Fakes** — `DotCharter.Presentation.Fakes/PlayMode/Fake<Screen>Screen.cs` and the
  shared `FakePlayModeFlowParts.cs`, each with a static `Variants` list of
  `FakeVariant<TContract>` (see `Presentation/FakeVariantsTests`). Every one carries a populated variant, an
  `Empty` one, a `Full` one at the list's realistic maximum where the contract has a list
  (Song Select: 2,160 songs) and a stress one with German and Japanese strings; the long
  strings are constants on `FakeFlowText`. Commands are plain `RelayCommand`
  FIELDS, never `[RelayCommand]` methods — a generated `…Command` reached only
  through an interface `x:DataType` reads as ambiguous to the reachability lint.
- **Views** — `DotCharter.UI/Views/PlayMode/Redesign/<Screen>ScreenView.axaml`
  (+ ctor-only code-behind). Structural parts are named
  `<Screen>Redesign<Part>`.
- **Tests** — `Perf/PlayModeFlowRedesignUiPerfSmokeTests` (headless: every
  screen × variant constructs, attaches, lays out and captures
  `playmode-redesign-<screen>-<variant>.png`, plus a per-screen fact and a
  no-text-outside-its-container check on each stress variant) and
  `UI/PlayModeFlowFakesTests` in the DEFAULT gate (the fixtures cannot decay
  into empty ones).
- **Gallery and goldens** — DERIVED from `DotCharter.UI/Views/Shell/ScreenCatalog.cs`
  (one line per screen, in FLOW order) × each fake's `Variants`
  (`DotCharter.UI.Gallery/Pages/Screens/ScreenPages.cs`), so
  `Perf/GalleryPagesUiPerfSmokeTests` and `Perf/ScreenGoldenUiPerfSmokeTests`
  cover each screen with no second registration.

Three decisions worth not undoing:

- **The HUD's highway is a placeholder `Border`, and that is permanent for this
  view.** The real one is a native Vulkan surface, so `PlayingScreenView`
  designs the chrome AROUND a token-coloured rectangle.
- **Countdown and Failed take a `Viewbox` with a FRACTIONAL ceiling**
  (`Converters/ViewportFractionConverter`, `StretchDirection="UpOnly"`), for the
  reason the resolution audit above gives: a literal ceiling restores the 0.25
  signature immediately. Get Ready deliberately keeps its full-bleed four-region
  layout and takes none.
- **No key chip inside a `Button.FireCta`.** The CTA clips to a chamfer, so a
  badge at its right edge is sliced — the key hints live in each screen's
  footer. Seen in the first capture round on both Start and Get Ready.
