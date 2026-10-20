using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
///     Which EDITOR the selected module opens, from the tone-rig design
///     (<c>tone-rig/components.js</c> <c>renderEditor</c>): a hardware amp
///     face, the cabinet console, the routing console, one of the specialised
///     racks, or the generic pedal enclosure that every other module gets.
/// </summary>
public enum RigEditorFamily
{
    /// <summary>A stompbox enclosure sized to its own control count.</summary>
    Pedal,
    /// <summary>The wide amp head with its faceplate, lamp and six main knobs.</summary>
    AmpFace,
    /// <summary>Cabinet shell, two IR slots, voicing row, filter response and its mic.</summary>
    Cabinet,
    /// <summary>The mic model: the speaker, the mic on it, its distance and type.</summary>
    MicPlacement,
    /// <summary>Splitter / merger: the lane mix console.</summary>
    Routing,
    /// <summary>Ten vertical faders plus the level knob.</summary>
    GraphicEq,
    /// <summary>Three labelled bands plus output trim.</summary>
    ParametricEq,
    /// <summary>A treadle pedal (wah, whammy, swell) with its vertical position fader.</summary>
    Expression,
    /// <summary>The Miku vocal processors with their lyric fields.</summary>
    Vocal,
    /// <summary>Compressor / gate / noise reducer as a sectioned rack.</summary>
    Dynamics
}

/// <summary>
///     The rig's ARTWORK catalogue: which illustration each native module is
///     drawn with on the board and in the gear list, which enclosure coat and
///     accent its editor wears, and which editor family opens for it.
///
///     <para>
///         The illustrations are the texture pack's prepared regions
///         (<c>Assets/Rig/Art/*.webp</c>); every entry names a file that exists,
///         and <c>RigArtworkTests</c> proves it for every id in the native
///         catalogue. Artwork is decorative: no entry here ever supplies a
///         parameter, a range or a control — those come from the module
///         catalogue alone (root AGENTS.md §7).
///     </para>
/// </summary>
public static class RigArtwork
{
    public const string AssetRoot = "avares://DotCharter.Tone.UI/Assets/Rig/Art/";

    /// <summary>The brand mark engraved on every enclosure and rack. A name,
    /// not a phrase: it is the same in every language.</summary>
    public const string BrandMark = "DOTCHARTER";

    /// <summary>The neural head's plate: the product's own name and its
    /// expansion (Neural Amp Modeler) — proper nouns, never translated.</summary>
    public const string NeuralBrand = "NAM";
    public const string NeuralBrandSubtitle = "NEURAL AMP MODELER";
    public const string LegacyBrand = "LEGACY";

    /// <summary>The stationary knob cap (256 px, transparent), pointer drawn by code.</summary>
    public const string KnobCap = "knob-alpha.png";

    public const string PowderCoat = "neutral-powder-coat.webp";
    public const string BrushedAluminium = "black-brushed-aluminum.webp";
    public const string ChassisScrew = "chassis-screw.webp";
    public const string JackSocket = "jack-socket.webp";
    public const string FootswitchUp = "footswitch-up.webp";
    public const string FootswitchDown = "footswitch-down.webp";
    public const string RotaryGrille = "rotary-grille.webp";
    public const string WovenGrille = "woven-grille.webp";
    public const string BlackTolex = "black-tolex.webp";
    public const string NamShell = "nam-editor-shell.webp";
    public const string LegacyShell = "legacy-editor-shell.webp";

    /// <summary>The fallback when a module id has no illustration of its own —
    /// a future native module still draws, on the utility sprite.</summary>
    public const string FallbackIllustration = "merger.webp";

