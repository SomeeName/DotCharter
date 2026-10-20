using DotCharter.Localization;

namespace DotCharter.Services.Editing.Improvement;

public interface IChartImprovementService
{
    ChartImprovementResult ImproveProject(ChartProject? project, AppConfig config, double totalAudioLengthMs);
}

public sealed class ChartImprovementResult
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
            ? localization["Status_ImproveChartNoFixes"]
            : localization.Plural(
                "Status_ImproveChartAppliedOne", "Status_ImproveChartApplied", TotalChanges);
    }

    public string ToLogDetails()
    {
        if (_changes.Count == 0) return $"ArrangementsProcessed={ArrangementsProcessed}, changes=none.";

        var changeText = string.Join(", ", _changes.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return $"ArrangementsProcessed={ArrangementsProcessed}, {changeText}.";
    }
}
