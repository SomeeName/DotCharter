// RigSampler part 1 of 3 — the VOCABULARY: which modules exist, what family each is
// in, what order the families go in, and how likely each module is inside its family
// for each archetype. No sampling logic here.


using DotCharter.Tone.Ai;

namespace DotCharter.Services.Ai.Match;

public sealed partial class RigSampler
{
    // ---------------------------------------------------------------- families

    /// <summary>
    ///     Canonical pedalboard order, as the tone-design prompt already states it
    ///     (dynamics → drive → amp → cab → mic → eq/filter → modulation → delay →
    ///     reverb → utility), with two additions the prompt's rule list does not
    ///     cover because a text draft never had to place them:
    ///     <list type="bullet">
    ///         <item><b>pitch</b> sits at 1, BEFORE drive. Every monophonic tracker in
    ///         the drawer (<c>basssynth</c>, <c>pitchcorrect</c>) says so in its own
    ///         guide — they track a clean neck pickup far better than a clipped
    ///         signal.</item>
    ///         <item><b>mic</b> is not its own family in <c>AiGearNotes</c>: the
    ///         engine files <c>micpos</c> under <c>cab</c>. It is ordered by
    ///         <see cref="SubOrder" /> instead, so a rig is always
    ///         <c>cab → micpos</c> and never the reverse.</item>
    ///     </list>
    /// </summary>
    internal static readonly string[] FamilyOrder =
    [
        "dynamics", "pitch", "drive", "amp", "cab", "eq", "filter",
        "modulation", "delay", "reverb", "utility"
    ];

