# `Audio/` — the playback service, its gates and the engine config (deep reference) — `Audio/Equalizer/` (the six-band parametric EQ, ABI v29); `Audio/SilentTransport/`

## `Audio/Equalizer/` — the six-band parametric EQ (ABI v29)

- `Audio/Equalizer/` — the inspector's six-band parametric EQ over ABI v29
  (`SupportsParametricEq` / `ApplyEq` / `EngineEqActiveCount` /
  `ReadEqSpectrum` on `IAudioPlaybackService`). Five PURE types and no service:
  `EqBand` + `EqLimits` (one immutable band and the ranges every value is held
  to), `EqCurve` (RBJ coefficients per shape, the summed magnitude response, the
  three axis mappings and the DRAG VERB), `EqSection` (the mutable whole),
  `EqPresets` (seven charting presets as DATA, with derived resx keys) and
  `EqSpectrum` (the analyser's level mapping, its silence predicate and the
  fixed PREVIEW spectrum). Rules:
  - **`EqSection.AudibleBands` is the single predicate** the engine push, the
    drawn curve and the "n/6" badge all read. Nothing keeps a second copy, and
    the badge's number is read back from the ENGINE
    (`EngineEqActiveCount`) rather than counted here.
  - **`EngineEqActiveCount` answers −1 for "no engine has been given this
    section", not 0.** The engine is built lazily (first audio load / first
    transport call) while the module's view model is constructed with the
    transport, so a cold start really does produce an engine that has never
    heard of the section — and its truthful zero is indistinguishable on a badge
    from "nothing is switched on". The owner reported exactly that: `0/6`
    standing over lit band chips. `JuceAudioPlaybackService` gates on
    `_eqSection is not null`; the module's `RefreshLiveState` closes the other
    half by pushing on the transition into a live engine.
  - **`EqCurve.Drag` takes the band as it was at the GRAB, and a gainless shape's
    vertical axis is Q.** Bands 1 and 6 of every preset are a high pass and a low
    pass: neither has gain, so half of their handle's travel used to do nothing
    ("6 and 1 can't be moved in height"). The Q travel is multiplicative and
    RELATIVE — an absolute mapping would snap Q the instant the handle was
    touched (it is drawn on the 0 dB line whatever its Q), and feeding the LIVE
    band back in compounds once per pointer move and pins Q in a few pixels.
    Drag also switches the band ON, which is what the mockup's `onMove` does and
    what stops a dragged-but-bypassed band reading as a broken control.
  - **A made-up spectrum must be MARKED.** `EqSpectrum.Preview` is the mockup's
    own three-hump average curve and it is what the plot draws while nothing is
    playing (`EqSpectrum.IsSilent`, which treats a floor-level frame as silence —
    a stopped transport still feeds the analyser the interface's noise floor).
    `EqSpectrumKind` makes "which one is on screen" a value rather than an
    inference, and the plot draws the preview dimmer and tagged. Drawing it
    UNMARKED would be the analyser that lies.
  - **`EqLimits` is duplicated in `AudioEngine::setEqBand` on purpose.** The ABI
    is a boundary and a boundary that trusts its caller is not one, so both
    sides clamp and both carry a comment naming the other. Change either and
    change both — and **the agreement IS asserted now**: this file used to say
    "no test can assert an agreement across two languages", which was never true
    here, because the C++ side is a submodule of this repository and can simply
    be read. `EqNativeMirrorTests` parses `JuceSharp/src/JuceSharp.Native/src/AudioEngine.h`
    for the six `kEq*` clamp constants and `include/dotcharter_audio.h` for
    `DCA_EQ_BAND_COUNT` and the ORDINALS of `dca_eq_band_type` / `dca_eq_route`
    (which `PushEqTo` casts straight across — a renumber turns every user's bell
    into a low pass and every drawn curve into a lie). It also pins that every
    shipped `EqPresets` entry is already inside the ranges: `EqSection.SetBand`
    and `PushEqTo` clamp, but `LoadPreset` and the constructor copy the table
    VERBATIM, so an out-of-range preset would be DRAWN at the authored value and
    PLAYED at the clamped one.
  - **`JuceAudioPlaybackService` keeps the last pushed section and REPLAYS it in
    the engine-rebuild block** next to `SetHighPass`/`SetLowPass`/`SetBandPass`.
    Without it a device change / NAM 48 kHz request / watchdog recovery drops
    the user's whole EQ silently — the same shape as the per-seat routing state.
    `PushEqTo` is the one place a section becomes engine calls, and it sends the
    master power LAST because every band write republishes the coefficient set.
  - **No band may stop the replay.** `JuceAudioEngine.SetEqBand` THROWS on a bad
    index since ABI v36's wave-4 wrapper audit (it used to log and return), and
    the loop that calls it IS the rebuild replay — the window where the engine is
    most likely in the state that throws. A throw on band *i* abandoned bands
    *i+1…n* AND the solo / route / trim / power calls after them, which is
    exactly the half-written coefficient set the ordering above exists to
    prevent, and left the section switched OFF while the UI kept drawing the
    user's curve. The loop now lives in `JuceAudioPlaybackService.WriteEqBands`:
    bands past `JuceAudioEngine.EqBandCount` are dropped BEFORE it, each write is
    guarded, and a rejected band costs one ERROR line and nothing else.
    `EqBandWriteReplayTests` drives it through a delegate — no engine, no device
    — and pins both halves, including that a rejection is reported rather than
    swallowed.
  - **`ReadEqSpectrum` validates the buffer LENGTH at the service, not at the
    wrapper.** `JuceAudioEngine.ReadEqSpectrum` throws
    `ArgumentOutOfRangeException` outside
    `[MinEqSpectrumBins, MaxEqSpectrumBins]` (8 … 512) so a host cannot mistake a
    refused call for a silent engine; the service is the app's boundary, its
    contract is "how many bins were written", and the reader behind it is a
    ~15 Hz UI poll. A bad size is logged and answered with 0.
  - **The namespace is `Audio.Equalizer`, not `Audio.Eq`** — `RsToneConverter`
    already uses `Eq` as a local, and a namespace by that name is a compile
    error at its call sites.

## `Audio/SilentTransport/` — charts with no audio

- `Audio/SilentTransport/` — `SilentAudioTrack`: a cached silent WAV of a
  project's length so an **audio-less chart** (RS1 chart-only import whose audio
  lives in a game archive we don't have, or a missing/unresolvable audio ref) still
  drives the transport — playhead, scrubbing, count-in and Play Mode all work.
  `ProjectSessionService.LoadSilentTransportAsync` loads it into the engine ONLY;
  it is NEVER stored as `ProjectState.CurrentAudioFileName` (which stem separation
  and the save path both key on), so the declared audio ref is preserved for save
  and separation/detection never run against silence. Only kicks in when the
  project has content (`DeriveProjectLengthMs > 0`); a truly empty/no-project state
  keeps the free-engine behaviour.
- `Session/SilentTransportSession` is the session's half: it owns the loaded
  length, the growth check's re-entrancy guard and the watch on the ACTIVE
  project's beat grid (re-attached per load), and regenerates a longer track
  when the chart outgrows the current one. `ProjectSessionService` builds it,
  forwards `LoadSilentTransportAsync` to it and `Stop()`s it whenever real audio
  loads and on dispose. Pinned by `Services/Session/SilentTransportSessionTests`
  and, through the session, `SilentTransportGrowthTests`.

