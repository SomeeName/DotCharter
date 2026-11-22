namespace DotCharter.Services.Parsing.Xml.Vocals;

/// <summary>
///     The one place vocal STRUCTURE becomes a suffix on the lyric string.
///     <para>
///         RS2014 has no word or phrase element: a trailing <c>-</c> means "this
///         syllable joins the next one into a word", and a trailing <c>+</c> marks
///         the last syllable of a phrase — the <c>+</c> IS the line break in the
///         in-game display, and in Editor on Fire it is what creates a lyric LINE
///         at all. A file with fewer than two <c>+</c> markers makes EOF fall back
///         to guessing line breaks from capitalisation and gaps, and say so in a
///         modal.
///     </para>
///     <para>
///         <b>Shared because there are now two writers.</b>
///         <see cref="RsXmlVocalsWriter" /> writes a chart's vocal track on the
///         beat grid; <c>RsVocalsSecondsWriter</c> writes an alignment straight
///         from seconds for the standalone CLI. Two copies of this rule would
///         drift, and the failure is silent — a missing <c>+</c> does not break an
///         import, it just loses every line break.
///     </para>
/// </summary>
public static class RsVocalLyricEncoding
{
    /// <summary>
    ///     Appends the structural suffix, if any.
    ///     <para>
    ///         A syllable that is BOTH a word part and the phrase end is written as
    ///         the phrase end: RS reads only one marker, and the line break is the
    ///         one that changes the lines when the file is read back.
    ///     </para>
    ///     <para>
    ///         <b>A line end is never lost:</b> a syllable that ends its phrase is written
    ///         with <c>+</c> whatever its text ends in, so a stored <c>hel-</c> goes out as
    ///         <c>hel-+</c> and reads back as the same text ending the same line.
    ///     </para>
    ///     <para>
    ///         <b>A word part is never lost either:</b> its <c>-</c> is appended unless the text
    ///         already ends in one, so a word part "a+" goes out as <c>a+-</c>.
    ///     </para>
    ///     <para>
    ///         <b>Otherwise inside a line there is no escape:</b> text that already ends in
    ///         <c>-</c> or <c>+</c> is written as it is, and <see cref="Decode" /> reads that last
    ///         character as a marker. "a-" that joins no word comes back as a word part and "a+" as
    ///         a line end — a new line break; a word part "a-" keeps its structure but comes back
    ///         as "a", the hyphen gone. Nothing is normalised on the way out: every syllable whose
    ///         text does not survive (<see cref="LosesText" />) is reported by the export check
    ///         (<c>ChartExportValidator.KeyLyricMarkerText</c>), so the charter decides.
    ///     </para>
    /// </summary>
    /// <param name="text">The syllable as stored — the hyphen is input syntax, never stored text.</param>
    /// <param name="continuesWord">The next syllable finishes the same word.</param>
    /// <param name="endsPhrase">This is the last syllable of its phrase.</param>
    /// <returns>The <c>lyric</c> attribute value.</returns>
    public static string Encode(string? text, bool continuesWord, bool endsPhrase)
    {
        var value = text ?? "";
        if (endsPhrase) return value + "+";
        if (continuesWord && !value.EndsWith('-')) return value + "-";
        return value;
    }

    /// <summary>
    ///     The text a syllable is written with: trailing spaces are not text (the word gap is
    ///     <see cref="LyricEvent.IsWordPart" />'s job), and a syllable with nothing left is not
    ///     written at all.
    /// </summary>
    public static string WrittenText(LyricEvent lyric) => (lyric.Text ?? "").TrimEnd(' ');

