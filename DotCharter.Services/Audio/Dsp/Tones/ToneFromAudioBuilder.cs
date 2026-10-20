using System.Globalization;
using DotCharter.Analysis.Tone;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Audio.Dsp.Tones;

/// <summary>
///     The rig family a <see cref="ToneAudioProfile" /> was mapped onto. This is
///     the machine-readable half of <see cref="ToneSuggestion" />: the UI should
///     render its own localized wording from this rather than parsing
///     <see cref="ToneSuggestion.Rationale" />.
/// </summary>
public enum ToneArchetype
{
    /// <summary>No amp colour at all — a DI preamp into a PA speaker. Chosen for
    /// an undriven span that is unusually bright and top-heavy, which is what an
    /// acoustic, a piezo or a DI'd clean measures like. The mapper cannot tell
    /// those three apart, so it picks the one that adds the least.</summary>
    Direct,

    /// <summary>An undriven amp with headroom left.</summary>
    Clean,

    /// <summary>Compressing on a hard pick, clean when you back off.</summary>
    EdgeOfBreakup,

    /// <summary>The preamp is clipping, but the spectrum still reads as an amp
    /// pushed hard rather than as distortion.</summary>
    Crunch,

    /// <summary>The spectrum itself says clipped: a flat spectrum and energy
    /// smeared between the harmonics, not just a squashed crest.</summary>
    HighGain
}

/// <summary>
///     A tone built from a span of audio, together with why it came out that way.
///     <para>
///         Returned instead of a bare <see cref="RsTone" /> because the charter did
///         not choose this gear — the mapper did. Handing back only the tone would
///         leave the UI guessing at an explanation, and guessing is exactly the
///         failure mode this feature has to avoid.
///     </para>
/// </summary>
/// <param name="Tone">The tone, ready to store or hand to the tone editor.</param>
/// <param name="Archetype">The rig family chosen — the stable thing to branch on.</param>
/// <param name="Rationale">
///     One English, invariant-culture line naming the archetype, the measurements
///     that selected it and the gear it landed on. Diagnostic text, not a
///     localized UI string.
/// </param>
public sealed record ToneSuggestion(RsTone Tone, ToneArchetype Archetype, string Rationale);

/// <summary>
///     Turns the measurements in a <see cref="ToneAudioProfile" /> into a real
///     Rocksmith tone: an amp, a cabinet, a tone stack and — only when the amp
///     alone will not reach — a drive pedal.
///     <para>
///         <b>What this is.</b> A STARTING POINT that lands in a recognisable
///         neighbourhood, so the charter opens the tone editor with something
///         plausible in front of them instead of the default amp. It is not a
///         recovered rig and cannot be: the audio it measured is already through
///         somebody's amp, a room, a mix bus and (usually) a stem separator. No
///         claim here is stronger than "this is roughly the right family, now
///         adjust it".
///     </para>
///     <para>
///         <b>Pure and deterministic.</b> Same profile + same catalogue ⇒ byte-identical
///         tone. No clock, no randomness, no unordered iteration: every gear key is
///         a literal, and knob dictionaries are seeded in the catalogue's own knob
///         order.
///     </para>
///     <para>
///         <b>The one guarantee worth relying on.</b> Holding the spectral shape
///         (<see cref="ToneAudioProfile.SpectralCentroidHz" /> and the three band
///         energies) fixed, the amp's Gain knob is NON-DECREASING in
///         <see cref="ToneAudioProfile.Drive" />. The archetype gain windows are
///         disjoint and ascending precisely so that a threshold table cannot make
///         "more driven" quietly mean "less gain".
///     </para>
/// </summary>
public static class ToneFromAudioBuilder
{
    // ------------------------------------------------------------ thresholds
    //
    // Every boundary below is stated in terms of Drive, which ToneAudioProfile
    // folds from crest factor (weight .5), spectral flatness (.3) and harmonic
    // smearing (.2). The arguments read most easily in crest terms, so each one
    // names the crest factor it corresponds to for a spectrally clean source
    // (flatness ~0, harmonic ratio ~0.9, i.e. Drive ~ 0.5*(18-crest)/12 + 0.02).

