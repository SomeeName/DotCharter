namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
/// What the six-string projection of a 7-/8-string guitar left out: the notes
///     on the extra strings (loose notes and the ones a chord played there), the chords
///     that sounded ONLY on extra strings, and the chords that lost some of their strings
///     but stay. Counted at the export boundary, so the chart itself is never touched.
/// </summary>
internal sealed record SixStringLoss(int Notes, int Chords, int TrimmedChords)
{
    public bool IsEmpty => Notes == 0 && Chords == 0 && TrimmedChords == 0;

    /// <summary>The one warning line the log carries for an arrangement.</summary>
    public string Describe(Arrangement arrangement) =>
        $"'{arrangement.Name}' is a {arrangement.StringCount}-string guitar: the six-string projection for " +
        $"EOF/DLC Builder dropped {Notes} notes on the extra strings and {Chords} chords that only sound there; " +
        $"{TrimmedChords} more chords lost their extra-string notes.";
}
