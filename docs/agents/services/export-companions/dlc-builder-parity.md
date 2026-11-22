# Export dialog: DLC Builder parity (#953)

The Rocksmith export dialog follows DLC Builder (Rocksmith2014.NET, MIT) where it has a
matching feature. There are NO export presets (no picker, no default tone-set, no stored
  package target): the platform and the package form are the whole package target, per export.
`AppConfig` members from the old presets ride along in `UnknownSettings` and are never read.

- **Version.** `ChartData.Version` ("1" by default), saved as `<dcVersion>` (absent at "1";
  blank reads as "1"), carried by `ChartProjectRestorer`/`SongMetadataFields` (so it is one
  undo step). The dialog edits it and commits it to the chart on close or on a release export.
  It reaches the package as `Package Version` in `toolkit.version`
  (`RocksmithBuildOptions.Version`); the byte pin `version` holds that.
- **Volume is PER SONG (#293), never global.** `ChartData.AudioVolume` / `PreviewVolume` (dB, null until measured) with
  the content signature each was measured from and a manual flag, saved as `<dcAudioVolume>` … `<dcPreviewVolumeManual>`.
  `Export/Audio/ProjectVolumes.Measure` (the BS.1770 `RocksmithAutoVolume`, the file that ships; the preview is the
  preview file or the 28 s clip cut from the audio) runs when audio is attached (`ProjectSessionService.AttachAudioAsync`,
  `NewProjectCreator`) and when the export dialog opens. The export command joins
    applicable pending measurement before capturing options, preserving typed values.
    `RocksmithExportVolumeInput` captures the chart start actually measured, source
    paths/content signatures, loudness target and volume fields. Export commits its draft first and
    starts a fresh measurement when those inputs differ. Source content and PCM policy
    fix decoded duration and the 28 s clip's effective clamp/length; editor duration is
    not a meter input. A failed measurement logs its warning and keeps the existing
    gain, or the default when unset, as on the original measuring path.
    `RocksmithExportContinuation` carries the run token/owner and the shown-value
    baseline. Cancelled, superseded or closed/disposed continuations cannot restart,
    recapture shown values or publish fields; ordinary prefill still fills untouched
    fields. A baseline may refresh only while its current run keeps controls disabled.
  `SaveCompanionService.VolumesForAsync` measures its detached snapshot's actual main
  audio and generated clip after the save, so an explicit-preview gain is never
  trusted for different audio. Guarded automatic adoption reaches the next project
  write without dirty/undo state. No writer awaits decode and no failed/rejected
  input is memoized as settled. Requested-preview signatures allow cache checks
  before decoding duration; source-content signatures include the PCM policy.
  A signature or target change re-measures automatic values on the next refresh. Typed values
  (`CommitVolumes` on a successful export) retain their manual flag.
  The dialog measures a detached `ChartData` on the worker and commits its result on the UI continuation
  without an undo node or dirty mark when refreshing an existing automatic value; the next project
  write retains those fields. The first measurement of previously unset values still uses
  `SetProjectVolumesAction`; typed overrides use one action for both volumes too. Undo restores
  values, manual flags and signatures together. A late measurement is discarded if the project, audio
  paths, preview start or persisted volume fields changed. `ProjectVolumesTests` covers detached
  measurement, history and a newer manual override winning the race.
  The `.rs2dlc` writes the same two numbers. `AppConfig.ExportAudioVolume` / `ExportPreviewVolume` are gone: they gave every
  song the last measured song's volume. Unmeasured projects use -8 / -7 until measured.
- **Preview start (#456).** `ChartData.PreviewStartMs` (null = automatic, a quarter into the song), saved as
  `<dcPreviewStartMs>`, typed in the export dialog as `m:ss.fff` (`Export/Audio/PreviewStartTime`), one undo step
  (`SongMetadataFields`). It reaches `RocksmithBuildOptions.PreviewStartMs` (the project's value when the options name none),
  the `.dcsong` writer and the `.rs2dlc` (`AudioPreviewStartTime`, seconds; `PreviewStartTime.FromRs2DlcSeconds` reads one back).
  `PreviewClip` clamps it so a whole 28 s clip fits and follows `Preview.fs` + `AudioFader.fs` (fade-out ramp 1 to 1/N, short-song
  halving by the whole song's length); the clip is re-cut on every build. A new start re-measures the clip's preview volume.
- **Sort names, DLC key, author, tool version (#292).** `ChartData.TitleSort` / `AlbumSort` (with the existing
  `ArtistSort`) and `DlcKey` are saved as `<dcTitleSort>` / `<dcAlbumSort>` / `<dcDlcKey>` (absent while blank =
  derived); both exporters take an authored value first (`RsPackageMetadataFactory`, `Rs2DlcProjectBuilder`; the
  build options' key still wins in the package). The dialogs (`ISongNamesFields`, shared by New project and Song
  metadata) show the DERIVED values prefilled (`Editing/SongMetadataDefaults`) and save a value left equal to its
  default as blank, so it keeps following the names. `toolkit.version` names `DotCharter v<AppVersionInfo.Current>`
  (`RocksmithPackageBuilder.ToolkitVersionText`); the byte pins fix it through `RocksmithBuildIdentity.ToolVersion`
  so a version bump re-pins nothing. The author is `DefaultCharterName` (onboarding and Settings), overridable per project.
- **Release file name.** `RocksmithPackageName.For` supplies
  `<Artist>_<Title>_v<Version>[_rsxr]_p.psarc` to the save companion and export dialog.
  Mac uses `_m.psarc` without `_rsxr`. Version edits rename a derived path;
  a chosen path keeps its name. A per-project output draft wins over
  defaults. Saved projects default to their own `export/`; `LastExportDir` is
  for unsaved projects and the Browse picker's start folder.
  Publishing to the exact PC companion path stamps the finished package in
  `.dotcharter/companions.json` for future saves.
- **Test export.** The blank folder field uses the normal output file's folder. Settings ▸ General ▸
  Advanced ▸ Rocksmith export can set a default for every song (`AppConfig.ExportTestDefaultFolder`).
  The export dialog keeps typed or browsed overrides in the current project's draft.
  `Export/Rocksmith/RocksmithTestExport` plans version "test" and the shared artist/title filename with
  `_test` (`IRocksmithGameProbe` tells it whether the game is running).
  with the game running `_test_vN` goes on the file, `vN` on the key and ` vN` on the title (private
  copy of the chart), otherwise the plain test name is reused. Cleanup recycles earlier test builds
  only with a matching project receipt and after the replacement set is published. It excludes the
  current outputs and preserves chord sidecars.
  `RocksmithTestPackageOwnership` stores receipts under the saved project, never the game folder;
  unproven/edited files remain and an unproven existing destination refuses replacement.
  `PackagePublication` stages every platform before committing and retains rollback copies
  until the whole set succeeds. A failed rollback keeps the `.previous` files for recovery.
  PC (or Mac when only Mac is chosen), never PC + Mac. `_vN` is read from the file NAME, not the
  whole path as DLC Builder does.
  Imported tone bodies are resolved using the live project identity, including for test builds;
  the build copy holds the test title and version without changing that session lookup.
  Cleanup candidate and version discovery recognize exact artist/title `_test[_vN]` names and
  legacy lower-case key families, with range/platform suffixes (case-insensitive). Prefix neighbours
  and notes/backup files never count (`RocksmithTestExportTests`, #551).
- **Auto volume (#754).** `Export/Audio/RocksmithAutoVolume` uses DLC Builder's BS.1770
  `LufsMeter` on the packaged 48 kHz stereo 16-bit PCM. DotCharter deliberately differs
  from DLC Builder's fixed −16 LUFS gain policy. Settings ▸ General ▸ Advanced ▸ Rocksmith
  export sets a target from −20 to −10 LUFS in whole steps (default −14). Gain is the
  target minus measured loudness, rounded to 0.1 dB away from zero. Boosts are capped
  at the sample-peak headroom, rounded down to 0.1 dB. Silence reads 0 dB.
  Automatic gain signatures include the packaged-PCM policy and target, so older
  automatic measurements refresh. A `.wem` is measured through the `.wav` or
  `.ogg` beside it, as DLC Builder does, and refused otherwise (no vgmstream here).
  `RocksmithAutoVolumeTests` holds numbers produced by running DLC Builder's meter on the same signal.
  The volume fields take -45..45 like DLC Builder's.
