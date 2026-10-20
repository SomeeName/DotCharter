# Settings (`Views/Settings/`) — UI deep reference

Extracted verbatim from `DotCharter.UI/AGENTS.md`. **Read this before adding a
settings row, a group, a tab, or anything the cross-tab search has to find.**
The dialog grammar one level down is [dialogs.md](dialogs.md); the two share the
no-`Cursor` / no-`Transitions` ban and the Fire-ladders-only lint.

## The Graphite grammar (branch `design/graphite-settings`)

**Where this section and the long bullets below disagree, this section wins** —
the bullets describe the 0.49 card grammar, which Graphite replaced.

- **Page:** a left RAIL (the title "Settings", search, grouped tabs, and a save
  block of Cancel · Save plus a quiet reset link) and one reading column.
  `Controls/LeftColumnPanel` pins that column to the LEFT, capped at
  `FireColumnMaxWidthLg`. Neither alignment can do it alone: a Stretch child
  that MaxWidth clamps is centred, and a Left child sizes to its content.
- **Sections, not cards:** `Border.SettingsGroup` is transparent with a
  hairline TOP rule (`FireBorder`), no radius, `BoxShadow none` (the app-level
  `ShadowGroupCard` drew a line otherwise). `Border.SettingsGroup.First` drops
  the rule. The title is 18 px (`FireSizeCardTitle`); the kicker and the icon
  badge are hidden by style; the tabs split out of General and Appearance declare neither.
- **Rows:** `*,300`, top-aligned, with the label dropped 8 px to meet a
  control's text baseline. A disabled row dims its label. **The control column
  reads from its LEFT edge.** Status values are `ValueText`: body colour,
  regular weight, left-aligned, and mono only for identifiers such as versions,
  model IDs and URIs.
- **Actions:** `Button.RowAction` is the quiet outline button that sits IN the
  control column (Refresh, Open folder, Download, Sign out). The accent belongs
  to the save block and to at most ONE primary action per screen: a user's
  next step (TONE3000's Sign in, a downloaded update's Show). A `GroupActions`
  strip under a group is legacy; do not add one.
- **Flow:** `MasonryPanel.SettingsFlow` is `VerticalAlignment=Top`. The masonry
  returns less height than it is given, and a Stretch child that does so is
  CENTRED, so a short tab floated mid-page until this was set.
- **Save block:** Cancel reverts and leaves, so with nothing to revert its
  caption is Close (`SettingsShellUiPerfSmokeTests.ARebind_MovesTheFooterCounter_AndResetSectionClearsIt`).
- **Appearance's colour list** is grouped by `ThemeTokenInfo.Category` under
  headings (`IAppearanceSettingsScreen.ColorGroups`), and each row shows a mono
  hex readout beside its swatch. Token names and group headings are
  localization keys (`ThemeToken_*`, `ThemeTokenGroup_*`) in
  `ThemePresets.CustomizableTokens`, reaching Presentation through
  `IAppearanceTheme.Tokens`. The picker binds the row's `Hex` string through
  `Converters/HexColorConverter`, two-way.
