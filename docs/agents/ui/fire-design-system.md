# The "Fire" design system (UI router)

## Graphite rules that override the Fire mockups

The one theme is Graphite ([fire/graphite.md](fire/graphite.md)). Where a
Fire mockup below says otherwise, **these win**, and each is pinned by a test:

- **Copy is sentence case, in every language's own rules.** No all-caps labels,
  no Title Case, no shouting a word inside a sentence, no trailing colon on a
  label. Nothing re-uppercases a string: `{localization:Loc …, Upper=True}` and
  the `ToUpper` converters are gone from the views. Acronyms, product names and
  Rocksmith's own phrase names keep their case.
- **Labels are words, so the UI face; figures are the mono face.** Eyebrows,
  column heads, stat labels, tags, badges, key hints, dialog section labels and
  pane titles use `FireFontUi` untracked; scores, times, tunings, hex and code
  use `FireFontMono` (`FireFigure` included, for tabular digits). The rig's
  hardware silkscreen keeps its mono on purpose.
- **The lit action is flat:** 8 px rounded, no chamfer, no glow, no hover lift
  (`FireTokenValueTests.TheLitAction_IsFlatSquareAndUnlit`,
  `Perf/FireComponentUiPerfSmokeTests.TheLitActions_AreGraphiteFlat_RoundedAndUnlit`).
  `Button.Primary` is flat too. A disabled lit action is NEUTRAL and so is its
  label (`ADisabledLitAction_KeepsItsLabelReadable`).
- **An ON toggle is a tint with an accent glyph,** never a solid accent block:
  `ToolbarToggleBtn`, the stacked-track flags, `Chip`, `Seg`, `RibbonChip`.
- **A state colour on a Fluent Button or ToggleButton goes on its PRESENTER.**
  Fluent paints `:pointerover`, `:pressed`, `:disabled`, `:checked` and
  `:indeterminate` on `/template/ ContentPresenter` with TRIGGERED setters,
  which beat the presenter's binding to the control's own `Background` /
  `Foreground`. A state set on the control never shows: the Primary label went
  white on light blue on hover and the button went grey on press. Restate the
  label as `TextElement.Foreground` in every state. Likewise a template part's
  resting colours (the CheckBox box, the ToggleSwitch track) are template values
  only a triggered selector beats — `:unchecked`. Pinned by
  `Perf/ControlStateColourUiPerfSmokeTests`.
- **Fluent's stock accent follows the theme** (`Theming/FluentAccent`,
  `FluentAccentTests`): without it stock controls paint the Windows accent.
- **Figure line boxes are ~1.25× their size;** at 1.0× the comma is clipped and
  "1,940,200" reads "1.940.200" (`LineHeightTokenTests`).
- **Radii are the soft-square ladder** (screen 12, panel 10, card and control 8,
  field and row 6, tag 4, micro 3) in `FireTokens/Ladders.axaml`.
- **Glyphs the UI face lacks** (♭ ♯) fall back to Segoe UI Symbol before any
  CJK face (`Platform/TextFallbackFonts`, `TextFallbackUiPerfSmokeTests`).

The owner commissioned eleven inline-styled HTML mockups for the app's menus.

