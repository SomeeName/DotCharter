using System.Globalization;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Search;

/// <summary>One reason a rig is not a board a player would have built.</summary>
/// <param name="Code">A stable, machine-readable tag (<c>no-amp-no-cab</c>,
/// <c>two-amps</c>, …) so a UI or a report can group faults without parsing prose.</param>
/// <param name="Detail">The specific module(s), for a human reading the note.</param>
/// <param name="Weight">How much of the <c>[0,1]</c> penalty budget this fault
/// costs before the sum is clamped.</param>
public sealed record RigPlausibilityFault(string Code, string Detail, double Weight);

/// <summary>
///     <b>How far a rig is from the distribution <see cref="RigSampler" /> draws
///     from</b>, as a penalty in <c>[0,1]</c> — 0 for a board the sampler would
///     happily have produced, 1 for one it could not produce at all.
///     <para>
///         <b>Why this exists.</b> The first end-to-end measurement of the match
///         lane found the search BEATING the authored Rocksmith tone on
///         the metric with <c>octaver→klon→tapesat</c> — no amp, no cab — as top-1
///         for a clean tone, a chorus tone and an octave-distortion tone alike. The
///         search was doing its job; the metric was being gamed. `tonefx-v2`'s
///         measured content invariance is only ~1.56×, so a chain that happens to
///         land the stem's long-term spectrum can out-score a chain that is
///         physically the right answer. A ranking loss that knows nothing about
///         what a rig IS has no way to prefer the second.
///     </para>
///     <para>
///         <b>Why it is derived from the sampler and not hand-written.</b> The
///         retrieval library IS a sample from <see cref="RigSampler" />: its
///         archetype priors already encode an amp and a cab for a played guitar
///         unless the draw is <see cref="RigProfileKinds.Direct" />, bass gear for a
///         bass part, one amp at most, a mic only on a cabinet, each non-drive
///         family at most once, and a chain-length prior with no mass past 8. Every
///         rule below reads one of those structures — <see cref="RigSampler.TrueAmps" />,
///         <see cref="RigSampler.PreampTypeAmps" />, <see cref="RigSampler.FamilyOfTypeId" />,
///         <see cref="RigSampler.SamplableTypeIds" />, <see cref="RigSampler.BassModuleTypeIds" />,
///         <see cref="RigSampler.LengthPriorSupport" />, <see cref="RigSampler.MaxSampledDrives" />,
///         <see cref="RigSampler.CabDrawer" /> — rather than restating it. A second
///         hand-typed list would rot the day an archetype changed, and the rot would
///         be silent: the penalty would keep producing plausible numbers.
///     </para>
///     <para>
///         <b>It is a PRIOR, not a filter.</b> Nothing is removed from the shortlist
///         and no candidate is refused; the penalty multiplies the ranking distance
///         (<c>distance × (1 + w × penalty)</c>), so a rig that is measurably much
///         closer can still win while paying for its implausibility. A no-amp,
///         no-cab chain must beat an amp rig by more than 1.5× to take a guitar
///         part, which is exactly the margin the gamed rigs did NOT have.
///     </para>
///     <para>
///         <b>The weights are calibrated on the one measurement there is.</b> In that
///         eval the gamed <c>octaver→klon→tapesat</c> scored 0.4909 against the
///         authored rig's 0.6953 — a 1.42× gap. The worst fault (no amp AND no cab on
///         a played part) is therefore worth 0.85, which at the matcher's default
///         weight of 0.6 is a 1.51× multiplier: enough to flip that ranking, and not
///         so much that a genuinely direct-recorded tone can never be found. Every
///         other weight is a fraction of it, in proportion to how strongly the
///         sampler's own probabilities argue against the shape.
///     </para>
/// </summary>
public static class RigPlausibility
{
    // --- weights ------------------------------------------------------------
    // Calibrated against the e2e measurement; see the type remarks.

    /// <summary>A played part through no amp AND no cabinet at all. The heaviest
    /// fault there is: every archetype except <c>direct</c> draws an amp with
    /// probability ≥ 0.62, and even <c>direct</c> reaches for a cab sim.</summary>
    public const double NoAmpNoCabWeight = 0.85;

    /// <summary>No amp, but a cab sim — how a lot of direct recording is genuinely
    /// done, and something the sampler draws on purpose (<c>PCab × 0.45</c> when the
    /// amp slot came up empty). A real shape, just an uncommon one.</summary>
    public const double NoAmpWeight = 0.35;

