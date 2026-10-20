# `Audio/` — the playback service, its gates and the engine config (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md`. **Read this before
touching `JuceAudioPlaybackService`, the EQ, the engine-config path or anything
that takes `_gate`.** The lock rules here are structural: they are linted by
`PlaybackGateHoldGuardTests` and `EngineConfigPolicyTests` precisely because
proving them at runtime would need a real device and a real multi-second decode.

See also: `JuceSharp/src/JuceSharp/README.md` (the native ABI) ·
[dsp-tone-rig.md](../dsp-tone-rig.md) (the tone rig hanging off the same engine,
including the detach-before-destroy rule and the per-player seats) ·
[live-play.md](../live-play.md) (the judged session fed from this service) ·
[stem-separation.md](../stem-separation.md) (what the stem sources are)

---

## `Audio/JuceAudioPlaybackService.cs` + audio health logging

- `Audio/Abstractions/` — `IAudioPlaybackService`, `IAudioSource`, stem/VST contracts. UI code depends only on these. `IAudioPlaybackService` extends five focused seams (`IPlaybackTransport`, `IAudioDeviceControl`, `IStemMixer`, `IVstChainHost`, `ILiveInputCapture`) — a new caller takes the seam it needs; see [../audio-engine.md](../audio-engine.md), "Focused interfaces for view models".
- `Audio/JuceAudioPlaybackService.cs` — the `IAudioPlaybackService` implementation. Owns the `JuceAudioEngine`, sources (master + stems), VST chain, MIDI scheduling, and the 4 ms position timer (drift-corrected, latency-compensated playhead).
  - **Audio health logging (`Audio/Diagnostics/AudioHealthMonitor.cs`, owned by the service, + `Audio/Diagnostics/AudioDropoutDetector.cs`):** a background 20 ms poll flags callback stalls (buffer under-runs / xruns → audible dropouts) with NO native ABI change and NO audio-thread work. It reads the render clock ({rendered frames, QPC tick} published by the callback) and measures STALENESS — how long since the callback last published — against a managed QPC now; a staleness over `max(12 ms, 3×block period)` is a stall. Logs the device config on change (INFO), a WARNING per dropout burst (worst staleness + block period), and a 30 s DEBUG heartbeat (tally + measured render rate, which catches a SUSTAINED under-run the per-poll check can miss). **Gated on the `Audio` log category** — that switch is the on/off. It's a HEURISTIC (catches stalls overlapping a poll, i.e. the ones users hear), NOT a sample-accurate counter — and the precise count this line used to describe as hypothetical ("a native atomic in the callback + one ABI getter") SHIPPED as ABI v30: `dca_xrun_stats`, read once per heartbeat and attributed by `AudioXrunAttribution` two bullets down. The 20 ms poll above is still the no-ABI-change, no-audio-thread-work half. The detector is pure/unit-tested; QPC ↔ `Stopwatch` are the same clock on Windows.
    - **The heartbeat's render-RATE half is `Audio/Diagnostics/AudioRenderRateTracker`, and "the clock could not be read" must stay a different value from "the clock read zero".** Folding them together is how the monitor came to INVENT the fault it exists to report: a heartbeat whose read failed (the engine went away between the poll and the heartbeat, or the read threw between devices) stored the window's frame count as `0`, so the next heartbeat measured the engine's whole lifetime of frames across one 30 s window — ~960 kHz on a ten-minute-old 48 kHz engine — tripped the 2 % check and printed "the audio thread is not keeping up (sustained under-run)" with nothing wrong. A frame count that went BACKWARDS is an engine REBUILD, not an under-run, and also yields no rate. The tracker is `Forget()`ten wherever `AudioDropoutDetector.Reset()` is called (no engine / category off / disposed), because a window spanning a gap in which the callback was not running always measures LOW — i.e. always reads as an under-run. Pure, so `AudioRenderRateTrackerTests` covers all of it without a device.
    - **The heartbeat's ATTRIBUTION half is `Audio/Diagnostics/AudioXrunAttribution.cs` over the engine's own ABI v30 counters, and its one job is to be actionable — which means saying which TIMEBASE each number came from and never handing out advice the numbers do not carry.** Three timebases meet in one log line: `Window` is a DELTA over the 30 s heartbeat, `XrunStats.Load` is ONE callback sampled when the heartbeat fired (possibly 30 s after everything the window describes), and `XrunStats.MaxCallbackNs` is a HIGH-WATER mark since the counters were last reset. `Describe` labels them `This window:` … `Sampled now: …(a high-water mark, not this window)` for exactly that reason: printed unlabelled they read as a contradiction, which is how the owner got "16 of our own callbacks overran … OUR audio processing is too heavy … last callback used 11% of its block budget" in a single sentence during plain playback. Rules: **(1) `Window.Cause` answers WHICH callbacks were responsible, `Window.OverrunsAreSustained` answers whether that is bad enough to act on, and only the SECOND may unlock "raise the buffer size or remove DSP/VST gear"** — the floor is `SustainedOverrunRate` = 1 % of the window's callbacks, because removing gear lowers the STEADY per-block cost and a steady cost that matters overruns most blocks, not 16 of 11,250 (0.14 %, the reported case). **(2) An overrun is WALL CLOCK across the callback** (`recordCallbackTiming` in `JuceSharp/src/JuceSharp.Native/src/AudioEngine.h`), so an OS preemption inside our callback is counted as our overrun AND delays the next callback — the exact one-for-one ratio the attribution keys on. It means "our callback ran long", never "our DSP is expensive", and the wording must keep saying so. **(3) `MaxCallbackNs` is deliberately NOT in `Window`**: the difference between two high-water marks is not the window's peak. **(4) Near misses print on every verdict** — they used to print only when nothing happened, i.e. they were suppressed on the two verdicts that claim the rig is near the edge. Pure, so `AudioXrunAttributionTests` covers all of it without a device.

