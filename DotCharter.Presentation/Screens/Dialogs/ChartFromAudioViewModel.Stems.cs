// Chart from audio's stem strip: whether the analysis would hear the full
// mix, the Separate stems verb that runs the app's own separation, and following
// a run that outlives the dialog.
using System.Windows.Input;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Import.Audio;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

public sealed partial class ChartFromAudioViewModel
{
    private bool _needsStem;
    private string _stemHint = "";
    private SongStemSeparationAvailability _availability = SongStemSeparationAvailability.Blocked("");
    private bool _isSeparating;
    private double _separationProgress;
    private CancellationTokenSource? _separationCts;
    private SongStemSeparationRun? _followedRun;
    private ClosableProgress<double>? _progressTarget;
    private int _stemProbeGeneration;

    /// <summary>The latest look for a stem; a test awaits it, the view never needs to.</summary>
    public Task StemCheck { get; private set; } = Task.CompletedTask;

    public bool HasStemHint => _needsStem || IsSeparating;

    public string StemHint
    {
        get => _stemHint;
        private set => SetProperty(ref _stemHint, value);
    }

    public bool CanSeparateStems => _availability.CanSeparate && !IsSeparating;

    public string SeparateStemsBlockedReason => IsSeparating || _availability.CanSeparate ? "" : _availability.Reason;

    public bool IsSeparating
    {
        get => _isSeparating;
        private set
        {
            if (!SetProperty(ref _isSeparating, value)) return;
            OnPropertyChanged(nameof(HasStemHint));
            OnPropertyChanged(nameof(CanSeparateStems));
            OnPropertyChanged(nameof(SeparateStemsBlockedReason));
            OnPropertyChanged(nameof(SeparationKeepsRunningNote));
        }
    }

    public string SeparationKeepsRunningNote =>
        IsSeparating ? _edit.Localization["ChartFromAudio_SeparationKeepsRunning"] : "";

    public double SeparationProgress
    {
        get => _separationProgress;
        private set => SetProperty(ref _separationProgress, value);
    }

    public ICommand SeparateStemsCommand { get; }

    public ICommand CancelSeparationCommand { get; }

    /// <summary>
    ///     Looks for the arrangement instrument's stem of the chosen file, off the UI
    ///     thread. None found ⇒ the strip shows <paramref name="line" /> (or the plain
    ///     hint) and whether Separate stems could run. A newer look drops this one.
    /// </summary>
    private async Task RefreshStemHintAsync(string? line)
    {
        if (!IsSeparating && _separator.Running is { } running && running.Separates(AudioPath))
        {
            await FollowSeparationAsync(running);
            return;
        }

        var generation = ++_stemProbeGeneration;
        var path = AudioPath;
        var type = _edit.State.ActiveArrangement?.ArrangementType;
        (bool Needed, SongStemSeparationAvailability Availability) probe;
        try
        {
            probe = await Task.Run(async () =>
            {
                if (path.Length == 0 || ChartFromAudioStemLocator.SourcesFor(type).Count == 0 || !_files.FileExists(path)
                    || _stems.TryLocate(path, type) != null)
                {
                    return (false, SongStemSeparationAvailability.Blocked(""));
                }

                return (true, await _separator.GetAvailabilityAsync(path).ConfigureAwait(false));
            });
        }
        catch (Exception ex)
        {
            _edit.Logger.Warning(AppLogCategory.Import, $"Chart-from-audio stem check failed. File='{path}': {ex.Message}");
            probe = (true, SongStemSeparationAvailability.Blocked(ex.Message));
        }

        if (generation != _stemProbeGeneration) return;
        _needsStem = probe.Needed;
        _availability = probe.Availability;
        if (!IsSeparating) StemHint = probe.Needed ? line ?? _edit.Localization["ChartFromAudio_StemHint"] : "";
        OnPropertyChanged(nameof(HasStemHint));
        OnPropertyChanged(nameof(CanSeparateStems));
        OnPropertyChanged(nameof(SeparateStemsBlockedReason));
    }

