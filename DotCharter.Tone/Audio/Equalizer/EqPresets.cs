namespace DotCharter.Tone.Audio.Equalizer;

/// <summary>
///     The EQ presets, written for CHARTING work rather than for mixing: find
///     the guitar in a dense mix, kill the kick, notch out mains hum.
///     <para>
///         Data, not markup. The picker, the "Custom" badge and the section all
///         read this one table, so a preset added here appears in the UI with no
///         second list to keep in step — the same rule
///         <c>GameplaySettingsCatalog</c> and <c>GetReadyKeys</c> follow.
///     </para>
///     <para>
///         The KEYS are stable identifiers (they are persisted in
///         <see cref="EqSection.PresetKey" /> and they name a resx entry); the
///         DISPLAYED name is <c>Inspector_EqPreset_&lt;Key&gt;</c> in
///         <c>Resources.resx</c>, which is why nothing here is a user-visible
///         string.
///     </para>
/// </summary>
public static class EqPresets
{
    /// <summary>The key of the preset a fresh section starts on — six parked,
    /// switched-OFF bands, i.e. audibly nothing.</summary>
    public const string FullRangeKey = "FullRange";

    /// <summary>
    ///     Every band OFF, parked where the six handles spread evenly across the
    ///     plot. This is the state the ENGINE boots in, so a section built in
    ///     managed code and an engine nobody has pushed to are already in
    ///     agreement.
    /// </summary>
    public static IReadOnlyList<EqBand> FullRange { get; } =
    [
        new(EqBandType.HighPass, 20, 0.71, 0, false),
        new(EqBandType.LowShelf, 120, 0.71, 0, false),
        new(EqBandType.Bell, 500, 1.0, 0, false),
        new(EqBandType.Bell, 2500, 1.0, 0, false),
        new(EqBandType.HighShelf, 8000, 0.71, 0, false),
        new(EqBandType.LowPass, 20000, 0.71, 0, false)
    ];

    /// <summary>Every preset, in the order the picker offers them. First entry is
    /// always <see cref="FullRangeKey" /> — it is the "off" the others are
    /// departures from.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        FullRangeKey, "FindTheGuitar", "KillTheKick", "BassOnly", "HearTheVocal", "NotchHum", "MetronomeClarity"
    ];

    private static readonly Dictionary<string, IReadOnlyList<EqBand>> Table = new(StringComparer.Ordinal)
    {
        [FullRangeKey] = FullRange,

        // Lift the guitar out of a full mix: cut the rhythm section's weight,
        // push the body and the pick attack.
        ["FindTheGuitar"] =
        [
            new(EqBandType.HighPass, 180, 0.8, 0, true),
            new(EqBandType.LowShelf, 260, 0.71, -4, true),
            new(EqBandType.Bell, 900, 1.6, 3.5, true),
            new(EqBandType.Bell, 3000, 1.1, 6.5, true),
            new(EqBandType.HighShelf, 9000, 0.71, -3, true),
            new(EqBandType.LowPass, 20000, 0.71, 0, false)
        ],

        // Get the kick and the floor tom out of the way so a low riff is
        // audible against them.
        ["KillTheKick"] =
        [
            new(EqBandType.HighPass, 95, 1.1, 0, true),
            new(EqBandType.LowShelf, 150, 0.8, -6, true),
            new(EqBandType.Bell, 400, 1.4, -3, true),
            new(EqBandType.Bell, 2200, 1.0, 2, true),
            new(EqBandType.HighShelf, 8000, 0.71, 0, false),
            new(EqBandType.LowPass, 20000, 0.71, 0, false)
        ],

        // Charting a bass part: keep the fundamentals, lose everything above them.
        ["BassOnly"] =
        [
            new(EqBandType.HighPass, 20, 0.71, 0, false),
            new(EqBandType.LowShelf, 140, 0.71, 4, true),
            new(EqBandType.Bell, 700, 1.2, -5, true),
            new(EqBandType.Bell, 2500, 1.0, -8, true),
            new(EqBandType.HighShelf, 6000, 0.71, -12, true),
            new(EqBandType.LowPass, 900, 0.9, 0, true)
        ],

        // For vocal / lyric work: the syllable band forward, the rest back.
        ["HearTheVocal"] =
        [
            new(EqBandType.HighPass, 140, 0.8, 0, true),
            new(EqBandType.LowShelf, 300, 0.8, -3, true),
            new(EqBandType.Bell, 700, 1.2, 3, true),
            new(EqBandType.Bell, 4200, 1.4, 4, true),
            new(EqBandType.HighShelf, 11000, 0.71, -4, true),
            new(EqBandType.LowPass, 20000, 0.71, 0, false)
        ],

        // Mains hum and its first harmonic. Two narrow notches rather than one
        // wide one: 50 and 100 (or 60 and 120) are what a ground loop actually
        // puts into a guitar input, and a wide cut would take the low E with it.
        ["NotchHum"] =
        [
            new(EqBandType.HighPass, 34, 0.71, 0, true),
            new(EqBandType.Notch, 50, 14, 0, true),
            new(EqBandType.Notch, 100, 12, 0, true),
            new(EqBandType.Bell, 2500, 1.0, 1.5, true),
            new(EqBandType.HighShelf, 8000, 0.71, 0, false),
            new(EqBandType.LowPass, 20000, 0.71, 0, false)
        ],

        // Hear the click over the song while tapping a tempo map.
        ["MetronomeClarity"] =
        [
            new(EqBandType.HighPass, 400, 0.9, 0, true),
            new(EqBandType.LowShelf, 700, 0.8, -4, true),
            new(EqBandType.Bell, 1800, 1.3, 4, true),
            new(EqBandType.Bell, 5500, 1.5, 7, true),
            new(EqBandType.HighShelf, 9000, 0.71, 3, true),
            new(EqBandType.LowPass, 20000, 0.71, 0, false)
        ]
    };

    /// <summary>
    ///     Where a band of this SHAPE is parked by default — what the frequency
    ///     slider goes back to on a double-click.
    ///     <para>
    ///         Read off <see cref="FullRange" /> wherever that preset has a band
    ///         of the shape, so "the default" and "where the six handles start"
    ///         cannot drift. Two shapes need a stated answer instead: BELL appears
    ///         twice there (500 and 2500 Hz — neither is more default than the
    ///         other, so the round decade between them is used) and NOTCH appears
    ///         in no preset at all, where 50 Hz is the only useful guess a notch
    ///         has (mains hum is what one is for).
    ///     </para>
    /// </summary>
    public static double DefaultFrequencyFor(EqBandType type) => type switch
    {
        EqBandType.Bell => 1000.0,
        EqBandType.Notch => 50.0,
        _ => FullRange.FirstOrDefault(b => b.Type == type).FrequencyHz
    };

    /// <summary>The bands of a preset, or null when the key names none.</summary>
    public static IReadOnlyList<EqBand>? ByKey(string? key) =>
        key != null && Table.TryGetValue(key, out var bands) ? bands : null;

    /// <summary>The resx key the picker shows for a preset. Derived rather than
    /// listed so a new preset needs one table entry and one resx pair, never a
    /// third place to forget.</summary>
    public static string ResourceKeyFor(string presetKey) => $"Inspector_EqPreset_{presetKey}";
}
