# Fire — the shared custom-drawn controls (`Controls/Fire/`)

Part of the Fire design system. Router: [../fire-design-system.md](../fire-design-system.md).

## The shared custom-drawn controls (`Controls/Fire/`)

A class skin can restyle a control; it cannot draw a 270° arc, colour a bar by
what its dB MEANS, or follow the cursor with a full-size ghost. These are the
parts the Amp & FX Rig and the data screens are assembled from, and they exist
before any screen that uses them.

| Type | What it is | Public API |
|---|---|---|
| `FireKnob` | 46 px dial (38 compact), 270° value arc from 225°, indicator, mono caption + mono readout. **Fully keyboard-operable** and it names itself (below) | `Value` (two-way) · `Minimum`/`Maximum`/`DefaultValue`/`Step` · `Label` · `ValueText` (null ⇒ formats `Value`) · `IsCompact` · read-only `Fraction`/`IndicatorAngleDegrees` |
| `FireLevelMeter` | a row of 4 px bars 2 px apart, heights ramping 5 → 14 px | `LevelDb` · `MinimumDb`/`MaximumDb` · `SegmentCount` · `BarWidth`/`BarGap` · `MinBarHeight`/`MaxBarHeight` · read-only `Band` |
| `FireTunerMeter` (0.48) | the per-string tuner well: target band at the centre, one lit needle, flat left / sharp right | `Cents` · `HasReading` · `InTuneCents` · `TrackHeight` · read-only `Band` |
| `FireProgress` | continuous (6 px track, violet gradient + glow) **and** segmented (N × 3 px, 5 px apart) | `Value` (0..1) · `Segments` (0 = continuous) · `TrackHeight` · `SegmentGap` · `Tone` |
| `FireChipStrip` (2026-08-14) | the song lists' `L R B V` arrangement-chip cluster, drawn as ONE element — it replaced a per-row `ItemsControl` + `Border.FireTag.Square` per chip, whose children rebuilt on every recycled re-bind. Look-parity with the class-built tag is a pinned contract (geometry constants = the `FireTag Square` tokens); the per-chip tooltips fold into the HOST's one `ToolTip.Tip` + `AutomationProperties.Name` (set both at the call site, from `ArrangementSummary`) | `Initials` (`IReadOnlyList<string>`, reference-stable per row) · `Accent` · `ChipSize`/`Gap` |
| `FireDragSession` + `FireDropMarker` | the full-size drag ghost and the 6 px insertion bar, both in the `AdornerLayer` | `Begin(source, grabPointInSource, payload)` · `MoveGhost(pointerInSource)` · `Target(FireDragLane, pointInLane) → FireDropSlot` · `ClearTarget()` · `Dispose()` |

Each has a pure geometry/mapping half that carries the contract and is pinned
in the DEFAULT gate: `FireKnobGeometry`, `FireLevelScale`,
`FireProgressGeometry`, `FireSlotHitTest`, `FireTunerScale`,
`FireChipStripGeometry` (token parity + wiring: `FireChipStripTests`). The
controls are the Avalonia shell over them.

**An UNLIT meter bar goes through `FireLevelScale.UnlitColor`, never
`FireUnitState.At`.** `At` REPLACES the alpha channel, and running the
10 %-white `FireSurfaceTrack` token through it at `UnlitAlpha = 1.0` painted
every unlit bar SOLID white — an idle meter (level −∞, zero lit segments)
rendered as a wall of bright bars and read as a hot input with no audio
(live-audit finding, 2026-08-11). `UnlitColor` scales the track's OWN
translucency; `FireLevelScaleTests.AnUnlitBar_KeepsTheTracksOwnTranslucency`
pins it.

**They derive from `Controls/Fire/FireDrawnControl`**, the one base that owns
the shared plumbing: the counted `Repaint()` / `RepaintRequests`, the
`Paint` cache (created on first use, dropped on attach and on detach) and, for a
control constructed with `base(followsThemeVariant: true)` — `FireKnob`,
`FireLevelMeter`, `FireTunerMeter` — the `ActualThemeVariantChanged`
subscription, taken on attach, released on detach, and answered with a cache
drop + `Repaint()`. `FireProgress`, `FireChipStrip` and the drawn rig parts
(`RigFan`, `RigWire`, `RigMicGlyph`, `RigMicStage`, `RigMicSideView`,
`RigFilterResponsePlot`) use the parameterless constructor (no theme
subscription). `FireEqContour` and `FireDropMarker` share none of it and stay
plain `Control`s. A new drawn Fire control derives from it rather than growing
a fifth copy; `UI/Controls/FireDrawnControlTests` pins the base.

