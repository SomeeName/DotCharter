// ReampTakes: the per-seat injected re-amp take (ABI v37) — what is loaded, its transport as last seen, the ONE engine write a live load and the rebuild replay share (a collaborator of JuceAudioPlaybackService).
using JuceSharp;

namespace DotCharter.Tone.Audio.Reamp;

/// <summary>
///     The take half of <see cref="IReampEngine" />. <c>JuceAudioPlaybackService</c>
///     implements the seam by forwarding here (the capture reads stay on the service:
///     they are the live-input ring, not the take), and calls
///     <see cref="ReplayTakesTo" /> from its per-seat attach so a rebuilt engine gets
///     every take back.
///     <para>
///         It shares no state with the service: the engine comes in as a getter (one
///         volatile read per call, the same read the partial made of <c>_engine</c>),
///         and its one lock is its own and never held across an engine call.
///     </para>
/// </summary>
internal sealed class ReampTakes
{
    /// <summary>One seat's loaded take: the source samples at their own rate
    /// (what a rebuild at a new rate resamples from), the copy last resampled for
    /// the engine, and the transport as last known — what the replay restores.</summary>
    private sealed class ReampTake
    {
        public required float[] Source { get; init; }
        public required int SourceRate { get; init; }
        public float[]? Resampled { get; set; }
        public int ResampledRate { get; set; }
        public bool Loop { get; set; }
        public bool Playing { get; set; }
        public double PositionSeconds { get; set; }
    }

    private readonly IAppLogger? _logger;
    private readonly Func<JuceAudioEngine?> _engine;
    private readonly object _reampGate = new();
    private readonly ReampTake?[] _reampTakes = new ReampTake?[Dsp.DspPlayers.MaxPlayers];

    public ReampTakes(IAppLogger? logger, Func<JuceAudioEngine?> engine)
    {
        _logger = logger;
        _engine = engine;
    }

    public bool LoadTake(int player, float[] mono, int sampleRate, bool loop)
    {
        ArgumentNullException.ThrowIfNull(mono);
        if (!Dsp.DspPlayers.IsValid(player) || mono.Length == 0 || sampleRate <= 0) return false;
        var engine = _engine();
        if (engine == null) return false;
        var take = new ReampTake { Source = mono, SourceRate = sampleRate, Loop = loop };
        if (!PushTakeTo(engine, player, take)) return false;
        // Recorded only once the engine took it: a take the engine refused must
        // not come back on the next rebuild as if it had been live.
        lock (_reampGate) _reampTakes[player] = take;
        return true;
    }

    public bool HasTake(int player) => TakeFor(player) != null;

    public void ClearTake(int player)
    {
        if (!Dsp.DspPlayers.IsValid(player)) return;
        lock (_reampGate) _reampTakes[player] = null;
        var engine = _engine();
        if (engine == null) return;
        try { engine.ClearInputInjection(player); }
        catch (Exception ex) when (ex is ObjectDisposedException or JuceAudioException)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Re-amp: clearing player {player + 1}'s take failed: {ex.Message}");
        }
    }

    public bool SetTakePlaying(int player, bool playing)
    {
        var take = TakeFor(player);
        if (take == null) return false;
        take.Playing = playing;
        var engine = _engine();
        if (engine == null) return false;
        try { return engine.SetInputInjectionPlaying(player, playing); }
        catch (ObjectDisposedException) { return false; }
    }

    public bool SetTakeLoop(int player, bool loop)
    {
        var take = TakeFor(player);
        if (take == null) return false;
        take.Loop = loop;
        var engine = _engine();
        if (engine == null) return false;
        try { return engine.SetInputInjectionLoop(player, loop); }
        catch (ObjectDisposedException) { return false; }
    }

    public bool SeekTake(int player, double seconds)
    {
        var take = TakeFor(player);
        if (take == null) return false;
        take.PositionSeconds = Math.Max(0, seconds);
        var engine = _engine();
        if (engine == null) return false;
        try
        {
            var rate = engine.SampleRate;
            return rate > 0 && engine.SeekInputInjection(player, (long)Math.Round(take.PositionSeconds * rate));
        }
        catch (ObjectDisposedException) { return false; }
    }

    public bool TryGetTakeStatus(int player, out ReampTransportStatus status)
    {
        status = default;
        var take = TakeFor(player);
        var engine = _engine();
        if (take == null || engine == null) return false;
        try
        {
            var rate = engine.SampleRate;
            if (rate <= 0 || !engine.TryGetInputInjectionStatus(player, out var s)) return false;
            status = new ReampTransportStatus((double)s.PositionFrames / rate, (double)s.LengthFrames / rate,
                s.IsPlaying, s.IsLooping);
            // The poll is also what keeps the replay honest: a rebuild resumes the
            // take where the player last SAW it, playing or paused as it was.
            take.PositionSeconds = status.PositionSeconds;
            take.Playing = status.IsPlaying;
            return true;
        }
        catch (ObjectDisposedException) { return false; }
    }

    private ReampTake? TakeFor(int player)
    {
        if (!Dsp.DspPlayers.IsValid(player)) return null;
        lock (_reampGate) return _reampTakes[player];
    }

    /// <summary>
    ///     Puts every recorded take back on a REBUILT engine — which starts with no
    ///     injection at all — resampled to its rate, at the position and in the
    ///     play state it had. Called from
    ///     <c>JuceAudioPlaybackService.AttachPerPlayerStateToEngine</c>.
    ///     A take that cannot be replayed is DROPPED and logged, so the UI's next
    ///     status poll reads "no take" and the transport says so, rather than a
    ///     player that looks loaded and plays nothing.
    /// </summary>
    public void ReplayTakesTo(JuceAudioEngine engine)
    {
        for (var player = 0; player < _reampTakes.Length; player++)
        {
            var take = TakeFor(player);
            if (take == null) continue;
            if (PushTakeTo(engine, player, take))
            {
                _logger?.Info(AppLogCategory.Audio,
                    $"Re-amp: player {player + 1}'s take replayed onto the rebuilt engine at {take.PositionSeconds:0.00} s.");
                continue;
            }
            lock (_reampGate) if (ReferenceEquals(_reampTakes[player], take)) _reampTakes[player] = null;
            _logger?.Warning(AppLogCategory.Audio,
                $"Re-amp: player {player + 1}'s take could not be replayed after the engine rebuild; it was unloaded.");
        }
    }

    /// <summary>The ONE engine write of a take — used by a live load and by the
    /// rebuild replay alike, so the two cannot drift. Resamples (and caches) the
    /// source for the engine's rate, loads it, then restores position, loop and
    /// play state.</summary>
    private bool PushTakeTo(JuceAudioEngine engine, int player, ReampTake take)
    {
        try
        {
            var rate = engine.SampleRate;
            if (rate <= 0) return false;
            if (take.Resampled == null || take.ResampledRate != rate)
            {
                take.Resampled = DiTakeResampler.Resample(take.Source, take.SourceRate, rate);
                take.ResampledRate = rate;
            }
            if (take.Resampled.Length == 0) return false;
            engine.SetInputInjection(player, take.Resampled, take.Loop);
            if (take.PositionSeconds > 0)
                engine.SeekInputInjection(player, (long)Math.Round(take.PositionSeconds * rate));
            if (take.Playing) engine.SetInputInjectionPlaying(player, true);
            return true;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or JuceAudioException or ArgumentException)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Re-amp: loading player {player + 1}'s take failed: {ex.Message}");
            return false;
        }
    }
}
