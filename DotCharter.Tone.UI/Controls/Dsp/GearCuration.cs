namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
/// Curated quality ranking for the DSP module catalogue — the ONE ordering seam
/// the gear rail (and the add-flow) sort by so the highest-value gear surfaces
/// first: amps + cabs, then drives, then time/modulation, then the
/// eq/filter/dynamics/pitch utilities, with anything unmapped last. A small pure
/// API (<see cref="RankOf"/>) with NO Avalonia types so it is unit-tested
/// directly (<c>GearCurationTests</c>) and can be shared verbatim by the
/// parallel preset-curation seam. Lower rank = listed earlier.
/// </summary>
public static class GearCuration
{
    // Curated signal-chain order: flagship tone-shapers (amp/cab) first, then the
    // drives players reach for, then time + modulation colour, then the
    // utility/corrective boxes. Ids mirror the native catalogue
    // (DcRigTargetCatalog); unmapped ids fall to the end via the fallback rank.
    private static readonly string[] Order =
    [
        // ── Amps & cabs (the rig's centrepiece) ──
        // `namamp` leads (0.36): it is the amp with an actual FACE now — gain,
        // tone stack, presence/resonance, master — and it runs at any device
        // rate, so it is what the rail should offer first. `amp` stays second
        // and is labelled "Legacy Amp" by the native catalogue. This is
        // presentation only; the type ids are persisted and never change.
        // The JuceSharp 0.9 gear wave (native catalogue 57 → 100) is
        // folded INTO the existing families rather than appended as a "new gear"
        // block. The rail sorts by this order, so a tail block would have put 43
        // pedals of every family together below every curated one — which is the
        // "sorts last" fallback this list exists to avoid, just spelled
        // deliberately.
        "namamp", "amp", "rsamp", "bassamp", "poweramp", "acpre", "cab", "micpos",

        // ── Drives ──
        "overdrive", "distortion", "fuzz", "klon", "bigmuff", "rat", "ds1", "hm2",
        "octfuzz", "tubepre", "bassod", "tapesat", "boost", "feedbacker",
        "bitcrusher",

        // ── Time ──
        "delay", "tapedelay", "multitap", "duckdelay", "bbddelay", "rhythmdelay",
        "reverb", "hallreverb", "convreverb", "platereverb", "springreverb",
        "gatedreverb", "reversereverb", "shimmer", "slapback", "reversedelay",
        "granular", "freeze", "resonator", "tapestop", "glitch", "looper",

        // ── Modulation ──
        "chorus", "ensemble", "dimension", "flanger", "tzflanger", "phaser",
        "tremolo", "harmtremolo", "autopan", "rotary", "vibrato", "univibe",
        "barberpole", "ringmod", "freqshift",

        // ── Eq / filter ──
        "eq", "graphiceq", "tilt", "wah", "autowah", "envfilter", "svf",
        "filterseq", "comb", "formant", "vocoder", "talkbox", "acousticsim",

        // ── Pitch / vocal ──
        "whammy", "octaver", "subharmonic", "detune", "twelvestring", "harmonizer",
        "pitchcorrect", "doubler", "basssynth", "mikustomp", "miku",

        // ── Routing (structural — near the end, before correction) ──
        "splitter", "merger", "widener", "midside",

        // ── Dynamics / utility (correction, last) ──
        "compressor", "optocomp", "multiband", "limiter", "transient", "expander",
        "ducker", "sustainer", "swell", "volume", "noisereducer", "gate", "gain"
    ];

    private static readonly Dictionary<string, int> RankByIndex = BuildIndex();

    private static Dictionary<string, int> BuildIndex()
    {
        var map = new Dictionary<string, int>(Order.Length, StringComparer.Ordinal);
        for (var i = 0; i < Order.Length; i++)
            map[Order[i]] = i;
        return map;
    }

    /// <summary>Rank used for the sort — an unmapped (future) module ranks after
    /// every curated one (but stable), so a new native module never jumps ahead
    /// of the flagship gear before it is deliberately placed here.</summary>
    public static int Fallback => Order.Length;

    /// <summary>Curated rank of a module id: 0-based position in the quality
    /// ordering, or <see cref="Fallback"/> for anything unmapped. Lower sorts
    /// first.</summary>
    public static int RankOf(string? moduleTypeId)
        => moduleTypeId != null && RankByIndex.TryGetValue(moduleTypeId, out var rank)
            ? rank
            : Fallback;
}
