# `Gameplay/` — profiles, scoring, stats, leaderboards, practice coach (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md`. **Read this before touching profiles, gameplay stats, the local leaderboard, the crowd meter, the practice-coach read-models or the count-in.** Everything here is pure with an injected clock — keep it that way.

---

## Two rules for the whole folder — read these first

- **`player-profiles.json` is the only map from `profiles/<id>/` back to a
  person, so it is NEVER written over on a failed READ.** `PlayerProfileService`
  now tells the three outcomes apart exactly the way
  `PracticeProfileService` does for the scores: **ABSENT** is a new install
  (fresh default, written); **CORRUPT** (`JsonException`) is moved aside as
  `player-profiles.corrupt-<stamp>.json` before a fresh registry is written, and
  if the move fails nothing is written at all; **UNREADABLE** (an
  `IOException`/`UnauthorizedAccessException` from an AV scan, cloud sync or
  backup tool) serves a stand-in profile at the constant
  `PlayerProfileService.DegradedProfileId`, is deliberately NOT cached so the
  next call retries and a transient lock heals mid-session, and makes
  `SaveLocked` refuse every write (`ImportProfile` refuses up front rather than
  extracting a data folder no registry can name). `IsDegraded` exposes the
  state. **The bug this replaced was silent and total**: the old code swallowed
  every exception, minted one fresh "Player 1" with a NEW guid and saved it over
  the file on the first property read — nothing was deleted and every profile's
  scores, best runs, session history, goals and practice plan were orphaned at
  once, in folders whose only name-map had just been destroyed. Pinned by
  `PlayerProfileServiceTests.UnreadableStore_IsNeverWrittenOver_AndHealsWhenTheLockLifts`
  and `.CorruptStore_IsQuarantinedInsteadOfOverwritten`. The stand-in is
  deliberately NOT `IsDefault`: the practice store's one-shot 0.10 migration
  MOVES the pre-profile score file into the DEFAULT profile's folder, and a
  session that cannot read the registry must not consume it.
- **…and a degraded read is not just "do not WRITE" — it is "do not DELETE on
  the strength of it" (0.61).** `PracticeProfileService` already told the three
  outcomes apart, but the unreadable case still handed the caller an EMPTY
  dictionary, and `LiveTakeStore.CleanupAsync` reads that dictionary to decide
  which take files on disk are still referenced. Against an empty snapshot every
  take is an orphan — **the star is no protection**, because `SelectDeletions`
  consults the pinned set only in the cap-eviction pass, never in the orphan
  pass — and the sweep runs after every run and on every take delete. One locked
  store file wiped the player's whole listen-back library, keepers included,
  behind a single logged warning. Two independent guards now:
  `IPracticeProfileService.IsStoreAuthoritative` (reading it performs the load,
  so it answers about the store the next call will see) makes `CleanupAsync`
  return early and say so; and `SelectDeletions` refuses to delete when the
  reference set is EMPTY and files are present, because "nothing is referenced"
  and "the store could not be read" are indistinguishable there. Cost when the
  store really is empty: the orphans wait for the first sweep that sees one
  referenced take. Pinned by
  `LiveTakeStoreTests.Cleanup_DegradedPracticeStore_DeletesNothing_AndSaysSo`
  (only the early return logs, so it fails on its own) and
  `.SelectDeletions_NothingReferenced_ButFilesPresent_DeletesNothing`.
  **A new consumer of `GetAll()` that deletes, prunes or evicts asks
  `IsStoreAuthoritative` first.**
