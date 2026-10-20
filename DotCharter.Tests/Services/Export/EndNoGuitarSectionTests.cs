using System.Globalization;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     EOF parity: every Rocksmith export without a <c>noguitar</c> section gets
///     one where the playing stops (EOF_SRC/rs.c, eof_rs_export_common, "Adding
///     missing noguitar RS section"), and the RS-XML and the SNG agree on it.
/// </summary>
public sealed class EndNoGuitarSectionTests
{
    // 120 BPM: a beat every 500 ms.
    private static ChartProject Project(Action<Arrangement> author)
    {
        var project = new ChartProject();
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });

        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));
        arr.Levels.Add(new ChartLevel());
        author(arr);
        project.Arrangements.Add(arr);
        return project;
    }

    private static void Phrase(Arrangement arr, int beat, string phrase, string section = "")
    {
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Phrase = phrase, Section = section });
        if (!arr.Phrases.Contains(phrase)) arr.Phrases.Add(phrase);
    }

    private static void NoteAt(Arrangement arr, int beat) =>
        arr.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), Fret = 5 });

    /// <summary>A verse with its last note on beat 4 (2000 ms) and nothing after it.</summary>
    private static void Verse(Arrangement arr)
    {
        Phrase(arr, 0, "COUNT");
        Phrase(arr, 2, "verse", "verse");
        NoteAt(arr, 3);
        NoteAt(arr, 4);
    }

    private static List<(string Name, double StartMs)> XmlSections(ChartProject project) =>
        [.. new RsXmlSongWriter().Write(project, null).Root!.Element("sections")!.Elements("section")
            .Select(s => ((string)s.Attribute("name")!,
                double.Parse((string)s.Attribute("startTime")!, CultureInfo.InvariantCulture) * 1000))];

    private static List<(string Name, double StartMs)> SngSections(ChartProject project) =>
        [.. SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, 8000).Sng.Sections
            .Select(s => (s.Name, Math.Round(s.StartTime * 1000.0)))];

    [Fact]
    public void A_chart_without_noguitar_gets_one_on_the_beat_after_its_last_note()
    {
        var project = Project(Verse);

        // The last note sits on beat 4 (2000 ms): EOF's endbeat is beat 5.
        Assert.Equal([("verse", 1000.0), ("noguitar", 2500.0)], XmlSections(project));
        Assert.Equal([("verse", 1000.0), ("noguitar", 2500.0)], SngSections(project));
    }

    [Fact]
    public void The_added_noguitar_follows_the_last_note_not_a_moved_END_phrase()
    {
        // EOF places it on endbeat whatever the charter did with END.
        var project = Project(arr =>
        {
            Verse(arr);
            Phrase(arr, 8, "END");
        });

        Assert.Equal([("verse", 1000.0), ("noguitar", 2500.0)], XmlSections(project));
        Assert.Equal([("verse", 1000.0), ("noguitar", 2500.0)], SngSections(project));
    }

    [Fact]
    public void An_authored_noguitar_anywhere_suppresses_the_closing_one()
    {
        // EOF adds one only when the chart has none at all.
        var project = Project(arr =>
        {
            Phrase(arr, 0, "COUNT");
            Phrase(arr, 1, "rest", "noguitar");
            Phrase(arr, 2, "verse", "verse");
            NoteAt(arr, 4);
        });

        Assert.Equal([("noguitar", 500.0), ("verse", 1000.0)], XmlSections(project));
        Assert.Equal([("noguitar", 500.0), ("verse", 1000.0)], SngSections(project));
    }

    [Fact]
    public void A_section_already_on_that_beat_is_kept_not_doubled()
    {
        var project = Project(arr =>
        {
            Verse(arr);
            Phrase(arr, 5, "outro", "outro");
        });

        Assert.Equal([("verse", 1000.0), ("outro", 2500.0)], XmlSections(project));
        Assert.Equal([("verse", 1000.0), ("outro", 2500.0)], SngSections(project));
    }

    [Fact]
    public void An_arrangement_without_sections_gets_no_lone_noguitar()
    {
        var project = Project(arr =>
        {
            Phrase(arr, 0, "COUNT");
            Phrase(arr, 2, "verse");
            NoteAt(arr, 4);
        });

        Assert.Empty(XmlSections(project));
        Assert.Equal([("riff", 0.0)], SngSections(project));
    }

    [Fact]
    public void An_arrangement_without_notes_gets_no_noguitar()
    {
        var project = Project(arr =>
        {
            Phrase(arr, 0, "COUNT");
            Phrase(arr, 2, "verse", "verse");
        });

        Assert.Equal([("verse", 1000.0)], XmlSections(project));
    }
}
