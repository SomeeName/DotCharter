# tools/release — one-command release pipeline

CI cannot upload release artifacts (the GitHub Actions artifact storage quota is
exhausted), so every release is **built and shipped locally**. This folder turns
the by-hand procedure into one command.

## Usage

```powershell
# From anywhere (paths are resolved relative to the repo root):
pwsh tools/release/New-Release.ps1                 # full build, version from DotCharter.UI.csproj
pwsh tools/release/New-Release.ps1 -Version 1.1.0  # explicit version
pwsh tools/release/New-Release.ps1 -SkipTools      # UI pack WITHOUT the bundled media tools (fast)
pwsh tools/release/New-Release.ps1 -SkipTonelab    # UI pack WITHOUT tools/tonelab/ (no pinned tonelab build here)
pwsh tools/release/New-Release.ps1 -AllowMissingTools  # pack even if a pinned media tool can't be fetched (default: FAIL)
pwsh tools/release/New-Release.ps1 -SkipHeadlessUi # skip the blocking UI test gate (escape hatch)
pwsh tools/release/New-Release.ps1 -SkipLaunchSmoke # skip starting the packed exe (escape hatch)
pwsh tools/release/New-Release.ps1 -Tone3000ClientId t3k_pub_...  # bake the rig's TONE3000 key (never commit it)
pwsh tools/release/New-Release.ps1 -SkipRigLab     # DotCharter only, no standalone rig
pwsh tools/release/New-Release.ps1 -SelfTest       # verify the pack's own checks, build nothing
pwsh tools/release/New-Release.ps1 -SmokeOnly <zip|folder>  # binary check + launch smoke of an existing pack
pwsh tools/release/New-Release.ps1 -WithPlugins -Repo <owner/name>   # the build WITH the plugins under plugins/ (see "Two modes")
```

Prerequisites:

- `juce_sharp.dll` already built at
  `JuceSharp/src/JuceSharp/runtimes/win-x64/native/juce_sharp.dll`
  (the script refuses to publish without it and prints the cmake commands).
- The version bump commit is in (see checklist below).
- The release-signing key (see "Release signing" below). Without it the script
  stops in preflight.

The script builds the **5 release assets** into `artifacts/release/v<ver>/`,
writes a changelog stub (`notes-v<ver>.md`, grouped from `git log` since the
last tag), and **prints** the exact `gh release create` command. It never runs
`gh` itself — review the notes before you paste the command. Starting the packed
`DotCharter.exe` is no longer a manual step: the launch smoke below does it, and
the pack fails if it does not start.

## Two modes: the public build and the plugin build (`-WithPlugins`)

A plugin is a folder `plugins/<name>/` (git-ignored, its own repository) with its
project file at the top; its assembly is named after that file
(`Get-PluginAssemblyNames`).

| | default (public) | `-WithPlugins` |
|---|---|---|
| UI publish | `-p:DotCharterPlugins=false`: `plugins/` is ignored even when something is checked out | `-p:DotCharterPlugins=true`: at least one plugin must be checked out |
| payload folder, zip, `-SmokeOnly` input | REFUSED when they carry a plugin assembly | REFUSED when they LACK one |
| printed `gh release create -R` | `-Repo`, by default the public repo | `-Repo`, required, never the public repo |
| `--target` | the HEAD of `-TagCheckout`, by default the DotCharter checkout | the same |

Every check is one function in `PluginPayloadCheck.psm1` (`Assert-ReleasePayloadPlugins`,
`Get-UiPluginPublishArgs`, `Resolve-ReleaseFeed`), run on fixtures by
`Infrastructure/ReleasePluginRefusalTests`; `-SelfTest` exercises both refusals and
`-PayloadOnly -WithPlugins` is the dry run of the plugin payload. The script names no
plugin and no plugin's repo: a plugin supplies its own update feed (its module
registers an `IUpdateFeed`, `docs/agents/services/infrastructure-update/update-feed.md`)
and its repository carries the release script that calls this one with `-WithPlugins`,
its `-Repo` and its checkout as `-TagCheckout`.

A build carrying a plugin updates from that feed and refuses a payload without the
plugin (`PluginUpdateGuard`), so the two kinds of install never cross. **Releasing a
plugin build:** commit both checkouts, run the plugin's release script, review the
notes, and paste the printed line.

## Pack integrity: what stops a corrupt pack

A restart that interrupts a pack while it writes `bin/Release` / `obj/Release`
can leave files whose allocation NTFS kept but whose data it did not: the next
incremental build reuses them (their timestamps look current) and the zip
validates, because the updater rules check LAYOUT, not content. The exe then dies
at launch with `BadImageFormatException`. Three checks stand in the way, each
failing the pack by default:

| Check | When | Catches |
|---|---|---|
| **Clean Release build** (`Clear-ReleaseBuildOutputs`) | before the publishes | deletes `bin/Release` + `obj/Release` of every project in the ProjectReference closure of `DotCharter.UI` and `DotCharter.RigLab`, so every shipped byte is compiled by this run. `obj/project.assets.json` stays. A locked output fails here, by name |
| **Binary check** (`CheckPayloadBinaries.cs` over `DotCharter.Services/Update/ReleasePayloadIntegrity`) | each staged payload (UI after the tools are bundled, RigLab) BEFORE hashing/zipping, and again on the extracted zip | every `.dll`/`.exe` must start with `MZ`, have PE headers that parse and — if managed — metadata that reads; no file of any kind may be non-empty and all zeros. Prints each offender with its fault (`AllZeros`, `NotPortableExecutable`, `BadPeHeaders`, `BadMetadata`, `Empty`). Rules pinned by `ReleasePayloadIntegrityTests` in the default gate. ~5 s on a real payload |
| **Launch smoke** (`Invoke-PackSmoke`) | after the UI zip validates | extracts the ZIP to `artifacts/release/v<ver>/smoke`, points `DOTCHARTER_DATA_ROOT` at a folder inside that copy and writes `appconfig.json` there and beside the exe (config, profiles, logs and caches stay in the copy — your `%APPDATA%\DotCharter` and `%LOCALAPPDATA%\DotCharter` are never read or written; audio opens WASAPI, never ASIO), starts the TOP `DotCharter.exe` (the launcher) **on a hidden Win32 desktop** (`HiddenDesktop.psm1`, below), finds `app\DotCharter.exe` as its child, and requires that app's `StartupPhases:` log line within `-SmokeTimeoutSeconds` (120) and the app alive `-SmokeSettleSeconds` (5) later. FAILS if DotCharter ever owns the foreground window; prints the foreground window before/after. Kills the process tree and releases the desktop after. On failure it prints the exit code, stderr and the log tail, and DELETES the UI zip so the printed upload command cannot ship it. Warns if the line lacks `r2r=yes`. A real pack takes ~20 s |

