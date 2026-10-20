namespace DotCharter.Services.Ai.Match.Search;

/// <summary>Hands the matcher its retrieval library, building it on first use.
/// An interface rather than a <see cref="RigLibrary" /> because the build is
/// minutes long and must not happen in a constructor — and because a test hands
/// over a library it already has.</summary>
public interface IRigLibraryProvider
{
    /// <summary>The library to search NOW. May be partial (see
    /// <see cref="RigLibrary.IsComplete" />); never blocks on more than
    /// <see cref="RigLibraryOptions.FirstMatchCount" /> renders.</summary>
    /// <param name="status">Progress lines while the first rows are rendered.</param>
    /// <param name="ct">Cancels the wait; rows already rendered are kept.</param>
    Task<RigLibrary> GetAsync(IProgress<string>? status, CancellationToken ct);

    /// <summary>
    ///     Holds the background growth for the life of the returned scope, so a
    ///     match's own renders are not queued behind a library render on the one
    ///     offline engine. The matcher takes it for the whole search. A provider
    ///     with nothing growing returns a no-op.
    /// </summary>
    IDisposable PauseGrowth() => NoScope.Instance;

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
///     The production provider: a SHIPPED library for this key if one is
///     installed, else the local cache file grown small-first and then in the
///     background.
///     <para>
///         <b>The first match never waits for more than
///         <see cref="RigLibraryOptions.FirstMatchCount" /> renders.</b> With no
///         library for the DI, <see cref="GetAsync" /> renders that many rows in
///         front of the caller (cancellable, with an ETA on every status line),
///         runs the match on them, and then grows the same file towards K on a
///         BelowNormal thread of its own — so later matches get better without
///         anyone waiting, and an app restart picks up at the row it reached. The
///         user's OWN recorded DI (a different fingerprint, so never shipped)
///         always takes this path.
///     </para>
///     <para>
///         <b>The shipped library wins whenever it is there.</b> The bundled DI is
///         fixed and the sampler is seeded, so the 4,000-rig library for that DI
///         is the same file on every machine; it is published as a release asset
///         (<c>RigEstimatorModelCatalog.Library</c>) and installed next to the
///         estimator model. Every call re-checks for it, so a download that lands
///         after the local build started replaces the partial file on the next
///         match and stops the grower.
///     </para>
///     <para>
///         <b>Growth is PAUSED while a match runs</b> (<see cref="PauseGrowth" />):
///         there is one device-less engine and its renders serialise, so a
///         background row in flight would otherwise be queued in front of every
///         one of the match's 48 renders.
///     </para>
/// </summary>
public sealed class RigLibraryProvider : IRigLibraryProvider, IDisposable
{
    private readonly Func<CancellationToken, Task<MonoAudio>> _medley;
    private readonly IRigSampler _sampler;
    private readonly IRigRenderer _renderer;
    private readonly IToneFeatureExtractor _extractor;
    private readonly RigLibraryOptions _options;
    private readonly Func<string?>? _installedLibrary;
    private readonly string? _cachePath;
    private readonly bool _growInBackground;
    private readonly IAppLogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ManualResetEventSlim _resume = new(true);
    private readonly Lock _pauseGate = new();
    private int _pauses;
    private MonoAudio? _di;
    private string? _fingerprint;
    private string? _installedTried;
    private RigLibraryBuilder? _builder;
    private volatile RigLibrary? _complete;
    private Thread? _grower;
    private bool _disposed;