- **An own chart's song key is its PROJECT PATH, so a path change must move the
  stores keyed by it** (`LivePlayViewModel.ProfileSongKey`;
  `PlaySongItem.SongIdentity`). The `.rscp` to `.dcproj` migration does it
  through `IProjectPathRekey` (`ProjectPathRekey`, called by
  `ProjectSaver.SaveToPickedPathAsync` right after the write, and only for the
  same-named `.dcproj`). Path-keyed stores and what happens to each:
  practice stats, bests and leaderboards (`practice-profiles.json` of EVERY
  profile; `PracticeSongRekey` moves and merges), setlist entries
  (`setlists.json` of every profile; `PlaySetlistViewModel` re-reads on
  `Rekeyed`), `AppConfig.SongTonePreferences` and `DropTunerSongs`: all moved.
  Checkpoints and tone edits are copied by the Save As tail the migration shares
  (`CarryCheckpointsAsync`, `SongToneEditKeyFollow`); AI chats are keyed by
  `ChartProject.ProjectId` and carried by `AiChatSaveAsCarry`. Replay and take
  files are named from the session record, which moves with its entry. The stem
  and waveform caches of an own chart key on the audio CONTENT signature
  (`StemCacheIdentity`), never on the project path. A NEW store keyed by a project path joins `ProjectPathRekey`
  in the same change. Pinned by `ProjectPathRekeyTests` and
  `ProjectFileExtensionSessionTests`.
- **A stored `BestAccuracy` of 0 means UNMEASURED, and no readout may grade it
  (0.58).** `PracticeProfileService.Record` declines to raise the stored best for
  a run under `MinCoverageForBestAccuracy`, so a player whose runs are all
  section drills or mid-song quits keeps `BestAccuracy == 0` with
  `Sessions.Count > 0` — and `GradeCalculator.FromAccuracy(0)` is `F`. The song
  row, the local leaderboard, the session-history header and the stats page's
  grade histogram (the last two since removed) each printed that **F** (and `0.0%`) beside a real score, while
  `PlayModeViewModel.GetReady`, gating on `stats is { BestAccuracy: > 0 }` over
  the SAME store, showed nothing. It is the `judged == 0 ? 1.0` lie with the sign
  flipped, and it is now ONE predicate: **`AccuracyMath.IsMeasuredBest`**, carried
  on the record (`SongPlayStats.HasBestAccuracy`/`GradeLabel`,
  `LeaderboardEntry.HasBestAccuracy`/`GradeLabel`,
  `ProfileStatistics.HasBestAccuracy`) rather than re-derived per readout.
  **Bind the `*GradeLabel`, never the raw `Grade`** — the enum still holds the
  letter for ORDERING (`LibraryQuery`'s min-grade record filter needs an order, and a
  song with no grade correctly fails it). The unmeasured placeholder is
  `AccuracyMath.UnmeasuredLabel` (the em dash the results screen and the
  since-removed take history and co-op HUD used). One ambiguity is deliberate and unrepresentable otherwise: a
  qualifying run that genuinely scored 0% stores 0 and reads as unmeasured.
  Pinned by `Services/Gameplay/UnmeasuredBestAccuracyTests`.
- **`Gameplay/` has a type- and verb-level reachability lint** — `Services/Gameplay/GameplayReachabilityTests`. It sweeps
  the three DI-bound interfaces (`IPlayerProfileService`, `IGameplayStatsService`,
  `ILeaderboardService`; the list is asserted COMPLETE against the folder's
  declarations) and requires each one — **and every member on it** — to be named
  by product code outside `Gameplay/` and outside the `App*.cs` composition root.
  It found three verbs that were declared, implemented, unit-tested, registered
  and driven by nothing, all removed rather than left reading as shipped:
  `ILeaderboardService.GetSongLeaderboardAnyArrangement`,
  `IPracticePlanService.UpdateManualEntry` (the plan service itself went on
  2026-09-19) and `IPlayerProfileService.ActiveDataDirectory`. **A future miss has two honest
  fixes: wire the verb up, or delete it — never an exclusion list.**

---

