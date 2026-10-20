using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>Whether a tonelab game side is there to take commands.</summary>
public enum TonelabSessionState
{
    Disconnected,

    /// <summary>The pipe answers but <c>status</c> says <c>starting</c>.</summary>
    Starting,

    Ready
}

/// <summary>
///     The app's one tonelab session: which game process it talks to, whether that answers,
///     and the commands. Starting the game and injecting the DLL is NOT here
///     (<see cref="IGameToneLauncher" />); this attaches to a game side that is up.
/// </summary>
public interface ITonelabSession
{
    TonelabSessionState State { get; }

    int? ProcessId { get; }

    TonelabStatus? LastStatus { get; }

    /// <summary>The audio insert's counters while it is open, else null (also null in every build without the native module).</summary>
    ExternalInsertStats? AudioStats { get; }

    /// <summary>
    ///     Routes the live input through the game (the audio insert) while ANY owner asks for it
    ///     and the session is ready; the last owner to let go hands it back to the rig. Owners are
    ///     a set, not a count: the same <paramref name="owner" /> asking twice is one claim, so the
    ///     Tone Designer and the editor's preview never take the route from each other. Each
    ///     in-game preview claims it with its first accepted tone and lets go on restore, so
    ///     attaching alone never takes the guitar away. May re-clock the device to the game's
    ///     rate: never call it on the UI thread.
    /// </summary>
    void RouteAudio(object owner, bool on);

    /// <summary>Whether any owner routes the live input through the game now (a live in-game preview).</summary>
    bool IsRoutingAudio { get; }

    /// <summary>
    ///     Takes the game's audio exchange for an OFFLINE render (the AI tone check, <see cref="TonelabOfflineRenderer" />):
    ///     null while any owner routes the live input, because the game has one tone and one exchange. While the lease
    ///     is held a route claim is recorded, the insert stays closed until the lease ends, and the lease reads
    ///     <see cref="ITonelabAudioLease.Interrupted" />. One lease at a time.
    /// </summary>
    ITonelabAudioLease? TryLeaseAudio();

    /// <summary>Raised on any thread whenever <see cref="State" /> changes, and after a failed <c>status</c> (so a reason that rests on <see cref="LastStatus" /> is re-read).</summary>
    event EventHandler? StateChanged;

    /// <summary>
    ///     Finds a running game side when none is attached (several: the NEWEST process), then
    ///     asks it <c>status</c>.
    /// </summary>
    Task<TonelabSessionState> ProbeAsync(CancellationToken ct = default);

    /// <summary>Drops the current game side (if any) and probes again: the user's Reconnect.</summary>
    Task<TonelabSessionState> ReconnectAsync(CancellationToken ct = default);

    /// <summary>Whether an <see cref="IGameToneLauncher" /> is registered (only when the install carries <c>tools/tonelab/</c>).</summary>
    bool CanLaunch { get; }

    /// <summary>Has the launcher start (or inject into) the game, then attaches to it; Disconnected without a launcher or on failure.</summary>
    Task<TonelabSessionState> LaunchAsync(CancellationToken ct = default);

    /// <summary>Why the last <see cref="LaunchAsync" /> failed (e.g. Steam is not running), for the reason the UI prints; None otherwise.</summary>
    GameToneLaunchFailure LastLaunchFailure { get; }

    Task<TonelabSessionState> AttachAsync(int processId, CancellationToken ct = default);

    Task<TonelabResult> ApplyToneFileAsync(string path, CancellationToken ct = default);

    Task<TonelabResult> SetKnobAsync(string gearSlot, string knobKey, float value, CancellationToken ct = default);

    Task<TonelabResult> RestoreToneAsync(CancellationToken ct = default);

    /// <summary><c>tone dump &lt;path&gt;</c>: the game writes its active tone (e.g. the song's) to <paramref name="path" /> as JSON.</summary>
    Task<TonelabResult> DumpToneAsync(string path, CancellationToken ct = default);

