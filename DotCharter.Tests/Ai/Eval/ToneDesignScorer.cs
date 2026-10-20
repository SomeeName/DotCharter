using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     Grades a designed tone against a Rocksmith tone converted by
///     <see cref="RsToneConverter" />. Pure, deterministic, no I/O, no engine —
///     it compares two <see cref="DcRigPreset" />s and nothing else, which is what
///     lets the offline half of the eval run in the default gate.
///     <para>
///         <b>It does not claim to measure how a tone SOUNDS.</b> It measures how
///         close the CHAIN is to the one DotCharter builds for the real in-game
///         tone. Two chains can score 60 and sound nearly identical, or score 90
///         and not. The number is a regression signal and a model-vs-model
///         comparison, not a verdict — see <c>tools/ai-eval/README.md</c>.
///     </para>
/// </summary>
public static class ToneDesignScorer
{
    /// <summary>Scores a designed preset against the truth rig.</summary>
    public static ToneScore Score(DspTonePreset designed, DcRigPreset truth)
        => Score(designed.Rig, truth);

    /// <summary>Scores two rigs directly.</summary>
    public static ToneScore Score(DcRigPreset designed, DcRigPreset truth)
    {
        var d = Canonicalize(designed);
        var t = Canonicalize(truth);

        var dIds = d.Modules.Select(m => m.ModuleTypeId).ToList();
        var tIds = t.Modules.Select(m => m.ModuleTypeId).ToList();
        var dFamilies = FamilySequence(dIds);
        var tFamilies = FamilySequence(tIds);

        var (knob, compared) = KnobScore(d, t);

        return new ToneScore(
            F1(dFamilies.ToHashSet(), tFamilies.ToHashSet()),
            Jaccard(dIds.ToHashSet(StringComparer.Ordinal), tIds.ToHashSet(StringComparer.Ordinal)),
            knob,
            OrderScore(dFamilies, tFamilies),
            dIds, tIds, dFamilies, tFamilies, compared);
    }

    // ------------------------------------------------------------ canonical form

    /// <summary>
    ///     Puts a rig in the shape the scorer compares, in three steps:
    ///     <list type="number">
    ///         <item>
    ///             Drop a TRAILING, non-bypassed <c>gain</c>. That is
    ///             <see cref="DcRigLoudnessNormalizer" />'s master trim — it is
    ///             appended to EVERY converted tone, so leaving it in would hand
    ///             every designed chain a free <see cref="ToneFamily.Dynamics" />
    ///             and punish one that did not invent a master volume. The
    ///             predicate is the normalizer's own (last module, type
    ///             <c>gain</c>, not bypassed), so the two cannot drift apart.
    ///         </item>
    ///         <item>
    ///             Drop BYPASSED modules. A bypassed pedal is not heard, and a
    ///             model that lists ten pedals with nine off has designed a
    ///             one-pedal tone.
    ///         </item>
    ///         <item>
    ///             <see cref="DcRigTargetCatalog.FillDefaults" />. An omitted param
    ///             IS its catalogue default once the rig is applied, so filling
    ///             both sides compares what the engine would actually run — and
    ///             closes the hole where emitting one knob per module wins the
    ///             knob score by having nothing else to be wrong about.
    ///         </item>
    ///     </list>
    /// </summary>
    public static DcRigPreset Canonicalize(DcRigPreset preset)
    {
        var modules = preset.Modules;
        var hasTrailingMaster = modules.Count > 0 &&
                                modules[^1].ModuleTypeId == DcRigTargetCatalog.Gain &&
                                !modules[^1].Bypassed;
        var take = modules.Count - (hasTrailingMaster ? 1 : 0);

        var kept = new List<DcRigModule>(take);
        for (var i = 0; i < take; i++)
        {
            if (!modules[i].Bypassed)
                kept.Add(modules[i].ModuleTypeId == DcRigTargetCatalog.RsAmpChain ? RsAmpStandIn(modules[i]) : modules[i]);
        }

        return DcRigTargetCatalog.FillDefaults(new DcRigPreset(kept));
    }

    /// <summary>
    ///     A converted amp is Rocksmith's own chain (<c>rsamp</c>, #88), which a design
    ///     cannot write (<c>AiGearNotes.NotDesignable</c>); the truth is judged as the
    ///     designable archetype the prompt's idiom names for that amp's slot — the
    ///     guitar <c>amp</c>, the <c>bassamp</c> head or the <c>tubepre</c> DI — at its
    ///     defaults, so a design is neither rewarded nor punished for knobs it could
    ///     never have set.
    /// </summary>
    public static DcRigModule RsAmpStandIn(DcRigModule rsamp)
    {
        var row = (int)MathF.Round(rsamp.Params.GetValueOrDefault("model"));
        var key = row >= 0 && row < RsAmpChains.Models.Count ? RsAmpChains.Models[row].Key : "";
        var standIn = key.StartsWith("Bass_Amp_", StringComparison.Ordinal) ? DcRigTargetCatalog.BassAmp
            : key.StartsWith("DI_Amp_", StringComparison.Ordinal) ? DcRigTargetCatalog.TubePre
            : DcRigTargetCatalog.Amp;
        return new DcRigModule(standIn, new Dictionary<string, float>(StringComparer.Ordinal));
    }

