# Editor keyboard input (UI router) — the pointer wheel; right-click; double-click, pan aliases and the cursor; the time ruler

## The pointer WHEEL over the timeline (owner-corrected same day)

**A bare wheel notch runs the EDIT chain — the owner's mapping. Panning by
wheel is the opt-in, on Alt.** `Input/TimelineWheelRouter.Classify` is the one
routing decision and `MainWindow.Timeline_PointerWheelChanged` must go through
it (`UI/Input/TimelineWheelRouterTests` pins both the table and the call site):

| Gesture | Does |
|---|---|
| wheel | the `TimelineWheelAdjuster` chain: selected sustain → hand-shape length → preview fret → preview FHP fret → lyric length; a notch NOTHING claims does **nothing** |
| Ctrl+wheel | zoom (around pointer when Follow is off, playhead when on) |
| Alt+wheel | pan the viewport along time (`TimelineNavigation.PanByWheel`) — never a seek, never an edit |
| Shift+wheel | adjusts, same as bare — Shift is deliberately NOT consulted (precision touchpads report Shift-wheel as a horizontal delta, so a Shift verb would be device-dependent) |

**Increase / Decrease Length (keybind, Edit menu, palette) is the wheel's order over the
SELECTION only** — selected sounds → selected hand shapes → the selected lyric, never
anything hovered (`TimelineWheelAdjuster.ApplyToSelection` / `LengthTargetFor`, 2026-09-25).
All three doors go through `MainWindowViewModel.IncreaseLength`/`DecreaseLength`. Until
then each called `AdjustSelectedNotesSustain` directly, so the key did nothing with a hand
shape or a lyric selected. `UI/Input/LengthVerbTargetTests`.

**A notch with no VERTICAL delta is not a notch** (`TimelineWheelRouter
.NotchesFrom`, 0.50.1). The handler read `e.Delta.Y > 0 ? 1 : -1`, so a delta of
exactly `0` — what a precision touchpad reports for a horizontal two-finger
scroll, and for the Shift+wheel the row above is about — was a notch DOWNWARD:
every sideways flick over the lanes shortened the selected sustain by a grid
division, and Ctrl+sideways zoomed OUT. `NotchesFrom` returns `0` there and the
handler returns before the switch. Pinned by `UI/Input/TimelineWheelRouterTests`
(pure cases plus a source lint that the handler asks it and no longer spells
`e.Delta.Y > 0`).

**History, so nobody "fixes" this again:** the 0.59 audit flipped the bare
notch to pan (the adjust chain always found a target — hover creates a preview
note, selection persists — so wheeling to scroll rewrote the selected sustain).
**The owner reversed that within hours (2026-08-11): wheel-edits is his
charting muscle memory and stays the bare gesture.** Alt is the coherent VIEW
modifier instead — Alt+wheel pans exactly like Alt+left-drag pans. Do not
re-flip without an explicit owner decision.

**…and the unclaimed-notch FALLBACK went the same way (owner, 2026-08-22):**
it used to pan, which made the one gesture edit or scroll depending on where the
pointer happened to sit — a notch that missed the note by one lane scrolled the
chart out from under the charter. The reason is EOF, where the wheel is reserved
for LENGTH (hand shape, note sustain, lyric) and never moves the view. The
decision is `TimelineWheelRouter.UnclaimedBareNotch`, so it is one named place
rather than an `else` in the view, and
`TimelineWheelRouterTests.AnUnclaimedBareNotch_DoesNothing` pins it.

**Generating a hand shape TRIMS the tails it covers.** The tail is how a charter
SETS the bar's length — stretch the last note until the bar comes out right,
generate, then shrink every tail again — and the second half of that loop is dead
work once the shape exists. Exception: a sound whose tail CARRIES the technique
keeps its length, because a bend / slide / vibrato / tremolo / link-next is drawn
ALONG the tail and is unreadable without it. `Core/Utilities/TailTechniques` is
the rule (head techniques — mute, HOPO, harmonic, accent — say as much with no
tail); a CHORD keeps its length when any of its notes needs one. The trim rides
in the same history node as the bar.

**Carrying work from one arrangement to another** is
`Services/Editing/Sync/ArrangementSoundSync` (picked source/target pair,
Arrangements ▸ Apply Edits to Another Arrangement). The unit is the SOUND, never
the measure: a note matches on (position, string, fret) and a chord on
(position, its exact set of string→fret pairs), and anything the target does not
play identically is untouched. A hand shape travels only when EVERY sound under
it is shared — a bar covering one source-only lick would declare a string the
target never sounds. An anchor travels when the target has a shared sound in the
region it governs. Template ids are arrangement-LOCAL, so a copied grip is
resolved into the target by SHAPE and the source's FINGERING is written onto it
(the same grip is played with the same fingers). It OVERWRITES, in one undo
step, and the dialog previews the counts before you press anything.

