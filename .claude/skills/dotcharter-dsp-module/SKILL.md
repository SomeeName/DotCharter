---
name: dotcharter-dsp-module
description: Add or change a module (pedal, amp, cab, effect, gear model) in DotCharter's native tone rig — the dcdsp core, the gear-file catalogue row, params/value labels/category, the RT scenario, the managed mirror (DcRigTargetCatalog), the pedalboard visual mapping, tests and catalogue-count floors. Use this whenever the user wants a new effect/pedal/amp/cab/gear model, a new or changed parameter on an existing module, or a DSP core rewritten — even if they only say "add a chorus", "model a Tube Screamer" or "the delay needs a ping-pong switch".
---

# Adding a module to the tone rig

A module touches native C++, the managed mirror, the UI palette and three
test suites. Each of those has a test that goes red when a step is skipped,
so work through the steps in order. Reference:
`JuceSharp/docs/agents/dsp-tone-rig.md`. Read § "The rig, the catalogue and
the cores" and § "`prepare()` is where cores go wrong — seven rules" before
writing the core. The core runs on the audio callback, so the realtime
checklist applies throughout (`.claude/rules/realtime-audio.md`).

Scope first: a new module is substantial work. Plan it (Superpowers),
decide its category and params with the user, and write the behavioural
test before the core where you can.

## 1. Category

Choose one of the fixed words in `JuceSharp/src/JuceSharp.Native/src/dsp/GearEntry.h`
(`kCategoryDynamics … kCategorySynth`). It is not free text: the managed
catalogue and the pedalboard drawers key off it, and the native gate
(`tests/dsp/tone_regressions.cpp`) rejects anything else. The category
decides which `src/dsp/gear/<Category>Gear.cpp` the module goes in.

## 2. The core: `src/dsp/cores/dcdsp_<name>.h`

Header-only with no JUCE dependency: a params struct plus
`prepare(sampleRate, maxBlock, numChannels)`, `reset()` and
`process(float* const* ch, int numCh, int n)`. Model it on a small existing
core. The rules that have each shipped broken once:

1. `prepare()` never touches user content (persisted slots, strings).
2. Call `.prepare(sr)` on every rate-owned primitive. A filter's default
   corner is a coefficient, not a frequency.
3. `prepare()` clears every stateful member (`reset()`), not just the tuning.
4. Re-derive every rate-dependent count or coefficient in `prepare()` from the
   stored milliseconds or Hz, clamped the same way `setParams` clamps.
5. On first prepare use `SmoothedValue::snapToTarget()`, never
   `setCurrentAndTarget(x.current())`: current is 0, which means silence.
6. A `prepare()` that reassigns a filter must force the redesign by clearing
   any "unchanged, skip" cache.
7. Skip an oversized block whole (`return`); never process part of it. Loop
   over the channel count `prepare()` was given, not the caller's.

Also: no allocation, locks, I/O or `resize` in `process()`. Ramp every
user-facing gain, mix and level (`SmoothedValue`), never apply a per-block
constant. A param may not **step** a modulator shape, a loaded delay tap or
one callback's worth of work: crossfade a waveform switch (~10 ms), ramp a
re-solved tap (~20 ms), and drain large rebuilds incrementally. Keep clamps
NaN-safe, since `v < lo` and `v > hi` are both false for NaN.

## 3. Adapter and catalogue row: `src/dsp/gear/<Category>Gear.cpp`

Inside `append<Category>Gear(std::vector<Entry>&)`, add one
`DspModule`-derived adapter (the `VolumeModule` in `UtilityGear.cpp` is the
small example) and one row:
`out.push_back({ {id, "Display Name", {ParamInfo…}}, &makeX, kCategoryX[, kFlagSidechain] })`.

- Leave the original table and hook order in `DspModuleFactory.cpp` alone;
  new modules go in the gear files.
- Every discrete param declares itself, either **named** via `valueLabels` (a
  picker) or **ordinal** via `step > 0` (a snapping knob). Never a plain knob
  the core rounds.
