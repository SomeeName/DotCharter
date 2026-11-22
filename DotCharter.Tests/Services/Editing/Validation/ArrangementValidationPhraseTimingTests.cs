namespace DotCharter.Tests.Services.Editing.Validation;

public sealed class ArrangementValidationPhraseTimingTests
{
    [Fact]
    public void AnAuthoredPhraseOverridesAnImportedSilenceAtTheSamePosition()
    {
        var arrangement = new Arrangement();
        arrangement.Phrases.Add("noguitar");
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(2), 0));
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(2), Phrase = "verse" });
        var context = Context(arrangement);

        Assert.False(context.IsInsideNoguitar(1500));
        Assert.True(context.IsOnPhraseBoundary(1000));
        Assert.Equal("verse", Assert.Single(context.Phrases).Name);
    }

    [Fact]
    public void ImportedPhrasesSupplyOrdinaryAndMoverBoundaries()
    {
        var arrangement = new Arrangement();
        arrangement.Phrases.Add("verse");
        arrangement.Phrases.Add("mover1");
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(2), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
        var context = Context(arrangement);

        Assert.True(context.IsOnPhraseBoundary(1000));
        Assert.False(context.IsOnPhraseBoundary(2000));
        Assert.True(context.IsOnMoverPhraseBoundary(2000));
        Assert.True(context.HasPhraseChangeOnSustain(new GuitarNote
        {
            Position = new FractionalPosition(1), Sustain = new FractionalPosition(1)
        }));
        Assert.False(context.HasPhraseChangeOnSustain(new GuitarNote
        {
            Position = new FractionalPosition(3), Sustain = new FractionalPosition(1)
        }));
    }

    [Theory]
    [InlineData("noguitar")]
    [InlineData("ng")]
    public void ImportedSilenceEndsAtTheNextAuthoredPhrase(string name)
    {
        var arrangement = new Arrangement();
        arrangement.Phrases.Add(name);
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(2), 0));
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse" });
        var context = Context(arrangement);

        Assert.False(context.IsInsideNoguitar(999));
        Assert.True(context.IsInsideNoguitar(1000));
        Assert.True(context.IsInsideNoguitar(1999));
        Assert.False(context.IsInsideNoguitar(2000));
    }

    private static ArrangementValidationContext Context(Arrangement arrangement) =>
        ArrangementValidationContext.Build(arrangement,
            Enumerable.Range(0, 8).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList(), null);
}
