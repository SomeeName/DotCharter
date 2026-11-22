# Tonelab bridge (in-game Rocksmith tone preview)

The Tone Designer can play the tone being edited through Rocksmith itself instead of
through DotCharter's own clone of it. The game side is `tonelab.dll`, injected into
`Rocksmith2014.exe`; it is a separate project whose PROTOCOL document (protocol v1, in the tonelab repository) is
the authoritative contract. This page is the host side: what DotCharter does, where each
part lives, and the audio path that is designed here and built in a later phase.

**Phase 1 (built):** the command pipe, the session (attach + reconnect), the
preview-target switch in the Tone Designer, validation, debounce, temp files, a loopback
mock for tests. **Phase 2 (designed below, built separately):** the shared-memory audio
exchange in the native engine. **The launcher** (start or attach, inject through the 32-bit helper,
RS_ASIO.ini, quit) is public too: see Lifecycle below.

## The layer split (decided)

| Layer | Owns | Knows it is Rocksmith? |
|---|---|---|
| JuceSharp (native) | a GENERIC *external process insert*: a send/return node that hands the live input to another process through shared memory and takes it back. Non-blocking ring I/O, conversion to and from 48 kHz, dry fallback on underrun, under/overrun counters, `dca_*` exports to open/close it and read its stats and latency (an ABI bump, done in the main checkout). | No. No tonelab or Rocksmith name, no game knowledge. |
| `DotCharter.Tone` | the pipe client, the session state, debounce, the validation hook, temp files and the preview-target seam (`Audio/Tonelab/`, `Audio/Dsp/Tones/ITonePreviewTarget.cs`). The managed face of the native insert is the generic `Audio/Abstractions/IExternalProcessInsert.cs`; the tonelab session USES it. | Yes. This is where "the other process is tonelab" lives. |
| Tone Designer UI (`DotCharter.Presentation` + `DotCharter.UI`) | the "Preview in: DotCharter rig / Rocksmith" switch, the reason it is disabled, Reconnect, the error and latency line, the ASIO note. | Through the session only. |
| `DotCharter.Tone` launcher (`TonelabGameLauncher`, only when the install carries `tools/tonelab/`) | the tone-server folder (mirror), starting `Rocksmith2014.exe` from it (or attaching), injecting `tonelab.dll` through the 32-bit helper, ending a game it started; RS_ASIO.ini only in the live-folder fallback. | Yes. No game keys, no decryption: the game loads its own gear banks. |
| tonelab (C++, Win32, its own private repository, NOT in this one) | `tonelab.dll` and `tonelab-launch.exe`, built by tonelab and pinned here by SHA-256 (`tools/release/tonelab/tonelab.json`); `New-Release.ps1` copies them into `tools/tonelab/`. | Yes: it is the game side. |

## Protocol summary (v1)

- Command pipe `\\.\pipe\tonelab-<pid>`, UTF-8, one command per line, one reply line per
  command: `ok …` or `err <reason> …`. The game replies after the command ran on its
  game thread (its own timeout is 10 s → `err timeout …`).
- `status` → `ok ready v1 <rate> <block> <offset>` (e.g. `ok ready v1 48000 128 0`) /
  `ok starting v1`. Parsed STRICTLY (`TonelabProtocol.ParseStatus`); a version other than
  v1 is refused by name (`UnsupportedVersion`, the designer prints both versions) and
  nothing else is sent. `<offset>` is the measured in/out block offset, -1 until
  measured (`TonelabStatus.BlockOffset`). The bare `ok starting` of tonelab M2 still reads
  as starting.
- `tone apply-file <path>` → `ok applied <toneKey>` / `err unknown-gear <key>` / `err …`;
  `tone knob <gearSlot> <knobKey> <value>` → `ok` (live, no chain rebuild); `tone restore`
  → `ok restored` / `err nothing-to-restore` (back to the tone active before the first
  apply; the host only asks after an apply the game accepted); `tone dump <path>` →
  `ok dumped <n> live gears` / `err no-active-tone` (the game writes its ACTIVE tone there
  as JSON with the live knob values — "read the song's tone from the game",
  `ITonelabSession.DumpToneAsync`); `mix mute on|off`, `window hide|show`, `quit` → `ok`.
  Other errors the game sends (`err parse`, `err no-gearlist`, `err apply-failed`,
  `err no-instance`, `err unknown-gear-slot <slot>`, `err unknown-knob <key>`,
  `err nothing-to-restore`) arrive as `Rejected` with the code, printed verbatim.
- Tones travel as FILE PATHS (a manifest tone body: GearList, KnobValues, Volume as a
  negative dB string), written by the host.
- Audio: shared memory `Local\tonelab-<pid>`, little-endian float32, 48 kHz: a 64-byte
  header (magic `TONELAB\0`, version 1, rate, block frames, ring blocks (32, DLL-defined),
  in/out channel counts, the indices `inWrite`/`inRead`/`outWrite`/`outRead`, then
  `gameUnderruns`, `hostUnderruns`, `overruns`, `outOverruns`), then a mono input ring and
  a stereo output ring, each 64-byte aligned. `blockFrames` is 0 until the game rendered
  its first block. NO lockstep from the host: the game renders one block per unread input
  block, as soon as it is there.

## Phase 1: what is built

