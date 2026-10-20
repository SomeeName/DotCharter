using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Parsing.Xml.Showlights;

/// <summary>
/// Reads an RS-XML <c>&lt;showlights&gt;</c> document into a <see cref="ChartProject"/>.
/// </summary>
internal sealed class RsXmlShowlightsReader
{
    public ChartProject Read(string filePath)
    {
        var doc = SafeXml.LoadFromFile(filePath);
        return ReadDocument(doc, filePath);
    }

    public ChartProject ReadDocument(XDocument doc, string filePath)
        => ReadDocument(doc, filePath, externalBeats: null);

    public ChartProject ReadDocument(XDocument doc, string filePath, IList<BeatMarker>? externalBeats)
    {
        var project = new ChartProject();
        project.Chart.SongName = Path.GetFileNameWithoutExtension(filePath);

        var root = doc.Root;
        if (root == null) return project;

        var beats = externalBeats ?? project.Beats;

        foreach (var slEl in root.Elements("showlight"))
        {
            var timeMs = Fmt.ParseTime(slEl.Attribute("time")?.Value);
            var note = Fmt.AttrInt(slEl, "note");

            var pos = beats.Count > 0
                ? TimelineMath.PositionFromImportedMs(timeMs, beats)
                : FractionalPosition.Zero;

            project.Showlights.Add(new Showlight
            {
                Position = pos,
                Type = ShowlightCatalog.NormalizeType(null, note),
                Note = ShowlightCatalog.NormalizeNote(null, note)
            });
        }

        return project;
    }
}
