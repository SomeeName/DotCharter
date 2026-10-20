# Bundled NAM neural-amp captures — attribution

DotCharter ships a set of freely-licensed NAM (`.nam`) neural-amp captures. They
install into the local NAM capture library on first run
(`BundledNamCaptureInstaller`) and appear in the pedalboard's **NAM Captures →
Library** view with no sign-in.

**These are not a demo set — they are the backbone of the factory tone
catalogue.** Since 0.37 every factory preset has a capture-backed twin whose
`namamp` module names one of these files in its `modelPath`, so a capture that
goes missing is a preset that loads silent. Provenance for every file is
encoded in `manifest.json` (the installer copies it into each capture's
`.t3k.json` sidecar) and gated by `BundledNamCaptureAttributionTests`, which
fails the build if a shipped file has no entry here.

## Licensing — read this before adding or removing a file

Two licences are represented, and the distinction is deliberate:

- **MIT** — the five original starter captures (below). Redistributable with no
  conditions beyond attribution.
- **GPL-3.0** — the 58 captures from [`pelennor2170/NAM_models`][pack]. These
  are **copyleft data files**. Bundling them is consistent with what DotCharter
  already ships: the release binary statically links JUCE 9, Rubber Band 4 and
  the Steinberg VST 3 SDK, all of which are GPLv3-or-commercial, so the
  distributed application is *already* effectively GPLv3 (see
  `THIRD-PARTY-NOTICES.md` §2.2 and §2.6 at the repository root).

  DotCharter's own source is GPL-3.0-or-later (since v0.50.0), so the code and
  these data files carry compatible copyleft terms; the `.nam` files must still
  keep their attribution. If that is ever unacceptable, the fix is mechanical: delete
  the GPL-3.0 files, drop their manifest entries, and the capture-backed presets
  degrade to the algorithmic `amp` module (their `*-legacy` twins, which are
  never removed, cover the same ground).

[pack]: https://github.com/pelennor2170/NAM_models

## The original MIT starter set

