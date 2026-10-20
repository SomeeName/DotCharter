using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml;

namespace DotCharter.Tests.Services.Parsing;

public class ShowlightSerializationTests
{
    [Fact]
    public void XmlSerializer_SaveShowlights_WritesCanonicalNotes()
    {
        var project = CreateProjectWithBeats();
        project.Showlights.Add(new Showlight
        {
            Position = FractionalPosition.Zero,
            Note = 32
        });
        project.Showlights.Add(new Showlight
        {
            Position = new FractionalPosition(1),
            Type = "BEAMS_BLUE_PLUS"
        });

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_showlights.xml");

        try
        {
            new XmlChartSerializer().Save(path, project);

            var notes = XDocument.Load(path)
                .Root!
                .Elements("showlight")
                .Select(element => int.Parse(element.Attribute("note")!.Value))
                .ToArray();

            Assert.Equal([32, 57], notes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void XmlSerializer_LoadShowlights_MapsKnownNotesToTypes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_showlights.xml");

        try
        {
            var doc = new XDocument(
                new XElement("showlights",
                    new XAttribute("count", 1),
                    new XElement("showlight",
                        new XAttribute("time", "0.000"),
                        new XAttribute("note", 57))));
            doc.Save(path);

            var project = new XmlChartSerializer().Load(path);
            var showlight = Assert.Single(project.Showlights);

            Assert.Equal("BEAMS_BLUE_PLUS", showlight.Type);
            Assert.Equal(57, showlight.Note);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RscpSerializer_RoundTripsShowlightsUsingTypeAndNoteFallbacks()
    {
        var project = new ChartProject();
        project.Showlights.Add(new Showlight
        {
            Position = new FractionalPosition(4),
            Type = "BEAMS_BLUE_PLUS"
        });
        project.Showlights.Add(new Showlight
        {
            Position = new FractionalPosition(8),
            Note = 32
        });

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.rscp");

        try
        {
            var serializer = new RscpChartSerializer();
            serializer.Save(path, project);
            var loaded = serializer.Load(path);

            Assert.Equal(2, loaded.Showlights.Count);
            Assert.Collection(loaded.Showlights.OrderBy(showlight => showlight.Position),
                showlight =>
                {
                    Assert.Equal(new FractionalPosition(4), showlight.Position);
                    Assert.Equal("BEAMS_BLUE_PLUS", showlight.Type);
                    Assert.Equal(57, showlight.Note);
                },
                showlight =>
                {
                    Assert.Equal(new FractionalPosition(8), showlight.Position);
                    Assert.Equal("FOG_RED", showlight.Type);
                    Assert.Equal(32, showlight.Note);
                });
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ChartProject CreateProjectWithBeats()
    {
        var project = new ChartProject();
        project.Beats.Add(new BeatMarker
        {
            Time = 0,
            Bpm = 120,
            BeatsInMeasure = 4,
            NoteDenominator = 4,
            IsMeasure = true
        });
        project.Beats.Add(new BeatMarker
        {
            Time = 500,
            Bpm = 120,
            BeatsInMeasure = 4,
            NoteDenominator = 4
        });
        return project;
    }
}
