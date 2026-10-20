# `Audio/StemSeparation/` + `Audio/Waveform/` — stem cache, encoder, component manager (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md`. **Read this before touching stem separation, the stem/waveform caches or the component manager.** Two invariants are non-negotiable and are repeated in the slim guide: the stem cache is NEVER regenerated, and Opus stems are playback-only — never fed to live note detection.

**The playback-only half is linted now** (`LiveDetectionAudioSourceTests`), and it was not before: nothing under `Audio/LivePlay/` may name `StemTrack`, `IStemSeparationService`, the stem cache/archive types, `HasStems`/`LoadedStemNames` or a `.opus` literal. Its only input is the live capture ring. The file set is DERIVED by walking the folder, minus `Takes/` and `Replays/` — those hold the player's OWN recorded performance and its verdict track, which are outputs of a judged run and never inputs to one, so `LiveTakeStore` writes Opus legitimately. A second test asserts both excluded folders still exist, so the exclusion cannot silently widen onto something else.

**A persisted stem path is relative to the PROJECT folder at BOTH ends (fixed
2026-09-04).** `StemSeparationViewModel`'s `RegisterAndPersistStemsAsync`
relativized against `ProjectAudioLocator.ResolveOutputDirectory` — the AUDIO's
folder, where the WAVs are actually written — while
`StemMixerViewModel.Rebuild` resolved against
`Path.GetDirectoryName(CurrentProjectPath)`. The two agree whenever the audio
sits beside the project, which is every ordinary save and the reason it survived
so long; they diverge for a rooted `AudioFileName`, a PSARC session and a chart
in a per-arrangement subfolder, and there EVERY stem silently failed to load on
the next open — minutes of separation apparently lost with the files still on
disk. The project folder wins (the convention every relative name in the `.rscp`
uses; see [parsing-psarc.md](parsing-psarc.md)), the WAVs still live beside the
audio, and `Audio/StemSeparation/StemFolderProbe.ResolveStemPath` still ACCEPTS
the old audio-relative form — a project written by any earlier build carries it,
so changing only the base would have been a data-loss change. It picks between
the two bases with `File.Exists`, so it runs inside `StemFolderProbe.Probe`, on
the stem mixer's worker and never on the UI thread (`StemFolderProbeTests`); `StemPathBaseTests` covers both bases, the precedence, the
missing-file answer, rooted paths and the persist side's base.

---

