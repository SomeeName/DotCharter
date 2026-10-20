# Export: Wwise-free audio + the save companions — export cost measured, the four non-reproducible things, still open

## Export cost, measured — and the four things that are not reproducible

Measured 2026-08-29 on the owner's box (48 GB, Debug, min of 5 after a warm-up)
against `TestData/RsCharter/*` and `TestData/EOF/*`.

**Read the same-run rows first, and distrust the rest.** Up to eight testhosts
from parallel agents were on the box, and the load MOVED during the session:
`PsarcWriter.WriteFile` over a fixed 578 KB payload — code nobody touched —
measured 15.6 ms early and 36.8 ms late, so a late "after" number carries a ~2.4x
tax that has nothing to do with the change. Where a before/after pair could be
taken inside ONE run it is marked *same run*, and that is the number to trust.

| Phase | Artifact | Before | After |
|---|---|---|---|
| Album art, 3 DDS sizes — ***same run*** | `Cover.jpg`, 1.25 MB | **452 ms** | **108 ms** (4.2x; one decode of the 70 ms decode instead of three) |
| `RsChordDensity.Compute`, all levels | Odorouze (1,071-chord Rhythm among 4 arrs) | 159 ms | 18 ms |
| same | Ave Mujica DIVINE (3 arrs, up to 956 chords) | 325 ms | 29 ms |
| `SngFromDomainMapper.BuildInstrumental`, all arrangements | Odorouze | 183 ms | 46 ms |
| same | DIVINE | 291 ms | 48 ms |
| The thirty header lookups — ***same run***, micro-probe | Odorouze (12,964 elements) | **11.8 ms** | **1.4 ms** |
| same | DIVINE (10,679 elements) | **17.2 ms** | **2.2 ms** |
| Guitar Pro import, all tracks | Fireworks Beneath My Shoes (12 MB, 3 tracks) | 154 ms | inconclusive — see below |
| same | Musica Caelestis (11 MB, 7 tracks) | 271 ms | inconclusive — see below |

