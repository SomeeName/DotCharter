namespace DotCharter.Tone.Ai;

/// <summary>
///     One catalogue row as the model sees it: the engine's type id, a display
///     name, a broad category and a ONE-LINE note saying what the pedal actually
///     is. The note is the whole point — a model handed a bare id list writes
///     <c>rat</c> where it means a boutique overdrive, and a model told
///     "RAT-style hard-clipping distortion, filter knob darker clockwise" does
///     not.
/// </summary>
public readonly record struct AiGearNote(string Id, string Name, string Category, string Note);

/// <summary>
///     The prompt's gear glossary: every designable module in
///     <c>DcRigTargetCatalog</c> with a display name, a category and a ten-word
///     description.
///
///     <para>
///         <b>Why this is a hand-written table and not the live catalogue.</b>
///         <c>IDspChainService</c>'s catalogue carries display names and native
///         categories, but it needs an OPEN ENGINE — and the prompt is built in
///         the Settings page, in the eval harness and in every unit test, none of
///         which has one. The ranges still come from <c>DcRigTargetCatalog</c>
///         (the managed mirror the validator clamps against); only the prose
///         lives here.
///     </para>
///     <para>
///         <b>It must cover the catalogue exactly.</b>
///         <c>ToneDesignPromptTests</c> fails if the catalogue gains a module
///         this table does not describe, or if this table names one the catalogue
///         does not have — a missing row is a module the model can never reach,
///         and a stale row is a module it will confidently hallucinate.
///     </para>
/// </summary>
public static class AiGearNotes
{
    /// <summary>Catalogue ids deliberately kept OUT of the prompt: v1 designs
    /// SERIES chains only (see <see cref="ToneDraft" />), and a splitter without a
    /// merger — or either one in a chain the draft cannot express branch tags for —
    /// is a rig the engine runs differently from what the model described.</summary>
    public static IReadOnlySet<string> SeriesExcluded { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "splitter", "merger" };

    /// <summary>Catalogue ids the designer never offers, with no gear note: `rsamp`
    /// is Rocksmith's own amp chains, whose ten knobs mean a different
    /// control on each of 63 amps — a map no prompt line can carry. The RS converter
    /// writes it; a design asks for an `amp` / `bassamp` / `tubepre` archetype
    /// (<see cref="RsConverterIdiom" /> says which stands for which RS amp).</summary>
    public static IReadOnlySet<string> NotDesignable { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "rsamp" };

    /// <summary>The neural-capture module's catalogue id. It is the one module whose
    /// prompt note and rule depend on the PLAYER'S machine — see
    /// <see cref="NamCaptureCatalog" /> — so the id is named once rather than spelled
    /// at each of the four sites that key off it.</summary>
    public const string NamAmpId = "namamp";

    /// <summary>The <see cref="NamAmpId" /> note used when the player has NO captures
    /// at all. The table's own note points at a capture list, and a prompt that
    /// points at an empty list is how a design comes back naming a capture nobody
    /// has; with none installed the module is simply unusable and the prompt says
    /// so.</summary>
    public const string NamAmpNoteWithoutCaptures =
        "Neural capture player; needs a captured model file, so avoid designing with it.";

