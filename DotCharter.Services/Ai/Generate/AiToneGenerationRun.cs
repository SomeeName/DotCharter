using System.Globalization;
using DotCharter.Localization;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.State;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Services.Ai.Generate;

/// <summary>
///     Tools ▸ Tone &amp; sound ▸ <b>Generate tones with AI…</b>: one Rocksmith tone per
///     placed tone change, from <see cref="IRsToneGenerator" />, written into the
///     project as ONE undo step.
///     <para>
///         <b>Not the Tone designer's "Estimate tones from audio…"</b>
///         (<c>Editing/Tones/ToneGenerationRun</c>). That one measures the audio on this
///         computer, picks gear by rule and never overwrites a tone. This one asks the
///         AI provider from Settings ▸ AI, isolates the arrangement's own stem, validates
///         against the game's gear catalogue, and REPLACES the arrangement's tone slots
///         and the placed changes' names. Both menus say so, in all three languages.
///     </para>
///     <para>
///         <b>The user sees every step.</b> The run is a status-rail task with a cancel
///         affordance and a progress bar per tone change; every outcome (nothing to do,
///         cancelled, none generated, done) is the rail's main line with its severity.
///         The log keeps the full per-tone trail.
///     </para>
///     <para>
///         <b>Threading:</b> call <see cref="RunAsync" /> on the UI thread. The project is
///         read there, the generator runs inside <c>Task.Run</c>, and the await resumes on
///         the caller's context, so the write and the rail line land on the UI thread.
///     </para>
/// </summary>
public sealed class AiToneGenerationRun
{
    /// <summary>The status-rail task id.</summary>
    public const string TaskId = "ai-tone-generate";

    private readonly ProjectState _state;
    private readonly IChartMaintenanceService _maintenance;
    private readonly IStatusBarService? _statusBar;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly Func<IRsToneGenerator?> _generator;

    /// <summary>0 = idle, 1 = running. A second run would ask the model for the same
    /// windows and race the first one's write into the project.</summary>
    private int _inFlight;

    /// <summary>Builds the run.</summary>
    /// <param name="state">The open project and its audio.</param>
    /// <param name="maintenance">The one-undo-step seam the write goes through.</param>
    /// <param name="statusBar">The rail's task pills; null in a host without one.</param>
    /// <param name="localization">The text the user reads.</param>
    /// <param name="logger">The per-tone trail.</param>
    /// <param name="generator">The AI lane, resolved on first use so nothing builds it
    /// on the startup path.</param>
    public AiToneGenerationRun(
        ProjectState state,
        IChartMaintenanceService maintenance,
        IStatusBarService? statusBar,
        ILocalizationService localization,
        IAppLogger logger,
        Func<IRsToneGenerator?> generator)
    {
        _state = state;
        _maintenance = maintenance;
        _statusBar = statusBar;
        _localization = localization;
        _logger = logger;
        _generator = generator;
    }

    /// <summary>True while a run is in flight.</summary>
    public bool IsRunning => Volatile.Read(ref _inFlight) != 0;