    /// <summary>The user's Separate stems: the app's own run, then a fresh look for the stem.</summary>
    private async Task SeparateStemsAsync()
    {
        if (!CanSeparateStems) return;
        using var cts = new CancellationTokenSource();
        _separationCts = cts;
        var progress = ShowSeparation(0);

        SongStemSeparationOutcome outcome;
        try
        {
            outcome = await _separator.SeparateAsync(progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = new SongStemSeparationOutcome(SongStemSeparationStatus.Cancelled);
        }
        catch (Exception ex)
        {
            _edit.Logger.Error(AppLogCategory.Import, "Chart-from-audio stem separation failed.", ex);
            outcome = new SongStemSeparationOutcome(SongStemSeparationStatus.Failed, ex.Message);
        }
        finally
        {
            _separationCts = null;
        }

        await EndSeparationAsync(outcome);
    }

    /// <summary>
    ///     Reopened while the app separates this file: show that run, never start a
    ///     second. The strip, its progress, Cancel separation and the note all follow it.
    /// </summary>
    private async Task FollowSeparationAsync(SongStemSeparationRun run)
    {
        _followedRun = run;
        _edit.Logger.Info(AppLogCategory.Import,
            $"Chart-from-audio follows the stem separation already running. File='{run.AudioPath}'.");
        SongStemSeparationOutcome outcome;
        using (run.Watch(ShowSeparation(run.Progress)))
        {
            outcome = await run.Completion;
        }

        _followedRun = null;
        await EndSeparationAsync(outcome);
    }

    /// <summary>Shows a run in the strip; the returned progress drops reports once the run or the dialog ends.</summary>
    private ClosableProgress<double> ShowSeparation(double progress)
    {
        SeparationProgress = Math.Clamp(progress, 0, 1);
        IsSeparating = true;
        StemHint = _edit.Localization["ChartFromAudio_Separating"];
        IProgress<double> onUiThread = new Progress<double>(v =>
        {
            if (IsSeparating && !IsClosed) SeparationProgress = Math.Clamp(v, 0, 1);
        });
        _progressTarget?.Close();
        _progressTarget = new ClosableProgress<double>(onUiThread.Report);
        return _progressTarget;
    }

    /// <summary>
    ///     The run ended. A closed dialog only logs it: the run was the app's, its stems
    ///     are in the cache, and nothing here is shown any more.
    /// </summary>
    private async Task EndSeparationAsync(SongStemSeparationOutcome outcome)
    {
        _progressTarget?.Close();
        if (IsClosed)
        {
            _edit.Logger.Info(AppLogCategory.Import,
                $"Chart-from-audio stem separation ended after the dialog closed: {outcome.Status}.");
            return;
        }

        IsSeparating = false;
        _edit.Logger.Info(AppLogCategory.Import,
            $"Chart-from-audio stem separation ended: {outcome.Status}. File='{AudioPath}'.");
        StemCheck = RefreshStemHintAsync(LineFor(outcome));
        await StemCheck;
    }

    /// <summary>Closing leaves the run going and stops this dialog from hearing about it.</summary>
    private void LetSeparationRunOn() => _progressTarget?.Close();

    private void CancelSeparation()
    {
        _separationCts?.Cancel();
        _followedRun?.Cancel();
    }

    /// <summary>What the strip says when a separation left no usable stem behind.</summary>
    private string? LineFor(SongStemSeparationOutcome outcome) => outcome.Status switch
    {
        _ when outcome.ProducedStems => _edit.Localization["ChartFromAudio_StemStillMissing"],
        SongStemSeparationStatus.Cancelled => _edit.Localization["ChartFromAudio_SeparationCancelled"],
        SongStemSeparationStatus.Declined => null,
        _ when string.IsNullOrWhiteSpace(outcome.Reason) => _edit.Localization["ChartFromAudio_SeparationFailedNoReason"],
        _ => _edit.Localization.Format("ChartFromAudio_SeparationFailed", outcome.Reason)
    };
}
