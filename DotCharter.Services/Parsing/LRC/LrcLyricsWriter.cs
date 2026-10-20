using System.Globalization;
using System.Text;

namespace DotCharter.Services.Parsing.Lrc;

/// <summary>
/// Formats DotCharter's vocal phrase model back into an enhanced (word-timed)
/// <c>.lrc</c> document. This is the export counterpart to
/// <see cref="LrcLyricsParser"/>; the two round-trip syllable <em>start</em>
/// times within the beat-grid snap tolerance.
/// <para>
/// Each <see cref="LyricPhrase"/> becomes one LRC line whose line stamp
/// (<c>[mm:ss.xx]</c>) is the first syllable's start, followed by one inline
/// <c>&lt;mm:ss.xx&gt;</c> stamp per syllable. Word continuation
/// (<see cref="LyricEvent.IsWordPart"/>) is encoded the same way the parser
/// reads it: a <c>-</c> separator joins a syllable to the next, a space starts
/// a new word. Classic LRC has no duration channel, so syllable end positions
/// are intentionally not emitted (they are reconstructed by the parser as the
/// gap to the following syllable).
/// </para>
/// Pure: no I/O, no UI. The caller owns reading/writing files.
/// </summary>
public sealed class LrcLyricsWriter
{
    /// <summary>
    /// Writes every phrase of <paramref name="track"/> as enhanced LRC.
    /// Optional <paramref name="title"/>/<paramref name="artist"/> are emitted
    /// as ID tags when supplied.
    /// </summary>
    public string Write(VocalTrack track, IList<BeatMarker> beats, string? title = null, string? artist = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        return Write(track.Phrases, beats, title, artist);
    }

    public string Write(IEnumerable<LyricPhrase> phrases, IList<BeatMarker> beats,
        string? title = null, string? artist = null)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        ArgumentNullException.ThrowIfNull(beats);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) sb.Append("[ti:").Append(title.Trim()).Append(']').Append('\n');
        if (!string.IsNullOrWhiteSpace(artist)) sb.Append("[ar:").Append(artist.Trim()).Append(']').Append('\n');

        // Phrases are emitted in time order so a re-import lands them the same
        // way regardless of the authoring order in the track.
        var ordered = phrases
            .Where(p => p.Lyrics.Count > 0)
            .OrderBy(p => p.Position)
            .ToList();

        foreach (var phrase in ordered)
        {
            var lyrics = phrase.Lyrics
                .Where(l => !string.IsNullOrWhiteSpace(l.Text))
                .OrderBy(l => l.Position)
                .ToList();
            if (lyrics.Count == 0) continue;

            var lineStartMs = TimelineMath.CalculateTimeMs(lyrics[0].Position, beats);
            sb.Append('[').Append(FormatTimestamp(lineStartMs)).Append(']');

            for (var i = 0; i < lyrics.Count; i++)
            {
                var lyric = lyrics[i];
                var startMs = TimelineMath.CalculateTimeMs(lyric.Position, beats);
                sb.Append('<').Append(FormatTimestamp(startMs)).Append('>');
                sb.Append(lyric.Text.Trim());

                // Separator to the next syllable: '-' keeps the word intact,
                // ' ' starts a new word. Nothing after the final syllable so a
                // trailing dash never re-imports as a spurious word part.
                if (i < lyrics.Count - 1)
                    sb.Append(lyric.IsWordPart ? "-" : " ");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Formats a millisecond offset as an LRC <c>mm:ss.xx</c> stamp (centisecond precision).</summary>
    internal static string FormatTimestamp(double ms)
    {
        if (ms < 0) ms = 0;
        var totalCentis = (long)Math.Round(ms / 10.0, MidpointRounding.AwayFromZero);
        var centis = (int)(totalCentis % 100);
        var totalSeconds = totalCentis / 100;
        var seconds = (int)(totalSeconds % 60);
        var minutes = (int)(totalSeconds / 60);
        return string.Create(CultureInfo.InvariantCulture, $"{minutes:D2}:{seconds:D2}.{centis:D2}");
    }
}
