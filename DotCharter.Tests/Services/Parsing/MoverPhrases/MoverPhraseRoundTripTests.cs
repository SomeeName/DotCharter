using System.Xml.Linq;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.MoverPhrases;

/// <summary>
///     A phrase that starts on a note between two beats, through every format: the moveR
///     form EOF and DLC Builder read, the real time the package uses, and back — with the
///     phrase, section and anchor times unchanged.
/// </summary>
public sealed class MoverPhraseRoundTripTests : IDisposable
{
    private const double BeatMs = 500;
    private static readonly FractionalPosition HalfPastBeat4 = new(4, new Fraction(1, 2)); // 2250 ms

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-mover-" + Guid.NewGuid().ToString("N"));

    public MoverPhraseRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Xml_MoverForm_ThroughDlcBuildersPhraseMover_ComesBackUnchanged()
    {
        var project = Project();
        var doc = new RsXmlSongWriter { MoverPhrases = true }.Write(project);

        // Written on the beat before, as moveR2 (the note ON beat 4 is the 1st).
        var iteration = IterationNamed(doc, "moveR2");
        Assert.Equal("2.000", iteration.Attribute("time")!.Value);
        Assert.Contains(doc.Root!.Element("sections")!.Elements(),
            s => s.Attribute("name")!.Value == "chorus" && s.Attribute("startTime")!.Value == "2.000");

        // DLC Builder's processing, then the import — which must not move it a second time.
        RsXmlPhraseMover.Apply(doc);
        Assert.Equal("2.250", iteration.Attribute("time")!.Value);
        var back = new RsXmlSongReader().ReadDocument(doc, "mover.xml");

        Assert.Equal(Shape(project), Shape(back));
        Assert.Contains("chorus", back.Arrangements[0].Phrases);
        Assert.DoesNotContain(back.Arrangements[0].Phrases, p => p.StartsWith("mover", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Xml_MoverForm_ReadStraightBack_ComesBackUnchanged()
    {
        var project = Project();
        var doc = new RsXmlSongWriter { MoverPhrases = true }.Write(project);

        Assert.Equal(Shape(project), Shape(new RsXmlSongReader().ReadDocument(doc, "mover.xml")));
    }

    [Fact]
    public void Xml_ByDefault_WritesTheRealTime()
    {
        var project = Project();
        var doc = new RsXmlSongWriter().Write(project);

        Assert.Equal("2.250", IterationNamed(doc, "chorus").Attribute("time")!.Value);
        Assert.DoesNotContain(doc.Root!.Element("phrases")!.Elements(),
            p => p.Attribute("name")!.Value.StartsWith("mover", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Shape(project), Shape(new RsXmlSongReader().ReadDocument(doc, "real.xml")));
    }

    [Fact]
    public void Sng_UsesTheRealTime()
    {
        var project = Project();
        var arrangement = project.Arrangements[0];
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(HalfPastBeat4, 1));

        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, 7000).Sng;

        Assert.Contains(sng.PhraseIterations, pi => Math.Abs(pi.StartTime - 2.25f) < 0.0005f);
        Assert.Contains(sng.Sections, s => s.Name == "chorus" && Math.Abs(s.StartTime - 2.25f) < 0.0005f);
    }

    [Fact]
    public void DotCharter_ToEof_AndBack_IsUnchanged_AndTheEofCarriesMoveROnTheBeat()
    {
        var project = Project();

        var eof = ExportEof(project, "a.eof");
        var (events, back) = ImportEof(eof);

        Assert.Contains(events, e => e is { Text: "moveR2", BeatPos: 4, IsMs: false });
        Assert.Contains(events, e => e is { Text: "chorus", BeatPos: 4, IsMs: false });
        Assert.Equal(Shape(project), Shape(back));
        Assert.Contains(back.Arrangements[0].EventPoints, ep => ep.Phrase == "chorus" && ep.Position == HalfPastBeat4);
    }

    [Fact]
    public void Eof_ToDotCharter_AndBack_IsUnchanged()
    {
        // An EOF chart: moveR2 and its section on beat 4, the hand position on the note.
        var project = Project(phraseAt: new FractionalPosition(4), phraseName: "moveR2", separateSection: true);
        var first = ExportEof(project, "first.eof");
        var (firstEvents, imported) = ImportEof(first);

        var phrase = Assert.Single(imported.Arrangements[0].EventPoints, ep => ep.Phrase == "chorus");
        Assert.Equal(HalfPastBeat4, phrase.Position);

        var (secondEvents, reimported) = ImportEof(ExportEof(imported, "second.eof"));

        Assert.Equal(Events(firstEvents), Events(secondEvents));
        Assert.Equal(Shape(imported), Shape(reimported));
    }

    [Fact]
    public void EofImport_MovesTheAnchor_AndDropsTheOldOneWhenNoNoteIsLeftOnIt()
    {
        var project = Project(phraseAt: new FractionalPosition(4), phraseName: "moveR1", noteOnBeat4: false,
            anchorAt: new FractionalPosition(4));

        var (_, back) = ImportEof(ExportEof(project, "a.eof"));

        var anchors = back.Arrangements[0].Levels[0].Anchors.Select(a => (a.Position, a.Fret)).ToList();
        Assert.Equal([(new FractionalPosition(0), 1), (HalfPastBeat4, 5)], anchors);
    }

    [Fact]
    public void EofImport_KeepsTheOldAnchor_WhenANoteIsStillOnIt()
    {
        var project = Project(phraseAt: new FractionalPosition(4), phraseName: "moveR2",
            anchorAt: new FractionalPosition(4));

        var (_, back) = ImportEof(ExportEof(project, "a.eof"));

        var anchors = back.Arrangements[0].Levels[0].Anchors.Select(a => (a.Position, a.Fret)).ToList();
        Assert.Equal([(new FractionalPosition(0), 1), (new FractionalPosition(4), 5), (HalfPastBeat4, 5)], anchors);
    }

    [Fact]
    public void EofImport_GivesThePhraseBackItsOwnName()
    {
        var project = Project(phraseAt: new FractionalPosition(4), phraseName: "riff moveR2", separateSection: true);

        var (_, back) = ImportEof(ExportEof(project, "a.eof"));

        var arrangement = back.Arrangements[0];
        Assert.Equal(HalfPastBeat4, Assert.Single(arrangement.EventPoints, ep => ep.Phrase == "riff").Position);
        Assert.DoesNotContain("riff moveR2", arrangement.Phrases);
        Assert.Contains("riff", arrangement.Phrases);
    }

    [Fact]
    public void EofImport_LeavesAFloatingMoveRWhereItIs()
    {
        // EOF reads moveR only off a phrase attached to a beat; this one floats at 2125 ms.
        var floating = new FractionalPosition(4, new Fraction(1, 4));
        var project = Project(phraseAt: floating, phraseName: "moveR1", noteOnBeat4: false);

        var (events, back) = ImportEof(ExportEof(project, "a.eof"));

        Assert.Contains(events, e => e is { Text: "moveR1", IsMs: true });
        Assert.Equal(floating, Assert.Single(back.Arrangements[0].EventPoints, ep => ep.Phrase == "moveR1").Position);
    }

    /// <summary>
    ///     16 beats of 500 ms. Notes on every beat up to 7 and at 4½; phrase + section
    ///     "intro" on beat 0 and "chorus" at 4½ (2250 ms); anchors on 0 and on the note.
    /// </summary>
    internal static ChartProject Project(FractionalPosition? phraseAt = null, string phraseName = "chorus",
        bool separateSection = false, bool noteOnBeat4 = true, FractionalPosition? anchorAt = null)
    {
        var project = new ChartProject { AudioFileName = "song.wav" };
        for (var i = 0; i < 16; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * BeatMs, Bpm = 120, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, NoteDenominator = 4
            });
        }

        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = 6,
            Tuning = ["E", "A", "D", "G", "B", "E"]
        };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        arrangement.Levels.Add(level);

