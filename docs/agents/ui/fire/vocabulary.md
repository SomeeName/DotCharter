# Fire — the vocabulary, the signature marks, and where the mockups disagree

Part of the Fire design system. Router: [../fire-design-system.md](../fire-design-system.md).

## The vocabulary

Everything below is a class in `Fire.axaml`. Composition, not invention: a new
screen should be assemblable from these, and anything genuinely new belongs
here rather than inline in the view.

A `Border.X`, `Button.X`, `TextBlock.X` or `ToggleButton.X` family is a
`ControlTheme` since #240, keyed `<Family>Border` / `<Family>Button` /
`<Family>Text` / `<Family>Toggle`: wear the theme AND the class
(`Theme="{StaticResource FireTagBorder}" Classes="FireTag Accent"`,
`Theme="{StaticResource FireMonoText}" Classes="FireMono Faint"`). The
whole-template toggles are worn by theme: `FireNavRowToggle`, `FireChoiceToggle`
(+`FireChoice`), `FireSegmentItemToggle` (+`FireSegmentItem`), `FireRailToggle`. See
[../fire-design-system.md](../fire-design-system.md) § "A button is a ControlTheme".
The track/step/stack meters, `FireChip`, `FireCheck`, `FireTopBar`, `FireFeedDot`,
`FireSample` and the read-only `Border.FireSegmentItem` had no wearer left and were
deleted with that move.

| Area | Classes |
|---|---|
| ground | `Border.FireGround` (+`.Screen`), `Border.FireWash` (+`.UpperLeft` / `.StageKey` / `.StageRim`), `Border.FireBackdropClip` |
| screen | `Border.FireScreen` — floating modal, radius 18, one very deep soft shadow |
| regions | `Border.FireRail` (+`.Wide`), `Border.FireContextRail`, `Border.FireHeader`, `Border.FireFooter`, `Border.FireRule` (+`.Vertical`); and the chart editor's three chrome rails `Border.FireMenuBar` / `Border.FireToolbar` / `Border.FireStatusBar` |
| menus | `MenuItem.FireMenuTop` for a top-level header; a dropdown's own chrome is NOT a class — see `Styles/FireMenuChrome.axaml` |
| surfaces | `Border.FirePanel` (+`.Warn` / `.Danger`), `Border.FireCard` (+`.Warn` / `.Accent`), `Border.FireHeroPanel` |
| lockup | `TextBlock.FireEyebrow` (+`.Accent` / `.OnHero` / `.Warn`) over `FireScreenTitle` (+`.Full`) / `FireTitle` / `FirePanelTitle` / `FireWordmark` (+`.Small`), with `FireSubtitle` (+`.Lead`) |
| rows | a ToggleButton wearing `FireNavRowToggle` (`RadioButton.FireNavRow` for a pick-one rail) / a Button wearing `FireNavRowButton`; `TextBlock.FireRowTitle` + `FireRowDesc` (+`.Wide`); `Border.FireListRow` (+`.selected` / `.Record` / `.Unavailable` / `.Alert` / `.Done`) — read-only — and the clickable `Button.FireListRow` (+`.Record` / `.Done`) when the row GOES somewhere; `ListBox.FireList` |
| state | `Border.FireStatusPill` (+`.Success`/`.Warn`/`.Danger`/`.Accent`/`.Bare`) = `Border.FireDot` (+ variants, `.Small`) + `TextBlock.FireStatusText` (+ variants) |
| figures | `TextBlock.FireFigure` (+`.Hero`/`.Inline`/`.Success`/`.Warn`/`.Danger`) over `TextBlock.FireStatLabel`; `TextBlock.FireMono` (+`.Value`/`.Accent`/`.Success`/`.Warn`/`.Faint`) |
| marks | `Border.FireTag` (+`.Accent`/`.Warn`/`.Danger`/`.Square`/`.Now`), `Border.FireBadge`, `Border.FireGradeBadge` (+`.Small`/`.Row`), `Border.FireMedal` (+`.Gold`/`.Bronze`), `Border.FireAvatar`, `Border.FireMark` (+`.Hero`) |
| meters | `ProgressBar.FireMeter` (+`.Thin`/`.Success`/`.Warn`/`.Danger`); `Border.FireColumn` (+`.Mini`/`.Success`/`.Danger`) |
| inputs | `Border.FireField` (+`.Tall`) around `TextBox.FireFieldInput` (+`.Multiline`) — NEVER either half alone; `Border.FireSegment` around the clickable `ToggleButton.FireSegmentItem` / `RadioButton.FireSegmentItem` (+`.Tab`); `ToggleButton.FireChoice` (+`.Card`); `ComboBox.FireCombo` (+`.Bare`); `Slider.FireSlider`; `ToggleSwitch Theme="{DynamicResource FireToggleSwitch}"` |
| actions | `Button.FireCta` > `Button.FirePrimary` > `Button.FireGhost` (+`.Compact` / `.Accent`) > `Button.FireQuiet` (+`.Link`); `Button.FireIconAction` (+`.Close`); `Button.FireBack` |

