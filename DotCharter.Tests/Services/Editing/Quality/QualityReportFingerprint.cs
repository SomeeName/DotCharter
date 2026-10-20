using System.Globalization;
using DotCharter.Services.Editing.Quality;

namespace DotCharter.Tests.Services.Editing.Quality;

/// <summary>
///     A quality report as text with every reference replaced by its POSITION in the project it
///     was built over, so reports over two different copies of one chart compare equal exactly
///     when they say the same thing about the same places (#711).
/// </summary>
internal static class QualityReportFingerprint
{
    public static List<string> Of(ChartQualityReport report, ChartProject project)
    {
        var places = Places(project);
        string At(object? item) => item == null ? "-" : places.TryGetValue(item, out var place) ? place : "OUTSIDE";
        var lines = report.Findings.Select(f => string.Create(CultureInfo.InvariantCulture,
            $"{f.Checks}|{f.Severity}|{f.Code}|{At(f.Arrangement)}|{At(f.Level)}|{f.TimeMs:R}|{At(f.Element)}|" +
            $"{f.Rule?.Type}|{f.Rule?.Message}|{f.Rule?.StringIndex}|{At(f.Rule?.Note)}|{At(f.Rule?.Chord)}|" +
            $"{At(f.Rule?.HandShape)}|{At(f.Rule?.Anchor)}|{string.Join(",", (f.Rule?.HandShapes ?? []).Select(At))}|" +
            $"{f.Export}|{f.Audio}")).ToList();
        lines.AddRange(report.Arrangements.Select(a => string.Create(CultureInfo.InvariantCulture,
            $"{At(a.Arrangement)}|{a.Difficulty:R}|{a.Errors}|{a.Important}|{a.Warnings}|{a.AudioChecked}")));
        return lines;
    }

    private static Dictionary<object, string> Places(ChartProject project)
    {
        var places = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        for (var a = 0; a < project.Arrangements.Count; a++)
        {
            var arrangement = project.Arrangements[a];
            places[arrangement] = $"A{a}";
            for (var l = 0; l < arrangement.Levels.Count; l++)
            {
                var level = arrangement.Levels[l];
                places[level] = $"A{a}L{l}";
                Add(level.Notes, $"A{a}L{l}N");
                Add(level.Chords, $"A{a}L{l}C");
                Add(level.HandShapes, $"A{a}L{l}H");
                Add(level.Anchors, $"A{a}L{l}F");
            }
        }

        return places;

        void Add<T>(IList<T> items, string prefix) where T : class
        {
            for (var i = 0; i < items.Count; i++) places.TryAdd(items[i], prefix + i.ToString(CultureInfo.InvariantCulture));
        }
    }
}
