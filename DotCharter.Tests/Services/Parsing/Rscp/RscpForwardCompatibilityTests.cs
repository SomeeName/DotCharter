using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     <b>The `.rscp` format promise, mechanically.</b> Every clause of
///     <c>docs/RSCP-FORMAT-GUARANTEES.md</c> that can be checked at all is
///     checked here, so the doc cannot rot into a claim the code stopped
///     honouring — this repo treats a false safety claim in a guide as the
///     dangerous kind, because a reader stops looking.
///     <para>
///         The promise has two halves and they are deliberately UNEQUAL, which
///         is the thing most worth pinning: an unknown node at the TOP LEVEL
///         survives a load/save round trip, and an unknown attribute or child
///         DEEPER in the document does not. The second half is a promise to
///         drop, not an admission of a bug: re-attaching a newer build's
///         per-note hint needs an identity a `.rscp` note does not have, and
///         hanging it on the wrong note after an edit is worse than losing it.
///     </para>
/// </summary>
public sealed class RscpForwardCompatibilityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_fwd_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string Path_(string name) => Path.Combine(_dir, name);

    /// <summary>A project this build could have written itself — no residue.</summary>
    private static ChartProject OwnOutput()
    {
        var project = RichProjectFactory.Build();
        project.UnknownRscpNodes.Clear();
        return project;
    }

    // ---------------------------------------------------------------- residue

    [Fact]
    public void AnUnknownTopLevelNode_FromANewerBuild_SurvivesLoadAndSave()
    {
        var ser = new RscpChartSerializer();
        var path = Path_("newer.rscp");
        ser.Save(path, OwnOutput());

        // The newer build's whole-feature node.
        var doc = XDocument.Load(path);
        doc.Root!.Add(new XElement("futureFeatureBlock",
            new XAttribute("version", "9"),
            new XElement("futureChild", new XAttribute("p", "12/7"))));
        doc.Save(path);

        var loaded = ser.Load(path);
        Assert.Single(loaded.UnknownRscpNodes);

        var resaved = Path_("newer.resaved.rscp");
        ser.Save(resaved, loaded);

        var block = XDocument.Load(resaved).Root!.Element("futureFeatureBlock");
        Assert.NotNull(block);
        Assert.Equal("9", block!.Attribute("version")?.Value);
        Assert.Equal("12/7", block.Element("futureChild")?.Attribute("p")?.Value);
    }

    /// <summary>
    ///     The OTHER half, and the one the promise had to be honest about. An
    ///     attribute a newer build hung on a note is gone after a save on this
    ///     build. If someone implements deep retention later, this test is where
    ///     the promise changes — flipping it silently would be the bug.
    /// </summary>
    [Fact]
    public void AnUnknownAttributeOnANote_IsDroppedOnSave_AndTheDocSaysSo()
    {
        var ser = new RscpChartSerializer();
        var path = Path_("deep.rscp");
        ser.Save(path, OwnOutput());

        var doc = XDocument.Load(path);
        var note = doc.Descendants("soundNote").First();
        note.Add(new XAttribute("futureHint", "42"));
        note.Add(new XElement("futureNoteChild", "x"));
        var arrangement = doc.Root!.Element("arrangements")!.Elements("arrangement").First();
        arrangement.Add(new XElement("futureArrangementBlock", "y"));
        doc.Save(path);

        // Vacuity keeper. This test asserts an ABSENCE, so it would pass just as
        // happily if the injection above had silently gone nowhere — assert the
        // unknown data really is in the file before the round trip removes it.
        var injected = XDocument.Load(path);
        Assert.NotEmpty(injected.Descendants().Attributes("futureHint"));
        Assert.NotEmpty(injected.Descendants("futureNoteChild"));
        Assert.NotEmpty(injected.Descendants("futureArrangementBlock"));

        var resaved = Path_("deep.resaved.rscp");
        ser.Save(resaved, ser.Load(path));

        var after = XDocument.Load(resaved);
        Assert.Empty(after.Descendants().Attributes("futureHint"));
        Assert.Empty(after.Descendants("futureNoteChild"));
        Assert.Empty(after.Descendants("futureArrangementBlock"));

        // …and the note itself is intact, i.e. this is a DROP, not a failure to
        // parse the element that carried the unknown attribute.
        Assert.NotEmpty(after.Descendants("soundNote"));
    }

    /// <summary>
    ///     The failure this would have if the known-name set ever fell behind the
    ///     writer: a node written from the model AND re-appended as residue, one
    ///     more copy on every save. The emit side drops a name the writer already
    ///     used, so the file is stable no matter how many times it is re-saved.
    /// </summary>
    [Fact]
    public void ResavingRepeatedly_NeitherDuplicatesNorAccumulatesTheResidue()
    {
        var ser = new RscpChartSerializer();
        var path = Path_("stable.rscp");
        ser.Save(path, OwnOutput());

        var doc = XDocument.Load(path);
        doc.Root!.Add(new XElement("futureFeatureBlock", new XAttribute("version", "9")));
        doc.Save(path);

        var counts = new List<int>();
        var duplicated = new List<string>();
        var project = ser.Load(path);
        for (var i = 0; i < 4; i++)
        {
            ser.Save(path, project);
            project = ser.Load(path);

            var root = XDocument.Load(path).Root!;
            counts.Add(root.Elements("futureFeatureBlock").Count());

            // The general form, and the one that catches a STALE known-name set:
            // a root name written from the model and re-appended as residue would
            // show up here as two elements of the same name, growing every save.
            duplicated.AddRange(root.Elements()
                .GroupBy(e => e.Name.LocalName, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"save {i + 1}: <{g.Key}> ×{g.Count()}"));
        }

        Assert.Equal(new[] { 1, 1, 1, 1 }, counts);
        Assert.Single(project.UnknownRscpNodes);
        Assert.True(duplicated.Count == 0,
            "A root element is written more than once — the residue is being appended next to a node the writer "
            + "already emitted, so the file grows a copy on every save:\n  " + string.Join("\n  ", duplicated));
    }

    /// <summary>
    ///     …and the guard that stops that duplication may only test the WRITER's
    ///     names. It used to be a growing set — every appended fragment's name
    ///     went back in — so the second and later unknown roots that share a
    ///     name were DROPPED. A newer build's
    ///     <c>&lt;dcSection id="1"/&gt;&lt;dcSection id="2"/&gt;&lt;dcSection id="3"/&gt;</c>
    ///     came back as one node after a single re-save on this build, and the
    ///     other two were gone for good. Nothing about a residue name is
    ///     required to be unique; only "do not re-emit what the writer wrote" is.
    /// </summary>
    [Fact]
    public void SameNamedResidueNodes_AllSurviveEveryResave()
    {
        var ser = new RscpChartSerializer();
        var path = Path_("siblings.rscp");
        ser.Save(path, OwnOutput());

        var doc = XDocument.Load(path);
        for (var id = 1; id <= 3; id++)
            doc.Root!.Add(new XElement("dcSection", new XAttribute("id", id.ToString())));
        doc.Save(path);

        var project = ser.Load(path);
        Assert.Equal(3, project.UnknownRscpNodes.Count);

        for (var i = 0; i < 4; i++)
        {
            ser.Save(path, project);
            project = ser.Load(path);

            var ids = XDocument.Load(path).Root!.Elements("dcSection")
                .Select(e => e.Attribute("id")?.Value)
                .ToArray();
            Assert.Equal(new[] { "1", "2", "3" }, ids);
            Assert.Equal(3, project.UnknownRscpNodes.Count);
        }
    }

    /// <summary>
    ///     Order is the whole reason the residue goes LAST. The header/beats/
    ///     vocals parsers resolve their nodes with <c>Descendants(name)</c> +
    ///     <c>FirstOrDefault()</c>, which is document order, so a preserved node
    ///     that happens to contain a <c>&lt;title&gt;</c> must not be able to
    ///     answer for the real one.
    /// </summary>
    [Fact]
    public void APreservedNodeCannotShadowARealOne_BecauseItIsWrittenLast()
    {
        var ser = new RscpChartSerializer();
        var path = Path_("shadow.rscp");
        var original = OwnOutput();
        ser.Save(path, original);

        var doc = XDocument.Load(path);
        doc.Root!.Add(new XElement("futureFeatureBlock",
            new XElement("title", "SHADOW"),
            new XElement("musicFileName", "shadow.wav")));
        doc.Save(path);

        var resaved = Path_("shadow.resaved.rscp");
        ser.Save(resaved, ser.Load(path));

        var reloaded = ser.Load(resaved);
        Assert.Equal(original.Chart.SongName, reloaded.Chart.SongName);
        Assert.Equal(original.AudioFileName, reloaded.AudioFileName);

        var root = XDocument.Load(resaved).Root!;
        var names = root.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal("futureFeatureBlock", names[^1]);
        Assert.True(names.IndexOf("title") < names.IndexOf("futureFeatureBlock"));
    }

    /// <summary>
    ///     Residue has to survive a SNAPSHOT too, or the first undo-able edit
    ///     after opening a newer build's project deletes what the reader just
    ///     went to the trouble of keeping. <see cref="ChartProjectCloner" /> is
    ///     the single snapshot source of truth (undo, torn-save protection,
    ///     crash recovery), so cloning is the check.
    /// </summary>
    [Fact]
    public void ASnapshotCarriesTheResidue()
    {
        var project = RichProjectFactory.Build();
        Assert.NotEmpty(project.UnknownRscpNodes);

        var clone = ChartProjectCloner.Clone(project);

        Assert.Equal(project.UnknownRscpNodes, clone.UnknownRscpNodes);
    }

    // ------------------------------------------------- the known-name registry

    /// <summary>
    ///     The writer-side lint. Every root element this build emits must be
    ///     registered, or it is captured as residue on the next load — and then
    ///     written twice.
    /// </summary>
    [Fact]
    public void EveryRootElementTheWriterEmits_IsRegisteredInTheContract()
    {
        var path = Path_("registry.rscp");
        new RscpChartSerializer().Save(path, OwnOutput());

        var root = XDocument.Load(path).Root!;
        var emitted = root.Elements().Select(e => e.Name.LocalName).ToList();

        Assert.True(emitted.Count >= 25,
            $"Only {emitted.Count} root elements were written — the rich fixture stopped reaching the writer.");

        var unregistered = emitted.Where(n => !RscpFormatContract.KnownRootElements.Contains(n)).Distinct().ToList();
        Assert.True(unregistered.Count == 0,
            "RscpWriter emits root elements that RscpFormatContract.KnownRootElements does not list, so the next "
            + "load captures them as a newer build's residue and the save after that writes each one twice:\n  "
            + string.Join("\n  ", unregistered));
    }

    /// <summary>The reader-side half: our own output is fully understood.</summary>
    [Fact]
    public void AProjectThisBuildWrote_ComesBackWithNoResidue()
    {
        var ser = new RscpChartSerializer();
        var path = Path_("noresidue.rscp");
        ser.Save(path, OwnOutput());

        Assert.Empty(ser.Load(path).UnknownRscpNodes);
    }

    /// <summary>
    ///     A frozen claim in the doc: the <c>&lt;project&gt;</c> root carries no
    ///     attributes, which is what lets the residue be a list of ELEMENTS with
    ///     no attribute half. A future build that wants a root attribute has to
    ///     come here, read the promise, and decide deliberately.
    /// </summary>
    [Fact]
    public void TheProjectRootCarriesNoAttributes()
    {
        var path = Path_("rootattrs.rscp");
        new RscpChartSerializer().Save(path, OwnOutput());

        var root = XDocument.Load(path).Root!;
        Assert.Equal("project", root.Name.LocalName);
        Assert.False(root.HasAttributes,
            "The root grew an attribute. Unknown ROOT ATTRIBUTES are not preserved (only root elements are), so "
            + "adding one silently creates a class of data an older build will drop — update "
            + "docs/RSCP-FORMAT-GUARANTEES.md and ChartProject.UnknownRscpNodes together, or don't add it.");
    }

    /// <summary>
    ///     The doc names the frozen root vocabulary; the code enforces it. Keeping
    ///     the two set-equal is what stops the freeze from being prose — a field
    ///     removed from the code without the doc, or promised in the doc without
    ///     the code, fails here.
    /// </summary>
    [Fact]
    public void TheGuaranteesDoc_FreezesExactlyTheRootElementsTheContractKnows()
    {
        var docPath = Path.Combine(TestPaths.RepoRoot(), "docs", "RSCP-FORMAT-GUARANTEES.md");
        Assert.True(File.Exists(docPath), $"{docPath} is missing — the promise IS the deliverable.");

        var text = File.ReadAllText(docPath);
        const string begin = "<!-- frozen-root-elements: begin -->";
        const string end = "<!-- frozen-root-elements: end -->";
        var from = text.IndexOf(begin, StringComparison.Ordinal);
        var to = text.IndexOf(end, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from,
            "The frozen-root-elements block markers are gone from docs/RSCP-FORMAT-GUARANTEES.md. They are what "
            + "makes the freeze checkable; do not delete them to silence this test.");

        var listed = System.Text.RegularExpressions.Regex
            .Matches(text[(from + begin.Length)..to], "`([A-Za-z][A-Za-z0-9]*)`")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missingFromDoc = RscpFormatContract.KnownRootElements.Except(listed).OrderBy(n => n, StringComparer.Ordinal);
        var missingFromCode = listed.Except(RscpFormatContract.KnownRootElements).OrderBy(n => n, StringComparer.Ordinal);

        Assert.True(!missingFromDoc.Any() && !missingFromCode.Any(),
            "docs/RSCP-FORMAT-GUARANTEES.md and RscpFormatContract.KnownRootElements disagree about the frozen "
            + "root vocabulary.\n  not promised in the doc: " + string.Join(", ", missingFromDoc)
            + "\n  promised but not known to the code: " + string.Join(", ", missingFromCode));
    }

    // ------------------------------------------------------- the writer stamp

    [Fact]
    public void EverySave_StampsTheWritingBuild()
    {
        var path = Path_("stamp.rscp");
        new RscpChartSerializer().Save(path, OwnOutput());

        var stamp = RscpFormatContract.ReadWriterVersion(XDocument.Load(path));
        Assert.Equal(RscpFormatContract.CurrentWriterVersion, stamp);
        Assert.False(RscpFormatContract.IsNewerThanCurrent(stamp),
            "A file this build just wrote must never read as newer than this build.");
    }

    [Fact]
    public void TheStampIsNotResidue()
    {
        Assert.Contains(RscpFormatContract.WriterVersionElement, RscpFormatContract.KnownRootElements);

        var doc = XDocument.Parse(
            $"<project><{RscpFormatContract.WriterVersionElement}>0.48.0" +
            $"</{RscpFormatContract.WriterVersionElement}></project>");
        Assert.Empty(RscpFormatContract.CaptureUnknownRootNodes(doc));
    }

    [Theory]
    // Absent, and everything that is not a version: never a warning.
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not-a-version", false)]
    [InlineData("99", false)] // no dot: rscharter-shaped garbage, not a version
    // A pre-stamp file from an OLDER build has no stamp at all — same as absent.
    [InlineData("0.0.1", false)]
    [InlineData("999.0.0", true)]
    [InlineData("999.0.0+deadbeef", true)] // SourceLink build metadata
    [InlineData("999.0.0-beta.2", true)] // pre-release suffix
    public void IsNewerThanCurrent_OnlyFiresForAVersionThatReallyIsAhead(string? stamp, bool expected) => Assert.Equal(expected, RscpFormatContract.IsNewerThanCurrent(stamp));
}
