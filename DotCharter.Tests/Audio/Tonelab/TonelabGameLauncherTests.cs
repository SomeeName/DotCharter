using DotCharter.Tone.Audio.Tonelab;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>
///     The public launcher over fakes: start or attach, inject through the helper, RS_ASIO.ini
///     around it, and the quit sequence (quit, a grace, only then the process is ended, then the ini
///     back). A game the user started is never closed.
/// </summary>
public sealed class TonelabGameLauncherTests
{
    private const string GameDir = @"C:\Games\Rocksmith2014";

    private static readonly byte[] UserIni = [0xEF, 0xBB, 0xBF, .. "[Config]\r\nEnableAsio=1\r\n[Asio.Output]\r\nDriver=Focusrite USB ASIO\r\n"u8.ToArray()];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MemoryTonelabFileSystem _files = new();
    // Upper bound for waits that only matter when the test is already failing: a loaded gate can starve the pool
    // for seconds, and EndStartedGameOnExit gives up (without the quit) when its cap elapses first.
    private static readonly TimeSpan NeverFires = TimeSpan.FromSeconds(60);

    private readonly FakeAppLogger _logger = new();

    private string IniPath => Path.Combine(GameDir, RsAsioIniGuard.IniName);

    private string BackupPath => Path.Combine(GameDir, RsAsioIniGuard.BackupName);

    private TonelabGameLauncher Launcher(FakeRocksmith game, FakeHelperRunner? runner = null, IRocksmithStarter? mirror = null)
    {
        _files.WriteAllBytes(IniPath, UserIni);
        IReadOnlyList<IRocksmithStarter> starters = mirror is null ? [game] : [mirror, game];
        return new TonelabGameLauncher(new TonelabInjectHelper(@"C:\DC\tools\tonelab\tonelab-launch.exe", @"C:\DC\tools\tonelab\tonelab.dll",
                runner ?? FakeHelperRunner.Ok()), starters, game, new RsAsioIniGuard(_files, () => game.Running, GameDir), _logger,
            (_, _) => Task.CompletedTask)
        {
            QuitGrace = TimeSpan.FromMilliseconds(200)
        };
    }

    private void AssertIniIsTheUsers()
    {
        Assert.True(SpinWait.SpinUntil(() => !_files.Exists(BackupPath), NeverFires), "the backup was never put back");
        Assert.Equal(UserIni, _files.ReadAllBytes(IniPath));
    }

    [Fact]
    public async Task NoGameRunning_BacksUpTheIni_StartsTheGame_AndInjectsItsPid()
    {
        var game = new FakeRocksmith();
        var runner = FakeHelperRunner.Ok();
        var launcher = Launcher(game, runner);

        var launch = await launcher.LaunchAsync(Ct);

        Assert.NotNull(launch);
        Assert.Equal(1, game.Starts);
        Assert.Equal(game.Pid, launch.ProcessId);
        Assert.True(launch.StartedByApp);
        Assert.True(launch.GameOpensNoAudioDevice);
        Assert.Same(launch, launcher.Current);
        Assert.Equal(["--inject", "4242", "--dll", @"C:\DC\tools\tonelab\tonelab.dll"], Assert.Single(runner.Calls).Arguments);
        Assert.Equal(RsAsioIniGuard.DriverlessIni, _files.ReadAllBytes(IniPath));
        Assert.Equal(UserIni, _files.ReadAllBytes(BackupPath));
    }

    [Fact]
    public async Task QuitHonoured_EndsNothing_AndTheIniComesBackByteIdentical()
    {
        var game = new FakeRocksmith { HonoursQuit = true };
        var launch = (await Launcher(game).LaunchAsync(Ct))!;

        await launch.EndAsync(game.SendQuit, Ct);

        Assert.False(game.Running);
        Assert.Equal(0, game.Kills);
        AssertIniIsTheUsers();
    }

    [Fact]
    public async Task QuitIgnored_TheGameIsEndedAfterTheGrace_ThenTheIniComesBack()
    {
        var game = new FakeRocksmith { HonoursQuit = false };
        var launcher = Launcher(game);
        var launch = (await launcher.LaunchAsync(Ct))!;

        var started = DateTime.UtcNow;
        await launch.EndAsync(game.SendQuit, Ct);

        Assert.Equal(1, game.Kills);
        Assert.True(DateTime.UtcNow - started >= launcher.QuitGrace - TimeSpan.FromMilliseconds(50), "ended before the grace was over");
        AssertIniIsTheUsers();
        Assert.Null(launcher.Current);
    }

    [Fact]
    public async Task QuitThatCannotBeSent_EndsTheGameItStarted()
    {
        var game = new FakeRocksmith();
        var launch = (await Launcher(game).LaunchAsync(Ct))!;

        await launch.EndAsync(_ => Task.FromResult(false), Ct);

        Assert.Equal(1, game.Kills);
        AssertIniIsTheUsers();
    }