    // ----------------------------------------------------------------- families

    /// <summary>The scored families a chain carries, in first-occurrence order,
    /// each listed once.</summary>
    public static List<ToneFamily> FamilySequence(IEnumerable<string> moduleTypeIds)
    {
        var seen = new HashSet<ToneFamily>();
        var order = new List<ToneFamily>();
        foreach (var id in moduleTypeIds)
        {
            var family = ToneFamilies.Of(id);
            if (!ToneFamilies.IsScored(family)) continue;
            if (seen.Add(family)) order.Add(family);
        }

        return order;
    }

    private static double F1<T>(IReadOnlySet<T> designed, IReadOnlySet<T> truth)
    {
        if (designed.Count == 0 && truth.Count == 0) return 1.0;
        if (designed.Count == 0 || truth.Count == 0) return 0.0;

        var hit = designed.Count(truth.Contains);
        if (hit == 0) return 0.0;

        var precision = (double)hit / designed.Count;
        var recall = (double)hit / truth.Count;
        return 2 * precision * recall / (precision + recall);
    }

    private static double Jaccard<T>(IReadOnlySet<T> designed, IReadOnlySet<T> truth)
    {
        if (designed.Count == 0 && truth.Count == 0) return 1.0;
        var union = designed.Count + truth.Count - designed.Count(truth.Contains);
        return union == 0 ? 0.0 : (double)designed.Count(truth.Contains) / union;
    }

    // -------------------------------------------------------------------- knobs

    /// <summary>
    ///     Mean per-param agreement over the module types both chains carry.
    ///     Instances of one type are paired in order of occurrence, up to the
    ///     smaller count — a chain with two delays and a truth with one compares
    ///     the FIRST delay and lets the module score account for the extra.
    ///     <para>
    ///         Every catalogue param counts, gains and levels included: a level is
    ///         a tonal decision in a rig that feeds an amp stage, not presentation.
    ///         A param the catalogue does not know (a module id outside
    ///         <c>DcRigTargetCatalog</c>, or a zero-width range) is skipped rather
    ///         than scored 0, so an unknown id costs the module score and nothing
    ///         twice.
    ///     </para>
    /// </summary>
    private static (double Score, int Compared) KnobScore(DcRigPreset designed, DcRigPreset truth)
    {
        var byType = truth.Modules
            .GroupBy(m => m.ModuleTypeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var cursor = new Dictionary<string, int>(StringComparer.Ordinal);
        var sum = 0.0;
        var count = 0;
        var sharedTypes = 0;

        foreach (var module in designed.Modules)
        {
            if (!byType.TryGetValue(module.ModuleTypeId, out var instances)) continue;
            var index = cursor.GetValueOrDefault(module.ModuleTypeId);
            if (index >= instances.Count) continue;
            cursor[module.ModuleTypeId] = index + 1;
            sharedTypes++;

            var other = instances[index];
            foreach (var (paramId, value) in module.Params)
            {
                if (!other.Params.TryGetValue(paramId, out var expected)) continue;
                if (!DcRigTargetCatalog.TryGetParam(module.ModuleTypeId, paramId, out var spec)) continue;

                var range = spec.Max - spec.Min;
                if (!(range > 0) || !float.IsFinite(value) || !float.IsFinite(expected)) continue;

                sum += 1.0 - Math.Clamp(Math.Abs(value - expected) / range, 0.0, 1.0);
                count++;
            }
        }

        // No comparable param anywhere. With a shared module type that is "nothing
        // to be wrong about" (a cab carries no knobs); with none it is the empty
        // chain, which must not collect a free quarter of the total.
        if (count == 0) return (sharedTypes > 0 ? 1.0 : 0.0, 0);
        return (sum / count, count);
    }

    // -------------------------------------------------------------------- order

    /// <summary>
    ///     Kendall-tau concordance over the families BOTH chains carry: every
    ///     unordered pair of common families is concordant when the two chains
    ///     introduce them in the same relative order.
    ///     <para>
    ///         Fewer than two common families means no pair can disagree — 1.0 with
    ///         one common family, 0.0 with none (which is the empty chain again).
    ///     </para>
    /// </summary>
    private static double OrderScore(List<ToneFamily> designed, List<ToneFamily> truth)
    {
        var common = designed.Where(truth.Contains).ToList();
        if (common.Count == 0) return 0.0;
        if (common.Count == 1) return 1.0;

        var pairs = 0;
        var concordant = 0;
        for (var i = 0; i < common.Count; i++)
        {
            for (var j = i + 1; j < common.Count; j++)
            {
                pairs++;
                var a = truth.IndexOf(common[i]);
                var b = truth.IndexOf(common[j]);
                if (a < b) concordant++; // designed order is i < j by construction
            }
        }

        return pairs == 0 ? 1.0 : (double)concordant / pairs;
    }
}
