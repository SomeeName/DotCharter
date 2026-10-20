using System.Xml.Linq;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     #911: for a 7-/8-string guitar the outputs meant for EOF and DLC Builder are a
///     SIX-string projection. EOF aborts the import on a <c>string</c> above 5 and
///     Rocksmith2014.NET builds the SNG from six-wide arrays, so the projection obeys:
///     no string above 5 anywhere, six-wide templates, six tuning entries, no
///     <c>strings</c> attribute. Six-string guitars and basses are untouched.
/// </summary>
public sealed class RsXmlSixStringProjectionTests : IDisposable
{
    private const int BeatMs = 500;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", "six-string-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAppLogger _logger = new();

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void The_dtrs_xml_bundle_always_carries_the_six_string_projection_and_logs_what_it_dropped()
    {
        Directory.CreateDirectory(_dir);
        // DLC Builder's steps off: the count below is the chart's one level, not a generated ladder.
        var service = Service(new AppConfig
        {
            RsXmlSixStringProjection = false, ExportApplyDlcBuilderFixes = false, ExportGenerateDynamicDifficulty = false
        });

        service.SaveDlcBuilderXmlBundle(Path.Combine(_dir, "project.rscp"), Chart(7));

        var file = Directory.GetFiles(Path.Combine(_dir, DlcBuilderBundle.DirectoryName), "*_Lead_*.xml").Single();
        var song = XDocument.Load(file).Root!;
        Assert.Null(song.Element("tuning")!.Attribute("strings"));
        Assert.All(song.Descendants("note"), n => Assert.InRange((int)n.Attribute("string")!, 0, 5));
        Assert.Contains(_logger.Entries, e => e.Level == "WARN" && e.Message.Contains("dropped 2 notes"));
    }

    [Fact]
    public void The_dtrs_xml_bundle_of_a_six_string_guitar_logs_no_warning()
    {
        Directory.CreateDirectory(_dir);

        Service(new AppConfig()).SaveDlcBuilderXmlBundle(Path.Combine(_dir, "project.rscp"), Chart(6));

        Assert.DoesNotContain(_logger.Entries, e => e.Level == "WARN");
    }