    /// <summary>
    ///     THE line rule both vocal writers follow (the vocals XML and the package's vocals SNG),
    ///     and the export check with them: the phrase's written syllables (<see cref="WrittenText" />
    ///     not empty) in the phrase's own order, each with its encoded token; a syllable ends the
    ///     line when its flag says so or it is the last WRITTEN one (the phrase IS the line, flag
    ///     or not). A blank syllable is not written, so the line end it carries passes to the last
    ///     WRITTEN syllable before it in that line; with none (the line would be empty) it is dropped.
    /// </summary>
    public static IReadOnlyList<RsVocalToken> EncodeLine(LyricPhrase phrase)
    {
        var written = new List<(LyricEvent Lyric, bool EndsLine)>(phrase.Lyrics.Count);
        var lineHasWritten = false;
        foreach (var lyric in phrase.Lyrics)
        {
            if (WrittenText(lyric).Length > 0)
            {
                written.Add((lyric, lyric.IsPhraseEnd));
                lineHasWritten = !lyric.IsPhraseEnd;
            }
            else if (lyric.IsPhraseEnd)
            {
                if (lineHasWritten) written[^1] = (written[^1].Lyric, true);
                lineHasWritten = false;
            }
        }

        var tokens = new List<RsVocalToken>(written.Count);
        for (var i = 0; i < written.Count; i++)
        {
            var (lyric, endsLine) = written[i];
            tokens.Add(new RsVocalToken(lyric, Encode(WrittenText(lyric), lyric.IsWordPart, endsLine || i == written.Count - 1)));
        }

        return tokens;
    }

    /// <summary>
    ///     The one place the two files order their vocals differently, a format limit: the XML
    ///     keeps each line together, the package lists every vocal by time (stable, so equal
    ///     times keep the line order). The export check blocks a chart where the two orders
    ///     differ (<c>ChartExportValidator.KeyLyricsOutOfOrder</c>: a line out of time order, or two
    ///     lines that overlap).
    /// </summary>
    public static IEnumerable<T> InPackageOrder<T>(IEnumerable<T> vocals, Func<T, float> time) => vocals.OrderBy(time);

    /// <summary>
    ///     True when the syllable as written reads back with a different text: its text ends in
    ///     a marker the writer does not follow with a marker of its own. A syllable that is not
    ///     written loses nothing here.
    /// </summary>
    public static bool LosesText(LyricPhrase phrase, int index)
    {
        var lyric = phrase.Lyrics[index];
        foreach (var token in EncodeLine(phrase))
            if (ReferenceEquals(token.Syllable, lyric)) return Decode(token.Lyric).Text != WrittenText(lyric);

        return false;
    }

    /// <summary>
    ///     The inverse of <see cref="Encode(string?, bool, bool)" />: splits a <c>lyric</c> attribute (or
    ///     an SNG vocal's lyric) into the stored syllable and its two flags.
    ///     <para>
    ///         Both RS readers — the psarc's SNG path and the RS-XML <c>vocals</c>
    ///         path — go through this, so the same song reads the same way from
    ///         either file. An XML reader that kept the marker in the text and
    ///         never split phrases would put <c>Hel-</c> / <c>world+</c> in the
    ///         lyric lane and the whole song on one line.
    ///     </para>
    ///     <para>
    ///         Exactly ONE trailing marker is removed — <see cref="Encode(string?, bool, bool)" /> appends
    ///         at most one — and no space is added: the word gap is
    ///         <see cref="LyricEvent.IsWordPart" />'s job, and every consumer
    ///         (karaoke line, LRC/UltraStar writers) appends it from the flag.
    ///     </para>
    /// </summary>
    public static RsVocalLyric Decode(string? lyric)
    {
        var value = lyric ?? "";
        if (value.EndsWith('-')) return new RsVocalLyric(value[..^1], IsWordPart: true, IsPhraseEnd: false);
        if (value.EndsWith('+')) return new RsVocalLyric(value[..^1], IsWordPart: false, IsPhraseEnd: true);
        return new RsVocalLyric(value, IsWordPart: false, IsPhraseEnd: false);
    }
}

/// <summary>One decoded RS syllable: the stored text and the structure its marker carried.</summary>
public readonly record struct RsVocalLyric(string Text, bool IsWordPart, bool IsPhraseEnd);

/// <summary>One written syllable of a line and the <c>lyric</c> token both vocal files carry for it.</summary>
public readonly record struct RsVocalToken(LyricEvent Syllable, string Lyric);
