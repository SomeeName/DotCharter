using System.Text;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Parsing.Lrc;

namespace DotCharter.Services.Export.Lyrics;

/// <summary>
///     Writes an alignment straight from SECONDS as enhanced (word-timed) LRC.
///     <para>
///         The chart-side counterpart is <see cref="LrcLyricsWriter" />; this is
///         the same document shape with the beat grid taken out, and it shares that
///         writer's <see cref="LrcLyricsWriter.FormatTimestamp" /> so the stamp
///         format cannot drift between the two.
///     </para>
///     <para>
///         One line per phrase: a line stamp <c>[mm:ss.xx]</c> at the first
///         syllable's onset, then one <c>&lt;mm:ss.xx&gt;</c> before each syllable.
///         A <c>-</c> separator joins syllables inside a word and a space starts a
///         new one — the same convention <see cref="LrcLyricsParser" /> reads back.
///     </para>
///     <para>
///         <b>Classic LRC has no duration channel, so ends are not emitted.</b> A
///         reader reconstructs a syllable's end as the gap to the next stamp; the
///         aligner's own end (the last frame it could still hear the syllable) has
///         nowhere to go. That is the format's limit, not a shortcut — a consumer
///         that needs ends wants the Rocksmith XML
///         (<see cref="RsVocalsSecondsWriter" />).
///     </para>
///     <para>
///         <b>RsCharter will not read this and that is not fixable here.</b> Its
///         LRC importer splits the line on spaces and only recognises a
///         <c>&lt;mm:ss.xx&gt;</c> that is its own whitespace-delimited token,
///         which is not the conventional enhanced-LRC form every player uses.
///         Editor on Fire's parser is the standard-conformant one. Send RsCharter
///         the vocals XML.
///     </para>
/// </summary>
public static class LrcSecondsWriter
{
    /// <summary>Renders the document. Pure — the caller owns the file.</summary>
    /// <param name="syllables">The alignment, in singing order.</param>
    /// <param name="title">Optional <c>[ti:]</c> tag.</param>
    /// <param name="artist">Optional <c>[ar:]</c> tag.</param>
    public static string Write(
        IReadOnlyList<LyricSyncSyllable> syllables, string? title = null, string? artist = null)
    {
        ArgumentNullException.ThrowIfNull(syllables);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) sb.Append("[ti:").Append(title.Trim()).Append("]\n");
        if (!string.IsNullOrWhiteSpace(artist)) sb.Append("[ar:").Append(artist.Trim()).Append("]\n");

        var line = new List<LyricSyncSyllable>();
        foreach (var syllable in syllables)
        {
            if (string.IsNullOrWhiteSpace(syllable.Text)) continue;
            line.Add(syllable);

            // The sheet's own line break is the authority — never the hyphens, and
            // never a gap heuristic. A sheet whose last line carries no explicit
            // end still has to be flushed, which is what the tail below does.
            if (!syllable.EndsLine) continue;
            AppendLine(sb, line);
            line.Clear();
        }

        if (line.Count > 0) AppendLine(sb, line);

        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, List<LyricSyncSyllable> line)
    {
        sb.Append('[').Append(Stamp(line[0].StartSeconds)).Append(']');

        for (var i = 0; i < line.Count; i++)
        {
            sb.Append('<').Append(Stamp(line[i].StartSeconds)).Append('>');
            sb.Append(line[i].Text.Trim());

            // Nothing after the final syllable, so a trailing dash never
            // re-imports as a spurious word part.
            if (i < line.Count - 1) sb.Append(line[i].ContinuesWord ? "-" : " ");
        }

        sb.Append('\n');
    }

    private static string Stamp(double seconds) =>
        LrcLyricsWriter.FormatTimestamp(Math.Max(0.0, seconds) * 1000.0);
}
