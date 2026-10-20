using Avalonia.Threading;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Audio.LivePlay.Replays;
using DotCharter.Services.Audio.LivePlay.Takes;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     What a finished Live Play run leaves on disk besides its session record:
///     the recorded TAKE (the dry input tee, finalized and encoded off-thread)
///     and the run REPLAY (the judged sounds, persisted off-thread), each
///     attached to the session record once it exists. Exposed as
///     <see cref="LivePlayViewModel.RunArchive" />, where Play Mode listens for
///     <see cref="TakeSaved" />.
///     <para>
///         No bindable state and no lock: <see cref="TryStartTake" />,
///         <see cref="FinalizeTakeAsync" /> and <see cref="SaveReplayAsync" /> are
///         called by <c>StartCore</c> / <c>StopCore</c> under the view model's
///         session gate, and the two save paths only ever hand their disk work to
///         a <see cref="Task.Run(Func{Task})" /> — never I/O under the gate.
///         Both stores are optional: a headless or minimal construction keeps
///         neither, and every member is then a cheap no-op.
///     </para>
/// </summary>
public sealed class LivePlayRunArchive
{
    private readonly IPracticeProfileService _profiles;
    private readonly IAppLogger _logger;

    // Take recorder (0.17 foundations): tees the same drained capture PCM
    // into a bounded background WAV writer. Null unless the config flag is on
    // and a session is active; Write() is allocation-free and never blocks,
    // so the tee adds nothing to the pump's per-tick cost.
    private readonly ILiveTakeStore? _takeStore;

    /// <summary>v0.32 run-replay store; null in headless/minimal constructions.</summary>
    private readonly IReplayStore? _replayStore;

    public LivePlayRunArchive(IPracticeProfileService profiles, IAppLogger logger,
        ILiveTakeStore? takeStore = null, IReplayStore? replayStore = null)
    {
        _profiles = profiles;
        _logger = logger;
        _takeStore = takeStore;
        _replayStore = replayStore;
    }

    /// <summary>
    ///     Raised (UI thread) once a run's take has been written, encoded and
    ///     attached to its session record — the file NAME, relative to
    ///     <see cref="ILiveTakeStore.TakesDirectory" />. Finalization is
    ///     deliberately off-thread and therefore lands AFTER
    ///     <see cref="LivePlayViewModel.SessionCompleted" />, so the results screen can only offer
    ///     listen-back once this fires (a second or two into the screen).
    /// </summary>
    public event Action<string>? TakeSaved;

    /// <summary>Whether a recorded run's replay is kept at all — the caller only
    /// snapshots the judged sounds when it is.</summary>
    public bool KeepsReplays => _replayStore != null;

    /// <summary>Opt-in take recorder: a recorder teeing this run's dry input
    /// capture to disk, or null when there is no store, it is disabled, or
    /// there is no input.</summary>
    public LiveTakeRecorder? TryStartTake(int sampleRate) => _takeStore?.TryStartTake(sampleRate);

    /// <summary>Fire-and-forget take finalization: stops the bounded writer,
    /// encodes (Opus when ffmpeg is available), attaches the file to the most
    /// recent session record and sweeps orphaned/over-cap takes. Never throws
    /// and never blocks the caller.</summary>
    public void FinalizeTakeAsync(LiveTakeRecorder recorder, bool attach, string songKey, string arrangement)
    {
        var store = _takeStore;
        if (store == null)
        {
            recorder.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var takeFile = await store.FinalizeTakeAsync(recorder).ConfigureAwait(false);
                if (attach && takeFile != null)
                {
                    _profiles.AttachTakeFile(songKey, arrangement, takeFile);
                    // Bound state lives on the other side of this event — marshal.
                    Dispatcher.UIThread.Post(() => TakeSaved?.Invoke(takeFile));
                }

                await store.CleanupAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(AppLogCategory.Audio, $"Take finalization failed: {ex.Message}");
            }
        });
    }

    /// <summary>Fire-and-forget replay persistence (v0.32): writes the tiny
    /// JSON + attaches it to the just-recorded session. Never throws, never
    /// blocks the caller — the projection already happened under the gate.</summary>
    public void SaveReplayAsync(RunReplay replay)
    {
        var store = _replayStore;
        if (store == null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var name = store.Save(replay);
                if (name != null)
                {
                    _profiles.AttachReplayFile(replay.SongKey, replay.ArrangementName, name);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(AppLogCategory.Audio, $"Replay save failed: {ex.Message}");
            }
        });
    }
}
