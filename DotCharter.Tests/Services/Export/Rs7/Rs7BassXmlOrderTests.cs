using DotCharter.Services.Export.Rocksmith;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>
///     Positional bass XML and matching companion tuning vectors preserve the domain
///     strings through a round trip. Stock four-string bass and unmarked foreign XML
///     retain their reader rules; golden model slots are mapped to positional columns.
/// </summary>
public sealed class Rs7BassXmlOrderTests
{
    [Theory]
    [InlineData(5, false, new short[] { 0, 0, 0, 0, 0, 0 })]
    [InlineData(6, false, new short[] { 0, 0, 0, 0, 0, 0 })]
    [InlineData(5, true, new short[] { 0, 0, 0, 0, 0, 0 })]
    [InlineData(6, true, new short[] { 0, 0, 0, 0, 0, 0 })]
    public void The_DLC_Builder_tuning_matches_the_extended_bass_XML(int strings, bool allStrings, short[] expected)
    {
        var project = Stock(strings);
        var arrangement = project.Arrangements[0];
        arrangement.TuningPitches.Clear();
        var authored = new[] { -5, -5, -5, -5, -4, strings == 5 ? 0 : -4 };
        foreach (var step in authored) arrangement.TuningPitches.Add(new TuningOffset(step));

        var vector = DotCharter.Services.Export.DlcBuilder.Rs2DlcProjectBuilder.TuningVector(arrangement, allStrings);
        var xml = Song(project).Element("tuning")!;

        Assert.Equal(expected, vector);
        Assert.Equal(vector.Select(s => (int)s), Enumerable.Range(0, 6).Select(i => Attr(xml, $"string{i}")));
        Assert.Equal(authored, arrangement.TuningPitches.Select(t => t.Step));
    }

    [Fact]
    public void The_DLC_Builder_tuning_preserves_each_custom_bass_string_in_package_order()
    {
        var project = Stock(6);
        var arrangement = project.Arrangements[0];
        arrangement.TuningPitches.Clear();
        foreach (var step in new[] { -7, -4, -5, -3, -5, -1 })
            arrangement.TuningPitches.Add(new TuningOffset(step));

        var vector = DotCharter.Services.Export.DlcBuilder.Rs2DlcProjectBuilder.TuningVector(arrangement);
        var xml = Song(project).Element("tuning")!;

        Assert.Equal(new short[] { -2, 1, 0, 2, -1, 3 }, vector);
        Assert.Equal(vector.Select(s => (int)s), Enumerable.Range(0, 6).Select(i => Attr(xml, $"string{i}")));
    }

