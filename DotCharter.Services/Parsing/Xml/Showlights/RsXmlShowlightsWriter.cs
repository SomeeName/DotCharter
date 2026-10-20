using System.Xml;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Parsing.Xml.Showlights;

/// <summary>
/// Writes showlights from a <see cref="ChartProject"/> into an RS-XML
/// <c>&lt;showlights&gt;</c> document, byte-compatible with rscharter's XStream output.
/// </summary>
internal sealed class RsXmlShowlightsWriter
{
    public XDocument Write(ChartProject project)
    {
        var beats = project.Beats;

        var slEls = project.Showlights
            .OrderBy(sl => sl.Position)
            .ThenBy(sl => ShowlightCatalog.GetSortOrder(sl.Type, sl.Note))
            .Select(sl => new XElement("showlight",
                new XAttribute("time", Fmt.Time(TimelineMath.ToWholeMs(TimelineMath.CalculateTimeMs(sl.Position, beats)))),
                new XAttribute("note", ShowlightCatalog.NormalizeNote(sl.Type, sl.Note))));

        var root = Fmt.CountedList("showlights", slEls);
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
    }

    public void WriteTo(ChartProject project, string filePath)
    {
        var doc = Write(project);
        using var writer = XmlWriter.Create(filePath, Fmt.GetWriterSettings());
        doc.WriteTo(writer);
    }
}