### Preloaded audio: a SOURCE costs its whole decoded length in RAM

Since JuceSharp `3db99e7` (v0.48) `TransportSource::wireReaderToTransport` decodes the
whole file into RAM and plays it from there whenever it fits the engine's budget — the
streaming `BufferingAudioSource` path holds ONE lock across the decode that the audio
callback also takes, which is what produced 22 deadline overruns in 11,030 callbacks with
the rig at 3–5 % of its block budget. Since #411 the decoded song is held as **16-bit PCM
with one per-source scale** (`Int16PreloadSource.h`), at the DEVICE rate, converted to
float in the callback; it was a `juce::AudioBuffer<float>` behind `juce::MemoryAudioSource`
before, twice the size. Consequences that bite the SERVICE layer:

- **A source is not cheap.** 16-bit stereo at 48 kHz is ~11 MB a minute (float32 was
  ~22), so a 5-minute song or stem is ~55 MB and a 6-stem chart plus its song parks
  ~385 MB while it is open (~770 MB before #411). That is the app's resting audio cost,
  and it is intentional. Measured over the real wrapper on the null device, 240 s stereo
  44.1 kHz WAVs: **87.9 MB → 43.9 MB** per preloaded source (the engine's own
  `preloaded into RAM` log line). The precision contract: a 16-bit file at the device
  rate round-trips bit-exact; anything rate-converted or deeper than 16 bits is rounded
  to the nearest step (≤ −96 dBFS); a source with overs gets a coarser step, never a
  clip. Pinned natively by `RtPreloadedSourceHoldsSixteenBitPcm`.
- **A load still PEAKS at float size.** The reader and the offline resampler work in
  float; the float buffer is freed as soon as it is quantised, so a load briefly needs
  ~3× what the source then holds. The budget is charged what is HELD.
- **A library song's file bytes never touch the managed heap.**
  `ProjectSessionService.LoadAudioFile(preferMemoryPlayback: true)` reads the cached WAV
  into a `NativeFileBuffer` (`DotCharter.Tone/Audio/NativeFileBuffer.cs`, native memory),
  hands `.Memory` to `LoadAudioFromMemoryAsync` — which does NOT copy it any more — and
  releases it in a `finally` the moment the engine's open returns. Before #411 this was
  `File.ReadAllBytesAsync` plus a `ToArray()` copy: two whole-file arrays on the large
  object heap per song (2 × 40 MB for the 240 s fixture), which the GC returns to the OS
  only much later. The waveform pass reads the FILE (`GenerateWaveformAsync`), not the
  blob. Pinned by `Services/Session/LibrarySongBytesReleaseTests`,
  `Services/Audio/NativeFileBufferTests` and the source lint
  `Services/Audio/SongMemoryLoadFootprintTests`.
- **The budget is engine-WIDE and one-shot.** `AudioEngine::preloadBudgetBytes` is
  `clamp(physicalRAM / 4, 256 MB, 4 GB)`; `reservePreloadBytes` refuses anything that no
  longer fits, and a source that is refused streams **for the rest of its life** — nothing
  re-preloads it when the budget frees up. So **anything that swaps a set of sources must
  release the OUTGOING set before it opens the incoming one**, or the incoming sources
  compete with corpses for the budget. `SwapMasterSource` always did this;
  `LoadStemsCore` did NOT until 0.48, which doubled the peak of a project switch
  (measured headless, 7 sources of 60.6 MB: 965.7 MB vs 492.7 MB) and, with two
  34-source sets, permanently demoted one incoming source to streaming. Pinned by
  `Services/Audio/StemSwapReleaseOrderTests`.
- **A stem set opens through `Audio/StemSetOpener`, never a loop of `OpenFile` calls
  (#203).** One after another, six 64 MB stems kept the mixer silent for 3.6 s after the
  project opened (read + decode + 44.1→48 kHz resample, ~600 ms a stem on a cold
  spinning disk). The opener READS the files in order on the calling thread (1 MB
  sequential reads into the OS cache) and runs up to three native opens at once behind
  the reader. **Never make the reads parallel too:** the native WAV reader pulls a few KB
  per read, and three cold 105 MB stems opened concurrently on a 7200 rpm disk took
  10.4 s against 2.1 s pipelined. Concurrent `dca_source_open_file` calls on ONE engine
  are safe — the preload budget is a CAS, the polyphase kernel cache and the source lists
  are mutex-guarded. Measured per step by the `Stem load:` line (Audio + Performance);
  pinned by `Services/Audio/StemSetOpenerTests`.

Measured 2026-08-28 with a standalone JUCE harness (not in the repo — a console app over
the `JuceSharp` wrapper, WASAPI, generated WAVs): **there is no leak on this path.** Six
open/dispose cycles of a 7-source set returned private bytes to ~15.8 MB every time and
the process ended at 10.7 MB; the managed heap sat at 0.1 MB throughout. A working set
that stays high after a switch is the PEAK above, not a retained source — and the growth
that survives a session is managed, in the UI layer, not here.

**Those two UI-layer leaks are FIXED — do not carry this note forward as if they were
open.** `TrackContextRegistry` and `StemWaveformCoordinator` both evict on a project
switch as of 2026-08-28, each pinned by its own test; the glyph atlases joined them on
2026-08-29. The list of what a project switch must forget, and the WeakReference rule
that proves it, lives in
[../ui/project-lifecycle.md](../../ui/project-lifecycle.md) § *A project switch must FORGET
the project it closed* — read that rather than this paragraph, which is only here to say
the native side is clean.

