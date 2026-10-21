# Third-Party Notices

DotCharter is free software, licensed under the **GNU General Public License,
version 3 or (at your option) any later version** (SPDX: `GPL-3.0-or-later`).
The full, unmodified licence text is in [`LICENSE`](LICENSE), shipped next to
the executable and shown in the app under **Help ▸ License & notices**.

This file is everything else, in three parts:

1. **[DotCharter's licence](#1--dotcharter-license)** — the notice, why it is
   the GPL, and where the corresponding source is.
2. **[Third-party notices](#2--third-party-notices)** — every library, binary,
   font, model, capture, impulse response, data file and picture DotCharter
   builds on, links against, redistributes or downloads: what it is, its
   licence, where it comes from and what ships.
3. **[AI-generated assets](#3--ai-generated-assets)** — the pictures made with
   image-generation tools.

`Infrastructure/ThirdPartyNoticeCoverageTests` fails the build when a NuGet
package, a bundled asset folder, a release-bundled tool or a downloadable model
has no entry here.

---

## 1 — DotCharter License

```
DotCharter — a guitar game and charting workstation.
Copyright (C) 2025-2026 the DotCharter authors and contributors.

This program is free software: you can redistribute it and/or modify it
under the terms of the GNU General Public License as published by the
Free Software Foundation, either version 3 of the License, or (at your
option) any later version.

This program is distributed in the hope that it will be useful, but
WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General
Public License (the LICENSE file) for more details.

Portions of DotCharter are derived from, or interoperate with, the work of
the projects listed in §2.1 below, each under its own licence.

The JuceSharp submodule (the managed wrapper, https://github.com/SomeeName/JuceSharp)
is offered under the MIT License by its own authors; the native engine it
builds, juce_sharp.dll, is a combined work under the GPLv3 (see §2.2).
```

The licence choice is what makes the shipped binary compliant: the audio
engine DotCharter ships (`juce_sharp.dll`) statically links **JUCE 9**
(AGPLv3 / commercial), **Rubber Band 4** (GPLv2+ / commercial), the
**Steinberg VST 3 SDK** (MIT) and the **Steinberg ASIO SDK** headers (GPLv3 /
proprietary). Under the GPLv3 none of those need a paid licence (GPLv3 §13
permits combining with the AGPLv3).

### Corresponding source

Every release `vX.Y.Z` is built from the tag of the same name:
<https://github.com/SomeeName/DotCharter/tree/vX.Y.Z> (GitHub also attaches
the tag's "Source code" archives to the release). The native engine is the
JuceSharp submodule at the commit that tag records. The install's top-level
`README.txt` names the exact tag of the build it came with.

### Additional permission under GNU GPL version 3 section 7: Microsoft DirectML

```
If you modify this Program, or any covered work, by linking or combining
it with Microsoft DirectML (DirectML.dll, or a modified version of that
library), containing parts covered by the terms of the Microsoft DirectML
redistributable licence, the licensors of this Program grant you
additional permission to convey the resulting work. Corresponding Source
for a non-source form of such a combination shall not include the source
code for the parts of Microsoft DirectML used as well as that of the
covered work.
```

DotCharter runs stem separation and lyric alignment on the GPU through ONNX
Runtime's DirectML provider, and ships Microsoft's `DirectML.dll` unmodified
beside the executable (§2.3). The permission above is granted by DotCharter's
authors for DotCharter's own code. It does not change the licence of any
third-party work in §2; none of them links DirectML (ONNX Runtime, which
loads it, is MIT).

---

## 2 — Third-Party Notices

DotCharter is built on top of, links to, or redistributes the works listed
below. Each upstream license is honoured; this section exists so that anyone
shipping or redistributing DotCharter can see at a glance what they are
agreeing to.

> ⚠️ **Commercial redistribution warning.** The native audio engine DotCharter
> ships (`juce_sharp.dll`) statically links **JUCE** (AGPLv3 or commercial),
> **Rubber Band** (GPLv2+ or commercial) and the **Steinberg ASIO SDK** headers
> (GPLv3 or proprietary); the **VST 3 SDK** inside it is MIT. Personal,
> educational and open-source GPLv3 use is generally fine; **closed-source
> commercial redistribution requires obtaining paid licences from JUCE Ltd.,
> Breakfast Quay and Steinberg.**

### 2.1 Code and data lineage

#### RSCharter / Charter — Lordszynencja
- License: **BSD 3-Clause** (the repository's `LICENSE`, reproduced below)
- Source: <https://github.com/Lordszynencja/Charter>
- What ships: DotCharter's chart-authoring workflow, several editor heuristics
  and parts of the RSCharter interop code were inspired by or ported from
  Charter. The 3D highway's three textures,
  `DotCharter.Rendering/Assets/Textures/notes.png`, `fingering.png` and
  `inlays.png`, are Charter's `resources/graphics/textures/default/notes.png`,
  `…/default/fingering.png` and `resources/graphics/inlays/default.png`,
  byte for byte (their git blob hashes equal upstream's); they are embedded in
  `DotCharter.Rendering.dll`.

```
BSD 3-Clause License

Copyright (c) 2025, Lordszynencja

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

jasiohost library: https://github.com/mhroth/jasiohost
rubberband library: https://github.com/breakfastquay/rubberband
```

#### Rocksmith2014.NET — Tapio Malmberg (iminashi)
- License: **MIT** (reproduced below)
- Source: <https://github.com/iminashi/Rocksmith2014.NET>
- What ships: C# ports of its F# code (the SNG writer's derived fields,
  `SoundBankWriter`, tone parsing, and DLC Builder's build steps in
  `DotCharter.Services/Export/DynamicDifficulty/` — `src/Rocksmith2014.DD` (the
  dynamic-difficulty generator) and `src/Rocksmith2014.XML.Processing`'s
  `ArrangementImprover` with the RS2014 XML shapes of `src/Rocksmith2014.XML` it
  works on; each marked "Ported from Rocksmith2014.NET" at the site), one data
  file copied byte for byte (its git blob hash equals upstream's), one data file
  re-encoded from upstream's, embedded in the DotCharter assemblies, and two small
  tables the code regenerates to the same bytes (`DefaultVocalSymbols`,
  `RsEnumerableModels`):
  - `DotCharter.Tone/Audio/Dsp/Tones/tone_gear_data.json` ← `samples/DLCBuilder.Domain/Tones/tone_gear_data.json`
  - `DotCharter.Services/Export/DynamicDifficulty/dd-level-count-model.gz` ← `src/Rocksmith2014.DD.Model/MLModel.zip`:
    the trained FastTree ensemble's trees (thresholds, leaf values, split features)
    as ML.NET loads them, in a compact binary form, so no ML.NET ships

```
MIT License

Copyright (c) 2020 Tapio Malmberg

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

#### OwnAudioSharp — Modernmube
- License: **MIT** (reproduced below)
- Source: <https://github.com/ModernMube/OwnAudioSharp>
- What ships: no OwnAudioSharp code. The HTDemucs ONNX inference in JuceSharp
  (`JuceSharp.StemSeparation.Onnx`, `HtdemucsOnnxInference`) follows
  OwnAudioSharp's HTDemucs sample (STFT padding, bin layout, window and
  chunk-crossfade scheme), so its notice is kept.

```
MIT License

Copyright (c) 2025 Modernmube

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

#### Editor on Fire (EOF)
- DotCharter parses & writes a subset of the EOF chart format for
  interoperability. No EOF source code is redistributed.

### 2.2 Native binary shipped via `JuceSharp/src/JuceSharp/runtimes/win-x64/native/`

DotCharter ships exactly **one** native binary: `juce_sharp.dll`. It is built from `JuceSharp/src/JuceSharp.Native/` (CMake) and statically links the components listed below. The combined binary therefore inherits the **most restrictive** of all bundled licences.

| Component | Version | License | Link |
|---|---|---|---|
| **JUCE** (framework, AudioDeviceManager, AudioFormatManager, VST3PluginFormat, DocumentWindow, MessageManager, …) | 9.0.3 | 🔴 **Dual-licensed: AGPLv3 OR commercial** (the [JUCE 9 licence](https://juce.com/legal/juce-9-licence/)). Closed-source commercial distribution requires a [JUCE commercial licence](https://juce.com/get-juce). | <https://github.com/juce-framework/JUCE> |
| **Rubber Band Library** (statically linked through upstream's single-file build, single/RubberBandSingle.cpp, fetched by CMake at the pinned tag) | 4.0.0 | 🔴 **Dual-licensed: GPLv2+ OR commercial.** Closed-source distribution requires a [commercial licence](https://breakfastquay.com/technology/license.html). | <https://github.com/breakfastquay/rubberband> |
| **ww2ogg** (Adam Gashlin): the Wwise RIFF Vorbis header and codebook rebuild, ported to C++ in `src/wem/WemVorbisDecoder.cpp`, and its two packed codebook libraries (the stock libvorbis and aoTuV beta 6.03 codebooks), generated into `src/wem/wem_codebooks.h` | upstream `master` | 🟢 **BSD-style** (ww2ogg's `COPYING`); the codebook data is under Xiph's BSD licence | <https://github.com/hcs64/ww2ogg> |
| **Steinberg VST 3 SDK** (vendored by JUCE for hosting) | 3.8.0, JUCE 9.0.3's bundled copy | 🟢 **MIT** (the licence of VST 3.8 and later). | <https://github.com/steinbergmedia/vst3sdk> |
| **Steinberg ASIO SDK** headers (vendored by JUCE for ASIO on Windows) | 2.3, JUCE 9.0.3's bundled copy (ASIO SDK 2.3.4 terms) | 🔴 **Dual-licensed: GPLv3 OR Steinberg Proprietary** (the GPLv3 option applies from ASIO SDK 2.3.4). DotCharter and JuceSharp vendor no copy of their own. | <https://www.steinberg.net/developers/> |

> ⚠️ **Commercial distribution.** Because `juce_sharp.dll` statically links JUCE + Rubber Band + the ASIO SDK headers, anyone shipping DotCharter (or any product built on JuceSharp) **commercially** without releasing their full source under GPLv3 must obtain commercial licences from **JUCE Ltd., Breakfast Quay and Steinberg**. Personal, educational and open-source GPLv3 use does not require any paid licences.

> P/Invoke / `LoadLibrary` searches the application directory; .NET also probes `runtimes/win-x64/native/`. The build deploys `juce_sharp.dll` into both locations automatically.

### 2.2.1 The built-in guitar and bass voice's recordings

The chart synth's built-in guitar and bass play DI recordings packed into
sample packs (`*.dcvp`, built — not stored in git — by
`JuceSharp/src/JuceSharp.Native/tools/voice/build_voice_pack.py` from a pinned,
SHA-256-checked manifest; sources and pinned commits in
`JuceSharp/src/JuceSharp.Native/src/dsp/voice/CREDITS.md`). Every recording in
the packs the release ships may be redistributed with the program. Attribution for every
pack is in `voices/VOICE-CREDITS.txt` and inside each pack.

| Pack | Recordings | Licence | Where it ships |
|---|---|---|---|
| `guitar.dcvp` | Karoryfer *Black And Green Guitars* (Gretsch Anniversary), *Shinyguitar* (dead notes) | CC0 1.0 | the release, `voices/` |
| `bass.dcvp` | Karoryfer *Black And Blue Basses*, *Growlybass*, *Swagbass* (slap thumb noises) | CC0 1.0 | the release, `voices/` |
| `strat-neck.dcvp`, `strat-neck-middle.dcvp`, `strat-middle.dcvp`, `strat-middle-bridge.dcvp`, `strat-bridge.dcvp` | **EGFxSet** clean notes, one pack per pickup position, plus *Black And Green Guitars* release noises and dead notes | CC BY 4.0 (+ CC0) | the release, `voices/` |
| `guitar-arts.dcvp` | **Guitar-TECHS** player 1's palm mutes, pinch and natural harmonics | CC BY 4.0 | the release, `voices/` |

**Karoryfer Samples** (Karoryfer Lecolds): *Black And Green Guitars*,
*Shinyguitar*, *Black And Blue Basses*, *Growlybass* and *Swagbass* — all
**CC0 1.0 Universal** (public domain dedication; no attribution required —
credited with thanks; verified at each repository's `LICENSE` at the pinned
commit). A small excitation bank derived from CC0 recordings (*Emilyguitar*,
*Growlybass*, *Black And Blue Basses*) is compiled into `juce_sharp.dll`.

**EGFxSet** by Hegel Pedroza, Gerardo Meza and Iran R. Roman,
<https://doi.org/10.5281/zenodo.7044411>, and **Guitar-TECHS** by Hegel Pedroza,
Termeh Taheri, Wallace Abreu, Ryan Corey and Iran R. Roman,
<https://doi.org/10.5281/zenodo.14963133>, both licensed under **Creative
Commons Attribution 4.0 International**
(<https://creativecommons.org/licenses/by/4.0/>) — attribution REQUIRED, and
given in `voices/VOICE-CREDITS.txt` and in every pack. Changes: single notes cut
out, trimmed, resampled to 44.1 kHz, 16-bit, level-normalised, split per pickup
position. The synth also filters them to SIMULATE other guitars (Telecaster,
Les Paul, the HSS bridge humbucker, coil splits); those sounds are derived from,
not recordings of, those instruments.

No other non-commercial or no-derivatives recording (IDMT-SMT, RAVE, GOAT,
EGDB, commercial libraries) is in any voice pack.

### 2.3 NuGet packages

Every package a DotCharter or JuceSharp project references, by id. The
version is the one in the referencing `.csproj`. Packages the tests and build
tools use are listed separately: they are never part of a release.

#### Shipped with the app

| Package | License | Source |
|---|---|---|
| `Avalonia`, `Avalonia.Desktop`, `Avalonia.HarfBuzz`, `Avalonia.Themes.Fluent`, `Avalonia.Controls.ColorPicker` | MIT | <https://github.com/AvaloniaUI/Avalonia> |
| `Avalonia.Fonts.Inter` (carries the **Inter** font, SIL OFL 1.1, <https://github.com/rsms/inter>) | MIT (package), OFL-1.1 (font) | <https://github.com/AvaloniaUI/Avalonia> |
| `Dock.Avalonia`, `Dock.Avalonia.Themes.Fluent`, `Dock.Model.Avalonia`, `Dock.Model.Mvvm` | MIT | <https://github.com/wieslawsoltes/Dock> |
| `CommunityToolkit.Mvvm` | MIT | <https://github.com/CommunityToolkit/dotnet> |
| `BouncyCastle.Cryptography` (Ed25519 verification of the release signature, `Update/ReleaseSignature`) | MIT | <https://github.com/bcgit/bc-csharp> |
| `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `System.Security.Cryptography.ProtectedData` | MIT | <https://github.com/dotnet/runtime> |
| `Silk.NET.Core`, `Silk.NET.Maths`, `Silk.NET.Vulkan`, `Silk.NET.Vulkan.Extensions.KHR`, `Silk.NET.Vulkan.Extensions.EXT`, `Silk.NET.DXGI`, `Silk.NET.Direct3D11` | MIT | <https://github.com/dotnet/Silk.NET> |
| `SkiaSharp` | MIT | <https://github.com/mono/SkiaSharp> |
| `AlphaTab` (carries the **Bravura** music font, SIL OFL 1.1, <https://github.com/steinbergmedia/bravura>) | MPL-2.0 | <https://github.com/CoderLine/alphaTab> |
| `AlphaSkia.Native.Windows` | MPL-2.0 | <https://github.com/CoderLine/alphaSkia> |
| `Microsoft.Data.Sqlite` | MIT | <https://github.com/dotnet/efcore> |
| `SQLitePCLRaw.bundle_e_sqlite3` (bundles `e_sqlite3.dll`, a build of **SQLite**, which is in the public domain: <https://sqlite.org/copyright.html>) | Apache-2.0 | <https://github.com/ericsink/SQLitePCL.raw> |
| `Anthropic` (the Claude API client behind the AI assistant) | MIT | <https://github.com/anthropics/anthropic-sdk-csharp> |
| `Serilog`, `Serilog.Sinks.Async`, `Serilog.Sinks.Console`, `Serilog.Sinks.File` | Apache-2.0 | <https://github.com/serilog> |
| `Microsoft.ML.OnnxRuntime.DirectML` (`onnxruntime.dll`) | MIT | <https://github.com/microsoft/onnxruntime> |
| `Microsoft.AI.DirectML` (`DirectML.dll`) | Microsoft DirectML redistributable licence (proprietary, **not** MIT): redistributed unmodified beside the app, as that licence permits; combining DotCharter with it is covered by the GPLv3 §7 additional permission in §1 | <https://www.nuget.org/packages/Microsoft.AI.DirectML> |

The ONNX Runtime packages are referenced by JuceSharp's
`JuceSharp.StemSeparation.Onnx`, which runs stem separation and lyric
alignment.

#### Tests and build tools only (never shipped)

| Package | License | Source |
|---|---|---|
| `xunit.v3`, `xunit.runner.visualstudio` | Apache-2.0 | <https://github.com/xunit/xunit> |
| `Microsoft.NET.Test.Sdk` | MIT | <https://github.com/microsoft/vstest> |
| `coverlet.collector`, `coverlet.msbuild` | MIT | <https://github.com/coverlet-coverage/coverlet> |
| `Avalonia.Headless`, `Avalonia.Skia` | MIT | <https://github.com/AvaloniaUI/Avalonia> |
| `Microsoft.CodeAnalysis.CSharp`, `Microsoft.CodeAnalysis.BannedApiAnalyzers` | MIT | <https://github.com/dotnet/roslyn> |
| `Silk.NET.Shaderc`, `Silk.NET.Shaderc.Native` (`tools/ShaderCompiler`; shaderc itself is Apache-2.0, <https://github.com/google/shaderc>) | MIT | <https://github.com/dotnet/Silk.NET> |
| `NVorbis`, `YamlDotNet` (JuceSharp's tests) | MIT | <https://github.com/NVorbis/NVorbis>, <https://github.com/aaubry/YamlDotNet> |
| `Ww2Ogg.Core` (JuceSharp's tests: the reference the native `.wem` decoder is compared against) | BSD-3-Clause | <https://www.nuget.org/packages/Ww2Ogg.Core/> |

### 2.4 Command-line tools bundled in the release

`tools/release/New-Release.ps1` copies two programs into the install's
`tools\` folder and writes their notice beside them
(`tools\THIRD-PARTY-NOTICES.txt`). DotCharter runs them as separate programs.
It also copies the pinned tonelab build into `tools\tonelab\` (below).

#### ffmpeg — `ffmpeg.exe`
- What ships: **`8.1.3-dotcharter-min1`**, DotCharter's own minimal static
  win64 build — only the decoders, encoders, formats and filters the app uses.
- License: **LGPL v2.1 or later.** The build is configured WITHOUT
  `--enable-gpl` / `--enable-nonfree` (`tools/release/ffmpeg-min/build.sh`), so
  it is the LGPL flavour, not a GPL build. Statically linked: libvorbis +
  libogg (BSD), Opus (BSD), LAME (LGPL v2), zlib (zlib licence).
  `ffmpeg-LICENSE.txt` ships beside it.
- How it is built (all in this repository): `tools/release/Build-MinimalFfmpeg.ps1`
  pins every source tarball by URL and SHA-256 and MSYS2 by its base archive;
  `tools/release/ffmpeg-min/build.sh` is the configure line;
  `tools/release/ffmpeg-min/components.json` is the component list and the
  version tag; `tools/release/FfmpegInventory.psm1` checks the built exe
  carries every component. The build writes `ffmpeg-BUILDINFO.txt` (exact
  sources, toolchain package versions, configure line), which ships beside the
  exe.
- Corresponding source: the unmodified FFmpeg 8.1.3 release,
  <https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz>, plus the library tarballs
  pinned in `Build-MinimalFfmpeg.ps1` and the scripts above.
- Upstream: <https://ffmpeg.org/> · <https://www.ffmpeg.org/legal.html>

#### yt-dlp — `yt-dlp.exe`
- What ships: the official `yt-dlp.exe` of the pinned release (`2026.07.04`,
  `$YtDlpVersion` in `New-Release.ps1`), downloaded unmodified from
  <https://github.com/yt-dlp/yt-dlp/releases>.
- License: **The Unlicense** (public domain dedication). The official Windows
  executable bundles Python and third-party packages, each under its own
  licence; yt-dlp's own `THIRD_PARTY_LICENSES.txt` in that release lists them.
- Source: <https://github.com/yt-dlp/yt-dlp>

#### tonelab — `tonelab.dll`, `tonelab-launch.exe`
- What ships: `tonelab.dll` (the game side of the in-game tone preview, loaded
  into the user's own running Rocksmith 2014), `tonelab-launch.exe` (the 32-bit
  helper that injects it) and the default `tonelab.ini`, built Release|Win32 by
  the tonelab repository and pinned by SHA-256 in
  `tools/release/tonelab/tonelab.json` (commit and hashes). `-SkipTonelab`
  leaves them out.
- License: **MIT** (`tools/release/tonelab/LICENSE.txt`, shipped as
  `tools\tonelab\LICENSE.txt`); the same author as DotCharter.
- Contains no decryption, no key material and no game files; it patches the
  running game's code in memory and the game loads its own data.
- Source: the tonelab repository (private, same author), at the pinned commit.

The separate lyric-sync command-line tool (`tools/lyric-sync`, not part of the
app release) bundles its own ffmpeg; `tools/lyric-sync/README.md` carries its
notice.

### 2.5 Fonts

Embedded in `DotCharter.Tone.UI.dll` from `DotCharter.Tone.UI/Assets/Fonts/`,
each with its licence file beside it in the repository.

| Font | Files | Copyright | License | Source |
|---|---|---|---|---|
| **Atkinson Hyperlegible** | `AtkinsonHyperlegible-Regular.ttf`, `AtkinsonHyperlegible-Bold.ttf` | Copyright 2020 Braille Institute of America, Inc. | SIL OFL 1.1 (`OFL-AtkinsonHyperlegible.txt`) | <https://github.com/googlefonts/atkinson-hyperlegible> |
| **JetBrains Mono** | `JetBrainsMono-Regular.ttf`, `JetBrainsMono-Bold.ttf` | Copyright 2020 The JetBrains Mono Project Authors (https://github.com/JetBrains/JetBrainsMono) | SIL OFL 1.1 (`OFL-JetBrainsMono.txt`) | <https://github.com/JetBrains/JetBrainsMono> |
| **Inter** | via `Avalonia.Fonts.Inter` (§2.3) | The Inter Project Authors | SIL OFL 1.1 | <https://github.com/rsms/inter> |
| **Bravura** | via `AlphaTab` (§2.3) | Steinberg Media Technologies GmbH | SIL OFL 1.1 | <https://github.com/steinbergmedia/bravura> |

The fonts are unmodified. The SIL Open Font License 1.1, which all four use:

```
-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded, 
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```

### 2.6 Bundled NAM neural-amp captures

63 `.nam` captures ship in the install's `NamCaptures\` folder (from
`DotCharter.UI/NamCaptures/`) and install into the user's capture library on
first run. Per-file author, source path and licence are in
[`DotCharter.UI/NamCaptures/ATTRIBUTION.md`](DotCharter.UI/NamCaptures/ATTRIBUTION.md)
and `manifest.json`, both of which ship beside the files;
`BundledNamCaptureAttributionTests` fails the build for a file without them.

| Count | Licence | Source | Authors |
|---|---|---|---|
| 3 | **MIT** | <https://github.com/sdatkinson/NeuralAmpModelerPlugin> (`Models/`), repacked from the legacy two-file form into single `.nam` files, weights unchanged | Steven Atkinson |
| 1 | **MIT** | <https://github.com/sdatkinson/NeuralAmpModelerCore> (`example_models/wavenet_a1_standard.nam`), byte for byte | Steven Atkinson |
| 1 | **MIT** | <https://github.com/mikeoliphant/NeuralAudio> (`Utils/Models/BossWN-feather.nam`), byte for byte | Mike Oliphant |
| 58 | **GPL-3.0** | <https://github.com/pelennor2170/NAM_models> (`COPYING`), byte for byte, renamed only | the contributors named per file in `ATTRIBUTION.md`: Tim R, Phillipe P, Helga B, Sascha S, Tudor N, Jason Z, Tom C, Roman A, Keith B, Peter N |

Each of the 60 byte-for-byte files has the same git blob hash as the upstream
file. The capture names describe the circuit
the capture author recorded; no trademark claim or endorsement is implied.

### 2.7 Impulse responses compiled into `juce_sharp.dll`

`JuceSharp/src/JuceSharp.Native/src/dsp/cores/dcdsp_cab_irs.h` (26 cabinet
IRs) and `dcdsp_acoustic_irs.h` (3 acoustic-body IRs) are **synthetic, made
for this project** — no recorded or third-party impulse response is used:

- Cabinet models 0–11 and the three acoustic bodies: minimum-phase IRs
  reconstructed from authored magnitude targets (roll-offs, resonances,
  reflection combs), as each header's comment describes. Their offline
  generator was never committed; the rows are kept verbatim.
- Cabinet models 12–25: generated by
  `JuceSharp/src/JuceSharp.Native/tools/cab_irs/gen_cab_irs.py` from published
  loudspeaker frequency-response data (Jensen's 1 W / 1 m anechoic sweeps in
  `tools/cab_irs/driver_curves.json`, and KEF's published R7 Meta response)
  through standard box, crossover and directivity formulas. Only measured
  facts are used; no third-party file is redistributed.

Licence: as the rest of DotCharter / JuceSharp.

### 2.8 Artwork

| What | Where | Origin | Licence |
|---|---|---|---|
| App icon | `DotCharter.UI/Assets/app-icon.png`, `app-icon.ico` | Generated for DotCharter with an image-generation tool (§3); prompt and master in `tools/branding/` | DotCharter's (GPL-3.0-or-later) |
| 3D highway textures | `DotCharter.Rendering/Assets/Textures/` | Charter by Lordszynencja, byte for byte (§2.1) | BSD 3-Clause |
| Amp chassis, speaker cabinet | `DotCharter.Tone.UI/Assets/Rig/amp-chassis.png`, `speaker-cabinet.png` | Generated with OpenAI GPT Image (§3); prompt and masters in `tools/branding/rig/`. No third-party rights claimed | DotCharter's |
| Module pictures and materials (the rig "texture pack": device renders, knob caps, LEDs, switches, finishes) | `DotCharter.Tone.UI/Assets/Rig/Art/` | Generated with OpenAI GPT Image (§3). No third-party rights claimed | DotCharter's |
| Unit faceplates and knob caps | `DotCharter.Tone.UI/Assets/Rig/Faces/`, `DotCharter.Tone.UI/Assets/Rig/Caps/`, `faceplates.json` | Generated with OpenAI GPT Image (§3), cut out and measured by `tools/rig-art/import-faceplates.py`. No third-party rights claimed, except: `Faces/miku-vocal-synth-face.webp` depicts Hatsune Miku, a character © Crypton Future Media, INC., as non-commercial fan art; Crypton's rights in the character are not DotCharter's | DotCharter's (character: Crypton Future Media) |
| 44 placeholder module pictures | `DotCharter.Tone.UI/Assets/Rig/Art/` (the ids listed in `tools/rig-art/generate-gear-art.py`) | Drawn procedurally with Pillow by that script | DotCharter's |

The gear the pictures depict is drawn generically; any resemblance of a unit's
look to a real product's is not a trademark claim or an endorsement.

### 2.9 Models downloaded on demand

None of these ship in the release; the app downloads one only when the user
asks for it (Settings ▸ Stem models, lyric auto-sync, tone matching), checks
its SHA-256 and stores it in the user's data folder.

#### Stem separation

| File | Model | Downloaded from | Licence |
|---|---|---|---|
| `ggml-model-htdemucs-4s-f16.bin`, `ggml-model-htdemucs-6s-f16.bin`, `ggml-model-hdemucs_mmi-v3-f16.bin` | Demucs v4 / Hybrid Demucs (Meta), converted to ggml for demucs.cpp | <https://huggingface.co/datasets/Retrobear/demucs.cpp> | Weights MIT (<https://github.com/facebookresearch/demucs>); the re-host's card says MIT |
| `htdemucs_6s.onnx` | HTDemucs v4 6-stem, ONNX export | <https://huggingface.co/arjune123/demucs-onnx> | Weights MIT (Demucs); the re-host states no licence |
| `bs_roformer_fp16.onnx` | BS-RoFormer (viperx's checkpoint), ONNX | <https://huggingface.co/safescribeai/bs-roformer-onnx-fp16> | **No explicit licence** stated for the weights |
| `bs_roformer_sw_6stem_fp16.onnx` | BS-RoFormer SW 6-stem, ONNX | <https://huggingface.co/elicwhite/bs-roformer-sw-6stem-onnx> | **Weights' licence unknown.** No licence was ever stated for these weights, and the trainer is unknown. The lineage is the checkpoint `BS-ROFO-SW-Fixed`, re-hosted on Hugging Face by jarredou (account since deleted); a mirror survives at <https://huggingface.co/enerjazzer/BS-ROFO-SW-Fixed>. The MIT licences of lucidrains/BS-RoFormer, ZFTurbo/Music-Source-Separation-Training and elicwhite/bs-roformer-web cover the code, not the weights. |
| `bs_roformer_sw_6stem_13s_fp16.onnx` | The same weights re-exported at a 13.35 s chunk by `JuceSharp/tools/bs-roformer-sw-export` | DotCharter's own release `models-bs-roformer-sw-v1` | **Weights' licence unknown.** No licence was ever stated for these weights, and the trainer is unknown. The lineage is the checkpoint `BS-ROFO-SW-Fixed`, re-hosted on Hugging Face by jarredou (account since deleted); a mirror survives at <https://huggingface.co/enerjazzer/BS-ROFO-SW-Fixed>. The MIT licences of lucidrains/BS-RoFormer, ZFTurbo/Music-Source-Separation-Training and elicwhite/bs-roformer-web cover the code, not the weights. DotCharter hosts this re-export on its own release and removes it on request from a rights holder (see [LEGAL.md](LEGAL.md)). |
| `UVR-MDX-NET-Inst_HQ_3.onnx`, `UVR-MDX-NET-Voc_FT.onnx`, `Kim_Vocal_2.onnx`, `Kim_Inst.onnx`, `UVR-MDX-NET-Inst_Main.onnx` | Ultimate Vocal Remover MDX-Net community models | <https://huggingface.co/seanghay/uvr_models>, <https://huggingface.co/Politrees/UVR_resources> | UVR's tooling is MIT; the weights carry **no explicit licence** |

Each entry's licence note is also in the code that downloads it
(`JuceSharp.StemSeparation.Onnx/OnnxModelCatalog`,
`JuceSharp.StemSeparation/Runtime/DemucsGgmlModelCatalog`,
`DotCharter.Services/Audio/StemSeparation/StemModelCatalog`).

#### Lyric alignment

| File | Model | Downloaded from | Licence |
|---|---|---|---|
| `lyric-align-v1.zip` | MMS-FA, `facebook/mms-300m-1130-forced-aligner`, exported to ONNX (`tools/lyric-sync/export-models.md`) | DotCharter's release `v0.41.0` | **CC-BY-NC 4.0** — non-commercial use only. Attribution: Meta AI, Massively Multilingual Speech, <https://huggingface.co/facebook/mms-300m-1130-forced-aligner> |
| `lyric-align-en-v1.zip` | `facebook/wav2vec2-large-960h-lv60-self`, exported to ONNX | DotCharter's release `v0.41.0` | Apache-2.0, <https://huggingface.co/facebook/wav2vec2-large-960h-lv60-self> |

#### Tone matching

| File | What | Downloaded from | Licence |
|---|---|---|---|
| `rig-estimator-v1.zip` | The rig estimator, trained by this project (`tools/rig-model`) | DotCharter's release `v0.55.0` | DotCharter's. Trained on clips rendered from **EGFxSet** (Pedroza, Meza, Roman; <https://doi.org/10.5281/zenodo.7044411>) and **Guitar-TECHS** DI recordings, both CC BY 4.0 (<https://creativecommons.org/licenses/by/4.0/>), as `tools/rig-dataset/README.md` records |
| `rig-library-v1.zip` | The tone library the estimator matches against, rendered by this project's rig | DotCharter's release `v0.55.1` | DotCharter's |

### 2.10 Repository-only files (not shipped)

| File | Origin |
|---|---|
| `DotCharter.Tests/Services/Parsing/Fixtures/tone-440hz-0.3s.ogg` | A synthetic 0.3 s 440 Hz tone encoded with ffmpeg's libvorbis; made for the test. No recorded material |
| `DotCharter.Tests/Services/Parsing/Fixtures/tone-440hz-0.3s.nvorbis-f32.bin` | NVorbis's decode of the file above, captured by the test suite |
| `DotCharter.Tests/Services/Parsing/Fixtures/tone-stereo48k-inline-mod.wem` | Written by JuceSharp's `WemFixtureWriter` from JuceSharp's synthetic `stereo48k.ogg`; made for the test |
| `DotCharter.Tests/Services/Export/Fixtures/Rs7/rs7fixture7_p.psarc` | Exported by DotCharter itself from a test project with 4 KB of placeholder audio (`Rs7PlainFixtureTests.Write_the_fixture`); no game content |
| `DotCharter.Tests/Services/Export/DynamicDifficulty/Fixtures/*.xml.gz` | RS2014 XMLs from the owner's own EOF charts, made anonymous by a script (frets permuted per string, chord names numbered, metadata blanked), and what Rocksmith2014.NET's F# generator and `ArrangementImprover` (MIT, §2.1) wrote for each, gzip-compressed; no song can be read back from them |
| `DotCharter.Tests/Services/Export/DynamicDifficulty/Fixtures/mlnet-predictions.csv.gz` | 2,000 scores ML.NET 4.0.0 predicted with Rocksmith2014.NET's `MLModel.zip` (MIT, §2.1) for random inputs |
| `DotCharter.Tests/Ai/Model/Assets/rig-estimator-v2-contract.onnx` | A ~20 KB graph with constant, hand-chosen outputs, written by `tools/rig-model/tests/make_contract_fixture.py`; no trained weights |

Licence: as the rest of DotCharter. Tests that need copyrighted game content
(official `.psarc` files) read the owner's local copies and skip without them;
none is committed.

#### Test tools downloaded on demand (never shipped, never committed)

| Tool | License | Source |
|---|---|---|
| **Mesa lavapipe** (`vulkan_lvp.dll`, the software Vulkan rasterizer the headless GPU golden frames can run on). Mesa 26.2.4, Windows x64 build by jakoch/rasterizers release `20261006`; LLVM is linked into the DLL | MIT (Mesa); Apache-2.0 WITH LLVM-exception (LLVM) | <https://gitlab.freedesktop.org/mesa/mesa>, build: <https://github.com/jakoch/rasterizers> |

`tools/ci/Get-SoftwareVulkan.ps1` downloads it at a pinned version, refuses
it unless its SHA-256 matches, and unpacks it outside the repository
(`%LOCALAPPDATA%\DotCharter\swvk\`). Only a test process opted in with
`DOTCHARTER_GPU_TESTS=sw` loads it; nothing is installed and the app never
uses it.

### 2.11 SDKs and system components

- **JUCE 9**, the **Steinberg VST 3 SDK** and the **Steinberg ASIO SDK** — see §2.2. End-users supply their own VST3 plugins; those plugins are **not** redistributed by DotCharter.
- **Vulkan / Direct3D / OpenGL drivers** — system-provided.
- **.NET runtime** (self-contained in the release) — MIT, <https://github.com/dotnet/runtime>.

### 2.12 Asset inventory

Every folder of the repository's DotCharter projects that holds binary assets,
and the section that covers it. `ThirdPartyNoticeCoverageTests` fails when a
folder holds a font, picture, model, capture, archive, binary or audio file
and is missing from this list.

| Folder | Covered by |
|---|---|
| `DotCharter.UI/NamCaptures/` | §2.6 |
| `DotCharter.UI/Assets/` | §2.8 |
| `DotCharter.Tone.UI/Assets/Fonts/` | §2.5 |
| `DotCharter.Tone.UI/Assets/Rig/` | §2.8 |
| `DotCharter.Tone.UI/Assets/Rig/Art/` | §2.8 |
| `DotCharter.Tone.UI/Assets/Rig/Caps/` | §2.8 |
| `DotCharter.Tone.UI/Assets/Rig/Faces/` | §2.8 |
| `DotCharter.Rendering/Assets/Textures/` | §2.1, §2.8 |
| `DotCharter.Rendering/Shaders/` | DotCharter's own: SPIR-V compiled from the GLSL beside it (`tools/ShaderCompiler`) |
| `DotCharter.Services/Export/Sng/` | §2.1 (Rocksmith2014.NET) |
| `DotCharter.Services/Export/DynamicDifficulty/` | §2.1 (Rocksmith2014.NET) |
| `DotCharter.Tone/Audio/Dsp/Tones/` | §2.1 (Rocksmith2014.NET) |
| `DotCharter.Tests/Services/Parsing/Fixtures/` | §2.10 |
| `DotCharter.Tests/Services/Export/Fixtures/Rs7/` | §2.10 |
| `DotCharter.Tests/Services/Export/DynamicDifficulty/Fixtures/` | §2.10 |
| `DotCharter.Tests/Ai/Model/Assets/` | §2.10 |
| `DotCharter.Tests/UI/Goldens/ui/` | Approved screenshots of DotCharter itself (golden-image tests); DotCharter's own |

### 2.13 Reporting a missing notice

If you spot a third-party component used by DotCharter that is **not** listed
here, please open an issue — we will add it.

---

## 3 — AI-generated assets

These pictures were made with image-generation tools rather than drawn or
photographed. No third-party rights are claimed in them; they are DotCharter's,
under its licence (§1), and the gear they show is drawn generically (§2.8).
Each one's row in §2.8 says where it lives.

| What | Generated with |
|---|---|
| The tone rig's gear-model textures: amp chassis and speaker cabinet (`DotCharter.Tone.UI/Assets/Rig/`), module pictures and materials (`Assets/Rig/Art/`), unit faceplates and knob caps (`Assets/Rig/Faces/`, `Assets/Rig/Caps/`) | OpenAI GPT Image |
| The app icon (`DotCharter.UI/Assets/app-icon.png`, `app-icon.ico`) | An image-generation tool; prompt and master in `tools/branding/` |

The 44 placeholder module pictures listed in §2.8 are not AI-generated: a
script draws them.

DotCharter's code was also written largely with AI coding assistants and
reviewed and tested by its maintainers; that is a statement about how it was
made, not a licence term, and the README's credits say more.
