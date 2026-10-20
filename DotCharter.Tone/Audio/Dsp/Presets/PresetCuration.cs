namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
/// Editorial "quality-first" ordering for the factory tone catalogue — the
/// preset-browser analogue of the gear rail's <c>GearCuration.RankOf</c>. A
/// pure, side-effect-free seam: it maps a preset id to a curated rank (lower =
/// more featured) so the browser can surface the marquee artist/genre tones
/// ahead of the long tail, WITHOUT baking presentation order into the immutable
/// <see cref="DspTonePresetLibrary"/> (which stays grouped clean→experimental for
/// the category tabs). Unranked presets sort after the featured set, keeping
/// their original relative order (a stable sort).
/// </summary>
public static class PresetCuration
{
    // The curated featured order (quality-first). Ids MUST exist in
    // DspTonePresetLibrary — PresetCurationTests gates that so a renamed/removed
    // preset can't leave a dangling featured id.
    //
    // Every marquee slot names the `nam-` CAPTURE-BACKED twin, not the
    // algorithmic original. The featured shelf is the first thing a player sees,
    // and a real capture of the amp is what these tones are actually FOR — the
    // "… (Legacy)" rig behind each one is the CPU-cheap alternative, not the
    // headline. The legacy ids are deliberately absent rather than listed lower:
    // an unranked preset already sorts after the featured set in its original
    // order, so both versions stay one scroll apart.
    private static readonly string[] Featured =
    [
        // Marquee signature tones first.
        "nam-artist-polyphia-clean-tap",
        "nam-artist-metallica-mop-rhythm",
        "nam-artist-acdc-angus-crunch",
        "nam-artist-gilmour-comfortably",
        "nam-artist-srv-texas-blues",
        "nam-artist-evh-brown-sound",
        "nam-artist-galneryus-neo-lead",
        "nam-artist-yorushika-bright-clean",
        "nam-artist-tool-adam-jones",
        "nam-artist-slipknot-drop-metal",
        "nam-artist-ink-metalcore-rhythm",
        "nam-genre-djent-tight-boost",
        "nam-artist-knopfler-sultans-clean",
        "nam-artist-frusciante-funk-clean",
        "nam-artist-nirvana-teen-spirit",
        "nam-artist-weezer-blue-crunch",
        "nam-genre-city-pop-clean",
        "nam-genre-shoegaze-wall",
        "nam-genre-surf-twang",
        "nam-genre-punk-marshall",
        // Capture-backed showcases (the original hand-authored NAM rigs).
        "nam-deluxe-clean",
        "nam-rhythm-crunch",
        // A few enduring generic staples round out the featured shelf.
        "nam-clean-glass",
        "nam-crunch-plexi",
        "nam-metal-modern-djent",
        "nam-lead-singing-sustain",
        // The 0.9 gear-wave showcase, deliberately BELOW the marquee tones:
        // these are how a player meets the 43 new modules, and gear nothing
        // features is gear reachable only by building a board by hand. They sit
        // after the signature tones because that is what the top of the shelf is
        // for — and ahead of the long tail, where an unranked preset buries them.
        "gear-rodent-rhythm",
        "gear-chainsaw",
        "gear-optical-clean",
        "gear-bass-di",
        "gear-twelve-string-jangle",
        "gear-dotted-multitap",
        "gear-shoegaze-reverse",
        "gear-vocoder-lead",
        "gear-talk-box-funk",
        "gear-gated-snap",
        "gear-loop-station",
        "gear-bass-synth",
        "gear-tube-di-warm",
    ];

    private static readonly Dictionary<string, int> Rank = BuildRank();

    private static Dictionary<string, int> BuildRank()
    {
        var map = new Dictionary<string, int>(Featured.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Featured.Length; i++)
            map.TryAdd(Featured[i], i);
        return map;
    }

    /// <summary>The number of curated/featured presets.</summary>
    public static int FeaturedCount => Featured.Length;

    /// <summary>The featured ids, in curated order (defensive copy).</summary>
    public static IReadOnlyList<string> FeaturedIds => Featured;

    /// <summary>The curated rank of a preset id — lower is more featured. Unranked
    /// (or null/empty) ids return <see cref="int.MaxValue"/> so they sort last.
    /// Mirrors <c>GearCuration.RankOf</c>.</summary>
    public static int RankOf(string? presetId)
        => presetId is not null && Rank.TryGetValue(presetId, out var r) ? r : int.MaxValue;

    /// <summary>True when the preset id is on the curated featured shelf.</summary>
    public static bool IsFeatured(string? presetId)
        => presetId is not null && Rank.ContainsKey(presetId);

    /// <summary>Returns <paramref name="presets"/> ordered quality-first: featured
    /// presets by <see cref="RankOf"/>, then the rest in their original order
    /// (stable). Pure — never mutates the input. Useful as the browser's default
    /// "Featured" ordering.</summary>
    public static IReadOnlyList<DspTonePreset> OrderFeaturedFirst(IEnumerable<DspTonePreset> presets)
        => presets
            .Select((p, index) => (p, index))
            .OrderBy(t => RankOf(t.p.Id))
            .ThenBy(t => t.index)
            .Select(t => t.p)
            .ToList();
}