    /// <summary>
    ///     Clean ends here. For a spectrally clean source this is a crest factor
    ///     of about 13 dB — a guitar that still has 13 dB of peak above its own
    ///     RMS is not compressing, it is just being played. Below this the amp
    ///     should have headroom left.
    /// </summary>
    public const double CleanDriveMax = 0.22;

    /// <summary>
    ///     Edge-of-breakup ends here, at roughly a 10 dB crest. This is the widest
    ///     genuinely ambiguous band — the same measurement covers a compressed
    ///     clean and a lightly overdriven amp — so it gets a low-gain tube amp,
    ///     which a charter can push or back off by hand from either direction.
    /// </summary>
    public const double EdgeDriveMax = 0.42;

    /// <summary>
    ///     Crunch ends here. This boundary is deliberately above 0.5, which is the
    ///     most a squashed crest ALONE can contribute: to cross it the span must
    ///     also measure a flat spectrum or smeared harmonics. That is the honest
    ///     line between "an amp pushed hard" (dynamics gone) and "distortion"
    ///     (the spectrum itself has changed).
    /// </summary>
    public const double CrunchDriveMax = 0.68;

    /// <summary>
    ///     Above this a drive pedal is added in front of the amp. The high-gain
    ///     amp's own preamp covers the band below it; past here the measurement
    ///     says more saturation than a single preamp stage gets to, and a pushed
    ///     front end is what a player actually does about that.
    /// </summary>
    public const double PedalDriveMin = 0.82;

    /// <summary>
    ///     A span may only be called <see cref="ToneArchetype.Direct" /> if its
    ///     centroid is at least this high. An amped electric guitar, even a bright
    ///     one, rarely centres above 2 kHz; an acoustic, a piezo or a DI'd clean
    ///     does. Paired with <see cref="DirectHighEnergyMin" /> so a single thin
    ///     mix does not trigger it.
    /// </summary>
    public const double DirectCentroidHz = 2000;

    /// <inheritdoc cref="DirectCentroidHz" />
    public const double DirectHighEnergyMin = 0.22;

    /// <summary>
    ///     Centroid mapped to 0 for the tone stack. Below ~250 Hz nothing that
    ///     reaches this mapper is a guitar part — the treble knob should already
    ///     be at its floor.
    /// </summary>
    public const double DarkCentroidHz = 250;

    /// <summary>
    ///     Centroid mapped to 1. Above ~3.5 kHz the extra brightness is cymbal
    ///     bleed or separator artefact far more often than it is guitar, so the
    ///     knob pins rather than chasing it.
    /// </summary>
    public const double BrightCentroidHz = 3500;

    /// <summary>Brightness at or below this puts the mic off-axis (the darkest of
    /// the three positions).</summary>
    public const double MicOffAxisMaxBrightness = 1.0 / 3.0;

    /// <summary>Brightness at or below this puts the mic at the cone edge; above
    /// it the mic goes on the cone, which is the brightest position.</summary>
    public const double MicEdgeMaxBrightness = 2.0 / 3.0;

    // The tone stack never uses the outer 15% of a knob's travel. A generated
    // tone the charter cannot turn DOWN is as unhelpful as a wrong one, and a
    // knob sitting on a hard stop reads as "the app is sure", which it is not.
    private const double StackFloor = 0.15;
    private const double StackCeiling = 0.85;

    // The drive pedal's own gain window, as a fraction of its catalogue range.
    private const double PedalGainLo = 0.25;
    private const double PedalGainHi = 0.85;

    /// <summary>The drive pedal added above <see cref="PedalDriveMin" />. The
    /// catalogue's distortion pedals expose Gain and Tone (there is no Level pot
    /// on any of them), so those are the two this mapper sets.</summary>
    public const string DrivePedalKey = "Pedal_SuperDrive";

    /// <summary>
    ///     One row of the archetype table: which gear the family uses, the
    ///     <see cref="ToneAudioProfile.Drive" /> band that selects it, and the
    ///     fraction of the amp's Gain travel that band maps onto.
    /// </summary>
    private readonly record struct ArchetypeSpec(
        ToneArchetype Archetype,
        string Display,
        string AmpKey,
        string CabModelKey,
        double DriveMin,
        double DriveMax,
        double GainLo,
        double GainHi,
        string DescriptorUIName);

