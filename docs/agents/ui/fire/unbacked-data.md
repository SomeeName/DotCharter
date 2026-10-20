# Fire — data the mockups show that the app does NOT have

Part of the Fire design system. Router: [../fire-design-system.md](../fire-design-system.md).

## Data the mockups show that the app does NOT have

**A number nothing computes is a lie that survives review.** Each of these was
checked against a real service; none of them may be hard-coded to make a screen
look finished. Sources: `DotCharter.Services/Gameplay/{Stats,Profiles,Leaderboards}`, `ViewModels/PlayMode/PlayModeViewModel*.cs`.

> **⚠ This file rots in the most dangerous direction, so re-check a row before
> you act on it.** Every row is a claim that something is MISSING, and the
> project's whole trajectory is to build those things — so a row goes stale by
> the feature SHIPPING, and then reads as permission to delete it. A 2026-08-08
> sweep found six rows saying "ABSENT"/"never tallied"/"does not exist" about
> code that had shipped up to two releases earlier (the bend/chord/tremolo
> tallies, the trend range switch, the coaching sentence, held speed, section
> bar numbers, per-attempt history), plus **five subjects listed TWICE with
> opposite verdicts** because the superseding row was appended instead of
> replacing the old one. **Edit the row in place; never append a second one.**
> `DocReferenceLintTests` cannot catch any of this — an "ABSENT" claim names no
> file and no test.

