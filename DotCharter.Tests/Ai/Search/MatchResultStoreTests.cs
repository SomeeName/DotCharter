using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     The per-run artefact: a match leaves its feature vectors and its breakdown
///     behind, and they come back the same.
/// </summary>
public sealed class MatchResultStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-w5b-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    private static readonly ToneSimilarity Metric = new();

    private static ToneFeatures Flat(float v)
    {
        var x = new float[ToneFeatureLayout.Length];
        for (var i = 0; i < x.Length; i++) x[i] = v;
        return new ToneFeatures(x, ToneFeatureExtractor.ExtractorId);
    }

    private static ToneMatchResult Result()
    {
        var target = Flat(0f);
        var reference = Flat(1f);
        var candidate = Flat(0.4f);
        var score = MatchScale.Score(target, candidate, reference, Metric, selfDistance: 0.3);

        return new ToneMatchResult(
            [
                new ToneMatchCandidate(
                    new DcRigPreset([new DcRigModule("amp", new Dictionary<string, float>(StringComparer.Ordinal))]),
                    score.Distance, "retrieval")
                {
                    PlausibilityPenalty = 0.25,
                    RankingDistance = score.Distance * 1.15,
                    Features = candidate.Vector,
                    Score = score
                }
            ],
            0.3, TimeSpan.FromSeconds(12.5), ["a note"])
        {
            TargetFeatures = target.Vector,
            ReferenceFeatures = reference.Vector,
            ReferenceDistance = score.ReferenceDistance
        };
    }

    [Fact]
    public void ARunLeavesItsVectorsBehind_AndTheyRoundTrip()
    {
        var store = new MatchResultStore(_root, () => new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
        var result = Result();

        var path = store.TryWrite(result, ToneFeatureExtractor.ExtractorId, ToneStems.Guitar, out var reason);

        Assert.Null(reason);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        var read = MatchArtifact.TryRead(path!);
        Assert.NotNull(read);
        Assert.Equal(MatchArtifact.CurrentVersion, read!.Version);
        Assert.Equal(ToneFeatureExtractor.ExtractorId, read.ExtractorId);
        Assert.Equal(ToneStems.Guitar, read.StemHint);
        Assert.Equal(result.TargetSelfDistanceFloor, read.SelfDistance, 9);
        Assert.Equal(result.ReferenceDistance, read.ReferenceDistance, 9);
        Assert.Equal(result.TargetFeatures, read.TargetFeatures);
        Assert.Equal(result.ReferenceFeatures, read.ReferenceFeatures);
        Assert.Equal(result.Notes, read.Notes);

        var stored = Assert.Single(read.Candidates);
        var original = result.Candidates[0];
        Assert.Equal(original.Origin, stored.Origin);
        Assert.Equal(original.Distance, stored.Distance, 9);
        Assert.Equal(original.RankingDistance, stored.RankingDistance, 9);
        Assert.Equal(original.PlausibilityPenalty, stored.PlausibilityPenalty, 9);
        Assert.Equal(original.Score!.Percent, stored.Percent);
        Assert.Equal(["amp"], stored.Chain);
        Assert.Equal(original.Features, stored.Features);
        Assert.Equal(original.Score.Blocks.Count, stored.Blocks.Count);
        for (var i = 0; i < stored.Blocks.Count; i++)
        {
            Assert.Equal(original.Score.Blocks[i].Name, stored.Blocks[i].Name);
            Assert.Equal(original.Score.Blocks[i].Contribution, stored.Blocks[i].Contribution, 9);
            Assert.Equal(original.Score.Blocks[i].ReferenceContribution, stored.Blocks[i].ReferenceContribution, 9);
        }
    }

    [Fact]
    public void AStoredResult_CanBeRescoredFromItsVectorsAlone()
    {
        // The point of persisting the vectors rather than the numbers: "why did this
        // score N" is answerable months later, without the song, the engine or the
        // library.
        var store = new MatchResultStore(_root);
        var path = store.TryWrite(Result(), ToneFeatureExtractor.ExtractorId, null, out _);
        var read = MatchArtifact.TryRead(path!)!;

        var rescored = MatchScale.Score(
            new ToneFeatures(read.TargetFeatures!, read.ExtractorId),
            new ToneFeatures(read.Candidates[0].Features!, read.ExtractorId),
            new ToneFeatures(read.ReferenceFeatures!, read.ExtractorId),
            Metric, read.SelfDistance);

        Assert.Equal(read.Candidates[0].Percent, rescored.Percent);
    }

    [Fact]
    public void AnUnratedCandidate_PersistsItsReason_NotAZero()
    {
        var target = Flat(0f);
        var candidate = Flat(0.4f);
        var score = MatchScale.Score(target, candidate, null, Metric, 0.3);
        var result = new ToneMatchResult(
            [
                new ToneMatchCandidate(new DcRigPreset([]), score.Distance, "retrieval")
                    { Features = candidate.Vector, Score = score }
            ],
            0.3, TimeSpan.Zero, [])
        { TargetFeatures = target.Vector };

        var path = new MatchResultStore(_root).TryWrite(result, ToneFeatureExtractor.ExtractorId, null, out _);
        var read = MatchArtifact.TryRead(path!)!;

        Assert.Null(read.Candidates[0].Percent);
        Assert.Equal(MatchUnrated.NoReference, read.Candidates[0].UnratedReason);
        Assert.Null(read.ReferenceFeatures);
    }

    [Fact]
    public void TheFolderIsBounded()
    {
        var store = new MatchResultStore(_root);
        for (var i = 0; i < MatchResultStore.KeepFiles + 7; i++)
            Assert.NotNull(store.TryWrite(Result(), ToneFeatureExtractor.ExtractorId, null, out _));

        Assert.Equal(MatchResultStore.KeepFiles, Directory.GetFiles(_root, "match-*.json").Length);
    }

    [Fact]
    public void AStoreThatCannotWrite_ReportsWhy_AndDoesNotThrow()
    {
        // A match is never lost to its own bookkeeping. A file where the folder
        // should be is the cheapest way to make the directory create fail.
        var blocked = Path.Combine(_root, "blocked");
        Directory.CreateDirectory(_root);
        File.WriteAllText(blocked, "not a directory");

        var path = new MatchResultStore(blocked).TryWrite(
            Result(), ToneFeatureExtractor.ExtractorId, null, out var reason);

        Assert.Null(path);
        Assert.NotNull(reason);
    }

    [Fact]
    public void AnotherVersion_IsRefused_RatherThanMapped()
    {
        var path = Path.Combine(_root, "match-other.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, """{"Version":99,"ExtractorId":"tonefx-v2","Candidates":[]}""");

        Assert.Null(MatchArtifact.TryRead(path));
        Assert.Null(MatchArtifact.TryRead(Path.Combine(_root, "absent.json")));
    }

    [Fact]
    public void TheShippingRootIsUnderTheLanesOwnFolder()
    {
        // Named here so a test can never be the thing that discovers it moved.
        Assert.EndsWith(Path.Combine("DotCharter", "ai-match", "results"),
            MatchResultStore.DefaultRoot(), StringComparison.Ordinal);
    }
}
