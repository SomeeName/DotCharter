namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     The slot order shared by the internal SNG model and legacy readers. Guitar slots
///     0..5 are E A D G B e, then low B and F#. Bass slots 0..3 are E A D G, then
///     low B and high C. Domain strings are lowest first; ToSng and ToDomain bridge
///     these orders. RS Extended Range serialization writes positional strings from
///     this model, combining stock chord columns with the extra guitar columns.
/// </summary>
internal static class Rs7StringLayout
{
    /// <summary>The strings stock Rocksmith (and every chord template) can carry.</summary>
    public const int NativeStrings = 6;

    /// <summary>The widest guitar the format can spell.</summary>
    public const int MaxStrings = 8;

    /// <summary>The widest bass the format can spell (low B and high C).</summary>
    public const int MaxBassStrings = 6;

    private static readonly int[] OpenMidiBySngIndex = [40, 45, 50, 55, 59, 64, 35, 30];

    private static readonly int[] BassOpenMidiBySngIndex = [28, 33, 38, 43, 23, 48];

    /// <summary>MIDI pitch of open package string <paramref name="sngIndex" /> in
    /// standard tuning (E A D G B e, then B1 and F#1).</summary>
    public static int OpenMidi(int sngIndex) =>
        sngIndex >= 0 && sngIndex < OpenMidiBySngIndex.Length ? OpenMidiBySngIndex[sngIndex] : 0;

    /// <summary>MIDI pitch of open package string <paramref name="sngIndex" /> of an extended
    /// bass: E1 A1 D2 G2, then low B0 (4) and high C3 (5).</summary>
    public static int BassOpenMidi(int sngIndex) =>
        sngIndex >= 0 && sngIndex < BassOpenMidiBySngIndex.Length ? BassOpenMidiBySngIndex[sngIndex] : 0;

    /// <summary>Whether this guitar needs the extended internal model.</summary>
    public static bool IsExtendedGuitar(Arrangement arrangement) =>
        IsExtendedGuitar(arrangement.ArrangementType, arrangement.StringCount);

    public static bool IsExtendedGuitar(ArrangementType type, int stringCount) =>
        type is not (ArrangementType.Bass or ArrangementType.Vocal) && stringCount is > NativeStrings and <= MaxStrings;

    /// <summary>Whether this arrangement is a five- or six-string bass.</summary>
    public static bool IsExtendedBass(Arrangement arrangement) =>
        IsExtendedBass(arrangement.ArrangementType, arrangement.StringCount);

    public static bool IsExtendedBass(ArrangementType type, int stringCount) =>
        type is ArrangementType.Bass && stringCount is > 4 and <= MaxBassStrings;

    /// <summary>Whether this arrangement is written as RS Extended Range: seven-/eight-string guitar
    /// or five-/six-string bass. Every other supported arrangement is stock.</summary>
    public static bool IsExtended(Arrangement arrangement) => IsExtendedGuitar(arrangement) || IsExtendedBass(arrangement);

    /// <summary>
    ///     Why this arrangement is NOT exported, or null when it is: a part with more strings than
    ///     any layout spells (a 9-string guitar, a 7-string bass: <see cref="MaxStringsFor" /> is the
    ///     limit, and folding the extra string's notes onto the last one would change the song).
    /// </summary>
    public static string? ExportRefusal(Arrangement arrangement) =>
        HasUnsupportedStringCount(arrangement)
            ? $"'{arrangement.Name}' has {arrangement.StringCount} strings; RS Extended Range supports at most " +
              $"{MaxStringsFor(arrangement.ArrangementType)} for this instrument."
            : null;

    /// <summary>The most strings a package can spell for this kind of part: eight for a guitar,
    /// six for a bass.</summary>
    public static int MaxStringsFor(ArrangementType type) => type is ArrangementType.Bass ? MaxBassStrings : MaxStrings;

    /// <summary>A string count past <see cref="MaxStringsFor" />: no layout spells it, and its
    /// notes would fold onto the last string the package has.</summary>
    public static bool HasUnsupportedStringCount(Arrangement arrangement) =>
        arrangement.ArrangementType is not ArrangementType.Vocal &&
        arrangement.StringCount > MaxStringsFor(arrangement.ArrangementType);

    /// <summary>The package string index of chart string <paramref name="domainIndex" />.
    /// Identity for six strings or fewer.</summary>
    public static int ToSng(int domainIndex, int stringCount)
    {
        var extra = stringCount - NativeStrings;
        if (extra <= 0) return domainIndex;
        return domainIndex < extra ? NativeStrings - 1 + extra - domainIndex : domainIndex - extra;
    }

    /// <summary>The chart string index of package string <paramref name="sngIndex" />.
    /// Identity for six strings or fewer.</summary>
    public static int ToDomain(int sngIndex, int stringCount)
    {
        var extra = stringCount - NativeStrings;
        if (extra <= 0) return sngIndex;
        return sngIndex >= NativeStrings ? NativeStrings - 1 + extra - sngIndex : sngIndex + extra;
    }

    /// <summary>The package string index of chart string <paramref name="domainIndex" /> of an
    /// instrument of <paramref name="type" />. A 5-/6-string bass (chart order B E A D G [C]) puts
    /// the low B at package index 4 and the high C at 5; everything else is
    /// <see cref="ToSng(int, int)" />.</summary>
    public static int ToSng(int domainIndex, int stringCount, ArrangementType type) =>
        !IsExtendedBass(type, stringCount) ? ToSng(domainIndex, stringCount)
        : domainIndex == 0 ? 4 : domainIndex <= 4 ? domainIndex - 1 : domainIndex;

    /// <summary>The chart string index of package string <paramref name="sngIndex" /> (the inverse of
    /// <see cref="ToSng(int, int, ArrangementType)" />).</summary>
    public static int ToDomain(int sngIndex, int stringCount, ArrangementType type) =>
        !IsExtendedBass(type, stringCount) ? ToDomain(sngIndex, stringCount)
        : sngIndex == 4 ? 0 : sngIndex < 4 ? sngIndex + 1 : sngIndex;
}
