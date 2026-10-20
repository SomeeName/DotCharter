using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tools.RigDataset;

/// <summary>
///     One row of a trainer's <c>predictions-test.jsonl</c>: the held-out clip, the
///     DI it was rendered from, the span inside that DI, the TRUE rig, the model's
///     PREDICTED rig, and how confident the model was.
///     <para>
///         <b>Parsed by hand rather than by attribute</b> — the file is written by
///         the python side, so its casing is <c>snake</c>/<c>camel</c> while a
///         <see cref="DcRigPreset" /> inside it is the C# <c>Pascal</c> serialisation
///         the dataset manifest already uses. One record with two conventions in it
///         is exactly the kind of seam a serializer attribute makes silently wrong:
///         a misspelt property does not throw, it arrives as <c>null</c> and the row
///         is scored against an empty rig, which produces a perfectly plausible
///         distance. <see cref="EvalRenderPredictions.Parse" /> REFUSES a row it
///         cannot read.
///     </para>
/// </summary>
/// <param name="ClipPath">The held-out wet clip, relative to the dataset root (the
/// truth render already on disk).</param>
/// <param name="DiPath">The dry DI, relative to the dataset root.</param>
/// <param name="StartSeconds">Where inside the DI the clip's span starts.</param>
/// <param name="LengthSeconds">How long the span is; 0 means "the whole DI".</param>
/// <param name="Truth">The rig the clip was actually rendered through.</param>
/// <param name="Predicted">The rig the model guessed.</param>
/// <param name="Confidence">The model's own confidence, used only to pick the
/// top-N subset. <see cref="double.NaN" /> when the file does not carry one.</param>
public sealed record RigPrediction(
    string ClipPath,
    string DiPath,
    double StartSeconds,
    double LengthSeconds,
    DcRigPreset Truth,
    DcRigPreset Predicted,
    double Confidence);

/// <summary>Reads a predictions JSONL, one <see cref="RigPrediction" /> per line.</summary>
public static class EvalRenderPredictions
{
    private static readonly JsonSerializerOptions RigJson = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    ///     Parses one line. Returns null and a reason for a line this eval cannot
    ///     score — never a half-filled row.
    /// </summary>
    /// <param name="line">One JSON object.</param>
    /// <param name="error">Why the line was refused; null on success.</param>
    /// <returns>The row, or null.</returns>
    public static RigPrediction? Parse(string line, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(line)) { error = "blank line"; return null; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException ex) { error = "not JSON: " + ex.Message; return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "not a JSON object"; return null; }

            var clip = String_(root, "clip", "clipPath", "ClipPath");
            var di = String_(root, "di", "diPath", "DiPath");
            if (string.IsNullOrEmpty(clip) && string.IsNullOrEmpty(di))
            {
                error = "neither 'clip' nor 'di' is present";
                return null;
            }

            var truth = Rig(root, out var truthError, "truth", "Truth", "rig", "Rig");
            if (truth is null) { error = "'truth' rig: " + truthError; return null; }

            var predicted = Rig(root, out var predError, "predicted", "Predicted", "prediction");
            if (predicted is null) { error = "'predicted' rig: " + predError; return null; }

            var (start, length) = Span(root);
            var confidence = Number(root, "confidence", "Confidence");

