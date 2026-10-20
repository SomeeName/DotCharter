using System.Text.Json;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>The tests of <see cref="ArrangementPropertiesImportTests" /> that need no package reader; its fixtures are in <c>ArrangementPropertiesImportTests.Fixtures.cs</c>.</summary>
public sealed partial class ArrangementPropertiesImportTests
{
    /// <summary>
    ///     The RS-XML reader restores the FIELD, not just the flag sidecar — the
    ///     writer and the manifest both read <c>Arrangement.PickedBass</c>, so leaving
    ///     it false dropped the technique on the very next export.
    /// </summary>
    [Fact]
    public void RsXml_RestoresPickedBass_ToTheField()
    {
        var path = Path.Combine(_dir, "bass.xml");
        var doc = new XDocument(new XElement("song",
            new XAttribute("version", 8),
            new XElement("title", "Picked"),
            new XElement("arrangement", "bass"),
            new XElement("tuning",
                new XAttribute("string0", 0), new XAttribute("string1", 0), new XAttribute("string2", 0),
                new XAttribute("string3", 0), new XAttribute("string4", 0), new XAttribute("string5", 0)),
            new XElement("arrangementProperties",
                new XAttribute("represent", 1), new XAttribute("bassPick", 1), new XAttribute("pathBass", 1)),
            new XElement("ebeats", new XAttribute("count", 2),
                new XElement("ebeat", new XAttribute("time", "0.000"), new XAttribute("measure", 1)),
                new XElement("ebeat", new XAttribute("time", "0.500"), new XAttribute("measure", -1))),
            new XElement("levels", new XAttribute("count", 0))));
        doc.Save(path);

        var arrangement = new XmlChartSerializer().Load(path).Arrangements[0];

        Assert.True(arrangement.PickedBass);
        Assert.Equal(1, arrangement.Properties.BassPick);
        // And it survives a re-export, which is the half that was actually broken.
        Assert.Equal(1, ArrangementPropertiesDeriver.Derive(arrangement).BassPick);
    }

    /// <summary>An unset flag must stay unset — the whole point is that it is
    /// authored, not implied by the arrangement being a bass.</summary>
    [Fact]
    public void RsXml_ABassWithoutTheFlag_IsNotPicked()
    {
        var path = Path.Combine(_dir, "fingered.xml");
        var doc = new XDocument(new XElement("song",
            new XAttribute("version", 8),
            new XElement("title", "Fingered"),
            new XElement("arrangement", "bass"),
            new XElement("arrangementProperties",
                new XAttribute("represent", 1), new XAttribute("bassPick", 0), new XAttribute("pathBass", 1)),
            new XElement("ebeats", new XAttribute("count", 1),
                new XElement("ebeat", new XAttribute("time", "0.000"), new XAttribute("measure", 1))),
            new XElement("levels", new XAttribute("count", 0))));
        doc.Save(path);

        var arrangement = new XmlChartSerializer().Load(path).Arrangements[0];

        Assert.False(arrangement.PickedBass);
        Assert.Equal(0, ArrangementPropertiesDeriver.Derive(arrangement).BassPick);
    }

    /// <summary>Sanity: the JSON above is the shape the parser really walks.</summary>
    [Fact]
    public void TheFixtureManifest_IsWellFormed()
    {
        using var doc = JsonDocument.Parse(ManifestJson(HeaderOnly));
        Assert.True(doc.RootElement.TryGetProperty("Entries", out _));
    }
}
