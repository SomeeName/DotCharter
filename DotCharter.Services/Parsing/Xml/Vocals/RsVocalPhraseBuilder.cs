namespace DotCharter.Services.Parsing.Xml.Vocals;

/// <summary>One RS vocal as either reader finds it: times in ms, the raw marked lyric, the MIDI note.</summary>
internal readonly record struct RsVocalSyllable(double TimeMs, double EndMs, string Lyric, int Note);

/// <summary>
///     Turns a run of RS vocals into lyric phrases — the ONE rule both RS readers
///     share (the package source for the SNG and
///     <see cref="RsXmlVocalsReader" /> for <c>vocals.xml</c>), so a song reads the
///     same from either file.
///     <para>
///         A phrase ends after a syllable whose lyric ends in <c>+</c> (RS's line
///         break), and a new one also starts after a silence longer than
///         <see cref="PhraseGapMs" />, for files charted without any <c>+</c>.
///         The marker is stripped by <see cref="RsVocalLyricEncoding.Decode" />.
///     </para>
/// </summary>
internal static class RsVocalPhraseBuilder
{
    /// <summary>A silence this long starts a new phrase even without a <c>+</c>.</summary>
    public const double PhraseGapMs = 5000.0;

    public static List<LyricPhrase> Build(IEnumerable<RsVocalSyllable> syllables,
        Func<double, FractionalPosition> positionFromMs)
    {
        var phrases = new List<LyricPhrase>();
        LyricPhrase? current = null;
        double lastEndMs = -1;

        foreach (var syllable in syllables.OrderBy(s => s.TimeMs))
        {
            if (string.IsNullOrWhiteSpace(syllable.Lyric)) continue;

            if (current == null || syllable.TimeMs - lastEndMs > PhraseGapMs)
            {
                current = new LyricPhrase { Position = positionFromMs(syllable.TimeMs) };
                phrases.Add(current);
            }

            var decoded = RsVocalLyricEncoding.Decode(syllable.Lyric);
            var lyric = new LyricEvent
            {
                Position = positionFromMs(syllable.TimeMs),
                EndPosition = positionFromMs(syllable.EndMs),
                Note = syllable.Note,
                Text = decoded.Text,
                IsWordPart = decoded.IsWordPart,
                IsPhraseEnd = decoded.IsPhraseEnd
            };

            current.Lyrics.Add(lyric);
            current.EndPosition = lyric.EndPosition;
            lastEndMs = syllable.EndMs;

            if (decoded.IsPhraseEnd) current = null;
        }

        return phrases;
    }
}