    /// <summary>Every designable module, in the order the prompt lists them
    /// (roughly signal order, which is also the order the rules ask for).</summary>
    public static IReadOnlyList<AiGearNote> All { get; } =
    [
        // ---- dynamics ------------------------------------------------------
        new("gate", "Noise Gate", "dynamics", "Silences the signal below a threshold; kills high-gain hiss."),
        new("noisereducer", "Noise Reducer", "dynamics", "Smoother program-dependent gate, less chatter than a hard gate."),
        new("compressor", "Compressor", "dynamics", "Studio compressor; evens dynamics, adds sustain and attack control."),
        new("optocomp", "Opto Compressor", "dynamics", "Slow optical squash, very musical; classic clean and country glue."),
        new("limiter", "Limiter", "dynamics", "Brick-wall ceiling; stops peaks, no tone shaping of its own."),
        new("expander", "Expander", "dynamics", "Downward expander; quiet parts get quieter without a hard gate."),
        new("transient", "Transient Shaper", "dynamics", "Boosts or softens pick attack and note sustain independently."),
        new("multiband", "Multiband Compressor", "dynamics", "Three-band compression; tames boom or fizz without squashing the rest."),
        new("ducker", "Ducker", "dynamics", "Ducks the guitar against a key signal; sidechain-capable pumping effect."),
        new("sustainer", "Sustainer", "dynamics", "Heavy upward compression for infinite singing sustain on leads."),

        // ---- drive ---------------------------------------------------------
        new("boost", "Clean Boost", "drive", "Transparent level lift; pushes an amp harder without its own colour."),
        new("overdrive", "Overdrive", "drive", "Tube-Screamer-style soft clipping with a mid hump; classic amp pusher."),
        new("klon", "Transparent Drive", "drive", "Klon-style near-transparent drive; keeps clean lows, adds gritty highs."),
        new("bassod", "Bass Overdrive", "drive", "Bass overdrive that keeps the low end clean under the grit."),
        new("tubepre", "Tube Preamp", "drive", "Valve preamp warmth and light saturation; a DI-friendly front end."),
        new("tapesat", "Tape Saturation", "drive", "Soft tape-style compression and harmonics; glue rather than distortion."),
        new("distortion", "Distortion", "drive", "General hard-clipping distortion with tone control; tight and aggressive."),
        new("rat", "Rodent Distortion", "drive", "RAT-style hard-clipping distortion; filter knob gets darker clockwise."),
        new("ds1", "Orange Distortion", "drive", "DS-1-style buzzy mid-scooped distortion; classic punk and grunge."),
        new("hm2", "Chainsaw Distortion", "drive", "HM-2-style all-knobs-up buzzsaw; Swedish death-metal signature."),
        new("fuzz", "Fuzz", "drive", "Fuzz Face-style splattery vintage fuzz; cleans up with guitar volume."),
        new("bigmuff", "Muff Fuzz", "drive", "Big-Muff-style scooped sustaining fuzz wall; doom and lead sustain."),
        new("octfuzz", "Octave Fuzz", "drive", "Octave-up fuzz; ring-modulated screaming leads above the twelfth fret."),

        // ---- amps ----------------------------------------------------------
        new("amp", "Guitar Amp", "amp", "Modelled guitar amp; voicing selects clean, crunch or high-gain."),
        new(NamAmpId, "Neural Amp Capture", "amp", "Plays a neural amp capture; choose one from the capture list."),
        new("bassamp", "Bass Amp", "amp", "Modelled bass amp with blend and grit; use for bass requests."),
        new("acpre", "Acoustic Preamp", "amp", "Acoustic preamp with notch and body control; DI, no cab needed."),
        new("poweramp", "Power Amp", "amp", "Power-stage saturation and sag; follows a preamp, before the cab."),

        // ---- cab / mic -----------------------------------------------------
        new("cab", "Speaker Cabinet", "cab", "Cabinet simulation; put one after any amp that is not a DI preamp."),
        new("micpos", "Mic Position", "cab", "Mic placement shaping after a cab; cap-edge brightness and room distance."),
        new("acousticsim", "Acoustic Simulator", "cab", "Turns an electric into a plausible acoustic; a body impulse response."),

        // ---- eq / filter ---------------------------------------------------
        new("eq", "Parametric EQ", "eq", "Three-band parametric EQ with sweepable frequencies; surgical tone shaping."),
        new("graphiceq", "Graphic EQ", "eq", "Ten-band graphic EQ; the classic mid-scoop or mid-boost tool."),
        new("tilt", "Tilt EQ", "eq", "One knob tilts the whole spectrum darker or brighter."),
        new("svf", "State Variable Filter", "filter", "Resonant low/high/band-pass filter; static or a sweepable synth-like tone."),
        new("wah", "Wah", "filter", "Wah pedal at a fixed or swept position; the classic vocal sweep."),
        new("autowah", "Auto Wah", "filter", "Wah swept by an LFO; hands-free rhythmic filter movement."),
        new("envfilter", "Envelope Filter", "filter", "Filter swept by picking dynamics; funk quack and synth-bass squelch."),
        new("formant", "Formant Filter", "filter", "Vowel filter morphing between ah/ee/oo shapes; talking guitar."),
        new("talkbox", "Talk Box", "filter", "Talk-box vowel resonance driven by the signal; a vocal-tract filter."),
        new("vocoder", "Vocoder", "filter", "Vocoder-style spectral shaping; robotic, carrier-driven vowels."),
        new("comb", "Comb Filter", "filter", "Short-delay comb; metallic hollow resonance, tuneable pitch."),

        // ---- modulation ----------------------------------------------------
        new("chorus", "Chorus", "modulation", "Detuned doubling; thickens cleans, classic eighties shimmer."),
        new("ensemble", "Ensemble Chorus", "modulation", "Multi-voice string-ensemble chorus; wide and lush."),
        new("dimension", "Dimension", "modulation", "Subtle fixed-mode stereo chorus; widens without obvious wobble."),
        new("doubler", "Doubler", "modulation", "Short detuned delay pair; fakes a double-tracked take."),
        new("widener", "Stereo Widener", "modulation", "Widens the stereo image; bass stays mono below a corner."),
        new("flanger", "Flanger", "modulation", "Jet-plane sweep from a modulated short delay with feedback."),
        new("tzflanger", "Through-Zero Flanger", "modulation", "Through-zero flanger; passes the null point for tape-flange whoosh."),
        new("phaser", "Phaser", "modulation", "All-pass notch sweep; swirly funk and seventies rock."),
        new("univibe", "Uni-Vibe", "modulation", "Photocell phase wobble; watery Hendrix-style throb."),
        new("rotary", "Rotary Speaker", "modulation", "Leslie-style rotating horn and drum; doppler swirl, slow or fast."),
        new("tremolo", "Tremolo", "modulation", "Amplitude wobble; surf and blackface pulsing."),
        new("harmtremolo", "Harmonic Tremolo", "modulation", "Tremolo that pans highs against lows; thicker than plain tremolo."),
        new("autopan", "Auto Pan", "modulation", "Sweeps the signal across the stereo field with an LFO."),
        new("vibrato", "Vibrato", "modulation", "True pitch vibrato, no dry signal; seasick warble."),
        new("ringmod", "Ring Modulator", "modulation", "Multiplies against an oscillator; clangorous inharmonic bell tones."),
        new("freqshift", "Frequency Shifter", "modulation", "Shifts every partial by a fixed hertz amount; inharmonic and alien."),
        new("barberpole", "Barberpole Phaser", "modulation", "Endlessly rising or falling phase sweep; a Shepard-tone illusion."),

        // ---- pitch ---------------------------------------------------------
        new("octaver", "Octaver", "pitch", "Adds an octave below and/or above; monophonic and tight."),
        new("subharmonic", "Subharmonic", "pitch", "Synthesised sub-octave weight under the note; earth-moving lows."),
        new("harmonizer", "Harmonizer", "pitch", "Adds a diatonic harmony voice in a chosen key and interval."),
        new("whammy", "Whammy", "pitch", "Big pedal-swept pitch shift; dive bombs and octave-up leads."),
        new("detune", "Detune", "pitch", "Slightly detuned copies; thickens without an audible chorus rate."),
        new("pitchcorrect", "Pitch Correct", "pitch", "Snaps pitch to a scale; robotic or gentle intonation fixing."),
        new("twelvestring", "12-String", "pitch", "Adds octave-up companion strings; jangly twelve-string shimmer."),
        new("basssynth", "Bass Synth", "pitch", "Tracks the note and plays a synth voice under it; bass-synth growl."),
        new("miku", "Vocal Synth", "pitch", "Formant vocal synth that sings the played notes; needs lyric text."),
        new("mikustomp", "Vocal Stomp", "pitch", "Faithful singing-voice stomp with phrase slots; needs lyric text."),

        // ---- delay ---------------------------------------------------------
        new("delay", "Digital Delay", "delay", "Clean digital delay with feedback; the default repeat."),
        new("slapback", "Slapback", "delay", "One short bright repeat; rockabilly and country snap."),
        new("tapedelay", "Tape Delay", "delay", "Warm darkening repeats with wow and flutter; vintage echo."),
        new("bbddelay", "Analog Delay", "delay", "Bucket-brigade analog delay; each repeat darker and softer."),
        new("multitap", "Multi-Tap Delay", "delay", "Several independently timed and panned taps; rhythmic patterns."),
        new("rhythmdelay", "Rhythm Delay", "delay", "Pattern-gated delay locked to a step grid; stuttering rhythms."),
        new("duckdelay", "Ducking Delay", "delay", "Delay that hides while you play and blooms in the gaps."),
        new("reversedelay", "Reverse Delay", "delay", "Plays buffered audio backwards; swelling psychedelic reverse lines."),
        new("granular", "Granular Delay", "delay", "Chops repeats into grains; smeared, pitched, textural clouds."),
        new("freeze", "Freeze", "delay", "Holds a sustained pad of the current note under new playing."),
        new("feedbacker", "Feedbacker", "delay", "Controlled runaway feedback tone; infinite screaming sustain."),
        new("resonator", "Resonator", "delay", "Tuned resonant string models; adds ringing chordal overtones."),

        // ---- reverb --------------------------------------------------------
        new("reverb", "Room Reverb", "reverb", "General-purpose room reverb; the safe default ambience."),
        new("springreverb", "Spring Reverb", "reverb", "Drippy spring tank; surf, blackface cleans and dub splashes."),
        new("platereverb", "Plate Reverb", "reverb", "Dense bright studio plate; smooth on vocals and leads."),
        new("hallreverb", "Hall Reverb", "reverb", "Large hall with long decay and early reflections; cinematic space."),
        new("convreverb", "Convolution Reverb", "reverb", "Impulse-response reverb; realistic rooms, needs an impulse file."),
        new("shimmer", "Shimmer Reverb", "reverb", "Reverb with an octave-up feedback loop; angelic ambient wash."),
        new("gatedreverb", "Gated Reverb", "reverb", "Big reverb chopped short by a gate; eighties drum-room effect."),
        new("reversereverb", "Reverse Reverb", "reverb", "Reverb tail played backwards; swells into every note."),

        // ---- utility / performance -----------------------------------------
        new("gain", "Gain", "utility", "Plain level trim in decibels; no tone change at all."),
        new("volume", "Volume Pedal", "utility", "Swept volume with a taper curve; swells and level rides."),
        new("swell", "Volume Swell", "utility", "Automatic attack removal; every note fades in like a bow."),
        new("midside", "Mid/Side", "utility", "Mid and side level and width control; stereo balance surgery."),
        new("bitcrusher", "Bit Crusher", "utility", "Bit-depth and sample-rate reduction; lo-fi digital destruction."),
        new("tapestop", "Tape Stop", "utility", "Pitch-and-time slowdown to a halt; a turntable brake."),
        new("glitch", "Glitch", "utility", "Captures and stutters slices of playing; buffer-repeat chaos."),
        new("filterseq", "Filter Sequencer", "utility", "Step-sequenced filter pattern; rhythmic synth-style gating."),
        new("looper", "Looper", "utility", "Records and overdubs a loop; a performance tool, not a tone."),
    ];

