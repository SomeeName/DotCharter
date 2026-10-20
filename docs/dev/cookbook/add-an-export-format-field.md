# Add a field to an export format

Use this when the chart already holds the data and an output file (Rocksmith XML,
SNG, Guitar Pro, the project file) should carry it. If the data is new to the chart
too, start with [add-a-note-technique.md](add-a-note-technique.md).

## Steps

1. **Find the writer.** Exporters live under `DotCharter.Services/Export/`:
   - Guitar Pro: `DotCharter.Services/Export/GuitarPro/GpExporter.cs`
   - SNG: `DotCharter.Services/Export/Sng/SngFromDomainMapper.cs`
   - Rocksmith XML: `DotCharter.Services/Parsing/Xml/Song/RsXmlSongWriter.cs`
   - Project file: `DotCharter.Services/Parsing/Rscp/RscpWriter.cs`
2. **Write the field, and only when it is set.** A default value must produce the
   same bytes as before. If the format has a flag bit for it, set the bit only
   when you have verified what the reference tool writes.
3. **Share the rule with the importer.** If both directions decide "is this a X?",
   put that decision in one helper that both call. The example does this with
   `GpPickScrape` in `DotCharter.Services/Parsing/GuitarPro/GpPickScrape.cs`.
4. **Read it back.** Make sure the matching reader returns the same value, or
   write down in the pull request why it cannot.
5. **Check the export gate.** `DotCharter.Services/Export/Validation/` holds checks
   that run before a package is built; add one if the field can be wrong in a way
   the game would reject.
6. **Docs.** If the export has a guide under `docs/agents/services/` (see
   `import-tabs-audio.md` and `export-companions.md`), add one line to it.

## Tests to add

- A round-trip test: build or import a chart, export, re-import, and compare every
  field. Put it next to the exporter's other tests, for example in
  `DotCharter.Tests/Services/Export/GuitarPro/`.
- A negative case: input that looks similar but must not use the new path.
- Run `DotCharter.Tests/Services/Export/ExportBytePinTests.cs`. It must stay green
  without changing its pinned bytes.
- Break the writer on purpose (drop the new line) and confirm the test fails.

## Example in the code

Guitar Pro export writes pick scrapes back as pick slides, with
`DotCharter.Tests/Services/Export/GuitarPro/GpExporterPickScrapeTests.cs` as the
round-trip test.
