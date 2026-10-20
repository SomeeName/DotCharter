using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>Broad visual family a DSP module belongs to — decides the pedal
/// enclosure palette and whether it renders as an amp head or a stompbox.</summary>
public enum DspModuleCategory
{
    Dynamics,   // gain, gate, compressor, sustainer, noisereducer — slate/steel
    Eq,         // eq, graphiceq — limes
    Filter,     // wah, envfilter, formant, acousticsim — yellows
    Drive,      // overdrive, distortion, fuzz, bitcrusher, klon, bigmuff — warm reds/oranges
    Amp,        // amp — red amp head
    Cab,        // cab — warm-tan amp head
    Modulation, // chorus, flanger, phaser, tremolo, harmtremolo, rotary, vibrato, univibe, ringmod — cool blues/purples
    Time,       // delay, tapedelay, reverb, platereverb, springreverb, shimmer — ambient teals/greens
    Pitch,      // octaver, harmonizer, doubler — bold magentas
    Routing,    // splitter, merger — neutral silvers (structural, no "voice")
    Utility     // anything unmapped
}

/// <summary>The enclosure skin a module renders as on the board. Anything
/// without a specialized face falls back to <see cref="Stompbox"/> — the
/// generic metadata-driven skin that covers every future module.</summary>
public enum PedalSkin
{
    /// <summary>Generic knob-row stompbox (the metadata-driven fallback).</summary>
    Stompbox,
    /// <summary>Wide amp-head chassis with faceplate + power lamp (amp).</summary>
    AmpHead,
    /// <summary>Speaker-cabinet face: grille cloth + driver (cab).</summary>
    Cabinet,
    /// <summary>The NEURAL amp head (<c>namamp</c>): the same covered chassis as
    /// <see cref="AmpHead"/>, but its faceplate names the loaded CAPTURE (model,
    /// author, format) instead of a built-in voicing, and it carries the
    /// capture-status pill. A head only — the <c>cab</c> module already owns the
    /// speaker baffle, and a capture models an amp, not a speaker.</summary>
    NeuralAmp,

    /// <summary>
    ///     A <c>namamp</c> holding a capture of a PEDAL (an overdrive, a boost, a
    ///     dirt box) rather than of an amp — the same neural module, rendered on
    ///     the portrait die-cast stompbox chassis instead of a wide amp head.
    ///     <para>
    ///         Load-bearing, not decoration. A boosted preset puts a
    ///         pedal capture IN FRONT OF an amp capture, and both are
    ///         <c>namamp</c> nodes — so with one skin for the module type, a rig
    ///         with a TS9 into a JCM2000 drew two amp heads side by side and read
    ///         as "there are two amps in here". A capture's gear type is the only
    ///         thing that distinguishes them, and it is a property of the loaded
    ///         FILE, not of the module, which is why the skin has to be able to
    ///         change when the capture does.
    ///     </para>
    /// </summary>
    NeuralPedal,

    /// <summary>Vertical fader bank instead of knobs (graphiceq).</summary>
    GraphicEq,
    /// <summary>Treadle rocker pedal (wah).</summary>
    Wah,
    /// <summary>Routing junction (<c>splitter</c>/<c>merger</c>): a COMPACT,
    /// full-height card that spans the branch rows it wires — never a portrait
    /// stompbox (a junction is a patchbay, not a pedal you'd stomp).</summary>
    Junction
}

