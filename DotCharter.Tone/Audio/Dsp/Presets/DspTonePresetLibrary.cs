using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
/// The built-in, immutable factory tone catalogue: musically-authored
/// <see cref="DspTonePreset"/>s spanning every <see cref="DspToneCategory"/>.
/// Every rig is expressed in signal order (gate → dynamics → drive → amp → cab →
/// eq → modulation → delay → reverb) using only real native DSP module types,
/// with each param inside its catalogue range (mirrored by
/// <see cref="DcRigTargetCatalog"/>). Omitted params fall back to the module
/// default when applied. Names are evocative soundalikes — no literal trademarks.
/// </summary>
public static partial class DspTonePresetLibrary
{
    /// <summary>
    ///     All factory presets, grouped by category (clean → experimental).
    ///     <para>
    ///         The capture-backed twins come FIRST (see
    ///         <c>DspTonePresetLibrary.Captures.cs</c>): every rig built
    ///         on the algorithmic <c>amp</c> module has a
    ///         <c>namamp</c> version carrying the original NAME, while the
    ///         algorithmic one keeps its id and is displayed as "… (Legacy)".
    ///         Order matters because a plain category listing shows this
    ///         sequence — the neural version is what a player should reach
    ///         first, with the legacy rig behind it as the CPU-cheap
    ///         alternative (a namamp node runs a neural model; the boosted
    ///         presets run two).
    ///     </para>
    /// </summary>
    public static IReadOnlyList<DspTonePreset> All { get; } = [.. BuildCaptureBacked(), .. Build()];

    // Loudness note (0.16 loudness-consistency pass): every rig below is tuned
    // so DcRigLoudnessNormalizer.EstimateChainOutputDb lands within ±6 dB of
    // the −12 dBFS target (gated by DspTonePresetLibraryTests) — saturated rigs
    // keep the amp master around 0.46–0.52, cleans around 0.6–0.75. The
    // preset-apply seam then trims the small residual with a trailing gain, so
    // don't "fix" perceived loudness here with drive params.

    // ---- fluent builders (keep the catalogue below readable) -----------------

    private static DcRigModule Mod(string type, params (string Id, float Value)[] p)
        => new(type, p.ToDictionary(x => x.Id, x => x.Value, StringComparer.Ordinal));

    // A namamp (neural amp capture) module carrying a bundled-capture file NAME
    // in its modelPath string param (ABI v22). DspTonePresetService resolves the
    // bare name to an absolute path under the NAM capture library dir at apply
    // time; DcRigApplier then pushes it via SetNodeString. Used ONLY for rigs
    // backed by a bundled capture (see DotCharter.UI/NamCaptures/ATTRIBUTION.md).
    private static DcRigModule NamAmp(string modelFile, params (string Id, float Value)[] p)
        => new(NamAmpType, p.ToDictionary(x => x.Id, x => x.Value, StringComparer.Ordinal))
        {
            StringParams = new Dictionary<string, string>(StringComparer.Ordinal) { ["modelPath"] = modelFile }
        };

    private const string NamAmpType = "namamp";

    // A module that also carries ABI-v22 STRING params (free text / file paths) —
    // used by the Miku vocal presets for their lyric slots (`miku` "phrases";
    // `mikustomp` "phrases"/"phrase2"/"phrase3"). DcRigApplier pushes each via
    // SetNodeString after the node is created, on add AND on reuse, so a preset
    // that carries its lyrics survives a re-add.
    private static DcRigModule ModS(
        string type, (string Id, string Value)[] strings, params (string Id, float Value)[] p)
        => new(type, p.ToDictionary(x => x.Id, x => x.Value, StringComparer.Ordinal))
        {
            StringParams = strings.ToDictionary(x => x.Id, x => x.Value, StringComparer.Ordinal)
        };

    private static DspTonePreset P(
        string id, string name, DspToneCategory category, string description, params DcRigModule[] mods)
        => new(id, name, category, description, new DcRigPreset(mods));