    /// <summary>Wires the provider.</summary>
    /// <param name="medley">Produces the reference DI (once; the result is kept).</param>
    /// <param name="sampler">Draws the rigs.</param>
    /// <param name="renderer">Renders them.</param>
    /// <param name="extractor">Embeds the renders.</param>
    /// <param name="options">K, seed, profile, the first-match size.</param>
    /// <param name="installedLibrary">
    ///     Where a SHIPPED library for this key would be — the installer's target
    ///     path for <see cref="RigLibrary.CacheFileName" /> — or null when there
    ///     is no installer. Re-read on every call. Null (the default) means "never
    ///     look for one".
    /// </param>
    /// <param name="cachePath">Overrides the local cache file; tests pass a temp path.</param>
    /// <param name="growInBackground">False disables the background grower (tests
    /// that want to count foreground renders exactly).</param>
    /// <param name="logger">Optional log sink.</param>
    public RigLibraryProvider(
        Func<CancellationToken, Task<MonoAudio>> medley,
        IRigSampler sampler,
        IRigRenderer renderer,
        IToneFeatureExtractor extractor,
        RigLibraryOptions? options = null,
        Func<string?>? installedLibrary = null,
        string? cachePath = null,
        bool growInBackground = true,
        IAppLogger? logger = null)
    {
        _medley = medley ?? throw new ArgumentNullException(nameof(medley));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _options = options ?? new RigLibraryOptions();
        _installedLibrary = installedLibrary;
        _cachePath = cachePath;
        _growInBackground = growInBackground;
        _logger = logger;
    }

    /// <summary>True while the background grower is running.</summary>
    public bool IsGrowing => _grower is { IsAlive: true };

    /// <summary>Rows the LOCAL builder holds, for a status page; 0 before the
    /// first call or when the shipped library is in use.</summary>
    public int LocalRows => _builder?.Rows ?? 0;

    /// <inheritdoc />
    public async Task<RigLibrary> GetAsync(IProgress<string>? status, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_complete is { } ready && !InstalledMayHaveChanged()) return ready;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_di is null)
            {
                _di = await _medley(ct).ConfigureAwait(false);
                _fingerprint = RigLibrary.Fingerprint(_di);
            }

            // (1) A shipped library for THIS key. Checked every call, because it can
            //     be installed from the settings page while this process runs.
            if (TryAdoptInstalled(status) is { } shipped) return shipped;
            if (_complete is { } done) return done;

            // (2) The local file: small first, then the background.
            _builder ??= new RigLibraryBuilder(
                _di, _sampler, _renderer, _extractor, _options, LocalPath(), _logger);

            if (_builder.IsComplete)
            {
                var whole = _builder.Snapshot();
                _complete = whole;
                status?.Report(RigLibraryProgress.FromCache(whole.Count));
                return whole;
            }

            // A v2 library for the same key left by an earlier build is adopted
            // ONCE, before anything is rendered — three verification renders
            // against ninety-six real ones.
            if (_builder.Rows == 0)
                await _builder.TryImportLegacyAsync(status, ct).ConfigureAwait(false);

            var firstMatch = Math.Min(_options.FirstMatchCount, _options.Count);
            if (_builder.Rows < firstMatch)
            {
                _logger?.Info(AppLogCategory.Audio,
                    $"Tone library: no library for DI {_fingerprint}; rendering the first {firstMatch} rows in the foreground.");
                await _builder.GrowAsync(firstMatch, status, ct).ConfigureAwait(false);
            }

            var snapshot = _builder.Snapshot();
            if (snapshot.IsComplete)
            {
                _complete = snapshot;
                return snapshot;
            }

            status?.Report(RigLibraryProgress.Partial(snapshot.Count, snapshot.TargetCount));
            if (_growInBackground) StartGrower();
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public IDisposable PauseGrowth()
    {
        lock (_pauseGate)
        {
            _pauses++;
            _resume.Reset();
        }

        return new PauseScope(this);
    }

    private void ResumeGrowth()
    {
        lock (_pauseGate)
        {
            if (--_pauses <= 0)
            {
                _pauses = 0;
                _resume.Set();
            }
        }
    }

    private sealed class PauseScope(RigLibraryProvider owner) : IDisposable
    {
        private RigLibraryProvider? _owner = owner;

        public void Dispose()
        {
            var o = Interlocked.Exchange(ref _owner, null);
            o?.ResumeGrowth();
        }
    }

    private string LocalPath() =>
        _cachePath ?? RigLibrary.DefaultCachePath(_extractor.Id, _options.Count, _options.Seed, _fingerprint!);

