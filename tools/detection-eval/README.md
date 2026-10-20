# tools/detection-eval

The Live Play **note-detection accuracy eval**: how many correctly played notes the
game actually credits, and how many wrong or unplayed ones it credits by mistake.

`DetectionEval.cs` is a .NET 10 file-based program. It references
`DotCharter.Services` and runs the production types — `GuitarInputAnalyzer` →
`LivePlaySessionService.Offer` for the classic path, `LiveCqtVerifier` for the CQT
path, the session's own `HandleTick`/`AdvanceTo` judging. The one piece it
re-states is the tick loop (`Runner.Run`, 1,600-sample ticks), which mirrors
`LivePlayViewModel.TickCore` in `DotCharter.UI/ViewModels/Editor/LivePlayViewModel.Tick.cs`.
**Change one, change the other.** Offline, stream time maps 1:1 onto chart time
(speed 1, zero latency); the live app gets that from the latency calibration.

## Two sources of truth

1. **Real DI — Guitar-TECHS** (CC-BY-4.0): direct-input recordings of chords,
   scales, single notes, techniques and 12 short songs, each with hex-pickup MIDI
   (one track per string) as ground truth. On the owner's machine the zips are in
   `DotCharter-local/datasets/rig/_raw/guitar-techs/`; extract only the DI and MIDI
   (the video is most of the 4 GB):

   ```bash
   cd DotCharter-local/datasets/rig/_raw/guitar-techs
   for z in P1_chords P1_singlenotes P1_techniques P1_scales P2_chords P2_singlenotes P2_techniques P2_scales P3_music; do
     unzip -q -o $z.zip "$z/midi/*.mid" "$z/audio/directinput/*" -d _extracted; done
   ```

   The MIDI converter emits ghost notes (a few ms long, velocity ~10); `GroundTruth.Clean`
   drops anything under 60 ms or velocity 25. Its onsets are offset from the DI by
   20–110 ms per file, so each file is aligned by the median lag against the production
   onset detector (`Align.EstimateLagMs`) — the same job the latency calibration does for
   a player. Notes that start within 60 ms on different strings become one chord.

2. **Real charts, rendered** (`--charts <dir>`): every `*RS2.xml` under the directory
   (duplicates by content skipped) is loaded, the matcher's own expected sounds are read
   back, and `Synth` renders exactly those: Karplus-Strong strings with an allpass for
   exact pitch, strummed chords, slides and bends as glides, palm mutes damped, full
   mutes as noise. This is what covers bass, B standard, drop tunings and dense riffs.
   It is a synth, not a guitar — use it for RELATIVE comparisons and for the registers
   the DI set does not have.

## Running

From the repo root:

```bash
dotnet run tools/detection-eval/DetectionEval.cs -- --modes classic,cqt --dop 10 > gt.txt
dotnet run tools/detection-eval/DetectionEval.cs -- --charts ../DotCharter-local/TestData/EOF --modes cqt --dop 10
```

| Option | Meaning |
|---|---|
| `--modes classic,cqt,cqtx,union` | classic = the pitch-candidate matcher; cqt = `LiveCqtVerifier` as shipped; cqtx = the CQT with every knob below (defaults = shipping, so `cqt` and `cqtx` must agree); union = a sound counts when classic OR cqt credited it (list both first) |
| `--quick` | the first file of every Guitar-TECHS set / every 8th chart — a ~5-minute spread for iterating |
| `--filter <text>` | only cases whose name/path contains it (`P3_music`, `Sophie/Rhythm`, `bass`) |
| `--audio real\|idle\|halfidle\|gainjump` | real audio; hiss + hum only; real for the first half then idle (reports `idle-part` separately); idle with a +12 dB gain step half way |
| `--wrong 1` / `--wrong oct` | every charted note a semitone off what is played / an octave ABOVE it (the player plays everything an octave down) — false-accept rates |
| `--strum <ms>` | notes on different strings starting within this many ms are one chord (default 60) |
| `--gt <dir>` | the Guitar-TECHS extract (or `DOTCHARTER_GUITARTECHS_ROOT`) |
| `--skew <ms>` | shift the chart against the audio (a mis-calibrated latency) |
| `--synthgt 1` | render the Guitar-TECHS charts with the synth instead of the DI (calibrates the synth against real audio) |
| `--csv <file>` | one row per judged sound; Guitar-TECHS rows add the ground truth's quietest velocity, shortest note and neighbour gaps (to tell a detector miss from answer-key noise) |
| `--forgiving` | run as the Lenient setting (the forgiving CQT, Lenient hit windows) |
| `--noearly` | credit CQT hits only when the window closes (no `ChartVerifyEarly`); every run prints how long after the note hits were credited (median / p90) |
| `--noprovisional` | do not pump the analyzer's provisional onsets: the timing cue waits for the finalized onset, as before. Every run prints when the cue and the finalized grade landed after the note, each grade's stage (`Final`/`Provisional`/`Confirmed`/`Corrected`) and the tier counters — which must be identical with and without |
| `--why 1` | (cqtx) for every miss, print the strongest pitches the CQT hears there and the presence ratio |
| `--techniques` | the contour judge on: how often a credited bend, slide or vibrato is ruled met / not met / unknown. Guitar-TECHS: `Bendings` and `Vibrato` played (a hex-MIDI run of notes stepping on one string is charted back as one bent note); `singlenotes` and `scales` charted with a full-step bend, a vibrato and a two-fret slide the player did NOT play. With `--charts`: real charts rendered by `TechniqueSynth` with and without their techniques (synthetic — relative only). Results: `docs/agents/services/live-play.md` |
| cqtx knobs | `--minmidi --bins --post before,after --chordpost after --margin --veto --coverage --quality --kernel --relfloor --harmpeak --idlesec --idleratio --idlerise --overtones --ring --quarter` |

**It takes at most half the machine, never cores 0–3, at idle priority** (`Polite.Apply`;
launched under `start /affinity` it keeps the set it was given). A 12-thread run once
made the owner's PC unusable. Run one measurement at a time.

The analyzer runs once per file and every mode replays its events (`Runner.Detect`), so
extra modes cost only their CQT pass. The full DI set is ~5 hours of audio; `--quick` and
`--filter P3_music` (the songs, ~1 minute) are the everyday loops.

## Baseline (2026-09-29, the change that made CQT the default)

Real DI, Guitar-TECHS, 14,250 charted sounds — hit rate on correctly played notes:

| | classic | CQT before | CQT 0.57.4 |
|---|---|---|---|
| all | 59.5 % | 92.4 % | see the re-measure in `docs/agents/services/live-play.md` |
| songs (P3_music, 1,263) | 44.9 % | 61.4 % | 83.0 % |
| chords sets | 40.0 % | 93.9 % | |
| scales | 82.3 % | 97.6 % | |
| pinch harmonics | 35.2 % | 61.4 % | |

False accepts on the songs, CQT before → 0.57.4: idle input 0 % → 0 %; the player
stopping half way 3.0 % → 0.0 %; +12 dB gain while idle 21 % → 4.5 %; every note a
semitone off 10.1 % → 14.9 %.

Rendered charts, bass (19,144 sounds): CQT before 53.3 % → 0.57.4 85.5 %; B-standard
bass 12.4 % → 77.7 %; notes below E1 1.1 % → 63.7 %.

The other direction — the app WRITING a chart from a song's mix (Chart from
audio), scored against the hand-corrected `TestData/EOF` charts — has its own
baseline: onset F1 44.7 %, note F1 4.3 % at ±50 ms (2026-10-07, 89
arrangements). Method and numbers: `docs/agents/testing/autochart-eval.md`.