    private static IReadOnlyList<DspTonePreset> Build() =>
    [
        // ========================= CLEAN =========================
        P("clean-glass", "Glass Clean (Legacy)", DspToneCategory.Clean,
            "Sparkling blackface-style clean with airy top and a whisper of spring.",
            Mod("compressor", ("thresholdDb", -20f), ("ratio", 3f), ("attackMs", 15f), ("releaseMs", 120f), ("makeupDb", 3f)),
            Mod("amp", ("gain", 0.2f), ("bass", 0.5f), ("mid", 0.45f), ("treble", 0.65f), ("presence", 0.4f), ("master", 0.6f), ("voicing", 0f)),
            Mod("cab", ("model", 0f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("eq", ("highFreq", 6000f), ("highGainDb", 3f)),
            Mod("reverb", ("roomSize", 0.4f), ("damping", 0.5f), ("mix", 0.18f))),

        P("clean-chime-shimmer", "Chime & Shimmer (Legacy)", DspToneCategory.Clean,
            "Jangly chorused clean for arpeggios and shimmering chords.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3f), ("makeupDb", 2f)),
            Mod("amp", ("gain", 0.25f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.6f), ("voicing", 0f), ("master", 0.6f)),
            Mod("cab", ("model", 1f)),
            Mod("chorus", ("rateHz", 0.6f), ("depthMs", 5f), ("mix", 0.35f), ("voices", 2f)),
            Mod("reverb", ("roomSize", 0.45f), ("mix", 0.25f))),

        P("clean-blackface-sparkle", "Blackface Sparkle (Legacy)", DspToneCategory.Clean,
            "Classic American combo clean with tight lows and a bright bloom.",
            Mod("amp", ("gain", 0.3f), ("bass", 0.55f), ("mid", 0.4f), ("treble", 0.6f), ("presence", 0.35f), ("master", 0.6f), ("voicing", 0f)),
            Mod("cab", ("model", 0f)),
            Mod("reverb", ("roomSize", 0.45f), ("damping", 0.45f), ("mix", 0.2f))),

        P("clean-jazz-warm", "Jazz Box Warm (Legacy)", DspToneCategory.Clean,
            "Round neck-pickup jazz tone with the highs rolled off and mids full.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 2.5f), ("makeupDb", 2f)),
            Mod("amp", ("gain", 0.2f), ("bass", 0.6f), ("mid", 0.55f), ("treble", 0.35f), ("presence", 0.2f), ("voicing", 0f), ("master", 0.6f)),
            Mod("cab", ("model", 2f), ("highCutHz", 5000f)),
            Mod("eq", ("lowGainDb", 2f), ("highGainDb", -2f))),

        P("clean-funk-snap", "Funk Snap (Legacy)", DspToneCategory.Clean,
            "Heavily compressed clean with percussive snap for rhythm funk.",
            Mod("compressor", ("thresholdDb", -28f), ("ratio", 6f), ("attackMs", 5f), ("releaseMs", 80f), ("makeupDb", 5f), ("kneeDb", 3f)),
            Mod("amp", ("gain", 0.22f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.6f), ("voicing", 0f), ("master", 0.6f)),
            Mod("cab", ("model", 0f)),
            Mod("eq", ("midFreq", 1200f), ("midGainDb", 2f))),

        P("clean-country-twang", "Country Twang (Legacy)", DspToneCategory.Clean,
            "Bright chicken-pickin' clean with a short slapback echo.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 4f)),
            Mod("amp", ("gain", 0.28f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.7f), ("presence", 0.45f), ("voicing", 0f), ("master", 0.6f)),
            Mod("cab", ("model", 0f), ("highCutHz", 10000f)),
            Mod("delay", ("timeMs", 110f), ("feedback", 0.15f), ("mix", 0.18f), ("toneHz", 5000f))),

        P("clean-deep-chorus", "Deep Chorus Clean (Legacy)", DspToneCategory.Clean,
            "Lush, wide chorus over a soft clean for dreamy backing parts.",
            Mod("amp", ("gain", 0.2f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.55f), ("voicing", 0f), ("master", 0.72f)),
            Mod("cab", ("model", 1f)),
            Mod("chorus", ("rateHz", 0.4f), ("depthMs", 8f), ("mix", 0.5f), ("voices", 3f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.22f))),

        // ========================= CRUNCH =========================
        P("crunch-vox-top-boost", "Vox Top Boost Crunch (Legacy)", DspToneCategory.Crunch,
            "Chimey British combo on the edge of breakup for shimmering rhythm.",
            Mod("overdrive", ("drive", 0.4f), ("tone", 0.6f), ("level", 0.7f)),
            Mod("amp", ("gain", 0.55f), ("bass", 0.45f), ("mid", 0.55f), ("treble", 0.65f), ("presence", 0.5f), ("master", 0.48f), ("voicing", 1f)),
            Mod("cab", ("model", 1f)),
            Mod("eq", ("highGainDb", 2f))),

        P("crunch-plexi", "Plexi Crunch (Legacy)", DspToneCategory.Crunch,
            "Cranked British stack crunch with a mid-forward growl.",
            Mod("amp", ("gain", 0.6f), ("bass", 0.5f), ("mid", 0.6f), ("treble", 0.6f), ("presence", 0.5f), ("master", 0.52f), ("voicing", 1f)),
            Mod("cab", ("model", 2f)),
            Mod("eq", ("midFreq", 800f), ("midGainDb", 2f))),

        P("crunch-tweed-breakup", "Tweed Breakup (Legacy)", DspToneCategory.Crunch,
            "Warm tweed on the verge of dirt — dynamic and touch-sensitive.",
            Mod("overdrive", ("drive", 0.35f), ("tone", 0.45f), ("level", 0.7f)),
            Mod("amp", ("gain", 0.5f), ("bass", 0.55f), ("mid", 0.55f), ("treble", 0.5f), ("presence", 0.3f), ("voicing", 1f), ("master", 0.48f)),
            Mod("cab", ("model", 0f))),

        P("crunch-blues-driver", "Blues Driver (Legacy)", DspToneCategory.Crunch,
            "Transparent overdrive pushing a crunch amp for gritty blues rhythm.",
            Mod("overdrive", ("drive", 0.55f), ("tone", 0.5f), ("level", 0.7f)),
            Mod("amp", ("gain", 0.45f), ("bass", 0.5f), ("mid", 0.6f), ("treble", 0.55f), ("voicing", 1f), ("master", 0.48f)),
            Mod("cab", ("model", 2f))),

        P("crunch-ac-grind", "AC Grind (Legacy)", DspToneCategory.Crunch,
            "Bright top-boost grind with singing upper mids.",
            Mod("amp", ("gain", 0.62f), ("bass", 0.45f), ("mid", 0.55f), ("treble", 0.6f), ("presence", 0.55f), ("voicing", 1f), ("master", 0.48f)),
            Mod("cab", ("model", 1f)),
            Mod("eq", ("highFreq", 5000f), ("highGainDb", 2f))),

        P("crunch-classic-rock", "Classic Rock Rhythm (Legacy)", DspToneCategory.Crunch,
            "'70s midrange crunch with a touch of room for open chords.",
            Mod("overdrive", ("drive", 0.45f), ("tone", 0.5f), ("level", 0.7f)),
            Mod("amp", ("gain", 0.55f), ("bass", 0.55f), ("mid", 0.6f), ("treble", 0.5f), ("voicing", 1f), ("master", 0.5f)),
            Mod("cab", ("model", 2f)),
            Mod("reverb", ("roomSize", 0.35f), ("mix", 0.12f))),

        P("crunch-boosted-marshall", "Boosted Marshall (Legacy)", DspToneCategory.Crunch,
            "Tube-screamer boost tightening a British stack for punchy rhythm.",
            Mod("overdrive", ("drive", 0.3f), ("tone", 0.55f), ("level", 0.75f)),
            Mod("amp", ("gain", 0.6f), ("bass", 0.5f), ("mid", 0.65f), ("treble", 0.55f), ("voicing", 1f), ("master", 0.48f)),
            Mod("cab", ("model", 2f)),
            Mod("eq", ("lowFreq", 120f), ("lowGainDb", -2f))),

        // ========================= HIGH GAIN =========================
        // How these are built (0.16 hi-gain rebuild — measured, not vibes):
        //   gate → screamer-style boost (overdrive model 1, drive LOW / level HIGH:
        //   the mid hump + bass cut is what tightens a hi-gain amp) → the amp model
        //   that actually matches the style (v24 roster: 15 Modern Djent, 16
        //   PowerBall, 4/14 Recto, 2 Five-Oh-One, 11 JMP-800) at a REALISTIC gain
        //   (0.55–0.8, never maxed — past that the preamp only adds fizz and
        //   intermodulation) → a cab that matches the era (8 = 4x12 V30 Modern for
        //   anything modern, 7 = Greenback for classic/thrash) → corrective EQ.
        // What it replaced: a maxed generic distortion PEDAL stacked into a nearly
        // maxed lead amp through the dark Vintage 4x12 (model 3). Measured on the
        // real dcdsp cores that cost ~5x the presence-band energy (2–6 kHz) and
        // 3–5x the intermodulation of the rebuilt chains.
        P("metal-modern-djent", "Modern Djent (Legacy)", DspToneCategory.HighGain,
            "Ultra-tight scooped chug: screamer boost into a modern 8-string amp, hard gate for staccato riffing.",
            Mod("gate", ("thresholdDb", -45f), ("ratio", 12f), ("attackMs", 1f), ("holdMs", 8f), ("releaseMs", 80f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.15f), ("tone", 0.7f), ("level", 0.85f)),
            Mod("amp", ("gain", 0.68f), ("bass", 0.45f), ("mid", 0.5f), ("treble", 0.6f), ("presence", 0.55f), ("master", 0.48f), ("voicing", 15f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 110f), ("highCutHz", 9000f)),
            Mod("eq", ("lowFreq", 100f), ("lowGainDb", -2f), ("midFreq", 700f), ("midQ", 1.4f), ("midGainDb", -3f), ("highFreq", 4500f), ("highGainDb", 2f))),

        P("metal-scooped-thrash", "Scooped Thrash (Legacy)", DspToneCategory.HighGain,
            "Bay Area thrash — boosted JMP stack, heavily scooped mids, biting highs, fast gate.",
            Mod("gate", ("thresholdDb", -50f), ("ratio", 10f), ("releaseMs", 60f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.2f), ("tone", 0.65f), ("level", 0.82f)),
            Mod("amp", ("gain", 0.82f), ("bass", 0.6f), ("mid", 0.3f), ("treble", 0.74f), ("presence", 0.68f), ("master", 0.48f), ("voicing", 11f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 95f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 650f), ("midQ", 1.2f), ("midGainDb", -6f), ("highFreq", 4500f), ("highGainDb", 4f))),

        P("metal-5150-rhythm", "5150 Rhythm (Legacy)", DspToneCategory.HighGain,
            "Saturated American high-gain rhythm — boosted lead channel, controlled low end.",
            Mod("gate", ("thresholdDb", -48f), ("ratio", 10f), ("releaseMs", 70f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.15f), ("tone", 0.65f), ("level", 0.85f)),
            Mod("amp", ("gain", 0.72f), ("bass", 0.55f), ("mid", 0.45f), ("treble", 0.6f), ("presence", 0.5f), ("master", 0.46f), ("voicing", 2f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 100f), ("highCutHz", 8500f)),
            Mod("eq", ("midFreq", 700f), ("midGainDb", -2f), ("highFreq", 4500f), ("highGainDb", 2f))),

        // Doom deliberately keeps NO gate (the sustain/feedback IS the instrument);
        // the wall is the Gnash distortion model into a big British head — the
        // old maxed GENERIC pedal into a lead amp measured 33 % intermodulation
        // with a 615 Hz centroid; this keeps the sludge but finds the top again.
        P("metal-doom-fuzz", "Doom Fuzz Wall (Legacy)", DspToneCategory.HighGain,
            "Thick, loose sludge fuzz — a snarling dirt box into a cranked British stack, massive lows, cavernous verb.",
            Mod("distortion", ("model", 1f), ("gain", 0.8f), ("tone", 0.4f), ("level", 0.65f), ("tightHz", 60f)),
            Mod("amp", ("gain", 0.6f), ("bass", 0.7f), ("mid", 0.55f), ("treble", 0.55f), ("presence", 0.45f), ("master", 0.48f), ("voicing", 10f)),
            Mod("cab", ("model", 2f), ("lowCutHz", 80f), ("highCutHz", 7000f)),
            Mod("eq", ("lowFreq", 90f), ("lowGainDb", 1f), ("midFreq", 800f), ("midGainDb", -2f), ("highFreq", 4000f), ("highGainDb", 2f)),
            Mod("reverb", ("roomSize", 0.6f), ("mix", 0.2f))),

        P("metal-nu-chug", "Nu-Metal Chug (Legacy)", DspToneCategory.HighGain,
            "Detuned, scooped, low-heavy chug — boosted rectifier for down-tuned groove riffs.",
            Mod("gate", ("thresholdDb", -46f), ("ratio", 12f), ("releaseMs", 70f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.2f), ("tone", 0.6f), ("level", 0.8f)),
            Mod("amp", ("gain", 0.72f), ("bass", 0.65f), ("mid", 0.4f), ("treble", 0.58f), ("presence", 0.5f), ("voicing", 4f), ("master", 0.48f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 95f), ("highCutHz", 8500f)),
            Mod("eq", ("lowFreq", 110f), ("lowGainDb", 1f), ("midFreq", 1200f), ("midQ", 1f), ("midGainDb", 3f), ("highFreq", 4500f), ("highGainDb", 2f))),

        // The buzzsaw IS a pedal tone: the Metal distortion model (2) slammed into
        // a moderately driven British amp — that is how the Swedish records were cut.
        P("metal-death-buzzsaw", "Death Metal Buzzsaw (Legacy)", DspToneCategory.HighGain,
            "Scandinavian buzzsaw — a maxed metal pedal into a Greenback stack, tight gate.",
            Mod("gate", ("thresholdDb", -44f), ("ratio", 14f), ("releaseMs", 55f)),
            Mod("distortion", ("model", 2f), ("gain", 0.8f), ("tone", 0.7f), ("level", 0.6f), ("tightHz", 90f)),
            Mod("amp", ("gain", 0.6f), ("bass", 0.5f), ("mid", 0.55f), ("treble", 0.72f), ("presence", 0.65f), ("voicing", 11f), ("master", 0.46f)),
            Mod("cab", ("model", 2f), ("lowCutHz", 100f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1000f), ("midGainDb", 3f), ("highFreq", 5000f), ("highGainDb", 3f))),

        P("metal-metalcore-rhythm", "Metalcore Rhythm (Legacy)", DspToneCategory.HighGain,
            "Modern metalcore rhythm — boosted PowerBall, tight lows, scooped mids, crisp attack.",
            Mod("gate", ("thresholdDb", -47f), ("ratio", 11f), ("releaseMs", 65f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.2f), ("tone", 0.65f), ("level", 0.85f)),
            Mod("amp", ("gain", 0.7f), ("bass", 0.55f), ("mid", 0.45f), ("treble", 0.62f), ("presence", 0.55f), ("voicing", 16f), ("master", 0.48f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 110f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 800f), ("midQ", 1.2f), ("midGainDb", -3f), ("highFreq", 4500f), ("highGainDb", 2f))),

        P("metal-recto-crush", "Recto Crush (Legacy)", DspToneCategory.HighGain,
            "Big-bottom American rectifier — boosted, tightened, and pushed back into focus.",
            Mod("gate", ("thresholdDb", -48f), ("ratio", 10f), ("releaseMs", 75f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.18f), ("tone", 0.65f), ("level", 0.85f)),
            Mod("amp", ("gain", 0.75f), ("bass", 0.62f), ("mid", 0.42f), ("treble", 0.6f), ("presence", 0.5f), ("voicing", 4f), ("master", 0.46f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 100f), ("highCutHz", 8500f)),
            Mod("eq", ("lowFreq", 100f), ("lowGainDb", -1f), ("midFreq", 1300f), ("midQ", 1f), ("midGainDb", 3f), ("highFreq", 4500f), ("highGainDb", 2f))),

        P("metal-industrial-grind", "Industrial Grind (Legacy)", DspToneCategory.HighGain,
            "Cold, gated, surgically tight machine-metal with a subtle jet flange.",
            Mod("gate", ("thresholdDb", -42f), ("ratio", 16f), ("attackMs", 0.5f), ("releaseMs", 50f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.12f), ("tone", 0.72f), ("level", 0.88f)),
            Mod("amp", ("gain", 0.7f), ("bass", 0.45f), ("mid", 0.48f), ("treble", 0.65f), ("presence", 0.6f), ("voicing", 15f), ("master", 0.48f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 130f), ("highCutHz", 9500f)),
            Mod("eq", ("lowFreq", 120f), ("lowGainDb", -2f), ("midFreq", 900f), ("midGainDb", -2f), ("highFreq", 5000f), ("highGainDb", 2f)),
            Mod("flanger", ("rateHz", 0.2f), ("depthMs", 2f), ("feedback", 0.3f), ("mix", 0.2f))),

        // ========================= LEAD =========================
        // Same rebuild rules as the high-gain block: a boost in FRONT (low drive /
        // high level), a REAL amp model per style (17 Overdrive Special, 11 JMP-800,
        // 6 Brown Variac, 16 PowerBall …) at a sane gain, and a cab that isn't the
        // dark Vintage 4x12 by default (7 Greenback for classic, 8 V30 for modern).
        P("lead-singing-sustain", "Singing Sustain Lead (Legacy)", DspToneCategory.Lead,
            "Smooth, violin-like sustain — hot-rodded lead amp with mid push, delay and hall.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 3f)),
            Mod("overdrive", ("model", 3f), ("drive", 0.4f), ("tone", 0.6f), ("level", 0.78f)),
            Mod("amp", ("gain", 0.72f), ("bass", 0.48f), ("mid", 0.7f), ("treble", 0.72f), ("presence", 0.65f), ("master", 0.48f), ("voicing", 2f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1000f), ("midGainDb", 4f), ("highFreq", 4000f), ("highGainDb", 4f)),
            Mod("delay", ("timeMs", 400f), ("feedback", 0.35f), ("mix", 0.25f), ("toneHz", 4000f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.22f))),

        P("lead-brit-800", "Brit 800 Lead (Legacy)", DspToneCategory.Lead,
            "Boosted JMP-style lead with singing mids and a tasteful echo.",
            Mod("overdrive", ("model", 1f), ("drive", 0.3f), ("tone", 0.62f), ("level", 0.82f)),
            Mod("amp", ("gain", 0.76f), ("bass", 0.48f), ("mid", 0.65f), ("treble", 0.75f), ("presence", 0.68f), ("master", 0.48f), ("voicing", 11f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1100f), ("midGainDb", 3f), ("highFreq", 4000f), ("highGainDb", 4f)),
            Mod("delay", ("timeMs", 350f), ("feedback", 0.3f), ("mix", 0.2f)),
            Mod("reverb", ("roomSize", 0.4f), ("mix", 0.15f))),

        P("lead-liquid-legato", "Liquid Legato (Legacy)", DspToneCategory.Lead,
            "Fluid fusion lead — smooth boutique gain, ping-pong delay and deep reverb.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3f), ("makeupDb", 2f)),
            Mod("amp", ("gain", 0.7f), ("bass", 0.5f), ("mid", 0.7f), ("treble", 0.68f), ("presence", 0.6f), ("voicing", 17f), ("master", 0.48f)),
            Mod("cab", ("model", 2f), ("lowCutHz", 85f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1200f), ("midGainDb", 3f), ("highFreq", 4000f), ("highGainDb", 4f)),
            Mod("delay", ("timeMs", 450f), ("feedback", 0.4f), ("mix", 0.3f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.6f), ("mix", 0.28f))),

        P("lead-brown-sound", "Brown Sound Lead (Legacy)", DspToneCategory.Lead,
            "'80s hot-rodded brown-sound lead with tight lows and a slap of verb.",
            Mod("overdrive", ("model", 1f), ("drive", 0.3f), ("tone", 0.6f), ("level", 0.8f)),
            Mod("amp", ("gain", 0.75f), ("bass", 0.52f), ("mid", 0.62f), ("treble", 0.74f), ("presence", 0.66f), ("voicing", 6f), ("master", 0.48f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1000f), ("midGainDb", 2f), ("highFreq", 4000f), ("highGainDb", 4f)),
            Mod("delay", ("timeMs", 300f), ("feedback", 0.3f), ("mix", 0.18f)),
            Mod("reverb", ("roomSize", 0.4f), ("mix", 0.15f))),

        P("lead-soulful-blues", "Soulful Blues Lead (Legacy)", DspToneCategory.Lead,
            "Expressive, vocal blues lead — mid-rich crunch with room and echo.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3f), ("makeupDb", 3f)),
            Mod("overdrive", ("drive", 0.55f), ("tone", 0.5f), ("level", 0.7f)),
            Mod("amp", ("gain", 0.55f), ("bass", 0.5f), ("mid", 0.65f), ("treble", 0.55f), ("voicing", 1f), ("master", 0.48f)),
            Mod("cab", ("model", 2f)),
            Mod("delay", ("timeMs", 380f), ("feedback", 0.25f), ("mix", 0.16f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.22f))),

        P("lead-neoclassical-shred", "Neoclassical Shred (Legacy)", DspToneCategory.Lead,
            "Fast, tight, mid-boosted shred lead — screamer in front, gate for clean runs.",
            Mod("gate", ("thresholdDb", -50f), ("ratio", 8f), ("releaseMs", 90f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.25f), ("tone", 0.65f), ("level", 0.85f)),
            Mod("amp", ("gain", 0.7f), ("bass", 0.45f), ("mid", 0.7f), ("treble", 0.62f), ("presence", 0.55f), ("voicing", 16f), ("master", 0.48f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 100f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1100f), ("midGainDb", 4f), ("highFreq", 4500f), ("highGainDb", 2f)),
            Mod("delay", ("timeMs", 320f), ("feedback", 0.35f), ("mix", 0.22f)),
            Mod("reverb", ("roomSize", 0.45f), ("mix", 0.18f))),

        P("lead-harmonic-boost", "Harmonic Lead Boost (Legacy)", DspToneCategory.Lead,
            "Searing mid-focused lead that coaxes out feedback and harmonics.",
            Mod("overdrive", ("model", 1f), ("drive", 0.35f), ("tone", 0.62f), ("level", 0.82f)),
            Mod("amp", ("gain", 0.7f), ("bass", 0.48f), ("mid", 0.72f), ("treble", 0.7f), ("presence", 0.6f), ("voicing", 2f), ("master", 0.48f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 95f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 900f), ("midQ", 2f), ("midGainDb", 5f), ("highFreq", 4000f), ("highGainDb", 3f)),
            Mod("delay", ("timeMs", 420f), ("feedback", 0.33f), ("mix", 0.24f)),
            Mod("reverb", ("roomSize", 0.55f), ("mix", 0.2f))),

        // ========================= ACOUSTIC =========================
        P("acoustic-natural", "Natural Acoustic", DspToneCategory.Acoustic,
            "Piezo-tamed acoustic sim: body boost, honk cut, sparkling air.",
            Mod("compressor", ("thresholdDb", -24f), ("ratio", 2.5f), ("makeupDb", 2f)),
            Mod("eq", ("lowFreq", 120f), ("lowGainDb", 2f), ("midFreq", 500f), ("midQ", 1.5f), ("midGainDb", -3f), ("highFreq", 8000f), ("highGainDb", 5f)),
            Mod("reverb", ("roomSize", 0.4f), ("damping", 0.4f), ("mix", 0.18f))),

        P("acoustic-bright-strummer", "Bright Strummer", DspToneCategory.Acoustic,
            "Sparkly steel-string strumming voice with plenty of top-end shimmer.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3f), ("makeupDb", 2f)),
            Mod("eq", ("lowGainDb", 1f), ("midFreq", 600f), ("midGainDb", -4f), ("highFreq", 10000f), ("highGainDb", 6f)),
            Mod("reverb", ("roomSize", 0.45f), ("mix", 0.2f))),

        P("acoustic-warm-fingerstyle", "Warm Fingerstyle", DspToneCategory.Acoustic,
            "Mellow, woody fingerpicking tone with rounded body and soft verb.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 2.5f), ("makeupDb", 2f)),
            Mod("eq", ("lowFreq", 150f), ("lowGainDb", 3f), ("midFreq", 700f), ("midGainDb", -2f), ("highFreq", 7000f), ("highGainDb", 3f)),
            Mod("reverb", ("roomSize", 0.5f), ("damping", 0.6f), ("mix", 0.16f))),

        P("acoustic-12-string", "12-String Shimmer", DspToneCategory.Acoustic,
            "Chorused twelve-string shimmer — octave sparkle and gentle wash.",
            Mod("eq", ("lowGainDb", 1f), ("highFreq", 9000f), ("highGainDb", 5f), ("outputTrimDb", 2f)),
            Mod("chorus", ("rateHz", 0.5f), ("depthMs", 4f), ("mix", 0.3f), ("voices", 3f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.2f))),

        P("acoustic-stage", "Stage Acoustic", DspToneCategory.Acoustic,
            "Feedback-tamed live acoustic: notched low-mids, controlled presence.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3f), ("makeupDb", 2f)),
            Mod("eq", ("lowFreq", 100f), ("lowGainDb", -2f), ("midFreq", 400f), ("midQ", 3f), ("midGainDb", -5f), ("highFreq", 8000f), ("highGainDb", 4f)),
            Mod("reverb", ("roomSize", 0.35f), ("mix", 0.14f))),

        // ========================= BASS =========================
        P("bass-studio-di", "Studio DI Bass (Legacy)", DspToneCategory.Bass,
            "Clean, compressed hi-fi DI bass with a modern scooped voicing.",
            Mod("compressor", ("thresholdDb", -20f), ("ratio", 4f), ("attackMs", 15f), ("releaseMs", 120f), ("makeupDb", 4f), ("kneeDb", 3f)),
            Mod("amp", ("gain", 0.3f), ("bass", 0.6f), ("mid", 0.45f), ("treble", 0.5f), ("presence", 0.3f), ("master", 0.6f), ("voicing", 0f)),
            Mod("eq", ("lowFreq", 80f), ("lowGainDb", 4f), ("midFreq", 500f), ("midGainDb", -3f), ("highFreq", 3000f), ("highGainDb", 2f))),

        P("bass-motown-fingerstyle", "Motown Fingerstyle (Legacy)", DspToneCategory.Bass,
            "Warm, round fingerstyle bass with full mids and rolled-off top.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3.5f), ("makeupDb", 3f)),
            Mod("amp", ("gain", 0.35f), ("bass", 0.65f), ("mid", 0.55f), ("treble", 0.4f), ("voicing", 0f), ("master", 0.6f)),
            Mod("eq", ("lowFreq", 80f), ("lowGainDb", 3f), ("midFreq", 700f), ("midGainDb", 2f), ("highGainDb", -2f))),

        P("bass-pick-rock", "Pick Rock Bass (Legacy)", DspToneCategory.Bass,
            "Bright pick attack with mid punch and a hair of grit.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 3f)),
            Mod("overdrive", ("drive", 0.2f), ("tone", 0.6f), ("level", 0.7f)),
            Mod("amp", ("gain", 0.4f), ("bass", 0.6f), ("mid", 0.55f), ("treble", 0.55f), ("voicing", 1f), ("master", 0.48f)),
            Mod("eq", ("lowFreq", 90f), ("lowGainDb", 2f), ("midFreq", 900f), ("midGainDb", 3f), ("highFreq", 3500f), ("highGainDb", 2f))),

        P("bass-grindstone", "Grindstone Bass (Legacy)", DspToneCategory.Bass,
            "Overdriven grindy bass that cuts through a loud mix.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 2f)),
            Mod("overdrive", ("drive", 0.55f), ("tone", 0.55f), ("level", 0.7f)),
            Mod("amp", ("gain", 0.5f), ("bass", 0.6f), ("mid", 0.6f), ("treble", 0.5f), ("voicing", 1f), ("master", 0.48f)),
            Mod("eq", ("lowFreq", 100f), ("lowGainDb", 2f), ("midFreq", 800f), ("midGainDb", 3f))),

        P("bass-slap-funk", "Slap Funk Bass (Legacy)", DspToneCategory.Bass,
            "Scooped, snappy slap tone — big lows, sizzling highs, quick comp.",
            Mod("compressor", ("thresholdDb", -24f), ("ratio", 5f), ("attackMs", 8f), ("releaseMs", 100f), ("makeupDb", 4f)),
            Mod("amp", ("gain", 0.32f), ("bass", 0.6f), ("mid", 0.35f), ("treble", 0.6f), ("voicing", 0f), ("master", 0.6f)),
            Mod("eq", ("lowFreq", 80f), ("lowGainDb", 4f), ("midFreq", 500f), ("midQ", 1.5f), ("midGainDb", -5f), ("highFreq", 4000f), ("highGainDb", 4f))),

        P("bass-deep-sub", "Deep Sub Bass (Legacy)", DspToneCategory.Bass,
            "Dub-deep sub bass — huge lows, dark top, felt more than heard.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 3f)),
            Mod("amp", ("gain", 0.35f), ("bass", 0.75f), ("mid", 0.4f), ("treble", 0.3f), ("voicing", 0f), ("master", 0.6f)),
            Mod("eq", ("lowFreq", 60f), ("lowGainDb", 5f), ("midFreq", 600f), ("midGainDb", -2f), ("highFreq", 3000f), ("highGainDb", -3f))),

        P("bass-metal-clank", "Metal Bass Clank (Legacy)", DspToneCategory.Bass,
            "Aggressive distorted bass with clanky pick attack and tight gate.",
            Mod("gate", ("thresholdDb", -50f), ("ratio", 8f), ("releaseMs", 90f)),
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 2f)),
            Mod("distortion", ("gain", 0.5f), ("tone", 0.6f), ("level", 0.6f), ("tightHz", 120f)),
            Mod("amp", ("gain", 0.5f), ("bass", 0.6f), ("mid", 0.55f), ("treble", 0.6f), ("voicing", 1f), ("master", 0.48f)),
            Mod("eq", ("lowFreq", 90f), ("lowGainDb", 2f), ("midFreq", 1000f), ("midGainDb", 3f), ("highFreq", 3500f), ("highGainDb", 3f))),

        // ========================= AMBIENT =========================
        P("ambient-wash", "Ambient Wash (Legacy)", DspToneCategory.Ambient,
            "Soft clean drowned in ping-pong delay and a vast reverb wash.",
            Mod("compressor", ("thresholdDb", -24f), ("ratio", 2f), ("makeupDb", 2f)),
            Mod("amp", ("gain", 0.2f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.5f), ("voicing", 0f), ("master", 0.6f)),
            Mod("cab", ("model", 1f)),
            Mod("chorus", ("rateHz", 0.3f), ("depthMs", 6f), ("mix", 0.3f), ("voices", 3f)),
            Mod("delay", ("timeMs", 500f), ("feedback", 0.45f), ("mix", 0.35f), ("pingPong", 1f), ("toneHz", 3500f)),
            Mod("reverb", ("roomSize", 0.85f), ("damping", 0.4f), ("width", 1f), ("mix", 0.4f))),

        P("ambient-cathedral", "Cathedral (Legacy)", DspToneCategory.Ambient,
            "Enormous, slow-decaying reverb space for sustained, prayerful chords.",
            Mod("amp", ("gain", 0.18f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.5f), ("voicing", 0f), ("master", 0.75f)),
            Mod("cab", ("model", 1f)),
            Mod("delay", ("timeMs", 600f), ("feedback", 0.4f), ("mix", 0.3f)),
            Mod("reverb", ("roomSize", 0.95f), ("damping", 0.3f), ("width", 1f), ("mix", 0.45f))),

        P("ambient-shimmer-pad", "Shimmer Pad (Legacy)", DspToneCategory.Ambient,
            "Chorused pad with long regenerating delay and a huge shimmering tail.",
            Mod("amp", ("gain", 0.2f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.5f), ("voicing", 0f), ("master", 0.72f)),
            Mod("cab", ("model", 1f)),
            Mod("chorus", ("rateHz", 0.25f), ("depthMs", 8f), ("mix", 0.4f), ("voices", 3f)),
            Mod("delay", ("timeMs", 550f), ("feedback", 0.5f), ("mix", 0.32f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.9f), ("mix", 0.4f))),

        P("ambient-post-rock-swell", "Post-Rock Swell (Legacy)", DspToneCategory.Ambient,
            "Edge-of-crunch guitar under sweeping delays and a big reverb bed.",
            Mod("overdrive", ("drive", 0.35f), ("tone", 0.55f), ("level", 0.7f)),
            Mod("amp", ("gain", 0.4f), ("bass", 0.5f), ("mid", 0.55f), ("treble", 0.55f), ("voicing", 1f), ("master", 0.48f)),
            Mod("cab", ("model", 2f)),
            Mod("delay", ("timeMs", 480f), ("feedback", 0.5f), ("mix", 0.35f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.8f), ("mix", 0.35f))),

        P("ambient-dream-tremolo", "Dream Tremolo (Legacy)", DspToneCategory.Ambient,
            "Pulsing tremolo over a clean amp with a soft delay and deep reverb.",
            Mod("amp", ("gain", 0.22f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.5f), ("voicing", 0f), ("master", 0.72f)),
            Mod("cab", ("model", 0f)),
            Mod("tremolo", ("rateHz", 4.5f), ("depth", 0.6f), ("shape", 1f)),
            Mod("delay", ("timeMs", 420f), ("feedback", 0.35f), ("mix", 0.25f)),
            Mod("reverb", ("roomSize", 0.7f), ("mix", 0.3f))),

        // ========================= EXPERIMENTAL =========================
        P("exp-jet-flanger", "Jet Flanger Freakout (Legacy)", DspToneCategory.Experimental,
            "Extreme high-feedback flanger jet-sweep over a crunchy rhythm.",
            Mod("amp", ("gain", 0.4f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.55f), ("voicing", 1f), ("master", 0.48f)),
            Mod("cab", ("model", 2f)),
            Mod("flanger", ("rateHz", 0.15f), ("depthMs", 4.5f), ("feedback", 0.9f), ("mix", 0.6f)),
            Mod("delay", ("timeMs", 350f), ("feedback", 0.4f), ("mix", 0.25f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.2f))),

        P("exp-runaway-delay", "Runaway Delay (Legacy)", DspToneCategory.Experimental,
            "Near-self-oscillating delay spiraling over a driven lead voice.",
            Mod("overdrive", ("drive", 0.5f), ("tone", 0.55f), ("level", 0.7f)),
            Mod("amp", ("gain", 0.5f), ("bass", 0.5f), ("mid", 0.55f), ("treble", 0.55f), ("voicing", 2f), ("master", 0.48f)),
            Mod("cab", ("model", 2f)),
            Mod("delay", ("timeMs", 666f), ("feedback", 0.9f), ("mix", 0.45f), ("pingPong", 1f), ("toneHz", 3000f)),
            Mod("reverb", ("roomSize", 0.7f), ("mix", 0.3f))),

        P("exp-seasick-warble", "Seasick Warble (Legacy)", DspToneCategory.Experimental,
            "Wobbly detuned chorus crossed with a square-wave tremolo for queasy motion.",
            Mod("amp", ("gain", 0.3f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.5f), ("voicing", 0f), ("master", 0.6f)),
            Mod("cab", ("model", 1f)),
            Mod("chorus", ("rateHz", 0.15f), ("depthMs", 12f), ("mix", 0.6f), ("voices", 3f)),
            Mod("tremolo", ("rateHz", 6f), ("depth", 0.7f), ("shape", 2f)),
            Mod("reverb", ("roomSize", 0.55f), ("mix", 0.25f))),

        P("exp-gated-stutter-fuzz", "Gated Stutter Fuzz (Legacy)", DspToneCategory.Experimental,
            "Hard-gated velcro fuzz that stutters and chokes into silence.",
            Mod("gate", ("thresholdDb", -30f), ("ratio", 20f), ("attackMs", 0.5f), ("holdMs", 0f), ("releaseMs", 30f)),
            Mod("distortion", ("gain", 0.95f), ("tone", 0.7f), ("level", 0.6f), ("tightHz", 200f)),
            Mod("amp", ("gain", 0.7f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.6f), ("voicing", 2f), ("master", 0.48f)),
            Mod("cab", ("model", 3f), ("lowCutHz", 140f)),
            Mod("reverb", ("roomSize", 0.4f), ("mix", 0.15f))),

        P("exp-sub-octave-doom", "Sub-Octave Doom (Legacy)", DspToneCategory.Experimental,
            "Cavernous low-tuned doom with a slow flange and endless reverb.",
            Mod("distortion", ("gain", 0.85f), ("tone", 0.3f), ("level", 0.65f), ("tightHz", 40f)),
            Mod("amp", ("gain", 0.65f), ("bass", 0.8f), ("mid", 0.5f), ("treble", 0.35f), ("voicing", 2f), ("master", 0.48f)),
            Mod("cab", ("model", 2f), ("lowCutHz", 40f), ("highCutHz", 4500f)),
            Mod("flanger", ("rateHz", 0.1f), ("depthMs", 3f), ("feedback", 0.6f), ("mix", 0.4f)),
            Mod("reverb", ("roomSize", 0.9f), ("mix", 0.4f))),

        // ================= ARTIST & GENRE SIGNATURES (0.16 tone catalogue) =====
        // Evocative soundalikes translated from real-world rig documentation into
        // the 39-module catalogue. Names are descriptive, never an endorsement.
        // Every rig exploits the Wave-2A cores (real spring/tape/wah/doubler) and
        // stays inside the ±6 dB loudness band (see DspTonePresetLibraryTests).

        // ---- Clean ----
        P("artist-polyphia-clean-tap", "Polyphia Clean Tap (Legacy)", DspToneCategory.Clean,
            "Glassy hi-fi tapping clean: heavy compression, mid-forward voice, subtle stereo bloom.",
            Mod("compressor", ("thresholdDb", -26f), ("ratio", 6f), ("attackMs", 8f), ("releaseMs", 90f), ("makeupDb", 5f), ("kneeDb", 4f)),
            Mod("amp", ("gain", 0.28f), ("bass", 0.4f), ("mid", 0.68f), ("treble", 0.6f), ("presence", 0.45f), ("master", 0.62f), ("voicing", 0f)),
            Mod("cab", ("model", 0f), ("lowCutHz", 95f), ("highCutHz", 10000f)),
            Mod("eq", ("midFreq", 1400f), ("midGainDb", 3f), ("highFreq", 6000f), ("highGainDb", 2f)),
            Mod("doubler", ("detuneCents", 6f), ("spread", 0.6f), ("delayMs", 10f), ("mix", 0.22f)),
            Mod("reverb", ("roomSize", 0.4f), ("damping", 0.5f), ("mix", 0.14f))),

        P("artist-yorushika-bright-clean", "Yorushika Bright Clean (Legacy)", DspToneCategory.Clean,
            "Airy J-rock Strat clean with sparkling top and lush ambient delay-and-verb tails.",
            Mod("compressor", ("thresholdDb", -24f), ("ratio", 3f), ("makeupDb", 3f)),
            Mod("amp", ("gain", 0.24f), ("bass", 0.48f), ("mid", 0.46f), ("treble", 0.66f), ("presence", 0.4f), ("master", 0.62f), ("voicing", 0f)),
            Mod("cab", ("model", 0f), ("highCutHz", 11000f)),
            Mod("eq", ("highFreq", 8000f), ("highGainDb", 3f)),
            Mod("delay", ("timeMs", 440f), ("feedback", 0.32f), ("mix", 0.24f), ("pingPong", 1f), ("toneHz", 5000f)),
            Mod("reverb", ("roomSize", 0.6f), ("damping", 0.4f), ("mix", 0.26f))),

        P("artist-knopfler-sultans-clean", "Sultans Clean (Legacy)", DspToneCategory.Clean,
            "Brown-Vibrolux fingerstyle clean with squeezed Orange-Squeezer bloom and Strat notch quack.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("attackMs", 12f), ("releaseMs", 110f), ("makeupDb", 4f)),
            Mod("amp", ("gain", 0.3f), ("bass", 0.52f), ("mid", 0.5f), ("treble", 0.62f), ("presence", 0.38f), ("master", 0.6f), ("voicing", 0f)),
            Mod("cab", ("model", 0f)),
            Mod("eq", ("midFreq", 900f), ("midQ", 1.5f), ("midGainDb", -2f), ("highFreq", 6500f), ("highGainDb", 2f))),

        P("artist-frusciante-funk-clean", "Frusciante Funk (Legacy)", DspToneCategory.Clean,
            "Bright compressed single-note funk with a cocked wah honk — snappy, percussive, muted.",
            Mod("compressor", ("thresholdDb", -26f), ("ratio", 5f), ("attackMs", 5f), ("releaseMs", 80f), ("makeupDb", 5f)),
            Mod("wah", ("mode", 0f), ("position", 0.62f), ("resonance", 0.6f), ("mix", 0.8f)),
            Mod("amp", ("gain", 0.34f), ("bass", 0.45f), ("mid", 0.55f), ("treble", 0.64f), ("presence", 0.45f), ("master", 0.6f), ("voicing", 0f)),
            Mod("cab", ("model", 1f))),

        P("genre-city-pop-clean", "City Pop Shine (Legacy)", DspToneCategory.Clean,
            "Immaculate hi-fi clean under a lush dual chorus and doubler with a soft plate wash.",
            Mod("compressor", ("thresholdDb", -24f), ("ratio", 3f), ("makeupDb", 3f)),
            Mod("amp", ("gain", 0.22f), ("bass", 0.5f), ("mid", 0.48f), ("treble", 0.62f), ("presence", 0.42f), ("master", 0.64f), ("voicing", 0f)),
            Mod("cab", ("model", 1f)),
            Mod("chorus", ("rateHz", 0.45f), ("depthMs", 6f), ("mix", 0.42f), ("voices", 3f)),
            Mod("doubler", ("detuneCents", 10f), ("spread", 0.8f), ("delayMs", 14f), ("mix", 0.3f)),
            Mod("platereverb", ("decay", 0.5f), ("damping", 0.35f), ("mix", 0.2f), ("width", 1f))),

        P("genre-surf-twang", "Surf Twang (Legacy)", DspToneCategory.Clean,
            "Drippy cranked spring reverb and lush amp tremolo over a bright twangy clean.",
            Mod("amp", ("gain", 0.28f), ("bass", 0.48f), ("mid", 0.5f), ("treble", 0.68f), ("presence", 0.5f), ("master", 0.62f), ("voicing", 0f)),
            Mod("cab", ("model", 0f), ("highCutHz", 11000f)),
            Mod("tremolo", ("rateHz", 5.5f), ("depth", 0.6f), ("shape", 0f)),
            Mod("springreverb", ("decay", 0.7f), ("tone", 0.6f), ("drip", 0.8f), ("mix", 0.35f))),

        // ---- Crunch ----
        P("artist-acdc-angus-crunch", "Angus High Voltage (Legacy)", DspToneCategory.Crunch,
            "Low-gain dimed Plexi crunch — bright, mid-and-treble forward, light on bass, no effects.",
            Mod("amp", ("gain", 0.5f), ("bass", 0.42f), ("mid", 0.58f), ("treble", 0.66f), ("presence", 0.55f), ("master", 0.5f), ("voicing", 1f)),
            Mod("cab", ("model", 2f)),
            Mod("eq", ("lowFreq", 120f), ("lowGainDb", -2f), ("highGainDb", 2f))),

        P("artist-srv-texas-blues", "Texas Flood (Legacy)", DspToneCategory.Crunch,
            "Edge-of-breakup Fender pushed by a level-boost overdrive, warm mids, light spring drip.",
            Mod("klon", ("gain", 0.25f), ("treble", 0.55f), ("output", 0.7f), ("blend", 0.6f)),
            Mod("amp", ("gain", 0.48f), ("bass", 0.55f), ("mid", 0.62f), ("treble", 0.58f), ("presence", 0.4f), ("master", 0.5f), ("voicing", 1f)),
            Mod("cab", ("model", 0f)),
            Mod("eq", ("midFreq", 800f), ("midGainDb", 2f)),
            Mod("springreverb", ("decay", 0.4f), ("tone", 0.5f), ("drip", 0.5f), ("mix", 0.18f))),

        P("artist-nirvana-teen-spirit", "Teen Spirit Slam (Legacy)", DspToneCategory.Crunch,
            "A maxed orange distortion box slammed into a clean preamp, with a watery small-clone chorus.",
            Mod("chorus", ("rateHz", 1.2f), ("depthMs", 4f), ("mix", 0.28f), ("voices", 2f)),
            Mod("distortion", ("gain", 0.6f), ("tone", 0.55f), ("level", 0.6f), ("tightHz", 80f)),
            Mod("amp", ("gain", 0.32f), ("bass", 0.55f), ("mid", 0.55f), ("treble", 0.55f), ("presence", 0.35f), ("master", 0.5f), ("voicing", 0f)),
            Mod("cab", ("model", 2f))),

        P("genre-punk-marshall", "Punk Buzz (Legacy)", DspToneCategory.Crunch,
            "Bright fast cranked-Marshall crunch — percussive downstrokes, minimal gain smear, bone dry.",
            Mod("amp", ("gain", 0.58f), ("bass", 0.45f), ("mid", 0.55f), ("treble", 0.68f), ("presence", 0.6f), ("master", 0.5f), ("voicing", 1f)),
            Mod("cab", ("model", 2f)),
            Mod("eq", ("highFreq", 5000f), ("highGainDb", 3f))),

        P("artist-weezer-blue-crunch", "Blue Album Crunch (Legacy)", DspToneCategory.Crunch,
            "Fat mid-rich low-volume Boogie Mark crunch — big round fuzzy power chords, bone dry.",
            Mod("amp", ("gain", 0.62f), ("bass", 0.55f), ("mid", 0.66f), ("treble", 0.52f), ("presence", 0.4f), ("master", 0.5f), ("voicing", 1f)),
            Mod("cab", ("model", 2f)),
            Mod("eq", ("midFreq", 700f), ("midGainDb", 3f))),

        // ---- Lead ----
        P("artist-galneryus-neo-lead", "Neoclassical Fire (Legacy)", DspToneCategory.Lead,
            "Articulate mid-forward high-gain lead: front-end boost for cut, singing sustain, epic delay.",
            Mod("gate", ("thresholdDb", -52f), ("ratio", 8f), ("releaseMs", 90f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.25f), ("tone", 0.65f), ("level", 0.85f)),
            Mod("amp", ("gain", 0.72f), ("bass", 0.46f), ("mid", 0.72f), ("treble", 0.6f), ("presence", 0.55f), ("master", 0.48f), ("voicing", 16f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 100f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1100f), ("midQ", 1.5f), ("midGainDb", 4f), ("highFreq", 4500f), ("highGainDb", 2f)),
            Mod("delay", ("timeMs", 380f), ("feedback", 0.34f), ("mix", 0.22f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.2f))),

        P("artist-evh-brown-sound", "Brown Sound Frankenstrat (Legacy)", DspToneCategory.Lead,
            "Variac'd dimed Plexi — thick spongy singing sustain with a Phase-90 swirl and warm tape slap.",
            Mod("phaser", ("rateHz", 0.6f), ("depth", 0.7f), ("feedback", 0.2f), ("stages", 4f), ("mix", 0.4f)),
            Mod("amp", ("gain", 0.78f), ("bass", 0.52f), ("mid", 0.62f), ("treble", 0.74f), ("presence", 0.66f), ("master", 0.5f), ("voicing", 6f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1000f), ("midGainDb", 2f), ("highFreq", 4000f), ("highGainDb", 4f)),
            Mod("tapedelay", ("timeMs", 250f), ("feedback", 0.25f), ("mix", 0.16f), ("wowFlutter", 0.3f), ("drive", 0.3f))),

        P("artist-gilmour-comfortably", "Comfortably Lead (Legacy)", DspToneCategory.Lead,
            "Compressor into a moderate Big Muff into a clean Hiwatt — vocal sustain and long analog delay.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 3f)),
            Mod("bigmuff", ("sustain", 0.55f), ("tone", 0.55f), ("volume", 0.6f)),
            Mod("amp", ("gain", 0.32f), ("bass", 0.55f), ("mid", 0.6f), ("treble", 0.52f), ("presence", 0.5f), ("master", 0.6f), ("voicing", 0f)),
            Mod("cab", ("model", 2f)),
            Mod("delay", ("timeMs", 480f), ("feedback", 0.38f), ("mix", 0.26f), ("toneHz", 3500f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.18f))),

        P("artist-tool-adam-jones", "Diezel Monolith (Legacy)", DspToneCategory.Lead,
            "Thick mid-forward high-gain with a cocked wah honk and a hypnotic half-second delay.",
            Mod("wah", ("mode", 0f), ("position", 0.55f), ("resonance", 0.55f), ("mix", 0.6f)),
            Mod("amp", ("gain", 0.7f), ("bass", 0.6f), ("mid", 0.7f), ("treble", 0.66f), ("presence", 0.6f), ("master", 0.48f), ("voicing", 16f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 95f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1000f), ("midGainDb", 2f), ("highFreq", 4500f), ("highGainDb", 2f)),
            Mod("delay", ("timeMs", 500f), ("feedback", 0.36f), ("mix", 0.24f), ("toneHz", 3500f)),
            Mod("reverb", ("roomSize", 0.4f), ("mix", 0.14f))),

        P("artist-ink-metalcore-lead", "Cinematic Metalcore Lead (Legacy)", DspToneCategory.Lead,
            "Smooth sustaining metalcore lead — mid-present high gain with lush delay and hall for the theatrics.",
            Mod("gate", ("thresholdDb", -48f), ("ratio", 10f), ("releaseMs", 70f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.22f), ("tone", 0.65f), ("level", 0.85f)),
            Mod("amp", ("gain", 0.72f), ("bass", 0.5f), ("mid", 0.62f), ("treble", 0.62f), ("presence", 0.55f), ("master", 0.48f), ("voicing", 15f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 110f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 900f), ("midGainDb", 3f), ("highFreq", 4500f), ("highGainDb", 2f)),
            Mod("delay", ("timeMs", 420f), ("feedback", 0.36f), ("mix", 0.24f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.55f), ("mix", 0.22f))),

        // ---- High gain ----
        P("artist-metallica-mop-rhythm", "Puppets Rhythm (Legacy)", DspToneCategory.HighGain,
            "Textbook scooped thrash — boosted JMP stack into Greenbacks, tight percussive chug, bone-dry.",
            Mod("gate", ("thresholdDb", -50f), ("ratio", 10f), ("releaseMs", 60f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.12f), ("tone", 0.62f), ("level", 0.85f)),
            Mod("amp", ("gain", 0.84f), ("bass", 0.6f), ("mid", 0.3f), ("treble", 0.75f), ("presence", 0.7f), ("master", 0.48f), ("voicing", 11f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("eq", ("lowFreq", 100f), ("lowGainDb", -1f), ("midFreq", 750f), ("midQ", 1.4f), ("midGainDb", -6f), ("highFreq", 4500f), ("highGainDb", 4f))),

        P("artist-slipknot-drop-metal", "Drop-B Brutalizer (Legacy)", DspToneCategory.HighGain,
            "Tight-gated screamer-boosted rectifier built to keep drop-tuned palm mutes articulate.",
            Mod("gate", ("thresholdDb", -46f), ("ratio", 12f), ("attackMs", 1f), ("releaseMs", 60f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.18f), ("tone", 0.6f), ("level", 0.85f)),
            Mod("amp", ("gain", 0.75f), ("bass", 0.58f), ("mid", 0.5f), ("treble", 0.62f), ("presence", 0.55f), ("master", 0.46f), ("voicing", 4f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 115f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1200f), ("midQ", 1f), ("midGainDb", 3f), ("highFreq", 4500f), ("highGainDb", 2f))),

        P("artist-ink-metalcore-rhythm", "Symphonic Metalcore Chug (Legacy)", DspToneCategory.HighGain,
            "Modern digital metalcore rhythm — tight scooped chug, firm low end, crisp presence, hard gate.",
            Mod("gate", ("thresholdDb", -46f), ("ratio", 12f), ("releaseMs", 60f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.18f), ("tone", 0.65f), ("level", 0.85f)),
            Mod("amp", ("gain", 0.72f), ("bass", 0.55f), ("mid", 0.45f), ("treble", 0.64f), ("presence", 0.58f), ("master", 0.46f), ("voicing", 15f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 120f), ("highCutHz", 9000f)),
            Mod("eq", ("lowFreq", 110f), ("lowGainDb", -2f), ("midFreq", 700f), ("midQ", 1.3f), ("midGainDb", -3f), ("highFreq", 4500f), ("highGainDb", 3f))),

        P("genre-djent-tight-boost", "Djent Tight Boost (Legacy)", DspToneCategory.HighGain,
            "Ruthless screamer-boosted djent — surgically tight lows, defined scoop, crisp staccato attack.",
            Mod("gate", ("thresholdDb", -44f), ("ratio", 16f), ("attackMs", 0.5f), ("holdMs", 6f), ("releaseMs", 50f)),
            Mod("overdrive", ("model", 1f), ("drive", 0.1f), ("tone", 0.7f), ("level", 0.9f)),
            Mod("amp", ("gain", 0.7f), ("bass", 0.48f), ("mid", 0.45f), ("treble", 0.62f), ("presence", 0.55f), ("master", 0.46f), ("voicing", 15f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 130f), ("highCutHz", 9000f)),
            Mod("eq", ("lowFreq", 120f), ("lowGainDb", -2f), ("midFreq", 620f), ("midQ", 1.6f), ("midGainDb", -4f), ("highFreq", 4500f), ("highGainDb", 3f))),

        // ---- Ambient / Experimental ----
        P("genre-shoegaze-wall", "Shoegaze Wall (Legacy)", DspToneCategory.Ambient,
            "A Big-Muff wall drowned in shimmer reverb and long modulated delay — hazy, cavernous, blurred.",
            Mod("bigmuff", ("sustain", 0.7f), ("tone", 0.5f), ("volume", 0.6f)),
            Mod("amp", ("gain", 0.4f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.55f), ("presence", 0.4f), ("master", 0.5f), ("voicing", 1f)),
            Mod("cab", ("model", 2f)),
            Mod("chorus", ("rateHz", 0.3f), ("depthMs", 8f), ("mix", 0.4f), ("voices", 3f)),
            Mod("delay", ("timeMs", 520f), ("feedback", 0.5f), ("mix", 0.32f), ("pingPong", 1f)),
            Mod("shimmer", ("decay", 0.8f), ("shimmer", 0.6f), ("damping", 0.4f), ("mix", 0.4f))),

        // ============ NAM CAPTURE-BACKED (bundled MIT .nam files) =============
        // These reference the app's bundled neural-amp captures via the namamp
        // module's modelPath (the file NAME; resolved to an absolute library path
        // at apply time). They stay dormant until the native namamp module ships
        // (Wave 3 / ABI v22) — DcRigApplier degrades gracefully if the type is
        // absent. Loudness centered via inputDb/outputDb (see the namamp case in
        // DcRigLoudnessNormalizer). Provenance: DotCharter.UI/NamCaptures/ATTRIBUTION.md.

        P("nam-deluxe-clean", "Deluxe Reverb (Capture)", DspToneCategory.Clean,
            "A neural capture of a blackface Deluxe Reverb — glassy compressed clean with a soft room.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3f), ("makeupDb", 3f)),
            NamAmp("fender-deluxe-reverb-clean.nam", ("inputDb", 0f), ("outputDb", 4f)),
            Mod("cab", ("model", 0f), ("highCutHz", 10000f)),
            Mod("reverb", ("roomSize", 0.4f), ("damping", 0.5f), ("mix", 0.18f))),

        P("nam-dingwall-bass", "Dingwall DI Bass (Capture)", DspToneCategory.Bass,
            "A neural capture of a modern bass DI — clean, tight and hi-fi with a scooped studio voicing.",
            Mod("compressor", ("thresholdDb", -20f), ("ratio", 4f), ("attackMs", 15f), ("releaseMs", 120f), ("makeupDb", 4f)),
            NamAmp("dingwall-bass-di.nam", ("inputDb", 0f), ("outputDb", 2f)),
            Mod("eq", ("lowFreq", 80f), ("lowGainDb", 3f), ("midFreq", 600f), ("midGainDb", -3f), ("highFreq", 3000f), ("highGainDb", 2f))),

        P("nam-rhythm-crunch", "Rhythm Crunch (Capture)", DspToneCategory.Crunch,
            "A neural capture of a driven rhythm amp — mid-forward crunch that sits in a busy mix.",
            NamAmp("nam-rhythm-crunch.nam", ("inputDb", 6f), ("outputDb", 0f)),
            Mod("cab", ("model", 2f)),
            Mod("eq", ("midFreq", 800f), ("midGainDb", 2f))),

        P("nam-combo-breakup", "Combo Breakup (Capture)", DspToneCategory.Crunch,
            "A neural capture of a combo on the edge of breakup — touch-sensitive grit with a room tail.",
            NamAmp("boss-wn-feather.nam", ("inputDb", 4f), ("outputDb", 0f)),
            Mod("cab", ("model", 1f)),
            Mod("reverb", ("roomSize", 0.35f), ("mix", 0.14f))),

        // ================= VOCAL SYNTH (miku / mikustomp) =====================
        // The two singing-voice stomps. `mikustomp` (Wave 6) is the faithful KORG
        // recreation — EpR excitation into a Klatt cascade, AEM-style head/joint
        // articulation — and defaults to mix = 1 (WET ONLY, like the hardware:
        // the pedal replaces the guitar, it doesn't blend). `miku` (v1) is the
        // older formant stomp, kept for contrast.
        //
        // AUTHORING NOTES (they behave unlike any other module):
        //   * The synth voice is what you hear — a mix < 1 is the ONLY way the
        //     guitar stays audible, so a "duet" rig must set mix explicitly.
        //   * Both modules track a MONOPHONIC pitch (E1–E6). Put a `gate` and a
        //     `compressor` in front: a clean, even, single-note signal is what
        //     makes the pitch/onset tracker fire one syllable per pick.
        //   * The loudness estimator treats a vocal module as level-NEUTRAL (its
        //     output level is the synth's, not a function of the input), so a
        //     preset with no amp centres itself on the −12 dBFS target with the
        //     compressor makeup + the `eq` outputTrimDb — never with `mix`.
        //   * Lyrics ride the string params (`phrases` / `phrase2` / `phrase3`);
        //     whitespace/comma/hyphen-split syllables, 64 max. A Phrase pattern
        //     (6/7/8) only sings when its slot is non-empty.
        //   * mikustomp pattern roster (v24 labels): 0 Ahh, 1 Lahh, 2 Looh,
        //     3 Pahh, 4 Nyan, 5 Scat, 6–8 Phrase 1–3, 9–10 Random 1–2.

        P("miku-stomp-kawaii", "Miku Stomp Kawaii", DspToneCategory.Experimental,
            "Wet-only kawaii vocal lead — every note sung as a bright 'nyan' with a fast, wide vibrato.",
            Mod("gate", ("thresholdDb", -50f), ("ratio", 8f), ("attackMs", 1f), ("releaseMs", 80f)),
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("attackMs", 8f), ("releaseMs", 100f), ("makeupDb", 4f)),
            Mod("mikustomp", ("mix", 1f), ("character", 0.85f), ("breath", 0.28f), ("attackMs", 8f),
                ("portamentoMs", 35f), ("vibrato", 0.8f), ("vibratoRateHz", 6.4f), ("octave", 1f), ("pattern", 4f)),
            Mod("eq", ("midFreq", 1800f), ("midQ", 1f), ("midGainDb", 2f), ("highFreq", 6000f), ("highGainDb", 2f), ("outputTrimDb", 2f)),
            Mod("delay", ("timeMs", 300f), ("feedback", 0.25f), ("mix", 0.18f), ("toneHz", 5000f)),
            Mod("reverb", ("roomSize", 0.5f), ("damping", 0.4f), ("mix", 0.22f))),

        P("miku-stomp-duet", "Miku Stomp Duet (Legacy)", DspToneCategory.Experimental,
            "Half guitar, half voice — a clean combo under a soft 'lahh' choir, widened by chorus and echo.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3f), ("makeupDb", 3f)),
            Mod("amp", ("gain", 0.25f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.55f), ("presence", 0.4f), ("voicing", 0f), ("master", 0.75f)),
            Mod("cab", ("model", 1f)),
            // The whole point of this rig: mikustomp is WET-ONLY by default, so the
            // guitar only survives because mix is pinned at 0.5.
            Mod("mikustomp", ("mix", 0.5f), ("character", 0.6f), ("breath", 0.35f), ("attackMs", 14f),
                ("portamentoMs", 70f), ("vibrato", 0.5f), ("vibratoRateHz", 5.4f), ("octave", 1f), ("pattern", 1f)),
            Mod("chorus", ("rateHz", 0.5f), ("depthMs", 5f), ("mix", 0.3f), ("voices", 2f)),
            Mod("delay", ("timeMs", 380f), ("feedback", 0.3f), ("mix", 0.2f), ("toneHz", 4500f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.2f))),

        P("miku-stomp-hatsune-phrase", "Miku Stomp Phrase Singer", DspToneCategory.Experimental,
            "Custom-lyric showcase: the pedal sings 'ha-tsu-ne mi-ku' one syllable per pick (Phrase 1; two more slots loaded).",
            Mod("gate", ("thresholdDb", -48f), ("ratio", 10f), ("releaseMs", 70f)),
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 4f)),
            ModS("mikustomp",
                [("phrases", "ha tsu ne mi ku"), ("phrase2", "a ri ga to u"), ("phrase3", "sa yo na ra")],
                ("mix", 1f), ("character", 0.7f), ("breath", 0.35f), ("attackMs", 10f),
                ("portamentoMs", 50f), ("vibrato", 0.5f), ("vibratoRateHz", 5.8f), ("octave", 1f), ("pattern", 6f)),
            Mod("eq", ("midFreq", 1500f), ("midGainDb", 2f), ("highFreq", 6000f), ("highGainDb", 2f), ("outputTrimDb", 2f)),
            Mod("delay", ("timeMs", 340f), ("feedback", 0.26f), ("mix", 0.18f)),
            Mod("reverb", ("roomSize", 0.45f), ("mix", 0.2f))),

