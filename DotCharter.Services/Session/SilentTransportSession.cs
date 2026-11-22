using System.Collections.Specialized;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Audio.SilentTransport;
using DotCharter.Services.State;

namespace DotCharter.Services.Session;

/// <summary>
///     The silent-transport fallback of one project session: a chart with content
///     but no resolvable audio runs on a GENERATED silent WAV, so the playhead,
///     scrubbing, count-in and Play Mode work. Owns the loaded length, the
///     re-entrancy guard of the growth check and the watch on the active
///     project's beat grid. The silent file is loaded into the engine only and is
///     never the song identity.
/// </summary>
internal sealed class SilentTransportSession
{
    private readonly IAudioPlaybackService _audioService;
    private readonly ProjectState _projectState;
    private readonly IEditorTransport _transport;
    private readonly IAppLogger _logger;
    private readonly SilentAudioTrack _silentAudio;
    private readonly Func<double, Task>? _reload;

    /// <summary>The length of the silent transport currently in the engine, or 0
    /// when the engine is on real audio (or nothing). Non-zero is exactly "this
    /// session is running on generated silence".</summary>
    private double _lengthMs;

    /// <summary>Re-entrancy guard: the regeneration below mutates nothing the
    /// beat collection watches, but the load is awaited and a burst of beat
    /// additions would otherwise queue one regeneration per beat.</summary>
    private bool _growing;

    /// <summary>A growth check arrived while a load was in flight: run the check
    /// again when it lands, against the chart as it is THEN. Dropping it
    /// left a chart extended during the load stuck on the shorter track.</summary>
    private bool _growAgain;

    private INotifyCollectionChanged? _watchedBeats;

    public SilentTransportSession(
        IAudioPlaybackService audioService,
        ProjectState projectState,
        IEditorTransport transport,
        IAppLogger logger,
        SilentAudioTrack silentAudio, IHistoryService history, Func<double, Task>? reload = null)
    {
        _audioService = audioService;
        _projectState = projectState;
        _transport = transport;
        _logger = logger;
        _silentAudio = silentAudio;
        _reload = reload;
        // A tempo edit moves beats IN PLACE, which the beat collection never
        // reports; every undoable edit raises HistoryChanged.
        history.HistoryChanged += OnHistoryChanged;
    }

    /// <summary>The loaded silent length in ms; 0 when the engine is not on silence.</summary>
    public double LengthMs => _lengthMs;

    public async Task LoadAsync(double projectLengthMs, Func<bool>? isCurrent = null)
    {
        try
        {
            // The CONTENT length, not the planned one: GetOrCreateSilentWav
            // plans the length it is given, so handing it an already-planned
            // value bought a whole extra bucket every time (a 20 s chart got a
            // 45 s file while everything downstream believed 30 s). The recorded
            // length has to be the file's real one, because the growth check
            // below compares against it.
            var planned = SilentAudioTrack.PlanLengthMs(projectLengthMs);
            _lengthMs = planned;
            var path = _silentAudio.GetOrCreateSilentWav(projectLengthMs);
            _logger.Info(AppLogCategory.Audio,
                $"No song audio — loading a {planned / 1000.0:0.#}s silent transport so playback/scrubbing/count-in/Play Mode work (path='{path}').");
            // NOTE: the silent file is loaded into the engine ONLY. We deliberately
            // do NOT touch ProjectState.CurrentAudioFileName — the project's own
            // (possibly missing) audio reference is preserved for save, and the
            // silent WAV is never the song identity, so stem separation / live
            // detection never run against silence.
            if (isCurrent != null && !isCurrent()) return;
            await _audioService.LoadAudioAsync(path);
            if (isCurrent != null && !isCurrent()) return;
            _projectState.TotalAudioLengthMs = _audioService.GetTotalAudioLengthMs();
            _projectState.WaveformData = WaveformSamples.Empty;
            _transport.SeekTo(0);
            WatchTimeline();
        }
        catch (Exception ex)
        {
            _lengthMs = 0;
            _logger.Warning(AppLogCategory.Audio, "Silent-transport fallback failed", ex);
        }
    }

