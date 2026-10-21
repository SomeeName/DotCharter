using System.Globalization;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.MoverPhrases;

/// <summary>
///     <see cref="RsXmlPhraseMover" /> against DLC Builder's <c>PhraseMover.improve</c>: the
///     iteration and the section on it move to the N-th note, an anchor on the old time is
///     copied to the new one when none is there and removed when no note is left on it, and
///     only levels up to the phrase's maxDifficulty are touched.
/// </summary>
public sealed class RsXmlPhraseMoverTests
{
    [Fact]
    public void Apply_MovesTheIteration_ToTheNthNote_CountingTheOneOnTheBeat()
    {
        var doc = Song("moveR2", notes: [1000, 1250, 1500]);

        RsXmlPhraseMover.Apply(doc);

        Assert.Equal(1250, IterationTime(doc));
    }

    [Fact]
    public void Apply_MovesTheSectionOnTheOldTime_AndNoOther()
    {
        var doc = Song("moveR2", notes: [1250, 1500], extraSections: [("outro", 3000)]);

        RsXmlPhraseMover.Apply(doc);

        Assert.Equal([1500, 3000], SectionTimes(doc));
    }

    [Fact]
    public void Apply_MovesTheAnchor_AndRemovesTheOldOneWhenNoNoteIsLeftOnIt()
    {
        var doc = Song("moveR1", notes: [1250, 1500], anchors: [(1000, 5)]);

        RsXmlPhraseMover.Apply(doc);

        Assert.Equal([(1250, 5)], Anchors(doc, 0));
    }

    [Fact]
    public void Apply_KeepsTheOldAnchor_WhenANoteIsStillOnIt()
    {
        var doc = Song("moveR2", notes: [1000, 1250], anchors: [(1000, 5)]);

        RsXmlPhraseMover.Apply(doc);

        Assert.Equal([(1000, 5), (1250, 5)], Anchors(doc, 0));
    }

    [Fact]
    public void Apply_KeepsTheOldAnchor_WhenAChordIsStillOnIt()
    {
        var doc = Song("moveR2", notes: [1250], chords: [1000], anchors: [(1000, 5)]);

        RsXmlPhraseMover.Apply(doc);

        Assert.Equal([(1000, 5), (1250, 5)], Anchors(doc, 0));
    }

    [Fact]
    public void Apply_LeavesAnchorsAlone_WhenTheNewTimeAlreadyHasOne()
    {
        var doc = Song("moveR1", notes: [1250], anchors: [(1000, 5), (1250, 7)]);

        RsXmlPhraseMover.Apply(doc);

        Assert.Equal([(1000, 5), (1250, 7)], Anchors(doc, 0));
    }

    [Fact]
    public void Apply_CountsInTheLevelAtMaxDifficulty_AndMovesAnchorsOnlyUpToIt()
    {
        // Level 0 has a note at 1100 that level 1 (the phrase's max) does not.
        var doc = Song("moveR1", notes: [1100, 1300], maxDifficulty: 1, anchors: [(1000, 3)],
            secondLevel: (notes: [1300], anchors: [(1000, 5)]), thirdLevelAnchors: [(1000, 9)]);

        RsXmlPhraseMover.Apply(doc);

        Assert.Equal(1300, IterationTime(doc));
        Assert.Equal([(1300, 3)], Anchors(doc, 0));
        Assert.Equal([(1300, 5)], Anchors(doc, 1));
        Assert.Equal([(1000, 9)], Anchors(doc, 2)); // above maxDifficulty: untouched
    }

    [Fact]
    public void Apply_LeavesAnUnresolvableMoverInPlace_AndReportsIt()
    {
        var doc = Song("moveR3", notes: [1250]);
        var unresolved = new List<MoverMove>();

        Assert.Empty(RsXmlPhraseMover.Apply(doc, unresolved: unresolved));

        Assert.Equal(1000, IterationTime(doc));
        Assert.Single(unresolved);
    }

    [Fact]
    public void ResolveForImport_RenamesThePhrase_AfterItsSection()
    {
        var doc = Song("moveR2", notes: [1250, 1500]);

        RsXmlPhraseMover.ResolveForImport(doc);

        Assert.Equal(1500, IterationTime(doc));
        Assert.Equal("chorus", PhraseName(doc, 1));
    }

    [Fact]
    public void ResolveForImport_DoesNotMoveAnIterationThatIsAlreadyOffTheBeat()
    {
        // An XML converted from a package DLC Builder built: the iteration is already on
        // its note under the same name. Moving it again would push it two more notes on.
        var doc = Song("moveR2", notes: [1250, 1500, 1750], iterationMs: 1500);

        RsXmlPhraseMover.ResolveForImport(doc);

        Assert.Equal(1500, IterationTime(doc));
        // ...but it loses the name that would move it again on the way out.
        Assert.Equal("chorus", PhraseName(doc, 1));
    }

    [Fact]
    public void ResolveForImport_KeepsTheMoverName_OfAPhraseThatCouldNotMove()
    {
        var doc = Song("moveR3", notes: [1250]);

        RsXmlPhraseMover.ResolveForImport(doc);

        Assert.Equal(1000, IterationTime(doc));
        Assert.Equal("moveR3", PhraseName(doc, 1));
    }

