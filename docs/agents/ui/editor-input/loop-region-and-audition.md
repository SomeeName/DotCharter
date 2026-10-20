# Editor keyboard input — the loop region and the selection audition

Part of the editor keyboard-input contract. Router: [../editor-input.md](../editor-input.md).

## The editor loop region and the selection audition

`DotCharter.Services/Editing/Playback/`. Charting a hard passage means hearing
it repeatedly, and the editor had no loop at all — `LoopStart`/`LoopEnd` existed
only inside `LivePlayViewModel`, behind a live-play session.

| Key | Verb |
|---|---|
| `Alt+[` (`SetLoopStart`) | punch the loop start at the playhead |
| `Alt+]` (`SetLoopEnd`) | punch the end; the loop turns on |
| `F6` (`ToggleLoop`) | loop on/off, KEEPING the region |
| `F7` (`AuditionSelection`) | play the resolved target once |
| — (palette `transport.clearLoop`) | drop the region |

Clear is **palette-only on purpose** — it is the rare half of the loop and a
default chord would cost a fifth row in an already crowded table for no muscle
memory. All five are in the command palette; the toggle is also a toolbar
button.

rscharter's repeater keys verbatim where they were free (`DefaultShortcuts.java
:169-171`). The bare and `Ctrl` rows of those brackets are sustain ±1 step and
zoom; `Alt`+<letter> is the "group" technique family, and `Alt`+<bracket> is not
in it. All four are NEW rows, so **no `NormalizeKeybinds` migration**.

### It reuses Live Play's plumbing rather than copying it

`LivePlayLoop` **is** the region type, and its `TryCreate` (swap the punch
points, refuse anything under `MinLengthMs`) and `ShouldWrap` are now the single
implementation — `LivePlaySessionService.SetLoop` and its wrap check were
rewritten onto them in the same change, so the two loops cannot drift. What is
deliberately NOT shared is the session BRAIN: count-in, passes, the speed ladder
and dynamic difficulty are gameplay, and an editor loop is a plain transport
loop.

- **The wrap fires on `RawPlayheadMs`, never on `PositionChanged`/
  `CurrentTimeMs`.** The loop end is a point of the SONG. On the compensated
  (audible) axis the engine has already RENDERED one output latency of material
  past the loop end before the seek lands, and the user hears it — inaudible on
  a tight ASIO buffer, obvious on the ~50 ms Steam-mirror device the
  compensation exists for. On the raw axis those samples are never written at
  all and output latency never enters the arithmetic, so a wrong latency
  estimate cannot bias the loop point. Same rule, same reason, as
  `TryScheduleClick`. Pinned by
  `AudioPlaybackCoordinatorTests.EditorLoop_WrapsOnTheRawPlayhead_*`.
- **Setting a loop point reads the COMPENSATED playhead** — the other side of
  the same rule. Marking is not scheduling: the user is pointing at the playhead
  they can see and hear, and punching on the raw axis would land the mark one
  output latency past it.
- **One seek per pass, not one per tick.** `EditorLoopDecision` carries a
  `WaitingForSeek` latch that clears the moment the playhead is observed back
  inside the region — at 250 Hz a seek takes several ticks to show up. Same
  guard as Live Play's `_pendingSeekTargetMs`.
- **The decision is PURE** (`EditorLoopDecision.Advance`), because it is the one
  thing that runs on the audio timer thread. `EditorLoopService` reads/writes
  `ProjectState`; the coordinator applies the step (`SeekTo` inline — engine-safe
  off the UI thread and what keeps the loop point tight — and the audition's
  PAUSE posted to the UI thread, because it writes bound state).
- **Punching the end does NOT seek.** An editor loop takes effect on the next
  pass instead of yanking a paused playhead away from what you were looking at.
  (Live Play's does seek, because there the punch-out starts a practice pass.)
- **With the loop ON the transport stays in the region**, including entering it
  from outside — standard loop behaviour, and `F6` is the way out.
- **It is DRAWN.** `BackgroundLayer` tints the span and posts both edges (dimmed
  while the region is marked but disabled), a lone post shows a half-punched
  loop, and `ChartRenderer.OnProjectStateChanged` invalidates on the three
  `ProjectState.Loop*` properties because the punch keys are keyboard-only and
  nothing else would produce a frame. Plus the toolbar toggle. **A loop that
  silently re-seeks the transport must never be invisible modal state** — the
  same rule as the string mask.
- **Not persisted.** Like the mask and the grid divisor it is a working mode; a
  loop restored at launch would trap the transport in a region the user does not
  remember marking.

### Audition

`F7` plays **whatever the target ladder resolves** — selection → hand shape →
pen — so "hear this" needs no separate notion of what "this" is, and the sounds
are promoted to a visible selection while they play. It is the loop with ONE
pass: same region, same wrap test, ending in a seek back to the start plus a
pause, so pressing the key again replays the same span.

The sounding is the **existing MIDI/VSTi chart preview** (`UpdateMidiNotes`,
driven by the coordinator on the raw axis) plus whatever else is on — the song,
the metronome, the claps. Nothing new schedules audio; the audition only drives
the transport across the span, which is what makes it correct by construction
and what makes it work on a chart whose audio is silent or muted.

`AuditionSpan` is pure: start exactly on the earliest onset (no lead-in — what
you hear starts on the thing you selected), end at the latest sound end plus
`TailMs` so the last strum RINGS instead of being cut at its notated end (RS
convention trims a chord's sustain to a strike and lets the hand shape carry the
duration), widened to `LivePlayLoop.MinLengthMs` because every placement
produces `Sustain = Zero`, and clamped to the audio length — an end past the
song can never be REACHED, so the pass would never complete and the audition
would never stop.

