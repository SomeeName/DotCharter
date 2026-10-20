using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>A gear with the knob values that bring its forward conversion closest to a
/// run of rig modules, and how far it still is (mean squared normalized param error).</summary>
internal sealed record RigToRsFit(RigToRsGearEntry Entry, IReadOnlyDictionary<string, float> Knobs, double Distance)
{
    /// <summary>1 at an exact match, 0 when every param sits at the other end of its range.</summary>
    public float Closeness => (float)Math.Clamp(1.0 - Math.Sqrt(Distance), 0.0, 1.0);
}

/// <summary>
///     The knob half of the inverse: searches each landing knob of a gear INSIDE its catalogue
///     range so the forward conversion reproduces the rig's params. Coordinate descent: per knob
///     a grid over the range (every step when the knob has few, every option of an enum), then a
///     golden-section refinement between the best grid point's neighbours — bisection on the
///     monotone knobs, a bracketed search on the rest. Every value is snapped to the knob's step
///     and lies in its range by construction: nothing is clamped after the fact.
/// </summary>
internal static class RigToRsKnobSearch
{
    private const int GridPoints = 21;
    private const int CoarseGridPoints = 5;
    private const int RefineIterations = 18;
    private const int Passes = 5;
    private const double GoldenRatio = 0.6180339887498949;

    private static readonly Dictionary<string, IReadOnlyList<DcRigTargetCatalog.ParamSpec>> Specs =
        DcRigTargetCatalog.AllModules.ToDictionary(m => m.ModuleTypeId, m => m.Params, StringComparer.Ordinal);

    /// <summary>The knobs that fit <paramref name="target" /> best. <paramref name="coarse" /> is a
    /// cheap one-pass grid for ranking many candidates before the full search on the best.</summary>
    public static RigToRsFit Fit(RigToRsGearIndex index, RigToRsGearEntry entry, IReadOnlyList<DcRigModule> target, bool coarse)
    {
        var values = new Dictionary<string, float>(RsToneAuthoring.DefaultKnobValues(entry.Gear), StringComparer.Ordinal);
        var best = Evaluate(index, entry, values, target);
        var passes = coarse ? 1 : Passes;
        for (var pass = 0; pass < passes && best > 0; pass++)
        {
            var before = best;
            foreach (var knob in entry.LandingKnobs)
                best = SearchKnob(index, entry, values, knob, target, best, coarse);
            if (before - best < 1e-12) break;
        }

        return new RigToRsFit(entry, values, best);
    }

