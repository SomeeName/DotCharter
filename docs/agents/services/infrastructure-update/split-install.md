# `Infrastructure/` + `Update/` — the split install: a launcher on top, the app in `app/` (#413)

## The layout

Since v0.63 a release unpacks to:

```
DotCharter-v<ver>-win-x64/
├── DotCharter.exe            the launcher (DotCharter.Launcher, NativeAOT, ~1 MB)
├── README.txt
├── app/                      the app and its ~390 files; app/DotCharter.exe is the real app
│   └── payload-manifest.json this release's manifest (paths relative to the TOP)
├── voices/                   the voice sample packs
├── tools/                    ffmpeg, yt-dlp and their notices
└── DotCharter.runtimeconfig.json, DotCharter.deps.json, hostfxr.dll, coreclr.dll
                              LegacyUpdaterShim: for pre-0.63 updaters only, deleted on first start
```

A portable install's own state (`appconfig.json`, `keybinds.json`, profiles,
logs, the library index, checkpoints, credential stores, downloaded `models/`)
lives at the TOP, beside the launcher — exactly where a v0.62 flat install kept
it. Nothing the app writes ever lands in `app/`.

## Where each path comes from

- **`Infrastructure/Storage/InstallLocation`** (in `DotCharter.Tone`) is the one
  answer. `InstallRoot` is the launcher's folder when the app runs from a folder
  named `app` with `DotCharter.exe` both inside it and above it
  (`ResolveInstallRoot`, pure); everywhere else (a dev build, the standalone rig,
  any pre-0.63 release) it is `AppContext.BaseDirectory`, so a flat folder behaves
  exactly as before.
- From `InstallRoot`: `WritablePaths` (the portable marker and the portable
  config/log roots — both ignored under a `DOTCHARTER_DATA_ROOT` override, see
  [storage.md](storage.md)), `MediaToolResolver` (it also probes `<root>/tools` itself,
  so every caller that passes `AppContext.BaseDirectory` finds the bundled tools),
  `AudioFormatConverter`, `ComponentManager`, `LyricModelCatalog.ModelRoots`,
  `JuceStemSeparationService`'s legacy model probe, and `GitHubUpdateService`'s
  install dir. `InstallLocationTests.InstallScopedPaths_AreNotResolvedFromTheExeFolder`
  lints the four that would lose user data or update the wrong folder.
- From `AppContext.BaseDirectory` (they ship inside `app/`): `NamCaptures/`, the
  tone designer's bundled DI, `IBundledDocuments`, the VST sandbox host,
  `juce_sharp.dll`.
- **Voices**: the native store reads `DOTCHARTER_VOICE_DIR` before looking beside
  `juce_sharp.dll` (`dcvoice_pack_store.h`, through `GetEnvironmentVariableW`).
  `Program.Main` calls `InstallLocation.ExportVoiceDirectory()` first thing, so a
  split install points it at `<root>/voices`; a value already set wins.

Every start of a split install logs ONE line saying what it found
(`SplitInstallCleanup.DescribeLayout`): `Split install at '<root>': voices
'<dir>' (8 pack(s)), ffmpeg '<path>', yt-dlp '<path>'` — the first thing to read
when a user reports a silent chart synth or a missing ffmpeg after the move.

## The launcher (`DotCharter.Launcher/`)

NativeAOT (`PublishAot`, size-optimised, no globalization or stack-trace data):
~1.0 MB, no runtime beside it, and it adds ~20 ms to a start (measured
2026-10-08 against a GUI child started directly; the app itself is interactive
~1.3 s later, `r2r=yes`). It starts `app\DotCharter.exe` with its own raw
command-line tail (`LaunchCommandLine.ArgumentTail`, unparsed — quoting reaches
the app byte for byte), its own `STARTUPINFO` (desktop, show state, redirected
handles, which are inherited only when redirected) and its working directory,
waits, and exits with the app's exit code. It never shows a window or a message
box: a missing `app\DotCharter.exe` or a failed `CreateProcess` appends one line
to `dotcharter-launcher.log` beside it (or `%TEMP%\DotCharter\`) and exits
`0x44430001` / `0x44430002`. It references nothing first-party; the tests compile
`LaunchCommandLine.cs` in by link. A `dotnet build` of the solution builds it as
an ordinary exe so the gate style-checks it; only `New-Release.ps1`'s
`Publish-Launcher` publishes it AOT (it puts `vswhere.exe` on `PATH` — the
ILCompiler finds the MSVC linker through it and a Build-Tools-only machine has
it off `PATH`).

The updater script waits for the APP's pid only. The launcher exits the moment
the app does; robocopy's own retries (`/R:5 /W:2`) cover the instant in between.

## Updating

**The current updater** treats the install's TOP as the install
(`GitHubUpdateService._installDir = InstallLocation.InstallRoot`, handed to
`UpdateInstaller`, which owns the apply):

- `AppPayload` is split-aware: `AppPrefix` (`"app/"` when `app/DotCharter.exe`
  exists) prefixes every requirement `Validate` checks, and a split payload also
  needs the launcher. `ClassifyDirectory`, `ValidateDirectory`, `ValidateArchive`
  and `TryReadVersion` read the app's files in `app/`.
- `AppPayload.CheckShape` refuses a FLAT payload over a SPLIT install (the old app
  would keep running behind a new launcher) and allows the reverse — that is the
  one-step migration.
- `PayloadManifest.AlwaysIncludeFor` gives a split manifest's six must-ship files
  (the launcher and the app's five in `app/`); `PayloadDelta.Between` uses it.
- `LegacyUpdaterShim.StripFrom` keeps the four shim files out of a partial fetch,
  and `LegacyUpdaterShim.RemoveFrom` deletes them from a staged split payload
  before the copy.
- The manifest an install carries is `app/payload-manifest.json`
  (`PayloadManifest.SplitInstalledPath`); `InstalledPayloadReader.FromDirectory`
  reads it before the top-level one.
- `PayloadReceiptFilter.IsLocalState` also refuses a local-state name under
  `app/` (`app/appconfig.json` is contraband just the same).

**A v0.62 install (flat) reaches the split layout in ONE update**, run by its own
old updater, which validates the payload ROOT with the flat rule. That is the
whole reason for `LegacyUpdaterShim`: with its four copies at the top the old
validator accepts the split payload, robocopies it over the flat install
(launcher over the old apphost; `app/`, `tools/`, `voices/` alongside), and
relaunches `DotCharter.exe` — now the launcher. `ValidateReleaseZip.cs`,
`BuildPayloadManifest.cs` and `-SelfTest` refuse a split release without them.
**Do not remove the shim while any pre-0.63 install is left to update.**

The old updater's full-download path deletes nothing, so the flat files stay at
the top. **`SplitInstallCleanup`** removes them on the app's first start from
`app/` (`App.StartSplitInstallCleanup`, a background task; every later start is a
few `File.Exists`, `NeedsCleanup`). Its plan (`Plan`, pure) deletes a top-level
file only when the OLD release's manifest (still at the top, because the split
payload keeps its own in `app/`) lists it, or `app/` has a file at the same
path, or it is a shim file — and never when it is
`PayloadReceiptFilter.IsLocalState`, the launcher, under `tools/` or `voices/`,
or listed at the top by the new manifest (the readme). A file nobody shipped
stays. If the old updater took its PARTIAL path instead, it wrote the NEW
manifest to the top; the plan reads that as current, and `Run` moves it into
`app/`. Pinned by `SplitInstallCleanupTests` (including a real-folder run with
every class of user state beside the binaries) and `SplitInstallLayoutTests`.
