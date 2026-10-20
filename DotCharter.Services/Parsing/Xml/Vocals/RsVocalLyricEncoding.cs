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
    ///         A syllable that is BOTH a word part and the phrase end keeps the
    ///         word join — a phrase cannot end mid-word, and RS reads only one
    ///         marker. Text that already carries a marker (round-tripped from an RS
    ///         import) is left alone.
    ///     </para>
    /// </summary>
    /// <param name="text">The syllable as stored — the hyphen is input syntax, never stored text.</param>
    /// <param name="continuesWord">The next syllable finishes the same word.</param>
    /// <param name="endsPhrase">This is the last syllable of its phrase.</param>
    /// <returns>The <c>lyric</c> attribute value.</returns>
    public static string Encode(string? text, bool continuesWord, bool endsPhrase)
    {
        var value = text ?? "";
        if (value.EndsWith('-') || value.EndsWith('+')) return value;

        if (continuesWord) return value + "-";
        if (endsPhrase) return value + "+";
        return value;
    }

    /// <summary>
    ///     The inverse of <see cref="Encode" />: splits a <c>lyric</c> attribute (or
    ///     an SNG vocal's lyric) into the stored syllable and its two flags.
    ///     <para>
    ///         Both RS readers — the psarc's SNG path and the RS-XML <c>vocals</c>
    ///         path — go through this, so the same song reads the same way from
    ///         either file. An XML reader that kept the marker in the text and
    ///         never split phrases would put <c>Hel-</c> / <c>world+</c> in the
    ///         lyric lane and the whole song on one line.
    ///     </para>
    ///     <para>
    ///         Exactly ONE trailing marker is removed — <see cref="Encode" /> appends
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