`-SmokeOnly <zip|folder>` runs the binary check and the launch smoke on an
existing pack (copied/extracted first; the input is never modified) — use it to
re-verify a zip before publishing it. `-SkipLaunchSmoke` exists for iterating on
the script; the final summary then says nobody started the exe.

### Hidden-desktop launches (`HiddenDesktop.psm1`)

A minimised or no-activate launch is NOT enough to keep the app out of the
foreground: it activates its own window when it shows, and 9 of 42 such
automated launches still ended in front of whatever the owner was doing (a game,
say). `Start-HiddenDesktopProcess` creates a never-shown desktop
(`CreateDesktop`), starts the program there (`STARTUPINFO.lpDesktop`) suspended
at idle priority, sets its affinity (default CPUs 8-15, intersected with the
machine's), resumes it and returns a handle that HOLDS the desktop until
`Stop-HiddenDesktopProcess` kills the tree: closing the desktop before the child
has connected to it destroys it, and the child dies in user32 initialisation.
The launcher passes its `STARTUPINFO` on, so the app inherits the desktop.
`Get-ChildProcess` finds the app under the launcher. Standard output and error
redirect to files like `Start-Process`'s.

**Every scripted launch of the app under `tools/` goes through it**, linted by
`DotCharter.Tests/Infrastructure/ScriptedAppLaunchLintTests`. The one exemption
is `tools/agent/screen/Capture-App.ps1`, which screenshots the visible monitor —
a hidden desktop is never composited and `CopyFromScreen` there fails with
"invalid handle".

## The blocking gate: the headless UI suite

**The script runs `DotCharter.Tests` with `--filter "FullyQualifiedName~UiPerfSmokeTests"`
before it publishes anything, and refuses to build the assets if it is red.**
That suite is the only thing in the repo that catches the five classes of XAML
fault which build perfectly clean and fail at runtime (unresolvable
`StaticResource`, `StringFormat` fed a `Binding`, `RelayCommand<int>` fed a
literal `CommandParameter`, a view that fails to construct, a `Thickness`
property given an `x:Double` `Space*` token). It cannot share a testhost with
the default gate — `Avalonia.Headless` owns a process-global dispatcher — so it
needs its own `dotnet test` invocation, which meant it had no home in the commit
protocol and nobody ran it. Failures have reached shipped releases that way —
`DotCharter.Tests/AGENTS.md` keeps the list.

What you see when you run it:

- **Green**: one `==>` step, a one-line case count, and
  `headless UI suite green (N passed, M skipped)`. Costs ~8–13 min before the
  publishes start.
- **Red**: the test NAMES, the first lines of each message, and the first stack
  frame in repo source — not the tens of thousands of lines of xunit output,
  which go to `artifacts/release/v<ver>/tests/headless-ui-run1.log` alongside
  the `.trx`. The script then throws and no asset is built.
- **A case that dies inside the Avalonia session itself** never reached its own
  assertions, so its class is re-run in a fresh process. Only REAL failures
  block, in either run; a case that races again is printed as
  `UNVERIFIED by this release` rather than silently counted green. A single case
  cannot be re-run alone: the session is built per run, so the retry is
  per CLASS. **The known cause of those deaths is fixed**
  (`HeadlessSessionFixture.Dispatch` drains the app's own background work before
  Avalonia's hard-coded 5 s teardown drain sees it — see
  `docs/agents/testing/perf-harnesses.md`), so this path should now fire rarely
  or never; it is kept because a NEW long-running background chain would
  reintroduce the same shape.
- **A run that executes FEWER cases than the filter discovers is now LOUD, and
  it blocks.** This was the quietest way the suite lied: a class killed before
  it reported anything is neither a failure nor a skip, so a run of 223 of 330
  cases printed "0 failed" and read as green. `Test-HeadlessCoverage` lists the
  classes `dotnet test --list-tests` finds, subtracts the classes that actually
  reported a result, re-runs whatever is missing in its own process, and — if a
  class STILL reports nothing — fails the gate, because nothing in it was
  graded. Proved in both directions by `-SelfTest`.

`-SkipHeadlessUi` exists for iterating on the asset build. It prints a WARNING
naming what is now unchecked. Do not use it to cut a real release.

## The assets

| Asset | Built by |
|---|---|
| `DotCharter-v<ver>-win-x64.zip` | `dotnet publish DotCharter.UI -c Release -r win-x64 --self-contained true` **into `app/`**, the NativeAOT launcher as the top-level `DotCharter.exe`, `voices/` and the bundled `tools/` at the top (see "The install layout" below), zipped folder-wrapped |
| `DotCharter-RigLab-v<rigver>.zip` | The standalone rig: `Publish-RigLab.ps1` (its own `<Version>`, the publishable TONE3000 key from `-Tone3000ClientId`), zipped folder-wrapped. **Never name it `*win-x64*` / `*x64*`**: every DotCharter up to v0.56 (the last release before the rig shipped) installs the first zip containing `win-x64`, and `GitHubReleaseRules.SelectWinX64Asset` now also excludes `RigLab` by name (`GitHubUpdateServiceTests.SelectWinX64Asset_NeverPicksTheStandaloneRig`). `-SkipRigLab` leaves it out |
| `payload-manifest.json` | `BuildPayloadManifest.cs manifest` — a SHA-256 per payload file, paths relative to the install's top. **Ships both INSIDE the zip (at `app/payload-manifest.json`) and as its own asset.** Load-bearing: it is what lets the in-app updater range-fetch only the changed members out of the full zip |
| `SHA256SUMS.txt` | `Get-FileHash` over the three above, in `sha256sum` format, plus one `# release: v<ver>` line binding it to the tag. **Also load-bearing, not a convenience:** `GitHubUpdateService` fetches it by release TAG and refuses a downloaded asset whose digest disagrees — the zip is robocopied over the live install, so these digests are the only thing that says the bytes are the build they claim to be |
| `SHA256SUMS.txt.sig` | `SignReleaseManifest.cs sign` — the Ed25519 signature over the exact bytes of `SHA256SUMS.txt`, made with the release-signing key. **Required:** from v1.1.0 on the app refuses a release whose signature is missing, malformed, made with another key, or over a manifest naming another tag (`ReleaseSignature`, `ReleaseSigningTests`) |

> Every failure is a refusal: an unreachable `SHA256SUMS.txt`, a missing or bad
> signature, a manifest that does not list the asset, a digest that disagrees.
> `FullAssetChecksumTests` and `ReleaseSigningTests` prove the checks run.

## Release signing (#109)

`SHA256SUMS.txt` comes from the same release page as the zip, so on its own it
proves nothing about WHO published the release: a stolen GitHub token could
publish a matching pair. The signature is what the release channel cannot forge.

- **The private key lives outside the repository**, on the release machine:
  `..\DotCharter-local\release-signing\dotcharter-release-ed25519.key` beside
  the checkout (override with `-SigningKeyPath` or `DOTCHARTER_RELEASE_SIGNING_KEY`).
  Base64 of the 32-byte Ed25519 seed; the folder's ACL is user-only. Never commit
  it, print it or put it in a log. `SignReleaseManifest.cs` never echoes it.
- **Back it up offline.** If it is lost, no install from v1.1.0 on can take
  another update; a new key could only reach them in a release signed by the old
  one. If it leaks, rotate the same way, at once.
- **The public half is pinned** in
  `DotCharter.Services/Update/ReleaseSignature.cs` (`PinnedPublicKeyBase64`).
  Preflight compares the key's public half with it and stops on a mismatch.
- **Signing** is the step after `SHA256SUMS.txt`: `SignReleaseManifest.cs sign`
  refuses a key that is not the pinned one, signs, verifies the result with the
  app's own `ReleaseSignature.Check` and writes `SHA256SUMS.txt.sig`.
- **A new key** (only ever for a rotation): `dotnet run tools/release/SignReleaseManifest.cs keygen <keyFile>`
  creates it with `CreateNew` (never overwrites) and prints only the public half.
- v1.0.0 does not verify signatures, so 1.0.0 → 1.1.0 is unaffected; every
  release from v1.1.0 on MUST be signed.

## The install layout

The zip's top folder holds only what a user looks for:

```
DotCharter-v<ver>-win-x64/
├── DotCharter.exe     the launcher (DotCharter.Launcher, NativeAOT, ~1 MB) - starts app\DotCharter.exe
├── README.txt
├── app/               the published app, ~390 files, and app/payload-manifest.json
├── voices/            the voice sample packs (moved up from the publish)
├── tools/             ffmpeg, yt-dlp, their notices
└── DotCharter.runtimeconfig.json, DotCharter.deps.json, hostfxr.dll, coreclr.dll
```

The last four are `LegacyUpdaterShim`: byte copies of the app's own files, there
only because the updater inside every v0.62-and-older install validates the
payload root with the flat rule. Without them no such install can update to this
release. The app deletes them on its first start (`SplitInstallCleanup`), and the
current updater never copies them. `Test-InstallLayout` refuses any other entry at
the top; `ValidateReleaseZip.cs` validates `app/` with the current rule AND the top
with the old one; `-SelfTest` builds a split payload both ways. `Publish-Launcher`
publishes the launcher NativeAOT with the app's `-p:Version` (the old updater
reads the target version off the top exe) and puts `vswhere.exe` on `PATH`, which
the ILCompiler needs to find the MSVC linker. The launch smoke starts the
TOP-level exe. Full detail:
[../../docs/agents/services/infrastructure-update/split-install.md](../../docs/agents/services/infrastructure-update/split-install.md).

