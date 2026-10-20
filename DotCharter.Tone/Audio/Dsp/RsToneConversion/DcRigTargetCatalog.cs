namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
/// A static mirror of the native DSP module catalogue's param ranges (min/max/
/// default) so the pure <see cref="RsToneConverter"/> needs no live
/// <c>IDspChainService</c>. Kept in sync by hand with the native
/// <c>DspModuleFactory</c> catalogue.
/// </summary>
internal static class DcRigTargetCatalog
{
    internal readonly record struct ParamSpec(string Id, float Min, float Max, float Default);

    // Module type ids (must match the native catalogue).
    public const string Gain = "gain";
    public const string Gate = "gate";
    public const string Compressor = "compressor";
    public const string Eq = "eq";
    public const string Overdrive = "overdrive";
    public const string Distortion = "distortion";
    public const string Amp = "amp";
    public const string Cab = "cab";
    public const string Tremolo = "tremolo";
    public const string Chorus = "chorus";
    public const string Flanger = "flanger";
    public const string Delay = "delay";
    public const string Reverb = "reverb";
    // 0.15.1 expansion (native catalogue → 27 modules).
    public const string Fuzz = "fuzz";
    public const string Phaser = "phaser";
    public const string Wah = "wah";
    public const string Octaver = "octaver";
    public const string RingMod = "ringmod";
    public const string BitCrusher = "bitcrusher";
    public const string TapeDelay = "tapedelay";
    public const string PlateReverb = "platereverb";
    public const string Rotary = "rotary";
    public const string Formant = "formant";
    public const string GraphicEq = "graphiceq";
    public const string Harmonizer = "harmonizer";
    public const string Vibrato = "vibrato";
    public const string Sustainer = "sustainer";
    // 0.15.2 expansion (native catalogue → 37 modules).
    public const string SpringReverb = "springreverb";
    public const string Shimmer = "shimmer";
    public const string UniVibe = "univibe";
    public const string HarmTremolo = "harmtremolo";
    public const string EnvFilter = "envfilter";
    public const string Doubler = "doubler";
    public const string AcousticSim = "acousticsim";
    public const string NoiseReducer = "noisereducer";
    public const string Klon = "klon";
    public const string BigMuff = "bigmuff";
    // 0.16 Wave 3 (ABI v22 — native catalogue → 39 modules).
    public const string Miku = "miku";
    public const string NamAmp = "namamp";
    // 0.16 Wave 5 (ABI v23 — native catalogue → 44 modules; split chains).
    public const string AutoWah = "autowah";
    public const string Boost = "boost";
    public const string Dimension = "dimension";
    public const string Splitter = "splitter";
    public const string Merger = "merger";
    // 0.16 Wave 6: the faithful KORG-Miku-Stomp recreation (EpR singing voice,
    // AEM-style head/joint articulation). `miku` (v1, formant synth) STAYS —
    // the owner keeps both.
    public const string MikuStomp = "mikustomp";
    // 0.18 expressive/performance wave (native catalogue → 57 modules).
    public const string Swell = "swell";
    public const string Whammy = "whammy";
    public const string ReverseDelay = "reversedelay";
    public const string Slapback = "slapback";
    public const string Widener = "widener";
    public const string Freeze = "freeze";
    public const string Subharmonic = "subharmonic";
    public const string FilterSeq = "filterseq";
    public const string Feedbacker = "feedbacker";
    public const string Resonator = "resonator";
    public const string TapeStop = "tapestop";
    public const string Glitch = "glitch";
    // Gear catalogue rows (mirroring the native catalogue). One
    // drawer per source file under JuceSharp/src/JuceSharp.Native/src/dsp/gear/;
    // every range below was read OUT of that catalogue, never invented, and
    // DcRigTargetCatalogTests re-checks each one against the live native
    // metadata on a machine with the DLL.
    // DynamicsGear.cpp
    public const string Limiter = "limiter";
    public const string Transient = "transient";
    public const string Multiband = "multiband";
    public const string Expander = "expander";
    public const string OptoComp = "optocomp";
    public const string Ducker = "ducker";
    // DriveGear.cpp
    public const string Rat = "rat";
    public const string Ds1 = "ds1";
    public const string Hm2 = "hm2";
    public const string TapeSat = "tapesat";
    public const string BassOd = "bassod";
    public const string TubePre = "tubepre";
    public const string OctFuzz = "octfuzz";
    // AmpCabGear.cpp
    public const string BassAmp = "bassamp";
    public const string AcPre = "acpre";
    public const string PowerAmp = "poweramp";
    public const string MicPos = "micpos";
    // FilterGear.cpp
    public const string Svf = "svf";
    public const string Comb = "comb";
    public const string Vocoder = "vocoder";
    public const string Talkbox = "talkbox";
    public const string Tilt = "tilt";
    // ModulationGear.cpp
    public const string TzFlanger = "tzflanger";
    public const string Barberpole = "barberpole";
    public const string AutoPan = "autopan";
    public const string Ensemble = "ensemble";
    public const string FreqShift = "freqshift";
    // PitchGear.cpp
    public const string Detune = "detune";
    public const string PitchCorrect = "pitchcorrect";
    public const string TwelveString = "twelvestring";
    public const string BassSynth = "basssynth";
    // DelayGear.cpp
    public const string Multitap = "multitap";
    public const string DuckDelay = "duckdelay";
    public const string BbdDelay = "bbddelay";
    public const string Granular = "granular";
    public const string RhythmDelay = "rhythmdelay";
    // ReverbGear.cpp
    public const string HallReverb = "hallreverb";
    public const string ConvReverb = "convreverb";
    public const string GatedReverb = "gatedreverb";
    public const string ReverseReverb = "reversereverb";
    // UtilityGear.cpp
    public const string Volume = "volume";
    public const string MidSide = "midside";
    public const string Looper = "looper";
    // RsAmpGear.cpp: Rocksmith 2014's own amp chains, one row per RS amp.
    public const string RsAmpChain = "rsamp";

