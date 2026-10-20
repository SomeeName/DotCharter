# Rocksmith gear coverage — what each RS 2014 gear piece becomes on our rig

**GENERATED — do not edit by hand.** `RsGearCoverageAuditTests` renders this
file from the REAL converter (`RsToneConverter`) and the embedded gear
catalogue (`Tones/tone_gear_data.json`), and fails the gate when it is stale.
Regenerate with `DOTCHARTER_REGEN_RS_COVERAGE=1` and
`Invoke-Gate.ps1 -FilterClass "*RsGearCoverageAuditTests"`. The library usage
column comes from the committed snapshot
`DotCharter.Tests/Audio/Dsp/RsToneConversion/Coverage/rs_gear_library_usage.json`
(rescan: `DOTCHARTER_RS_LIBRARY_SCAN=1`, read-only).

How each row is decided:

- every knob is swept from its catalogue minimum to its maximum through
  `RsToneConverter.ConvertGear`; the params that move are what it reaches, and a
  knob that moves nothing **folds**;
- **✅ modelled** — a module or roster model of the same circuit, every knob
  lands, and the converter reports nothing lossy;
- **🟡 approximated** — it reaches the rig, but a knob folds, the converter
  reports a lossy decision, or the model is the nearest stand-in rather than
  the same circuit (that last call is the one curated input:
  `RsGearCoverageJudgements`);
- **❌ missing** — the converter drops it.

✅ never means "sounds identical to Rocksmith": no Rocksmith render exists to
compare against. How close the rig is, measured, is the calibration harness's
job (`RsToneFidelityHarnessTests`).