            return new RigPrediction(clip ?? "", di ?? "", start, length, truth, predicted, confidence);
        }
    }

    /// <summary>Parses a whole file, collecting the refusals rather than throwing on
    /// the first one — a trainer's file with three bad lines in 13,000 is still worth
    /// scoring, and the count of what was refused goes in the summary.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="errors">One entry per refused line: <c>"line N: reason"</c>.</param>
    /// <returns>Every row that parsed, in file order.</returns>
    public static IReadOnlyList<RigPrediction> ParseAll(IEnumerable<string> lines, out IReadOnlyList<string> errors)
    {
        var rows = new List<RigPrediction>();
        var bad = new List<string>();
        var n = 0;
        foreach (var line in lines)
        {
            n++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var row = Parse(line, out var error);
            if (row is null) bad.Add($"line {n}: {error}");
            else rows.Add(row);
        }

        errors = bad;
        return rows;
    }

    private static string? String_(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }

        return null;
    }

    private static double Number(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
                if (v.ValueKind == JsonValueKind.String
                    && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                {
                    return p;
                }
            }
        }

        return double.NaN;
    }

    // "seconds": [start, length] — the shape the trainer emits. A bare number is
    // read as the LENGTH from 0, and an object with start/length is accepted too,
    // because those are the three things a python side plausibly writes and getting
    // the span wrong means rendering the prediction over audio the truth never saw.
    private static (double Start, double Length) Span(JsonElement root)
    {
        foreach (var name in new[] { "seconds", "Seconds", "span" })
        {
            if (!root.TryGetProperty(name, out var v)) continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.Array:
                {
                    var parts = v.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.Number)
                        .Select(e => e.GetDouble()).ToArray();
                    if (parts.Length >= 2) return (parts[0], parts[1]);
                    if (parts.Length == 1) return (0, parts[0]);
                    break;
                }
                case JsonValueKind.Number:
                    return (0, v.GetDouble());
                case JsonValueKind.Object:
                {
                    var start = Number(v, "start", "Start", "from");
                    var length = Number(v, "length", "Length", "seconds", "len");
                    return (double.IsNaN(start) ? 0 : start, double.IsNaN(length) ? 0 : length);
                }
            }
        }

        var flatStart = Number(root, "startSeconds", "start");
        var flatLength = Number(root, "lengthSeconds", "length");
        return (double.IsNaN(flatStart) ? 0 : flatStart, double.IsNaN(flatLength) ? 0 : flatLength);
    }

    private static DcRigPreset? Rig(JsonElement root, out string? error, params string[] names)
    {
        error = "absent";
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind != JsonValueKind.Object)
            {
                error = $"is {v.ValueKind}, not an object";
                return null;
            }

            try
            {
                var rig = v.Deserialize<DcRigPreset>(RigJson);
                if (rig?.Modules is null) { error = "has no 'Modules'"; return null; }
                error = null;
                return rig;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return null;
            }
        }

        return null;
    }
}

/// <summary>
///     One scored row of the render eval. Every distance is a
///     <c>ToneSimilarity</c> distance in the same space; <see cref="double.NaN" />
///     means "not measured", never "zero".
/// </summary>
/// <param name="Index">The row's index in the predictions file, 0-based — so a
/// subsampled run can be traced back to the file it came from.</param>
/// <param name="ClipPath">The held-out clip, as the predictions file named it.</param>
/// <param name="DiPath">The DI, as the predictions file named it.</param>
/// <param name="Distance">Predicted rig's render against the target.</param>
/// <param name="SelfFloor">The target's own distance between two disjoint windows
/// of itself — the noise floor of this row's comparison. NaN when the target is too
/// short to hold two of them.</param>
/// <param name="DistanceOverFloor"><see cref="Distance" /> / <see cref="SelfFloor" />:
/// how many "the target against itself"s away the prediction landed. NaN when the
/// floor is unknown or zero.</param>
/// <param name="GenericDistance">The fixed generic overdrive → amp → cab rig's
/// render against the same target, on the same DI span — the per-row "would an
/// information-free answer have done as well?" control.</param>
/// <param name="TruthDistance">The TRUTH rig re-rendered against the target.
/// Measured only in <c>--target render</c> mode, where it is 0 by construction and
/// therefore the harness's own non-vacuity check; NaN in clip mode.</param>
/// <param name="RefinedDistance">The distance after the matcher refined the
/// predicted rig as its prior. NaN unless <c>--refine</c>.</param>
/// <param name="Plausibility">The predicted rig's <c>RigPlausibility</c> penalty,
/// 0 for a board a player would have built and 1 for one the sampler could not
/// produce at all.</param>
/// <param name="Confidence">The model's own confidence, straight from the file.</param>
/// <param name="TargetSource"><c>clip</c> when the target was the wet clip already
/// on disk, <c>render</c> when the truth rig was re-rendered for it.</param>
/// <param name="Error">Why the row was skipped; null when it was scored.</param>
public sealed record EvalRenderRow(
    int Index,
    string ClipPath,
    string DiPath,
    double Distance,
    double SelfFloor,
    double DistanceOverFloor,
    double GenericDistance,
    double TruthDistance,
    double RefinedDistance,
    double Plausibility,
    double Confidence,
    string TargetSource,
    string? Error)
{
    /// <summary>True when this row carries a measurement.</summary>
    [JsonIgnore]
    public bool Scored => Error is null && double.IsFinite(Distance);
}

