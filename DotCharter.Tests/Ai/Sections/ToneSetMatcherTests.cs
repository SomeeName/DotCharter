using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Sections;

namespace DotCharter.Tests.Ai.Sections;

/// <summary>
///     The set matcher over fakes: the per-section loop, the prior rule, the failure
///     rule, progress and cancellation.
/// </summary>
public sealed class ToneSetMatcherTests
{
    private static readonly MonoAudio Di = new(SectionFakes.Plucks(2.0), SectionFakes.Rate);

    private static ToneSetMatchRequest Request(params ToneSection[] sections) =>
        new("song.wav", Di, sections, CandidatesPerSection: 3, BudgetPerSection: TimeSpan.FromSeconds(5),
            SongTitle: "Song", Artist: "Artist");

    private static SectionFakes.FakeIsolator NewIsolator() =>
        new(new MonoAudio(SectionFakes.Plucks(60.0), SectionFakes.Rate));

    [Fact]
    public async Task EverySectionIsMatched_InOrder_WithItsOwnStemAndSpan()
    {
        var isolator = NewIsolator();
        var matcher = new SectionFakes.RecordingMatcher();
        var set = new ToneSetMatcher(isolator, matcher, FakeLocalization.English());

        var result = await set.MatchAsync(
            Request(
                SectionFakes.Section("intro", 0, 10),
                SectionFakes.Section("verse", 10, 30, ToneStems.Bass)),
            null, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Sections.Count);
        Assert.All(result.Sections, s => Assert.NotNull(s.Match));
        Assert.Equal(["intro", "verse"], result.Sections.Select(s => s.Section.Id));
        Assert.Equal([ToneStems.Guitar, ToneStems.Bass], isolator.Requested);

        // The target handed to the matcher is the SECTION's audio, not the song's.
        Assert.Equal(10.0, matcher.Requests[0].Target.Duration.TotalSeconds, 1);
        Assert.Equal(20.0, matcher.Requests[1].Target.Duration.TotalSeconds, 1);
        Assert.Equal(3, matcher.Requests[0].Candidates);
        Assert.Equal(TimeSpan.FromSeconds(5), matcher.Requests[0].Budget);
        Assert.Same(Di, matcher.Requests[0].ReferenceDi);
    }

    /// <summary>The stem travels to the matcher as a HINT, so a bass part can be
    /// retrieved as a bass rig instead of as a dark guitar amp.</summary>
    [Fact]
    public async Task TheSectionsStemBecomesTheMatchersHint()
    {
        var matcher = new SectionFakes.RecordingMatcher();
        var set = new ToneSetMatcher(NewIsolator(), matcher, FakeLocalization.English());

        await set.MatchAsync(
            Request(SectionFakes.Section("bassline", 0, 20, ToneStems.Bass)),
            null, TestContext.Current.CancellationToken);

        Assert.Equal(ToneStems.Bass, Assert.Single(matcher.Requests).StemHint);
    }

    /// <summary>
    ///     A section that fails does not fail the SET. Four sections and one missing
    ///     bass stem must leave the user with three rigs, not with an error dialog.
    /// </summary>
    [Fact]
    public async Task AFailedSectionBecomesAnErrorAndTheSetContinues()
    {
        var isolator = NewIsolator();
        isolator.Failures[ToneStems.Bass] = "the stems contain no bass source";
        var set = new ToneSetMatcher(isolator, new SectionFakes.RecordingMatcher(), FakeLocalization.English());

        var result = await set.MatchAsync(
            Request(
                SectionFakes.Section("intro", 0, 10),
                SectionFakes.Section("bassline", 10, 30, ToneStems.Bass),
                SectionFakes.Section("outro", 30, 45)),
            null, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Sections.Count);
        Assert.NotNull(result.Sections[0].Match);
        Assert.Null(result.Sections[1].Match);
        Assert.Contains("no bass source", result.Sections[1].Error);
        Assert.NotNull(result.Sections[2].Match);
        Assert.Contains(result.Notes, n => n.Contains("2/3 sections matched", StringComparison.Ordinal));
    }

    /// <summary>A span the metric cannot measure is refused BEFORE a separation: the
    /// expensive half must not be spent on a section that was never going to produce
    /// an answer.</summary>
    [Fact]
    public async Task ASectionShorterThanTheMinimumIsSkippedWithoutIsolating()
    {
        var isolator = NewIsolator();
        var set = new ToneSetMatcher(isolator, new SectionFakes.RecordingMatcher(), FakeLocalization.English());

        var result = await set.MatchAsync(
            Request(SectionFakes.Section("stab", 10, 12)),
            null, TestContext.Current.CancellationToken);

        var only = Assert.Single(result.Sections);
        Assert.Null(only.Match);
        Assert.Contains("shorter than", only.Error);
        Assert.Equal(0, isolator.Calls);
    }

