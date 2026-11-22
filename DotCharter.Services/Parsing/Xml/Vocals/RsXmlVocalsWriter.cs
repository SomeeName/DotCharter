using System.Xml;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Parsing.Xml.Vocals;

/// <summary>
/// Writes a vocal track from a <see cref="ChartProject"/> into an RS-XML
/// <c>&lt;vocals&gt;</c> document, byte-compatible with rscharter's XStream output.
/// </summary>
internal sealed class RsXmlVocalsWriter
{
    /// <summary>
    ///     RS2014 encodes vocal structure INSIDE the lyric string: a trailing
    ///     <c>-</c> means "this syllable joins the next one into a word", and a
    ///     trailing <c>+</c> marks the last syllable of a phrase — the <c>+</c> IS
    ///     the line break in the in-game display.
    ///     <para>
    ///         Neither was written. The text went out verbatim while the nested loop
    ///         flattened every <see cref="LyricPhrase" /> away, so an exported song
    ///         showed one continuous run of separated syllables with no words and no
    ///         line breaks. The SNG export
    ///         (<c>SngFromDomainMapper</c>) has always encoded both, and the rscp
    ///         writer persists the two flags, so only this writer lost them.
    ///     </para>
    ///     <para>
    ///         The rule for a syllable that is both a word part and the phrase end (a
    ///         project file may carry both): it is written as the phrase end, so the
    ///         file reads back with the same lines. A syllable ends its line by its flag or
    ///         by being the last WRITTEN syllable of its phrase
    ///         (<see cref="RsVocalLyricEncoding.EncodeLine" />, the rule the vocals SNG and the export
    ///         check read too).
    ///     </para>
    /// </summary>
    public XDocument Write(ChartProject project)
    {
        var beats = project.Beats;
        var lines = new List<List<(int TimeMs, int LengthMs, string Lyric, int Note)>>();

        foreach (var track in project.VocalTracks)
        {
            foreach (var phrase in track.Phrases)
            {
                var line = new List<(int TimeMs, int LengthMs, string Lyric, int Note)>();
                foreach (var token in RsVocalLyricEncoding.EncodeLine(phrase))
                {
                    var lev = token.Syllable;
                    var timeMs = TimelineMath.ToWholeMs(TimelineMath.CalculateTimeMs(lev.Position, beats));
                    var endMs = TimelineMath.ToWholeMs(TimelineMath.CalculateTimeMs(lev.EndPosition, beats));
                    var lengthMs = Math.Max(0, endMs - timeMs);
                    line.Add((timeMs, lengthMs, token.Lyric, lev.Note));
                }

                if (line.Count > 0) lines.Add(line);
            }
        }

        // Line membership wins over time order: the lines go out ordered by their first
        // syllable and each keeps its own order, so a line whose syllables are not in time
        // order still reads back as one line (the export check reports it as an error).
        var allLyrics = lines.OrderBy(l => l[0].TimeMs).SelectMany(l => l).ToList();

        var vocalEls = allLyrics.Select(l => new XElement("vocal",
            new XAttribute("time", Fmt.Time(l.TimeMs)),
            new XAttribute("length", Fmt.Time(l.LengthMs)),
            new XAttribute("lyric", l.Lyric),
            new XAttribute("note", l.Note)));

        var root = Fmt.CountedList("vocals", vocalEls);
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
    }

    public void WriteTo(ChartProject project, string filePath)
    {
        var doc = Write(project);
        using var writer = XmlWriter.Create(filePath, Fmt.GetWriterSettings());
        doc.WriteTo(writer);
    }
}