    [Fact]
    public async Task AGameTheUserStarted_IsInjected_TheIniUntouched_AndItIsNeverClosed()
    {
        var game = new FakeRocksmith();
        game.RunByUser();
        var launcher = Launcher(game);

        var launch = (await launcher.LaunchAsync(Ct))!;
        Assert.Equal(0, game.Starts);
        Assert.False(launch.StartedByApp);
        Assert.False(launch.GameOpensNoAudioDevice);   // it holds RS ASIO's device
        Assert.Null(launcher.Current);
        Assert.Equal(UserIni, _files.ReadAllBytes(IniPath));
        Assert.False(_files.Exists(BackupPath));

        var quitSent = false;
        await launch.EndAsync(_ => Task.FromResult(quitSent = true), Ct);
        Assert.False(quitSent);
        Assert.Equal(0, game.Kills);
        Assert.True(game.Running);
    }

    [Fact]
    public async Task AGameThatExitsByItself_GetsTheIniBack()
    {
        var game = new FakeRocksmith();
        var launcher = Launcher(game);
        Assert.NotNull(await launcher.LaunchAsync(Ct));

        game.Exit();   // the user closed it, or it crashed

        AssertIniIsTheUsers();
        Assert.True(SpinWait.SpinUntil(() => launcher.Current is null, NeverFires));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AStartThatFails_PutsTheIniBack_AndLaunchesNothing(bool startWorks, bool showsProcess)
    {
        var game = new FakeRocksmith { StartWorks = startWorks, ShowsProcess = showsProcess };
        var launcher = Launcher(game);

        Assert.Null(await launcher.LaunchAsync(Ct));
        AssertIniIsTheUsers();
        Assert.Null(launcher.Current);
    }

    [Fact]
    public async Task AFailedInjection_EndsTheGameItWasFor_AndPutsTheIniBack()
    {
        var game = new FakeRocksmith();
        var launcher = Launcher(game, new FakeHelperRunner(new HelperProcessRun(8, "err inject-failed", false)));

        Assert.Null(await launcher.LaunchAsync(Ct));
        Assert.Equal(1, game.Kills);
        AssertIniIsTheUsers();
        Assert.Contains(_logger.Entries, e => e.Message.Contains("inject-failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedInjection_IntoTheUsersGame_LeavesItRunning()
    {
        var game = new FakeRocksmith();
        game.RunByUser();
        var launcher = Launcher(game, new FakeHelperRunner(new HelperProcessRun(4, "err not-rocksmith", false)));

        Assert.Null(await launcher.LaunchAsync(Ct));
        Assert.Equal(0, game.Kills);
        Assert.True(game.Running);
    }

    [Fact]
    public async Task TheSessionsQuit_DrivesTheWholeSequence_ThroughTheLaunch()
    {
        using var mock = new TonelabGameMock();
        var game = new FakeRocksmith { Pid = mock.ProcessId, HonoursQuit = false };
        var launcher = Launcher(game);
        using var session = new TonelabSession(() => [], null, null, TonelabSessionTests.Quick) { Launcher = launcher };

        Assert.Equal(TonelabSessionState.Ready, await session.LaunchAsync(Ct));
        Assert.False(session.GameHoldsAudioDevice);
        Assert.True((await session.QuitAsync(Ct)).IsOk);

        Assert.Equal("quit", mock.Commands()[^1]);
        Assert.Equal(1, game.Kills);   // the mock answers ok but does not exit
        AssertIniIsTheUsers();
    }

    [Fact]
    public async Task TheMirrorStart_InjectsItsPidAtOnce_LeavesTheLiveIniAlone_AndRestoresNothing()
    {
        var game = new FakeRocksmith { Pid = 5150 };
        var mirror = new FakeMirrorStarter(RocksmithStart.Running(5150), game);
        var runner = FakeHelperRunner.Ok();
        var launcher = Launcher(game, runner, mirror);

        var launch = await launcher.LaunchAsync(Ct);

        Assert.NotNull(launch);
        Assert.Equal(1, mirror.Starts);
        Assert.Equal(0, game.Starts);   // Steam is never asked
        Assert.Equal(["--inject", "5150", "--dll", @"C:\DC\tools\tonelab\tonelab.dll"], Assert.Single(runner.Calls).Arguments);
        Assert.True(launch.GameOpensNoAudioDevice);
        Assert.Equal(UserIni, _files.ReadAllBytes(IniPath));
        Assert.False(_files.Exists(BackupPath));

        await launch.EndAsync(game.SendQuit, Ct);
        Assert.Equal(0, game.Kills);
        Assert.Equal(UserIni, _files.ReadAllBytes(IniPath));
        Assert.Equal(GameToneLaunchFailure.None, launcher.LastFailure);
    }

    [Fact]
    public async Task NoMirrorPossible_FallsBackToSteam_WithTheIniSwap()
    {
        var game = new FakeRocksmith();
        var mirror = new FakeMirrorStarter(RocksmithStart.NotHere("CrossVolume"), game);
        var launcher = Launcher(game, mirror: mirror);

        var launch = await launcher.LaunchAsync(Ct);

        Assert.NotNull(launch);
        Assert.Equal(1, game.Starts);
        Assert.Equal(RsAsioIniGuard.DriverlessIni, _files.ReadAllBytes(IniPath));
        game.Exit();
        AssertIniIsTheUsers();
    }

    [Fact]
    public async Task SteamNotRunning_RefusesTheLaunchWithThatReason_AndDoesNotFallBack()
    {
        var game = new FakeRocksmith();
        var mirror = new FakeMirrorStarter(RocksmithStart.Refused(GameToneLaunchFailure.SteamNotRunning, "Steam is not running"), game);
        var runner = FakeHelperRunner.Ok();
        var launcher = Launcher(game, runner, mirror);

        Assert.Null(await launcher.LaunchAsync(Ct));

        Assert.Equal(GameToneLaunchFailure.SteamNotRunning, launcher.LastFailure);
        Assert.Equal(0, game.Starts);
        Assert.Empty(runner.Calls);
        Assert.Equal(UserIni, _files.ReadAllBytes(IniPath));
        Assert.Contains(_logger.Entries, e => e.Message.Contains("SteamNotRunning", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAppsLauncher_TriesTheMirrorFirst_AndSteamOnlyAsTheFallback()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the mirror is Windows-only");
        var launcher = TonelabGameLauncher.Create(new TonelabInstall(@"C:\DC\tools\tonelab\tonelab-launch.exe", @"C:\DC\tools\tonelab\tonelab.dll"),
            _logger);

        Assert.Collection(launcher.Starters,
            first => Assert.False(Assert.IsType<MirrorRocksmithStarter>(first).UsesLiveGameFolder),
            last => Assert.True(Assert.IsType<SteamRocksmithStarter>(last).UsesLiveGameFolder));
        Assert.Equal(TimeSpan.FromSeconds(10), launcher.QuitGrace);
        Assert.True(TonelabGameLauncher.ExitCap > launcher.QuitGrace, "an app exit must outlast the quit grace");
    }

    [Fact]
    public void Registration_NeedsBothFilesUnderToolsTonelab()
    {
        var root = @"C:\DC";
        var helper = Path.Combine(root, "tools", "tonelab", "tonelab-launch.exe");
        var dll = Path.Combine(root, "tools", "tonelab", "tonelab.dll");

        var both = new ServiceCollection();
        both.AddSingleton<IAppLogger>(_logger);
        Assert.True(TonelabGameLauncher.Register(both, root, p => p == helper || p == dll));
        using var provider = both.BuildServiceProvider();
        Assert.IsType<TonelabGameLauncher>(Assert.Single(provider.GetServices<IGameToneLauncher>()));

        foreach (var only in new[] { helper, dll })
        {
            var services = new ServiceCollection();
            Assert.False(TonelabGameLauncher.Register(services, root, p => p == only));
            Assert.Empty(services);
        }
    }

    [Fact]
    public void TheAppRegistersIt_RestoresAtStartup_AndEndsItsGameOnBothExitPaths()
    {
        static string Read(params string[] parts) => SourceText.StripCommentLines(File.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. parts])));
        var dialogs = Read("DotCharter.Presentation", "Dialogs", "DialogsModule.cs");
        Assert.Contains("TonelabGameLauncher.Register(services, DotCharter.Tone.Infrastructure.Storage.InstallLocation.InstallRoot)",
            dialogs, StringComparison.Ordinal);
        Assert.Contains("sp.GetServices<DotCharter.Tone.Audio.Tonelab.IGameToneLauncher>()", dialogs, StringComparison.Ordinal);
        Assert.Contains("TonelabGameLauncher.RestoreLeftIniAtStartup(", Read("DotCharter.UI", "App.axaml.cs"), StringComparison.Ordinal);
        // The window's close runs before BOTH exit paths (TerminateNow and the clean shutdown).
        var close = Read("DotCharter.UI", "Views", "MainWindow.axaml.cs");
        Assert.Contains("TonelabGameLauncher.EndStartedGameOnExit(CompositionRoot())", close, StringComparison.Ordinal);
        Assert.True(close.IndexOf("TonelabGameLauncher.EndStartedGameOnExit(", StringComparison.Ordinal)
            < close.IndexOf("App.TryBeginCleanShutdown()", StringComparison.Ordinal), "the game must end before the exit path is chosen");
    }

    [Fact]
    public async Task OnExit_OnlyAGameTheAppStarted_IsEnded_AndTheSessionIsNotBuiltForNothing()
    {
        var idle = Launcher(new FakeRocksmith());
        var built = 0;
        TonelabGameLauncher.EndStartedGameOnExit([idle], () =>
        {
            built++;
            return null;
        }, NeverFires, _logger);
        Assert.Equal(0, built);

        var game = new FakeRocksmith();
        var launcher = Launcher(game);
        await launcher.LaunchAsync(Ct);
        var session = new FakeTonelabSession();
        TonelabGameLauncher.EndStartedGameOnExit([launcher], () => session, NeverFires, _logger);
        Assert.Equal(1, session.Quits);
    }
}