- Float params go through `ParamMailbox` (`src/dsp/DspModule.h`):
  `setParam` publishes, and `process()` calls `mail_.applyPending(core_)`.
  Never call `core_.setParams` from `setParam`, because that thread is not
  the audio thread.
- Declare `kFlagSidechain` / `supportsSidechain()` only if the sidechain is
  really consumed.
- An in-path bass cut uses a bounded shelf, not an unbounded high-pass.
- The display name comes from the native row; there is no per-module resx
  key. Add EN/de-DE/ja-JP keys only for extra UI strings, and add the same
  key to all three files.

A brand-new `<Category>Gear.cpp` file must also be added to
`JUCESHARP_RIG_FACTORY_SOURCES` in `JuceSharp.Native/CMakeLists.txt`. That
one list feeds the DLL and the native test executables. A new row alone does
not bump `DCA_ABI_VERSION`; changing the ABI surface does (use the
`dotcharter-abi-bump` skill).

## 4. RT scenario

`RtFullRigAllocatesNothing` already sweeps the whole catalogue at default
settings. For driven behaviour, extend the drawer's
gear scenario in `tests/rt/rt_main.cpp` (`scenarioDriveGearChain`,
`scenarioModulationGear`, `scenarioUtilityGearLooperFootswitch`, …; grep
`run("Rt.*Gear`): chain the module,
move every switch and param for about 200 blocks, and assert that it actually
did something. Run it with the `dotcharter-rt-gate` skill.

## 5. Managed and app side

- **`DotCharter.Services/Audio/Dsp/RsToneConversion/DcRigTargetCatalog.cs`**:
  add a `public const string <Name> = "<id>";` and a `Modules[…]` entry of
  `ParamSpec(id, min, max, default)` that mirrors the native ranges exactly.
  `DcRigTargetCatalogTests` checks this against the live DLL.
- `JuceSharp/src/JuceSharp/Dsp/JuceDspModuleCatalog.cs` reads names,
  categories, params and labels from the ABI, so it needs no edit.
- **`DotCharter.UI/Controls/Dsp/DspModuleVisuals.cs`**: give the id a
  category and a unique accent colour. `PedalboardVisualsTests` fails on a
  module that would fall back to the grey Utility look, or on a duplicate
  accent. A bespoke `PedalSkin` is optional; faces are drawn procedurally,
  so there are no bitmaps to add.
- **`DotCharter.UI/Controls/Dsp/GearCuration.cs`**: place the id in the
  `Order` array inside its family block. Otherwise it sorts last.
- `tone_gear_data.json` is the real Rocksmith gear list and is **not**
  touched for a rig module. Only extend `RsToneConverter` if RS tones should
  convert to the new module, and that is a separate task.

## 6. Tests and counts

- `JuceSharp/tests/JuceSharp.Tests/Dsp/Gear<Category>Tests.cs`: add the
  module and cover a behavioural claim (seen failing before the fix), a
  three-render ramp on each level/mix param, NaN rejection, a two-rate
  `prepare()` and an oversized block.
- `JuceSharp/tests/JuceSharp.Tests/Dsp/JuceDspChainWrapperTests.cs`: raise
  the base of the `99 + (hasNamAmp ? 1 : 0)` floor. Keep the `namamp` term;
  a bare `>= N` breaks the NEURAL=OFF build.
- Rebuild natively (full `dotcharter-rt-gate` run), then run the JuceSharp
  tests and `tools/agent/Invoke-Gate.ps1 -FilterClass
  "*PedalboardVisualsTests,*DcRigTargetCatalogTests,*FactoryPresetsAgainstLiveCatalogueTests"`,
  then the full gate.

## 7. Docs, in the same change

Update `JuceSharp/docs/guides/gear-<category>.md` and the catalogue-count
prose. The measured count lives in `JuceSharp/docs/agents/layout.md`'s sub-project table
and in `dsp-tone-rig.md`. Re-count rather than incrementing the stated
number, because those figures have drifted before.
