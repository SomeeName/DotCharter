using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>
///     The in-game <see cref="ITonePreviewTarget" />: the same snapshot/apply/restore contract
///     as the clone rig, mapped onto the tonelab pipe. The game keeps the restore point itself
///     (<c>tone restore</c> goes back to the tone active before the first <c>apply-file</c>),
///     so <see cref="Stop" /> only has to ask for it, and only while no other preview on the
///     same session is applying or holds a tone (the last owner restores).
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
    private readonly TonelabPreviewOwnership _ownership;
    private EventHandler? _sessionChanged;
    private bool _disposed;
    private int _requestId;
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
        _ownership = TonelabPreviewOwnership.For(session);
        _validate = validate;
        _files = files;
        _logger = logger;
        _debouncer = new TonelabDebouncer(scheduler, TonelabDebouncer.DefaultDelay);
        WatchSession(session);
    }

    /// <summary>Raised on a worker thread with each send's (or each refused tone's) result.</summary>
    public event EventHandler<TonelabResult>? Completed;

    public bool IsAvailable => !_disposed && _session.State == TonelabSessionState.Ready;

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
        lock (_sync)
        {
            if (_disposed || !IsAvailable) return;
        }
        if (_validate(tone) is { } refused)
        {
            StopCore(false);
            // Logged as well as reported: the switch only shows it in a tooltip.
            _logger.Warning(AppLogCategory.Audio, $"tonelab: tone '{tone.Key}' not sent ({refused.ErrorCode}): {refused.Detail}");
            Completed?.Invoke(this, refused);
            return;
        }

        int generation;
        lock (_sync)
        {
            if (_disposed) return;
            _requestId++;
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

    public bool Stop() => StopCore(true);

    private bool StopCore(bool reportRestore)
    {
        _debouncer.Cancel();
        lock (_sync)
        {
            _generation++;
            _again = false;
            if (!_active) return false;
            _active = false;
            _wanted = null;
            // Reserve the restore's I/O turn before a new generation can flush.
            var turn = _io.WaitAsync();
            LastTask = Task.Run(() => RestoreAsync(turn, reportRestore));
        }
        return true;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _session.StateChanged -= _sessionChanged;
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
                if (!_again)
                {
                    _again = false;
                    _flushing = false;
                    return;
                }

                _again = false;
                generation = _generation;
            }
        }
    }

    private async Task FlushOnceAsync(int generation)
    {
        await _io.WaitAsync().ConfigureAwait(false);
        var request = -1;
        try
        {
            RsTone? wanted;
            lock (_sync)
            {
                if (generation != _generation) return;
                wanted = _wanted;
                request = _requestId;
            }

            if (wanted is null) return;
            var result = await _ownership.ApplyAsync(this, () => CanSend(generation), async current =>
            {
                var knobs = current && _sent is not null ? TonelabToneDiff.KnobChanges(_sent, wanted) : null;
                return knobs is null ? await ApplyFileAsync(wanted).ConfigureAwait(false) : await MoveKnobsAsync(knobs).ConfigureAwait(false);
            }, _logger).ConfigureAwait(false);
            if (result is null) return;
            _sent = result.IsOk ? wanted : null;
            if (!result.IsOk)
            {
                _sent = null;
                _sentFile = null;
                _files.ReleaseAllBut(null);
                lock (_sync)
                {
                    if (generation == _generation && request == _requestId)
                    {
                        _active = false;
                        _wanted = null;
                    }
                }
            }
            Report(result);
        }
        catch (Exception ex)
        {
            try
            {
                await _ownership.ReleaseAsync(this).ConfigureAwait(false);
            }
            catch (Exception restoreError)
            {
                _logger.Error(AppLogCategory.Audio, "tonelab: failed preview cleanup failed.", restoreError);
            }
            _sent = null;
            _sentFile = null;
            _files.ReleaseAllBut(null);
            lock (_sync)
            {
                if (generation == _generation && request == _requestId)
                {
                    _active = false;
                    _wanted = null;
                }
            }
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

    private bool CanSend(int generation)
    {
        lock (_sync) return !_disposed && _active && generation == _generation;
    }

    private async Task RestoreAsync(Task turn, bool reportRestore)
    {
        await turn.ConfigureAwait(false);
        try
        {
            var result = await _ownership.ReleaseAsync(this).ConfigureAwait(false);
            if (reportRestore && result is not null) Report(result);
            if (!reportRestore && result is { IsOk: false })
                _logger.Warning(AppLogCategory.Audio, $"tonelab: invalid-edit restore failed ({result.Outcome} {result.Detail}).");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Audio, "tonelab: tone restore failed.", ex);
        }
        finally
        {
            _sent = null;
            _sentFile = null;
            _files.ReleaseAllBut(null);
            _io.Release();
        }
    }

    private void WatchSession(ITonelabSession session)
    {
        var weak = new WeakReference<TonelabTonePreview>(this);
        EventHandler? changed = null;
        changed = (_, _) =>
        {
            if (!weak.TryGetTarget(out var preview)) session.StateChanged -= changed;
            else if (session.State != TonelabSessionState.Ready) preview.LoseSession();
        };
        _sessionChanged = changed;
        session.StateChanged += changed;
    }

    private void LoseSession()
    {
        _debouncer.Cancel();
        lock (_sync)
        {
            _generation++;
            _active = false;
            _wanted = null;
            _again = false;
            var turn = _io.WaitAsync();
            LastTask = Task.Run(async () =>
            {
                await turn.ConfigureAwait(false);
                try
                {
                    _sent = null;
                    _sentFile = null;
                    _files.ReleaseAllBut(null);
                }
                finally
                {
                    _io.Release();
                }
            });
        }
    }

    private void Report(TonelabResult result)
    {
        if (!result.IsOk) _logger.Warning(AppLogCategory.Audio, $"tonelab: {result.Outcome} {result.ErrorCode} {result.Detail}".TrimEnd());
        Completed?.Invoke(this, result);
    }
}
