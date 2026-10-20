# Add a note technique, end to end

A technique (a new field on a note or chord) has to survive every format and every
undo. The order below follows the data: model first, then each reader and writer,
then what the user sees. Do it in one pull request; a half-wired field loses data
silently.

## Steps

1. **Core model.** Add the field to `DotCharter.Core/Domain/Guitar/GuitarNote.cs`
   (and `Chord.cs` for a chord-level field). A new enum goes in
   `DotCharter.Core/Domain/Guitar/GuitarEnums.cs`. Choose a default that means
   "not set" so old charts are unchanged.
2. **Copy and clone.** Add the field to the type's `CopyFrom` (and `Clone`). The
   project cloner and the undo restorer both go through them, so no other copy
   code is needed. `DotCharter.Tests/Core/DomainCloneCoverageTests.cs` fails if you
   forget.
3. **Change detection.** Add the field to the comparer in
   `DotCharter.Services/State/ChartActions.cs` (`ChartActionStateComparer`), or an
   edit to it will not register as a change.
4. **Project file.** Read and write it in the project format:
   `DotCharter.Services/Parsing/Rscp/RscpArrangementParser.cs` and
   `DotCharter.Services/Parsing/Rscp/RscpWriter.cs`.
5. **Rocksmith XML.** `DotCharter.Services/Parsing/Xml/Song/RsXmlSongReader.cs` and
   `DotCharter.Services/Parsing/Xml/Song/RsXmlSongWriter.cs`.
6. **SNG.** Export in `DotCharter.Services/Export/Sng/SngFromDomainMapper.cs`, import
   in the package reader's SNG mapper. Only import what the
   file really tells you; if a default byte is also a valid value, reading it
   would stamp every note.
7. **Other formats**, if they carry the technique: Guitar Pro in
   `DotCharter.Services/Export/GuitarPro/GpExporter.cs` and
   `DotCharter.Services/Parsing/GuitarPro/`. The default value must write nothing,
   so existing exports stay byte-identical (`DotCharter.Tests/Services/Export/ExportBytePinTests.cs`
   proves it).
8. **3D highway and 2D timeline**, if the technique has a look. The snapshot the
   renderers read is built in `DotCharter.Rendering/Data/FrameSnapshot.Notes.cs`, with
   flags in `DotCharter.Rendering/Data/Render/RenderFlags.cs` and the drawing in
   `DotCharter.Rendering/Components/NoteRendering/`. The 2D timeline is in
   `DotCharter.Rendering/Timeline2D/`. Shader changes have extra steps; read
   `DotCharter.Rendering/AGENTS.md` first. A field with no visual (like a finger
   number) skips this step.
9. **Inspector.** Add a row in the Sound tab:
   `DotCharter.UI/Views/Editor/Inspector/SoundNoteTechniqueView.axaml` for notes,
   `SoundChordTechniqueView.axaml` for chords, bound through
   `DotCharter.UI/ViewModels/Editor/InspectorViewModel.Notes.cs` and
   `InspectorViewModel.Chords.cs`. Write edits through the inspector's mutation
   funnel so they are undoable.
10. **Strings.** Row labels go into all three resx files
    ([add-a-localized-string.md](add-a-localized-string.md)).
11. **Docs.** Add the field to `docs/agents/core/domain-model.md`.

## Tests to add

- A round trip through every format you touched, plus byte-identical defaults,
  clone, `CopyFrom` and the comparer. In the example that is
  `DotCharter.Tests/Services/Export/NoteFingerPickStrumTests.cs`.
- Add the field to the deep fixture in
  `DotCharter.Tests/Services/Parsing/Rscp/RscpDeepValueRoundTripTests.cs`.
- A view smoke test for the inspector rows (`DotCharter.Tests/Perf/NoteFingerPickStrumInspectorUiPerfSmokeTests.cs`).
  See [add-a-view.md](add-a-view.md).
- Prove each writer is covered: drop the field from one writer at a time and watch
  the matching test fail.

## Example in the code

Pick direction runs the whole length of the recipe: `PickDirection` on
`DotCharter.Core/Domain/Guitar/GuitarNote.cs`, its `.rscp` attribute in
`RscpArrangementParser` and the writer, and the inspector row. For step 8, follow
any technique that has a look on the highway (for example a palm mute) into
`DotCharter.Rendering`.
