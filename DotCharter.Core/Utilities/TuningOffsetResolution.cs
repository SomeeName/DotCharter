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
///         A known tuning name supplies its octave and extended-string offsets
///         when its pitch classes agree with the letters, or when letters are
///         absent. Conflicting letters take precedence. Without a coherent name,
///         letter-only derivation is restricted to four and six strings and uses
///         the nearest signed offset; pitch classes alone cannot specify an octave.
///     </para>
/// </remarks>
public static class TuningOffsetResolution
{
    /// <summary>Standard open-string pitch classes, low → high, for the two string
    /// counts whose offset base is unambiguous.</summary>
    private static readonly int[] Guitar6Std = [4, 9, 2, 7, 11, 4]; // E A D G B E
    private static readonly int[] Bass4Std = [4, 9, 2, 7];          // E A D G

    /// <summary>
    ///     Per-string offsets, low → high. Authored offsets win, then a known name
    ///     coherent with the letters, then readable letters. Never null;
    ///     empty when no source can be resolved.
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

        var stringCount = arrangement.StringCount > 0
            ? arrangement.StringCount
            : arrangement.ArrangementType == ArrangementType.Bass ? 4 : 6;
        if (TuningUtilities.TryGetOffsetsForTuningType(arrangement.TuningType, stringCount, out var named) &&
            (arrangement.Tuning.Length == 0 || SamePitchClasses(arrangement.Tuning, stringCount, named)))
        {
            return named;
        }

        return DeriveFromNoteNames(arrangement.Tuning) ?? [];
    }

    private static bool SamePitchClasses(IReadOnlyList<string> letters, int stringCount, int[] offsets)
    {
        if (letters.Count != stringCount) return false;
        var named = TuningUtilities.GetTuningNames(stringCount, offsets);
        for (var i = 0; i < stringCount; i++)
            if (NoteToPitchClass(letters[i]) != NoteToPitchClass(named[i])) return false;
        return true;
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

    private static int NoteToPitchClass(string? note) => TuningNameResolver.NoteToPitchClass(note);
}