        P("miku-stomp-android", "Miku Stomp Dark Android", DspToneCategory.Experimental,
            "Cold robotic vocaloid — dark character, no vibrato, zero portamento and a hard attack, swept by a slow flanger.",
            Mod("gate", ("thresholdDb", -46f), ("ratio", 12f), ("attackMs", 0.5f), ("releaseMs", 55f)),
            Mod("mikustomp", ("mix", 1f), ("character", 0.06f), ("breath", 0.1f), ("attackMs", 2f),
                ("portamentoMs", 0f), ("vibrato", 0f), ("vibratoRateHz", 3f), ("octave", 0f), ("pattern", 3f)),
            Mod("eq", ("lowFreq", 120f), ("lowGainDb", -2f), ("midFreq", 900f), ("midQ", 1.5f), ("midGainDb", 3f),
                ("highFreq", 5000f), ("highGainDb", -2f), ("outputTrimDb", 6f)),
            Mod("flanger", ("rateHz", 0.15f), ("depthMs", 3f), ("feedback", 0.5f), ("mix", 0.35f)),
            Mod("delay", ("timeMs", 250f), ("feedback", 0.35f), ("mix", 0.22f), ("toneHz", 3000f))),

        P("miku-stomp-scat", "Miku Stomp Scat Machine (Legacy)", DspToneCategory.Experimental,
            "Rhythmic scat syllables riding a snappy clean amp — the guitar keeps the pick attack, the voice does the phrasing.",
            Mod("compressor", ("thresholdDb", -24f), ("ratio", 5f), ("attackMs", 5f), ("releaseMs", 80f), ("makeupDb", 3f)),
            Mod("amp", ("gain", 0.3f), ("bass", 0.5f), ("mid", 0.55f), ("treble", 0.6f), ("presence", 0.45f), ("voicing", 0f), ("master", 0.7f)),
            Mod("cab", ("model", 0f)),
            Mod("mikustomp", ("mix", 0.75f), ("character", 0.55f), ("breath", 0.45f), ("attackMs", 4f),
                ("portamentoMs", 8f), ("vibrato", 0.2f), ("vibratoRateHz", 5f), ("octave", 1f), ("pattern", 5f)),
            Mod("delay", ("timeMs", 260f), ("feedback", 0.3f), ("mix", 0.22f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.4f), ("mix", 0.16f))),

