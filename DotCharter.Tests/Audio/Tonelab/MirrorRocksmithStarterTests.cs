using System.Collections;
using System.Runtime.Versioning;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>
///     The direct start over fakes: the mirror first, then <c>CreateProcess</c> on the mirror's exe,
///     hidden, in the mirror, with Steam's app id in the environment; refused by name when Steam is
///     not running; unavailable (so the launcher falls back) when no mirror is possible.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MirrorRocksmithStarterTests
{
    private const string Game = @"C:\Steam\steamapps\common\Rocksmith2014";
    private const string Mirror = @"C:\Users\u\AppData\Local\tonelab\game";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MemoryGameMirrorFileSystem _disk = new();
    private readonly FakeProcessCreator _creator = new();

    public MirrorRocksmithStarterTests()
    {
        foreach (var name in TonelabGameMirror.LinkedFiles) _disk.Put(Path.Combine(Game, name));
        foreach (var dir in TonelabGameMirror.Junctions) _disk.PutDirectory(Path.Combine(Game, dir));
    }

    private MirrorRocksmithStarter Starter(bool steamRunning = true, string mirrorRoot = Mirror) =>
        new(new TonelabGameMirror(_disk, mirrorRoot), () => Game, () => steamRunning, _creator,
            () => new Hashtable { ["PATH"] = @"C:\Windows", ["SteamAppId"] = "1" });

    [Fact]
    public async Task ItStartsTheMirrorsExe_Hidden_InTheMirror_WithSteamsAppId()
    {
        var starter = Starter();

        var start = await starter.StartAsync(Ct);

        Assert.Equal(RocksmithStartStatus.Started, start.Status);
        Assert.Equal(FakeProcessCreator.Pid, start.ProcessId);
        Assert.False(starter.UsesLiveGameFolder);
        var request = Assert.Single(_creator.Requests);
        Assert.Equal(Path.Combine(Mirror, "Rocksmith2014.exe"), request.Exe);
        Assert.Equal(Mirror, request.WorkingDirectory);
        Assert.True(request.Hidden);
        Assert.Equal("221680", request.Environment["SteamAppId"]);
        Assert.Equal("221680", request.Environment["SteamGameId"]);
        Assert.Equal(@"C:\Windows", request.Environment["PATH"]);
        Assert.True(_disk.FileExists(Path.Combine(Mirror, "RS_ASIO.ini")), "the mirror is brought up to date before the start");
    }

    [Fact]
    public async Task WithoutSteam_TheStartIsRefused_WithThatReason_AndNothingStarts()
    {
        var start = await Starter(steamRunning: false).StartAsync(Ct);

        Assert.Equal(RocksmithStartStatus.Failed, start.Status);
        Assert.Equal(GameToneLaunchFailure.SteamNotRunning, start.Failure);
        Assert.Contains("Steam", start.Detail, StringComparison.Ordinal);
        Assert.Empty(_creator.Requests);
    }

    [Fact]
    public async Task NoMirrorPossible_IsUnavailable_SoTheLauncherFallsBack()
    {
        _disk.Unwritable.Add(@"C:\tonelab-game");
        var start = await Starter(mirrorRoot: @"E:\Local\tonelab\game").StartAsync(Ct);

        Assert.Equal(RocksmithStartStatus.Unavailable, start.Status);
        Assert.Empty(_creator.Requests);
    }

    [Fact]
    public async Task AProcessThatCannotBeCreated_IsAFailedStart()
    {
        _creator.Error = "Access is denied.";
        var start = await Starter().StartAsync(Ct);

        Assert.Equal(GameToneLaunchFailure.StartFailed, start.Failure);
        Assert.Equal("Access is denied.", start.Detail);
    }

    [Fact]
    public void Hidden_IsStartfUseShowWindow_WithSwHide()
    {
        var hidden = Win32GameProcessCreator.StartupFor(true);
        Assert.Equal(Win32GameProcessCreator.StartfUseShowWindow, hidden.Flags);
        Assert.Equal(Win32GameProcessCreator.SwHide, hidden.ShowWindow);
        Assert.Equal((uint)(IntPtr.Size == 8 ? 104 : 68), hidden.Size);
        Assert.Equal(0u, Win32GameProcessCreator.StartupFor(false).Flags);
    }

    [Fact]
    public void TheEnvironmentBlock_IsSortedNameValuePairs_EndingInTwoNuls()
    {
        var block = new string(Win32GameProcessCreator.EnvironmentBlock(new Dictionary<string, string>
        {
            ["steamGameId"] = "221680",
            ["Path"] = @"C:\x",
            ["SteamAppId"] = "221680"
        }));

        Assert.Equal("Path=C:\\x\0SteamAppId=221680\0steamGameId=221680\0\0", block);
    }

    private sealed class FakeProcessCreator : IGameProcessCreator
    {
        public const int Pid = 777;

        public List<GameProcessRequest> Requests { get; } = [];

        public string? Error { get; set; }

        public GameProcessCreated Create(GameProcessRequest request)
        {
            Requests.Add(request);
            return Error is null ? new GameProcessCreated(Pid, "") : new GameProcessCreated(null, Error);
        }
    }
}
