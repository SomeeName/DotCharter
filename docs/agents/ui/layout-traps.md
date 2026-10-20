# Layout, binding and styling traps (UI deep reference)

Extracted verbatim from `DotCharter.UI/AGENTS.md`. Every entry below is a fault
that **builds perfectly clean** and only shows up at runtime — a thrown
`InvalidCastException`, an unstyled control, sliced or overpainted text, a
silently unset binding, or a control that measures fine and draws nothing.

The two highest-frequency ones stay inline in `DotCharter.UI/AGENTS.md` (they
are cheap enough to carry everywhere): `Padding`/`Margin`/`BorderThickness` take
`Padding*`/`Gap*` and never `Space*`, and a style class that matches no selector
is a silent no-op. Everything else is here.

## Text that runs out of its container

- **`TextWrapping="Wrap"` is a NO-OP inside a horizontal `StackPanel`**, and a
  star Grid row STARVES its child rather than containing it. Both are the same
  trap: the panel measures with infinite width (or the child measures its full
  desired height), so the content runs out of whatever contains it and is
  sliced — no warning, no exception, and a bounds assertion still passes
  because the TextBlock itself measured fine. It is invisible in English and
  obvious in German, which runs ~40 % longer; the LAN modal shipped it four
  times in one screen (a hint off the card edge, a fact line straight through
  the Close button, a CTA overlapping the card above it, and a label sliced at
  45° by the primary button's chamfer). **Wrapping text needs a width bound —
  a `MaxWidth`, or a Grid star column. Content that can outgrow its slot needs
  a `ScrollViewer`, not a star row.** The direct-child half is linted by
  `DotCharter.Tests/UI/WrappingTextLayoutTests.cs`.
- **…and the same panel does not just SLICE its content — it PAINTS it into the
  next Grid cell (0.44).** A horizontal `StackPanel` arranges each child at that
  child's own `DesiredSize` starting from x=0 and clips nothing, while its own
  `DesiredSize` is clamped to the slot it was given. So the panel reports a
  perfectly correct rectangle and draws outside it, over whatever the neighbouring
  column holds. Measured in `RecoveryDialogView`: the star column gave the name
  block 216 px, the name measured 188 and the kind tag was arranged at
  x=194..284 — 54 px into the autosave stamp's column, which starts at 230, and
  the stamp (declared later, so higher in z) painted on top. That is the owner's
  "the timestamp is drawn over the subtitle", and neither `TextWrapping` nor
  `TextTrimming` could help: a `NoWrap` TextBlock in a horizontal `StackPanel` is
  never told a width to trim to. **A name-plus-tag line inside a sized cell is a
  `Grid ColumnDefinitions="*,Auto"`, not a horizontal `StackPanel`** — then the
  text has a real slot and its `CharacterEllipsis` engages. `ClipToBounds` on the
  cell's content is the width-independent backstop; an `Auto` column whose
  content is a CULTURE-formatted sentence also needs a `MaxWidth`, or it eats the
  star column in German. Caught in bulk by the UI sweep's `cell-content-overlap`
  check (see `DotCharter.Tests/AGENTS.md`), and pinned for this dialog by
  `Perf/DialogConstructionUiPerfSmokeTests.Recovery_TheAutosaveStamp_NeverPaintsOverTheNameBlock`.
- **A drawn Fire control (`FireLevelMeter`, `FireChipStrip`) paints at its
  NATURAL width whatever its bounds.** Its `MeasureOverride` returns the row
  width of its segments or chips and its `Render` draws them from x=0, so a slot
  narrower than that is overdrawn, not clipped — and no text check sees it,
  because nothing in it is a TextBlock. Size the slot from the control's own
  geometry (`FireLevelScale.RowWidth`, `FireChipStripGeometry.RowWidth`) or cut
  the segment / chip count. Found twice on 2026-09-24: the rig SIGNAL card's
  40-segment meters (238 px) pushed their readings into the other meter
  (`Perf/RigNodeMetersLayoutUiPerfSmokeTests`), and four path chips (106 px)
  spilled out of a 96 px column (`Perf/LibraryConstructionUiPerfSmokeTests`,
  `Perf/SongSelectRedesignUiPerfSmokeTests.ThePathChips_FitTheirColumn_UnderLongText`).
  Assert the geometry's width, never the measured `DesiredSize`, which Avalonia
  clamps to the slot.

## Columns

- **EQUAL columns size to the NARROWEST label, so a row of equal-width buttons
  is as wide as its shortest word allows.** A `UniformGrid Columns="3"` (or a
  Grid whose columns are all `*`) over Add / Duplicate / Delete gave every
  button the width "Add" needs, and "Duplicate" was sliced **in English** — 59.6
  px of text in a 47 px slot in the Pattern Builder, 52.7 in 36 in the Tone
  Designer. Translation turns that from a few pixels into a structural
  impossibility (`Hinzufügen`/`Duplizieren`/`Löschen` is 28 characters against
  18), so there is no fixed division of a 196-262 px rail that fits three of
  those words on one line. **Size such a cluster to its content and let it wrap**
  (`WrapPanel` + `ItemSpacing`/`LineSpacing`; `WrapPanel` has no `Spacing`).
- **…and a horizontal `StackPanel` of buttons inside a `MaxWidth`-capped card is
  the SAME fault with no slicing to warn you** — the panel arranges from x=0 and
  clips nothing, so the third button simply DRAWS past the card's border. The
  chart editor's launch card shipped it: three verbs
  (`Menu_NewProject`/`Menu_OpenProject`/`Menu_AttachAudio`) in a horizontal
  `StackPanel` inside a card capped at 440 px, measured **36 px outside the card
  in de-DE and 45 px in ja-JP, and 0 px in English** — which is why it was
  reported by the owner's German-speaking family and by nobody else. **A
  MaxWidth-capped card is WIDTH-INDEPENDENT: the overflow is identical at
  1920×1080 and at 1366×768, so "it's a small-screen bug" is the wrong instinct
  — the LANGUAGE is the variable.** The fix is a `WrapPanel` (the guarantee)
  plus a cap sized from the longest shipped language (the aesthetics): the card
  now measures 447 / 499 / 508 px in en-US / de-DE / ja-JP against a 560 cap, so
  every language still reads as one line. Pinned by
  `Perf/EditorLaunchCardContainmentUiPerfSmokeTests`, which lays
  `Views/Shell/EditorLaunchCardView` out at three screen sizes × all three
  cultures and asserts every button's arranged bounds lie inside the card. That
  view exists because the assertion needed a home — **`MainWindow` cannot be
  `Show()`n in the headless session** (the testhost dies without reporting a
  result), so markup inline in the shell window is markup no layout test can
  reach.
- **A FIXED column must be derived from the longest thing that can land in it,
  and the default data is part of "can".** Settings → Keybinds shipped 135 rows
  of `ColumnDefinitions="*,132"` (**it is `*,184` over 138 rows today** — the
  132 is the bug, not the current value), which left a 110 px slot inside
  `KeybindCaptureBox`'s pill (`Padding="10,3"` + a 1 px border) — and nine
  DEFAULT chords are wider, up to `Ctrl+Shift+PageDown` at 159.6 px. The pill's
  label is `FireFontMono`, so its width is exactly `characters × 8.4 px`;
  `UI/ActionLabelFitTests` computes the requirement from `KeybindConfig`'s real
  defaults rather than from a number written down anywhere, so a longer default
  chord fails the gate instead of shipping unreadable. `TextTrimming` on the
  pill is the BACKSTOP, not the fix — a chord that overflows must at least READ
  as truncated, because `Ctrl+Shift+Pag` is not this shortcut, it is a different
  one.
- **…and "derived from the longest thing that can land in it" means IN EVERY
  SHIPPED LANGUAGE.** Eight faults were found at once on 2026-08-28 the first
  time the headless suite was pointed at `de-DE`, and every one was a slot sized
  for English — a fixed 92 px label column (`DiagnosticsWindow`), a fixed 150 px
  footer cell (Get Ready's "Skip setup"), a fixed 300 px settings control column
  ×2, a `*` column starved by an `Auto` button beside it (Keybinds' reset), a
  third of a 320 px popover card, and a 150 px `ComboBox` whose PLACEHOLDER has
  no ellipsis of its own. The four fixes worth reusing: turn the fixed cell
  `Auto` and put the old number on it as `MinWidth` (English rhythm unchanged,
  longer languages take the room from a neighbouring `*`); wrap the label when
  its row is Auto-height; bound the greedy `Auto` sibling with `MaxWidth` and let
  ITS label wrap; and re-flow a three-across strip to 2 + 1. **Wrapping is not
  always available** — `Button.FirePopoverGridAction` is a fixed 32 px high, and
  a one-word title (`"Tastenbelegung"`) under `TextWrapping.Wrap` breaks
  mid-word, which is worse than the slice. Run
  `$env:DOTCHARTER_HEADLESS_CULTURE = "de-DE"` (or `ja-JP`) over the headless
  suite after any row work; the release gate does it for you but only at release
  time.

## Bindings and collections

- **Never fill a `Selection*` collection one item at a time — use
  `SelectionBulkOps.AddRange`/`ReplaceWith`.** The rule has now been broken
  three times, in three separate files, and the cost is quadratic rather than
  linear: every `Add` raises `CollectionChanged`, `SelectionService` answers by
  re-deriving its scalar and raising `SelectionChanged`, and
  **`InspectorViewModel` — a DI singleton, alive from boot whether or not the
  pane is shown** — answers THAT with `SyncNoteProperties`, which does
  `SelectedNotes.ToList()` plus seven full scans of the result (and
  `SyncChordProperties` also rebuilds an `ObservableCollection<ChordNote>` and
  the bend editor). Shift-clicking a 5,000-note level therefore cost ~12.5 M
  element copies across 5,000 list allocations, on the UI thread, on one click.
  `SelectionBulkOps` computes the union with a `HashSet` (the same
  `EqualityComparer<T>.Default` the `Contains` loop used, so the result is
  item-for-item identical) and writes it through
  `BulkObservableCollection.ResetWith` — ONE `Reset`, one rebuild. `AddRange`
  is the additive gesture, `ReplaceWith` the replacing one; nothing new raises
  nothing at all. Linted for `UI/Input/Tools` + the coordinators now in Services (`Editing/`, `Editing/Maintenance/`, `Session/`, `Audio/Coordination/`, `Infrastructure/Recovery/`) by
  `Services/Selection/SelectionBulkOpsTests.NoToolOrCoordinator_FillsASelectionInsideALoop`,
  which walks the statement chain above each `Selected*.Add` and fails if any
  link in it is a loop header. A SINGLE-item add (the click target itself) is
  fine and is what the four remaining calls in those folders are.
- **Never bind into a collection by index** (`{Binding Groups[0].Severity}`).
  Every such collection in this app is refreshed by `Clear()` + `Add()`, so the
  binding resolves against an EMPTY list in the empty state and in the window on
  every single refresh; Avalonia logs "Index was out of range" and leaves the
  target property UNSET — which is not the converter's fallback, because the
  converter is never reached. Expose the value as a property recomputed where the
  collection is rebuilt (`InspectorIssuesViewModel.TopIssueSeverity` ←
  `IssueGrouping.TopSeverity`). Linted repo-wide by
  `UI/IssueDotSeverityTests.NoViewBindsIntoACollectionByIndex`.

## Templates, themes and resource dictionaries

- **A custom `TemplatedControl` gets its template from a TYPE-KEYED
  `ControlTheme` in `Styles/ControlThemes.axaml` — never from a `Style` with a
  `Template` setter, and never from one view's `UserControl.Styles`.**
  `KeybindCaptureBox` lived as a `Style` inside the old settings shell's own
  `UserControl.Styles` until 0.39.1,
  so the Pattern Builder's three capture fields had **no template at all** and
  rendered as an empty column. That failure is invisible three ways over: no
  warning, no exception, and the control still MEASURES — it stretches to fill
  its grid cell, so bounds-based smoke tests pass while it draws nothing. Moving
  it to an app-level `Style` did not help either; the template only resolved once
  it became `<ControlTheme x:Key="{x:Type controls:KeybindCaptureBox}">`. The
  guard is `DialogConstructionUiPerfSmokeTests
  .TheKeybindCaptureBox_HasAnAppScopedThemeAndMeasures`, and it has to walk the
  **visual** tree — a template's children are not logical descendants, so this
  file's own `Descendants<T>` (logical) cannot see them and would pass either way.
- **A duplicate `x:Key` in a resource dictionary builds clean and then crashes
  the whole app at `App.Initialize()`.** Merging two style files is the usual way
  to introduce one.

## Construction-time platform traps

- **A `Geometry` must resolve through `{DynamicResource}` and the view must be
  constructed on a real UI thread.** `Geometry.Parse` needs
  `IPlatformRenderInterface` off the locator, so an inline `Data="M…"` or a
  deferred `StreamGeometry` resource throws
  "Unable to locate 'Avalonia.Platform.IPlatformRenderInterface'" when the
  view is built off the dispatcher — which is how a headless test constructing
  a view outside `Dispatch` reports what looks like a product fault.

## Traps that live next to their surface

- **A slider thumb is clipped by THREE nested boxes** (`Thumb`, `Slider`,
  `DataValidationErrors`), and a comma-joined selector mixing a plain and a
  `/template/` part applies to NEITHER — both in
  [shell-chrome.md](shell-chrome.md) § the popover shell.
- **A `Padding`/`Margin` setter fed a `Space*` token** on a board view —
  [network-surfaces.md](network-surfaces.md) restates it for that grammar.
- **A `ControlTheme` that must be type-keyed inside a popup namescope** —
  [shell-chrome.md](shell-chrome.md) § the popover shell.
- **A DROPDOWN'S chrome cannot be set by a style selector at all** (override the
  Fluent resource KEY instead) — [shell-chrome.md](shell-chrome.md) and, for the
  dock pane chrome, [editor-workspace.md](editor-workspace.md).

## Width-dependent layout

- **A number that decides SHAPE is a token, and the shape switch lives in ONE
  place.** `MaxWidth="1040"` written into a view is invisible to the breakpoint
  mechanism, and a hand-rolled `SizeChanged` handler that toggles a class is a
  second source of truth for the same question. Read
  [fire-design-system.md](fire-design-system.md) § Breakpoints before adding
  either: the thresholds (`< 1600` / `1600…2560` / `> 2560` **inner**, i.e.
  post-`RootScaler`, DIPs), the `Window.Wide` / `Window.Compact` selector route,
  the `Ladders.Wide.axaml` token route, and the list of tokens that may never be
  breakpointed are all there.
- **`MaxWidth` + `HorizontalAlignment="Center"` is a CEILING, not a fill.** The
  panel is arranged at its DESIRED width, so a column whose content wants 570 px
  looks identical at a 1040 cap and a 1400 one — which is exactly how a "the
  page now uses the window" change can measure as a no-op. Raising a cap only
  helps content that already asks for the room.
- **`LineHeight` is half a type step and never a literal** — same section. A
  frozen line box beside a token-driven `FontSize` clips the moment anything
  ramps the size ladder.
- **A fixed `Height` inside a CLAMPED card does not shrink with it — it PAINTS
  OVER its neighbours.** The dialog grammar caps every card at the window
  (`DialogCardClampConverter`); a `Height="560"` last child of the card's
  `DockPanel` is still arranged 560 tall in the shorter slot, CENTRED, so it
  spills over the docked head and footer by the same amount on both sides (#210,
  the Tone designer at 1280×720: the body over the subtitle and over Done). A
  `MinHeight` on a card beats the clamp the same way. When the height is stated
  on purpose (lists inside that own their scrollbars), use
  `Controls/YieldingHeightDecorator` (`PreferredHeight`): it asks for that height
  whatever the content and takes what it is given. Every hosted dialog is held to
  head / body / footer at 1280×720 by `Perf/DialogWindowFitUiPerfSmokeTests`.
- **A `StackPanel` handed less height than it asked for keeps the bounds it was
  given and PAINTS ITS CHILDREN ON, under the footer** (#725). A capped list
  (`ScrollViewer MaxHeight="280"`) inside a body `StackPanel` never shrinks: a
  StackPanel offers its children infinite height. In a clamped card the growing
  list goes in the body's STAR row (a `Grid`, so it gives up height and scrolls
  itself) or the whole body is a `ScrollViewer.DialogBody`; see
  [dialogs/hosted-dialogs.md](dialogs/hosted-dialogs.md) § "A body that outgrows
  1280x720". A bounds check on the body's own children cannot see it —
  `DialogWindowFitUiPerfSmokeTests` reads what every unclipped descendant PAINTS,
  through transforms (`TransformToVisual`), so a `Viewbox` is read at its drawn
  scale, not its child's design size.

## Typed DataContext boundaries

When a control both changes `DataContext` and declares the child's `x:DataType`,
use an explicitly typed parent path for the context lookup. Pattern Builder and
Amp & FX use `$parent[UserControl]` with the owning view-model cast, so tools do
not resolve the parent property against the child type. `TypedContextUiPerfSmokeTests`
checks the attached pattern editor, preset/save browser and NAM context identities,
and the EQ chip's `EqBandOff` class still applies its disabled opacity.
