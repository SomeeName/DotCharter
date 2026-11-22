using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Audio.LivePlay.Takes;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The Results screen's "listen back" to the run just played.
///     <see cref="PlayModeViewModel" /> owns one and exposes it as
///     <c>ListenBack</c>, so <c>ResultsView</c> binds <c>ListenBack.…</c>.
///     <para>
///         The recorder (opt-in, <c>AppConfig.LivePlayTakeRecorderEnabled</c>)
///         tees every run's DRY input capture to disk and <c>LiveTakeStore</c>
///         attaches the file to the session record; this plays one back.
///         There is no second audio transport — the engine has exactly one master
///         source — so listen-back BORROWS it and puts the song back afterwards
///         (path + playhead + any stem mutes it had to make). That is why every
///         exit path goes through <see cref="StopAsync" />: a take left loaded
///         would mean the results screen's Retry plays the recording instead of
///         the song.
///     </para>
///     <para>
///         Play Mode tells it three things: a run started (<see cref="Reset" />),
///         a take landed (<see cref="OnTakeSaved" />, with whether Results is
///         showing) and the screen left Results (<see cref="StopAsync" />).
///     </para>
/// </summary>
public sealed partial class RunListenBackViewModel : ObservableObject
{
    private readonly IAudioPlaybackService _audio;
    private readonly ILiveTakeStore _takeStore;
    private readonly IExportTargetWriter _files;
    private readonly StemMixerViewModel? _stemMixer;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly DispatcherTimer _takeWatchTimer;
    private readonly List<StemTrackViewModel> _mutedForTake = [];

    /// <summary>Take of the run that just finished. Null until finalization lands.</summary>
    private string? _resultTakeFile;

    // What the transport had before listen-back borrowed it.
    private string? _takeRestoreAudioPath;
    private double _takeRestorePositionMs;
    private StemTrackViewModel? _takeRestoreMaster;
    private bool _takeRestoreMasterMuted;
    private long _takeMediaGeneration;
    private CancellationTokenSource? _takeLoad;
    private Task? _pendingTakeLoad;
    private Task _stopping = Task.CompletedTask;
    private string? _borrowedTakePath;
    private int _requestGeneration;

    /// <summary>Results screen: a listen-back recording of this run exists.</summary>
    [ObservableProperty] private bool _canListenBackToRun;

    [ObservableProperty] private bool _isTakePlaying;

    /// <summary>"Playing your take…" / "That recording is gone." — a one-liner
    /// beside the button.</summary>
    [ObservableProperty] private string _takePlaybackStatus = "";

    public RunListenBackViewModel(
        IAudioPlaybackService audio,
        ILiveTakeStore takeStore,
        IExportTargetWriter files,
        StemMixerViewModel? stemMixer,
        ILocalizationService localization,
        IAppLogger logger)
    {
        _audio = audio;
        _takeStore = takeStore;
        _files = files;
        _stemMixer = stemMixer;
        _localization = localization;
        _logger = logger;
        // Polls the borrowed transport while a take plays: a take that simply
        // ran out has to hand the transport (and the song) back on its own.
        _takeWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _takeWatchTimer.Tick += (_, _) => TakeWatchTick();
    }

    /// <summary>A new run supersedes the last one's take (its own lands on finalize).</summary>
    public void Reset()
    {
        _ = StopAsync();
        _resultTakeFile = null;
        CanListenBackToRun = false;
        TakePlaybackStatus = "";
    }

    /// <summary>Take finalization (WAV close + Opus encode) runs off-thread and
    /// lands a second or two AFTER the results screen appears — that is when
    /// listen-back becomes offerable, not before.</summary>
    public void OnTakeSaved(string takeFile, bool resultsShowing)
    {
        _resultTakeFile = takeFile;
        CanListenBackToRun = resultsShowing && ResolveTakePath(takeFile) != null;
    }

    /// <summary>A stored take file NAME → an absolute path that exists, or null.</summary>
    private string? ResolveTakePath(string? takeFile)
    {
        if (string.IsNullOrEmpty(takeFile)) return null;
        try
        {
            var path = Path.Combine(_takeStore.TakesDirectory, takeFile);
            return _files.FileExists(path) ? path : null;
        }
        catch (Exception ex)
        {
            _logger.Debug(AppLogCategory.Audio, $"Take path could not be resolved: {ex.Message}");
            return null;
        }
    }

    [RelayCommand]
    private Task ListenBackToRun() => PlayTakeAsync(_resultTakeFile);

