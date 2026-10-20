namespace DotCharter.Services.Parsing;

/// <summary>
///     The ONE track-name → <see cref="ArrangementType"/> heuristic every tab
///     import path shares: EOF (<c>EofProGuitarTrackParser.InferArrangementType</c>
///     delegates here), the gpif importer, the GP3-5 binary importer and the
///     Songsterr converter. The three GP paths must not each hard-code
///     <c>isBass ? Bass : Lead</c>: a five-track GP5 with two tracks NAMED
///     "Rhythm (…)" would import every guitar as Lead — and a psarc built from that
///     chart would ship the wrong Rocksmith route (<c>pathLead</c> for a rhythm part),
///     which is export correctness, not cosmetics.
///     <para>
///     Keyword order is the package reader's SNG mapper's (the
///     Rocksmith-manifest reader): bass → rhythm → combo → vocal, then lead, then
///     the caller's fallback. Vocal before lead on purpose — "Lead Vocals" is a
///     vocal track. The FALLBACK carries each format's content signal (string
///     count for the GP paths, the Songsterr <c>instrument</c> field), so a name
///     with no keyword still lands on the best available answer instead of a
///     hard-coded Lead.
///     </para>
/// </summary>
public static class ArrangementTypeHeuristics
{
    /// <summary>
    ///     Derives the arrangement type from a track's display name;
    ///     <paramref name="fallback"/> when the name carries no keyword.
    /// </summary>
    public static ArrangementType FromTrackName(string? trackName,
        ArrangementType fallback = ArrangementType.Lead)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return fallback;

        // Separators normalized so "REAL_BASS" / "rhythm-guitar" read the same
        // as their spaced spellings (EOF track names use underscores).
        var normalized = trackName
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();

        if (normalized.Contains("bass", StringComparison.OrdinalIgnoreCase))
            return ArrangementType.Bass;
        if (normalized.Contains("rhythm", StringComparison.OrdinalIgnoreCase))
            return ArrangementType.Rhythm;
        if (normalized.Contains("combo", StringComparison.OrdinalIgnoreCase))
            return ArrangementType.Combo;
        if (normalized.Contains("vocal", StringComparison.OrdinalIgnoreCase))
            return ArrangementType.Vocal;
        if (normalized.Contains("lead", StringComparison.OrdinalIgnoreCase))
            return ArrangementType.Lead;

        return fallback;
    }
}
