using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>Where an install keeps tonelab: <c>tools/tonelab/tonelab.dll</c> and the injection helper beside it.</summary>
public sealed record TonelabInstall(string HelperPath, string DllPath)
{
    public const string FolderName = "tonelab";
    public const string HelperName = "tonelab-launch.exe";
    public const string DllName = "tonelab.dll";

    /// <summary>The install's tonelab pair, or null when either file is missing (the public launcher then stays unregistered).</summary>
    public static TonelabInstall? Find(string installRoot, Func<string, bool> fileExists)
    {
        var folder = Path.Combine(installRoot, "tools", FolderName);
        var install = new TonelabInstall(Path.Combine(folder, HelperName), Path.Combine(folder, DllName));
        return fileExists(install.HelperPath) && fileExists(install.DllPath) ? install : null;
    }
}

/// <summary>
///     The public <see cref="IGameToneLauncher" />: DotCharter starts (or attaches to) Rocksmith and
///     ends it; the 32-bit helper only injects.
///     <list type="number">
///         <item>A running game (the user's): inject into it; RS_ASIO.ini is not touched (never while the game runs), and the game is only ever detached.</item>
///         <item>Otherwise the start strategies in order: the mirror's direct start (<see cref="MirrorRocksmithStarter" />), and only when no mirror is possible Steam on the live folder, around which <see cref="RsAsioIniGuard.Apply" /> swaps the ini. Then inject at once.</item>
///         <item>A game DotCharter started on the live folder is watched: when it exits, by <c>quit</c> or otherwise, the ini is put back. A mirror game restores nothing.</item>
///     </list>
/// </summary>
public sealed class TonelabGameLauncher : IGameToneLauncher
{
    /// <summary>Set by the headless test host: the startup restore never touches the developer's game folder.</summary>
    public const string DisableStartupRestoreSwitch = "DotCharter.Tonelab.DisableStartupIniRestore";

    private readonly TonelabInjectHelper _helper;
    private readonly IReadOnlyList<IRocksmithStarter> _starters;
    private readonly IRocksmithProcesses _processes;
    private readonly RsAsioIniGuard _ini;
    private readonly IAppLogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private IGameToneLaunch? _current;

    public TonelabGameLauncher(TonelabInjectHelper helper, IReadOnlyList<IRocksmithStarter> starters, IRocksmithProcesses processes,
        RsAsioIniGuard ini, IAppLogger logger, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _helper = helper;
        _starters = starters;
        _processes = processes;
        _ini = ini;
        _logger = logger;
        _delay = delay;
    }

    /// <summary>How long a start may take to show a <c>Rocksmith2014.exe</c> (Steam may update or ask first).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>How often the start looks for the process.</summary>
    public TimeSpan StartPoll { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a game DotCharter started gets to exit after <c>quit</c> before it is ended (tonelab's HANDOVER: 10 s).</summary>
    public TimeSpan QuitGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The start strategies, in the order they are tried.</summary>
    public IReadOnlyList<IRocksmithStarter> Starters => _starters;

    public GameToneLaunchFailure LastFailure { get; private set; }

    /// <summary>The launch now running, when DotCharter started its game; null otherwise.</summary>
    public IGameToneLaunch? Current => _current;

    /// <summary>
    ///     The app's launcher over the real processes and the registry's game folder: the mirror's
    ///     direct start (<see cref="TonelabGameMirror.DefaultRoot" />), Steam on the live folder as the fallback.
    /// </summary>
    public static TonelabGameLauncher Create(TonelabInstall install, IAppLogger logger)
    {
        var processes = RocksmithProcesses.Instance;
        return new TonelabGameLauncher(new TonelabInjectHelper(install.HelperPath, install.DllPath, HiddenHelperProcessRunner.Instance),
            [CreateMirrorStarter(TonelabGameMirror.DefaultRoot), new SteamRocksmithStarter(logger)], processes, IniGuardFor(processes), logger,
            Task.Delay);
    }

    /// <summary>The direct start over the real disk, Steam and processes, mirroring into <paramref name="mirrorRoot" />.</summary>
    public static IRocksmithStarter CreateMirrorStarter(string mirrorRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The tonelab mirror is Windows-only.");
        return new MirrorRocksmithStarter(new TonelabGameMirror(Win32GameMirrorFileSystem.Instance, mirrorRoot), RocksmithGame.FindGameDirectory,
            RocksmithGame.IsSteamRunning, Win32GameProcessCreator.Instance, Environment.GetEnvironmentVariables);
    }

    /// <summary>Registers the launcher only when the install root on disk carries the tonelab pair; true when it did.</summary>
    public static bool Register(IServiceCollection services, string installRoot) => Register(services, installRoot, File.Exists);

    /// <summary>Registers the launcher only when <paramref name="installRoot" /> carries the tonelab pair; true when it did.</summary>
    public static bool Register(IServiceCollection services, string installRoot, Func<string, bool> fileExists)
    {
        if (TonelabInstall.Find(installRoot, fileExists) is not { } install) return false;
        services.AddSingleton<IGameToneLauncher>(sp => Create(install, sp.GetRequiredService<IAppLogger>()));
        return true;
    }

    /// <summary>
    ///     At every app start, off the caller's thread: a backup an earlier session left (a crash) goes
    ///     back, unless the game runs. Never throws.
    /// </summary>
    public static Task RestoreLeftIniAtStartup(IAppLogger? logger)
    {
        if (AppContext.TryGetSwitch(DisableStartupRestoreSwitch, out var disabled) && disabled) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try
            {
                LogRestore(IniGuardFor(RocksmithProcesses.Instance).Restore(), logger, "at startup");
            }
            catch (Exception ex)
            {
                logger?.Warning(AppLogCategory.Audio, "tonelab: the RS_ASIO.ini startup check failed.", ex);
            }
        });
    }