    private async Task PlayTakeAsync(string? takeFile)
    {
        var path = ResolveTakePath(takeFile);
        if (path == null)
        {
            TakePlaybackStatus = _localization["PlayMode_Take_Missing"];
            return;
        }

        var stopping = StopAsync();
        var generation = _requestGeneration;
        await stopping;
        if (generation != _requestGeneration) return;

        var load = new CancellationTokenSource();
        _takeLoad = load;
        _borrowedTakePath = path;

        _takeRestoreAudioPath = _audio.CurrentAudioPath is { Length: > 0 } loaded ? loaded : null;
        _takeRestorePositionMs = _audio.CurrentTimeMs;
        // Stems ride the same transport as the master source, so they would play
        // the SONG underneath the take. Run-scoped mutes only (never the domain
        // Stem.Muted flag — see ApplyBackingTrackMutes).
        try
        {
            var pending = _audio.LoadTemporaryAudioAsync(path, load.Token, out var owner);
            _takeMediaGeneration = owner;
            _pendingTakeLoad = pending;
            await pending;
            if (load.IsCancellationRequested || !ReferenceEquals(_takeLoad, load)) return;
            if (await LostTakeAsync(path, owner)) return;
            if (!_audio.RunIfOwned(path, owner, _audio.Stop) ||
                !_audio.RunIfOwned(path, owner, PrepareTakeMix) ||
                !_audio.RunIfOwned(path, owner, () => _audio.SeekTo(0)) ||
                !_audio.RunIfOwned(path, owner, _audio.Play))
            {
                await StopAsync();
                return;
            }

            IsTakePlaying = true;
            TakePlaybackStatus = _localization["PlayMode_Take_Playing"];
            _takeWatchTimer.Start();
            _logger.Info(AppLogCategory.Audio, $"Listen-back: playing take '{Path.GetFileName(path)}'.");
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            // The Results screen (or this take) lost ownership during decode.
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Audio, "Listen-back: take load failed.", ex);
            if (ReferenceEquals(_takeLoad, load))
            {
                await StopAsync();
                TakePlaybackStatus = _localization["PlayMode_Take_Missing"];
            }
        }
        finally
        {
            if (ReferenceEquals(_takeLoad, load))
            {
                _takeLoad = null;
                _pendingTakeLoad = null;
            }
            load.Dispose();
        }
    }

    private void PrepareTakeMix()
    {
        _takeRestoreMaster = _stemMixer?.Tracks.FirstOrDefault(t => t.IsMasterMix);
        _takeRestoreMasterMuted = _takeRestoreMaster?.IsMuted ?? false;
        MuteStemsForTake(true);
        _takeRestoreMaster?.SetMutedForRun(false);
        _audio.SetMasterMixMuted(false);
    }

    /// <summary>True, after handing the transport back, when the take lost the media.</summary>
    private async Task<bool> LostTakeAsync(string path, long owner)
    {
        if (_audio.OwnsMedia(path, owner)) return false;
        await StopAsync();
        return true;
    }

    [RelayCommand]
    private Task StopTake() => StopAsync();

    /// <summary>Hands the transport back: stops, un-mutes the stems and reloads
    /// the song that was open (at the position it was at). Safe to call when no
    /// take is playing.</summary>
    public Task StopAsync()
    {
        _requestGeneration++;
        var load = _takeLoad;
        _takeLoad = null;
        load?.Cancel();
        if (!IsTakePlaying && _borrowedTakePath == null) return _stopping;
        return _stopping = StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        var pending = _pendingTakeLoad;
        var take = _borrowedTakePath;
        var owner = _takeMediaGeneration;
        var restore = _takeRestoreAudioPath;
        var position = _takeRestorePositionMs;
        var master = _takeRestoreMaster;
        var masterMuted = _takeRestoreMasterMuted;
        _takeRestoreMaster = null;
        _pendingTakeLoad = null;
        _borrowedTakePath = null;
        _takeRestoreAudioPath = null;
        IsTakePlaying = false;
        TakePlaybackStatus = "";
        _takeWatchTimer.Stop();

        if (pending != null)
        {
            try { await pending; }
            catch (OperationCanceledException) { /* The cancelled decode kept the song. */ }
            catch (Exception ex) { _logger.Warning(AppLogCategory.Audio, "Listen-back: pending load failed while stopping.", ex); }
        }
        if (take is not null)
        {
            try
            {
                _audio.RunIfOwned(take, owner, () =>
                {
                    MuteStemsForTake(false);
                    if (master != null && _stemMixer?.Tracks.Contains(master) == true)
                    {
                        master.SetMutedForRun(masterMuted);
                        _audio.SetMasterMixMuted(masterMuted);
                    }
                });
                _mutedForTake.Clear();
                await _audio.TryRestoreAudioAsync(restore, take, owner, position);
            }
            catch (Exception ex)
            {
                _logger.Warning(AppLogCategory.Audio, "Listen-back: restoring the song failed.", ex);
                TakePlaybackStatus = _localization["PlayMode_Take_Missing"];
            }
        }
    }

    /// <summary>The take ran out (or the transport was taken from under us):
    /// give the song back without waiting for a Stop click.</summary>
    private void TakeWatchTick()
    {
        if (!IsTakePlaying)
        {
            _takeWatchTimer.Stop();
            return;
        }

        var ended = false;
        var owned = _borrowedTakePath is { } take && _audio.RunIfOwned(take, _takeMediaGeneration, () =>
        {
            var length = _audio.GetTotalAudioLengthMs();
            ended = !_audio.IsPlaying || (length > 0 && _audio.CurrentTimeMs >= length - 100);
        });
        if (!owned || ended) _ = StopAsync();
    }

    private void MuteStemsForTake(bool mute)
    {
        if (_stemMixer == null) return;
        if (mute)
        {
            foreach (var track in _stemMixer.Tracks.Where(t => !t.IsMasterMix && !t.IsMuted))
            {
                track.SetMutedForRun(true);
                _mutedForTake.Add(track);
            }

            return;
        }

        foreach (var track in _mutedForTake.Where(track => _stemMixer.Tracks.Contains(track)))
        {
            track.SetMutedForRun(false);
            track.RestoreSoloFromProject();
        }

        _mutedForTake.Clear();
    }
}