    private static (ChartProject Project, Rs7ExtendedRangeGoldenTests.Case Case) GoldenBass(string name)
    {
        var c = Rs7ExtendedRangeGoldenTests.CaseOf(name);
        var (arrangement, beats) = Rs7ExtendedRangeGoldenTests.Chart(c);
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);
        return (project, c);
    }

    private static XElement Song(ChartProject project) => new RsXmlSongWriter().Write(project, 30_000).Root!;

    private static int Attr(XElement e, string name) => (int)e.Attribute(name)!;

    [Theory]
    [InlineData("b5")]
    [InlineData("b6")]
    public void The_xml_numbers_a_bass_by_position(string name)
    {
        var (project, c) = GoldenBass(name);

        var song = Song(project);

        Assert.Equal(c.Strings, Attr(song.Element("tuning")!, "strings"));
        Assert.Equal(c.Notes.Select(n => (Rs7StringLayout.ToDomain(n.String, c.Strings, ArrangementType.Bass), n.Fret)).OrderBy(n => n),
            song.Descendants("note").Where(n => n.Parent!.Name == "notes").Select(n => (Attr(n, "string"), Attr(n, "fret"))).OrderBy(n => n));
        var chordStrings = song.Descendants("chord").Select(ch => ch.Elements("chordNote").Select(cn => (Attr(cn, "string"), Attr(cn, "fret"))).OrderBy(x => x));
        Assert.Equal(c.Chords.Select(ch => ch.Columns.Select(x => (Rs7StringLayout.ToDomain(x.String, c.Strings, ArrangementType.Bass), x.Fret)).OrderBy(x => x)), chordStrings);
    }

    [Theory]
    [InlineData("b5")]
    [InlineData("b6")]
    public void The_xml_tuning_and_chord_templates_match_rs7charts_sng(string name)
    {
        var (project, _) = GoldenBass(name);
        var golden = Rs7ExtendedRangeGoldenTests.LoadGolden(name);

        var song = Song(project);

        var tuning = song.Element("tuning")!;
        Assert.Equal(Enumerable.Range(0, project.Arrangements[0].StringCount).Select(i => golden.Tuning[Rs7StringLayout.ToSng(i, project.Arrangements[0].StringCount, ArrangementType.Bass)]).Concat(Enumerable.Repeat(0, 6 - project.Arrangements[0].StringCount)), Enumerable.Range(0, 6).Select(i => Attr(tuning, $"string{i}")));
        var templates = song.Element("chordTemplates")!.Elements("chordTemplate").ToList();
        Assert.Equal(golden.Chords.Count, templates.Count);
        for (var i = 0; i < templates.Count; i++)
        {
            var frets = Enumerable.Range(0, 6).Select(s => templates[i].Attribute($"fret{s}") is { } a ? (int)a : -1).ToArray();
            Assert.Equal(Enumerable.Range(0, project.Arrangements[0].StringCount).Select(s => golden.Chords[i].Frets[Rs7StringLayout.ToSng(s, project.Arrangements[0].StringCount, ArrangementType.Bass)]).Concat(Enumerable.Repeat(-1, 6 - project.Arrangements[0].StringCount)), frets);
        }
    }

    [Theory]
    [InlineData("b5")]
    [InlineData("b6")]
    public void A_bass_round_trips_through_the_xml_in_the_chart_order(string name)
    {
        var (project, _) = GoldenBass(name);
        var before = project.Arrangements[0];

        var read = new RsXmlSongReader().ReadDocument(new XDocument(Song(project)), "bass.xml").Arrangements[0];

        Assert.Equal(before.StringCount, read.StringCount);
        Assert.Equal(before.TuningPitches.Select(t => t.Step), read.TuningPitches.Select(t => t.Step));
        Assert.Equal(Notes(before), Notes(read));
        Assert.Equal(ChordsOf(before), ChordsOf(read));
        Assert.Equal(Templates(before), Templates(read));
    }

    [Fact]
    public void A_stock_four_string_bass_keeps_its_numbers_both_ways()
    {
        var project = Stock(4);

        var song = Song(project);

        Assert.Equal(4, Attr(song.Element("tuning")!, "strings"));
        Assert.Equal([3, 0], song.Descendants("note").Select(n => Attr(n, "string")).Where(s => s is 0 or 3).Distinct());
        var read = new RsXmlSongReader().ReadDocument(new XDocument(song), "bass.xml").Arrangements[0];
        Assert.Equal(Notes(project.Arrangements[0]), Notes(read));
    }

    [Fact]
    public void A_foreign_bass_xml_with_six_string_attributes_and_no_strings_attribute_is_read_as_it_is()
    {
        var song = Song(Stock(4));
        song.Element("tuning")!.Attribute("strings")!.Remove();

        var read = new RsXmlSongReader().ReadDocument(new XDocument(song), "bass.xml").Arrangements[0];

        Assert.Equal(Notes(Stock(4).Arrangements[0]), Notes(read));
    }

    private static ChartProject Stock(int strings)
    {
        var beats = Rs7TestSupport.Beats(40);
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Bass, Name = "Bass", StringCount = strings };
        for (var i = 0; i < strings; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(TimelineMath.PositionFromMs(4 * Rs7TestSupport.BeatMs, beats), 1, 4));
        level.Notes.Add(new GuitarNote { Position = TimelineMath.PositionFromMs(4 * Rs7TestSupport.BeatMs, beats), StringIndex = 3, Fret = 5 });
        level.Notes.Add(new GuitarNote { Position = TimelineMath.PositionFromMs(5 * Rs7TestSupport.BeatMs, beats), StringIndex = 0, Fret = 2 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static List<(int, int)> Notes(Arrangement a) =>
        [.. a.Levels[0].Notes.Select(n => (n.StringIndex, n.Fret)).OrderBy(n => n)];

    private static List<string> ChordsOf(Arrangement a) =>
        [.. a.Levels[0].Chords.Select(c => string.Join(',', c.ChordNotes.OrderBy(n => n.StringIndex).Select(n => $"{n.StringIndex}:{n.Fret}")))];

    private static List<string> Templates(Arrangement a) =>
        [.. a.ChordTemplates.Select(t => string.Join(',', t.Frets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")))];
}
