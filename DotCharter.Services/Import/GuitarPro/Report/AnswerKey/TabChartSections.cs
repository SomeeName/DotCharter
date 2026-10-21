using System.Xml.Linq;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Import.GuitarPro.Report.AnswerKey;

/// <summary>A section of the finished chart, where it really starts.</summary>
public sealed record ChartSection(string Name, FractionalPosition Position);

/// <summary>
///     The answer key's chart and sections. EOF cannot put a phrase between beats, so a charter
///     names the phrase on the beat <c>moveR</c> + N and the phrase (and a section on it) really
///     starts at the Nth note from there. The importer resolves that on the way in
///     (<see cref="RsXmlPhraseMover" />, DLC Builder's rules), so the chart this loads already has
///     every phrase and section where it plays and the comparison holds no resolution of its own.
/// </summary>
public static class TabChartSections
{
    /// <summary>
    ///     Reads a single-arrangement song XML exactly as the importer does, and says how many
    ///     <c>moveR</c> phrase iterations the importer moved on the way.
    /// </summary>
    public static (ChartProject Project, int MoveRPhrases) LoadChart(string xmlPath)
    {
        var doc = SafeXml.LoadFromFile(xmlPath);
        // Counted on a copy: the reader resolves the phrases itself, and one that landed on a beat would move again.
        var moved = RsXmlPhraseMover.ResolveForImport(new XDocument(doc)).Count;
        var project = new RsXmlSongReader { MergeSimultaneousNotes = true }.ReadDocument(doc, xmlPath);
        return (project, moved);
    }

    /// <summary>The chart's sections in order.</summary>
    public static List<ChartSection> Resolve(Arrangement arrangement) =>
    [
        .. arrangement.EventPoints.Where(e => e.Section.Length > 0).OrderBy(e => e.Position)
            .Select(e => new ChartSection(e.Section, e.Position))
    ];
}
