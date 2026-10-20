using System.Xml.Linq;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>
///     #904: a 7-/8-string guitar's RS XML follows the rs7string spec's order — indices
///     0..5 are the stock E A D G B e, the extra strings come after (6 = low B, 7 = low
///     F#) — while the chart keeps counting lowest string first. The writer and reader
///     renumber through <see cref="Rs7StringLayout" /> at the XML boundary and nowhere else.
/// </summary>
public sealed class RsXmlRs7StringOrderTests
{
    private const int BeatMs = 500;

    /// <summary>The rs7string XML spec's section 4 example (our own spec text), with the
    /// mandatory song elements it omits added around it.</summary>
    private const string SpecExample = """
        <song version="8">
          <title>Spec</title>
          <arrangement>Lead</arrangement>
          <tuning strings="7" string0="0" string1="0" string2="0" string3="0" string4="0" string5="0" string6="0" />
          <chordTemplates count="1">
            <chordTemplate chordName="C#5" displayName="C#5" finger0="3" fret0="4" finger6="1" fret6="2" />
          </chordTemplates>
          <ebeats count="3">
            <ebeat time="0.000" measure="1" />
            <ebeat time="10.000" measure="-1" />
            <ebeat time="20.000" measure="-1" />
          </ebeats>
          <levels count="1">
            <level difficulty="0">
              <notes count="1">
                <note time="10.000" string="6" fret="3" sustain="0.000" />
              </notes>
              <chords count="1">
                <chord time="12.000" chordId="0" strum="down">
                  <chordNote time="12.000" string="0" fret="4" sustain="0.000" />
                  <chordNote time="12.000" string="6" fret="2" sustain="0.000" />
                </chord>
              </chords>
              <anchors count="1"><anchor time="10.000" fret="2" width="4.000" /></anchors>
              <handShapes count="1"><handShape chordId="0" startTime="12.000" endTime="12.500" /></handShapes>
            </level>
          </levels>
        </song>
        """;

    [Fact]
    public void The_spec_example_imports_in_chart_order()
    {
        var arr = new RsXmlSongReader().ReadDocument(XDocument.Parse(SpecExample), "spec.xml").Arrangements[0];

        Assert.Equal(7, arr.StringCount);
        Assert.Equal(["B", "E", "A", "D", "G", "B", "E"], arr.Tuning);
        var level = arr.Levels[0];
        var note = Assert.Single(level.Notes);
        Assert.Equal(0, note.StringIndex); // XML string 6 = the low B = the chart's string 0
        Assert.Equal(3, note.Fret);
        var chord = Assert.Single(level.Chords);
        Assert.Equal([(0, 2), (1, 4)], chord.ChordNotes.OrderBy(c => c.StringIndex).Select(c => (c.StringIndex, c.Fret)));
        var template = Assert.Single(arr.ChordTemplates);
        Assert.Equal(new Dictionary<int, int> { [0] = 2, [1] = 4 }, template.Frets);
        Assert.Equal(new Dictionary<int, int> { [0] = 1, [1] = 3 }, template.Fingers);
    }