    /// <summary>Cheap pre-check outside the gate: a new installed path since the
    /// last look means the gate is worth taking.</summary>
    private bool InstalledMayHaveChanged()
    {
        if (_installedLibrary is null) return false;
        var path = _installedLibrary();
        return path is not null && !string.Equals(path, _installedTried, StringComparison.Ordinal)
                                && File.Exists(path);
    }

    private RigLibrary? TryAdoptInstalled(IProgress<string>? status)
    {
        if (_installedLibrary is null) return null;
        var path = _installedLibrary();
        if (path is null || string.Equals(path, _installedTried, StringComparison.Ordinal)) return null;
        if (!File.Exists(path)) return null;
        _installedTried = path;

        var shipped = RigLibrary.TryLoad(path, _extractor.Id, _options.Count, _options.Seed, _fingerprint!);
        if (shipped is not { IsComplete: true })
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Tone library: installed file '{path}' is not a complete library for this key " +
                $"(extractor {_extractor.Id}, K {_options.Count}, seed {_options.Seed}, DI {_fingerprint}); ignoring it.");
            return null;
        }

        _complete = shipped;
        StopGrower();
        _logger?.Info(AppLogCategory.Audio, $"Tone library: using the installed library ({shipped.Count} rigs).");
        status?.Report(RigLibraryProgress.FromCache(shipped.Count));
        return shipped;
    }

    private void StartGrower()
    {
        if (_grower is { IsAlive: true } || _builder is null || _builder.IsComplete) return;

        var builder = _builder;
        var token = _shutdown.Token;
        _logger?.Info(AppLogCategory.Audio,
            $"Tone library: growing {builder.Rows} → {builder.TargetCount} rows in the background (BelowNormal).");

        _grower = new Thread(() => Grow(builder, token))
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "tone-library-grower"
        };
        _grower.Start();
    }

    private void Grow(RigLibraryBuilder builder, CancellationToken token)
    {
        try
        {
            var lastLogged = builder.Rows;
            // The loop is synchronous by construction: onCallerThread renders HERE
            // and the extractor is synchronous, so the awaited task is complete
            // before it is awaited and the whole run stays on this thread.
            builder.GrowAsync(
                builder.TargetCount, null, token, onCallerThread: true,
                beforeEachRender: t =>
                {
                    _resume.Wait(t);
                    if (builder.Rows - lastLogged >= 500)
                    {
                        lastLogged = builder.Rows;
                        _logger?.Info(AppLogCategory.Audio,
                            $"Tone library: {builder.Rows}/{builder.TargetCount} rows.");
                    }
                }).GetAwaiter().GetResult();

            if (builder.IsComplete)
            {
                _complete ??= builder.Snapshot();
                _logger?.Info(AppLogCategory.Audio,
                    $"Tone library: complete at {builder.TargetCount} rows ('{Path.GetFileName(builder.Path)}').");
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.Debug(AppLogCategory.Audio, $"Tone library: background growth stopped at {builder.Rows} rows.");
        }
        catch (Exception ex)
        {
            // The next GetAsync starts it again; a renderer that is broken for good
            // fails the match itself, which is where that gets reported.
            _logger?.Warning(AppLogCategory.Audio,
                $"Tone library: background growth failed at {builder.Rows} rows: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void StopGrower()
    {
        // The token is process-wide; the grower is restarted only from GetAsync,
        // which returns the installed library first once one is adopted.
        if (_grower is { IsAlive: true }) _shutdown.Cancel();
    }

    /// <summary>Stops the background grower. Never waits for a render in flight —
    /// the row it was producing is lost, the file stays consistent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _resume.Set();
        _shutdown.Dispose();
        _gate.Dispose();
    }
}

/// <summary>A provider over a library that already exists (tests, and a caller
/// that built one itself).</summary>
public sealed class FixedRigLibraryProvider(RigLibrary library) : IRigLibraryProvider
{
    /// <inheritdoc />
    public Task<RigLibrary> GetAsync(IProgress<string>? status, CancellationToken ct)
        => Task.FromResult(library);
}