    Task<TonelabResult> MuteMixAsync(bool on, CancellationToken ct = default);

    Task<TonelabResult> ShowWindowAsync(bool show, CancellationToken ct = default);

    /// <summary>
    ///     Ends the session. A game DotCharter started: <c>quit</c>, a few seconds to exit, only then
    ///     the process is ended, then RS_ASIO.ini goes back (<see cref="IGameToneLaunch.EndAsync" />).
    ///     A game the user started is only detached: nothing is sent, it keeps running.
    /// </summary>
    Task<TonelabResult> QuitAsync(CancellationToken ct = default);

    /// <summary><c>perf hidden_fps|visible_fps &lt;n&gt;</c>: the game's main-loop cap (0 = none).</summary>
    Task<TonelabResult> SetFrameCapAsync(bool whileHidden, int framesPerSecond, CancellationToken ct = default);

    /// <summary>
    ///     Whether the game holds an audio device of its own: true when the user started it (RS ASIO), false
    ///     for a launch with <c>--no-rsasio</c>. Only then does DotCharter's own ASIO device collide with it.
    /// </summary>
    bool GameHoldsAudioDevice { get; }
}

/// <summary>
///     <see cref="ITonelabSession" /> over <see cref="TonelabPipeClient" />. A command that
///     finds the pipe gone flips the state to <see cref="TonelabSessionState.Disconnected" />;
///     a later <see cref="ProbeAsync" /> or command reconnects. When the game reports ready
///     and an <see cref="IExternalProcessInsert" /> exists, the insert is opened on the
///     game's shared memory and closed again when the session ends.
/// </summary>
public sealed class TonelabSession : ITonelabSession, IDisposable
{
    private readonly Func<IReadOnlyList<int>> _discover;
    private readonly Func<int, DateTime?> _startTime;
    private readonly IExternalProcessInsert? _insert;
    private readonly IAppLogger? _logger;
    private readonly TonelabTimeouts _timeouts;
    private readonly object _sync = new();
    private TonelabPipeClient? _client;
    private TonelabSessionState _state;
    private CancellationTokenSource? _readyPoll;
    private IGameToneLaunch? _launch;
    private readonly HashSet<object> _routeOwners = new(ReferenceEqualityComparer.Instance);
    private AudioLease? _lease;

    /// <summary>The app's session, over the native audio insert when the build has one.</summary>
    public TonelabSession(IAppLogger logger, IEnumerable<IGameToneLauncher> launchers, IExternalProcessInsert? insert)
        : this(DiscoverRunningGames, insert, logger, null, ProcessStartTime) => Launcher = launchers.FirstOrDefault();

    internal TonelabSession(Func<IReadOnlyList<int>> discover, IExternalProcessInsert? insert, IAppLogger? logger, TonelabTimeouts? timeouts,
        Func<int, DateTime?>? startTime = null)
    {
        _discover = discover;
        _startTime = startTime ?? (_ => null);
        _insert = insert;
        _logger = logger;
        _timeouts = timeouts ?? TonelabTimeouts.Default;
    }

    /// <summary>
    ///     How long the session keeps asking a STARTING game: the backoff between polls. Tonelab
    ///     answers <c>ok starting</c> until a profile is loaded (its launcher drives the game to the main
    ///     menu); its handover allows up to 120 s from the launch (~120 s here). After the last step the session stays Starting and
    ///     the user's Reconnect asks again.
    /// </summary>
    public static IReadOnlyList<TimeSpan> ReadyBackoff { get; } =
    [
        .. new[] { 250, 500, 1000, 2000 }.Select(ms => TimeSpan.FromMilliseconds(ms)),
        .. Enumerable.Repeat(TimeSpan.FromSeconds(4), 29)
    ];

    /// <summary>
    ///     The ready poll after a launch DotCharter started (tonelab's HANDOVER, step 4): <c>status</c>
    ///     every 250 ms for up to 60 s (ready is measured at ~18 s).
    /// </summary>
    public static IReadOnlyList<TimeSpan> LaunchReadyPoll { get; } = Enumerable.Repeat(TimeSpan.FromMilliseconds(250), 240).ToList();