## Bundled media tools (`tools/` folder in the UI zip)

The UI payload ships `ffmpeg.exe` and `yt-dlp.exe` in a `tools/` folder at the
install's top, beside the launcher `DotCharter.exe`, plus ffmpeg's licence (`ffmpeg-LICENSE.txt`), its build
record (`ffmpeg-BUILDINFO.txt`) and a `THIRD-PARTY-NOTICES.txt`. This makes stem
**Opus** encoding (`FfmpegStemEncoder`), audio import and **YouTube import**
(`YtDlpAudioDownloader`) work out of the box — the app resolves each tool
**configured path → exe dir → exe dir/`tools` → dev packer cache** (`MediaToolResolver`);
PATH is consulted only with `DOTCHARTER_TOOLS_FROM_PATH=1`.

How the script does it (`Ensure-RedistTools` + `Add-BundledTools`):

- Binaries are cached in the **git-ignored** repo folder `tools/redist/win-x64/`
  and reused **only while each still passes when RUN**: `yt-dlp.exe` must first
  hash to its pin and then `yt-dlp --version` must report the pinned version,
  and `ffmpeg -version` must report the inventory's
  `<ffmpegVersion>-<extraVersion>` AND the exe must list every inventory
  component (below). Otherwise the next pack re-fetches / rebuilds by itself.
- **yt-dlp** — pinned in `tools/release/yt-dlp.json`: the dated release tag
  (e.g. `2026.07.04`), the asset URL and the exe's SHA-256 (the release's own
  `SHA2-256SUMS` line). It is fetched through `Save-PinnedFile`, so a cached or
  downloaded exe that does not hash to the pin is deleted and NEVER run;
  `Add-BundledTools` checks the hash again before copying it into the payload.
  To move the pin, change all three together after checking the new hash
  against `SHA2-256SUMS`. Single ~18 MB exe, Unlicense/public-domain, from
  `github.com/yt-dlp/yt-dlp/releases`. `ReleasePackChecksLintTests` fails a
  download in `New-Release.ps1` that bypasses a pin.