    [Theory]
    [InlineData(true, 1, 1)]
    [InlineData(false, 6, 0)]
    public void Save_as_xml_follows_the_option(bool sixString, int expectedHighestString, int expectedWarnings)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "song.xml");

        Service(new AppConfig { RsXmlSixStringProjection = sixString }).SaveChart(path, Chart(7));

        var song = XDocument.Load(path).Root!;
        var highest = song.Descendants("note").Max(n => (int)n.Attribute("string")!);
        Assert.Equal(expectedHighestString, highest);
        Assert.Equal(expectedWarnings, _logger.Entries.Count(e => e.Level == "WARN"));
    }

    private ProjectDocumentService Service(AppConfig config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppLogger>(_logger);
        services.AddKeyedTransient<IChartSerializer, RscpChartSerializer>("rscp");
        services.AddKeyedTransient<IChartSerializer, XmlChartSerializer>("xml");
        services.AddSingleton<IConfigurationService>(new FakeConfigurationService { AppConfig = config });
        return new ProjectDocumentService(services.BuildServiceProvider(), _logger);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void The_projection_obeys_the_rules_of_the_six_wide_readers(int strings)
    {
        var song = SixString(Chart(strings));

        Assert.NotEmpty(song.Descendants("note"));
        Assert.All(song.Descendants("note"), n => Assert.InRange((int)n.Attribute("string")!, 0, 5));
        Assert.All(song.Descendants("chordNote"), n => Assert.InRange((int)n.Attribute("string")!, 0, 5));
        foreach (var template in song.Element("chordTemplates")!.Elements("chordTemplate"))
        {
            Assert.DoesNotContain(template.Attributes(), a => Extra(a.Name.LocalName));
            Assert.Contains(template.Attributes(), a => a.Name.LocalName.StartsWith("fret"));
        }

        var tuning = song.Element("tuning")!;
        Assert.Null(tuning.Attribute("strings"));
        Assert.Equal(["string0", "string1", "string2", "string3", "string4", "string5"],
            tuning.Attributes().Select(a => a.Name.LocalName));
    }

    [Fact]
    public void The_tuning_is_the_six_stock_entries_not_the_charts_first_six()
    {
        var project = Chart(7);
        // chart order is lowest first: index 0 = the extra low B, index 1 = the low E.
        project.Arrangements[0].TuningPitches[0] = new TuningOffset(-5);
        project.Arrangements[0].TuningPitches[1] = new TuningOffset(-2);

        var tuning = SixString(project).Element("tuning")!;

        Assert.Equal("-2", (string?)tuning.Attribute("string0"));
        Assert.Equal("0", (string?)tuning.Attribute("string5"));
        Assert.Null(tuning.Attribute("string6"));
        Assert.Equal(new short[] { -2, 0, 0, 0, 0, 0 }, Rs2DlcProjectBuilder.TuningVector(project.Arrangements[0]));
    }

    [Fact]
    public void The_kept_notes_are_the_stock_strings_unchanged_and_the_counts_are_right()
    {
        var project = Chart(7);
        var writer = new RsXmlSongWriter { SixStringProjection = true };

        var song = writer.Write(project).Root!;

        var six = new RsXmlSongWriter().Write(Chart(6)).Root!;
        Assert.Equal(Notes(six).Order(), Notes(song).Order());
        // Chart(7): the low-B single note and the chord's low-B chordNote; the chord keeps two strings.
        Assert.Equal(new SixStringLoss(Notes: 2, Chords: 0, TrimmedChords: 1), writer.LastLoss);
        Assert.Contains("dropped 2 notes", writer.LastLoss!.Describe(project.Arrangements[0]));
    }

    [Fact]
    public void A_chord_only_on_the_extra_strings_counts_as_a_dropped_chord()
    {
        var project = Chart(8);
        var arrangement = project.Arrangements[0];
        var template = new ChordTemplate { Id = 5, Name = "Low" };
        template.Frets[0] = 2;
        template.Frets[1] = 2;
        arrangement.ChordTemplates.Add(template);
        var chord = new Chord { Position = TimelineMath.PositionFromMs(14000, project.Beats), TemplateId = 5 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        arrangement.Levels[0].Chords.Add(chord);
        var writer = new RsXmlSongWriter { SixStringProjection = true };

        var song = writer.Write(project).Root!;

        Assert.Equal(1, writer.LastLoss!.Chords);
        Assert.Equal(1, writer.LastLoss.TrimmedChords);
        Assert.Equal(4, writer.LastLoss.Notes);
        Assert.Single(song.Descendants("chord"));
        Assert.DoesNotContain(song.Descendants("chordTemplate"), t => (string?)t.Attribute("chordName") == "Low");
        Assert.All(song.Descendants("note"), n => Assert.InRange((int)n.Attribute("string")!, 0, 5));
    }

    [Fact]
    public void Without_the_option_the_spec_xml_keeps_every_string()
    {
        var writer = new RsXmlSongWriter();

        var song = writer.Write(Chart(7)).Root!;

        Assert.Null(writer.LastLoss);
        Assert.Equal("7", (string?)song.Element("tuning")!.Attribute("strings"));
        Assert.Contains(song.Descendants("note"), n => (int)n.Attribute("string")! == 6);
    }

    [Fact]
    public void The_chart_itself_is_never_touched()
    {
        var project = Chart(7);
        var arrangement = project.Arrangements[0];
        (int, int, int) Shape() => (arrangement.StringCount, arrangement.Levels[0].Notes.Count,
            arrangement.Levels[0].Chords[0].ChordNotes.Count);
        var before = Shape();

        SixString(project);

        Assert.Equal(before, Shape());
    }

    [Fact]
    public void A_six_string_guitar_and_a_bass_are_identical_with_the_option_on()
    {
        foreach (var project in new[] { Chart(6), BassChart() })
        {
            var writer = new RsXmlSongWriter { SixStringProjection = true };

            var projected = writer.Write(project, 20_000);

            Assert.Null(writer.LastLoss);
            Assert.Equal(Normalized(new RsXmlSongWriter().Write(project, 20_000)), Normalized(projected));
        }
    }

    [Fact]
    public void The_tuning_vector_of_a_six_string_guitar_is_unchanged()
    {
        var six = Chart(6).Arrangements[0];
        six.TuningPitches[0] = new TuningOffset(-2);

        Assert.Equal(new short[] { -2, 0, 0, 0, 0, 0 }, Rs2DlcProjectBuilder.TuningVector(six));
    }

    [Fact]
    public void A_projection_with_nothing_on_the_extra_strings_loses_nothing()
    {
        var project = Chart(7);
        var arrangement = project.Arrangements[0];
        var level = arrangement.Levels[0];
        level.Notes.Remove(level.Notes.Single(n => n.StringIndex == 0));
        level.Chords[0].ChordNotes.Remove(level.Chords[0].ChordNotes.Single(c => c.StringIndex == 0));
        arrangement.ChordTemplates[0].Frets.Remove(0);
        arrangement.ChordTemplates[0].Fingers.Remove(0);
        var writer = new RsXmlSongWriter { SixStringProjection = true };

        writer.Write(project);

        Assert.True(writer.LastLoss!.IsEmpty);
    }

    private static bool Extra(string name) => name is "fret6" or "fret7" or "finger6" or "finger7";

    private static XElement SixString(ChartProject project) =>
        new RsXmlSongWriter { SixStringProjection = true }.Write(project).Root!;

    /// <summary>The document minus its conversion timestamp.</summary>
    private static string Normalized(XDocument doc)
    {
        var copy = new XDocument(doc);
        copy.Root!.Element("lastConversionDateTime")?.Remove();
        return copy.ToString();
    }

    private static List<(int String, int Fret, int TimeMs)> Notes(XElement song) =>
    [
        .. song.Descendants("note").Select(n =>
            ((int)n.Attribute("string")!, (int)n.Attribute("fret")!, (int)Math.Round((double)n.Attribute("time")! * 1000)))
    ];

    private static ChartProject BassChart()
    {
        var project = Chart(6);
        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = 5 };
        for (var i = 0; i < 5; i++) bass.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { StringIndex = 4, Fret = 2, Position = TimelineMath.PositionFromMs(4000, project.Beats) });
        bass.Levels.Add(level);
        project.Arrangements.Clear();
        project.Arrangements.Add(bass);
        return project;
    }

    /// <summary>A guitar of <paramref name="strings" /> strings, chart order lowest first: a note on its
    /// low E and A, a low-B note and a chord over low B, low E and A when it has a 7th string.</summary>
    private static ChartProject Chart(int strings)
    {
        var beats = Enumerable.Range(0, 32).Select(i => new BeatMarker
        {
            Time = i * BeatMs, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4
        }).ToList();
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = strings };
        for (var i = 0; i < strings; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Tuning = TuningUtilities.GetTuningNames(strings, [.. arrangement.TuningPitches.Select(t => t.Step)]);

        var lowE = strings - 6;
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(TimelineMath.PositionFromMs(4000, beats), 1, 4));
        if (strings > 6)
            level.Notes.Add(new GuitarNote { StringIndex = 0, Fret = 3, Position = TimelineMath.PositionFromMs(4000, beats) });
        level.Notes.Add(new GuitarNote { StringIndex = lowE, Fret = 5, Position = TimelineMath.PositionFromMs(6000, beats) });
        level.Notes.Add(new GuitarNote { StringIndex = lowE + 1, Fret = 7, Position = TimelineMath.PositionFromMs(7000, beats) });

        var template = new ChordTemplate { Id = 0, Name = "C#5" };
        template.Frets[lowE] = 4;
        template.Fingers[lowE] = 3;
        template.Frets[lowE + 1] = 6;
        template.Fingers[lowE + 1] = 4;
        if (strings > 6)
        {
            template.Frets[0] = 2;
            template.Fingers[0] = 1;
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
