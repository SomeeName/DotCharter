using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Search;

/// <summary>
///     Decides, for one catalogue param, whether the matcher's search may move it
///     and how its travel maps onto <c>[0,1]</c>.
///     <para>
///         <b>Why discrete params are held FIXED during refinement.</b> A NAMED
///         param (<c>overdrive.model</c>: Generic / Screamer / Blues Breaker /
///         Transparent) and an ORDINAL one (<c>phaser.stages</c>, a count the core
///         <c>lround</c>s) are not knobs — they are a CHOICE of a different
///         circuit. CMA-ES works on a continuous, locally smooth landscape; a
///         param the core rounds makes the loss a staircase, so the sampled
///         population sees zero gradient inside a step and a full-scale
///         discontinuity at every edge, which is exactly the shape that makes the
///         covariance estimate diverge. Structure (which modules, which models) is
///         the RETRIEVAL stage's job — the library carries one entry per discrete
///         combination that was sampled — and refinement then tunes the continuous
///         knobs of the structure retrieval chose. This is ST-ITO's split
///         (arXiv 2410.21233) and Okita 2026's "predict the type set, then search
///         the parameters".
///     </para>
///     <para>
///         <b>The discrete set is curated by ID</b> because
///         <see cref="DcRigTargetCatalog" /> is a range-only mirror: the native
///         catalogue's <c>valueLabels</c> / <c>step</c> metadata only exists on a
///         machine with the DLL (<c>DspParamInfo.IsStepped</c> /
///         <c>HasValueLabels</c>), and this type must work offline, in a test, with
///         no engine. The id list below is the set of selector / toggle / count ids
///         in the catalogue; membership alone is not enough — the spec's min, max
///         and default must ALL be integral and the range non-degenerate, which is
///         what separates <c>curve</c> 0..3 (a named curve) from <c>curve</c>
///         0..1 default 0.5 (a morph) without a per-module table.
///         <see cref="IsDiscreteOverride" /> lets a caller that HAS live catalogue
///         metadata replace the whole judgement after the engine lane lands.
///     </para>
/// </summary>
internal static class KnobParamKinds
{
    private static readonly Dictionary<string, IReadOnlyList<DcRigTargetCatalog.ParamSpec>> SpecsByType =
        DcRigTargetCatalog.AllModules.ToDictionary(m => m.ModuleTypeId, m => m.Params, StringComparer.Ordinal);

    /// <summary>The catalogue's param specs for a module type. A local lookup over
    /// <see cref="DcRigTargetCatalog.AllModules" /> rather than a new member on the
    /// catalogue, so this lane adds nothing to a file three other lanes edit.</summary>
    internal static bool TryGetParams(string moduleTypeId, out IReadOnlyList<DcRigTargetCatalog.ParamSpec> specs)
        => SpecsByType.TryGetValue(moduleTypeId, out specs!);

    /// <summary>Selector / toggle / count param ids in the catalogue. Each one is
    /// pinned to a real catalogue param by <c>KnobVectorTests</c>, so a stale id
    /// cannot rot here unnoticed.</summary>
    internal static readonly IReadOnlySet<string> DiscreteIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "bits", "curve", "direction", "division", "key", "lfoShape", "mode", "model",
        "pattern", "scale", "solo1", "solo2", "solo3", "source", "stages", "state",
        "steps", "swap", "sync", "taps", "trails", "trigger", "tubes", "undo",
        "voices", "waveform"
    };

    /// <summary>An optional replacement for the discreteness judgement, for a
    /// caller holding live catalogue metadata (module type id, param id, spec →
    /// discrete?). Null by default; set once at composition, never per call.</summary>
    internal static Func<string, DcRigTargetCatalog.ParamSpec, bool>? IsDiscreteOverride { get; set; }

    /// <summary>Whether the search must leave this param alone.</summary>
    internal static bool IsDiscrete(string moduleTypeId, in DcRigTargetCatalog.ParamSpec spec)
    {
        if (IsDiscreteOverride is { } over) return over(moduleTypeId, spec);
        if (!DiscreteIds.Contains(spec.Id)) return false;
        return spec.Max > spec.Min && IsIntegral(spec.Min) && IsIntegral(spec.Max) && IsIntegral(spec.Default);
    }

    private static bool IsIntegral(float v) => Math.Abs(v - MathF.Round(v)) < 1e-6f;

    /// <summary>Whether a log taper is DEFINABLE on this range: both ends strictly
    /// positive and ordered. A bipolar param (<c>freqshift.shiftHz</c> runs
    /// −1,200…+1,200) and an "0 = off" one (<c>midside.monoBelowHz</c>) both fail
    /// it and stay linear rather than producing a NaN coordinate.</summary>
    internal static bool IsLoggable(float min, float max) => min > 0f && max > min;

    /// <summary>Ranges at or past this many decades are log-scaled whatever they
    /// are called — the arithmetic is the same in hertz or milliseconds.</summary>
    internal const double LogDecades = 2.0;

    /// <summary>
    ///     Whether the coordinate is log-scaled. This MIRRORS the UI's taper rule
    ///     (<c>DotCharter.UI/Controls/Dsp/KnobMath.TaperFor</c>): an id ending in
    ///     <c>Hz</c>/<c>KHz</c> is a frequency and frequency is heard in ratios, and
    ///     any range spanning <see cref="LogDecades" /> decades or more gets it too.
    ///     It is a MIRROR and not a call because <c>Services</c> may never reference
    ///     <c>UI</c> (the layering test enforces that); the two are pinned to each
    ///     other by <c>KnobVectorTests.TaperRule_MatchesTheUiKnobTaper</c>.
    ///     <para>
    ///         Searching a frequency linearly is not a cosmetic difference: a
    ///         20–20,000 Hz <c>svf.cutoffHz</c> put everything below 2 kHz — every
    ///         guitar fundamental — into the bottom 10 % of the coordinate, so a
    ///         CMA-ES step small enough to tune the presence region was smaller than
    ///         the resolution of the entire usable band.
    ///     </para>
    /// </summary>
    internal static bool IsLogarithmic(in DcRigTargetCatalog.ParamSpec spec)
    {
        if (!IsLoggable(spec.Min, spec.Max)) return false;
        return DspParamInfo.IsFrequencyId(spec.Id) || Math.Log10(spec.Max / (double)spec.Min) >= LogDecades;
    }
}
