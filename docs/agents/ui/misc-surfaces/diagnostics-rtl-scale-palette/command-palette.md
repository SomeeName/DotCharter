# Session Health, accessibility, RTL, global UI scale and the command palette — command palette (Ctrl+Shift+P)

## Command palette (Ctrl+Shift+P)

- A fuzzy-search overlay over every discoverable command lives in
  `ViewModels/CommandPalette/` + `Views/CommandPalette/`. `CommandCatalog.Build(
  MainWindowViewModel)` is the read-model: it enumerates the menu commands + the
  reachable `AppKeybindRegistrar` actions as `AppCommand`s (id/title/category/live
  shortcut lookup/execute), reusing the existing `Keybind_*` / `Keybinds_*` /
  `Menu_*` resource keys so no per-command strings are duplicated. The catalogue
  is rebuilt each open (via a `Func` provider) so shortcuts + enabled state are
  always current. `CommandFuzzyMatcher` is the pure, unit-tested subsequence+rank
  scorer (`CommandFuzzyMatcherTests`); `CommandPaletteViewModel` filters/selects.
- The overlay is a scrimmed `CommandPaletteView` hosted directly in
  `MainWindow.axaml` (bound to `MainWindowViewModel.CommandPalette`, self-collapsing
  via `IsOpen`). Toggle + in-palette nav (Esc/Enter/Up/Down) are handled in
  `MainWindow.GlobalKeyDownPreview` (window tunnel) so they win regardless of
  focus; typing still flows to the search box.
- **Every rebindable action belongs in the catalogue, and that is now TESTED
  (0.41).** The palette claimed to be "one flat list of every discoverable app
  command" while only wrapping what hung off `MainWindowViewModel`, so ~45
  EDITING verbs had no entry at all — every technique toggle and its group
  variant, fret ±1/±12, string ±1, the six fingering keys, the string mask,
  double/half time, duplicate, stamp section, paste-at-mouse, delete-related and
  the four FHP nudges — and several were reachable by KEYBIND ALONE. Two more
  (Select-by-Query, Checkpoints) were menu-only with no key and no entry.
  `CommandPaletteCoverageTests` now reflects over `KeybindConfig` and fails on any
  `KeyChord` property with no palette row unless it is on the `NotInThePalette`
  list **with a reason** (today: the four digit FAMILIES whose verb needs an
  argument, the seek/speed/grid granularity keys, and four surface-scoped modal
  keys). It also pins id uniqueness — an id is what the usage statistics count
  under, so a duplicate silently merges two commands' totals — and that every
  title and category key resolves in BOTH resx files, because a missing one
  renders as the raw key name in the row.
  - **`CommandCatalog` reaches the editing services through
    `MainWindowViewModel.HighwayEditing` / `.GlobalEditing`, which are
    `internal`** — the palette is the one legitimate consumer; a view that wants
    to edit goes through a command on the shell like everything else.
  - **A verb the palette cannot find also reads as UNUSED.** The 0.41 usage
    statistics take their denominator from this catalogue
    (`Diagnostics/UsageStatistics`), so an unlisted command is invisible in the
    "never used" report that decides what gets moved into a submenu.
  - **MENU-ONLY verbs are the coverage test's blind spot** — it walks
    `KeybindConfig`, so a menu row with a command but no keybind is structurally
    invisible to it. Five real gaps accumulated there until 2026-08-15
    (the ten Edit ▸ Bookmarks rows, the five View ▸ Timeline Rows toggles —
    whose only other surface, the pill strip, had been deleted — Help ▸ Usage
    Statistics / Session Health, and a File ▸ Exit row that was bound to
    NOTHING at all). `CommandPaletteMenuVerbTests` pins those, deriving the
    Timeline Rows set from the MENU markup so a sixth row cannot arrive without
    its palette twin.
  - A keybind whose action was written INLINE in `AppKeybindRegistrar` cannot be
    listed without duplicating it. Lift it onto the shell as a `[RelayCommand]`
    and have the registrar call that (`StampSection` was the last one), so the
    key and the palette run one implementation.
  - **…and the mirror of that: a catalogue row may not re-implement a verb the
    shell already exposes as a command.** `FromCommand` resolves the usage bucket
    through `CommandIdResolver`, exactly as the menu's click hook does;
    `FromAction` cannot, so an inline row counts under its synthetic catalogue id
    while its neighbours count under the VERB. Three panel toggles were inline
    until 0.60 — Inspector, 3D Preview and Riff Library — beside four that were
    not, so one family of View rows reported half each way, and the shell's
    `ToggleInspectorCommand` / `TogglePreviewCommand` / `TogglePhraseCatalogCommand`
    each had a single caller (the keybind registrar) while the palette ran a
    second copy of the same one-liner. `CommandUsageBucketTests
    .ThePaletteDoesNotReImplementAPanelToggleInline` is the lint: no
    `vm.Is…Visible = !vm.Is…Visible` in `CommandCatalog.cs`, because panel
    visibility is the shell's own toggle-verb family and every member has a
    command. Flags on CHILD view models (`vm.Transport.MetronomeEnabled`,
    `vm.ProjectState.ShowWaveform`) stay `FromAction` — those types expose no
    toggle command.