### The signature marks

- **The eyebrow-over-title lockup.** Mono, uppercase, 9.5 px, `.24em`, dim,
  over a Chakra title. It opens every screen, every panel and every section in
  every mockup — it is the single most repeated element in the set.
- **The 3 px accent bar.** A selected nav row or list row gets a lit accent bar
  on its left edge plus an accent tint and an accent border. This is the one
  selection idiom; do not invent a second.
- **The status pill.** A glowing dot beside mono caps text, both in the state
  colour, on a matching faint tint. `.Bare` drops the chrome for an inline
  status readout in a top bar.
- **The big-figure lockup.** Chakra figure with `line-height:1` over a mono
  caps caption. Where a header says what a screen IS, this says what it
  MEASURES.

### The shared components (§5 of the spec)

Nine marks a screen is assembled FROM. Each one's contract, and the way it is
most often lost:

| Component | Classes | The contract |
|---|---|---|
| **Section header** | `StackPanel.FireSectionHeader` > `StackPanel.FireKicker` (themes `FireSectionHeaderPanel` / `FireKickerPanel`) (`Border.FireKickerRule` + `TextBlock.FireEyebrow`) over a Chakra title | It opens every screen, panel and section. The 22 × 1 px violet rule is optional; the kicker row is its own panel so dropping the rule does not move the title. |
| **Nav row** | a ToggleButton wearing `FireNavRowToggle` / a Button wearing `FireNavRowButton` (+ `TextBlock.FireRowCount`) | **The 3 px indicator is there AT REST**, violet-dim. Hover tints violet and slides `translateX(3px)`. Title 13.5/600 over an 11 px tertiary sub, with an optional right-aligned mono count. |
| **Primary button** | `Button.FirePrimary` > `Button.FireCta` | **CLIPPED corners — a 45° cut off the top-right and bottom-left.** See below. |
| **Secondary / ghost** | `Button.FireGhost` (+`.Compact`/`.Accent`) > `Button.FireQuiet` (+`.Link`) > `Button.FireBack`, and `ToggleButton.FireGhost` (+`.Compact`) | Radius 11, `white .04` fill, hairline border, 13.5/600 `#d9d2ee`. Hover tints violet. **The toggle spelling exists because a standalone chip is NOT a `FireSegmentItem`** — that one only draws chrome inside a `FireSegment` strip, so on its own it renders as bare grey text, which is exactly how the library's FILTERS pill shipped. Checked wears the accent as an outline + tint, never a fill. |
| **Segmented control** | `Border.FireSegment` > `ToggleButton.FireSegmentItem` / `RadioButton.FireSegmentItem` (+`.Tab`) | **BUILT ONCE** — one `ControlTheme` (`FireSegmentItemToggle`) for both spellings. Items radius 6, active = violet **.28** fill + white text, inactive tertiary, mono 10.5. |
| **Status chip** | `Border.FireStatusPill` (+state) = `Border.FireDot` + `TextBlock.FireStatusText` | 30 px floor so a strip of chips is not ragged; 6 px dot with a glow; tint 8–14 % of the signal colour. |
| **Data list row** | `Border.FireListRow` / `Button.FireListRow` / `ListBox.FireList`, with `TextBlock.FireValueCell` + `TextBlock.FireColumnHead` | **Right-hand value columns are FIXED width** (`FireColValue*` = 80/100/110) so the numbers line up down the list. Selected = 3 px left accent bar + a horizontal gradient tint. |
| **Overlay** | `Border.FireOverlayScrim` > `Border.FireOverlayCard` + `Button.FireOverlayClose` | **The scrim is OPAQUE** and **there is always an explicit close control.** See below. The close half is now LINTED, and generalised past Fire: `UI/ModalEscapeHatchTests` requires a focusable dismiss (a Close/Cancel command, `Button.FireOverlayClose`, or `IsCancel="True"`) inside every `*Scrim` surface in `Views/**` — the ~39 `DialogScrim` dialogs included. A `PointerPressed="OnScrimPressed"` does not count: it is a pointer gesture. |
| **Empty state** | `StackPanel.FireEmptyState`, theme `FireEmptyStatePanel`, its TextBlocks `TextAlignment="Center"` (`TextBlock.FireEmptyKicker` + `FireEmptyBody` + an action), or `Border.FireEmptyBanner` (+`.Accent`/`.Danger`) | Never a lone icon in a big box: kicker, ONE concrete sentence, and the action that fixes it. When the empty case is a CONFIGURATION problem it is the inline banner, not the whole screen. |

