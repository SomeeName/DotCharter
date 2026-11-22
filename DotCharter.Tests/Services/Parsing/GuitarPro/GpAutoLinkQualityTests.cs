using DotCharter.Core.Linking;
using DotCharter.Services.Editing.Linking;
using DotCharter.Services.Import.GuitarPro.Report;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

public sealed class GpAutoLinkQualityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("AABA", "1:0,1,3")]
    [InlineData("ABCDXABCDYABZAB", "4:0,5;2:10,13")]
    [InlineData("ABCDXABCDYAZA", "4:0,5;1:10,12")]
    public void RecoversWholeRunsAndTheirCopies(string pattern, string expected)
    {
        var (project, metadata) = Import([.. pattern.Select(NoteBar)]);
        var source = metadata.Bars.Select(b => b.SourceBarIndex).ToArray();
        if (pattern.Length == 15)
        {
            foreach (var start in new[] { 5, 10, 13 })
                Array.Copy(source, 0, source, start, 2);
        }

        Assert.Equal(expected, ApplyAndMeasure(project, source));
    }

    [Theory]
    [InlineData("ABCDEFGHIJKLMNXABCDEFGHIJKLMNYEFGHIJKLMN", "10:4,19,30;4:0,15")]
    [InlineData("ABCDXABCDYBCD", "4:0,5")]
    public void ASharedSectionCanOutrankAPairWithoutPromotingShortFragments(string pattern, string expected)
    {
        var bars = pattern.Select(letter =>
        {
            var index = letter - 'A';
            return new FxMeasure().AddVoice(FxBeat.Note(0, index / 6, 3), FxBeat.Note(0, index % 6, 3));
        }).ToArray();
        var (project, _) = Import(bars);

        Assert.Equal(expected, ApplyAndMeasure(project, null));
    }

    [Fact]
    public void KeepsAllFourPassesOfOneWrittenRiffTogether()
    {
        var bars = "ABCD".Select(NoteBar).ToArray();
        bars[0].RepeatStart = true;
        bars[3].RepeatTotalPasses = 4;
        var (project, metadata) = Import(bars);

        Assert.Equal("4:0,4,8,12", ApplyAndMeasure(project, metadata.Bars.Select(b => b.SourceBarIndex).ToArray()));
    }

    [Fact]
    public void KeepsTheVerseAndChorusTogetherAcrossAlternateEndings()
    {
        var bars = "ABCDEF".Select(NoteBar).ToArray();
        bars[0].RepeatStart = true;
        bars[0].Marker = "Verse";
        bars[2].Marker = "Chorus";
        bars[4].AlternateEndingsMask = 1;
        bars[5].AlternateEndingsMask = 2;
        bars[5].RepeatTotalPasses = 2;
        var (project, metadata) = Import(bars);

        Assert.Equal("4:0,5", ApplyAndMeasure(project, metadata.Bars.Select(b => b.SourceBarIndex).ToArray()));
    }

    [Fact]
    public void SkipsAPickupThatWouldCutTheIncomingShapeAndSustain()
    {
        var (project, _) = Import([.. "PABCXQABC".Select(NoteBar)]);
        var level = Assert.Single(Assert.Single(project.Arrangements).Levels);
        level.Notes[0].Sustain = new FractionalPosition(5);
        level.Notes[5].Sustain = new FractionalPosition(5);
        level.HandShapes.Add(new HandShape { Position = new FractionalPosition(3, new Fraction(3, 4)), EndPosition = new FractionalPosition(5) });
        level.HandShapes.Add(new HandShape { Position = new FractionalPosition(23, new Fraction(3, 4)), EndPosition = new FractionalPosition(25) });

        Assert.Equal("2:2,7", ApplyAndMeasure(project, null));
    }

    [Fact]
    public void OutgoingLinkNextCannotEndBeforeItsLanding()
    {
        var (project, _) = Import([.. "ABXABY".Select(NoteBar)]);
        var notes = Assert.Single(Assert.Single(project.Arrangements).Levels).Notes;
        foreach (var origin in new[] { notes[1], notes[4] })
        {
            origin.LinkNext = true;
            origin.Sustain = new FractionalPosition(4);
        }

        Assert.Equal("1:0,3", ApplyAndMeasure(project, null));
    }

    [Fact]
    public void DifferentWrittenChordDurationsAreExcludedFromLinkAll()
    {
        var (project, _) = Import([.. "ABXAB".Select(letter => letter == 'B'
            ? new FxMeasure().AddVoice(FxBeat.Chord(2, new FxNote(0, 1), new FxNote(1, 2)))
            : NoteBar(letter))]);
        var level = Assert.Single(Assert.Single(project.Arrangements).Levels);
        level.Chords[1].NotationDuration = new Fraction(3, 4);

        var review = Assert.Single(LinkSuggestionFinder.Find(project, project.Arrangements, null,
            MeasureContentOptions.Detection), s => s.RequiresReview);
        Assert.Equal(2, review.MeasureCount);
        Assert.Equal([0, 3], review.FirstMeasures);
        Assert.Equal("1:0,3", ApplyAndMeasure(project, null));
    }

    [Fact]
    public void ReopeningRestoresRehearsalEdgesWithMarkerDisplayOff()
    {
        var bars = "ABCD".Select(NoteBar).ToArray();
        bars[0].Marker = "Verse";
        bars[0].RepeatStart = true;
        bars[3].RepeatTotalPasses = 2;
        var (project, metadata) = Import(bars);
        Assert.Empty(Assert.Single(project.Arrangements).EventPoints);
        project.GpSourceBars = [.. GpSourceMap.Capture(metadata, project.Beats)];
        var path = Path.Combine(Path.GetTempPath(), $"gp-source-{Guid.NewGuid():N}.dcproj");
        try
        {
            var serializer = new RscpChartSerializer();
            serializer.Save(path, project);
            var loaded = serializer.Load(path);
            Assert.Equal(project.GpSourceBars.AsEnumerable(), loaded.GpSourceBars);
            var arrangement = Assert.Single(loaded.Arrangements);
            var group = Assert.Single(LinkSuggestionFinder.Detect(loaded, arrangement,
                LinkDetectionScope.UnlinkedOnly, MeasureContentOptions.Detection));
            Assert.True(group.FromRepeats);
            Assert.Equal("Verse", group.Name);
            Assert.Equal(4, group.AlignedEdges);
            Assert.Equal([0, 4], group.FirstMeasures);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RehearsalMarksBreakATieBetweenEqualLengthRuns()
    {
        var bars = "ABABAZ".Select(NoteBar).ToArray();
        bars[1].Marker = "Verse";
        bars[3].Marker = "Chorus";
        bars[5].Marker = "Outro";
        var (project, metadata) = Import(bars);
        project.GpSourceBars = [.. GpSourceMap.Capture(metadata, project.Beats)];

        Assert.Equal("2:1,3", ApplyAndMeasure(project, null, new HashSet<int> { 1, 3, 5 }));
    }

    private string ApplyAndMeasure(ChartProject project, IReadOnlyList<int>? source, IReadOnlySet<int>? markers = null)
    {
        var arrangement = Assert.Single(project.Arrangements);
        var before = ChartProjectCloner.CloneLevel(Assert.Single(arrangement.Levels));
        var rule = MeasureContentOptions.Detection;
        var suggestions = LinkSuggestionFinder.Find(project, [arrangement], source, rule);
        LinkSuggestionFinder.LinkAll(project, suggestions, rule)?.Execute();
        var score = GpAutoLinkScorecard.Measure(project, arrangement, before, source, markers);
        output.WriteLine(score.ToString());
        Assert.Equal(0, score.CrossedEdges);
        Assert.Equal(0, score.ShapeCountDelta);
        Assert.True(score.NotesEqual);
        if (markers != null) Assert.Equal(2, score.MarkerAlignedRuns);
        return score.Placements;
    }

    internal static (ChartProject Project, GpImportMetadata Metadata) Load(string path)
    {
        var importer = new GpImporter();
        var imported = importer.ImportTracksWithMetadata(path, [.. importer.GetAvailableTracks(path).Select(t => t.Id)]);
        var meta = imported.Metadata;
        var beats = GpImportHealthProbe.PrepareArrangements(imported);
        return (new ChartProject { Beats = [.. beats], Arrangements = [.. imported.Arrangements] }, meta);
    }

    private static (ChartProject Project, GpImportMetadata Metadata) Import(FxMeasure[] measures)
    {
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", measures);
        try
        {
            return Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static FxMeasure NoteBar(char letter) =>
        new FxMeasure().AddVoice(FxBeat.Note(0, 0, letter >= 'X' ? letter - 'X' + 18 : letter - 'A' + 1));
}
