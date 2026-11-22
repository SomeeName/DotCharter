using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tests.Services.Export.Rs7;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrXmlTests
{
    [Theory]
    [InlineData(ArrangementType.Lead, 7)]
    [InlineData(ArrangementType.Rhythm, 8)]
    [InlineData(ArrangementType.Bass, 5)]
    [InlineData(ArrangementType.Bass, 6)]
    public void Extended_XML_marks_the_tuning_and_numbers_every_string_thickest_first(ArrangementType type, int count)
    {
        var project = Chart(type, count);
        var before = Snapshot(project.Arrangements[0]);
        var song = Write(project).Root!;
        var tuning = song.Element("tuning")!;

        Assert.Equal(new[] { "extendedRange", "strings" }.Concat(Enumerable.Range(0, Math.Max(6, count)).Select(i => $"string{i}")),
            tuning.Attributes().Select(a => a.Name.LocalName));
        Assert.Equal(1, (int)tuning.Attribute("extendedRange")!);
        Assert.Equal(count, (int)tuning.Attribute("strings")!);
        Assert.Equal(Offsets(count).Concat(Enumerable.Repeat(0, Math.Max(0, 6 - count))),
            Enumerable.Range(0, Math.Max(6, count)).Select(i => (int)tuning.Attribute($"string{i}")!));
        Assert.Equal(Enumerable.Range(0, count).Select(i => (i, i + 2)), Columns(song.Descendants("note")));
        Assert.Equal(Enumerable.Range(0, count).Select(i => (i, i + 2)), Columns(song.Descendants("chordNote")));
        Assert.Equal(0, (int)song.Descendants("note").First().Attribute("string")!);
        AssertTemplate(Assert.Single(song.Element("chordTemplates")!.Elements()), count);
        AssertTemplate(Assert.Single(song.Element("fretHandMuteTemplates")!.Elements()), count);
        Assert.Equal(before, Snapshot(project.Arrangements[0]));
    }

    [Fact]
    public void Five_string_bass_tuning_matches_the_declared_layout_literally()
    {
        var project = Chart(ArrangementType.Bass, 5);
        SetTuning(project.Arrangements[0], [-2, 0, 0, 0, 0]);

        var tuning = Write(project).Root!.Element("tuning")!;

        Assert.Equal("<tuning extendedRange=\"1\" strings=\"5\" string0=\"-2\" string1=\"0\" string2=\"0\" string3=\"0\" string4=\"0\" string5=\"0\" />",
            tuning.ToString(SaveOptions.DisableFormatting));
    }

    [Theory]
    [InlineData(ArrangementType.Lead, 7)]
    [InlineData(ArrangementType.Lead, 8)]
    [InlineData(ArrangementType.Bass, 5)]
    [InlineData(ArrangementType.Bass, 6)]
    public void XML_round_trips_the_chart_order_and_tuning(ArrangementType type, int count)
    {
        var project = Chart(type, count);
        var before = Snapshot(project.Arrangements[0]);

        var read = new RsXmlSongReader().ReadDocument(Write(project), "chart.xml").Arrangements[0];

        Assert.Equal(before, Snapshot(read));
        Assert.Equal(count, read.StringCount);
        Assert.Equal(project.Arrangements[0].TuningPitches.Select(t => t.Step), read.TuningPitches.Select(t => t.Step));
    }

    [Theory]
    [InlineData(ArrangementType.Lead, 6, false)]
    [InlineData(ArrangementType.Bass, 4, false)]
    [InlineData(ArrangementType.Lead, 7, true)]
    [InlineData(ArrangementType.Lead, 8, true)]
    public void Stock_and_six_string_copies_have_only_stock_columns(ArrangementType type, int count, bool sixString)
    {
        var project = Chart(type, count);
        var xml = Write(project, sixString);
        Assert.Null(xml.Root!.Element("tuning")!.Attribute("extendedRange"));
        Assert.Equal(6, xml.Root.Element("tuning")!.Attributes().Count(a => a.Name.LocalName.StartsWith("string", StringComparison.Ordinal) && a.Name != "strings"));
        Assert.All(xml.Descendants("note").Concat(xml.Descendants("chordNote")), n => Assert.InRange((int)n.Attribute("string")!, 0, 5));
        if (sixString)
        {
            Assert.Contains(xml.Descendants("note"), n => (int)n.Attribute("string")! == 0 && (int)n.Attribute("fret")! == count - 4);
            Assert.Equal(6, xml.Descendants("note").Count());
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void Six_string_projection_keeps_all_extended_bass_strings(int count)
    {
        var project = Chart(ArrangementType.Bass, count);

        var doc = Write(project, true);
        var read = new RsXmlSongReader().ReadDocument(doc, "bass.xml").Arrangements[0];

        Assert.Equal("1", doc.Root!.Element("tuning")!.Attribute("extendedRange")!.Value);
        Assert.Equal(Snapshot(project.Arrangements[0]), Snapshot(read));
    }

    [Theory]
    [InlineData(ArrangementType.Lead, 7)]
    [InlineData(ArrangementType.Lead, 8)]
    [InlineData(ArrangementType.Bass, 5)]
    [InlineData(ArrangementType.Bass, 6)]
    public void Projection_content_preserves_split_and_link_landings(ArrangementType type, int count)
    {
        var project = Chart(type, count);
        var level = project.Arrangements[0].Levels[0];
        FractionalPosition At(int beat) => TimelineMath.PositionFromMs(beat * Rs7TestSupport.BeatMs, project.Beats);
        var split = new Chord { Position = At(20), TemplateId = 0, SplitIntoNotes = true };
        var landing = new Chord { Position = At(24), TemplateId = 0 };
        foreach (var note in level.Chords[0].ChordNotes)
        {
            split.ChordNotes.Add(new ChordNote { StringIndex = note.StringIndex, Fret = note.Fret });
            landing.ChordNotes.Add(new ChordNote { StringIndex = note.StringIndex, Fret = note.Fret });
        }

        level.Chords.Add(split);
        level.Notes.Add(new GuitarNote { Position = At(23), Sustain = At(1), StringIndex = 0, Fret = 2, LinkNext = true });
        level.Chords.Add(landing);
        var before = Snapshot(project.Arrangements[0]);
        var xml = Write(project);
        Assert.Single(xml.Descendants("chord"));
        Assert.All(xml.Descendants("note"), n => Assert.InRange((int)n.Attribute("string")!, 0, count - 1));
        foreach (var index in Enumerable.Range(0, count))
            Assert.Contains(xml.Descendants("note"), n => (int)n.Attribute("string")! == index && (double)n.Attribute("time")! >= 15);
        Assert.Equal(before, Snapshot(project.Arrangements[0]));
    }

    [Theory]
    [InlineData(ArrangementType.Lead, "2", "7", "2")]
    [InlineData(ArrangementType.Bass, "0", "5", "0")]
    [InlineData(ArrangementType.Lead, "invalid", "7", "invalid")]
    [InlineData(ArrangementType.Lead, "1", null, "strings")]
    [InlineData(ArrangementType.Bass, "1", null, "strings")]
    [InlineData(ArrangementType.Lead, "1", "6", "6")]
    [InlineData(ArrangementType.Bass, "1", "7", "7")]
    [InlineData(ArrangementType.Lead, "1", "999999999999", "999999999999")]
    public void Invalid_markers_and_instrument_counts_are_rejected(ArrangementType type, string marker, string? count, string message)
    {
        var doc = Write(Chart(type, type is ArrangementType.Bass ? 5 : 7));
        var tuning = doc.Root!.Element("tuning")!;
        tuning.SetAttributeValue("extendedRange", marker);
        tuning.SetAttributeValue("strings", count);

        var error = Assert.Throws<InvalidDataException>(() => new RsXmlSongReader().ReadDocument(doc, "chart.xml"));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    private static XDocument Write(ChartProject project, bool sixString = false) =>
        new RsXmlSongWriter { SixStringProjection = sixString }.Write(project, 30_000);

    private static IEnumerable<(int String, int Fret)> Columns(IEnumerable<XElement> notes) =>
        notes.Select(n => ((int)n.Attribute("string")!, (int)n.Attribute("fret")!)).OrderBy(n => n);

    private static void AssertTemplate(XElement template, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(i + 2, (int)template.Attribute($"fret{i}")!);
            Assert.Equal(i % 4 + 1, (int)template.Attribute($"finger{i}")!);
        }
    }

    private static string Snapshot(Arrangement arr) => string.Join('|',
        new[] { arr.StringCount.ToString(), string.Join(',', arr.TuningPitches.Select(t => t.Step)) }
            .Concat(arr.ChordTemplates.Select(Template))
            .Concat(arr.FretHandMuteTemplates.Select(Template))
            .Concat(arr.Levels.SelectMany(l => l.Notes.Select(n => $"note:{n.StringIndex}:{n.Fret}").OrderBy(n => n)))
            .Concat(arr.Levels.SelectMany(l => l.Chords.Select(c => "chord:" + string.Join(',', c.ChordNotes.OrderBy(n => n.StringIndex).Select(n => $"{n.StringIndex}:{n.Fret}")))))
            .Concat(arr.Levels.SelectMany(l => l.HandShapes.Select(h => $"shape:{h.TemplateId}"))));

    private static string Template(ChordTemplate template) =>
        string.Join(',', template.Frets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}:{template.Fingers.GetValueOrDefault(kv.Key, -1)}"));

    private static int[] Offsets(int count) => [.. new[] { -2, 1, -1, 2, -3, 0, 3, -4 }.Take(count)];

    private static void SetTuning(Arrangement arr, int[] offsets)
    {
        var chartMidi = arr.ArrangementType is ArrangementType.Bass
            ? [28, 33, 38, 43, 47, 52]
            : GuitarStandardMidi(arr.StringCount);
        var standard = arr.ArrangementType is ArrangementType.Bass
            ? arr.StringCount == 4 ? [28, 33, 38, 43] : [23, 28, 33, 38, 43, 48]
            : chartMidi;
        arr.TuningPitches = [.. offsets.Select((offset, i) => new TuningOffset(offset + standard[i] - chartMidi[i]))];
    }

    private static int[] GuitarStandardMidi(int count) => count switch
    {
        8 => [30, 35, 40, 45, 50, 55, 59, 64],
        7 => [35, 40, 45, 50, 55, 59, 64],
        _ => [40, 45, 50, 55, 59, 64]
    };

    private static ChartProject Chart(ArrangementType type, int count)
    {
        var project = new ChartProject();
        foreach (var beat in Rs7TestSupport.Beats(40)) project.Beats.Add(beat);
        var arr = new Arrangement { ArrangementType = type, Name = "Synthetic", StringCount = count };
        SetTuning(arr, Offsets(count));
        var template = new ChordTemplate { Id = 0, Name = "Synthetic" };
        var muted = new ChordTemplate { Id = 0, Name = "Muted" };
        var level = new ChartLevel { DifficultyIndex = 0 };
        FractionalPosition At(int beat) => TimelineMath.PositionFromMs(beat * Rs7TestSupport.BeatMs, project.Beats);
        var chord = new Chord { Position = At(16), TemplateId = 0 };
        for (var i = 0; i < count; i++)
        {
            template.Frets[i] = muted.Frets[i] = i + 2;
            template.Fingers[i] = muted.Fingers[i] = i % 4 + 1;
            level.Notes.Add(new GuitarNote { Position = At(4 + i), StringIndex = i, Fret = i + 2 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = i, Fret = i + 2 });
        }

        arr.ChordTemplates.Add(template);
        arr.FretHandMuteTemplates.Add(muted);
        level.Chords.Add(chord);
        level.Anchors.Add(new FretAnchor(At(4), 1, 12));
        level.HandShapes.Add(new HandShape { Position = At(16), EndPosition = At(18), TemplateId = 0 });
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);
        return project;
    }
}
