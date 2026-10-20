# Editor keyboard input — the keyboard pen

Part of the editor keyboard-input contract. Router: [../editor-input.md](../editor-input.md).

## The keyboard pen

`AppKeybindRegistrar` used to register ~100 editing keybinds and **not one of
them created a note** — DotCharter was mouse-first placement with a keyboard
*modification* layer, while EOF and rscharter are keyboard-first note *entry*
tools. The pen is that missing layer, behind
**`AppConfig.UseFastChartingInput` (OFF by default)**: the two input paths do
not conflict (one is keyboard, one is pointer), so they run side by side until
the toggle is removed at stage 5.

`Editing/Pen/` holds three types and the split between them is the point:

| Type | What it is |
|---|---|
| `FretDigitAccumulator` | the multi-digit fret typist, **pure**. Lifted out of `SheetMusicView.OnKeyDown`, which had it inline and untestable. ONE implementation, two windows — `PenWindowMs` 2000 (rscharter's) and `SheetWindowMs` 800 (what the sheet shipped with; the lift had to be a strict no-op there). |
| `ChartPen` | the **pure state machine** — no clock, no chart, no services; every input is a parameter, the same reason `GlobalChordGate`/`ActivationKeyUpLatch` were split out of `MainWindow`. Decides; never edits. |
| `ChartPenService` | the driver. The only thing that touches the chart, and it does so through the EXISTING verbs. |

### States and transitions

Two states. **Idle** — no typing run open. **Typing** — a digit was pressed and
committed; a further digit inside the window extends it.

| Key | Idle | Typing |
|---|---|---|
| `1..9` (`PenToggleStringBase`, **bare**) | toggle that string at the pen | ends the run, then toggles |
| digit (`SetFretBase`, `Alt`+digit) | opens a run, commits the fret | extends the value (`1`,`2` ⇒ 12) and **replaces** the run's node |
| `Esc` | `PenVerb.None` — falls through to the shell's Escape chain (clear selection) | reverts the run |
| a seek / another edit / a different target | — | ends the run |

`ToggleString` is `ApplyNotePlacementCommand` — the same toggle tree, collision
service and template repointing the pointer's right-click uses: create a note /
add the string to the chord there / remove it / collapse-or-delete when the last
string goes (EOF's XOR at the seek position, `editor.c:3988-4008`). Then two
side effects, both deliberate and both VISIBLE: the placed sound is **selected**
(EOF's implied selection made drawable), and the mask becomes `Only(thatString)`
so the next typed fret lands where you just were — **but only when the string is
now PLAYED**; pointing the mask at a string the same keystroke removed would aim
every per-string verb at nothing.

`TypeFretDigit` resolves its target rscharter-style
(`GuitarModeHandler:337-355`): **the selection wins, otherwise the sound at the
pen**, and the implied target is promoted to a real selection so the user can see
what the digits are rewriting. With a mask it is `SelectedNoteMutationCommands
.SetFret`; **with NO mask it is `TransposeShapeTo`** — the typed number becomes
the shape's lowest fret and the whole chord moves (`GuitarSoundsHandler:379-400`),
the idiom that replaces most `FretUp`×n on a barre chord. A transpose that would
push the shape's highest fret off the neck is **refused, not clamped** (clamping
would squash the shape into a different chord); so is a move of zero. A note is
its own lowest fret, so on a single note both branches set the value.

Typing a fret also writes `ProjectState.PreviewNoteFret`, which is what finally
gives the keyboard a route to frets 10–24 — that ambient placement fret was
mouse-wheel-only, so the next note the pen creates now inherits what you typed.
That write happens **even when there was nothing to fret**, which closes the
other half of the loop: type the fret with nothing under the pen, then press a
string key and the note arrives at that fret. (rscharter cannot do this — it
places at fret 0 and you fix it afterwards.)

### Undo granularity — decided deliberately

- **One string toggle = one node.** A discrete structural act (create / add /
  remove / delete) that a charter expects to undo on its own; EOF records one
  undo state per lane keypress for the same reason.
- **One typing RUN = one node.** A continuation digit reports
  `PenDecision.ContinuesTypingRun` and the driver **undoes the run's own node**
  before committing the new value, so `Ctrl+Z` after typing "12" lands on the
  fret you had BEFORE the run, never on an intermediate "fret 1". The abandoned
  value survives as a sibling branch in the undo TREE — exactly what that tree is
  for.
- **ONE window governs both.** The same 2 s that decides whether a digit extends
  the value decides whether it replaces the node. Type `1`, pause, type `2` and
  you made two decisions: both are on the undo path.
- The run is anchored to a **history node id** and to the **target's identity**,
  so a run something else has already built on top of (an undo, a pointer edit, a
  paste) can never be reopened and can never undo somebody else's work, and
  seeking between two digits cannot land the extended value on the wrong sound.

### Why bare digits toggle strings and `Alt`+digits type frets

The audit's §6.3 asks for both "`1..9` toggle strings" and "digits type frets",
which collide. Both reference tools split them with a modifier — EOF puts lanes
on bare `1..7` and frets on `CTRL+n`; rscharter puts frets on bare digits and
lanes behind its `Insert` prefix modifier, which `KeyChord` (a `Key` + a
`KeyModifiers`) cannot spell. And **every other digit row is taken**: `Shift` =
bookmarks, `Alt` = set fret, `Ctrl` = the string mask, `Shift+Alt` = fingering,
`Ctrl+Alt` is AltGr. So bare is the only free family, note entry is the verb that
did not exist, and the typed-fret half rides the `SetFretBase` row it subsumes —
no new family, no changed default, **no `NormalizeKeybinds` migration**. With the
pen OFF, `Alt`+digit is byte-for-byte the pre-0.35 single-digit "set the
selection's fret"; the branch lives inside `ChartPenService` so there is one call
site.

The pen keybinds are registered **unconditionally** (so the rows are rebindable
and collision-checked by `KeybindDefaultsTests`) and are **inert** until the
setting is on.

**Nine digit keys is not nine strings.** `ApplyNotePlacementCommand` never
consults the arrangement's string count, so the registration guards instead:
bare `1..9` refuses a string the ACTIVE arrangement has not got
(`Input/StringDigitTargeting`, over `Tuning.Length`). Without it a bare `6` on a
4-string bass created a note on string index 5 — a lane the renderer does not
draw, the exporters cannot express and the pointer cannot reach. Same guard, same
reason, on the `Ctrl+1..9` mask row; see the mask section of
[../editor-input.md](../editor-input.md).

### Esc

**All Esc handling lives in `MainWindowViewModel.HandleEscapeKey`** — the window
tunnel claims the key, so the rebindable `ClearSelection` registration never
runs. The pen is therefore a branch of that chain, sitting directly ahead of
"clear the chart selection": behind it, Esc would drop the target you were typing
onto and never reach the revert. `RevertPendingEntry` claims the key ONLY when it
actually reverted, so with nothing pending Esc behaves exactly as it always has.
Pinned by the source lint `PenEscapeReachabilityTests`.

**"In progress" is bounded by the accumulator WINDOW, and it was not until
0.50.1.** The state table above says a seek / a selection change / a foreign
edit ends the run through `EndTypingRun` — **nothing outside `ChartPen` and
`ChartPenService.TypeFretDigit` ever calls it**, and a seek and a selection
change push no history node, so the run's `_runNodeId` stayed CURRENT
indefinitely. Type a fret, move the playhead, press Esc — the pen claimed the
key and undid the fret instead of clearing the selection, which is what Esc is
for the rest of the time. `PressEscape` takes `nowTicks` and asks the same
`WithinWindow` a continuation digit asks, so the "ONE window governs both" rule
is now true of the revert as well. `ChartPenTests.EscapePastTheWindow…` /
`.EscapeOnTheWindowsLastTick…`.

### Seek to a grid line

`SeekPrevGridLine` / `SeekNextGridLine` (`Alt+←` / `Alt+→`) over the pure
`GridNavigation.Step`. The editor had no such verb — the arrow keys move a fixed
number of MILLISECONDS, which on a 6/8 bar or after a tempo change never lines up
with the grid the notes snap to. Built on `ITempoService`'s plain nearest-grid
snap, **not** `ChartEditorViewModel.SnapToGrid`: magnet-snapping onto sounds is
`PgUp`/`PgDn`'s job. EOF puts this on `Up`/`Down` and rscharter on
`Ctrl+Alt+←/→`; both are unavailable here.

### The pen snaps like the pointer

`ChartPenService.PenPosition` goes through the shared `SoundMagnetSnap` — the
loop lifted out of `ChartEditorViewModel.SnapToGrid`. Without it the two disagree
in exactly the case that matters: after `PgDn` the playhead sits ON an off-grid
imported sound, and a plain grid snap would start a SECOND sound a few ms away
instead of adding a string to the one that is there. EOF tightens its hover
tolerance in the keyboard input mode for the same reason (`editor.c:4565-4569`).