        var notes = Enumerable.Range(0, 8).Where(b => b != 4 || noteOnBeat4).Select(b => new FractionalPosition(b))
            .Append(HalfPastBeat4).Order();
        foreach (var position in notes) level.Notes.Add(new GuitarNote { Position = position, StringIndex = 2, Fret = 5 });

        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 1));
        level.Anchors.Add(new FretAnchor(anchorAt ?? HalfPastBeat4, 5));

        var at = phraseAt ?? HalfPastBeat4;
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro", Section = "intro" });
        if (separateSection)
        {
            arrangement.EventPoints.Add(new EventPoint { Position = at, Section = "chorus" });
            arrangement.EventPoints.Add(new EventPoint { Position = at, Phrase = phraseName });
        }
        else
        {
            arrangement.EventPoints.Add(new EventPoint { Position = at, Phrase = phraseName, Section = "chorus" });
        }

        arrangement.Phrases.AddRange(["intro", phraseName]);
        project.Arrangements.Add(arrangement);
        return project;
    }

    /// <summary>Phrase, section and anchor times (ms) — the writers' own END and closing
    /// noguitar left out, since the chart never had them.</summary>
    private static string Shape(ChartProject project)
    {
        var arrangement = project.Arrangements[0];
        var beats = project.Beats;
        var phrases = PhraseMarkers.All(arrangement).Where(m => m.Name != "END")
            .Select(m => m.Position.GetPositionMs(beats));
        var sections = arrangement.EventPoints.Where(ep => ep.Section is not ("" or "noguitar"))
            .Select(ep => ep.Position.GetPositionMs(beats)).Order();
        var anchors = arrangement.Levels.OrderByDescending(l => l.DifficultyIndex).First().Anchors
            .Select(a => $"{a.Position.GetPositionMs(beats)}@{a.Fret}");
        return $"phrases {string.Join(",", phrases)} | sections {string.Join(",", sections)} | anchors {string.Join(",", anchors)}";
    }

    private static XElement IterationNamed(XDocument doc, string name)
    {
        var phrases = doc.Root!.Element("phrases")!.Elements().ToList();
        var id = phrases.FindIndex(p => p.Attribute("name")!.Value == name);
        Assert.True(id >= 0, $"no phrase named {name}");
        return doc.Root.Element("phraseIterations")!.Elements()
            .Single(pi => pi.Attribute("phraseId")!.Value == id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private string ExportEof(ChartProject project, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, EofExporter.Export(project).Bytes);
        return path;
    }

    private static (List<EofTextEventInfo> Events, ChartProject Project) ImportEof(string path)
    {
        var importer = new EofImporter();
        var result = importer.ImportTracksWithMetadata(path, importer.GetAvailableTracks(path).Select(t => t.Id).ToList());
        var project = new ChartProject { AudioFileName = "song.wav" };
        foreach (var beat in EofImporter.BuildBeatMarkers(result.Metadata)) project.Beats.Add(beat);
        project.Arrangements.Add(Assert.Single(result.Arrangements));
        return (result.Metadata.TextEvents, project);
    }

    private static List<string> Events(IEnumerable<EofTextEventInfo> events) =>
    [
        .. events.Select(e => $"{e.Text}@{e.BeatPos}{(e.IsMs ? "ms" : "")} t{e.Track} f{e.Flags}").Order(StringComparer.Ordinal)
    ];
}