| Mockup shows | Reality | Where it appears |
|---|---|---|
| profile **LEVEL 14** | **STILL ABSENT, and deliberately not invented (0.48).** `PlayerProfile` has `Id/Name/AccentColor/Avatar/LeftyFlip/CreatedUtc/LastUsedUtc` and no XP, level or rank — the concept does not exist anywhere in the repo. The launch screen's profile rows print the real per-profile facts instead (`ProfileOption.Detail` = last used + left-handed). **If levels are wanted, they are a FEATURE decision, not a label** — pinned by `UI/LaunchScreenTests.NoLevelConceptWasInvented`, which fails if any `Level`/`Xp`/`Rank` member appears on `PlayerProfile` or any such caption reaches the screen. | Main Menu Sidebar, Launch Screen |
| launch footer **`BUILD 2026.07`** | ABSENT. `<Version>` in `DotCharter.UI.csproj` is the only version input and the build is deterministic, so there is no date and no SHA to print — `FrontDoorFacts.ResolveAppVersion` even trims the `+metadata` slot because nothing fills it. **Cut**; the footer prints the real version alone. | Launch Screen |
| launch footer **What's new** | ABSENT. The app ships no changelog (`tools/release/New-Release.ps1` packages `LICENSE` and `THIRD-PARTY-NOTICES.md` only) and release notes exist only inside an update prompt. **Replaced** with the app's real version surface: About, on the existing `ShowAboutCommand`. | Launch Screen |
| **GUITAR CONNECTED** | **REAL since 0.48, as exactly what it can honestly mean.** `LaunchFacts.InputStateFor` reads `IAudioPlaybackService.InputSampleRate > 0` — an OPEN capture stream, which is the nearest thing the engine has to "a guitar is plugged in" — and it is a THREE-state answer: no engine at all, engine up with nothing capturing, or connected. The paired figure is the MEASURED output latency (falling back to the driver's estimate), and with no engine there is no number: the chip prints its state. It does NOT claim a signal is present; that is the tuner's `VirtualStrobeReading.HasSignal` and is still not on a menu view model. | Main Menu Sidebar, Launch Screen |
| chart editor **"2 drafts, 1 ready"** | STILL ABSENT as written — there is no drafts registry and no "ready" state. **The row carries a real figure instead (0.47): how many of your OWN charts the library holds**, the same own-chart set `RebuildFromBrowser` assembles. | Main Menu Sidebar |
| **TUNE TO DROP D** | **REAL since 0.47** for the SONG LIBRARY's detail rail, where there IS a selected song: its tuning against `AppConfig.LastVerifiedTuning` (what the pre-play check already trusts). The front door's footer prints that verified tuning; neither claims a live reading. | Main Menu Sidebar, Song Library |
| **GUITAR · 12 ms** on Get Ready | **PARTIAL, and the chip says so.** `IAudioPlaybackService.InputSampleRate > 0` means an input DEVICE is open — nothing in the stack can know a guitar is plugged into it — so the chip reads "INPUT", and the tuner's own readings are what prove a guitar is there. The latency beside it is the engine's real `MeasuredOutputLatencyMs`. | Get Ready |
| Get Ready's **"Separate stems… (~40 s)"** | **REAL since §6.6, by MEASUREMENT.** Separation cost is dominated by the machine and the model and no constant is honest, so every successful run records seconds-of-work per second-of-audio (`StemSeparationCost`, blended 50/50, outliers discarded) and the estimate is that rate times this song's length. **Before the first run there is no rate and the screen shows no estimate** — the one place on this screen where the answer is "nothing yet". | Get Ready |
| per-arrangement **note counts** (lead 982 / rhythm 1,140 / bass 604) | **REAL since 0.47** — the manifest's `NotesHard`, which Rocksmith writes into the `.hsan` header the scan ALREADY reads (`AttributesCreation.initSongCommon` fills it for header and non-header alike). Carried on `LibraryArrangementEntry.NoteCount`; library index schema bumped to **v7** so existing caches re-read it. It is a FLOAT in the JSON — read it with the integer helper and every real package silently reports 0. | Song Library |
| song **year** | **REAL since 0.47** — surfaced as `PlaySongItem.Year` / `DetailSubtitle` ("3 Doors Down · 2002"). | Song Library |
| rig footer **`CPU 14%`** and the **OUT** meter | **CPU: REAL since ABI v46 (JuceSharp #29).** `dca_engine_get_callback_load` exports the audio callback's load measured INSIDE the callback (an average over ~0.5 s and a peak decaying over ~1.5 s, as fractions of the buffer period); the footer's `RigCpuStrip` prints the average, the peak in its tooltip, amber past 80 % peak (`RigCpuLoadViewModel`, read at 4 Hz off the view's meter clock). With no engine it prints a dash and says why; a host with no engine reader draws no readout (`Perf/RigFooterStabilityUiPerfSmokeTests.WithoutAnEngineReader_TheCpuReadoutIsNotDrawn`). It is the cost of the whole render, not a process CPU figure. **The master OUT meter is still ABSENT and DROPPED (§6.8):** the ABI's only level readings are `dca_engine_get_input_peak` and the per-node DSP meters, so the footer's OUTPUT meter is the last unit's output, never a master; `AmpFxRigScreenTests.MetersReadRealInputAndSelectedModuleTelemetry` pins what the meters read. The IN meter and its CLIP lamp ARE real (clip is peak ≥ 0 dBFS). | Amp & FX rig |
| rig **SCENE A / B / C / D** | PARTIAL — the rig has **two** compare slots (`ToneCompareSlots`), not four, so the strip prints A and B. Two more would be two more snapshot buffers, i.e. a feature. | Amp & FX rig |
| rig per-unit **level meter** on each card footer | **The data is REAL, the card-footer meter is not drawn.** Since ABI v30 the engine meters every node (`dca_engine_dsp_get_node_meter`, the bulk `dca_engine_dsp_read_node_meters`: input/output peak and RMS, off until `dca_engine_dsp_set_metering`). The rig shows them for the SELECTED module through `RigNodeMetersViewModel` (amp face, cabinet editor, routing console; polled only while a rig host is visible). The card footer keeps the LED + ACTIVE/BYPASSED. | Amp & FX rig |
| gear-row blurbs (**"Optical"**, **"Soft clip"**, **"Pitch widener"**) | ABSENT — copy the mockup wrote for seventeen units, which would be a second hand-written catalogue for fifty-seven and exactly the §7 fault. **Replaced** with the module's real control count (`Rig_Gear_Controls`); the category was rejected because the rows are already grouped by it. | Amp & FX rig |
| song library **count**, Random, Free play, Shuffle, ENTER hint, `v0.9.4 · E STANDARD · 12 ms` | ABSENT — no backing command or value. | Main Menu Sidebar |
| artist **group headers** ("3 DOORS DOWN · 9 SONGS") | **REAL since 0.47** — `SongArtistGrouping` builds real `SongArtistGroup` runs (published as `SongGroups`) and STAMPS each run's header onto its first row. The list stays FLAT on purpose: one `ListBox`, one selection model, one virtualized panel. Headers only in the artist sort. | Song Library |
| per-song **accuracy**, **mastery** and a formatted **"4 days ago"** | **REAL since 0.47** — `ApplyStats` was dropping `SongPlayStats.BestAccuracy` and `.MasteryPercent` on the floor; both are now on the row, and the relative time goes through the SAME `RelativeTimeText.Format` the profile switcher uses. Mastery is 0..100, so the meter divides. | Song Library |

Real and already computed, so safe to bind: per-song mastery / best accuracy /
best score / grade / play count / last played (`SongPlayStats`), UNPLAYED
(`!HasBest`, already a filter), most-played ranking, the newest run
(`IGameplayStatsService.GetMostRecentSession`), output latency, tuning targets,
and all three leaderboard tiers. (The Session History and Practice Trends rows
of this table went with those screens on 2026-09-19 — restore from tag
`pre-cut-2026-09`; so did the session-history, weak-spot and per-section-trend
read-models they described.)

