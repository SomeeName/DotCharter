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
    /// <summary>False for a section the tab is silent in: there is nothing to match, so it has no confidence
    ///     and <see cref="IRecordingAlignmentService.Apply" /> never takes its alignment.</summary>
    public bool HasNotes => NoteCount > 0;

    /// <summary>Applied by default: it has notes, scored at or above <see cref="RecordingAligner.LowConfidence" />, and has beats to move.</summary>
    public bool SuggestApply => HasNotes && FreeBeats > 0 && Confidence >= RecordingAligner.LowConfidence;
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
    NothingToAlign,

    /// <summary>The chart, or the chosen range, has no notes: the tool matches a tab's notes against the
    ///     recording, so there is nothing to listen for.</summary>
    NoNotes
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
/// Tempo map ▸ Fit the tab's tempo to the recording: warps the open chart's beat grid onto its
///     recording by matching the chart's notes against the audio. <see cref="AnalyzeAsync" /> reads
///     the chart on the calling thread and refuses a chart, or a range, without notes there
///     (<see cref="RecordingAlignmentFailure.NoNotes" />), before any audio is opened; then it decodes
///     and aligns on a worker — cancellable, and it never writes to the project. <see cref="Apply" />
///     writes the sections the charter accepted as ONE undo node; a section without notes is never
///     one of them, whatever was passed in: it follows its neighbours like any section left out.
/// </summary>
public interface IRecordingAlignmentService
{
    Task<RecordingAlignmentAnalysis> AnalyzeAsync(ChartProject project, RecordingAlignmentRange range, bool keepAnchors,
        string? audioPath, CancellationToken cancellationToken, string? projectPath = null);

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
        string? audioPath, CancellationToken cancellationToken, string? projectPath = null)
    {
        if (project.IsBeatMapLocked) return RecordingAlignmentAnalysis.Failed(RecordingAlignmentFailure.Locked);
        if (project.Beats.Count < 2) return RecordingAlignmentAnalysis.Failed(RecordingAlignmentFailure.TooFewBeats);
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return RecordingAlignmentAnalysis.Failed(RecordingAlignmentFailure.NoAudio);

        var plan = RecordingAlignmentPlan.Build(project, range, keepAnchors);
        if (plan.FreeBeats == 0) return RecordingAlignmentAnalysis.Failed(RecordingAlignmentFailure.NothingToAlign);
        if (plan.NotesInRange == 0)
        {
            logger.Info(AppLogCategory.Audio,
                $"Align to recording not run: no notes to match. range={range.Kind}, beats={plan.Beats.Length}, free={plan.FreeBeats}, notes={plan.Events.Count}.");
            return RecordingAlignmentAnalysis.Failed(RecordingAlignmentFailure.NoNotes);
        }

        var stems = project.Stems.Select(s => new Stem { Name = s.Name, Path = s.Path, Offset = s.Offset }).ToArray();
        var audioFileName = project.AudioFileName;
        return await Task.Run(() =>
        {
            var paths = RecordingAlignmentAudio.LocateInstrumentStems(audioPath, stems, projectPath, audioFileName, logger);
            return AnalyzeCore(plan, audioPath, paths, cancellationToken);
        }, cancellationToken);
    }

    private RecordingAlignmentAnalysis AnalyzeCore(RecordingAlignmentPlan plan, string audioPath,
        IReadOnlyList<string> stems, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        float[] mono;
        int rate;
        RecordingAlignmentSource source;
        try
        {
            (mono, rate, source) = RecordingAlignmentAudio.Load(audioPath, ct, stems, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(AppLogCategory.Audio, "Align to recording: decode failed", ex);
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
        var accepted = proposal.Sections.Where(s => s.HasNotes && acceptedSections.Contains(s.Index)).Select(s => s.Index).ToHashSet();
        if (accepted.Count == 0) return RecordingAlignmentApplyStatus.NothingSelected;

        // The grid the analysis read must still be the grid: an edit since then makes the proposal stale.
        var old = proposal.Plan.OldTimes;
        if (project.Beats.Count != old.Length || project.Beats.Where((b, i) => b.Time != old[i]).Any())
            return RecordingAlignmentApplyStatus.Stale;

        var composed = RecordingAlignmentComposer.Compose(project.Beats, proposal.Plan, proposal.AlignedTimes, accepted);
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
            $"Align to recording applied. sections={accepted.Count}/{proposal.Sections.Count}, beats={composed.Count}, source={proposal.Source}.");
        return RecordingAlignmentApplyStatus.Applied;
    }
}