- **Readouts:** `TextBlock.ValueReadout` beside a slider or combo (Audio,
  Appearance's UI scale). Avalonia has no `+` sibling selector, so a disabled
  readout is styled as `TextBlock.ValueReadout:disabled`.
- **Review captures:** `SettingsShellUiPerfSmokeTests.EveryTab_OnThePage_CapturedForReview`
  writes the top, middle and bottom of every tab to `artifacts/ui-review/`,
  with `Session.IsOpen = true` so the combos are filled.
- **Opening loads every tab; leaving reverts every tab.** Both are posted at
  `Background` from `SettingsShellViewModel.OnSessionChanged`, which still lands
  them BEFORE the next frame (the queue is idle until the render tick): measured
  on the Release R2R build at ~11-13 ms a visit (30-40 ms the first time), inside
  the switch budget, so they stay — a tab's `Revert` has side effects beyond its
  tracked values (Appearance restores the persisted theme, Updates cancels a
  device flow, Keybinds rebuilds the dispatch). **The shell builds the settings
  surface on the first SHOW, after it has set `IsOpen`**, so a shell view model
  constructed into an open session loads on its own
  (`SettingsShellViewModelTests.APageBuiltWhileTheSessionIsOpen_LoadsOnTheNextFrame_Once`).

- **Settings (`Views/Settings/`) — one row grammar, defined once in
  `Views/Settings/SettingsStyles.axaml`, included by every tab and NOT by the
  shell** (#412: the shell hosts the tabs, so its copy attached every rule twice
  to every control in every tab — 33,543 duplicate value frames, ~20 MB. The
  shell wears none of the grammar's classes. `UI/StyleIncludeScopeTests`).
  **Every Border / TextBlock / Grid / StackPanel family of the grammar is a
  `ControlTheme` in its `Styles.Resources` (#428)**, worn with its class:
  `<Grid Theme="{StaticResource FormRowGrid}" Classes="FormRow" …>`,
  `<TextBlock Theme="{StaticResource SettingTitleText}" Classes="SettingTitle" …>`,
  `SettingsGroupBorder`, `GroupHeadGrid`, `GroupTitleText`, `GroupSubtitleText`,
  `GroupKickerText`, `SettingDescText` (based on `MonoText`), `ValueReadoutText`,
  `SettingsValueText`, `NoticeStripBorder` + `NoticeStripText` (`Classes="NoticeText"`
  on a strip's TextBlocks), the readout strip's themes. As class RULES they were
  attached to every control of their type on every tab (~30,000 frames). A
  `FormRow`'s children carry their alignment LOCALLY: `VerticalAlignment="Top"`
  on each child (not on a `ValueReadout` or a keybind `Label`, whose own rule
  sets another) and the label column's `Margin="0,8,0,0"` on a first-child
  StackPanel or a `SettingTitle` — a child rule would be attached to every child
  of every Grid. The shell's own rules live on its RAIL (`Border.Styles`), where
  every control that wears them is. `UI/StyleIncludeScopeTests
  .TheConvertedGrammars_DeclareTheirFamiliesAsControlThemes` /
  `TheSettingsShell_KeepsItsRailRulesOnTheRail` and the headless
  `TheConvertedGrammars_StayWithinTheirRuleFrameCeilings` are the guards.
  **Since wave 1 step A the page is a screen on the target architecture**
  ([target-architecture.md](target-architecture.md) § 3.5): the shell is
  `Views/Settings/SettingsShellView.axaml` over
  `Presentation/Screens/Settings/SettingsShellViewModel`, hosted by a
  `ScreenHost` in `MainWindow`'s `SettingsHost` — `Views/Settings/SettingsSurface.cs`
  is the seam that builds it. **All twelve tabs are screens of their own** —
  `Views/Settings/GeneralSettingsView.axaml`,
  `Views/Settings/EditorSettingsView.axaml`,
  `Views/Settings/PlaySettingsView.axaml`,
  `Views/Settings/AppearanceSettingsView.axaml`,
  `Views/Settings/GraphicsSettingsView.axaml`,
  `Views/Settings/AudioSettingsView.axaml`,
  `Views/Settings/FilesSettingsView.axaml`,
  `Views/Settings/KeybindsSettingsView.axaml`,
  `Views/Settings/StemModelsSettingsView.axaml`,
  `Views/Settings/Tone3000SettingsView.axaml`,
  `Views/Settings/AiSettingsView.axaml` and
  `Views/Settings/UpdatesSettingsView.axaml` over view models in
  `Presentation/Screens/Settings/`, shown by the shell's `Views/Shell/ScreenTabHost.cs`.
  **Appearance was the last** (2026-09-22): the old page (`SettingsViewModel`),
  its `Views/Settings/Tabs/` and `Views/Settings/Legacy/` folders, the reflection
  change tracker and the markup search walker are all deleted, and the shell has
  no legacy slot. A new tab follows the checklist at the end of this file. A row is
  `<Grid Classes="FormRow" ColumnDefinitions="*,300">` with
  `TextBlock.SettingTitle` (+ `TextBlock.SettingDesc`) on the left and exactly
  ONE control in the fixed right column — that fixed column is what gives a tab
  a single right edge. **300 px is the figure for six of the seven tab files
  (122 rows, re-measured 2026-09-04 — 2 Appearance · 21 Audio · 53 General ·
  32 Graphics · 5 StemModels · 7 Updates, plus 2 on the TONE3000 panel; Graphics
  moved to `GraphicsSettingsView.axaml` 2026-09-21 with its rows unchanged, and
  on 2026-09-22 General SPLIT into General 17 · Editor 24 · Play 4 · Files &
  backups 11, see below);
  Keybinds uses `*,184` for all 140 of its rows** and has no `*,300`
  at all, because its right-hand control is a chord pill rather than a combo.
  Consistency is per TAB, not global. Also:
  `SettingsConstructionUiPerfSmokeTests` measures it. A group is
  `Border.SettingsGroup` opening with `<Grid Classes="GroupHead">` =
  `Border.GroupIconBox` + `PathIcon.GroupIcon` + title/subtitle. Vertical rhythm
  lives in the themes (`FormRowGrid` margin + `SettingsGroupBorder` padding),
  never inline per row (the children's alignment above is the exception); `Grid.FormRow.Sub` indents a dependent row. Booleans are
  `ToggleSwitch` (the style blanks Fluent's untranslated On/Off captions and its
  154 px MinWidth so every switch lands on the same x); `CheckBox` is only for
  multi-select clusters. **Icon geometry must come through
  `{DynamicResource IconX}`, never `{StaticResource}`** — see the thread/platform
  rule below. **Every `ComboBox`/`Slider`/`ToggleSwitch` in a row also carries
  `AutomationProperties.Name` off the SAME Loc key as the row's `SettingTitle`**
  (2026-08-11) — without it the control reaches assistive tech unnamed; linted
  in the default gate by
  `AccessibleNameTests.EveryRowInputControl_CarriesAnAccessibleName`, which
  since 2026-08-15 covers **all of `Views/`** rather than just Settings
  (exemption list empty, and shrink-only).
  - **Since 0.49 every group also opened with a MONO KICKER
    (`TextBlock.GroupKicker`) above its title; Graphite hides it, and the four
    tabs split out of General (2026-09-22) declare none, which retired six of the
    `Settings_Kicker_*` values, and Appearance declares none either, which
    retired two more (eight are left)** — the mark that turns one long
    scroll into titled cards (`Editor Project Settings.dc.html`). It is a SMALL
    SHARED vocabulary: seventeen `Settings_Kicker_*` values across all 43
    groups, so three groups reading EDITING is the information. Deliberately NOT
    searchable — the search matches a group's title and subtitle as each tab's
    `SearchGroups` declares them, and a category word would make every group on
    a tab a hit for one query.
  - **Each rail entry carries a one-line description** (`TextBlock.SideTabDesc`,
    wrapping — German runs ~40 % longer), the content column is capped at
    **`FireColumnMaxWidthLg` and CENTRED** (1040 px; 1400 above the wide
    breakpoint — see below), and the footer is `dirty note | Reset section ·
    Cancel · SAVE CHANGES`.
  - **The footer's "N unsaved changes" is REAL, and the shell SUMS it.**
    `SettingsShellViewModel` adds up every `ISettingsChangeSource.UnsavedSettings`
    it was given and retakes the baseline in `LoadAll` and after `Save`, so
    flipping a switch back reads zero (`Presentation/Settings/SettingsShellViewModelTests`).
    Every tab is its own source through Presentation's explicit
    `SettingsChangeSet` — it names each setting it tracks (a static `Tracked`
    array, or Audio's `TrackedSettings`), so a status field can never be counted
    (`Presentation/Settings/SettingsChangeSetTests`). The reflection tracker that
    counted the old page's generated properties went with the page.
  - **Two whole families were invisible to it until 2026-09-04, and both lose
    the user's edit in SILENCE — leaving the Settings tab runs the page's
    `Cancel`, which reverts them.**
    - **The Graphics tab's sixteen effect toggles** bind live at `ProjectState`.
      `ProjectState` was not a source at all, so turning Bloom off read
      "Everything saved". Since the tab migrated (2026-09-21) they are
      pass-through properties on `GraphicsSettingsViewModel` (`.Effects.cs`),
      tracked by its `SettingsChangeSet` and forwarded from `ProjectState`'s
      `PropertyChanged` — Play Mode's own settings modal writes the same
      properties, and `ProjectState` is not a change source of its own;
      making it one would count every flip twice. Guard:
      `Presentation/Settings/GraphicsSettingsViewModelTests
      .EverySettableBoolOnTheContract_CountsAsOneUnsavedChange`, derived by
      reflection over the contract.
    - **The SELECTION rows** — colour-blind string palette, fps cap, present
      mode, MSAA — bound an OBJECT while persisting a stable id, and the old
      tracker rejected every non-primitive. Graphics' three bind value-equal
      choice records now; the palette is `SelectedStringPaletteIndex`, an index
      into the FIXED `IAppearanceTheme.StringPalettes`, persisted as the id it
      selects (the one theme since 0.62 left no theme picker to count). Guards:
      `Presentation/Settings/GraphicsSettingsViewModelTests` and
      `Presentation/Settings/AppearanceSettingsViewModelTests
      .PickingAPalette_PreviewsIt_AndKeepsTheCustomColours`.
    - **Appearance's colour edits are previewed and reverted, but NOT counted**
      — exactly as on the old page, whose tracker never saw its colour list. The
      footer reads zero after a colour edit while Cancel still puts the persisted
      theme back; counting them is a separate decision.
  - **A tracked setting must also have a ROW, and nothing checked that until
    2026-09-04.** `UI/SettingsTabReachabilityTests` walks from every tracked
    property to the markup under `Views/Settings` (plus the TONE3000 panel). It
    found three on the Stem Models tab: `MaxConcurrentSeparations` and
    `EnableCpuFallback` had no row at all — persisted, counted, pushed at the
    JUCE orchestrator, settable only by hand-editing `appconfig.json` (both were
    later deleted with that orchestrator, which never separated, #662) — and the
    "Execution provider" row bound a SECOND, near-identically named property
    (`ExecutionProvider`, a pass-through to `StemModelManager`'s in-memory
    field) instead of `PreferredExecutionProvider` (today the row binds
    `SelectedExecutionProvider`, which reads and writes it), so the pick reached no
    config field and no orchestrator and was overwritten on the next load. The
    dead pair is deleted; do not restore a second one. Its exemption list
    carries a ROUTE per entry (the input-jack ComboBox mirrors
    `AsioInputChannelLeft`; the mixer inspector's `Transport.MidiOffsetMs`
    persists `MidiDelayMs`) and only ever shrinks.
  - **A STATUS field on an EXCLUSION-list type is counted as a setting until
    somebody names it, and both of them shipped.** `BackupStatusText`
    ("Exported 412 file(s) to …") and `AsioControlPanelError` were
    `[ObservableProperty]` strings on two types the old tracker corrected
    NEGATIVELY, so its default rule swept them in: exporting a backup, or a
    driver refusing to open its ASIO panel, left the footer claiming "1 unsaved
    change" that no edit had made and no Cancel could clear — and the import
    path sets its status AFTER its own `ReloadFromDisk` rebaseline, so that one
    lied too. That is why every tab now lists what it counts POSITIVELY; an
    exclusion list is only right for as long as nobody adds a status field.
  - **Every tracked setting must ROUND-TRIP, and that is now linted** —
    default-gate `UI/SettingsRoundTripTests`. For each of the six settings
    surfaces it takes the tab's own tracked list and requires each name to be
    ASSIGNED on a `LoadAppConfig()` path and NAMED
    on a `SaveAppConfig()` path (plus one level of the private helpers a save
    calls — that is what carries `BuildLogCategories` and
    `GetSelectedLanguageCulture`). Comments are stripped first. It caught all
    three faults it was written for: a status field counted as a setting (both
    of the above) and `UseWebPedalboard`, a DEAD `[ObservableProperty]` with no
    config field, no row and no consumer, left behind when the WebView
    pedalboard was dropped on airspace grounds. **The only justified entry is
    `AnchorPreviewLeadMs`** (persisted from `ProjectState`, which its OnChanged
    partial writes); a new one is a claim somebody has to argue for, and the
    list only ever shrinks.
  - **Saving is EXPLICIT (owner's call left to the agent, 2026-09-22).** Save is
    enabled only while the unsaved count is non-zero (the footer then reads "No
    unsaved changes", not "Everything saved" beside a live Save button); Reset
    section is enabled only while the SELECTED tab has edits, and asks through
    `IDialogs.MessageAsync` before it discards them. That gating is safe only
    because every tab is a change source whose settable members are all counted
    (`SplitTabAssert.EverySettableMember_CountsOnce`) — a setting the count
    missed could not be saved at all.
  - **"Reset section" resets the SELECTED TAB through that tab's own existing
    load path**, never to defaults — so it cannot drift from what a re-open
    restores. The shell's `ResetSectionCommand` calls `ISettingsTab.Reset()` on
    the selected tab, and each tab keeps that agreement in its own view model.
    On the old page two branches once did not (Audio routed at
    `AudioSettingsViewModel.Cancel`, whose whole body was `IsOpen = false;`, so
    the button visibly did nothing while the footer went on counting the
    change). Keybinds' `Reset()` is its `Revert()` (reload the table from disk
    and re-register), Updates' is its `Load()`, and Appearance's is its
    `Revert()` — the persisted theme back if a colour was previewed, then
    `Load()` — pinned by `Presentation/Settings/KeybindsSettingsViewModelTests`,
    `Presentation/Settings/UpdatesSettingsViewModelTests` and
    `Presentation/Settings/AppearanceSettingsViewModelTests.Reset_IsRevert_ThroughTheLoadPath`.
    - **Agreeing on the METHOD is not agreeing on what it re-reads**, and Stem
      Models proved it (2026-09-04). Both routed at `StemModels.Refresh()`, and
      that method rebuilt only the model ROWS — so the three backend knobs
      were read once in the constructor and never again. That screen persists ON
      CHANGE rather than on Save, so after a config-backup import (which reloads
      every tab over replaced files) the next touch of any of the three wrote
      the STALE pre-import value straight back. `StemBackendViewModel.Load` is
      the seed now, guarded from re-persisting during a load, and
      `Presentation/Settings/StemBackendViewModelTests` pins that a reload
      re-reads and writes nothing.
    - **The Keybinds tab's rows bind a table that gets REPLACED.** The rows
      are DATA now — `Presentation/Screens/Settings/KeybindRowCatalog` (one line
      per `KeybindConfig` chord, 140, held equal to the config by
      `UI/KeybindDefaultsTests`) → one `KeybindRowViewModel` each, reading the
      LIVE table through a supplier. `Revert` assigning a fresh `KeybindConfig`
      onto `ProjectState` IS the revert, so without a notification all 140 rows
      went on resolving against the discarded object — showing the chords the
      user had just reverted away from, and writing any later rebind into an
      orphan the next Save would throw away. `KeybindsSettingsViewModel` follows
      `ProjectState.PropertyChanged` and re-announces every row's `Chord`; Play
      Mode's `GameplayKeybinds` twin (`PlayModeSettingsViewModel`) re-announces in
      `WatchKeybinds(true)`, which
      runs when its modal opens. `Presentation/Settings/KeybindsSettingsViewModelTests`
      covers both, and pins the negative (editing a chord announces only that row).
    - **The same rows bind a MIDI controller (#310).** The tab's
      Keyboard | MIDI controller segment (`IsKeyboardMode` / `IsMidiMode`, a view
      state, never saved) flips every row from its chord pill to a MIDI pill
      (`Button.RowAction.MidiPill` in a `Grid.MidiCell`, plus a clear ×). A click
      LEARNS: the next press from `Services/Infrastructure/Input/IMidiTriggerInput`
      is written into the same live table (`KeybindConfig.MidiBindings`, verb →
      `MidiTrigger`, saved in `keybinds.json` by the same Save, dropped by the same
      Revert) and counts as one unsaved change (`midi.<Verb>`). A trigger runs ONE
      verb: learning it on another row takes it away. The four digit-family rows
      (`KeybindConfig.MidiUnbindableVerbs`) read "Keyboard only". "Reset all
      keybinds" leaves MIDI binds alone; "Clear all MIDI bindings" is its MIDI-mode
      twin. With no device listening the strip under the segment is a
      `NoticeStrip` warning (`MidiInputStatus`, `IsMidiInputLive`) and the rows stay
      editable. Each row's pill of the mode NOT on screen is a hidden subtree, so
      `KeybindsSettingsView` is an `IPrewarmableSurfaces` (listed by
      `MainWindowPrewarm.PrewarmableSurfaceOwners`); without it the first switch
      paid ~110-400 ms of first layout in the Debug headless harness, ~27 ms after.
      Pinned by `Presentation/Settings/KeybindsMidiSettingsTests` and
      `Perf/KeybindsMidiUiPerfSmokeTests`.
  - **Since the Fire conversion it speaks the Fire ladders ONLY**, same rule and
    same lint as the dialog grammar: group card radius 16 on `FireSurfaceCard`,
    a Chakra Petch 17 px group title over a Space Grotesk 12.5 subtitle, 13.5/Bold
    row titles over 11.5 descriptions, a MONO `ValueReadout`, and
    `TextBlock.AdvancedLabel` as a real Fire EYEBROW (mono 9.5, `.24em`, dim).
    `Border.NoticeStrip` is now the dialog grammar's `Border.DlgStrip`
    byte-for-byte in shape — a 3 px left bar on a matching tint — because a
    notice is a notice on both surfaces.
  - **The boolean is the shared `FireToggleSwitch`** (44 × 24 pill), set as a
    `Theme` on `Grid.FormRow > ToggleSwitch`. It resolves to nothing under a
    platform-free construction and the switch keeps Fluent's look, which is
    exactly why the three caption/MinWidth setters beside it MUST stay.
    `Perf/SettingsConstructionUiPerfSmokeTests
    .EveryBooleanOnTheSettingsPage_IsTheSharedFireSwitch` measures `PART_Track`
    at 44 × 24 — and only over VISIBLE switches, because a control in a
    collapsed Advanced section has applied no template at all.
  - **The SHELL (now `SettingsShellView.axaml`) went with it**: a Chakra Petch page title,
    `TextBlock.RailSection` as a mono eyebrow, `FireSurfaceChrome`/`FireSurfaceSunken`/
    `FireSurfaceFooter` on the header / rail / footer bars, the footer's Save as
    the chamfered `FirePrimaryButton` inside `StackPanel.SettingsActions`, and —
    the biggest change — **every rail row is now the shared `FireNavRowToggle`
    ControlTheme** rather than the local pill+spine template it carried. That
    brings the spec's rule that the 3 px accent bar is there AT REST (violet-dim),
    which is what gives an unselected rail a left edge; it is the same component
    the Play Mode settings modal's rail uses, so the two settings surfaces cannot
    drift. Guarded by `Perf/SettingsShellUiPerfSmokeTests
    .TheShell_ListsEveryRegisteredTab_WithItsGlyph_AndWearsTheFireRail`, which
    builds the page over the REAL service graph (`SettingsSurface.Create`).
  - Group-action buttons (`StackPanel.GroupActions > Button`) are a Cursor-free
    restatement of `Button.FireGhost` — see the next bullet for why the class
    itself cannot be worn here.
  - **The control column is FIXED (300 px; 184 on the keybind rows) and the
    content strip is capped, so very little in it is rescued by a wider
    window.** The cap became a TOKEN on 2026-09-12 — `FireColumnMaxWidthLg`,
    1040 by default and **1400 above the wide breakpoint** (inner width > 2560
    DIPs; `Services/UiBreakpointController`, and
    [fire-design-system.md](fire-design-system.md) § Breakpoints). **That widens
    the CEILING and not the content**, and the difference WAS measured: the style
    was `MaxWidth` + `HorizontalAlignment="Center"`, every tab is a
    `ScrollViewer` over an auto-sized `StackPanel`, and at 3840×2160 the widest
    of the nine tabs arranged at **570 px at both 1040 and 1400**
    (`Perf/UiResolutionAuditUiPerfSmokeTests
    .TheSettingsColumn_WidensAtTheWideBreakpoint` still logs both, and still
    reads 570 — it measures the NON-embedded card, see the next bullet).
  - **…and the page USES the column since 2026-09-13 (audit P0.4). The fix was
    ONE WORD, and the 570 px above is partly a HARNESS artefact.** Two separate
    things were wrong:
    - **The old shell had a floating 880×780 card mode beside the embedded
      page, and the 570 px figure was first measured on the card.** The card
      is GONE with the old shell: the page is embedded only, and there is no
      `IsEmbedded` flag left to set.
    - **The tab roots were `HorizontalAlignment="Center"`, which is a CEILING
      and not a fill** — a centred element is arranged at its DESIRED width. The
      setter — `Grid#TabContentGrid shell|ScreenTabHost > :is(UserControl)` in
      `SettingsShellView.axaml` — is `Stretch` now;
      Avalonia's `ArrangeCore` still CENTRES a `Stretch` child whose size was
      clamped by `MaxWidth`, so the cap and the centred column both survive.
      Measured on the embedded page: the NARROWEST of the nine tabs went
      **570 → 1040 px at 1920×1080 and 570 → 1400 px at 3840×2160** (100 % of
      the column), and the room lands on each row's `*` label column while the
      300 px control column keeps its single right edge.
  - **Above the wide breakpoint FIVE tabs flow their groups into TWO columns,
    and the four that do not are a decision, not an omission.** The tab's root
    `StackPanel` becomes `ctl:MasonryPanel Classes="SettingsFlow"` (the
    inspector's own panel — it packs each card into the currently-SHORTEST
    column, so the two columns end level, where a `WrapPanel` would make every
    row as tall as its tallest card and needs an `ItemWidth`, i.e. a numeric
    card cap this page must not grow). `ColumnCount` is **1 by default and 2
    under `Window.Wide`**, both setters in `SettingsStyles.axaml`, so below 2560
    inner DIPs the tab is the vertical stack it always was — and children keep
    their DECLARATION order, so the search walk, the tab order and keyboard
    navigation are untouched.

    | tab | groups | two-column? |
    |---|---|---|
    | General | 3 | yes — the Advanced (logging) body is the second column's |
    | Editor | 4 | yes |
    | Play | 1 | **no** — one group, so a plain `StackPanel` like TONE3000 |
    | Files & backups | 3 | yes |
    | Graphics | 6 | yes — `GraphicsSettingsView` |
    | Stem Models | 6 | yes |
    | Audio | 5 | yes |
    | Updates | 2 | yes |
    | AI | 2 | yes — the provider card and the match-model card |
    | Keybinds | 7 | **no** — its 140 rows are ALREADY two-up in a `UniformGrid Columns="2"` on the `*,184` grammar; splitting again gives four chord columns of ~230 px and the capture pill truncates |
    | Appearance | 2 | **no** — the colour list is one WIDE control that flows its own 320 px columns |
    | TONE3000 | 1 | **no** — two columns of one card is a column and an empty gutter. A one-group screen uses a plain `StackPanel`, not `MasonryPanel Classes="SettingsFlow"` |

    Asserted at both resolutions by `Perf/UiResolutionAuditUiPerfSmokeTests
    .TheSettingsTabs_FillTheColumn_AndFlowIntoTwoColumnsWhenWide` and
    `.At1080p_TheSettingsPage_KeepsItsSingleColumnAndDoesNotOverflow`. **The
    first asserts the NARROWEST tab, never the widest, and that is
    load-bearing**: a `MasonryPanel` returns the available width from
    `MeasureOverride`, so the five opt-in tabs fill under `Center` too, and a
    max over nine tabs only goes red when every tab is broken. Slowest
    steady-state tab switch measured 8.0 ms against the 150 ms budget (logged,
    never asserted — Settings is an `IPrewarmableSurfaces` and the first
    measure of each hidden tab is the prewarm drain's).
    A horizontal `StackPanel` there arranges each child at that
    `DesiredSize` clamped to the slot — a perfectly correct rectangle drawn
    outside of. The Updates tab's "Check for updates" row shipped it: with a
    pending update its two buttons were arranged 27 px past the row's right edge
    **in English**, before German roughly doubles the captions. The fix is the
    layout guide's: a `WrapPanel` with `ItemSpacing`/`LineSpacing` (it has no
    `Spacing`) and `MinWidth="0"` per button, so each sizes to its own content
    and the cluster takes a second line. Measured by
    `Perf/SettingsConstructionUiPerfSmokeTests
    .NothingInAControlColumn_IsArrangedPastTheRowsRightEdge`, which walks the
    ARRANGED position — desired width is clamped to the constraint the control
    was measured under, so it reads as a clean fit on a row that is slicing its
    content, and re-measuring against INFINITY is wrong the other way (it flags
    the `WrapPanel` that is the fix). It is one `[Fact]` over all eight tabs
    rather than a Theory per tab, because Keybinds and StemModels put a single
    control in every control column and cannot have this fault at all.
    **Two more rows had it, and only a de-DE run could see them** (2026-08-28):
    General's Backup & Migration pair arranged to 991 px in an 896 px row and
    Updates' LAN pair to 969. Both are `WrapPanel`s now, identical to the
    Check-for-updates row above. The suite grades English unless you tell it
    otherwise — `$env:DOTCHARTER_HEADLESS_CULTURE = "de-DE"` — and the release
    script runs this class under de-DE and ja-JP.
  - **Two more Settings surfaces were sized for English and are not any more.**
    The page SUBTITLE in `SettingsShellView`'s header wraps: the search box beside it
    is a fixed 320 px, so its column is whatever is left (470 px under the
    sweep) and the German sentence wanted 514.2. And the Keybinds tab's
    `GroupHead` is `Auto,*,Auto` — the Danger "reset" button on the right takes
    its width FIRST, and in German it starved the `*` title column to 137 px
    while `"Tastenbelegung"` wanted 148.5. Wrapping the title cannot fix that
    (one word; `TextWrapping.Wrap` breaks it mid-word), so the BUTTON is bounded
    — `MaxWidth="200"` with a wrapping `TextBlock` face and an explicit
    `AutomationProperties.Name`, since the face is no longer a `Content` string.
- **`SettingsStyles.axaml` must stay constructible with NO Avalonia platform and
  on ANY thread**: **no `Cursor` setter** (building a `Cursor` resolves
  `ICursorFactory`) and **no `Transitions` setter**
  (`AvaloniaList<ITransition>.Add` calls `Dispatcher.VerifyAccess`). Both were
  added during the 0.35 settings rework and both blew up the platform-free tab
  constructions of the time — the Transitions one only when another test had
  created the dispatcher first, so a focused run was green and the gate was red.
  Source lint: `UI/FireStyleClassTests
  .NeitherSharedGrammar_CarriesACursorOrTransitionsSetter`, which applies the
  same ban to `DialogStyles.axaml` and the shell grammars (the old
  `SettingsSearchTests` copy of it went with the markup walker). The three tabs
  with an Advanced section set `Cursor="Hand"` inline on the header instead.
  **This is why a settings control may not simply wear `Button.FireGhost`:** that
  class sets a `Cursor`. Wearing it is harmless — the class lives in the
  app-scoped `Styles/Fire.axaml`, which is only ever built under a real `App`,
  so a platform-free tab construction never touches it — but COPYING it into
  this file is not. Settings' ghost buttons are a Cursor-free restatement;
  anything that needs the hand cursor sets it inline.
- **A row hidden behind a driver check is a row the user is told to go and use
  and cannot find (0.44.3).** Audio's "Input channels" row was gated on
  `SelectedInputDriver == 'ASIO'`, so on WASAPI there was NO way to open a second
  input at all — while the (since removed) co-op HUD's Shared-input tooltip said to "open the
  interface's second input in Settings". Only half of that row is ASIO-only: the
  channel COUNT is `Mono ? 1 : 2` on every driver
  (`JuceAudioPlaybackService.ApplyAudioConfigurationCore`), and only the channel
  INDEXES need the mask, which is built for ASIO alone. The row is now shown on
  both drivers; `AreInputChannelIndexesUsed` hides just the two boxes, and it
  keys on **`SelectedDriver`** — the ENGINE driver, `AppConfig.AudioDriver`,
  because ASIO is single-driver-exclusive and the input is forced onto the
  output's driver, so the boxes did nothing for an output-WASAPI/input-ASIO user
  either. The WASAPI note beside them says what the driver cannot do rather than
  promising it can. **Since 2026-08-30 the LEFT box is a `ComboBox` over
  `AudioSettingsViewModel.InputJackOptions`** ("Input 1", "Input 2", … from
  `GuitarInputChannels.JackOptions`, the device's `MaxInputChannels` when the
  probe knows it, a generic eight before it does), mirroring
  `AsioInputChannelLeft` both ways so Save writes the field it always wrote;
  picking the right box's jack swaps the two. The same rows are what the
  onboarding wizard's Input page and the rig footer's picker show — see
  `docs/agents/services/audio-engine.md` § the live seat count. Guard:
  `UI/AudioSettingsJackPickerTests`.
- **`AudioSettingsViewModel.Save` must never write a device the DROPDOWN did not
  offer (2026-09-04).** Every device index and device NAME it persists — WASAPI
  and ASIO output, both inputs, the streaming mirror, the MIDI output — is
  derived from rows `Open()` fills on a BACKGROUND probe. Save asked nothing
  about them, so a Save landing first saw each `Selected…Index` at its initial 0
  over an EMPTY list and wrote index 0 with an empty name; `ResolveIndex` reads
  an empty name back as "fall through to the index", i.e. **the first device**,
  and the routing change then restarted the engine onto it. Not a rare race: the
  first-run wizard's "Play test click" and "Apply input" are
  `EnsureAudioPagesOpened()` (Open) followed immediately by this Save, in one
  click. `DeviceListsLoaded` gates the four device blocks; the MIDI row carries
  the winmm ids the same enumeration produced (`_midiOutputDeviceIds`) instead of
  re-enumerating, so Save does no P/Invoke at all. An unprobed list is not a
  selection — leave the stored value alone. Guards:
  `UI/ViewModels/Settings/AudioSettingsDeviceSaveTests`,
  `UI/ViewModels/Settings/AudioSettingsMidiSaveTests`.
  The MIDI enumeration is `IAudioDeviceControl.GetMidiOutputDevices()` (a ctor
  `Func` until 2026-09-21, moved onto the seam to keep the view model at seven
  constructor parameters; `FakeAudioPlaybackService.MidiOutputs` answers it in
  tests) and `DeviceProbe` exposes the probe task, so
  `Tests/Infrastructure/OnboardingWizardRig` neither
  touches the box's real MIDI drivers nor races the probe (`Advance(n)` settles
  it per step — its `InlineDispatcher` otherwise runs the marshalled
  continuation on the POOL thread, mutating the device collections while Save
  indexes them).
- **…and `AudioSettingsViewModel.Save` must APPLY what it wrote (2026-09-04).**
  It calls `IAudioPlaybackService.ApplyAudioConfiguration` only when its own
  `HasRoutingChanged` (now `Presentation/Screens/Settings/AudioSettingsRules`)
  says something moved, and that check compared device
  INDEXES while `JuceAudioPlaybackService.ResolveOutputDeviceId` opens devices by
  **NAME** — so a Save that moved the user to a different device at the SAME row
  number applied nothing. The ordinary case is a saved device that has been
  unplugged: `Open` cannot match the stale name, `ResolveIndex` falls through to
  the index, and the Save that finally records the right name restarted nothing,
  leaving the engine on whatever the startup fallback opened. All five names
  (both outputs, both inputs, the mirror) and **`MidiOutputDevice`** are in the
  comparison now — `ApplyMidiDevice` is reached from
  `ApplyAudioConfigurationCore` and nowhere else, so picking another MIDI port
  and pressing SAVE CHANGES used to leave the old one open for the session.
  The two DELAY trims are deliberately NOT in that check: they are pushed
  straight at the service beside `InputPitchShiftMode`, because the playhead
  compensation and the MIDI scheduler read them live and routing them through
  `HasRoutingChanged` would restart the engine for a slider — but until then
  `ApplyAudioConfigurationCore` was their only writer, so a delay-only Save also
  did nothing until the next launch. Guard:
  `UI/ViewModels/Settings/AudioSettingsApplyReachTests`, which pins the negative
  too (an unchanged Save must NOT restart the engine — two restarts in quick
  succession on the UI thread can fast-fail the process inside the native
  driver-close path).
- **Settings is a KEYBOARD FORM, so `BringIntoViewOnFocusChange="False"` is not
  available to its tabs.** The owner reports that opening a dropdown scrolls the
  pane back to the top, and the obvious remedy is the
  attribute the Play Mode lists wear. **It would cost Tab navigation**: with the
  property left alone, focusing a settings row above the viewport moved a parked
  pane from y 400 to 109; with it false on the tab's `ScrollViewer`, 400 to 400 —
  the focused row simply stays off screen. Both numbers are measured, and
  `Perf/SettingsScrollAnchorUiPerfSmokeTests
  .FocusingARowAboveTheViewport_StillScrollsItIntoView` is the second one turned
  into a failure. **The lever is on the SCROLLVIEWER, not on the focused
  control** — `ScrollViewer.OnGotFocus` reads the attached property on ITSELF,
  so `ScrollViewer.SetBringIntoViewOnFocusChange(combo, false)` changes nothing
  (400 to 109 either way, measured).
  - **The jump does NOT reproduce headless, and that is a measurement, not a
    shrug** (2026-08-29, Avalonia 12.1.1). Parked at y 400 and driven with real
    headless mouse input, the old General tab kept its offset through the
    dropdown opening, through it closing, and through focus being forced onto a
    zero-size sink the way `MainWindow.AnchorFocusIfLost` does — and again at
    y 2000 with the combo at the top of the viewport, at UI scale 1.0 and 1.25
    through a `LayoutTransformControl` like `MainWindow`'s `RootScaler`, with the
    window at screen y 0 and 300, and with 3 / 40 / 200-item lists whose
    selection forces the popup to scroll itself. **The reason is the popup
    HOST**: `Avalonia.Headless` puts a ComboBox popup in the window's own overlay
    layer (owner and item report the same visual root), so the cross-root case
    the desktop backend produces never arises. Reproducing this needs the real
    Win32 `PopupRoot`.
  - What IS confirmed of the handover's chain: **a popup's `GotFocus` and its
    `RequestBringIntoView` really do travel out of the popup and through the
    settings `ScrollViewer`** (a popup root's interactive parent is its LOGICAL
    parent, so a popup is never routing-isolated from the control that owns it) —
    four `ComboBoxItem`-targeted requests per open, pinned by
    `.ThePopupsOwnFocusAndBringIntoView_ReachTheSettingsScrollViewer`. What is
    NOT confirmed is the last link, "a `(0,0,0,0)` target sets `Offset.Y=0`":
    `ScrollContentPresenter.BringDescendantIntoView` returns early on a null
    `TransformToVisual`, and a zero-SIZE target at a real position scrolls to
    that position (measured 400 to 3123), not to the top. **Do not build a fix on
    that link without re-measuring it on the desktop backend.** Since the General
    split (2026-09-22) both scroll classes park Settings ▸ Editor, the longest
    tab left; it has ONE dropdown, so the Tab-navigation case focuses any row
    control above the parked offset rather than a second dropdown.
- **General ▸ "Open at startup" and "Run setup again" (0.49).** The first is
  `GeneralSettingsViewModel.SelectedStartupScreenIndex` (0 = Play Mode front
  door, 1 = chart editor) over `AppConfig.StartupScreen` — the same field the
  onboarding wizard's role page writes and the shell's constructor routes on
  (`StartupRouting`); it round-trips through the tab's `Load` / `Save` like
  every other row. The second is `RunSetupAgainCommand` →
  `IFirstRunSetupPrompt.Open()` → `OnboardingWizardViewModel.Open()`, which overlays
  the Settings tab (its dialog sits above Settings in `AppDialogService
  .AllDialogs`, so Esc reaches it first). Both are linted by
  `UI/Onboarding/OnboardingReachabilityTests`; the wizard itself is documented
  in [dialogs.md](dialogs.md).
- **The old General tab was SPLIT into four screens (2026-09-22), and the
  inventory of all four is pinned** (`UI/GeneralSplitInventoryTests`). The old
  tab had 11 groups, 54 rows and 80 binding paths; after the split:
  - **General** (`general`, Workspace) — Language & region (language, startup
    screen, run setup again) · Diagnostics (crash report) · and, behind a
    collapsed Advanced disclosure, Logging (13 rows: the two old checkbox rows
    became four switches, console and file each with their dependent rows).
  - **Editor** (`editor`, Workspace) — Chart editing (10) · Copy & paste (one
    full-width row of six checkboxes) · New project (4) · Guitar Pro import (9).
    The millisecond row and the denominator row enable by the spacing mode
    (`IsMinNoteDistanceInMilliseconds`).
  - **Play** (`play`, Workspace) — Gameplay (count-in, metronome, claps, rock
    meter). The count-in map is `GameplayCountInRows`, shared with Play Mode's
    modal.
  - **Files & backups** (`files`, System) — Save companions (7) · Backups
    (automatic backup, its interval, export / import through
    `BackupMigrateViewModel`) · Decoded song audio.
  The lint exists because a REGROUP is the change most likely to lose a row
  silently: a dropped option compiles, lays out and reads as done while its
  config field, its save path and `UI/SettingsRoundTripTests` all stay green.
  Moving a row between groups or tabs is free; losing one is red, in both
  directions. It also pins that every group carries a `GroupTitle`, that no
  `FormRow` sits outside a group, that no group is row-less, and the row count
  of each view. `UI/SettingsRoundTripTests` reads all four view models too.
- **A backup IMPORT re-applies the theme through Appearance, not through Files &
  backups.** The import (`BackupMigrateViewModel.ImportAsync`) drops the caches
  over the imported files (`ImportedConfigStores`, again in a `finally`),
  re-applies the language and the log filter and bumps
  `SettingsSession.RequestReload`; the shell reloads every tab, and
  `AppearanceSettingsViewModel` answers the same bump by applying the imported
  theme through `IAppearanceTheme.ApplyPersisted` — unconditionally, because the
  palette on screen is the one the import replaced
  (`AppearanceSettingsViewModelTests.AReloadRequest_AppliesTheImportedTheme`).
- **Appearance talks to two Services seams, colours as hex strings**
  (`Services/Infrastructure/Appearance/`, decided 2026-09-22).
  `IAppearanceTheme` (implemented by
  `Platform/AppearanceTheme` over `IThemeService`, `ThemePresets`,
  `StringColorPresets` and `ThemeOverrides`) carries the token catalogue, the
  string palettes, the live per-token preview, `ApplyStringPalette` (keeps the
  customized colours), `ResetColors`, `ApplyPersisted` and the free-form
  override set Save persists. `IInterfaceScale` (`Platform/InterfaceScale` over
  `UiScaleController` + `InterfaceScaleChoices`) carries the Auto-first choice
  list, the index/factor map, a pick (`Apply`) and a load (`Restore`). **Revert
  re-applies the theme only when something was previewed** — a re-apply
  invalidates every `DynamicResource` in the app (30–80 ms) and was the bulk of
  the leave-Settings freeze when nothing changed
  (`Revert_WithNothingPreviewed_DoesNotReapplyTheTheme`). **Load never
  re-applies the theme**, so a reopen does not clobber a preview.

## The cross-tab search

**It reads what every tab DECLARES; it walks no tree of its own.** Each
`ISettingsTab` exposes `SearchGroups` — groups with a title, a subtitle and rows
carrying a label and whatever else the row prints — and `ApplySearch(query)`,
which hides the rows and groups that do not match. `SettingsShellViewModel`
(`SettingsShellViewModel.Search.cs`) runs one pass per typing burst: it applies
the query to every tab and lists the hits `Presentation/Screens/Settings/SettingsSearch.Hits`
returns, in rail order, capped at 40 rows while the count line reports the real
total. A tab the user has never opened is searched exactly like the one on
screen. The pure rules are in `Presentation/Settings/SettingsSearchTests`; the
shell's half (coalescing, the cap, the keyboard, the jump) in
`Presentation/Settings/SettingsShellViewModelTests`; the end-to-end
"every tab hits while unvisited" case, over the REAL service graph and real
localized strings, in `Perf/SettingsShellUiPerfSmokeTests.EveryTab_IsSearchable_WhileUnvisited`.

- **The rules are the old walk's.** A row matches when its label or its
  description contains the query (case-insensitive); a group whose title or
  subtitle matches shows every row in it; a group with NO rows is one setting
  whose label IS its title — it is reachable in the dropdown by that title. No
  tab declares one today: Appearance's colour list, the last, declares every
  colour as a row (label: its name, description: its heading). Each hit
  carries the group it came from, because labels like "Device" / "Interval"
  repeat across tabs. Search is text; a migrated tab builds its groups from
  `ILocalizationService` on every read, so a language change needs no rebuild.
- **Every tab declares its rows in its view model and filters by flipping
  contract booleans.** Keybinds' rows and Appearance's colours are items with `IsVisible` (one template
  draws them); Updates, AI and Stem models have few rows and carry
  `Is…RowVisible` / `Is…GroupVisible` flags on their contracts. **Stem models
  splits its flags across four PARTS** — `IStemBackendPart`, `IGpuRuntimePart`,
  `IStemCachePart` and the screen itself — because its six groups have
  independent lifetimes; the screen's `ApplySearch` hands each part its own
  rows. That is the shape to copy once a tab's groups stop moving together. **AI folds the provider condition
  into the SAME flag** — `IsApiKeyRowVisible` is "the search kept it AND this
  provider takes a key" — so the view binds one flag per row and decides
  nothing; the provider half stays readable as `ShowKey` / `ShowBaseUrl` /
  `ShowCliPath` / `ShowClaudeCliNote` on the view model. Prefer items with `IsVisible` once a
  tab has more than about ten rows — a flag per row on the contract stops
  scaling, and `ContractBindingLintTests` requires every one to be bound.
- **There is no markup walk any more.** The old page's tabs were searched by
  `SettingsSearchWalker` over their logical tree, which is why every row had to
  be a `Grid.FormRow` and every group a `Border.SettingsGroup`; the walker,
  its `search-hidden` classes and its tests went with Appearance (2026-09-22).
  The grammar still keeps those classes — the layout lints find rows and groups
  by them — and a collapsible "Advanced" section is still a
  `ToggleButton.AdvancedHeader` over an `IsVisible`-bound panel, never an
  `Expander`.
- **Typing is coalesced through `Presentation/Shared/CoalescedPass`** (the UI's
  `ViewModels/Shell/CoalescedFilterPass` is the same type with the Avalonia
  dispatcher as its default post) — one pass per burst, posted at background
  priority through `IUiDispatcher`, no timer, no guessed delay.
- **The keyboard stays in the box.** Up / Down / Enter are `KeyBinding`s on the
  search `TextBox` bound to `HighlightNextResultCommand` /
  `HighlightPreviousResultCommand` / `ActivateResultCommand`; the highlighted
  row scrolls into view through the `Views/Shell/ScrollIntoView.cs` attached
  property. Ctrl+F is the one thing in `SettingsShellView.axaml.cs` past the
  constructor, because FOCUS is a view concern no view model can express.
  Pinned end to end by `Perf/SettingsShellUiPerfSmokeTests
  .TheKeyboard_WalksTheHits_AndEnterJumpsToTheTab_WithoutLeavingTheBox`.
- A hit jumps to its tab with the filter still applied and closes the dropdown;
  clearing the query restores every row and group on every tab.

## How to add a Settings tab (step B is done)

**Nothing on the page is legacy any more.** Appearance, the last old tab,
migrated on 2026-09-22 over two new Services seams (`IAppearanceTheme`,
`IInterfaceScale`); General had split into four screens the same day, and
Graphics (over a widened `IGraphicsDeviceSettings`) and Audio moved on
2026-09-21. A new tab follows the same pattern — copy the Play or Appearance
pair. **Audio is
the one SHARED tab**: its view model is an app-wide singleton the editor tab,
Play Mode's modal and the first-run wizard all drive, so
`SettingsModule.RegisterSharedTab` forwards each scope's three registrations to
it instead of building one per scope (`Presentation/Settings/SettingsModuleTests
.TheAudioTab_IsTheSameInstanceInEveryScope`). Its search catalogue is
`AudioSettingsCatalog`, kept in step with the view by
`AudioSettingsTabTests.TheCatalogueAndTheView_NameTheSameKeys`; a row that
already had its own visibility condition combines it with the search through a
`MultiBinding` over `BoolConverters.And`. A tab declares its search rows once,
as resx keys, in a `SettingsTabRows` (`GeneralSettingsViewModel` is the worked
example, with a collapsed Advanced section) — or, when its rows are data, builds
them from that data (`AppearanceSettingsViewModel`'s colour rows) — and tracks
what it saves in a static `Tracked` array the tests read by reflection. The old
finish line, `UI/SettingsPageSeamTests`, was deleted with the page it pinned.

1. **Contract** `Presentation/Contracts/Settings/I<Tab>SettingsScreen.cs`: formatted strings, booleans,
   `ICommand`s, `IReadOnlyList<item contract>` for data rows; every member gets bound (`ContractBindingLintTests`).
2. **Fake** `Presentation.Fakes/Settings/Fake<Tab>SettingsScreen.cs`: `Default`, `Empty`, `LongText` (German +
   Japanese), `Full` for lists, `Busy` / `Error` when the contract has `IsBusy` / `HasError`; all in `Variants`.
3. **View model** `Presentation/Screens/Settings/<Tab>SettingsViewModel.cs`: `ObservableObject, I<Tab>SettingsScreen,
   ISettingsTab, ISettingsChangeSource`, at most 7 constructor parameters (split a block into a same-folder part,
   as `GitHubSignInViewModel`). `Key` is the `SettingsTabKeys` constant, `Screen` the contract type; `Load` / `Save` /
   `Revert` / `Reset` are the old page's four branches for the tab; a `SettingsChangeSet` tracks each setting and
   raises `UnsavedSettings`; `SearchGroups` from the row keys, `ApplySearch` via `SettingsSearch`. Config through
   `IConfigurationService`, never files. **Search hides rows through ONE set, not a flag per row**: expose
   `IReadOnlySet<string> HiddenKeys` and bind each row / group with
   `IsVisible="{Binding HiddenKeys, Converter={x:Static settings:SettingsKeyVisibility.Instance}, ConverterParameter=<key>}"`
   (`Views/Settings/SettingsKeyVisibility.cs`); `GraphicsSettingsViewModel.ApplySearch` is the worked example,
   including opening a collapsed Advanced section when a hit is inside it.
4. **View** `DotCharter.UI/Views/Settings/<Tab>SettingsView.axaml` on the contract (`x:CompileBindings`), `StyleInclude` of `SettingsStyles.axaml`, the row grammar unchanged, code-behind `InitializeComponent()` only.
5. **Register** `Screen<I<Tab>SettingsScreen, <Tab>SettingsView>(Settings, "<Tab>")` in `Views/Shell/ScreenCatalog.cs`
   and `RegisterTab<I<Tab>SettingsScreen, <Tab>SettingsViewModel>` in `SettingsModule.cs`.
6. **Rail row** in `SettingsRail.Entries` (and its `SettingsTabKeys` constant), with its title, description and glyph keys
   in all three resx files.
7. **Tests** `Presentation/Settings/<Tab>SettingsViewModelTests` on fake services (load / edit counts / save / revert /
   reset / search / `ContractConformanceTests.Violations`); add the tab to the lints that list surfaces
   (`SettingsRoundTripTests`, `SettingsTabReachabilityTests`, `SettingsUnsavedCounterHonestyTests`,
   `SettingsModuleTests`, `SettingsConstructionUiPerfSmokeTests.AllTabs`).
8. **Gates** `Invoke-Gate.ps1`, `-Headless`, `Update-Goldens.ps1 -Stage`; report the contact sheet, the owner accepts
   the baseline. Update this file's file list.