    // The gain windows are disjoint and ascending across the whole table — that
    // is what makes the monotonicity guarantee hold ACROSS a boundary and not
    // just inside a band. Direct sits below Clean on purpose: it shares Clean's
    // Drive band, and is only ever entered/left by the spectral-shape gate,
    // which the guarantee holds fixed.
    private static readonly ArchetypeSpec DirectSpec = new(
        ToneArchetype.Direct, "Direct",
        "DI_Amp_TubePre", "Cab_PA600C",
        0.0, CleanDriveMax, 0.02, 0.12,
        "$[35752]DIRECT");

    private static readonly ArchetypeSpec[] DriveTable =
    [
        new(ToneArchetype.Clean, "Clean",
            "Amp_TW26", "Cab_TW410C",
            0.0, CleanDriveMax, 0.14, 0.32,
            RsToneAuthoring.CleanDescriptorUIName),
        new(ToneArchetype.EdgeOfBreakup, "Edge of breakup",
            "Amp_MarshallJTM45", "Cab_Marshall1960AX",
            CleanDriveMax, EdgeDriveMax, 0.34, 0.52,
            "$[35716]OVERDRIVE"),
        new(ToneArchetype.Crunch, "Crunch",
            "Amp_MarshallJCM800", "Cab_Marshall1960TV",
            EdgeDriveMax, CrunchDriveMax, 0.54, 0.74,
            "$[27156]CRUNCH"),
        new(ToneArchetype.HighGain, "High gain",
            "Amp_MarshallDSL100H", "Cab_Marshall1960a",
            CrunchDriveMax, 1.0, 0.76, 0.98,
            "$[35755]HIGH GAIN")
    ];

    /// <summary>
    ///     Builds a tone from one span's measurements.
    ///     <para>
    ///         Returns <c>null</c> — and only — when <see cref="ToneAudioProfile.IsSilent" />.
    ///         <b>Silence is refused rather than answered.</b> A "neutral" tone would
    ///         sit in the tone list looking exactly like a generated one, and the
    ///         charter would have no way to tell that the span the app measured had
    ///         nothing in it. The caller must say so instead of placing a tone.
    ///     </para>
    /// </summary>
    /// <param name="profile">The span's measurements.</param>
    /// <param name="catalog">The gear catalogue, normally <see cref="RsGearCatalog.Shared" />.
    ///     An empty catalogue degrades to gear with no knob values rather than throwing.</param>
    /// <param name="key">Desired tone key; folded through <see cref="RsToneAuthoring.SanitizeKey" />.</param>
    /// <param name="name">Display name. Blank falls back to a name that names its own
    ///     origin, e.g. <c>"Crunch (from audio)"</c>.</param>
    public static ToneSuggestion? Build(ToneAudioProfile profile, RsGearCatalog catalog, string? key, string? name)
    {
        if (profile.IsSilent) return null;

        var drive = Math.Clamp(profile.Drive, 0, 1);
        var spec = SelectArchetype(profile, drive);

        var brightness = Brightness(profile);
        var withPedal = drive >= PedalDriveMin;

        var amp = BuildAmp(catalog, spec, profile, drive, brightness);
        var cab = BuildCab(catalog, spec.CabModelKey, brightness);
        var prePedals = withPedal
            ? [BuildDrivePedal(catalog, drive, brightness)]
            : Array.Empty<RsPedal>();

        var descriptors = withPedal
            ? new[] { spec.DescriptorUIName, "$[35722]DISTORTION" }
            : [spec.DescriptorUIName];

        var tone = new RsTone(
            RsToneAuthoring.SanitizeKey(key),
            string.IsNullOrWhiteSpace(name) ? spec.Display + " (from audio)" : name!,
            RsToneAuthoring.ClampVolumeDb(RsToneAuthoring.DefaultVolumeDb),
            descriptors,
            amp,
            cab,
            prePedals,
            Array.Empty<RsPedal>(),
            Array.Empty<RsPedal>());

        return new ToneSuggestion(tone, spec.Archetype, Explain(spec, profile, drive, cab, withPedal));
    }

    // ------------------------------------------------------------- selection

