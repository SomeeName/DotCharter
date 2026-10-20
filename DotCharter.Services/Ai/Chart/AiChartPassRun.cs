using System.Globalization;
using DotCharter.Localization;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.State;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
///     Tools ▸ Fix &amp; clean up ▸ <b>Fix FHP &amp; fingering</b> (together, FHP only, fingering only) /
/// <b>Fix chord names</b>, as the
///     user sees them: on the selection, or the whole arrangement when nothing is selected;
///     a status-rail task with progress and a cancel; the proposals HELD for review
/// (<see cref="Review" />) until the charter applies the accepted sections as ONE
///     undo step (<see cref="ApplyReview" />) or discards them; the outcome on the rail's
///     main line.
///     <para>
///         <b>Threading:</b> call <see cref="RunAsync" /> on the UI thread. The active
///         arrangement is SNAPSHOTTED there (<see cref="ChartProjectCloner" />), the pass runs
///         on the snapshot inside <c>Task.Run</c>, and the await resumes on the caller's
///         context where the plans are re-checked against the live chart and written. A
///         chunk whose chords were edited meanwhile is dropped, never written over.
///     </para>
///     <para>
///         <b>The configured provider, or none.</b> The provider is the one Settings ▸ AI
///         names, resolved per run through <see cref="AiProviderResolution" />; with none
///         configured the verb says so and names that page. Web research is switched OFF
///         for this lane whatever the tone designer's setting says — a chart has nothing to
///         look up, and a research turn would only cost time.
///     </para>
/// </summary>
public sealed class AiChartPassRun
{
    /// <summary>The status-rail task id.</summary>
    public const string TaskId = "ai-chart-pass";

    private readonly ProjectState _state;
    private readonly IHistoryService _history;
    private readonly IStatusBarService? _statusBar;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly IFingeringGenerationService _fingering;
    private readonly IToneDesignModelFactory _models;
    private readonly Func<RigConfig> _config;
    private readonly Func<(FractionalPosition From, FractionalPosition To)?> _selection;
    private readonly Action? _redraw;

    /// <summary>0 = idle, 1 = running: a second run would race the first one's write.</summary>
    private int _inFlight;

    private AiChartReview? _review;
    private ChartQualityReporter? _quality;

    /// <summary>Builds the run.</summary>
    /// <param name="state">The open project.</param>
    /// <param name="history">Where the one undo step goes.</param>
    /// <param name="statusBar">The rail's task pills; null in a host without one.</param>
    /// <param name="localization">The text the user reads.</param>
    /// <param name="logger">The per-chunk trail.</param>
    /// <param name="fingering">Resolves identical templates.</param>
    /// <param name="models">Mints the configured provider.</param>
    /// <param name="config">Live settings, read per run.</param>
    /// <param name="selection">The selected sound range, or null for the whole arrangement.</param>
    /// <param name="redraw">Asks the chart surfaces to redraw after the write.</param>
    public AiChartPassRun(
        ProjectState state,
        IHistoryService history,
        IStatusBarService? statusBar,
        ILocalizationService localization,
        IAppLogger logger,
        IFingeringGenerationService fingering,
        IToneDesignModelFactory models,
        Func<RigConfig> config,
        Func<(FractionalPosition From, FractionalPosition To)?> selection,
        Action? redraw = null)
    {
        _state = state;
        _history = history;
        _statusBar = statusBar;
        _localization = localization;
        _logger = logger;
        _fingering = fingering;
        _models = models;
        _config = config;
        _selection = selection;
        _redraw = redraw;
    }

    /// <summary>Concurrent model calls per pass.</summary>
    public int Concurrency { get; init; } = 3;

    /// <summary>Chunk sizes; null for <see cref="AiChartChunkOptions.Default" />.</summary>
    public AiChartChunkOptions? ChunkOptions { get; init; }

    /// <summary>The chart quality report the structure job's checks compare; null builds one
    /// over the default arrangement rules.</summary>
    public ChartQualityReporter? Quality { get; init; }

    /// <summary>
    /// The AI chat's Tones job: mints the W6 generator over the mapper this run built from the
    ///     chat's model. The composition root hands it a CACHE-ONLY stem isolator, so a chat message
    ///     never starts a separation. Null: the Tones job says it is not available.
    /// </summary>
    public Func<IRsToneMapper, IRsToneGenerator?>? ToneGenerator { get; init; }

    /// <summary>The song's audio file for the Tones job; null resolves it from the open project
    /// (<see cref="ProjectAudioLocator" />). A test seam.</summary>
    public Func<string?>? SongAudio { get; init; }