/// <summary>
///     The aggregate over a set of scored rows. Every "fraction" is in
///     <c>[0,1]</c>; the tool prints them as percentages and the JSON keeps them as
///     fractions, so nobody has to guess which a number is.
/// </summary>
/// <param name="Rows">How many scored rows went in.</param>
/// <param name="MedianDistance">Median predicted-vs-target distance.</param>
/// <param name="MeanDistance">Mean predicted-vs-target distance.</param>
/// <param name="MedianDistanceOverFloor">Median of the floor-normalised distance,
/// over the rows whose floor is known.</param>
/// <param name="FloorKnown">How many rows had a usable self-floor.</param>
/// <param name="FractionUnder1p5">Fraction of floor-known rows under 1.5× the floor.</param>
/// <param name="FractionUnder2">Fraction of floor-known rows under 2× the floor.</param>
/// <param name="FractionUnder3">Fraction of floor-known rows under 3× the floor.</param>
/// <param name="MedianGenericDistance">Median generic-rig distance on the same rows.</param>
/// <param name="FractionBeatsGeneric">Fraction of rows where the prediction is
/// strictly closer than the generic rig.</param>
/// <param name="MedianTruthDistance">Median truth-render distance; NaN in clip mode.</param>
/// <param name="MedianRefinedDistance">Median distance after refinement; NaN without
/// <c>--refine</c>.</param>
/// <param name="FractionRefinedBeatsPredicted">Fraction of rows refinement improved.</param>
/// <param name="MeanPlausibility">Mean plausibility penalty of the predicted rigs.</param>
public sealed record EvalRenderStats(
    int Rows,
    double MedianDistance,
    double MeanDistance,
    double MedianDistanceOverFloor,
    int FloorKnown,
    double FractionUnder1p5,
    double FractionUnder2,
    double FractionUnder3,
    double MedianGenericDistance,
    double FractionBeatsGeneric,
    double MedianTruthDistance,
    double MedianRefinedDistance,
    double FractionRefinedBeatsPredicted,
    double MeanPlausibility)
{
    /// <summary>The empty aggregate — every statistic NaN, no rows. Returned rather
    /// than thrown for an empty set, because "no rows survived" is a result the
    /// summary has to be able to state.</summary>
    public static EvalRenderStats Empty { get; } = new(
        0, double.NaN, double.NaN, double.NaN, 0, double.NaN, double.NaN, double.NaN,
        double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

    /// <summary>
    ///     Folds a set of scored rows. Rows that were skipped are ignored, and each
    ///     statistic is computed over the rows that CARRY it — the floor-normalised
    ///     ones over the floor-known subset, never over the whole set with NaNs
    ///     folded in, which would make one short clip erase the column.
    /// </summary>
    /// <param name="rows">Scored and skipped rows; skipped ones are dropped.</param>
    /// <returns>The aggregate.</returns>
    public static EvalRenderStats Compute(IEnumerable<EvalRenderRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var scored = rows.Where(r => r.Scored).ToArray();
        if (scored.Length == 0) return Empty;

        var overFloor = scored.Where(r => double.IsFinite(r.DistanceOverFloor))
            .Select(r => r.DistanceOverFloor).ToArray();
        var generic = scored.Where(r => double.IsFinite(r.GenericDistance)).ToArray();
        var truth = scored.Where(r => double.IsFinite(r.TruthDistance)).Select(r => r.TruthDistance).ToArray();
        var refined = scored.Where(r => double.IsFinite(r.RefinedDistance)).ToArray();

        return new EvalRenderStats(
            scored.Length,
            Median(scored.Select(r => r.Distance)),
            scored.Average(r => r.Distance),
            Median(overFloor),
            overFloor.Length,
            Fraction(overFloor, v => v < 1.5),
            Fraction(overFloor, v => v < 2.0),
            Fraction(overFloor, v => v < 3.0),
            Median(generic.Select(r => r.GenericDistance)),
            Fraction(generic, r => r.Distance < r.GenericDistance),
            Median(truth),
            Median(refined.Select(r => r.RefinedDistance)),
            Fraction(refined, r => r.RefinedDistance < r.Distance),
            scored.Where(r => double.IsFinite(r.Plausibility)).Select(r => r.Plausibility)
                .DefaultIfEmpty(double.NaN).Average());
    }

    /// <summary>
    ///     Median of a sample, NaN for an empty one. Even counts average the two
    ///     middle values.
    /// </summary>
    /// <param name="values">The sample; non-finite values are dropped.</param>
    /// <returns>The median, or NaN.</returns>
    public static double Median(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var v = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (v.Length == 0) return double.NaN;
        return v.Length % 2 == 1 ? v[v.Length / 2] : 0.5 * (v[v.Length / 2 - 1] + v[v.Length / 2]);
    }

    private static double Fraction<T>(IReadOnlyCollection<T> items, Func<T, bool> predicate) =>
        items.Count == 0 ? double.NaN : items.Count(predicate) / (double)items.Count;
}

/// <summary>
///     Picks the deterministic subsample and the top-N-confident subset — the two
///     selection rules the eval makes, kept pure so they can be asserted without an
///     engine.
/// </summary>
public static class EvalRenderSelection
{
    /// <summary>
    ///     A deterministic subsample of <paramref name="count" /> indices out of
    ///     <paramref name="total" />, returned SORTED so the output file's order is
    ///     the predictions file's order whatever the seed did.
    /// </summary>
    /// <param name="total">How many rows the file has.</param>
    /// <param name="count">How many to keep; 0 or more than <paramref name="total" />
    /// keeps all of them.</param>
    /// <param name="seed">The draw's seed — the same seed picks the same rows.</param>
    /// <returns>Ascending indices.</returns>
    public static int[] Subsample(int total, int count, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        if (count <= 0 || count >= total) return Enumerable.Range(0, total).ToArray();

        // Fisher-Yates over the prefix: O(count) swaps, and it cannot repeat an index
        // the way "draw until you have N distinct" can stall on.
        var order = Enumerable.Range(0, total).ToArray();
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var j = rng.Next(i, total);
            (order[i], order[j]) = (order[j], order[i]);
        }

        var picked = order[..count];
        Array.Sort(picked);
        return picked;
    }

    /// <summary>
    ///     The <paramref name="n" /> most confident scored rows. Rows with no
    ///     confidence (NaN) are EXCLUDED rather than sorted to one end — a file with
    ///     no confidence column has no top-N, and saying so beats reporting the first
    ///     N rows as if the model had chosen them.
    /// </summary>
    /// <param name="rows">Scored rows.</param>
    /// <param name="n">How many to keep; 0 or less returns nothing.</param>
    /// <returns>The subset, most confident first.</returns>
    public static IReadOnlyList<EvalRenderRow> TopConfident(IEnumerable<EvalRenderRow> rows, int n)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (n <= 0) return [];
        return rows.Where(r => r.Scored && double.IsFinite(r.Confidence))
            .OrderByDescending(r => r.Confidence)
            .ThenBy(r => r.Index)
            .Take(n)
            .ToArray();
    }
}