    /// <summary>A SPEAKER amp with nothing in front of it. <c>PCab</c> is ≥ 0.76 in
    /// every archetype, and a cab-less speaker amp is the single most recognisable
    /// "this was never plugged in" sound there is.</summary>
    public const double NoCabWeight = 0.30;

    /// <summary>A bass part through a guitar amp. The <c>bass</c> archetype's amp
    /// pool is <c>bassamp</c>/<c>poweramp</c> and nothing else.</summary>
    public const double GuitarAmpOnBassWeight = 0.40;

    /// <summary>A bass part with no bass-voiced module anywhere.</summary>
    public const double NoBassGearWeight = 0.25;

    /// <summary>A module the sampler cannot draw at all — excluded, series-excluded,
    /// or not in the vocabulary. The library contains no such row by construction,
    /// so this can only reach the ranking through a prior or an estimator.</summary>
    public const double UnsamplableWeight = 0.50;

    /// <summary>Two amplifiers. The sampler's "never two amps" rule, and one it was
    /// fixed once already to enforce properly.</summary>
    public const double TwoAmpsWeight = 0.50;

    /// <summary>A microphone position with no cabinet under it. The sampler deletes
    /// <c>micpos</c> whenever the trim drops the cab.</summary>
    public const double MicWithoutCabWeight = 0.25;

    /// <summary>A cabinet in FRONT of the amp feeding it. The one adjacent swap the
    /// sampler's order-deviation is forbidden from making
    /// (<c>IsAmpCabSeam</c>) — a board no player wired.</summary>
    public const double CabBeforeAmpWeight = 0.30;

    /// <summary>Per repeated family beyond the first. Deliberately SMALL and capped:
    /// the sampler adds each non-drive family once, but real authored Rocksmith rigs
    /// do carry e.g. a <c>graphiceq</c> and an <c>eq</c> together, and a penalty that
    /// punished the ground truth would be the same mistake in the other
    /// direction.</summary>
    public const double DuplicateFamilyWeight = 0.12;

    /// <summary>Ceiling on the duplicate-family group.</summary>
    public const double DuplicateFamilyCap = 0.25;

    /// <summary>Per module past <see cref="RigSampler.LengthPriorSupport" />, where
    /// the chain-length prior has no mass at all.</summary>
    public const double OverLengthWeight = 0.12;