    /// <summary>
    ///     Generates and writes the set for the active arrangement. Never throws: every
    ///     failure is a line on the rail.
    /// </summary>
    /// <param name="charterNotes">The charter's own words for these tones, or null; cleaned and
    /// capped by <see cref="AiToneContextText.Normalize" /> and sent beside the project's metadata.</param>
    public async Task RunAsync(string? charterNotes = null)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            _logger.Info(AppLogCategory.Audio, "Tone generation is already running; ignoring the request.");
            return;
        }

        try
        {
            await RunCoreAsync(charterNotes);
        }
        catch (OperationCanceledException)
        {
            _logger.Info(AppLogCategory.Audio, "Tone generation cancelled.");
            _state.SetMessage(_localization["ToneGenerate_Cancelled"], StatusSeverity.Warning);
        }
        catch (Exception ex)
        {
            var text = _localization["ToneGenerate_Failed"] + " " + ex.Message;
            _logger.Error(AppLogCategory.Errors, text, ex);
            _state.SetMessage(text, StatusSeverity.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private async Task RunCoreAsync(string? charterNotes)
    {
        if (Plan(charterNotes) is not { } plan) return;

        var (project, arrangement, request, generator) = plan;
        _logger.Info(AppLogCategory.Audio, string.Create(CultureInfo.InvariantCulture,
            $"Generating {request.Windows.Count} Rocksmith tone(s) for '{arrangement.Name}' from the " +
            $"{request.Instrument} stem."));

        ToneSetGenerationResult result;
        using (var cts = new CancellationTokenSource())
        {
            _statusBar?.BeginTask(TaskId, _localization.Format("ToneGenerate_Running", arrangement.Name),
                indeterminate: false, cancel: () => cts.Cancel());
            var rail = new RailProgress(this);
            try
            {
                // The generator separates, decodes, measures and renders. However much
                // of that runs before its first await, none of it runs on this thread.
                result = await Task.Run(() => generator.GenerateAsync(request, rail, cts.Token, rail), cts.Token);
            }
            finally
            {
                _statusBar?.CompleteTask(TaskId);
            }
        }

        foreach (var note in result.Notes) _logger.Info(AppLogCategory.Audio, note);

        // Minutes can pass. A project that was closed or switched meanwhile is not the
        // one these tones were generated for.
        if (!ReferenceEquals(_state.ActiveProject, project) || !project.Arrangements.Contains(arrangement))
        {
            Say("ToneGenerate_ProjectChanged", StatusSeverity.Warning);
            return;
        }

        if (result.Generated == 0)
        {
            var why = result.Tones.Select(t => t.Error).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
            var text = why is null
                ? _localization["ToneGenerate_NoneGenerated"]
                : _localization.Format("ToneGenerate_NoneGeneratedWhy", why);
            _logger.Warning(AppLogCategory.Audio, text);
            _state.SetMessage(text, StatusSeverity.Error);
            return;
        }

        // ONE undo node for the whole set: the tone definitions, the arrangement's slots
        // and every placed change's name come back with one Ctrl+Z.
        RsToneWriteResult applied = new([], 0, null);
        _maintenance.RunAsSingleUndoStep(
            () => applied = RsToneProjectWriter.Write(project, arrangement, result.Tones));

        var done = _localization.PluralWith("ToneGenerate_DoneOne", "ToneGenerate_Done", request.Windows.Count,
            applied.Keys.Count, request.Windows.Count, applied.BaseTone ?? "");
        _logger.Info(AppLogCategory.Audio, done);
        _state.SetMessage(done, StatusSeverity.Success);
    }

    /// <summary>The pill's one progress target: the latest line becomes its label and
    /// the share of tone changes started its bar. Called from the generator's thread;
    /// the status service marshals to the UI itself.</summary>
    private sealed class RailProgress(AiToneGenerationRun owner) : IProgress<string>, IProgress<double>
    {
        private readonly Lock _gate = new();
        private double _fraction;

        public void Report(string value)
        {
            owner._logger.Info(AppLogCategory.Audio, value);
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

    private sealed record RunPlan(
        ChartProject Project, Arrangement Arrangement, ToneGenerateRequest Request, IRsToneGenerator Generator);

    /// <summary>Reads the project on the calling thread, or says on the rail why there
    /// is nothing to generate from and answers null.</summary>
    private RunPlan? Plan(string? charterNotes)
    {
        var project = _state.ActiveProject;
        var arrangement = _state.ActiveArrangement;
        if (project is null || arrangement is null) return Refuse("ToneGenerate_NoProject");
        if (arrangement.ToneChanges.Count == 0) return Refuse("ToneGenerate_NoToneChanges");

        // A window is one MEASURE of musical time. Without a tempo map there is no
        // measure, and a fixed millisecond fallback would be a different window on
        // every song.
        if (project.Beats.Count == 0) return Refuse("ToneGenerate_NoBeatMap");

        var audio = ProjectAudioLocator.TryResolve(_state.CurrentProjectPath, _state.CurrentAudioFileName);
        if (audio is null) return Refuse("ToneGenerate_NoAudio");
        if (_generator() is not { } generator) return Refuse("ToneGenerate_NotAvailable");

        var windows = ToneWindowPlanner.Plan(arrangement.ToneChanges, project.Beats, _state.TotalAudioLengthMs);
        var request = new ToneGenerateRequest(
            audio,
            string.IsNullOrWhiteSpace(arrangement.Name) ? "tone" : arrangement.Name,
            StemFor(arrangement, project),
            windows,
            ContextFor(project, arrangement, charterNotes),
            project.Chart.SongName,
            project.Chart.Artist,
            project.ToneDefinitions.Select(d => d.Key).ToList());
        return new RunPlan(project, arrangement, request, generator);
    }

    private RunPlan? Refuse(string key)
    {
        Say(key, StatusSeverity.Warning);
        return null;
    }

    private void Say(string key, StatusSeverity severity)
    {
        var text = _localization[key];
        _logger.Warning(AppLogCategory.Audio, text);
        _state.SetMessage(text, severity);
    }

    /// <summary>
    ///     Which separated stem an arrangement's tones are measured from.
    ///     <para>
    ///         <b>A bass arrangement is measured off the BASS stem</b>, and a missing
    ///         one is an explicit failure per change rather than a quiet fall-back to
    ///         the full mix — a bass tone built from the drums looks exactly as
    ///         confident as a real one.
    ///     </para>
    ///     <para>
    /// "Bass" is the PART, not the path: a 5/6-string bass charted on
    ///         a guitar path at A220, or one whose tones are all bass tones, is
    ///         measured off the bass stem too (<see cref="ArrangementBassTones.IsBassPart" />).
    ///     </para>
    /// </summary>
    internal static string StemFor(Arrangement arrangement, ChartProject? project = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        return ArrangementBassTones.IsBassPart(project, arrangement) ||
               (arrangement.Name ?? string.Empty).Contains("bass", StringComparison.OrdinalIgnoreCase)
            ? ToneStems.Bass
            : ToneStems.Guitar;
    }

    /// <summary>
    ///     The prior-knowledge half of the two generation sources, from what the
    ///     project already knows: song, artist, album, year, arrangement.
    ///     <para>
    /// The charter's own words (the box in Generate tones with AI…) follow the
    ///         metadata as one more part, cleaned and capped by <see cref="AiToneContextText" />.
    ///         None leaves the line exactly as the metadata alone wrote it.
    ///     </para>
    /// </summary>
    internal static string? ContextFor(ChartProject project, Arrangement arrangement, string? charterNotes = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);

        var parts = new List<string>(6);
        if (!string.IsNullOrWhiteSpace(project.Chart.Artist)) parts.Add("band: " + project.Chart.Artist);
        if (!string.IsNullOrWhiteSpace(project.Chart.SongName)) parts.Add("song: " + project.Chart.SongName);
        if (!string.IsNullOrWhiteSpace(project.Chart.AlbumName)) parts.Add("album: " + project.Chart.AlbumName);
        if (!string.IsNullOrWhiteSpace(project.Chart.AlbumYear)) parts.Add("year: " + project.Chart.AlbumYear);
        if (!string.IsNullOrWhiteSpace(arrangement.Name)) parts.Add("arrangement: " + arrangement.Name);
        if (AiToneContextText.Normalize(charterNotes) is { } notes) parts.Add(AiToneContextText.Label + notes);
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
