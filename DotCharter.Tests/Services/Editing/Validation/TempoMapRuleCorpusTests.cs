using System.Globalization;
using DotCharter.Services.Editing.Tempo.Estimate;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     Where <see cref="TempoMapRule" />'s spike and anchor thresholds come from: every finished reference chart
///     in the local <c>TestData/</c> corpus (Editor on Fire projects and Rocksmith XML), none of which may draw a spike
///     or anchor-spam finding. Prints the measured maxima. Skips without the corpus.
/// </summary>
public sealed class TempoMapRuleCorpusTests(ITestOutputHelper output)
{
    /// <summary>
    ///     The two thresholds picked from this corpus. D62's 40 / 300 BPM limits are fixed by the charting
    ///     convention, not by the corpus; the run prints how many reference charts they flag.
    /// </summary>
    private static readonly ArrangementIssueType[] MustNotFire =
        [ArrangementIssueType.TempoSpike, ArrangementIssueType.TempoAnchorSpam];

    [Fact]
    public void TheReferenceCharts_DrawNoTempoFinding()
    {
        var root = LocalFixtures.TestData();
        Assert.SkipUnless(root != null && Directory.Exists(root), "The TestData corpus is not on this machine.");

        var charts = EofCharts(root).Concat(XmlCharts(root)).ToList();
        Assert.SkipWhen(charts.Count == 0, "The TestData corpus holds no chart.");

        var offenders = new List<string>();
        var (maxSpike, maxRedundant, meter, range) = (1.0, 0, 0, 0);
        foreach (var (name, arrangement, beats) in charts)
        {
            if (TempoMapRule.ChartedSpan(arrangement, beats.Count) is { } span)
            {
                for (var i = Math.Max(1, span.First); i <= span.Last; i++)
                    maxSpike = Math.Max(maxSpike, TempoMapRule.SpikeOf(beats, i));
            }

            maxRedundant = Math.Max(maxRedundant, TempoMapRule.RedundantAnchors(beats));
            var issues = new List<ArrangementIssue>();
            new TempoMapRule(new AudioTempoEstimateStore()).Validate(
                ArrangementValidationContext.Build(arrangement, beats, null), issues);
            foreach (var issue in issues.Where(i => i.Type is ArrangementIssueType.MeterMissingOnFirstMeasure
                         or ArrangementIssueType.MeterChangeUnmarked or ArrangementIssueType.TempoOutOfRange))
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: {issue.Type.GetCode()} at {issue.TimeMs:0} ms ({issue.Message})"));
            }

            range += issues.Count(i => i.Type == ArrangementIssueType.TempoOutOfRange);
            meter += issues.Count(i => i.Type is ArrangementIssueType.MeterMissingOnFirstMeasure
                or ArrangementIssueType.MeterChangeUnmarked);
            offenders.AddRange(issues.Where(i => MustNotFire.Contains(i.Type))
                .Select(i => $"{name}: {i.Type.GetCode()} at {i.TimeMs?.ToString("0", CultureInfo.InvariantCulture)} ms"));
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{charts.Count} charts; max spike in the charted part {maxSpike:0.000}x (limit {TempoMapRule.SpikeRatio}); " +
            $"max redundant anchors {maxRedundant} (limit {TempoMapRule.RedundantAnchorLimit}); meter findings {meter}; out-of-range findings {range}."));
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders.Take(40)));
        Assert.True(maxSpike < TempoMapRule.SpikeRatio);
        Assert.True(maxRedundant < TempoMapRule.RedundantAnchorLimit);
    }

    private static IEnumerable<(string, Arrangement, List<BeatMarker>)> EofCharts(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.eof", SearchOption.AllDirectories))
        {
            var importer = new EofImporter();
            var result = importer.ImportTracksWithMetadata(path, [.. importer.GetAvailableTracks(path).Select(t => t.Id)]);
            var beats = EofImporter.BuildBeatMarkers(result.Metadata);
            foreach (var arrangement in result.Arrangements)
                yield return (Path.GetFileName(Path.GetDirectoryName(path)) + "/" + arrangement.Name, arrangement, beats);
        }
    }

    private static IEnumerable<(string, Arrangement, List<BeatMarker>)> XmlCharts(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
        {
            if (path.EndsWith(".debug.xml", StringComparison.OrdinalIgnoreCase)) continue;
            // Only Rocksmith arrangement XML: vocals, showlights and other XML hold no beat map.
            var text = File.ReadAllText(path);
            if (!text.Contains("<song", StringComparison.Ordinal) || !text.Contains("<ebeat ", StringComparison.Ordinal)) continue;
            var project = new RsXmlSongReader().Read(path);
            if (project.Beats.Count < 3) continue;
            foreach (var arrangement in project.Arrangements)
                yield return (Path.GetFileName(path), arrangement, [.. project.Beats]);
        }
    }
}