    [Fact]
    public void A_seven_string_chart_is_written_in_the_spec_order()
    {
        var project = Chart(7);
        project.Arrangements[0].TuningPitches[0] = new TuningOffset(-2); // drop-A on the B string

        var song = new RsXmlSongWriter().Write(project).Root!;

        var tuning = song.Element("tuning")!;
        Assert.Equal("7", (string?)tuning.Attribute("strings"));
        Assert.Equal("-2", (string?)tuning.Attribute("string6"));
        Assert.Equal("0", (string?)tuning.Attribute("string0"));
        Assert.Equal("0", (string?)song.Element("arrangementProperties")!.Attribute("standardTuning"));

        var notes = Notes(song);
        Assert.Contains((6, 3, 4000), notes); // the chart's string 0 (low B)
        Assert.Contains((0, 5, 6000), notes); // the chart's string 1 (low E)

        var template = Assert.Single(song.Element("chordTemplates")!.Elements("chordTemplate"));
        Assert.Equal("2", (string?)template.Attribute("fret6"));
        Assert.Equal("4", (string?)template.Attribute("fret0"));
        Assert.Equal("1", (string?)template.Attribute("finger6"));
        Assert.Equal([0, 1, 6], song.Descendants("chordNote").Select(c => (int)c.Attribute("string")!).Order());
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Charts_round_trip_through_the_xml(int strings)
    {
        var project = Chart(strings);
        var written = project.Arrangements[0];

        var doc = new RsXmlSongWriter().Write(project);
        var read = new RsXmlSongReader().ReadDocument(doc, "round.xml").Arrangements[0];

        Assert.Equal(strings, read.StringCount);
        Assert.Equal(written.TuningPitches.Select(t => t.Step), read.TuningPitches.Select(t => t.Step));
        Assert.Equal(written.Tuning, read.Tuning);
        Assert.Equal(
            written.Levels[0].Notes.Select(n => (n.StringIndex, n.Fret)).Order(),
            read.Levels[0].Notes.Select(n => (n.StringIndex, n.Fret)).Order());
        var chord = Assert.Single(read.Levels[0].Chords);
        Assert.Equal(
            written.Levels[0].Chords[0].ChordNotes.Select(c => (c.StringIndex, c.Fret)).Order(),
            chord.ChordNotes.Select(c => (c.StringIndex, c.Fret)).Order());
        var template = Assert.Single(read.ChordTemplates);
        Assert.Equal(written.ChordTemplates[0].Frets.OrderBy(k => k.Key), template.Frets.OrderBy(k => k.Key));
        Assert.Equal(written.ChordTemplates[0].Fingers.OrderBy(k => k.Key), template.Fingers.OrderBy(k => k.Key));
    }

    [Fact]
    public void Indices_zero_to_five_of_a_seven_string_xml_are_the_six_string_projection()
    {
        // The same song on a 6-string guitar (the stock strings only) and on a 7-string
        // one that adds low-B notes: the six-wide reader's view of both is identical.
        var six = new RsXmlSongWriter().Write(Chart(6)).Root!;
        var seven = new RsXmlSongWriter().Write(Chart(7)).Root!;

        Assert.Equal(
            Notes(six).Where(n => n.String <= 5).Order(),
            Notes(seven).Where(n => n.String <= 5).Order());
        var sixTemplate = six.Element("chordTemplates")!.Elements("chordTemplate").Single();
        var sevenTemplate = seven.Element("chordTemplates")!.Elements("chordTemplate").Single();
        for (var i = 0; i <= 5; i++)
        {
            Assert.Equal((string?)sixTemplate.Attribute($"fret{i}"), (string?)sevenTemplate.Attribute($"fret{i}"));
            Assert.Equal((string?)sixTemplate.Attribute($"finger{i}"), (string?)sevenTemplate.Attribute($"finger{i}"));
        }
    }

    [Fact]
    public void Xml_to_package_equals_chart_to_package_byte_for_byte()
    {
        var project = Chart(7);
        var when = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Unspecified);

        var direct = SngBytes(project.Arrangements[0], project.Beats, when);

        var doc = new RsXmlSongWriter().Write(project);
        var readProject = new RsXmlSongReader().ReadDocument(doc, "bytes.xml");
        var viaXml = SngBytes(readProject.Arrangements[0], readProject.Beats, when);

        Assert.Equal(direct, viaXml);
    }

    [Fact]
    public void A_template_keeps_a_stock_column_and_a_name_is_printable_ascii_of_30()
    {
        var project = Chart(7);
        var arr = project.Arrangements[0];
        var extraOnly = new ChordTemplate { Id = 1, Name = "Low" };
        extraOnly.Frets[0] = 3; // the chart's string 0 = XML string 6 only
        arr.ChordTemplates.Add(extraOnly);
        arr.ChordTemplates[0].Name = "C#5 é" + new string('x', 40);

        var song = new RsXmlSongWriter().Write(project).Root!;

        var templates = song.Element("chordTemplates")!.Elements("chordTemplate").ToList();
        Assert.NotEmpty(templates);
        Assert.All(templates, t => Assert.Contains(Enumerable.Range(0, 6), i => t.Attribute($"fret{i}") != null));
        Assert.All(templates, t => Assert.True(((string?)t.Attribute("chordName"))!.Length <= 30));
        Assert.All(templates, t => Assert.All(((string?)t.Attribute("chordName"))!, c => Assert.InRange(c, ' ', '~')));
    }

