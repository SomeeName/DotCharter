using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     The one answer to "what are this arrangement's per-string semitone
///     offsets?", for every consumer that writes a tuning out.
/// </summary>
/// <remarks>
///     <para>
///         <b>A chart states its tuning TWICE.</b> <see cref="Arrangement.Tuning" />
///         holds note LETTERS (what the editor shows, what Song Select stores, what
///         <see cref="TuningNameResolver" /> reads) and
///         <see cref="Arrangement.TuningPitches" /> holds per-string OFFSETS (what
///         RS-XML, SNG, Guitar Pro and the <c>.rs2dlc</c> all write). Every exporter
///         read the offsets directly with a <c>0</c> default for a missing entry —
///         so an arrangement carrying only letters exported as <b>E standard</b>,
///         silently, in four places at once.
///     </para>
///     <para>
///         This is the reconciliation, and it is deliberately CONSERVATIVE: the
///         offsets win whenever there are any, so every chart that has them exports
///         byte-for-byte as before. Only a completely empty offsets vector falls
///         back to deriving from the letters.
///     </para>
///     <para>
///         <b>And that derivation is restricted to 4- and 6-string instruments on
///         purpose.</b> A 5-string bass offset has no agreed meaning — GP measures
///         it against B-E-A-D-G and the RS SNG path against E-A-D-G-B, which is the
///         same ambiguity <see cref="TuningUtilities.GetTuningNames" /> documents and
///         refuses to resolve. Guessing there would write a WRONG tuning into a
///         shipped package, which is strictly worse than the zeros it replaces. For
///         every other string count this returns the zeros the callers already used,
///         so nothing regresses; the honest answer is that such a chart has to
///         author its offsets.
///     </para>
/// </remarks>
public static class TuningOffsetResolution
{
    /// <summary>Standard open-string pitch classes, low → high, for the two string
    /// counts whose offset base is unambiguous.</summary>
    private static readonly int[] Guitar6Std = [4, 9, 2, 7, 11, 4]; // E A D G B E
    private static readonly int[] Bass4Std = [4, 9, 2, 7];          // E A D G

    /// <summary>
    ///     Per-string offsets, low → high. Never null; may be empty when the
    ///     arrangement says nothing at all.
    /// </summary>
    public static IReadOnlyList<int> Resolve(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);

        if (arrangement.TuningPitches.Count > 0)
        {
            var authored = new int[arrangement.TuningPitches.Count];
            for (var i = 0; i < authored.Length; i++) authored[i] = arrangement.TuningPitches[i].Step;
            return authored;
        }

        return DeriveFromNoteNames(arrangement.Tuning) ?? [];
    }

    /// <summary>The offset for one string, or <c>0</c> when the arrangement does not
    /// say — which is what every caller's own default already was.</summary>
    public static int For(Arrangement arrangement, int stringIndex)
    {
        var offsets = Resolve(arrangement);
        return stringIndex >= 0 && stringIndex < offsets.Count ? offsets[stringIndex] : 0;
    }

    /// <summary>
    ///     Offsets from note letters, or null when the base is ambiguous or the
    ///     letters are unreadable.
    /// </summary>
    /// <remarks>
    ///     Each offset is the signed distance to the standard open string in
    ///     <c>[-6, +5]</c>, so re-deriving the letters from the result reproduces the
    ///     input exactly — the round trip is exact by construction, and
    ///     <see cref="TuningNameResolver" /> therefore names the derived offsets the
    ///     same way it names the letters they came from.
    /// </remarks>
    public static int[]? DeriveFromNoteNames(IReadOnlyList<string>? noteNames)
    {
        if (noteNames is not { Count: > 0 }) return null;

        var standard = noteNames.Count switch
        {
            6 => Guitar6Std,
            4 => Bass4Std,
            _ => null // see the class remarks: a guess here ships a wrong tuning
        };
        if (standard is null) return null;

        var offsets = new int[noteNames.Count];
        for (var i = 0; i < noteNames.Count; i++)
        {
            var pitchClass = NoteToPitchClass(noteNames[i]);
            if (pitchClass < 0) return null; // one unreadable string ⇒ no answer at all

            // Signed distance in [-6, +5]: the nearest spelling of the same pitch.
            offsets[i] = ((pitchClass - standard[i] + 18) % 12) - 6;
        }

        return offsets;
    }

    /// <summary>One note letter (any case, sharps or flats) to a pitch class 0-11,
    /// or -1. Matches <see cref="TuningNameResolver" />'s parsing exactly — the two
    /// must agree or a derived vector would name differently from its letters.</summary>
    private static int NoteToPitchClass(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return -1;
        var trimmed = note.Trim();
        var normalized = char.ToUpperInvariant(trimmed[0]) +
                         (trimmed.Length > 1 ? trimmed[1..].ToLowerInvariant() : "");

        return normalized switch
        {
            "C" or "B#" => 0,
            "C#" or "Db" => 1,
            "D" => 2,
            "D#" or "Eb" => 3,
            "E" or "Fb" => 4,
            "F" or "E#" => 5,
            "F#" or "Gb" => 6,
            "G" => 7,
            "G#" or "Ab" => 8,
            "A" => 9,
            "A#" or "Bb" => 10,
            "B" or "Cb" => 11,
            _ => -1
        };
    }
}