    private static ArchetypeSpec SelectArchetype(ToneAudioProfile profile, double drive)
    {
        // The Direct gate deliberately uses ONLY measurements that Drive does not
        // consume (centroid + the high band). Because those are held fixed by the
        // monotonicity guarantee, a Drive sweep can never flip into Direct and
        // drop the gain knob.
        if (drive < CleanDriveMax
            && profile.SpectralCentroidHz >= DirectCentroidHz
            && profile.HighEnergy >= DirectHighEnergyMin)
        {
            return DirectSpec;
        }

        foreach (var spec in DriveTable)
        {
            if (drive < spec.DriveMax)
                return spec;
        }

        return DriveTable[^1];
    }

    // ------------------------------------------------------------------ gear

    private static RsAmp BuildAmp(
        RsGearCatalog catalog, ArchetypeSpec spec, ToneAudioProfile profile, double drive, double brightness)
    {
        var knobs = SeedKnobs(catalog, spec.AmpKey);

        // Gain: where this Drive sits inside the archetype's own band, mapped
        // onto that archetype's slice of the Gain pot's travel.
        var within = spec.DriveMax > spec.DriveMin
            ? (drive - spec.DriveMin) / (spec.DriveMax - spec.DriveMin)
            : 0.0;
        SetKnob(catalog, spec.AmpKey, "Gain", Lerp(spec.GainLo, spec.GainHi, within), knobs);

        // The tone stack simply describes what was measured: a bass-heavy span
        // gets the Bass knob up, because that is the knob a guitarist reaches for
        // when they want the sound they just heard.
        SetKnob(catalog, spec.AmpKey, "Bass", InStackWindow(BandPosition(profile.LowEnergy)), knobs);
        SetKnob(catalog, spec.AmpKey, "Mid", InStackWindow(BandPosition(profile.MidEnergy)), knobs);

        // Treble blends the >2 kHz share with the centroid: both speak to the top
        // end, and blending stops one separator artefact from owning the knob.
        SetKnob(catalog, spec.AmpKey, "Treble",
            InStackWindow(0.5 * BandPosition(profile.HighEnergy) + 0.5 * brightness), knobs);

        // Presence is a power-amp control living above the treble band; the
        // centroid is the only measurement that speaks to it at all.
        SetKnob(catalog, spec.AmpKey, "Pres", InStackWindow(brightness), knobs);

        // Resonance is its low-end mirror. Only some amps have one — a gear
        // without the pot is skipped here, never invented.
        SetKnob(catalog, spec.AmpKey, "Res", InStackWindow(BandPosition(profile.LowEnergy)), knobs);

        var category = catalog.GetCategory(spec.AmpKey) ?? "Amp";
        return new RsAmp(spec.AmpKey, category, knobs);
    }

    /// <summary>
    ///     Cabinets carry no knobs, so the only lever is WHICH of the 450 entries
    ///     to name — and the catalogue is one cabinet model per mic type per mic
    ///     position. So: the MODEL follows the amp — each archetype names a cab
    ///     from that amp's OWN family, which for the three Marshalls is also the
    ///     period-correct pairing (greenback 1960AX behind the JTM45, the darker
    ///     TV behind the JCM800's crunch band, the tighter 1960A behind the DSL).
    ///     The mic POSITION then follows
    ///     the measured brightness, cone → edge → off-axis being exactly the
    ///     bright → dark trade an engineer makes with a mic. The mic TYPE is fixed
    ///     to the dynamic (57) capture: it is the near-universal guitar-cab mic,
    ///     and we have one brightness measurement, not two, so spending it twice
    ///     would just be an arbitrary pick wearing a rule's clothes.
    /// </summary>
    private static RsCab BuildCab(RsGearCatalog catalog, string cabModelKey, double brightness)
    {
        var position = brightness <= MicOffAxisMaxBrightness ? "OffAxis"
            : brightness <= MicEdgeMaxBrightness ? "Edge"
            : "Cone";

        string[] candidates =
        [
            cabModelKey + "_57_" + position,
            cabModelKey,                    // PA cabs have no mic variants
            cabModelKey + "_57_Edge",
            RsToneJsonParser.DefaultCabinetKey
        ];

        foreach (var candidate in candidates)
        {
            if (catalog.TryGetGear(candidate, out var gear))
                return new RsCab(gear.Key, gear.Category, EmptyKnobs);
        }

        // Empty catalogue: still emit a structurally valid tone.
        return new RsCab(RsToneJsonParser.DefaultCabinetKey, "Dynamic Cone", EmptyKnobs);
    }

