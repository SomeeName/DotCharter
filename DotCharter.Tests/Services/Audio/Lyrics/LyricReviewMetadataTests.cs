using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Services.Audio.Lyrics;

public sealed class LyricReviewMetadataTests
{
    [Fact]
    public void FoldingPreservesTheScoreAndFlagsDoubtfulPlacements()
    {
        var sheet = LyricSheetParser.Parse("a-b-c");
        AlignedSyllable[] aligned = [new(0, 1, 1.02, 0.01, false), new(1, 1.04, 1.06, 0.9, false), new(2, 4, 4.1, 0.9, false)];
        var beats = Enumerable.Range(0, 24).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList();

        var lyrics = LyricAutoSyncService.BuildPhrases(sheet, aligned, beats, LyricAutoSyncOptions.Default)
            .SelectMany(p => p.Lyrics).ToArray();

        var confidence = typeof(LyricEvent).GetProperty("AlignmentConfidence");
        var reasons = typeof(LyricEvent).GetProperty("ReviewReasons");
        Assert.NotNull(confidence);
        Assert.NotNull(reasons);
        Assert.Equal(0.01, confidence.GetValue(lyrics[0]));
        Assert.Contains("LowScore", reasons.GetValue(lyrics[0])!.ToString());
        Assert.Contains("Compressed", reasons.GetValue(lyrics[0])!.ToString());
        Assert.Contains("Gap", reasons.GetValue(lyrics[1])!.ToString());
    }
}