    public GameToneLaunchFailure LastLaunchFailure { get; private set; }

    public event EventHandler? StateChanged;

    public bool CanLaunch => Launcher is not null;

    public bool GameHoldsAudioDevice => _launch?.GameOpensNoAudioDevice != true;

    internal IGameToneLauncher? Launcher { get; init; }

    /// <summary>The wait between ready polls; a test hands in one that does not sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>The running ready poll, for a test to await.</summary>
    internal Task ReadyPoll { get; private set; } = Task.CompletedTask;

    public TonelabSessionState State => _state;

    public int? ProcessId { get; private set; }

    public TonelabStatus? LastStatus { get; private set; }

    public ExternalInsertStats? AudioStats => _insert is { IsOpen: true } insert ? insert.ReadStats() : null;

    public async Task<TonelabSessionState> ProbeAsync(CancellationToken ct = default)
    {
        if (ProcessId is null)
        {
            if (ChooseNewest(_discover(), _startTime) is not { } pid) return SetState(TonelabSessionState.Disconnected);
            Use(pid);
        }

        return await RefreshStatusAsync(ct).ConfigureAwait(false);
    }

    public Task<TonelabSessionState> ReconnectAsync(CancellationToken ct = default)
    {
        Forget();
        LastLaunchFailure = GameToneLaunchFailure.None;
        return ProbeAsync(ct);
    }

    public async Task<TonelabSessionState> LaunchAsync(CancellationToken ct = default)
    {
        if (Launcher is not { } launcher) return SetState(TonelabSessionState.Disconnected);
        var launch = await launcher.LaunchAsync(ct).ConfigureAwait(false);
        LastLaunchFailure = launch is null ? launcher.LastFailure : GameToneLaunchFailure.None;
        if (launch is null)
        {
            _logger?.Warning(AppLogCategory.Audio, "tonelab: the launcher did not start the game.");
            return SetState(TonelabSessionState.Disconnected, announce: true);
        }

        Use(launch.ProcessId);
        _launch = launch;
        return await RefreshStatusAsync(ct).ConfigureAwait(false);
    }

    public Task<TonelabSessionState> AttachAsync(int processId, CancellationToken ct = default)
    {
        Use(processId);
        return RefreshStatusAsync(ct);
    }

    public Task<TonelabResult> ApplyToneFileAsync(string path, CancellationToken ct = default) => RunAsync(c => c.ApplyFileAsync(path, ct));

    public Task<TonelabResult> SetKnobAsync(string gearSlot, string knobKey, float value, CancellationToken ct = default) =>
        RunAsync(c => c.KnobAsync(gearSlot, knobKey, value, ct));

    public Task<TonelabResult> RestoreToneAsync(CancellationToken ct = default) => RunAsync(c => c.RestoreAsync(ct));

    public Task<TonelabResult> DumpToneAsync(string path, CancellationToken ct = default) => RunAsync(c => c.DumpAsync(path, ct));

    public Task<TonelabResult> SetFrameCapAsync(bool whileHidden, int framesPerSecond, CancellationToken ct = default) =>
        RunAsync(c => c.PerfAsync(whileHidden, framesPerSecond, ct));

    public Task<TonelabResult> MuteMixAsync(bool on, CancellationToken ct = default) => RunAsync(c => c.MixMuteAsync(on, ct));

    public Task<TonelabResult> ShowWindowAsync(bool show, CancellationToken ct = default) => RunAsync(c => c.WindowAsync(show, ct));