    private static RsPedal BuildDrivePedal(RsGearCatalog catalog, double drive, double brightness)
    {
        var knobs = SeedKnobs(catalog, DrivePedalKey);

        var above = (drive - PedalDriveMin) / (1.0 - PedalDriveMin);
        SetKnob(catalog, DrivePedalKey, "Gain", Lerp(PedalGainLo, PedalGainHi, above), knobs);
        SetKnob(catalog, DrivePedalKey, "Tone", InStackWindow(brightness), knobs);

        var category = catalog.GetCategory(DrivePedalKey) ?? "Distortion";
        return new RsPedal(RsGearRole.PrePedal, 1, DrivePedalKey, RsGearType.Pedal, category, knobs);
    }

    // ----------------------------------------------------------------- knobs

    private static readonly IReadOnlyDictionary<string, float> EmptyKnobs =
        new Dictionary<string, float>(StringComparer.Ordinal);

    /// <summary>Every knob the gear declares, at its catalogue default, inserted
    /// in the catalogue's own knob order (so the written JSON is stable). An
    /// unknown gear key yields an empty set rather than throwing.</summary>
    private static Dictionary<string, float> SeedKnobs(RsGearCatalog catalog, string gearKey)
    {
        var knobs = new Dictionary<string, float>(StringComparer.Ordinal);
        if (catalog.TryGetGear(gearKey, out var gear) && gear.Knobs is { } declared)
        {
            foreach (var knob in declared)
                knobs[knob.Key] = knob.DefaultValue;
        }

        return knobs;
    }

    /// <summary>
    ///     Writes one knob at position <paramref name="t" /> (0..1) of its
    ///     CATALOGUE range, snapped to the knob's own step and clamped to its own
    ///     bounds. A knob the chosen gear does not declare is skipped — inventing
    ///     one is the failure that ships a tone the game rejects.
    /// </summary>
    private static void SetKnob(
        RsGearCatalog catalog, string gearKey, string pot, double t, Dictionary<string, float> knobs)
    {
        if (!catalog.TryGetKnob(gearKey, pot, out var knob)) return;

        var lo = MathF.Min(knob.MinValue, knob.MaxValue);
        var hi = MathF.Max(knob.MinValue, knob.MaxValue);
        var raw = lo + (float)Math.Clamp(t, 0, 1) * (hi - lo);

        if (knob.ValueStep > 0)
            raw = lo + MathF.Round((raw - lo) / knob.ValueStep) * knob.ValueStep;

        knobs[knob.Key] = RsToneAuthoring.ClampKnobValue(knob, raw);
    }

    // ------------------------------------------------------------- profiling

    /// <summary>Centroid folded to 0..1 between <see cref="DarkCentroidHz" /> and
    /// <see cref="BrightCentroidHz" />.</summary>
    private static double Brightness(ToneAudioProfile profile)
        => Math.Clamp(
            (profile.SpectralCentroidHz - DarkCentroidHz) / (BrightCentroidHz - DarkCentroidHz), 0, 1);

    /// <summary>A band's energy share folded to a knob position. The three shares
    /// sum to 1, so an even split is 1/3 — which maps to the middle of the knob's
    /// travel. A band holding two thirds of the energy or more pins it.</summary>
    private static double BandPosition(double share) => Math.Clamp(share * 1.5, 0, 1);

    private static double InStackWindow(double t)
        => StackFloor + (StackCeiling - StackFloor) * Math.Clamp(t, 0, 1);

    private static double Lerp(double lo, double hi, double t) => lo + (hi - lo) * Math.Clamp(t, 0, 1);

    private static string Explain(
        ArchetypeSpec spec, ToneAudioProfile profile, double drive, RsCab cab, bool withPedal)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{spec.Display}: drive {drive:0.00} (crest {profile.CrestFactorDb:0.0} dB, flatness {profile.SpectralFlatness:0.00}, centroid {profile.SpectralCentroidHz:0} Hz) → {spec.AmpKey} into {cab.GearKey}{(withPedal ? " with " + DrivePedalKey + " in front" : "")}. A starting point built from the span's audio, not a recovered rig.");
}
