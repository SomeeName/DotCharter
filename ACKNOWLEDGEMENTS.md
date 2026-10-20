# Acknowledgements

DotCharter is a newcomer to a community that has been charting, building and
sharing Rocksmith custom songs for more than a decade. It exists because of the
tools below, and it is meant to work **alongside** them, not in their place.
Their authors did the hard work of understanding these formats; we read it,
learned from it and, where the licence allows, built on it.

The exact licence texts and file-level lineage are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). This page is the human version:
what we took, what we learned, and how DotCharter works with each project.

**If we got your credit wrong, left something out or described your work
inaccurately, please [open an issue](https://github.com/SomeeName/DotCharter/issues).**
We will fix it quickly.

## Editor on Fire (EOF)

[Editor on Fire](https://github.com/raynebc/editor-on-fire) is the chart editor
most Rocksmith custom-song authors work in, maintained by **raynebc** (it began
as a Frets on Fire companion released by NewCreature in 2008).

- **What we took or learned.** The Rocksmith section and event vocabularies
  (`RsSectionCatalog`, `RsEventCatalog`) are ported from EOF's tables, with its
  names, order and display labels. How the arrangement properties are derived
  (`ArrangementPropertiesDeriver`) and EOF's rule for when a repeated chord is
  written as a short "high density" chord (`RsChordDensity`) follow EOF's
  source, which we read closely. The `E##` family of chart-validation codes
  mirrors checks EOF makes. Many keyboard shortcuts follow EOF's so the muscle
  memory carries over.
- **How we work with it.** DotCharter reads and writes the `.eof` format, so a
  chart can go from EOF to DotCharter and back, and it reports what an export
  approximated or dropped. No EOF source code is included in DotCharter.

## Charter (also known as Log Charter), by Lordszynencja

[Charter](https://github.com/Lordszynencja/Charter) is a Java chart editor by
**Lordszynencja**. Its code lives in the `log.charter` package, which is why
some of our check codes and comments say "Log Charter"; it is the same project.
Its project file is the `.rscp` format.

- **What we took or learned.** The 3D highway's three textures (`notes.png`,
  `fingering.png`, `inlays.png`) are Charter's own files, used unchanged under
  its BSD 3-Clause licence. Parts of the editor's logic and heuristics are
  inspired by or ported from Charter, including the auto-fix pipeline
  (`ArrangementAutoFixService`) and several arrangement-property rules. The
  `L##` validation codes mirror Charter's checks.
- **How we work with it.** DotCharter opens and saves Charter's `.rscp` project
  files and keeps what it does not understand intact, so a chart can move
  between the two (the promise is spelled out in
  [docs/RSCP-FORMAT-GUARANTEES.md](docs/RSCP-FORMAT-GUARANTEES.md)).

## Rocksmith2014.NET and DLC Builder, by iminashi

[Rocksmith2014.NET](https://github.com/iminashi/Rocksmith2014.NET) is a set of
F# libraries and **DLC Builder**, the application for building Rocksmith 2014
custom songs, by **iminashi** (MIT licence; the copyright line reads Tapio
Malmberg).

- **What we took or learned.** Several pieces are C# ports of its F# code, each
  marked where it appears: the derived fields of the SNG writer, the sound-bank
  writer and tone parsing. The tone-gear catalogue (`tone_gear_data.json`) is
  its file, byte for byte. DLC Builder's `I##` validation codes are the model
  for our `I##` family, and its project file format is the reference for our
  companion file.
- **How we work with it.** DotCharter writes a DLC Builder project
  (`.rs2dlc`) beside the chart, so the next step of your usual pipeline can be
  DLC Builder. We take care not to overwrite what you added there.

## CustomsForge

[CustomsForge](https://customsforge.com) is the community hub where custom
songs are shared and where the tools above are documented and supported. Its
Ignition4 upload page can import a `.rs2dlc` project and fill in the metadata
it carries; DotCharter writes the file in the shape that import expects. The
community's guides on charting and its conventions are how most of us learned
the craft.

## Rocksmith Custom Song Toolkit

The [Rocksmith Custom Song Toolkit](https://github.com/rscustom/rocksmith-custom-song-toolkit)
(maintained under the **rscustom** account) was, for many years, the way most
custom songs were made. DotCharter includes none of its code. We list it because
the conventions it helped set are the ones the whole community, and therefore
DotCharter, follows.

## Other projects we lean on

Libraries, models, captures, fonts and recordings are credited with their
licences in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); among them JUCE,
Avalonia, ONNX Runtime, the Demucs family of separation models, the Neural
Amp Modeler community and the TONE3000 capture library. The HTDemucs inference
in JuceSharp follows a sample from **OwnAudioSharp** by Modernmube, and the JUCE
wrapper grew from **NowSound** by Rob Jellinghaus.

## How DotCharter is made

DotCharter is developed with heavy use of AI coding assistants (Claude Code, and
Codex in places) under the direction of one maintainer. Most of its code was
written by those assistants; the maintainer decides what to build, reviews the
results and tests them in the game. Some pictures in the tone rig were generated
with an image model (see THIRD-PARTY-NOTICES.md, section 3).

What keeps quality in check:

- a test gate of roughly 20,000 cases that must pass before anything merges;
- a rule that every change ships with evidence, usually a test that was
  watched failing before it was made to pass;
- source lints that guard layering, licences, personal data and file sizes;
- review by the maintainer and play-testing with a real guitar.

The limits, honestly: AI-written code can be wrong in ways tests do not catch,
and the maintainer cannot hand-review every line. DotCharter is young, charts
can be misread or mis-exported, and it can disagree with the tools above in
corners nobody has hit yet. Please keep your originals, and tell us when
something is off. Bug reports, especially ones where DotCharter differs from
EOF, Charter or DLC Builder, are welcome.
