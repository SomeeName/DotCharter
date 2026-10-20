using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

public sealed class DynamicDifficultyEngineTests
{
    private static LivePhraseSpan Span(string name, int maxDifficulty = 5, int iteration = 0)
        => new(iteration, name, 0, 1000, maxDifficulty);

    [Fact]
    public void StartsAtInitialLevel()
    {
        var engine = new DynamicDifficultyEngine { InitialLevel = 2 };
        Assert.Equal(2, engine.LevelFor(Span("verse")));
    }

    [Fact]
    public void AccuratePhrase_LevelsUp()
    {
        var engine = new DynamicDifficultyEngine();
        Assert.True(engine.CompletePhrase(Span("verse"), hits: 19, misses: 1));
        Assert.Equal(1, engine.LevelFor(Span("verse")));
    }

    [Fact]
    public void PoorPhrase_LevelsDown()
    {
        var engine = new DynamicDifficultyEngine { InitialLevel = 3 };
        Assert.True(engine.CompletePhrase(Span("verse"), hits: 2, misses: 8));
        Assert.Equal(2, engine.LevelFor(Span("verse")));
    }

    [Fact]
    public void MiddlingPhrase_HoldsLevel()
    {
        var engine = new DynamicDifficultyEngine { InitialLevel = 2 };
        Assert.False(engine.CompletePhrase(Span("verse"), hits: 7, misses: 3));
        Assert.Equal(2, engine.LevelFor(Span("verse")));
    }

    [Fact]
    public void EmptyPhrase_NeverMovesLevel()
    {
        var engine = new DynamicDifficultyEngine { InitialLevel = 2 };
        Assert.False(engine.CompletePhrase(Span("rest"), hits: 0, misses: 0));
        Assert.Equal(2, engine.LevelFor(Span("rest")));
    }

    [Fact]
    public void LevelIsSharedAcrossIterationsOfTheSamePhrase()
    {
        var engine = new DynamicDifficultyEngine();
        engine.CompletePhrase(Span("riff", iteration: 0), hits: 10, misses: 0);
        // A later iteration of the same phrase plays at the raised level.
        Assert.Equal(1, engine.LevelFor(Span("riff", iteration: 7)));
    }

    [Fact]
    public void LevelIsClampedToPhraseCeilingAndZero()
    {
        var engine = new DynamicDifficultyEngine();
        var span = Span("easy", maxDifficulty: 1);
        engine.CompletePhrase(span, 10, 0);
        engine.CompletePhrase(span, 10, 0);
        engine.CompletePhrase(span, 10, 0);
        Assert.Equal(1, engine.LevelFor(span));

        engine.CompletePhrase(span, 0, 10);
        engine.CompletePhrase(span, 0, 10);
        engine.CompletePhrase(span, 0, 10);
        Assert.Equal(0, engine.LevelFor(span));
    }

    [Fact]
    public void AverageLevel_ReflectsPerPhraseState()
    {
        var engine = new DynamicDifficultyEngine();
        var a = Span("a");
        var b = Span("b", iteration: 1);
        engine.CompletePhrase(a, 10, 0); // a -> 1
        Assert.Equal(0.5, engine.AverageLevel(new[] { a, b }), 3);
    }
}
