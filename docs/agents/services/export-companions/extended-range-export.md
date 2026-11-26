# RS Extended Range export

RS Extended Range is the export format for seven-/eight-string guitar and
five-/six-string bass. A six-string guitar or four-string bass keeps its stock
bytes, including inside a package with an extended arrangement.

## Package and chart records

- An accepted extended arrangement makes the package an unencrypted archive
  with `PSXR` magic, `<name>_rsxr_p.psarc` on PC and `<name>_rsxr_m.psarc` on Mac.
  The Mac form is plain too (the Mac layout and entry folders, the same PC chart
  bodies), so it needs no Mac key; `RocksmithPlatformTargets` owns the platform
  decision. The builder names from the converted arrangements it actually writes:
  a package has the `_rsxr` tag exactly when its magic is `PSXR`, and stock-only
  packages drop it (`ExtendedRangePackageName.Apply` / `ApplyMac` / `Untag`).
  Release, test and version-rename names (`RocksmithPackageName`,
  `RocksmithReleaseName`, `RocksmithTestExport`) carry the tag on both platforms.
  Encryption follows the stock rules for stock-only content. Every caller uses the
  builder's reported written paths. The Mac package is untested on a real Mac.
- **Standard package beside it (#898).** With stock and extended arrangements ticked
  together and `RocksmithBuildOptions.WriteStandardPackage` on (the dialog's option,
  default on; the `.psarc` save companion always), `RocksmithPackageBuilder.StandardPackage.cs`
  runs the builder a second time on the stock arrangements alone, to the untagged
  name (`StandardOutputPath` overrides it) with the stock encryption choice
  (`StandardPlainPackage`). Its bytes equal a stock-only export of the same project
  unless a stock arrangement shares its type with an extended one before it;
  the RS Extended Range package is unchanged. Both share the DLC key, song key and
  each arrangement's persistent and master ids AND song partition
  (`SharedArrangementIds`), so an entry name such as `key_lead` is the same arrangement
  in both packages (#1043: the standard package used to renumber a stock Alternate to
  `key_lead`, the extended Main's name and files). A player installs one or the other. Its paths follow the extended ones in `PackagePaths`
  and are listed in `StandardPackagePaths`. A stock-only or extended-only selection
  writes one package. `RsxrDualPackageTests` pins it.
- **One Main per path in the extended package (#898).** When a path (Lead, Rhythm,
  Bass) has a stock Main beside an extended Main, the RS Extended Range package
  ships the stock one as Alternate (`RsPackageArrangement.DemotedToAlternate`, set by
  `ResolvePathMains`): its `.hsan` `Representative` and manifest `represent`
  are 0. The standard package keeps it Main; a path there whose only Main was extended
  ships its first stock arrangement as Main (`PromotedToMain`, #1043), so no path is left
  without one; the standard `.rs2dlc` does the same (`PromoteLonePathMains`). The project file never changes. `RsxrFourArrangementExportTests` pins both.
  `RsxrDualPackageTests` covers both packages and a path without a duplicate.
- `RsxrSngWriter` writes an eight-byte `SNXR` header: version 1, instrument,
  string count, reserved zero. Strings are numbered thickest first. Templates,
  chord techniques and tuning have one column per string; note indices and section
  masks use that same order. The body has zero padding of
  `256 + max(0, 6 - N) * (6 * templateCount + 396 * chordNotesCount + 2)` bytes.
- `RsManifestGenerator` writes `ExtendedRange: { Version: 1 }` immediately before
  an extended arrangement's `Tuning`, in its manifest and `.hsan` entry. The
  tuning keys are `string0` through `stringN-1`, with offsets from positional
  standard open notes. Stock entries have neither the marker nor extra keys.
- **The low bass tuning fix (#906) never touches an extended bass.** The format spells the
  five-/six-string bass's low B0 string itself (`Rs7StringLayout.BassOpenMidi`), and nothing in
  the repo says how the mod's note detection treats it, so its SNXR record and manifest stay
  as they were; `LowBassTuningFixTests` pins them byte-equal with the fix on and off.
- `RocksmithSngWriter` rejects an extension model. Its stock output stays pinned.
  `Rs7ChordSidecar.RemoveStale` removes exact stale sidecar names at the requested
  and published package paths, leaving unrelated files alone.

## One chart model and one internal projection

The domain numbers strings lowest first. `Rs7Projection`, `Rs7StringLayout`,
`Rs7BlockBuilder` and `RocksmithSng.Extension` retain the internal slot-order
model. The six stock slots and the extra guitar columns are combined by the
RS Extended Range writer at serialization. This model is also used by legacy
readers; it is not an RS7X export target.

Template deduplication includes every column. Chord techniques, open strings,
double stops, ghost gems, section masks and extra-only chords retain their
model rules. A bass chord keeps low B and high C as chord columns. No extended
pitch is inserted into a free stock MIDI slot.

## Arrangement XML and companions

An extended arrangement written with all its strings has
`<tuning extendedRange="1" strings="N" ...>` and positional numbering for tuning,
templates, single notes and chord notes. The tuning still has at least six
attributes, with unused ones zero. `RsXmlSongWriter` and the dynamic-difficulty
round trip use this numbering throughout.

`SixStringProjection` keeps the six stock guitar strings, drops extra-string
content, and reports `LastLoss`. It writes stock XML without the extended marker.
With `SixStringProjection` enabled, seven-/eight-string guitar writes a six-string copy, so RS Extended Range refusals do not apply to that stock XML.
The option does not trim extended bass strings. `Rs2DlcProjectBuilder.TuningVector`
matches the XML: positional when every string is written, stock slots for a
six-string guitar copy. Stock guitar and bass tuning vectors keep six slots.

More than eight guitar strings, more than six bass strings, or an extended
tuning offset outside -24..24 is refused by `RsxrStringLayout`. `TryWrite` returns
no document and `WriteTo` opens no file for a refusal. The serializer forwards
the sentence to its warning callback or logger. Companion XML, the bundle and
the `.rs2dlc` project leave out the refused arrangement and keep accepted content.
Saving a project document succeeds with these accepted companions. When the
main document is arrangement XML, `ProjectDocumentService.PrepareSave` explicitly
selects throwing on refusal: the save fails with an `InvalidDataException` whose
message is the refusal sentence, preserves the existing file and removes its
`.saving` stage. The serializer's companion entry points keep skipping without
throwing. The low-level `Write` entry point throws the same refusal.

The extended-tuning validation finding is a warning. Save companions and the
export dialog build accepted arrangements, keeping the stock package name and
magic when none of them is extended. The companion log and the dialog result's
warnings include `Arrangement '<name>' was skipped: <sentence>` from the builder.

## Reading existing work

RS7X is read only. `RsXmlSongReader` and `RsXmlSongStringOrder` read unmarked
arrangement XML in slot order, honouring its declared string count. Marker `1`
selects positional order; any other marker value is rejected. The project
serializers keep the domain order. An obsolete `ExtendedRangeFormat` config key
is ignored on load and discarded on the next configuration save; it cannot
select an export target. Other unknown settings retain their JSON values.

`RsxrXmlCompatibilityTests` reads eight frozen synthetic XML fixtures: marked
and unmarked seven-/eight-string guitar and five-/six-string bass. It checks
string counts, tuning and every sound. The reader implementations are shared by
these tests and normal imports.

## Dialog and evidence

The package picker offers Encrypted and Plain, or just Plain without keys. An
accepted extended selection shows Plain, disables the picker, and displays the reason:
unencrypted, read by the RS Extended Range mod. The stock plain
package warning is hidden for this selection. Deselection restores the stock
choice. The output field always adds or removes the tag for the accepted
selection and target, including a tagged draft reopened with stock content.

The picker remembers the final package path the user confirmed for either target,
including any completed suffix. At Export, the builder supplies each actual platform path
after all stages are complete and before publishing. If that path differs from the confirmed path and already exists, the
dialog asks the package overwrite question. Declining keeps the export open
and writes nothing; accepting exports and confirms the written path. Typing a
path clears the picker confirmation. Default and typed paths retain their
ordinary overwrite behavior. Mac-only and test exports do not ask this question.
Publishing a Mac-only package preserves the picked path; publishing a PC package
confirms the written PC path. When the export also writes the standard package,
its untagged name beside the confirmed path counts as confirmed, so a repeat export
asks nothing more (`RsxrPickedOutputTests`). A mixed selection shows the standard
package option with a one-line hint that both packages are one song.

`RsxrOutputPinTests` freezes 49 packages, 72 chart bodies, 121 manifest/header
entries and 72 arrangement XMLs in `rsxr-output-pins.json`. Only the XML conversion
timestamp is fixed before hashing. These values must not be regenerated.
2026-11-22, #898: `multi_5_arrangements`, `multi_8_arrangements` and
`multi_10_arrangements` were re-pinned on purpose. Each has a stock Main beside an
extended Main on one path, now demoted to Alternate in the extended package; only
the demoted manifests, the `.hsan` and the package hash changed. Every other pin is
unchanged.
`RsxrPackageExportTests`, `RsxrSngWriterTests`, `RsxrXmlTests`,
`RsxrXmlRefusalTests`, `RsxrDialogStateTests`, `RsxrUiPathTests` and companion tests
cover the semantic paths. `SettingsConstructionUiPerfSmokeTests`,
`RocksmithExportLayoutUiPerfSmokeTests` and
`RocksmithExportPlatformUiPerfSmokeTests` measure the attached views.

`Rs7TestSuiteTests.Write_the_suite` writes the synthetic in-game suite through
the automatic exporter. `RsxrComparisonPackagesTests.Write_the_comparison_packages`
is an opt-in writer of the same native packages under an `rsxr` folder, with
pinned identities and click-track audio for an outside comparison. It writes
only the RS Extended Range packages. Internal projection and technique checks
remain in `Rs7BlockExportTests`, `Rs7ExtendedRangeRuleTests`, `Rs7Rules1101Tests`,
`Rs7ChordProjectionTests` and `Rs7ExtendedRangeGoldenTests`.
