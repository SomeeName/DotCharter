# Export: Wwise-free audio + the save companions

Deep reference for `DotCharter.Services/Export/`. Linked from
[DotCharter.Services/AGENTS.md](../../../../DotCharter.Services/AGENTS.md); read it
before changing audio encoding, the sound bank, or what a save writes.

---

## Why we encode `.wem` ourselves

Wwise was the only non-managed step in the whole package build, and it cannot be
removed by shipping it: **Audiokinetic's EULA (§2.2) forbids distributing the
Wwise authoring tool standalone**, and the wav→wem converter lives in the
authoring tool. Only the *runtime* may be redistributed, and only inside a game.

We sidestep it. **Vorbis is written by JuceSharp's own Wwise-Vorbis encoder**
(`JuceWemEncoder`, ABI v44, #317): libvorbis at quality 0.4 with an 18 kHz lowpass,
whose setup header is byte-identical to Wwise's, framed exactly the way Wwise frames
it (`JuceSharp/docs/agents/wem-encoder.md`). It is the ONLY encoder: the optional
Wwise-console path and its project templates were removed (#894), so no Wwise
file ships and no setting chooses between encoders.

| Codec | Wwise plugin id | `fmt ` tag | 4-min stereo | Written by | Plays in-game |
|---|---|---|---|---|---|
| Vorbis | `0x00040001` (262145) | `0xFFFF` | ~4 MB | JuceSharp's encoder | Wwise's own output: yes. Ours: awaiting the owner's check (`rs7test_nowwise_p.psarc`) |
| IMA-ADPCM | `0x00020001` (131073) | `0x0002` | ~13 MB | **never** | **NO** — the song loads and its timeline never advances (owner, 2026-10-11, #717) |
| PCM | `0x00010001` (65537) | `0x0001` | ~46 MB | `WemWriter` | unverified (probe `rs7test_pcm_p.psarc`) |

PSARC stores `.wem` entries **uncompressed**, so that size lands in the archive
verbatim. **Vorbis is the default everywhere** (`RocksmithBuildOptions.AudioCodec`,
`AppConfig.SaveCompanionPsarcCodec`). The argument that PCM/ADPCM decoders are
compiled into every Wwise runtime (Audiokinetic lists only Vorbis and Opus as
pluggable) turned out wrong for ADPCM in Rocksmith, so do not lean on it for PCM
either: only the game decides.

**ADPCM is never written.** `WemWriter` refuses it, `RocksmithPackageBuilder.ManagedCodec`
turns a request for it into Vorbis, and `SaveCompanionService.ParseCodec` reads a
stored `"ImaAdpcm"` (the default before #717) as Vorbis. `WwiseImaAdpcm` stays only
to DECODE wems built before that.

## The two things that silently break a CDLC

1. **The bank must declare the codec that is actually in the wem.** The `.bnk`'s
   HIRC sound object carries a codec plugin id, and it is what selects the
   decoder. A bank claiming Vorbis over PCM data produces a song that loads,
   scrolls, and plays **silence** — no error anywhere. `SoundBankWriter.Generate`
   takes a `WemCodec` for this reason; it used to hardcode 262145.

2. **The u32 at absolute offset 40 of the wem is the channel configuration**, and
   Rocksmith's runtime reads it as a *legacy* `WAVEFORMATEXTENSIBLE` channel MASK
   (3 = front-left|front-right). Wwise 2018+ writes a *packed* `AkChannelConfig`
   there (`0x3102` for stereo) which the old runtime reads as a nonsense speaker
   layout — which is exactly why Rocksmith2014.NET post-patches a `3` over
   whatever the modern encoder emitted. `WemWriter` writes the 3 directly.

Verified against real files: `TestData/EOF/*/guitar.wem` has fmt size `0x42`,
tag `0xFFFF`, 48 kHz stereo, `blockAlign=0`, `bitsPerSample=0`, and `3` at
offset 40.

## `WwiseImaAdpcm`

Wwise's IMA variant, not textbook IMA:

- Delta is the **"mul" flavour** — `((code & 7) * 2 + 1) * step >> 3`, multiplying
  *before* the shift. Textbook IMA accumulates then shifts, and the two diverge on
  truncation (step 7, code 1 → 2 here, 1 there). A textbook decoder drifts.
- Blocks are `0x24` (36) bytes **per channel, interleaved by block**.
- 64 samples per block: the header sample is sample 0, then 63 codes. The 64th
  code is **skipped** on decode; we emit a zero there.
- Step index is carried **across** blocks per channel (each block stores the index
  it starts from), so the predictor keeps converging.

> **The game does not play it** (#717): a package whose wem is IMA-ADPCM loads,
> and its timeline never advances. So nothing writes ADPCM any more; this codec is
> kept to read the wems DotCharter built before (the ADPCM default shipped from
> commit `0bd02a3a` until #717). The round-trip tests here prove the decoder
> against our own old encoder, nothing about the game.

## Audio decode (`PcmAudioLoader`)

Source audio → 48 kHz stereo 16-bit PCM. **The decoder is the EDITOR'S**: the
native engine's (`JuceAudioDecoder`, JUCE's basic format set — what the editor
plays), so a chart exports against exactly the audio it was charted against.
Until 2026-09-29 ffmpeg decoded first, and two decoders disagree about where an
MP3 starts: ffmpeg drops the encoder's leading padding, the engine's Windows Media
reader keeps it, so every MP3 project's wem ran **1,729 samples (36 ms at 48 kHz,
39 ms at 44.1 kHz) ahead of its chart**. FLAC, OGG and WAV were always
sample-identical. `PcmAudioLoaderEditorParityTests` pins it for MP3 at both rates
and for FLAC. ffmpeg (bundled; `MediaToolResolver`) still RESAMPLES a non-48 kHz
decode — from the engine's own float PCM, timing-neutral — and still DECODES what
the engine cannot read (.m4a/.aac/.wma, which the editor cannot play either).
Without ffmpeg the resample is **linear** and recorded as a build warning,
because it is audibly worse. The same loader feeds the audio lint, lyric
auto-sync and tone generation, so all of them follow the editor too. The Vorbis
encoder adds no delay (JuceSharp `WemEncoderTests.TheDecodedAudio_IsTheInput_AlignedAndClose`:
best correlation at lag 0).

The encoded wem is cached next to the source audio (`<stem>.vorbis.wem` /
`<stem>.pcm.wem` — codec in the name, so switching codec regenerates instead of
silently reusing the other one). Without this cache, psarc-on-save would re-encode
the entire song on every Ctrl+S.

