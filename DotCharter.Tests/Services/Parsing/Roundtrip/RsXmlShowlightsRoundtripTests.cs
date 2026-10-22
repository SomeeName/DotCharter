using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Roundtrip;

public sealed class RsXmlShowlightsRoundtripTests : IDisposable
{
    private readonly string _tempDir;

    public RsXmlShowlightsRoundtripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DotCharterShowlightsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    private static ChartProject BuildShowlightsProject()
    {
        var project = new ChartProject();
        project.Chart.SongName = "Test Song";

        // 8 beats at 120 BPM
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });

        project.Showlights.Add(new Showlight { Position = FractionalPosition.Zero, Note = 24 });
        project.Showlights.Add(new Showlight { Position = new FractionalPosition(4), Note = 48 });
        project.Showlights.Add(new Showlight { Position = new FractionalPosition(6), Note = 72 });

        return project;
    }

    [Fact]
    public void SaveLoad_PreservesShowlightCount()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("showlights.xml");
        var original = BuildShowlightsProject();

        ser.Save(path, original);
        Assert.True(File.Exists(path));

        var loaded = ser.Load(path);

        Assert.Equal(original.Showlights.Count, loaded.Showlights.Count);
    }

    [Fact]
    public void SaveLoad_PreservesShowlightNotes()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("showlights.xml");
        var original = BuildShowlightsProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        var origNotes = original.Showlights.OrderBy(sl => sl.Position).Select(sl => sl.Note).ToList();
        var loadedNotes = loaded.Showlights.OrderBy(sl => sl.Position).Select(sl => sl.Note).ToList();
        Assert.Equal(origNotes, loadedNotes);
    }

    [Fact]
    public void Save_WritesShowlightsRootElement()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("showlights.xml");
        var original = BuildShowlightsProject();

        ser.Save(path, original);

        var doc = XDocument.Load(path);
        Assert.Equal("showlights", doc.Root?.Name.LocalName);
    }

    [Fact]
    public void Save_WritesCorrectCountAttribute()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("showlights.xml");
        var original = BuildShowlightsProject();

        ser.Save(path, original);

        var doc = XDocument.Load(path);
        var count = int.Parse(doc.Root!.Attribute("count")!.Value);
        Assert.Equal(original.Showlights.Count, count);
    }

    [Fact]
    public void Load_DetectsShowlightsRootElement()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("showlights.xml");

        File.WriteAllText(path, """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <showlights count="2">
              <showlight time="0.000" note="24"/>
              <showlight time="2.000" note="48"/>
            </showlights>
            """);

        var loaded = ser.Load(path);
        Assert.Equal(2, loaded.Showlights.Count);
        Assert.Equal(24, loaded.Showlights[0].Note);
        Assert.Equal(48, loaded.Showlights[1].Note);
    }

    [Fact]
    public void Load_UnknownRootElement_Throws()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("unknown.xml");

        File.WriteAllText(path, """
            <?xml version="1.0" encoding="UTF-8"?>
            <unknown/>
            """);

        Assert.Throws<InvalidDataException>(() => ser.Load(path));
    }

    [Fact]
    public void SaveShowlights_NamingConvention()
    {
        // Even without naming convention, can save to a showlights XML via
        // the reader recognizing the root element on round-trip.
        var project = BuildShowlightsProject();
        var path = TempPath("song_Showlights.xml");

        var ser = new XmlChartSerializer();
        // Force showlights save via naming convention
        ser.Save(path, project);

        var doc = XDocument.Load(path);
        Assert.Equal("showlights", doc.Root?.Name.LocalName);
    }
}
