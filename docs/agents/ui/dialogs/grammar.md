# Dialogs (`Views/Dialogs/`) — UI deep reference

Extracted verbatim from `DotCharter.UI/AGENTS.md`. **Read this before adding or
editing any dialog** — the grammar is defined once and included once, and every
class name below already exists. Its sibling one level up is the settings row
grammar in [settings.md](../settings.md); the two share the no-`Cursor` /
no-`Transitions` ban and the Fire-ladders-only lint.

## The grammar

- **Dialogs (`Views/Dialogs/`) — one dialog grammar, reached through
  `Views/Dialogs/DialogStyles.axaml` and included ONCE, by
  `Views/Shell/DialogContainerView.axaml`** (every overlay dialog is a child of
  its `DialogHost` panel, so one instantiation styles all **33** of them
  (Keyboard Shortcuts joined in 0.59, RS-XML Import in 0.60, the first-run
  onboarding wizard in 0.49 — see its own section below; four LEFT on
  2026-09-04: Selection Filter, Select By Query, Songsterr Resync (the whole Songsterr import
  was deleted later, #820) and Stem Regenerate. Count the `RegisterLazy` lines in `DialogContainerView.axaml.cs`
  rather than trusting this number); do NOT add a
  per-view `StyleInclude`). Exactly TWO dialogs are outside that host and
  carry their own include, each saying why in a comment:
  `AboutWindow` and — since 0.51 — `Views/Diagnostics/DiagnosticsWindow` (their
  own top-level `Window`s; the last is deliberately NON-MODAL so Session Health
  can be watched while the app is used). `LibraryDoctorDialogView` was the
  third until it became a catalog dialog on the `DialogHost`; the view that
  used to host it, `Views/Panels/SongBrowserView`, was deleted on 2026-09-23. A fourth, `ComponentManagerWindow`,
  went on 2026-09-04 — it duplicated Settings ▸ Stem Models with strictly fewer
  affordances. The lint's file sweep covers `Views/Diagnostics` as well as
  `Views/Dialogs`, so a fourth includer cannot arrive in a folder it cannot see.
  **`DialogStyles.axaml` is a CONTAINER now, not the definitions**: it is three
  `StyleInclude`s over `Views/Dialogs/DialogStyles/{ShellAndHead,BodyAndStates,
  Foot}.axaml` plus one `Styles.Resources` block, so the classes below are
  "defined there" only transitively — **edit the part, and any lint that reads
  this file must go through `Tests/Infrastructure/StyleFileText.ReadWithIncludes`
  or it reads an empty container and passes vacuously.**

  **Every Border / TextBlock / Grid / StackPanel family is a `ControlTheme`
  (#428)** in its part's `Styles.Resources`, worn with its class and reached with
  `DynamicResource` (a dialog view is built before it is parented under the host
  whose Styles hold them): `<Grid Theme="{DynamicResource DlgRowGrid}"
  Classes="DlgRow" …>`, `DlgLabelText`, `DialogCardBorder` (the card's whole look,
  fill included — the app-level `DialogCardBorder` in `Styles/Containers.axaml` is
  gone), `DlgStripBorder`, `DlgActionsPanel`, … As class RULES they were attached
  to every control of their type under the host (~30,000 frames). `DlgValueText`
  is based on `MonoText`; the `SelectableTextBlock.X` halves stay rules (only
  SelectableTextBlocks carry them). A `DlgRow`'s children carry
  `VerticalAlignment="Center"` LOCALLY — the child rule was attached to every
  child of every Grid. Descendant rules (`Border.DlgStrip TextBlock`,
  `Border.DlgSection Grid.DlgRow:nth-last-child(1)`, the footer's button rules)
  stay rules and still outrank a theme. **A descendant rule is attached to every
  control of its last type under ANY ancestor of its first type** — `Border.X
  TextBlock` to ~700 TextBlocks in the host — while a CHILD rule (`Border.X >
  TextBlock`) attaches only where the parent can match: the empty state's text rule
  is `Border.DlgEmpty > TextBlock` (+ `> StackPanel > TextBlock`) since #707, which
  took the dialog host from 2,040 to 1,395 frames. Prefer the child form for a new
  rule. Guards: `UI/StyleIncludeScopeTests`, `Perf/StyleIncludeScopeUiPerfSmokeTests`
  (the host's ceiling is 1,450).

  The vocabulary deliberately mirrors `SettingsStyles.axaml` one level down:
  `Grid.DialogScrim` → `Border.DialogCard` →
  `Grid.DialogHead` (a 30 px `Border.DialogIconBox` + `PathIcon.DialogIcon`
  badge, left-aligned `TextBlock.DialogTitle`/`DialogSubtitle`/`DialogContext`)
  → body of `Grid.DlgRow` rows (`ColumnDefinitions="*,N"`, label +
  optional `TextBlock.DlgDesc` left, exactly ONE control in the fixed column;
  `.Sub` indents a dependent row, `.Last` drops the trailing gap) optionally
  clustered in `Border.DlgSection` under a `TextBlock.DlgSectionLabel` →
  `Border.DialogFooter` (hairline rule; `StackPanel.DlgActionsLeft` for the
  tertiary action, `StackPanel.DlgActions` on the right with ONE
  `Button.Primary` LAST). States are `Border.DlgStrip` (+`.Notice`/`.Error`/
  `.Ok`), `Border.DlgEmpty`, `StackPanel.DlgBusy`, `Border.DlgListFrame` +
  `Border.DlgListRow`. Three marks joined in 0.51: `Button.DlgChip` (a body
  quick-pick that fills the field beside it — chrome on the
  `/template/ ContentPresenter`, like every other button class here),
  `TextBlock.DlgKeyName` (a derived readout on the display face — the song's
  KEY; a keyboard chord beside its command is `TextBlock.DlgChord`, the row
  label's size in the accent, since the shortcuts dialog wore `DlgKeyName`
  and out-shouted every command until 2026-09-24) and
  `SelectableTextBlock.Mono.Value` (mono at the reading weight, for a measured
  figure under its caption). **Three more came with the Tone Designer's body
  rebuild**: `ListBox.DlgList` (a list whose rows are PICKED — the Fluent
  container chrome neutralized so the row's own `Border.DlgListRow` carries
  hover / focus / selected; the hairline is there at rest and merely
  transparent, so selecting a row repaints it instead of growing it by 2 px),
  `TextBlock.DlgRowMeta` (+`.Dim` — the mono fact at the END of a row: what is
  loaded in this slot, `(empty)` quieter) and `Border.DlgTag` (an inline
  reported tag, the non-pressable sibling of `Button.DlgChip`).
  **`Button.DlgLink` names `ToggleButton` explicitly beside `Button`** for the
  Tone Designer's two-state Preview action, and that is not tidiness: an
  Avalonia type selector matches its type EXACTLY, so `Button.DlgLink` can
  never reach a ToggleButton and the control would render in the default look
  with no warning.
  - **Since the Fire conversion the file speaks the Fire ladders ONLY**
    (`FireRadius*`/`FireSize*`/`FirePad*`/`FireGap*`/`FireSpace*`) — the class
    NAMES are unchanged, the shapes are not: card radius 18 + `FireShadowScreen`,
    a Chakra Petch 20 px title over a Space Grotesk 12.5 subtitle over a MONO
    `DialogContext`, 13.5/Bold row labels over 11.5 descriptions, a MONO
    `DlgSectionLabel` eyebrow (9.5 px, `.24em`), and a 34 px row rhythm shared
    with the settings grammar. **Mixing an app token back in is a source lint**
    (`UI/FireStyleClassTests.EachConvertedGrammar_ReachesOnlyForFireLadderTokens`,
    default gate) — several app steps do not exist on the Fire ladders, so one
    stray `RadiusSm` is a corner 2 px rounder than its neighbours.
  - **It REUSES two shared Fire components instead of restating them**:
    the footer's `Button.Primary` sets `Theme="{DynamicResource FirePrimaryButton}"`
    (the 45° chamfer is a live `Clip` bound to the button's own bounds and the
    glow lives on an unclipped ancestor — a class style cannot express either),
    and a boolean in a row sets `Theme="{DynamicResource FireToggleSwitch}"`.
    Both are pinned at RUNTIME (`Perf/DialogConstructionUiPerfSmokeTests
    .EveryConvertedDialogFooter_ShowsTheChamferedFirePrimary` walks the laid-out
    TEMPLATE for the `Cut` border and its geometry) because a `Theme` setter that
    does not resolve leaves a fully functional control wearing the old look.
  - **`:not(.Primary)` on the footer's ghost rules is load-bearing.** A `Style`
    beats a `ControlTheme`, so a plain `> Button` ghost rule repaints the
    primary's violet fill grey while its chamfered template still applies — it
    reads as the theme half-failing. And the ghost's CHROME goes on
    `/template/ ContentPresenter`, never on the Button: Fluent's Button template
    ROOT is the presenter, so a fill set on the control is painted over and every
    Cancel in the app renders as bare text (that shipped for one build of this
    conversion and was caught in a screenshot, not by a test).
  - The scrim stays the app's TRANSLUCENT `OverlayScrim`, deliberately not the
    Fire layer's opaque `FireOverlayFill`: "a scrim is opaque" is a rule for a
    full-screen Play Mode modal with nothing behind it. A dialog here opens over
    the chart being edited, which is the context the dialog is about.
  - **A CLICKABLE list row is the one shape the vocabulary lacks, and the
    sanctioned answer is inline Fire tokens on that view — never a local
    `<Style>` block.** `Border.DlgListRow` is a Border selector, so it never
    matches a Button; `HistoryDialogView` (the undo tree) sets the row's
    Background / BorderThickness / Padding / CornerRadius inline and lets the
    hover fill arrive through the button template's own `TemplateBinding`, which
    is what makes the inline `CornerRadius` round it. Its previous local style
    block is what "an older build" looked like — it dressed the row out of the
    APP ladder (`RadiusXs`/`PaddingRow`/`SurfaceHover`/`AccentColor`) while the
    grammar around it had moved onto the Fire one.
  - **The DANGER tier (2026-08-11): a destructive action wears `Danger` in its
    class list** (`Classes="DlgAction Danger"`, `"Subtle Danger"`,
    `"DlgChip Danger"`, `"DlgLink Danger"`). The tier is COLOUR-ONLY — border
    and caption go `FireDanger`, hover fills `FireDangerTint` — and is declared
    LAST in `Foot.axaml` so it composes over every silhouette; it never changes
    shape, so it cannot compete with the primary. "Don't Save" next to a
    pixel-identical "Cancel" is the bug it exists to stop. Linted in the default
    gate by `UI/DialogDangerTierTests` (a destructive caption — Delete /
    Discard / DontSave / RemoveAll — without the class fails; the tier must stay
    the grammar file's final word) and at runtime by
    `Perf/DialogConstructionUiPerfSmokeTests.EveryDangerAction_ResolvesTheDangerForeground`.
    **The tier is colour, NOT a confirm gate.** A delete whose target is the
    ONLY copy (no undo, no file on disk to reopen) additionally takes the
    TWO-CLICK latch — the `Propagate` idiom from `PhrasesInspectorViewModel`:
    first click arms the row and relabels the button, second click executes;
    arming another row, a refresh or closing disarms. Checkpoint Delete is the
    worked example (`CheckpointsViewModel.Delete` and its row latch, pinned by
    `CheckpointsViewModelTests`) — it used to run on a single click directly
    beside Restore, while Restore (the RECOVERABLE neighbour: the .rscp on
    disk is untouched) was the one that prompted.
    **The latch is not dialog-only — it is the idiom for the shape, wherever the
    row lives.** The Riff Library's Delete is the second instance
    (`PhraseCatalogViewModel.IsConfirmingDelete` + `DeleteCaption`, pinned by
    `PhraseCatalogDeleteConfirmTests`): a stored riff is in the per-user library
    rather than the project, so undo cannot reach it and there is no `.rscp` to
    reopen. (The export dialog's preset Delete was a third; the presets are gone, #953.)
    When the caption is a BINDING, keep the Loc key on
    `AutomationProperties.Name` — it is what the screen reader gets, and it is
    also what `DialogDangerTierTests` counts.
  - **WHICH confirm: the latch is for an IN-PLACE row/selection delete, the
    app-wide `MessageDialogViewModel` prompt is for a SHELL or MENU verb.** A
    latch works because the button that armed it is still under the pointer and
    relabelled — a menu item that has already closed has no such surface, so a
    destructive menu verb takes `Dialogs.Message.ShowAsync(…, confirmation:
    true)` (`MessageDialogKind.Warning`, ZIndex 300, `ShowAsync` `virtual` so a
    caller's use of it is assertable) and the message NAMES what is lost.
    **`ShowAsync` queues behind an open message for a BOUNDED time** (since
    2026-08-29, `DialogTurnstile`, 3 min): a message dialog that opens while
    nothing renders it — the missing `RegisterLazy` line — used to hold the
    queue's semaphore forever and wedge every later `ShowAsync` in the process,
    the save-failure dialog included, with no log. Past the wait the newcomer
    DISPLACES the stuck one (its task answers `false`, the cancel) and shows;
    `MessageDialogViewModel.StuckDialogsDisplaced` non-zero means a dialog
    opened with no view. `DialogTurnstileTests` pins it.
    View ▸ Reset Layout is the worked example
    (`MainWindowViewModel.ConfirmLayoutResetAsync` + the pure
    `ResetLayoutAsync(confirm, apply)` rule, pinned by
    `LayoutResetConfirmTests`): it discarded the splitters, every floated pane,
    both pop-out windows and the persisted `DockLayout` on one click, from the
    menu AND the command palette.
    **Route the prompt through a settable `Func<Task<bool>>?` rather than
    calling `Dialogs.Message` inline, and treat UNWIRED as REFUSE.** Both halves
    are load-bearing: `MainWindowViewModel` takes 26 required services so no
    default-gate test can build one, and a confirm nobody can drive is a confirm
    nobody should trust; `BatchLintViewModel.ConfirmOverwriteAsync` is the
    older instance of the same shape. "No way to ask" must never resolve to
    "do it silently".
  - **DIRTY is not the same question as "is there something to lose".**
    `IProjectSessionService.MayReplaceOpenProjectAsync` asks only when the
    project is DIRTY, so every replace path that also runs
    `ApplyProject(resetHistory: true)` erases the undo/redo tree in SILENCE on a
    clean project — saved, or loaded-edited-saved, which is the ordinary state
    of a project someone opens Checkpoints from. Checkpoint Restore is the fixed
    case (`CheckpointsViewModel.ConfirmDiscardHistoryAsync`, pinned by
    `CheckpointsViewModelTests`): after the replace guard passes, a CLEAN
    project with a NON-EMPTY tree (`CanUndo || CanRedo`) takes a second prompt.
    **A dirty project keeps exactly ONE prompt and an empty tree keeps NONE** —
    a confirm that fires when nothing is discarded is the one people learn to
    click through without reading, which costs more than it buys.
  - **The SCRIM RULE (2026-08-11): clicking the scrim may only dismiss a
    READ-ONLY dialog; an EDITING dialog ignores it.** Six editing dialogs
    (Event, Paste Special, Pattern Builder, Showlights and the since-deleted
    Selection Filter / Selection Query) used to close on scrim-click with no
    dirty check — a mis-click
    one pixel past the card threw the typed work away. Esc still cancels every
    dialog (`IsCancel`, pinned by `UI/ModalEscapeHatchTests`); that is a
    deliberate gesture, a click beside the card is not. Enforced by
    `UI/DialogScrimRuleTests`: a `Grid.DialogScrim` wiring `PointerPressed`, or
    an `OnScrimPressed` handler, fails the gate unless the file is on the
    test's read-only allow-list — which is EMPTY, and an entry must be a dialog
    that edits nothing.
  - **The CLOSE TIER (2026-08-11): a footer Close follows its footer, not
    taste.** A read-only dialog whose only footer-right action is dismissal
    gets Close as the PRIMARY with `IsCancel` + `IsDefault` (Enter and Esc both
    leave — ChartDiff, Checkpoints, History, Library Doctor); a Close beside a
    committing Primary is SUBTLE so the chamfer stays on the action that
    changes something (Batch Lint's Run, Export's Export). The Tone Designer is
    the third shape: every edit in it commits immediately, so its one action is
    `Action_Done` with `IsDefault` and NO `IsCancel` — a button cannot honestly
    be the committing default and the cancel at once, and there is deliberately
    NO revert system behind it (decision 2026-08-11; Esc still closes via the
    global escape route). Linted by
    `UI/DialogDefaultActionTests.AFooterClose_SitsOnTheTierItsFooterDictates`.
  - **FOCUS-ON-OPEN (2026-08-11): a dialog's first input is marked
    `dlgv:FocusOnOpen.IsTarget="True"`** (attached behaviour in
    `Views/Dialogs/DialogStyles/FocusOnOpen.cs`). It watches the ancestor
    `Grid.DialogScrim`'s `IsVisible` and posts focus at
    `DispatcherPriority.Loaded` — after the layout pass that realizes the
    hidden subtree — so there is NO `Task.Delay` anywhere, and
    `UI/DialogFocusOnOpenTests` fails the gate if one comes back or if a
    target sits outside a scrim. One target per dialog; the calibration
    wizard marks its scrim itself (Focusable, Space/Enter tap). Runtime proof:
    `Perf/DialogConstructionUiPerfSmokeTests.EveryFocusOnOpenTarget_ReceivesFocusWhenItsDialogOpens`.
    **The target count is a RATCHET set to the CURRENT count, so losing one
    fails — 14 as of 2026-09-04; raise it when a dialog joins, never lower
    it.** It stood at 8 while five editing dialogs
    (Event, Level Generation, Vocal Track Settings, Arrangement Settings, Paste
    Special) still opened with focus nowhere, so the first keystroke went to the
    surface UNDERNEATH; in an editor whose single-key binds place notes, that is
    an edit to the chart rather than a lost character.
  - **A GREYED control owes the user its reason, and the reason binds to the
    INVERSE of the predicate that greyed it (2026-09-04).** A dependent row
    whose `IsEnabled` is a `StringConverters.IsNotNullOrEmpty` over a field
    above it reads, on screen, as an inert control with nothing connecting the
    two — the explanation lived in a source comment in every case found. The
    device is the grammar's own `TextBlock.DlgDesc` under the row label, shown
    on `StringConverters.IsNullOrEmpty` over the SAME source, so the hint and
    the gate can never disagree; a hint that shows while the control is live is
    worse than none. Worked examples: the event dialog's section-status and
    solo-phrase rows (`UI/EventDialogFidelityTests
    .EachDependentRow_PrintsTheReasonItIsUnavailable`) and the export dialog's
    vocals toggle. **A disabled PRIMARY is the same rule one level up**: every
    clause of its `CanExecute` needs a readout. Export has five and had four —
    "nothing is ticked", the state the dialog OPENS in on an all-unticked
    chart, reached a greyed primary in silence until
    `RocksmithExportDialogViewModel.NothingSelectedToExport` raised a
    `DlgStrip Notice` for it.
  - **A watermark is not a place to explain a field.** A `PlaceholderText`
    carries no ellipsis of its own (the `TracksPopoverView` ComboBox finding in
    [layout-traps.md](../layout-traps.md)), so a sentence in one is sliced
    mid-word at the control column's width — the export dialog's 62-character
    "Default tone-set" hint did that in ENGLISH at 400 px. The sentence goes in
    the row's `DlgDesc`; the watermark shows the SHAPE of the input.
  - **The WIDTH LADDER + CLAMP (2026-08-11): a card's width is one of FOUR
    steps, worn as a second class — `Classes="DialogCard Sm|Md|Lg|Xl"`**
    (480 / 600 / 720 / 1120, `FireDialogWidth*` in `FireTokens/Ladders.axaml`),
    replacing seventeen ad-hoc widths. The shared style also CLAMPS every card:
    `MaxWidth`/`MaxHeight` bind to the scrim's live bounds through
    `Converters/DialogCardClampConverter` (64 px gutter; height keeps the old
    820 cap on top), and a `Width` larger than `MaxWidth` loses by Avalonia's
    min/max rule — so the Xl workspace dialogs shrink on a laptop instead of
    centring off both screen edges, and the body row is the one that scrolls.
    A numeric `Width`/`Min*`/`Max*` on a card fails the gate
    (`UI/DialogWidthLadderTests`, which also pins the four tokens ascending and
    the grammar reaching them).
  - **The footer's LEFT slot may hold a mono meta line instead of a tertiary
    action** — `FireFontMono` + `FireSizeMicro` + `FireTrackMicro` +
    `FireTextFaint`, inline. Edit history prints its state/branch count there
    (derived in `Rebuild`, never a literal), Checkpoints "kept on disk",
    Arrangement "applies to this arrangement only" and New Project the formats
    its picker accepts (read off `AudioFileTypes.Extensions`, so the printed
    list cannot drift from the filter). A COUNTED one goes through
    `LocalizationService.Plural` and its pair joins
    `DotCharter.Tests/UI/PluralStringTests.Pairs`.
  - **A dialog whose rows only exist under a DataContext is UNMEASURED by the
    shared grammar checks** — they lay every dialog out with none, so an
    `ItemsControl` with no items leaves its whole template untested. Those
    dialogs carry their own case in `DialogConstructionUiPerfSmokeTests` that
    seeds the real view model (History, Checkpoints, Recovery, Showlights,
    Vocal track, Arrangement, New Project).
  **The control-column literal is per dialog but must be
  the SAME literal for every row in that dialog** — that shared column is what
  gives a dialog one right edge, and
  `Perf/DialogConstructionUiPerfSmokeTests` measures it (it also pins the
  prewarm contract: `DialogContainerView.HiddenSurfaces()` materializes every
  deferred dialog view exactly ONCE — the prewarm and the first open both call
  `Materialize`, so its re-entrancy guard is what stops one view model ending up
  with two views). Same two traps as
  `SettingsStyles.axaml` apply (no `Cursor` setter, no `Transitions` setter),
  plus one this file found: **only the `Padding*`/`Gap*` tokens are
  `Thickness`** — handing a `Space*` (`x:Double`) to a `Padding`/`Margin`
  setter throws `InvalidCastException` out of `OnAttachedToLogicalTree`, which
  reads as the dialog failing to construct. The wizard
  (`LatencyCalibrationWizardView`) is the one dialog with more than one footer:
  one per step, and exactly one step is visible. Regression:
  `DotCharter.Tests/Perf/DialogConstructionUiPerfSmokeTests.cs` (outside the
  default gate — builds every dialog inside a real `DialogContainerView`,
  attaches, lays out, and asserts scrim/card/head, the shared right edge, one
  primary action last, and that every state tint and icon `DynamicResource`
  resolved; plus two source lints for `StaticResource` use and for stray
  re-includes of the grammar).

