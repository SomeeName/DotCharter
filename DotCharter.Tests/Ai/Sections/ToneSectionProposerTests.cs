using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Sections;
using DotCharter.Services.Ai.Match.Similarity;

namespace DotCharter.Tests.Ai.Sections;

/// <summary>
///     The proposer over synthetic stems, with the REAL extractor and the REAL
///     distance.
///     <para>
///         <b>The thresholds are in the metric's own units, so a fake metric would
///         make every one of these cases vacuous.</b> <c>MinNovelty</c> = 0.35 means
///         something only against the scale <c>ToneSimilarity</c> documents — two
///         performances through one rig at 0.1–0.3, a clean amp against a fuzz above
///         1.0 — and a fake extractor scaled differently would pass these tests while
///         the shipping configuration found a boundary every four seconds.
///     </para>
///     <para>
///         <b>The stems are as short as the case allows, because the real extractor is
///         not cheap.</b> One <c>tonefx-v2</c> vector over a four-second window costs
///         roughly 0.9 s here (three STFT scales, the longest 16,384 samples), and the
///         scan takes one per second of stem — so a 16-second stem is 13 of them and a
///         careless 60-second one would put minutes into the default gate on its own.
///         Sixteen seconds with the splice at eight is the shortest stem that still
///         holds two sections of more than <c>ToneSection.MinDuration</c>.
///     </para>
/// </summary>
public sealed class ToneSectionProposerTests
{
    private const double Half = 8.0;
    private const double Whole = 16.0;
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    private static ToneSectionProposer NewProposer(MonoAudio stem, out SectionFakes.FakeIsolator isolator)
    {
        isolator = new SectionFakes.FakeIsolator(stem);
        return new ToneSectionProposer(isolator, new ToneSimilarity(), FakeLocalization.English());
    }

    private static MonoAudio Spliced() => SectionFakes.Spliced(Half, Half);

    private static Task<IReadOnlyList<ToneSectionProposal>> ProposeAsync(
        ToneSectionProposer proposer, IReadOnlyList<ToneSection>? chart = null) =>
        proposer.ProposeAsync("song.wav", ToneStems.Guitar, chart ?? [], null, TestContext.Current.CancellationToken);

