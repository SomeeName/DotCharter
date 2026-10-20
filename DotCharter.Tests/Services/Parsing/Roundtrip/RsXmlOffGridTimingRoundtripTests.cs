using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.Roundtrip;

/// <summary>
///     #325: an RS-XML stores whole milliseconds, and a note an EOF or Rocksmith
///     chart placed between DotCharter's 1/192-beat grid points used to come back
///     moved. The import snapped it to 1/192 beat (up to 2.6 ms at 60 BPM) and the
///     writer then TRUNCATED the ms (<c>(int)</c>), adding up to 1 ms of early
///     bias. Read → write must now give back the same millisecond for every time
///     attribute, on the grid or off it.
/// </summary>
public sealed class RsXmlOffGridTimingRoundtripTests
{
    /// <summary>
    ///     Every whole millisecond across one beat, at three tempos. Before the fix
    ///     roughly four in five of them moved.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(40)]
    public void EveryMillisecondInABeat_RoundTripsExactly(int bpm)
    {
        var beatMs = 60_000 / bpm;
        var noteTimes = Enumerable.Range(beatMs, beatMs).ToList();
        var notes = noteTimes.Select((ms, i) => Note(ms, i % 6)).ToList();

        var source = SongXml(bpm, notes);
        var written = RoundTrip(source);

        var expected = Times(XDocument.Parse(source), "note", "time");
        var actual = Times(written, "note", "time");
        Assert.Equal(expected.Count, actual.Count);

        var moved = expected.Zip(actual).Where(p => p.First != p.Second).ToList();
        var worst = moved.Count == 0 ? 0 : moved.Max(p => Math.Abs(p.First - p.Second));
        Assert.True(moved.Count == 0,
            $"{moved.Count} of {expected.Count} notes moved at {bpm} BPM, worst by {worst} ms " +
            $"(first: {moved.FirstOrDefault().First} -> {moved.FirstOrDefault().Second})");
    }

    /// <summary>
    ///     The issue's own example at 60 BPM, through every element type that
    ///     carries a time: notes and their sustains, a chord and its chord-note
    ///     sustain, bend points, an anchor, a hand shape and a section.
    /// </summary>
    [Fact]
    public void OffGridTimesOnEveryElement_RoundTripExactly()
    {
        var notes = new List<string>
        {
            Note(1003, 0, sustainMs: 664),
            Note(1167, 1),
            Note(1501, 2, sustainMs: 333,
                bendValues: [(1551, "1.000"), (1602, "0.000")])
        };
        var chords = new List<string> { Chord(2003, sustainMs: 514) };
        var anchors = new List<string> { $"<anchor time=\"{Sec(1003)}\" fret=\"3\" width=\"4.000\" />" };
        var handShapes = new List<string>
        {
            $"<handShape chordId=\"0\" startTime=\"{Sec(2003)}\" endTime=\"{Sec(2517)}\" />"
        };
        var sections = new List<string>
        {
            $"<section name=\"verse\" number=\"1\" startTime=\"{Sec(1003)}\" />"
        };

        var source = XDocument.Parse(SongXml(60, notes, chords, anchors, handShapes, sections));
        var written = RoundTrip(source.ToString());

        Assert.Equal([1003, 1167, 1501], Times(written, "note", "time"));
        Assert.Equal([664, 333], Times(written, "note", "sustain"));
        Assert.Equal([1551, 1602], Times(written, "bendValue", "time"));
        Assert.Equal([2003], Times(written, "chord", "time"));
        Assert.Equal(Times(source, "chordNote", "sustain"), Times(written, "chordNote", "sustain"));
        Assert.Equal([1003], Times(written, "anchor", "time"));
        Assert.Equal([2003], Times(written, "handShape", "startTime"));
        Assert.Equal([2517], Times(written, "handShape", "endTime"));
        Assert.Contains(1003, Times(written, "section", "startTime"));
    }

    /// <summary>
    ///     On-grid content must STAY on the grid: a triplet written as its rounded
    ///     ms (166.67 → 0.167) has to come back as exactly 1/3 beat, not as a
    ///     1/19200-beat neighbour of it.
    /// </summary>
    [Fact]
    public void ARoundedTripletImports_BackOnItsOwnGridPoint()
    {
        var project = new RsXmlSongReader().ReadDocument(
            XDocument.Parse(SongXml(120, [Note(500 + 167, 0)])), "triplet_lead.xml");

        var note = Assert.Single(project.Arrangements[0].Levels[0].Notes);
        Assert.Equal(new FractionalPosition(1, new Fraction(1, 3)), note.Position);
    }

