# DotCharter

**A guitar game and a charting workstation for Windows.**
You plug in your real guitar or bass and play your own charts and `.dcsong`
songs on a 3D note highway. If a chart is wrong, you fix it in the editor, one
click away. You can export packages with your own keys. You can also shape your
tone on a built-in amp and pedal rig.

[![Release](https://img.shields.io/github/v/release/SomeeName/DotCharter)](https://github.com/SomeeName/DotCharter/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey.svg)](#system-requirements)
[![License](https://img.shields.io/badge/license-GPL--3.0--or--later-blue.svg)](LICENSE)
[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/A3U028PMFM)

<p align="center">
  <img src="docs/media/hero.webp" alt="The DotCharter chart editor: timeline with a bend and slides, note inspector and 3D highway preview" width="900">
</p>

DotCharter includes no game content and no songs. It plays and edits the
songs and charts you make with it, and the files you bring yourself.
An optional plugin can extend the library.

---

## Features

<table>
  <tr>
    <td width="50%"><img src="docs/media/play.webp" alt="Play Mode on the 3D highway with lyrics"><br><sub><b>Play</b> the 3D highway with your guitar plugged in.</sub></td>
    <td width="50%"><img src="docs/media/song-select.webp" alt="Song select with covers, paths and records"><br><sub><b>Song select</b> with your records and setlists.</sub></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/media/rig.webp" alt="The tone rig: neural amp, cabinet, chorus, reverb and boost"><br><sub><b>Tone rig</b> with an amp, a cabinet and pedals on a native DSP engine.</sub></td>
    <td width="50%"><img src="docs/media/library.webp" alt="The library with covers, paths and a detail panel"><br><sub><b>Library</b> every song, arrangement and path at a glance.</sub></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/media/seven-string.webp" alt="A 7-string arrangement in the editor"><br><sub><b>7-string</b> charts, edited like any other.</sub></td>
    <td width="50%"><img src="docs/media/ai-chat.webp" alt="The AI chat pane beside the chart"><br><sub><b>AI assistant</b> proposes fingerings for you to review. It never writes notes.</sub></td>
  </tr>
</table>

<table>
  <tr>
    <td width="33%"><img src="docs/media/bends.webp" alt="The bend editor"><br><sub>Bends drawn on a curve.</sub></td>
    <td width="33%"><img src="docs/media/chord-shapes.webp" alt="Chord shapes with fingering"><br><sub>Chord shapes with fingering.</sub></td>
    <td width="33%"><img src="docs/media/techniques.webp" alt="The technique panel"><br><sub>Every technique one click away.</sub></td>
  </tr>
</table>

The pictures show a chart, lyrics, cover and audio made for this README (see
[docs/agents/readme-media.md](docs/agents/readme-media.md)). Nothing in them is from a real song.

**Play**
- A scrolling **3D highway** with **live note detection** from your guitar or
  bass. You get scores, letter grades, per-section results, per-song bests and
  local leaderboards for each player profile.
- Practice tools include a **riff repeater** in the pause menu, A/B loops, a
  speed ladder, dynamic difficulty and a latency-calibration wizard.
- A **tuner**, and a **drop tuner** that pitch-shifts the song to your tuning.

**Chart**
- A beat- and tempo-aware **editor** for guitar and bass. It has keyboard note
  entry, chord templates, and hand shapes and fret-hand positions that you can
  generate or set by hand. It also has validators with one-click fixes,
  checkpoints, chart diff, undo for every edit and a command palette
  (`Ctrl+Shift+P`). Keybinds follow Editor on Fire where the two overlap.
- **Stem separation** (Demucs and other downloadable models) makes practice
  backing tracks. DotCharter caches the stems for each song.
- **Lyric auto-sync** times each syllable against the vocals. You just paste
  the lyrics.
- A **sheet-music** view follows the playhead and can export to PDF.
- An optional **AI assistant** works with your own Claude or OpenAI-compatible
  endpoint. It proposes fret-hand positions, fingerings, chord names and
  sections for you to review. It never writes notes.

**Import and export**
- **`.dcsong`** songs hold a chart, its audio, a preview and the cover art (and
  optionally stems) in one file. You can play, share and reopen them.
- You can export playable Rocksmith 2014 `.psarc` packages with tones and album
  art. They are encrypted with keys you supply yourself. 7- and 8-string guitar
  charts export as RS Extended Range packages for the RS Extended Range mod.
- Other formats are Editor on Fire `.eof`, Guitar Pro (`.gp`, `.gpx`, `.gp5` in
  and `.gp` out), Rocksmith XML, RSCharter `.rscp` and MIDI.
- Projects are saved as `.dcproj` files. Every save also writes a
  Charter-compatible `.rscp` beside it, so the same folder opens in Charter.
  A `.rscp` from Charter or from an older DotCharter still opens. When you save
  it, DotCharter creates the `.dcproj` and keeps the old file.

**Tone rig**
- A pedalboard of amps, cabinets and effects runs on a native DSP engine. It
  comes with **neural amp captures** (NAM and AIDA-X, 63 bundled), cabinet
  impulse responses, presets and A/B slots.
- You can browse and download captures from **TONE3000**. You can also describe
  a tone in words and let the AI assistant build it.
- Audio goes in and out over ASIO, WASAPI or DirectSound. The rig hosts VST3
  plugins, and a streaming mirror sends the mix to a second device for OBS.
- The rig also comes as a standalone app, **RigLab**.

The interface is available in **English, German and Japanese**.

---

## Built on the work of

DotCharter is a newcomer to a community that has charted and shared Rocksmith
custom songs for years. It works **alongside** that community's tools. It
exists because their authors worked out the formats first.
[ACKNOWLEDGEMENTS.md](ACKNOWLEDGEMENTS.md) lists what we took from each one.
The license texts are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

- **[Editor on Fire](https://github.com/raynebc/editor-on-fire)** (raynebc).
  We ported its section and event catalogs, follow its rules for arrangement
  properties and repeated chords, and borrowed its keybinds. DotCharter reads
  and writes `.eof`, so a chart can travel both ways.
- **[Charter / Log Charter](https://github.com/Lordszynencja/Charter)**
  (Lordszynencja). The highway textures are Charter's own files. We ported
  parts of the editor logic from it, and its checks are the model for our `L##`
  codes. DotCharter opens its `.rscp` projects and saves a Charter-compatible
  `.rscp` beside every `.dcproj` project.
- **[Rocksmith2014.NET and DLC Builder](https://github.com/iminashi/Rocksmith2014.NET)**
  (iminashi). Parts of our writers are C# ports of its F# code, the tone-gear
  catalog is its file, and its `I##` checks are our model. DotCharter writes
  a `.rs2dlc` beside the chart, so DLC Builder is the next step.
- **[CustomsForge](https://customsforge.com)** and the
  **[Rocksmith Custom Song Toolkit](https://github.com/rscustom/rocksmith-custom-song-toolkit)**
  (rscustom) are the community and the long-standing tool whose conventions we
  follow. The `.rs2dlc` we write is shaped for Ignition4's import.

Libraries, models, captures, fonts and recordings are credited in
THIRD-PARTY-NOTICES.md. **If we got your credit wrong, please
[open an issue](https://github.com/SomeeName/DotCharter/issues) and we will fix
it.**

## How DotCharter is made

DotCharter is built with heavy use of AI coding assistants (Claude Code, and
Codex in places). One maintainer directs them. The maintainer decides what to
build, reviews the results and plays them with a real guitar. Several things
keep quality in check. These are a gate of roughly 20,000 tests, a rule that
every change ships with evidence, source lints, and that review and in-game
testing. The limits are real. AI-written code can be wrong in ways tests miss,
so keep your originals and report anything that looks off. Some rig pictures
were generated with an image model
([THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md#3--ai-generated-assets)).

---

## Install and update

1. Download `DotCharter-v<version>-win-x64.zip` from the
   [Releases page](https://github.com/SomeeName/DotCharter/releases).
2. Unzip it anywhere and run `DotCharter.exe`. You don't need to install
   anything else, because the .NET runtime, ffmpeg and yt-dlp come with it.

The app checks GitHub for new releases (**Settings ▸ Updates**). It downloads
only the files that changed and applies the update with one restart. It always
asks you first. Settings, profiles and logs live in your user profile. If you
put an `appconfig.json` next to `DotCharter.exe`, they live there instead. That
is a portable install.

The same Releases page has **RigLab**, the standalone tone rig.

New to DotCharter? The [usage guide](docs/guide/README.md) walks through
charting a song and playing one, screen by screen.

## System requirements

- **Windows 10 or 11, 64-bit.** Windows is the only supported platform.
- A **GPU with Vulkan 1.2 drivers**. Current NVIDIA, AMD and Intel drivers all
  qualify. DotCharter draws the highway and the editor timeline with Vulkan.
- An audio interface or guitar input for Play Mode and the rig. An **ASIO**
  driver gives the lowest latency.
- **Disk space:** about 550 MB for the installed program. The download is a
  375 MB zip. Each stem-separation model is roughly 300 to 360 MB. It is an
  optional download and is not part of the zip. You also need room for the
  stems DotCharter caches for your songs.
- Optional: a DirectX 12 GPU makes stem separation much faster. Without one,
  it runs on the CPU.

---

## Contributing

Bug reports and pull requests are welcome. Read
[CONTRIBUTING.md](CONTRIBUTING.md) first. It covers building from source, the
two test runs and what a pull request needs. Please don't report security
problems as public issues. Follow [SECURITY.md](SECURITY.md) instead. Taking
part means agreeing to the [Code of Conduct](CODE_OF_CONDUCT.md).
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) explains the structure of the
code, and [docs/adr/](docs/adr/README.md) records why the big decisions were
made. [docs/README.md](docs/README.md) is the index of every guide.

### Development

- [Getting started](docs/dev/GETTING-STARTED.md) shows how to clone, build the
  native engine, and run the app and the tests. `tools/setup/Bootstrap.ps1`
  checks your machine without changing it.
- [Debugging and diagnostics](docs/dev/DEBUGGING.md) covers logs, crash
  reports, attaching a debugger and what to include in a bug report.

## Privacy and legal

[PRIVACY.md](PRIVACY.md) lists what the app sends over the network, and when.
[LEGAL.md](LEGAL.md) has the contact for rights holders, explains how a notice
is handled and describes your responsibility for the audio and charts you
import. DotCharter ships no game content and no keys.

## Support

DotCharter is free. If you'd like to support development, you can do so on [Ko-fi](https://ko-fi.com/A3U028PMFM).

## License

DotCharter is free software under the **GNU General Public License v3.0 or
later**. See [LICENSE](LICENSE). Third-party notices are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and in the app under
**Help ▸ License & notices**. That file also carries a GPLv3 section 7
additional permission for combining DotCharter with Microsoft DirectML, the
GPU runtime it ships for stem separation.

## Disclaimer

DotCharter is an independent project. Not affiliated with or endorsed by Ubisoft. Rocksmith is a trademark of Ubisoft Entertainment. All other product names are trademarks of their respective owners.
