namespace DotCharter.Services.Parsing.Xml.Vocals;

/// <summary>One RS vocal as either reader finds it: times in ms, the raw marked lyric, the MIDI note.</summary>
internal readonly record struct RsVocalSyllable(double TimeMs, double EndMs, string Lyric, int Note);

/// <summary>How <see cref="RsVocalPhraseBuilder" /> finds the line breaks of a run of vocals.</summary>
internal enum RsVocalLineBreaks
{
    /// <summary>
    ///     A Rocksmith file: syllables in file order, a <c>+</c> ends a line, and a long
    ///     silence ends one only when no syllable in the file carries a <c>+</c>.
    /// </summary>
    MarkersElseGaps,

    /// <summary>
    ///     An Editor on Fire file saved without line sections: syllables in time order, and
    ///     both a <c>+</c> and a long silence end a line.
    /// </summary>
    MarkersAndGaps
}

/// <summary>
///     Turns a run of RS vocals into lyric phrases — the ONE rule both RS readers
///     share (the package source for the SNG and
///     <see cref="RsXmlVocalsReader" /> for <c>vocals.xml</c>), so a song reads the
///     same from either file.
///     <para>
///         A phrase ends after a syllable whose lyric ends in <c>+</c> (RS's line
///         break). A file that carries a <c>+</c> anywhere keeps exactly the lines its
///         markers say; only a file charted without any <c>+</c> also starts a new
///         phrase after a silence longer than <see cref="PhraseGapMs" />. The file's
///         order is kept, so a line whose syllables are not in time order reads back
///         as the same line. The marker is stripped by <see cref="RsVocalLyricEncoding.Decode" />.
///     </para>
/// </summary>
internal static class RsVocalPhraseBuilder
{
    /// <summary>A silence this long starts a new phrase in a file without any <c>+</c>.</summary>
    public const double PhraseGapMs = 5000.0;

    public static List<LyricPhrase> Build(IEnumerable<RsVocalSyllable> syllables,
        Func<double, FractionalPosition> positionFromMs)
        => Build(syllables, positionFromMs, RsVocalLineBreaks.MarkersElseGaps);

    public static List<LyricPhrase> Build(IEnumerable<RsVocalSyllable> syllables,
        Func<double, FractionalPosition> positionFromMs, RsVocalLineBreaks lineBreaks)
    {
        var phrases = new List<LyricPhrase>();
        LyricPhrase? current = null;
        double lastEndMs = -1;
        var ordered = lineBreaks == RsVocalLineBreaks.MarkersAndGaps
            ? syllables.OrderBy(s => s.TimeMs).ToList()
            : syllables.ToList();
        var splitAtGaps = lineBreaks == RsVocalLineBreaks.MarkersAndGaps
                          || !ordered.Any(s => RsVocalLyricEncoding.Decode(s.Lyric).IsPhraseEnd);

        foreach (var syllable in ordered)
        {
            if (string.IsNullOrWhiteSpace(syllable.Lyric)) continue;

            if (current == null || (splitAtGaps && syllable.TimeMs - lastEndMs > PhraseGapMs))
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

        // Gap-derived lines and the final unmarked line have boundaries too.
        foreach (var phrase in phrases) phrase.Lyrics[^1].IsPhraseEnd = true;
        return phrases;
    }
}
