# Contested features: the public-build switch

One MSBuild property takes the contested features out of a build, so a takedown
notice is answered with one release instead of a refactor under time pressure.

```powershell
# a normal build and release: everything on (the default)
dotnet build DotCharter.sln
tools\release\New-Release.ps1 -Version 0.70.0

# the public-build variant: the features below are absent
tools\release\New-Release.ps1 -Version 0.70.0 -ContestedFeatures:$false
dotnet build DotCharter.UI\DotCharter.UI.csproj -p:DotCharterContestedFeatures=false
```

## How it works

| Piece | Where |
|---|---|
| The property `DotCharterContestedFeatures` (default `true`) and the compile constant `CONTESTED_FEATURES` it defines for every `DotCharter.*` project | `Directory.Build.props` |
| `ContestedFeatures.Enabled`, that constant as a `const bool`, and `ContestedFeatures.All`, the registry this page mirrors | `DotCharter.Services/Infrastructure/Features/ContestedFeatures.cs` |
| DI: one module registers every contested service; off, it registers nothing | `DotCharter.Presentation/Dialogs/ContestedFeaturesModule.cs` (called by `DialogsModule.Register`) |
| Menu rows: marked `menus:ContestedMenu.Feature="<id>"` and REMOVED (not disabled) by `ContestedMenu.Prune` in the menu file's constructor | `DotCharter.UI/Views/Shell/Menus/ContestedMenu.cs` |
| Palette entries: dropped by `ContestedFeatures.FilterPalette` at the end of `CommandCatalog.Build` | `DotCharter.UI/ViewModels/CommandPalette/CommandCatalog.cs` |
| yt-dlp is not listed in the component inventory | `ComponentCatalog.ExternalTools` |
| The release: `-ContestedFeatures:$false` publishes with the property off and bundles no yt-dlp | `tools/release/New-Release.ps1` |

Off means **absent, not greyed**: nothing is registered, no row or palette entry
is built, and nothing contacts a YouTube host at run time.

**The code stays in the source.** Both builds compile every type (`MediaToolResolver`
in `Import/Youtube/` also finds ffmpeg for stems and exports, so that folder cannot
leave), and the host names remain as string literals. `PRIVACY.md` keeps listing the
hosts, so `PrivacyHostsLintTests` passes either way. Removing the SOURCE is a
separate commit that deletes the paths below; the switch only keeps the shipped
program from using them.

**Adding a contested feature:** add a `ContestedFeature` to `ContestedFeatures.All`,
register its services in `ContestedFeaturesModule`, mark its menu rows, route its
palette ids through `FilterPalette`, and add a row below. `ContestedFeaturesDocSyncTests`
fails until the list here and the code agree.

## What the switch covers

Each row: the feature id, then everything that goes when the switch is off.

| Feature id | Menu rows (`Menu_*` key) | Palette ids | Components | Source |
|---|---|---|---|---|
| `youtube-import` | `Menu_AttachYoutubeAudio` (File ▸ Import ▸ Audio from YouTube…) | `file.attachYoutubeAudio` | `tool-yt-dlp` | `DotCharter.Services/Import/Youtube/YtDlpAudioDownloader.cs`, `DotCharter.Services/Import/Youtube/YoutubeUrl.cs`, `DotCharter.Presentation/Screens/Dialogs/YoutubeAudioViewModel.cs`, `DotCharter.Presentation/Screens/Dialogs/YoutubeAudioLauncher.cs`, `DotCharter.UI/Views/Dialogs/YoutubeAudioDialogView.axaml` |