    /// <summary>
    ///     The penalty, in <c>[0,1]</c>.
    /// </summary>
    /// <param name="rig">The candidate chain.</param>
    /// <param name="stemHint">One of <see cref="ToneStems" />, or null. <b>Null is
    /// treated as <see cref="ToneStems.Guitar" /></b>: the panel's "match from audio"
    /// does not set a hint and its target is always a played guitar part, so
    /// defaulting to "no instrument expectation" would switch the whole feature's
    /// main path off. <see cref="ToneStems.Piano" /> and <see cref="ToneStems.Other" />
    /// carry no amp/cab expectation and are scored on structure alone.</param>
    /// <returns>0 for a rig the sampler would draw, up to 1.</returns>
    public static double Penalty(DcRigPreset rig, string? stemHint = null)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var total = 0.0;
        foreach (var fault in Explain(rig, stemHint)) total += fault.Weight;
        return Math.Clamp(total, 0.0, 1.0);
    }

    /// <summary>The faults behind <see cref="Penalty" />, so a note, a report or a
    /// tooltip can say WHY a rig was marked down instead of showing a bare
    /// number.</summary>
    /// <param name="rig">The candidate chain.</param>
    /// <param name="stemHint">See <see cref="Penalty" />.</param>
    public static IReadOnlyList<RigPlausibilityFault> Explain(DcRigPreset rig, string? stemHint = null)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var faults = new List<RigPlausibilityFault>();
        var ids = rig.Modules.Select(m => m.ModuleTypeId).ToList();

        var isBass = string.Equals(stemHint, ToneStems.Bass, StringComparison.OrdinalIgnoreCase);
        var isPlayedInstrument = isBass ||
                                 stemHint is null ||
                                 string.Equals(stemHint, ToneStems.Guitar, StringComparison.OrdinalIgnoreCase);

        var amps = ids.Where(RigSampler.TrueAmps.Contains).ToList();
        var hasCab = ids.Contains("cab", StringComparer.Ordinal);
        var hasCabOrSim = hasCab || ids.Contains("acousticsim", StringComparer.Ordinal);

        // --- (A) the amp/cab expectation, for a played part ----------------------
        if (isPlayedInstrument)
        {
            if (amps.Count == 0 && !hasCabOrSim)
            {
                faults.Add(new RigPlausibilityFault(
                    "no-amp-no-cab", "no amplifier and no cabinet", NoAmpNoCabWeight));
            }
            else if (amps.Count == 0)
            {
                faults.Add(new RigPlausibilityFault(
                    "no-amp", "a cabinet with no amplifier in front of it", NoAmpWeight));
            }
            else if (!hasCabOrSim && amps.Any(a => !RigSampler.PreampTypeAmps.Contains(a)))
            {
                faults.Add(new RigPlausibilityFault(
                    "no-cab", $"'{amps[0]}' is a speaker amp with no cabinet", NoCabWeight));
            }
        }

        // --- (B) the instrument -------------------------------------------------
        if (isBass)
        {
            var wrongAmp = amps.FirstOrDefault(a => !RigSampler.BassAmpTypeIds.Contains(a, StringComparer.Ordinal));
            if (wrongAmp is not null)
            {
                faults.Add(new RigPlausibilityFault(
                    "guitar-amp-on-bass", $"'{wrongAmp}' on a bass part", GuitarAmpOnBassWeight));
            }

            if (!ids.Any(RigSampler.BassModuleTypeIds.Contains))
            {
                faults.Add(new RigPlausibilityFault(
                    "no-bass-gear", "no bass-voiced module on a bass part", NoBassGearWeight));
            }
        }

        // --- (C) shapes the sampler cannot produce ------------------------------
        var samplable = new HashSet<string>(RigSampler.SamplableTypeIds, StringComparer.Ordinal);
        var alien = ids.Where(id => !samplable.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
        if (alien.Count > 0)
        {
            faults.Add(new RigPlausibilityFault(
                "unsamplable", string.Join(", ", alien), UnsamplableWeight));
        }

        if (amps.Count > 1)
        {
            faults.Add(new RigPlausibilityFault(
                "two-amps", string.Join(" + ", amps), TwoAmpsWeight));
        }

        if (ids.Contains("micpos", StringComparer.Ordinal) && !hasCab)
        {
            faults.Add(new RigPlausibilityFault(
                "mic-without-cab", "a microphone position with no cabinet", MicWithoutCabWeight));
        }

        // The one order the sampler protects absolutely. `acousticsim` is NOT in it:
        // an acoustic simulator at the front of a board is a real rig, and the
        // converter emits exactly that for Rocksmith's acoustic tones.
        if (amps.Count > 0 && hasCab)
        {
            var firstAmp = ids.FindIndex(RigSampler.TrueAmps.Contains);
            var firstCab = ids.IndexOf("cab");
            if (firstCab >= 0 && firstCab < firstAmp)
            {
                faults.Add(new RigPlausibilityFault(
                    "cab-before-amp", "the cabinet is in front of the amplifier", CabBeforeAmpWeight));
            }
        }

        // --- (D) repeated families ----------------------------------------------
        // The cab DRAWER is exempt: `cab` + `micpos` + `acousticsim` share one family
        // by filing, not by function, and the rules above already police them.
        // `drive` is exempt up to the most any archetype draws.
        var repeated = ids
            .Where(id => !RigSampler.CabDrawer.Contains(id))
            .Select(id => RigSampler.FamilyOfTypeId.TryGetValue(id, out var f) ? f : "?")
            .GroupBy(f => f, StringComparer.Ordinal)
            .Select(g => (Family: g.Key, Excess: g.Count() -
                                         (string.Equals(g.Key, "drive", StringComparison.Ordinal)
                                             ? RigSampler.MaxSampledDrives
                                             : 1), Count: g.Count()))
            .Where(x => x.Excess > 0)
            .ToList();

        if (repeated.Count > 0)
        {
            // ONE fault for the whole group, capped: several repeated families are one
            // symptom ("this chain is a pile"), not N independent ones.
            faults.Add(new RigPlausibilityFault(
                "duplicate-family",
                string.Join(", ", repeated.Select(x =>
                    string.Create(CultureInfo.InvariantCulture, $"{x.Count} × {x.Family}"))),
                Math.Min(repeated.Sum(x => x.Excess) * DuplicateFamilyWeight, DuplicateFamilyCap)));
        }

        // --- (E) length ----------------------------------------------------------
        var over8 = ids.Count - RigSampler.LengthPriorSupport;
        if (over8 > 0)
        {
            faults.Add(new RigPlausibilityFault(
                "over-length",
                string.Create(CultureInfo.InvariantCulture,
                    $"{ids.Count} modules, prior has no mass past {RigSampler.LengthPriorSupport}"),
                over8 * OverLengthWeight));
        }

        return faults;
    }
}