    private static ToneSection Chart(string label, double from, double to) =>
        new(label, label, ToneStems.Guitar, TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to));

    /// <summary>
    ///     Two timbres spliced at a known instant: exactly one boundary, within the
    ///     agreement window of the splice, tiling the stem — and labelled "Part n"
    ///     with the gain heuristic's word. One scan, several assertions, because the
    ///     scan is the expensive part.
    /// </summary>
    [Fact]
    public async Task TwoTimbresSpliced_ProduceOneBoundaryAtTheSplice()
    {
        var proposals = await ProposeAsync(NewProposer(Spliced(), out _));

        Assert.Equal(2, proposals.Count);
        Assert.All(proposals, p => Assert.Equal(ToneSectionOrigins.Audio, p.Origin));
        Assert.Equal(TimeSpan.Zero, proposals[0].Section.From);
        Assert.True(
            (proposals[1].Section.From - TimeSpan.FromSeconds(Half)).Duration() <= Tolerance,
            $"the boundary landed at {proposals[1].Section.From.TotalSeconds:0.0} s, " +
            $"not within {Tolerance.TotalSeconds:0} s of {Half:0}");

        // They tile the stem: no gap, no overlap, nothing shorter than the minimum.
        Assert.Equal(proposals[0].Section.To, proposals[1].Section.From);
        Assert.Equal(Whole, proposals[1].Section.To.TotalSeconds, 1);
        Assert.All(proposals, p => Assert.True(p.Section.IsUseful));

        // Labels: numbered parts, with the gain heuristic's word appended.
        Assert.Equal("Part 1" + SectionGainHeuristic.CleanSuffix, proposals[0].Section.Label);
        Assert.Equal("Part 2" + SectionGainHeuristic.DrivenSuffix, proposals[1].Section.Label);

        // Confidence is the evidence for the boundary; the opening part inherits it
        // from the change that ends it.
        Assert.True(proposals[1].Confidence is > 0 and <= 1.0);
        Assert.Equal(proposals[0].Confidence, proposals[1].Confidence, 6);
    }

    /// <summary>One timbre throughout is ONE part — not "no answer", and emphatically
    /// not a boundary every time the player changes note.</summary>
    [Fact]
    public async Task AConstantTone_ProducesNoBoundaries()
    {
        var proposal = Assert.Single(await ProposeAsync(NewProposer(SectionFakes.Constant(Whole), out _)));

        Assert.Equal(TimeSpan.Zero, proposal.Section.From);
        Assert.Equal(Whole, proposal.Section.To.TotalSeconds, 1);
        Assert.Equal(ToneSectionOrigins.Audio, proposal.Origin);
        Assert.Equal(1.0, proposal.Confidence, 3);
    }

    /// <summary>A charted change the audio also heard is promoted, and the audio peak
    /// is CONSUMED — it does not go on to open a second section two seconds away from
    /// the charted one.</summary>
    [Fact]
    public async Task AChartBoundaryTheAudioAgreesWith_BecomesChartPlusAudio()
    {
        var proposals = await ProposeAsync(
            NewProposer(Spliced(), out _), [Chart("Intro", 0, Half), Chart("Solo", Half, Whole)]);

        Assert.Equal(2, proposals.Count);
        Assert.Equal(ToneSectionOrigins.Chart, proposals[0].Origin);
        Assert.Equal(ToneSectionOrigins.Both, proposals[1].Origin);
        Assert.Equal(["Intro", "Solo"], proposals.Select(p => p.Section.Label));
        Assert.All(proposals, p => Assert.Equal(1.0, p.Confidence, 3));
    }

    /// <summary>An audio boundary nowhere near a charted one is kept as a SUGGESTION —
    /// "the chart missed a change here" is the whole reason to scan a song that
    /// already has a chart. And a charted boundary the audio did not hear is still
    /// authoritative: a segmenter never overrules the charter.</summary>
    [Fact]
    public async Task TheChartAndTheAudioEachKeepWhatOnlyTheyFound()
    {
        var proposals = await ProposeAsync(
            NewProposer(Spliced(), out _), [Chart("Whole song", 0, Whole), Chart("Outro", 14, Whole)]);

        Assert.Equal(3, proposals.Count);
        Assert.Equal(ToneSectionOrigins.Chart, proposals[0].Origin);
        Assert.Equal("Whole song", proposals[0].Section.Label);

        // The audio found a change at ~8 s that the chart does not have.
        Assert.Equal(ToneSectionOrigins.Audio, proposals[1].Origin);
        Assert.True((proposals[1].Section.From - TimeSpan.FromSeconds(Half)).Duration() <= Tolerance);
        Assert.StartsWith("Part 2", proposals[1].Section.Label, StringComparison.Ordinal);

        // …and the chart's 0:14 change survives it untouched, at confidence 1.
        Assert.Equal(ToneSectionOrigins.Chart, proposals[2].Origin);
        Assert.Equal("Outro", proposals[2].Section.Label);
        Assert.Equal(1.0, proposals[2].Confidence, 3);
    }

    [Fact]
    public async Task ItIsDeterministic()
    {
        var stem = Spliced();
        var first = await ProposeAsync(NewProposer(stem, out _));
        var second = await ProposeAsync(NewProposer(stem, out _));

        Assert.Equal(
            first.Select(p => (p.Section.From, p.Section.To, p.Origin, p.Confidence, p.Section.Label)),
            second.Select(p => (p.Section.From, p.Section.To, p.Origin, p.Confidence, p.Section.Label)));
    }

    /// <summary>It isolates ONCE, for the stem it was asked about — the scan runs over
    /// the whole stem, not over a separation per window. The status line names where
    /// it is, so a four-minute song does not look like a hang.</summary>
    [Fact]
    public async Task ItIsolatesTheRequestedStemOnce_AndSaysWhereItIsScanning()
    {
        var proposer = NewProposer(SectionFakes.Constant(Whole), out var isolator);
        var status = new SectionFakes.Lines();

        await proposer.ProposeAsync(
            "song.wav", ToneStems.Bass, [], status, TestContext.Current.CancellationToken);

        Assert.Equal(1, isolator.Calls);
        Assert.Equal(ToneStems.Bass, Assert.Single(isolator.Requested));
        Assert.Contains(status.Reported, l => l.StartsWith("Scanning 0:00", StringComparison.Ordinal));
        Assert.Contains(status.Reported, l => l.StartsWith("Scanning 0:10", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A chart is an answer on its own. Losing it because no separation backend is
    ///     installed would be the worst of both worlds: the ground truth is on screen
    ///     and the app claims to know nothing.
    /// </summary>
    [Fact]
    public async Task AFailedIsolationStillProposesTheChart()
    {
        var isolator = new SectionFakes.FakeIsolator(SectionFakes.Constant(4.0));
        isolator.Failures[ToneStems.Guitar] = "no separation backend is available";
        var proposer = new ToneSectionProposer(isolator, new ToneSimilarity(), FakeLocalization.English());

        var proposals = await proposer.ProposeAsync(
            "song.wav", ToneStems.Guitar, [Chart("Verse", 0, 30), Chart("Solo", 30, 60)], null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, proposals.Count);
        Assert.All(proposals, p => Assert.Equal(ToneSectionOrigins.Chart, p.Origin));
        Assert.Equal(60.0, proposals[1].Section.To.TotalSeconds, 1);
    }

    /// <summary>…and a failed isolation with NO chart is a real failure that must
    /// surface, not an empty list that reads as "this song has no parts".</summary>
    [Fact]
    public async Task AFailedIsolationWithNoChartThrows()
    {
        var isolator = new SectionFakes.FakeIsolator(SectionFakes.Constant(4.0));
        isolator.Failures[ToneStems.Guitar] = "no separation backend is available";
        var proposer = new ToneSectionProposer(isolator, new ToneSimilarity(), FakeLocalization.English());

        await Assert.ThrowsAsync<InvalidOperationException>(() => proposer.ProposeAsync(
            "song.wav", ToneStems.Guitar, [], null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnknownStemIsRefusedBeforeAnythingIsIsolated()
    {
        var proposer = NewProposer(SectionFakes.Constant(4.0), out var isolator);

        await Assert.ThrowsAsync<ArgumentException>(() => proposer.ProposeAsync(
            "song.wav", "guitars", [], null, TestContext.Current.CancellationToken));

        Assert.Equal(0, isolator.Calls);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        var proposer = NewProposer(SectionFakes.Constant(4.0), out _);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            proposer.ProposeAsync("song.wav", ToneStems.Guitar, [], null, cts.Token));
    }
}
