// RigSampler part 3 of 3 — the ALGORITHM. Part 1 is Vocabulary (families, order,
// archetype weights), part 2 is Knobs (the priors).

using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match;

/// <summary>
///     Draws rigs a guitarist would actually build, deterministically for a seed.
///     <para>
///         This is the training corpus's LABEL distribution, and that makes the priors
///         load-bearing rather than cosmetic. A sampler that drew uniformly over 94
///         modules and 578 knobs would produce noise, and a model trained on noise
///         learns to recognise the ARTEFACTS of noise — every paper in the
///         audio-to-parameters literature that skipped this step reports the same
///         failure. So: a chain-length prior (mostly 2–5), a family-order prior (the
///         canonical pedalboard order the AI tone prompt already states, deviating
///         about one draw in fourteen), one amp at most, a cab whenever the amp wants
///         one, six player archetypes with their own drive windows and family
///         probabilities, and per-knob Beta priors that put an amp's gain in the
///         middle, a delay's time on a subdivision of a beat, and a modulation mix
///         well short of 100 %.
///     </para>
///     <para>
///         Determinism is by construction: every decision comes from the caller's
///         <see cref="Random" /> in a fixed order, and nothing reads a clock, a file
///         (except the NAM capture list, which is sorted) or an environment variable.
///         Two <c>new Random(s)</c> produce two bit-identical rigs.
///     </para>
/// </summary>
public sealed partial class RigSampler : IRigSampler
{
    /// <summary>How often the canonical family order is deliberately broken. Real
    /// boards do it — a fuzz after the amp's preamp, a compressor last, a delay into
    /// a reverb the other way round — and a corpus whose order is a hard rule teaches
    /// the model the rule rather than the sound. One adjacent swap, ~7 % of draws, so
    /// the order prior still holds in well over 90 %.</summary>
    private const double OrderDeviationChance = 0.07;

    /// <summary>How often a rig gets one module drawn uniformly from the WHOLE
    /// vocabulary instead of from its family prior. This is what guarantees the tail:
    /// without it a module the archetypes never reach for (a barberpole phaser, a
    /// twelve-string) would be absent from a 50,000-clip corpus, and the model could
    /// never name it. At 15 % it is ~16 appearances per module per 10,000 draws.</summary>
    private const double WildcardChance = 0.15;

    /// <summary>Longest chain the sampler will build regardless of the profile — the
    /// engine takes more, but a rig a listener could pick apart does not.</summary>
    public const int HardMaxModules = 12;

    /// <inheritdoc />
    public DcRigPreset Sample(Random rng, RigSamplingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(profile);

        var kind = ResolveKind(rng, profile);
        var arch = Archetypes[kind];
        var namCaptures = LoadNamCaptures(profile);
        var allowed = AllowedSet(profile, namCaptures.Count > 0);
        // The bass archetype never draws the GUITAR amp. Its amp slot draws from
        // BassAmps, but the wildcard tail and GrowToMinimum draw from `allowed`, so
        // one bass rig in ~a thousand without a bass amp could take `amp` there —
        // found when the micpos knob moved the seeded draw stream onto it
        // (RigSamplerTests.TheBassArchetype_ReachesForABassAmp_AndNeverAGuitarAmp).
        if (kind == RigProfileKinds.Bass) allowed.Remove("amp");

        var minModules = Math.Max(1, profile.MinModules);
        var maxModules = Math.Clamp(profile.MaxModules, minModules, HardMaxModules);
        var target = SampleChainLength(rng, minModules, maxModules);

        var picked = new List<string>(target);

        // --- the amp slot first: everything else is conditioned on it -----------
        string? amp = null;
        if (rng.NextDouble() < arch.PAmp)
        {
            amp = PickWeighted(rng, arch.Amps
                .Where(a => allowed.Contains(a.Id))
                .Where(a => a.Id != "namamp" || namCaptures.Count > 0)
                .Select(a => (a.Id, a.W)));
            if (amp is not null) picked.Add(amp);
        }

        // A cab whenever the amp is a speaker amp — the prompt's own rule, sampled.
        // An acoustic preamp reaches for `acousticsim` instead, which is the same
        // idea one drawer over.
        if (amp is not null && !DiTypeAmps.Contains(amp) && rng.NextDouble() < arch.PCab)
        {
            if (amp == "acpre" && allowed.Contains("acousticsim")) picked.Add("acousticsim");
            else if (allowed.Contains("cab")) picked.Add("cab");
            if (picked.Contains("cab") && allowed.Contains("micpos") && rng.NextDouble() < arch.PMicPos)
                picked.Add("micpos");
        }
        else if (amp is null && rng.NextDouble() < arch.PCab * 0.45)
        {
            // An amp-less rig with a cab sim is how a lot of direct recording is done.
            if (allowed.Contains("cab")) picked.Add("cab");
        }

        // --- drives -------------------------------------------------------------
        var drives = PickCount(rng, arch.DriveCountWeights);
        for (var i = 0; i < drives; i++)
            AddFrom(rng, picked, allowed, "drive", arch.DriveBias);

        // --- everything else, each family at most once --------------------------
        if (rng.NextDouble() < arch.PDynamics) AddFrom(rng, picked, allowed, "dynamics", arch.DynamicsBias);
        if (rng.NextDouble() < arch.PPitch) AddFrom(rng, picked, allowed, "pitch", NoBias);
        if (rng.NextDouble() < arch.PEq) AddFrom(rng, picked, allowed, "eq", NoBias);
        if (rng.NextDouble() < arch.PFilter) AddFrom(rng, picked, allowed, "filter", NoBias);
        if (rng.NextDouble() < arch.PModulation) AddFrom(rng, picked, allowed, "modulation", NoBias);
        if (rng.NextDouble() < arch.PDelay) AddFrom(rng, picked, allowed, "delay", NoBias);
        if (rng.NextDouble() < arch.PReverb) AddFrom(rng, picked, allowed, "reverb", NoBias);
        if (rng.NextDouble() < arch.PUtility) AddFrom(rng, picked, allowed, "utility", NoBias);

        // --- the tail: one uniform draw so no module is unreachable -------------
        if (rng.NextDouble() < WildcardChance)
        {
            var pool = allowed.Where(id => !picked.Contains(id)).ToArray();
            if (pool.Length > 0)
            {
                var wild = pool[rng.Next(pool.Length)];
                // Never a second amp, and the check is TrueAmps rather than the "amp"
                // FAMILY: `tubepre` is filed under drive and `poweramp` under amp, so a
                // family test let the wildcard put a poweramp behind a tubepre amp slot
                // and called it one amp.
                if (!(TrueAmps.Contains(wild) && picked.Any(TrueAmps.Contains))) picked.Add(wild);
            }
        }

        // --- fit the length prior ----------------------------------------------
        TrimToTarget(rng, picked, target, amp);
        GrowToMinimum(rng, picked, allowed, minModules, amp);

        // --- order ---------------------------------------------------------------
        var ordered = picked
            .OrderBy(OrderKey, TupleComparer.Instance)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count > 1 && rng.NextDouble() < OrderDeviationChance)
        {
            var i = rng.Next(ordered.Count - 1);
            // A cab must stay behind its amp and a mic behind its cab: swapping those
            // is not a board a player built, it is a rig the renderer would fold.
            if (!IsAmpCabSeam(ordered[i], ordered[i + 1]))
                (ordered[i], ordered[i + 1]) = (ordered[i + 1], ordered[i]);
        }

