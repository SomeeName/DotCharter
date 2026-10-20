using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     The one answer to "is this arrangement played on a BASS?" — which is not
/// the same question as "is it on the Bass PATH?".
///     <para>
///         Rocksmith has no 5- or 6-string bass path, so CDLC authors chart an
///         extended-range bass on a GUITAR path (Rhythm, Lead, Alt, Bonus) with
///         the tuning reference an octave down: <c>CentOffset</c> −1200 (A220)
///         and bass-style offsets (<c>-5 -5 -5 -5 -4 -4</c> = B E A D G C). Their
///         tones are bass gear. In the owner's 1,825-song CDLC library all 44
///         guitar-path arrangements below −50 ¢ sit at exactly −1200 and every
///         one carries a bass tone; no other guitar-path offset is lower than
///         −45 ¢. <see cref="OctaveDownThresholdCents" /> therefore sits well
///         clear of both.
///     </para>
///     <para>
///         <b>Ask this for what the part SOUNDS like and what it is CALLED</b> —
///         the chart synth's voice, the stem a tone is measured from or muted on,
///         the bassline anchor rule, a sheet's clef. <b>Never for what the
///         arrangement IS in a file</b>: an export keeps the authored path
///         (<see cref="Arrangement.ArrangementType" />), and so does every rule
///         that is about the path itself — the bass path's implicit octave in
///         <see cref="SoundingPitch" />, the four-string limit of the Bass path,
///         <c>bassPick</c>, the route mask.
///     </para>
/// </summary>
public static class ArrangementInstrument
{
    /// <summary>
    ///     A tuning reference at or below this many cents is an octave down
    ///     (A220 is −1200). Real guitar paths stay within ±65 ¢ of A440.
    /// </summary>
    public const double OctaveDownThresholdCents = -1150.0;

    /// <summary>
    ///     True when the arrangement is a bass part: the Bass path, or a guitar
    ///     path whose tuning reference is an octave down.
    /// </summary>
    /// <param name="arrangement">The arrangement.</param>
    /// <returns>True for a bass part.</returns>
    public static bool IsBassPart(Arrangement arrangement) => IsBassPart(arrangement, null);

    /// <summary>
    ///     True when the arrangement is a bass part: the Bass path; a guitar path
    ///     whose tuning reference is an octave down; or a guitar path whose tones
    ///     are bass gear (<paramref name="tonesAreBassGear" />, asked only when the
    ///     cheaper signals have not already answered — it parses tone bodies).
    /// </summary>
    /// <param name="arrangement">The arrangement.</param>
    /// <param name="tonesAreBassGear">Whether the arrangement's tones are bass
    /// gear, for a caller that has them; null = not known.</param>
    /// <returns>True for a bass part.</returns>
    public static bool IsBassPart(Arrangement arrangement, Func<bool>? tonesAreBassGear)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        if (arrangement.ArrangementType == ArrangementType.Bass) return true;
        if (!IsGuitarPath(arrangement.ArrangementType)) return false;
        if (IsOctaveDownReference(arrangement.CentOffset)) return true;
        return tonesAreBassGear?.Invoke() == true;
    }

    /// <summary>True for the three guitar paths (Lead, Rhythm, Combo).</summary>
    /// <param name="type">The path.</param>
    /// <returns>True for a guitar path.</returns>
    public static bool IsGuitarPath(ArrangementType type) =>
        type is ArrangementType.Lead or ArrangementType.Rhythm or ArrangementType.Combo;

    /// <summary>True when a tuning reference is an octave down (A220).</summary>
    /// <param name="centOffset">The arrangement's <c>CentOffset</c>.</param>
    /// <returns>True at or below <see cref="OctaveDownThresholdCents" />.</returns>
    public static bool IsOctaveDownReference(double centOffset) => centOffset <= OctaveDownThresholdCents;

    /// <summary>
    ///     The whole octave a tuning reference moves every note, in semitones:
    ///     −12 at A220, 0 otherwise. <see cref="SoundingPitch.MidiFor" /> does NOT
    ///     include it (it is the chart's pitch at its own reference); anything that
    ///     NAMES a sounding note, or compares one with real audio, adds it — and
    ///     then measures cents against <see cref="ResidualCents" />, never against
    ///     the raw offset, or the octave is applied twice.
    /// </summary>
    /// <param name="centOffset">The arrangement's <c>CentOffset</c>.</param>
    /// <returns>−12 or 0.</returns>
    public static int ReferenceOctaveSemitones(double centOffset) => IsOctaveDownReference(centOffset) ? -12 : 0;

    /// <summary>
    ///     What is left of a tuning reference once its whole octave
    ///     (<see cref="ReferenceOctaveSemitones" />) is folded into the note:
    ///     0 ¢ for an exact A220, the offset itself otherwise.
    /// </summary>
    /// <param name="centOffset">The arrangement's <c>CentOffset</c>.</param>
    /// <returns>The residual offset in cents.</returns>
    public static double ResidualCents(double centOffset) => centOffset - 100.0 * ReferenceOctaveSemitones(centOffset);
}