| Piece | File |
|---|---|
| Wire format, typed results (`TonelabOutcome`: Ok, Rejected, UnknownGear, Timeout, NotConnected, Malformed, Invalid, UnsupportedVersion) | `DotCharter.Tone/Audio/Tonelab/TonelabProtocol.cs` |
| Pipe client: one command in flight, per-command timeouts (apply-file and dump longer), a timeout or I/O fault DROPS the connection so a late reply is never read as the next answer; the next call reconnects | `DotCharter.Tone/Audio/Tonelab/TonelabPipeClient.cs` |
| Session (`ITonelabSession`, DI singleton): discovery of `tonelab-<pid>` pipes, the newest when there are several, `status` → Disconnected / Starting / Ready, a backoff poll while Starting (`ReadyBackoff`, ~60 s), Reconnect, Launch through an optional `IGameToneLauncher`, drops itself when the game goes away. The `IExternalProcessInsert` opens only while at least one OWNER routes (`RouteAudio(owner, true)`) AND the game is ready: each in-game preview (the designer's, the editor's) claims the route with its first ACCEPTED tone and lets go on restore, the insert closes when the LAST owner lets go (owners are a set: a second claim by one owner is still one), so attaching (the Tone Designer probes on open) never takes the guitar from the rig and the designer and the editor never close each other's route; it closes when the game goes away. The app's session watches the attached PROCESS for its whole life (`WaitForExit`, #599): a game closed while Ready drops the state, the insert and every route claim at once, not at the next failing command | `DotCharter.Tone/Audio/Tonelab/TonelabSession.cs` |
| The launcher seam and its public implementation (Lifecycle below) | `DotCharter.Tone/Audio/Tonelab/IGameToneLauncher.cs`, `TonelabGameLauncher.cs` |
| Latest-wins debounce (40 ms, inside the protocol's 30-50 ms) over an injected `ITonelabScheduler` | `DotCharter.Tone/Audio/Tonelab/TonelabDebouncer.cs` |
| Temp tone files under `UserDataFolders.LocalAppData\DotCharter\tonelab`, named `tone-<pid>-<instance>-<n>.json` so two owners in one process never share or release each other's file (#599): the previous file is deleted once the game answered, all on restore; a new run sweeps files another process id left | `DotCharter.Tone/Audio/Tonelab/TonelabToneFiles.cs` |
| Knob-only vs whole-tone diff (pure): only knob values moved → `tone knob` per knob; anything else (gear, a knob set, Volume) → `apply-file` | `DotCharter.Tone/Audio/Tonelab/TonelabToneDiff.cs` |
| The in-game `ITonePreviewTarget`: validate → debounce → knob or file → report, at most ONE send in flight (drags during a pending reply coalesce into one more send of the latest tone); `Stop` → `tone restore` (only if the game accepted something, and only when no other preview is applying or holds the tone: the session-wide ownership queue retains the ONE restore obligation until its final owner leaves) | `DotCharter.Tone/Audio/Tonelab/TonelabTonePreview.cs` |
| The switch (a router over both targets), the catalogue check (`RsToneCatalogValidation` is in Services, so it is handed to Tone as a delegate), Reconnect, the status line, the ASIO note | `DotCharter.Presentation/Screens/Dialogs/TonePreviewTargets.cs` |

**Preview ownership and ordering.** `TonelabPreviewOwnership` is shared by the session's
preview targets. A flush registers its owner before waiting for its command turn, so a
pending first reply already prevents another preview from restoring. Accepted replies
alone claim audio routing. Applies, knob edits and final-owner restore share one queue;
the offline renderer's lease holds that queue through its own final restore. A pending
preview interrupts that lease and waits for it to finish. Failed applies release their
owner; the shared restore obligation survives other owners leaving and is consumed once
by the final release. Timeouts and malformed replies may have changed the game, so a
reachable session gets one best-effort restore. Loss of the session clears claims/routes
and invalidates late replies without restoring into a later session. A returning owner
sends its whole tone if another owner replaced its cached baseline. Stop/Apply generations
retain their local ordering: an already-reserved Stop turn finishes before a later
application by the same target. A queued write checks that generation again when its
turn arrives, so Stop/Dispose skips work that has not started. None of these operations
quits a game.

- **The preview contract does not change.** The clone path (`TonePreviewSession`, the
  default) and the in-game path both implement `ITonePreviewTarget`: the first apply of a
  session takes the restore point, later applies replace the tone (latest wins), `Stop`
  puts it back on preview off AND on close (see [../ui/tone-designer.md](../ui/tone-designer.md)).
  The game keeps its own restore point, so the in-game `Stop` only sends `tone restore`.
  The designer talks to ONE target, `TonePreviewTargets`, which routes to the chosen one
  (it is the view model's constructor argument in place of the bare clone session).
  Switching while Preview is on stops the old target and re-applies the last tone on the
  new; closing the designer disposes it, which stops whichever target holds the tone.
- **Rocksmith is never live without a ready session.** The radio button is disabled and
  the reason is printed (`Dialog_Tones_InGameNotConnected` / `…Starting` / `…Version`). A
  session that ends while chosen flips the switch back to the rig and says so (`…InGameEnded`).
- **Attach is the primary path (control-only).** Tonelab attaches to a RUNNING game live,
  so the owner starts Rocksmith with tonelab (its launcher); the guitar reaches the game
  through RS ASIO, and DotCharter only controls the tone. The designer probes once per
  open (off the UI thread) by enumerating `\\.\pipe\` for `tonelab-<pid>`: none →
  Rocksmith disabled with "In-game unavailable: start Rocksmith with tonelab, then
  Reconnect" (with a launcher: "Rocksmith is not running: Start Rocksmith, or …" and a
  **Start Rocksmith** link); one → attach; several → the NEWEST process
  (`TonelabSession.ChooseNewest`, by process start time). The **Reconnect** link drops the
  session and probes again (`ReconnectCommand` → `ITonelabSession.ReconnectAsync`).
- **Not ready yet.** `status` says `ok starting v1` until a profile is loaded (the
  launcher drives the game to the main menu itself). The session then polls `status` with
  backoff (`TonelabSession.ReadyBackoff`: 0.25, 0.5, 1, 2 s, then 4 s steps, ~120 s in all)
  and turns Ready on the first `ok ready` (after a launch DotCharter started, the poll is
  `TonelabSession.LaunchReadyPoll` instead: every 250 ms for 60 s, tonelab's HANDOVER step 4);
  meanwhile Rocksmith stays disabled with
  "Rocksmith is starting… preview there once its profile is loaded". Out of steps it becomes
  Disconnected with `StartTimedOut`; Reconnect clears the reason and starts a new bounded poll. Tonelab's launcher (`tonelab-launch.exe`) either starts
  the game or injects into the running one (live attach works); either way the pipe it
  leaves is the one this code finds.
- **The audio device in control-only mode (decided: a message, no device release).**
  Only for a game the user started normally (attach): it plays through its own ASIO
  device (RS ASIO), and an ASIO driver serves one program. A launch with `--no-rsasio`
  opens no device (`ITonelabSession.GameHoldsAudioDevice` false), so no note. There is no supported way in this phase to make the engine
  let go of its device and take it back (`IAudioDeviceControl` exposes none;
  `TryOpenAsioControlPanel` stops and restarts the engine only around the panel, and a
  release would need every master-bus and seat state replayed on reopen). So while
  Rocksmith is chosen, the game holds a device and DotCharter's driver is ASIO, the designer prints
  `Dialog_Tones_InGameAsioNote`: pick another output in Settings or stop DotCharter's
  audio. Phase 2 (DotCharter keeps the device, the game opens none) makes the note obsolete.
- **Threads.** Pipe I/O runs on the debounce timer's thread-pool callback and `Task.Run`,
  serialized per target; session and send events are posted to the UI thread through
  `IUiDispatcher`. Nothing here runs on the UI thread or on any audio thread.
- **Validation happens before anything is written.** A tone that fails the gear
  catalogue (`RsToneCatalogValidation.FirstLoadFault`: volume, descriptors, gear, slots,
  knobs) is reported as `Invalid` with the fault's message, logged as a warning, and never
  sent. The KEY is not held to the authoring rule (`RsToneAuthoring.IsValidKey`, a letter
  first): an official package keys its tones after the song code (`311allm_clean`), the
  game loads those, and the full `FirstFault` would refuse every one of them. The game's
  own `err unknown-gear` (returned since tonelab M3/M4; M2 dropped unknown gear silently)
  is reported as `UnknownGear`, and the next edit re-sends the whole tone (the game's
  state is unknown after a refusal).
- **Tone changes cost the game work.** Each `apply-file` rebuilds the game's chain (and
  leaked a few KB in M2, fixed since): the host debounces (40 ms, latest wins), sends
  `tone knob` rather than a whole file when only knobs moved, and never re-sends an
  unchanged tone.
- **Stuck tone load (#479).** Now and then the game's tone build never finishes: its load-pending
  flag (manager `+0x82`) stays set and every later request only queues, so the game keeps the old
  (clean) tone while the approximated rig switches. tonelab 0c62d5e clears a flag that stays set
  over 2 s and queues the slot again, up to 3 times (log `tone: load pending > 2000 ms; cleared it`).

Tests (all in the default gate except the headless one): `TonelabProtocolTests`,
`TonelabPipeClientTests`, `TonelabSessionTests`, `TonelabDebouncerTests`,
`TonelabTonePreviewTests`, `TonelabSharedMemoryMockTests`, `TonePreviewTargetsTests`,
and `Perf/ToneDesignerPreviewTargetUiPerfSmokeTests` (headless). The game side is
`DotCharter.Tests/Audio/Tonelab/TonelabGameMock.cs`: a real pipe server (`tonelab-<pid>`,
fake pids) and a real shared-memory block that answer like protocol v1 and render input ×
gain, delayed. It is test-only and never ships. Mock identities hold a named kernel reservation
across hosts; tone/dump paths live in a separate temp directory per mock. The mock reads and closes
an applied tone before replying. `TonelabGameMockTests` forces an identity collision and checks
exclusive access to the tone after an apply reply.

Session tests pass a manual `TimeProvider` through `TonelabSession` to its pipe client; connect and reply
deadlines advance only when the test advances that clock. Ready polling has a separate injected
`Delay`: hold it while asserting the reconnect's Starting state and cleared failure, then release
and await `ReadyPoll` before checking exhaustion. `TonelabTonePreviewTests` holds an apply reply
across Stop and verifies restore is last; the existing semaphore ordering needs no product fix.
Launcher tests await `TonelabGameLauncher.ExitWatch` through the INI restore instead of spinning
for a file change. Their fake process wait is explicitly released to verify quit grace before kill;
it does not measure elapsed wall time. Real-time wait caps only detect deadlocks (60 s).

## The editor's playhead preview (#7)

The toolbar's **Preview tones** (`ChartTonePreviewController` over `RsTonePlaybackCoordinator`:
the tone at the playhead, switching at tone changes) has a **Preview in Rocksmith** switch
beside it (`ToolbarView.axaml`, `ToneRigViewModel.ToggleTonePreviewRocksmithCommand`).

- **`EditorRocksmithTonePreview`** (`DotCharter.Services/Audio/Coordination/`) is the
  target. The controller owns it (built by `ToneRigViewModel` over the app's
  `ITonelabSession`); it holds its own `TonelabTonePreview`. The coordinator keeps driving
  the clone rig exactly as before; on every `ActiveToneChanged` of the EDITOR preview
  (`SongIdentity` null) while Rocksmith is chosen, the same tone's BODY (the project's
  `ToneDefinitions`, the designer's unsaved edits laid over them,
  `EditorRocksmithTonePreview.ParseTones`) goes to the game (`apply-file`, or `tone knob`
  when only knobs moved). `PreviewStarted` sends the playhead tone when the preview starts,
  because the coordinator's pre-roll apply fires before it reports `IsPreviewing`.
- **Synth and guitar both reach the game** with no native change: the chart synth is
  mixed into seat 0's chain before the ABI v47 insert, and the insert opens once the first
  tone is accepted (the route claim above). The clone chain is bypassed, not torn down.
- **Off puts it back.** `DrivingStopped` (toggle and synth both off, a Play Mode run taking
  the rig, shutdown's abandon) and switching back to the rig `Stop` the target: `tone
  restore` and the route let go. A session that stops being ready while chosen falls back to
  the rig and the tip says so (`Dialog_Tones_InGameEnded`).
- **Never live without a session.** The switch is disabled while the session is not
  ready, with the reason on its tip (`Toolbar_TonePreviewRocksmith_NotConnected`,
  `Dialog_Tones_InGameStarting`, `…Version`). The one exception: with a launcher registered
  and no game, the switch is enabled and STARTS Rocksmith (`Toolbar_TonePreviewRocksmith_Start`),
  choosing it once the session is ready (see Lifecycle). The target probes (off the UI thread) when the
  toolbar builds the controller, when Preview tones turns on and on return to the editor;
  the Tone Designer's Reconnect reaches it too (one session).
- **Tone-switch lag (documented, not compensated).** A switch reaches the game after the
  playhead tick (≤ 60 Hz), the clone apply, the 40 ms debounce, the pipe round trip and
  the game's own tone tick, which follows its main-loop rate while hidden. That rate is the
  game side's (the `perf hidden_fps` cap, and tonelab's renderer changes), so no number is
  hard-coded here or in the UI ("a moment after"). Sending early would need a lead time
  measured from `apply-file` round trips, in ONE place, as a clock-injected pure unit.
- Tests: `EditorRocksmithTonePreviewTests` (the loopback mock + a real session: a crossed
  tone change sends `apply-file`, off restores and un-routes, designer + editor routes,
  disabled with reason, session end), `TonelabSessionTests` (route owners),
  `TonePreviewReachabilityTests` (the switch, the shell hop, the verbs, the strings).

## Phase 2: the audio path (design)

**Rule: all real-time audio lives in the native engine; the audio callback never waits
on the game.** So the exchange is a native module, and managed code only opens it,
closes it and reads its counters (`IExternalProcessInsert`).

- **Routing assumption: DotCharter keeps the device (ASIO), the game opens none.** The
  DLL forces the game's dummy output and replaces its capture input with the input ring.
  The game has no device clock of its own: it renders when input arrives, so there is no
  drift between two device clocks to correct.
- **Open, not yet stream.** The insert maps `Local\tonelab-<pid>`, checks `magic`,
  `version` (1), `sampleRate` (48000) and waits for `blockFrames != 0` (the game sets it on
  its first rendered block) before using the rings; until then it passes the dry signal.
- **Where it sits.** One node in the live input path of a seat (the place the clone rig's
  chain sits today): the dry input goes out, the processed stereo comes back and replaces
  the chain's output while the insert is open. The clone chain is bypassed, not torn down.
- **Per callback, non-blocking** (PROTOCOL.md's host steps):
  1. convert the dry input (device rate → 48 kHz, a fixed-ratio resampler preallocated at
     open, no allocation in the callback) and write whole 48 kHz blocks into the input
     ring; ring full → skip the write and count `overruns` (never overwrite unread blocks);
  2. publish `inWrite` with a release store. No event: the game polls at most 1 ms apart
     and does not create `Local\tonelab-<pid>-in`;
  3. read the processed blocks the game has published (`outWrite`, acquire load), ONE
     BLOCK BEHIND: the reader keeps one game block of headroom, so a game that is a little
     late is not an underrun;
  4. none ready, or a block that is late or short: play the DRY signal from the same point
     in time (the dry history is kept at the insert's latency, so the switch is
     time-aligned), with a short crossfade both ways, and count `hostUnderruns` (also
     written to the header for the game's log). Dry, not silence: a player who loses the
     game for a block should hear their guitar, not a dropout;
  5. convert back 48 kHz → device rate; advance `outRead`.
- **The producer is checked, not trusted.** Never assume the game keeps 48 kHz pace (M2
  rendered at ~37 kHz while hidden; fixed since, hidden now renders a steady 48 kHz): the
  header's `sampleRate` and `blockFrames` are re-read (two loads per callback); a change
  closes the stream to dry and re-opens the converter OFF the audio thread, never in it.
- **Counters and latency** (`dca_*` stats export → `ExternalInsertStats`): host underruns,
  overruns, the game's own `gameUnderruns` / `outOverruns` read from the header, the added
  latency in engine frames. The Tone Designer prints the latency after a successful send
  (`Dialog_Tones_InGameLatency`).
- **Latency budget** at the game's 128-frame block (2.7 ms at 48 kHz): game processing,
  measured by tonelab at ~7.3 ms (2.7 blocks, 0 skipped, 0 late), + reader headroom 1 block
  (2.7 ms) + resampling (~1 ms when the device is not at 48 kHz) ≈ 10-11 ms on top of the
  device's own input and output buffers. PROTOCOL.md's target is one extra host block plus
  1-2 game blocks; the measured game side is the bigger share.
- **Rebuild replay.** The insert is per-seat engine state: it must be re-opened on engine
  REBUILD (device change, sample-rate change, watchdog recovery) like any other seat state.
- **The work.** A new native module + `dca_*` open/close/stats exports (names settled in
  the ABI change) through the `dotcharter-abi-bump` skill, an RT-gate scenario through
  `dotcharter-rt-gate` (no lock, no allocation, no wait in the callback path), a P/Invoke
  mirror, and the `IExternalProcessInsert` implementation in `JuceAudioPlaybackService`
  (passed to `TonelabSession`). Built in the main checkout: an agent worktree cannot commit
  to the submodule.

## Phase 2: as built

The native insert is JuceSharp ABI v47 (`dca_engine_extproc_*`, `JuceSharp/docs/agents/native-engine-abi.md`), managed in `DotCharter.Tone/Audio/ExternalInsert/` (`JuceExternalProcessInsert`, replayed on engine rebuilds by `ExternalProcessInserts`). The app registers ONE for seat 0 in `App.AudioServices` with `TonelabProtocol.HeaderMagic`; without the JUCE engine it is `UnsupportedExternalProcessInsert`. An underrun plays the dry input (64-frame ramps); a device off 48 kHz is re-clocked in place like the NAM request.

## Offline renders for the AI tone check (#12)

The AI tone check ([ai-tone-generate.md](ai-tone-generate.md) § The audio check) judges its finalists
through the game itself. `IGameToneRenderer` (`DotCharter.Tone/Audio/Tonelab/IGameToneRenderer.cs`) is the seam;
`TonelabOfflineRenderer` is the shipping one, registered in `DialogsModule` over the app's session.

- **One user of the game at a time.** The game has one tone and one exchange. `ITonelabSession.TryLeaseAudio`
  answers null while any owner routes the live input (a live preview), and one lease at a time. While a lease
  is held a route claim is RECORDED but the insert stays closed (queued) and the lease reads `Interrupted`;
  ending the lease opens the insert for the waiting claim. A render that saw the claim is discarded
  (`GameRenderRefusals.Interrupted`), the rest of its batch is not sent.
- **Refusals are codes, printed by the creator:** `game-not-ready`, `live-preview-routing`, `game-own-device`
  (a game the user started holds RS ASIO's device, so its input is that device and not the exchange; only a game
  DotCharter launched opens none), `no-audio-ring`, `tone-refused`, `interrupted-by-live-preview`, `game-stalled`.
- **Per batch:** take the lease, open the exchange (`TonelabAudioRing.TryOpen`: magic, version 1, 48 kHz, waits
  up to 2 s for a non-zero block size), then per tone `tone apply-file` (a refusal skips that tone) and
  `TonelabAudioRing.Render`: the DI with 500 ms of silence before it (the previous tone's tails die out, the game's
  block offset lands there) and 250 ms after, pushed as fast as the game takes blocks (at most ring − 1 in flight,
  so the game never finds its output ring full), the two output channels averaged, the lead-in cut. A game that
  takes no block for 3 s is `game-stalled`. Then `tone restore`, once.
- **Not the audio callback.** It is a worker thread over a memory-mapped view, and only while the native insert
  is closed, so the "all real-time audio in the native engine" rule is untouched. No JuceSharp or ABI change.

**What the game side must confirm before a real run** (none of it can be tested here; `TonelabGameMock` is the
only counterpart this code has met):

1. **The exchange with no insert open.** A game DotCharter launched opens no device; the renderer assumes it
   still consumes the input ring whenever unread blocks are there, with no device clock and the native insert
   closed, and that `blockFrames` is non-zero by then (it is 0 "until the game rendered its first block").
   If the game renders only after a host insert has attached, `TryOpen` answers `no-audio-ring` with
   "the game has not rendered its first block".
2. **Tone change before the next block.** `ok applied` is taken to mean the chain is swapped before the next
   input block is processed; 500 ms of lead-in covers a later swap only up to that.
3. **Back-to-back renders as fast as the game can.** Tonelab M10's null renderer and fast start were the
   precondition; the issue asks to measure how fast a whole DI medley renders through the game before the round
   budget is fixed. Not measured: the budget (`RsToneLoopOptions`: 3 proposals, 2 revision rounds, 2 finalists)
   is the clone side's.
4. **The game's own DSP state** (a looper, a noise gate's hold) is not reset between tones beyond the silence.

Tests: `Audio/Tonelab/TonelabOfflineRendererTests` (the mock's exchange: input × gain, delayed, per tone, restore
once; the refusals; a route claimed mid-batch; the session's lease) and `Ai/Generate/RsToneFeedbackLoopTests`
(the loop over the real renderer and a routed fake session: clone-only, nothing sent).

## Lifecycle: the launcher

DotCharter starts and ends the game itself; a tiny 32-bit helper does ONLY the injection
(the game is 32-bit, DotCharter 64-bit). All of it is public and lives in
`DotCharter.Tone/Audio/Tonelab/`. Supported by tonelab: player 1 only, one game exe version.

**Registration.** `TonelabGameLauncher.Register` (called from `App.AudioServices`) adds the one
`IGameToneLauncher` only when `InstallLocation.InstallRoot` + `tools/tonelab/` holds BOTH
`tonelab.dll` and `tonelab-launch.exe` (`TonelabInstall.Find`). Otherwise nothing is registered:
the Tone Designer hides **Start Rocksmith** and prints "start Rocksmith with tonelab, then
Reconnect" (`Dialog_Tones_InGameNotConnected`), and the editor's Rocksmith switch stays
disabled with `Toolbar_TonePreviewRocksmith_NotConnected`. `TonelabSession` takes every
registered launcher (`IEnumerable<IGameToneLauncher>`), uses the first and keeps the launch.

**Who drives it.** The Tone Designer's **Start Rocksmith** (`TonePreviewTargets.LaunchCommand`)
and the editor's Rocksmith switch: with a launcher and no game it starts one
(`EditorRocksmithTonePreview.StartAsync`, tip `Toolbar_TonePreviewRocksmith_Start`) and picks
Rocksmith once the session is ready. Both go through `ITonelabSession.LaunchAsync`.

**Start** (`TonelabGameLauncher.LaunchAsync`, tonelab's HANDOVER § "What DotCharter implements (M10)"):

1. **A game is running** (`Rocksmith2014.exe`, the user's): inject into it, nothing else.
   RS_ASIO.ini is NOT touched (never while the game runs), `StartedByApp` is false, and the game
   keeps RS ASIO's device (`GameOpensNoAudioDevice` false, so the ASIO note can show).
2. **No game:** the `IRocksmithStarter`s in order (`TonelabGameLauncher.Create`): the mirror's
   direct start first, Steam on the live folder only when that strategy answers `Unavailable`.
   A start that FAILS (`Failed`, e.g. Steam not running) is not retried another way. Then inject
   AT ONCE (the window is created ~50 ms after the game's entry point; tonelab patches before),
   then the session's ready poll. The reason of a failed launch is kept
   (`IGameToneLauncher.LastFailure` → `ITonelabSession.LastLaunchFailure`, reset by Reconnect) and
   the Tone Designer prints it (`Dialog_Tones_InGameSteamNotRunning`, `…InGameLaunchFailed`).
3. **Mirror + direct start (the default, `MirrorRocksmithStarter`).** The upkeep
   (`TonelabGameMirror.Prepare`, pure over `IGameMirrorFileSystem`; the real one is
   `Win32GameMirrorFileSystem`) runs at every start into `TonelabGameMirror.DefaultRoot` =
   `UserDataFolders.LocalAppData\tonelab\game` (tonelab's own default; `DOTCHARTER_DATA_ROOT` moves it):
   - hardlinks (`CreateHardLinkW`) for `TonelabGameMirror.LinkedFiles` and every root `*.psarc`,
     each checked by identity (`GetFileInformationByHandle` volume serial + file index, on a
     handle opened with NO access) and dropped + relinked when stale (a game update writes new
     files); a vanished psarc leaves the mirror;
   - junctions (mount-point reparse points, no admin) for `base`, `guitarcade`, `venues`,
     `UPlay`; a wrong one is removed with `RemoveDirectoryW` alone, never recursively; a REAL
     folder in a junction's place fails the upkeep (`Failed`) and is left alone;
   - `Rocksmith.ini` copied fresh, `RS_ASIO.ini` the mirror's own driverless one, `Tmp` created;
     `TonelabGameMirror.LeftOut` (rs7string, RSMods, the CDLC enabler, `dlc`) never mirrored and
     removed if found;
   - **never a write through a mirror name**: real files are deleted first and created NEW
     (`CopyToNewFile` / `WriteNewFile` = `FileMode.CreateNew`), because a hardlink name writes
     the game's original;
   - `ERROR_NOT_SAME_DEVICE` on the first link (the exe) → the mirror goes on the game's volume
     (`<drive>:\tonelab-game`, `TonelabGameMirror.GameVolumeRoot`); not writable →
     `CrossVolume` → the strategy is `Unavailable` and the launcher falls back to Steam.

   Then `Win32GameProcessCreator`: `CreateProcessW` (P/Invoke: `ProcessStartInfo.WindowStyle` is
   only read by the shell-execute path) on the mirror's exe, working folder = the mirror,
   `STARTF_USESHOWWINDOW` + `SW_HIDE`, `CREATE_NO_WINDOW`, a Unicode environment block = the app's
   plus `SteamAppId` = `SteamGameId` = 221680 (`MirrorRocksmithStarter.GameEnvironment`). Steam not
   running (`RocksmithGame.IsSteamRunning`, a `steam` process) → REFUSED, `SteamNotRunning`. The
   game opens no audio device (`GameOpensNoAudioDevice` true) and nothing is restored afterwards.
4. **Live-folder fallback (`SteamRocksmithStarter`, `UsesLiveGameFolder`).** `RsAsioIniGuard.Apply`
   (below), `steam://rungameid/221680`, wait for the process (`StartTimeout`, 180 s), inject; the
   ini goes back after the exit. rs7string and RSMods load in this mode.
   A start that is not possible (`NoGame`, cross-volume) answers `Unavailable` and the live-folder start (4.) takes over; every
   decision is logged. There is no failure marker: a game that never gets ready is a boot hang (below), not a mirror problem
   (#463 removed `MirrorFailureMarker` and `IGameToneLauncher.MirrorFailed`).
**Boot hazard (#461).** ANY connection to `\\.\pipe\tonelab-<pid>` while the game boots hangs it before its title screen (tonelab's pipe server thread; fixing that belongs to tonelab). So after a launch DotCharter started the session makes NO pipe use until tonelab's own log says the game is past boot: `TonelabBootGate` reads `tonelab.log` beside the injected `tonelab.dll` (`IGameToneLaunch.BootLogPath`), shared (`FileShare.ReadWrite | Delete`), every 500 ms, for `tonelab M2 loaded into pid <pid>` then `ready: profile loaded` (a log of another pid is ignored), and only then asks the first `status`. Meanwhile the state is Starting and `ProbeAsync`/`ReconnectAsync` answer Starting without connecting (`TonelabSession._bootPid`). **Boot hang retry (#463).** The hang is intermittent and lives in the game's boot (a good boot takes 23-31 s; no pipe use needed). **Root cause (#476):** the title screen asks for its Flash movie while the game is still rebuilding its file index, the request is never answered and nothing asks again (the soft lock RSMods also retries; the mirror start loads no RSMods). It comes in streaks (13 of 14 starts hung for over an hour on 2026-11-09). tonelab c3fe9a5 asks again every 250 ms, up to 30 s (`[startup] menu_retry`, log lines `menu retry: 'titlescreen_view' ...`); with it 10 of 10 starts were ready on the first attempt. The retry below stays as a backstop. No ready line within `TonelabBootGate.Timeout` (45 s) quits that game (`QuitAsync`: `quit` or, failing that, the process is ended) and launches again, as attempt 2 and then 3 of `TonelabSession.MaxAttempts` (3); each attempt is logged. `ITonelabSession.LaunchAttempt` drives the status text ("Rocksmith is starting… attempt 2 of 3", resx `Dialog_Tones_InGameStartingRetry`). After the third hang the state is Disconnected with `StartTimedOut` until Reconnect or Start; a Start that finds the game this launcher started still running reuses that launch (already injected, `StartedByApp` stays true), so Quit still ends it (#599). Nothing is remembered between launches, so no later start skips the mirror. A game DotCharter did not start is attached as before. The #455 "mirror never ready" was this hang, not a broken mirror. Tests: `TonelabBootGateTests` (injected log reader and clock: retry, ready on attempt 2, give up after 3).
5. A failed injection into a game DotCharter started ENDS that game (it has no pipe to quit
   through); in the fallback the ini goes back too.

**Inject** (`TonelabInjectHelper`): `tools/tonelab/tonelab-launch.exe --inject <pid> --dll <tonelab.dll>`,
run hidden (`HiddenHelperProcessRunner`: no shell, `CreateNoWindow`, stdout read), bounded by
`TonelabInjectHelper.Timeout` (45 s; the helper itself waits up to 30 s for the game's loader;
past the bound the HELPER is ended, never the game). The exit code decides: 0 ok (also when
already loaded), 2 usage, 3 no-process, 4 not-rocksmith, 5 open-failed, 6 dll-not-found,
7 exited / loader-timeout, 8 inject-failed; anything else is `Failed`. The last stdout line
(`ok` / `err <reason>`) is logged.

**RS_ASIO.ini** (`RsAsioIniGuard`, tonelab's HANDOVER rules, pure over `ITonelabFileSystem` and
an "is the game running" probe; the game folder comes from Steam's uninstall key
`Steam App 221680`, 32-bit view, `InstallLocation`, read only):

- **Apply:** refused while the game runs; a backup an earlier session left goes back FIRST (so
  the backup is always the user's own file); no `RS_ASIO.ini` means RS ASIO is not installed,
  nothing to do; otherwise publish it atomically to `RS_ASIO.ini.tonelab-backup` (tonelab's own
  name, so either launcher cleans up after the other), THEN atomically publish the driverless ini (ASIO off,
  WASAPI endpoints off, every driver empty). A failed write puts the original back at once.
- **Restore:** validate and atomically publish the backup byte for byte, then delete it; refused while the game
  runs (the backup stays). Runs after a game DotCharter started exits (a watcher on the
  process, whatever ended it), and at EVERY app start
  (`TonelabGameLauncher.RestoreLeftIniAtStartup`, the crash case; off in the headless host
  through `DisableStartupRestoreSwitch`).
- DotCharter publishes a `.sha256` receipt before its backup and checks it on
  restore. A damaged backup or receipt is kept for recovery and never replaces
  the INI. Legacy backups without a receipt must contain a recognizable config
  and may replace a driverless INI or an identical original; they cannot replace
  another intact config. `RsAsioIniGuardTests` covers partial writes, changed
  backups and legacy restoration.

**End** (`ITonelabSession.QuitAsync`, then `IGameToneLaunch.EndAsync`):

- **A game DotCharter started:** `quit` over the session's pipe, up to `QuitGrace` (10 s) for
  the process to exit, only then it is ended (`IRocksmithProcesses.Kill`), then (live folder only)
  RS_ASIO.ini goes back. A quit that could not be sent skips the wait. A mirror game restores
  nothing.
- **A game the user started is only DETACHED:** nothing is sent, nothing is ended.
- **App exit** ends a game DotCharter started, on both exit paths (`MainWindow.OnClosing` runs it before either is chosen; it
  calls `TonelabGameLauncher.EndStartedGameOnExit`, bounded at `ExitCap` (15 s, past the quit grace), and never builds the
  session when no launch is live).

Tests: `RsAsioIniGuardTests`, `TonelabInjectHelperTests`, `TonelabGameLauncherTests` (fakes for
the processes, the starters and the helper: mirror first, the Steam fallback, the Steam-not-running
refusal; the session's quit through the loopback mock), `TonelabGameMirrorTests` (the upkeep over
memory: fresh, stale relink, psarc churn, junctions, left-out, no write through a name,
cross-volume), `Win32GameMirrorFileSystemTests` (the real Win32 calls in a temp folder, and a
hidden `CreateProcessW` that carries its environment), `MirrorRocksmithStarterTests`,
`TonelabSessionTests` (quit vs detach, the 250 ms launch poll, the kept failure),
`EditorRocksmithTonePreviewTests` (the switch starts), `TonelabReleaseLintTests` (the release
step, the hash pin, no tonelab source in the repository).

**Shipping.** tonelab's source stays in its own repository: it patches the running game's
code, and this repository only names the build it ships. `tools/release/tonelab/tonelab.json`
pins the commit and each file's SHA-256; `tools/release/New-Release.ps1` takes the prebuilt
files from `-TonelabDir` (default `DOTCHARTER_TONELAB_DIR`), checks them (`Test-TonelabPin`,
in preflight and again in `Add-Tonelab`) and copies them into `tools/tonelab/` (`tonelab.dll`,
`tonelab-launch.exe`, `tonelab.ini`, `LICENSE.txt`) before the payload is hashed. A missing
folder or a different hash fails preflight unless `-SkipTonelab` (printed). A new tonelab build
means a deliberate change to `tonelab.json`. A dev build has no `tools/tonelab/`, so the launcher is not
registered there. Not built: a "connected / starting / off" indicator.

**Errors** (HANDOVER): `err timeout …` means the game is not ticking (loading screen, crash,
debugger): read as `Timeout`, the next edit resends (retry after ~1 s); `status` stuck at
`ok starting`: the session's ready poll gives it ~60 s, then Reconnect asks again (tonelab's
auto_menu gives up after 120 s, then a user must press Enter in the game); the process exits:
pipe and mapping vanish, the session drops to Disconnected; `err unknown-gear`: the tone is
not applied, the previous one stays. Every other `err …` is shown verbatim in the designer.

Other commands the client carries: `perf hidden_fps|visible_fps <n>` (`SetFrameCapAsync`,
the main-loop cap; replies take up to one game frame, 100 ms at the hidden 10 fps cap),
`window hide|show`, `mix mute on|off`, `tone dump <path>`.

## Open questions for the game side

1. **`apply-file` / `tone dump` path quoting.** The host sends the rest of the line as the
   path, spaces included, unquoted. Confirm, or specify quoting.
2. **File lifetime.** May the host delete the tone file once `ok applied` arrived (the host
   does), or does the DLL read it later?
3. **`gearSlot` vocabulary for `tone knob`.** The host sends the GearList slot names
   (`Amp`, `Cabinet`, `PrePedal1`-`4`, `Rack1`-`4`, `PostPedal1`-`4`) and the FULL knob key
   (`<GearKey>_<Pot>`); the value in the knob's natural unit, invariant culture. Confirm.
4. **Volume.** There is no knob command for the tone Volume, so a Volume change re-sends
   the whole file. A `tone volume <dB>` would make it live.
5. **Restore after reconnect.** If the host reconnects after a dropped pipe, is the
   restore point (the tone before the first apply) still the original one?
6. **One client at a time.** The pipe takes one client; if the owner's own tools hold it,
   DotCharter's connect times out and the designer says "unavailable". A distinct
   `err busy` (or a second instance) would let the message say why.
