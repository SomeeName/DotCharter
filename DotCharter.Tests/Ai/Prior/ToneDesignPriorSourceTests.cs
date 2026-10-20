using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Prior;

namespace DotCharter.Tests.Ai.Prior;

/// <summary>
///     The model's guess at a section's rig. Everything here is about the REQUEST
///     TEXT and about not failing the match: the source owns no prompt, so the only
///     way it can be wrong is by describing the part badly — or by throwing where a
///     null was the contract.
/// </summary>
public sealed class ToneDesignPriorSourceTests
{
    private static ToneSection Section(
        string label = "chorus", string stem = ToneStems.Guitar, int fromSeconds = 65, int toSeconds = 92)
        => new("s1", label, stem, TimeSpan.FromSeconds(fromSeconds), TimeSpan.FromSeconds(toSeconds));

    private static ToneDesignResult Result(params string[] modules)
        => new(PriorFakes.Tone(PriorFakes.Rig(modules)), "because", 0.8, [], "{}",
            new AiUsage("claude-fake-1", 10, 20));

    [Fact]
    public async Task TheRequestNamesTheLabel_TheSpanAsMinutesAndSeconds_AndTheStem()
    {
        var design = new FakeToneDesignService(_ => Result("amp", "cab"));
        var source = new ToneDesignPriorSource(design, FakeLocalization.English(), () => PriorFakes.Claude);

        await source.GetPriorAsync(Section(), "Song", "Artist", null, CancellationToken.None);

        var text = design.LastRequest!.Description;
        Assert.Contains("chorus", text, StringComparison.Ordinal);
        Assert.Contains("1:05", text, StringComparison.Ordinal);
        Assert.Contains("1:32", text, StringComparison.Ordinal);
        Assert.Contains("guitar", text, StringComparison.Ordinal);
        Assert.Equal("Song", design.LastRequest.SongTitle);
        Assert.Equal("Artist", design.LastRequest.Artist);
    }

    /// <summary>
    ///     The design prompt's rule 6 ("a BASS request uses bassamp, never the guitar
    ///     amp") and its bass few-shot only fire if the request SAYS it is bass. This
    ///     is the one assertion that stands between a bass part and a guitar rig, and
    ///     it is on the text rather than on the answer because the answer is the
    ///     model's and this lane cannot test it.
    /// </summary>
    [Fact]
    public async Task ABassSection_SaysBassInTheRequest()
    {
        var design = new FakeToneDesignService(_ => Result("bassamp"));
        var source = new ToneDesignPriorSource(design, FakeLocalization.English(), () => PriorFakes.Claude);

        await source.GetPriorAsync(Section("verse", ToneStems.Bass), null, null, null, CancellationToken.None);

        var text = design.LastRequest!.Description;
        Assert.Contains("bass", text, StringComparison.Ordinal);
        Assert.DoesNotContain("guitar", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePriorIsTheDesignedRig_TaggedWithTheProviderThatAnsweredIt()
    {
        var design = new FakeToneDesignService(_ => Result("overdrive", "amp", "cab"));
        var source = new ToneDesignPriorSource(design, FakeLocalization.English(), () => PriorFakes.Claude);

        var prior = await source.GetPriorAsync(Section(), null, null, null, CancellationToken.None);

        Assert.NotNull(prior);
        Assert.Equal(["overdrive", "amp", "cab"], prior!.Rig.Modules.Select(m => m.ModuleTypeId));
        Assert.Equal("ai:claude", prior.Source);
    }

    [Fact]
    public async Task AnUnconfiguredProvider_IsUnavailableAndAsksNothing()
    {
        var design = new FakeToneDesignService(_ => Result("amp"), configured: false);
        var source = new ToneDesignPriorSource(design, FakeLocalization.English());

        Assert.False(source.IsAvailable);
        Assert.Null(await source.GetPriorAsync(Section(), null, null, null, CancellationToken.None));
        Assert.Equal(0, design.Calls);
    }

    /// <summary>A prior is an optimisation: losing it costs the match its head start
    /// and nothing else. Every design fault must therefore read as "no prior", not
    /// as a failed match.</summary>
    [Theory]
    [InlineData(ToneDesignFault.Network)]
    [InlineData(ToneDesignFault.RateLimited)]
    [InlineData(ToneDesignFault.ModelRefused)]
    [InlineData(ToneDesignFault.InvalidOutput)]
    [InlineData(ToneDesignFault.NotConfigured)]
    public async Task ADesignFault_IsNullAndNotAThrow(ToneDesignFault fault)
    {
        var design = new FakeToneDesignService(
            _ => throw new ToneDesignException(fault, "nope"));
        var source = new ToneDesignPriorSource(design, FakeLocalization.English(), () => PriorFakes.Claude);

        Assert.Null(await source.GetPriorAsync(Section(), null, null, null, CancellationToken.None));
    }

    /// <summary>Cancellation is the ONE thing that propagates: a user Cancel is not
    /// a prior that could not be obtained, it is a match that is over.</summary>
    [Fact]
    public async Task Cancellation_Propagates()
    {
        var design = new FakeToneDesignService(_ => Result("amp"));
        var source = new ToneDesignPriorSource(design, FakeLocalization.English(), () => PriorFakes.Claude);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetPriorAsync(Section(), null, null, null, cts.Token));
    }

    [Fact]
    public async Task NoHistoryAndNoCurrentTone_ItIsAFreshDesignEveryTime()
    {
        var design = new FakeToneDesignService(_ => Result("amp"));
        var source = new ToneDesignPriorSource(design, FakeLocalization.English(), () => PriorFakes.Claude);

        await source.GetPriorAsync(Section(), null, null, null, CancellationToken.None);

        Assert.Empty(design.LastRequest!.History);
        Assert.Null(design.LastRequest.CurrentTone);
    }

    /// <summary>A section the proposer labelled with nothing still has to read as
    /// something — "the  part" is a bug on screen and a model asked about nothing
    /// answers about nothing.</summary>
    [Fact]
    public async Task AnEmptyLabel_FallsBackToTheStem()
    {
        var design = new FakeToneDesignService(_ => Result("amp"));
        var source = new ToneDesignPriorSource(design, FakeLocalization.English(), () => PriorFakes.Claude);

        await source.GetPriorAsync(Section("  ", ToneStems.Piano), null, null, null, CancellationToken.None);

        Assert.Contains("the piano part", design.LastRequest!.Description, StringComparison.Ordinal);
    }
}