**They paint through `Controls/Fire/FirePaintCache` (0.49).** Colours by
token (one `FireUnitState.ColorFrom` per token per attach / theme change),
immutable brushes and pens by final colour, cleared on attach and on
`ActualThemeVariantChanged`, released on detach. Before: the tuner meter
resolved every token TWICE per brush and built ~8 brushes per paint on six
live instances, the level meter one `SolidColorBrush` per SEGMENT per paint on
three meters at 20–30 Hz, the knob ~14 pens/brushes/geometries per drag frame.
Measured per paint (`Perf/FireMeterRenderAllocationUiPerfSmokeTests`, headless
drawing, value moving every frame): level 8,840 → 217 B, tuner 6,440 → 227 B,
knob 12,899 → 1,208 B (the knob still builds its LIT arc per frame; the unlit
one is kept per size). `UI/FirePaintCacheTests` lints that no
`ColorFrom(this` / `new SolidColorBrush(` / `new Pen(` is left in the three
files — draw through `Paint`, or the lint fails — and that
`FireDrawnControl.cs` keeps the clears and the detach-side unsubscribe.

**Why `FireTunerMeter` is not a variant of the other two.** `FireLevelMeter` is
a MAGNITUDE meter: it fills from a floor and colours each bar by the dB that bar
represents. `ProgressBar.FireMeter` is a 0..1 RATIO that fills from an edge. A
tuner is neither — the needle is anchored to the CENTRE and shows a SIGNED
deviation either side of it, with a tolerance band drawn on the track, and it has
a third state (`NoReading`) that is not a value at all. Both would have to be
lied to about their meaning to draw this, and the picture would still look
plausible, which is the kind of lie that survives review.

Two rules it carries:

- **The scale is DERIVED from the judge's tolerance, not declared beside it.**
  `FireTunerScale.ScaleToBandRatio` (7, from the mockup's ±1-cent band on a
  ±7.1-cent scale) times the LIVE `GuidedTuningCheck.InTuneCents`. Change the
  tolerance and the band and the whole scale move together, instead of the
  picture claiming a precision the judge does not use.
- **"Not yet plucked" is a state, never `Cents == 0`.** A never-plucked string
  and a perfect one are the two things this screen most needs to keep apart, and
  a single `double` cannot spell the difference — hence `HasReading`.

### Rules these four exist to enforce

- **A unit's identity and its bypass are ONE inherited attached property each,
  on the CARD** (`FireUnitState.Category` / `FireUnitState.IsBypassed`). The
  spec's own list of "behaviour rules the agent keeps missing" opens with
  *"bypassed units drop to 0.55 opacity AND grey their signal-strip chip AND
  kill their LED — all three"*, and four independent visual switches is the
  shape that ships with one forgotten. A card writes one property and every
  knob, meter and bar under it greys together. A knob takes a **category**, not
  a colour, so it cannot be handed the wrong identity; a lone knob in a gear
  preview still sets the attached property on itself (a local value beats an
  inherited one).
- **Every knob gesture has a KEYBOARD equal, and one pure mapping owns them
  all** — `FireKnobGeometry.ValueForKey`: Up/Right ±1 `Step`, Down/Left ∓1,
  PageUp/PageDown ±`PageStepMultiplier` (10) steps, Home/End to the ends, and
  **Delete/Backspace back to `DefaultValue`** — the keyboard's half of the
  double-click reset, which was otherwise the ONLY way back to a parameter's
  default. A continuous parameter (`Step == 0`) gets 1 % of its range per arrow.
  Keys the knob does not own return `null` so `Handled` stays false and Tab
  still leaves the dial; a MODIFIED press is never the knob's. Every one of them
  commits through the same clamp + `KnobMath.Snap` the drag does, so no gesture
  can leave a stepped param off its grid. **A Page press used to be ONE step
  whenever the param declared one**, which is nearly every param in the
  catalogue — PageUp and Up were indistinguishable across the whole rig.