    public async Task<TonelabResult> QuitAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _launch, null) is not { StartedByApp: true } launch)
        {
            Forget();
            return new TonelabResult(TonelabOutcome.Ok, "detached");
        }

        var quit = TonelabResult.NotConnected("quit not sent");
        await launch.EndAsync(async token => (quit = await RunAsync(c => c.QuitAsync(token)).ConfigureAwait(false)).IsOk, ct)
            .ConfigureAwait(false);
        Forget();
        return quit;
    }

    public void Dispose() => Forget();

    /// <summary>
    ///     The game side to attach to among <paramref name="pids" />: the newest process (one started
    ///     after another is the one the user just launched); a process whose start time cannot be
    ///     read ranks last, ties go to the higher pid. Null when there is none.
    /// </summary>
    internal static int? ChooseNewest(IReadOnlyList<int> pids, Func<int, DateTime?> startTime) =>
        pids.Count == 0 ? null : pids.OrderByDescending(pid => startTime(pid) ?? DateTime.MinValue).ThenByDescending(pid => pid).First();

    /// <summary>Every <c>\\.\pipe\tonelab-&lt;pid&gt;</c> on this machine.</summary>
    internal static IReadOnlyList<int> DiscoverRunningGames()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return [];
        try
        {
            return [.. Directory.EnumerateFiles(@"\\.\pipe\", TonelabProtocol.PipePrefix + "*")
                .Select(path => TonelabProtocol.ProcessIdOf(Path.GetFileName(path)))
                .OfType<int>()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static DateTime? ProcessStartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private void Use(int processId)
    {
        lock (_sync)
        {
            if (ProcessId == processId && _client is not null) return;
            if (_launch is { } launch && launch.ProcessId != processId) _launch = null;
            CloseInsert();
            _client?.Dispose();
            _client = new TonelabPipeClient(TonelabProtocol.PipeNameFor(processId), _timeouts);
            ProcessId = processId;
        }
    }

    private async Task<TonelabSessionState> RefreshStatusAsync(CancellationToken ct)
    {
        var client = _client;
        if (client is null) return SetState(TonelabSessionState.Disconnected);
        var status = await client.StatusAsync(ct).ConfigureAwait(false);
        LastStatus = status;
        if (!status.Result.IsOk)
        {
            _logger?.Info(AppLogCategory.Audio, $"tonelab: status failed ({status.Result.Outcome} {status.Result.Detail}).");
            if (status.Result.Outcome is TonelabOutcome.NotConnected or TonelabOutcome.Timeout or TonelabOutcome.UnsupportedVersion) Forget();
            return SetState(TonelabSessionState.Disconnected, announce: true);
        }

        if (status.State != TonelabGameState.Ready)
        {
            SetState(TonelabSessionState.Starting);
            StartReadyPoll();
            return TonelabSessionState.Starting;
        }

        if (RoutingWanted) OpenInsert(status);
        return SetState(TonelabSessionState.Ready);
    }

    private async Task<TonelabResult> RunAsync(Func<TonelabPipeClient, Task<TonelabResult>> command)
    {
        var client = _client;
        if (client is null) return TonelabResult.NotConnected("no tonelab session");
        var result = await command(client).ConfigureAwait(false);
        if (result.Outcome == TonelabOutcome.NotConnected) Forget();
        else if (_state != TonelabSessionState.Ready && result.IsOk) await RefreshStatusAsync(CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    public void RouteAudio(object owner, bool on)
    {
        bool wanted;
        lock (_sync)
        {
            if (on) _routeOwners.Add(owner);
            else _routeOwners.Remove(owner);
            wanted = _routeOwners.Count > 0;
            if (on) _lease?.Interrupt();
        }

        if (!wanted) CloseInsert();
        else if (_state == TonelabSessionState.Ready && LastStatus is { } status) OpenInsert(status);
    }

    public bool IsRoutingAudio => RoutingWanted;

    public ITonelabAudioLease? TryLeaseAudio()
    {
        lock (_sync)
        {
            if (_routeOwners.Count > 0 || _lease is not null) return null;
            _lease = new AudioLease(this);
            return _lease;
        }
    }

    private void EndLease(AudioLease lease)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_lease, lease)) return;
            _lease = null;
        }

        // A route claimed during the lease was queued: it opens now.
        if (RoutingWanted && _state == TonelabSessionState.Ready && LastStatus is { } status) OpenInsert(status);
    }

    /// <summary>Whether any owner wants the live input routed through the game.</summary>
    private bool RoutingWanted
    {
        get
        {
            lock (_sync) return _routeOwners.Count > 0;
        }
    }

    private void OpenInsert(TonelabStatus status)
    {
        if (_insert is not { IsSupported: true } insert || insert.IsOpen || ProcessId is not { } pid || Volatile.Read(ref _lease) is not null) return;
        var endpoint = new ExternalInsertEndpoint(TonelabProtocol.SharedMemoryNameFor(pid), status.SampleRate, status.BlockFrames);
        if (!insert.Open(endpoint)) _logger?.Warning(AppLogCategory.Audio, $"tonelab: the audio insert did not open on {endpoint.SharedMemoryName}.");
    }

    private void CloseInsert()
    {
        if (_insert is { IsOpen: true } insert) insert.Close();
    }

    /// <summary>The game is gone (or quit): drop the pipe and the pid, so the next probe looks for a game again.</summary>
    /// <summary>One poll at a time: re-asks <c>status</c> with <see cref="ReadyBackoff" /> until ready, gone or out of steps.</summary>
    private void StartReadyPoll()
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_readyPoll is not null) return;
            _readyPoll = cts = new CancellationTokenSource();
        }

        ReadyPoll = Task.Run(async () =>
        {
            try
            {
                foreach (var wait in _launch is { StartedByApp: true } ? LaunchReadyPoll : ReadyBackoff)
                {
                    await Delay(wait, cts.Token).ConfigureAwait(false);
                    var client = _client;
                    if (client is null) return;
                    var status = await client.StatusAsync(cts.Token).ConfigureAwait(false);
                    LastStatus = status;
                    if (!status.Result.IsOk)
                    {
                        if (status.Result.Outcome is TonelabOutcome.NotConnected or TonelabOutcome.UnsupportedVersion) Forget();
                        SetState(TonelabSessionState.Disconnected, announce: true);
                        return;
                    }

                    if (status.State != TonelabGameState.Ready) continue;
                    if (RoutingWanted) OpenInsert(status);
                    SetState(TonelabSessionState.Ready);
                    return;
                }

                _logger?.Info(AppLogCategory.Audio, "tonelab: the game is still starting; Reconnect asks again.");
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // The session let go of the game (Reconnect, Quit, Dispose) while the poll waited.
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_readyPoll, cts)) _readyPoll = null;
                }

                cts.Dispose();
            }
        });
    }

    private void Forget()
    {
        lock (_sync)
        {
            _readyPoll?.Cancel();
            CloseInsert();
            _client?.Dispose();
            _client = null;
            ProcessId = null;
        }

        SetState(TonelabSessionState.Disconnected);
    }

    private TonelabSessionState SetState(TonelabSessionState state, bool announce = false)
    {
        if (_state == state && !announce) return state;
        _state = state;
        _logger?.Info(AppLogCategory.Audio, $"tonelab: session {state}.");
        StateChanged?.Invoke(this, EventArgs.Empty);
        return state;
    }

    /// <summary>The offline render's hold on the game's exchange; a route claim while it is held marks it interrupted.</summary>
    private sealed class AudioLease(TonelabSession owner) : ITonelabAudioLease
    {
        private int _interrupted;
        private int _ended;

        public bool Interrupted => Volatile.Read(ref _interrupted) != 0;

        public void Interrupt() => Volatile.Write(ref _interrupted, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) owner.EndLease(this);
        }
    }
}

/// <summary>A hold on the game's audio exchange for an offline render (<see cref="ITonelabSession.TryLeaseAudio" />).</summary>
public interface ITonelabAudioLease : IDisposable
{
    /// <summary>A live preview claimed the route while the lease was held: what was rendered may have heard its tone.</summary>
    bool Interrupted { get; }
}