The four custom-DRAWN components (Knob, Meter, Progress, Drag ghost + drop
marker) live in `Controls/`, not here.

### The chamfer

> **Graphite (2026-09-22) turns it OFF.** The one theme sets `FireCutSize` to
> 0, and the lit action is an 8 px rounded rectangle with no glow and no hover
> lift ([graphite.md](graphite.md) § Shape). Until then the converter read a
> 0 cut as "unset" and drew 16 px anyway; 0 now means square, and
> `UI/FireTokenValueTests.TheLitAction_IsFlatSquareAndUnlit` plus
> `Perf/FireComponentUiPerfSmokeTests.TheLitActions_AreGraphiteFlat_RoundedAndUnlit`
> pin it. The machinery below stays, so a theme can bring the cut back.

The primary action's silhouette is a **45° cut off the top-right and bottom-left
corners** — the spec calls it a brand signature and says "keep it". The first
port approximated it by ROUNDING those two corners (`FireRadiusCut = 0,16,0,16`),
which reads as a lozenge. It is now a real `Clip`, from
`Converters/FireChamferClipConverter` bound to the button's live `Bounds`.

Three things follow, and all three are load-bearing:

- **It cannot be a static geometry.** A `StreamGeometry` resource is fixed at
  parse time, and a `Path` with `Stretch="Fill"` would SCALE the cut with the
  button — 16 px would become 40 px on a wide CTA and the corners would stop
  being 45°. Binding to the bounds is what keeps the notch a constant size.
- **`Clip` cuts the element's own rendering, including its box shadow.** So the
  glow lives on an UNCLIPPED ancestor Border whose own fill is `Transparent` (a
  fill there would show through the two notches). Move the `BoxShadow` onto the
  clipped `Cut` border and the button silently loses its glow.
- **`Clip` is in the element's OWN space; `Bounds` is relative to its parent.**
  The converter reads only width and height for exactly that reason.
- The maths is `ChamferPoints`, deliberately separate from the geometry:
  constructing a `StreamGeometry` resolves `IPlatformRenderInterface` and throws
  without a platform, so only the points can be asserted in the default gate
  (`UI/Converters/FireChamferClipConverterTests`). The geometry actually
  reaching `Clip` is asserted in `Perf/FireComponentUiPerfSmokeTests`.

### Focus is required, everywhere

The menu is keyboard AND controller navigable, so a control you can reach with
Tab and cannot see is a defect. Two shapes, both from the spec:

- **Button-shaped:** a 2 px `FireAccentPale` ring, offset 3–4 px OUTSIDE the
  control, via `FocusAdornerTemplate` (`FireFocusRing*`). Never inside — it must
  not eat into the layout.
- **List rows:** a violet tint plus a violet border (`FireFocusRow*`). A ring
  around a full-width row reads as a second selection.

`Perf/FireComponentUiPerfSmokeTests.EveryInteractiveControl_DeclaresItsOwnFocusVisual`
is the guard; a control with no `FocusAdorner` falls back to the app-wide accent
ring, which is the wrong colour and the wrong offset on a Fire surface.

### Where the mockups disagree with each other

Resolved once here so nine screens do not each re-decide:

- **Two panel families, not one.** The *dialog surface* is the opaque gradient
  (`FireScreen`: radius 18, border `.10`) and appears twice in eleven mockups.
  The *screen panel* is flat translucent white (`FirePanel`: radius 16, border
  `.08`, fill `.025`) and is the dominant surface everywhere else.
- **Ground is `#07060c`** (`FireGroundScreen`); `#06050b` (`FireGround`) is a
  one-off behind a centred modal.
- **The radial wash moves per screen.** Origin drifts toward whichever side
  carries the hero (14% / 20% / 50% / 80%), alpha `.22`–`.30`. Four presets
  cover every case; pick the one that pulls light toward the hero.
- **Border alpha `.10` is the CONTROL border, not the panel border.** Panels
  use `.08`, tab wrappers `.09`, dialog chrome `.07`, list dividers `.04`–`.06`.
- **Radius 11 and 8 are used in every mockup** — more often than 10. The full
  ladder is in `FireTokens.axaml`.
- Amber `#f0a54a` and the fourth-level text `#4f4966` appear in every mockup
  and were missing from the first reading of `Settings.dc.html`.
- Three near-identical hero gradients (150°/160°, `.40`/`.42`, three dark
  stops) are collapsed into one `FireHeroFill`. Four different meter-track
  alphas are collapsed into one `FireSurfaceTrack`.