- **ffmpeg — our minimal LGPL static build, ~6 MB instead of BtbN's
  157 MB GPL build.** `tools/release/ffmpeg-min/components.json` is the
  inventory: every decoder, encoder, muxer, demuxer, parser, filter, protocol
  and bitstream filter the app's argument vectors (and yt-dlp's) need, the
  external libraries (libvorbis, libopus, libmp3lame, zlib), and one `callSites`
  entry per product file that runs ffmpeg, with its job. Three things read it:
  - `Build-MinimalFfmpeg.ps1` builds ffmpeg with exactly those components
    (`--disable-everything --disable-autodetect`, no `--enable-gpl`), from
    tarballs pinned by URL + SHA-256 (ffmpeg, libogg, libvorbis, opus, lame,
    zlib), with a pinned portable MSYS2 (`msys2-base-x86_64-<date>.sfx.exe` +
    SHA-256) unpacked under `%LOCALAPPDATA%\DotCharter-build\ffmpeg-min` —
    nothing is installed system-wide. Its toolchain is pinned the same way:
    `ffmpeg-min/msys2-packages.json` names every package file (gcc, make,
    diffutils, nasm, pkgconf and their whole dependency closure) with its
    SHA-256; each is fetched through `Save-PinnedFile` and installed with
    `pacman -U` from the verified file. pacman never syncs (`-S`), and the build
    stops unless `pacman -Q` reports exactly the pinned versions and no
    unpinned MinGW package (`Compare-MsysPinnedPackages`). To re-pin, install the
    toolchain into a fresh MSYS2 base and copy every file from its
    `/var/cache/pacman/pkg` with its hash, all at once. The versions are written
    into `ffmpeg-BUILDINFO.txt` with the exact configure line. The first build takes ~10 minutes at `-Jobs 4` and
    runs at low priority; `New-Release.ps1` runs it in preflight when the cache
    is stale.
  - **Mirrors:** each pin is a list of URLs tried in order under ONE SHA-256
    (`PinnedDownload.psm1`'s `Save-PinnedFile`): LAME is Debian's
    `lame_3.100.orig.tar.gz` first, then SourceForge (which once served an HTML
    page instead); libogg/libvorbis/opus are xiph.org then GitHub; zlib
    is GitHub then zlib.net/fossils; MSYS2 is GitHub then repo.msys2.org;
    ffmpeg has ffmpeg.org only. The log names the mirror that served each file
    (`<file> <- <url>`), a mirror serving other bytes is skipped with a
    warning, and bytes are moved into `sources/` only after they hash to the
    pin. Add a mirror only after it served exactly the pinned bytes; never
    change a hash to make a mirror pass.
    `Infrastructure/PinnedDownloadMirrorTests` runs the loop with a fake
    downloader.
  - `New-Release.ps1`'s preflight runs `FfmpegInventory.psm1`'s
    `Test-FfmpegInventory`: it parses `ffmpeg -decoders/-encoders/-muxers/
    -demuxers/-filters/-protocols/-bsfs` and `-buildconf` (parsers, external
    libraries) and FAILS the pack on any missing component. `-SelfTest` proves
    the parser both ways on canned output.
  - `DotCharter.Tests/Infrastructure/FfmpegComponentInventoryTests` (default
    gate) pins the inventory against the code: every product file that runs
    ffmpeg must be a call site, each literal `-c:a`/`-f` must be declared and in
    the inventory, the silence filter graph, every `StemCodec`, yt-dlp's
    `--audio-format` and every `AudioFileTypes` extension must be covered.
  - **To use a new codec/format/filter:** add it to `components.json` in the same
    change; the next pack rebuilds. **To move ffmpeg:** change `ffmpegVersion`
    (and the tarball's SHA-256 in `Build-MinimalFfmpeg.ps1`), bump
    `extraVersion`, and re-run every job on a real file.
  - Licence: LGPL v2.1+ (ffmpeg, LAME), BSD (libvorbis, libogg, Opus), zlib.
    `THIRD-PARTY-NOTICES.txt` names the exact build and points the source offer
    at the unmodified upstream tarballs + `ffmpeg-BUILDINFO.txt`.
- **A tool that cannot be acquired FAILS the release in preflight**, before the
  headless gate or any publish. `-AllowMissingTools` packs anyway (warning);
  `-SkipTools` skips bundling entirely (fast test pack). The final summary
  reports the outcome (`Bundled tools: ffmpeg yes (8.1.3-dotcharter-min1) …`).
- **No ffprobe** is built or shipped. `AudioMetadataExtractor` reads tags with
  `ffmpeg -f ffmetadata` and the cover with ffmpeg alone when ffprobe does not
  resolve, and yt-dlp probes through ffmpeg.
- `ValidateReleaseZip.cs` additionally checks the zip contains
  `tools/ffmpeg.exe` / `tools/yt-dlp.exe`, emitting a **WARNING** (not a hard
  failure) if either is absent — consistent with `-SkipTools`.

## tonelab (`tools/tonelab/` in the UI zip)

- tonelab's source is not in this repository. `tonelab/tonelab.json` pins the
  build: its commit and the SHA-256 of `tonelab.dll`, `tonelab-launch.exe` and
  `tonelab.ini`. Point `-TonelabDir` (or `DOTCHARTER_TONELAB_DIR`) at tonelab's
  build output; `Add-Tonelab` checks the hashes and copies the three files and
  the MIT `tonelab/LICENSE.txt` into the install's `tools/tonelab/` BEFORE the
  payload manifest is hashed, so the in-app updater carries them like any
  other file.
- A missing folder or a hash that differs FAILS the release in preflight; a new
  tonelab build is a deliberate edit of `tonelab.json`. `-SkipTonelab` packs
  without `tools/tonelab/` (printed). The app registers its tonelab launcher
  only when both binaries are there
  ([tonelab-bridge.md](../../docs/agents/services/tonelab-bridge.md) § Lifecycle).

## The voice sample packs (`voices/` in the UI zip)

The chart synth's built-in guitar and bass play recordings from seven packs
(~543 MB together, measured 2026-10-04, with `voices/VOICE-CREDITS.txt`):
`guitar.dcvp` + `bass.dcvp` (CC0 Karoryfer), one Stratocaster pack per pickup
position, `strat-{neck,neck-middle,middle,middle-bridge,bridge}.dcvp` (EGFxSet,
CC BY 4.0 — its attribution is in `VOICE-CREDITS.txt`) and `guitar-arts.dcvp`
(Guitar-TECHS, CC BY 4.0). **Only recordings that may ship with the GPL
program are in them**: the builder leaves out every manifest source marked
`"private": true` (the Rickenbacker 4001 slaps, CC BY-NC-SA 3.0 modified), and
`Assert-NoSeparatelyLicensedVoice` refuses a `voices/` whose `bass.dcvp` has a
recorded-slap zone or that holds a `<pack>.LICENSE.txt` — both in preflight and in
`Test-PublishLayout`. Those recordings ship in no release (the builder's
`--separately-licensed` mode is no longer used here). The list is
`$VoicePackFiles` in `New-Release.ps1`. They are not in git: the preflight's
`Initialize-VoicePacks` runs
`JuceSharp/src/JuceSharp.Native/tools/voice/build_voice_pack.py build --out artifacts/voice-pack`
when they are missing (Python 3.9+ with numpy, network on the first run; every
source is pinned by commit and SHA-256 in its manifest), `DotCharter.UI.csproj`
publishes them into `voices/`, `Test-PublishLayout` refuses a layout without
them, and `New-SplitInstallLayout` moves `voices/` up to the install's top (the app
points the native store there through `DOTCHARTER_VOICE_DIR`). They are ordinary payload members: hashed into `payload-manifest.json`, so
an update re-downloads a pack only when its bytes changed.

## Small updates: partial downloads, not patch assets

A release is ~414 MB and most of it never changes. **There is no second asset.**
The updater fetches `payload-manifest.json`, works out which payload files this
install is missing, reads the release zip's central directory over HTTP
`Range`, and pulls only those members — out of the SAME zip everyone else
downloads whole. Implementation: `DotCharter.Services/Update/`
(`ZipCentralDirectory`, `UpdateFetchPlanner`, `PartialUpdateDownloader`).

- **The manifest is the whole handshake now.** No asset NAME is parsed, so
  nothing here has to stay in step with a string in `GitHubUpdateService`. A
  release that ships without a manifest still installs; every install just
  downloads the full zip.
- **It works from ANY prior version**, which a patch asset cannot: a patch only
  helps installs inside a fixed window, and widening that window means
  publishing more assets on every release page forever.
- **The manifest must be written before the zip** so the payload carries its
  own copy. That is what lets an install describe itself to the NEXT update
  without re-hashing 414 MB; `BuildPayloadManifest.cs` excludes the manifest
  from itself, since writing it would change its own hash.
- **It must list the five `PayloadManifest.AlwaysInclude` files** —
  `DotCharter.exe`, the runtimeconfig, the deps json, and
  `hostfxr.dll`/`coreclr.dll`, which a managed-only release leaves unchanged but
  `AppPayload.Validate` demands. Those are forced into every fetch plan;
  omitting them would make the assembled payload get REFUSED on the user's
  machine, after the download. The tool hard-fails if the publish output lacks
  any of them.
- **Files the release DROPPED** are derived from the manifest diff and written
  into the assembled payload as `payload-deletions.txt`, because the apply step
  robocopies with **no purge**.
- Releases up to v0.42.4 published `DotCharter-v<new>-from-v<old>-win-x64.zip`
  patch assets and those release pages are permanent, so
  `GitHubReleaseRules.SelectWinX64Asset` still excludes `-from-v` names — a
  patch is called `…-win-x64.zip` too.

## Payload size: what is precompiled, what is trimmed

The UI publish is ReadyToRun (startup is ~3x slower without it), but not all of
it. `DotCharter.UI.csproj` holds both settings, and
`Infrastructure/PublishPayloadSettingsTests` pins them:

- **`PublishReadyToRunExclude`** ships assemblies that are NOT loaded before the
  `StartupPhases:` line as IL: the AI SDK (`Anthropic`,
  `Microsoft.Extensions.AI.Abstractions`), Guitar Pro (`AlphaTab`, `AlphaSkia*`),
  PSARC (`Newtonsoft.Json`), stem
  separation (`Microsoft.ML.OnnxRuntime`, `System.Numerics.Tensors`), GDI+
  (`System.Drawing.Common`, `System.Private.Windows.Core`) and the
  Linux/macOS/designer Avalonia backends. They JIT on first use. The test fails
  if a startup assembly (ours, Avalonia's core, Skia, Silk.NET.Vulkan, Dock, DI,
  Serilog, ...) joins the list. The list came from mapping the published app's
  loaded images after it went interactive; re-measure before adding to it.
- **Only `Silk.NET.Direct3D11` and `Silk.NET.DXGI` are trimmed** (30 + 3 MB
  precompiled down to ~70 KB): `DotCharterTrimOnlyBindings` marks every other
  assembly not trimmable, so the linker copies it unchanged. `PublishTrimmed` is
  set inside `DotCharterEnableBindingTrim`, never as a property: as a property
  it writes the linker's trimmed-app feature switches (System.Text.Json
  reflection off, among others) into `runtimeconfig.json` for the whole app.
  `Test-PublishTrimSettings` (called from `Test-PublishLayout`, covered by
  `-SelfTest`) refuses a payload whose `runtimeconfig.json` carries one of those
  switches, or whose `Silk.NET.Direct3D11.dll` is over 1 MB (the trim did not run).