    /// <summary>Mean squared error between two module runs, each param normalized by its catalogue
    /// range; a param one side leaves out counts at its catalogue default. Runs of different shape are 1.</summary>
    public static double Distance(IReadOnlyList<DcRigModule> target, IReadOnlyList<DcRigModule> produced)
    {
        if (target.Count != produced.Count) return 1;
        double sum = 0;
        var count = 0;
        for (var i = 0; i < target.Count; i++)
        {
            if (!string.Equals(target[i].ModuleTypeId, produced[i].ModuleTypeId, StringComparison.Ordinal)) return 1;
            var (moduleSum, moduleCount) = ModuleError(target[i], produced[i]);
            sum += moduleSum;
            count += moduleCount;
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>Snaps a value onto the knob's step grid inside its range (an enum to an option index).</summary>
    public static float Snap(RsGearKnob knob, float value)
    {
        var lo = Math.Min(knob.MinValue, knob.MaxValue);
        var hi = Math.Max(knob.MinValue, knob.MaxValue);
        if (IsEnum(knob)) return Math.Clamp(MathF.Round(value), 0, knob.EnumValues!.Count - 1);
        if (knob.ValueStep > 0) value = lo + (MathF.Round((value - lo) / knob.ValueStep) * knob.ValueStep);
        return Math.Clamp(value, lo, hi);
    }

    private static (double Sum, int Count) ModuleError(DcRigModule target, DcRigModule produced)
    {
        double sum = 0;
        var count = 0;
        if (Specs.TryGetValue(target.ModuleTypeId, out var specs))
        {
            foreach (var spec in specs)
            {
                var range = spec.Max - spec.Min;
                if (range <= 0) continue;
                var d = (target.Params.GetValueOrDefault(spec.Id, spec.Default) - produced.Params.GetValueOrDefault(spec.Id, spec.Default)) / range;
                sum += d * d;
                count++;
            }

            return (sum, count);
        }

        foreach (var id in target.Params.Keys.Union(produced.Params.Keys))
        {
            float a = target.Params.GetValueOrDefault(id), b = produced.Params.GetValueOrDefault(id);
            var d = (a - b) / Math.Max(1f, Math.Max(Math.Abs(a), Math.Abs(b)));
            sum += d * d;
            count++;
        }

        return (sum, count);
    }

    private static double Evaluate(RigToRsGearIndex index, RigToRsGearEntry entry, Dictionary<string, float> values, IReadOnlyList<DcRigModule> target)
        => Distance(target, index.Forward(entry.Gear, values));

    private static double SearchKnob(
        RigToRsGearIndex index, RigToRsGearEntry entry, Dictionary<string, float> values, RsGearKnob knob,
        IReadOnlyList<DcRigModule> target, double best, bool coarse)
    {
        var grid = Grid(knob, coarse ? CoarseGridPoints : GridPoints);
        var bestValue = values[knob.Key];
        var bestAt = -1;
        for (var i = 0; i < grid.Count; i++)
        {
            values[knob.Key] = grid[i];
            var d = Evaluate(index, entry, values, target);
            if (d >= best) continue;
            best = d;
            bestValue = grid[i];
            bestAt = i;
        }

        values[knob.Key] = bestValue;
        if (coarse || IsEnum(knob) || IsWholeStepGrid(knob, grid.Count)) return best;
        // No grid point beat the current value: refine around the grid point nearest to it.
        if (bestAt < 0) bestAt = grid.IndexOf(grid.MinBy(g => Math.Abs(g - bestValue)));
        var a = grid[Math.Max(0, bestAt - 1)];
        var b = grid[Math.Min(grid.Count - 1, bestAt + 1)];
        return knob.ValueStep > 0
            ? RefineSteps(index, entry, values, knob, target, a, b, best)
            : Refine(index, entry, values, knob, target, a, b, best);
    }

    /// <summary>A stepped knob: a ternary search over the step indices between the grid neighbours,
    /// then every step of what is left — golden-section probes snapped to a step can skip the best one.</summary>
    private static double RefineSteps(
        RigToRsGearIndex index, RigToRsGearEntry entry, Dictionary<string, float> values, RsGearKnob knob,
        IReadOnlyList<DcRigModule> target, float a, float b, double best)
    {
        var lo = 0;
        var hi = (int)MathF.Round((b - a) / knob.ValueStep);
        while (hi - lo > 4)
        {
            var m1 = lo + ((hi - lo) / 3);
            var m2 = hi - ((hi - lo) / 3);
            if (Probe(index, entry, values, knob, target, a + (m1 * knob.ValueStep)) <= Probe(index, entry, values, knob, target, a + (m2 * knob.ValueStep)))
                hi = m2;
            else
                lo = m1;
        }

        var bestValue = values[knob.Key];
        for (var i = lo; i <= hi; i++)
        {
            var x = Snap(knob, a + (i * knob.ValueStep));
            var d = Probe(index, entry, values, knob, target, x);
            if (d >= best) continue;
            best = d;
            bestValue = x;
        }

        values[knob.Key] = bestValue;
        return best;
    }

    private static double Refine(
        RigToRsGearIndex index, RigToRsGearEntry entry, Dictionary<string, float> values, RsGearKnob knob,
        IReadOnlyList<DcRigModule> target, float a, float b, double best)
    {
        var bestValue = values[knob.Key];
        for (var i = 0; i < RefineIterations && b - a > 1e-6f; i++)
        {
            var x1 = (float)(b - (GoldenRatio * (b - a)));
            var x2 = (float)(a + (GoldenRatio * (b - a)));
            var d1 = Probe(index, entry, values, knob, target, x1);
            var d2 = Probe(index, entry, values, knob, target, x2);
            if (d1 <= d2) b = x2;
            else a = x1;

            var (x, d) = d1 <= d2 ? (Snap(knob, x1), d1) : (Snap(knob, x2), d2);
            if (d >= best) continue;
            best = d;
            bestValue = x;
        }

        values[knob.Key] = bestValue;
        return best;
    }

    private static double Probe(
        RigToRsGearIndex index, RigToRsGearEntry entry, Dictionary<string, float> values, RsGearKnob knob,
        IReadOnlyList<DcRigModule> target, float x)
    {
        var keep = values[knob.Key];
        values[knob.Key] = Snap(knob, x);
        var d = Evaluate(index, entry, values, target);
        values[knob.Key] = keep;
        return d;
    }

    private static List<float> Grid(RsGearKnob knob, int points)
    {
        if (IsEnum(knob)) return [.. Enumerable.Range(0, knob.EnumValues!.Count).Select(i => (float)i)];

        var lo = Math.Min(knob.MinValue, knob.MaxValue);
        var hi = Math.Max(knob.MinValue, knob.MaxValue);
        var grid = new List<float>(points + 1);
        if (IsWholeStepGrid(knob, points))
        {
            for (var v = lo; v <= hi + (knob.ValueStep * 0.5f); v += knob.ValueStep) grid.Add(Snap(knob, v));
        }
        else
        {
            for (var i = 0; i < points; i++) grid.Add(Snap(knob, lo + ((hi - lo) * i / (points - 1))));
        }

        return [.. grid.Distinct()];
    }

    private static bool IsWholeStepGrid(RsGearKnob knob, int points)
        => knob.ValueStep > 0 && Math.Abs(knob.MaxValue - knob.MinValue) / knob.ValueStep < points;

    private static bool IsEnum(RsGearKnob knob) => knob.UnitType == RsKnobUnit.Enum && knob.EnumValues is { Count: > 0 };
}
