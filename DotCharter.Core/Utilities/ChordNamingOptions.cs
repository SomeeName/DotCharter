using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     How the automatic chord namer spells a name.
///     <para>
/// <b>Two styles, because two references disagree.</b> The owner's
///         FINISHED charts carry Editor on Fire's automatic names — <c>A#min7</c>,
///         <c>D#Maj7</c>, sharps even in flat keys — and <see cref="Eof" /> reproduces
///         them (~91% exact on 1,688 named templates). The chord names typed into
///         the owner's Guitar Pro tabs are the MUSICAL reference — <c>Bbm7</c>,
///         <c>Ebmaj7</c>, <c>Cm7/Bb</c> — and <see cref="Musical" /> is measured
///         against those. Neither is a bug fix of the other; which one a charter
///         wants is a choice, so it is a setting.
///     </para>
/// </summary>
public enum ChordNamingStyle
{
    /// <summary>
    ///     Editor on Fire / RS2014 spelling: <c>min</c>, <c>Maj7</c>, sharps first,
    ///     an inversion named by its plain chord. The default.
    /// </summary>
    Eof = 0,

    /// <summary>
    ///     Spelled in the song's key (<see cref="ChordKeySignature" />), <c>m</c> /
    ///     <c>maj7</c> / <c>m7b5</c>, and an inversion written as a slash chord
    ///     over its bass (<c>Cm7/Bb</c>).
    /// </summary>
    Musical = 1
}

/// <summary>
///     A key signature: <paramref name="Accidentals" /> is −7 … +7 (negative =
///     flats), the same encoding as <see cref="ChartData.KeyAccidentals" />.
/// </summary>
public readonly record struct ChordKeySignature(int Accidentals, bool IsMinor)
{
    /// <summary>The tonic's pitch class (C = 0).</summary>
    public int Tonic => Mod12((7 * Accidentals) + (IsMinor ? 9 : 0));

    /// <summary>
    ///     The key with this tonic and mode, spelled with the fewer accidentals
    ///     (Db major, not C# major); the six-accidental tie goes to the FLAT side
    ///     (Gb major, Eb minor).
    /// </summary>
    public static ChordKeySignature FromTonic(int tonic, bool isMinor)
    {
        var relativeMajor = Mod12(tonic + (isMinor ? 3 : 0));
        // 7 is its own inverse mod 12, so 7·k ≡ tonic ⇔ k ≡ 7·tonic.
        var accidentals = Mod12(7 * relativeMajor);
        if (accidentals > 5) accidentals -= 12;
        return new ChordKeySignature(accidentals, isMinor);
    }

    internal static int Mod12(int value)
    {
        var result = value % 12;
        return result < 0 ? result + 12 : result;
    }
}

/// <summary>
///     What <see cref="ChordNameSuggester" /> needs besides the shape: the style,
///     and — for <see cref="ChordNamingStyle.Musical" /> only — the key to spell
///     roots and bass notes in.
/// </summary>
/// <param name="Style">The naming style.</param>
/// <param name="Key">
///     The key to spell in. Null keeps today's order (sharp spelling first, flat
///     second). <see cref="ChordNamingStyle.Eof" /> ignores it: EOF never spells
///     in a key.
/// </param>
public readonly record struct ChordNamingOptions(ChordNamingStyle Style, ChordKeySignature? Key = null)
{
    /// <summary>EOF style, no key — the original vocabulary only, no extended patterns.</summary>
    public static ChordNamingOptions Default { get; } = new(ChordNamingStyle.Eof);

    /// <summary>
    ///     The options for naming shapes in <paramref name="arrangement" />. For
    ///     <see cref="ChordNamingStyle.Musical" /> this resolves the key once
    ///     (<see cref="ChordKeyEstimator.Resolve(Arrangement, ChartData)" />), so a caller naming many
    ///     templates should call it once and pass the result to each.
    /// </summary>
    /// <param name="style">The style the charter chose.</param>
    /// <param name="arrangement">The arrangement whose notes the key is read from.</param>
    /// <param name="chart">
    ///     The project's chart data, for its authored key
    ///     (<see cref="ChartData.KeyAccidentals" />, <see cref="ChartData.KeyIsMinor" />).
    ///     Null when there is no project.
    /// </param>
    public static ChordNamingOptions For(ChordNamingStyle style, Arrangement? arrangement, ChartData? chart) =>
        style == ChordNamingStyle.Musical
            ? new ChordNamingOptions(style, ChordKeyEstimator.Resolve(arrangement, chart))
            : Default;
}