    private static readonly Dictionary<string, string> Illustrations = new(StringComparer.Ordinal)
    {
        ["gain"] = "clean-boost", ["gate"] = "noise-gate", ["compressor"] = "compressor",
        ["eq"] = "parametric-eq", ["overdrive"] = "overdrive", ["distortion"] = "distortion",
        ["amp"] = "legacy-amp", ["cab"] = "cabinet", ["tremolo"] = "tremolo", ["chorus"] = "chorus",
        ["flanger"] = "flanger", ["delay"] = "delay", ["reverb"] = "reverb", ["fuzz"] = "fuzz",
        ["phaser"] = "phaser", ["wah"] = "wah-auto-wah", ["autowah"] = "auto-wah-envelope",
        ["octaver"] = "octaver", ["ringmod"] = "ring-modulator", ["bitcrusher"] = "bit-crusher",
        ["tapedelay"] = "tape-delay", ["platereverb"] = "plate-reverb", ["rotary"] = "rotary-speaker",
        ["formant"] = "formant-vocal", ["graphiceq"] = "graphic-eq", ["harmonizer"] = "pitch-harmonizer",
        ["vibrato"] = "vibrato", ["sustainer"] = "sustainer", ["springreverb"] = "spring-reverb",
        ["shimmer"] = "shimmer-reverb", ["univibe"] = "uni-vibe", ["harmtremolo"] = "harmonic-tremolo",
        ["envfilter"] = "envelope-filter", ["doubler"] = "detune-doubler", ["acousticsim"] = "acoustic-sim",
        ["noisereducer"] = "noise-reducer", ["boost"] = "boost-clean-treble", ["dimension"] = "dimension-chorus",
        ["klon"] = "transparent-boost", ["bigmuff"] = "big-muff-fuzz", ["miku"] = "miku-vocal-synth",
        ["mikustomp"] = "miku-stomp-vocaloid", ["swell"] = "volume-swell", ["whammy"] = "whammy-pitch-treadle",
        ["reversedelay"] = "reverse-delay", ["slapback"] = "slapback-echo", ["widener"] = "stereo-widener",
        ["freeze"] = "freeze-infinite-sustain", ["subharmonic"] = "subharmonic-synth",
        ["filterseq"] = "filter-sequencer", ["feedbacker"] = "feedbacker", ["resonator"] = "resonator",
        ["tapestop"] = "tape-stop", ["glitch"] = "glitch-beat-repeat", ["splitter"] = "splitter",
        ["merger"] = "merger", ["namamp"] = "neural-amp-nam",

        // ── JuceSharp 0.9 gear wave (native catalogue 57 → 100) ──
        // These 43 files are PROCEDURAL PLACEHOLDERS drawn by
        // tools/rig-art/generate-gear-art.py — stylised flat panels, not the
        // photoreal renders the first 57 are. They ship so every native module
        // has its OWN sprite instead of the merger patchbay fallback (which
        // reads as "some grey box" on the board and in the gear rail). The
        // owner decides what hand-drawn art replaces them; swapping a file in
        // needs no change here, only the basename is load-bearing.
        ["limiter"] = "pedal-limiter", ["transient"] = "transient-shaper",
        ["multiband"] = "multiband-compressor", ["expander"] = "expander",
        ["optocomp"] = "optical-compressor", ["ducker"] = "ducker",
        ["rat"] = "rodent-distortion", ["ds1"] = "orange-distortion",
        ["hm2"] = "chainsaw-distortion", ["tapesat"] = "tape-saturation",
        ["bassod"] = "bass-overdrive", ["tubepre"] = "tube-preamp-di",
        ["octfuzz"] = "octave-fuzz", ["bassamp"] = "bass-amp", ["rsamp"] = "rocksmith-amp",
        ["acpre"] = "acoustic-preamp", ["poweramp"] = "power-amp-sag",
        ["micpos"] = "cab-mic-position", ["svf"] = "multimode-filter",
        ["comb"] = "comb-filter", ["vocoder"] = "vocoder", ["talkbox"] = "talk-box",
        ["tilt"] = "tilt-eq", ["tzflanger"] = "thru-zero-flanger",
        ["barberpole"] = "barber-pole-phaser", ["autopan"] = "auto-pan",
        ["ensemble"] = "string-ensemble", ["freqshift"] = "frequency-shifter",
        ["detune"] = "detune-micro-pitch", ["pitchcorrect"] = "pitch-correction",
        ["twelvestring"] = "twelve-string-sim", ["basssynth"] = "bass-synth-tracker",
        ["multitap"] = "multitap-delay", ["duckdelay"] = "ducking-delay",
        ["bbddelay"] = "analog-bbd-delay", ["granular"] = "granular-cloud",
        ["rhythmdelay"] = "rhythmic-tremolo-delay", ["hallreverb"] = "hall-room-reverb",
        ["convreverb"] = "convolution-reverb", ["gatedreverb"] = "gated-reverb",
        ["reversereverb"] = "reverse-reverb", ["volume"] = "volume-expression-pedal",
        ["midside"] = "mid-side-processor", ["looper"] = "looper"
    };

