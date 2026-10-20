using System.Diagnostics;
using DotCharter.Analysis.BeatMap;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Tempo.RecordingAlignment;

/// <summary>One reported section after analysis. <paramref name="Confidence" /> is 0..1, or NaN when it has no notes.</summary>
public sealed record RecordingAlignmentSectionResult(
    int Index,
    RecordingAlignmentSectionSpan Span,
    double Confidence,
    int NoteCount,
    double MeanShiftMs,
    int FreeBeats)
{
    /// <summary>Applied by default: it has notes, scored at or above <see cref="RecordingAligner.LowConfidence" />, and has beats to move.</summary>
    public bool SuggestApply => FreeBeats > 0 && Confidence >= RecordingAligner.LowConfidence;
}

/// <summary>An analysis held for the charter's decision: nothing in the project has changed yet.</summary>
public sealed record RecordingAlignmentProposal(
    RecordingAlignmentPlan Plan,
    IReadOnlyList<double> AlignedTimes,
    IReadOnlyList<RecordingAlignmentSectionResult> Sections,
    RecordingAlignmentSource Source);

public enum RecordingAlignmentFailure
{
    None,
    NoAudio,
    DecodeFailed,
    TooFewBeats,
    Locked,
    NothingToAlign
}

public sealed record RecordingAlignmentAnalysis(RecordingAlignmentProposal? Proposal, RecordingAlignmentFailure Failure)
{
    public static RecordingAlignmentAnalysis Failed(RecordingAlignmentFailure failure) => new(null, failure);
}

public enum RecordingAlignmentApplyStatus
{
    Applied,
    NothingSelected,
    NoChange,
    Locked,
    Stale,
    Invalid
}

/// <summary>
/// Tempo map ▸ Align to recording: warps the open chart's beat grid onto its recording.
///     <see cref="AnalyzeAsync" /> reads the chart on the calling thread, then decodes and aligns
///     on a worker — cancellable, and it never writes to the project. <see cref="Apply" /> writes
///     the sections the charter accepted as ONE undo node.
/// </summary>
public interface IRecordingAlignmentService
{
    Task<RecordingAlignmentAnalysis> AnalyzeAsync(ChartProject project, RecordingAlignmentRange range, bool keepAnchors,
        string? audioPath, CancellationToken cancellationToken);

    RecordingAlignmentApplyStatus Apply(ChartProject project, RecordingAlignmentProposal proposal, IReadOnlySet<int> acceptedSections);
}

/// <inheritdoc cref="IRecordingAlignmentService" />
public sealed class RecordingAlignmentService(
    IBeatResyncService beatResync,
    IHistoryService history,
    IConfigurationService config,
    IEditorTransport transport,
    IChartEditorSurface editor,
    IAppLogger logger) : IRecordingAlignmentService
{
    public async Task<RecordingAlignmentAnalysis> AnalyzeAsync(ChartProject project, RecordingAlignmentRange range, bool keepAnchors,
        string? audioPath, CancellationToken cancellationToken)
    {
        if (project.IsBeatMapLocked) return RecordingAlignmentAnalysis.Failed(RecordingAlignmentFailure.Locked);
        if (project.Beats.Count < 2) return RecordingAlignmentAnalysis.Failed(RecordingAlignmentFailure.TooFewBeats);
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return RecordingAlignmentAnalysis.Failed(RecordingAlignmentFailure.NoAudio);

        var plan = RecordingAlignmentPlan.Build(project, range, keepAnchors);
        if (plan.FreeBeats == 0) return RecordingAlignmentAnalysis.Failed(RecordingAlignmentFailure.NothingToAlign);

        return await Task.Run(() => AnalyzeCore(plan, audioPath, cancellationToken), cancellationToken);
    }

    private RecordingAlignmentAnalysis AnalyzeCore(RecordingAlignmentPlan plan, string audioPath, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        float[] mono;
        int rate;
        RecordingAlignmentSource source;
        try
        {
            (mono, rate, source) = RecordingAlignmentAudio.Load(audioPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(AppLogCategory.Audio, $"Align to recording: decode failed: {ex.Message}");
            return RecordingAlignmentAnalysis.Failed(RecordingAlignmentFailure.DecodeFailed);
        }

        var starts = plan.Sections.Select(s => s.StartBeat).ToArray();
        var result = new RecordingAligner().Align(mono, rate, plan.Beats, plan.Events, starts, cancellationToken: ct);
        var sections = new List<RecordingAlignmentSectionResult>(plan.Sections.Count);
        for (var s = 0; s < plan.Sections.Count; s++)
        {
            var span = plan.Sections[s];
            var score = result.Sections[s];
            var free = 0;
            for (var i = span.StartBeat; i < span.EndBeat; i++) free += plan.Beats[i].IsFixed ? 0 : 1;
            sections.Add(new RecordingAlignmentSectionResult(s, span, score.Confidence, score.EventCount, score.MeanShiftMs, free));
        }

        logger.Info(AppLogCategory.Audio,
            $"Align to recording analysed. source={source}, beats={plan.Beats.Length}, free={plan.FreeBeats}, notes={plan.Events.Count}, " +
            $"sections={sections.Count}, low={sections.Count(s => s.FreeBeats > 0 && !s.SuggestApply)}, " +
            $"took={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms.");
        return new RecordingAlignmentAnalysis(new RecordingAlignmentProposal(plan, result.BeatTimesMs, sections, source),
            RecordingAlignmentFailure.None);
    }

    public RecordingAlignmentApplyStatus Apply(ChartProject project, RecordingAlignmentProposal proposal, IReadOnlySet<int> acceptedSections)
    {
        if (project.IsBeatMapLocked) return RecordingAlignmentApplyStatus.Locked;
        if (acceptedSections.Count == 0) return RecordingAlignmentApplyStatus.NothingSelected;

        // The grid the analysis read must still be the grid: an edit since then makes the proposal stale.
        var old = proposal.Plan.OldTimes;
        if (project.Beats.Count != old.Length || project.Beats.Where((b, i) => b.Time != old[i]).Any())
            return RecordingAlignmentApplyStatus.Stale;

        var composed = RecordingAlignmentComposer.Compose(project.Beats, proposal.Plan, proposal.AlignedTimes, acceptedSections);
        if (composed == null) return RecordingAlignmentApplyStatus.Invalid;

        var result = beatResync.BuildResyncAction(project, composed, () =>
        {
            ChartSpacingEnforcer.EnforceProject(project, config.LoadAppConfig());
            project.Bpm = project.Beats[0].Bpm;
            transport.Bpm = project.Beats[0].Bpm;
            editor.RequestRedraw();
        });
        if (result.Status != BeatResyncStatus.Ok || result.Action == null)
            return result.Status == BeatResyncStatus.Locked ? RecordingAlignmentApplyStatus.Locked : RecordingAlignmentApplyStatus.Invalid;
        if (!result.Action.HasEffect) return RecordingAlignmentApplyStatus.NoChange;

        history.Do(result.Action);
        logger.Info(AppLogCategory.Events,
            $"Align to recording applied. sections={acceptedSections.Count}/{proposal.Sections.Count}, beats={composed.Count}, source={proposal.Source}.");
        return RecordingAlignmentApplyStatus.Applied;
    }
}