        P("miku-lead-vocal", "Miku Lead Vocal (v1)", DspToneCategory.Experimental,
            "The original formant vocal stomp for contrast — a breathy synthetic lead voice singing its own syllable table.",
            Mod("gate", ("thresholdDb", -48f), ("ratio", 10f), ("releaseMs", 80f)),
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 4f)),
            ModS("miku", [("phrases", "mi ku mi ku ha tsu ne")],
                ("mix", 0.85f), ("character", 0.6f), ("octave", 1f), ("pattern", 2f)),
            Mod("eq", ("midFreq", 1600f), ("midGainDb", 2f), ("highFreq", 6500f), ("highGainDb", 3f), ("outputTrimDb", 2f)),
            Mod("delay", ("timeMs", 420f), ("feedback", 0.3f), ("mix", 0.2f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.55f), ("mix", 0.24f))),

        // ================= 0.9 GEAR WAVE SHOWCASE (43 new modules) ============
        // The wave took the native catalogue from 57 modules to 100, and a
        // module reachable ONLY by building a board by hand is a module most
        // players never meet. One or two rigs per drawer, each built around the
        // new gear rather than merely containing it, so the drawer's character
        // is what you hear:
        //
        //   dynamics  optocomp · multiband · limiter · transient
        //   drive     rat · ds1 · hm2 · bassod · tubepre · octfuzz · tapesat
        //   amp/cab   bassamp · acpre · poweramp · micpos
        //   filter    svf · vocoder · talkbox · comb · tilt
        //   mod       tzflanger · autopan · ensemble · barberpole
        //   pitch     twelvestring · basssynth · detune
        //   delay     multitap · bbddelay · duckdelay
        //   reverb    hallreverb · gatedreverb · reversereverb
        //   utility   volume · looper · midside
        //
        // AUTHORING NOTES for this block:
        //   * `micpos` goes AFTER `cab`, never before it — it models moving a
        //     mic in front of a speaker, not a speaker (gear-ampcab.md).
        //   * `vocoder` at carrier = Sidechain needs a sidechain SOURCE the
        //     preset cannot carry, and with none available the engine falls back
        //     to the module's own input. These presets therefore pick an
        //     INTERNAL carrier (saw/pulse), which is self-contained and audible
        //     the moment the preset loads.
        //   * `pitchcorrect` and `basssynth` track a MONOPHONIC pitch, so they
        //     get a gate and a compressor in front for the same reason the miku
        //     rigs do.
        //   * `looper`'s two rings are allocated for the 60 s worst case at
        //     prepare() — 43 MiB at 48 kHz — so the showcase caps `maxLoopS`
        //     well under the maximum rather than shipping the worst case as a
        //     default (gear-utility.md).
        //   * Loudness: the same ±6 dB band as everything above. A rig with no
        //     amp centres itself on the eq's outputTrimDb or the module's own
        //     output level — never by opening up a drive knob.

        P("gear-rodent-rhythm", "Rodent Rhythm", DspToneCategory.Crunch,
            "A rodent distortion straight into a valve power stage and a close-miked 4x12 — no preamp, all pedal.",
            Mod("gate", ("thresholdDb", -52f), ("ratio", 8f), ("releaseMs", 90f)),
            Mod("rat", ("distortion", 0.55f), ("filter", 0.45f), ("volume", 0.62f), ("clip", 0f)),
            Mod("poweramp", ("tubes", 2f), ("drive", 0.45f), ("sag", 0.35f), ("presence", 0.55f), ("resonance", 0.55f), ("level", 0.6f)),
            Mod("cab", ("model", 2f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("micpos", ("mic", 0f), ("position", 0.35f), ("distanceCm", 4f), ("level", 0.7f))),

        P("gear-chainsaw", "Chainsaw", DspToneCategory.HighGain,
            "Both colour knobs pinned on a chainsaw distortion, then a sagging power stage and a ribbon off-axis.",
            Mod("gate", ("thresholdDb", -44f), ("ratio", 14f), ("attackMs", 0.5f), ("releaseMs", 55f)),
            Mod("hm2", ("dist", 0.8f), ("lowDb", 12f), ("highDb", 12f), ("level", 0.5f)),
            Mod("poweramp", ("tubes", 3f), ("drive", 0.55f), ("sag", 0.5f), ("presence", 0.6f), ("resonance", 0.6f), ("level", 0.55f)),
            Mod("cab", ("model", 2f), ("lowCutHz", 110f), ("highCutHz", 8000f)),
            Mod("micpos", ("mic", 1f), ("position", 0.8f), ("distanceCm", 8f), ("level", 0.7f))),

        P("gear-optical-clean", "Optical Clean", DspToneCategory.Clean,
            "An optical compressor breathing under a clean amp, finished with a chamber the size of a live room.",
            Mod("optocomp", ("peakReduction", 0.55f), ("gainDb", 6f), ("limit", 0f), ("emphasis", 0.35f)),
            Mod("amp", ("gain", 0.22f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.6f), ("presence", 0.35f), ("master", 0.62f), ("voicing", 0f)),
            Mod("cab", ("model", 0f), ("highCutHz", 10000f)),
            Mod("micpos", ("mic", 3f), ("position", 0.25f), ("distanceCm", 12f), ("room", 0.15f), ("level", 0.7f)),
            Mod("hallreverb", ("space", 1f), ("decayS", 1.6f), ("predelayMs", 18f), ("dampingKHz", 7f), ("erLevel", 0.55f), ("diffusion", 0.7f), ("mix", 0.22f))),

        P("gear-bass-di", "Bass DI", DspToneCategory.Bass,
            "A blended bass overdrive into a valve head and a close 4x10 — the DI-plus-amp sound, in one chain.",
            Mod("bassod", ("drive", 0.45f), ("blend", 0.45f), ("bass", 3f), ("treble", 2f), ("level", 0.6f)),
            Mod("bassamp", ("model", 0f), ("gain", 0.35f), ("bass", 3f), ("lowMid", -2f), ("hiMid", 1f), ("treble", 2f), ("drive", 0.25f), ("compress", 0.4f), ("master", 0.6f)),
            Mod("micpos", ("mic", 2f), ("position", 0.2f), ("distanceCm", 6f), ("level", 0.7f))),

        P("gear-bass-synth", "Bass Synth", DspToneCategory.Bass,
            "A tracked square-wave sub under the dry bass, with a multiband compressor keeping the three bands in their lanes.",
            Mod("gate", ("thresholdDb", -50f), ("ratio", 10f), ("releaseMs", 80f)),
            Mod("compressor", ("thresholdDb", -20f), ("ratio", 4f), ("attackMs", 12f), ("releaseMs", 120f), ("makeupDb", 3f)),
            Mod("basssynth", ("waveform", 0f), ("octave", 1f), ("cutoffHz", 900f), ("resonance", 0.45f), ("envAmount", 0.6f), ("decayMs", 320f), ("glideMs", 25f), ("dryMix", 0.45f), ("synthMix", 0.55f)),
            Mod("multiband", ("lowXoverHz", 140f), ("highXoverHz", 2200f), ("lowThresholdDb", -22f), ("lowRatio", 4f), ("lowGainDb", 1f), ("midThresholdDb", -20f), ("midRatio", 3f), ("highThresholdDb", -18f), ("highRatio", 2.5f), ("attackMs", 8f), ("releaseMs", 140f))),

        P("gear-twelve-string-jangle", "12-String Jangle", DspToneCategory.Acoustic,
            "Octave and unison courses under an acoustic preamp, widened by a string ensemble — one pick, twelve strings.",
            Mod("twelvestring", ("octaveLevel", 0.62f), ("unisonLevel", 0.6f), ("detuneCents", 9f), ("delayMs", 7f), ("brightness", 0.6f), ("mix", 0.6f)),
            Mod("acpre", ("notchHz", 120f), ("notchDepthDb", 0f), ("bass", 1f), ("mid", -2f), ("treble", 3f), ("presence", 0.4f), ("piezoFix", 0.35f), ("level", 0.72f)),
            Mod("ensemble", ("rateHz", 0.55f), ("fastRateHz", 5.5f), ("depth", 0.5f), ("voices", 3f), ("tone", 0.55f), ("mix", 0.4f)),
            Mod("hallreverb", ("space", 0f), ("decayS", 1.2f), ("predelayMs", 12f), ("dampingKHz", 8f), ("erLevel", 0.6f), ("mix", 0.18f))),

        P("gear-vocoder-lead", "Vocoder Lead", DspToneCategory.Experimental,
            "An envelope-swept filter feeding a vocoder on its own pulse carrier — the guitar shapes a synth that is already singing.",
            Mod("gate", ("thresholdDb", -48f), ("ratio", 10f), ("releaseMs", 70f)),
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 4f)),
            Mod("svf", ("mode", 1f), ("cutoffHz", 900f), ("resonance", 0.45f), ("drive", 0.2f), ("source", 2f), ("envSens", 0.6f), ("envAttackMs", 12f), ("envReleaseMs", 220f), ("mix", 0.8f)),
            // carrier 2 = Pulse: an INTERNAL carrier, so the rig speaks with no
            // sidechain source wired (a preset cannot carry one).
            Mod("vocoder", ("bands", 20f), ("carrier", 2f), ("pitchHz", 110f), ("attackMs", 6f), ("releaseMs", 45f), ("formantShift", 2f), ("emphasis", 0.65f), ("mix", 0.85f)),
            Mod("eq", ("midFreq", 1600f), ("midGainDb", 2f), ("highFreq", 6000f), ("highGainDb", 2f), ("outputTrimDb", 3f)),
            Mod("hallreverb", ("space", 2f), ("decayS", 2.2f), ("dampingKHz", 6f), ("mix", 0.22f))),

        P("gear-talk-box-funk", "Talk Box Funk", DspToneCategory.Experimental,
            "A picking-hand-swept talk box thrown across the stereo field by an auto-pan — vowels on the beat.",
            Mod("compressor", ("thresholdDb", -26f), ("ratio", 6f), ("attackMs", 5f), ("releaseMs", 90f), ("makeupDb", 5f)),
            Mod("talkbox", ("vowel", 0.45f), ("sweep", 1f), ("envSens", 0.65f), ("resonance", 0.65f), ("drive", 0.4f), ("mix", 0.85f)),
            Mod("autopan", ("rateHz", 2.2f), ("depth", 0.7f), ("shape", 0f), ("phaseDeg", 90f), ("law", 0f)),
            Mod("eq", ("midFreq", 1200f), ("midGainDb", 2f), ("outputTrimDb", 3f)),
            Mod("delay", ("timeMs", 250f), ("feedback", 0.24f), ("mix", 0.16f))),

        P("gear-dotted-multitap", "Dotted Multitap", DspToneCategory.Ambient,
            "Four dotted taps walking across the stereo field under a clean amp — a rhythm part that plays itself.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3f), ("makeupDb", 3f)),
            Mod("amp", ("gain", 0.26f), ("bass", 0.5f), ("mid", 0.48f), ("treble", 0.6f), ("presence", 0.4f), ("master", 0.62f), ("voicing", 0f)),
            Mod("cab", ("model", 1f)),
            Mod("multitap", ("taps", 4f), ("timeMs", 375f), ("spread", 1f), ("decay", 0.62f), ("pan", 0.8f), ("feedback", 0.25f), ("tone", 0.45f), ("mix", 0.38f)),
            Mod("hallreverb", ("space", 2f), ("decayS", 2.6f), ("predelayMs", 24f), ("dampingKHz", 5f), ("mix", 0.2f))),

        P("gear-shoegaze-reverse", "Reverse Shoegaze", DspToneCategory.Ambient,
            "A thru-zero flanger through its own zero crossing, swelling backwards out of a reverse reverb.",
            Mod("bigmuff", ("sustain", 0.62f), ("tone", 0.5f), ("volume", 0.55f)),
            Mod("amp", ("gain", 0.38f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.55f), ("presence", 0.4f), ("master", 0.5f), ("voicing", 1f)),
            Mod("cab", ("model", 2f)),
            Mod("tzflanger", ("rateHz", 0.18f), ("depthMs", 4.5f), ("manualMs", 4f), ("feedback", -0.45f), ("mix", 0.5f), ("shape", 0f)),
            Mod("reversereverb", ("decayS", 2.4f), ("windowMs", 1100f), ("predelayMs", 0f), ("dampingKHz", 7f), ("mix", 0.45f), ("swellShape", 0.75f))),

        P("gear-gated-snap", "Gated Snap", DspToneCategory.Experimental,
            "An eighties gated reverb slammed shut a quarter-second after every hit, with a transient shaper sharpening the attack.",
            Mod("transient", ("attackDb", 5f), ("sustainDb", -3f), ("sensitivity", 0.55f), ("mix", 1f)),
            Mod("ds1", ("dist", 0.45f), ("tone", 0.6f), ("level", 0.6f)),
            Mod("amp", ("gain", 0.35f), ("bass", 0.5f), ("mid", 0.55f), ("treble", 0.6f), ("presence", 0.5f), ("master", 0.52f), ("voicing", 1f)),
            Mod("cab", ("model", 1f)),
            Mod("gatedreverb", ("decayS", 4f), ("gateMs", 240f), ("thresholdDb", -34f), ("releaseMs", 18f), ("predelayMs", 12f), ("dampingKHz", 9f), ("mix", 0.55f))),

        P("gear-loop-station", "Loop Station", DspToneCategory.Experimental,
            "A volume pedal in front of a looper: swell a part in, park it, and play over your own take. The looper starts STOPPED — the transport is the footswitch.",
            Mod("optocomp", ("peakReduction", 0.4f), ("gainDb", 4f), ("emphasis", 0.3f)),
            Mod("amp", ("gain", 0.24f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.58f), ("presence", 0.35f), ("master", 0.62f), ("voicing", 0f)),
            Mod("cab", ("model", 0f)),
            // `position` 1 is toe-down: the pedal ships OPEN, so a preset load is
            // never silence. `minDb` −60 keeps heel-down a usable swell floor
            // rather than the module's −90 dB mute.
            Mod("volume", ("position", 1f), ("curve", 2f), ("minDb", -60f), ("maxDb", 0f), ("smoothMs", 30f)),
            Mod("looper", ("state", 0f), ("maxLoopS", 20f), ("loopLevel", 0.85f), ("overdubDecay", 0.9f), ("reverse", 0f), ("halfSpeed", 0f), ("mix", 0.9f)),
            Mod("hallreverb", ("space", 2f), ("decayS", 2f), ("mix", 0.18f))),

        P("gear-tube-di-warm", "Tube DI Warm", DspToneCategory.Clean,
            "A valve DI preamp doing all the work — a tilt tone stack, a little sag, and a mid/side widener instead of a cab.",
            Mod("tubepre", ("gain", 0.42f), ("sag", 0.35f), ("bright", 1f), ("tone", 0.55f), ("level", 0.5f), ("cabSim", 1f)),
            Mod("tilt", ("tilt", 2f), ("pivotHz", 900f), ("level", 0f)),
            Mod("midside", ("midDb", 0f), ("sideDb", 2.5f), ("width", 1.3f), ("monoBelowHz", 140f)),
            // gainDb is the limiter's INPUT gain, not makeup: leaving it at 0
            // keeps this rig's level the DI's own and the ceiling a safety net.
            Mod("limiter", ("ceilingDb", -1.5f), ("releaseMs", 90f), ("lookaheadMs", 1f), ("gainDb", 0f))),
    ];
}