    private ChartQualityReporter QualityReporter =>
        _quality ??= Quality ?? new ChartQualityReporter(new ArrangementValidationService(_logger));

    /// <summary>True while a run is in flight.</summary>
    public bool IsRunning => Volatile.Read(ref _inFlight) != 0;

    /// <summary>True when a provider is configured (the verbs' tooltip state).</summary>
    public bool IsConfigured => AiProviderResolution.IsConfigured(_config, _models);

    /// <summary>The proposals of the last run, held until <see cref="ApplyReview" /> or
    /// <see cref="DiscardReview" />; null when none is waiting.</summary>
    public AiChartReview? Review => _review;

    /// <summary>Raised on the UI thread when <see cref="Review" /> changes.</summary>
    public event EventHandler? ReviewChanged;

    /// <summary>Raised on the UI thread after <see cref="ApplyReview" /> wrote an FHP / fingering review,
    /// with the findings left in its range.</summary>
    public event EventHandler<AiChartApplied>? ReviewApplied;

    /// <summary>
    ///     Writes the review's ACCEPTED sections as ONE undo node and closes the review. A
    ///     section edited since the pass is stale and left out; a project closed or switched
    ///     meanwhile writes nothing. Call on the UI thread.
    /// </summary>
    /// <returns>True when something was written.</returns>
    public bool ApplyReview()
    {
        if (_review is not { } review) return false;
        if (review.Job == AiChartJob.SectionsAndPhrases) return ApplyStructure(review);
        if (review.Job == AiChartJob.Tones) return ApplyTones(review);
        SetReview(null);

        if (!ReferenceEquals(_state.ActiveProject, review.Project) ||
            !AiChartReview.IsLive(review.Project, review.Arrangement, review.Level))
        {
            Say("AiChart_ProjectChanged", StatusSeverity.Warning);
            return false;
        }

        var accepted = review.AcceptedPlans;
        if (accepted.Count == 0)
        {
            Say("AiChart_ReviewDiscarded", StatusSeverity.Info);
            return false;
        }

        var action = AiChartApplier.Build(review.Arrangement, review.Level, accepted, _fingering, out var summary);
        var written = action is not null && _history.Do(action);
        if (written) _redraw?.Invoke();

        var result = review.Result;
        _logger.Info(AppLogCategory.Editing, string.Create(CultureInfo.InvariantCulture,
            $"AI chart review applied: {accepted.Count} of {review.Chunks.Count} section(s) accepted; anchors " +
            $"{summary.AnchorsChanged}, re-fingered {summary.ChordsRefingered}, renamed {summary.NamesChanged}, " +
            $"minted {summary.TemplatesMinted}, hand shapes {summary.HandShapesRepointed}, stale {summary.StaleChunks}."));

        // Not written: the checks' rejections, the charter's, the stale ones.
        var rejected = result.Rejected.Count + review.RejectedCount + review.PendingCount + summary.StaleChunks;
        if (!written || summary.IsEmpty)
        {
            _state.SetMessage(_localization.Format("AiChart_NoChange", rejected, result.ChunkCount),
                StatusSeverity.Success);
            return false;
        }

        var done = review.Job == AiChartJob.ChordNames
            ? _localization.Format("AiChart_DoneNames", summary.NamesChanged, rejected, result.ChunkCount)
            : _localization.Format("AiChart_Done", summary.AnchorsChanged, summary.ChordsRefingered, rejected,
                result.ChunkCount);
        _state.SetMessage(done, rejected > 0 ? StatusSeverity.Warning : StatusSeverity.Success);
        if (review.Job != AiChartJob.ChordNames && ReviewApplied is { } applied)
            applied(this, new AiChartApplied(review, Leftovers(review)));
        return true;
    }

    /// <summary>The gate's findings for the review's job on the LIVE level, in the pass's range.</summary>
    private IReadOnlyList<ArrangementIssue> Leftovers(AiChartReview review)
    {
        var beats = review.Project.Beats;
        var gate = new AiChartRuleGate(review.Arrangement, review.Level, beats, _fingering, review.Job, null);
        return gate.FindingsIn(review.PassFrom?.GetPositionMs(beats), review.PassTo?.GetPositionMs(beats));
    }

