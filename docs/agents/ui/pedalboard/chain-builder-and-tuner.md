# The chain builder canvas, the routing toolbar and the rig tuner

Part of the Amp & FX rig reference. Router: [../pedalboard.md](../pedalboard.md).

> **⚠ THE CANVAS IS HISTORY — verified against the tree 2026-08-08.**
> `SignalChainCanvas`, its `ChainPainter`, `GearThumbnail` and the whole
> `SignalFlowLayout` / `SignalFlowTag` geometry layer are DELETED — they exist
> nowhere in the repo outside this file and its two siblings. Everything below
> describing that canvas is a record of the retired rack.
>
> **Still true:** the ROUTING commands it drives
> (`Routing{Serial,Split2,Split3}Command`, `DropToRow`/`MoveTo`, `SelectNodeAt`)
> are all still on `PedalboardViewModel`, and the rig tuner's math
> (`Controls/Dsp/RigTunerMath.cs`, `UI/RigTunerMathTests`) survives unchanged.
> The live tuner and routing controls are in
> `Views/PlayMode/AmpFxRigView.axaml`.

- `Controls/Dsp/` — continued from [widgets-and-faces.md](widgets-and-faces.md):
    - **Chain builder (the routing canvas) + selection.** `SignalChainCanvas`
      (replaces the old `SignalFlowStrip`) sits at the board's foot: IN jack →
      tuner dial → a **`GearThumbnail` card per module** (a node LOOKS like its
      gear) wired by thick amber rounded-elbow **cables** → OUT speaker glyph; a
      split region fans the cables into stacked parallel **branch rows** with
      numbered branch markers (1/2/3) at the merge point and splitter/merger drawn
      as junction glyphs. It is a `Panel` (Panel seals `Render`) whose FIRST child
      is a hit-test-invisible `ChainPainter` drawing the cables/plates/glyphs UNDER
      the real `GearThumbnail` children; the canvas itself owns all pointer input.
      Geometry + hit tests are the pure `SignalFlowLayout.ComputeBuilder` /
      `HitTestBuilder` / `HitTestBuilderTuner` / `ComputeBuilderDrop`.
      **Interaction:** card click → `PedalboardViewModel.SelectNodeAt` (sets
      `PedalNodeViewModel.IsSelected` → the `.Selected` highlight styles on every
      skin) + BringIntoView-scrolls the rack; **drag a card** → reorder live
      (`NodeDropped` → the view calls `DropToRow` inside a split region, else
      `MoveTo` — the canvas NEVER touches the model); **double-click** → bypass;
      hover lights the card + its cable segments; the dial toggles the tuner.
      Bypassed cards + their outgoing cables dim. Invalidates only on
      chain/bypass/selection/hover/drop-target change — no timers, no style
      animations. Selection clears on Sync/remove; selecting a **Pitch**-family
      pedal auto-opens the rig tuner (AmpliTube parity).
    - **Routing-mode toolbar.** Left of the canvas: serial / 2-branch / 3-branch
      `ToggleButton.RoutingMode` buttons whose CHECKED state mirrors the chain
      (`PedalboardViewModel.RoutingBranches` / `IsRoutingSerial|Split2|Split3`,
      re-notified from `RebuildBoardColumns`) and whose click routes through
      `RoutingSerialCommand` / `RoutingSplit2Command` / `RoutingSplit3Command`.
      Split builds the `splitter`+`merger` pair AROUND the current selection (at
      the chain end when nothing is selected) or just re-widths a live region via
      `PedalNodeViewModel.SetEnabledBranches`; serial removes the live junction
      pair and keeps the body pedals. Covered by `PedalboardSplitViewModelTests`.
    - **Rig tuner panel.** `RigTunerViewModel` (on
      `PedalboardViewModel.RigTuner`; App DI passes `IAudioPlaybackService`,
      null in tests keeps it inert) drives a strobe+needle card from the
      managed tuner suite (`TunerPitchEngine` coarse, `VirtualStrobe` fine —
      fed the two CONTIGUOUS HALVES of each `ReadInputCaptureWindow` snapshot
      — `TunerDisplaySmoother` display). The 66 ms poll timer is LAZY and runs
      only while the panel is open AND a hosting view reports itself live
      (attach + `IsEffectivelyVisible` → `SetSurfaceLive`; MenuBackdrop gating
      semantics — both hosts share the singleton VM, so liveness is a
      per-surface set). Never polls while hidden.
      - **The reporting half only arrived on 2026-08-15, and until then this
        paragraph described a caller that did not exist.** `SetSurfaceLive` had
        NO product caller, so the live set was permanently empty, `SyncRunning`
        could never start the timer and the footer's `FireTunerMeter` needle
        never moved once — while the gating contract test over it
        (`PedalboardAuditRegressionTests
        .Rig_tuner_polls_only_while_open_and_a_host_surface_is_live`) stayed
        green, because it called the method itself. `AmpFxRigView.axaml.cs` is
        now the caller, off the same `EffectiveVisibilityWatcher` the meter
        clock uses (never a `nameof(IsEffectivelyVisible)` branch — Avalonia 12
        raises nothing for that). It hands the old VM's token back on a
        DataContext swap, so a retired rig cannot be left polling. **It also
        OPENS the tuner when live** (`_liveTuner.Open()`): the poll gates on
        `IsOpen` too, and the only other opener is selecting a Pitch-family
        pedal — so liveness alone still left the footer needle still on every
        rig without one, and the first smoke test over it passed only because
        it called `Open()` itself. The footer draws the tuner permanently:
        a live rig surface IS an open tuner. Pinned by
        `AmpFxRigScreenTests.TheRigCodeBehind_ReportsItsSurfaceLivenessToTheTuner`
        (source, default gate) and `Perf/PlayModeScreensUiPerfSmokeTests
        .RigScreen_ReportsItsOwnLiveness_SoTheFooterTunerActuallyReads`
        (navigates the real screen and watches `IsPolling` rise and fall).
      - **…and a STILL needle has to say WHY (0.61).** The reporting half made
        the poll run; the footer still drew only the note, the meter and the
        cents, so every state in which there is legitimately no reading — no
        transport, no capture device, a seat the engine is not running,
        nothing plucked yet — rendered as a dash and a dead needle with no
        reason beside it, and the owner reported the tuner as a control with no
        function. The VM had WORDED all four the whole time (`StatusHint`:
        `Tuner_NoInput` / `Tuner_Listening` / `Tuner_SeatNotLive`); nothing drew
        it. `RigTunerStatus` in the footer binds it under
        `IsVisible="{Binding !…HasReading}"`, and the constructor seeds it for
        the one state the tick can never report (no transport ⇒ the poll never
        runs at all). Pinned by `UI/RigTunerStatusTests` — three VM states plus
        the markup binding. **A blank readout is never an acceptable answer
        here**: same rule as an engine capability shipping visibly disabled with
        its reason printed.
      - **A TUNING to tune to (2026-09-27).** `RigTunerViewModel.SelectedTuning`
        over `Services/Audio/Tuning/TunerTunings` (E / Eb / D / Db / C / B
        Standard, Drop D / C# / C / B / A, DADGAD, Double Drop D, Open D / G /
        E / A / C, and four bass tunings; names from Core's
        `TuningNameResolver`, ids built from the steps), persisted as
        `AppConfig.TunerTuning` ("chromatic" = none). A tuning judges each
        reading against its NEAREST string (`ArrangementTunerLogic`) instead of
        the nearest semitone, so an E standard low string reads "D2, sharp"
        against Drop D. The footer's `RigTunerTuning` picker IS the strip's
        label; with a tuning, the note's cell shows one chip per string
        (`RigTunerStrings`, `Border.FireTunerString`: `.current` amber, `.tuned`
        green). The reference pitch's eyebrow is "A4 =" (`Rig_TunerReferenceShort`)
        — the full name cost the strip its single line at 1920. **Every cell is
        FIXED** (picker 180, note/strings 101, meter 130, readout 86 — "−50 ¢" is 32 px; the status hint trims, and 86 is what keeps the German footer, with the drop pedal, on one line): picking a
        tuning shifted the whole footer until it was (owner), and the meter is
        `VerticalAlignment="Center"` because it draws its 22 px track from its
        own top and sat 5 px high in the 32 px row. The tuning also goes to the
        AI designer (`DesignTuning`). Pinned by `UI/RigTunerTuningTests` (real
        sine readings; `TickClock` is the poll's test clock) and
        `RigFooterStabilityUiPerfSmokeTests.ATuning_ShowsItsStrings_AndPickingOneMovesNothing`
        (no x moves across Chromatic / Drop D / DADGAD / bass, every name fits
        untrimmed, the meter's track centred; PNGs with the capture env vars).
      - **The DROP TUNE pedal (2026-09-27)** sits after the input cluster
        (`RigDropTuneStrip` / `RigDropTuneCombo`, +12…−12, fixed 84 px, the
        eyebrow `.Accent` while engaged). `RigDropTuneViewModel` owns NO value:
        DotCharter hands it the transport's `InputPitchShiftSemis`
        (`UI/ViewModels/PlayMode/RigDropTune.Over`, both rig hosts — one value for
        the footer, the transport's picker and a song's own drop tune), RigLab
        the engine's `InputPitchShiftSemitones` + `RigConfig.InputPitchShiftSemis`
        (`RigLabHost.DropTuneOver`, applied at start; the field moved down from
        `AppConfig`, same JSON). The input's grey-reason text was capped at 80 px
        to keep the footer one line. Pinned by `UI/RigDropTuneTests` and
        `RigFooterStabilityUiPerfSmokeTests.TheDropTunePedal_WritesTheHostsValue_AndHoldsStill`.
      - **`Toggle()` / `ToggleCommand` are GONE (0.61).** There is no tuner
        PANEL to toggle any more — the footer draws the needle permanently, and
        `Open()`/`Close()` are called directly — so neither the generated
        command nor the method had a caller. It had sat in
        `UI/RelayCommandReachabilityTests.KnownUnreachable` behind an excuse
        ("its `SetSurfaceLive` is called by no view either") that stopped being
        true on 2026-08-15. **Do not re-add a toggle without a control that
        raises it.**
      - **It reads the SEAT the board is pointed at (0.59).** `RigTuner.Player`
        is written by `PedalboardViewModel.OnSelectedSeatChanged`, and the tick
        takes `IPlayerInputRouting.ReadPlayerInputCaptureWindow(player, …)` for
        any seat past 0. Until then it read the global tap for every seat, so
        switching the rig to player 2 left the needle on player 1's guitar and
        "tune each guitar" tuned one guitar twice. **No native change was
        needed** — `dca_engine_read_input_capture_for_player` and the wrapper's
        `ReadInputCapture(player, …)` shipped with ABI v28; the managed member
        and its caller were the whole gap.
      - **Always the WINDOWED read, never `ReadPlayerInputCaptureStream`.** The
        stream cursor belongs to a streaming reader; a needle consuming it
        would steal its samples. The engine
        keeps the two cursors separate for exactly this reason.
      - **A seat the engine is not RUNNING says so** (`IsSeatLive` over
        `IPlayerInputRouting.LiveInputPlayerCount`, printing
        `Tuner_SeatNotLive`). The audio callback only walks the live seats, so a
        higher seat's ring is never written unless the input configuration
        (or an override) raised the seat count — and an unlit
        needle with "Pluck a string…" under it would be the capability lie this
        repo keeps paying for. Guarded by `UI/RigTunerSeatTests`.

## DI recorder + re-amp (ABI v37)

- The header's **`RigReampButton`** ("● DI", column 4 of `RigHeader`) opens a
  `Flyout` holding `Views/PlayMode/RigReampPanel` over the process-wide
  `RigReampViewModel` (`AmpFxRigViewModel.Reamp`; both DotCharter hosts and
  RigLab hand it the same one). NOT the footer: the footer must stay one line
  (`RigFooterStabilityUiPerfSmokeTests`). The lamp lights while recording or
  re-amping (`RigReampViewModel.IsActive`), so a take running behind a closed
  popover is never invisible.
- Record DI captures the SEAT's dry input (the capture ring the tuner reads,
  after the drop tune) to `<per-user folder>/DI Takes/DI <date time>.wav`.
  Re-amp plays a take INTO the seat's input — rig, tuner and meter hear it, the
  guitar is muted until Stop — with play/pause, a seek slider, loop (default on)
  and a `0:37 / 1:23` readout. Recording and re-amping exclude each other.
- Every readout sits in a FIXED cell (record 96, record time 44, transport time
  104 = "20:00 / 20:00"); `RigReampUiPerfSmokeTests` asserts the cells hold
  still through idle / re-amping / recording and fit their widest value, and
  that the panel prints its no-engine reason. Pure state: `RigReampViewModelTests`
  (drives `Tick` by hand; `ManualTick` keeps the default gate off the dispatcher).