- **The knob names ITSELF, from `Label`** — `AutomationProperties.Name` follows
  the caption (in the caption's own case; the dial uppercases for the look
  only), and `FireKnobAutomationPeer` is a real `AutomationControlType.Slider`
  over `IRangeValueProvider` whose `SetValue` lands on the same `Commit` seam.
  Naming at the CALL SITE would be a second place to forget: there are two
  markup sites, both binding a catalogue caption, so deriving it in the control
  names every knob the rig can build, exactly once. The value-changed automation
  event is raised only when a peer already exists
  (`ControlAutomationPeer.FromElement`) — creating one would build an automation
  tree for a board nobody is inspecting.
- **The knob's drag is VERTICAL and 170 px of travel is the whole range** —
  `FireKnobGeometry.PixelsForFullRange`, a `const` and not a styled property,
  because a knob whose sensitivity a caller can change is a rig where two knobs
  feel different. It is not a rotary gesture and it does not scale with the
  dial.
- **Avalonia has no conic gradient.** The value arc is a stroked `ArcTo` path,
  drawn twice (the unlit remainder, then the lit part), plus a wider fainter
  pass for the glow — a `DrawingContext` has no blur, so everything lit is
  layered.
- **The meter's thresholds are SEMANTIC.** Each bar's colour comes from the dB
  IT represents, never from its index: **green below −6 dB, amber −6…−2, red
  above −2**. The default −30 … +1 dB scale is chosen
  so the −6/−2 rule reproduces the mockup's ten-green/two-amber/two-red footer
  meter exactly — the picture falls out of the rule.
- **`FireProgress` EXTENDS `ProgressBar.FireMeter`, it does not replace it.**
  That class skin stays the right answer for the list-row ratios already bound
  to it (it keeps Fluent's PART contract, indeterminate mode and the
  accessibility peer). `FireProgress` is for the two things a class skin cannot
  do: a gradient + glow fill (both need the indicator's own Border, i.e. a
  template override) and the segmented variant, which is not a `ProgressBar`
  at all. Row ratio ⇒ `ProgressBar.FireMeter`; lit, emphasised or stepped ⇒
  `FireProgress`.
- **A drop target is a LANE AND A SLOT** (`FireDropSlot(LaneId, Index)`), never
  "something is being dragged". The rig board forks into parallel lanes and the
  gear panel is a lane of its own, so an index with no lane cannot be acted on.
  `Index` is an INSERTION index in `[0, count]`, decided by item CENTRES — an
  edge test leaves the gaps ambiguous and makes the marker flicker.
- **Both adorners are positioned inside a `Canvas` adorned to a real element** —
  the ghost's to the dragged item, the marker's to the lane — so every
  coordinate is in THAT element's own space and survives the board reflowing
  mid-drag. Nothing is expressed in window coordinates, and there is no
  `DragDrop` and no OS drag image.
- **All four repaint only when the RENDERED state changed.** A knob is dragged
  at pointer rate and a meter is fed from audio: the meter gates on the lit BAR
  COUNT, the segmented bar on the FILLED COUNT, the drag on the SLOT. Every
  invalidation goes through one counted path (`FireDrawnControl.Repaint` →
  `RepaintRequests`, and `SlotUpdates` for the drag), which is what makes the gate testable.
- **No literal hex in any of them.** The eight identities live in
  `FireControls.axaml` and the control has no per-category fallback on purpose —
  a fallback table would BE the second source this layer exists to avoid, so a
  missing token renders visibly wrong (grey) instead of plausibly right.
  `UI/Controls/FireUnitIdentityTests` pins all eight values against the spec,
  fails if a second dictionary redeclares one, and lints the control sources for
  literals.

Guards: `UI/Controls/Fire{KnobGeometry,LevelScale,ProgressGeometry,SlotHitTest}Tests`
and `UI/Controls/FireControlStateTests` (default gate, platform-free — none of
these controls builds a `Cursor` or a `Geometry` before it attaches), plus
`Perf/FireControlsUiPerfSmokeTests` (real session: construction + attach +
layout for all four, every identity colour resolved out of the real dictionary,
a REAL 170 px pointer drag taking a knob min → max, **a REAL key-by-key pass
over a focused knob — arrows, page, Home/End, the reset key, Tab really leaving
it — plus the automation peer's own read/write**, and the ghost/marker really
entering and leaving the `AdornerLayer`).

