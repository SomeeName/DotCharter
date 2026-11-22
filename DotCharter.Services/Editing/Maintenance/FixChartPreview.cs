using DotCharter.Localization;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.Improvement;

namespace DotCharter.Services.Editing.Maintenance;

/// <summary>
///     The two groups of "Fix chart…". <see cref="Errors" /> is what Auto-fix arrangements does (faults that
///     break the chart or its export); <see cref="Polish" /> is what Improve chart does (tidying that does not).
///     When both run, Errors runs first, as it always did before they were one dialog.
/// </summary>
[Flags]
public enum FixChartGroups
{
    None = 0,
    Errors = 1,
    Polish = 2,
    Both = Errors | Polish
}

/// <summary>One kind of change a fix makes: the operation's own label (a stable English id, not display text) and how many.</summary>
public sealed record FixChartChange(string Label, int Count);

/// <summary>What the fix groups WOULD change, counted per kind of change, from a run on a copy of the chart; nothing in the project moved.</summary>
public sealed record FixChartPreview(IReadOnlyList<FixChartChange> Errors, IReadOnlyList<FixChartChange> Polish)
{
    public static FixChartPreview Empty { get; } = new([], []);

    public int ErrorCount => Errors.Sum(c => c.Count);

    public int PolishCount => Polish.Sum(c => c.Count);

    public int Total => ErrorCount + PolishCount;

    /// <summary>The status line after Apply: the wording each operation always had when one group ran, a joint one for both.</summary>
    public string ToStatusMessage(ILocalizationService localization, FixChartGroups groups)
    {
        var (none, one, many) = groups switch
        {
            FixChartGroups.Errors => ("Status_AutoFixNoChanges", "Status_AutoFixAppliedOne", "Status_AutoFixApplied"),
            FixChartGroups.Polish => ("Status_ImproveChartNoFixes", "Status_ImproveChartAppliedOne", "Status_ImproveChartApplied"),
            _ => ("Status_FixChartNoChanges", "Status_FixChartAppliedOne", "Status_FixChartApplied")
        };
        return Total == 0 ? localization[none] : localization.Plural(one, many, Total);
    }
}

/// <summary>
///     Runs the two existing operations, unchanged, over a chart: Auto-fix arrangements (Errors), then Improve chart
///     (Polish), and reads the counts per kind off their results.
/// </summary>
public sealed class FixChartRunner(IArrangementAutoFixService autoFix, IChartImprovementService improvement)
{
    public FixChartPreview Run(ChartProject target, FixChartGroups groups, AppConfig config, double audioLengthMs)
    {
        IReadOnlyList<FixChartChange> errors = [];
        IReadOnlyList<FixChartChange> polish = [];
        if (groups.HasFlag(FixChartGroups.Errors))
        {
            var result = new ArrangementFixResult();
            foreach (var arrangement in target.Arrangements) autoFix.Fix(arrangement, result);
            errors = [.. result.Changes.Select(c => new FixChartChange(c.Key, c.Value))];
        }

        if (groups.HasFlag(FixChartGroups.Polish))
        {
            var result = improvement.ImproveProject(target, config, audioLengthMs);
            polish = [.. result.Changes.Select(c => new FixChartChange(c.Key, c.Value))];
        }

        return new FixChartPreview(errors, polish);
    }
}