/// <summary>The physical dial family a module's dial renders as —
/// picked per module category so every family reads like its real-world
/// hardware. Nine distinct dials so no two families wear the same knob AND the
/// same chassis finish (see <see cref="ChassisFinish"/>).</summary>
public enum KnobStyle
{
    /// <summary>Machined aluminium dial with a milled pointer notch (default / utility).</summary>
    Aluminium,
    /// <summary>Pointer blade on a dark bakelite skirt — vintage amp heads.</summary>
    ChickenHead,
    /// <summary>Black skirted knob with a numbered 0–10 tick ring — drives.</summary>
    Skirted,
    /// <summary>Vintage cream/ivory dial with a dark indicator — modulation.</summary>
    Cream,
    /// <summary>Black rubber dome with a knurled grip collar + white index —
    /// compressors/gates (Dynamics), like an MXR rubber knob.</summary>
    KnurledRubber,
    /// <summary>Low, dark machined puck with a bright radial pointer bar and a
    /// hairline scale — modern boutique time/ambient pedals (Time).</summary>
    PointerBar,
    /// <summary>Tall two-tone knob with a bold accent-coloured pointer chip —
    /// pitch/harmony pedals (Pitch), the "Davies 1900"/synth look.</summary>
    ChipPointer,
    /// <summary>Fluted metal top-hat knob with a white pointer line and a fluted
    /// skirt — EQ/filter voices (Eq/Filter).</summary>
    TopHat,
    /// <summary>Knurled brass pointer knob with a dark etched index and an inset
    /// bright cap — the dial bolted through an amp head's anodised panel (Amp).
    /// The only tinted dial on the board; the tint is the panel's anodising, so
    /// it never follows the module accent.</summary>
    Brass
}

/// <summary>The die-cast enclosure FINISH a stompbox-family pedal wears — the
/// procedural coat texture, corner hardware and edge treatment the custom-drawn
/// enclosure renders. Combined with <see cref="KnobStyle"/>
/// this gives every family a chassis you can name at a glance (a metal-flake
/// drive next to a wrinkle-coat compressor next to an extruded delay), never a
/// recolour of one template. Amp/Cab wear tolex (their own skins) and Routing is
/// a patchbay card, so their finish is unused (<see cref="ChassisFinish.Cast"/>).</summary>
public enum ChassisFinish
{
    /// <summary>Standard powder-coated die-cast box: Phillips corner screws,
    /// gentle gloss (Filter / Utility / the amp-cab-routing don't-care case).</summary>
    Cast,
    /// <summary>Automotive metal-flake sparkle under a glossy clear-coat, Phillips
    /// screws — Drive pedals.</summary>
    Sparkle,
    /// <summary>Matte hammertone / wrinkle powder coat with hex-head bolts and an
    /// embossed control-plate frame — Dynamics.</summary>
    Hammer,
    /// <summary>Brushed anodised EXTRUDED aluminium: vertical grain, two side
    /// rails, end-cap screws instead of corner screws — Time / Eq.</summary>
    Anodized,
    /// <summary>Glossy vintage enamel, rounded, with dome rivets and a strong
    /// specular gloss band — Modulation.</summary>
    Enamel,
    /// <summary>Carbon-fibre twill under a satin clear-coat with chamfer accent
    /// lines and hex bolts — modern Pitch.</summary>
    Carbon
}

/// <summary>
/// Pure, metadata-driven look-up that turns a native DSP module type id into its
/// rig visuals: a <see cref="DspModuleCategory"/>, the neon enclosure colour and
/// the amp-vs-stompbox split. Every id in the native catalogue (mirrored by
/// <c>DcRigTargetCatalog</c>) has a deliberate family + its OWN accent — hue
/// variation within the family — so no two pedals on the board look identical
/// (<c>PedalboardVisualsTests</c> gates catalogue coverage and accent
/// uniqueness; a new native module without a mapping fails the gate). No
/// Avalonia app instance required (<see cref="Color"/> is a plain struct). New
/// native modules fall back to <see cref="DspModuleCategory.Utility"/> — the
/// rig never crashes on an unknown id.
/// </summary>
public static class DspModuleVisuals
{
    private readonly record struct Visual(DspModuleCategory Category, Color Accent);