    private static readonly Dictionary<string, int> FamilyRank =
        FamilyOrder.Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i, StringComparer.Ordinal);

    /// <summary>Type id → the family <c>AiGearNotes</c> files it under. One source of
    /// truth: the glossary the AI prompt already ships, not a second table.</summary>
    private static readonly Dictionary<string, string> FamilyOf =
        AiGearNotes.All.ToDictionary(n => n.Id, n => n.Category, StringComparer.Ordinal);

    /// <summary>Within-family tie-break. Only the cab drawer needs one: a mic is
    /// placed on a cabinet, so <c>cab</c> (0) precedes <c>micpos</c> (1), and
    /// <c>acousticsim</c> (1) likewise follows an acoustic preamp's cab slot.</summary>
    private static int SubOrder(string typeId) => typeId switch
    {
        "micpos" => 1,
        "acousticsim" => 1,
        _ => 0
    };

    /// <summary>
    ///     Type id → the family the vocabulary files it under, READ-ONLY. Exposed
    ///     for <see cref="Search.RigPlausibility" />, which has to answer "would this
    ///     sampler ever have drawn this chain?" and must read the SAME table the
    ///     sampler draws from rather than keeping a second copy of it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FamilyOfTypeId => FamilyOf;

    /// <summary>The canonical family order, read-only. Same reason as
    /// <see cref="FamilyOfTypeId" />.</summary>
    public static IReadOnlyList<string> Families => FamilyOrder;

    /// <summary>
    ///     Amps that model a SPEAKER-less preamp and therefore do NOT imply a cab
    ///     (<c>acpre</c>, <c>tubepre</c>). The sampler's own
    ///     <c>DiTypeAmps</c>; everything in <see cref="TrueAmps" /> that is NOT here
    ///     is a rig the sampler puts a cabinet in front of with probability
    ///     <c>PCab</c> ≥ 0.76 in every archetype.
    /// </summary>
    public static IReadOnlySet<string> PreampTypeAmps => DiTypeAmps;

    // The three below are LAZY rather than field initialisers: `BassAmps`,
    // `Archetypes` and `LengthWeights` are declared further down this file and in
    // RigSampler.cs, and a static field initialiser that reads a field declared later
    // sees null. (It did, and the compiler said so.)

    /// <summary>The amps the <see cref="RigProfileKinds.Bass" /> archetype draws
    /// from — its pool, not a second list.</summary>
    public static IReadOnlyList<string> BassAmpTypeIds =>
        field ??= BassAmps.Select(a => a.Item1).ToArray();

    /// <summary>Every module that makes a rig a BASS rig for the purposes of a bass
    /// part: the bass archetype's amps plus the two bass-only pedals its allowed set
    /// keeps (see <c>AllowedSet</c>, which removes exactly these when
    /// <c>AllowBass</c> is false).</summary>
    public static IReadOnlySet<string> BassModuleTypeIds =>
        field ??= new HashSet<string>(
            BassAmps.Select(a => a.Item1).Concat(["bassod", "basssynth"]), StringComparer.Ordinal);

    /// <summary>The longest chain the length prior has any mass on — 8, the length of
    /// <c>LengthWeights</c>. Past it the sampler draws NOTHING, so a longer chain is
    /// outside the distribution the library was drawn from by construction.</summary>
    public static int LengthPriorSupport => LengthWeights.Length;

    /// <summary>The most drives one draw can produce: the largest index any
    /// archetype's <c>DriveCountWeights</c> reaches.</summary>
    public static int MaxSampledDrives =>
        field is 0 ? field = Archetypes.Values.Max(a => a.DriveCountWeights.Length - 1) : field;

    /// <summary>The cab DRAWER: <c>cab</c> and the two companions the sub-order rule
    /// places behind it. They share the <c>cab</c> family without being a second
    /// cabinet, which is why the duplicate-family rule has to know about them.</summary>
    public static IReadOnlySet<string> CabDrawer { get; } =
        new HashSet<string>(["cab", "micpos", "acousticsim"], StringComparer.Ordinal);

    /// <summary>The sort key a canonically-ordered chain is sorted by.</summary>
    internal static (int Family, int Sub) OrderKey(string typeId)
        => (FamilyOf.TryGetValue(typeId, out var fam) && FamilyRank.TryGetValue(fam, out var r)
                ? r
                : FamilyOrder.Length,
            SubOrder(typeId));

    // -------------------------------------------------------------- exclusions

    /// <summary>
    ///     Modules the sampler never draws, and why. Four reasons, all of them
    ///     "the LABEL would not describe the AUDIO":
    ///     <list type="bullet">
    ///         <item><c>splitter</c>/<c>merger</c> — parallel topology. v1 is series
    ///         only (they are already in <c>AiGearNotes.SeriesExcluded</c>).</item>
    ///         <item><c>looper</c> — 43 MiB of ring buffer per instance at 48 kHz,
    ///         paid up front, and it processes nothing until a footswitch drives
    ///         its state machine.</item>
    ///         <item><c>miku</c>/<c>mikustomp</c> — the sound is carried by a
    ///         string-valued phrase table, not by the float params a
    ///         <c>DcRigModule</c> label captures.</item>
    ///         <item><c>glitch</c> — its <c>chance</c> param rolls a native RNG this
    ///         side does not seed, so the same rig and the same DI would render two
    ///         different clips. A dataset row must be reproducible from its seed.</item>
    ///     </list>
    /// </summary>
    public static IReadOnlySet<string> Excluded { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "splitter", "merger", "looper", "miku", "mikustomp", "glitch"
    };

    /// <summary>Every module the sampler can draw, sorted. The dataset's vocabulary,
    /// and what the coverage lint enumerates.</summary>
    public static IReadOnlyList<string> SamplableTypeIds { get; } =
        AiGearNotes.All.Select(n => n.Id)
            .Where(id => !Excluded.Contains(id) && !AiGearNotes.SeriesExcluded.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    private static readonly Dictionary<string, string[]> ByFamily =
        SamplableTypeIds.GroupBy(id => FamilyOf[id])
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

    // ------------------------------------------------------------- archetypes

    /// <summary>
    ///     How often each archetype is drawn under <see cref="RigProfileKinds.Mixed" />.
    ///     Loosely the shape of the 166-preset factory library (clean, crunch and
    ///     high-gain dominate; bass and acoustic are small), tilted toward more
    ///     <c>direct</c> and <c>ambient</c> than the library has, because those are
    ///     the two the library UNDER-represents and a training corpus must not
    ///     inherit a curation bias.
    /// </summary>
    private static readonly (string Kind, double Weight)[] ArchetypeWeights =
    [
        (RigProfileKinds.Clean, 0.20),
        (RigProfileKinds.Crunch, 0.20),
        (RigProfileKinds.HighGain, 0.17),
        (RigProfileKinds.Bass, 0.11),
        (RigProfileKinds.Ambient, 0.16),
        (RigProfileKinds.Direct, 0.16)
    ];

    /// <summary>One archetype's structural priors. Every probability is per-rig.</summary>
    private sealed record Archetype(
        double PAmp,
        (string Id, double W)[] Amps,
        double PCab,
        double PMicPos,
        double[] DriveCountWeights,
        double PDynamics,
        double PEq,
        double PFilter,
        double PModulation,
        double PDelay,
        double PReverb,
        double PPitch,
        double PUtility,
        (string Id, double W)[] DriveBias,
        (string Id, double W)[] DynamicsBias);

    private static readonly (string, double)[] NoBias = [];

    // Amp pools. `namamp` is only reachable when the caller hands over a capture
    // directory (see Sample); it is listed so the weight survives that filter.
    private static readonly (string, double)[] GuitarAmps =
        [("amp", 6.0), ("namamp", 2.5), ("poweramp", 0.8), ("tubepre", 0.7)];

    private static readonly (string, double)[] BassAmps = [("bassamp", 9.0), ("poweramp", 1.0)];

    private static readonly Dictionary<string, Archetype> Archetypes = new(StringComparer.Ordinal)
    {
        // Clean: a compressor in front, a quiet amp, a cab, a little ambience.
        [RigProfileKinds.Clean] = new Archetype(
            PAmp: 0.86, Amps: GuitarAmps, PCab: 0.92, PMicPos: 0.22,
            DriveCountWeights: [0.62, 0.32, 0.06],
            PDynamics: 0.52, PEq: 0.34, PFilter: 0.06, PModulation: 0.34,
            PDelay: 0.26, PReverb: 0.46, PPitch: 0.05, PUtility: 0.12,
            DriveBias: [("boost", 3.0), ("klon", 2.5), ("tubepre", 1.5), ("tapesat", 1.5), ("overdrive", 1.2)],
            DynamicsBias: [("compressor", 3.0), ("optocomp", 2.5), ("transient", 1.0)]),

        // Crunch: exactly the shape the factory library's biggest group has.
        [RigProfileKinds.Crunch] = new Archetype(
            PAmp: 0.90, Amps: GuitarAmps, PCab: 0.94, PMicPos: 0.24,
            DriveCountWeights: [0.22, 0.58, 0.17, 0.03],
            PDynamics: 0.34, PEq: 0.38, PFilter: 0.07, PModulation: 0.22,
            PDelay: 0.30, PReverb: 0.36, PPitch: 0.04, PUtility: 0.10,
            DriveBias: [("overdrive", 3.0), ("klon", 2.0), ("distortion", 1.6), ("rat", 1.6), ("ds1", 1.4), ("boost", 1.4), ("tubepre", 1.0)],
            DynamicsBias: [("gate", 2.0), ("compressor", 1.5)]),

        // High gain: the gate is nearly mandatory, the cab always there.
        [RigProfileKinds.HighGain] = new Archetype(
            PAmp: 0.93, Amps: GuitarAmps, PCab: 0.96, PMicPos: 0.26,
            DriveCountWeights: [0.18, 0.55, 0.22, 0.05],
            PDynamics: 0.66, PEq: 0.44, PFilter: 0.05, PModulation: 0.12,
            PDelay: 0.24, PReverb: 0.24, PPitch: 0.05, PUtility: 0.10,
            DriveBias: [("distortion", 2.4), ("hm2", 2.0), ("rat", 1.8), ("boost", 2.4), ("ds1", 1.4), ("bigmuff", 1.2), ("overdrive", 1.6), ("octfuzz", 0.6)],
            DynamicsBias: [("gate", 4.0), ("noisereducer", 2.5), ("multiband", 0.6)]),

        // Bass: `bassamp`/`bassod`, never a guitar amp, compression the norm.
        [RigProfileKinds.Bass] = new Archetype(
            PAmp: 0.90, Amps: BassAmps, PCab: 0.76, PMicPos: 0.16,
            DriveCountWeights: [0.44, 0.46, 0.10],
            PDynamics: 0.62, PEq: 0.40, PFilter: 0.12, PModulation: 0.10,
            PDelay: 0.08, PReverb: 0.10, PPitch: 0.16, PUtility: 0.12,
            DriveBias: [("bassod", 5.0), ("tapesat", 1.5), ("distortion", 0.8), ("overdrive", 0.8), ("bigmuff", 0.8)],
            DynamicsBias: [("compressor", 3.0), ("optocomp", 2.0), ("multiband", 1.2), ("limiter", 1.0)]),

        // Ambient: the sound is the tail. Amp optional, delay and reverb dominant.
        [RigProfileKinds.Ambient] = new Archetype(
            PAmp: 0.62, Amps: GuitarAmps, PCab: 0.88, PMicPos: 0.18,
            DriveCountWeights: [0.60, 0.32, 0.08],
            PDynamics: 0.26, PEq: 0.24, PFilter: 0.12, PModulation: 0.58,
            PDelay: 0.74, PReverb: 0.80, PPitch: 0.22, PUtility: 0.18,
            DriveBias: [("boost", 2.0), ("overdrive", 1.5), ("tapesat", 1.5), ("klon", 1.2)],
            DynamicsBias: [("compressor", 2.0), ("optocomp", 1.5), ("swell", 1.0)]),

        // Direct: no amp at all, or an acoustic preamp. Pedals to the desk.
        [RigProfileKinds.Direct] = new Archetype(
            PAmp: 0.20, Amps: [("acpre", 5.0), ("tubepre", 3.0), ("poweramp", 1.0)],
            PCab: 0.34, PMicPos: 0.10,
            DriveCountWeights: [0.58, 0.34, 0.08],
            PDynamics: 0.44, PEq: 0.44, PFilter: 0.16, PModulation: 0.38,
            PDelay: 0.36, PReverb: 0.44, PPitch: 0.18, PUtility: 0.26,
            DriveBias: [("tapesat", 2.5), ("tubepre", 2.0), ("boost", 1.5), ("klon", 1.2)],
            DynamicsBias: [("compressor", 2.0), ("limiter", 1.5), ("optocomp", 1.2)])
    };

    /// <summary>
    ///     Amps that emulate a SPEAKER-less preamp and therefore do not imply a cab:
    ///     <c>acpre</c> is an acoustic DI preamp and <c>tubepre</c> carries its own
    ///     <c>cabSim</c> switch. Everything else (<c>amp</c>, <c>namamp</c>,
    ///     <c>bassamp</c>, <c>poweramp</c>) is a rig that wants a cabinet in front of
    ///     it — which is the prompt's "a cab after an amp unless the amp is a
    ///     DI/preamp" rule, sampled rather than written.
    /// </summary>
    private static readonly HashSet<string> DiTypeAmps = new(StringComparer.Ordinal) { "acpre", "tubepre" };

    /// <summary>
    ///     The modules that ARE an amplifier, for the "never two amps" rule. It is
    ///     deliberately NOT the <c>amp</c> family: <c>AiGearNotes</c> files
    ///     <c>tubepre</c> under <b>drive</b> (it is a preamp PEDAL, and one in front of
    ///     an amp is an ordinary rig) while it files <c>poweramp</c> under <b>amp</b>.
    ///     Testing the family let a <c>tubepre</c> amp slot take a <c>poweramp</c>
    ///     behind it and report as one amp — caught by
    ///     <c>RigSamplerTests.NoRigHasTwoAmps_…</c> on the first run.
    /// </summary>
    public static IReadOnlySet<string> TrueAmps { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "amp", "namamp", "bassamp", "acpre", "poweramp"
    };

    /// <summary>Per-module weight inside its family, for families the archetype does
    /// not bias. Unlisted modules get 1.0. These are the "a player owns three of
    /// these and one of those" numbers: a chorus is common, a barberpole phaser is
    /// not, and a training corpus that drew them equally would spend a sixth of its
    /// modulation budget on an effect almost nobody uses.</summary>
    private static readonly Dictionary<string, double> BaseWeight = new(StringComparer.Ordinal)
    {
        // modulation
        ["chorus"] = 3.0, ["flanger"] = 1.6, ["phaser"] = 2.0, ["tremolo"] = 2.0,
        ["univibe"] = 1.2, ["rotary"] = 1.0, ["vibrato"] = 0.8, ["ensemble"] = 0.9,
        ["dimension"] = 0.8, ["doubler"] = 0.8, ["widener"] = 0.9, ["harmtremolo"] = 0.5,
        ["autopan"] = 0.5, ["tzflanger"] = 0.4, ["barberpole"] = 0.3,
        ["ringmod"] = 0.3, ["freqshift"] = 0.25,
        // delay
        ["delay"] = 3.0, ["tapedelay"] = 2.0, ["slapback"] = 1.8, ["bbddelay"] = 1.6,
        ["multitap"] = 0.8, ["duckdelay"] = 0.7, ["rhythmdelay"] = 0.5,
        ["reversedelay"] = 0.5, ["granular"] = 0.4, ["freeze"] = 0.25,
        ["feedbacker"] = 0.25, ["resonator"] = 0.3,
        // reverb
        ["reverb"] = 3.0, ["springreverb"] = 2.0, ["platereverb"] = 1.8,
        ["hallreverb"] = 1.6, ["shimmer"] = 0.8, ["convreverb"] = 0.5,
        ["gatedreverb"] = 0.5, ["reversereverb"] = 0.4,
        // eq / filter
        ["eq"] = 3.0, ["graphiceq"] = 1.2, ["tilt"] = 1.0,
        ["wah"] = 1.6, ["autowah"] = 1.2, ["envfilter"] = 1.2, ["svf"] = 0.8,
        ["comb"] = 0.4, ["formant"] = 0.3, ["talkbox"] = 0.25, ["vocoder"] = 0.2,
        // pitch
        ["octaver"] = 2.0, ["harmonizer"] = 1.2, ["detune"] = 1.2, ["whammy"] = 0.8,
        ["subharmonic"] = 0.7, ["twelvestring"] = 0.5, ["pitchcorrect"] = 0.4,
        ["basssynth"] = 0.4,
        // utility
        ["gain"] = 2.0, ["volume"] = 1.2, ["swell"] = 0.8, ["midside"] = 0.6,
        ["bitcrusher"] = 0.5, ["tilt2"] = 0.0, ["tapestop"] = 0.2, ["filterseq"] = 0.3,
        // cab drawer
        ["cab"] = 5.0, ["micpos"] = 1.0, ["acousticsim"] = 0.6,
        // dynamics (when the archetype gives no bias)
        ["compressor"] = 3.0, ["gate"] = 2.5, ["optocomp"] = 1.5, ["limiter"] = 1.0,
        ["noisereducer"] = 1.0, ["transient"] = 0.8, ["expander"] = 0.5,
        ["multiband"] = 0.5, ["sustainer"] = 0.6, ["ducker"] = 0.3,
        // drive (when the archetype gives no bias)
        ["overdrive"] = 3.0, ["distortion"] = 2.0, ["boost"] = 2.0, ["klon"] = 1.5,
        ["rat"] = 1.2, ["ds1"] = 1.0, ["fuzz"] = 1.0, ["bigmuff"] = 1.0,
        ["tapesat"] = 1.0, ["hm2"] = 0.6, ["octfuzz"] = 0.4, ["bassod"] = 0.6
    };

    private static double WeightOf(string id) => BaseWeight.TryGetValue(id, out var w) ? w : 1.0;
}
