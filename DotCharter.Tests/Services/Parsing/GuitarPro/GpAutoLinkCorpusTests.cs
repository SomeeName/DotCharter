using DotCharter.Services.Editing.Linking;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

public sealed class GpAutoLinkCorpusTests(ITestOutputHelper output)
{
    [Fact]
    public void LinkAllPreservesTheImportedChart()
    {
        var root = LocalFixtures.TestData();
        Assert.SkipWhen(root == null, "The local tab corpus is unavailable.");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".gp3" or ".gp4" or ".gp5" or ".gpx" or ".gp")
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.SkipWhen(files.Count == 0, "The local corpus contains no Guitar Pro scores.");
        var measured = 0;
        foreach (var path in files)
        {
            (ChartProject Project, GpImportMetadata Metadata) score;
            try
            {
                score = GpAutoLinkQualityTests.Load(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or NotSupportedException)
            {
                output.WriteLine($"Skipped unreadable score ({ex.GetType().Name}).");
                continue;
            }

            Measure(score.Project, score.Metadata);
            output.WriteLine($"Measured score {++measured}.");
        }

        Assert.True(measured > 0, "The corpus contains no readable Guitar Pro scores.");
    }

    private void Measure(ChartProject project, GpImportMetadata metadata)
    {
        var source = metadata.Bars.Select(b => b.SourceBarIndex).ToArray();
        project.GpSourceBars = [.. GpSourceMap.Capture(metadata, project.Beats)];
        var rule = LinkSuggestionFinder.RuleFor(new AppConfig());
        var before = project.Arrangements.ToDictionary(a => a,
            a => ChartProjectCloner.CloneLevel(a.Levels.MaxBy(l => l.DifficultyIndex) ?? new ChartLevel()));
        LinkSuggestionFinder.LinkAll(project, LinkSuggestionFinder.Find(project, project.Arrangements, source, rule), rule).Action?.Execute();
        foreach (var part in project.Arrangements.Where(a => a.Levels.Count > 0))
        {
            var score = GpAutoLinkScorecard.Measure(project, part, before[part], source);
            output.WriteLine(score.ToString());
            Assert.True(score.NotesEqual);
            Assert.Equal(0, score.ShapeCountDelta);
            Assert.Equal(0, score.CrossedEdges);
        }
    }
}