    /// <summary>The writer half alone: 166.67 ms is 0.167 s, not the truncated 0.166.</summary>
    [Fact]
    public void Writer_RoundsToTheNearestMillisecond()
    {
        var project = new ChartProject();
        for (var i = 0; i < 4; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i == 0 });

        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0, new Fraction(1, 3)), // 166.67 ms
            Sustain = new FractionalPosition(0, new Fraction(2, 3)), // ends at 500.00 ms
            StringIndex = 0,
            Fret = 5
        });
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);

        var written = new RsXmlSongWriter().Write(project);
        var note = written.Descendants("note").Single();

        Assert.Equal("0.167", note.Attribute("time")!.Value);
        Assert.Equal("0.333", note.Attribute("sustain")!.Value);
    }

    // ---- helpers ------------------------------------------------------------

    private static XDocument RoundTrip(string xml)
    {
        var project = new RsXmlSongReader().ReadDocument(XDocument.Parse(xml), "offgrid_lead.xml");
        return new RsXmlSongWriter().Write(project);
    }

    private static List<int> Times(XDocument doc, string element, string attribute) =>
        doc.Descendants(element)
            .Select(e => e.Attribute(attribute)?.Value)
            .Where(v => v != null)
            .Select(v => (int)Math.Round(double.Parse(v!, CultureInfo.InvariantCulture) * 1000))
            .ToList();

    private static string Sec(int ms) => (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    private static string Note(int timeMs, int stringIndex, int sustainMs = 0,
        IReadOnlyList<(int TimeMs, string Step)>? bendValues = null)
    {
        var sb = new StringBuilder();
        sb.Append($"<note time=\"{Sec(timeMs)}\" string=\"{stringIndex}\" fret=\"5\"");
        if (sustainMs > 0) sb.Append($" sustain=\"{Sec(sustainMs)}\"");
        if (bendValues is not { Count: > 0 }) return sb.Append(" />").ToString();

        sb.Append(" bend=\"1\">");
        sb.Append($"<bendValues count=\"{bendValues.Count}\">");
        foreach (var (ms, step) in bendValues)
            sb.Append($"<bendValue time=\"{Sec(ms)}\" step=\"{step}\" />");
        return sb.Append("</bendValues></note>").ToString();
    }

    private static string Chord(int timeMs, int sustainMs) =>
        $"<chord time=\"{Sec(timeMs)}\" chordId=\"0\">" +
        $"<chordNote time=\"{Sec(timeMs)}\" string=\"0\" fret=\"3\" sustain=\"{Sec(sustainMs)}\" />" +
        $"<chordNote time=\"{Sec(timeMs)}\" string=\"1\" fret=\"5\" sustain=\"{Sec(sustainMs)}\" />" +
        "</chord>";

    private static string SongXml(int bpm, IReadOnlyList<string> notes, IReadOnlyList<string>? chords = null,
        IReadOnlyList<string>? anchors = null, IReadOnlyList<string>? handShapes = null,
        IReadOnlyList<string>? sections = null)
    {
        chords ??= [];
        anchors ??= [];
        handShapes ??= [];
        sections ??= [];
        var beatMs = 60_000 / bpm;
        const int beatCount = 12;

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<song version=\"8\">\n");
        sb.Append("<title>Off grid</title><arrangement>Lead</arrangement>");
        sb.Append("<offset>0.000</offset><startBeat>0.000</startBeat><songLength>60.000</songLength>");
        sb.Append("<tuning string0=\"0\" string1=\"0\" string2=\"0\" string3=\"0\" string4=\"0\" string5=\"0\" />\n");
        sb.Append("<phrases count=\"1\"><phrase disparity=\"0\" ignore=\"0\" maxDifficulty=\"0\" name=\"verse\" solo=\"0\" /></phrases>");
        sb.Append("<phraseIterations count=\"1\"><phraseIteration time=\"0.000\" phraseId=\"0\" /></phraseIterations>");
        sb.Append("<chordTemplates count=\"1\"><chordTemplate chordName=\"\" displayName=\"\" " +
                  "fret0=\"3\" fret1=\"5\" finger0=\"1\" finger1=\"3\" /></chordTemplates>\n");
        sb.Append($"<ebeats count=\"{beatCount}\">");
        for (var i = 0; i < beatCount; i++)
            sb.Append($"<ebeat time=\"{Sec(i * beatMs)}\" measure=\"{(i % 4 == 0 ? (i / 4 + 1).ToString(CultureInfo.InvariantCulture) : "-1")}\" />");
        sb.Append("</ebeats>\n");
        sb.Append($"<sections count=\"{sections.Count}\">{string.Concat(sections)}</sections>\n");
        sb.Append("<levels count=\"1\"><level difficulty=\"0\">\n");
        sb.Append($"<notes count=\"{notes.Count}\">{string.Join("\n", notes)}</notes>\n");
        sb.Append($"<chords count=\"{chords.Count}\">{string.Concat(chords)}</chords>\n");
        sb.Append($"<anchors count=\"{anchors.Count}\">{string.Concat(anchors)}</anchors>\n");
        sb.Append($"<handShapes count=\"{handShapes.Count}\">{string.Concat(handShapes)}</handShapes>\n");
        sb.Append("</level></levels>\n</song>\n");
        return sb.ToString();
    }
}