    /// <summary>
    ///     The rule gate's findings for <paramref name="job" /> on the LIVE level an instructed run on
    /// <paramref name="arrangement" /> would read, timed in <c>[from, to]</c> (a typed FHP / fingering
    ///     message takes them as its targets). Empty with no project, no beat map or no level. Call on the UI thread.
    /// </summary>
    /// <param name="job">Whose rule list runs.</param>
    /// <param name="arrangement">The chat's arrangement, or null for the active one.</param>
    /// <param name="from">The scope's first position, or null for the start.</param>
    /// <param name="to">Its last position, or null for the end.</param>
    public IReadOnlyList<ArrangementIssue> ScopeFindings(AiChartJob job, Arrangement? arrangement,
        FractionalPosition? from, FractionalPosition? to)
    {
        if (!job.ProposesAnchors() && !job.ProposesFingers()) return [];
        var project = _state.ActiveProject;
        var chosen = arrangement ?? _state.ActiveArrangement;
        if (project is null || chosen is null || !project.Arrangements.Contains(chosen) || project.Beats.Count == 0 ||
            LevelFor(arrangement) is not { } level || !chosen.Levels.Contains(level))
        {
            return [];
        }

        var beats = project.Beats;
        var gate = new AiChartRuleGate(chosen, level, beats, _fingering, job, null);
        return gate.FindingsIn(from?.GetPositionMs(beats), to?.GetPositionMs(beats));
    }

    /// <summary>The level a run on <paramref name="chosen" /> reads: the active one for the active
    /// arrangement, else the hardest — the one <see cref="ProjectState" /> makes active on switching to it.</summary>
    private ChartLevel? LevelFor(Arrangement? chosen) =>
        chosen is null || ReferenceEquals(chosen, _state.ActiveArrangement)
            ? _state.ActiveLevel
            : chosen.Levels.MaxBy(l => l.DifficultyIndex);

    /// <summary>
    /// The structure job's write: the ACCEPTED section and phrase changes as ONE undo node.
    ///     The accepted subset is judged by the chart quality report first
    ///     (<see cref="AiStructureGate.Introduced" />): a subset that would add a finding writes
    ///     nothing and leaves the review open, so the charter can reject the change that causes it.
    /// </summary>
    private bool ApplyStructure(AiChartReview review)
    {
        if (!ReferenceEquals(_state.ActiveProject, review.Project) || !review.Project.Arrangements.Contains(review.Arrangement))
        {
            SetReview(null);
            Say("AiChart_ProjectChanged", StatusSeverity.Warning);
            return false;
        }

        var accepted = review.AcceptedChanges;
        if (accepted.Count == 0)
        {
            SetReview(null);
            Say("AiChart_ReviewDiscarded", StatusSeverity.Info);
            return false;
        }

        var added = AiStructureGate.Introduced(review.Project, review.Arrangement, accepted, QualityReporter);
        if (added.Count > 0)
        {
            var refused = _localization.Format("AiStructure_RulesRefused", string.Join(" ", added));
            _logger.Info(AppLogCategory.Editing, "AI structure review not applied: the accepted changes add " +
                string.Join(" ", added) + ".");
            _state.SetMessage(refused, StatusSeverity.Warning);
            return false;
        }

        SetReview(null);
        var action = AiStructureApplier.Build(review.Arrangement, accepted, out var applied, out var stale);
        var written = action is not null && _history.Do(action);
        if (written) _redraw?.Invoke();
        _logger.Info(AppLogCategory.Editing, string.Create(CultureInfo.InvariantCulture,
            $"AI structure review applied: {accepted.Count} of {review.Items.Count} change(s) accepted; written {applied}, stale {stale}."));

        var rejected = review.Result.Rejected.Count + review.RejectedCount + review.PendingCount + stale;
        if (!written)
        {
            _state.SetMessage(_localization.Format("AiStructure_NoChange", rejected), StatusSeverity.Success);
            return false;
        }

        _state.SetMessage(_localization.Format("AiStructure_Done", applied, rejected),
            rejected > 0 ? StatusSeverity.Warning : StatusSeverity.Success);
        return true;
    }

    /// <summary>
    /// The Tones job's write: the ACCEPTED tones' definitions as ONE undo node
    ///     (<see cref="AiToneApplier" />). A definition edited since the proposal is stale and left out;
    ///     nothing but tone definitions is reachable.
    /// </summary>
    private bool ApplyTones(AiChartReview review)
    {
        SetReview(null);
        if (!ReferenceEquals(_state.ActiveProject, review.Project))
        {
            Say("AiChart_ProjectChanged", StatusSeverity.Warning);
            return false;
        }

        var accepted = review.AcceptedTones;
        if (accepted.Count == 0)
        {
            Say("AiChart_ReviewDiscarded", StatusSeverity.Info);
            return false;
        }

        IChartAction? action;
        int applied, stale;
        try
        {
            action = AiToneApplier.Build(review.Project, accepted, out applied, out stale);
        }
        catch (RsToneCatalogException ex)
        {
            var text = _localization.Format("AiChart_Failed", ex.Message);
            _logger.Warning(AppLogCategory.Editing, text);
            _state.SetMessage(text, StatusSeverity.Error);
            return false;
        }

        var written = action is not null && _history.Do(action);
        _logger.Info(AppLogCategory.Editing, string.Create(CultureInfo.InvariantCulture,
            $"AI tone review applied: {accepted.Count} of {review.Tones.Count} tone(s) accepted; written {applied}, stale {stale}."));

        var rejected = review.Result.Rejected.Count + review.RejectedCount + review.PendingCount + stale;
        if (!written)
        {
            _state.SetMessage(_localization.Format("AiTones_NoChange", rejected), StatusSeverity.Success);
            return false;
        }

        _state.SetMessage(_localization.Format("AiTones_Done", applied, rejected),
            rejected > 0 ? StatusSeverity.Warning : StatusSeverity.Success);
        return true;
    }