Measured (publish without voice packs or tools): the two settings take the
payload from 464.7 MB to 409.7 MB (−55.0 MB) with startup like-for-like, `r2r=yes`
on both. Rejected: composite ReadyToRun (`PublishReadyToRunComposite`, +105 MB)
and trimming the framework (−30 MB more, but every reflection user of a BCL type
would need checking).

## Payload / zip invariants (why this script exists)

These are enforced by `DotCharter.Services/Update/AppPayload.cs` — the same code
the in-app updater runs on users' machines:

1. **Self-contained only.** The updater applies a payload by copying it over an
   install *without purging* (`robocopy /E`), so payload and install must share
   an `InstallLayout`. A framework-dependent payload merged onto a
   self-contained install strands the old `hostfxr.dll` next to a new
   `runtimeconfig.json` and the app can never start again. The script verifies
   `runtimeOptions.includedFrameworks` is present and that
   `DotCharter.exe` / `juce_sharp.dll` / `hostfxr.dll` / `coreclr.dll` all
   landed in the publish (in `app/`; a split payload is validated there).
2. **Forward-slash zip entries, one top-level folder.**
   `AppPayload.ResolveArchiveRoot` unwraps a single version-named top folder by
   matching entry names ending in `"/DotCharter.exe"` — **forward slashes
   only**. PowerShell's `Compress-Archive` can emit backslash-separated entries,
   which the updater rejects. The script zips via
   `[System.IO.Compression.ZipFile]::CreateFromDirectory(dir, zip, Optimal, $true)`
   (includeBaseDirectory = folder wrap) and then validates the result with the
   real rules via `ValidateReleaseZip.cs` (a .NET 10 file-based program that
   references `DotCharter.Services` directly, so it can never drift from the
   updater).