    [Fact]
    public void A_bass_and_a_six_string_guitar_keep_their_indices()
    {
        var project = Chart(6);
        var six = new RsXmlSongWriter().Write(project).Root!;
        Assert.Contains((0, 5, 6000), Notes(six));
        Assert.Null(six.Element("tuning")!.Attribute("strings"));

        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = 5 };
        for (var i = 0; i < 5; i++) bass.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { StringIndex = 4, Fret = 2, Position = TimelineMath.PositionFromMs(4000, project.Beats) });
        bass.Levels.Add(level);
        var bassProject = new ChartProject();
        foreach (var beat in project.Beats) bassProject.Beats.Add(beat);
        bassProject.Arrangements.Add(bass);

        var bassSong = new RsXmlSongWriter().Write(bassProject).Root!;

        Assert.Equal("5", (string?)bassSong.Element("tuning")!.Attribute("strings"));
        Assert.Contains((4, 2, 4000), Notes(bassSong));
        var read = new RsXmlSongReader().ReadDocument(new XDocument(bassSong), "bass.xml").Arrangements[0];
        Assert.Equal(4, read.Levels[0].Notes[0].StringIndex);
    }

    private static List<(int String, int Fret, int TimeMs)> Notes(XElement song) =>
    [
        .. song.Descendants("note").Select(n =>
            ((int)n.Attribute("string")!, (int)n.Attribute("fret")!, (int)Math.Round((double)n.Attribute("time")! * 1000)))
    ];

    private static byte[] SngBytes(Arrangement arrangement, IList<BeatMarker> beats, DateTime when)
    {
        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 20_000, when).Sng;
        return RocksmithSngWriter.WriteUnpacked(sng);
    }

    /// <summary>A guitar of <paramref name="strings" /> strings in the chart's lowest-first order:
    /// notes on its low E and A, a low-B note when it has a 7th string, and a chord over the same
    /// strings with fingers.</summary>
    private static ChartProject Chart(int strings)
    {
        var beats = Enumerable.Range(0, 32).Select(i => new BeatMarker
        {
            Time = i * BeatMs, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4
        }).ToList();
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = strings };
        for (var i = 0; i < strings; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Tuning = TuningUtilities.GetTuningNames(strings, [.. arrangement.TuningPitches.Select(t => t.Step)]);

        var lowE = strings - 6; // the chart index of the low E
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(TimelineMath.PositionFromMs(4000, beats), 1, 4));
        if (strings > 6)
            level.Notes.Add(new GuitarNote { StringIndex = strings - 7, Fret = 3, Position = TimelineMath.PositionFromMs(4000, beats) });
        level.Notes.Add(new GuitarNote { StringIndex = lowE, Fret = 5, Position = TimelineMath.PositionFromMs(6000, beats) });
        level.Notes.Add(new GuitarNote { StringIndex = lowE + 1, Fret = 7, Position = TimelineMath.PositionFromMs(7000, beats) });

        var template = new ChordTemplate { Id = 0, Name = "C#5" };
        template.Frets[lowE] = 4;
        template.Fingers[lowE] = 3;
        template.Frets[lowE + 1] = 6;
        template.Fingers[lowE + 1] = 4;
        if (strings > 6)
        {
            template.Frets[strings - 7] = 2;
            template.Fingers[strings - 7] = 1;
        }

        arrangement.ChordTemplates.Add(template);
        var chord = new Chord { Position = TimelineMath.PositionFromMs(12000, beats), TemplateId = 0 };
        foreach (var (s, fret) in template.Frets.OrderBy(kv => kv.Key))
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
        level.Chords.Add(chord);
        level.HandShapes.Add(new HandShape
        {
            Position = chord.Position,
            EndPosition = TimelineMath.PositionFromMs(12500, beats),
            TemplateId = 0
        });
        arrangement.Levels.Add(level);

        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);
        return project;
    }
}