    [Fact]
    public void ToMoverForm_WritesTheIterationAndSectionOnTheBeatBefore_AndApplyPutsThemBack()
    {
        var doc = Song("chorus", notes: [1000, 1250, 1500], iterationMs: 1250, anchors: [(1250, 5)]);

        var moves = RsXmlPhraseMover.ToMoverForm(doc);

        Assert.Single(moves);
        Assert.Equal(1000, IterationTime(doc));
        Assert.Equal([1000], SectionTimes(doc));
        Assert.Equal("moveR2", PhraseName(doc, 1));

        RsXmlPhraseMover.Apply(doc);

        Assert.Equal(1250, IterationTime(doc));
        Assert.Equal([1250], SectionTimes(doc));
        Assert.Equal([(1250, 5)], Anchors(doc, 0));
    }

    [Fact]
    public void ToMoverForm_KeepsTheRealTime_WhenNoNoteStartsThere()
    {
        var doc = Song("chorus", notes: [1000, 1500], iterationMs: 1250);

        Assert.Empty(RsXmlPhraseMover.ToMoverForm(doc));
        Assert.Equal(1250, IterationTime(doc));
        Assert.Equal("chorus", PhraseName(doc, 1));
    }

    [Fact]
    public void ToMoverForm_KeepsTheRealTime_WhenDlcBuilderWouldMoveAnAnchor()
    {
        // An anchor on the beat and none on the note: DLC Builder would move it.
        var doc = Song("chorus", notes: [1250], iterationMs: 1250, anchors: [(1000, 5)]);

        Assert.Empty(RsXmlPhraseMover.ToMoverForm(doc));
        Assert.Equal(1250, IterationTime(doc));
    }

    private static XDocument Song(string phrase, int[] notes, int[]? chords = null, int iterationMs = 1000,
        int maxDifficulty = 0, (int Ms, int Fret)[]? anchors = null, (string Name, int Ms)[]? extraSections = null,
        (int[] notes, (int Ms, int Fret)[] anchors)? secondLevel = null, (int Ms, int Fret)[]? thirdLevelAnchors = null)
    {
        var levels = new List<XElement> { Level(0, notes, chords ?? [], anchors ?? []) };
        if (secondLevel is { } second) levels.Add(Level(1, second.notes, [], second.anchors));
        if (thirdLevelAnchors is { } third) levels.Add(Level(2, [], [], third));

        var sections = new List<(string Name, int Ms)> { ("chorus", iterationMs) };
        sections.AddRange(extraSections ?? []);

        return new XDocument(new XElement("song",
            new XElement("ebeats", Enumerable.Range(0, 8).Select(i =>
                new XElement("ebeat", new XAttribute("time", T(i * 1000)), new XAttribute("measure", i)))),
            new XElement("phrases",
                new XElement("phrase", new XAttribute("name", "COUNT"), new XAttribute("maxDifficulty", 0)),
                new XElement("phrase", new XAttribute("name", phrase), new XAttribute("maxDifficulty", maxDifficulty))),
            new XElement("phraseIterations",
                new XElement("phraseIteration", new XAttribute("time", T(0)), new XAttribute("phraseId", 0)),
                new XElement("phraseIteration", new XAttribute("time", T(iterationMs)), new XAttribute("phraseId", 1))),
            new XElement("sections", sections.Select(s =>
                new XElement("section", new XAttribute("name", s.Name), new XAttribute("number", 1),
                    new XAttribute("startTime", T(s.Ms))))),
            new XElement("levels", levels)));
    }

    private static XElement Level(int difficulty, int[] notes, int[] chords, (int Ms, int Fret)[] anchors) =>
        new("level", new XAttribute("difficulty", difficulty),
            new XElement("notes", notes.Select(n => new XElement("note", new XAttribute("time", T(n))))),
            new XElement("chords", chords.Select(c => new XElement("chord", new XAttribute("time", T(c))))),
            new XElement("anchors", anchors.Select(a => new XElement("anchor", new XAttribute("time", T(a.Ms)),
                new XAttribute("fret", a.Fret), new XAttribute("width", "4.000")))));

    private static string T(int ms) => (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    private static int Ms(XElement el, string attribute) =>
        (int)Math.Round(double.Parse(el.Attribute(attribute)!.Value, CultureInfo.InvariantCulture) * 1000);

    private static int IterationTime(XDocument doc) =>
        Ms(doc.Root!.Element("phraseIterations")!.Elements().Last(), "time");

    private static List<int> SectionTimes(XDocument doc) =>
        [.. doc.Root!.Element("sections")!.Elements().Select(s => Ms(s, "startTime"))];

    private static string PhraseName(XDocument doc, int id) =>
        doc.Root!.Element("phrases")!.Elements().ElementAt(id).Attribute("name")!.Value;

    private static List<(int, int)> Anchors(XDocument doc, int level) =>
    [
        .. doc.Root!.Element("levels")!.Elements().ElementAt(level).Element("anchors")!.Elements()
            .Select(a => (Ms(a, "time"), int.Parse(a.Attribute("fret")!.Value, CultureInfo.InvariantCulture)))
    ];
}
