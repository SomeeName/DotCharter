using DotCharter.Localization;

namespace DotCharter.Services.Editing.Fixing;

/// <summary>
///     Auto-fix engine ported from Log Charter's <c>ArrangementFixer</c>
///     (services/data/fixers): a deterministic, ordered pipeline of safe
///     structural corrections applied to an arrangement in place. Callers
///     are responsible for undo snapshots (see ChartMaintenanceService).
/// </summary>
public interface IArrangementAutoFixService
{
    /// <summary>Runs every fixer over one arrangement, accumulating per-fixer change counts.</summary>
    void Fix(Arrangement arrangement, ArrangementFixResult result);
}

/// <summary>
///     Per-fixer change tally for the status bar and the log — the same
///     shape as <c>ChartImprovementResult</c> but with auto-fix wording.
/// </summary>
public sealed class ArrangementFixResult
{
    private readonly Dictionary<string, int> _changes = [];

    public IReadOnlyDictionary<string, int> Changes => _changes;
    public int ArrangementsProcessed { get; set; }
    public int TotalChanges => _changes.Values.Sum();

    public void Add(string label, int count)
    {
        if (count <= 0) return;
        _changes[label] = _changes.TryGetValue(label, out var existing) ? existing + count : count;
    }

    public string ToStatusMessage(ILocalizationService localization)
    {
        return TotalChanges == 0
            ? localization["Status_AutoFixNoChanges"]
            : localization.Plural(
                "Status_AutoFixAppliedOne", "Status_AutoFixApplied", TotalChanges);
    }

    public string ToLogDetails()
    {
        if (_changes.Count == 0) return $"ArrangementsProcessed={ArrangementsProcessed}, changes=none.";
        var changeText = string.Join(", ", _changes.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return $"ArrangementsProcessed={ArrangementsProcessed}, {changeText}.";
    }
}
