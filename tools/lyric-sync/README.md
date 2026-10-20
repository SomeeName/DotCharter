# dotcharter-lyricsync

**Paste a song's lyrics, get every syllable timed.** A standalone command-line
tool: it takes an audio file and a plain-text lyric sheet and writes a Rocksmith
2014 vocals XML and/or an enhanced `.lrc`, ready to import into **Editor on
Fire** or **RSCharter**.

It is the same alignment DotCharter itself runs — the same decode, the same two
acoustic models, the same ensemble — with the chart taken out. You do not need
DotCharter to use it.

---

## Quick start

```
dotcharter-lyricsync <audio> <lyrics.txt> [options]
```

```
dotcharter-lyricsync "Matasaburo.wav" matasaburo.txt --format both
```

writes `Matasaburo_vocals.xml` and `Matasaburo.lrc` beside the audio.

### The lyric sheet

Plain UTF-8 text. **A newline ends a phrase, a space ends a word, and a `-`
splits a word into syllables.**

```
Mi-zu ta-ma-ri ni a-shi wo tsuk-kon-de
A-na-ta wa o-ki-na a-ku-bi wo su-ru
```

Blank lines are stanza breaks and are ignored. Rocksmith's own `-`/`+` suffixes
are accepted on input too, so a line copied out of an existing chart pastes
straight back in and round-trips losslessly.

**Write Japanese as romaji.** The model aligns the roman alphabet; kana and kanji
have no columns in it and the run will tell you so rather than produce nonsense.

---

## Options

