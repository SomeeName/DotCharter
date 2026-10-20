namespace DotCharter.Core.Utilities;

/// <summary>
///     Whether a two-string shape gets an automatic chord name, and whether power
///     chords are exempt.
///     <para>
///         <b>Two pitches do not determine a chord</b>, so any name detected from
///         them is a guess: the same two notes belong to a dozen chords and the
///         matcher simply reaches one of them first. A confident-looking
///         <c>B7sus4</c> over a two-note shape is worse than no label, because the
///         charter has no way to see that it was invented. Editor on Fire ships the
///         same switch (<c>eof_dont_auto_name_double_stops</c>,
///         <c>EOF_SRC/tuning.c:425</c>) and rscharter never auto-names at all —
///         both render a blank name as nothing, which is exactly the "hidden" the
///         owner asked for.
///     </para>
///     <para>
///         <b>The default suppresses power chords too</b>, matching EOF character
///         for character. A root-and-fifth is genuinely unambiguous as a SET of
///         pitches, but which of the two is the root is not — the same
///         D-and-G double stop is <c>G5</c> or <c>G5/D</c> depending on a judgement
///         nothing in the shape settles. <see cref="KeepPowerChordNames" /> is the
///         escape hatch for charters who want them anyway.
///     </para>
///     <para>
///         This only ever gates the AUTOMATIC namer. The full candidate list stays
///         available, so naming a double stop by hand in the inspector is
///         unaffected — the point is that the app should not invent one.
///     </para>
/// </summary>
/// <param name="Suppress">Leave two-string shapes unnamed.</param>
/// <param name="KeepPowerChordNames">
///     Exempt a root-and-fifth (or its inversion) from the suppression, so
///     <c>G5</c> survives while an ambiguous two-note shape does not.
/// </param>
public readonly record struct DoubleStopNameOptions(bool Suppress, bool KeepPowerChordNames)
{
    /// <summary>What the app ships with: no auto-name on any two-string shape.</summary>
    public static DoubleStopNameOptions Default { get; } = new(Suppress: true, KeepPowerChordNames: false);

    /// <summary>The pre-0.41 behaviour — every shape the matcher can name, gets named.</summary>
    public static DoubleStopNameOptions NameEverything { get; } = new(Suppress: false, KeepPowerChordNames: false);
}
