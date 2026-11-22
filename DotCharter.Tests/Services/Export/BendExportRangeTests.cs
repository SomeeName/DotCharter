using System.Globalization;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

public sealed class BendExportRangeTests
{
    [Theory]
    [InlineData(3.5, 3.5)]
    [InlineData(6, 6)]
    [InlineData(7, 6)]
    public void XmlAndSngPreserveSupportedSemitones_AndNormalizeTheSameRange(double step, double expected)
    {
        var project = new ChartProject();
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), Fret = 5, Sustain = new FractionalPosition(1),
            BendValues = { new BendValue { Position = new FractionalPosition(3), Step = step } }
        });
        arrangement.ChordTemplates.Add(new ChordTemplate { Id = 0, Frets = { [0] = 5, [1] = 7 } });
        level.Chords.Add(new Chord
        {
            Position = new FractionalPosition(6), TemplateId = 0,
            ChordNotes =
            {
                new ChordNote
                {
                    Fret = 5, Sustain = new FractionalPosition(1),
                    BendValues = { new BendValue { Position = new FractionalPosition(7), Step = step } }
                },
                new ChordNote { StringIndex = 1, Fret = 7 }
            }
        });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        var xml = new RsXmlSongWriter().Write(project);
        var points = xml.Descendants("bendValue").ToList();
        Assert.Equal(2, points.Count);
        Assert.All(points, p => Assert.Equal(expected, double.Parse(p.Attribute("step")!.Value, CultureInfo.InvariantCulture)));
        Assert.All(xml.Descendants().Where(e => e.Attribute("bend") != null),
            e => Assert.Equal(expected, double.Parse(e.Attribute("bend")!.Value, CultureInfo.InvariantCulture)));
        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, 8000).Sng;
        Assert.Equal(expected, Assert.Single(sng.Levels[0].Notes, n => n.BendData.Count > 0).MaxBend);
        Assert.Equal(expected, Assert.Single(sng.ChordNotes[0].BendData[0]).Step);
        Assert.Equal(step, level.Notes[0].BendValues[0].Step);
    }
}