**This is the conversion WITHOUT a Rocksmith 2014 install** (#755). With one
(and the player's key file), every amp the game ships runs its own soundbank
chain on `rsamp`, and Rack_StudioEQ, Pedal_Compression, Pedal_LoFiFilter,
Pedal_BassEmulator, Pedal_BitCruncher, Bass_Pedal_BassOverdrive,
Pedal_AcousticEmulator and six chorus pedals follow the game's own
knob curves — read from that install at run time (`RsGameTables`), never
stored in the repository. See `docs/agents/services/dsp-tone-rig.md`.

## Summary

Library snapshot: 580 distinct tones in 40 packages.

| Type | Keys | ✅ | 🟡 | ❌ | Library uses | ✅ uses | 🟡 uses | ❌ uses |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Amps | 63 | 0 | 63 | 0 | 580 | 0 | 580 | 0 |
| Cabinets | 450 | 445 | 5 | 0 | 580 | 580 | 0 | 0 |
| Pedals | 83 | 48 | 34 | 1 | 894 | 689 | 205 | 0 |
| Racks | 17 | 10 | 7 | 0 | 703 | 669 | 34 | 0 |
| **All** | 613 | 503 | 109 | 1 | 2757 | 1938 | 819 | 0 |

## Gaps ranked by library use

Every 🟡 and ❌ row that the library uses, most-used first. Cabinets are folded to
their family (all mic/position variants together).

| Uses | Gear | Status | Becomes | Why |
|---:|---|---|---|---|
| 113 | `Bass_Pedal_MBComp` MB Comp | 🟡 | multiband | MB Comp: the game's own model is read from a Rocksmith 2014 install; without one it is converted by its knob labels |
| 66 | `Amp_GB100` GBC-1H | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 54 | `Bass_Amp_HT300B` HW-300B | 🟡 | amp (Plexi 50) | knob 'Mid-Frequency' folds (no param moves); knob 'High-Shift' folds (no param moves); knob 'Low-Shift' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 31 | `Amp_OrangeOR100` Orange OR100 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 30 | `Amp_EN50` EN-50 | 🟡 | amp (Plexi 50) | knob 'Bright' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 29 | `Pedal_AcousticEmulator` Acoustic Emulator | 🟡 | acousticsim | knob 'Mid-range' folds (no param moves); knob 'Mid Frequency' folds (no param moves) |
| 28 | `Amp_CA100` Cali-100 | 🟡 | amp (Five-Oh-One Lead) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 25 | `Amp_TW22` AMTW-22 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 24 | `Amp_CS90` CL-SS 90 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 24 | `Amp_TW40` AMTW-40 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 24 | `Rack_StudioChorus` Studio Chorus | 🟡 | chorus | knob 'Delay' folds (no param moves); knob 'HiFilter' folds (no param moves) |
| 20 | `Amp_HG180` HG-180 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 20 | `Amp_OrangeAD50` Orange AD50 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 19 | `Amp_AT20` AT-20 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 17 | `Amp_AT120` AT-120 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 17 | `Bass_Amp_CH350B` CLH-350B | 🟡 | amp (Plexi 50) | knob '30hz' folds (no param moves); knob '90hz' folds (no param moves); knob '250hz' folds (no param moves); knob '800hz' folds (no param moves); knob '2.5khz' folds (no param moves); knob '7.5khz' folds (no param moves); knob '15khz' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 16 | `Bass_Amp_BT880B` BH-880B | 🟡 | amp (Plexi 50) | knob 'Bass' folds (no param moves); knob 'Low-Mid Freq.' folds (no param moves); knob 'Mid Frequency' folds (no param moves); knob 'High-Mid' folds (no param moves); knob 'High-Mid Freq.' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 15 | `Amp_HG500` HG-500 | 🟡 | amp (Plexi 50) | knob 'Mid-Frequency' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 15 | `Bass_Amp_CS75B` CSS-75B | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 14 | `Amp_TW26` AMTW-26 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 14 | `Bass_Amp_CH300B` CLH-300B | 🟡 | amp (Plexi 50) | knob 'Mid-Frequency' folds (no param moves); knob 'Enhance' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 12 | `Amp_BT15` BTQ-15 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 12 | `Bass_Amp_HT400B` HW-400B | 🟡 | amp (Plexi 50) | knob 'High-Shift' folds (no param moves); knob 'Low-Shift' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 11 | `Amp_GB50` GBC-50 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 10 | `Amp_HG100` HG-100 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 10 | `Bass_Pedal_BassOverdrive` Bass Overdrive | 🟡 | bassod | knob 'Filter' folds (no param moves); bass drive: filter / presence / mid knob has no bassod param — folded |
| 10 | `DI_Amp_TubePre` DI. Pre Tube | 🟡 | amp (Plexi 50) | knob 'Mid. Freq. Select' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 9 | `Amp_EN30` EN-30 | 🟡 | amp (Plexi 50) | knob 'Bright' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 8 | `Amp_OrangeOR50H` Orange OR50H | 🟡 | amp (Plexi 50) | knob 'Punch' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 8 | `Bass_Amp_BT600B` BH-600B | 🟡 | amp (Plexi 50) | knob '180hz' folds (no param moves); knob '340hz' folds (no param moves); knob '1.2khz' folds (no param moves); knob '2.6khz' folds (no param moves); knob 'Compression' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 8 | `Pedal_PlateVerb` Plate Reverb | 🟡 | platereverb | knob 'Voice' folds (no param moves) |
| 7 | `Amp_BT30` BTQ-30 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 7 | `Amp_MarshallDSL100H` Marshall DSL100H | 🟡 | amp (Plexi 50) | knob 'Resonance' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 7 | `Bass_Amp_LT25B` LW-25B | 🟡 | amp (Plexi 50) | knob 'Mid-Frequency' folds (no param moves); knob 'High-Shift' folds (no param moves); knob 'Low-Shift' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 7 | `Pedal_CustomDrive` Custom Drive | 🟡 | overdrive | knob 'Voicing' folds (no param moves) |
| 6 | `Pedal_BakedRotatoe` Baked Rotatoe | 🟡 | rotary | knob 'Balance' folds (no param moves) |
| 5 | `Amp_CA85` Cali-85 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 5 | `Bass_Amp_BT975B` BH-975B | 🟡 | amp (Plexi 50) | knob 'Low-Shift' folds (no param moves); knob 'High-Shift' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 5 | `Pedal_AnalogueDelay` Analog Delay | 🟡 | bbddelay | delay time 1000 ms clamped to the BBD's 600 ms ceiling |
| 5 | `Pedal_USWah` US Wah | 🟡 | wah | knob 'Speed' folds (no param moves) |
| 5 | `Rack_TapeEcho` Tape Echo | 🟡 | tapedelay | knob 'Hi Filter' folds (no param moves); knob 'Lo Filter' folds (no param moves) |
| 4 | `Pedal_DigitalChorus` Digital Chorus | 🟡 | chorus | knob 'HiFilter' folds (no param moves) |
| 4 | `Rack_StudioDelay` Studio Delay | 🟡 | multitap | knob 'Time Right' folds (no param moves); second tap time approximated — multitap derives its taps from one base interval and a ratio set |
| 3 | `Bass_Pedal_BassAutoFilter` Bass Auto Filter | 🟡 | envfilter | knob 'Attack Speed' folds (no param moves); knob 'Release Speed' folds (no param moves); knob 'Filter Type' folds (no param moves) |
| 3 | `DI_Amp_BassDriver` DI. Bass Driver | 🟡 | amp (Plexi 50) | knob 'Blend' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 3 | `DI_Amp_MixerPre` DI. Mixer | 🟡 | amp (Plexi 50) | knob 'Tone' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 3 | `Pedal_ShredZone` Shred Zone | 🟡 | distortion | knob 'Bass' folds (no param moves); knob 'Mid' folds (no param moves); knob 'Treble' folds (no param moves) |
| 3 | `Pedal_UKWah` UK Wah | 🟡 | wah | knob 'Speed' folds (no param moves) |
| 2 | `Amp_CA38` Cali-38 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 2 | `Amp_CS120` CL-SS 120 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 2 | `Amp_GB38` GBC-38 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 2 | `Bass_Amp_CH600B` CLH-600B | 🟡 | amp (Plexi 50) | knob '30hz' folds (no param moves); knob '90hz' folds (no param moves); knob '250hz' folds (no param moves); knob '800hz' folds (no param moves); knob '2.5khz' folds (no param moves); knob '7.5khz' folds (no param moves); knob 'Compression' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 2 | `Bass_Amp_HT100B` HW100B | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 2 | `Bass_Amp_LT85B` LW-85B | 🟡 | amp (Plexi 50) | knob 'High-Shift' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 2 | `Pedal_AutoFilter` Auto Filter | 🟡 | envfilter | knob 'Attack Speed' folds (no param moves); knob 'Release Speed' folds (no param moves); knob 'Filter Type' folds (no param moves) |
| 2 | `Pedal_MultiPitch` MultiPitch | 🟡 | harmonizer | knob 'Tone' folds (no param moves); pitch shift of -24 semitones clamped to ±12; pitch shift of 24 semitones clamped to ±12 |
| 2 | `Pedal_NPNDelay` Classic NPN Delay | 🟡 | bbddelay | delay time 1000 ms clamped to the BBD's 600 ms ceiling |
| 1 | `Amp_BT45` BTQ-45 | 🟡 | amp (Plexi 50) | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 1 | `Bass_Amp_CS300B` CSS-300B | 🟡 | amp (Plexi 50) | knob 'Lo Mid' folds (no param moves); knob 'Hi Mid' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| 1 | `Bass_Pedal_BassFuzz` Bass Fuzz | 🟡 | fuzz | knob 'Tone' folds (no param moves) |
| 1 | `Pedal_AmpEQ` Amp EQ | 🟡 | eq | knob 'Bass Freq.' folds (no param moves); knob 'Mid Freq. Shift' folds (no param moves); knob 'Treble Freq.' folds (no param moves); BassFreq 0 Hz clamped to the eq lowFreq range 20-500 Hz; BassFreq 1 Hz clamped to the eq lowFreq range 20-500 Hz; TrebleFreq 0 Hz clamped to the eq highFreq range 2000-20000 Hz; TrebleFreq 1 Hz clamped to the eq highFreq range 2000-20000 Hz |
| 1 | `Pedal_ModDelay` Mod Delay | 🟡 | delay | knob 'Mod Depth' folds (no param moves); knob 'Mod Rate' folds (no param moves); modulated-delay depth/rate dropped (static delay) |
| 1 | `Rack_StudioFlanger` Studio Flanger | 🟡 | flanger | knob 'Tone' folds (no param moves) |

## Amps

| Gear | Category | Uses | Becomes | Knobs (RS → rig param) | Status | Notes |
|---|---|---:|---|---|---|---|
| `Amp_GB100`<br>GBC-1H | Amp | 66 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_HT300B`<br>HW-300B | Amp | 54 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Mid-Frequency [0..4 (enum)] → **folds**<br>Treble [1..100] → `amp.treble`<br>High-Shift [0..1 (enum)] → **folds**<br>Low-Shift [0..1 (enum)] → **folds** | 🟡 | knob 'Mid-Frequency' folds (no param moves); knob 'High-Shift' folds (no param moves); knob 'Low-Shift' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_OrangeOR100`<br>Orange OR100 | Amp | 31 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_EN50`<br>EN-50 | Amp | 30 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Bright [0..1] → **folds** | 🟡 | knob 'Bright' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_CA100`<br>Cali-100 | Amp | 28 | amp (Five-Oh-One Lead) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_TW22`<br>AMTW-22 | Amp | 25 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_CS90`<br>CL-SS 90 | Amp | 24 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_TW40`<br>AMTW-40 | Amp | 24 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_HG180`<br>HG-180 | Amp | 20 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_OrangeAD50`<br>Orange AD50 | Amp | 20 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.mid` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_AT20`<br>AT-20 | Amp | 19 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_AT120`<br>AT-120 | Amp | 17 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_CH350B`<br>CLH-350B | Amp | 17 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Treble [1..100] → `amp.treble`<br>30hz [-12..12 dB] → **folds**<br>90hz [-12..12 dB] → **folds**<br>250hz [-12..12 dB] → **folds**<br>800hz [-12..12 dB] → **folds**<br>2.5khz [-12..12 dB] → **folds**<br>7.5khz [-12..12 dB] → **folds**<br>15khz [-12..12 dB] → **folds** | 🟡 | knob '30hz' folds (no param moves); knob '90hz' folds (no param moves); knob '250hz' folds (no param moves); knob '800hz' folds (no param moves); knob '2.5khz' folds (no param moves); knob '7.5khz' folds (no param moves); knob '15khz' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_BT880B`<br>BH-880B | Amp | 16 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [-12..12 dB] → **folds**<br>Low-Mid [-12..12 dB] → `amp.bass`<br>Low-Mid Freq. [30..300 Hz] → **folds**<br>Mid [-12..12 dB] → `amp.mid`<br>Mid Frequency [200..2000 Hz] → **folds**<br>High-Mid [-12..12 dB] → **folds**<br>High-Mid Freq. [1.2..12 kHz] → **folds**<br>Treble [-12..12 dB] → `amp.treble` | 🟡 | knob 'Bass' folds (no param moves); knob 'Low-Mid Freq.' folds (no param moves); knob 'Mid Frequency' folds (no param moves); knob 'High-Mid' folds (no param moves); knob 'High-Mid Freq.' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_HG500`<br>HG-500 | Amp | 15 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Mid-Frequency [1..100] → **folds**<br>Treble [1..100] → `amp.treble` | 🟡 | knob 'Mid-Frequency' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_CS75B`<br>CSS-75B | Amp | 15 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [-12..12 dB] → `amp.bass`<br>Mid [-12..12 dB] → `amp.mid`<br>Treble [-12..12 dB] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_TW26`<br>AMTW-26 | Amp | 14 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_CH300B`<br>CLH-300B | Amp | 14 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [-12..12 dB] → `amp.bass`<br>Mid [-12..12 dB] → `amp.mid`<br>Mid-Frequency [200..2000 Hz] → **folds**<br>Treble [-12..12 dB] → `amp.treble`<br>Enhance [1..100 %] → **folds** | 🟡 | knob 'Mid-Frequency' folds (no param moves); knob 'Enhance' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_BT15`<br>BTQ-15 | Amp | 12 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_HT400B`<br>HW-400B | Amp | 12 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [-12..12 dB] → `amp.bass`<br>Mid [-12..12 dB] → `amp.mid`<br>Treble [-12..12 dB] → `amp.treble`<br>High-Shift [0..1 (enum)] → **folds**<br>Low-Shift [0..1 (enum)] → **folds** | 🟡 | knob 'High-Shift' folds (no param moves); knob 'Low-Shift' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_GB50`<br>GBC-50 | Amp | 11 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_HG100`<br>HG-100 | Amp | 10 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `DI_Amp_TubePre`<br>DI. Pre Tube | Amp | 10 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Mid. Freq. Select [0..2] → **folds** | 🟡 | knob 'Mid. Freq. Select' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_EN30`<br>EN-30 | Amp | 9 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Treble [1..100] → `amp.treble`<br>Mid [1..100] → `amp.mid`<br>Bright [0..1] → **folds**<br>Presence [1..100] → `amp.presence` | 🟡 | knob 'Bright' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_OrangeOR50H`<br>Orange OR50H | Amp | 8 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Punch [1..100] → **folds** | 🟡 | knob 'Punch' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_BT600B`<br>BH-600B | Amp | 8 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>180hz [-12..12 dB] → **folds**<br>340hz [-12..12 dB] → **folds**<br>Mid [1..100] → `amp.mid`<br>1.2khz [-12..12 dB] → **folds**<br>2.6khz [-12..12 dB] → **folds**<br>Treble [1..100] → `amp.treble`<br>Compression [0..100 %] → **folds** | 🟡 | knob '180hz' folds (no param moves); knob '340hz' folds (no param moves); knob '1.2khz' folds (no param moves); knob '2.6khz' folds (no param moves); knob 'Compression' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_BT30`<br>BTQ-30 | Amp | 7 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.presence`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_MarshallDSL100H`<br>Marshall DSL100H | Amp | 7 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence`<br>Resonance [1..100] → **folds** | 🟡 | knob 'Resonance' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_LT25B`<br>LW-25B | Amp | 7 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Mid-Frequency [0..2 (enum)] → **folds**<br>Treble [1..100] → `amp.treble`<br>High-Shift [0..1 (enum)] → **folds**<br>Low-Shift [0..1 (enum)] → **folds** | 🟡 | knob 'Mid-Frequency' folds (no param moves); knob 'High-Shift' folds (no param moves); knob 'Low-Shift' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_CA85`<br>Cali-85 | Amp | 5 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_BT975B`<br>BH-975B | Amp | 5 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Low-Shift [0..1 (enum)] → **folds**<br>Bass [-12..12 dB] → `amp.bass`<br>Mid [-12..12 dB] → `amp.mid`<br>High-Shift [0..1 (enum)] → **folds**<br>Treble [-12..12 dB] → `amp.treble` | 🟡 | knob 'Low-Shift' folds (no param moves); knob 'High-Shift' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `DI_Amp_BassDriver`<br>DI. Bass Driver | Amp | 3 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Presence [1..100] → `amp.presence`<br>Treble [1..100] → `amp.treble`<br>Blend [0..100 %] → **folds** | 🟡 | knob 'Blend' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `DI_Amp_MixerPre`<br>DI. Mixer | Amp | 3 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Tone [1..100] → **folds** | 🟡 | knob 'Tone' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_CA38`<br>Cali-38 | Amp | 2 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_CS120`<br>CL-SS 120 | Amp | 2 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_GB38`<br>GBC-38 | Amp | 2 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_CH600B`<br>CLH-600B | Amp | 2 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [-12..12 dB] → `amp.bass`<br>Treble [-12..12 dB] → `amp.treble`<br>30hz [-12..12 dB] → **folds**<br>90hz [-12..12 dB] → **folds**<br>250hz [-12..12 dB] → **folds**<br>800hz [-12..12 dB] → **folds**<br>2.5khz [-12..12 dB] → **folds**<br>7.5khz [-12..12 dB] → **folds**<br>Compression [1..100] → **folds** | 🟡 | knob '30hz' folds (no param moves); knob '90hz' folds (no param moves); knob '250hz' folds (no param moves); knob '800hz' folds (no param moves); knob '2.5khz' folds (no param moves); knob '7.5khz' folds (no param moves); knob 'Compression' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_HT100B`<br>HW100B | Amp | 2 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_LT85B`<br>LW-85B | Amp | 2 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>High-Shift [0..1 (enum)] → **folds** | 🟡 | knob 'High-Shift' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_BT45`<br>BTQ-45 | Amp | 1 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_CS300B`<br>CSS-300B | Amp | 1 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Lo Mid [1..100] → **folds**<br>Hi Mid [1..100] → **folds**<br>Treble [1..100] → `amp.treble` | 🟡 | knob 'Lo Mid' folds (no param moves); knob 'Hi Mid' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_CS100`<br>CL-SS 100 | Amp | 0 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_EpiphoneElectarCentury`<br>Epiphone Electar Century | Amp | 0 | amp (Plexi 50) | Volume [1..100] → `amp.gain` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_EpiphoneElectarM`<br>Epiphone Electar Model M | Amp | 0 | amp (Plexi 50) | Gain [1..100] → `amp.gain` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_EpiphoneElectricHawaiian`<br>Epiphone Electric Hawaiian | Amp | 0 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Tone [1..100] → **folds** | 🟡 | knob 'Tone' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_EpiphoneZephyr`<br>Epiphone Zephyr | Amp | 0 | amp (Plexi 50) | Speaker [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_GibsonGA79`<br>Gibson GA-79 | Amp | 0 | amp (Plexi 50) | Volume 1 [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_GibsonGA8`<br>Gibson GA-8 | Amp | 0 | amp (Plexi 50) | Loudness [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_GibsonGA88`<br>Gibson GA-88 | Amp | 0 | amp (Plexi 50) | Volume [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_Marshall1962Bluesbreaker`<br>Marshall 1962 "Bluesbreaker" | Amp | 0 | amp (Plexi 50) | Volume [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_MarshallDSL15H`<br>Marshall DSL15H | Amp | 0 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_MarshallJCM800`<br>Marshall JCM800 | Amp | 0 | amp (Plexi 50) | PreAmp [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_MarshallJTM45`<br>Marshall JTM45 | Amp | 0 | amp (Plexi 50) | Vol 1 [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_MarshallJVM410H`<br>Marshall JVM410H | Amp | 0 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_MarshallPlexi`<br>Marshall Plexi | Amp | 0 | amp (Plexi 50) | Loudness 1 [1..100] → `amp.gain`<br>Loudness 2 [1..100] → `amp.master`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble`<br>Presence [1..100] → `amp.presence` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_OrangeJimmyBean`<br>Orange 'Jimmy Bean' Amp | Amp | 0 | amp (Plexi 50) | Volume [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_OrangeRockerverb`<br>Orange Rockerverb 50 MkII | Amp | 0 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Amp_OrangeTinyTerror`<br>Orange Tiny Terror | Amp | 0 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Tone [1..100] → **folds** | 🟡 | knob 'Tone' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_CS240B`<br>CSS-240B | Amp | 0 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_EdenE300`<br>Eden E300 | Amp | 0 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [-12..12 dB] → `amp.bass`<br>Mid [-12..12 dB] → `amp.mid`<br>Treble [-12..12 dB] → `amp.treble`<br>Enhance [1..100 %] → **folds** | 🟡 | knob 'Enhance' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_EdenWT550`<br>Eden WT550 | Amp | 0 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [-12..12 dB] → **folds**<br>Low-Mid [-12..12 dB] → `amp.bass`<br>Low-Mid Freq. [30..300 Hz] → **folds**<br>Mid [-12..12 dB] → `amp.mid`<br>Mid Frequency [200..2000 Hz] → **folds**<br>High-Mid [-12..12 dB] → **folds**<br>High-Mid Freq. [1.2..12 kHz] → **folds**<br>Treble [-12..12 dB] → `amp.treble`<br>Enhance [1..100 %] → **folds** | 🟡 | knob 'Bass' folds (no param moves); knob 'Low-Mid Freq.' folds (no param moves); knob 'Mid Frequency' folds (no param moves); knob 'High-Mid' folds (no param moves); knob 'High-Mid Freq.' folds (no param moves); knob 'Enhance' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_EdenWT800`<br>Eden WT800 | Amp | 0 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [-12..12 dB] → **folds**<br>Low-Mid [-12..12 dB] → `amp.bass`<br>Low-Mid Freq. [30..300 Hz] → **folds**<br>Mid [-12..12 dB] → `amp.mid`<br>Mid Frequency [200..2000 Hz] → **folds**<br>High-Mid [-12..12 dB] → **folds**<br>High-Mid Freq. [1.2..12 kHz] → **folds**<br>Treble [-12..12 dB] → `amp.treble`<br>Enhance [1..100 %] → **folds** | 🟡 | knob 'Bass' folds (no param moves); knob 'Low-Mid Freq.' folds (no param moves); knob 'Mid Frequency' folds (no param moves); knob 'High-Mid' folds (no param moves); knob 'High-Mid Freq.' folds (no param moves); knob 'Enhance' folds (no param moves); not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |
| `Bass_Amp_OrangeAD200B`<br>Orange AD200B | Amp | 0 | amp (Plexi 50) | Gain [1..100] → `amp.gain`<br>Bass [1..100] → `amp.bass`<br>Mid [1..100] → `amp.mid`<br>Treble [1..100] → `amp.treble` | 🟡 | not an amp Rocksmith ships: the nearest guitar-amp roster model by its key; not the game's own chain: the guitar amp's substring guess |

## Cabinets

450 keys: 49 families × {57, Condenser, Tube} × {Cone, Edge, OffAxis}, 29 of them also
with Ribbon, plus 9 one-off cabinets. One row per family; the mic columns show what
each mic/position variant becomes.

| Family | Name | Variants | Uses | Becomes | ✅ | 🟡 | ❌ | Notes |
|---|---|---:|---:|---|---:|---:|---:|---|
| `Bass_Cab_AT810BC` | AT-810BC | 9 | 84 | cab (8x10 Bass Fridge) | 9 | 0 | 0 |  |
| `Cab_TW112C` | TW-112C | 9 | 65 | cab (1x12 Combo) | 9 | 0 | 0 |  |
| `Cab_GB412CMKIII` | GB-412 MKIII | 9 | 62 | cab (4x12 Greenback) | 9 | 0 | 0 |  |
| `Cab_OrangePPC212OB` | Orange PPC212OB | 9 | 62 | cab (2x12 AC Open) | 9 | 0 | 0 |  |
| `Cab_CA412C` | CA-412C | 9 | 45 | cab (4x12 V30 Modern) | 9 | 0 | 0 |  |
| `Cab_EN212C` | EN-212C | 9 | 24 | cab (2x12 Boutique Closed) | 9 | 0 | 0 |  |
| `Cab_CS212C` | CSS-212C | 9 | 23 | cab (2x12 Combo) | 9 | 0 | 0 |  |
| `Cab_TW410C` | TW-410C | 9 | 23 | cab (4x10 Tweed Open) | 9 | 0 | 0 |  |
| `Bass_Cab_BT410BC` | BH-410BC | 9 | 22 | cab (4x10 Bass) | 9 | 0 | 0 |  |
| `Cab_GB412CMKII` | GB-412 MKII | 9 | 22 | cab (4x12 Greenback) | 9 | 0 | 0 |  |
| `Cab_TW110C` | TW-110C | 9 | 17 | cab (1x10 Combo) | 9 | 0 | 0 |  |
| `Bass_Cab_AT1150BC` | AT-1150BC | 9 | 16 | cab (1x15 Vintage Bass) | 9 | 0 | 0 |  |
| `Cab_PA600C` | PS-600 | 1 | 16 | cab (PA 15 + Horn) | 1 | 0 | 0 |  |
| `Bass_Cab_CA1510BC` | CA-1510BC | 9 | 12 | cab (1x15 + 1x10 Bass) | 9 | 0 | 0 |  |
| `Cab_HG2120C` | HG-2120C | 9 | 11 | cab (2x12 Boutique Closed) | 9 | 0 | 0 |  |
| `Bass_Cab_BT212BC` | BH-212BC | 9 | 10 | cab (2x12 Bass) | 9 | 0 | 0 |  |
| `Cab_AT0112C` | AT-0112C | 9 | 10 | cab (1x12 Combo) | 9 | 0 | 0 |  |
| `Cab_GB412CMKI` | GB-412 MKI | 9 | 8 | cab (4x12 Greenback) | 9 | 0 | 0 |  |
| `Cab_CA112C` | CA-112C | 9 | 7 | cab (1x12 Combo) | 9 | 0 | 0 |  |
| `Cab_Marshall1960TV` | Marshall 1960 TV | 9 | 7 | cab (4x12 Greenback) | 9 | 0 | 0 |  |
| `Cab_BT1120C` | BT-1120C | 9 | 5 | cab (1x12 Combo) | 9 | 0 | 0 |  |
| `Bass_Cab_BT115BC` | BH-115BC | 9 | 4 | cab (1x15 Vintage Bass) | 9 | 0 | 0 |  |
| `Cab_HG215C` | HG-215C | 9 | 4 | cab (2x15 Guitar) | 9 | 0 | 0 |  |
| `Bass_Cab_CH410BC` | CH-410BC | 9 | 3 | cab (4x10 Bass) | 9 | 0 | 0 |  |
| `Cab_EN4120C` | EN-4120C | 9 | 3 | cab (4x12 V30 Modern) | 9 | 0 | 0 |  |
| `Cab_HG212C` | HG-212C | 9 | 3 | cab (2x12 Boutique Closed) | 9 | 0 | 0 |  |
| `Bass_Cab_TW215BC` | TW-215BC | 9 | 2 | cab (2x15 Bass) | 9 | 0 | 0 |  |
| `Cab_CS1515C` | CSS-1515C | 9 | 2 | cab (2x15 Guitar) | 9 | 0 | 0 |  |
| `Cab_PA999C` | PA-999C | 1 | 2 | cab (PA Column) | 1 | 0 | 0 |  |
| `Bass_Cab_CS410BC` | CS-410BC | 9 | 1 | cab (4x10 Bass) | 9 | 0 | 0 |  |
| `Cab_CA215C` | CA-215C | 9 | 1 | cab (2x15 Guitar) | 9 | 0 | 0 |  |
| `Cab_CS1120C` | CSS-1120C | 9 | 1 | cab (1x12 Combo) | 9 | 0 | 0 |  |
| `Cab_Marshall1960AX` | Marshall 1960 AX | 9 | 1 | cab (4x12 Greenback) | 9 | 0 | 0 |  |
| `Cab_Marshall1960a` | Marshall 1960a | 9 | 1 | cab (4x12 Scooped) | 9 | 0 | 0 |  |
| `Cab_PA1152C` | PS-115.2C | 1 | 1 | cab (PA 15 + Horn) | 1 | 0 | 0 |  |
| `Bass_Cab_CH210BC` | CH-21BC | 9 | 0 | cab (2x10 Bass) | 9 | 0 | 0 |  |
| `Bass_Cab_CH310BC` | CH-3120BC | 9 | 0 | cab (3x10 Bass) | 9 | 0 | 0 |  |
| `Bass_Cab_CS112BC` | CS-112BC | 9 | 0 | cab (1x12 Bass) | 9 | 0 | 0 |  |
| `Bass_Cab_CS15BC` | CS-15BC | 9 | 0 | cab (1x15 Vintage Bass) | 9 | 0 | 0 |  |
| `Bass_Cab_EdenD115XLT` | Eden D115XLT | 9 | 0 | cab (1x15 Vintage Bass) | 9 | 0 | 0 |  |
| `Bass_Cab_EdenD212XLT` | Eden D212XLT | 9 | 0 | cab (2x12 Bass) | 9 | 0 | 0 |  |
| `Bass_Cab_EdenD410XST` | Eden D410XST | 9 | 0 | cab (4x10 Bass) | 9 | 0 | 0 |  |
| `Bass_Cab_EdenD610XST` | Eden D610XST | 9 | 0 | cab (6x10 Bass) | 9 | 0 | 0 |  |
| `Bass_Cab_GB415BC` | GB-415BC | 9 | 0 | cab (4x15 Bass) | 9 | 0 | 0 |  |
| `Bass_Cab_OrangeOBC115` | Orange OBC115 | 9 | 0 | cab (1x15 Vintage Bass) | 9 | 0 | 0 |  |
| `Bass_Cab_OrangeOBC810` | Orange OBC810 | 9 | 0 | cab (8x10 Bass Fridge) | 9 | 0 | 0 |  |
| `Cab_AT1121C` | AT-112.1C | 9 | 0 | cab (1x12 Combo) | 9 | 0 | 0 |  |
| `Cab_Audiophile` | Audiophile Speakers | 1 | 0 | cab (Hi-Fi Tower) | 1 | 0 | 0 |  |
| `Cab_BT1121C` | BT-112.1C | 9 | 0 | cab (1x12 Combo) | 9 | 0 | 0 |  |
| `Cab_BT410C` | BT-410C | 9 | 0 | cab (4x10 Tweed Open) | 9 | 0 | 0 |  |
| `Cab_BoomBox` | Boom Box | 1 | 0 | cab (Vintage 4x12) | 0 | 1 | 0 | nearest roster IR, not the same speaker layout: cab (Vintage 4x12) |
| `Cab_CabinetRadio` | Console Radio | 1 | 0 | cab (Vintage 4x12) | 0 | 1 | 0 | nearest roster IR, not the same speaker layout: cab (Vintage 4x12) |
| `Cab_Gramophone` | Gramophone | 1 | 0 | cab (Vintage 4x12) | 0 | 1 | 0 | nearest roster IR, not the same speaker layout: cab (Vintage 4x12) |
| `Cab_JukeBox` | Jukebox | 1 | 0 | cab (Vintage 4x12) | 0 | 1 | 0 | nearest roster IR, not the same speaker layout: cab (Vintage 4x12) |
| `Cab_Marshall1936` | Marshall 1936 | 9 | 0 | cab (2x12 Boutique Closed) | 9 | 0 | 0 |  |
| `Cab_OrangeJimmyBean` | Orange 'Jimmy Bean' Cab | 9 | 0 | cab (4x12 V30 Modern) | 9 | 0 | 0 |  |
| `Cab_OrangePPC412` | Orange PPC412 | 9 | 0 | cab (4x12 V30 Modern) | 9 | 0 | 0 |  |
| `Cab_VintageHiFi` | Vintage Hi-Fi setup | 1 | 0 | cab (Hi-Fi Tower) | 0 | 1 | 0 | nearest roster IR, not the same speaker layout: cab (Hi-Fi Tower) |

### Mic and position

What one representative family's nine variants become (the mic layer is the same for every family).

| Variant | Uses (all families) | Mic layer |
|---|---:|---|
| 57 / Cone | 115 | micpos (57) position 0 |
| 57 / Edge | 96 | micpos (57) position 1 |
| 57 / OffAxis | 5 | micpos (57) position 0, angle 45° |
| Condenser / Cone | 92 | micpos (Condenser) position 0 |
| Condenser / Edge | 23 | micpos (Condenser) position 1 |
| Condenser / OffAxis | 21 | micpos (Condenser) position 0, angle 45° |
| Ribbon / Cone | 121 | micpos (121) position 0 |
| Ribbon / Edge | 40 | micpos (121) position 1 |
| Ribbon / OffAxis | 4 | micpos (121) position 0, angle 45° |
| Tube / Cone | 25 | micpos (Tube) position 0 |
| Tube / Edge | 17 | micpos (Tube) position 1 |
| Tube / OffAxis | 2 | micpos (Tube) position 0, angle 45° |

## Pedals

| Gear | Category | Uses | Becomes | Knobs (RS → rig param) | Status | Notes |
|---|---|---:|---|---|---|---|
| `Pedal_Compression`<br>Compression | Dynamics | 164 | compressor | Compression [1..100] → `compressor.ratio`<br>Attack [1..100 ms] → `compressor.attackMs`<br>Release [10..1000 ms] → `compressor.releaseMs` | ✅ |  |
| `Pedal_EQ8`<br>8-Band Graphic EQ | Filter | 152 | graphiceq | 50HZ [-15..15 dB] → `graphiceq.band63`<br>100HZ [-15..15 dB] → `graphiceq.band125`<br>200HZ [-15..15 dB] → `graphiceq.band250`<br>400HZ [-15..15 dB] → `graphiceq.band500`<br>800HZ [-15..15 dB] → `graphiceq.band1k`<br>1.6KHZ [-12..12 dB] → `graphiceq.band2k`<br>3.2KHZ [-12..12 dB] → `graphiceq.band4k`<br>6.4KHZ [-12..12 dB] → `graphiceq.band8k` | ✅ |  |
| `Bass_Pedal_MBComp`<br>MB Comp | Dynamics | 113 | multiband | Compression [1..100] → `multiband.highRatio`, `multiband.lowRatio`, `multiband.midRatio`<br>Recovery Speed [1..100] → `multiband.releaseMs`<br>Transition Band [100..1000 Hz] → `multiband.lowXoverHz` | 🟡 | MB Comp: the game's own model is read from a Rocksmith 2014 install; without one it is converted by its knob labels |
| `Pedal_SpringReverb`<br>Spring Reverb | Reverb | 93 | springreverb | Time [1..100] → `springreverb.decay`<br>Depth [1..100] → `springreverb.drip`<br>Mix [1..100 %] → `springreverb.mix` | ✅ |  |
| `Pedal_GermaniumDrive`<br>Germanium Drive | Distortion | 61 | overdrive | Gain [1..100] → `overdrive.drive`<br>Tone [1..100] → `overdrive.tone` | ✅ |  |
| `Pedal_EQ5`<br>5-Band Graphic EQ | Filter | 39 | graphiceq | 63hz [-15..15 dB] → `graphiceq.band63`<br>250hz [-15..15 dB] → `graphiceq.band250`<br>750hz [-15..15 dB] → `graphiceq.band1k`<br>2.2khz [-15..15 dB] → `graphiceq.band2k`<br>5.7khz [-15..15 dB] → `graphiceq.band8k` | ✅ |  |
| `Pedal_AcousticEmulator`<br>Acoustic Emulator | Filter | 29 | acousticsim | Body [1..100] → `acousticsim.body`<br>Mid-range [1..100] → **folds**<br>Mid Frequency [50..5000 Hz] → **folds**<br>Tone [1..100] → `acousticsim.brightness` | 🟡 | knob 'Mid-range' folds (no param moves); knob 'Mid Frequency' folds (no param moves) |
| `Pedal_Chorus`<br>Chorus | Modulation | 22 | chorus | Rate [0.1..10 Hz] → `chorus.rateHz`<br>Depth [1..100 %] → `chorus.depthMs`<br>Mix [0..100 %] → `chorus.mix` | ✅ |  |
| `Bass_Pedal_BassEQ8`<br>8-Band Bass EQ | Filter | 21 | graphiceq | 30hz [-15..15 dB] → `graphiceq.band31`<br>75hz [-15..15 dB] → `graphiceq.band63`<br>185hz [-15..15 dB] → `graphiceq.band250`<br>460hz [-15..15 dB] → `graphiceq.band500`<br>1.1khz [-15..15 dB] → `graphiceq.band1k`<br>2.7khz [-15..15 dB] → `graphiceq.band2k`<br>6.8khz [-15..15 dB] → `graphiceq.band8k`<br>16khz [-15..15 dB] → `graphiceq.band16k` | ✅ |  |
| `Pedal_DigitalVerb`<br>Digital Verb | Reverb | 20 | hallreverb | Time [1..100] → `hallreverb.decayS`<br>Depth [1..100] → `hallreverb.diffusion`<br>Mix [1..100 %] → `hallreverb.mix`<br>Tone [1..100] → `hallreverb.dampingKHz` | ✅ |  |
| `Pedal_LoFiFilter`<br>Lo-Fi Filter | Filter | 13 | svf | Hi Cut [1..100] → `svf.mode`<br>Lo Cut [1..100] → `svf.mix` | ✅ |  |
| `Pedal_OctaveUp`<br>OctaveUp! | Pitch | 12 | octfuzz | Octave-UP! [1..100 %] → `octfuzz.octave`<br>Tone [1..100] → `octfuzz.tone` | ✅ |  |
| `Bass_Pedal_BassOverdrive`<br>Bass Overdrive | Distortion | 10 | bassod | Gain [1..100] → `bassod.drive`<br>Tone [1..100] → `bassod.treble`<br>Blend [1..100 %] → `bassod.blend`<br>Filter [1..100 %] → **folds** | 🟡 | knob 'Filter' folds (no param moves); bass drive: filter / presence / mid knob has no bassod param — folded |
| `Pedal_CosmicEcho`<br>Cosmonaut Echo | Delay | 9 | delay | Time [10..1000 ms] → `delay.timeMs`<br>Feedback [0..100 %] → `delay.feedback`<br>Mix [1..100 %] → `delay.mix` | ✅ |  |
| `Pedal_PlateVerb`<br>Plate Reverb | Reverb | 8 | platereverb | Time [1..100] → `platereverb.decay`<br>Depth [1..100] → `platereverb.width`<br>Mix [1..100 %] → `platereverb.mix`<br>Voice [0..1 (enum)] → **folds** | 🟡 | knob 'Voice' folds (no param moves) |
| `Pedal_CustomDrive`<br>Custom Drive | Distortion | 7 | overdrive | Gain [1..100] → `overdrive.drive`<br>Tone [1..100] → `overdrive.tone`<br>Voicing [0..1 (enum)] → **folds** | 🟡 | knob 'Voicing' folds (no param moves) |
| `Pedal_BakedRotatoe`<br>Baked Rotatoe | Modulation | 6 | rotary | Rate [1..100] → `rotary.speed`<br>Depth [1..100 %] → `rotary.depth`<br>Mix [1..100 %] → `rotary.mix`<br>Balance [1..100 %] → **folds** | 🟡 | knob 'Balance' folds (no param moves) |
| `Pedal_Phaser`<br>Phaser-363 | Modulation | 6 | phaser | Rate [1..100] → `phaser.rateHz` | ✅ |  |
| `Pedal_RangeBooster`<br>Range Booster | Filter | 6 | boost | Boost! [1..100 %] → `boost.gainDb` | ✅ |  |
| `Pedal_80sFlanger`<br>80's Flanger | Modulation | 5 | flanger | Rate [1..100] → `flanger.rateHz`<br>Depth [1..100 %] → `flanger.depthMs`<br>Mix [1..100 %] → `flanger.mix` | ✅ |  |
| `Pedal_AmpTrem`<br>Amp Trem | Tremolo | 5 | tremolo | Speed [1..100] → `tremolo.rateHz`<br>Mix [1..100] → `tremolo.depth` | ✅ |  |
| `Pedal_AnalogueDelay`<br>Analog Delay | Delay | 5 | bbddelay | Time [10..1000 ms] → `bbddelay.timeMs`<br>Feedback [0..100 %] → `bbddelay.feedback`<br>Mix [1..100 %] → `bbddelay.mix` | 🟡 | delay time 1000 ms clamped to the BBD's 600 ms ceiling |
| `Pedal_CaptFuzzle`<br>Captain Fuzzle | Distortion | 5 | fuzz | Gain [1..150] → `fuzz.fuzz`<br>Tone [1..100] → `fuzz.tone` | ✅ |  |
| `Pedal_USWah`<br>US Wah | Filter | 5 | wah | Auto-Mode [0..1 (enum)] → `wah.mode`<br>Sensitivity [1..100] → `wah.sensitivity`<br>Speed [1..100] → **folds**<br>Pedal (Auto=OFF) [1..100] → `wah.position` | 🟡 | knob 'Speed' folds (no param moves) |
| `Bass_Pedal_BassChorus`<br>Bass Chorus | Modulation | 4 | chorus | Rate [0.1..10 Hz] → `chorus.rateHz`<br>Mix [0..100 %] → `chorus.mix`<br>Depth [1..100 %] → `chorus.depthMs`<br>Low Filter [1..100] → `chorus.wetHpfHz` | ✅ |  |
| `Pedal_Chorus20`<br>Chorus 2.0 | Modulation | 4 | chorus | Rate [0.1..10 Hz] → `chorus.rateHz`<br>Depth [1..100 %] → `chorus.depthMs`<br>Mix [0..100 %] → `chorus.mix` | ✅ |  |
| `Pedal_DigitalChorus`<br>Digital Chorus | Modulation | 4 | chorus | Rate [0.1..10 Hz] → `chorus.rateHz`<br>Mix [0..100 %] → `chorus.mix`<br>Depth [1..100 %] → `chorus.depthMs`<br>LoFilter [1..100 %] → `chorus.wetHpfHz`<br>HiFilter [1..100 %] → **folds** | 🟡 | knob 'HiFilter' folds (no param moves) |
| `Pedal_MetalDistortion`<br>Alloy Distortion | Distortion | 4 | hm2 | Gain [1..100] → `hm2.dist`<br>Tone [1..100] → `hm2.highDb`, `hm2.lowDb` | ✅ |  |
| `Pedal_ModernFlanger`<br>Modern Flange | Modulation | 4 | flanger | Rate [1..100] → `flanger.rateHz`<br>Depth [1..100 %] → `flanger.depthMs`<br>Mix [1..100 %] → `flanger.mix`<br>Regen. [1..100 %] → `flanger.feedback` | ✅ |  |
| `Pedal_Octavius`<br>Octavius | Pitch | 4 | octfuzz | Mix [1..100 %] → `octfuzz.octave`<br>Tone [1..100] → `octfuzz.tone` | ✅ |  |
| `Pedal_Tremolo`<br>Tremolo | Tremolo | 4 | tremolo | Speed [1..100] → `tremolo.rateHz`<br>Mix [1..100 %] → `tremolo.depth` | ✅ |  |
| `Bass_Pedal_BassAutoFilter`<br>Bass Auto Filter | Filter | 3 | envfilter | Sensitivity [1..100] → `envfilter.sensitivity`<br>Attack Speed [1..250 ms] → **folds**<br>Release Speed [10..1000 ms] → **folds**<br>Filter Type [0..2 (enum)] → **folds**<br>Mix [1..100 %] → `envfilter.resonance` | 🟡 | knob 'Attack Speed' folds (no param moves); knob 'Release Speed' folds (no param moves); knob 'Filter Type' folds (no param moves) |
| `Pedal_BuzzToo`<br>Buzz 2 | Distortion | 3 | fuzz | Gain [1..100] → `fuzz.fuzz`<br>Tone [1..100] → `fuzz.tone` | ✅ |  |
| `Pedal_NoiseGate`<br>Noise Gate | Dynamics | 3 | gate | Threshold [-90..-40 dB] → `gate.thresholdDb`<br>Rate [1..100] → `gate.releaseMs` | ✅ |  |
| `Pedal_ShredZone`<br>Shred Zone | Distortion | 3 | distortion | Gain [1..100] → `distortion.gain`<br>Bass [-9..9 dB] → **folds**<br>Mid [-9..9 dB] → **folds**<br>Treble [-9..9 dB] → **folds** | 🟡 | knob 'Bass' folds (no param moves); knob 'Mid' folds (no param moves); knob 'Treble' folds (no param moves) |
| `Pedal_UKWah`<br>UK Wah | Filter | 3 | wah | Auto-Mode [0..1 (enum)] → `wah.mode`<br>Sensitivity [1..100] → `wah.sensitivity`<br>Speed [1..100] → **folds**<br>Pedal (Auto=OFF) [1..100] → `wah.position` | 🟡 | knob 'Speed' folds (no param moves) |
| `Pedal_ValveEcho`<br>Valve Echo | Delay | 3 | tapedelay | Time [10..1000 ms] → `tapedelay.timeMs`<br>Feedback [0..100 %] → `tapedelay.feedback`<br>Mix [1..100 %] → `tapedelay.mix` | ✅ |  |
| `Pedal_AutoFilter`<br>Auto Filter | Filter | 2 | envfilter | Sensitivity [1..100] → `envfilter.sensitivity`<br>Attack Speed [1..250 ms] → **folds**<br>Release Speed [10..1000 ms] → **folds**<br>Filter Type [0..2 (enum)] → **folds**<br>Mix [1..100 %] → `envfilter.resonance` | 🟡 | knob 'Attack Speed' folds (no param moves); knob 'Release Speed' folds (no param moves); knob 'Filter Type' folds (no param moves) |
| `Pedal_ClassicFlanger`<br>Classic Flange | Modulation | 2 | flanger | Rate [1..100] → `flanger.rateHz`<br>Depth [1..100 %] → `flanger.depthMs`<br>Mix [1..100 %] → `flanger.mix` | ✅ |  |
| `Pedal_FuzzWasHe`<br>Fuzz Was He? | Distortion | 2 | bigmuff | Gain [1..100] → `bigmuff.sustain`<br>Tone [1..100] → `bigmuff.tone` | ✅ |  |
| `Pedal_MultiPitch`<br>MultiPitch | Pitch | 2 | harmonizer | Pitch 1 [-24..24] → `harmonizer.semitones`<br>Mix [1..100 %] → `harmonizer.mix`<br>Tone [1..100] → **folds** | 🟡 | knob 'Tone' folds (no param moves); pitch shift of -24 semitones clamped to ±12; pitch shift of 24 semitones clamped to ±12 |
| `Pedal_MultiTrem`<br>Multi-Trem | Tremolo | 2 | tremolo | Speed [1..100] → `tremolo.rateHz`<br>Mix [1..100 %] → `tremolo.depth`<br>Waveform [0..4 (enum)] → `tremolo.shape` | ✅ |  |
| `Pedal_NPNDelay`<br>Classic NPN Delay | Delay | 2 | bbddelay | Time [10..1000 ms] → `bbddelay.timeMs`<br>Feedback [0..100 %] → `bbddelay.feedback`<br>Mix [1..100 %] → `bbddelay.mix` | 🟡 | delay time 1000 ms clamped to the BBD's 600 ms ceiling |
| `Pedal_NoFiEcho`<br>No-Fi Echo | Delay | 2 | delay | Time [10..1000 ms] → `delay.timeMs`<br>Feedback [0..100 %] → `delay.feedback`<br>Mix [1..100 %] → `delay.mix` | ✅ |  |
| `Pedal_SuperDrive`<br>Super Drive | Distortion | 2 | overdrive | Gain [1..100] → `overdrive.drive`<br>Tone [1..100] → `overdrive.tone` | ✅ |  |
| `Pedal_TubeSpring`<br>Tube Spring Reverb | Reverb | 2 | springreverb | Mix [1..100] → `springreverb.mix`<br>Depth [1..100] → `springreverb.drip` | ✅ |  |
| `Bass_Pedal_BassFuzz`<br>Bass Fuzz | Distortion | 1 | fuzz | Gain [1..100] → `fuzz.fuzz`<br>Tone [1..100] → **folds**<br>Filter [1..100 %] → `fuzz.tone` | 🟡 | knob 'Tone' folds (no param moves) |
| `Pedal_AmpEQ`<br>Amp EQ | Filter | 1 | eq | Bass [1..100] → `eq.lowGainDb`<br>Bass Freq. [0..1 (enum)] → **folds**<br>Midrange [1..100] → `eq.midGainDb`<br>Mid Freq. Shift [0..2 (enum)] → **folds**<br>Treble [1..100] → `eq.highGainDb`<br>Treble Freq. [0..1 (enum)] → **folds** | 🟡 | knob 'Bass Freq.' folds (no param moves); knob 'Mid Freq. Shift' folds (no param moves); knob 'Treble Freq.' folds (no param moves); BassFreq 0 Hz clamped to the eq lowFreq range 20-500 Hz; BassFreq 1 Hz clamped to the eq lowFreq range 20-500 Hz; TrebleFreq 0 Hz clamped to the eq highFreq range 2000-20000 Hz; TrebleFreq 1 Hz clamped to the eq highFreq range 2000-20000 Hz |
| `Pedal_AmpVibe`<br>Amp Vibe | Tremolo | 1 | vibrato | Speed [1..100] → `vibrato.rateHz`<br>Mix [1..100 %] → `vibrato.depthMs` | ✅ |  |
| `Pedal_BuzzOne`<br>Buzz 1 | Distortion | 1 | fuzz | Gain [1..100] → `fuzz.fuzz`<br>Tone [1..100] → `fuzz.tone` | ✅ |  |
| `Pedal_Distortion`<br>Distortion | Distortion | 1 | ds1 | Gain [1..100] → `ds1.dist`<br>Tone [1..100] → `ds1.tone` | ✅ |  |
| `Pedal_Enbiggenator`<br>Enbiggenator | Modulation | 1 | chorus | Rate [1..100] → `chorus.rateHz`<br>Depth [1..100 %] → `chorus.depthMs`<br>Mix [1..100 %] → `chorus.mix` | ✅ |  |
| `Pedal_LineDrive`<br>Line Drive | Distortion | 1 | overdrive | Gain [1..100] → `overdrive.drive`<br>Tone [1..100] → `overdrive.tone` | ✅ |  |
| `Pedal_ModDelay`<br>Mod Delay | Delay | 1 | delay | Time [10..1000 ms] → `delay.timeMs`<br>Feedback [0..100 %] → `delay.feedback`<br>Mix [1..100 %] → `delay.mix`<br>Mod Depth [1..100 %] → **folds**<br>Mod Rate [0.1..10 Hz] → **folds** | 🟡 | knob 'Mod Depth' folds (no param moves); knob 'Mod Rate' folds (no param moves); modulated-delay depth/rate dropped (static delay) |
| `Pedal_OmniMod`<br>Omni-Mod | Modulation | 1 | chorus | Rate [1..100] → `chorus.rateHz`<br>Depth [1..100 %] → `chorus.depthMs`<br>Mix [1..100 %] → `chorus.mix` | ✅ |  |
| `Pedal_PlanePhase`<br>Plane Phase | Modulation | 1 | phaser | Rate [1..100] → `phaser.rateHz`<br>Depth [1..100 %] → `phaser.depth`<br>Mix [1..100 %] → `phaser.mix` | ✅ |  |
| `Pedal_ShaverPhaser`<br>Shaver Phaser | Modulation | 1 | phaser | Rate [1..100] → `phaser.rateHz`<br>Depth [1..100 %] → `phaser.depth` | ✅ |  |
| `Pedal_VintageChorus`<br>Vintage Chorus | Modulation | 1 | chorus | Rate [0.1..10 Hz] → `chorus.rateHz`<br>Mix [0..100 %] → `chorus.mix`<br>Depth [1..100 %] → `chorus.depthMs` | ✅ |  |
| `Pedal_VintageDistortion`<br>Vintage Distortion | Distortion | 1 | rat | Gain [1..100] → `rat.distortion`<br>Tone [1..100] → `rat.filter` | ✅ |  |
| `Pedal_VintageFlanger`<br>Vintage Flanger | Modulation | 1 | flanger | Rate [1..100] → `flanger.rateHz`<br>Depth [1..100 %] → `flanger.depthMs`<br>Mix [1..100 %] → `flanger.mix` | ✅ |  |
| `Bass_Pedal_BassDistortion`<br>Bass Distortion | Distortion | 0 | bassod | Gain [1..100] → `bassod.drive`<br>Tone [1..100] → `bassod.treble`<br>Filter [1..100 %] → **folds** | 🟡 | knob 'Filter' folds (no param moves); bass drive: filter / presence / mid knob has no bassod param — folded |
| `Bass_Pedal_BassEnbig`<br>Bass Enbiggenator | Modulation | 0 | chorus | Rate [0.1..10 Hz] → `chorus.rateHz`<br>Mix [0..100 %] → `chorus.mix`<br>Depth [1..100 %] → `chorus.depthMs`<br>Filter [1..100 %] → **folds** | 🟡 | knob 'Filter' folds (no param moves) |
| `Bass_Pedal_BassFilterDelay`<br>Bass Filter Delay | Delay | 0 | delay | Time [20..2000 ms] → `delay.timeMs`<br>Feedback [0..100 %] → `delay.feedback`<br>Mix [1..100 %] → `delay.mix`<br>Low Filter [1..100] → **folds** | 🟡 | knob 'Low Filter' folds (no param moves) |
| `Bass_Pedal_BassFilterEcho`<br>Bass Filter Echo | Delay | 0 | delay | Time [20..2000 ms] → `delay.timeMs`<br>Feedback [0..100 %] → `delay.feedback`<br>Mix [1..100 %] → `delay.mix`<br>Low Filter [1..100] → **folds** | 🟡 | knob 'Low Filter' folds (no param moves) |
| `Bass_Pedal_BassFlanger`<br>Bass Flange | Modulation | 0 | flanger | Rate [1..100] → `flanger.rateHz`<br>Depth [1..100 %] → `flanger.depthMs`<br>Mix [0..100 %] → `flanger.mix`<br>Filter [1..100 %] → **folds** | 🟡 | knob 'Filter' folds (no param moves) |
| `Bass_Pedal_BassPhase`<br>Bass Phase | Modulation | 0 | phaser | Rate [1..100] → `phaser.rateHz`<br>Depth [1..100 %] → `phaser.depth`<br>Mix [0..100 %] → `phaser.mix`<br>Filter [1..100 %] → **folds** | 🟡 | knob 'Filter' folds (no param moves) |
| `Bass_Pedal_BassSubOctave`<br>Bass Sub Octave | Pitch | 0 | octaver | Mix [0..100 %] → `octaver.sub1`<br>Tone [1..100] → `octaver.toneHz` | ✅ |  |
| `Bass_Pedal_BassWah`<br>Bass Wah | Filter | 0 | wah | Auto-Mode [0..1 (enum)] → `wah.mode`<br>Sensitivity [1..100] → `wah.sensitivity`<br>Speed [1..100] → **folds**<br>Pedal (Auto=OFF) [1..100] → `wah.position` | 🟡 | knob 'Speed' folds (no param moves) |
| `Bass_Pedal_EdenWTDI`<br>Eden WT D.I. | Filter | 0 | eq | Gain [1..100] → **folds**<br>Bass [-12..12 dB] → `eq.lowGainDb`<br>Mid [-12..12 dB] → `eq.midGainDb`<br>Treble [-12..12 dB] → `eq.highGainDb`<br>Enhance [1..100 %] → **folds**<br>Bass Boost [0..1 (enum)] → **folds**<br>Mid-Shift [0..1 (enum)] → **folds**<br>Compression [0..100 %] → **folds** | 🟡 | knob 'Gain' folds (no param moves); knob 'Enhance' folds (no param moves); knob 'Bass Boost' folds (no param moves); knob 'Mid-Shift' folds (no param moves); knob 'Compression' folds (no param moves) |
| `Pedal_AutoVibe`<br>Auto Vibe | Tremolo | 0 | vibrato | Sensitivity [1..100] → **folds**<br>Mix [1..100 %] → `vibrato.depthMs`<br>Attack Speed [1..250 ms] → **folds**<br>Release Speed [10..1000 ms] → **folds** | 🟡 | knob 'Sensitivity' folds (no param moves); knob 'Attack Speed' folds (no param moves); knob 'Release Speed' folds (no param moves) |
| `Pedal_BassEmulator`<br>Guitar>Bass Emulator | Filter | 0 | — | Body [1..100] → **folds**<br>Tone [1..100] → **folds** | ❌ | non-EQ filter/effect with no equivalent module — skipped |
| `Pedal_BitCruncher`<br>Bit Cruncher | Filter | 0 | bitcrusher | Mix [1..100 %] → `bitcrusher.mix`<br>Filter [1..100] → **folds**<br>Downsample [0..32] → `bitcrusher.downsample`<br>Bit Depth [2..8] → `bitcrusher.bits`<br>Rectify [1..100] → **folds** | 🟡 | knob 'Filter' folds (no param moves); knob 'Rectify' folds (no param moves); bit cruncher: generic mapping (bits, rate and mix by their labels); the game's own stages need a Rocksmith 2014 installation |
| `Pedal_BobFilter`<br>Analog Filter | Filter | 0 | svf | Sensitivity [1..100] → `svf.envSens`<br>Attack Speed [1..250 ms] → `svf.envAttackMs`<br>Release Speed [10..1000 ms] → `svf.envReleaseMs`<br>Filter Type [0..2 (enum)] → `svf.mode`<br>Mix [1..100 %] → `svf.mix` | ✅ |  |
| `Pedal_Limiter`<br>Limiter | Dynamics | 0 | limiter | Limit [1..100] → `limiter.ceilingDb`<br>Speed [1..100] → `limiter.releaseMs` | ✅ |  |
| `Pedal_MarshallGuvnorPlus`<br>Marshall Guv'nor Plus | Distortion | 0 | rat | Gain [1..100] → `rat.distortion`<br>Deep [1..100 %] → **folds**<br>Bass [1..100] → **folds**<br>Mid [1..100] → **folds**<br>Treble [1..100] → `rat.filter` | 🟡 | knob 'Deep' folds (no param moves); knob 'Bass' folds (no param moves); knob 'Mid' folds (no param moves) |
| `Pedal_MarshallSupervibe`<br>Marshall Supervibe | Modulation | 0 | univibe | Speed [1..100] → `univibe.rateHz`<br>Depth [1..100 %] → `univibe.depth`<br>Wave [1..100] → **folds**<br>Filter [0..100] → `univibe.mix` | 🟡 | knob 'Wave' folds (no param moves) |
| `Pedal_ModernWah`<br>Modern Wah | Filter | 0 | wah | Auto-Mode [0..1 (enum)] → `wah.mode`<br>Sensitivity [1..100] → `wah.sensitivity`<br>Speed [1..100] → **folds**<br>Pedal (Auto=OFF) [1..100] → `wah.position` | 🟡 | knob 'Speed' folds (no param moves) |
| `Pedal_MultiVibe`<br>Multi-Vibe | Tremolo | 0 | vibrato | Speed [1..100] → `vibrato.rateHz`<br>Mix [1..100 %] → `vibrato.depthMs`<br>Waveform [0..4 (enum)] → `vibrato.waveform` | ✅ |  |
| `Pedal_OilCanEcho`<br>Oil-Can Echo | Delay | 0 | tapedelay | Time [10..1000 ms] → `tapedelay.timeMs`<br>Feedback [0..100 %] → `tapedelay.feedback`<br>Mix [1..100 %] → `tapedelay.mix` | ✅ |  |
| `Pedal_RingMod`<br>Ring Mod | Modulation | 0 | ringmod | Rate [1..250 ms] → `ringmod.freqHz`<br>Depth [1..100 %] → **folds**<br>Mix [1..100 %] → `ringmod.mix`<br>Funk [0..1 (enum)] → `ringmod.waveform` | 🟡 | knob 'Depth' folds (no param moves); ring modulator: depth folded — ringmod's carrier depth is fixed, the wet mix is the control |
| `Pedal_SendInTheClones`<br>Send In The Clones! | Modulation | 0 | chorus | CLONES! [1..100 %] → **folds**<br>Depth [1..100 %] → `chorus.depthMs`<br>Mix [1..100 %] → `chorus.mix` | 🟡 | knob 'CLONES!' folds (no param moves) |
| `Pedal_Swole`<br>Swole | Dynamics | 0 | sustainer | Smash [1..100 %] → `sustainer.sustain`<br>Rate [1..100] → `sustainer.attack` | ✅ |  |
| `Pedal_TremOle`<br>TremOle' | Tremolo | 0 | tremolo | Sensitivity [1..100] → **folds**<br>Mix [1..100 %] → `tremolo.depth`<br>Attack Speed [1..250 ms] → **folds**<br>Release Speed [10..1000 ms] → **folds** | 🟡 | knob 'Sensitivity' folds (no param moves); knob 'Attack Speed' folds (no param moves); knob 'Release Speed' folds (no param moves) |

## Racks

| Gear | Category | Uses | Becomes | Knobs (RS → rig param) | Status | Notes |
|---|---|---:|---|---|---|---|
| `Rack_StudioEQ`<br>Studio Parametric EQ | Filter | 418 | eq → eq | Bass [-12..12 dB] → `eq.lowGainDb`<br>Bass Freq. [20..500 Hz] → `eq.lowFreq`<br>Low-Mid [-12..12 dB] → `eq.midGainDb`<br>Low-Mid Freq. [100..2000 Hz] → `eq.midFreq`<br>Low-Mid "Q" [0..1 (enum)] → `eq.midQ`<br>High-Mid [-12..12 dB] → `eq#2.midGainDb`<br>High-Mid Freq. [0.8..8 kHz] → `eq#2.midFreq`<br>High-Mid "Q" [0..1 (enum)] → `eq#2.midQ`<br>Treble [-12..12 dB] → `eq.highGainDb`<br>Treble Freq. [2..20 kHz] → `eq.highFreq` | ✅ |  |
| `Rack_StudioCompressor`<br>Studio Compressor | Dynamics | 89 | compressor | Threshold [-50..0 dB] → `compressor.thresholdDb`<br>Ratio [0..4 (enum)] → `compressor.ratio`<br>Attack [0..250 ms] → `compressor.attackMs`<br>Release [10..1000 ms] → `compressor.releaseMs` | ✅ |  |
| `Rack_StudioChamber`<br>Studio Chamber Reverb | Reverb | 53 | hallreverb | Decay [1..100] → `hallreverb.decayS`<br>Diffusion [1..100] → `hallreverb.diffusion`<br>Mix [1..100 %] → `hallreverb.mix`<br>Tone [1..100] → `hallreverb.dampingKHz` | ✅ |  |
| `Rack_StudioVerb`<br>Studio Hall Reverb | Reverb | 41 | hallreverb | Decay [1..100] → `hallreverb.decayS`<br>Diffusion [1..100] → `hallreverb.diffusion`<br>Mix [1..100 %] → `hallreverb.mix`<br>Tone [1..100] → `hallreverb.dampingKHz` | ✅ |  |
| `Rack_StudioGraphicEQ`<br>Studio ParaGraphic EQ | Filter | 34 | graphiceq | Bass [-12..12 dB] → `graphiceq.band125`<br>Bass Freq. [20..500 Hz] → `graphiceq.band31`<br>Low-Mid [-12..12 dB] → `graphiceq.band1k`<br>Low-Mid Freq. [200..2000 Hz] → `graphiceq.band250`<br>Mid [-12..12 dB] → `graphiceq.band500`<br>Mid Freq. [0.5..5 kHz] → `graphiceq.band500`<br>High-Mid [-12..12 dB] → `graphiceq.band2k`<br>High-Mid Freq. [1..10 kHz] → `graphiceq.band8k`<br>Treble [-12..12 dB] → `graphiceq.band4k`<br>Treble Freq. [2..20 kHz] → `graphiceq.band16k` | ✅ |  |
| `Rack_StudioPlate`<br>Studio Plate Reverb | Reverb | 34 | platereverb | Decay [1..100] → `platereverb.decay`<br>Diffusion [1..100] → `platereverb.width`<br>Mix [1..100 %] → `platereverb.mix`<br>Tone [1..100] → `platereverb.damping` | ✅ |  |
| `Rack_StudioChorus`<br>Studio Chorus | Modulation | 24 | chorus | Rate [0.1..10 Hz] → `chorus.rateHz`<br>Mix [0..100 %] → `chorus.mix`<br>Depth [1..100 %] → `chorus.depthMs`<br>Stereo [1..100 %] → `chorus.spread`<br>Delay [1..100] → **folds**<br>LoFilter [1..100 %] → `chorus.wetHpfHz`<br>HiFilter [1..100 %] → **folds** | 🟡 | knob 'Delay' folds (no param moves); knob 'HiFilter' folds (no param moves) |
| `Rack_TapeEcho`<br>Tape Echo | Delay | 5 | tapedelay | Time [20..2000 ms] → `tapedelay.timeMs`<br>Feedback [0..100 %] → `tapedelay.feedback`<br>Mix [1..100 %] → `tapedelay.mix`<br>Hi Filter [1..100 %] → **folds**<br>Lo Filter [0..2 (enum)] → **folds** | 🟡 | knob 'Hi Filter' folds (no param moves); knob 'Lo Filter' folds (no param moves) |
| `Rack_StudioDelay`<br>Studio Delay | Delay | 4 | multitap | Time Left [20..1000 ms] → `multitap.timeMs`<br>Time Right [20..1000 ms] → **folds**<br>Feedback [0..100 %] → `multitap.feedback`<br>Mix [1..100 %] → `multitap.mix`<br>Filter [1..100 %] → `multitap.tone` | 🟡 | knob 'Time Right' folds (no param moves); second tap time approximated — multitap derives its taps from one base interval and a ratio set |
| `Rack_StudioFlanger`<br>Studio Flanger | Modulation | 1 | flanger | Rate [0.1..10 Hz] → `flanger.rateHz`<br>Mix [0..100 %] → `flanger.mix`<br>Regen [1..100 %] → `flanger.feedback`<br>Depth [1..100 %] → `flanger.depthMs`<br>Tone [1..100] → **folds** | 🟡 | knob 'Tone' folds (no param moves) |
| `Rack_RotaVibe`<br>RotaVibe | Modulation | 0 | univibe | Rate [1..100] → `univibe.rateHz`<br>Mix [0..100 %] → `univibe.mix`<br>Depth [1..100 %] → `univibe.depth`<br>Balance [1..100 %] → **folds** | 🟡 | knob 'Balance' folds (no param moves) |
| `Rack_StereoAnalogVibe`<br>Stereo Analog Vibrato | Tremolo | 0 | vibrato | Speed [1..100] → `vibrato.rateHz`<br>Mix [1..100 %] → `vibrato.depthMs`<br>Waveform [0..2 (enum)] → `vibrato.waveform` | ✅ |  |
| `Rack_StereoPhaser`<br>Stereo Phaser | Modulation | 0 | phaser | Rate [0.1..10 Hz] → `phaser.rateHz`<br>Mix [0..100 %] → `phaser.mix`<br>Depth [1..100 %] → `phaser.depth` | ✅ |  |
| `Rack_StereoTubeTrem`<br>Stereo Tube Trem | Tremolo | 0 | tremolo | Speed [1..100] → `tremolo.rateHz`<br>Mix [1..100 %] → `tremolo.depth`<br>Waveform [0..2 (enum)] → `tremolo.shape` | ✅ |  |
| `Rack_StudioPitch`<br>Studio Pitch | Pitch | 0 | harmonizer | Pitch [-24..24] → `harmonizer.semitones`<br>Mix [0..100 %] → `harmonizer.mix`<br>Tone [1..100] → **folds**<br>Pan [1..100 %] → **folds** | 🟡 | knob 'Tone' folds (no param moves); knob 'Pan' folds (no param moves); pitch shift of -24 semitones clamped to ±12; pitch shift of 24 semitones clamped to ±12 |
| `Rack_StudioWahFilter`<br>Studio Wah Filter | Filter | 0 | wah | Auto-Mode [0..1 (enum)] → `wah.mode`<br>Sensitivity [1..100] → `wah.sensitivity`<br>Attack [0..1000 ms] → **folds**<br>Release [10..1000 ms] → **folds**<br>Pedal [1..100] → `wah.position` | 🟡 | knob 'Attack' folds (no param moves); knob 'Release' folds (no param moves) |
| `Rack_SynthFilter`<br>Synth Filter | Filter | 0 | svf | Sensitivity [1..100] → `svf.envSens`<br>Attack Speed [10..1000 ms] → `svf.envAttackMs`<br>Release Speed [10..1000 ms] → `svf.envReleaseMs`<br>Filter Type [0..2 (enum)] → `svf.mode`<br>Mix [1..100 %] → `svf.mix` | ✅ |  |

## Feature gaps (not tied to one gear key)

| Feature | State | Detail |
|---|---|---|
| Mic type and position | ✅ | 441 of 441 mic/position cabinet keys get a `micpos` layer after the cab (RsToneConverter.BuildCabMic). |
| Mic distance and room | ✅ | Rocksmith exposes neither. The mic layer sits at a fixed 15 cm (the farthest `micpos` goes before its floor reflection) with no room. |
| Amps: Rocksmith's own chains | ❌ | 0 of 63 amp keys run the game's own soundbank chain (`rsamp`, #88: Wwise Guitar Distortion / FutzBox / Compressor clones, every knob through its RTPC curves). 0 of 0 amp knobs move only what the clone runs exactly (✅: Guitar Distortion EQ bands, FutzBox levels); 0 carry an inference (🟡: the Guitar Distortion drive law, the FutzBox distortion, filters or EQ Q, the Wwise Compressor's detector, or an unpublished S-curve; each amp's Notes say which). No Rocksmith render exists to compare against. |
| Cab IR roster use | 🟡 | Cabinets land on 25 distinct `cab.model` IRs (26 exist); the IRs are synthetic, not captures of the RS cabinets (12-25 are built from measured driver responses, gen_cab_irs.py). |
| Amp switches and extra tone controls | 🟡 | folded: Amp_EN30.Bright, Amp_EN50.Bright, Amp_EpiphoneElectricHawaiian.Tone, Amp_HG500.Mid-Frequency, Amp_MarshallDSL100H.Resonance, Amp_OrangeOR50H.Punch, Amp_OrangeTinyTerror.Tone, Bass_Amp_BT600B.1.2khz, Bass_Amp_BT600B.180hz, Bass_Amp_BT600B.2.6khz, Bass_Amp_BT600B.340hz, Bass_Amp_BT600B.Compression, Bass_Amp_BT880B.Bass, Bass_Amp_BT880B.High-Mid, Bass_Amp_BT880B.High-Mid Freq., Bass_Amp_BT880B.Low-Mid Freq., Bass_Amp_BT880B.Mid Frequency, Bass_Amp_BT975B.High-Shift, Bass_Amp_BT975B.Low-Shift, Bass_Amp_CH300B.Enhance, Bass_Amp_CH300B.Mid-Frequency, Bass_Amp_CH350B.15khz, Bass_Amp_CH350B.2.5khz, Bass_Amp_CH350B.250hz, Bass_Amp_CH350B.30hz, Bass_Amp_CH350B.7.5khz, Bass_Amp_CH350B.800hz, Bass_Amp_CH350B.90hz, Bass_Amp_CH600B.2.5khz, Bass_Amp_CH600B.250hz, Bass_Amp_CH600B.30hz, Bass_Amp_CH600B.7.5khz, Bass_Amp_CH600B.800hz, Bass_Amp_CH600B.90hz, Bass_Amp_CH600B.Compression, Bass_Amp_CS300B.Hi Mid, Bass_Amp_CS300B.Lo Mid, Bass_Amp_EdenE300.Enhance, Bass_Amp_EdenWT550.Bass, Bass_Amp_EdenWT550.Enhance, Bass_Amp_EdenWT550.High-Mid, Bass_Amp_EdenWT550.High-Mid Freq., Bass_Amp_EdenWT550.Low-Mid Freq., Bass_Amp_EdenWT550.Mid Frequency, Bass_Amp_EdenWT800.Bass, Bass_Amp_EdenWT800.Enhance, Bass_Amp_EdenWT800.High-Mid, Bass_Amp_EdenWT800.High-Mid Freq., Bass_Amp_EdenWT800.Low-Mid Freq., Bass_Amp_EdenWT800.Mid Frequency, Bass_Amp_HT300B.High-Shift, Bass_Amp_HT300B.Low-Shift, Bass_Amp_HT300B.Mid-Frequency, Bass_Amp_HT400B.High-Shift, Bass_Amp_HT400B.Low-Shift, Bass_Amp_LT25B.High-Shift, Bass_Amp_LT25B.Low-Shift, Bass_Amp_LT25B.Mid-Frequency, Bass_Amp_LT85B.High-Shift, DI_Amp_BassDriver.Blend, DI_Amp_MixerPre.Tone, DI_Amp_TubePre.Mid. Freq. Select |
| Knob tapers | 🟡 | Amp knobs run through the game's own RTPC curves (rsamp, #88), and four pedals through the curves in RsGameCurves; every other RS knob is mapped LINEARLY from its catalogue range onto the param range (RsToneConverter.Norm01). None is calibrated against a Rocksmith render. |
| Gain staging / tone volume | 🟡 | Playback levels each converted tone by its RENDERED loudness (RsToneLevelling: a fixed phrase through the real chain on the offline rig, -24 LUFS under a -5 dBFS peak ceiling, RsToneLoudnessPolicy); until that measurement lands the tone plays on the walker's estimate plus the per-amp-model trim (RsAmpLevelTrims.ByKey). The RS `Volume` is not a level here; only its difference from the song's other tones is kept, ±3 dB. Rocksmith's own output levels are unknown, so 'as loud as in Rocksmith' is not claimed: see RsToneFidelityHarnessTests. |
| Signal order (pre / loop / rack) | 🟡 | Rendered PrePedal1-4 -> Amp -> Cab -> Rack1-4 -> PostPedal1-4 (RsTone.InSignalOrder). Whether Rocksmith runs the post ("loop") pedals before the cabinet is not documented; unverified. |
| Stereo rack controls | 🟡 | Second-tap times, pan and stereo-width knobs fold where the target is mono or single-tap — see the Racks table. |
| Mid-song tone switching | ✅ | DcRigApplier applies the minimal diff at each tone change; RsTonePlaybackCoordinator schedules it. |