- `Gameplay/` — `Profiles/` (`PlayerProfileService`, per-profile data dirs),
  `Scoring/` (`GradeCalculator`), `Stats/` (`GameplayStatsService` —
  `SongPlayStats` carries the FC badge flag and the computed 1–10
  `Difficulty` stamped from `ChartDifficultyEstimator` when a run records;
  null = never rated; `GetMostRecentSession` is the front door's one-record
  newest-run read; `RelativeTime.Between` buckets dates for the UI to
  localize. The `SessionHistory` read-model and `GetSessionHistory` went with
  the History screen on 2026-09-19 — restore from tag `pre-cut-2026-09`),
  `Leaderboards/` (local `LeaderboardService` — one board per song+ARRANGEMENT
  and per DETECTOR (#329): a row is the profile's best run with that detector
  (`PracticeArrangementProfile.DetectorBests`), because the CQT verifier and the
  classic path hear differently and their scores do not compare; the results screen
  asks for its own run's detector. A best of UNKNOWN detector (every store from
  before 0.64.0, when a CQT run paid every hit the untimed 100) is on no
  detector's board — `LiveDetectorMode.Unknown` asks for the old all-runs board.
  `Services/Gameplay/LeaderboardServiceTests`. Read straight off every profile's own store file via
  `PracticeProfileService.ReadStoreFile` so the active profile's live store is
  never disturbed), `Spectator/` (`CrowdMeter` —
  pure 0..1 room-enthusiasm curve for the 0.17 spectator UI + 3D crowd:
  exponential decay toward neutral, streak-scaled hit kicks, misses cost more;
  time always injected, `CombineRoom` is the pure multi-player combiner),
  and **no `Practice/` folder any more.** The per-session
  `PracticeSessionRecord.Techniques` and per-section tallies are still
  captured (the per-section ones feed the results screen's breakdown). **Removed on 2026-09-19** (restore
  from tag `pre-cut-2026-09`): the SM-2-lite "Today's Practice" queue
  (`SpacedRepetitionScheduler`, `PracticeQueue`, `IPracticeQueueService`),
  `TechniqueReportCard`, the calendar and goals (`PracticeCalendar`,
  `PracticeGoalsService`), the adaptive practice plan (`PracticePlanGenerator`,
  `PracticePlanService`, `PracticeSkillEstimator`, `PracticePillarMix`), and —
  with the History and Trends screens — practice coach II: `WeakSpotHeatmap`,
  `SmartLoopPlanner`, `PracticeTime`, `PracticeTrends` and `ProgressInsights`.
  Old `practice-goals.json` / `practice-plan.json` files in a profile folder are
  simply never read, and stored takes with their library metadata stay on disk
  untouched — `Services/Gameplay/RemovedPracticeDataCompatibilityTests` pins
  that such a folder still loads its profile, bests and newest run.
  **Rock meter (0.24 performance stakes):** `RockMeter` (`Gameplay/RockMeter.cs`) —
  pure 0..1 stage-energy curve behind the opt-in Play Mode fail-out. Hits raise
  a little (streak-scaled, saturating at 25); misses drain double and CONSECUTIVE
  misses escalate the drain ×1.0→×2.5 (sustained silence collapses a run in
  ~9–10 passing notes); empty chart stretches drift slowly UP but never past
  `RecoveryCeiling` (= the 0.5 start energy) and a healthy meter neither decays
  nor drifts — only hits reach 1.0. Below `FailThreshold` (0.05) sustained for
  `FailDebounceMs` (900 ms) latches `HasFailed` and raises `Failed` exactly once;
  a failed meter is inert. Time is injected (transport ms — a paused transport
  stops the clock; backwards time, e.g. the resume rewind, is clamped for both
  drift AND the debounce). All constants documented on the class; curves,
  escalation, debounce and determinism are unit-tested (`RockMeterTests`).
  **Wiring:** held by `LivePlayMetersViewModel` (`LivePlayViewModel.Meters`) and
  fed by `LivePlayViewModel` — it owns the single judgment
  funnel (`OnJudged`, silent-stretch misses included) and the 30 Hz session
  tick; NEVER fed from the render thread's drained `FeedbackEvents` copy.
  **The app must listen to `Failed`, not to `Tick`'s return value** — the meter
  latches from EITHER entry point, and `OnJudged` discards what `OnVerdict`
  did. `ArmRockMeter` subscribes and `DisarmRockMeter` unsubscribes; both that
  and sudden death post through the one-shot `PostRunFailed`. The bug this
  replaced: `PumpRockMeter`'s `Tick` was the only detector, so a debounce
  crossed inside a verdict — reachable, because a verdict's `DetectedAtMs` is a
  RAW detection stamp while the pump's playhead is latency-compensated and
  trails it — raised `Failed` into the void and made every later `Tick` return
  false forever (a failed meter is inert). The run hung UNFAILABLE at zero
  energy, never reaching `PlayModeScreen.Failed`
  (`UI/ViewModels/Editor/LivePlayRunFailRoutingTests`).
  Armed per run ONLY by `PlayModeViewModel.BeginGameplay` behind
  `AppConfig.GameplayRockMeterEnabled` (default OFF; mirrored toggle in the
  gameplay-settings overlay + Settings → General → Gameplay). Hard exclusions
  regardless of the flag: LAN sync runs (`_isSyncRun`) never arm, the Riff
  Repeater disarms mid-run, and editor practice / free play / listen-back
  never arm at all.
  **Gameplay count-in (0.14.4 C):** `CountInPlan` is the pure schedule for the
  pre-gameplay metronome count-in (`AppConfig.GameplayCountInBeats` off/4/8) —
  click spacing = start tempo scaled by practice speed, measure downbeats
  accented; the UI (`PlayModeViewModel.RunCountInThenStartAsync`) just awaits
  each click and plays the native metronome click, then starts the transport.
  **The count-in tempo is `CountInPlan.ResolveGridTempo(grid, runStartMs,
  fallback)` — never raw `grid[0].Bpm` (v0.24 fix).** Many CDLC imports park an
  isolated beat 0 at t=0 whose one-beat "measure" spans the intro silence, so
  its per-beat Bpm (SNG delta to beat 1) encodes that gap: 11.45 "bpm" from a
  5240 ms gap (Ice Nine Kills – Funeral Derangements) put ~21 s between session
  start and transport start; worst library case 65 s (Animals as Leaders –
  Monomyth, 3.68). The resolver starts at the beat covering the run start and
  takes the first sane per-beat Bpm (`MinSaneGridBpm` 30 ≤ bpm < 1000), which
  also fixes the meter (that beat 0 has `BeatsInMeasure=1`, which accented every
  click). 2026-07-17 library scan: 46/1824 CDLCs over 5 s. `GameplayStartPlan`
  itself was exonerated — `runStart` was 0 for every affected chart.
  The gameplay metronome itself (`AppConfig.GameplayMetronomeEnabled`) reuses the
  existing `AudioPlaybackCoordinator` beat-crossing tick — no service change.
  **Pre-first-note count-in (0.24.x):** `FirstNoteCountInPlan` is the sibling pure
  plan for a SECOND count-in — the N beats immediately BEFORE the first expected
  sound — so a long noteless intro no longer dumps the player into silence then
  unannounced notes. `Build(beatTimesMs, firstNoteMs, runStartMs, beats,
  beatsPerMeasure)` reads the tempo map's ACTUAL beat times (tempo-change-safe by
  construction — no BPM re-derivation), gates on the first note sitting ≥ N +
  `ClearanceBeats` (2) beats past the run start (else the run-start pre-roll
  already covered it → empty), and accents mirror `CountInPlan.IsAccent` (first
  beat + measure downbeats). It is only a PLAN: the native engine drops any click
  scheduled > ~5 s ahead of its render clock, so `LivePlayViewModel` schedules
  each click over the render clock (`TryScheduleClick`) as the playhead comes
  within `FirstNoteCountInSchedule.LookaheadMs` (500 ms) on its 30 Hz tick —
  re-arming on a seek (the resume rewind clears the engine's scheduled clicks).
  WHICH click is due is the pure `FirstNoteCountInSchedule` (`Gameplay/`,
  `RearmAfterSeek` then `TryTakeDue` until false; stale > 250 ms behind is
  skipped); the view model only reads `RawPlayheadMs` and schedules what it is
  handed, under the session gate. Armed per run by
  `PlayModeViewModel.BeginGameplay` (skipped on LAN sync runs), disarmed on every
  run exit. Unit-tested in `FirstNoteCountInPlanTests` and `FirstNoteCountInScheduleTests`.

---