**Drawing a bar on the hand-shape lane (#481).** A right-press over EMPTY lane
only ARMS `Input/Tools/HandShapeDrawGesture`; the release decides. A DRAG (past
`GestureDragThreshold`) spans the two snapped ends in either direction, clamped
between the neighbouring bars, and its preview bar prints the grip it will carry
(`RenderHandShape.Label`, drawn by `HandShapeLayer` for previews only). A plain
CLICK spans from the cursor to the next FRET CHANGE or CHORD
(`Services/Editing/HandShapes/HandShapeLaneSpan.ClickEnd`): single notes add
strings to the grip until one replays a string at another fret or a chord comes;
a run that opens on a chord keeps identical strums. The bar ends ON that sound and
`ChartSpacingEnforcer` opens the gap — nothing in the gesture picks one. It never
runs into a bar nor across an FHP anchor after its first sound (I20); with no
sound it is the old one-beat bar. Bar and minted grip are ONE `AddHandShape`
(one undo node). Right-click ON a bar still removes it, Ctrl+right-click splits
it. `UI/Input/Tools/HandShapeDrawGestureTests`.

**Suggested bars (#491).** The hand-shape lane draws `HandShapeSuggester`'s
proposals as DASHED bars (View ▸ Show suggested hand shapes, on by default): runs of
single notes, or notes mixed with ONE chord shape, that one grip holds. A left-click
on one accepts it as a PLAIN bar, Alt+left-click as an ARPEGGIO; hovering one prints
how in the status line. Tools ▸ Hand shapes ▸ Accept suggested hand shapes in
selection (`Ctrl+Shift+U`) and …as arpeggios (`Shift+Alt+U`, the Alt row of the same
letter, as Generate arpeggio is of Generate hand shape; #639) accept every dashed bar
holding a selected sound. Each
gesture is ONE undo node through `ArrangementHandShapeGenerator.BuildActions`, the
#478 path (`HandShapeSuggestionAcceptor`). The click hit-tests the SAME list the
timeline draws — `HandShapeSuggestionLatch`, keyed on the history revision — with the
committed bars' own `ChartHitTester.HitTestHandShape`. The song map marks each open
suggestion as a faint band (`MinimapOverviewControl.DrawSuggestions`, its own latch over
the same inputs; `SongMapMarks.CollectSuggestions`).
`Services/Editing/HandShapes/HandShapeSuggestion*Tests`,
`UI/Input/Tools/HandShapeSuggestionClickTests`, `UI/HandShapeSuggestionKeybindTests`,
`UI/Controls/SongMapSuggestionMarksTests`.

**Every new bar is named from its template** — the drawn bar, both selection
generators — or left empty; never the literal `"Chord"` (the `HandShape.Name`
default, which import paths still write). **Generate arpeggio from selection**
(`Shift+Alt+G`, Tools ▸ Hand shapes) is `HandShapeFromSelectionGenerator.Generate`
with `arpeggio: true`: it reuses an arpeggio template the chart already declares
in EITHER spelling, else mints through `ChordTemplateResolver` with `Arpeggio`
set — the flag the chord-template panel's box writes.
`Services/Editing/HandShapeArpeggioGenerationTests`.

**A hand-shape resize also rebuilds the bar's GRIP** — the frets it now covers,
fingering filled in — but only on a template the bar owns ALONE
(`Core/Utilities/HandShapeAutoTemplate`). A shared template belongs to a real
chord and is never rewritten. Both halves ride in ONE mergeable action
(`Input/Actions/ResizeHandShapesAction`) so a whole wheel sweep stays a single
undo step: a template edit pushed separately would undo without the length.

**Releasing Alt must not light the menu bar.** The TopLevel's
`AccessKeyHandler` answers a bare Alt KeyUp with `MainMenu.Open()`, and its
handlers register at TopLevel construction — before any app handler can
pre-empt them. The suppression therefore lives in `Views/Shell/EditorMenu`'s
own `Open()` override: a bar-open with the pointer NOT over the bar is the
access-key path and is ignored (keyboard bar activation is deliberately
sacrificed; the app's keyboard surface is the keybind registry + palette).
Pinned by `UI/MenuFlyoutPrewarmerTests.The_bar_ignores_an_open_*`.

## Right-click on the highway

**A plain right-click is the INSTANT charting verb; Shift+right-click opens the
CONTEXT MENU** (#467). On a chord it toggles the clicked string (removes that
one string's note, or adds one), on a note it removes the note, over an empty
lane it places one. Alt+right-click does the same (0.59–0.63 put the verbs
there and gave the bare click the menu, which blocked charting). Shift means
nothing on the right button, so the menu steals no gesture. The decision and
its application are the pure `Input/HighwayRightClick` (`Resolve` → `Apply`
through `IHighwayEditingService`), pinned against the real service by
`UI/Input/HighwayRightClickTests`; the menu is armed only when `Apply`
declines.

- The model is the pure `Input/TimelineContextMenuBuilder` (items, order,
  actions, and that every LocKey exists in both resx files are pinned by
  `UI/Input/TimelineContextMenuBuilderTests`); `ChartRenderer.ShowContextMenu`
  (`IChartViewport`) only realizes it into a `MenuFlyout` at the pointer — a
  popup is its own HWND, so it renders over the native 3D panel.
- On a note/chord (near-miss tolerant, the same `ChartHitTester` pass every
  click uses): Delete · Duplicate · Open in Inspector · Set fret (0–24
  submenu) · Add hand shape from selection · Add FHP anchor here. On empty
  highway: Add note here · Paste here · Select all in view · Add section/event
  here (the existing Event dialog).
- **Every item reuses an EXISTING command/service verb** — the same undo
  actions the keybinds ride. Duplicate and Paste capture the CLICKED time into
  the closure (`MainWindowViewModel.DuplicateSelectionAt` / `PasteAt` — one
  code path with the keybinds); `LastPointerTimeMs` may move while the menu is
  open. The FHP item places through
  `FretAnchorInteractionTool.AddAnchorAt` — the one anchor-placement verb.
- A target outside the current selection REPLACES it before the menu shows
  (a menu on A acting on B loses work); a target inside it keeps the whole
  selection.
- The menu is armed on the right-PRESS and shown on the right-RELEASE —
  opening a flyout while the button is down lets the release light-dismiss it.

## Double-click, pan aliases and the cursor

- **Double-click on a note/chord opens the Sound inspector**
  (`HighwayInteractionTool`, `e.ClickCount == 2` → `ShowInspectorTab(0)`; the
  first click of the pair already selected the target). Double-click on an
  event chip / beat already opened its dialog and is unchanged.
- **Viewport pan is middle-drag OR Alt+left-drag** (`TimelinePanGesture`,
  consumed by `TimelineInteractionManager`). **Space+drag was REJECTED**:
  Space is the play/pause chord and fires on KeyDown at the window tunnel, so
  holding it to pan would toggle playback. Alt is the editor's
  "manipulate the view/value" modifier (Alt+wheel) and Alt+left collides with
  nothing.
- **The cursor follows the zone** (`Input/TimelineCursorPolicy`, pure over
  `TimelineZones`, pinned by `UI/Input/TimelineCursorPolicyTests` including a
  full-height cursor↔routing agreement sweep): cross over the lanes, arrow
  over the event rows and the ruler, size-we over the two length-editing
  bands, size-all while panning. The permanent `Cursor="Cross"` on
  `TimelineGrid` is gone. **Deliberate omission:** no resize cursor on a
  note's sustain edge — no sustain edge-drag gesture exists (sustain is
  Alt+wheel / the nudge keys), and a size cursor there would advertise one.
  Cursor instances are cached (`TimelineCursorPolicy.Cached`) and only
  written on a zone CHANGE — this runs at pointer-move rate.

## The time ruler seeks; the bands route through TimelineZones

**A left press on the time ruler is a SEEK (drags scrub at the pointer-move
throttle's ~60 Hz), decided in `ToolCoordinator.HandlePointerPressed` BEFORE
any tool is chosen.** Until 0.59 ruler presses fell through the band routing:
the highway tool armed the marquee and the release cleared the selection, and
a click a few pixels above landed in the unlabeled FHP band and silently
placed an amber anchor — the live audit's "clicking the ruler created a
persistent yellow region labeled 1". The vertical band predicates
(hand-shape / FHP / ruler) are the pure `Input/TimelineZones`, shared by
`DetermineTool` so labels, cursor and routing cannot disagree; pinned by
`UI/Input/TimelineZonesTests` (including that the ruler overlaps neither
editing band, and a source lint that the ruler seek is decided before a tool
is chosen). Ruler seeks go through `ExecuteSeek`, the deliberate-navigation
funnel — so they re-lock Follow, exactly like the minimap.

