# Tone-preset authoring

How to author a factory preset that sounds good, especially a high-gain one.

Catalogue: `DotCharter.Services/Audio/Dsp/Presets/DspTonePresetLibrary.cs`
(83 presets). Gates: `DotCharter.Tests/Audio/Dsp/Presets/DspTonePresetLibraryTests.cs`.
Param ranges: `DcRigTargetCatalog`.

## The rule: model a REAL rig's topology

```
gate → SCREAMER boost → amp model matched to the style → era-correct cab → corrective EQ
```

- **`gate`** — `thresholdDb` −44…−52, `releaseMs` 50–90. (Doom/fuzz rigs keep NO
  gate: the sustain IS the instrument.)
- **`overdrive` boost — `model` = 1 (Screamer), `drive` LOW 0.10–0.25, `level`
  HIGH 0.80–0.90.** This is the single most-missed trick. A screamer in front of
  a hi-gain amp is **not** a second distortion — it is a **mid hump + bass cut**,
  and that is what tightens the amp. Cranking its `drive` defeats the purpose.
- **`amp` — pick the `voicing` that matches the style, at `gain` 0.55–0.84,
  NEVER maxed.** Past ~0.85 the preamp only adds fizz and intermodulation.
  v24 roster indices worth knowing:

  | idx | voicing | use for |
  |---|---|---|
  | 2 | Five-Oh-One | American hi-gain rhythm/lead (5150-family) |
  | 4 | Recto Dual | nu-metal, drop-tuned groove |
  | 6 | Brown Variac | '80s hot-rodded lead |
  | 10 | Plexi 100 | doom / big British wall |
  | 11 | JMP-800 | thrash, classic boosted lead |
  | 14 | Recto Triple | big-bottom modern rectifier |
  | 15 | Modern Djent | djent, industrial, modern metalcore |
  | 16 | PowerBall | metalcore rhythm, neoclassical shred |

- **`cab` — era-correct, and it dominates the presence band.**
  `8` = 4x12 V30 Modern for anything modern; `2` = scooped 4x12 for
  classic/thrash; `7` = Greenback where dark IS the style; `3` = Vintage 4x12 is
  the DARKEST IR in the set — almost never what a modern metal preset wants.
  Measured: the same chain keeps **~21 % of its energy in 2–6 kHz on cab 8 vs
  ~3 % on cab 3/7**. `highCutHz` is nearly irrelevant next to the IR's own
  rolloff.
- **`eq`** — corrective only, last.

## What the OLD presets did wrong

1. Every metal preset shipped a **cranked generic `distortion` PEDAL** (`gain`
   0.80–0.95, `model` 0) stacked into a **near-maxed lead amp** (`gain`
   0.78–0.85). Measured intermodulation **6–33 %** vs **1.6–2.4 %** for
   boost-into-amp. *That cascade is the fizz/mush.*
2. Every one of them **ended on the darkest cab** (model 3, Vintage 4x12).
3. **The v24 gear roster went entirely unused**: no preset selected a hi-gain amp
   model, and the `overdrive`/`distortion` `model` param was never set — so every
   "boost" pedal was the generic voicing, not a Screamer.

## Measured before → after (the rebuild)

Probe: synthesized chug + an **82.41 + 220 Hz dual-tone** IMD probe at the
normalizer's **−18 dBFS** reference input, run through an offline harness over
the same headers the engine compiles, post-normalizer.

| metric | before | after |
|---|---|---|
| spectral centroid | 409–615 Hz | **535–1496 Hz** |
| 2–6 kHz presence (fizz/presence proxy) | 3.6–10.5 % | **8–32 %** |
| IMD (dual-tone, off-harmonic-grid energy) | 5–33 % | **1.6–6.4 %** on the boosted rigs |
| <100 Hz (flub proxy), doom | 3.0 % | **1.7 %** |

Doom Fuzz Wall is the deliberate exception: rebuilt on the Gnash `distortion`
model (1) into Plexi 100 through the scooped 4x12, its IMD stays **30.9 %** — a
fuzz wall *is* intermodulation by definition, and it is the only hi-gain preset
that should read high there.

**The metrics**: spectral centroid; **2–6 kHz %** = presence/fizz proxy;
**<100 Hz %** = flub proxy; **IMD** = the fraction of dual-tone energy landing
OFF the two harmonic grids.

## The loudness gate — non-negotiable

`DspTonePresetLibraryTests.EveryFactoryPreset_EstimatedOutput_InsideTheLoudnessBand`
requires every preset's ESTIMATED static output
(`DcRigLoudnessNormalizer.EstimateChainOutputDb`, −18 dBFS reference input) to
land **within ±6 dB of the −12 dBFS target**.

Retouch **`level` / `master` / `outputTrimDb` / `makeupDb`** (or a `namamp`
preset's `inputDb`/`outputDb`) to centre a preset. **Never `drive`/`gain`** —
that changes the *sound*, not the level; the estimator caps saturating stages at
the −3 dBFS ceiling precisely so it can't be gamed that way. Typical landings:
saturated rigs keep amp `master` ≈ 0.46–0.52, cleans ≈ 0.6–0.75.

## Vocal-synth presets (`miku`, `mikustomp`) are different

The loudness estimator has **no case** for a vocal module — its output level is
the synth's, not a function of the input, so it reads **level-neutral**. An
amp-less vocal rig therefore centres on the target with the compressor's
`makeupDb` + the `eq`'s `outputTrimDb` — **never** with `mix`.

Other rules (block comment lives above the `miku-*` block in the library):
`mikustomp` defaults to **`mix` = 1 (wet-only, like the hardware)**, so a "duet"
rig must pin `mix` explicitly or the guitar is simply gone; put a `gate` +
`compressor` in FRONT so the monophonic E1–E6 tracker fires one syllable per
pick; lyrics ride the string params (`phrases`/`phrase2`/`phrase3`) and a Phrase
pattern (6/7/8) only sings when its slot is non-empty.

## Mechanics

- Params omitted from a preset are pinned to the **catalogue default** at apply
  (`DcRigTargetCatalog.FillDefaults`) — a sparse preset never inherits the
  previous tone's values.
- Factory presets are loudness-normalized at the apply seam (a trailing `gain`
  module); **user** presets apply verbatim.
- String params (`namamp` `modelPath`, `cab` `irPath`, `miku*` `phrases`) go in
  `DcRigModule.StringParams` and are re-pushed by `DcRigApplier` on add AND on
  reuse — so a preset that CARRIES its capture/lyrics survives a node re-add.
- Editorial order for the browser is a separate seam: `PresetCuration`
  (`RankOf`/`IsFeatured`/`OrderFeaturedFirst`).
