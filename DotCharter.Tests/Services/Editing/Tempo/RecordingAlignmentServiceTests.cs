using DotCharter.Services.Editing.Tempo.RecordingAlignment;
using DotCharter.Tests.Analysis;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Services.Editing.Tempo;

/// <summary>
///     Align to recording (#784) through the service the dialog drives: a real chart editor, the real
///     undo history and a WAV of <see cref="RecordingAlignmentFixture" /> on disk. Asserts error
///     bounds on the known ground truth, the undo shape, and that nothing is written before Apply.
/// </summary>
public sealed class RecordingAlignmentServiceTests : IDisposable
{
    private const double MaxErrorBoundMs = 6;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-784-" + Guid.NewGuid().ToString("N"));

    public RecordingAlignmentServiceTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0.5, "offset 0.5")]
    [InlineData(-0.5, "offset -0.5")]
    public async Task ADeclaredInstrumentOffsetRejectsStemsWithMatchingRawDurations(double offset, string offsetText)
    {
        var (h, _, _, audio) = Build(new RecordingAlignmentFixture());
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "stems")).FullName;
        var guitar = Path.Combine(folder, "guitar.wav");
        File.Copy(audio, guitar);
        h.Project.Stems.Add(new Stem { Name = "guitar", Path = guitar, Offset = offset });
        var logger = new FakeAppLogger();
        var service = new RecordingAlignmentService(new BeatResyncService(), h.History,
            new FakeConfigurationService(), h.Transport, h.Editor, logger);

        var result = await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.WholeSong, true, audio, Ct);

        Assert.Equal(RecordingAlignmentFailure.None, result.Failure);
        Assert.NotNull(result.Proposal);
        Assert.Equal(RecordingAlignmentSource.Mix, result.Proposal.Source);
        Assert.True(logger.HasEntryContaining("WARN", guitar));
        Assert.True(logger.HasEntryContaining("WARN", offsetText));
    }

    [Fact]
    public async Task AnalysisUsesTheProjectsPaddedInstrumentStem()
    {
        var (h, _, service, audio) = Build(new RecordingAlignmentFixture());
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "stems", "silence-2")).FullName;
        var padded = Path.Combine(folder, "guitar.wav");
        File.Copy(audio, padded);
        File.WriteAllText(Path.Combine(_dir, "stems", "guitar.wav"), "unusable old stem");
        h.Project.Stems.Add(new Stem { Name = "guitar", Path = padded });

        var result = await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.WholeSong, true, audio, Ct);

        Assert.Equal(RecordingAlignmentFailure.None, result.Failure);
        Assert.NotNull(result.Proposal);
        Assert.Equal(RecordingAlignmentSource.Stems, result.Proposal.Source);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task AnalyzeThenApply_PutsTheBeatsOnTheRecording_AsOneUndoNode()
    {
        var (h, fx, service, audio) = Build(new RecordingAlignmentFixture());
        var before = h.Project.Beats.Select(b => b.Clone()).ToList();
        var notesBefore = h.Project.Arrangements[0].Levels[0].Notes.Select(n => n.Position).ToList();
        var steps = h.History.GetUndoSteps().Count;

        var analysis = await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.WholeSong, keepAnchors: true, audio, Ct);
        Assert.Equal(RecordingAlignmentFailure.None, analysis.Failure);
        Assert.Equal(before.Select(b => b.Time), h.Project.Beats.Select(b => b.Time));

        var proposal = analysis.Proposal!;
        Assert.All(proposal.Sections, s => Assert.True(s.SuggestApply, $"section {s.Index}: {s.Confidence:0.00}"));
        Assert.Equal(RecordingAlignmentApplyStatus.Applied, service.Apply(h.Project, proposal, AllSuggested(proposal)));

        Assert.True(MaxError(h, fx) < MaxErrorBoundMs, $"max beat error {MaxError(h, fx):0.0} ms");
        Assert.Equal(notesBefore, h.Project.Arrangements[0].Levels[0].Notes.Select(n => n.Position));
        Assert.Equal(before.Select(b => b.IsMeasure), h.Project.Beats.Select(b => b.IsMeasure));
        Assert.Equal(before.Select(b => b.BeatsInMeasure), h.Project.Beats.Select(b => b.BeatsInMeasure));
        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        var aligned = h.Project.Beats.Select(b => b.Time).ToList();

        h.History.Undo();
        Assert.Equal(before.Select(b => b.Time), h.Project.Beats.Select(b => b.Time));
        Assert.Equal(before.Select(b => b.Bpm), h.Project.Beats.Select(b => b.Bpm));

        h.History.Redo();
        Assert.Equal(aligned, h.Project.Beats.Select(b => b.Time));
    }

    [Fact]
    public async Task ACancelledAnalysis_LeavesTheProjectUntouched()
    {
        var (h, _, service, audio) = Build(new RecordingAlignmentFixture());
        var before = h.Project.Beats.Select(b => b.Time).ToList();
        var steps = h.History.GetUndoSteps().Count;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AnalyzeAsync(h.Project, RecordingAlignmentRange.WholeSong, true, audio, cts.Token));

        Assert.Equal(before, h.Project.Beats.Select(b => b.Time));
        Assert.Equal(steps, h.History.GetUndoSteps().Count);
    }

    [Fact]
    public async Task AnchoredBeats_StayPut_UnlessTheCharterLetsThemGo()
    {
        // A grid already near the recording, with beat 20 placed by hand 30 ms off it.
        var (h, fx, service, audio) = Build(new RecordingAlignmentFixture());
        var slope = (fx.TrueTimes[^1] - fx.TrueTimes[0]) / (RecordingAlignmentFixture.Beats - 1);
        for (var i = 0; i < h.Project.Beats.Count; i++) h.Project.Beats[i].Time = fx.TrueTimes[0] + i * slope;
        h.Project.Beats[20].Time = fx.TrueTimes[20] + 30;
        h.Project.Beats[20].IsAnchor = true;
        var anchored = h.Project.Beats[20].Time;

        var kept = (await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.WholeSong, true, audio, Ct)).Proposal!;
        Assert.Equal(1, kept.Plan.AnchoredInRange);
        Assert.Equal(RecordingAlignmentApplyStatus.Applied, service.Apply(h.Project, kept, AllSuggested(kept)));
        Assert.Equal(anchored, h.Project.Beats[20].Time);
        Assert.True(h.Project.Beats[20].IsAnchor);

        h.History.Undo();
        var released = (await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.WholeSong, false, audio, Ct)).Proposal!;
        Assert.Equal(RecordingAlignmentApplyStatus.Applied, service.Apply(h.Project, released, AllSuggested(released)));
        Assert.True(Math.Abs(h.Project.Beats[20].Time - fx.TrueTimes[20]) < MaxErrorBoundMs, "released, the beat goes where it is played");
    }

    [Fact]
    public async Task FromBar_MovesNothingBeforeThatBar()
    {
        var (h, fx, service, audio) = Build(new RecordingAlignmentFixture());
        // Put bars 1–4 where they really are first, as a previous run would have.
        for (var i = 0; i < 16; i++) h.Project.Beats[i].Time = fx.TrueTimes[i];
        for (var i = 16; i < h.Project.Beats.Count; i++) h.Project.Beats[i].Time = fx.TrueTimes[15] + (i - 15) * 500;
        var before = h.Project.Beats.Select(b => b.Time).ToList();

        var proposal = (await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.FromBarNumber(5), true, audio, Ct)).Proposal!;
        Assert.Equal(48, proposal.Plan.FreeBeats);
        Assert.Equal(RecordingAlignmentApplyStatus.Applied, service.Apply(h.Project, proposal, AllSuggested(proposal)));

        Assert.Equal(before.Take(16), h.Project.Beats.Take(16).Select(b => b.Time));
        Assert.True(MaxError(h, fx) < MaxErrorBoundMs, $"max beat error {MaxError(h, fx):0.0} ms");
    }

    [Fact]
    public async Task AnEditAfterTheAnalysis_MakesTheProposalStale()
    {
        var (h, _, service, audio) = Build(new RecordingAlignmentFixture());
        var proposal = (await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.WholeSong, true, audio, Ct)).Proposal!;
        h.Project.Beats[5].Time += 1;
        var steps = h.History.GetUndoSteps().Count;

        Assert.Equal(RecordingAlignmentApplyStatus.Stale, service.Apply(h.Project, proposal, AllSuggested(proposal)));
        Assert.Equal(steps, h.History.GetUndoSteps().Count);
    }

    [Fact]
    public async Task ALockedBeatMap_OrNoAudio_IsRefusedUpFront()
    {
        var (h, _, service, audio) = Build(new RecordingAlignmentFixture());
        Assert.Equal(RecordingAlignmentFailure.NoAudio,
            (await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.WholeSong, true, Path.Combine(_dir, "missing.wav"), Ct)).Failure);
        h.Project.IsBeatMapLocked = true;
        Assert.Equal(RecordingAlignmentFailure.Locked,
            (await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.WholeSong, true, audio, Ct)).Failure);
    }

    [Fact]
    public async Task AMismatchedSection_IsNotSuggested_AndSkippingItKeepsTheGridAscending()
    {
        var (h, _, service, audio) = Build(new RecordingAlignmentFixture(mismatchedSection: 2));
        var proposal = (await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.WholeSong, true, audio, Ct)).Proposal!;

        Assert.False(proposal.Sections[2].SuggestApply, $"confidence {proposal.Sections[2].Confidence:0.00}");
        Assert.Equal(RecordingAlignmentApplyStatus.Applied, service.Apply(h.Project, proposal, AllSuggested(proposal)));
        for (var i = 1; i < h.Project.Beats.Count; i++) Assert.True(h.Project.Beats[i].Time > h.Project.Beats[i - 1].Time);
    }

    [Fact]
    public async Task AChartWithoutNotes_IsRefusedBeforeAnyAudioIsDecoded()
    {
        var (h, _, service, _) = Build(new RecordingAlignmentFixture());
        h.Project.Arrangements[0].Levels[0].Notes.Clear();

        var analysis = await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.WholeSong, true, Undecodable(), Ct);

        Assert.Equal(RecordingAlignmentFailure.NoNotes, analysis.Failure);
        Assert.Null(analysis.Proposal);
    }

    [Fact]
    public async Task ARangeWithoutNotes_IsRefusedBeforeAnyAudioIsDecoded_AndARangeWithNotesStillRuns()
    {
        var (h, _, service, audio) = Build(new RecordingAlignmentFixture());
        var notes = h.Project.Arrangements[0].Levels[0].Notes;
        foreach (var note in notes.Where(n => n.Position.BeatId >= 48).ToList()) notes.Remove(note);

        Assert.Equal(RecordingAlignmentFailure.NoNotes,
            (await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.FromBarNumber(13), true, Undecodable(), Ct)).Failure);
        Assert.Equal(RecordingAlignmentFailure.NoNotes,
            (await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.Span(24_000, 31_500), true, Undecodable(), Ct)).Failure);
        Assert.Equal(RecordingAlignmentFailure.None,
            (await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.FromBarNumber(9), true, audio, Ct)).Failure);
    }

    [Fact]
    public async Task Apply_NeverTakesTheAlignmentOfASectionWithoutNotes_EvenWhenAskedTo()
    {
        var (h, _, service, audio) = Build(new RecordingAlignmentFixture());
        var notes = h.Project.Arrangements[0].Levels[0].Notes;
        foreach (var note in notes.Where(n => n.Position.BeatId is >= 32 and < 48).ToList()) notes.Remove(note);
        var before = h.Project.Beats.Select(b => b.Time).ToList();
        var steps = h.History.GetUndoSteps().Count;

        var proposal = (await service.AnalyzeAsync(h.Project, RecordingAlignmentRange.WholeSong, true, audio, Ct)).Proposal!;
        Assert.False(proposal.Sections[2].HasNotes);
        Assert.False(proposal.Sections[2].SuggestApply);
        Assert.NotEqual(before.Skip(32).Take(16), proposal.AlignedTimes.Skip(32).Take(16));

        // Asked for alone, nothing moves: no beat of the chart, no undo step.
        Assert.Equal(RecordingAlignmentApplyStatus.NothingSelected, service.Apply(h.Project, proposal, new HashSet<int> { 2 }));
        Assert.Equal(before, h.Project.Beats.Select(b => b.Time));
        Assert.Equal(steps, h.History.GetUndoSteps().Count);

        // Asked for beside the others, it is treated as left out: the grid is the one the other three give.
        Assert.Equal(RecordingAlignmentApplyStatus.Applied, service.Apply(h.Project, proposal, new HashSet<int> { 0, 1, 2, 3 }));
        var withIt = h.Project.Beats.Select(b => b.Time).ToList();
        h.History.Undo();
        Assert.Equal(RecordingAlignmentApplyStatus.Applied, service.Apply(h.Project, proposal, new HashSet<int> { 0, 1, 3 }));
        Assert.Equal(h.Project.Beats.Select(b => b.Time), withIt);
    }

    /// <summary>A file that exists and is not audio: reaching the decoder with it answers DecodeFailed.</summary>
    private string Undecodable()
    {
        var path = Path.Combine(_dir, $"not-audio-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private static HashSet<int> AllSuggested(RecordingAlignmentProposal proposal) =>
        proposal.Sections.Where(s => s.SuggestApply).Select(s => s.Index).ToHashSet();

    private static double MaxError(ChartEditHarness h, RecordingAlignmentFixture fx) =>
        RecordingAlignerTests.Errors(h.Project.Beats.Select(b => b.Time).ToList(), fx.TrueTimes).Max();

    private (ChartEditHarness Harness, RecordingAlignmentFixture Fixture, RecordingAlignmentService Service, string Audio) Build(
        RecordingAlignmentFixture fx)
    {
        var (h, service, audio) = RecordingAlignmentProject.Build(fx, _dir);
        return (h, fx, service, audio);
    }
}