    private static readonly Dictionary<string, AiGearNote> ById =
        All.ToDictionary(n => n.Id, StringComparer.Ordinal);

    /// <summary>The note for <paramref name="moduleTypeId" />, or null when this
    /// table does not describe it.</summary>
    public static AiGearNote? Find(string moduleTypeId)
        => ById.TryGetValue(moduleTypeId, out var note) ? note : null;

    /// <summary>
    ///     The unit a param id is expressed in, derived from the engine's own
    ///     naming convention (<c>*Db</c>, <c>*Hz</c>, <c>*KHz</c>, <c>*Ms</c>,
    ///     <c>*S</c>, <c>*Pct</c>, <c>*Cents</c>, <c>*Semis</c>/<c>*Semitones</c>,
    ///     <c>*Cm</c>, <c>*Deg</c>). Everything else is a normalized or ordinal
    ///     knob and reports <c>"0..1"</c>-style guidance through its RANGE instead,
    ///     so this returns null and the prompt simply omits the unit.
    ///     <para>
    ///         Derived rather than tabulated on purpose: a hand-written unit column
    ///         over ~700 params is a second source of truth that would drift the
    ///         first time the catalogue grew, and the suffix convention is one the
    ///         native catalogue already enforces.
    ///     </para>
    /// </summary>
    public static string? UnitOf(string paramId)
    {
        if (string.IsNullOrEmpty(paramId)) return null;
        if (paramId.EndsWith("KHz", StringComparison.Ordinal)) return "kHz";
        if (paramId.EndsWith("Hz", StringComparison.Ordinal)) return "Hz";
        if (paramId.EndsWith("Db", StringComparison.Ordinal)) return "dB";
        if (paramId.EndsWith("Ms", StringComparison.Ordinal)) return "ms";
        if (paramId.EndsWith("Pct", StringComparison.Ordinal)) return "%";
        if (paramId.EndsWith("Cents", StringComparison.Ordinal)) return "cents";
        if (paramId.EndsWith("Semis", StringComparison.Ordinal) ||
            paramId.EndsWith("Semitones", StringComparison.Ordinal))
        {
            return "semitones";
        }

        if (paramId.EndsWith("Cm", StringComparison.Ordinal)) return "cm";
        if (paramId.EndsWith("Deg", StringComparison.Ordinal)) return "degrees";
        // A trailing capital S is seconds ("decayS", "maxLoopS") — but only when
        // it is a SUFFIX on a longer word, never a one-letter id.
        if (paramId.Length > 2 && paramId[^1] == 'S' && char.IsLower(paramId[^2])) return "s";
        return null;
    }
}