| Option | What it does |
|---|---|
| `--stem <vocals.wav>` | Listen to an already-separated vocal stem. **This is worth more than every other option combined** — see [Accuracy](#accuracy). The tool never separates one itself. |
| `--out <base>` | Output path without an extension. Default: beside the audio, named after it. |
| `--format rsxml\|lrc\|both` | Default `rsxml`. |
| `--model-dir <dir>` | Models root to use instead of the default locations. |
| `--lang auto\|en\|romaji` | Override the language router. Default `auto`. |
| `--no-ensemble` | Run one model instead of blending two. Faster, measurably less accurate. |
| `--span-word-gap` | Hold the last syllable of a word until the next word starts. Syllables *inside* a word always do this. |
| `--note <0-255>` | The `note` attribute in the vocals XML. Default `254` (no pitch); EOF's own exports write `255`. Both mean "no pitch". |
| `--json` | Also dump the syllable table with per-syllable confidences. |
| `--about` | The measured accuracy figures. |
| `--help` | Usage and licences. |

**If there is a `stems/vocals.wav` (or `.flac`/`.opus`/…) beside your audio it is
found automatically** — no flag needed. The name test is exact: a file called
`no_vocals` is never mistaken for the voice.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Everything was written. |
| 2 | Bad input — a missing file, an unknown option, an empty sheet. |
| 3 | The acoustic model is missing and could not be fetched, or failed to load. |
| 4 | The audio could not be decoded. |

Warnings (a low-confidence run, a stem whose name does not say "vocals", a
degraded ensemble) go to **stderr**; the report goes to **stdout**. So
`dotcharter-lyricsync … > report.txt` keeps the two apart.

---

## The models

Two ONNX acoustic models, fetched **once** on first run:

| Model | Size | What it is |
|---|---|---|
| `lyric-align` | ~226 MB | MMS-FA (`facebook/mms-300m-1130-forced-aligner`). Romanized text in 1,100+ languages. **Required.** |
| `lyric-align-en` | ~189 MB | `facebook/wav2vec2-large-960h-lv60-self`. English only. **Optional**, fetched only for English sheets. |

**They are installed into a `models\` folder beside the executable**, so a
portable copy carries its own. `%APPDATA%\DotCharter\models\` is searched too,
second, which is where a hand-installed copy can go. An existing model is never
overwritten, whichever root it is in. `--model-dir` points at a folder that
*contains* `lyric-align\` (and optionally `lyric-align-en\`).

> **Downloading currently needs a GitHub token.** The release assets live on a
> private repository, so set `DOTCHARTER_GITHUB_TOKEN` (or `GITHUB_TOKEN`) to a
> token with read access, or install the model folders by hand. Once the assets
> are published publicly this is no longer necessary and nothing else changes.

---

## Audio formats

Anything **ffmpeg** can decode. The tool looks for `ffmpeg` beside itself, then
in a `tools\` folder beside itself (which is where the release zip puts it),
then on `PATH`.

**Without ffmpeg you get `.wav` and `.ogg` only** (plus whatever the bundled
native decoder happens to register). Exit code 4 says so and names ffmpeg.

---

## Importing the result

### Editor on Fire

1. Open (or create) your EOF project — lyric import is refused when no chart is open.
2. **File → Import → Lyric** (or `Shift+F8`).
3. Pick the `_vocals.xml`. EOF detects the format from the file's contents, not
   its extension, so the `.lrc` works from the same menu item.

EOF replaces the existing vocal track wholesale and adds beats to cover the
lyrics. It reads the `+` markers as the line breaks; the tool writes one on every
phrase end, which is why a newline in your sheet is what you see in game.

> EOF's *Rocksmith* importer (`F7`) deliberately refuses a vocals XML and tells
> you to use File → Import → Lyric instead. That is expected.

### RSCharter

1. Open your project — the Import submenu only exists when one is open.
2. **File → Import → RS vocals arrangement XML**.
3. Pick the `.xml`. It is added as a new vocal path and the project is saved.

Dragging the `.xml` onto the window does the same thing.

> **Send RSCharter the XML, not the LRC.** Its LRC parser only recognises a
> `<mm:ss.xx>` tag that stands alone as a space-delimited token, which is not the
> conventional enhanced-LRC form this tool (and every player) writes. The XML
> path is exact.

### Anything else

The `.lrc` is standard enhanced LRC — one line per phrase with a `[mm:ss.xx]`
line stamp and a `<mm:ss.xx>` before each syllable. Classic LRC has no duration
channel, so syllable *ends* are not in it; a reader reconstructs them as the gap
to the next stamp. If you need ends, use the XML.

---

## Times are seconds, not beats

This tool has no beat map, so every time it writes is a plain offset into the
audio file. **DotCharter itself additionally snaps each syllable to the chart's
beat grid** and applies its note-spacing rules; nothing here does. That is a
feature for EOF and RSCharter, which both position against their own beat map on
import.

`--span-word-gap` is the one place this differs in kind from DotCharter: the app
extends a syllable to the next onset minus a *fraction of a measure*, which needs
a tempo map. Here the gap is a flat 30 ms.

---

## Accuracy

Onsets within 100 ms of where a human charter put them, two models blended,
listening to a **separated vocal stem**:

| Corpus | ≤100 ms | notes |
|---|---|---|
| romaji | **91.3 %** | 4 hand-synced arrangements, 2,036 syllables; p90 92 ms |
| English | **79.2 %** | 40 official Rocksmith vocal arrangements, ~14,300 syllables; p90 292 ms |

The two disagree because of how the songs are **mixed**, not because of the
language: the romaji set has sparse, vocal-forward mixes.

**A vocal stem is worth more than any setting.** Same English corpus, one model:

| Input | ≤100 ms | p90 \|error\| |
|---|---|---|
| the mix | 66.2 % | 5,862 ms |
| a vocal stem | **75.5 %** | **1,082 ms** |

Paired per song the stem wins 36, loses 2, ties 2. The median barely moves; what
collapses is the tail — and the tail is what you notice. If you have any
separation tool (Demucs, Spleeter, UVR, Moises…), run it once and pass `--stem`.

Blending the two heads rather than choosing one buys English 76.7 % → 79.2 % and
pulls its worst song from −38.9 pp to −5.9 pp; romaji 90.5 % → 91.3 %, 4 wins
0 losses. It costs a second pass over the audio — that is what `--no-ensemble`
gives back.

**Some songs are simply unreadable.** A shouted vocal can score 0 % within 100 ms
on every model. That is a limit of speech-trained alignment, not a setting you
have got wrong.

The `--json` dump carries a per-syllable confidence. Absolute values look low and
that is normal: CTC emits a letter on *one* frame of its span and blank on the
rest, so the mean is diluted. Compare them to each other, not to an intuition —
the tool's own low-confidence count uses a threshold calibrated against real
error.

---

## Licences

- **The tool**: GPL-3.0-or-later.
- **`lyric-align` weights** (MMS-FA, `facebook/mms-300m-1130-forced-aligner`):
  **CC-BY-NC 4.0 — non-commercial use only.** If you chart for money, this model
  is not licensed for it.
- **`lyric-align-en` weights** (`facebook/wav2vec2-large-960h-lv60-self`):
  Apache 2.0.
- **ffmpeg**, when bundled: GPLv3, unmodified, from
  [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds).

---

## Building it yourself

```powershell
dotnet build tools/lyric-sync/DotCharter.LyricSync/DotCharter.LyricSync.csproj -c Release
pwsh tools/lyric-sync/Publish-LyricSync.ps1     # self-contained zip + SHA256SUMS
```

The publish is deliberately **not trimmed**: ONNX Runtime resolves its providers
and tensor element types reflectively, and a trimmed build fails at the first
inference rather than at publish time.