        // --- knobs ---------------------------------------------------------------
        var modules = new List<DcRigModule>(ordered.Count);
        foreach (var typeId in ordered)
        {
            var values = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var (id, specs) in DcRigTargetCatalog.AllModules)
            {
                if (!string.Equals(id, typeId, StringComparison.Ordinal)) continue;
                foreach (var spec in specs)
                    values[spec.Id] = SampleParam(rng, typeId, spec.Id, spec, kind);
                break;
            }

            Dictionary<string, string>? strings = null;
            if (typeId == "namamp" && namCaptures.Count > 0)
            {
                strings = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["modelPath"] = namCaptures[rng.Next(namCaptures.Count)]
                };
            }

            modules.Add(new DcRigModule(typeId, values) { StringParams = strings });
        }

        return new DcRigPreset(modules);
    }

    /// <summary>The archetype this draw used. Exposed so the dataset tool can record
    /// it on the manifest row without re-deriving it from the chain (which is exactly
    /// the inference the model is being trained to make, and would be circular).</summary>
    public static string ResolveKind(Random rng, RigSamplingProfile profile)
    {
        if (!string.Equals(profile.Kind, RigProfileKinds.Mixed, StringComparison.Ordinal))
            return Archetypes.ContainsKey(profile.Kind) ? profile.Kind : RigProfileKinds.Crunch;

        var roll = rng.NextDouble();
        var acc = 0d;
        foreach (var (kind, w) in ArchetypeWeights)
        {
            acc += w;
            if (roll < acc && (profile.AllowBass || kind != RigProfileKinds.Bass)) return kind;
        }

        return RigProfileKinds.Crunch;
    }

    // ---------------------------------------------------------------- helpers

    private static HashSet<string> AllowedSet(RigSamplingProfile profile, bool haveCaptures)
    {
        var set = new HashSet<string>(SamplableTypeIds, StringComparer.Ordinal);
        if (profile.OnlyTypeIds is { Count: > 0 }) set.IntersectWith(profile.OnlyTypeIds);
        if (!profile.AllowBass)
        {
            set.Remove("bassamp");
            set.Remove("bassod");
            set.Remove("basssynth");
        }

        // `namamp` is gated on the CAPTURES, not on the flag: the flag says "you may",
        // an empty library says "there is nothing to point at", and a namamp whose
        // modelPath names nothing is a label the renderer cannot honour. It leaked in
        // through the wildcard draw until this moved from the flag to the list.
        if (!profile.AllowNamCaptures || !haveCaptures) set.Remove("namamp");
        return set;
    }

    /// <summary>The capture files a <c>namamp</c> label may name, sorted so the choice
    /// is reproducible across machines with the same library. A namamp with no capture
    /// is a module whose whole voicing is undefined, so an empty list disables it.</summary>
    private static IReadOnlyList<string> LoadNamCaptures(RigSamplingProfile profile)
    {
        if (!profile.AllowNamCaptures || string.IsNullOrWhiteSpace(profile.NamCaptureDirectory))
            return [];
        try
        {
            if (!Directory.Exists(profile.NamCaptureDirectory)) return [];
            return Directory.GetFiles(profile.NamCaptureDirectory, "*.nam", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    ///     The chain-length prior: 2–5 modules carry ~79 % of the mass, which is the
    ///     shape of the 166-preset factory library (2–5 is 141 of its 166 rigs) and of
    ///     the 125 converted Rocksmith tones. One module is a pedal demo and eight is
    ///     a pedalboard photo, not a tone.
    /// </summary>
    private static readonly double[] LengthWeights =
        [0.05, 0.16, 0.24, 0.22, 0.17, 0.09, 0.05, 0.02];

    private static int SampleChainLength(Random rng, int min, int max)
    {
        var roll = rng.NextDouble();
        var acc = 0d;
        var n = 3;
        for (var i = 0; i < LengthWeights.Length; i++)
        {
            acc += LengthWeights[i];
            if (roll < acc) { n = i + 1; break; }
            n = i + 1;
        }

        return Math.Clamp(n, min, max);
    }

    private static int PickCount(Random rng, IReadOnlyList<double> weights)
    {
        var roll = rng.NextDouble();
        var acc = 0d;
        for (var i = 0; i < weights.Count; i++)
        {
            acc += weights[i];
            if (roll < acc) return i;
        }

        return weights.Count - 1;
    }

    private static void AddFrom(Random rng, List<string> picked, HashSet<string> allowed,
        string family, (string Id, double W)[] bias)
    {
        if (!ByFamily.TryGetValue(family, out var pool)) return;
        var biasMap = bias.Length == 0 ? null : bias.ToDictionary(b => b.Id, b => b.W, StringComparer.Ordinal);
        var candidates = pool
            .Where(id => allowed.Contains(id) && !picked.Contains(id))
            .Select(id => (id, w: biasMap is not null && biasMap.TryGetValue(id, out var bw) ? bw : WeightOf(id) * 0.25));
        var choice = PickWeighted(rng, candidates);
        if (choice is not null) picked.Add(choice);
    }

    private static string? PickWeighted(Random rng, IEnumerable<(string Id, double W)> items)
    {
        var list = items.Where(i => i.W > 0d).ToArray();
        if (list.Length == 0) return null;
        var total = list.Sum(i => i.W);
        var roll = rng.NextDouble() * total;
        var acc = 0d;
        foreach (var (id, w) in list)
        {
            acc += w;
            if (roll < acc) return id;
        }

        return list[^1].Id;
    }

    /// <summary>Drops modules until the chain is the length the prior asked for,
    /// least structural first: a utility before a modulation before a drive, and the
    /// amp and its cab last of all — losing the amp is losing the tone.</summary>
    private static void TrimToTarget(Random rng, List<string> picked, int target, string? amp)
    {
        while (picked.Count > target)
        {
            var victim = picked
                .Where(id => id != amp && id != "cab")
                .OrderByDescending(id => OrderKey(id).Family == FamilyRank["utility"] ? 3 : 0)
                .ThenByDescending(id => OrderKey(id).Family)
                .FirstOrDefault();
            victim ??= picked.FirstOrDefault(id => id != amp);
            if (victim is null) break;
            picked.Remove(victim);
            if (victim == "cab") picked.Remove("micpos");
        }

        // A mic with no cabinet under it is not a rig.
        if (!picked.Contains("cab")) picked.Remove("micpos");
        _ = rng;
    }

    private static void GrowToMinimum(Random rng, List<string> picked, HashSet<string> allowed,
        int min, string? amp)
    {
        var guard = 0;
        while (picked.Count < min && guard++ < 32)
        {
            var hasAmp = picked.Any(TrueAmps.Contains);
            var pool = allowed
                .Where(id => !picked.Contains(id))
                .Where(id => !(TrueAmps.Contains(id) && hasAmp))
                .Where(id => id != "micpos")
                .ToArray();
            if (pool.Length == 0) break;
            picked.Add(pool[rng.Next(pool.Length)]);
        }
    }

    private static bool IsAmpCabSeam(string a, string b)
        => (FamilyOf[a] == "amp" && FamilyOf[b] == "cab")
           || (a == "cab" && b is "micpos" or "acousticsim");

    private sealed class TupleComparer : IComparer<(int Family, int Sub)>
    {
        public static readonly TupleComparer Instance = new();

        public int Compare((int Family, int Sub) x, (int Family, int Sub) y)
            => x.Family != y.Family ? x.Family.CompareTo(y.Family) : x.Sub.CompareTo(y.Sub);
    }
}
