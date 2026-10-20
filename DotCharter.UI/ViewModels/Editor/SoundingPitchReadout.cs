namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The Sound tab's PITCH readout: what the selected (string, fret) actually
///     SOUNDS, spelled the way a guitarist names it.
///     <para>
///         The inspector showed a string number and a fret number and stopped
///         there, which is exactly the arithmetic a charter cannot do in their
///         head on this app's own turf: with a non-standard tuning or a capo,
///         fret 5 on the low E is not A. The pitch itself comes from
///         <see cref="SoundingPitch" /> — the one derivation that folds in the
///         arrangement's tuning offsets, the bass octave and the capo — and this
///         type only decides when there is NOTHING to name.
///     </para>
///     <para>
///         There are three such cases and all three must print an empty string
///         rather than a plausible-looking wrong note: no arrangement to measure
///         against, a string the arrangement does not have, and a fret BEHIND the
///         capo (malformed — inspector <c>L01</c>). The per-string chord editor
///         adds a fourth: its fret column is free TEXT, and an empty or muted
///         (<c>x</c>) string is not played at all.
///     </para>
/// </summary>
public static class SoundingPitchReadout
{
    /// <summary>
    ///     Scientific-pitch name for a fretted string (<c>A2</c>, <c>C#4</c>),
    ///     or the empty string when there is no pitch to name. Never null, so it
    ///     binds straight to a <c>TextBlock</c>.
    /// </summary>
    public static string For(Arrangement? arrangement, int stringIndex, int fret) =>
        SoundingPitch.NameFor(arrangement, stringIndex, fret) ?? string.Empty;

    /// <summary>
    ///     The same answer for the chord editor's per-string rows, whose fret
    ///     column is text: <c>""</c> is a string this shape does not play and
    ///     <c>x</c> / <c>X</c> is a muted one. Neither sounds a pitch, so neither
    ///     gets a name — the parse is the SAME <c>int.TryParse</c> the row's own
    ///     commit path uses, so the readout and the shape cannot disagree about
    ///     what counts as a fret.
    /// </summary>
    public static string ForFretText(Arrangement? arrangement, int stringIndex, string? fretText) =>
        int.TryParse(fretText, out var fret) ? For(arrangement, stringIndex, fret) : string.Empty;
}