Registered services (all in `ContestedFeaturesModule`): `IYtDlpAudioDownloader`,
`YoutubeAudioDownload`, `YoutubeAudioLauncher`, `IYoutubeAudioScreen`. The Songsterr
import was deleted from the source (#820), not switched off.

Not covered, on purpose: Guitar Pro, EOF, Rocksmith XML and MIDI import; ffmpeg;
the other hosts in `PRIVACY.md`.

## Limits

- The default gate and the headless suite run the default (switch on) build. The
  off build is covered by tests that pass `false` through the seams
  (`ContestedFeaturesTests`), not by a second full test run.
- With the switch off a user with a YouTube link has nowhere to put it; there is no "disabled" message because the entry points are absent.

## Optional plugins and package features: what the build can READ (#887)

A plugin is a separate repository cloned into the git-ignored `plugins/<name>/`, with
its project file at the top of that folder; its assembly is named after that file.
The product names no plugin: it builds, declares, loads, guards and releases "the
plugins under `plugins/`". Reading game packages and the game install is not part of
this repository: a plugin can supply it through the seams below, and every entry
point that reads a game package or the game install asks `IPackageFeatures`
(`ReadsGamePackages`, `ReadsGameInstall`) instead of assuming. Off means **absent, not
greyed**, the same rule as above.
Writing packages (File ▸ Export Rocksmith, the companion `.psarc`) never asks.

| Piece | Where |
|---|---|
| The contract (`IPluginModule`), the plugin API version (`PluginApiVersion`), the declaration (`PluginAssemblyAttribute`) and the loader | `DotCharter.Services/Plugins/IPluginModule.cs`, `IPackageFeatures.cs`, `PluginLoader.cs` |
| Declaration: the build writes one `[assembly: PluginAssembly("<name>")]` per `plugins/*/*.csproj` on every project that sets `DotCharterDeclarePlugins` (DotCharter.UI, DotCharter.Tests, a plugin's tests). Discovery: `PluginLoader.Declared(typeof(App).Assembly)` lists them, `PluginLoader.FindModule` loads each assembly and takes its ONE public `IPluginModule` (none or several: refused). A public build declares nothing and registers nothing. A module whose `ApiVersion` differs from `PluginApiVersion.Current`, or that throws, is refused WHOLE (it registers into a scratch collection) and logged as a WARNING the status bar shows (`Plugin_Outdated` / `Plugin_Failed`); the app starts without it | `Directory.Build.targets` (`DotCharterDeclarePlugins`), `DotCharter.UI/App.Plugins.cs` (`AppPlugins.AddPlugins`, `AppPlugins.ReportPlugins`) |
| The default registration (`PackageFeatures.None`), the merge of every module's `IPackageFeatures` instance into the ONE the app resolves (a feature is on when any says so); `AppPlugins.Activate` hands the plugin's `IGameInstallReader` and `IPackageToneImporter` to their process holders (`GameInstall`, `PackageTones`) | `DotCharter.UI/App.Plugins.cs` |
| The game-install seam (#888): `IGameInstallReader` (install directories, the background table load) and `GameInstall`, the holder `AppPlugins.Activate` fills and the plugin's package source / `MainWindowPrewarm` read. An optional plugin implements it (the install locator, the table loader, the game-table builder) and registers `ReadsGameInstall`; without it the game tables stay empty and the converter maps generically. `DOTCHARTER_PACKAGE_READER=off` and a `packageReader: false` composition do not even look for the plugin. Its own tests run from its repository (through `Invoke-Gate.ps1 -Project <its test project> -Solution <same>`); the checked-in synthetic table snapshot the converter tests load is pinned to the builder there | `DotCharter.Services/Plugins/IGameInstallReader.cs`, `DotCharter.Tests/Audio/Dsp/RsToneConversion/SyntheticRsGame.cs` |
| The build: `DotCharter.UI.csproj` references every `plugins/*/*.csproj` (a wildcard: nothing when the folder is empty); `DotCharter.Services` and `DotCharter.Tone` open their internals to every project under `plugins/` (`InternalsVisibleTo` from the same wildcard); `-p:DotCharterPlugins=false` ignores the folder even when something is there | `Directory.Build.props`, `DotCharter.UI/DotCharter.UI.csproj`, `DotCharter.Services/DotCharter.Services.csproj`, `DotCharter.Tone/DotCharter.Tone.csproj`, `.gitignore` |
| The public gate: `Invoke-Gate.ps1 -Public` builds with the switch, fails on a stale plugin assembly (`Get-PluginAssemblyNames`) in the output, and runs the gate | `tools/agent/Invoke-Gate.ps1` |
| The release: by default the UI is published with the switch (`Publish-UiPayload`, `Get-UiPluginPublishArgs`); `Assert-ReleasePayloadPlugins` refuses the folder and the zip if they hold a plugin assembly, and so does `-SmokeOnly` before it launches anything. `-WithPlugins` is the explicit plugin build: published WITH every plugin checked out, and the same checks refuse a payload that LACKS one; it needs `-Repo` and refuses the public repo, and `-TagCheckout` names the checkout the release tags (`Resolve-ReleaseFeed`). A plugin repository drives that from its own release script. `-PayloadOnly` runs that publish and the check alone (the payload dry run, #893). The checks are a module the gate runs on fixture payloads (`Infrastructure/ReleasePluginRefusalTests`) | `tools/release/New-Release.ps1`, `tools/release/PluginPayloadCheck.psm1` |
| The update feeds (#893, #925): a build carrying a plugin reads the update feed the plugin registers (`IUpdateFeed`, `UpdateFeed.Select`; a private feed needs the GitHub sign-in, `update-feed.md` under `infrastructure-update/`), and an install whose app folder holds a declared plugin's file (`PluginLoader.DeclaredFiles` of the running app) refuses a payload without it (the file, or a payload manifest that lists it — a partial payload ships only what changed). The partial path refuses from the published manifest BEFORE any asset byte is fetched (`PluginUpdateRefusedException`, never a fallback to the full download); `UpdateInstaller.ApplyDownloadedUpdate` refuses on apply, for every source. A public install is never refused by it | `DotCharter.Services/Update/PluginUpdateGuard.cs`, `GitHubUpdateService.TryDownloadPartialAsync`, `UpdateInstaller.cs` (`Services/Update/PluginUpdateGuardTests`) |
| Menu rows: a row marked `menus:ContestedMenu.Feature="game-packages"` (or `game-install`) is pruned by `ContestedMenu.Prune(root, enabled, packages)`; `FileMenu` prunes again when its view model arrives. No row is marked today | `DotCharter.UI/Views/Shell/Menus/ContestedMenu.cs` |
| Palette entries: `ContestedFeatures.PackagePaletteCommandIds` (empty today) filtered by `FilterPalette(..., packages)` | `DotCharter.Services/Infrastructure/Features/ContestedFeatures.cs` |
| Dev switch: `DOTCHARTER_PACKAGE_READER=off` boots the app as a build without the reader | `AppPlugins.PackageReaderFromEnvironment` |

What is absent without `ReadsGamePackages` (`ReadsGameInstall` for the last row):

| Entry point | How |
|---|---|
| `.psarc` in the Library and Play Mode's Song Select | no plugin registers a `.psarc` `ILibraryPackageSource`, so no `.psarc` row exists; Library Doctor's package probe goes with it |
| Onboarding's Library page (it asks for a folder of `.psarc` files) | `OnboardingStepMachine(includeLibrary: false)`; the summary drops its line |
| Settings ▸ Files: the decoded-audio cache group and "Opening Rocksmith songs" | `FilesSettingsViewModel.PackageReaderGroups` (empty search groups, so hidden and never found) |
| Tone Designer: "Import tones from PSARC…" and the IMPORTED TONES section | `IToneDesignerScreen.CanImportFromPackages`; the import reads through `PackageTones` (`Plugins/IPackageToneImporter.cs`), which also feeds Play Mode's package tones and is empty without the plugin |
| A dropped or opened `.psarc` | `DroppedFileRouter.Route(path, readsGamePackages)`: an unsupported type |
| The AI designer's library tone anchors | `IToneAnchorLibrary` not registered (the designer asks `GetService`) |
| The game tables (amp chains, knob curves) read from the install | `MainWindowPrewarm.PrewarmTopLevelViews` starts the plugin's `IGameInstallReader` only with `ReadsGameInstall` |

Kept on purpose: the Library folder itself (it also holds `.dcsong` files), the
companion `.psarc` rows and File ▸ Export Rocksmith (the writer).

Tests: `UI/PackageFeaturesGateTests` (the composition, the routes, the menu and
palette rules, and a lint that every UI/Presentation file naming a reader asks the
gate), `Presentation/Settings/FilesSettingsViewModelTests`, `Services/Plugins/PluginLoaderTests`,
`Infrastructure/PluginNeutralityLintTests` (no tracked file names a particular plugin, the
plugin folder used before `plugins/`, or tooling switches that existed for one plugin),

`Infrastructure/PublicTreeHasNoPackageReaderTests` (#892: no tracked file names a reader type, a game-package file name or the container magic outside the writer, and no tracked package or game-audio binary beyond a reasoned allow-list; the source-level twin of `Infrastructure/PublicAssembliesHaveNoPackageReaderTests`), and the headless `Perf/PackageReaderOffUiPerfSmokeTests` (the booted app with the
reader off: Library, Song Select, Settings ▸ Files) plus the Tone Designer case in
`Perf/DialogConstructionUiPerfSmokeTests.ToneDesigner.cs`.