    /// <summary>How long an app exit waits for a Rocksmith DotCharter started to end (quit, the 10 s grace, then ended).</summary>
    public static TimeSpan ExitCap { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Both app exit paths: <see cref="EndStartedGameOnExit(IEnumerable{IGameToneLauncher}, Func{ITonelabSession?}, TimeSpan, IAppLogger?)" /> over the app's container. Never throws.</summary>
    public static void EndStartedGameOnExit(IServiceProvider? services)
    {
        if (services is null) return;
        EndStartedGameOnExit(services.GetServices<IGameToneLauncher>(), services.GetService<ITonelabSession>, ExitCap,
            services.GetService<IAppLogger>());
    }

    /// <summary>
    ///     On app exit: ends a game DotCharter started (through the session's quit, so the pipe it holds
    ///     sends it), bounded by <paramref name="cap" />. A game the user started is only detached.
    ///     Never throws; never constructs the session when no launcher started anything.
    /// </summary>
    public static void EndStartedGameOnExit(IEnumerable<IGameToneLauncher> launchers, Func<ITonelabSession?> session, TimeSpan cap,
        IAppLogger? logger)
    {
        try
        {
            if (!launchers.OfType<TonelabGameLauncher>().Any(l => l.Current is not null) || session() is not { } tonelab) return;
            if (!Task.Run(() => tonelab.QuitAsync()).Wait(cap))
                logger?.Warning(AppLogCategory.Audio, $"tonelab: the game DotCharter started did not end within {cap.TotalSeconds:0} s of exit.");
        }
        catch (Exception ex)
        {
            logger?.Warning(AppLogCategory.Audio, "tonelab: ending the game on exit failed.", ex);
        }
    }

    public async Task<IGameToneLaunch?> LaunchAsync(CancellationToken ct)
    {
        LastFailure = GameToneLaunchFailure.None;
        if (_processes.FindGame() is { } running) return await AttachAsync(running, ct).ConfigureAwait(false);

        foreach (var starter in _starters)
        {
            RsAsioIniOutcome? ini = null;
            if (starter.UsesLiveGameFolder)
            {
                ini = _ini.Apply();
                LogRestore(ini.Value, _logger, "before the start");
                if (ini is RsAsioIniOutcome.Failed or RsAsioIniOutcome.RefusedGameRunning) return Fail(GameToneLaunchFailure.IniRefused, $"RS_ASIO.ini {ini}");
            }

            var start = await starter.StartAsync(ct).ConfigureAwait(false);
            if (start.Status != RocksmithStartStatus.Unavailable) return await RunAsync(starter.UsesLiveGameFolder, ini, start, ct).ConfigureAwait(false);
            _logger.Info(AppLogCategory.Audio, $"tonelab: {starter.GetType().Name} is not possible here ({start.Detail}); trying the next start.");
            if (starter.UsesLiveGameFolder) LogRestore(_ini.Restore(), _logger, "after a start that was not possible");
        }

        return Fail(GameToneLaunchFailure.StartFailed, "no start strategy could run");
    }

    /// <summary>A start went out: find the process (a direct start knows it), inject AT ONCE, watch it.</summary>
    private async Task<IGameToneLaunch?> RunAsync(bool liveFolder, RsAsioIniOutcome? ini, RocksmithStart start, CancellationToken ct)
    {
        var pid = start.Status == RocksmithStartStatus.Started ? start.ProcessId ?? await WaitForGameAsync(ct).ConfigureAwait(false) : null;
        if (pid is not { } processId)
        {
            if (liveFolder) LogRestore(_ini.Restore(), _logger, "after a failed start");
            return start.Status == RocksmithStartStatus.Failed
                ? Fail(start.Failure, start.Detail)
                : Fail(GameToneLaunchFailure.StartFailed, "Rocksmith did not start");
        }

        var opensNoDevice = !liveFolder || ini is RsAsioIniOutcome.Applied or RsAsioIniOutcome.NoIni;
        var launch = new TonelabGameLaunch(processId, true, opensNoDevice, liveFolder, this);
        _current = launch;
        var inject = await _helper.InjectAsync(processId, ct).ConfigureAwait(false);
        launch.WatchExit();
        if (inject.IsOk) return launch;

        // The game DotCharter started has no tonelab: end it (no pipe to quit through); the live folder gets its ini back.
        Fail(GameToneLaunchFailure.InjectFailed, $"injection failed ({inject.Outcome}, {inject.Reason}); ending the game it was for");
        await launch.EndAsync(_ => Task.FromResult(false), ct).ConfigureAwait(false);
        return null;
    }

    private IGameToneLaunch? Fail(GameToneLaunchFailure failure, string detail)
    {
        LastFailure = failure;
        _logger.Warning(AppLogCategory.Audio, $"tonelab: the launch failed ({failure}): {detail}.");
        return null;
    }

    private async Task<IGameToneLaunch?> AttachAsync(int pid, CancellationToken ct)
    {
        var inject = await _helper.InjectAsync(pid, ct).ConfigureAwait(false);
        if (inject.IsOk) return new TonelabGameLaunch(pid, false, false, true, this);
        return Fail(GameToneLaunchFailure.InjectFailed, $"injection into the running game failed ({inject.Outcome}, {inject.Reason})");
    }

    private async Task<int?> WaitForGameAsync(CancellationToken ct)
    {
        for (var waited = TimeSpan.Zero; waited <= StartTimeout; waited += StartPoll)
        {
            if (_processes.FindGame() is { } pid) return pid;
            await _delay(StartPoll, ct).ConfigureAwait(false);
        }

        return null;
    }

    private static RsAsioIniGuard IniGuardFor(IRocksmithProcesses processes) =>
        new(PhysicalTonelabFileSystem.Instance, () => processes.FindGame() is not null, RocksmithGame.FindGameDirectory());

    private static void LogRestore(RsAsioIniOutcome outcome, IAppLogger? logger, string when)
    {
        if (logger is null || outcome is RsAsioIniOutcome.NothingToRestore or RsAsioIniOutcome.NoGameFolder) return;
        var message = $"tonelab: RS_ASIO.ini {when}: {outcome}.";
        if (outcome is RsAsioIniOutcome.Failed or RsAsioIniOutcome.RefusedGameRunning) logger.Warning(AppLogCategory.Audio, message);
        else logger.Info(AppLogCategory.Audio, message);
    }

    /// <summary>One launch; the quit sequence and, for the live folder, the ini restore after the game's exit live here.</summary>
    private sealed class TonelabGameLaunch(int processId, bool startedByApp, bool opensNoDevice, bool liveFolder, TonelabGameLauncher owner)
        : IGameToneLaunch
    {
        /// <summary>How long an ended process gets to go away before the ini is put back (or left for the next start).</summary>
        private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(3);

        /// <summary>tonelab's own launcher waits this long after the exit before it restores.</summary>
        private static readonly TimeSpan ExitSettle = TimeSpan.FromMilliseconds(500);

        public int ProcessId => processId;

        public bool GameOpensNoAudioDevice => opensNoDevice;

        public bool StartedByApp => startedByApp;

        public async Task EndAsync(Func<CancellationToken, Task<bool>> sendQuit, CancellationToken ct)
        {
            if (!startedByApp) return;
            var processes = owner._processes;
            bool quitSent;
            try
            {
                quitSent = await sendQuit(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                owner._logger.Warning(AppLogCategory.Audio, "tonelab: sending quit failed.", ex);
                quitSent = false;
            }

            var exited = quitSent && await processes.WaitForExitAsync(processId, owner.QuitGrace, ct).ConfigureAwait(false);
            if (!exited)
            {
                owner._logger.Warning(AppLogCategory.Audio, quitSent
                    ? $"tonelab: Rocksmith did not exit within {owner.QuitGrace.TotalSeconds:0} s of quit; ending it."
                    : "tonelab: quit could not be sent; ending the Rocksmith DotCharter started.");
                if (!processes.Kill(processId)) owner._logger.Warning(AppLogCategory.Audio, "tonelab: Rocksmith could not be ended.");
                await processes.WaitForExitAsync(processId, KillWait, ct).ConfigureAwait(false);
            }

            await FinishAsync().ConfigureAwait(false);
        }

        /// <summary>A game DotCharter started, ending any way at all (quit, the user, a crash), gets its ini back.</summary>
        public void WatchExit() => _ = Task.Run(async () =>
        {
            await owner._processes.WaitForExitAsync(processId, Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
            await FinishAsync().ConfigureAwait(false);
        });

        /// <summary>The game is gone: a short settle (its files close), then the live folder's ini goes back. The second caller finds nothing to restore.</summary>
        private async Task FinishAsync()
        {
            Interlocked.CompareExchange(ref owner._current, null, this);
            if (!liveFolder) return;
            await owner._delay(ExitSettle, CancellationToken.None).ConfigureAwait(false);
            LogRestore(owner._ini.Restore(), owner._logger, "after the game exited");
        }
    }
}