    /// <summary>The enclosure coat and accent per module (the design's
    /// <c>finishes</c> map). Absent ⇒ the plum default coat and the category accent.</summary>
    private static readonly Dictionary<string, (uint Coat, uint Accent)> Finishes = new(StringComparer.Ordinal)
    {
        ["overdrive"] = (0x204d3b, 0xa0dfa7), ["distortion"] = (0x6f1326, 0xf68a92), ["fuzz"] = (0x422347, 0xd9a1ef),
        ["klon"] = (0x978266, 0xffe0a0), ["bigmuff"] = (0x612a45, 0xf1abc4), ["boost"] = (0x51402c, 0xedbf70),
        ["feedbacker"] = (0x541c30, 0xe7859e), ["bitcrusher"] = (0xb94b0b, 0xffc584),
        ["chorus"] = (0x56227d, 0xd1a0ff), ["flanger"] = (0x262e68, 0xadbaff), ["tremolo"] = (0x6b2855, 0xf7a3d7),
        ["phaser"] = (0x402158, 0xc79bfa), ["vibrato"] = (0x82274e, 0xffadd1), ["harmonizer"] = (0x5e2439, 0xf89fb5),
        ["rotary"] = (0x342238, 0xd5a7de), ["univibe"] = (0x585078, 0xd6c3ff), ["harmtremolo"] = (0x58236b, 0xe7adfa),
        ["octaver"] = (0x58253f, 0xf5a2d5), ["doubler"] = (0x41273e, 0xe7b1dc), ["subharmonic"] = (0x35223e, 0xd6a6f0),
        ["filterseq"] = (0x672674, 0xeda0ff), ["resonator"] = (0x645b89, 0xe0d0ff),
        ["delay"] = (0x076576, 0x78e9ef), ["tapedelay"] = (0x254960, 0xb0d8ed), ["reverb"] = (0x096877, 0x81eff2),
        ["platereverb"] = (0x53657b, 0xc5e4ff), ["springreverb"] = (0x3b6757, 0xb3e6c6), ["shimmer"] = (0x0e4359, 0x8ed9ef),
        ["reversedelay"] = (0x1c345a, 0xa0c6ff), ["tapestop"] = (0x057888, 0x81f2fa), ["slapback"] = (0x537991, 0xcfedff),
        ["freeze"] = (0x195860, 0x9df0e5), ["glitch"] = (0x282b30, 0x5fe8ee), ["widener"] = (0x292b30, 0x6be9df),
        ["gain"] = (0x63686d, 0xe6eaf0), ["sustainer"] = (0x55636f, 0xd5e9fb), ["formant"] = (0x295443, 0xa9edcc),
        ["autowah"] = (0x164c3f, 0x81e8b3), ["envfilter"] = (0x2b5648, 0x97e8c4), ["acousticsim"] = (0x516647, 0xd4e5a7),
        ["ringmod"] = (0x403154, 0xd8b4f5), ["dimension"] = (0x424d66, 0xc7d9ff),
        ["splitter"] = (0x30243e, 0xcfa7eb), ["merger"] = (0x30243e, 0xcfa7eb),

        // ── JuceSharp 0.9 gear wave ── the SAME (coat, accent) pairs the
        // placeholder sprites are painted with (tools/rig-art SPECS), so the
        // enclosure the editor draws and the sprite on the board are the same
        // pedal. Every pair is distinct from every other pair here; the one
        // duplicate in this map (splitter/merger) is deliberate and predates
        // this wave. If a hand-drawn sprite lands with a different colourway,
        // move the pair here with it.
        ["limiter"] = (0x39424b, 0xcfe0ee), ["transient"] = (0x4a5560, 0xdbe8f4),
        ["multiband"] = (0x2c3239, 0xbfd4e6), ["expander"] = (0x445059, 0xc8dceb),
        ["optocomp"] = (0x5d5348, 0xffe9b8), ["ducker"] = (0x353f46, 0xb7d0e0),
        ["rat"] = (0x1b1b1e, 0xe8e3d6), ["ds1"] = (0xc8621a, 0xffd9a8),
        ["hm2"] = (0x1c1c22, 0xff5a3c), ["tapesat"] = (0x7a5c33, 0xf2d9a6),
        ["bassod"] = (0x1f3a6b, 0xa9c8ff), ["tubepre"] = (0x8a7a5c, 0xffeec2),
        ["octfuzz"] = (0x3b2a12, 0xf5c44a), ["bassamp"] = (0x2a2118, 0xe8b878),
        ["rsamp"] = (0x2b1610, 0xff8a4c),
        ["acpre"] = (0x6b5a41, 0xf3dcb0), ["poweramp"] = (0x3a1d1d, 0xf08a6a),
        ["micpos"] = (0x241f1b, 0xd8a86a), ["svf"] = (0x4c4a16, 0xf2e06a),
        ["comb"] = (0x3f4a20, 0xd8e77a), ["vocoder"] = (0x6a5a1f, 0xffe89a),
        ["talkbox"] = (0x5c3a1e, 0xffc78a), ["tilt"] = (0x3d5220, 0xc6e876),
        ["tzflanger"] = (0x1e3a5c, 0x9ecbf5), ["barberpole"] = (0x7a2a2a, 0xffb0a0),
        ["autopan"] = (0x334a78, 0xb6c9ff), ["ensemble"] = (0x4a3a72, 0xc3b0ff),
        ["freqshift"] = (0x2d2a5e, 0x9f9bf0), ["detune"] = (0x6b2f5e, 0xf3a8e0),
        ["pitchcorrect"] = (0x47304f, 0xdcb0e8), ["twelvestring"] = (0x5a4630, 0xebc79a),
        ["basssynth"] = (0x24305a, 0x8fb4ff), ["multitap"] = (0x0e5a63, 0x7fe3e8),
        ["duckdelay"] = (0x14454f, 0x86d6dd), ["bbddelay"] = (0x2a4a4a, 0x9fd8d0),
        ["granular"] = (0x1a4a58, 0x8ae0f0), ["rhythmdelay"] = (0x105057, 0x74dad6),
        ["hallreverb"] = (0x2b4a5e, 0xa8d2ea), ["convreverb"] = (0x334a52, 0xa6dde0),
        ["gatedreverb"] = (0x1c3f44, 0x8fcfc6), ["reversereverb"] = (0x274a44, 0x9fe0c2),
        ["volume"] = (0x2e2e34, 0xd6dbe2), ["midside"] = (0x3a3a44, 0xc2c8d6),
        ["looper"] = (0x45264f, 0xe3a8f0)
    };

