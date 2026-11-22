using System.Globalization;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>The status-rail words for the outcome of a time nudge ("Moved 8 items +10 ms").</summary>
public static class TimeNudgeStatus
{
    public static (string Text, StatusSeverity Severity) Describe(TimeMoveResult result, ILocalizationService l)
    {
        var delta = result.DeltaMs.ToString("+0.##;-0.##", CultureInfo.CurrentCulture);
        return result.Outcome switch
        {
            TimeMoveOutcome.Moved => (l.Format("Status_NudgeMoved", result.ItemCount, delta), StatusSeverity.Info),
            TimeMoveOutcome.Clamped => (l.Format("Status_NudgeClamped", result.ItemCount, delta), StatusSeverity.Warning),
            TimeMoveOutcome.AtStart => (l["Status_NudgeAtStart"], StatusSeverity.Warning),
            TimeMoveOutcome.Collision => (l["Status_NudgeCollision"], StatusSeverity.Warning),
            TimeMoveOutcome.HandShapeOverlap => (l["Status_NudgeHandShapeOverlap"], StatusSeverity.Warning),
            _ => (l["Status_NudgeNothingSelected"], StatusSeverity.Warning)
        };
    }
}
