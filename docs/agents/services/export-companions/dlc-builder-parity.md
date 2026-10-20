# Export dialog: DLC Builder parity (#953)

The Rocksmith export dialog follows DLC Builder (Rocksmith2014.NET, MIT) where it has a
matching feature. There are NO export presets (no picker, no default tone-set, no stored
target): the platform and the package form are the whole "target", per export.
`AppConfig` members from the old presets ride along in `UnknownSettings` and are never read.

- **Version.** `ChartData.Version` ("1" by default), saved as `<dcVersion>` (absent at "1";
  blank reads as "1"), carried by `ChartProjectRestorer`/`SongMetadataFields` (so it is one
  undo step). The dialog edits it and commits it to the chart on close or on a release export.
  It reaches the package as `Package Version` in `toolkit.version`
  (`RocksmithBuildOptions.Version`); the byte pin `version` holds that.
- **Release file name.** `Export/Rocksmith/RocksmithReleaseName`: `<Artist>_<Title>_v<Version with . as _>`
  from an authored sort value, else the name as written, then DLC Builder's
  `StringValidator.fileName`. A sort equal to the artist is this app's mirror, not authored.
  Deliberate difference: DLC Builder's `convertToSortField` fallback drops a leading
  "a "/"an "/"the "; here it stays (`Ave-Mujica_A-Song-Of-Romance_v1`). The output
  field follows the version while it still holds the name the dialog derived.
- **Test export.** `Export/Rocksmith/RocksmithTestExport` plans it (pure; `IRocksmithGameProbe`
  answers "is `Rocksmith2014` running"): into the test folder (`AppConfig.ExportTestFolder`, blank =
  a `test` folder beside the output file), version "test", lower-cased DLC key as the file name;
  with the game running `_vN` goes on the file, `vN` on the key and ` vN` on the title (private
  copy of the chart), otherwise the earlier test builds of the package are deleted first.
  PC (or Mac when only Mac is chosen), never PC + Mac. `_vN` is read from the file NAME, not the
  whole path as DLC Builder does.
- **Auto volume.** `Export/Audio/RocksmithAutoVolume` ports `LufsMeter` + `Volume.calculate`
  (BS.1770 integrated loudness against -16, rounded to 0.1 away from zero). It measures the engine's
  own decode (native rate and channels) like DLC Builder; a `.wem` is measured through the `.wav` or
  `.ogg` beside it, as DLC Builder does, and refused otherwise (no vgmstream here).
  `RocksmithAutoVolumeTests` holds numbers produced by running DLC Builder's meter on the same signal.
  The volume fields take -45..45 like DLC Builder's.