    private const uint DefaultCoat = 0x3f304b;

    /// <summary>The illustration file for a module id, always a file that ships.</summary>
    public static string IllustrationFor(string moduleTypeId) =>
        Illustrations.TryGetValue(moduleTypeId, out var name) ? name + ".webp" : FallbackIllustration;

    public static bool HasOwnIllustration(string moduleTypeId) => Illustrations.ContainsKey(moduleTypeId);

    /// <summary>Every module id this catalogue illustrates — the test's coverage set.</summary>
    public static IReadOnlyCollection<string> IllustratedIds => Illustrations.Keys;

    /// <summary>The editor family the design opens for this module.</summary>
    public static RigEditorFamily FamilyFor(string moduleTypeId) => moduleTypeId switch
    {
        "amp" or "namamp" => RigEditorFamily.AmpFace,
        "cab" => RigEditorFamily.Cabinet,
        "micpos" => RigEditorFamily.MicPlacement,
        SplitRegionLayout.SplitterId or SplitRegionLayout.MergerId => RigEditorFamily.Routing,
        "graphiceq" => RigEditorFamily.GraphicEq,
        "eq" => RigEditorFamily.ParametricEq,
        "wah" or "whammy" or "swell" => RigEditorFamily.Expression,
        "miku" or "mikustomp" => RigEditorFamily.Vocal,
        "compressor" or "gate" or "noisereducer" => RigEditorFamily.Dynamics,
        _ => RigEditorFamily.Pedal
    };