3. **`SelectWinX64Asset`** picks the update zip by name: it must be a `.zip`
   containing `win-x64`. Keep the `DotCharter-v<ver>-win-x64.zip` naming.

`-SelfTest` proves both directions against a synthetic payload in a temp dir:
the folder-wrapped `ZipFile` zip passes validation, and a deliberately
backslash-entried zip is rejected. It proves the binary check both ways (real
managed + native PE images pass; a zero-filled `DotCharter.dll` and a text-stub
`DotCharter.exe` fail, by name), the launch smoke's `StartupPhases:` reader, the
clean-build project closure, the trim check (`Test-PublishTrimSettings`, both
ways), the ffmpeg notice text and the ffmpeg inventory check. It runs a probe
through `Start-HiddenDesktopProcess` that opens and ACTIVATES a window: it must
report the hidden desktop's name, never own the foreground, and leave the
desktop released (the probe opens its window only on a desktop the helper named,
so a broken helper fails without showing anything). It also proves
`Test-HeadlessCoverage` both
ways against a hand-written `.trx` — a discovered class that reported nothing is
flagged, and a complete run is not — because a coverage check that never fires
is worth nothing and one that always fires blocks every release.

## NuGet lock files

Every `DotCharter.*` project commits a `packages.lock.json` (version + content
hash of every package, transitive ones included; `Directory.Build.props` sets
`RestorePackagesWithLockFile` and `RuntimeIdentifiers=win-x64`, so a restore with
or without `-r win-x64` reads the same file). CI restores with `--locked-mode`, and
the release's public UI publish and launcher publish pass
`-p:RestoreLockedMode=true` (`$LockedRestoreArgs`), so a graph that differs from
the committed one fails the run instead of shipping. A checkout with a plugin in
`plugins/` restores a different graph; its lock goes to `obj/packages.plugins.lock.json`
and never rewrites the committed file, which is why `-WithPlugins` is not locked.
After a package change, refresh the committed files with:

```powershell
dotnet restore DotCharter.sln -p:DotCharterPlugins=false --force-evaluate
```

`CiWorkflowLintTests` fails a CI restore without `--locked-mode` and a project
without a committed lock file; `ReleasePackChecksLintTests` fails a release
publish that is not locked.

## The prebuilt native runtime (`Publish-NativeRuntime.ps1`)

Not part of an app release: it publishes the `juce_sharp.dll` +
`juce_vst_host.exe` pair for one JuceSharp commit, which DotCharter's build
downloads when the DLL is missing (`tools/setup/Get-NativeRuntime.ps1`).

```powershell
pwsh -NoProfile -File tools/release/Publish-NativeRuntime.ps1 -NativeDir <folder with the pair>          # zip + pin + verify, prints the gh commands
pwsh -NoProfile -File tools/release/Publish-NativeRuntime.ps1 -NativeDir <folder with the pair> -Upload  # ...and uploads
```

It refuses a pair whose DLL does not report the header's `DCA_ABI_VERSION`,
writes `artifacts/native-runtime/juce-sharp-native-win-x64-<sha12>.zip`, pins
both files' SHA-256 in `tools/setup/native-runtime.json` (never repins a
commit), installs the zip through the build's own code into a temp folder, and
with `-Upload` creates the `native-prebuilt` release on `SomeeName/JuceSharp` if
needed and uploads the zip (never `--clobber`). Run it for every gitlink bump
and commit the pin with it. A `PENDING` pin (written by the public export, below)
is replaced, not compared.

The fetch does not trust the manifest to say where to download from: only
`SomeeName/JuceSharp` and `SomeeName/JuceSharp` are accepted and the release
must be a plain tag (`NativeRuntime.psm1`'s `$AllowedRepositories`). Both
hashes are checked BEFORE anything loads the DLL, and the `dca_abi_version()`
probe runs in a child PowerShell started without `GH_TOKEN`/`GITHUB_TOKEN`/
`SUBMODULES_TOKEN`. In CI only the private repo hands the fetch the PAT; the
public repo uses the run's own token (`NativeRuntimeFetchTests`,
`CiWorkflowLintTests`).

## The public export (`Export-PublicSnapshot.ps1`)

Work happens in the `-Dev` repos; the public DotCharter and JuceSharp repos get ONE
commit per release, each on top of the previous public one (a root commit the first
time). No dev commit id, trailer or private name reaches them.

```powershell
pwsh -NoProfile -File tools/release/Export-PublicSnapshot.ps1                       # dry run of origin/main
pwsh -NoProfile -File tools/release/Export-PublicSnapshot.ps1 -NativeDir <pair> -Build  # real pin + public build of the snapshot
pwsh -NoProfile -File tools/release/Export-PublicSnapshot.ps1 -PublicDotCharterUrl https://github.com/SomeeName/DotCharter.git `
    -PublicJuceSharpUrl https://github.com/SomeeName/JuceSharp.git -NativeDir <pair> -Gate -Push   # publish
