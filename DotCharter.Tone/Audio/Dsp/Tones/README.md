# RS in-game-tone parse + conversion (`Audio/Dsp/Tones` + `Audio/Dsp/RsToneConversion`)

Turns the Rocksmith tone gear that already ships inside a PSARC (surfaced by
the package tone importer (`IPackageToneImporter`) as an opaque JSON string) into a structured `RsTone`, then
converts it onto the in-house DSP rig (`IDspChainService` modules). Pure,
deterministic, zero I/O beyond the embedded catalogue. This folder is Phase 0
(parse); the converter lives next door in `RsToneConversion/` (Phase 1).

## Files

- `RsTone.cs` — the parsed tone model (`RsTone`, `RsGear`/`RsAmp`/`RsCab`/`RsPedal`,
  enums). Subtype is chosen by the SLOT KEY (RS serializes every slot the same),
  which gives Phase 1 pattern-matchable types and preserves slot role/index for
  the report.
- `RsToneJsonParser.cs` — raw JSON string → `RsTone`. `TryParse` (false only on
  non-JSON / missing `GearList`), `Parse(PsarcToneDefinition)` (throw-free
  bridge), `ParseVolume` (ports Rocksmith2014.NET `volumeFromString`).
- `RsToneJsonWriter.cs` — the inverse: `RsTone` → manifest-compatible tone JSON
  body (Rocksmith2014.NET `ToneDto` shape: `Volume` as `"0.000"` STRING,
  `IsCustom:true`, `NameSeparator " - "`, sparse `Rack1..4`/`PrePedal1..4`/
  `PostPedal1..4` slots, optional `SortOrder`). parse → write → parse is
  structurally lossless for everything `RsTone` models (per-gear `Skin` cosmetics
  are not modelled). Used by the tone editor to persist authored tones.
- `RsGearCatalog.cs` — gear-catalogue model (`RsGearData`, `RsGearKnob`,
  `RsKnobUnit`) + embedded-resource loader (`Shared`, load-once). Indexes gear by
  `Key` and every knob by its own full key.
- `RsToneAuthoring.cs` — the pure helpers behind the Tone Designer: key
  validation/uniquing, catalogue slicing for the gear pickers, `DefaultKnobValues`,
  `ClampKnobValue`, `ClampVolumeDb`, the fixed descriptor list, `CreateDefaultTone`.
- `ToneFromAudioBuilder.cs` — measurements → tone (see below).
- `tone_gear_data.json` — the embedded snapshot (see provenance below).

## Provenance of `tone_gear_data.json`

Verbatim copy of
`Rocksmith2014.NET/samples/DLCBuilder.Domain/Tones/tone_gear_data.json`
(the git-ignored local mirror of
[iminashi/Rocksmith2014.NET](https://github.com/iminashi/Rocksmith2014.NET),
MIT-licensed). It enumerates all **63 amps / 450 cabinets / 83 pedals / 17
racks** and, per knob, `UnitType` (`number`/`%`/`db`/`hz`/`khz`/`ms`/`enum`),
`MinValue`, `MaxValue`, `ValueStep`, `DefaultValue`, `EnumValues`. Those counts
are asserted at load time (`Debug.Assert`) as a truncation guard.

It is **static reference data** — gear names and knob ranges published by the
game — in the same class as the loc-id lyric descriptors and `default_symbols.bin`
we already ship as embedded resources. Refresh it by re-copying the upstream file
when the mirror updates; keep this note and the count constants in
`RsGearCatalog` in sync.

## Generating a tone from audio (`ToneFromAudioBuilder`)

The owner's rule for the feature: **the tone PLACEMENT is not generated** — the
charter places a tone change on the timeline and the app builds the tone from the
audio in that span. `DotCharter.Analysis/Tone/ToneAudioProfile` is the contract
between the two halves (span → measurements); this folder owns the second half
(measurements → `RsTone`).

`ToneFromAudioBuilder.Build(profile, catalog, key, name)` returns a
`ToneSuggestion` (tone + `ToneArchetype` + a one-line English rationale) rather
than a bare `RsTone`: the charter did not choose this gear, so the UI needs the
archetype to branch on and something to show them about why.

**It is a STARTING POINT, not a recovered rig.** The audio measured is already
through somebody's amp, a room, a mix bus and usually a stem separator. Do not
add a doc comment, a status string or a loc key that claims more.

Rules that bite:

- **Silence is REFUSED.** `profile.IsSilent` ⇒ `Build` returns `null`. A
  "neutral" tone would sit in the tone list looking exactly like a generated one.
  Callers must report the empty span, not place a tone.
- **A knob the chosen gear does not declare is SKIPPED, never invented**, and
  every value goes through the catalogue's own min/max/step and
  `RsToneAuthoring.ClampKnobValue`. Half the archetype amps have no Presence pot
  and one has no Resonance — a made-up knob key is what ships a tone the game
  rejects. Pinned by
  `Audio/Dsp/Tones/ToneFromAudioBuilderTests.Every_knob_set_exists_on_the_chosen_gear_and_is_within_its_catalogue_range`,
  which runs against the REAL embedded catalogue.
- **The archetype gain windows are disjoint and ascending** (`Direct` < `Clean` <
  `EdgeOfBreakup` < `Crunch` < `HighGain`). That is the only reason the guarantee
  "holding the spectral shape fixed, more Drive never means less Gain" survives a
  band boundary. If you retune a threshold, keep the windows ordered.
- **The `Direct` gate may only read measurements `Drive` does not consume**
  (centroid + the high band). Adding e.g. `HarmonicRatio` to it would let a Drive
  sweep flip INTO `Direct` and drop the gain knob.
- Pure and deterministic: no clock, no randomness, gear keys are literals and
  knob dictionaries are seeded in the catalogue's own knob order so the written
  JSON is stable.
