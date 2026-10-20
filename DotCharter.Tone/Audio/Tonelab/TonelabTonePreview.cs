using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>
///     The in-game <see cref="ITonePreviewTarget" />: the same snapshot/apply/restore contract
///     as the clone rig, mapped onto the tonelab pipe. The game keeps the restore point itself
///     (<c>tone restore</c> goes back to the tone active before the first <c>apply-file</c>),
///     so <see cref="Stop" /> only has to ask for it.
///     <list type="bullet">
///         <item>Every <see cref="Apply" /> is checked against the gear catalogue FIRST (the
///         validator is handed in: it lives above this assembly) and a tone that fails is
///         never sent; <see cref="Completed" /> reports it as <see cref="TonelabOutcome.Invalid" />.</item>
///         <item>Applies are debounced (latest wins). A flush that only moves knobs sends
///         <c>tone knob</c> per moved knob; anything else writes a temp file and sends
///         <c>apply-file</c>.</item>
///         <item>All pipe I/O runs on the scheduler's thread and the thread pool, one command
///         at a time, never on the caller's (UI) thread.</item>
///     </list>
/// </summary>
public sealed class TonelabTonePreview : ITonePreviewTarget, IDisposable
{
    private readonly ITonelabSession _session;
    private readonly Func<RsTone, TonelabResult?> _validate;
    private readonly TonelabToneFiles _files;
    private readonly TonelabDebouncer _debouncer;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _sync = new();
    private RsTone? _wanted;
    private RsTone? _sent;
    private string? _sentFile;
    private bool _active;
    private bool _flushing;
    private bool _again;
    private int _generation;

    /// <param name="session">The game side.</param>
    /// <param name="validate">The catalogue check: null when the tone may be sent, else the <see cref="TonelabOutcome.Invalid" /> result to report.</param>
    /// <param name="files">Where tone files are written.</param>
    /// <param name="scheduler">The debounce clock (<see cref="TimerTonelabScheduler.Instance" /> in the app).</param>
    /// <param name="logger">The log.</param>
    public TonelabTonePreview(ITonelabSession session, Func<RsTone, TonelabResult?> validate, TonelabToneFiles files,
        ITonelabScheduler scheduler, IAppLogger logger)
    {
        _session = session;
        _validate = validate;
        _files = files;
        _logger = logger;
        _debouncer = new TonelabDebouncer(scheduler, TonelabDebouncer.DefaultDelay);
    }

    /// <summary>Raised on a worker thread with each send's (or each refused tone's) result.</summary>
    public event EventHandler<TonelabResult>? Completed;

    public bool IsAvailable => _session.State == TonelabSessionState.Ready;

    public bool IsActive
    {
        get
        {
            lock (_sync) return _active;
        }
    }

    /// <summary>The last flush or restore, for a test to await.</summary>
    internal Task LastTask { get; private set; } = Task.CompletedTask;

    public void Apply(RsTone tone)
    {
        if (!IsAvailable) return;
        if (_validate(tone) is { } refused)
        {
            _debouncer.Cancel();
            Completed?.Invoke(this, refused);
            return;
        }

        int generation;
        lock (_sync)
        {
            _wanted = tone;
            _active = true;
            generation = _generation;
        }

        _debouncer.Post(() =>
        {
            var flush = FlushAsync(generation);
            if (!flush.IsCompleted) LastTask = flush;
        });
    }

    public bool Stop()
    {
        _debouncer.Cancel();
        lock (_sync)
        {
            _generation++;
            if (!_active) return false;
            _active = false;
            _wanted = null;
        }

        LastTask = Task.Run(RestoreAsync);
        return true;
    }

    public void Dispose()
    {
        Stop();
        _debouncer.Dispose();
    }

    /// <summary>
    ///     At most ONE send in flight (a knob reply takes ~0.2 s while the game window is hidden):
    ///     a flush that fires while one is running only marks the tone dirty, and the running one
    ///     sends the latest tone once its reply is in — the burst coalesces to two sends, never a queue.
    /// </summary>
    private async Task FlushAsync(int generation)
    {
        lock (_sync)
        {
            if (_flushing)
            {
                _again = true;
                return;
            }

            _flushing = true;
        }

        while (true)
        {
            await FlushOnceAsync(generation).ConfigureAwait(false);
            lock (_sync)
            {
                if (!_again || generation != _generation)
                {
                    _again = false;
                    _flushing = false;
                    return;
                }

                _again = false;
            }
        }
    }

    private async Task FlushOnceAsync(int generation)
    {
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            RsTone? wanted;
            lock (_sync)
            {
                if (generation != _generation) return;
                wanted = _wanted;
            }

            if (wanted is null) return;
            var knobs = _sent is null ? null : TonelabToneDiff.KnobChanges(_sent, wanted);
            var result = knobs is null ? await ApplyFileAsync(wanted).ConfigureAwait(false) : await MoveKnobsAsync(knobs).ConfigureAwait(false);
            if (result is null) return;
            if (result.IsOk)
            {
                _sent = wanted;
                // The game holds the tone now: from here the guitar is heard through it (RestoreAsync hands it back).
                _session.RouteAudio(this, true);
            }
            else
            {
                _sent = null;   // unknown state in the game: the next flush sends the whole tone
            }
            Report(result);
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Audio, "tonelab: tone preview send failed.", ex);
            Report(new TonelabResult(TonelabOutcome.Rejected, ex.Message));
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task<TonelabResult> ApplyFileAsync(RsTone tone)
    {
        var path = _files.Write(RsToneJsonWriter.Write(tone));
        var result = await _session.ApplyToneFileAsync(path).ConfigureAwait(false);
        if (result.IsOk) _sentFile = path;
        _files.ReleaseAllBut(_sentFile);
        return result;
    }

    /// <summary>Null when nothing the game hears changed.</summary>
    private async Task<TonelabResult?> MoveKnobsAsync(IReadOnlyList<TonelabKnobChange> knobs)
    {
        TonelabResult? last = null;
        foreach (var knob in knobs)
        {
            last = await _session.SetKnobAsync(knob.GearSlot, knob.KnobKey, knob.Value).ConfigureAwait(false);
            if (!last.IsOk) return last;
        }

        return last;
    }

    private async Task RestoreAsync()
    {
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            _session.RouteAudio(this, false);
            _sent = null;
            // Nothing the game accepted (Stop before the debounce fired, or every send refused):
            // nothing to put back, and the game would answer err nothing-to-restore.
            if (_sentFile is null)
            {
                _files.ReleaseAllBut(null);
                return;
            }

            var result = await _session.RestoreToneAsync().ConfigureAwait(false);
            _sentFile = null;
            _files.ReleaseAllBut(null);
            Report(result);
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Audio, "tonelab: tone restore failed.", ex);
        }
        finally
        {
            _io.Release();
        }
    }

    private void Report(TonelabResult result)
    {
        if (!result.IsOk) _logger.Warning(AppLogCategory.Audio, $"tonelab: {result.Outcome} {result.ErrorCode} {result.Detail}".TrimEnd());
        Completed?.Invoke(this, result);
    }
}