pwsh -NoProfile -File tools/release/Test-PublicLeaks.ps1 -Path <folder>              # the leak check alone
```

- **JuceSharp:** the tree of the dev gitlink (version from `Directory.Build.props`).
- **DotCharter:** the tree of `-DevRef` minus `Get-PublicExcludedPaths` (every entry is
  git-ignored today; each says why it never ships), with the rename map applied to every
  text file (the owner-qualified and bare `-Dev` names become the public names), the
  `JuceSharp` gitlink set to the PUBLIC JuceSharp commit, and a `native-runtime.json` pin for
  it: the `-NativeDir` pair's hashes; else the dev pin's, when the public JuceSharp tree id
  equals the dev gitlink's (the rename map changed nothing there, so the pair is the same);
  else `PENDING`, which fails the snapshot's own gate until the pair is pinned (version from
  `DotCharter.UI.csproj`).
- **Message** (`Get-SnapshotMessage`, after SomeeName/rs7string's public history): a root
  commit (no public parent yet) is `Initial public commit of <DotCharter|JuceSharp>`, every
  later snapshot `Update to v<version>`. Both snapshots get an annotated tag `v<version>`
  whose message is the commit's, tagged by the owner; a JuceSharp snapshot whose tree did not
  change reuses the public commit AND its message, so its tag is the one already public.
- **Identity:** author and committer are the dev checkout's `user.name`/`user.email`, dated
  with the dev commit's date (`-Push` refuses unless that is the snapshot identity in
  `tools/release/public-snapshot-identity.json`), so a re-run with the same inputs gives the same ids (the
  uploaded native asset name depends on the JuceSharp id). An unchanged tree reuses the
  previous public commit instead of stacking an empty one.
- **Leak check** (`Find-PublicTreeLeaks`, every finding as file:line, grouped by rule): the
  `-Dev` and archive repo names, the add-on's name and add-on folder paths, links to a
  dev-repo pull request, issue or commit (`history-link`), Windows user-profile paths, and every `PersonalDataLintTests` pattern. Nothing is skipped by
  content: a file with a NUL is read as Latin-1 (so a name inside a binary or a source
  file with a literal NUL is still found), and every gzip file (the `.gz` fixtures) is
  read as stored AND decompressed in memory. No allow-list for the
  add-on; the one exemption is the user-folder names the tree uses as made-up examples
  (`Get-UserPathPlaceholders`, matched whole), so any other name under the profile folder fails. Key material
  is gitleaks' job; the script runs it on the snapshot when it is installed (`-Push` refuses
  without it). The dry run exits 1 while there is a finding and writes `leak-report.txt`
  beside the scratch repos.
- **Screenshots are a MANUAL check.** The leak check reads bytes, not pixels: a PNG/JPG
  that SHOWS a private path, a real name, a dev repo name or a dev issue number (a title
  bar, a file dialog, a log pane) passes it. Before every `-Push`, list the images the
  snapshot adds or changes and look at each one:
  `git -C <scratch>\DotCharter diff --name-only --diff-filter=AM refs/remotes/public/main main -- '*.png' '*.jpg' '*.jpeg' '*.gif' '*.webp'`
  (no public main yet: `git -C <scratch>\DotCharter ls-files -- '*.png' '*.jpg' …`).
- **Push preflight** (`Get-PublicPushPlan`, dry run included whenever the URLs are given):
  `git ls-remote` of BOTH public repos before either is pushed. Public `main` must still be
  the snapshot's parent, and the tag `v<version>` absent or already naming the snapshot
  commit (an unchanged JuceSharp reuses its public commit and tag and pushes nothing). A
  tag for another commit means the version was not bumped: the export refuses and exits 1.
- **Scratch:** `%TEMP%\dotcharter-public-export\{JuceSharp.git,DotCharter}`, recreated per run.
  Both read the dev objects through `objects/info/alternates`: the dev checkout's working
  tree, index, branches and refs are never written. `-PublicDotCharterUrl` /
  `-PublicJuceSharpUrl` name the previous snapshots (fetched only).
- **`-Build` / `-Gate`** copy the native pair into the scratch tree and run
  `tools/agent/Invoke-Gate.ps1 -Public -Build` (or `-Gate`) there.
- **`-Push`** pushes each repo with `git push --atomic` (JuceSharp first: DotCharter's
  gitlink names it), after re-reading both preflights. It refuses, pushing nothing, without
  BOTH URLs; without `-Gate` in the same run; on any leak finding; when gitleaks is missing
  or reports findings; while the native pin is `PENDING`; when a commit or tag it would push
  has an author, committer or tagger email other than the snapshot identity; and on any
  preflight problem. Everything but the gate is checked before the gate runs.
  **Gate evidence is the run itself:** `-Push` requires `-Gate`, which runs
  `Invoke-Gate.ps1 -Public -Gate` in the very scratch tree being pushed, a moment before
  the push. No recorded result is accepted, so there is no evidence file to go stale or
  to match against a tree id. It never force-pushes, and creates no repository and no
  release. The native publish command is printed, not run.

`Infrastructure/PublicSnapshotExportTests` runs the module on fixture repos, including
every `-Push` refusal against fixture public repos (gitleaks and the snapshot's gate are
stubs there).

### Public commits come back to dev first (`PublicSync.psm1`)

A pull request merged on a PUBLIC repo exists nowhere in dev, and the next snapshot is the
dev tree, so it would overwrite that change. The sync guard stops that:

- **Marker:** every public commit after the last snapshot is imported as a dev commit with
  the trailer `Public-Commit: <DotCharter|JuceSharp>@<full sha>` (and `Co-authored-by:`
  for the contributor). A snapshot commit is one whose message is `Get-SnapshotMessage`'s
  AND whose author = committer = the snapshot identity (`Get-PublicSnapshotIdentity`,
  `tools/release/public-snapshot-identity.json`) or the author of the public root commit;
  a contributor's commit titled `Update to v…` is not one. That ONE definition
  (`Get-SnapshotIdentities`) is what the export, `Find-UnimportedPublicCommits.ps1` and
  `Import-PublicCommit.ps1` (which refuses to import a snapshot) all use.
  Merge commits are not listed; their non-merge commits are.
- **Hard stop:** with `-PublicDotCharterUrl` / `-PublicJuceSharpUrl` (so always before
  `-Push`), `Export-PublicSnapshot.ps1` lists the public `main` commits after the last
  snapshot and REFUSES, dry run included, when one has no trailer in the history of
  `-DevRef` (JuceSharp: also of the dev gitlink). It names each one with its import command.
- **Import:** `pwsh -NoProfile -File tools/release/Import-PublicCommit.ps1 -Repo DotCharter -Commit <sha>`
  (or `-Pr <n>` for a merged public pull request; `-Issue <n>` adds `Fixes #n`). It
  fetches public `main`, turns each changed text file back into dev form with the reverse
  rename map (`Convert-PublicTextToDev`: a public line that is the renamed form of a line
  in the dev file becomes that line), applies the diff with `git apply -3 --index` on a new
  branch from `origin/main`, commits once with the trailers, pushes and opens the dev PR.
  For JuceSharp it commits in the submodule and also bumps the gitlink in the superproject
  with the same trailers (pin the native pair before merging, as for any bump). A conflict
  stops it with the trailer lines to commit with. `-NoPullRequest` stops after the commit.