| File | Tone role | Emulates | Author | License | Source |
|---|---|---|---|---|---|
| `fender-deluxe-reverb-clean.nam` | Clean | Fender Deluxe Reverb (vibrato channel, LSTM) | Steven Atkinson | MIT | [NeuralAmpModelerPlugin/Models/deluxe_reverb_vibrato](https://github.com/sdatkinson/NeuralAmpModelerPlugin/tree/main/Models/deluxe_reverb_vibrato) |
| `dingwall-bass-di.nam` | Bass | Dingwall bass DI (WaveNet) | Steven Atkinson | MIT | [NeuralAmpModelerPlugin/Models/dingwall_bass](https://github.com/sdatkinson/NeuralAmpModelerPlugin/tree/main/Models/dingwall_bass) |
| `nam-rhythm-crunch.nam` | Crunch | Rhythm capture (WaveNet) | Steven Atkinson | MIT | [NeuralAmpModelerPlugin/Models/2022-11-14-01_rhythm](https://github.com/sdatkinson/NeuralAmpModelerPlugin/tree/main/Models/2022-11-14-01_rhythm) |
| `boss-wn-feather.nam` | Clean / edge-of-breakup combo | Combo amp capture (WaveNet feather tier) | Mike Oliphant | MIT | [NeuralAudio/Utils/Models](https://github.com/mikeoliphant/NeuralAudio/tree/main/Utils/Models) |
| `nam-wavenet-standard.nam` | Reference | WaveNet A1 standard reference model | Steven Atkinson | MIT | [NeuralAmpModelerCore/example_models](https://github.com/sdatkinson/NeuralAmpModelerCore/tree/main/example_models) |

Provenance notes for these five: the three `NeuralAmpModelerPlugin` models are
published there in the **legacy NAM v0.4.0 two-file form** (`config.json` +
`weights.npy`) and were repacked losslessly into single modern `.nam` files (the
config JSON with the float32 weight array inlined — the exact transform NAM Core
performs internally). Weight order unchanged; only the container was updated,
and each file's `metadata.training` field records the repack. The `NeuralAudio`
and `NeuralAmpModelerCore` files are already modern single-file `.nam` and are
bundled verbatim.

## The GPL-3.0 amp + pedal pack

All of the following come from [`pelennor2170/NAM_models`][pack] (GPL-3.0,
`COPYING` at the repo root), captured by the named contributors. Files are
bundled **byte-for-byte verbatim** — only the file NAME was changed, to a stable
kebab-case id the preset library can reference without depending on a
contributor's spelling. The "Source file" column is the exact path in that pack,
so every file can be re-verified against upstream.

Names describe the captured circuit generically ("JCM2000", "5150 Block
Letter") because that is what the capture author called it. No trademark claim
or endorsement is implied, by them or by us.

### Amp / preamp captures

| File | Capture | Author | Tier | Source file in the model pack |
|---|---|---|---|---|
| `twinverb-norm-bright.nam` | Twin Reverb Normal (Bright) | Tim R | standard | `Tim R Fender TwinVerb Norm Bright.nam` |
| `twinverb-vibrato-bright.nam` | Twin Reverb Vibrato (Bright) | Tim R | standard | `Tim R Fender TwinVerb Vibrato Bright.nam` |
| `voxac15-jangle.nam` | AC15 Jangle | Phillipe P | standard | `Phillipe P VOXAC15-JonAr1.nam` |
| `voxac15-topboost.nam` | AC15 Top Boost | Phillipe P | standard | `Phillipe P VOXAC15-TopBoost.nam` |
| `jcm2000-clean.nam` | JCM2000 Clean | Tim R | standard | `Tim R JCM2000 Clean.nam` |
| `bug333-clean.nam` | 333 Clean Channel | Phillipe P | standard | `Phillipe P Bug333-Clean-ESR0,002.nam` |
| `dirtyshirley-clean.nam` | Dirty Shirley Mini Clean | Sascha S | standard | `Sascha S DirtyShirleyMini_Clean_B1_M6_T7_MV10_G4.nam` |
| `magnatone-ch1-norm.nam` | Super 59 MkII Ch1 Normal | Tim R | standard | `Tim R Magnatone Super 59 Mkii Ch1 Norm.nam` |
| `magnatone-ch1-bright.nam` | Super 59 MkII Ch1 Bright | Tim R | standard | `Tim R Magnatone Super 59 Mkii Ch1 Br.nam` |
| `5152-clean.nam` | 5152 Clean | Tim R | standard | `Tim R 5152 Clean ish.nam` |
| `jvm-clean-green.nam` | JVM Clean (Green) | Phillipe P | standard | `Phillipe P JVM-CL-GR-NoDrive-ESR0,005.nam` |
| `jcm2000-crunch.nam` | JCM2000 Crunch | Tim R | standard | `Tim R JCM2000 Crunch.nam` |
| `jcm900-crunch.nam` | JCM900 Dual Reverb Ch A | Tim R | standard | `Tim R JCM90050WDualVerbCHAG6.nam` |
| `dirtyshirley-crunch.nam` | Dirty Shirley Mini Crunch | Sascha S | standard | `Sascha S DirtyShirleyMini_crunch_G6.nam` |
| `jetcity-g5.nam` | Jet City Gain 5 | Tim R | standard | `Tim R Jet City G5.nam` |
| `splawn-gear1.nam` | Pro Mod 1st Gear | Tim R | standard | `Tim R Splawn Pro Mod G10 Gear 1.nam` |
| `jvm-crunch-orange.nam` | JVM Crunch (Orange) | Phillipe P | standard | `Phillipe P JVM-CR-OR-NoDrive-ESR0,017.nam` |
| `laney-gh100s-crunch.nam` | GH100S Low Crunch | Phillipe P | standard | `Phillipe P LaneyGH100S-Lo-CrunchGain-ESR0,024.nam` |
| `ceriatone-molecular50.nam` | Molecular 50 | Tudor N | standard | `Tudor N Ceriatone Molecular 50.nam` |
| `bugera6262-crunch.nam` | 6262 Crunch | Phillipe P | standard | `Phillipe P Bugera6262-Crunch-NoDrive-ESR0,002.nam` |
| `magnatone-pushed.nam` | Super 59 MkII Pushed | Tim R | standard | `Tim R Magnatone Super 59 Mkii Pushed.nam` |
| `friedman-el84-poweramp.nam` | DSM EL84 Power Amp | Sascha S | feather | `Sascha S FriedmanDSM_PowerAmpEL84_MV6_feather.nam` |
| `5150-blockletter-boosted.nam` | 5150 Block Letter (Boosted) | Helga B | standard | `Helga B 5150 BlockLetter - Boosted.nam` |
| `5150-blockletter.nam` | 5150 Block Letter | Helga B | standard | `Helga B 5150 BlockLetter - NoBoost.nam` |
| `6505p-red-boosted.nam` | 6505+ Red Channel (Boosted) | Helga B | standard | `Helga B 6505+ Red ch - MXR Drive.nam` |
| `mesa-iii-red-boosted.nam` | Mark III Red (Boosted) | Helga B | standard | `Helga B III Red - 805 Boost.nam` |
| `mesa-iii-blue.nam` | Mark III Blue | Helga B | standard | `Helga B III Blue - NoBoost.nam` |
| `xxx-6l6-boosted.nam` | XXX 6L6 (Boosted) | Helga B | standard | `Helga B XXX-6L6-M77-ESR0015.nam` |
| `jsx-ultra-boosted.nam` | JSX Ultra (Boosted) | Helga B | standard | `Helga B JSX Ultra - M77(FIXED).nam` |
| `driftwood-nightmare-hm2.nam` | Nightmare High Gain + HM-2 | Tudor N | standard | `Tudor N Driftwood Nightmare High Gain   HM2.nam` |
| `ksr-ceres-purple.nam` | Ceres Purple Channel | Jason Z | standard | `Jason Z KSR CERES - purple channel - all noon EQ.nam` |
| `engl-savage.nam` | Savage 120 | Tom C | standard | `Tom C Axe FX 2 Engl Savage.nam` |
| `orange-rockerverb.nam` | Rockerverb | Tom C | standard | `Tom C Axe FX 2 Orange Rockerverb.nam` |
| `yerasov-djent-boosted.nam` | Yerasov Djent (Boosted) | Roman A | standard | `Roman A YERASOV_MESHUGGAH_BOOSTED.nam` |
| `ubermetal-insane.nam` | Ubermetal Insane | Jason Z | standard | `Jason Z Line 6 UBERMETAL INSANE droom metal at its best.nam` |
| `mesa-markiv.nam` | Mark IV Lead | Roman A | standard | `Roman A LT_MESA_MARKIV_1.nam` |
| `5152-lead-ts9.nam` | 5152 Lead + TS9 | Tim R | standard | `Tim R 5152 Lead + TS9.nam` |
| `5152-crunch.nam` | 5152 Crunch | Tim R | standard | `Tim R 5152 Crunch No Boost.nam` |
| `jcm2000-lead-boosted.nam` | JCM2000 Lead 2 (Boosted) | Tim R | standard | `Tim R JCM2000 L2 G8 805'd.nam` |
| `splawn-gear3.nam` | Pro Mod 3rd Gear | Tim R | standard | `Tim R Splawn Pro Mod 3rd Gear - G12.nam` |
| `magnatone-maxed-ts9.nam` | Super 59 MkII Maxed + TS9 | Tim R | standard | `Tim R Magnatone Super 59 Mkii Max'd TS9.nam` |
| `bug1990-lead.nam` | 1990 Lead | Phillipe P | standard | `Phillipe P Bug1990-Lead-NoDrive-ESR0,004.nam` |
| `jsx-crunch-boosted.nam` | JSX Crunch (Boosted) | Helga B | standard | `Helga B JSX-Crunch-M77-ESR0,007.nam` |
| `slo-super-lead.nam` | Super Lead Overdrive | Jason Z | standard | `Jason Z Soldano - Super Lead Overdrive pedal nice crunchy rhythm tone.nam` |
| `dp3x-bass-preamp.nam` | DP3X Bass Preamp | Jason Z | standard | `Jason Z Tech21 dUg DP3X bass preamp pedal all dimed no shift.nam` |

### Dirt / boost pedal captures

| File | Capture | Author | Tier | Source file in the model pack |
|---|---|---|---|---|
| `ts9.nam` | TS9 Screamer | Tim R | standard | `Tim R TS9.nam` |
| `ts9-driven.nam` | TS9 Screamer (Driven) | Tim R | standard | `Tim R TS9 Driven.nam` |
| `m77-badass-od.nam` | M77 Badass Overdrive | Phillipe P | feather | `Phillipe P MXR-M77-Feather-ESR0,001.nam` |
| `od808.nam` | OD808 Overdrive | Phillipe P | feather | `Phillipe P Maxon-OD808-Feather-ESR0,001.nam` |
| `sd1-super-overdrive.nam` | SD-1 Super Overdrive | Phillipe P | feather | `Phillipe P BOSS-SD1-Feather-ESR0,001.nam` |
| `ds1-distortion.nam` | DS-1 Distortion | Keith B | standard | `Keith B DS1_maxg_t5.nam` |
| `klone-transparent-boost.nam` | Klone Transparent Boost | Keith B | standard | `Keith B klone_g6_t6_o5.nam` |
| `plumes-drive.nam` | Plumes-style Drive | Keith B | standard | `Keith B PlumesClone_maxG_t5_switch1.nam` |
| `hm2-chainsaw.nam` | HM-2 Chainsaw (Swedish) | Peter N | lite | `Peter N HM-2_SWEDE_Lite_ESR-0.0057.nam` |
| `precision-drive.nam` | Precision Drive | Phillipe P | feather | `Phillipe P Precision-Drive-Feather-ESR0,001.nam` |
| `grind-mini-boost.nam` | Grind Mini Boost | Tudor N | lite | `Tudor N Fortin Grind Mini Knob @ 5 ESR_0.0012_lite.nam` |
| `sd-805-overdrive.nam` | 805 Overdrive | Tim R | standard | `Tim R Seymour Duncan 805.nam` |
| `os2-overdrive-distortion.nam` | OS-2 Overdrive/Distortion | Helga B | standard | `Helga B Boss OS-2.nam` |

## Architecture note (A1 vs A2)

**Every bundled capture is NAM A1** — `architecture` is `WaveNet` or `LSTM`,
file format `version` 0.5.0. This was verified at download time; the fetch
refuses any file that does not parse as a NAM model and reports the
architecture of each.

A2 (released June 2026, `architecture` = `2`) is supported by the engine —
NeuralAudio 0.1.3 loads it and the `namamp` module dispatches on the file, not
on a version we hard-code — but **no public, directly-downloadable A2 pack
exists as of 2026-07-26**. TONE3000 is the place A2 captures are being
published, and its API requires a signed-in bearer token for *search* as well as
download (`GET /api/v1/tones/search` returns 401 anonymously), so it cannot be
harvested unattended. The in-app TONE3000 browser with its A2 architecture
filter is the supported route: sign in, filter to A2, download, and the capture
appears in the same library these files install into — at which point any preset
can be repointed at it by editing one `modelPath`.

## Adding a capture

1. Drop the `.nam` in this folder with a kebab-case name.
2. Add a `manifest.json` entry with a real `license`, `licenseUrl` and
   `attributionUrl` — the allowlist in `BundledNamCaptureAttributionTests`
   decides what may ship.
3. Add a row to the correct table above; the test fails if a shipped file is
   not named in this document.
4. Keep the total under the 32 MB cap the same test enforces.
