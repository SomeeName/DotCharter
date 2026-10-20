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

        if (IsTakePlaying) await StopAsync();

        _takeRestoreAudioPath = _audio.CurrentAudioPath is { Length: > 0 } loaded ? loaded : null;
        _takeRestorePositionMs = _audio.CurrentTimeMs;
        _audio.Stop();
        // Stems ride the same transport as the master source, so they would play
        // the SONG underneath the take. Run-scoped mutes only (never the domain
        // Stem.Muted flag — see ApplyBackingTrackMutes).
        MuteStemsForTake(true);

        await _audio.LoadAudioAsync(path);
        _audio.SeekTo(0);
        _audio.Play();
        IsTakePlaying = true;
        TakePlaybackStatus = _localization["PlayMode_Take_Playing"];
        _takeWatchTimer.Start();
        _logger.Info(AppLogCategory.Audio, $"Listen-back: playing take '{Path.GetFileName(path)}'.");
    }

    [RelayCommand]
    private Task StopTake() => StopAsync();

    /// <summary>Hands the transport back: stops, un-mutes the stems and reloads
    /// the song that was open (at the position it was at). Safe to call when no
    /// take is playing.</summary>
    public async Task StopAsync()
    {
        if (!IsTakePlaying) return;
        IsTakePlaying = false;
        TakePlaybackStatus = "";
        _takeWatchTimer.Stop();
        _audio.Stop();
        MuteStemsForTake(false);

        var restore = _takeRestoreAudioPath;
        var position = _takeRestorePositionMs;
        _takeRestoreAudioPath = null;

        if (restore != null && _files.FileExists(restore))
        {
            await _audio.LoadAudioAsync(restore);
            _audio.SeekTo(position);
        }
        else
        {
            // Nothing was loaded before (or it vanished): don't leave the take
            // sitting on the transport pretending to be the song.
            _audio.FreeAudio();
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

        var length = _audio.GetTotalAudioLengthMs();
        var ended = !_audio.IsPlaying || (length > 0 && _audio.CurrentTimeMs >= length - 100);
        if (ended) _ = StopAsync();
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

        foreach (var track in _mutedForTake)
        {
            track.SetMutedForRun(false);
            track.RestoreSoloFromProject();
        }

        _mutedForTake.Clear();
    }
}
