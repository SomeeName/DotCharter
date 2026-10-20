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
    public XDocument Write(ChartProject project)
    {
        var beats = project.Beats;
        var allLyrics = new List<(int TimeMs, int LengthMs, string Lyric, int Note)>();

        foreach (var track in project.VocalTracks)
        {
            foreach (var phrase in track.Phrases)
            {
                for (var i = 0; i < phrase.Lyrics.Count; i++)
                {
                    var lev = phrase.Lyrics[i];
                    var timeMs = TimelineMath.ToWholeMs(TimelineMath.CalculateTimeMs(lev.Position, beats));
                    var endMs = TimelineMath.ToWholeMs(TimelineMath.CalculateTimeMs(lev.EndPosition, beats));
                    var lengthMs = Math.Max(0, endMs - timeMs);
                    allLyrics.Add((timeMs, lengthMs, EncodeLyric(lev, isLastInPhrase: i == phrase.Lyrics.Count - 1), lev.Note));
                }
            }
        }

        allLyrics.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

        var vocalEls = allLyrics.Select(l => new XElement("vocal",
            new XAttribute("time", Fmt.Time(l.TimeMs)),
            new XAttribute("length", Fmt.Time(l.LengthMs)),
            new XAttribute("lyric", l.Lyric),
            new XAttribute("note", l.Note)));

        var root = Fmt.CountedList("vocals", vocalEls);
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
    }

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
    ///         A syllable that is both a word part and the phrase end keeps the word
    ///         join — a phrase cannot end mid-word, and RS reads only one marker.
    ///     </para>
    /// </summary>
    private static string EncodeLyric(LyricEvent lyric, bool isLastInPhrase)
        => RsVocalLyricEncoding.Encode(
            lyric.Text, lyric.IsWordPart, lyric.IsPhraseEnd || isLastInPhrase);

    public void WriteTo(ChartProject project, string filePath)
    {
        var doc = Write(project);
        using var writer = XmlWriter.Create(filePath, Fmt.GetWriterSettings());
        doc.WriteTo(writer);
    }
}
