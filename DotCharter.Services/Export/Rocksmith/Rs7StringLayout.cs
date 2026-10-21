namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     How a 7- or 8-string GUITAR arrangement is spelled in a Rocksmith 2014 package
/// for the owner's <c>rs7string</c> game mod. The mod switches a guitar
///     arrangement into 7-string mode when its SNG tuning has 7 entries; 8 entries is
///     the format the generator <c>rs7chart</c> writes for a later 8-string build.
///     <para>
///         <b>The string ORDER differs from the chart's.</b> A chart numbers strings
///         LOWEST first (<c>TuningUtilities.GetStandardMidiPitches</c>: a 7-string is
///         B E A D G B e, string 0 = low B). The package keeps indices 0..5 as the
///         six-string E A D G B e and puts the extra strings AFTER them: index 6 = the
///         7th string (B1, MIDI 35 + offset), index 7 = the 8th (F#1, MIDI 30 +
///         offset). Every existing chart stays valid that way, which is why the mod
///         chose it. <see cref="ToSng" /> and <see cref="ToDomain" /> are the one
///         mapping between the two.
///     </para>
///     <para>
///         The manifest keeps <c>string0</c>..<c>string5</c> and adds
///         <c>Rs7String6</c> / <c>Rs7String7</c> — never <c>string6</c>, which
///         overflows the game's six-int song-list reader. Chord templates stay six
///         wide (a valid six-string projection); the extra strings' template columns
///         and chord-note techniques go into the SNG's RS7X extension block
///         (<see cref="Sng.Model.RsExtensionBlock" />, built by <see cref="Rs7BlockBuilder" />). Packages from before the block are still READ: the
///         <c>chords-ext.json</c> sidecar (<see cref="Rs7ChordSidecar" />) and the
///         template name's last byte.
///     </para>
///     <para>
///         Only the 7-string guitar is EXPORTED with a block today (<see cref="ExportRefusal" />):
///         RS7X v1 spells 8 strings but the mod does not play them yet. A bass never gets a
///         block (RS7X v1 reserves the bass instrument): a 5-/6-string bass keeps the stock
///         six-slot spelling stock Rocksmith bass charts use. The layout stays general.
///     </para>
/// </summary>
internal static class Rs7StringLayout
{
    /// <summary>The strings stock Rocksmith (and every chord template) can carry.</summary>
    public const int NativeStrings = 6;

    /// <summary>The widest guitar the format can spell.</summary>
    public const int MaxStrings = 8;

    /// <summary>What the current <c>rs7string</c> build plays, and so the widest guitar
    /// the exporter writes.</summary>
    public const int ModPlayableStrings = 7;

    private static readonly int[] OpenMidiBySngIndex = [40, 45, 50, 55, 59, 64, 35, 30];

    /// <summary>MIDI pitch of open package string <paramref name="sngIndex" /> in
    /// standard tuning (E A D G B e, then B1 and F#1).</summary>
    public static int OpenMidi(int sngIndex) =>
        sngIndex >= 0 && sngIndex < OpenMidiBySngIndex.Length ? OpenMidiBySngIndex[sngIndex] : 0;

    /// <summary>The manifest <c>Tuning</c> key for a package string index past five.</summary>
    public static string ManifestKey(int sngIndex) => $"Rs7String{sngIndex}";

    /// <summary>Whether this arrangement exports through the rs7string layout.</summary>
    public static bool IsExtendedGuitar(Arrangement arrangement) =>
        IsExtendedGuitar(arrangement.ArrangementType, arrangement.StringCount);

    public static bool IsExtendedGuitar(ArrangementType type, int stringCount) =>
        type is not (ArrangementType.Bass or ArrangementType.Vocal) && stringCount is > NativeStrings and <= MaxStrings;

    /// <summary>
    ///     Why this arrangement is NOT exported, or null when it is: an 8-string guitar
    ///     (RS7X v1 spells it, but the mod plays 7 strings and such a package may crash
    ///     the game).
    /// </summary>
    public static string? ExportRefusal(Arrangement arrangement)
    {
        if (IsExtendedGuitar(arrangement) && arrangement.StringCount > ModPlayableStrings)
        {
            return $"{arrangement.StringCount}-string guitar '{arrangement.Name}' is not exported yet: the rs7string " +
                   "mod plays 7 strings, and the RS7X block for 8 strings waits for a later mod build.";
        }

        return HasUnsupportedStringCount(arrangement)
            ? $"'{arrangement.Name}' has {arrangement.StringCount} strings; a song file spells at most " +
              $"{MaxStringsFor(arrangement.ArrangementType)} for this instrument."
            : null;
    }

    /// <summary>The most strings a package can spell for this kind of part: eight for a guitar,
    /// the stock six for a bass.</summary>
    public static int MaxStringsFor(ArrangementType type) => type is ArrangementType.Bass ? NativeStrings : MaxStrings;

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
}