    [Fact]
    public async Task AnUnknownStemIsSkippedWithItsOwnReason()
    {
        var set = new ToneSetMatcher(NewIsolator(), new SectionFakes.RecordingMatcher(), FakeLocalization.English());

        var result = await set.MatchAsync(
            Request(SectionFakes.Section("part", 0, 20, "guitars")),
            null, TestContext.Current.CancellationToken);

        Assert.Contains("not a stem", Assert.Single(result.Sections).Error);
    }

    [Fact]
    public async Task ThePriorSourceIsAskedOnlyForSectionsThatHaveNoPrior()
    {
        var priors = new SectionFakes.FakePriorSource(
            available: true, new ToneMatchPrior(SectionFakes.Rig("amp"), "model"));
        var matcher = new SectionFakes.RecordingMatcher();
        var set = new ToneSetMatcher(NewIsolator(), matcher, FakeLocalization.English(), priors);
        var own = new ToneMatchPrior(SectionFakes.Rig("bassamp"), "user");

        await set.MatchAsync(
            Request(
                SectionFakes.Section("a", 0, 10),
                SectionFakes.Section("b", 10, 30, ToneStems.Guitar, own)),
            null, TestContext.Current.CancellationToken);

        Assert.Equal(1, priors.Calls);
        Assert.Equal("a", Assert.Single(priors.Asked).Id);
        Assert.Equal("model", matcher.Requests[0].Prior?.Source);
        Assert.Same(own, matcher.Requests[1].Prior);
    }

    [Fact]
    public async Task AnUnavailablePriorSourceIsNeverAsked()
    {
        var priors = new SectionFakes.FakePriorSource(available: false);
        var matcher = new SectionFakes.RecordingMatcher();
        var set = new ToneSetMatcher(NewIsolator(), matcher, FakeLocalization.English(), priors);

        await set.MatchAsync(Request(SectionFakes.Section("a", 0, 10)), null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, priors.Calls);
        Assert.Null(Assert.Single(matcher.Requests).Prior);
    }

    /// <summary>A prior that throws is a NOTE, never a lost section — the matcher can
    /// always start from retrieval instead.</summary>
    [Fact]
    public async Task APriorSourceThatThrowsDoesNotLoseTheSection()
    {
        var priors = new SectionFakes.FakePriorSource(available: true) { Throws = true };
        var set = new ToneSetMatcher(NewIsolator(), new SectionFakes.RecordingMatcher(), FakeLocalization.English(), priors);

        var result = await set.MatchAsync(Request(SectionFakes.Section("a", 0, 10)), null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(Assert.Single(result.Sections).Match);
        Assert.Contains(result.Notes, n => n.Contains("prior unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressNamesTheSectionAndTheCount()
    {
        var status = new SectionFakes.Lines();
        var set = new ToneSetMatcher(NewIsolator(), new SectionFakes.RecordingMatcher(), FakeLocalization.English());

        await set.MatchAsync(
            Request(SectionFakes.Section("a", 0, 10), SectionFakes.Section("b", 10, 30)),
            status, TestContext.Current.CancellationToken);

        Assert.Contains(status.Reported, l => l.StartsWith("Section 1 of 2:", StringComparison.Ordinal));
        Assert.Contains(status.Reported, l => l.StartsWith("Section 2 of 2:", StringComparison.Ordinal));
    }

    /// <summary>Notes carry the per-section elapsed time, which is what makes a slow
    /// section visible in a report instead of only in a stopwatch.</summary>
    [Fact]
    public async Task NotesCarryPerSectionElapsedAndTheBestDistance()
    {
        var set = new ToneSetMatcher(NewIsolator(), new SectionFakes.RecordingMatcher(), FakeLocalization.English());

        var result = await set.MatchAsync(Request(SectionFakes.Section("a", 0, 10)), null,
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Notes, n =>
            n.Contains("Section 1 of 1 (a, guitar)", StringComparison.Ordinal) &&
            n.Contains("best 0.250", StringComparison.Ordinal) &&
            n.EndsWith(" s", StringComparison.Ordinal));
        Assert.True(result.Elapsed > TimeSpan.Zero);
    }

    /// <summary>A user's Cancel is not a failed section and must not be recorded as
    /// one — it propagates.</summary>
    [Fact]
    public async Task CancellationPropagatesRatherThanBecomingAnError()
    {
        var set = new ToneSetMatcher(NewIsolator(), new SectionFakes.RecordingMatcher(), FakeLocalization.English());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => set.MatchAsync(
            Request(SectionFakes.Section("a", 0, 10)), null, cts.Token));
    }
}