**The Guitar Pro row is honest about not having a number.** Post-change wall
clock (154 ms / 257 ms) is indistinguishable from pre-change on a box that had
meanwhile got ~2.4x busier, which means it improved by something this session
could not measure cleanly. **The per-note property scan is still worth
removing** (it is pure waste, and the fix is byte-pinned). **The DOM is NOT the
next win (#308, measured 2026-10-07, one process, best of 5):** the 11-12 MB is
the `.gp` ZIP, almost all of it an embedded MP3 backing track; the `score.gpif`
the importer reads is 54 KB-1.05 MB across all 25 `.gp` files in `TestData/`.
Building its `XDocument` is 3.5-10 ms of a 25-126 ms import (6-15 %), and a
streaming reader would still pay a 1.6-5.3 ms tokenize floor. 75-90 % of the
time is `GpArrangementParser.Parse` and what it calls (the bar/beat/note walk,
`GpPostProcessor.Run`, the hand-shape builder, the anchor generator): profile
THAT, in a Release build, before touching the reader.

Four shapes accounted for nearly all of it, and **every one was a linear scan
where the data was already sorted or already in hand**:

1. **`RsChordDensity` was the ENTIRE SNG build** — 159 ms of a 167 ms build on
   Odorouze, and on DIVINE it measured 325 ms on its own against a 298 ms full
   build (the isolated probe passes a constant `chordIdOf`, so more chords reach
   the expensive branch than in a real export; either way it is the whole cost). Three questions per chord (the last sound before it,
   whether its hand shape holds an earlier sound, the nearest earlier CHORD in
   that shape) were each a full scan of the level's merged sound list, i.e.
   O(chords x sounds). The list is built with `OrderBy(s => s.Position)`, so all
   three are binary searches over a range; a hand shape's sounds are a
   CONTIGUOUS slice of it.
2. **`DdsEncoder.EncodeCoverArt` was called once per size**, and each call
   decoded the source image again. `EncodeCoverArtSizes` decodes once and
   resizes three times — the decode IS the cost.
3. **`RscpHeaderParser` asked `doc.Descendants(name).FirstOrDefault()` thirty
   times.** That is a lazy pre-order walk of the WHOLE tree, so a name the file
   does not carry costs a full walk — and **11 of the 30 are absent from a real
   project**, plus four more (`bookmarks`, `dcAnnotations`, `dcToneDefinitions`,
   `text`) are written AFTER `<arrangements>` and so walk past every note anyway.
   `RscpFirstElementIndex` answers all thirty from one pass that stops early.
4. **`GpNoteParser.ApplyNoteAttributes` re-walked one note's `<Property>` list
   up to twenty-three times**, once per named property, re-reading every `name`
   attribute each time. One `foreach` with a `switch` and `??=` keeps the first
   element per name — what `FirstOrDefault` returned — and allocates nothing.

### Byte-exactness, and the four things that stop an export reproducing

All four changes are pinned as BYTE comparisons against output captured from the
pre-change code (`9cc50f87`) on real artifacts:
`ExportBytePinTests`
(`DotCharter.Tests/Services/Export/ExportBytePinTests.cs`) — 34 cases over 31
hashed artifacts: four `.rscp` projects round-tripped, fourteen SNGs, four
Guitar Pro imports (three gpif + one GP3-5 binary) and three real covers at
three sizes each, plus three cases asserting the batched art encoder matches
three separate calls. It
SKIPS without `TestData/`, so `RsChordDensityLookupEquivalenceTests` carries the
same decision on a synthetic fixture that runs everywhere, against a fingerprint
captured from the same pre-change build.

**Building that pin turned up four places where an export does not reproduce
from the same input.** Anyone adding a byte comparison over this pipeline hits
all four, in this order:

- **`RocksmithSngWriter.WritePackedPc` generates a fresh random 16-byte AES-CTR
  IV per call** (`iv ??= RandomNumberGenerator.GetBytes(16)`), so the packed SNG
  differs on EVERY run. Hash `WriteUnpacked` — the body the IV encrypts — or
  pass a fixed `iv`, which the parameter exists for.
- **`SngFromDomainMapper.BuildInstrumental` stamps `LastConversionDateTime` from
  `DateTime.Now` at MINUTE resolution** unless a `conversionTime` is passed. Two
  runs in the same minute agree and two runs either side of a minute boundary do
  not, which reads exactly like a flaky test. Pass the time.
- **A whole `.psarc` is never byte-reproducible**: `ConvertArrangements` assigns
  `PersistentId = Guid.NewGuid()` and `MasterId = RandomNumberGenerator
  .GetInt32(...)` per arrangement per build. Pin the SNG, the DDS and the
  manifests separately — never the archive.
- **`RscpChartSerializer.Save` used to MUTATE the project it was handed** (fixed,
  #304). `RscpWriter` ran `RscpTemplateManager.RegenerateTemplates` on the
  caller's arrangements, renumbering every `ChordTemplate.Id`, so *load → save →
  build the SNG* gave different SNG bytes from *load → build the SNG*. Three SNG
  pins were captured wrong that way. It now regenerates a `CloneArrangement`
  copy; `Parsing/RscpWriterNoMutationTests` asserts a saved project is
  deep-equal to itself. A round-trip test's EXPECTATION is the original with
  the same regeneration applied (`RscpDeepValueRoundTripTests`,
  `RscpTestDataParseAllTests`): saving canonicalizes templates on purpose.

## Still open

- **In-game verification** (the owner's machine, 2026-10-11): IMA-ADPCM does NOT
  play (#717), so it is never written. Wwise's own Vorbis plays. JuceSharp's
  Wwise-free Vorbis (#317) and PCM are waiting for the owner's check (probe
  packages `rs7test_nowwise_p.psarc`, `rs7test_pcm_p.psarc`). If a song does not
  play, suspect (in order): the bank/wem codec pairing, then offset 40.
- The Wwise-**Vorbis** encoder EXISTS (#317, JuceSharp ABI v44): JUCE's libvorbis
  1.3.7 re-framed into Wwise's layout, codebooks named by their aoTuV 6.03 library
  index. Every field rule and its evidence: `JuceSharp/docs/agents/wem-encoder.md`.

---

# Export-boundary fidelity (moved here from `DotCharter.Services/AGENTS.md`)

What the writers may fix, what they must never invent, and the three
chart-wide values every exporter has to resolve rather than read.