    /// <summary>The amp face's shell artwork: the legacy valve shell for the
    /// built-in amp, the NAM shell for a neural capture.</summary>
    public static string ShellFor(string moduleTypeId) => moduleTypeId == "amp" ? LegacyShell : NamShell;

    /// <summary>The enclosure coat colour for a pedal editor.</summary>
    public static Color CoatFor(string moduleTypeId) =>
        Rgb(Finishes.TryGetValue(moduleTypeId, out var f) ? f.Coat : DefaultCoat);

    /// <summary>The enclosure's own accent (labels, LED, arcs) — the design's
    /// per-pedal accent, else the module's category accent.</summary>
    public static Color AccentFor(string moduleTypeId) =>
        Finishes.TryGetValue(moduleTypeId, out var f) ? Rgb(f.Accent) : DspModuleVisuals.AccentColor(moduleTypeId);

    /// <summary>
    ///     The knob grid mirrors the device artwork (design §6.2): four knobs
    ///     sit 2×2, five or six go three across, seven or eight four across,
    ///     nine or more five across. The enclosure SCALES to fit its panel; it
    ///     never re-lays out, so a selected pedal keeps the silhouette of the
    ///     one pictured in the chain.
    /// </summary>
    public static int EnclosureColumns(int controlCount) => controlCount switch
    {
        >= 9 => 5,
        >= 7 => 4,
        >= 5 => 3,
        _ => 2
    };

    /// <summary>Editor scale-to-fit floor (design §3.3): below it the editor
    /// scrolls rather than shrinking labels past reading size.</summary>
    public const double MinEditorScale = 0.50;

    /// <summary>Editor scale-to-fit ceiling: a pedal in a tall
    /// dock GROWS to use it, the way the prototype's knobs scale with the
    /// viewport (clamp(44px, 5.7vh, 86px)), but not past the point where the
    /// texture pack's bitmaps go soft.</summary>
    public const double MaxEditorScale = 1.35;

    /// <summary>
    ///     The scale that fits <paramref name="content" /> into
    ///     <paramref name="available" /> pixels, never above <see cref="MaxEditorScale" />
    ///     and never below <see cref="MinEditorScale" />. Pure, so the two-pass fit the view
    ///     runs is testable without a layout.
    /// </summary>
    public static double FitScale(double content, double available) =>
        FitScale(content, available, MaxEditorScale);

    /// <summary>
    ///     The scale that fits <paramref name="content" /> into
    ///     <paramref name="available" /> pixels, never above
    ///     <paramref name="maxScale" /> and never below
    ///     <see cref="MinEditorScale" />.
    ///     <para>
    ///         The ceiling is a PARAMETER because the editor faces carry it as
    ///         a breakpointed token (<c>FireRigFaceMaxScale</c>: 1.35, 2.0 above
    ///         the wide breakpoint) while the pedal enclosure and every caller
    ///         that passes nothing keep <see cref="MaxEditorScale" />. A ceiling
    ///         that is not a finite number at or above the floor is nonsense —
    ///         an unresolved <c>DynamicResource</c> reads as 0 — and falls back
    ///         to <see cref="MaxEditorScale" /> rather than pinning the face at
    ///         the floor.
    ///     </para>
    /// </summary>
    public static double FitScale(double content, double available, double maxScale)
        => FitScale(content, available, maxScale, MinEditorScale);

    /// <summary>
    ///     The same fit with its own FLOOR. A generated faceplate may pass a lower
    ///     one than <see cref="MinEditorScale" />: its labels are printed in the
    ///     picture and shrink with it, so there is no reading size to protect — a
    ///     face cut off at the bottom of a short stage is the worse outcome. A floor
    ///     that is not a positive finite number falls back to <see cref="MinEditorScale" />.
    /// </summary>
    public static double FitScale(double content, double available, double maxScale, double minScale)
    {
        if (content <= 0 || available <= 0) return 1;
        var floor = double.IsFinite(minScale) && minScale > 0 ? minScale : MinEditorScale;
        var ceiling = double.IsFinite(maxScale) && maxScale >= floor
            ? maxScale
            : Math.Max(MaxEditorScale, floor);
        var scale = (available - 8) / content;
        return Math.Clamp(scale, floor, ceiling);
    }

    private static Color Rgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
