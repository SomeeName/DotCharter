# RS Extended Range export

RS Extended Range is the export format for seven-/eight-string guitar and
five-/six-string bass. A six-string guitar or four-string bass keeps its stock
bytes, including inside a package with an extended arrangement.

## Package and chart records

- An accepted extended arrangement makes the package an unencrypted Windows
  archive with `PSXR` magic and `<name>_rsxr_p.psarc` naming. Mac alone is refused;
  PC + Mac writes PC and reports the reason. `RocksmithPlatformTargets` owns this
  decision. The builder names from the converted arrangements it actually writes:
  a PC package has the `_rsxr_p.psarc` suffix exactly when its magic is `PSXR`.
  Stock-only PC packages remove the tag from a name ending in `_rsxr_p.psarc`.
  A Mac-only export writes the requested path exactly as given; PC + Mac
  derives the Mac sibling from the PC name without the tag. Encryption follows
  the stock rules for stock-only content. Every caller uses the builder's
  reported written path.
- `RsxrSngWriter` writes an eight-byte `SNXR` header: version 1, instrument,
  string count, reserved zero. Strings are numbered thickest first. Templates,
  chord techniques and tuning have one column per string; note indices and section
  masks use that same order. The body has zero padding of
  `256 + max(0, 6 - N) * (6 * templateCount + 396 * chordNotesCount + 2)` bytes.
- `RsManifestGenerator` writes `ExtendedRange: { Version: 1 }` immediately before
  an extended arrangement's `Tuning`, in its manifest and `.hsan` entry. The
  tuning keys are `string0` through `stringN-1`, with offsets from positional
  standard open notes. Stock entries have neither the marker nor extra keys.
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
unencrypted, Windows only, read by the RS Extended Range mod. The stock plain
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
confirms the written PC path.

`RsxrOutputPinTests` freezes 49 packages, 72 chart bodies, 121 manifest/header
entries and 72 arrangement XMLs in `rsxr-output-pins.json`. Only the XML conversion
timestamp is fixed before hashing. These values must not be regenerated.
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
