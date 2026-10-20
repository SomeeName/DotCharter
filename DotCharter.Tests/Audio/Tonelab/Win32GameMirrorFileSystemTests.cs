using System.Runtime.Versioning;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>
///     The real Win32 side, in a temp folder only (never a game folder, never the user's mirror):
///     hardlink identity, a relink after the original is replaced, junctions made and removed without
///     touching their target, the whole upkeep on a fake game folder, and a hidden
///     <c>CreateProcessW</c> that carries its environment.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32GameMirrorFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-tonelab-mirror-" + Guid.NewGuid().ToString("N"));
    private readonly Win32GameMirrorFileSystem _fs = Win32GameMirrorFileSystem.Instance;

    public Win32GameMirrorFileSystemTests() => Directory.CreateDirectory(_root);

    private string Game => Path.Combine(_root, "game");

    private string Mirror => Path.Combine(_root, "mirror");

    public void Dispose()
    {
        // Junctions first (RemoveDirectory only), so the recursive cleanup never walks into the game folder twice.
        if (Directory.Exists(Mirror))
        {
            foreach (var dir in Directory.EnumerateDirectories(Mirror))
            {
                if (_fs.JunctionTarget(dir) is not null) _fs.RemoveJunction(dir);
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder this test made.
        }
    }

    private void MakeGame()
    {
        Directory.CreateDirectory(Game);
        foreach (var name in TonelabGameMirror.LinkedFiles.Concat(["pack.psarc", "dinput8.dll"])) File.WriteAllText(Path.Combine(Game, name), name);
        File.WriteAllText(Path.Combine(Game, "Rocksmith.ini"), "[Audio]\r\nLatencyBuffer=4\r\n");
        File.WriteAllText(Path.Combine(Game, "RS_ASIO.ini"), "[Config]\r\nEnableAsio=1\r\n");
        foreach (var dir in TonelabGameMirror.Junctions)
        {
            Directory.CreateDirectory(Path.Combine(Game, dir));
            File.WriteAllText(Path.Combine(Game, dir, "inside.bin"), dir);
        }
    }

    [Fact]
    public void AHardlink_SharesTheOriginalsIdentity_UntilTheOriginalIsReplaced()
    {
        MakeGame();
        Directory.CreateDirectory(Mirror);
        var original = Path.Combine(Game, "Rocksmith2014.exe");
        var link = Path.Combine(Mirror, "Rocksmith2014.exe");

        _fs.CreateHardLink(link, original);
        Assert.NotNull(_fs.FileId(original));
        Assert.Equal(_fs.FileId(original), _fs.FileId(link));

        File.Delete(original);
        File.WriteAllText(original, "updated");
        Assert.NotEqual(_fs.FileId(original), _fs.FileId(link));
        Assert.Null(_fs.FileId(Path.Combine(Mirror, "missing.dll")));
    }

    [Fact]
    public void AJunction_PointsAtItsTarget_AndRemovingItLeavesTheTargetWhole()
    {
        MakeGame();
        Directory.CreateDirectory(Mirror);
        var link = Path.Combine(Mirror, "base");

        _fs.CreateJunction(link, Path.Combine(Game, "base"));
        Assert.Equal(Path.Combine(Game, "base"), _fs.JunctionTarget(link));
        Assert.Equal("base", File.ReadAllText(Path.Combine(link, "inside.bin")));

        _fs.RemoveJunction(link);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(Game, "base", "inside.bin")));
        Assert.Throws<IOException>(() => _fs.RemoveJunction(Path.Combine(Game, "base")));
        Assert.True(Directory.Exists(Path.Combine(Game, "base")), "a real folder is never removed as a junction");
    }

    [Fact]
    public void TheUpkeep_OnTheRealDisk_BuildsTheMirror_AndASecondRunChangesNoLink()
    {
        MakeGame();
        var gameIni = File.ReadAllBytes(Path.Combine(Game, "RS_ASIO.ini"));
        var mirror = new TonelabGameMirror(_fs, Mirror);

        Assert.Equal(GameMirrorOutcome.Ready, mirror.Prepare(Game).Outcome);
        var exeId = _fs.FileId(Path.Combine(Mirror, "Rocksmith2014.exe"));
        Assert.Equal(GameMirrorOutcome.Ready, mirror.Prepare(Game).Outcome);

        Assert.Equal(exeId, _fs.FileId(Path.Combine(Mirror, "Rocksmith2014.exe")));
        Assert.Equal(_fs.FileId(Path.Combine(Game, "pack.psarc")), _fs.FileId(Path.Combine(Mirror, "pack.psarc")));
        foreach (var dir in TonelabGameMirror.Junctions) Assert.Equal(Path.Combine(Game, dir), _fs.JunctionTarget(Path.Combine(Mirror, dir)));
        Assert.False(File.Exists(Path.Combine(Mirror, "dinput8.dll")));
        Assert.Equal(TonelabGameMirror.DriverlessIni, File.ReadAllBytes(Path.Combine(Mirror, "RS_ASIO.ini")));
        Assert.Equal(gameIni, File.ReadAllBytes(Path.Combine(Game, "RS_ASIO.ini")));
        Assert.Equal("[Audio]\r\nLatencyBuffer=4\r\n", File.ReadAllText(Path.Combine(Mirror, "Rocksmith.ini")));
        Assert.True(Directory.Exists(Path.Combine(Mirror, "Tmp")));
    }

    [Fact]
    public void TheMountPointBuffer_HasTheNtSubstituteAndThePrintName()
    {
        var buffer = Win32GameMirrorFileSystem.MountPointBuffer(@"C:\G\base\");
        Assert.Equal(0xA0000003u, BitConverter.ToUInt32(buffer, 0));
        var substituteLength = BitConverter.ToUInt16(buffer, 10);
        Assert.Equal(@"\??\C:\G\base", System.Text.Encoding.Unicode.GetString(buffer, 16, substituteLength));
        Assert.Equal(buffer.Length - 8, BitConverter.ToUInt16(buffer, 4));
    }

    [Fact]
    public void CreateProcess_StartsHidden_InItsFolder_WithTheGivenEnvironment()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var environment = MirrorRocksmithStarter.GameEnvironment(Environment.GetEnvironmentVariables());
        // cmd writes what it sees into its working folder: the relative out.txt proves the folder, the line the environment.
        var created = Win32GameProcessCreator.Instance.Create(new GameProcessRequest(cmd, _root, environment, true,
            "/d /c echo %SteamAppId% %SteamGameId%>out.txt"));

        Assert.True(created.ProcessId is not null, created.Error);
        var output = Path.Combine(_root, "out.txt");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(output) && new FileInfo(output).Length > 0, TimeSpan.FromSeconds(30)), "cmd wrote nothing");
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try
            {
                return File.ReadAllText(output).Trim() == "221680 221680";
            }
            catch (IOException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(10)), "the environment did not reach the child");
    }
}
