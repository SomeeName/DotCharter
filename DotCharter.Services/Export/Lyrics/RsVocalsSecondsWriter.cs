using System.Xml;
using System.Xml.Linq;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Services.Parsing.Xml.Vocals;

namespace DotCharter.Services.Export.Lyrics;

/// <summary>
///     Writes an alignment straight from SECONDS into a Rocksmith 2014
///     <c>&lt;vocals&gt;</c> document, with no chart and no beat grid.
///     <para>
///         <b>The beat grid is the only thing this drops, and it is deliberate.</b>
///         <see cref="RsXmlVocalsWriter" /> converts a
///         <see cref="LyricEvent" />'s <see cref="FractionalPosition" /> back to
///         milliseconds through the project's beats; a standalone alignment has no
///         project, and inventing a synthetic grid to route through the same writer
///         would add a quantisation error to times that are already exact. Both
///         writers share the ONE thing that must not drift — the <c>-</c>/<c>+</c>
///         convention (<see cref="RsVocalLyricEncoding" />) — and the same
///         <see cref="Fmt" /> formatting, so their output is byte-compatible.
///     </para>
///     <para>
///         <b>Three shapes are load-bearing for Editor on Fire's importer, which
///         reads this file LINE BY LINE rather than with an XML parser.</b> The
///         literal <c>&lt;vocals count</c> must be present (it is the format
///         detection); there must be exactly one <c>&lt;vocal/&gt;</c> per line;
///         and all four of <c>time</c>, <c>note</c>, <c>length</c> and
///         <c>lyric</c> must be on every entry or the import aborts. RsCharter
///         parses it properly but needs <c>time</c>/<c>length</c> at EXACTLY three
///         decimals — its converter parses the string with the dot deleted, so
///         <c>"12.34"</c> would read as 1,234 ms. <see cref="Fmt.Time" /> is
///         already that format.
///     </para>
/// </summary>
public static class RsVocalsSecondsWriter
{
    /// <summary>
    ///     The <c>note</c> every syllable gets when the caller has no pitch.
    ///     <para>
    ///         Both 254 and 255 are out-of-range sentinels meaning "no pitch"; EOF's
    ///         own exports write 255 and it stores the track as pitchless whenever
    ///         every note is identical, so either reads back the same. 254 is what
    ///         stock Rocksmith vocal arrangements carry.
    ///     </para>
    /// </summary>
    public const int PitchlessNote = 254;

    /// <summary>Renders the document. Pure — the caller owns the file.</summary>
    /// <param name="syllables">The alignment, in singing order.</param>
    /// <param name="note">Pitch for every syllable; see <see cref="PitchlessNote" />.</param>
    public static XDocument Write(IReadOnlyList<LyricSyncSyllable> syllables, int note = PitchlessNote)
    {
        ArgumentNullException.ThrowIfNull(syllables);

        var elements = new List<XElement>(syllables.Count);
        for (var i = 0; i < syllables.Count; i++)
        {
            var s = syllables[i];
            var timeMs = ToMs(s.StartSeconds);

            // Never negative and never overlapping the next onset: the aligner's
            // spans are non-overlapping in FRAMES, but the onset bias moves starts
            // and not ends, so a large bias can cross them.
            var endMs = ToMs(s.EndSeconds);
            if (i + 1 < syllables.Count) endMs = Math.Min(endMs, ToMs(syllables[i + 1].StartSeconds));

            elements.Add(new XElement("vocal",
                new XAttribute("time", Fmt.Time(timeMs)),
                new XAttribute("note", note),
                new XAttribute("length", Fmt.Time(Math.Max(0, endMs - timeMs))),
                new XAttribute("lyric", RsVocalLyricEncoding.Encode(s.Text, s.ContinuesWord, s.EndsLine))));
        }

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            Fmt.CountedList("vocals", elements));
    }

    /// <summary>Renders to a UTF-8 file, indented one entry per line.</summary>
    public static void WriteTo(
        string filePath, IReadOnlyList<LyricSyncSyllable> syllables, int note = PitchlessNote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var document = Write(syllables, note);
        using var writer = XmlWriter.Create(filePath, Fmt.GetWriterSettings());
        document.WriteTo(writer);
    }

    /// <summary>
    ///     Seconds → whole milliseconds. Rounds rather than truncates: truncation
    ///     biases every onset EARLY by up to a millisecond, in the same direction
    ///     for the whole song.
    /// </summary>
    private static int ToMs(double seconds) =>
        (int)Math.Round(Math.Max(0.0, seconds) * 1000.0, MidpointRounding.AwayFromZero);
}
