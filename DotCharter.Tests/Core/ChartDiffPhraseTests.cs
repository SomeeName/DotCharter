using DotCharter.Core.Diff;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Core;

public sealed class ChartDiffPhraseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingPhraseMetadata_DiffersFromAnExplicitZeroCeiling(bool removeFromNew)
    {
        var oldArrangement = Build();
        var newArrangement = Build();
        foreach (var arrangement in new[] { oldArrangement, newArrangement })
        {
            arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
            arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 1 });
        }
        var withoutMetadata = removeFromNew ? newArrangement : oldArrangement;
        withoutMetadata.PhraseInfos.Remove("verse");
        var beats = Enumerable.Range(0, 4).Select(i => new BeatMarker { Time = i * 500 }).ToList();

        var before = Assert.Single(LiveExpectedTimeline.BuildPhraseSpans(oldArrangement, beats));
        var after = Assert.Single(LiveExpectedTimeline.BuildPhraseSpans(newArrangement, beats));
        Assert.NotEqual(before.MaxDifficulty, after.MaxDifficulty);
        var entry = Assert.Single(ChartDiffEngine.Diff(oldArrangement, newArrangement).Structure);
        Assert.Equal("verse", entry.Label);
        Assert.Equal(new[] { "MaxDifficulty" }, entry.ChangedFields);

        var withMetadata = removeFromNew ? oldArrangement : newArrangement;
        withMetadata.PhraseInfos["verse"].MaxDifficulty = 1;
        Assert.True(ChartDiffEngine.Diff(oldArrangement, newArrangement).IsEmpty);
    }

    private static Arrangement Build()
    {
        var arrangement = new Arrangement();
        arrangement.Phrases.Add("verse");
        arrangement.Phrases.Add("chorus");
        arrangement.PhraseInfos.Add("verse", new PhraseInfo());
        arrangement.PhraseInfos.Add("chorus", new PhraseInfo());
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(1), 0));
        return arrangement;
    }

    public static IEnumerable<object[]> PhraseFields() => DomainFieldProbe.SettableProperties(typeof(PhraseInfo))
        .Select(property => new object[] { property.Name });

    [Theory]
    [MemberData(nameof(PhraseFields))]
    public void PhraseMetadataIsComparedByNameEvenWithoutAnIteration(string field)
    {
        var oldArrangement = Build();
        var newArrangement = Build();
        var property = typeof(PhraseInfo).GetProperty(field)!;
        property.SetValue(newArrangement.PhraseInfos["chorus"], DomainFieldProbe.Distinctive(property.PropertyType));

        var result = ChartDiffEngine.Diff(oldArrangement, newArrangement);

        Assert.False(result.IsEmpty);
        Assert.True(result.SoundsIdentical);
        var entry = Assert.Single(result.Structure);
        Assert.Equal("PhraseInfo", entry.Kind.ToString());
        Assert.Equal(ChartDiffChange.Changed, entry.Change);
        Assert.Equal("chorus", entry.Label);
        Assert.Equal(-1, entry.DifficultyIndex);
        Assert.Equal(new[] { field }, entry.ChangedFields);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AbsentMetadataUsesTheDocumentedDefaults(bool removeFromNew)
    {
        var oldArrangement = Build();
        var newArrangement = Build();
        var withoutMetadata = removeFromNew ? newArrangement : oldArrangement;
        withoutMetadata.PhraseInfos.Clear();

        Assert.True(ChartDiffEngine.Diff(oldArrangement, newArrangement).IsEmpty);

        var withMetadata = removeFromNew ? oldArrangement : newArrangement;
        withMetadata.PhraseInfos["verse"].Solo = true;
        var entry = Assert.Single(ChartDiffEngine.Diff(oldArrangement, newArrangement).Structure);
        Assert.Equal(new[] { "Solo" }, entry.ChangedFields);
        Assert.Equal("verse", entry.Label);
    }

    [Fact]
    public void PhraseListRenumberingDoesNotChangeMetadataOrIterations()
    {
        var oldArrangement = Build();
        var newArrangement = Build();
        newArrangement.Phrases.Reverse();
        newArrangement.PhraseIterations[0].PhraseId = 1;
        newArrangement.PhraseInfos["verse"].Solo = true;
        oldArrangement.PhraseInfos["verse"].Solo = true;

        Assert.True(ChartDiffEngine.Diff(oldArrangement, newArrangement).IsEmpty);
    }

    [Fact]
    public void AddedRemovedAndChangedIterationsUseTheirOwnArrangementsNames()
    {
        var oldArrangement = Build();
        var newArrangement = Build();
        newArrangement.Phrases.Reverse();
        newArrangement.PhraseIterations.Clear();
        newArrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(1), 0));
        newArrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(3), 0));
        oldArrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(5), 0));

        var entries = ChartDiffEngine.Diff(oldArrangement, newArrangement).Structure;

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Change == ChartDiffChange.Changed && e.Label == "chorus");
        Assert.Contains(entries, e => e.Change == ChartDiffChange.Added && e.Label == "chorus");
        Assert.Contains(entries, e => e.Change == ChartDiffChange.Removed && e.Label == "verse");
    }

    [Fact]
    public void AddedIterationResolvesANewPhraseOutsideTheOldPhraseList()
    {
        var oldArrangement = Build();
        var newArrangement = Build();
        newArrangement.Phrases.Add("solo");
        newArrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(3), 2));

        var entry = Assert.Single(ChartDiffEngine.Diff(oldArrangement, newArrangement).Structure);

        Assert.Equal(ChartDiffChange.Added, entry.Change);
        Assert.Equal("solo", entry.Label);
    }
}