- **Skip on purpose:** an empty dev commit whose last paragraph is the trailer, saying why.
- **Release preflight:** `New-Release.ps1` runs `Find-UnimportedPublicCommits.ps1 -DevRef HEAD
  -FailOnMissing` and stops while a public commit is missing (`-SkipPublicSyncCheck` when
  offline). Locally the check also reads the dev JuceSharp history at the gitlink, as the
  export does.
- **Watch:** `.github/workflows/public-sync-watch.yml` would run `Find-UnimportedPublicCommits.ps1
  -OpenIssues` daily on ubuntu-latest and open one dev issue per missing commit
  (deduplicated by its `Import public commit <Repo>@<sha12>` title prefix), but **Actions is
  disabled on the dev repos (billing), so it does not run.** The release preflight and the
  export's hard stop are what actually catch a missing import. On the runner it reads only
  the superproject's history, which is why a JuceSharp import bumps the gitlink.
- **Optional local schedule** (no script registers it; run this once by hand if wanted).
  `-OpenIssues` opens the dev issues through `gh` (logged in, run from the dev checkout so
  `gh` picks the dev repo); it compares against the local `origin/main`, so fetch first or
  point `-DevRef` at a ref that is kept current:
  `schtasks /Create /SC DAILY /ST 09:00 /TN DotCharterPublicSync /TR "pwsh -NoProfile -File <dev checkout>/tools/release/Find-UnimportedPublicCommits.ps1 -DevRef origin/main -OpenIssues"`

`Infrastructure/PublicSyncGuardTests` runs the export refusal, the watch's check and the
import on fixture repos; `PublicSyncWatchWorkflowLintTests` pins the watch's shape.

## Version-bump checklist (before running the script)

- `DotCharter.UI/DotCharter.UI.csproj` — `<Version>`, `<AssemblyVersion>`, `<FileVersion>`

## After publishing

- Check asset sizes on the release page byte-match the local builds and the tag
  points at the intended sha (release notes stay editable via `gh release edit`).
- Run the live updater test against the real repo:

```powershell
$env:DOTCHARTER_LIVE_UPDATE_TEST = '1'
$env:DOTCHARTER_LIVE_UPDATE_TOKEN = (gh auth token)
dotnet test DotCharter.Tests/DotCharter.Tests.csproj --filter "FullyQualifiedName~LiveCheckAgainstRealRepo"
```

## Order-dependent headless failures

A headless case that FAILS its assertions in the full run gets the repo's own
protocol (`DotCharter.Tests/AGENTS.md`: only an isolated re-run of the named
case decides it): its class is re-run once in a fresh process. Fails again →
blocks. Passes → printed as **ORDER-DEPENDENT**, dropped from the blocking set,
and it is somebody's job to find the earlier class that leaves the state
behind.

## SemVer releases and betas

`DotCharter.UI/DotCharter.UI.csproj` is the version source. For example,
`-Version 1.0.0-beta.1` preserves the suffix in the UI and launcher publish,
`DotCharter-v1.0.0-beta.1-win-x64.zip`, its wrapped folder, the source tag link in
`README.txt`, `payload-manifest.json` and the asset names in `SHA256SUMS.txt`.
`ReleaseVersion.psm1` validates the version and supplies the release identity;
`-SelfTest` includes its beta fixture. `Infrastructure/ReleaseVersionTests` exercises
that fixture, the generated README and checksums without publishing or starting the app.

The printed `gh release create` command uses `--latest` and omits `--prerelease`.
Beta tags must be normal releases marked Latest: the updater intentionally reads
`/releases/latest`, which excludes GitHub prereleases. The updater compares SemVer
precedence, so beta identifiers still sort below the stable release.

### Cached ffmpeg toolchain and process failures

`Build-MinimalFfmpeg.ps1` reuses a complete cached MSYS2 compiler toolchain instead
of refreshing its package database on every build. Missing tools trigger package
installation and a check that all required executables were installed. The source
tarballs remain pinned and hash-checked, and the resulting binary still has to pass
the component inventory and build-path checks. Compiler versions are recorded in
`ffmpeg-BUILDINFO.txt`.

Build children run without a console at low priority, inherit the caller's CPU
mask (or use the explicit mask), and have stdout/stderr drained concurrently. A
nonzero child exit code stops the build; the wrapper reports the actual child's
status. `Infrastructure/FfmpegBuildProcessTests` exercises failure propagation,
output capture and the cached-toolchain predicate without network access.

### Project cleanup follows the build mode

The public build's clean-output walk excludes references beneath `plugins/`,
whether that optional folder is absent or present. Plugin builds include those
references (DotCharter.UI's wildcard reference expands to what is checked out). Missing required projects still fail before
cleanup, so a broken checkout cannot produce an incomplete release. `ReleaseProjectClosureTests`
executes the walker against both layouts and verifies that the release mode
reaches the cleanup step.