- **A `.dcsong` opened as a project brings its stems as PROJECT stems, not as a cache
  entry (#709).** `Package/DcSong/DcSongProjectUnpacker` decodes each packaged Opus
  stem to `stems/<name>.wav` beside the new project and declares it with the mixer
  state the package carried — the same shape a stem-cache hit leaves — so the
  separation verb sees existing stems and asks before replacing them. The `.dcstems`
  cache is not seeded (it would re-encode lossy Opus); the package is the durable copy.
  See `dcsong-format.md` § Opening as a project.
- **One separation verb, two doors (#641).** Tools ▸ Separate Stems and chart from audio's **Separate stems** both run `StemSeparationViewModel.SeparateAsync`, the `ISongStemSeparation` seam (`Audio/StemSeparation/ISongStemSeparation.cs`, registered as the same singleton): cache first, the download and replace prompts, the status-bar task with its cancel. A caller's token and progress ride along, and `SongStemSeparationOutcome` says why nothing was separated. `GetAvailabilityAsync` reads state only. Never add a second separation path.
- **A run outlives the screen that started it (#710).** `StemSeparationViewModel.SeparateAsync` publishes the run in flight as `ISongStemSeparation.Running` (`SongStemSeparationRun`: the resolved audio, the latest progress, `Completion`, `Cancel`) once it knows its audio, and clears it before completing it. Its token links the caller's, so only an explicit cancel (or opening another song) stops it; closing chart from audio does not, and the stems still reach the `.dcstems` cache. A screen opened mid-run follows `Running` for its file (`Separates`) rather than asking for a second run, which the in-flight guard would refuse as `AlreadyRunning` anyway.
- `Audio/StemSeparation/` — `CompositeStemSeparationService` (ONNX preferred, native demucs.cpp fallback) + `Onnx/` backend + `Cache/` durable stem cache. `StemSeparationResourcePolicy` caps worker threads (leaves 2 cores free).
  - **The composite has TWO backends and there is no third.** `App.AudioServices`
    builds `new IStemSeparationService[] { onnx, native }`: the owner wants the
    stack pure in-process C# ONNX, with the bundled demucs.cpp behind it. There is
    no Python demucs fallback and no concurrent-separation limit. The Python CLI
    wrapper, the JuceSharp orchestrator, its adapter and the two settings it read
    (`MaxConcurrent`, `EnableCpuFallback`) were never in the chain and were
    deleted in #662 (restorable from git). An old `appconfig.json` that still
    carries the two keys loads, and the next save drops them
    (`JsonConfigurationServiceTests`). JuceSharp's own
    `JuceSharp.StemSeparation.Processing.StemSeparator` is still in the
    submodule; DotCharter does not call it. **The CPU fallback that DOES exist** is
    the ONNX backend's own: `Gpu/StemExecutionDecision.CpuFallbackReason`, when
    DirectML runs out of video memory or does not attach. The device choice is
    `AppConfig.StemSeparationExecutionProvider` + `StemSeparationGpuAdapter`,
    read by `StemModelManager` and written by Settings ▸ Stem Models' provider
    and GPU rows.
  - **A CANCEL propagates; it is never a failed result** — and since ABI v36 that
    rule reaches the NATIVE backend too. `dca_stems_separate_file2`'s progress
    callback returns 0 to stop, so `JuceStemSeparationService` hands the caller's
    token to the SIX-argument `JuceStemSeparation.Separate` and rethrows
    `OperationCanceledException` above its catch-all, exactly as the ONNX backend
    does. Converting it to `Success = false` is not a cosmetic bug: the composite
    reads a failed result as "this backend declined" and starts the NEXT backend,
    so the user's Cancel began a second multi-minute run. The composite's own
    comment used to say the native loop was uncancellable and that this was
    "latent because the backend is compiled out" — that sentence is now false and
    has been rewritten. **Open dependency:** JuceSharp's own
    `JuceStemSeparation.SeparateAsync` still forwards `CancellationToken.None` to
    the five-argument overload, so a runtime cancel test against the wrapper's
    ASYNC path cannot pass until the submodule lands its fix. The parent does not
    use that path. `JuceStemSeparationServiceFaultTests` pins all of it through an
    `INativeStemSeparator` fake.
  - **A missing model is a DOWNLOAD, not an error string.** ABI v36 gave "the
    weights are not on disk" its own answer (`DCA_ERR_NOT_INITIALIZED`, and a
    `FileNotFoundException` naming `DemucsGgmlModelCatalog` from the wrapper), so
    the native backend answers it with the one sentence it says about a missing
    model — `JuceStemSeparationService.ModelMissingMessage`, which names
    Settings ▸ Stem Models first — shared with its own pre-flight check so the two
    can never disagree. The wrapper's sentence is correct advice for a programmer
    and useless to a player who has a button for it.
  - **A backend the settings configure must be IN the chain.** The deleted
    orchestrator (#662) was the "registered but never DRIVEN" shape:
    registered, injected and configured from two Settings rows, never asked to
    separate. `ServiceRegistrationReachabilityTests` cannot see that shape,
    because the type IS named by product code. Before adding a knob for a
    backend, put the backend in `App.AudioServices`' array.
- `Audio/Waveform/` — `WaveformDiskCache`: generate-once waveform pyramid cache (0.14 P4), keyed like the stem cache (identity + `StemAudioSignature`), base level only (BOTH pyramids rebuilt via `WaveformSamples.FromBasePeaks`); every failure = miss + regenerate — never precious, unlike the stem cache.
  - **The base block is `WaveformSamples.GenerationBlockMs` (2 ms since v0.35,
    was 20 ms) and the cache VALIDATES it instead of trusting the entry's own
    value.** Level 0 is the finest level the renderer's LOD picker can reach, so
    the base block IS the timeline waveform's resolution ceiling — 20 ms meant a
    20 px staircase tread at 1 px/ms zoom, which is the whole "the waveform
    looks low-res" report. Cost: ~1.2 MB of persisted peaks per 10-minute song
    (was ~120 KB), ~2× that in memory for both pyramids, unchanged generation
    time (the decode dominates). **A resolution change is only safe because of
    that validation**: the file format carries its own `blockMs`, so without it
    every warm cache would keep loading the old coarse peaks perfectly and keep
    looking wrong. A stale-resolution entry is a miss and is deleted, like a
    corrupt one. Do not replace this with "remember to bump `SchemaVersion`".
    Pinned by `WaveformDiskCacheTests.AnEntryAtTheOldCoarseResolution_*`.
  - **The index↔time contract is `i × BaseBlockMs × 2^level`, EXACTLY — so the
    generator's block boundaries are FRACTIONAL sample positions, never a
    rounded integer samples-per-block.** That multiplication is the only way any
    consumer converts an index back to a time (`WaveformLayer`'s
    `x = (i·blockMs − viewStartMs)·zoom`, `WaveformOnsetSnapper`'s `onsetMs`,
    `MinimapOverviewControl`'s `totalMs = BaseBlockMs × blockCount`), so a block
    that is not really `BaseBlockMs` wide is not a rounding detail — it is a
    drift with no bound. `generateWaveformFromReader` (JuceSharp native) used
    `round(sr·blockMs/1000)`, and 2 ms at 44.1 kHz is 88.2 samples: rounded to
    88 every block was 1.99546 ms, the pyramid ran **0.227 % fast**, and the
    waveform was drawn to the RIGHT of its own audio — +68 ms 30 s in, +408 ms
    at three minutes, growing all song. 48 kHz (96 samples exactly) was perfect,
    which is why it survived. Fixed 0.35.x; **`SchemaVersion` was bumped to 2
    for it**, because the stored `blockMs` is 2.0 either way and nothing else
    could tell a pre-fix entry apart. Pinned by
    `WaveformGenerationAlignmentTests` (drives the REAL native generator with
    impulses at known times, at both rate families; skips when native is
    absent). The local-only probe that measured it per-impulse per-level is
    `DotCharter.Tests/LocalOnly_WaveformAlignmentProbe.cs`.
  - **`WaveformSamples` carries TWO pyramids over the one persisted base
    array**: `Mipmaps` (max-of-pair — transients) and `Body` (mean-of-pair —
    loudness). Max alone saturates over any span longer than a few tens of ms,
    which is why a zoomed-out peak-only waveform was a featureless slab. Level 0
    is SHARED by reference (max == mean over one block), so the body costs
    nothing on disk and nothing at the deepest zoom. `Body` falls back to
    `Mipmaps` for a hand-built instance.
  - **A consumer that ANALYSES the envelope must pin its own resolution with
    `PickLevelNearest`, never read level 0.** `WaveformOnsetSnapper` (the
    lyric-only onset snap) does: its flux thresholds are tuned for ~20 ms, and a
    2 ms cell ripples with the audio's own cycle (a low E's period is ~12 ms),
    which would feed the detector dozens of spurious local maxima per note.
  - **A save may ADD to the cache or replace an entry with an equal-or-better
    one; it may never SHRINK one, and a SHORT archive is corruption rather than a
    smaller hit** (both found and fixed 2026-09-04, pinned by
    `Services/Audio/StemCacheTests`). `SaveAsync`'s `usable` filter silently drops
    any stem whose WAV has left disk (a cleaned playback dir, a run that failed
    part-way, a file still open in the mixer) and then wrote the short set over
    the archive — six stems became four, permanently, under an Info line that
    read like a success; it now refuses and warns. And `ExtractAsync` skipped a
    manifest entry the archive did not contain, with no log and no count check,
    while `StemCacheCoordinator` only rejects an EMPTY result: the short set was
    served as a HIT and `WriteMarker` then named that archive in the playback
    dir, so every later open served it from the fast path. That case throws
    `StemArchiveCorruptException` now (delete + re-separate); a failing DECODE
    still throws `StemDecodeUnavailableException` and still KEEPS the archive.
  - **A RE-SEPARATION over an existing stem set is an explicit, confirmed
    action (2026-09-04).** `StemSeparationViewModel.SeparateStemsAsync` reaches
    the separator only after `ConfirmStemReplacementAsync` says so: the pure
    `StemReplacePlan.For(existing, modelWillProduce)` splits what the run would
    OVERWRITE from what it cannot reproduce, and the app-wide
    `MessageDialogViewModel` prompt (a menu verb takes the modal prompt, not an
    in-place latch) NAMES the stems it is about to replace. **UNWIRED means
    REFUSE** — the delegate `StemSeparationViewModel.ConfirmStemReplaceAsync` is
    settable so a default-gate test can drive it, its constructor wires it to the
    prompt (`StemReplaceConfirmTests`), and a build that stopped wiring it must
    not fall through to overwriting files. Stems the model does
    not produce are still moved aside and restored
    (`Services/Audio/StemSeparation/StemFolderBackup`), so a re-run never silently
    DROPS one. This was `StemRegenerateDialog`, a per-stem replace picker,
    until that dialog was deleted; the per-stem opt-out went with it, the
    explicit gate did not. Pinned by `UI/ViewModels/Shell/StemReplaceConfirmTests`.
  - **An OWN-CHART key does NOT fold the audio's path in, and it did until
    2026-09-05.** The basis was `"file:" + NormalizePath(SourceLabel)`, so a
    rename, a moved project folder, a switch to a synced/OneDrive path or opening
    the same song from a copy produced a different key for BYTE-IDENTICAL audio —
    a miss, and minutes of separation paid again for stems already on disk. The
    PSARC half was immune only because it keys on the song identity (`lib:SongKey`). The basis is
    the constant `audio:` now; the content signature was always the part carrying
    the uniqueness. **Changing it alone would have stranded every archive on
    disk**, so `StemCacheKey.TryDeriveSuperseded` still derives the old
    path-folding form and `StemCacheStore.TryAdoptSupersededArchive` files that
    archive under the new token (a same-directory rename — no re-encode, no
    rewrite of the manifest inside a 100 MB zip). The archive keeps the key it was
    written with and `StemCacheKey.AcceptsRecordedKey` accepts it by its
    `|b=…|h=…` half, which is also what makes an adopted archive survive the NEXT
    move. Adoption never overwrites. `StemCacheCoordinator.TryLoadCachedAsync`
    runs it before the manifest probe. Pinned by
    `Services/Audio/StemCacheKeyPathIndependenceTests`. **`WaveformDiskCache` keys
    through the same `TryDerive`**, so its own-chart entries miss once after this
    change and regenerate — that cache is explicitly never precious, and it gains
    the same immunity to a move.
  - **The cache has NO size cap, NO age eviction and NO LRU — and adding one
    would be a bug.** Stems are never regenerated, so every `.dcstems` is
    minutes of separation the user already paid for on a song they may not even
    have open; an automatic sweep would destroy exactly the artifact the
    invariant protects (contrast the waveform cache, where eviction costs a
    re-scan and nothing else). Reclaiming the space is an explicit user action:
    `StemCacheStore.MeasureUsage()` (songs + bytes) and `StemCacheStore.Clear()`
    back the "Stem cache" row in Settings → Stem Models.
    **Both of those used to enumerate `*.dcstems` TOP-DIRECTORY ONLY, and the
    bytes they missed were unbounded (fixed 2026-09-05).** `SaveAsync` stages a
    full copy of every encoded stem PLUS the finished archive under
    `.staging-<token>-<guid>/`, removed only by that call's own `finally` — and
    the app's normal exit is `App.TerminateNow` → `TerminateProcess`, which runs
    no `finally`. Quitting during a cache save stranded hundreds of MB (GB, on
    the WAV fallback) that the "Stem cache" row did not report and the "Clear"
    button could not free. `StemCacheStore.SweepAbandonedStaging()` now runs from
    `SaveAsync`, `MeasureUsage` and `Clear`; the bytes are counted (as bytes, not
    as songs). **The one-hour `AbandonedStagingAge` guard is load-bearing** — the
    sweep runs from the SAVE path, so it must never be able to take a save that
    is running, including one in a second instance of the app; that is why even
    an explicit user `Clear` leaves a fresh staging dir alone. **This is not
    eviction**: nothing in it touches a `.dcstems`. Pinned by
    `Services/Audio/StemCacheStagingSweepTests`. `Clear` is the ONLY
    bulk removal path and nothing in the app may call it on its own; it touches
    only `*.dcstems` and skips an archive currently held open rather than
    failing the whole clear. `Delete(key)` stays what it always was: the
    corrupt-archive branch.
  - `Cache/` — never-regenerate stem cache. `StemCacheCoordinator.GetOrSeparateAsync` is the front door: derive `StemCacheKey` (logical identity — `lib:SongKey|Charter|Version` for library songs (`StemCacheIdentity.ForLibrary` normalizes the `psarc:` spelling; an archive named after it is re-keyed, see "Library archives are re-keyed from `psarc:` to `lib:`" below), the constant `audio:` for own charts — folded with a `StemAudioSignature` content fingerprint so a re-packed CDLC never serves stale stems; WHICH file is fingerprinted is `StemCacheSource`'s one decision — a library song's source `.wem` since #426, see "Library songs are keyed on the source WEM" below), hit → expand archive (no separation), miss → separate + package. One compressed `.dcstems` archive per song under `%LocalAppData%/DotCharter/StemCache/` (LocalAppData, large/regenerable). Stems are Opus (via `FfmpegStemEncoder`, ffmpeg → Opus, else WAV) recorded in `StemCacheManifest`, expanded to WAV on demand for JUCE. Opus is playback-only — never fed to live detection.
    **The key SAMPLES the audio; a hit is VERIFIED against the whole file (#334).**
    `StemAudioSignature.ComputeFile` hashes three 256 KB windows plus the length, so
    a same-length re-render with an edit between them kept the same key. The key
    stays sampled (changing it would strand every archive on disk); a save records
    `StemCacheManifest.SourceFullHash` (`ComputeFullFileHash`, memoized per path,
    length and last-write time), and `StemCacheCoordinator.TryLoadCachedAsync`
    treats a recorded hash that no longer matches as a MISS, keeping the archive
    until the new separation replaces it under the same key. An archive from
    before the field (null) hits as before. `StemCacheFullHashTests`.
    **The cache-HIT decode is CONCURRENT (0.49), and that is the whole cost of
    opening a cached song.** `StemCacheStore.ExtractAsync` unzips the Opus
    payloads and then runs one ffmpeg per stem to produce the WAVs JUCE opens.
    Each stem is an independent process writing its own distinct file, so doing
    them one at a time simply made the user wait for the SUM. Measured on the
    owner's real cache, 6 Opus stems each, Debug, one box:

    | archive | sequential | concurrent |
    |---|---|---|
    | 13 MB | 25.8 s | 2.2 s |
    | 19 MB | 8.9 s | 0.75 s |

    Two things are load-bearing about the fan-out. It is capped by
    `StemSeparationResourcePolicy.RecommendedWorkerThreads()` — the same "half
    the machine, two cores reserved" rule the separator uses — so a six-stem
    decode cannot saturate the box and starve the audio callback. And the
    results are collected per INDEX and appended in MANIFEST ORDER afterwards:
    the mixer's channel order is the manifest's, completion order is not. The
    `Task.WhenAll` is over every task including the skipped ones, so a decode
    that throws never leaves a sibling writing into an abandoned directory.
    `StemArchive` also opens the archive through a 64 KB `SequentialScan`
    `FileStream` rather than `ZipFile.OpenRead`'s 4 KB default with no hint.

    **Measured and deliberately NOT changed:** the manifest is read three times
    per hit (probe, extract, and the zip's own open) — that is 0.2 ms warm, and
    threading the already-read manifest into `ExtractAsync` would lose the
    `StemArchiveCorruptException` typing the coordinator's delete decision keys
    on. Not worth it. The decode processes also still run at
    `ProcessPriorityClass.BelowNormal`, which is an ENCODE-side policy ("a batch
    of stem encodes never fights the UI or audio thread") inherited by a call
    the user is blocked on; the concurrency cap arguably supersedes it, but that
    is a judgement about the audio thread, not a perf tidy-up, so it was left.

    **ffmpeg is located by the shared pure `MediaToolResolver` — configured path (`AppConfig.FfmpegPath`) → exe dir → exe dir/`tools` → PATH → dev-tree `tools/redist/win-x64` — and NEVER by a local re-spelling.** Both `FfmpegStemEncoder` and `OnnxStemSeparationService` take a `configuredFfmpegPath` provider, wired in `App.axaml.cs`. The encoder used to hand-roll its own order that (a) ignored the configured path outright, (b) looped names-outer/dirs-inner so all of PATH beat the bundled `tools/` copy, and (c) had no dev-tree fallback — so a `bin/Debug` run with no PATH ffmpeg silently wrote the archive as WAV, and because **the cache is never regenerated that song stayed WAV forever**.
- **htdemucs ONNX comes in two IO layouts, and JuceSharp drives both** (#254):
  the host-STFT export (`htdemucs_6s.onnx`: waveform + CAC spectrogram in,
  spectrogram + waveform out) and the in-graph-STFT one (`input [batch,2,time]`
  -> `output [batch,stems,2,time]`). `HtdemucsIoBinding.Classify` decides; each
  catalogue entry declares `OnnxModelEntry.HtdemucsLayout`, linted against
  `HtdemucsIoBinding.SupportedLayouts`. **MrCitron's `htdemucs.onnx` /
  `htdemucs_ft.onnx` are NOT in either catalogue on purpose**: they bind and run,
  but their own spectral branch is defective (stems sum to 63-69 % of the mix at
  corr 0.81-0.84; the 6-stem export: 100 % / 0.999). JuceSharp's env-gated
  `HtdemucsRealModelTests` (`JUCESHARP_HTDEMUCS_MODEL_DIR`) holds any new entry
  to that reconstruction bar. So only `htdemucs-6s` has a GPU twin
  (`OnnxStemSeparationService.MapGgmlToOnnxTwin`); pinned by
  `HtdemucsMrCitronRemovalTests`, including the clean fallback for a user who
  still has a removed id Active.
- **The DEFAULT model (#258) is `StemDefaultModel`, and "on the default" is an EMPTY
  `AppConfig.HtDemucsActiveModelId`.** It is never written: a default download goes
  through `StemModelManager.DownloadAsync(..., activateIfNoneActive: false)`, so the
  user stays on the default and a later change of default reaches them. A non-empty
  id is an explicit choice and is never switched (an htdemucs_6s user keeps it).
  `StemDefaultModel.TierFor(plan)` picks a TIER and `Preference(tier)` its ladder, best
  first (#267):
  - **8 GB GPU (`LargeGpu`): BS-RoFormer SW at its 13.35 s training chunk**
    (`bs-roformer-sw-6s-13s`), then the 4 s SW, then htdemucs_6s. The 13 s file is a
    release asset of this repo's releases, read anonymously since the repo is public (#49;
    a token is sent only when one is stored).
  - **6 GB GPU (`Gpu`): BS-RoFormer SW 4 s** (`bs-roformer-sw-6s`, public Hugging Face;
    MVSep bass 14.62 / guitar 9.05 SDR against htdemucs_6s' 11.22 / 5.25), then htdemucs_6s.
  - **CPU or a smaller GPU (`Cpu`): htdemucs-6s-onnx**, then the 4 s SW.
  - **A card counts by its NOMINAL size** (`StemDefaultModel.NominalSlackBytes`, 512 MiB):
    DXGI reads it short (measured: a 16 GB RTX 5070 Ti reads 15.62 GiB, a 10 GB RTX 3080
    9.82 GiB), so an exact `>= 8 GiB` put every 8 GB card a tier down. An unknown adapter
    (no GPU environment) counts by the provider alone.
  - Measured on the 224 s Jukkai song on DirectML (RTX 5070 Ti, Release): the 13 s file
    20.1 s at ~5.2 GB above idle (the app's own path, end to end, 2026-10-06: 23.9 s, peak
    +5,140 MiB, stems sum to the mix at 32.81 dB); the 4 s file 32.5 s at ~4.1 GB; on the
    CPU 20 s of audio took 137–203 s on six threads.
  - **Nothing downloads unasked.** A separation on the default only uses an INSTALLED
    model (`OnnxStemSeparationService.ResolveDefaultAsync`, first installed of the tier's
    ladder). When none is installed, Separate Stems asks ONCE
    (`StemSeparationViewModel.ConfirmDefaultDownloadAsync`, the app-wide confirmation
    prompt) naming the model, its size and the fallback (`IStemDefaultModelProvisioner`
    / `StemDefaultDownloadOffer`); UNWIRED means refuse. The offer is
    `StemDefaultModel.DownloadChain`: the first two of the ladder that can be downloaded
    NOW (`StemModelManager.CanDownload`), so signed out a large GPU is offered the 4 s SW
    with htdemucs_6s behind it. The agreed download takes the fallback when the first fails
    (a cancel never falls back). Elsewhere (the tone matcher's `StemIsolator`) nothing
    downloads: no installed model reads as "no backend". Pinned by
    `Services/Audio/StemDefaultModelTests` and `UI/ViewModels/Shell/StemDefaultDownloadConfirmTests`.
  - **Migration (#267): a default user KEEPS what they separate with until they agree to
    the 13 s model.** `StemDefaultModel.UpgradeTarget` offers it (an UPGRADE offer,
    `StemDefaultDownloadOffer.IsUpgrade`, its own prompt naming the model they keep) only on
    a `LargeGpu`, signed in, with another default installed and the 13 s one not. A no is
    remembered (`AppConfig.StemDefaultUpgradeDeclinedId`, `IStemDefaultModelProvisioner
    .DeclineUpgrade`) and the installed default separates; a failed upgrade download
    separates with it too. An explicit Active choice is never offered anything. Settings
    still downloads the 13 s model at any time.
  - Existing cached stems are untouched: the `.dcstems` key is song identity + audio
    signature, not the model.
  - **SW is a single third-party re-host with an UNKNOWN weights licence** (trainer
    unknown; the original `jarredou/BS-ROFO-SW-Fixed` is deleted; the MIT on
    `elicwhite/bs-roformer-sw-6stem-onnx` covers the export code). Its SHA-256 is
    pinned in JuceSharp's `OnnxModelCatalog`, so a changed file is rejected and the
    default falls back to htdemucs_6s. If the re-host disappears, the checkpoint
    survives at `enerjazzer/BS-ROFO-SW-Fixed`; the export script in
    `elicwhite/bs-roformer-web` rebuilds the ONNX (new bytes, so a new pin). The Settings
    description says the licence is unknown. Mirroring it ourselves would need a
    licence, so that would block the project going public.
  - The driver (split-complex layout, reflect-padded STFT, the model's own stem order
    — bass first) is JuceSharp's `BsRoformerInference`; see
    `JuceSharp/docs/guides/stem-backends.md`. Since #267 its song loop is MSST's own
    `demix` (`FadeWindowChunker`), so a whole song agrees with PyTorch's demix at the same
    chunk to 83–102 dB per stem, and each chunk's iSTFTs run while the GPU runs the next
    chunk. Parity is measured on the CPU (`BsRoformerSwModelTests`) and on DirectML
    (`BsRoformerSwDirectMlTests`, opt-in with `JUCESHARP_BSROFORMER_SW_GPU=1`).
  - **DirectML graph fusion is per MODEL, keyed by the FILE** (JuceSharp
    `OnnxSessionFactory.DirectMlGraphFusionKey`, read by `OnnxStemSeparator` from the
    file's metadata before the session is built): off for every file unless it says
    `dml_graph_fusion=verified`. The 13 s SW export says so (its pinned hash makes that a
    property of the row); the 4 s SW file and htdemucs do not and are garbage with fusion
    on (#151), so they keep it off. The session log names the decision ("graph fusion on:
    the model is verified with it").
  - **The training-chunk export (#267) is the `bs-roformer-sw-6s-13s` row.**
    `JuceSharp/tools/bs-roformer-sw-export` rebuilds it byte for byte from
    `enerjazzer/BS-ROFO-SW-Fixed` at the model's 13.35 s training chunk (the 4 s file's
    stems agree with the 13.35 s ones to only 13–27 dB, and leak into the piano stem): fp16,
    357,936,365 bytes, SHA-256 `2fd77c88…57fe`. It is hosted as an asset of the PRE-release
    `models-bs-roformer-sw-v1` of this PRIVATE repo, because the weights' licence is unknown,
    so it is never mirrored publicly. The row is the APP's, not JuceSharp's
    (`StemModelCatalog.AppHostedRow`, `StemModelDescriptor.ReleaseAsset`): the submodule's
    downloader is anonymous by design. **A model release must stay a PRE-release**: the
    updater asks for `/releases/latest`, which never returns one, and a `models-*` tag is no
    `vX.Y.Z` (`Services/Update/ModelReleaseIsNeverAnUpdateTests`). Published as a full release
    it would BECOME "latest" and every update check would fail until the next app release.
- `Audio/StemSeparation/Gpu/` — **DirectML, any DirectX 12 GPU, nothing to
  download** (#151). JuceSharp's `.Onnx` project ships
  `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 (the last release; DirectML is in
  maintenance) plus `DirectML.dll`, and NO CUDA: measured 2026-10-05 on htdemucs
  6-stem / a 224 s song, DirectML took 22–37 s against CUDA's 36–49 s on the same
  RTX 5070 Ti / RTX 3080, peaked at ~2.5 GB of video memory against ~5.4 GB, and
  the native payload fell from ~182 MB to ~35 MB with no 990 MB CUDA/cuDNN
  download. Splitting one song across both cards gained nothing (the CPU-side
  STFT bounds a run), so there is no multi-GPU path.
  - **The decision is pure: `StemExecutionDecision.Decide(preference,
    directMlInRuntime, adapters, storedAdapterKey)`** → provider + DXGI device id +
    a `StemExecutionReason` (`DirectMl` / `UserChoseCpu` / `NoDirectMlInRuntime` /
    `NoUsableGpu`). `OnnxStemSeparationService.ResolvePlan` feeds it from
    `IStemGpuEnvironment` (`OnnxStemGpuEnvironment`: ORT's provider list +
    `Gpu/DxgiAdapterProbe`, `EnumAdapters1` order with LUID and "drives a display";
    JuceSharp's `OnnxGpuAdapters` only if that probe finds nothing) and passes the device id to
    `OnnxStemSeparator`; the status line prints where the next run goes and, after
    a GPU run that ended on the CPU, the separator's `FallbackReason`. A stored
    "Cuda" (the old automatic choice) reads as Auto (`StemModelManager.ExecutionProvider`).
  - **Never adapter 0 by accident.** The DirectML `device_id` is the
    `EnumAdapters1` index (ORT's `dml_provider_factory.cc`), and adapter 0 is the
    DISPLAY adapter (an iGPU on most laptops; the owner's RTX 5070 Ti, #463).
    `StemGpuRanking.Rank` drops the software adapter, blocks anything under
    `MinimumVideoMemoryBytes` (3 GB; htdemucs peaked at ~2.5 GB) and orders the
    rest: adapters that drive NO display first, then dedicated video memory.
    `Choose` honours the stored pick (`AppConfig.StemSeparationGpuAdapter`,
    "id:name" — by id AND name, else by name after a renumbering), otherwise the
    first ranked, and returns the reason; empty means Automatic. Every run logs
    each adapter (index, name, MB, LUID as the GPU perf counters print it,
    display or not) and the choice with its reason. Pinned by
    `StemGpuRankingTests` / `StemExecutionDecisionTests` on injected lists.
  - **Per-chunk progress** (`StemChunkProgressReporter`): the separator reports
    `(k + 1) / N` once per finished chunk, synchronously on the inference thread.
    The reporter logs every chunk's time (the gap between reports — the model
    stage when the model is the slower stage) plus a summary every 10 chunks, and
    hands "chunk i of N" to a caller progress that also implements
    `IStemChunkProgress` — the task toast (`StemSeparationToastProgress`,
    `Progress_SeparatingStemsChunk`).
  - **Settings ▸ Stem models** (`StemBackendViewModel` + `.Gpu.cs`; the entries
    come from
    `DotCharter.Presentation/Screens/Settings/StemExecutionProviderOptions.cs`). The
    provider picker is DirectML (any DirectX 12 GPU) and the CPU. DirectML is
    greyed with its reason when it cannot run: `For` takes the blocker (null when
    it can run) and prints `Settings_StemModels_DirectMlMissing` (no DirectML in
    this runtime) or `Settings_StemModels_DirectMlUnavailable` (no GPU with
    `MinimumVideoMemoryBytes`). `Normalize`: a stored "cpu" stays the CPU;
    anything else is the automatic choice, DirectML when it can run, else the
    CPU. The GPU picker shows only with two or more hardware GPUs and DirectML in
    effect; `GpuOptions` lists "Automatic (the card it picks)" first, stored as
    `AutomaticGpuKey` (empty, which `StemGpuRanking.Choose` reads as automatic),
    then every hardware GPU in `StemGpuRanking.Rank` order, a too-small one
    greyed with its reason; a pick persists through
    `StemModelManager.GpuAdapterKey`. The hardware probe
    runs on `OnShown`, never at construction, and re-normalising after it persists
    nothing (a missing GPU today must not overwrite the stored preference).
  - **`LegacyGpuRuntimeFolder`**: the ~1.4 GB `gpu-runtime/` folder the CUDA
    builds downloaded beside the models is unused; the "Old CUDA runtime" row
    (`GpuRuntimeViewModel`) shows only while it holds bytes and offers Remove. It
    is never deleted automatically. `PayloadReceiptFilter` still refuses to let a
    payload overwrite it.
- `Audio/StemSeparation/Components/` — the in-app **component manager** (optional
  models + tools). Pure core: `ComponentInventory` scans a set of
  `ComponentDefinition`s through the injected `IComponentFileSystem` seam
  (`SystemComponentFileSystem` is the real one) and reports
  installed/missing/active status + size; `Verify` adds a size (±25% of the
  approximate catalog size) + optional SHA-256 check. `ComponentCatalog.Build`
  is the pure factory that turns (models dir, active id, `StemModelCatalog`,
  app base dir) into definitions — one per stem model plus the list-only
  external tools (ffmpeg, yt-dlp; resolution: configured → exe dir → exe
  dir/`tools` → PATH → repo `tools/redist/win-x64` walked up from the exe dir
  (dev-run last resort so bake/tag features work from bin/Debug), never
  downloadable by the manager). Releases now **ship
  ffmpeg and yt-dlp (not ffprobe) in the UI zip's `tools/` folder** (bundled by
  `tools/release/New-Release.ps1`), so a fresh install finds them there without
  a PATH install. That ffmpeg is the minimal LGPL build (#409): libopus + flac
  encoders and the `opus`/`flac`/`wav` muxers are in
  `tools/release/ffmpeg-min/components.json` because the stem cache uses them; a
  new `StemCodec` needs its muxer there (`FfmpegComponentInventoryTests`). `ComponentManager` is the thin DI coordinator (reads the
  `StemModelManager` for the models dir + active-model wiring; `Activate(id)`
  points the active model). Network download of MODELS is not implemented —
  `IComponentDownloader` is stubbed by `DisabledComponentDownloader` (CanDownload
  = false). **`ComponentManager` has NO UI as of 2026-09-04** — the Component
  Manager window it backed was a strictly narrower duplicate of Settings ▸ Stem
  Models (which downloads, activates, deletes and imports custom models, all of
  which that window could not) and was deleted with its view model and its DI
  registration; the class stays for `ComponentInventoryTests` and is registered
  nowhere. Stem-model UI is Settings ▸ Stem Models over
  `StemModelsSettingsViewModel` / `StemModelManager`.
- **A row on the app's releases downloads through the updater's mechanism (#267, #49).**
  `StemModelManager.DownloadAsync` sends a row with a `ReleaseAsset` to
  `Update/GitHubReleaseAssetDownloader` (`IGitHubReleaseAssetDownloader`): the updater's
  own mechanism — the token from `IGitHubAuthService`, the release read BY TAG, the
  api.github.com asset url with `Accept: application/octet-stream`, every request through
  `Update/GitHubApiRequest` (GitHub hosts only). The release's declared size must equal the
  pin before a byte is fetched; the complete file is checked with JuceSharp's
  `VerifiedDownload.VerifyFileAsync` before the rename; a stopped transfer resumes from
  `<file>.github.part` with a `Range`. The app's downloader (`GitHubReleaseAssetDownloader.For`)
  reads the PUBLIC repo with `RequiresSignIn` false: anonymous, with the token only when one
  is stored, so the public build (which has no sign-in UI) never blocks. Only a downloader
  that `RequiresSignIn`, signed out, makes `StemModelManager.DownloadBlock`
  `NeedsGitHubSignIn`: Settings ▸ Stem models shows the row with a DISABLED Download and the
  reason printed (`IStemModelRow.IsDownloadUnavailable` / `DownloadUnavailableReason`), and
  a sign-in refreshes the list (`SignInChanged` → `ModelsChanged`). Pinned by
  `Services/Update/GitHubReleaseAssetDownloaderTests`, `Presentation/Settings/StemModelSignInRowTests`,
  `Perf/StemProviderPickerUiPerfSmokeTests` and the opt-in end-to-end
  `Services/Audio/StemSw13EndToEndTests` (`DOTCHARTER_SW13_E2E=1`: the real token, a temp
  models folder, the Jukkai song on the GPU).
- **A model DOWNLOAD is JuceSharp's, not ours (2026-09-11)** — for every row NOT on the
  app's private releases. Every such stem-model
  fetch goes through `JuceSharp.StemSeparation.Onnx.OnnxModelCatalog.EnsureAsync`
  or `JuceSharp.StemSeparation.Runtime.VerifiedDownload.EnsureFileAsync` — both
  the shared `VerifiedDownloader`: HTTP Range RESUME, mirror failover
  (never on a user Cancel), a per-URL `.part` file, hash + exact size checked on
  the COMPLETE file BEFORE the atomic rename, and an already-installed file that
  fails its hash discarded and re-fetched rather than trusted. `StemModelManager`
  **owns no `HttpClient` and no staging file at all** — linted, both negatively
  and positively, by `Services/Audio/StemModelDownloadIntegrityTests`, and
  `OnlineHttpPolicyTests` no longer lists it among the ungoverned download
  clients.
  - **The ONNX half of `StemModelCatalog` is DERIVED from
    `OnnxModelCatalog.All`, not restated.** `StemModelCatalog.FromOnnx` copies
    the id, file name, URL, hash, exact size, stem count and provenance across
    and keeps the entry itself on `StemModelDescriptor.Onnx`, which is what the
    manager hands `EnsureAsync` — so the MIRROR list travels too. **Add an ONNX
    model to the SUBMODULE, not here** — unless it is hosted on the app's private
    releases (`AppHosted`, above), which the submodule cannot download; the only app-side field is the long-form
    Settings description (`StemModelCatalog.OnnxDescriptions`, keyed by
    JuceSharp's id — an id with no entry there is still offered, with a
    description built from what the submodule knows).
  - That derivation is the fix for a real rot: the hand-maintained table said
    `htdemucs.onnx` was ~80 MB when the file is **302,899,058 bytes**, and nine
    of its eleven rows carried no hash at all. Every derived row is now pinned
    from the Hugging Face repository API (an LFS object's `oid` IS its SHA-256).
  - **The ggml half is DERIVED too, from
    `JuceSharp.StemSeparation.Runtime.DemucsGgmlModelCatalog.All`
    (`StemModelCatalog.FromGgml`, entry kept on `StemModelDescriptor.Ggml`), and
    every download — ggml, user import, the checkpoint importer's loose files —
    goes through the kind-less `VerifiedDownload.EnsureFileAsync`;** only a row
    with a MIRROR list (i.e. an ONNX one) still goes through a catalogue's
    `EnsureAsync`. All three weights are pinned from the Hugging Face **dataset**
    API's `lfs.oid`, which retires this file's old claim that a dataset repo
    publishes none — it does, under `api/datasets/`, and that wrong inference
    left two of three weights unpinned for a release. The only app-side ggml
    fields are the long-form Settings description and the persisted id
    (`StemModelCatalog.GgmlRows`: JuceSharp says `ggml-htdemucs-4s`, `AppConfig
    .HtDemucsActiveModelId` and every `StemCacheManifest.ModelId` say
    `htdemucs-4s`). The `*_ft_*` fine-tuned family is in neither catalogue — it
    is four per-source weights, which one-file-per-model install has no shape
    for. An unpinned entry still warns and is fetched anyway; that is a stated
    decision, not a default. **Never paste a hash computed from a copy fetched
    over the same unverified channel** — it only proves the bytes did not change
    between two GETs.
  - `Audio/StemSeparation/StemModelDownloadPlan` is the pure seam:
    `FileRequestFor` (a catalogue row as url + destination + hash + exact size,
    refusing a `FileName` that is not bare — that guard used to come free from
    `ValidateEntry`), `LooseFileRequest` (the checkpoint importer's `.ckpt` +
    companion config, which used to be a SECOND hand-rolled download and whose
    bytes go straight into the embedded-Python `CkptToOnnxConverter`), and
    `Adapt`, which maps `DownloadProgress` onto the UI's unchanged
    `ModelDownloadProgress` — a `null` total becomes 0, never the `-1` fraction
    JuceSharp uses for "indeterminate".
  - `ApproxBytes` is still a UI hint and must never become the integrity bound,
    but for a derived row it now equals `ExactBytes`: there is no reason to keep
    a rounded second copy of a number we know precisely, and the rounded copy is
    what was wrong by 220 MB.

## Library songs are keyed on the source WEM (#426)

`Cache/StemCacheSource.TryResolve(identity, playableAudio)` is the ONE place that
decides which file a stem key signs, and every lookup and save goes through it
(`StemCacheCoordinator.TryLoadCachedAsync`, `StemSeparationViewModel.SaveStemsToCacheAsync`,
`AudioSilenceSplicer`). A library identity whose playable WAV has its `.wem`
beside it (the `PsarcAudioCache` layout) is keyed on the WEM's bytes:
`StemCacheKey.TryDeriveForWem` → `lib:…|wem|b=…|h=…`, and the manifest records
the WEM's sampled signature and full hash. Everything else (own charts, a library
song with no WEM beside its audio, the match eval's `StemIsolator`, which keys
`ForAudioFile`) is keyed on the audio it plays, exactly as before.

**Why:** the WAV is a DECODE. The 8 GiB PSARC audio cache evicts it, and since
#426 the native decoder recreates it a few LSBs away from the ww2ogg + NVorbis
WAV every earlier build keyed on. A WAV key would have missed the song's
`.dcstems` and the next Separate would re-separate it.

**Every earlier archive is adopted while it can still be proven.**
`Cache/WemKeyAdoption` files an old archive under the WEM key (a rename through
`StemCacheStore.TryAdoptArchiveAt`, never an overwrite) when the WAV on disk still
has the bytes it was keyed on: the exact old key, and its recorded full hash when
present. That is the ONLY proof since #431 removed ww2ogg + NVorbis (#426 also
rebuilt the old decoder's WAV from the WEM; nothing can now). So it runs twice:

1. **once per stem cache at startup** (`WemKeyAdoption.StartAtStartup`, from
   `App.axaml.cs`, on the pool): `AdoptAll` reads every manifest still keyed on a
   library song's playable form, walks the PSARC audio cache for `<id>.wav` beside
   `<id>.wem` of the recorded length, and re-keys each archive whose WAV still
   matches, under its OWN logical id. A completed pass writes
   `.wem-key-adoption-v1` in the stem cache root and never runs again; the
   headless test host disables it (`WemKeyAdoption.DisableStartupPassSwitch`);
2. **on a WEM-key miss** for the song being looked up (`TryAdoptByPlayable`, from
   the coordinator).

**Known limit:** an archive whose WAV was evicted and re-decoded BEFORE a build
with this pass ever ran cannot be proven any more; its song re-separates once.

**The device-rate conversion waits for the pass.** `LibraryDeviceRateAudio` (#435)
REPLACES a cached `<id>.wav` with its device-rate form, which would destroy the
proof the same way a re-decode does. The composition root therefore hands it
`WemKeyAdoption.HasCompleted` (the marker exists, or there is no stem cache at
all), and no WAV is replaced before that; with the switch set, never.

The adopted archive keeps the key it was written with;
`StemCacheKey.AcceptsRecordedKey` accepts the playable form of the SAME logical id
under a WEM key (`IsPlayableFormOfThisSong`), and the coordinator skips the #334
full-hash check for it (the adoption verified it; the WEM key pins the source from
then on). A WEM-keyed archive's own full-hash check is against the WEM. Pinned by
`Services/Audio/StemCacheWemKeyTests` (the startup pass: adopts, then survives a
native re-decode; no proof, no move; the full hash; own song only; once only). `WaveformDiskCache` still keys on the WAV;
that cache is never precious.

## Library archives are re-keyed from `psarc:` to `lib:` (#898)

#884 renamed a library song's identity from `psarc:<SongKey>` to `lib:<SongKey>`;
the stem cache kept hashing `psarc:` until #898. Now `StemCacheIdentity.ForLibrary`
hashes `lib:`, and `StemCacheKey.LegacyLibraryForm` derives the old name of any
`lib:` key (a pure string swap of the basis — the only thing that ever differed).

- **On lookup, never in bulk.** `StemCacheCoordinator.TryLoadCachedAsync` calls
  `StemCacheStore.TryRekeyLegacyLibraryArchive(key)` first, and
  `WemKeyAdoption.TryAdoptByPlayable` does the same for the playable key before it
  adopts a pre-#426 archive. `WemKeyAdoption.AdoptAll` (the once-only #431 pass)
  compares a `psarc:` manifest in the `lib:` spelling and moves it with the re-key,
  not a bare rename. There is no new startup sweep: an archive is touched only when
  its song is looked up.
- **The order is the data-safety argument** (`Cache/LegacyLibraryRekey`): the old
  archive's manifest must read and record a key the legacy key accepts; a copy is
  written under `.staging-<token>-rekey-<guid>/` with `Key` and `LogicalId`
  rewritten to `lib:` and every other entry copied byte for byte
  (`StemArchive.Rewrite`, then `AtomicFile.FlushFile`); it is read back and every
  stem's decompressed bytes are compared by length + SHA-256
  (`StemArchive.HasSamePayload`); it is renamed to the new name with
  `File.Move(overwrite: false)`; only THEN is the old file deleted. Nothing is ever
  re-separated or re-encoded.
- **A failure leaves the old archive under its old name, and the lookup still reads
  it there** (the coordinator falls back to `LegacyLibraryForm` when the new name is
  absent); the next lookup tries the re-key again. A crash leaves a staging dir
  (`SweepAbandonedStaging`) or both names. **An archive already under the new name
  wins; the old one is kept and logged, never deleted automatically.** When the
  playable re-key fails, `TryAdoptByPlayable` adopts in the old spelling (the
  pre-#898 move) so the fallback still finds it.
- `WaveformDiskCache` keys through the same identity, so its library entries miss
  once and regenerate (never precious).
- Pinned by `Services/Audio/StemCacheLibraryRekeyTests` (rekeyed + byte-identical
  stems + the separator never called, Opus and WAV; second lookup a no-op; a
  failing write; a torn copy; a crash after the rename; a twin; pre-#426 archives
  through the lookup and the startup pass). Non-vacuity, 2026-10-14: disabling the
  fallback failed the failing-write case, skipping the read-back check failed the
  torn-copy case, disabling the re-key failed 7 of 10.

## Legacy cache migration after an audio move

`StemCacheStore.TryAdoptSupersededArchive` first probes the old path-derived key.
If the audio moved before migration, it searches archive manifests for a legacy
`file:` key carrying the same content signature. It renames that archive without
rewriting its bytes and never overwrites an existing destination.
`StemCacheKeyPathIndependenceTests.MovingAudioBeforeTheFirstMigrationStillFindsTheLegacyArchive`
covers this ordering. ONNX transcoding passes cancellation to `MediaProcess.Run`,
which kills its child process tree; `OnnxTranscodeCancellationTests` and
`MediaProcessTests` cover the cancellation path.

## Guitar roles: Lead (centre) / Rhythm (sides) — `Audio/StemSeparation/GuitarRoles/` (#260)

No downloadable lead/rhythm model exists (the hosted MVSep/Moises ones upload
the user's song and are not integrated), so Guitar roles is a LOCAL second stage
on the guitar stem the cache already delivered, from whichever model made it
(`GuitarRoleStems.IsGuitarStem`: "guitar"/"guitars").

- **The split is `CentreSideSplitter`**, offline and streaming: a 4096-point STFT,
  hop 1024, Hann analysis + synthesis. Per bin the auto- and cross-spectra are
  averaged over ±3 frames and ±1 bin; the in-phase cross power, less a
  `CoherenceFloor` of 0.3 (the estimator's noise for two UNcorrelated channels),
  estimates the centre component's power, and each channel's lead mask is that
  over its own power (rhythm is `1 - mask`). Not a mid/side sum: on three
  overlapping synthetic guitar parts it scores 12.5 dB SDR against M/S's 2.9 dB
  (`CentreSideSplitterTests`). Lead + Rhythm reproduce the stem to ~2e-7.
- **Honesty is `GuitarRoleAssessment`, pure.** The cheap pre-check (one streaming
  pass, no transform) refuses `Silent` (< -70 dBFS) and `Mono` (one channel, or
  a side share under 0.5 %); the split itself refuses `AllCentred` /
  `NothingCentred` when either half takes under 8 % of the energy. A refused
  image writes NO audio, only the sidecar saying why, so the switch shows the
  reason from the start next time. Never two copies of one guitar.
- **The files are a REGENERABLE artefact, not part of the `.dcstems` cache.**
  `GuitarRoleStemService` writes `<sig>-lead.wav` / `<sig>-rhythm.wav` and
  `split.json` in a `guitar-roles/` SUBFOLDER beside the guitar stem (a top-level
  WAV would be adopted by the mixer's `stems/` recovery and moved by
  `StemFolderBackup`). The sidecar keys them by the guitar stem's
  `StemAudioSignature` and `AlgorithmVersion`; a re-separation, a deleted file or
  a new algorithm makes them stale and they are derived again from the guitar
  stem. **No second separation ever runs for this** — the rule the never-
  regenerate cache protects is about the model, and the split is seconds of DSP
  (7–9 s for a 2.5–3 min stem on half the cores at low priority).
- **The mixer owns the substitution.** `ChartProject.GuitarRolesSplit` (saved as
  `<stems guitarRoles="true">`, absent when off) is read by
  `StemFolderProbe.Probe` on the stem mixer's worker, which asks
  `IGuitarRoleStems.TryGetDerived`; with a pair, `BuildStrips` loads
  `guitar-lead` + `guitar-rhythm` IN PLACE of the guitar (never beside it), on
  `Stem` objects the mixer owns — `ChartProject.Stems` keeps listing what the
  model produced. The switch is `GuitarRolesViewModel`
  (`StemMixerViewModel.GuitarRoles`), shown by `Views/Editor/Inspector/GuitarRolesView`
  in the Mixer inspector's Stems card; it is enabled only
  after the image check passes and prints the reason otherwise. A project whose
  flag is on but whose pair is missing derives it again ONCE; a second miss turns
  the flag off rather than loop. Its check and split finish on pool threads and
  dispatch through the mixer's release check (`ReleaseAwareDispatcher`, #263):
  after `StemMixerViewModel.Dispose` their results are dropped.
- **Playback only.** The derived names contain "guitar", so Play Mode's "mute my
  instrument" and its stem duck (`StemDuckFollower`, which follows SEVERAL stems
  with one gain) still treat the pair as the guitar. `LiveDetectionAudioSourceTests`
  forbids `GuitarRole` / `CentreSideSplitter` under `Audio/LivePlay/`.
- `GuitarRolesRealStemProbeTests` measures real stems when
  `DOTCHARTER_GUITAR_ROLES_PROBE` names WAVs (copied to a scratch folder first);
  it skips otherwise.
