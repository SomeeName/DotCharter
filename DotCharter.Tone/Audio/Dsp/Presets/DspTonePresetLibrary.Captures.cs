namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
/// The capture-backed half of the factory catalogue: for every preset in
/// <see cref="DspTonePresetLibrary"/> that was built on the algorithmic
/// <c>amp</c> module, a twin that plays the SAME rig through a real neural
/// capture (<c>namamp</c> + a bundled <c>.nam</c>).
/// </summary>
/// <remarks>
/// <para>
///     <b>The twin keeps the name; the original becomes "… (Legacy)".</b> Ids
///     are untouched on the legacy side and prefixed <c>nam-</c> here, so
///     nothing that ever persisted a preset id resolves to a different tone
///     than it did before — the rename is presentational only.
/// </para>
/// <para>
///     <b>How a legacy amp becomes a capture face.</b> A capture is one amp at
///     one knob setting: its gain is baked in and cannot be recovered. So the
///     authored tone-stack intent carries across (<c>bass</c>/<c>mid</c>/
///     <c>treble</c> verbatim — both are roughly ±12 dB stacks; <c>master</c>
///     verbatim), <c>presence</c> is damped toward neutral because namamp's
///     presence and treble both act at 16 kHz and would otherwise stack, and
///     <c>gain</c> is REMAPPED: on the legacy amp it chose how hard the model
///     distorted, here it is only level into a capture that already distorts,
///     so the 0.20–0.85 legacy range folds into a ±3.5 dB trim around neutral.
///     Choosing a capture whose recorded gain matches the original voicing is
///     what actually carries the character — the knob cannot.
///     <b>Any param left at 0.5 is omitted on purpose</b>: neutral is a strict
///     native no-op (each band is skipped, not run at 0 dB), so an omitted
///     param is measurably free.
/// </para>
/// <para>
///     <b>A boost baked into the capture is never boosted twice.</b> Where the
///     chosen capture already includes its screamer (`…-boosted`, `…-ts9`,
///     `driftwood-nightmare-hm2`), the legacy rig's <c>overdrive</c>/
///     <c>distortion</c> module is DROPPED rather than replaced — the real rig
///     that was captured had one boost, not two. Everywhere else the dirt box
///     becomes its own capture in front of the amp capture, which is the one
///     pedal class NAM genuinely models (a dirt/boost/preamp circuit is
///     memoryless enough to capture; anything with an LFO or a clock — delay,
///     reverb, chorus, phaser, tremolo, pitch — is NOT, and those modules stay
///     algorithmic here by necessity, not by preference).
/// </para>
/// <para>
///     <b>The cab stays.</b> Every bundled amp capture is amp-only (the pack's
///     `-Cab-` variants were deliberately not chosen), so the legacy rig's
///     <c>cab</c> module is still what provides the speaker — dropping it is
///     what makes a NAM rig sound like a fizzing DI.
/// </para>
/// <para>
///     Every <c>modelPath</c> here is a BARE bundled file name resolved to an
///     absolute path at apply time by <c>DspTonePresetService</c>'s
///     <c>namCaptureDirectory</c> hook. The files, their licences and their
///     provenance are in <c>DotCharter.UI/NamCaptures/ATTRIBUTION.md</c>, and
///     <c>CaptureBackedPresetTests</c> fails the build if a preset here names a
///     capture that is not bundled.
/// </para>
/// </remarks>
public static partial class DspTonePresetLibrary
{
    /// <summary>The capture-backed twins, in the same category order as
    /// <see cref="Build"/>.</summary>
    private static IReadOnlyList<DspTonePreset> BuildCaptureBacked() =>
    [
        P("nam-clean-glass", "Glass Clean", DspToneCategory.Clean,
            "Sparkling blackface-style clean with airy top and a whisper of spring — built on a neural capture of a Twin Reverb Normal (Bright).",
            Mod("compressor", ("thresholdDb", -20f), ("ratio", 3f), ("attackMs", 15f), ("releaseMs", 120f), ("makeupDb", 3f)),
            NamAmp("twinverb-norm-bright.nam", ("gain", 0.42f), ("mid", 0.45f), ("treble", 0.65f), ("presence", 0.44f), ("master", 0.6f), ("outputDb", 2.7f)),
            Mod("cab", ("model", 0f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("eq", ("highFreq", 6000f), ("highGainDb", 3f)),
            Mod("reverb", ("roomSize", 0.4f), ("damping", 0.5f), ("mix", 0.18f))),

        P("nam-clean-chime-shimmer", "Chime & Shimmer", DspToneCategory.Clean,
            "Jangly chorused clean for arpeggios and shimmering chords — built on a neural capture of an AC15 Jangle.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3f), ("makeupDb", 2f)),
            NamAmp("voxac15-jangle.nam", ("gain", 0.44f), ("treble", 0.6f), ("master", 0.6f), ("outputDb", 3.0f)),
            Mod("cab", ("model", 1f)),
            Mod("chorus", ("rateHz", 0.6f), ("depthMs", 5f), ("mix", 0.35f), ("voices", 2f)),
            Mod("reverb", ("roomSize", 0.45f), ("mix", 0.25f))),

        P("nam-clean-blackface-sparkle", "Blackface Sparkle", DspToneCategory.Clean,
            "Classic American combo clean with tight lows and a bright bloom — built on a neural capture of a Twin Reverb Vibrato (Bright).",
            NamAmp("twinverb-vibrato-bright.nam", ("gain", 0.45f), ("bass", 0.55f), ("mid", 0.4f), ("treble", 0.6f), ("presence", 0.41f), ("master", 0.6f), ("outputDb", 4.6f)),
            Mod("cab", ("model", 0f)),
            Mod("reverb", ("roomSize", 0.45f), ("damping", 0.45f), ("mix", 0.2f))),

        P("nam-clean-jazz-warm", "Jazz Box Warm", DspToneCategory.Clean,
            "Round neck-pickup jazz tone with the highs rolled off and mids full — built on a neural capture of a Super 59 MkII Ch1 Normal.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 2.5f), ("makeupDb", 2f)),
            NamAmp("magnatone-ch1-norm.nam", ("gain", 0.42f), ("bass", 0.6f), ("mid", 0.55f), ("treble", 0.35f), ("presence", 0.32f), ("master", 0.6f), ("outputDb", 3.7f)),
            Mod("cab", ("model", 2f), ("highCutHz", 5000f)),
            Mod("eq", ("lowGainDb", 2f), ("highGainDb", -2f))),

        P("nam-clean-funk-snap", "Funk Snap", DspToneCategory.Clean,
            "Heavily compressed clean with percussive snap for rhythm funk — built on a neural capture of a Twin Reverb Normal (Bright).",
            Mod("compressor", ("thresholdDb", -28f), ("ratio", 6f), ("attackMs", 5f), ("releaseMs", 80f), ("makeupDb", 5f), ("kneeDb", 3f)),
            NamAmp("twinverb-norm-bright.nam", ("gain", 0.43f), ("treble", 0.6f), ("master", 0.6f)),
            Mod("cab", ("model", 0f)),
            Mod("eq", ("midFreq", 1200f), ("midGainDb", 2f))),

        P("nam-clean-country-twang", "Country Twang", DspToneCategory.Clean,
            "Bright chicken-pickin' clean with a short slapback echo — built on a neural capture of a Super 59 MkII Ch1 Bright.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 4f)),
            NamAmp("magnatone-ch1-bright.nam", ("gain", 0.44f), ("treble", 0.7f), ("presence", 0.47f), ("master", 0.6f), ("outputDb", 1.0f)),
            Mod("cab", ("model", 0f), ("highCutHz", 10000f)),
            Mod("delay", ("timeMs", 110f), ("feedback", 0.15f), ("mix", 0.18f), ("toneHz", 5000f))),

        P("nam-clean-deep-chorus", "Deep Chorus Clean", DspToneCategory.Clean,
            "Lush, wide chorus over a soft clean for dreamy backing parts — built on a neural capture of a JCM2000 Clean.",
            NamAmp("jcm2000-clean.nam", ("gain", 0.42f), ("treble", 0.55f), ("master", 0.72f), ("outputDb", 2.5f)),
            Mod("cab", ("model", 1f)),
            Mod("chorus", ("rateHz", 0.4f), ("depthMs", 8f), ("mix", 0.5f), ("voices", 3f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.22f))),

        // ========================= CRUNCH =========================

        P("nam-crunch-vox-top-boost", "Vox Top Boost Crunch", DspToneCategory.Crunch,
            "Chimey British combo on the edge of breakup for shimmering rhythm — built on a neural capture of an AC15 Top Boost.",
            NamAmp("od808.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("voxac15-topboost.nam", ("gain", 0.53f), ("bass", 0.45f), ("mid", 0.55f), ("treble", 0.65f), ("master", 0.48f), ("outputDb", 5.6f)),
            Mod("cab", ("model", 1f)),
            Mod("eq", ("highGainDb", 2f))),

        P("nam-crunch-plexi", "Plexi Crunch", DspToneCategory.Crunch,
            "Cranked British stack crunch with a mid-forward growl — built on a neural capture of a JCM900 Dual Reverb Ch A.",
            NamAmp("jcm900-crunch.nam", ("gain", 0.54f), ("mid", 0.6f), ("treble", 0.6f), ("master", 0.52f), ("outputDb", 3.9f)),
            Mod("cab", ("model", 2f)),
            Mod("eq", ("midFreq", 800f), ("midGainDb", 2f))),

        P("nam-crunch-tweed-breakup", "Tweed Breakup", DspToneCategory.Crunch,
            "Warm tweed on the verge of dirt — dynamic and touch-sensitive — built on a neural capture of a Dirty Shirley Mini Crunch.",
            NamAmp("sd1-super-overdrive.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("dirtyshirley-crunch.nam", ("gain", 0.51f), ("bass", 0.55f), ("mid", 0.55f), ("presence", 0.38f), ("master", 0.48f), ("outputDb", 6.3f)),
            Mod("cab", ("model", 0f))),

        P("nam-crunch-blues-driver", "Blues Driver", DspToneCategory.Crunch,
            "Transparent overdrive pushing a crunch amp for gritty blues rhythm — built on a neural capture of a Super 59 MkII Pushed.",
            NamAmp("sd-805-overdrive.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("magnatone-pushed.nam", ("mid", 0.6f), ("treble", 0.55f), ("master", 0.48f), ("outputDb", 6.7f)),
            Mod("cab", ("model", 2f))),

        P("nam-crunch-ac-grind", "AC Grind", DspToneCategory.Crunch,
            "Bright top-boost grind with singing upper mids — built on a neural capture of an AC15 Top Boost.",
            NamAmp("voxac15-topboost.nam", ("gain", 0.55f), ("bass", 0.45f), ("mid", 0.55f), ("treble", 0.6f), ("presence", 0.53f), ("master", 0.48f), ("outputDb", 4.9f)),
            Mod("cab", ("model", 1f)),
            Mod("eq", ("highFreq", 5000f), ("highGainDb", 2f))),

        P("nam-crunch-classic-rock", "Classic Rock Rhythm", DspToneCategory.Crunch,
            "'70s midrange crunch with a touch of room for open chords — built on a neural capture of a JCM2000 Crunch.",
            NamAmp("ts9.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("jcm2000-crunch.nam", ("gain", 0.53f), ("bass", 0.55f), ("mid", 0.6f), ("outputDb", 4.9f)),
            Mod("cab", ("model", 2f)),
            Mod("reverb", ("roomSize", 0.35f), ("mix", 0.12f))),

        P("nam-crunch-boosted-marshall", "Boosted Marshall", DspToneCategory.Crunch,
            "Tube-screamer boost tightening a British stack for punchy rhythm — built on a neural capture of a JCM2000 Crunch.",
            NamAmp("ts9-driven.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("jcm2000-crunch.nam", ("gain", 0.54f), ("mid", 0.65f), ("treble", 0.55f), ("master", 0.48f), ("outputDb", 5.3f)),
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

        P("nam-metal-modern-djent", "Modern Djent", DspToneCategory.HighGain,
            "Ultra-tight scooped chug: screamer boost into a modern 8-string amp, hard gate for staccato riffing — built on a neural capture of a Yerasov Djent (Boosted).",
            Mod("gate", ("thresholdDb", -45f), ("ratio", 12f), ("attackMs", 1f), ("holdMs", 8f), ("releaseMs", 80f)),
            NamAmp("yerasov-djent-boosted.nam", ("gain", 0.57f), ("bass", 0.45f), ("treble", 0.6f), ("presence", 0.53f), ("master", 0.48f), ("outputDb", 4.2f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 110f), ("highCutHz", 9000f)),
            Mod("eq", ("lowFreq", 100f), ("lowGainDb", -2f), ("midFreq", 700f), ("midQ", 1.4f), ("midGainDb", -3f), ("highFreq", 4500f), ("highGainDb", 2f))),

        P("nam-metal-scooped-thrash", "Scooped Thrash", DspToneCategory.HighGain,
            "Bay Area thrash — boosted JMP stack, heavily scooped mids, biting highs, fast gate — built on a neural capture of a Mark III Red (Boosted).",
            Mod("gate", ("thresholdDb", -50f), ("ratio", 10f), ("releaseMs", 60f)),
            NamAmp("mesa-iii-red-boosted.nam", ("gain", 0.61f), ("bass", 0.6f), ("mid", 0.3f), ("treble", 0.74f), ("presence", 0.61f), ("master", 0.48f), ("outputDb", 2.8f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 95f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 650f), ("midQ", 1.2f), ("midGainDb", -6f), ("highFreq", 4500f), ("highGainDb", 4f))),

        P("nam-metal-5150-rhythm", "5150 Rhythm", DspToneCategory.HighGain,
            "Saturated American high-gain rhythm — boosted lead channel, controlled low end — built on a neural capture of a 5150 Block Letter (Boosted).",
            Mod("gate", ("thresholdDb", -48f), ("ratio", 10f), ("releaseMs", 70f)),
            NamAmp("5150-blockletter-boosted.nam", ("gain", 0.58f), ("bass", 0.55f), ("mid", 0.45f), ("treble", 0.6f), ("master", 0.46f), ("outputDb", 4.6f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 100f), ("highCutHz", 8500f)),
            Mod("eq", ("midFreq", 700f), ("midGainDb", -2f), ("highFreq", 4500f), ("highGainDb", 2f))),

        // Doom deliberately keeps NO gate (the sustain/feedback IS the instrument);
        // the wall is the Gnash distortion model into a big British head — the
        // old maxed GENERIC pedal into a lead amp measured 33 % intermodulation
        // with a 615 Hz centroid; this keeps the sludge but finds the top again.

        P("nam-metal-doom-fuzz", "Doom Fuzz Wall", DspToneCategory.HighGain,
            "Thick, loose sludge fuzz — a snarling dirt box into a cranked British stack, massive lows, cavernous verb — built on a neural capture of a Rockerverb.",
            NamAmp("os2-overdrive-distortion.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("orange-rockerverb.nam", ("gain", 0.54f), ("bass", 0.7f), ("mid", 0.55f), ("treble", 0.55f), ("presence", 0.47f), ("master", 0.48f), ("outputDb", 5.3f)),
            Mod("cab", ("model", 2f), ("lowCutHz", 80f), ("highCutHz", 7000f)),
            Mod("eq", ("lowFreq", 90f), ("lowGainDb", 1f), ("midFreq", 800f), ("midGainDb", -2f), ("highFreq", 4000f), ("highGainDb", 2f)),
            Mod("reverb", ("roomSize", 0.6f), ("mix", 0.2f))),

        P("nam-metal-nu-chug", "Nu-Metal Chug", DspToneCategory.HighGain,
            "Detuned, scooped, low-heavy chug — boosted rectifier for down-tuned groove riffs — built on a neural capture of a Mark IV Lead.",
            Mod("gate", ("thresholdDb", -46f), ("ratio", 12f), ("releaseMs", 70f)),
            NamAmp("precision-drive.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("mesa-markiv.nam", ("gain", 0.58f), ("bass", 0.65f), ("mid", 0.4f), ("treble", 0.58f), ("master", 0.48f), ("outputDb", 3.8f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 95f), ("highCutHz", 8500f)),
            Mod("eq", ("lowFreq", 110f), ("lowGainDb", 1f), ("midFreq", 1200f), ("midQ", 1f), ("midGainDb", 3f), ("highFreq", 4500f), ("highGainDb", 2f))),

        // The buzzsaw IS a pedal tone: the Metal distortion model (2) slammed into
        // a moderately driven British amp — that is how the Swedish records were cut.

        P("nam-metal-death-buzzsaw", "Death Metal Buzzsaw", DspToneCategory.HighGain,
            "Scandinavian buzzsaw — a maxed metal pedal into a Greenback stack, tight gate — built on a neural capture of a Nightmare High Gain + HM-2.",
            Mod("gate", ("thresholdDb", -44f), ("ratio", 14f), ("releaseMs", 55f)),
            NamAmp("driftwood-nightmare-hm2.nam", ("gain", 0.54f), ("mid", 0.55f), ("treble", 0.72f), ("presence", 0.59f), ("master", 0.46f), ("outputDb", 6.0f)),
            Mod("cab", ("model", 2f), ("lowCutHz", 100f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1000f), ("midGainDb", 3f), ("highFreq", 5000f), ("highGainDb", 3f))),

        P("nam-metal-metalcore-rhythm", "Metalcore Rhythm", DspToneCategory.HighGain,
            "Modern metalcore rhythm — boosted PowerBall, tight lows, scooped mids, crisp attack — built on a neural capture of a Ceres Purple Channel.",
            Mod("gate", ("thresholdDb", -47f), ("ratio", 11f), ("releaseMs", 65f)),
            NamAmp("grind-mini-boost.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("ksr-ceres-purple.nam", ("gain", 0.57f), ("bass", 0.55f), ("mid", 0.45f), ("treble", 0.62f), ("presence", 0.53f), ("master", 0.48f), ("outputDb", 4.2f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 110f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 800f), ("midQ", 1.2f), ("midGainDb", -3f), ("highFreq", 4500f), ("highGainDb", 2f))),

        P("nam-metal-recto-crush", "Recto Crush", DspToneCategory.HighGain,
            "Big-bottom American rectifier — boosted, tightened, and pushed back into focus — built on a neural capture of a Mark IV Lead.",
            Mod("gate", ("thresholdDb", -48f), ("ratio", 10f), ("releaseMs", 75f)),
            NamAmp("ts9.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("mesa-markiv.nam", ("gain", 0.59f), ("bass", 0.62f), ("mid", 0.42f), ("treble", 0.6f), ("master", 0.46f), ("outputDb", 4.2f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 100f), ("highCutHz", 8500f)),
            Mod("eq", ("lowFreq", 100f), ("lowGainDb", -1f), ("midFreq", 1300f), ("midQ", 1f), ("midGainDb", 3f), ("highFreq", 4500f), ("highGainDb", 2f))),

        P("nam-metal-industrial-grind", "Industrial Grind", DspToneCategory.HighGain,
            "Cold, gated, surgically tight machine-metal with a subtle jet flange — built on a neural capture of an Ubermetal Insane.",
            Mod("gate", ("thresholdDb", -42f), ("ratio", 16f), ("attackMs", 0.5f), ("releaseMs", 50f)),
            NamAmp("m77-badass-od.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("ubermetal-insane.nam", ("gain", 0.57f), ("bass", 0.45f), ("mid", 0.48f), ("treble", 0.65f), ("presence", 0.56f), ("master", 0.48f), ("outputDb", 4.2f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 130f), ("highCutHz", 9500f)),
            Mod("eq", ("lowFreq", 120f), ("lowGainDb", -2f), ("midFreq", 900f), ("midGainDb", -2f), ("highFreq", 5000f), ("highGainDb", 2f)),
            Mod("flanger", ("rateHz", 0.2f), ("depthMs", 2f), ("feedback", 0.3f), ("mix", 0.2f))),

        // ========================= LEAD =========================
        // Same rebuild rules as the high-gain block: a boost in FRONT (low drive /
        // high level), a REAL amp model per style (17 Overdrive Special, 11 JMP-800,
        // 6 Brown Variac, 16 PowerBall …) at a sane gain, and a cab that isn't the
        // dark Vintage 4x12 by default (7 Greenback for classic, 8 V30 for modern).

        P("nam-lead-singing-sustain", "Singing Sustain Lead", DspToneCategory.Lead,
            "Smooth, violin-like sustain — hot-rodded lead amp with mid push, delay and hall — built on a neural capture of a 5152 Lead + TS9.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 3f)),
            NamAmp("5152-lead-ts9.nam", ("gain", 0.58f), ("bass", 0.48f), ("mid", 0.7f), ("treble", 0.72f), ("presence", 0.59f), ("master", 0.48f), ("outputDb", 0.8f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1000f), ("midGainDb", 4f), ("highFreq", 4000f), ("highGainDb", 4f)),
            Mod("delay", ("timeMs", 400f), ("feedback", 0.35f), ("mix", 0.25f), ("toneHz", 4000f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.22f))),

        P("nam-lead-brit-800", "Brit 800 Lead", DspToneCategory.Lead,
            "Boosted JMP-style lead with singing mids and a tasteful echo — built on a neural capture of a JCM2000 Lead 2 (Boosted).",
            NamAmp("jcm2000-lead-boosted.nam", ("gain", 0.59f), ("bass", 0.48f), ("mid", 0.65f), ("treble", 0.75f), ("presence", 0.61f), ("master", 0.48f), ("outputDb", 3.5f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1100f), ("midGainDb", 3f), ("highFreq", 4000f), ("highGainDb", 4f)),
            Mod("delay", ("timeMs", 350f), ("feedback", 0.3f), ("mix", 0.2f)),
            Mod("reverb", ("roomSize", 0.4f), ("mix", 0.15f))),

        P("nam-lead-liquid-legato", "Liquid Legato", DspToneCategory.Lead,
            "Fluid fusion lead — smooth boutique gain, ping-pong delay and deep reverb — built on a neural capture of a JSX Ultra (Boosted).",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3f), ("makeupDb", 2f)),
            NamAmp("jsx-ultra-boosted.nam", ("gain", 0.57f), ("mid", 0.7f), ("treble", 0.68f), ("presence", 0.56f), ("master", 0.48f), ("outputDb", 2.2f)),
            Mod("cab", ("model", 2f), ("lowCutHz", 85f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1200f), ("midGainDb", 3f), ("highFreq", 4000f), ("highGainDb", 4f)),
            Mod("delay", ("timeMs", 450f), ("feedback", 0.4f), ("mix", 0.3f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.6f), ("mix", 0.28f))),

        P("nam-lead-brown-sound", "Brown Sound Lead", DspToneCategory.Lead,
            "'80s hot-rodded brown-sound lead with tight lows and a slap of verb — built on a neural capture of a 1990 Lead.",
            NamAmp("klone-transparent-boost.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("bug1990-lead.nam", ("gain", 0.59f), ("bass", 0.52f), ("mid", 0.62f), ("treble", 0.74f), ("presence", 0.6f), ("master", 0.48f), ("outputDb", 3.5f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1000f), ("midGainDb", 2f), ("highFreq", 4000f), ("highGainDb", 4f)),
            Mod("delay", ("timeMs", 300f), ("feedback", 0.3f), ("mix", 0.18f)),
            Mod("reverb", ("roomSize", 0.4f), ("mix", 0.15f))),

        P("nam-lead-soulful-blues", "Soulful Blues Lead", DspToneCategory.Lead,
            "Expressive, vocal blues lead — mid-rich crunch with room and echo — built on a neural capture of a Super 59 MkII Maxed + TS9.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3f), ("makeupDb", 3f)),
            NamAmp("magnatone-maxed-ts9.nam", ("gain", 0.53f), ("mid", 0.65f), ("treble", 0.55f), ("master", 0.48f), ("outputDb", 2.6f)),
            Mod("cab", ("model", 2f)),
            Mod("delay", ("timeMs", 380f), ("feedback", 0.25f), ("mix", 0.16f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.22f))),

        P("nam-lead-neoclassical-shred", "Neoclassical Shred", DspToneCategory.Lead,
            "Fast, tight, mid-boosted shred lead — screamer in front, gate for clean runs — built on a neural capture of a Pro Mod 3rd Gear.",
            Mod("gate", ("thresholdDb", -50f), ("ratio", 8f), ("releaseMs", 90f)),
            NamAmp("od808.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("splawn-gear3.nam", ("gain", 0.57f), ("bass", 0.45f), ("mid", 0.7f), ("treble", 0.62f), ("presence", 0.53f), ("master", 0.48f), ("outputDb", 4.2f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 100f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1100f), ("midGainDb", 4f), ("highFreq", 4500f), ("highGainDb", 2f)),
            Mod("delay", ("timeMs", 320f), ("feedback", 0.35f), ("mix", 0.22f)),
            Mod("reverb", ("roomSize", 0.45f), ("mix", 0.18f))),

        P("nam-lead-harmonic-boost", "Harmonic Lead Boost", DspToneCategory.Lead,
            "Searing mid-focused lead that coaxes out feedback and harmonics — built on a neural capture of a 5152 Lead + TS9.",
            NamAmp("5152-lead-ts9.nam", ("gain", 0.57f), ("bass", 0.48f), ("mid", 0.72f), ("treble", 0.7f), ("presence", 0.56f), ("master", 0.48f), ("outputDb", 4.2f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 95f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 900f), ("midQ", 2f), ("midGainDb", 5f), ("highFreq", 4000f), ("highGainDb", 3f)),
            Mod("delay", ("timeMs", 420f), ("feedback", 0.33f), ("mix", 0.24f)),
            Mod("reverb", ("roomSize", 0.55f), ("mix", 0.2f))),

        // ========================= ACOUSTIC =========================

        P("nam-bass-studio-di", "Studio DI Bass", DspToneCategory.Bass,
            "Clean, compressed hi-fi DI bass with a modern scooped voicing — built on a neural capture of a modern bass DI.",
            Mod("compressor", ("thresholdDb", -20f), ("ratio", 4f), ("attackMs", 15f), ("releaseMs", 120f), ("makeupDb", 4f), ("kneeDb", 3f)),
            NamAmp("dingwall-bass-di.nam", ("gain", 0.45f), ("bass", 0.6f), ("mid", 0.45f), ("presence", 0.38f), ("master", 0.6f), ("outputDb", 0.6f)),
            Mod("eq", ("lowFreq", 80f), ("lowGainDb", 4f), ("midFreq", 500f), ("midGainDb", -3f), ("highFreq", 3000f), ("highGainDb", 2f))),

        P("nam-bass-motown-fingerstyle", "Motown Fingerstyle", DspToneCategory.Bass,
            "Warm, round fingerstyle bass with full mids and rolled-off top — built on a neural capture of a modern bass DI.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3.5f), ("makeupDb", 3f)),
            NamAmp("dingwall-bass-di.nam", ("gain", 0.47f), ("bass", 0.65f), ("mid", 0.55f), ("treble", 0.4f), ("master", 0.6f), ("outputDb", 0.9f)),
            Mod("eq", ("lowFreq", 80f), ("lowGainDb", 3f), ("midFreq", 700f), ("midGainDb", 2f), ("highGainDb", -2f))),

        P("nam-bass-pick-rock", "Pick Rock Bass", DspToneCategory.Bass,
            "Bright pick attack with mid punch and a hair of grit — built on a neural capture of a DP3X Bass Preamp.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 3f)),
            NamAmp("sd1-super-overdrive.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("dp3x-bass-preamp.nam", ("gain", 0.48f), ("bass", 0.6f), ("mid", 0.55f), ("treble", 0.55f), ("master", 0.48f), ("outputDb", 4.4f)),
            Mod("eq", ("lowFreq", 90f), ("lowGainDb", 2f), ("midFreq", 900f), ("midGainDb", 3f), ("highFreq", 3500f), ("highGainDb", 2f))),

        P("nam-bass-grindstone", "Grindstone Bass", DspToneCategory.Bass,
            "Overdriven grindy bass that cuts through a loud mix — built on a neural capture of a DP3X Bass Preamp.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 2f)),
            NamAmp("ds1-distortion.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("dp3x-bass-preamp.nam", ("gain", 0.51f), ("bass", 0.6f), ("mid", 0.6f), ("master", 0.48f), ("outputDb", 4.3f)),
            Mod("eq", ("lowFreq", 100f), ("lowGainDb", 2f), ("midFreq", 800f), ("midGainDb", 3f))),

        P("nam-bass-slap-funk", "Slap Funk Bass", DspToneCategory.Bass,
            "Scooped, snappy slap tone — big lows, sizzling highs, quick comp — built on a neural capture of a modern bass DI.",
            Mod("compressor", ("thresholdDb", -24f), ("ratio", 5f), ("attackMs", 8f), ("releaseMs", 100f), ("makeupDb", 4f)),
            NamAmp("dingwall-bass-di.nam", ("gain", 0.46f), ("bass", 0.6f), ("mid", 0.35f), ("treble", 0.6f), ("master", 0.6f)),
            Mod("eq", ("lowFreq", 80f), ("lowGainDb", 4f), ("midFreq", 500f), ("midQ", 1.5f), ("midGainDb", -5f), ("highFreq", 4000f), ("highGainDb", 4f))),

        P("nam-bass-deep-sub", "Deep Sub Bass", DspToneCategory.Bass,
            "Dub-deep sub bass — huge lows, dark top, felt more than heard — built on a neural capture of a modern bass DI.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 3f)),
            NamAmp("dingwall-bass-di.nam", ("gain", 0.47f), ("bass", 0.75f), ("mid", 0.4f), ("treble", 0.3f), ("master", 0.6f), ("outputDb", 0.9f)),
            Mod("eq", ("lowFreq", 60f), ("lowGainDb", 5f), ("midFreq", 600f), ("midGainDb", -2f), ("highFreq", 3000f), ("highGainDb", -3f))),

        P("nam-bass-metal-clank", "Metal Bass Clank", DspToneCategory.Bass,
            "Aggressive distorted bass with clanky pick attack and tight gate — built on a neural capture of a DP3X Bass Preamp.",
            Mod("gate", ("thresholdDb", -50f), ("ratio", 8f), ("releaseMs", 90f)),
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 2f)),
            NamAmp("hm2-chainsaw.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("dp3x-bass-preamp.nam", ("gain", 0.51f), ("bass", 0.6f), ("mid", 0.55f), ("treble", 0.6f), ("master", 0.48f), ("outputDb", 4.3f)),
            Mod("eq", ("lowFreq", 90f), ("lowGainDb", 2f), ("midFreq", 1000f), ("midGainDb", 3f), ("highFreq", 3500f), ("highGainDb", 3f))),

        // ========================= AMBIENT =========================

        P("nam-ambient-wash", "Ambient Wash", DspToneCategory.Ambient,
            "Soft clean drowned in ping-pong delay and a vast reverb wash — built on a neural capture of a Twin Reverb Vibrato (Bright).",
            Mod("compressor", ("thresholdDb", -24f), ("ratio", 2f), ("makeupDb", 2f)),
            NamAmp("twinverb-vibrato-bright.nam", ("gain", 0.42f), ("master", 0.6f), ("outputDb", 3.7f)),
            Mod("cab", ("model", 1f)),
            Mod("chorus", ("rateHz", 0.3f), ("depthMs", 6f), ("mix", 0.3f), ("voices", 3f)),
            Mod("delay", ("timeMs", 500f), ("feedback", 0.45f), ("mix", 0.35f), ("pingPong", 1f), ("toneHz", 3500f)),
            Mod("reverb", ("roomSize", 0.85f), ("damping", 0.4f), ("width", 1f), ("mix", 0.4f))),

        P("nam-ambient-cathedral", "Cathedral", DspToneCategory.Ambient,
            "Enormous, slow-decaying reverb space for sustained, prayerful chords — built on a neural capture of a JCM2000 Clean.",
            NamAmp("jcm2000-clean.nam", ("gain", 0.42f), ("master", 0.75f), ("outputDb", 1.8f)),
            Mod("cab", ("model", 1f)),
            Mod("delay", ("timeMs", 600f), ("feedback", 0.4f), ("mix", 0.3f)),
            Mod("reverb", ("roomSize", 0.95f), ("damping", 0.3f), ("width", 1f), ("mix", 0.45f))),

        P("nam-ambient-shimmer-pad", "Shimmer Pad", DspToneCategory.Ambient,
            "Chorused pad with long regenerating delay and a huge shimmering tail — built on a neural capture of an AC15 Jangle.",
            NamAmp("voxac15-jangle.nam", ("gain", 0.42f), ("master", 0.72f), ("outputDb", 2.5f)),
            Mod("cab", ("model", 1f)),
            Mod("chorus", ("rateHz", 0.25f), ("depthMs", 8f), ("mix", 0.4f), ("voices", 3f)),
            Mod("delay", ("timeMs", 550f), ("feedback", 0.5f), ("mix", 0.32f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.9f), ("mix", 0.4f))),

        P("nam-ambient-post-rock-swell", "Post-Rock Swell", DspToneCategory.Ambient,
            "Edge-of-crunch guitar under sweeping delays and a big reverb bed — built on a neural capture of a Molecular 50.",
            NamAmp("klone-transparent-boost.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("ceriatone-molecular50.nam", ("gain", 0.48f), ("mid", 0.55f), ("treble", 0.55f), ("master", 0.48f), ("outputDb", 7.4f)),
            Mod("cab", ("model", 2f)),
            Mod("delay", ("timeMs", 480f), ("feedback", 0.5f), ("mix", 0.35f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.8f), ("mix", 0.35f))),

        P("nam-ambient-dream-tremolo", "Dream Tremolo", DspToneCategory.Ambient,
            "Pulsing tremolo over a clean amp with a soft delay and deep reverb — built on a neural capture of a Super 59 MkII Ch1 Normal.",
            NamAmp("magnatone-ch1-norm.nam", ("gain", 0.43f), ("master", 0.72f), ("outputDb", 2.2f)),
            Mod("cab", ("model", 0f)),
            Mod("tremolo", ("rateHz", 4.5f), ("depth", 0.6f), ("shape", 1f)),
            Mod("delay", ("timeMs", 420f), ("feedback", 0.35f), ("mix", 0.25f)),
            Mod("reverb", ("roomSize", 0.7f), ("mix", 0.3f))),

        // ========================= EXPERIMENTAL =========================

        P("nam-exp-jet-flanger", "Jet Flanger Freakout", DspToneCategory.Experimental,
            "Extreme high-feedback flanger jet-sweep over a crunchy rhythm — built on a neural capture of a Jet City Gain 5.",
            NamAmp("jetcity-g5.nam", ("gain", 0.48f), ("treble", 0.55f), ("master", 0.48f), ("outputDb", 7.4f)),
            Mod("cab", ("model", 2f)),
            Mod("flanger", ("rateHz", 0.15f), ("depthMs", 4.5f), ("feedback", 0.9f), ("mix", 0.6f)),
            Mod("delay", ("timeMs", 350f), ("feedback", 0.4f), ("mix", 0.25f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.2f))),

        P("nam-exp-runaway-delay", "Runaway Delay", DspToneCategory.Experimental,
            "Near-self-oscillating delay spiraling over a driven lead voice — built on a neural capture of a 5152 Crunch.",
            NamAmp("ts9.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("5152-crunch.nam", ("gain", 0.51f), ("mid", 0.55f), ("treble", 0.55f), ("master", 0.48f), ("outputDb", 6.3f)),
            Mod("cab", ("model", 2f)),
            Mod("delay", ("timeMs", 666f), ("feedback", 0.9f), ("mix", 0.45f), ("pingPong", 1f), ("toneHz", 3000f)),
            Mod("reverb", ("roomSize", 0.7f), ("mix", 0.3f))),

        P("nam-exp-seasick-warble", "Seasick Warble", DspToneCategory.Experimental,
            "Wobbly detuned chorus crossed with a square-wave tremolo for queasy motion — built on a neural capture of a Dirty Shirley Mini Clean.",
            NamAmp("dirtyshirley-clean.nam", ("gain", 0.45f), ("master", 0.6f), ("outputDb", 4.6f)),
            Mod("cab", ("model", 1f)),
            Mod("chorus", ("rateHz", 0.15f), ("depthMs", 12f), ("mix", 0.6f), ("voices", 3f)),
            Mod("tremolo", ("rateHz", 6f), ("depth", 0.7f), ("shape", 2f)),
            Mod("reverb", ("roomSize", 0.55f), ("mix", 0.25f))),

        P("nam-exp-gated-stutter-fuzz", "Gated Stutter Fuzz", DspToneCategory.Experimental,
            "Hard-gated velcro fuzz that stutters and chokes into silence — built on a neural capture of a 5150 Block Letter.",
            Mod("gate", ("thresholdDb", -30f), ("ratio", 20f), ("attackMs", 0.5f), ("holdMs", 0f), ("releaseMs", 30f)),
            NamAmp("os2-overdrive-distortion.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("5150-blockletter.nam", ("gain", 0.57f), ("treble", 0.6f), ("master", 0.48f), ("outputDb", 4.2f)),
            Mod("cab", ("model", 3f), ("lowCutHz", 140f)),
            Mod("reverb", ("roomSize", 0.4f), ("mix", 0.15f))),

        P("nam-exp-sub-octave-doom", "Sub-Octave Doom", DspToneCategory.Experimental,
            "Cavernous low-tuned doom with a slow flange and endless reverb — built on a neural capture of a Rockerverb.",
            NamAmp("ds1-distortion.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("orange-rockerverb.nam", ("gain", 0.56f), ("bass", 0.8f), ("treble", 0.35f), ("master", 0.48f), ("outputDb", 4.5f)),
            Mod("cab", ("model", 2f), ("lowCutHz", 40f), ("highCutHz", 4500f)),
            Mod("flanger", ("rateHz", 0.1f), ("depthMs", 3f), ("feedback", 0.6f), ("mix", 0.4f)),
            Mod("reverb", ("roomSize", 0.9f), ("mix", 0.4f))),

        // ================= ARTIST & GENRE SIGNATURES (0.16 tone catalogue) =====
        // Evocative soundalikes translated from real-world rig documentation into
        // the 39-module catalogue. Names are descriptive, never an endorsement.
        // Every rig exploits the Wave-2A cores (real spring/tape/wah/doubler) and
        // stays inside the ±6 dB loudness band (see DspTonePresetLibraryTests).

        // ---- Clean ----

        P("nam-artist-polyphia-clean-tap", "Polyphia Clean Tap", DspToneCategory.Clean,
            "Glassy hi-fi tapping clean: heavy compression, mid-forward voice, subtle stereo bloom — built on a neural capture of a Dirty Shirley Mini Clean.",
            Mod("compressor", ("thresholdDb", -26f), ("ratio", 6f), ("attackMs", 8f), ("releaseMs", 90f), ("makeupDb", 5f), ("kneeDb", 4f)),
            NamAmp("dirtyshirley-clean.nam", ("gain", 0.44f), ("bass", 0.4f), ("mid", 0.68f), ("treble", 0.6f), ("presence", 0.47f), ("master", 0.62f), ("outputDb", -0.6f)),
            Mod("cab", ("model", 0f), ("lowCutHz", 95f), ("highCutHz", 10000f)),
            Mod("eq", ("midFreq", 1400f), ("midGainDb", 3f), ("highFreq", 6000f), ("highGainDb", 2f)),
            Mod("doubler", ("detuneCents", 6f), ("spread", 0.6f), ("delayMs", 10f), ("mix", 0.22f)),
            Mod("reverb", ("roomSize", 0.4f), ("damping", 0.5f), ("mix", 0.14f))),

        P("nam-artist-yorushika-bright-clean", "Yorushika Bright Clean", DspToneCategory.Clean,
            "Airy J-rock Strat clean with sparkling top and lush ambient delay-and-verb tails — built on a neural capture of a JVM Clean (Green).",
            Mod("compressor", ("thresholdDb", -24f), ("ratio", 3f), ("makeupDb", 3f)),
            NamAmp("jvm-clean-green.nam", ("gain", 0.43f), ("bass", 0.48f), ("mid", 0.46f), ("treble", 0.66f), ("presence", 0.44f), ("master", 0.62f), ("outputDb", 1.8f)),
            Mod("cab", ("model", 0f), ("highCutHz", 11000f)),
            Mod("eq", ("highFreq", 8000f), ("highGainDb", 3f)),
            Mod("delay", ("timeMs", 440f), ("feedback", 0.32f), ("mix", 0.24f), ("pingPong", 1f), ("toneHz", 5000f)),
            Mod("reverb", ("roomSize", 0.6f), ("damping", 0.4f), ("mix", 0.26f))),

        P("nam-artist-knopfler-sultans-clean", "Sultans Clean", DspToneCategory.Clean,
            "Brown-Vibrolux fingerstyle clean with squeezed Orange-Squeezer bloom and Strat notch quack — built on a neural capture of a Twin Reverb Vibrato (Bright).",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("attackMs", 12f), ("releaseMs", 110f), ("makeupDb", 4f)),
            NamAmp("twinverb-vibrato-bright.nam", ("gain", 0.45f), ("bass", 0.52f), ("treble", 0.62f), ("presence", 0.43f), ("master", 0.6f), ("outputDb", 0.6f)),
            Mod("cab", ("model", 0f)),
            Mod("eq", ("midFreq", 900f), ("midQ", 1.5f), ("midGainDb", -2f), ("highFreq", 6500f), ("highGainDb", 2f))),

        P("nam-artist-frusciante-funk-clean", "Frusciante Funk", DspToneCategory.Clean,
            "Bright compressed single-note funk with a cocked wah honk — snappy, percussive, muted — built on a neural capture of a Super 59 MkII Ch1 Bright.",
            Mod("compressor", ("thresholdDb", -26f), ("ratio", 5f), ("attackMs", 5f), ("releaseMs", 80f), ("makeupDb", 5f)),
            Mod("wah", ("mode", 0f), ("position", 0.62f), ("resonance", 0.6f), ("mix", 0.8f)),
            NamAmp("magnatone-ch1-bright.nam", ("gain", 0.46f), ("bass", 0.45f), ("mid", 0.55f), ("treble", 0.64f), ("presence", 0.47f), ("master", 0.6f), ("outputDb", -0.7f)),
            Mod("cab", ("model", 1f))),

        P("nam-genre-city-pop-clean", "City Pop Shine", DspToneCategory.Clean,
            "Immaculate hi-fi clean under a lush dual chorus and doubler with a soft plate wash — built on a neural capture of a Twin Reverb Normal (Bright).",
            Mod("compressor", ("thresholdDb", -24f), ("ratio", 3f), ("makeupDb", 3f)),
            NamAmp("twinverb-norm-bright.nam", ("gain", 0.43f), ("mid", 0.48f), ("treble", 0.62f), ("presence", 0.45f), ("master", 0.64f), ("outputDb", 1.2f)),
            Mod("cab", ("model", 1f)),
            Mod("chorus", ("rateHz", 0.45f), ("depthMs", 6f), ("mix", 0.42f), ("voices", 3f)),
            Mod("doubler", ("detuneCents", 10f), ("spread", 0.8f), ("delayMs", 14f), ("mix", 0.3f)),
            Mod("platereverb", ("decay", 0.5f), ("damping", 0.35f), ("mix", 0.2f), ("width", 1f))),

        P("nam-genre-surf-twang", "Surf Twang", DspToneCategory.Clean,
            "Drippy cranked spring reverb and lush amp tremolo over a bright twangy clean — built on a neural capture of a Twin Reverb Vibrato (Bright).",
            NamAmp("twinverb-vibrato-bright.nam", ("gain", 0.44f), ("bass", 0.48f), ("treble", 0.68f), ("master", 0.62f), ("outputDb", 4.4f)),
            Mod("cab", ("model", 0f), ("highCutHz", 11000f)),
            Mod("tremolo", ("rateHz", 5.5f), ("depth", 0.6f), ("shape", 0f)),
            Mod("springreverb", ("decay", 0.7f), ("tone", 0.6f), ("drip", 0.8f), ("mix", 0.35f))),

        // ---- Crunch ----

        P("nam-artist-acdc-angus-crunch", "Angus High Voltage", DspToneCategory.Crunch,
            "Low-gain dimed Plexi crunch — bright, mid-and-treble forward, light on bass, no effects — built on a neural capture of a JCM900 Dual Reverb Ch A.",
            NamAmp("jcm900-crunch.nam", ("gain", 0.51f), ("bass", 0.42f), ("mid", 0.58f), ("treble", 0.66f), ("presence", 0.53f), ("outputDb", 5.6f)),
            Mod("cab", ("model", 2f)),
            Mod("eq", ("lowFreq", 120f), ("lowGainDb", -2f), ("highGainDb", 2f))),

        P("nam-artist-srv-texas-blues", "Texas Flood", DspToneCategory.Crunch,
            "Edge-of-breakup Fender pushed by a level-boost overdrive, warm mids, light spring drip — built on a neural capture of a Super 59 MkII Pushed.",
            Mod("klon", ("gain", 0.25f), ("treble", 0.55f), ("output", 0.7f), ("blend", 0.6f)),
            NamAmp("magnatone-pushed.nam", ("gain", 0.51f), ("bass", 0.55f), ("mid", 0.62f), ("treble", 0.58f), ("presence", 0.44f), ("outputDb", -7.8f)),
            Mod("cab", ("model", 0f)),
            Mod("eq", ("midFreq", 800f), ("midGainDb", 2f)),
            Mod("springreverb", ("decay", 0.4f), ("tone", 0.5f), ("drip", 0.5f), ("mix", 0.18f))),

        P("nam-artist-nirvana-teen-spirit", "Teen Spirit Slam", DspToneCategory.Crunch,
            "A maxed orange distortion box slammed into a clean preamp, with a watery small-clone chorus — built on a neural capture of a JVM Clean (Green).",
            Mod("chorus", ("rateHz", 1.2f), ("depthMs", 4f), ("mix", 0.28f), ("voices", 2f)),
            NamAmp("ds1-distortion.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("jvm-clean-green.nam", ("gain", 0.46f), ("bass", 0.55f), ("mid", 0.55f), ("treble", 0.55f), ("presence", 0.41f), ("outputDb", 7.4f)),
            Mod("cab", ("model", 2f))),

        P("nam-genre-punk-marshall", "Punk Buzz", DspToneCategory.Crunch,
            "Bright fast cranked-Marshall crunch — percussive downstrokes, minimal gain smear, bone dry — built on a neural capture of a JCM2000 Crunch.",
            NamAmp("jcm2000-crunch.nam", ("gain", 0.54f), ("bass", 0.45f), ("mid", 0.55f), ("treble", 0.68f), ("presence", 0.56f), ("outputDb", 4.6f)),
            Mod("cab", ("model", 2f)),
            Mod("eq", ("highFreq", 5000f), ("highGainDb", 3f))),

        P("nam-artist-weezer-blue-crunch", "Blue Album Crunch", DspToneCategory.Crunch,
            "Fat mid-rich low-volume Boogie Mark crunch — big round fuzzy power chords, bone dry — built on a neural capture of a Jet City Gain 5.",
            NamAmp("jetcity-g5.nam", ("gain", 0.55f), ("bass", 0.55f), ("mid", 0.66f), ("treble", 0.52f), ("presence", 0.44f), ("outputDb", 4.2f)),
            Mod("cab", ("model", 2f)),
            Mod("eq", ("midFreq", 700f), ("midGainDb", 3f))),

        // ---- Lead ----

        P("nam-artist-galneryus-neo-lead", "Neoclassical Fire", DspToneCategory.Lead,
            "Articulate mid-forward high-gain lead: front-end boost for cut, singing sustain, epic delay — built on a neural capture of a Pro Mod 3rd Gear.",
            Mod("gate", ("thresholdDb", -52f), ("ratio", 8f), ("releaseMs", 90f)),
            NamAmp("od808.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("splawn-gear3.nam", ("gain", 0.58f), ("bass", 0.46f), ("mid", 0.72f), ("treble", 0.6f), ("presence", 0.53f), ("master", 0.48f), ("outputDb", 3.8f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 100f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1100f), ("midQ", 1.5f), ("midGainDb", 4f), ("highFreq", 4500f), ("highGainDb", 2f)),
            Mod("delay", ("timeMs", 380f), ("feedback", 0.34f), ("mix", 0.22f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.2f))),

        P("nam-artist-evh-brown-sound", "Brown Sound Frankenstrat", DspToneCategory.Lead,
            "Variac'd dimed Plexi — thick spongy singing sustain with a Phase-90 swirl and warm tape slap — built on a neural capture of a 1990 Lead.",
            Mod("phaser", ("rateHz", 0.6f), ("depth", 0.7f), ("feedback", 0.2f), ("stages", 4f), ("mix", 0.4f)),
            NamAmp("bug1990-lead.nam", ("gain", 0.6f), ("bass", 0.52f), ("mid", 0.62f), ("treble", 0.74f), ("presence", 0.6f), ("outputDb", 2.4f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1000f), ("midGainDb", 2f), ("highFreq", 4000f), ("highGainDb", 4f)),
            Mod("tapedelay", ("timeMs", 250f), ("feedback", 0.25f), ("mix", 0.16f), ("wowFlutter", 0.3f), ("drive", 0.3f))),

        P("nam-artist-gilmour-comfortably", "Comfortably Lead", DspToneCategory.Lead,
            "Compressor into a moderate Big Muff into a clean Hiwatt — vocal sustain and long analog delay — built on a neural capture of a JCM2000 Clean.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 4f), ("makeupDb", 3f)),
            Mod("bigmuff", ("sustain", 0.55f), ("tone", 0.55f), ("volume", 0.6f)),
            NamAmp("ds1-distortion.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("jcm2000-clean.nam", ("gain", 0.46f), ("bass", 0.55f), ("mid", 0.6f), ("treble", 0.52f), ("master", 0.6f), ("outputDb", -6.3f)),
            Mod("cab", ("model", 2f)),
            Mod("delay", ("timeMs", 480f), ("feedback", 0.38f), ("mix", 0.26f), ("toneHz", 3500f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.18f))),

        P("nam-artist-tool-adam-jones", "Diezel Monolith", DspToneCategory.Lead,
            "Thick mid-forward high-gain with a cocked wah honk and a hypnotic half-second delay — built on a neural capture of a Savage 120.",
            Mod("wah", ("mode", 0f), ("position", 0.55f), ("resonance", 0.55f), ("mix", 0.6f)),
            NamAmp("engl-savage.nam", ("gain", 0.57f), ("bass", 0.6f), ("mid", 0.7f), ("treble", 0.66f), ("presence", 0.56f), ("master", 0.48f), ("outputDb", 4.2f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 95f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1000f), ("midGainDb", 2f), ("highFreq", 4500f), ("highGainDb", 2f)),
            Mod("delay", ("timeMs", 500f), ("feedback", 0.36f), ("mix", 0.24f), ("toneHz", 3500f)),
            Mod("reverb", ("roomSize", 0.4f), ("mix", 0.14f))),

        P("nam-artist-ink-metalcore-lead", "Cinematic Metalcore Lead", DspToneCategory.Lead,
            "Smooth sustaining metalcore lead — mid-present high gain with lush delay and hall for the theatrics — built on a neural capture of a Ceres Purple Channel.",
            Mod("gate", ("thresholdDb", -48f), ("ratio", 10f), ("releaseMs", 70f)),
            NamAmp("grind-mini-boost.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("ksr-ceres-purple.nam", ("gain", 0.58f), ("mid", 0.62f), ("treble", 0.62f), ("presence", 0.53f), ("master", 0.48f), ("outputDb", 3.8f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 110f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 900f), ("midGainDb", 3f), ("highFreq", 4500f), ("highGainDb", 2f)),
            Mod("delay", ("timeMs", 420f), ("feedback", 0.36f), ("mix", 0.24f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.55f), ("mix", 0.22f))),

        // ---- High gain ----

        P("nam-artist-metallica-mop-rhythm", "Puppets Rhythm", DspToneCategory.HighGain,
            "Textbook scooped thrash — boosted JMP stack into Greenbacks, tight percussive chug, bone-dry — built on a neural capture of a Mark III Red (Boosted).",
            Mod("gate", ("thresholdDb", -50f), ("ratio", 10f), ("releaseMs", 60f)),
            NamAmp("mesa-iii-red-boosted.nam", ("gain", 0.62f), ("bass", 0.6f), ("mid", 0.3f), ("treble", 0.75f), ("presence", 0.62f), ("master", 0.48f), ("outputDb", 2.4f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 90f), ("highCutHz", 9000f)),
            Mod("eq", ("lowFreq", 100f), ("lowGainDb", -1f), ("midFreq", 750f), ("midQ", 1.4f), ("midGainDb", -6f), ("highFreq", 4500f), ("highGainDb", 4f))),

        P("nam-artist-slipknot-drop-metal", "Drop-B Brutalizer", DspToneCategory.HighGain,
            "Tight-gated screamer-boosted rectifier built to keep drop-tuned palm mutes articulate — built on a neural capture of a 6505+ Red Channel (Boosted).",
            Mod("gate", ("thresholdDb", -46f), ("ratio", 12f), ("attackMs", 1f), ("releaseMs", 60f)),
            NamAmp("6505p-red-boosted.nam", ("gain", 0.59f), ("bass", 0.58f), ("treble", 0.62f), ("presence", 0.53f), ("master", 0.46f), ("outputDb", 4.2f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 115f), ("highCutHz", 9000f)),
            Mod("eq", ("midFreq", 1200f), ("midQ", 1f), ("midGainDb", 3f), ("highFreq", 4500f), ("highGainDb", 2f))),

        P("nam-artist-ink-metalcore-rhythm", "Symphonic Metalcore Chug", DspToneCategory.HighGain,
            "Modern digital metalcore rhythm — tight scooped chug, firm low end, crisp presence, hard gate — built on a neural capture of a Savage 120.",
            Mod("gate", ("thresholdDb", -46f), ("ratio", 12f), ("releaseMs", 60f)),
            NamAmp("precision-drive.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("engl-savage.nam", ("gain", 0.58f), ("bass", 0.55f), ("mid", 0.45f), ("treble", 0.64f), ("presence", 0.55f), ("master", 0.46f), ("outputDb", 4.6f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 120f), ("highCutHz", 9000f)),
            Mod("eq", ("lowFreq", 110f), ("lowGainDb", -2f), ("midFreq", 700f), ("midQ", 1.3f), ("midGainDb", -3f), ("highFreq", 4500f), ("highGainDb", 3f))),

        P("nam-genre-djent-tight-boost", "Djent Tight Boost", DspToneCategory.HighGain,
            "Ruthless screamer-boosted djent — surgically tight lows, defined scoop, crisp staccato attack — built on a neural capture of a Yerasov Djent (Boosted).",
            Mod("gate", ("thresholdDb", -44f), ("ratio", 16f), ("attackMs", 0.5f), ("holdMs", 6f), ("releaseMs", 50f)),
            NamAmp("precision-drive.nam", ("gain", 0.5f), ("master", 0.5f)),
            NamAmp("yerasov-djent-boosted.nam", ("gain", 0.57f), ("bass", 0.48f), ("mid", 0.45f), ("treble", 0.62f), ("presence", 0.53f), ("master", 0.46f), ("outputDb", 4.9f)),
            Mod("cab", ("model", 8f), ("lowCutHz", 130f), ("highCutHz", 9000f)),
            Mod("eq", ("lowFreq", 120f), ("lowGainDb", -2f), ("midFreq", 620f), ("midQ", 1.6f), ("midGainDb", -4f), ("highFreq", 4500f), ("highGainDb", 3f))),

        // ---- Ambient / Experimental ----

        P("nam-genre-shoegaze-wall", "Shoegaze Wall", DspToneCategory.Ambient,
            "A Big-Muff wall drowned in shimmer reverb and long modulated delay — hazy, cavernous, blurred — built on a neural capture of a GH100S Low Crunch.",
            Mod("bigmuff", ("sustain", 0.7f), ("tone", 0.5f), ("volume", 0.6f)),
            NamAmp("laney-gh100s-crunch.nam", ("gain", 0.48f), ("treble", 0.55f), ("presence", 0.44f), ("outputDb", -3.8f)),
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

        P("nam-miku-stomp-duet", "Miku Stomp Duet", DspToneCategory.Experimental,
            "Half guitar, half voice — a clean combo under a soft 'lahh' choir, widened by chorus and echo — built on a neural capture of a Dirty Shirley Mini Clean.",
            Mod("compressor", ("thresholdDb", -22f), ("ratio", 3f), ("makeupDb", 3f)),
            NamAmp("dirtyshirley-clean.nam", ("gain", 0.44f), ("treble", 0.55f), ("presence", 0.44f), ("master", 0.75f), ("outputDb", -1.9f)),
            Mod("cab", ("model", 1f)),
            // The whole point of this rig: mikustomp is WET-ONLY by default, so the
            // guitar only survives because mix is pinned at 0.5.
            Mod("mikustomp", ("mix", 0.5f), ("character", 0.6f), ("breath", 0.35f), ("attackMs", 14f),
                ("portamentoMs", 70f), ("vibrato", 0.5f), ("vibratoRateHz", 5.4f), ("octave", 1f), ("pattern", 1f)),
            Mod("chorus", ("rateHz", 0.5f), ("depthMs", 5f), ("mix", 0.3f), ("voices", 2f)),
            Mod("delay", ("timeMs", 380f), ("feedback", 0.3f), ("mix", 0.2f), ("toneHz", 4500f)),
            Mod("reverb", ("roomSize", 0.5f), ("mix", 0.2f))),

        P("nam-miku-stomp-scat", "Miku Stomp Scat Machine", DspToneCategory.Experimental,
            "Rhythmic scat syllables riding a snappy clean amp — the guitar keeps the pick attack, the voice does the phrasing — built on a neural capture of a Dirty Shirley Mini Clean.",
            Mod("compressor", ("thresholdDb", -24f), ("ratio", 5f), ("attackMs", 5f), ("releaseMs", 80f), ("makeupDb", 3f)),
            NamAmp("dirtyshirley-clean.nam", ("gain", 0.45f), ("mid", 0.55f), ("treble", 0.6f), ("presence", 0.47f), ("master", 0.7f), ("outputDb", -1.1f)),
            Mod("cab", ("model", 0f)),
            Mod("mikustomp", ("mix", 0.75f), ("character", 0.55f), ("breath", 0.45f), ("attackMs", 4f),
                ("portamentoMs", 8f), ("vibrato", 0.2f), ("vibratoRateHz", 5f), ("octave", 1f), ("pattern", 5f)),
            Mod("delay", ("timeMs", 260f), ("feedback", 0.3f), ("mix", 0.22f), ("pingPong", 1f)),
            Mod("reverb", ("roomSize", 0.4f), ("mix", 0.16f))),
    ];
}