> **These eleven files are GONE (verified 2026-08-23).** They lived only in
> `%USERPROFILE%\Downloads\fire\New Menu\` — outside the repo — and no copy
> survives anywhere on the machine. **This doc layer is now the only record of
> them**, so treat every value written down here as unrecoverable if it is lost
> too, and never "simplify" a token table on the grounds that the mockup can be
> re-checked. It cannot.

The table below is what each lost mockup contributed:

| Mockup | What it contributes |
|---|---|
| `Main Menu Sidebar.dc.html` | the front door: status bar, grouped nav rail, brand + CTA, session-queue rail. **The richest example — read it first.** |
| `Launch Screen.dc.html` | the launch screen: brand lockup, chamfered START with an `ENTER` key chip, the "playing as" row + segmented Profiles/Guest with an expanding panel, three status chips, a footer of version + key hints |
| `Settings.dc.html` | the modal screen: floating panel, tab rail with title lockup, content header + search + close, labelled rows, status pill on the rail floor, footer with one primary action |
| `Statistics.dc.html` | (its screen was removed on 2026-09-19) hero panel, big-figure stat cluster, technique bars, weak-spot cards, ranked list |
| `Session History.dc.html` | (its screen was removed on 2026-09-19) two left rails, the **selected-row 3 px accent bar**, run detail, section breakdown, stacked timing meter |
| `Practice Calendar.dc.html` | (its screen was removed on 2026-09-19) month heat grid + legend, checklist rows, goal meters, steppers |
| `Practice Trends.dc.html` | (its screen was removed on 2026-09-19) column chart with gridlines, per-section sparklines + trend legend, blocker card, speed meter |
| `Practice Planner.dc.html` | (its overlay was removed on 2026-09-19) the second modal shell, sliders, chips, choice groups, toggle switch, footer bar |
| `Song Library.dc.html` | grouped list rows, arrangement tags, mastery meters, right-hand song detail rail, notched PLAY |
| `LAN Multiplayer.dc.html` | the third modal shell (940 × 600, header / two-column body / footer), single-select choice CARDS, seat dots, a selection-driven list whose action lives in the footer |
| `Online.dc.html` | setup hero, feature sub-cards, sample board at reduced opacity |

**Read several before building a screen.** Any one of them looks like a
complete system and is not; the shared vocabulary is what
[fire/vocabulary.md](fire/vocabulary.md) records.

**This file is a ROUTER.** The exhaustive reference is split across
[`fire/`](fire/) and linked below. What stays here is the handful of rules that
bite whatever you came for, plus the row that tells you which file to open.

## The three rules that make this layer safe

1. **It redefines nothing.** Every key and class is `Fire`-prefixed. The
   editor, the pedalboard, the dialogs and the eight settings tabs are
   untouched until a surface opts in. A global colour change would restyle
   every surface at once with no way to review it one screen at a time.
2. **The colours are FIXED, not theme-switchable.** They are brushes with
   literal values, not `Color` tokens in `App.axaml` + `ThemeTokens` — the same
   call already made for the amp rig (`AmpOrange*`/`Rig*`) and the podium
   medals. This is an art direction, not a palette a user recolours. It also
   means the theme-switch completeness gate is untouched and a community theme
   pack cannot half-apply it.
3. **The ladders are self-contained.** A Fire surface uses ONLY `FireSize*` /
   `FireRadius*` / `FirePad*` / `FireGap*` / `FireSpace*`. Several mockup steps
   (9.5 / 10.5 / 11.5 / 12.5 / 13.5 / 22 / 26 / 58 px; radius 9 / 11 / 12 / 16 /
   18 / 20) do not exist on the app's scales, and mixing two ladders is how a
   screen ends up half-converted.

## Rules that bite (whatever you came here for)

- **`FirePad*`/`FireGap*` are `Thickness`; `FireSpace*`/`FireSize*` are
  `x:Double`.** Handing a double token to `Padding`/`Margin`/`BorderThickness`
  throws `InvalidCastException` when the control attaches, from a clean build.
  Pinned in both directions by `DotCharter.Tests/UI/ResourceTokenTypeTests`.
- **No literal hex in a view.** `DotCharter.Tests/UI/ViewHardCodedColourTests`
  scans `Views/**` for a colour in a brush position. A hex in a view is
  invisible to every other gate and is the one pixel that does NOT move when a
  token is corrected — eleven of them moved at once in 0.47. The one exemption
  (`RigWebView`'s browser slot) carries its reason in the test, and a second
  case asserts the exemption itself cannot rot.
- **`BoxShadow` is declared on `Border`, NOT on `Control`.** A `<Setter
  Property="BoxShadow">` inside a `Button` selector fails the AXAML compile
  with AVLN2000. That is why the two LIT actions (`FireCta`, `FirePrimary`) own
  a template whose ROOT is a Border, while the unlit ones (`FireGhost`,
  `FireQuiet`, `FireBack`) are class-gated rules over Fluent's template. All of
  them are `ControlTheme`s since #240 — see "A button is a ControlTheme" below.
- **A button is a ControlTheme (#240), and it wears its class TOO.** A global
  `Selector="Button.X"` style is attached to EVERY Button in the app, with its
  activator and its `DynamicResource` setters, whether or not the class is set:
  98 value frames per button after the prewarm, 17 once the families moved. So
  each family is a `ControlTheme` keyed `<Family>Button` (`FireGhostButton`,
  `SubtleButton`; a class already ending in `Button` keeps its name:
  `FireOutlineButton`) in the family's own file, and a button names it:
  `Theme="{StaticResource FireGhostButton}" Classes="FireGhost Compact"`
  (`DynamicResource` in a top-level `Views/Dialogs` or `Views/Diagnostics`
  view, whose rule is all-dynamic).
  - **The class stays because the theme's rules are gated on it** (`^.FireGhost`,
    `^.FireGhost.Compact`, …), copied from the global styles in their old order.
    A gated rule is a style TRIGGER, so it still beats Fluent's TemplateBinding
    and its state setters exactly as the global one did; an ungated theme setter
    would lose to both. The class also keeps view-local overrides
    (`Button.Subtle` in `InspectorStyles`) and descendant rules
    (`Button.FireCta TextBlock.FireCtaLabel`) working.
  - **A Button has ONE theme**, so a modifier two families share (`^.Compact`,
    `^.Danger`, the plain `^.Primary` that the dialog footer's `FirePrimaryButton`
    also carries) is COPIED into each theme that needs it, and the copies stay
    identical. A class worn only with one family (`FireMapTool`, `FireGearStep`,
    `Stepper` → `FireQuietButton`) lives in that family's theme.
  - **A copied state rule that paints `/template/ ContentPresenter` assumes
    Fluent's template, where the presenter IS the button.** `FireCtaButton` and
    `FirePrimaryButton` own their template, and there the presenter is the padded
    content strip inside the fill, so the copied `^.Danger:pointerover` drew a
    dark bar inside a Compact Danger CTA and a box behind a dialog Primary's label
    (#348). Such a theme adds its OWN rule after the copies, clearing the presenter
    (the Sheen is the hover there), rather than editing the copy;
    `ControlStateColourUiPerfSmokeTests` pins both.
  - **A theme loses to EVERY app style at the same priority.** Never add a global
    `Button.X` style (it is back on every button and outranks the themes); and a
    global TRIGGER style on a property a family sets (a `:focus-visible`-gated
    focus ring) would override every themed family — `Chrome.axaml`'s focus ring
    is ungated for exactly that reason.
  - **Where the theme comes from a style, never set it locally**: the dialog
    footer's `Border.DialogFooter Button.Primary` sets `FirePrimaryButton`, and a
    local `Theme` would win over it. A button whose family follows a binding
    (`FailedScreenView`'s restart: `FireCta` or `FireOutlineCta`) carries two
    class → `Theme` styles in its own `<Button.Styles>`. In code:
    `button[!StyledElement.ThemeProperty] = new DynamicResourceExtension("FireQuietButton")`
    (`RigFaceplateView`); a test calls `Perf/ButtonThemes.Themed()`.
  - **No Button family is global any more.** `FireInsertDot`, whose hover rules
    are descendant selectors (`Panel.FireConnector:pointerover Button.FireInsertDot`)
    that a ControlTheme may not contain, is SCOPED to the rig instead
    (`Views/PlayMode/AmpFxRigStyles.axaml`, #242 — see "View-scoped styles" below).
    The `ToggleButton` and `TextBlock` families followed (below).
  - Guarded by `UI/ButtonControlThemeTests`: no global `Button.X` style outside
    its shrink-only list; every button wears a theme carrying the rules its
    classes need (markup AND `Classes.Add` in code); copies stay identical.
- **A Border family is a ControlTheme too (#240), the same way.** Each family
  is `<Family>Border` (`FireTagBorder`, `FirePanelBorder`, `SettingsGroupBorder`,
  `DialogCardBorder`), in the file that held its global rules, and a Border
  wears both: `Theme="{StaticResource FireTagBorder}" Classes="FireTag Accent"`
  (`DynamicResource` in a top-level dialog view). In code:
  `border[!StyledElement.ThemeProperty] = new DynamicResourceExtension("FireOverlayScrimBorder")`
  (`Views/Shell/DialogHost`); a test calls `Perf/BorderThemes.Themed()`.
  - No Border wears two families, so nothing is copied: every modifier is stated
    per family (`^.FireTag.Warn`, `^.FireDot.Warn`).
  - **A Border theme is never worn inside a `ControlTemplate`.** There the
    templated parent's theme (`StyleTriggerTemplatedParentTheme`) outranks the
    Border's own, where the old global style outranked both; and a theme that
    reaches a control directly and through its templated parent's theme is
    attached twice (Avalonia PR #22276, open). Border themes carry no template.
  - **Still global:** the descendant rules a theme may not contain — `FireListRow`'s list
    states (`ListBox.FireList > ListBoxItem:selected Border.FireListRow`,
    `ListBox.DenseRows Border.FireListRow`), `FirePopoverDotGlow` and the release
    notes' `md|MarkdownView Border.MdCodeBlock` / `.MdRule`. They come AFTER the
    family rules they could conflict with, so they won before and still win.
  - A view's own grammar still restyles a family on top (`SettingsStyles`'
    `Border.SettingsGroup`, `DialogStyles`' `Border.DialogCard`): an app style
    beats a theme, as it beat the global rule it came after.
  - Guarded by `UI/BorderControlThemeTests`: no global `Border.X` style outside
    its two shrink-only lists; every Border (markup and `Classes.Add`) wears a
    theme carrying its rules; copies stay identical; no Border theme inside a
    template.
- **TextBlock and ToggleButton families are ControlThemes too (#240).** A
  TextBlock family is `<Family>Text` (`FireMonoText`, `FireEyebrowText`,
  `CaptionText`, `GroupTitleText`; `HelpText` and `FireStatusText` keep their
  names), a ToggleButton family `<Family>Toggle` (`ChipToggle`, `SegToggle`,
  `FireGhostToggle`, `FirePopoverChipToggle`), based on Fluent's ToggleButton
  theme. Each lives in the file that held its rules, in their old order, and the
  control wears both: `Theme="{StaticResource FireMonoText}" Classes="FireMono Faint"`
  (`DynamicResource` in a top-level dialog view). In code a test calls
  `Perf/TextAndToggleThemes.Themed()`.
  - **The whole-template toggles are worn directly.** `FireChoiceToggle`,
    `FireSegmentItemToggle`, `FireNavRowToggle` and `FireRailToggle` were reached
    by a global `ToggleButton.X { Theme }` style; a ToggleButton now names the
    theme itself (and keeps `FireChoice` / `FireSegmentItem`, which their
    `^.FireChoice.Card` / `^.FireSegmentItem.Dense` / `^.Tab` rules and the
    descendant `:is(ToggleButton).FireSegmentItem TextBlock` rules need). The
    rail's rig toggle wears `FireRailToggle` with no class. The `RadioButton.X`
    spellings are still global styles — RadioButton is not converted yet.
  - **One copy:** the ToggleButton `^.Compact` lives in `CompactToggle` and, first,
    in `FireGhostToggle` (`Compact FireGhost`). `ChipToggle` carries the one
    `Transitions` setter this layer has, unchanged from its global days.
  - **The panel families are ControlThemes too (#428):** `FirePopoverBodyPanel`,
    `FirePopoverControlsPanel`, `FireSectionHeaderPanel`, `FireKickerPanel`,
    `FireEmptyStatePanel`, `FireRailStackPanel` (StackPanel) and
    `FirePopoverKickerGrid`, `FirePopoverRowGrid`, `FirePopoverFooterGrid` (Grid).
    A global `StackPanel.X` / `Grid.X` rule was attached to every StackPanel /
    Grid of the app (~25,000 frames on the prewarmed shell). Their CHILD rules
    (`Grid.FirePopoverRow > TextBlock`, `Grid.FirePopoverFooter > TextBlock`,
    `StackPanel.FireEmptyState > TextBlock`, `Border.FirePopoverSlot > Grid`,
    `Border.FirePopoverSection > StackPanel`) are LOCAL values on the children
    now — a child of a popover row says `VerticalAlignment="Center"`, a TextBlock
    in an empty state `TextAlignment="Center"`.
  - **Still global:** the tag/badge child rules (`Border.FireTag > TextBlock`,
    `Border.FireTag.Success > TextBlock`, `Border.FireMedal > TextBlock`, …) and
    the descendant-only label families a theme may not express
    (`Button.FireCta TextBlock.FireCtaLabel`,
    `Button.FirePopoverOption.Picked TextBlock.FirePopoverMeta`, the popover row
    names, `md|MarkdownView TextBlock.MdBulletGlyph`). A rule that stays global
    outranks the theme whatever the order.
  - Guarded by `UI/TextBlockToggleButtonControlThemeTests` (one Theory per
    type): no global class style outside its shrink-only lists; every control in
    markup and code wears a theme carrying its rules (and the whole-template
    toggles their named theme, and a theme's `BasedOn` family theme counts as
    carried); copies stay identical; no family theme inside a template.
- **The rig's styles are ControlThemes or VIEW-SCOPED, never global (#242).**
  - Its Border / TextBlock / ToggleButton families are ControlThemes in
    `Fire/FireRig*.axaml` like every other family (`FireUnitCardBorder`,
    `RigRoleBodyText`, `FireModeToggle` — a class ending in `Button` drops it,
    one ending in `Toggle` keeps its name). Two elements wear two rig families
    and so ONE combined theme (`FireDockBarBorder` = `^.FireDockBar` + `^.FireGearBar`,
    `FireUnitCardBorder` + `^.FireDragGhost`, `FireGearStarToggle` + `^.FireGearTileStar`).
    A rig rule on a family worn with a shared one went into THAT theme, last:
    `^.FireTag.Success` and `^.FireGearTileBadge` in `FireTagBorder`,
    `^.FireStatusPill.FireMapChip` in `FireStatusPillBorder`, `^.FireMapZoomText`
    in `FireMonoText`. `FireModeToggle` copies `^.Compact`; `FireMicCardToggle` is
    `BasedOn` `ChipToggle` instead of copying it (its `Transitions` setter stays out
    of the Fire parts). `RigFaceplateView` binds `FireFaceFootswitchToggle` in code.
  - **What a theme cannot hold is scoped to the view that uses it**: descendant and
    child rules (`Border.FireUnitCard.selected Border.FireUnitArtFrame`,
    `StackPanel.FireEnclosureChoice > ComboBox`, the ToneRail list states) and the
    types no theme lint covers (`Path.FireRigGlyph`, `Panel.FireConnector`,
    `Grid.FireJackWell`, `Slider.FireEqFader`, `ScrollViewer.FireBoardScroll`,
    `Button.FireInsertDot`, `fire|RigFan`). `Views/PlayMode/AmpFxRigStyles.axaml` is
    `StyleInclude`d FIRST in `AmpFxRigView`'s `UserControl.Styles`,
    `RigModuleEditorStyles.axaml` in `RigModuleEditorView`'s (it reaches the family
    editors `RigEditorTemplateSelector` builds), and the tone designer's five rules
    sit in `ToneDesignCorePanel`'s own Styles (they reach `ToneDesignPanel`'s
    ExtrasTemplate blocks). Inside the rig they cost what they did; outside it nothing.
  - **Moving a rule from global to a view changes its rank, so the cascade was
    re-derived, not assumed.** A view-scoped style beats every global style AND
    every theme, so a scoped rule that used to LOSE to a later global rule now wins.
    One did: `Border.FireUnitCard TextBlock.FireCardTitle`'s Foreground/FontSize
    (and the `.selected` amber) always lost to the later `TextBlock.FireCardTitle`;
    those dead setters were dropped, so the card title renders as before. A new
    scoped rule must not count on losing.
  - **A class declared only in a view's Styles is a no-op outside it.**
    `UI/FireClassReachabilityTests.AClassDeclaredOnlyInAViewsOwnStyles_IsWornOnlyInsideThatView`
    places every wearer with `Infrastructure/ViewStyleScopes` (the view, the views it
    declares or builds in code, the dictionaries it merges, markup nested inside an
    instance of it); `FireStyleClassTests` accepts such a selector only for a file
    the view reaches. A rig popover or dialog hosted OUTSIDE the rig that wears one
    of these classes needs the rule in a global part or a theme instead.
  - The UI sweep hands a hosted view its host's grammar (`UiSweep.InheritedStyleIncludes`),
    including a view built in code by a type the host declares, so a family editor
    swept alone is still styled.
- **No `Transitions` setter, ever.** Building one calls
  `Dispatcher.VerifyAccess`, which would make the whole dictionary
  UI-thread-only (the trap that took out all nine `SettingsSearchTests` in
  0.35), and every Fire surface so far is `IsVisible`-gated, where style clocks
  keep ticking while hidden. The mockups' `transition: all .16s ease` is
  deliberately not ported — hover is instant, like the rest of this app's shell.
- **A `Fire*` class matching no selector is a SILENT no-op.** Same rule as the
  rest of the theme — except that the Fire vocabulary hangs off Borders,
  TextBlocks, ToggleButtons, Sliders, ComboBoxes and ProgressBars, so
  `ButtonStyleClassTests` (Buttons only) does not see almost any of it.
  `DotCharter.Tests/UI/FireStyleClassTests` is the lint that does, and it also
  requires the selector to live in **`Fire.axaml`** — a Fire mark re-invented
  inline in one view is how nine screens stop being one screen.
- **A `ToggleSwitch` template must declare `PART_MovingKnobs` (a `Panel`)** or
  the build fails with AVLN2205.
- **A SEARCH FIELD IS TWO ELEMENTS AND BOTH ARE MANDATORY: `Border.FireField`
  (the box) around `TextBox.FireFieldInput` (the text).** The input strips its
  padding, border, background, `Height` and `MinHeight` on purpose so the frame
  can supply them — which means a bare one is *text at x = 0 with no box at
  all*: no padding, no hover, no focus, nothing for a selection to fill. v0.44
  shipped four (the rig's lyrics editor / NAM search / TONE3000 client id, plus
  Play Mode's song-library search bar, which hand-copied six of the class's
  setters instead of wearing it) and the owner reported every one of them.
  `UI/FireFieldInputPairingTests` lints the pairing both ways over the real
  markup, and **also forbids restating at a call site anything the class owns** —
  nine views had written `VerticalAlignment="Center"` on the input, which pinned
  it to a single 18 px text line inside a 36 px frame and left the focus ring
  and the selection band covering a strip through the middle of the field.
  The class now carries `VerticalAlignment=Stretch` + `VerticalContentAlignment
  =Center` as a pair; the geometry is measured by
  `Perf/FireFieldUiPerfSmokeTests`.
  - **Hover and focus live on the FRAME** (`Border.FireField:pointerover` /
    `:focus-within`), and the input's own chrome is suppressed in three places,
    because two of them are unreachable from the control's properties: Fluent's
    TextBox `ControlTheme` sets `Background`/`BorderThickness` on
    `PART_BorderElement` from `^:pointerover`/`^:focus` **`/template/`**
    setters, which beat the TemplateBinding that would have carried
    `BorderThickness=0`. Hence the two `TextBox.FireFieldInput:… /template/
    Border#PART_BorderElement` rules plus `FocusAdorner={x:Null}`.
  - **A wrapping editor is the `.Tall`/`.Multiline` pair**
    (`Border.FireField.Tall` = `FirePadFieldTall` 12,9, around
    `TextBox.FireFieldInput.Multiline` = top-anchored). The one-line field's
    `FirePadField` has NO vertical inset — right for text centred in 36 px,
    wrong for anything taller, and the app-wide `VerticalContentAlignment
    =Center` had the rig's lyrics editor starting its first line at y = 27 of 72.
  - **The COMPACT size (#600) is a class, not a local `Height`:** `FireField
    Compact` (and `.Tall.Compact`, 10,5) and `ComboBox.FireCombo.Compact` are
    `FireFieldHeightCompact` (28) tall, for a docked pane where every pixel of
    chrome comes out of its content — the AI chat's head and composer
    (`Perf/AiChatPaneConstructionUiPerfSmokeTests` budgets both). The compact
    combo's NEGATIVE right inset (`FirePadComboCompact`) is deliberate: Fluent's
    template keeps a fixed 32 px chevron column, so its selected text runs on
    into it beside a smaller chevron. A compact wrapping box sizes by
    `MinLines`/`MaxLines` on the input, not a pixel `MinHeight`.
- **Accent is information.** The 3 px bar, ONE primary action per surface, and
  a status colour are its sanctioned uses. A row of equally accent-tinted cards
  says nothing.
- **Under Graphite the CTA has no chamfer, glow or lift** (`FireCutSize` 0,
  `FireGlowAction` transparent, figures in JetBrains Mono, eyebrows in the UI
  face; see [fire/vocabulary.md](fire/vocabulary.md) § "The chamfer").
- The CTA's chamfer is a real clip geometry (see
  [fire/vocabulary.md](fire/vocabulary.md) § "The chamfer"), **not** a
  `CornerRadius`. `FireRadiusCut` no longer exists.

## Breakpoints

**The interface scale cannot change layout SHAPE, and that is arithmetic, not
an opinion.** `MainWindow`'s `RootScaler` `LayoutTransformControl` lays the shell
out at `(W/s) × (H/s)` logical pixels and draws it at `s`, so every ratio
"content px ÷ window px" is INVARIANT under it: a 520 px hero column on a 3840 px
window at 150 % is a 520 px column on a 2560 px logical window — the same
picture, larger. That is the whole finding of §0 of the 2026-09-12 resolution
audit, and it is why coverage ("the screen does not use my monitor") needs a
second, independent mechanism.

**`Services/UiBreakpointController` is that mechanism, and it is the only place
that decides.** `MainWindow` calls `UiBreakpointController.Attach(this)` in its
constructor; nothing else attaches it.

| Shape | Inner width (logical DIPs) | Window class |
|---|---|---|
| `Compact` | `< 1600` | `Compact` |
| default | `1600 … 2560` inclusive | **none** |
| `Wide` | `> 2560` | `Wide` |

**It keys on the INNER, post-transform width** — `ClientSize.Width ÷
UiScaleController.Scale`. Breakpoints decide shape at the logical size the
content sees, which is also the coordinate space every token below is written
in. So **a 3840 window at 150 % is the DEFAULT shape, not wide**: that user asked
for bigger content, not a different layout, and their content genuinely has 2560
logical pixels. The two mechanisms compose this way instead of compounding, and
the controller re-evaluates on `ScaleChanged` as well as `SizeChanged`.

`HysteresisMargin` is 64 DIPs. A shape change re-styles the whole tree and
re-resolves every `DynamicResource` in it, so the switch happens only when the
width has left its band — a 1080p→4K drag costs exactly ONE re-style and a
window parked on 2560 costs none. That change-gate IS the coalescing; nothing is
posted or timed. Pure table + hysteresis: `UI/UiBreakpointTests`. Wiring:
`Perf/UiBreakpointUiPerfSmokeTests`.

### The two ways a surface opts in

1. **A selector**, for layout that must re-flow:
   `Selector="Window.Wide StackPanel#LaunchColumn"`. No code-behind. The default
   shape wears no class on purpose — the unqualified selector IS the design
   look, so nothing is written twice.
2. **A token**, for a constant. `Styles/FireTokens/Ladders.Wide.axaml` is merged
   over `Ladders.axaml` at APPLICATION scope while the shell is wide (a popup is
   its own `PopupRoot` and has to see it; `Theming/ThemeService` already
   establishes that `Application.Resources` beats the theme's `Styles`
   resources). `DynamicResource` re-resolves on the merge, so a consumer needs
   no edit at all — and that is why a Wide-ladder token may NEVER be read with
   `StaticResource` (it would freeze at the base value; `UI/FixedTokenReferenceTests`),
   while every other size token is static (#241).

**Two traps on the SELECTOR route, both found the hard way on 2026-09-13
(audit §4 P0.2, then in `Views/PlayMode/PlayModeView.axaml`, since #294 `Screens/ResultsView.axaml`):**

- **A local value written in markup outranks every style setter**, so a
  breakpoint rule that moves `Grid.ColumnSpan`, `FontSize` or anything else must
  not find that property spelled on the element. Declare the DEFAULT as a style
  too, before the `Wide` one — the last matching style wins. This reads as "the
  selector does not match", which is what makes it expensive: a `Tag` setter in
  the very same style applied while the `Grid.ColumnSpan` beside it did nothing.
- **`Window` vs `:is(Window)`.** Avalonia documents a type selector as matching
  the EXACT type, and the shell's root is `Views.MainWindow : Window`, which
  would make the example above green in every headless test and dead in the
  shipped app. **Measured — it is not:** both forms reach a `Window` subclass
  today (`Perf/UiResolutionAuditUiPerfSmokeTests
  .ABreakpointSelector_ReachesAWindowSubclass`), so the example is correct as
  written. `PlayModeView` still says `:is(Window)` because the failure mode is
  silent and ships, and that case names which form went dead if one ever does.

| Token | default | `.Wide` | why |
|---|---|---|---|
| `FireSidebarWidth` | 262 | 320 | front-door nav rail; 13.6 % of 1920 → 6.8 % of 3840 without it |
| `FireContextRailWidth` | 404 | 520 | session queue, same halving |
| `FireHeroMinWidth` | 520 | 720 | audit §4 P0.1's middle rung; lane A owns the screen |
| `FirePopoverMaxWidth` | 420 | 520 | audit §4 P2.2, verbatim |
| `FireColumnMaxWidth` | 520 | 760 | the centred reading column (the audit's 16 hand-written caps) |
| `FireColumnMaxWidthLg` | 1040 | 1400 | the full-page reading column; consumed by `SettingsView`'s tab cap |
| `FireCardMaxWidth` | 560 | 760 | one centred card: `EditorLaunchCardView` (lane F) |
| `FirePaletteWidth` | 680 | 920 | the command palette card (lane F) |
| `FirePaletteMaxHeight` | 500 | 760 | …and its ceiling. The row height is fixed, so this IS the visible row count: ~10 → ~17, measured (lane F) |
| `FireRigGearPanelWidth` | 320 | 420 | the Amp & FX rig's gear palette — the same rail halving, audit lane E |
| `FireRigFaceMaxScale` | 1.35 | 2.0 | the rig module-editor faces' scale-to-fit ceiling (`Controls/Fire/RigScaleToFit.MaxScale`) |

**`FireRigFaceMaxScale` is the one DIMENSIONLESS entry and it is not an
exception to the rule above.** It decides what share of the rig's dock row one
editor face occupies — a shape decision like a rail width, and invariant under
`RootScaler` for the same reason — and no font, padding or spacing token moves
with it: the face's own type scales WITH the face. Lane E (2026-09-13) put all
nine family faces on `RigScaleToFit` against it. See
[fire/amp-fx-rig-screen.md](fire/amp-fx-rig-screen.md) § "Every family FACE
scales into the dock row" for the measured per-family table, the two bindings
that are load-bearing, and why the 4K/1080p ratio lands at 0.81–1.23 rather than
lane C's flat 1.00 (the dock row grows 5.07× in area between the two and a face
capped at 2.0 can grow 4×).

**What must never go in that file:** `FireSize*`, `FontSize*`, `Space*`,
`FirePad*`, `FireGap*`, `LineHeight*`/`FireLineHeight*`. Those are the
transform's job; ramping them here compounds with `RootScaler` and produces a
size nobody can reproduce from a screenshot. **`FireDialogWidth*` is also
excluded and that is a measurement, not an oversight** — the audit measured all
33 dialogs at `ratio` 1.04 and 98 %/97 % of the window at 4K, because
`Converters/DialogCardClampConverter` already tracks the live scrim, and §4 P2.4
says the 480/600/720/1120 ladder is correct behaviour at 4K.

**There is deliberately no Compact sibling of that dictionary** (do not
spell the filename — `Infrastructure/DocReferenceLintTests` reads a guide's
backticked paths and fails on one that is not in the tree, which is the correct
answer for a file that is meant not to exist). No base constant was measured as
needing a smaller value below 1600, which is already the `FireScreenMinWidth`
(1320) region where the app scrolls rather than reflows. The `Compact` CLASS
ships regardless, for a view that wants to re-flow there.

**`MaxWidth` + `HorizontalAlignment="Center"` caps but does not FILL.** A panel
whose content desires less than the cap is arranged at its desired width, so
raising the cap changes nothing for it. Measured at 3840×2160 when this
mechanism landed: the settings tab cap moved 1040 → 1400 (27.1 % → 36.5 % of the
window) while the widest tab still arranged at 570 px, because every tab is a
`ScrollViewer` over an auto-sized `StackPanel`. Raising a cap is half a fix; the
content has to want the room.

**The other half is `Stretch`, and it does NOT cost the centring.** Avalonia's
`ArrangeCore` gives a `Stretch` child `min(slot, MaxWidth)` and then centres the
clamped result exactly as it centres a `Center` one — so a capped reading column
that must FILL its cap is one word, not a wrapper. Settings took that route on
2026-09-13 (audit P0.4): every tab now arranges at the full 1040 / 1400, and
five of the nine flow their group cards into two columns through a
`Window.Wide` selector on a `Controls/MasonryPanel`. See
[settings.md](settings.md) for which tabs opted out and why.

### Which of the two routes to take — and it is FORCED, not a preference

**If the number is written ON THE ELEMENT, only the TOKEN route can move it.** A
`Width="680"` / `MaxWidth="560"` / `Width="360"` in markup is a LOCAL value and
outranks every style setter, so a `Window.Wide` selector aimed at one is
silently dead — it reads as "the selector does not match", which is the
expensive failure this section already records for `Grid.ColumnSpan`. Lane F
moved five such numbers onto tokens (the last five rows of the table above) and
wrote no selector for any of them; `DynamicResource` re-resolves on the merge,
so the consumer needed no edit at all beyond the key.

**A TYPE rung is the other way round: it may never be a token here.**
`Ladders.Wide.axaml` is layout constants only, because a ramped font token
compounds with `RootScaler`. A rung steps through a `:is(Window).Wide` SELECTOR
reaching an EXISTING `FireSize*` / `FontSize*` step.

Two things bite on the selector route and both cost lane F a red test first:

- **A class and its VARIANT are two rungs, and a rule naming only the base class
  flattens them.** `TextBlock.FireMono` is 10.5 and `TextBlock.FireMono.Faint`
  is 9.5; one `Window.Wide TextBlock.FireMono` rule raised BOTH to the same
  size, deleting a hierarchy that exists at 1080p. State the variant too, after
  the base rule (last matching style wins). The status rail and the title-bar
  cluster each carry the pair.
- **SCOPE a rung rule to the container it is about.** A view that gives some
  rows a LOCAL `FontSize` defeats an unqualified class rule in the wrong
  direction (the removed LAN board's podium came out upside down that way):
  qualify the selector with the container, e.g. `ListBox#Name TextBlock.X`.

### The `LineHeight` rule

**`LineHeight` is the second half of a type step and always goes through a
token** — `LineHeight*` (`Styles/Typography.axaml`) on an app surface,
`FireLineHeight*` (`Styles/FireTokens/Ladders.axaml`) on a Fire one, same
two-ladder discipline as `FontSize*`/`FireSize*`. Until 2026-09-12 all 33
`LineHeight` setters in the theme were literals sitting beside a tokenised
`FontSize`. Under the transform that is invisible, because both scale; ramp the
size tokens without it and every one of them CLIPS — `FontSizeHero` 28 → 42
against a frozen `LineHeight="34"` cuts every descender app-wide.
`UI/LineHeightTokenTests` resolves all 33 through the ladders and pins the
resulting multiset of numbers, so "repointed with the same value" is checked
rather than asserted; `UI/ResourceTokenTypeTests` pins that every step is an
`x:Double`.

## Adding a screen

1. Read the mockup **and at least two others** — a single file always looks
   like a complete system.
2. Compose from the table in [fire/vocabulary.md](fire/vocabulary.md). If a mark
   is genuinely new, add it to
   `Fire.axaml` with a comment saying which mockup it came from, rather than
   inlining it in the view.
3. Check every number against the table in
   [fire/unbacked-data.md](fire/unbacked-data.md), and cut what
   has no backing rather than hard-coding it.
4. Localise every string, in **both** resx files (`LocalizationSweepTests`
   fails the default gate on a `{localization:Loc}` with no key).
5. Add a construction + attach + layout smoke test. A view whose styles failed
   still arranges — it just has no size, and that is the shape every silent
   XAML fault in this repo takes.
6. If the surface is `IsVisible`-gated, expose it through `IPrewarmableSurfaces`
   or it reintroduces the first-switch lag for itself.

## Where the detail lives

| Read this when… | File |
|---|---|
| you need a TOKEN — a brush, a ladder step, a font, a fixed string/gear/stem identity — or you are wondering which `.axaml` declares it and why there are five files | [fire/tokens-and-typography.md](fire/tokens-and-typography.md) |
| you are assembling a screen out of existing marks: the class table, the eyebrow lockup, the 3 px accent bar, the status pill, §5's nine shared components, the chamfered CTA, focus visuals, and how a mockup-vs-mockup contradiction was already resolved | [fire/vocabulary.md](fire/vocabulary.md) |
| you are touching `FireKnob`, `FireLevelMeter`, `FireTunerMeter`, `FireProgress`, `FireChipStrip` or the drag ghost / drop marker — their public API, the geometry types, the repaint gating and the eight identity rules | [fire/custom-controls.md](fire/custom-controls.md) |
| you are editing a Play Mode SCREEN root: the front door, the launch screen or Song Library (Session History, Practice Trends and the Progress hub were removed on 2026-09-19) | [fire/play-mode-screens.md](fire/play-mode-screens.md) |
| you are editing one of the Play Mode MODALS — the gameplay settings overlay (§6.5) or Get Ready (§6.6) | [fire/play-mode-modals.md](fire/play-mode-modals.md) |
| you are editing the Amp & FX rig screen (§6.8): the six regions, the module-editor faces, the board's signal path, card widths, the ancestor-cast trap, §7's corrected knob sets, and the render-vs-mockup capture loop | [fire/amp-fx-rig-screen.md](fire/amp-fx-rig-screen.md) |
| you are editing a surface OUTSIDE Play Mode — a dialog, the settings page, the editor's menu/tool/status rails, the inspector, the docked workspace, or the song browser — i.e. any of the four converted GRAMMAR files | [fire/editor-surfaces.md](fire/editor-surfaces.md) |
| a mockup shows a number and you are about to bind it — check here first whether anything computes it, and what was cut, replaced or built instead | [fire/unbacked-data.md](fire/unbacked-data.md) |
