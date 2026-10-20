namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     The weights the four components are mixed with. ONE place, on purpose: a
///     weight re-typed at a call site is how an eval starts reporting two numbers
///     for the same run.
///     <para>
///         Rationale, in the order a listener would notice a mistake:
///         <list type="number">
///             <item>
///                 <b>Family 0.40</b> — reaching for the wrong KIND of pedal is
///                 the only error that is always audible. A fuzz where the song
///                 wants a chorus is wrong however well it is dialled in.
///             </item>
///             <item>
///                 <b>Module 0.25</b> — the exact type id. Weighted below family
///                 because <c>rat</c> / <c>ds1</c> / <c>distortion</c> are three
///                 plausible answers to one request, and an eval that demanded
///                 the converter's pick would punish a good tone.
///             </item>
///             <item>
///                 <b>Knob 0.25</b> — same modules, dialled differently. Equal to
///                 module on purpose: a JCM800 at 10 % gain is not a rhythm tone.
///             </item>
///             <item>
///                 <b>Order 0.10</b> — real but small. Drive-into-amp vs
///                 amp-into-drive matters; delay before or after reverb mostly
///                 does not, and RS tones are series-only anyway.
///             </item>
///         </list>
///     </para>
/// </summary>
public static class ToneScoreWeights
{
    /// <summary>Weight of <see cref="ToneScore.FamilyScore" />.</summary>
    public const double Family = 0.40;

    /// <summary>Weight of <see cref="ToneScore.ModuleScore" />.</summary>
    public const double Module = 0.25;

    /// <summary>Weight of <see cref="ToneScore.KnobScore" />.</summary>
    public const double Knob = 0.25;

    /// <summary>Weight of <see cref="ToneScore.OrderScore" />.</summary>
    public const double Order = 0.10;

    /// <summary>The four weights must sum to 1 or <see cref="ToneScore.Total" />
    /// stops being a percentage. Asserted by <c>ToneDesignScorerTests</c>.</summary>
    public const double Sum = Family + Module + Knob + Order;
}

/// <summary>
///     How close one designed tone came to its Rocksmith ground truth. Every
///     component is 0..1; <see cref="Total" /> is 0..100.
/// </summary>
/// <param name="FamilyScore">F1 over the two tonal-family SETS (see <see cref="ToneFamily" />).</param>
/// <param name="ModuleScore">Jaccard over the two distinct module-type-id sets.</param>
/// <param name="KnobScore">
///     Mean <c>1 − |Δ| / (max − min)</c> over every catalogue param of every module
///     type present in both chains, ranges from <c>DcRigTargetCatalog</c>.
/// </param>
/// <param name="OrderScore">
///     Fraction of family PAIRS whose first-occurrence order agrees — a
///     Kendall-tau concordance restricted to the families both chains carry.
/// </param>
/// <param name="DesignedModules">The designed chain's type ids, in order, after canonicalization.</param>
/// <param name="TruthModules">The truth chain's type ids, in order, after canonicalization.</param>
/// <param name="DesignedFamilies">The designed chain's scored families, in first-occurrence order.</param>
/// <param name="TruthFamilies">The truth chain's scored families, in first-occurrence order.</param>
/// <param name="ComparedParams">How many params the knob score averaged over. 0 means it was defaulted.</param>
public sealed record ToneScore(
    double FamilyScore,
    double ModuleScore,
    double KnobScore,
    double OrderScore,
    IReadOnlyList<string> DesignedModules,
    IReadOnlyList<string> TruthModules,
    IReadOnlyList<ToneFamily> DesignedFamilies,
    IReadOnlyList<ToneFamily> TruthFamilies,
    int ComparedParams)
{
    /// <summary>The weighted total, 0..100.</summary>
    public double Total =>
        100.0 * (ToneScoreWeights.Family * FamilyScore +
                 ToneScoreWeights.Module * ModuleScore +
                 ToneScoreWeights.Knob * KnobScore +
                 ToneScoreWeights.Order * OrderScore);
}