    /// <summary>
    ///     Re-generates the silent transport when the chart has grown past it.
    ///     <para>
    ///         The length is derived from the project's content ONCE, at load, and
    ///         a chart-only project is exactly the kind that gets extended
    ///         afterwards. Past the silent file's end the session simply runs out
    ///         of transport: the playhead stops, every seek clamps there, and Play
    ///         Mode cannot reach notes the editor is drawing. The bucket grid
    ///         (<c>SilentAudioTrack.BucketMs</c>) keeps this cheap — the planned
    ///         length only moves in 15 s steps, so ordinary editing inside the
    ///         current bucket decides in one comparison and the WAV is reused from
    ///         the cache when a bucket is revisited.
    ///     </para>
    /// </summary>
    public async Task EnsureCoversProjectAsync()
    {
        if (_lengthMs <= 0) return;
        if (_growing)
        {
            _growAgain = true;
            return;
        }

        _growing = true;
        try
        {
            do
            {
                _growAgain = false;
                await GrowOnceAsync();
            } while (_growAgain && _lengthMs > 0);
        }
        finally
        {
            _growing = false;
            _growAgain = false;
        }
    }

    private async Task GrowOnceAsync()
    {
        if (_projectState.ActiveProject is not { } project) return;

        var contentMs = SilentAudioTrack.DeriveProjectLengthMs(project);
        if (!SilentAudioTrack.NeedsLongerTrack(_lengthMs, contentMs)) return;

        _logger.Info(AppLogCategory.Audio,
            $"The chart outgrew its silent transport ({_lengthMs / 1000.0:0.#}s) — " +
            $"regenerating for a content end of {contentMs / 1000.0:0.#}s.");
        var resumeMs = _projectState.CurrentTimeMs;
        if (_reload != null) await _reload(contentMs);
        else await LoadAsync(contentMs);
        if (_lengthMs <= 0 || !ReferenceEquals(project, _projectState.ActiveProject)) return;
        // LoadAsync seeks to 0 (it is the LOAD path). Growing the track is
        // not a load, so the playhead stays where the user left it — inside
        // the new, longer track by construction.
        _transport.SeekTo(Math.Clamp(resumeMs, 0, _projectState.TotalAudioLengthMs));
    }

    /// <summary>Loads the silent transport for a project that has content but no
    /// resolvable audio; a truly empty / no-content project keeps the free-engine
    /// behaviour (nothing to scrub).</summary>
    public async Task LoadForProjectAsync(ChartProject project, Func<bool>? isCurrent = null)
    {
        var contentMs = SilentAudioTrack.DeriveProjectLengthMs(project);
        if (!SilentAudioTrack.ShouldUseFallback(hasResolvableAudio: false, contentMs))
        {
            Stop();
            _audioService.FreeMasterAudio();
            _projectState.TotalAudioLengthMs = 0;
            _projectState.WaveformData = WaveformSamples.Empty;
            return;
        }

        await LoadAsync(contentMs, isCurrent);
    }

    /// <summary>The engine is leaving silence (real audio loaded, or shutdown):
    /// forget the length and stop watching the beat grid.</summary>
    public void Stop()
    {
        _lengthMs = 0;
        if (_watchedBeats == null)
        {
            return;
        }

        _watchedBeats.CollectionChanged -= OnTimelineChanged;
        _watchedBeats = null;
    }

    /// <summary>Watches the beat grid of the project the silent transport was
    /// just generated for. The collection instance belongs to the ACTIVE
    /// PROJECT, so it is re-attached per load rather than once in the
    /// constructor.</summary>
    private void WatchTimeline()
    {
        _watchedBeats?.CollectionChanged -= OnTimelineChanged;

        _watchedBeats = _projectState.Beats;
        _watchedBeats.CollectionChanged += OnTimelineChanged;
    }

    private void OnTimelineChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Removals can only shorten the chart, and the track is never shrunk —
        // a shorter chart inside a longer transport costs a few seconds of
        // scrubable silence, and rewriting the file for that would thrash the
        // engine mid-edit.
        if (e.Action == NotifyCollectionChangedAction.Remove)
        {
            return;
        }

        // Fire-and-forget on the UI thread: the method is re-entrancy guarded
        // and logs its own failures through LoadAsync.
        _ = EnsureCoversProjectAsync();
    }

    private void OnHistoryChanged()
    {
        // Only while this session runs on silence (the beat grid is watched
        // exactly then); inside the current bucket the check is one comparison.
        if (_watchedBeats != null) _ = EnsureCoversProjectAsync();
    }
}
