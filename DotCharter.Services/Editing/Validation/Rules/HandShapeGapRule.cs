using System.Globalization;
using DotCharter.Core.Linking;
using DotCharter.Services.Editing.HandShapes;

namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     D15: a hand shape that runs right up to the next attacked note or chord. The gaps, what
///     counts as an attack are <see cref="HandShapeGap" />'s, which Tools ▸
///     Improve Chart trims to as well, so the pass never leaves a shape this reports.
///     <para>
///         Notes inside the shape never count, and the LAST shape of a level (by position) is
///         exempt, whatever follows it. Judged per level, reported once per shape end across
///         levels, on the shape of the level with the greatest <c>DifficultyIndex</c> (RS XML keeps
///         the file's level order, and the Issues tab hides easier levels by default).
///     </para>
///     <para>
///         The gap is the app's minimum note distance setting, read on every run, so the check
///         agrees with the spacing clean-up the editor runs after each change; without a
///         configuration service it is <see cref="SpacingGap.AppDefault" />.
///     </para>
/// </summary>
public sealed class HandShapeGapRule : IArrangementRule
{
    private const string ShortKey = "Validation_HandShapeGap";

    private readonly IConfigurationService? _config;

    public HandShapeGapRule()
    {
    }

    public HandShapeGapRule(IConfigurationService config) => _config = config;

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var spacing = _config == null ? SpacingGap.AppDefault : ChartSpacingEnforcer.SpacingGapOf(_config.LoadAppConfig());
        SortedDictionary<FractionalPosition, (HandShape Shape, FractionalPosition Gap, int Difficulty)>? found = null;

        foreach (var level in context.Arrangement.Levels)
        {
            if (level.HandShapes.Count < 2) continue;
            var gaps = HandShapeGap.For(level, context.Beats, spacing);
            var lastStart = level.HandShapes.Max(s => s.Position);
            foreach (var shape in level.HandShapes)
            {
                if (shape.Position == lastStart || !gaps.IsTooClose(shape)) continue;
                found ??= [];
                if (found.TryGetValue(shape.EndPosition, out var seen) && seen.Difficulty > level.DifficultyIndex)
                    continue;
                found[shape.EndPosition] = (shape, gaps.RequiredGap(shape), level.DifficultyIndex);
            }
        }

        if (found == null) return;

        foreach (var (end, (shape, gap, _)) in found)
        {
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.HandShapeGap, ShortKey,
                end.GetPositionMs(context.Beats), handShape: shape, args: [Beats(gap)]));
        }
    }

    /// <summary>A grid gap as the message spells it: <c>1/16</c>, or decimal beats for a
    /// millisecond setting's odd fraction.</summary>
    internal static string Beats(FractionalPosition gap) => gap.BeatId == 0 && gap.Fraction.Denominator <= 64
        ? string.Create(CultureInfo.InvariantCulture, $"{gap.Fraction.Numerator}/{gap.Fraction.Denominator}")
        : gap.DoubleValue.ToString("0.###", CultureInfo.CurrentCulture);
}