    private static Visual V(DspModuleCategory category, uint rgb)
        => new(category, Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    // One entry per native module id (keep in sync with DcRigTargetCatalog —
    // the coverage test fails if a catalogue id is missing here).
    private static readonly Dictionary<string, Visual> ModuleMap = new(StringComparer.Ordinal)
    {
        // Dynamics — slate/steel.
        ["gain"] = V(DspModuleCategory.Dynamics, 0xB8C4D0), // bright steel
        ["gate"] = V(DspModuleCategory.Dynamics, 0x7E93A8), // gunmetal
        ["compressor"] = V(DspModuleCategory.Dynamics, 0x8FB6CC), // steel blue
        ["sustainer"] = V(DspModuleCategory.Dynamics, 0xA9B4D9), // steel lavender
        ["noisereducer"] = V(DspModuleCategory.Dynamics, 0x9AA8A6), // grey sage

        // Eq — limes.
        ["eq"] = V(DspModuleCategory.Eq, 0x84CC16), // lime
        ["graphiceq"] = V(DspModuleCategory.Eq, 0xA3E635), // chartreuse

        // Filter — yellows.
        ["wah"] = V(DspModuleCategory.Filter, 0xFACC15), // gold
        ["envfilter"] = V(DspModuleCategory.Filter, 0xFDE047), // lemon
        ["formant"] = V(DspModuleCategory.Filter, 0xC8B900), // olive gold
        ["acousticsim"] = V(DspModuleCategory.Filter, 0xDBC49A), // spruce sand

        // Drive — warm reds/oranges.
        ["overdrive"] = V(DspModuleCategory.Drive, 0xF97316), // orange
        ["distortion"] = V(DspModuleCategory.Drive, 0xE8402A), // vermilion
        ["fuzz"] = V(DspModuleCategory.Drive, 0xFB923C), // apricot
        ["bitcrusher"] = V(DspModuleCategory.Drive, 0xD97706), // burnt amber
        ["klon"] = V(DspModuleCategory.Drive, 0xE3B341), // centaur gold
        ["bigmuff"] = V(DspModuleCategory.Drive, 0xC81E3C), // crimson

        // Amp-head faces.
        ["amp"] = V(DspModuleCategory.Amp, 0xEF4444), // red
        ["cab"] = V(DspModuleCategory.Cab, 0xE0934B), // warm tan

        // Modulation — cool blues/purples.
        ["chorus"] = V(DspModuleCategory.Modulation, 0xA855F7), // violet
        ["flanger"] = V(DspModuleCategory.Modulation, 0x8B5CF6), // purple
        ["phaser"] = V(DspModuleCategory.Modulation, 0xC084FC), // lavender
        ["tremolo"] = V(DspModuleCategory.Modulation, 0x818CF8), // periwinkle
        ["harmtremolo"] = V(DspModuleCategory.Modulation, 0x6366F1), // indigo
        ["rotary"] = V(DspModuleCategory.Modulation, 0x4F46E5), // deep indigo
        ["vibrato"] = V(DspModuleCategory.Modulation, 0x9333EA), // deep violet
        ["univibe"] = V(DspModuleCategory.Modulation, 0x7C3AED), // electric purple
        ["ringmod"] = V(DspModuleCategory.Modulation, 0xC026D3), // fuchsia

        // Time — ambient teals/greens.
        ["delay"] = V(DspModuleCategory.Time, 0x2DD4BF), // teal
        ["tapedelay"] = V(DspModuleCategory.Time, 0x0D9488), // deep teal
        ["reverb"] = V(DspModuleCategory.Time, 0x34D399), // emerald
        ["platereverb"] = V(DspModuleCategory.Time, 0x6EE7B7), // mint
        ["springreverb"] = V(DspModuleCategory.Time, 0x10B981), // green
        ["shimmer"] = V(DspModuleCategory.Time, 0x5EEAD4), // pale aqua

        // Pitch — bold magentas.
        ["octaver"] = V(DspModuleCategory.Pitch, 0xE11D74), // magenta
        ["harmonizer"] = V(DspModuleCategory.Pitch, 0xEC4899), // pink
        ["doubler"] = V(DspModuleCategory.Pitch, 0xF472B6), // soft pink

        // Wave 3 (ABI v22).
        ["namamp"] = V(DspModuleCategory.Amp, 0xB91C1C),  // deep neural red — amp family, own accent
        ["miku"] = V(DspModuleCategory.Pitch, 0x39C5BB), // the signature Miku teal

        // Wave 5 (ABI v23).
        ["autowah"] = V(DspModuleCategory.Filter, 0xF59E0B),  // amber — envelope funk
        ["boost"] = V(DspModuleCategory.Drive, 0xFCA311),   // marigold clean boost
        ["dimension"] = V(DspModuleCategory.Modulation, 0x60A5FA), // sky blue — 80s lush
        ["splitter"] = V(DspModuleCategory.Routing, 0xCBD5E1), // light slate
        ["merger"] = V(DspModuleCategory.Routing, 0x8296B4), // steel slate

        // Wave 6: the faithful Miku Stomp recreation — same Pitch/vocal family
        // as miku v1, but her signature cyan so the two are never confused.
        ["mikustomp"] = V(DspModuleCategory.Pitch, 0x00D9E1),   // Miku cyan

        // 0.18 expressive / performance wave.
        ["swell"] = V(DspModuleCategory.Dynamics, 0x5E8CA8), // slate blue — a volume treadle
        ["whammy"] = V(DspModuleCategory.Pitch, 0xFF4FD8),    // hot pink — the dive-bomb treadle
        ["subharmonic"] = V(DspModuleCategory.Pitch, 0xBE185D),    // deep rose — synth bass under the guitar
        ["reversedelay"] = V(DspModuleCategory.Time, 0x0E7490),      // cyan-teal — backwards echo
        ["slapback"] = V(DspModuleCategory.Time, 0xB07D4A),      // tape brown — a Sun Studio machine
        ["freeze"] = V(DspModuleCategory.Time, 0xA7F3D0),      // pale jade — the held pad
        ["resonator"] = V(DspModuleCategory.Time, 0x14B8A6),      // sympathetic teal
        ["tapestop"] = V(DspModuleCategory.Time, 0x0891B2),      // deep cyan — the brake
        ["glitch"] = V(DspModuleCategory.Time, 0x059669),      // deep emerald — the stutter
        ["widener"] = V(DspModuleCategory.Routing, 0x7DD3FC),   // pale sky — a stereo utility
        ["filterseq"] = V(DspModuleCategory.Filter, 0xCA8A04),    // dark gold — the sequenced filter
        ["feedbacker"] = V(DspModuleCategory.Drive, 0xEA580C),     // ember orange — the howl

        // 0.9 gear wave (native catalogue 57 -> 100),
        // one block per palette drawer. Each accent is its own: the coverage
        // gate asserts a pairwise-distinct colour per module, so a copied
        // swatch is a failing test rather than two pedals nobody can tell
        // apart on the board.
        // DynamicsGear.cpp
        ["limiter"] = V(DspModuleCategory.Dynamics, 0x6B7F91), // dark steel - the ceiling
        ["transient"] = V(DspModuleCategory.Dynamics, 0xA7BCCB), // pale steel - attack/sustain
        ["multiband"] = V(DspModuleCategory.Dynamics, 0x78909C), // three-band slate
        ["expander"] = V(DspModuleCategory.Dynamics, 0x5D7A8C), // deep slate - downward expansion
        ["optocomp"] = V(DspModuleCategory.Dynamics, 0xC3D2DC), // optical pale steel
        ["ducker"] = V(DspModuleCategory.Dynamics, 0x8AA0B5), // mid steel - sidechain duck
        // DriveGear.cpp
        ["rat"] = V(DspModuleCategory.Drive, 0xA34A17), // rusted iron
        ["ds1"] = V(DspModuleCategory.Drive, 0xFF7A00), // the orange box
        ["hm2"] = V(DspModuleCategory.Drive, 0xCC3300), // chainsaw red
        ["tapesat"] = V(DspModuleCategory.Drive, 0xC98A3E), // tape bronze
        ["bassod"] = V(DspModuleCategory.Drive, 0xE06C25), // bass drive copper
        ["tubepre"] = V(DspModuleCategory.Drive, 0xF2A25C), // preamp tan
        ["octfuzz"] = V(DspModuleCategory.Drive, 0xFF5E1A), // octave-fuzz flame
        // AmpCabGear.cpp
        ["bassamp"] = V(DspModuleCategory.Amp, 0xC0392B), // bass head crimson
        ["acpre"] = V(DspModuleCategory.Amp, 0xD9736A), // acoustic preamp rosewood
        ["poweramp"] = V(DspModuleCategory.Amp, 0xA61B1B), // power stage oxblood
        ["micpos"] = V(DspModuleCategory.Cab, 0xC97F3A), // mic-position tan
        // RsAmpGear.cpp
        ["rsamp"] = V(DspModuleCategory.Amp, 0xD9480F), // Rocksmith amp ember
        // FilterGear.cpp
        ["svf"] = V(DspModuleCategory.Filter, 0xEAB308), // state-variable amber
        ["comb"] = V(DspModuleCategory.Filter, 0xD4A017), // comb brass
        ["vocoder"] = V(DspModuleCategory.Filter, 0xFFE066), // vocoder straw
        ["talkbox"] = V(DspModuleCategory.Filter, 0xB8860B), // talkbox dark gold
        ["tilt"] = V(DspModuleCategory.Eq, 0x65A30D), // tilt olive - the eq drawer
        // ModulationGear.cpp
        ["tzflanger"] = V(DspModuleCategory.Modulation, 0x7E22CE), // thru-zero royal purple
        ["barberpole"] = V(DspModuleCategory.Modulation, 0xA78BFA), // endless-rise lilac
        ["autopan"] = V(DspModuleCategory.Modulation, 0x6D28D9), // auto-pan indigo
        ["ensemble"] = V(DspModuleCategory.Modulation, 0x8368E6), // Solina blue-violet
        ["freqshift"] = V(DspModuleCategory.Modulation, 0xD946EF), // Bode shifter orchid
        // PitchGear.cpp
        ["detune"] = V(DspModuleCategory.Pitch, 0xF9A8D4), // detune blush
        ["pitchcorrect"] = V(DspModuleCategory.Pitch, 0xDB2777), // corrected rose
        ["twelvestring"] = V(DspModuleCategory.Pitch, 0xFB7185), // twelve-string coral
        ["basssynth"] = V(DspModuleCategory.Pitch, 0x9D174D), // synth-bass wine
        // DelayGear.cpp
        ["multitap"] = V(DspModuleCategory.Time, 0x1FA2A0), // multitap teal
        ["duckdelay"] = V(DspModuleCategory.Time, 0x0F766E), // ducking deep teal
        ["bbddelay"] = V(DspModuleCategory.Time, 0x8C6B3F), // bucket-brigade sepia
        ["granular"] = V(DspModuleCategory.Time, 0x99F6E4), // grain-cloud pale aqua
        ["rhythmdelay"] = V(DspModuleCategory.Time, 0x06B6D4), // rhythmic cyan
        // ReverbGear.cpp
        ["hallreverb"] = V(DspModuleCategory.Time, 0x4ADE80), // hall green
        ["convreverb"] = V(DspModuleCategory.Time, 0x22C55E), // convolution emerald
        ["gatedreverb"] = V(DspModuleCategory.Time, 0x86EFAC), // gated pale green
        ["reversereverb"] = V(DspModuleCategory.Time, 0x15803D), // reverse forest
        // UtilityGear.cpp
        ["volume"] = V(DspModuleCategory.Dynamics, 0x9FB3C8), // volume-pedal steel
        ["midside"] = V(DspModuleCategory.Routing, 0xE2E8F0), // mid/side pale slate
        ["looper"] = V(DspModuleCategory.Time, 0x2AB3A6), // looper jade
    };

    public static DspModuleCategory Categorize(string moduleTypeId)
        => ModuleMap.TryGetValue(moduleTypeId, out var v) ? v.Category : DspModuleCategory.Utility;

    /// <summary>True when the id has a deliberate mapping (false = the grey
    /// Utility fallback). The coverage test asserts this for every catalogue id.</summary>
    public static bool HasVisualMapping(string moduleTypeId) => ModuleMap.ContainsKey(moduleTypeId);

    /// <summary>The knob hardware family a category's pedals wear — one distinct
    /// dial per family so the board never reads as recolours of one template: amp
    /// HEADS a knurled brass pointer knob (the dial that belongs through an
    /// anodised panel), cabs chicken-head blades, drives a numbered 0–10 skirt,
    /// modulation a vintage cream dial, dynamics a knurled rubber dome, time a
    /// modern pointer bar, pitch a bold chip pointer, eq/filter a fluted metal
    /// top-hat; routing + anything unmapped fall back to machined aluminium.
    /// Amp and Cab split because their panels are different metal: a head's
    /// controls go through brass, a cab's mic trims through plain chassis steel.</summary>
    public static KnobStyle KnobStyleFor(DspModuleCategory category) => category switch
    {
        DspModuleCategory.Amp => KnobStyle.Brass,
        DspModuleCategory.Cab => KnobStyle.ChickenHead,
        DspModuleCategory.Drive => KnobStyle.Skirted,
        DspModuleCategory.Modulation => KnobStyle.Cream,
        DspModuleCategory.Dynamics => KnobStyle.KnurledRubber,
        DspModuleCategory.Time => KnobStyle.PointerBar,
        DspModuleCategory.Pitch => KnobStyle.ChipPointer,
        DspModuleCategory.Eq or DspModuleCategory.Filter => KnobStyle.TopHat,
        _ => KnobStyle.Aluminium
    };

    public static KnobStyle KnobStyleFor(string moduleTypeId) => KnobStyleFor(Categorize(moduleTypeId));

    /// <summary>The die-cast enclosure finish a category's stompboxes wear — the
    /// coat texture + corner hardware + edge treatment that gives each family a
    /// nameable chassis. Paired with <see cref="KnobStyleFor(DspModuleCategory)"/> so no two
    /// enclosure-rendering families share BOTH finish and knob. Amp/Cab (tolex
    /// skins) and Routing (patchbay card) don't render a stompbox enclosure, so
    /// they map to the harmless <see cref="ChassisFinish.Cast"/> default.</summary>
    public static ChassisFinish ChassisFinishFor(DspModuleCategory category) => category switch
    {
        DspModuleCategory.Drive => ChassisFinish.Sparkle,
        DspModuleCategory.Dynamics => ChassisFinish.Hammer,
        DspModuleCategory.Time => ChassisFinish.Anodized,
        DspModuleCategory.Eq => ChassisFinish.Anodized,
        DspModuleCategory.Modulation => ChassisFinish.Enamel,
        DspModuleCategory.Pitch => ChassisFinish.Carbon,
        _ => ChassisFinish.Cast // Filter / Utility (+ the unused Amp/Cab/Routing)
    };

    public static ChassisFinish ChassisFinishFor(string moduleTypeId) => ChassisFinishFor(Categorize(moduleTypeId));

    /// <summary>The enclosure skin for a module id: the amp gets the wide head
    /// chassis and the neural amp its own capture-faced head, the cab its
    /// speaker-grille face, graphiceq a fader bank, the
    /// three TREADLE pedals (wah/whammy/swell — each has a <c>position</c> param
    /// the board binds the rocker to) a treadle; every other (or future) module
    /// is the generic stompbox.</summary>
    public static PedalSkin SkinFor(string moduleTypeId) => moduleTypeId switch
    {
        "amp" => PedalSkin.AmpHead,
        "namamp" => PedalSkin.NeuralAmp,
        "cab" => PedalSkin.Cabinet,
        "graphiceq" => PedalSkin.GraphicEq,
        "wah" or "whammy" or "swell" => PedalSkin.Wah,
        SplitRegionLayout.SplitterId or SplitRegionLayout.MergerId => PedalSkin.Junction,
        _ => PedalSkin.Stompbox
    };

    /// <summary>Only the amp renders on the wide amp-head face — the cab has its
    /// own speaker-grille skin now (see <see cref="SkinFor"/>).</summary>
    public static bool IsAmpHead(string moduleTypeId)
        => SkinFor(moduleTypeId) == PedalSkin.AmpHead;

    /// <summary>
    /// True for the WIDE letterbox chassis family — the tolex-wrapped covered
    /// boxes (amp head, cabinet) as opposed to a portrait die-cast stompbox or a
    /// slim junction card. Three unrelated call sites would otherwise spell this as an
    /// inline <c>SkinFor(id) is AmpHead or Cabinet</c> and would have silently
    /// disagreed the moment a fourth wide skin appeared: the board's lift-scale
    /// cap (<c>SplitBoardPanel.IsWideChassis</c>), the gear-rail drag's reserved
    /// gap width (<c>PedalboardView.GhostGapWidth</c>) and the node's tolex body
    /// brush (<c>PedalNodeViewModel</c>). One predicate, so they cannot drift.
    /// </summary>
    public static bool IsWideChassis(string moduleTypeId)
        => IsWideChassis(SkinFor(moduleTypeId));

    /// <summary>The wide-chassis test by SKIN, for callers that already resolved
    /// one. Needed because a <c>namamp</c>'s skin depends on the loaded capture:
    /// with an amp capture it is a wide head, with a PEDAL capture it is a
    /// portrait stompbox, and the module-type overload cannot tell them apart.</summary>
    public static bool IsWideChassis(PedalSkin skin)
        => skin is PedalSkin.AmpHead or PedalSkin.NeuralAmp or PedalSkin.Cabinet;

    /// <summary>The family's base accent colour — the fallback for unmapped ids
    /// and the tint the preset browser derives its card accents from.</summary>
    public static Color AccentColor(DspModuleCategory category) => category switch
    {
        DspModuleCategory.Dynamics => Color.FromRgb(0x8F, 0xA9, 0xBE),   // steel
        DspModuleCategory.Eq => Color.FromRgb(0x84, 0xCC, 0x16),         // lime
        DspModuleCategory.Filter => Color.FromRgb(0xFA, 0xCC, 0x15),     // gold
        DspModuleCategory.Drive => Color.FromRgb(0xF9, 0x73, 0x16),      // orange
        DspModuleCategory.Amp => Color.FromRgb(0xEF, 0x44, 0x44),        // red
        DspModuleCategory.Cab => Color.FromRgb(0xE0, 0x93, 0x4B),        // warm tan
        DspModuleCategory.Modulation => Color.FromRgb(0xA8, 0x55, 0xF7), // violet
        DspModuleCategory.Time => Color.FromRgb(0x2D, 0xD4, 0xBF),       // teal
        DspModuleCategory.Pitch => Color.FromRgb(0xEC, 0x48, 0x99),      // magenta
        DspModuleCategory.Routing => Color.FromRgb(0xCB, 0xD5, 0xE1),    // light slate
        _ => Color.FromRgb(0x94, 0xA3, 0xB8)                             // slate
    };

    /// <summary>The module's OWN enclosure accent (the knob arcs, LED glow and
    /// pedal edge all derive from it) — hue-varied within its family so no two
    /// pedals look identical. Unknown ids get the Utility slate.</summary>
    public static Color AccentColor(string moduleTypeId)
        => ModuleMap.TryGetValue(moduleTypeId, out var v)
            ? v.Accent
            : AccentColor(DspModuleCategory.Utility);

    /// <summary>The dark enclosure body colour, a deep tint of the accent so the
    /// pedals read as anodised metal rather than flat swatches.</summary>
    public static Color BodyColor(DspModuleCategory category) => BodyFrom(AccentColor(category));

    public static Color BodyColor(string moduleTypeId) => BodyFrom(AccentColor(moduleTypeId));

    private static Color BodyFrom(Color accent)
        // Deep, desaturated toward the app's near-black panels.
        => Color.FromRgb(
            Mix(accent.R, 0x18, 0.82),
            Mix(accent.G, 0x19, 0.82),
            Mix(accent.B, 0x22, 0.82));

    private static byte Mix(byte value, byte toward, double amount)
        => (byte)(value + (toward - value) * amount);

    // ---- enclosure identity: type treatment + material ----------------------

    /// <summary>The per-family type treatment for a module's screen-printed model
    /// name — block industrial for high-gain amps/drives, rounded-wide for
    /// modulation/time ambiance, an expressive italic "script" for the cleaner
    /// EQ/dynamics/cab voices, plain otherwise. Drives font style + letter
    /// spacing on the pedal faces (no fonts are added — Avalonia styling only).</summary>
    public enum RigTypeface
    {
        Plain,
        Industrial, // block caps, wide tracking — amps + drives
        Rounded,    // gentle tracking — modulation + time
        Script      // italic, tight — cleans (eq/dynamics/cab)
    }

    public static RigTypeface TypefaceFor(DspModuleCategory category) => category switch
    {
        DspModuleCategory.Amp or DspModuleCategory.Drive => RigTypeface.Industrial,
        DspModuleCategory.Modulation or DspModuleCategory.Time => RigTypeface.Rounded,
        DspModuleCategory.Eq or DspModuleCategory.Dynamics or DspModuleCategory.Cab => RigTypeface.Script,
        _ => RigTypeface.Plain
    };

    /// <summary>Letter spacing (px) for a type treatment — the block-industrial
    /// stamp tracks widest.</summary>
    public static double LetterSpacingFor(RigTypeface face) => face switch
    {
        RigTypeface.Industrial => 2.4,
        RigTypeface.Rounded => 1.2,
        RigTypeface.Script => 0.0,
        _ => 0.4
    };

    // ---- the module's MODEL param (gear-rail "browse models") ---------------

    /// <summary>
    /// The param whose value picks the module's <b>model</b> — the amp's voicing,
    /// the cab's speaker, the drive pedal's circuit. Null for every other module.
    /// <para>
    /// This is the ONLY param the gear rail may pre-set when it adds a pedal
    /// ("Plexi 100" adds an amp with <c>voicing = 10</c>). It must not be inferred
    /// as "the module's FIRST labelled param", which is why the native catalogue
    /// could not honestly label a footswitch: labelling freeze's <c>capture</c>
    /// would have made "browse models" offer to drop in a pedal already STOMPED
    /// (dcdsp_freeze.h is explicit that a preset must never ship capture = 1), and
    /// a chorus would have offered to add itself pre-set to an LFO shape. Naming
    /// the model param explicitly decouples the two: <i>every</i> discrete param
    /// can now carry value labels and render as a picker, and none of them
    /// becomes a model.
    /// </para>
    /// A module id here MUST expose this param id in the native catalogue with
    /// value labels — <c>GearRailTests</c> gates that against the live catalogue.
    /// </summary>
    public static string? ModelParamId(string moduleTypeId) => moduleTypeId switch
    {
        "amp" => "voicing",
        "cab" or "overdrive" or "distortion" => "model",
        _ => null
    };

    // ---- amp/cab face badge (model / voicing readout) -----------------------

    /// <summary>The parameter whose value drives the amp-head face badge:
    /// the amp's voicing, the cab's speaker model. Null for every other
    /// module (no badge) — the drives show their model in their own selector, not
    /// a badge, which is why this is a NARROWER set than
    /// <see cref="ModelParamId"/>. Param ids + int semantics mirror the native
    /// catalogue (dcdsp_amp.h / dcdsp_cab.h via <c>DcRigTargetCatalog</c>).</summary>
    public static string? BadgeParamId(string moduleTypeId) => moduleTypeId switch
    {
        "amp" => "voicing",
        "cab" => "model",
        _ => null
    };

    /// <summary>Localization KEY for the badge text given the badge param's
    /// current value (rounded; native int semantics: amp voicing 0 clean /
    /// 1 crunch / 2 lead / 3 chime / 4 rectifier / 5 jazz / 6 brown — the ABI-v23
    /// 7-voicing table; cab model 0 1×12 / 1 2×12 / 2 4×12-scooped /
    /// 3 vintage-dark). Null when the module has no badge or the value is
    /// out of the known range (never invents a label).</summary>
    public static string? BadgeResourceKey(string moduleTypeId, float value)
    {
        var v = (int)Math.Round(value);
        return moduleTypeId switch
        {
            "amp" => v switch
            {
                0 => "Pedalboard_AmpVoicing_Clean",
                1 => "Pedalboard_AmpVoicing_Crunch",
                2 => "Pedalboard_AmpVoicing_Lead",
                3 => "Pedalboard_AmpVoicing_Chime",
                4 => "Pedalboard_AmpVoicing_Rectifier",
                5 => "Pedalboard_AmpVoicing_Jazz",
                6 => "Pedalboard_AmpVoicing_Brown",
                _ => null
            },
            "cab" => v switch
            {
                0 => "Pedalboard_CabModel_1x12",
                1 => "Pedalboard_CabModel_2x12",
                2 => "Pedalboard_CabModel_4x12",
                3 => "Pedalboard_CabModel_Vintage",
                _ => null
            },
            _ => null
        };
    }
}