    private static readonly Dictionary<string, ParamSpec[]> Modules = new(StringComparer.Ordinal)
    {
        [Gain] = [new ParamSpec("gainDb", -24f, 24f, 0f)],
        [Gate] =
        [
            new ParamSpec("thresholdDb", -80f, 0f, -55f),
            new ParamSpec("ratio", 1f, 50f, 10f),
            new ParamSpec("attackMs", 0.1f, 50f, 1f),
            new ParamSpec("holdMs", 0f, 500f, 40f),
            new ParamSpec("releaseMs", 5f, 1000f, 120f),
            // 1 = the threshold follows the input's measured noise floor
            // (Rocksmith's own input gate) and thresholdDb is its ceiling.
            new ParamSpec("autoFloor", 0f, 1f, 0f)
        ],
        [Compressor] =
        [
            new ParamSpec("thresholdDb", -60f, 0f, -18f),
            new ParamSpec("ratio", 1f, 20f, 4f),
            new ParamSpec("attackMs", 0.1f, 100f, 10f),
            new ParamSpec("releaseMs", 5f, 1000f, 120f),
            new ParamSpec("makeupDb", 0f, 24f, 0f),
            new ParamSpec("kneeDb", 0f, 24f, 6f),
            // Wave 2A: peak↔RMS detector blend; native default 1 (RMS) IS the
            // fix — presets wanting the old peak bite set detector=0.
            new ParamSpec("detector", 0f, 1f, 1f)
        ],
        [Eq] =
        [
            // Wide enough to cover Rack_StudioEQ's four bands (20 Hz
            // bass shelf .. 20 kHz treble shelf); native clamps the treble corner
            // under 0.9 x Nyquist at the device rate, never the stored value.
            new ParamSpec("lowFreq", 20f, 500f, 100f),
            new ParamSpec("lowGainDb", -18f, 18f, 0f),
            new ParamSpec("midFreq", 100f, 8000f, 1000f),
            new ParamSpec("midQ", 0.3f, 8f, 1f),
            new ParamSpec("midGainDb", -18f, 18f, 0f),
            new ParamSpec("highFreq", 2000f, 20000f, 6000f),
            new ParamSpec("highGainDb", -18f, 18f, 0f),
            new ParamSpec("outputTrimDb", -24f, 24f, 0f)
        ],
        [Overdrive] =
        [
            // v24 gear roster: 0 Generic (original sound) / 1 Screamer /
            // 2 Blues Breaker / 3 Transparent. First param, matching native.
            new ParamSpec("model", 0f, 3f, 0f),
            new ParamSpec("drive", 0f, 1f, 0.5f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("level", 0f, 1f, 0.7f)
        ],
        [Distortion] =
        [
            // v24: 0 Generic / 1 Gnash / 2 Metal / 3 Plus.
            new ParamSpec("model", 0f, 3f, 0f),
            new ParamSpec("gain", 0f, 1f, 0.6f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("level", 0f, 1f, 0.6f),
            new ParamSpec("tightHz", 20f, 300f, 90f)
        ],
        [Amp] =
        [
            new ParamSpec("gain", 0f, 1f, 0.5f),
            new ParamSpec("bass", 0f, 1f, 0.5f),
            new ParamSpec("mid", 0f, 1f, 0.5f),
            new ParamSpec("treble", 0f, 1f, 0.5f),
            new ParamSpec("presence", 0f, 1f, 0.3f),
            new ParamSpec("master", 0f, 1f, 0.6f),
            // 27 named amp models (indices 0..6 are the original voicings, 7..23
            // soundalikes); 24..26 are CIRCUIT voicings through a documented
            // passive tone stack (dcdsp_tonestack.h). Display names come from the
            // value-label ABI, never hardcoded here.
            new ParamSpec("voicing", 0f, 26f, 1f),
            // Bright (0/1, the AB763 bright cap) and Resonance
            // (the poweramp module's resonance shelf). Both default to the
            // plain sound (off).
            new ParamSpec("bright", 0f, 1f, 0f),
            new ParamSpec("resonance", 0f, 1f, 0f)
        ],
        [Cab] =
        [
            // Named cab models (12..25 are IRs built from measured driver
            // responses for the speaker layouts Rocksmith's cabinets use).
            new ParamSpec("model", 0f, 25f, 0f),
            new ParamSpec("mix", 0f, 1f, 1f),
            // 0.35: default 60, not 80. At 80 Hz this Butterworth took another
            // 2.8 dB at the low E on top of the IR's own rolloff — see
            // dcdsp_cab.h. This is the preset-apply default (FillDefaults), so
            // it MUST track the native catalogue entry.
            new ParamSpec("lowCutHz", 40f, 200f, 60f),
            new ParamSpec("highCutHz", 3000f, 12000f, 8000f),
            new ParamSpec("blend", 0f, 1f, 0f),
            new ParamSpec("gainADb", -24f, 24f, 0f),
            new ParamSpec("gainBDb", -24f, 24f, 0f),
            new ParamSpec("invertA", 0f, 1f, 0f),
            new ParamSpec("invertB", 0f, 1f, 0f),
            new ParamSpec("delayAMs", 0f, 20f, 0f),
            new ParamSpec("delayBMs", 0f, 20f, 0f),
            new ParamSpec("outputDb", -24f, 24f, 0f)
        ],
        [Tremolo] =
        [
            new ParamSpec("rateHz", 0.1f, 12f, 5f),
            new ParamSpec("depth", 0f, 1f, 0.5f),
            new ParamSpec("shape", 0f, 2f, 0f)
        ],
        [Chorus] =
        [
            new ParamSpec("rateHz", 0.1f, 5f, 0.8f),
            new ParamSpec("depthMs", 1f, 15f, 6f),
            new ParamSpec("mix", 0f, 1f, 0.5f),
            new ParamSpec("voices", 1f, 3f, 2f),
            // 0.18 BBD pass: four appended params, each a NO-OP at its default
            // (feedback 0 / spread 0 / lfoShape 0 = Sine / wetHpfHz 20 = the
            // filter BYPASSED), so every existing chorus preset is bit-identical.
            new ParamSpec("feedback", 0f, 0.9f, 0f),
            new ParamSpec("spread", 0f, 1f, 0f),
            new ParamSpec("lfoShape", 0f, 2f, 0f),
            new ParamSpec("wetHpfHz", 20f, 400f, 20f)
        ],
        [Flanger] =
        [
            new ParamSpec("rateHz", 0.05f, 3f, 0.3f),
            new ParamSpec("depthMs", 0.5f, 5f, 2f),
            new ParamSpec("feedback", -0.95f, 0.95f, 0.5f),
            new ParamSpec("mix", 0f, 1f, 0.5f)
        ],
        [Delay] =
        [
            new ParamSpec("timeMs", 10f, 2000f, 300f),
            new ParamSpec("feedback", 0f, 0.95f, 0.4f),
            new ParamSpec("mix", 0f, 1f, 0.35f),
            new ParamSpec("pingPong", 0f, 1f, 0f),
            new ParamSpec("toneHz", 500f, 12000f, 4000f),
            new ParamSpec("sync", 0f, 1f, 0f),
            new ParamSpec("bpm", 20f, 400f, 120f),
            new ParamSpec("division", 0f, 8f, 2f),
            new ParamSpec("trails", 0f, 1f, 0f)
        ],
        [Reverb] =
        [
            new ParamSpec("roomSize", 0f, 1f, 0.5f),
            new ParamSpec("damping", 0f, 1f, 0.5f),
            new ParamSpec("width", 0f, 1f, 1f),
            new ParamSpec("mix", 0f, 1f, 0.3f)
        ],
        // ---- 0.15.1 expansion (mirror the native catalogue exactly) ----------
        [Fuzz] =
        [
            new ParamSpec("fuzz", 0f, 1f, 0.6f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("level", 0f, 1f, 0.7f)
        ],
        [Phaser] =
        [
            new ParamSpec("rateHz", 0.05f, 8f, 0.5f),
            new ParamSpec("depth", 0f, 1f, 0.7f),
            new ParamSpec("feedback", -0.95f, 0.95f, 0.3f),
            new ParamSpec("stages", 2f, 12f, 6f),
            new ParamSpec("mix", 0f, 1f, 0.5f)
        ],
        [Wah] =
        [
            new ParamSpec("mode", 0f, 1f, 0f),
            new ParamSpec("position", 0f, 1f, 0.5f),
            new ParamSpec("sensitivity", 0f, 1f, 0.5f),
            new ParamSpec("rangeLoHz", 200f, 800f, 400f),
            new ParamSpec("rangeHiHz", 1200f, 3000f, 2000f),
            new ParamSpec("resonance", 0f, 1f, 0.5f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [Octaver] =
        [
            new ParamSpec("sub1", 0f, 1f, 0.6f),
            new ParamSpec("sub2", 0f, 1f, 0f),
            new ParamSpec("dry", 0f, 1f, 1f),
            new ParamSpec("toneHz", 500f, 5000f, 2000f)
        ],
        [RingMod] =
        [
            new ParamSpec("freqHz", 20f, 3000f, 500f),
            new ParamSpec("mix", 0f, 1f, 1f),
            new ParamSpec("waveform", 0f, 1f, 0f)
        ],
        [BitCrusher] =
        [
            new ParamSpec("bits", 1f, 16f, 16f),
            new ParamSpec("downsample", 1f, 64f, 1f),
            new ParamSpec("mix", 0f, 1f, 1f),
            // The crushed path's FutzBox stages (Rocksmith's Bit Cruncher);
            // at these defaults the path is the plain crusher.
            new ParamSpec("inputDb", -24f, 48f, 0f),
            new ParamSpec("drive", 0f, 100f, 0f),
            new ParamSpec("intensity", 0f, 100f, 0f),
            new ParamSpec("rectify", 0f, 100f, 0f),
            new ParamSpec("filter", 0f, 100f, 0f),
            new ParamSpec("outputDb", -48f, 24f, 0f)
        ],
        [TapeDelay] =
        [
            new ParamSpec("timeMs", 20f, 2000f, 350f),
            new ParamSpec("feedback", 0f, 0.95f, 0.35f),
            new ParamSpec("mix", 0f, 1f, 0.35f),
            new ParamSpec("wowFlutter", 0f, 1f, 0.25f),
            new ParamSpec("toneHz", 800f, 8000f, 4000f),
            // Wave 2A: record-path saturation amount; repeats saturate mildly
            // even at 0 (true tape topology) — pristine repeats = plain delay.
            new ParamSpec("drive", 0f, 1f, 0.25f)
        ],
        [PlateReverb] =
        [
            new ParamSpec("decay", 0f, 1f, 0.5f),
            new ParamSpec("damping", 0f, 1f, 0.3f),
            new ParamSpec("predelayMs", 0f, 120f, 0f),
            new ParamSpec("mix", 0f, 1f, 0.3f),
            new ParamSpec("width", 0f, 1f, 1f)
        ],
        [Rotary] =
        [
            new ParamSpec("speed", 0f, 1f, 0f),
            new ParamSpec("depth", 0f, 1f, 1f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [Formant] =
        [
            new ParamSpec("vowel", 0f, 1f, 0f),
            new ParamSpec("amount", 0f, 1f, 0.5f),
            new ParamSpec("brightness", 0f, 1f, 0.5f),
            new ParamSpec("rateHz", 0f, 8f, 0f)
        ],
        [GraphicEq] =
        [
            new ParamSpec("band31", -12f, 12f, 0f),
            new ParamSpec("band63", -12f, 12f, 0f),
            new ParamSpec("band125", -12f, 12f, 0f),
            new ParamSpec("band250", -12f, 12f, 0f),
            new ParamSpec("band500", -12f, 12f, 0f),
            new ParamSpec("band1k", -12f, 12f, 0f),
            new ParamSpec("band2k", -12f, 12f, 0f),
            new ParamSpec("band4k", -12f, 12f, 0f),
            new ParamSpec("band8k", -12f, 12f, 0f),
            new ParamSpec("band16k", -12f, 12f, 0f),
            new ParamSpec("level", -12f, 12f, 0f)
        ],
        [Harmonizer] =
        [
            new ParamSpec("semitones", -12f, 12f, 0f),
            new ParamSpec("mix", 0f, 1f, 0.5f),
            new ParamSpec("lowLatency", 0f, 1f, 0f)
        ],
        [Vibrato] =
        [
            new ParamSpec("rateHz", 0.1f, 10f, 5f),
            new ParamSpec("depthMs", 0.5f, 12f, 3f),
            new ParamSpec("waveform", 0f, 1f, 0f)
        ],
        [Sustainer] =
        [
            new ParamSpec("sustain", 0f, 1f, 0.5f),
            new ParamSpec("attack", 0f, 1f, 0.3f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("level", 0f, 1f, 0.5f)
        ],
        // ---- 0.15.2 expansion (mirror the native catalogue exactly) ----------
        [SpringReverb] =
        [
            new ParamSpec("decay", 0f, 1f, 0.5f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("drip", 0f, 1f, 0.5f),
            new ParamSpec("mix", 0f, 1f, 0.3f)
        ],
        [Shimmer] =
        [
            new ParamSpec("decay", 0f, 1f, 0.7f),
            new ParamSpec("shimmer", 0f, 1f, 0.5f),
            new ParamSpec("damping", 0f, 1f, 0.5f),
            new ParamSpec("mix", 0f, 1f, 0.35f)
        ],
        [UniVibe] =
        [
            new ParamSpec("rateHz", 0.1f, 8f, 2f),
            new ParamSpec("depth", 0f, 1f, 0.7f),
            new ParamSpec("mode", 0f, 1f, 0f),
            new ParamSpec("mix", 0f, 1f, 0.5f)
        ],
        [HarmTremolo] =
        [
            new ParamSpec("rateHz", 0.1f, 12f, 4f),
            new ParamSpec("depth", 0f, 1f, 0.6f),
            new ParamSpec("crossoverHz", 200f, 2000f, 800f)
        ],
        [EnvFilter] =
        [
            new ParamSpec("sensitivity", 0f, 1f, 0.5f),
            new ParamSpec("rangeLoHz", 100f, 800f, 200f),
            new ParamSpec("rangeHiHz", 800f, 4000f, 2500f),
            new ParamSpec("resonance", 0f, 1f, 0.7f),
            new ParamSpec("direction", 0f, 1f, 0f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [Doubler] =
        [
            new ParamSpec("detuneCents", 0f, 30f, 12f),
            new ParamSpec("spread", 0f, 1f, 0.7f),
            new ParamSpec("delayMs", 2f, 40f, 12f),
            new ParamSpec("mix", 0f, 1f, 0.5f)
        ],
        [AcousticSim] =
        [
            // Wave 2A: body is a size+amount morph over real body IRs
            // (0 = flat/dry, 0–0.5 parlor→OM, 0.5–1 OM→dreadnought), no
            // longer the depth of two fixed EQ peaks. Range/default unchanged.
            new ParamSpec("body", 0f, 1f, 0.5f),
            new ParamSpec("brightness", 0f, 1f, 0.5f),
            new ParamSpec("lowCutHz", 40f, 200f, 90f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [NoiseReducer] =
        [
            new ParamSpec("amount", 0f, 1f, 0.5f),
            new ParamSpec("thresholdDb", -80f, -20f, -55f),
            new ParamSpec("humFreq", 50f, 60f, 50f),
            new ParamSpec("humDepth", 0f, 1f, 0f),
            new ParamSpec("releaseMs", 20f, 500f, 120f)
        ],
        [Klon] =
        [
            new ParamSpec("gain", 0f, 1f, 0.5f),
            new ParamSpec("treble", 0f, 1f, 0.5f),
            new ParamSpec("output", 0f, 1f, 0.6f),
            // Not an absolute clean fraction but a ±0.25 trim
            // around the gain-ganged dual-gang law (0.5 = stock Klon law).
            new ParamSpec("blend", 0f, 1f, 0.6f)
        ],
        [BigMuff] =
        [
            new ParamSpec("sustain", 0f, 1f, 0.7f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("volume", 0f, 1f, 0.6f)
        ],
        // Wave 3 (ABI v22). Both also carry STRING params (not ParamSpecs —
        // no knobs): miku "phrases" (free text), namamp "modelPath"
        // (*.nam;*.aidax). namamp additionally reports poll-only
        // "loaded"/"rateMismatch" via get_param (the 4.5 48 kHz warning hook).
        [Miku] =
        [
            new ParamSpec("mix", 0f, 1f, 0.6f),
            new ParamSpec("character", 0f, 1f, 0.5f),
            new ParamSpec("octave", -1f, 2f, 1f),
            new ParamSpec("pattern", 0f, 7f, 0f)
        ],
        // The AMP FACE (0.36) wrapped around the capture. A capture is one amp
        // at ONE knob setting and has no controls of its own; `gain` is the only
        // one that reaches the nonlinearity (it is the level PRESENTED to the
        // model), the rest are post-capture. **Every default is the NEUTRAL
        // value and neutral is a strict native no-op** (each band is skipped,
        // not run at 0 dB), which is what keeps every preset authored before
        // 0.36 — and every user tone that omits them — bit-identical.
        // `master` is an audio taper ((2m)^2), exactly unity at 0.5.
        // Mirrors dcrig::namAmpTypeInfo(); change one, change the other.
        [NamAmp] =
        [
            new ParamSpec("gain", 0f, 1f, 0.5f),
            new ParamSpec("bass", 0f, 1f, 0.5f),
            new ParamSpec("mid", 0f, 1f, 0.5f),
            new ParamSpec("treble", 0f, 1f, 0.5f),
            new ParamSpec("presence", 0f, 1f, 0.5f),
            new ParamSpec("resonance", 0f, 1f, 0.5f),
            new ParamSpec("master", 0f, 1f, 0.5f),
            new ParamSpec("inputDb", -24f, 24f, 0f),
            new ParamSpec("outputDb", -24f, 24f, 0f)
        ],
        // Wave 5 (ABI v23).
        [AutoWah] =
        [
            new ParamSpec("sensitivity", 0f, 1f, 0.5f),
            new ParamSpec("attackMs", 1f, 100f, 10f),
            new ParamSpec("releaseMs", 20f, 500f, 100f),
            new ParamSpec("rangeLo", 100f, 800f, 300f),
            new ParamSpec("rangeHi", 800f, 4000f, 2200f),
            new ParamSpec("direction", 0f, 1f, 0f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [Boost] =
        [
            new ParamSpec("gainDb", 0f, 24f, 6f),
            new ParamSpec("tilt", -1f, 1f, 0f),
            new ParamSpec("character", 0f, 1f, 0f)
        ],
        [Dimension] =
        [
            new ParamSpec("mode", 0f, 3f, 1f),
            new ParamSpec("mix", 0f, 1f, 0.5f)
        ],
        [Splitter] =
        [
            new ParamSpec("enabledBranches", 2f, 3f, 2f)
        ],
        [Merger] =
        [
            new ParamSpec("level1", 0f, 2f, 1f),
            new ParamSpec("pan1", -1f, 1f, 0f),
            new ParamSpec("level2", 0f, 2f, 1f),
            new ParamSpec("pan2", -1f, 1f, 0f),
            new ParamSpec("level3", 0f, 2f, 1f),
            new ParamSpec("pan3", -1f, 1f, 0f),
            new ParamSpec("mode", 0f, 1f, 0f),
            new ParamSpec("mute1", 0f, 1f, 0f),
            new ParamSpec("solo1", 0f, 1f, 0f),
            new ParamSpec("mute2", 0f, 1f, 0f),
            new ParamSpec("solo2", 0f, 1f, 0f),
            new ParamSpec("mute3", 0f, 1f, 0f),
            new ParamSpec("solo3", 0f, 1f, 0f)
        ],
        // Wave 6. Wet-only like the hardware (mix default 1). `pattern` carries
        // v24 value labels (Ahh…Random 2); the free-text phrase slots
        // (`phrases`/`phrase2`/`phrase3`) are STRING params, not ParamSpecs —
        // `phrases` deliberately shares miku v1's id so a lyric string works in
        // either module.
        [MikuStomp] =
        [
            new ParamSpec("mix", 0f, 1f, 1f),
            new ParamSpec("character", 0f, 1f, 0.5f),
            new ParamSpec("breath", 0f, 1f, 0.35f),
            new ParamSpec("attackMs", 1f, 200f, 12f),
            new ParamSpec("portamentoMs", 0f, 300f, 50f),
            new ParamSpec("vibrato", 0f, 1f, 0.45f),
            new ParamSpec("vibratoRateHz", 3f, 8f, 5.8f),
            new ParamSpec("octave", -1f, 2f, 1f),
            new ParamSpec("pattern", 0f, 10f, 0f)
        ],
        // ---- 0.18 expressive / performance wave (mirror the native catalogue
        // exactly — DcRigTargetCatalogTests compares this table against the
        // NATIVE one param by param). Ranges/defaults come from each core's
        // params struct (JuceSharp/src/JuceSharp.Native/src/dsp/cores/).
        [Swell] =
        [
            // Named choices carry v24 value labels natively (mode: Auto Swell /
            // Volume) — the label STRINGS live in native, never here.
            new ParamSpec("mode", 0f, 1f, 0f),
            new ParamSpec("attackMs", 5f, 2000f, 350f),
            new ParamSpec("sensitivity", 0f, 1f, 0.5f),
            new ParamSpec("depth", 0f, 1f, 1f),
            // The module's static gain — see DcRigLoudnessNormalizer.
            new ParamSpec("position", 0f, 1f, 1f)
        ],
        [Whammy] =
        [
            new ParamSpec("position", 0f, 1f, 0f),
            new ParamSpec("heelSemis", -24f, 24f, 0f),
            new ParamSpec("toeSemis", -24f, 24f, 12f),
            new ParamSpec("mix", 0f, 1f, 1f),
            new ParamSpec("glideMs", 0f, 200f, 20f),
            new ParamSpec("formant", 0f, 1f, 0f),
            new ParamSpec("lowLatency", 0f, 1f, 0f)
        ],
        [ReverseDelay] =
        [
            new ParamSpec("timeMs", 100f, 2000f, 500f),
            // 0.7 is a stability bound, not taste: the two power-complementary
            // reverse heads can sum to √2 (dcdsp_reversedelay.h).
            new ParamSpec("feedback", 0f, 0.7f, 0.3f),
            new ParamSpec("mix", 0f, 1f, 0.5f),
            new ParamSpec("toneHz", 500f, 12000f, 4000f)
        ],
        [Slapback] =
        [
            new ParamSpec("timeMs", 40f, 300f, 110f),
            // An exact FIR echo COUNT (no feedback loop), not a decay coefficient.
            new ParamSpec("repeats", 1f, 3f, 1f),
            new ParamSpec("mix", 0f, 1f, 0.35f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("drive", 0f, 1f, 0.25f),
            new ParamSpec("wobble", 0f, 1f, 0.2f)
        ],
        [Widener] =
        [
            new ParamSpec("width", 0f, 2f, 1.4f),
            new ParamSpec("haas", 0f, 1f, 0.35f),
            new ParamSpec("delayMs", 1f, 40f, 18f),
            new ParamSpec("bassMonoHz", 0f, 500f, 120f)
        ],
        [Freeze] =
        [
            // A LATCH (rising edge captures, 0 releases) — a preset must NEVER
            // ship capture = 1; at apply time the buffer holds whatever was in
            // the room. Capture is a performance gesture.
            new ParamSpec("capture", 0f, 1f, 0f),
            new ParamSpec("lengthMs", 120f, 2000f, 500f),
            new ParamSpec("level", 0f, 1f, 0.7f),
            new ParamSpec("fadeMs", 5f, 2000f, 80f),
            new ParamSpec("toneHz", 500f, 12000f, 6000f)
        ],
        [Subharmonic] =
        [
            new ParamSpec("sub1", 0f, 1f, 0.7f),
            new ParamSpec("sub2", 0f, 1f, 0f),
            new ParamSpec("waveform", 0f, 2f, 0f),
            new ParamSpec("cutoffHz", 100f, 4000f, 900f),
            new ParamSpec("resonance", 0f, 1f, 0.25f),
            new ParamSpec("envAmount", 0f, 1f, 0.4f),
            new ParamSpec("attackMs", 0f, 300f, 5f),
            new ParamSpec("dry", 0f, 1f, 1f)
        ],
        [FilterSeq] =
        [
            new ParamSpec("rateHz", 0.1f, 16f, 4f),
            new ParamSpec("steps", 2f, 16f, 8f),
            new ParamSpec("mode", 0f, 3f, 0f),
            new ParamSpec("trigger", 0f, 1f, 0f),
            new ParamSpec("rangeLoHz", 100f, 1000f, 200f),
            new ParamSpec("rangeHiHz", 500f, 6000f, 3000f),
            new ParamSpec("resonance", 0f, 1f, 0.6f),
            new ParamSpec("glide", 0f, 1f, 0.15f),
            new ParamSpec("filterType", 0f, 2f, 0f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [Feedbacker] =
        [
            new ParamSpec("amount", 0f, 1f, 0.5f),
            new ParamSpec("riseMs", 10f, 3000f, 400f),
            new ParamSpec("harmonic", 0f, 3f, 1f),
            new ParamSpec("resonance", 0f, 1f, 0.7f),
            new ParamSpec("thresholdDb", -60f, -10f, -40f),
            // The howl's level — it is ADDED to the dry, never crossfaded with it.
            new ParamSpec("mix", 0f, 1f, 0.5f)
        ],
        [Resonator] =
        [
            new ParamSpec("key", 0f, 11f, 0f),
            new ParamSpec("chord", 0f, 4f, 0f),
            new ParamSpec("octave", 0f, 4f, 2f),
            new ParamSpec("decay", 0f, 1f, 0.7f),
            new ParamSpec("damping", 0f, 1f, 0.5f),
            new ParamSpec("spread", 0f, 1f, 0.5f),
            new ParamSpec("mix", 0f, 1f, 0.5f)
        ],
        [TapeStop] =
        [
            new ParamSpec("trigger", 0f, 1f, 0f),
            new ParamSpec("stopMs", 50f, 3000f, 600f),
            new ParamSpec("startMs", 50f, 3000f, 400f),
            new ParamSpec("curve", 0f, 1f, 0.5f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [Glitch] =
        [
            new ParamSpec("mode", 0f, 1f, 0f),
            new ParamSpec("trigger", 0f, 1f, 0f),
            new ParamSpec("sliceMs", 20f, 1000f, 250f),
            new ParamSpec("repeats", 1f, 16f, 4f),
            new ParamSpec("chance", 0f, 1f, 0.35f),
            new ParamSpec("decay", 0f, 1f, 0f),
            new ParamSpec("reverse", 0f, 1f, 0f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],

        // DynamicsGear.cpp
        [Limiter] =
        [
            new ParamSpec("ceilingDb", -20f, 0f, -1f),
            new ParamSpec("releaseMs", 5f, 500f, 80f),
            new ParamSpec("lookaheadMs", 0f, 5f, 1f),
            new ParamSpec("gainDb", -12f, 12f, 0f)
        ],
        [Transient] =
        [
            new ParamSpec("attackDb", -12f, 12f, 0f),
            new ParamSpec("sustainDb", -12f, 12f, 0f),
            new ParamSpec("sensitivity", 0f, 1f, 0.5f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [Multiband] =
        [
            new ParamSpec("lowXoverHz", 60f, 500f, 150f),
            new ParamSpec("highXoverHz", 1000f, 8000f, 2500f),
            new ParamSpec("lowThresholdDb", -60f, 0f, -20f),
            new ParamSpec("lowRatio", 1f, 20f, 3f),
            new ParamSpec("lowGainDb", -12f, 12f, 0f),
            new ParamSpec("midThresholdDb", -60f, 0f, -20f),
            new ParamSpec("midRatio", 1f, 20f, 3f),
            new ParamSpec("midGainDb", -12f, 12f, 0f),
            new ParamSpec("highThresholdDb", -60f, 0f, -20f),
            new ParamSpec("highRatio", 1f, 20f, 3f),
            new ParamSpec("highGainDb", -12f, 12f, 0f),
            new ParamSpec("attackMs", 0.1f, 100f, 10f),
            new ParamSpec("releaseMs", 5f, 1000f, 120f)
        ],
        [Expander] =
        [
            new ParamSpec("thresholdDb", -80f, 0f, -40f),
            new ParamSpec("ratio", 1f, 10f, 2f),
            new ParamSpec("attackMs", 0.1f, 100f, 10f),
            new ParamSpec("releaseMs", 5f, 1000f, 120f),
            new ParamSpec("rangeDb", 0f, 60f, 30f)
        ],
        [OptoComp] =
        [
            new ParamSpec("peakReduction", 0f, 1f, 0.4f),
            new ParamSpec("gainDb", 0f, 24f, 0f),
            new ParamSpec("limit", 0f, 1f, 0f),
            new ParamSpec("emphasis", 0f, 1f, 0.3f)
        ],
        [Ducker] =
        [
            new ParamSpec("thresholdDb", -60f, 0f, -30f),
            new ParamSpec("duckDb", 0f, 40f, 12f),
            new ParamSpec("attackMs", 1f, 200f, 10f),
            new ParamSpec("holdMs", 0f, 500f, 50f),
            new ParamSpec("releaseMs", 10f, 2000f, 300f)
        ],
        // DriveGear.cpp
        [Rat] =
        [
            new ParamSpec("distortion", 0f, 1f, 0.5f),
            new ParamSpec("filter", 0f, 1f, 0.4f),
            new ParamSpec("volume", 0f, 1f, 0.6f),
            new ParamSpec("clip", 0f, 2f, 0f)
        ],
        [Ds1] =
        [
            new ParamSpec("dist", 0f, 1f, 0.5f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("level", 0f, 1f, 0.6f)
        ],
        [Hm2] =
        [
            new ParamSpec("dist", 0f, 1f, 0.7f),
            new ParamSpec("lowDb", 0f, 12f, 10f),
            new ParamSpec("highDb", 0f, 12f, 10f),
            new ParamSpec("level", 0f, 1f, 0.5f)
        ],
        [TapeSat] =
        [
            new ParamSpec("drive", 0f, 1f, 0.3f),
            new ParamSpec("bias", 0f, 1f, 0.5f),
            new ParamSpec("hfRolloffKHz", 4f, 20f, 12f),
            new ParamSpec("hiss", 0f, 1f, 0f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [BassOd] =
        [
            new ParamSpec("drive", 0f, 1f, 0.4f),
            new ParamSpec("blend", 0f, 1f, 0.5f),
            new ParamSpec("bass", -12f, 12f, 0f),
            new ParamSpec("treble", -12f, 12f, 0f),
            new ParamSpec("level", 0f, 1f, 0.6f)
        ],
        [TubePre] =
        [
            new ParamSpec("gain", 0f, 1f, 0.4f),
            new ParamSpec("sag", 0f, 1f, 0.3f),
            new ParamSpec("bright", 0f, 1f, 0f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("level", 0f, 1f, 0.6f),
            new ParamSpec("cabSim", 0f, 1f, 0f)
        ],
        [OctFuzz] =
        [
            new ParamSpec("fuzz", 0f, 1f, 0.7f),
            new ParamSpec("octave", 0f, 1f, 0.6f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("volume", 0f, 1f, 0.5f)
        ],
        // AmpCabGear.cpp
        [BassAmp] =
        [
            new ParamSpec("model", 0f, 3f, 0f),
            new ParamSpec("gain", 0f, 1f, 0.4f),
            // +/-20 dB: the SVT-CL's treble / mid cut 20 dB.
            new ParamSpec("bass", -20f, 20f, 0f),
            new ParamSpec("lowMid", -20f, 20f, 0f),
            new ParamSpec("hiMid", -20f, 20f, 0f),
            new ParamSpec("treble", -20f, 20f, 0f),
            new ParamSpec("drive", 0f, 1f, 0.2f),
            new ParamSpec("compress", 0f, 1f, 0.3f),
            new ParamSpec("master", 0f, 1f, 0.6f),
            // The sweepable mid (Q 1), the two mid bells'
            // octave offsets and Ampeg's Ultra Lo / Ultra Hi (dcdsp_bassamp.h).
            new ParamSpec("mid", -20f, 20f, 0f),
            new ParamSpec("midHz", 100f, 5000f, 800f),
            new ParamSpec("midQ", 0.3f, 8f, 1f),
            new ParamSpec("lowMidOct", -4f, 4f, 0f),
            new ParamSpec("hiMidOct", -4f, 4f, 0f),
            new ParamSpec("lowShift", 0f, 1f, 0f),
            new ParamSpec("highShift", 0f, 1f, 0f)
        ],
        [AcPre] =
        [
            new ParamSpec("notchHz", 60f, 500f, 120f),
            new ParamSpec("notchDepthDb", 0f, 30f, 0f),
            new ParamSpec("notchQ", 2f, 30f, 10f),
            new ParamSpec("phase", 0f, 1f, 0f),
            new ParamSpec("bass", -12f, 12f, 0f),
            new ParamSpec("mid", -12f, 12f, 0f),
            new ParamSpec("treble", -12f, 12f, 0f),
            new ParamSpec("presence", 0f, 1f, 0.3f),
            new ParamSpec("piezoFix", 0f, 1f, 0f),
            new ParamSpec("level", 0f, 1f, 0.7f)
        ],
        [PowerAmp] =
        [
            new ParamSpec("tubes", 0f, 4f, 2f),
            new ParamSpec("drive", 0f, 1f, 0.3f),
            new ParamSpec("sag", 0f, 1f, 0.3f),
            new ParamSpec("presence", 0f, 1f, 0.5f),
            new ParamSpec("resonance", 0f, 1f, 0.5f),
            new ParamSpec("damping", 0f, 1f, 0.5f),
            new ParamSpec("level", 0f, 1f, 0.6f)
        ],
        [MicPos] =
        [
            // Mic 4 = the tube condenser, and the off-axis angle.
            new ParamSpec("mic", 0f, 4f, 0f),
            new ParamSpec("position", 0f, 1f, 0.3f),
            new ParamSpec("distanceCm", 0f, 30f, 5f),
            new ParamSpec("room", 0f, 1f, 0f),
            new ParamSpec("blend2", 0f, 1f, 0f),
            new ParamSpec("level", 0f, 1f, 0.7f),
            new ParamSpec("angleDeg", 0f, 60f, 0f)
        ],
        // FilterGear.cpp
        [Svf] =
        [
            new ParamSpec("mode", 0f, 3f, 0f),
            new ParamSpec("cutoffHz", 20f, 20000f, 1000f),
            new ParamSpec("resonance", 0f, 1f, 0.3f),
            new ParamSpec("drive", 0f, 1f, 0f),
            new ParamSpec("source", 0f, 2f, 0f),
            new ParamSpec("lfoRateHz", 0.05f, 20f, 1f),
            new ParamSpec("lfoDepth", 0f, 1f, 0.5f),
            new ParamSpec("envSens", 0f, 1f, 0.5f),
            new ParamSpec("envAttackMs", 1f, 200f, 10f),
            new ParamSpec("envReleaseMs", 10f, 2000f, 200f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [Comb] =
        [
            new ParamSpec("freqHz", 20f, 5000f, 220f),
            new ParamSpec("feedback", -0.98f, 0.98f, 0.7f),
            new ParamSpec("damping", 0f, 1f, 0.3f),
            new ParamSpec("polarity", 0f, 1f, 1f),
            new ParamSpec("mix", 0f, 1f, 0.5f)
        ],
        [Vocoder] =
        [
            new ParamSpec("bands", 8f, 24f, 16f),
            new ParamSpec("carrier", 0f, 3f, 1f),
            new ParamSpec("pitchHz", 30f, 500f, 110f),
            new ParamSpec("attackMs", 1f, 100f, 5f),
            new ParamSpec("releaseMs", 5f, 500f, 40f),
            new ParamSpec("formantShift", -12f, 12f, 0f),
            new ParamSpec("emphasis", 0f, 1f, 0.5f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [Talkbox] =
        [
            new ParamSpec("vowel", 0f, 1f, 0.5f),
            new ParamSpec("sweep", 0f, 2f, 0f),
            new ParamSpec("lfoRateHz", 0.05f, 10f, 0.5f),
            new ParamSpec("envSens", 0f, 1f, 0.5f),
            new ParamSpec("resonance", 0f, 1f, 0.6f),
            new ParamSpec("drive", 0f, 1f, 0.3f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [Tilt] =
        [
            new ParamSpec("tilt", -12f, 12f, 0f),
            new ParamSpec("pivotHz", 200f, 5000f, 1000f),
            new ParamSpec("level", -12f, 12f, 0f)
        ],
        // ModulationGear.cpp
        [TzFlanger] =
        [
            new ParamSpec("rateHz", 0.02f, 10f, 0.3f),
            new ParamSpec("depthMs", 0.1f, 10f, 3f),
            new ParamSpec("manualMs", 0f, 10f, 3f),
            new ParamSpec("feedback", -0.95f, 0.95f, 0.3f),
            new ParamSpec("mix", 0f, 1f, 0.5f),
            new ParamSpec("shape", 0f, 1f, 0f)
        ],
        [Barberpole] =
        [
            new ParamSpec("rateHz", 0.05f, 5f, 0.2f),
            new ParamSpec("stages", 2f, 12f, 6f),
            new ParamSpec("depth", 0f, 1f, 0.8f),
            new ParamSpec("feedback", -0.9f, 0.9f, 0.3f),
            new ParamSpec("direction", 0f, 1f, 0f),
            new ParamSpec("mix", 0f, 1f, 0.5f)
        ],
        [AutoPan] =
        [
            new ParamSpec("rateHz", 0.05f, 10f, 1f),
            new ParamSpec("depth", 0f, 1f, 1f),
            new ParamSpec("shape", 0f, 2f, 0f),
            new ParamSpec("phaseDeg", 0f, 180f, 90f),
            new ParamSpec("law", 0f, 1f, 0f)
        ],
        [Ensemble] =
        [
            new ParamSpec("rateHz", 0.1f, 2f, 0.6f),
            new ParamSpec("fastRateHz", 3f, 8f, 5.5f),
            new ParamSpec("depth", 0f, 1f, 0.6f),
            new ParamSpec("voices", 2f, 3f, 3f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("mix", 0f, 1f, 0.6f)
        ],
        [FreqShift] =
        [
            new ParamSpec("shiftHz", -1000f, 1000f, 5f),
            new ParamSpec("mix", 0f, 1f, 0.5f),
            new ParamSpec("feedback", 0f, 0.9f, 0f),
            new ParamSpec("direction", 0f, 2f, 0f)
        ],
        // PitchGear.cpp
        [Detune] =
        [
            new ParamSpec("cents", 0f, 50f, 12f),
            new ParamSpec("spreadMs", 0f, 30f, 8f),
            new ParamSpec("width", 0f, 1f, 1f),
            new ParamSpec("mix", 0f, 1f, 0.5f),
            new ParamSpec("lowLatency", 0f, 1f, 0f)
        ],
        [PitchCorrect] =
        [
            new ParamSpec("scale", 0f, 4f, 0f),
            new ParamSpec("key", 0f, 11f, 0f),
            new ParamSpec("speedMs", 0f, 500f, 50f),
            new ParamSpec("amount", 0f, 1f, 1f),
            new ParamSpec("humanize", 0f, 1f, 0f),
            new ParamSpec("formant", 0f, 1f, 0f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        [TwelveString] =
        [
            new ParamSpec("octaveLevel", 0f, 1f, 0.6f),
            new ParamSpec("unisonLevel", 0f, 1f, 0.6f),
            new ParamSpec("detuneCents", 0f, 25f, 8f),
            new ParamSpec("delayMs", 0f, 20f, 6f),
            new ParamSpec("brightness", 0f, 1f, 0.5f),
            new ParamSpec("mix", 0f, 1f, 0.6f)
        ],
        [BassSynth] =
        [
            new ParamSpec("waveform", 0f, 3f, 0f),
            new ParamSpec("octave", 0f, 2f, 1f),
            new ParamSpec("cutoffHz", 40f, 8000f, 800f),
            new ParamSpec("resonance", 0f, 1f, 0.4f),
            new ParamSpec("envAmount", 0f, 1f, 0.6f),
            new ParamSpec("decayMs", 10f, 2000f, 300f),
            new ParamSpec("glideMs", 0f, 300f, 20f),
            new ParamSpec("dryMix", 0f, 1f, 0.3f),
            new ParamSpec("synthMix", 0f, 1f, 0.7f)
        ],
        // DelayGear.cpp
        [Multitap] =
        [
            new ParamSpec("taps", 1f, 4f, 3f),
            new ParamSpec("timeMs", 10f, 2000f, 375f),
            new ParamSpec("spread", 0f, 3f, 0f),
            new ParamSpec("decay", 0f, 1f, 0.6f),
            new ParamSpec("pan", 0f, 1f, 0.7f),
            new ParamSpec("feedback", 0f, 0.9f, 0.2f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("mix", 0f, 1f, 0.4f)
        ],
        [DuckDelay] =
        [
            new ParamSpec("timeMs", 10f, 2000f, 400f),
            new ParamSpec("feedback", 0f, 0.9f, 0.35f),
            new ParamSpec("duckDb", 0f, 40f, 12f),
            new ParamSpec("sensitivity", 0f, 1f, 0.5f),
            new ParamSpec("releaseMs", 20f, 2000f, 300f),
            new ParamSpec("tone", 0f, 1f, 0.5f),
            new ParamSpec("mix", 0f, 1f, 0.4f)
        ],
        [BbdDelay] =
        [
            new ParamSpec("timeMs", 20f, 600f, 300f),
            new ParamSpec("feedback", 0f, 1f, 0.4f),
            new ParamSpec("modRateHz", 0.1f, 5f, 0.5f),
            new ParamSpec("modDepth", 0f, 1f, 0.1f),
            new ParamSpec("age", 0f, 1f, 0.3f),
            new ParamSpec("mix", 0f, 1f, 0.4f)
        ],
        [Granular] =
        [
            new ParamSpec("grainMs", 10f, 500f, 80f),
            new ParamSpec("density", 1f, 32f, 8f),
            new ParamSpec("pitchSemitones", -12f, 12f, 0f),
            new ParamSpec("spray", 0f, 1f, 0.3f),
            new ParamSpec("feedback", 0f, 0.9f, 0.2f),
            new ParamSpec("mix", 0f, 1f, 0.5f),
            new ParamSpec("freeze", 0f, 1f, 0f)
        ],
        [RhythmDelay] =
        [
            new ParamSpec("timeMs", 10f, 2000f, 500f),
            new ParamSpec("feedback", 0f, 0.9f, 0.4f),
            new ParamSpec("pattern", 0f, 7f, 0f),
            new ParamSpec("stepMs", 50f, 1000f, 125f),
            new ParamSpec("gateDepth", 0f, 1f, 1f),
            new ParamSpec("gateShape", 0f, 1f, 0.2f),
            new ParamSpec("mix", 0f, 1f, 0.5f)
        ],
        // ReverbGear.cpp
        [HallReverb] =
        [
            new ParamSpec("space", 0f, 3f, 2f),
            new ParamSpec("decayS", 0.2f, 20f, 2.5f),
            new ParamSpec("predelayMs", 0f, 200f, 20f),
            new ParamSpec("dampingKHz", 1f, 16f, 6f),
            new ParamSpec("lowCutHz", 20f, 500f, 80f),
            new ParamSpec("erLevel", 0f, 1f, 0.5f),
            new ParamSpec("diffusion", 0f, 1f, 0.7f),
            new ParamSpec("width", 0f, 1f, 1f),
            new ParamSpec("mix", 0f, 1f, 0.3f)
        ],
        [ConvReverb] =
        [
            new ParamSpec("predelayMs", 0f, 200f, 0f),
            new ParamSpec("lengthPct", 10f, 100f, 100f),
            new ParamSpec("lowCutHz", 20f, 500f, 40f),
            new ParamSpec("highCutKHz", 2f, 20f, 16f),
            new ParamSpec("mix", 0f, 1f, 0.3f)
        ],
        [GatedReverb] =
        [
            new ParamSpec("decayS", 0.5f, 10f, 3f),
            new ParamSpec("gateMs", 20f, 1000f, 250f),
            new ParamSpec("thresholdDb", -60f, 0f, -30f),
            new ParamSpec("releaseMs", 5f, 200f, 20f),
            new ParamSpec("predelayMs", 0f, 100f, 0f),
            new ParamSpec("dampingKHz", 1f, 16f, 8f),
            new ParamSpec("mix", 0f, 1f, 0.5f)
        ],
        [ReverseReverb] =
        [
            new ParamSpec("decayS", 0.5f, 5f, 2f),
            new ParamSpec("windowMs", 100f, 2000f, 800f),
            new ParamSpec("predelayMs", 0f, 200f, 0f),
            new ParamSpec("dampingKHz", 1f, 16f, 8f),
            new ParamSpec("mix", 0f, 1f, 0.5f),
            new ParamSpec("swellShape", 0f, 1f, 0.5f)
        ],
        // UtilityGear.cpp
        [Volume] =
        [
            new ParamSpec("position", 0f, 1f, 1f),
            new ParamSpec("curve", 0f, 3f, 0f),
            new ParamSpec("minDb", -90f, 0f, -90f),
            new ParamSpec("maxDb", -12f, 12f, 0f),
            new ParamSpec("smoothMs", 1f, 200f, 20f)
        ],
        [MidSide] =
        [
            new ParamSpec("midDb", -24f, 12f, 0f),
            new ParamSpec("sideDb", -24f, 12f, 0f),
            new ParamSpec("width", 0f, 2f, 1f),
            new ParamSpec("midPhase", 0f, 1f, 0f),
            new ParamSpec("sidePhase", 0f, 1f, 0f),
            new ParamSpec("monoBelowHz", 0f, 500f, 0f),
            new ParamSpec("swap", 0f, 1f, 0f)
        ],
        [Looper] =
        [
            new ParamSpec("state", 0f, 3f, 0f),
            new ParamSpec("maxLoopS", 1f, 60f, 30f),
            new ParamSpec("loopLevel", 0f, 1f, 1f),
            new ParamSpec("overdubDecay", 0f, 1f, 1f),
            new ParamSpec("reverse", 0f, 1f, 0f),
            new ParamSpec("halfSpeed", 0f, 1f, 0f),
            new ParamSpec("undo", 0f, 1f, 0f),
            new ParamSpec("mix", 0f, 1f, 1f)
        ],
        // `model` is a row of the generated table (RsAmpChains.g.cs, 63 amps);
        // k1..k10 are the 0..1 POSITIONS of that amp's own knobs, in its
        // manifest's order (RsAmpModel.Knobs).
        [RsAmpChain] =
        [
            new ParamSpec("model", 0f, 62f, 0f),
            new ParamSpec("k1", 0f, 1f, 0.5f),
            new ParamSpec("k2", 0f, 1f, 0.5f),
            new ParamSpec("k3", 0f, 1f, 0.5f),
            new ParamSpec("k4", 0f, 1f, 0.5f),
            new ParamSpec("k5", 0f, 1f, 0.5f),
            new ParamSpec("k6", 0f, 1f, 0.5f),
            new ParamSpec("k7", 0f, 1f, 0.5f),
            new ParamSpec("k8", 0f, 1f, 0.5f),
            new ParamSpec("k9", 0f, 1f, 0.5f),
            new ParamSpec("k10", 0f, 1f, 0.5f),
            // Output trim; the loudness levelling spills into it past the
            // trailing gain's ±24 dB (RsToneLevelling).
            new ParamSpec("level", -24f, 24f, 0f)
        ]
    };

    /// <summary>Every module type id in the catalogue — the authoritative managed
    /// mirror of the native module set (e.g. the pedalboard's visual-mapping
    /// coverage test iterates it).</summary>
    public static IReadOnlyCollection<string> ModuleTypeIds => Modules.Keys;

    /// <summary>Every module type in the managed mirror with its param specs —
    /// the enumeration surface for the catalogue-consistency tests.</summary>
    public static IEnumerable<(string ModuleTypeId, IReadOnlyList<ParamSpec> Params)> AllModules
        => Modules.Select(kv => (kv.Key, (IReadOnlyList<ParamSpec>)kv.Value));

    public static bool TryGetParam(string moduleTypeId, string paramId, out ParamSpec spec)
    {
        spec = default;
        if (!Modules.TryGetValue(moduleTypeId, out var specs)) return false;
        foreach (var s in specs)
        {
            if (s.Id == paramId)
            {
                spec = s;
                return true;
            }
        }

        return false;
    }

    /// <summary>Clamps a value to the target param's declared range (defensive —
    /// the converter never emits out-of-range params). Unknown module/param
    /// passes through unchanged.</summary>
    public static float Clamp(string moduleTypeId, string paramId, float value)
        => TryGetParam(moduleTypeId, paramId, out var spec)
            ? Math.Clamp(value, spec.Min, spec.Max)
            : value;

    /// <summary>Returns a preset in which every module param the author did NOT
    /// specify is pinned to its catalogue default. This guards the preset-apply
    /// seam against value inheritance: <c>DcRigApplier</c> reuses same-type nodes
    /// in place, so a sparse preset applied after another tone would otherwise
    /// keep the previous tone's values on the omitted params. Unknown module
    /// types / extra param ids pass through unchanged; the input is never mutated
    /// and is returned as-is when already fully specified.</summary>
    public static DcRigPreset FillDefaults(DcRigPreset preset)
    {
        List<DcRigModule>? filled = null;
        for (var i = 0; i < preset.Modules.Count; i++)
        {
            var mod = preset.Modules[i];
            if (!Modules.TryGetValue(mod.ModuleTypeId, out var specs)) continue;

            Dictionary<string, float>? expanded = null;
            foreach (var spec in specs)
            {
                if (mod.Params.ContainsKey(spec.Id)) continue;
                expanded ??= new Dictionary<string, float>(mod.Params, StringComparer.Ordinal);
                expanded[spec.Id] = spec.Default;
            }

            if (expanded is null) continue;
            filled ??= [.. preset.Modules];
            filled[i] = mod with { Params = expanded };
        }

        return filled is null ? preset : preset with { Modules = filled };
    }
}