    /// <summary>Drops the held proposals; the chart is not touched.</summary>
    public void DiscardReview()
    {
        if (_review is null) return;
        SetReview(null);
        Say("AiChart_ReviewDiscarded", StatusSeverity.Info);
    }

    private void SetReview(AiChartReview? review)
    {
        if (ReferenceEquals(_review, review)) return;
        _review = review;
        ReviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs the pass and HOLDS its proposals in <see cref="Review" />; nothing is
    /// written. Never throws: every outcome is a line on the rail.</summary>
    /// <param name="job">What to fix.</param>
    /// <param name="style">The naming style, for <see cref="AiChartJob.ChordNames" />.</param>
    public async Task RunAsync(AiChartJob job, ChordNamingStyle style = ChordNamingStyle.Eof)
        => await RunGuardedAsync(new RunTarget(job, style, null, SelectedRange, null, null), CancellationToken.None);

    /// <summary>
    /// The AI chat's run: the same pass, checks, retry, stale-chart checks and
    ///     held review as <see cref="RunAsync" />, on the arrangement and range the chat
    ///     chose instead of the editor's selection, with the instruction in every chunk's
    ///     prompt. Proposals are HELD in <see cref="Review" /> (the review card opens on
    ///     <see cref="ReviewChanged" />); nothing is written. A run that fails does not
    ///     throw: the outcome says how it ended, in the words the rail shows.
    /// </summary>
    /// <param name="request">The job, the instruction and the scope.</param>
    /// <param name="ct">Cancels the run, like the rail pill's cancel.</param>
    /// <exception cref="ArgumentOutOfRangeException">The name job: the chat may change hand
    /// positions, chord fingering, sections and phrases only, or ask (<see cref="AiChartJob.Explain" />,
    /// a text answer, nothing held, the review still waiting left as it is).</exception>
    public Task<AiChartRunOutcome> RunInstructionAsync(AiChartInstructedRun request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Instruction);
        if (!request.Job.ProposesAnchors() && !request.Job.ProposesFingers() &&
            request.Job is not (AiChartJob.SectionsAndPhrases or AiChartJob.Explain or AiChartJob.Tones))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Job,
                "The AI chat changes hand positions, chord fingering, sections and phrases or tone definitions only, " +
                "or answers a question.");
        }

        if (string.IsNullOrWhiteSpace(request.Instruction.Text))
            throw new ArgumentException("An instructed run needs an instruction.", nameof(request));

        var target = new RunTarget(request.Job, ChordNamingStyle.Eof, request.Arrangement, () => (request.From, request.To),
            request.Instruction, request.ModelId, request.ToneChanges);
        return RunGuardedAsync(target with { Targets = request.Targets }, ct);
    }

    private (FractionalPosition? From, FractionalPosition? To) SelectedRange()
        => _selection() is { } range ? (range.From, range.To) : (null, null);

    private async Task<AiChartRunOutcome> RunGuardedAsync(RunTarget target, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            _logger.Info(AppLogCategory.Editing, "AI chart pass is already running; ignoring the request.");
            return new AiChartRunOutcome(AiChartRunStatus.Busy, _localization["AiChart_Busy"]);
        }

        try
        {
            return await RunCoreAsync(target, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.Info(AppLogCategory.Editing, "AI chart pass cancelled.");
            return Tell(AiChartRunStatus.Cancelled, _localization["AiChart_Cancelled"], StatusSeverity.Warning);
        }
        catch (Exception ex)
        {
            var text = _localization.Format("AiChart_Failed", ex.Message);
            _logger.Error(AppLogCategory.Errors, text, ex);
            return Tell(AiChartRunStatus.Failed, text, StatusSeverity.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private async Task<AiChartRunOutcome> RunCoreAsync(RunTarget target, CancellationToken ct)
    {
        var job = target.Job;

        // A new run replaces proposals still waiting: they were made on the chart the new
        // run is about to read again. A question is not a new run of proposals: the
        // charter may be asking about the very card that is waiting.
        if (job != AiChartJob.Explain) SetReview(null);
        var project = _state.ActiveProject;
        var arrangement = target.Arrangement ?? _state.ActiveArrangement;

        // The chat may name another arrangement than the active one: its level is the one
        // ProjectState makes active on switching to it (the hardest).
        var level = LevelFor(target.Arrangement);
        if (project is null || arrangement is null || level is null || !arrangement.Levels.Contains(level) ||
            target.Arrangement is { } chosen && !project.Arrangements.Contains(chosen))
        {
            return Say(AiChartRunStatus.NoProject, "AiChart_NoProject", StatusSeverity.Warning);
        }

        if (project.Beats.Count == 0) return Say(AiChartRunStatus.NoBeatMap, "AiChart_NoBeatMap", StatusSeverity.Warning);

        if (!AiProviderResolution.IsConfigured(_config, _models))
            return Say(AiChartRunStatus.NotConfigured, "AiChart_NotConfigured", StatusSeverity.Warning);

        if (job == AiChartJob.SectionsAndPhrases) return await RunStructureAsync(project, arrangement, level, target, ct);
        if (job == AiChartJob.Explain) return await RunExplainAsync(project, arrangement, level, target, ct);
        if (job == AiChartJob.Tones) return await RunTonesAsync(project, arrangement, level, target, ct);

        // The snapshot the pass reads for minutes, off this thread.
        var levelIndex = arrangement.Levels.IndexOf(level);
        var snapshot = ChartProjectCloner.CloneArrangement(arrangement);
        var facts = new AiChartSongFacts(snapshot, ArrangementInstrument.IsBassPart(arrangement), KeyOf(project.Chart));
        var range = target.Range();
        var request = new AiChartPassRequest(job, target.Style, facts, snapshot.Levels[levelIndex],
            project.Beats.ToList(), range.From, range.To, ChunkOptions, target.Instruction);
        request = request with { Targets = target.Targets };

        var model = CreateModel(target);
        AiChartPassResult result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _statusBar?.BeginTask(TaskId, _localization.Format("AiChart_Running", arrangement.Name),
                indeterminate: false, cancel: () => cts.Cancel());
            var rail = new RailProgress(this);
            try
            {
                var pass = new AiChartPass(_fingering, _logger, Concurrency);
                result = await Task.Run(() => pass.RunAsync(request, model, rail, cts.Token), cts.Token);
            }
            finally
            {
                _statusBar?.CompleteTask(TaskId);
            }
        }
        finally
        {
            (model as IDisposable)?.Dispose();
        }

        if (result.ChunkCount == 0)
        {
            return Say(AiChartRunStatus.NothingToDo, job switch
            {
                AiChartJob.ChordNames => "AiChart_NoChords",
                AiChartJob.FingeringOnly => "AiChart_NoChordsToFinger",
                _ => "AiChart_NothingToDo"
            }, StatusSeverity.Warning);
        }

        // Minutes can pass. A project closed or switched meanwhile is not the one the
        // plans were made for.
        if (!ReferenceEquals(_state.ActiveProject, project) || !project.Arrangements.Contains(arrangement) ||
            !arrangement.Levels.Contains(level))
        {
            return Say(AiChartRunStatus.ProjectChanged, "AiChart_ProjectChanged", StatusSeverity.Warning);
        }

        foreach (var group in result.Rejected.GroupBy(r => r.Code))
            _logger.Info(AppLogCategory.Editing, $"AI chart pass: {group.Count()} chunk(s) rejected for {group.Key}.");
        _logger.Info(AppLogCategory.Editing, string.Create(CultureInfo.InvariantCulture,
            $"AI chart pass held for review: {result.Accepted.Count} of {result.ChunkCount} chunk(s) accepted by the " +
            $"checks ({result.Retried} retried); {result.InputTokens} in / {result.OutputTokens} out tokens " +
            $"({result.ModelId}), {result.Elapsed.TotalSeconds:F1} s."));

        if (result.Accepted.Count == 0)
        {
            var why = result.Rejected.FirstOrDefault()?.Reason ?? "";
            return Tell(AiChartRunStatus.AllRejected, _localization.Format("AiChart_AllRejected", result.ChunkCount, why),
                StatusSeverity.Error, result);
        }

        // HELD, not written: the charter sees every proposal marked and accepts or
        // rejects it per section. The chunks are cut again only for their phrase names.
        var chunks = AiChartChunker.Plan(snapshot, request.Level, request.Beats, request.From, request.To,
            request.Options);
        var review = AiChartReview.Hold(project, arrangement, level, project.Beats, result, chunks, _fingering);
        if (review is null)
        {
            return Tell(AiChartRunStatus.NoChange,
                _localization.Format("AiChart_NoChange", result.Rejected.Count, result.ChunkCount),
                StatusSeverity.Success, result);
        }

        review.PassFrom = request.From;
        review.PassTo = request.To;
        SetReview(review);
        return Tell(AiChartRunStatus.Held,
            _localization.Format("AiChart_ReviewReady", review.Chunks.Count, result.Rejected.Count),
            result.Rejected.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success, result, review);
    }

    /// <summary>
    /// The structure job: ONE request over the scope on a snapshot of the whole project
    ///     (the gate runs the chart quality report on it), the changes HELD for the review card.
    /// </summary>
    private async Task<AiChartRunOutcome> RunStructureAsync(ChartProject project, Arrangement arrangement,
        ChartLevel level, RunTarget target, CancellationToken ct)
    {
        var snapshot = ChartProjectCloner.Clone(project);
        var snapArrangement = snapshot.Arrangements[project.Arrangements.IndexOf(arrangement)];
        var facts = new AiChartSongFacts(snapArrangement, ArrangementInstrument.IsBassPart(arrangement), KeyOf(project.Chart));
        var range = target.Range();
        var request = new AiStructureRequest(snapshot, facts, snapArrangement.Levels[arrangement.Levels.IndexOf(level)],
            range.From, range.To, target.Instruction)
        {
            Targets = target.Targets
        };

        var model = CreateModel(target);
        AiChartPassResult result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _statusBar?.BeginTask(TaskId, _localization.Format("AiChart_Running", arrangement.Name),
                indeterminate: true, cancel: () => cts.Cancel());
            try
            {
                var pass = new AiStructurePass(QualityReporter, _logger);
                result = await Task.Run(() => pass.RunAsync(request, model, cts.Token), cts.Token);
            }
            finally
            {
                _statusBar?.CompleteTask(TaskId);
            }
        }
        finally
        {
            (model as IDisposable)?.Dispose();
        }

        if (!ReferenceEquals(_state.ActiveProject, project) || !project.Arrangements.Contains(arrangement) ||
            !arrangement.Levels.Contains(level))
        {
            return Say(AiChartRunStatus.ProjectChanged, "AiChart_ProjectChanged", StatusSeverity.Warning);
        }

        // ChunkCount is the number of changes the reply proposed: 0 with a rejection = refused whole.
        if (result.ChunkCount == 0 && result.Rejected.Count > 0)
        {
            return Tell(AiChartRunStatus.AllRejected, _localization.Format("AiStructure_Refused", result.Rejected[0].Reason),
                StatusSeverity.Error, result);
        }

        var review = AiChartReview.HoldStructure(project, arrangement, level, project.Beats, result);
        if (review is null)
        {
            return Tell(AiChartRunStatus.NoChange, _localization.Format("AiStructure_NoChange", result.Rejected.Count),
                StatusSeverity.Success, result);
        }

        SetReview(review);
        return Tell(AiChartRunStatus.Held,
            _localization.Format("AiStructure_ReviewReady", review.Items.Count, result.Rejected.Count),
            result.Rejected.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success, result, review);
    }

    /// <summary>
    /// Ask / explain: ONE request over the scope on a snapshot of the whole project (the
    ///     chart quality report runs on it), answered in TEXT. Nothing is proposed, held or written,
    ///     and a review still waiting is left as it is; the answer goes to the chat, not the rail.
    /// </summary>
    private async Task<AiChartRunOutcome> RunExplainAsync(ChartProject project, Arrangement arrangement,
        ChartLevel level, RunTarget target, CancellationToken ct)
    {
        var snapshot = ChartProjectCloner.Clone(project);
        var snapArrangement = snapshot.Arrangements[project.Arrangements.IndexOf(arrangement)];
        var facts = new AiChartSongFacts(snapArrangement, ArrangementInstrument.IsBassPart(arrangement), KeyOf(project.Chart));
        var range = target.Range();
        var request = new AiExplainRequest(snapshot, facts, snapArrangement.Levels[arrangement.Levels.IndexOf(level)],
            range.From, range.To, target.Instruction!);

        var model = CreateModel(target);
        AiExplainResult result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _statusBar?.BeginTask(TaskId, _localization.Format("AiChart_Running", arrangement.Name),
                indeterminate: true, cancel: () => cts.Cancel());
            try
            {
                var pass = new AiExplainPass(QualityReporter, _localization, _logger);
                result = await Task.Run(() => pass.RunAsync(request, model, cts.Token), cts.Token);
            }
            finally
            {
                _statusBar?.CompleteTask(TaskId);
            }
        }
        finally
        {
            (model as IDisposable)?.Dispose();
        }

        return result.Answer is { } answer
            ? new AiChartRunOutcome(AiChartRunStatus.Answered, answer, result.Pass)
            : new AiChartRunOutcome(AiChartRunStatus.Failed, _localization["AiChat_ExplainNoAnswer"], result.Pass);
    }

    /// <summary>
    /// The Tones job: the W6 generator on the scope's tone definitions — their song windows
    ///     (cached stems only) and the chat's instruction in every window's prompt — the tones HELD for the
    ///     review card. Nothing is written; the project's tone definitions stay as they are until Apply.
    /// </summary>
    private async Task<AiChartRunOutcome> RunTonesAsync(ChartProject project, Arrangement arrangement,
        ChartLevel level, RunTarget target, CancellationToken ct)
    {
        if (target.ToneChanges is not { Count: > 0 } changes)
            return Say(AiChartRunStatus.NothingToDo, "AiTones_NoToneChanges", StatusSeverity.Warning);
        var audio = SongAudio is { } song
            ? song()
            : ProjectAudioLocator.TryResolve(_state.CurrentProjectPath, _state.CurrentAudioFileName);
        if (audio is null) return Say(AiChartRunStatus.NothingToDo, "ToneGenerate_NoAudio", StatusSeverity.Warning);

        var plan = AiTonePass.Plan(project, arrangement, changes, _state.TotalAudioLengthMs, _localization);
        if (plan.Targets.Count == 0)
        {
            var empty = new AiChartPassResult(AiChartJob.Tones, 0, [], plan.Refused, 0, 0, "", TimeSpan.Zero);
            return Tell(AiChartRunStatus.AllRejected,
                _localization.Format("AiTones_NoneHeld", string.Join(" · ", plan.Refused.Select(r => r.Reason))),
                StatusSeverity.Warning, empty);
        }

        var model = CreateModel(target);
        try
        {
            var mapper = new MeteredToneMapper(new LanguageModelRsToneMapper(() => model,
                string.IsNullOrWhiteSpace(target.ModelId) ? $"{AiProviderResolution.SettingsOf(_config).ModelId}" : target.ModelId.Trim()));
            if (ToneGenerator?.Invoke(mapper) is not { } generator)
                return Say(AiChartRunStatus.Failed, "ToneGenerate_NotAvailable", StatusSeverity.Warning);

            var request = AiTonePass.Request(project, arrangement, plan, audio, target.Instruction);
            ToneSetGenerationResult generated;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                _statusBar?.BeginTask(TaskId, _localization.Format("ToneGenerate_Running", arrangement.Name),
                    indeterminate: false, cancel: () => cts.Cancel());
                var rail = new ToneRail(this);
                try
                {
                    generated = await Task.Run(() => generator.GenerateAsync(request, rail, cts.Token, rail), cts.Token);
                }
                finally
                {
                    _statusBar?.CompleteTask(TaskId);
                }
            }

            foreach (var note in generated.Notes) _logger.Info(AppLogCategory.Audio, note);
            if (!ReferenceEquals(_state.ActiveProject, project) || !project.Arrangements.Contains(arrangement))
                return Say(AiChartRunStatus.ProjectChanged, "AiChart_ProjectChanged", StatusSeverity.Warning);

            var (held, refused) = AiTonePass.Hold(plan, generated, _localization);
            var result = new AiChartPassResult(AiChartJob.Tones, plan.Targets.Count, [], refused, mapper.InputTokens,
                mapper.OutputTokens, mapper.ModelId, generated.Elapsed);
            var review = AiChartReview.HoldTones(project, arrangement, level, result, held);
            if (review is null)
            {
                return Tell(AiChartRunStatus.AllRejected,
                    _localization.Format("AiTones_NoneHeld", string.Join(" · ", refused.Select(r => r.Reason))),
                    StatusSeverity.Error, result);
            }

            SetReview(review);
            return Tell(AiChartRunStatus.Held, _localization.Format("AiTones_ReviewReady", review.Tones.Count, refused.Count),
                refused.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success, result, review);
        }
        finally
        {
            (model as IDisposable)?.Dispose();
        }
    }

    /// <summary>The Tones job's rail pill: the generator's latest localized line, and the share of tones started.</summary>
    private sealed class ToneRail(AiChartPassRun owner) : IProgress<string>, IProgress<double>
    {
        private readonly Lock _gate = new();
        private double _fraction;

        public void Report(string value)
        {
            lock (_gate) owner._statusBar?.ReportProgress(TaskId, _fraction, value);
        }

        public void Report(double value)
        {
            lock (_gate)
            {
                _fraction = value;
                owner._statusBar?.ReportProgress(TaskId, value);
            }
        }
    }

    /// <summary>Counts the tokens of every mapping (the generator keeps only the tones), for the chat's cost line.</summary>
    private sealed class MeteredToneMapper(IRsToneMapper inner) : IRsToneMapper
    {
        public int InputTokens { get; private set; }

        public int OutputTokens { get; private set; }

        public string ModelId { get; private set; } = "";

        public string Describe() => inner.Describe();

        public async Task<RsToneMapping> MapAsync(RsToneMapRequest request, CancellationToken ct)
        {
            var mapping = await inner.MapAsync(request, ct).ConfigureAwait(false);
            InputTokens += mapping.Usage.InputTokens;
            OutputTokens += mapping.Usage.OutputTokens;
            ModelId = mapping.ModelId;
            return mapping;
        }
    }

    /// <summary>The configured provider, web research off, with the chat's model when it picked one.</summary>
    private IToneDesignModel CreateModel(RunTarget target)
    {
        var settings = AiProviderResolution.SettingsOf(_config) with { WebResearch = false };
        if (!string.IsNullOrWhiteSpace(target.ModelId)) settings = settings with { ModelId = target.ModelId.Trim() };
        return _models.Create(settings);
    }

    /// <summary>
    ///     The range a selection asks the pass to cover: from the earliest selected sound's
    ///     START to the latest one's START (both inclusive), or null when nothing is selected
    ///     — which means the whole arrangement.
    /// </summary>
    /// <param name="notes">Selected notes.</param>
    /// <param name="chords">Selected chords.</param>
    public static (FractionalPosition From, FractionalPosition To)? RangeOf(
        IEnumerable<GuitarNote> notes, IEnumerable<Chord> chords)
    {
        var positions = notes.Select(n => n.Position).Concat(chords.Select(c => c.Position)).ToList();
        return positions.Count == 0 ? null : (positions.Min(), positions.Max());
    }

    /// <summary>The song's key signature in words, or null when it was never set.</summary>
    internal static string? KeyOf(ChartData chart)
    {
        if (chart.KeyAccidentals == 0 && !chart.KeyIsMinor) return null;
        var count = Math.Abs(chart.KeyAccidentals);
        var accidentals = chart.KeyAccidentals switch
        {
            0 => "no sharps or flats",
            > 0 => string.Create(CultureInfo.InvariantCulture, $"{count} sharp{(count == 1 ? "" : "s")}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{count} flat{(count == 1 ? "" : "s")}")
        };
        return accidentals + (chart.KeyIsMinor ? ", minor" : ", major");
    }

    private void Say(string key, StatusSeverity severity)
    {
        var text = _localization[key];
        _logger.Info(AppLogCategory.Editing, text);
        _state.SetMessage(text, severity);
    }

    private AiChartRunOutcome Say(AiChartRunStatus status, string key, StatusSeverity severity)
    {
        Say(key, severity);
        return new AiChartRunOutcome(status, _localization[key]);
    }

    /// <summary>Puts <paramref name="text" /> on the rail's main line and returns it as the outcome.</summary>
    private AiChartRunOutcome Tell(AiChartRunStatus status, string text, StatusSeverity severity,
        AiChartPassResult? result = null, AiChartReview? review = null)
    {
        _state.SetMessage(text, severity);
        return new AiChartRunOutcome(status, text, result, review) { Beats = result is null ? null : _state.ActiveProject?.Beats };
    }

    /// <summary>What one run reads: the job, where, and the chat's instruction and model.</summary>
    private sealed record RunTarget(
        AiChartJob Job,
        ChordNamingStyle Style,
        Arrangement? Arrangement,
        Func<(FractionalPosition? From, FractionalPosition? To)> Range,
        AiChartInstruction? Instruction,
        string? ModelId,
        IReadOnlyList<ToneChange>? ToneChanges = null)
    {
        public IReadOnlyList<AiChartFindingTarget>? Targets { get; init; }
    }

    /// <summary>The pill's progress: the share of chunks answered, and a localized line.</summary>
    private sealed class RailProgress(AiChartPassRun owner) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value)
        {
            if (value.Total <= 0) return;
            owner._statusBar?.ReportProgress(TaskId, (double)value.Done / value.Total,
                owner._localization.Format("AiChart_Progress", value.Done, value.Total));
        }
    }
}
