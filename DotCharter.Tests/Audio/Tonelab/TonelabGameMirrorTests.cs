using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>
///     The tone-server folder's upkeep over an in-memory disk (tonelab's HANDOVER, "The tone-server
///     folder"): links, relinks after a game update, psarc churn, junctions kept, the left-out
///     files, the real files only ever created new, and the cross-volume decision.
/// </summary>
public sealed class TonelabGameMirrorTests
{
    private const string Game = @"D:\Steam\steamapps\common\Rocksmith2014";
    private const string Mirror = @"C:\Users\u\AppData\Local\tonelab\game";

    private readonly MemoryGameMirrorFileSystem _disk = new();

    public TonelabGameMirrorTests()
    {
        foreach (var name in TonelabGameMirror.LinkedFiles) _disk.Put(Path.Combine(Game, name));
        foreach (var name in new[] { "pack.psarc", "cache.psarc", "dinput8.dll", "xinput1_3.dll", "D3DX9_42.dll", "RS_ASIO.ini", "RS_ASIO-log.txt" })
            _disk.Put(Path.Combine(Game, name));
        _disk.Put(Path.Combine(Game, "Rocksmith.ini"), "[Audio]\r\nLatencyBuffer=4\r\n");
        foreach (var dir in TonelabGameMirror.Junctions.Append("dlc")) _disk.PutDirectory(Path.Combine(Game, dir));
    }

    private static string In(string root, string name) => Path.Combine(root, name);

    /// <summary>Same volume for game and mirror (both on D:), the common case.</summary>
    private TonelabGameMirror SameVolume(out string root)
    {
        root = @"D:\Users\u\AppData\Local\tonelab\game";
        return new TonelabGameMirror(_disk, root);
    }

    [Fact]
    public void AFreshMirror_LinksTheGameFiles_JunctionsTheFolders_AndOwnsItsInis()
    {
        var result = SameVolume(out var root).Prepare(Game);

        Assert.Equal(GameMirrorOutcome.Ready, result.Outcome);
        Assert.Equal(root, result.Root);
        foreach (var name in TonelabGameMirror.LinkedFiles.Concat(["pack.psarc", "cache.psarc"]))
            Assert.Equal(_disk.FileId(In(Game, name)), _disk.FileId(In(root, name)));
        foreach (var name in TonelabGameMirror.Junctions) Assert.Equal(In(Game, name), _disk.JunctionTarget(In(root, name)));
        Assert.Equal("[Audio]\r\nLatencyBuffer=4\r\n", _disk.Text(In(root, "Rocksmith.ini")));
        Assert.NotEqual(_disk.FileId(In(Game, "Rocksmith.ini")), _disk.FileId(In(root, "Rocksmith.ini")));
        Assert.Equal(TonelabGameMirror.DriverlessIni, _disk.Node(In(root, "RS_ASIO.ini")).Bytes);
        Assert.Contains("EnableAsio=0", _disk.Text(In(root, "RS_ASIO.ini")), StringComparison.Ordinal);
        Assert.True(_disk.DirectoryExists(In(root, "Tmp")));
    }

    [Fact]
    public void TheLeftOutFiles_NeverReachTheMirror_AndStraysAreRemoved()
    {
        var mirror = SameVolume(out var root);
        _disk.Put(In(root, "dinput8.dll"));
        _disk.CreateJunction(In(root, "dlc"), In(Game, "dlc"));

        Assert.Equal(GameMirrorOutcome.Ready, mirror.Prepare(Game).Outcome);

        foreach (var name in TonelabGameMirror.LeftOut)
        {
            Assert.False(_disk.FileExists(In(root, name)), name);
            Assert.False(_disk.DirectoryExists(In(root, name)), name);
        }

        Assert.False(_disk.FileExists(In(root, "RS_ASIO-log.txt")));
        Assert.True(_disk.DirectoryExists(In(Game, "dlc")), "only the junction goes, never its target");
    }

    [Fact]
    public void AGameUpdate_BreaksALink_AndTheNextStartRelinksIt()
    {
        var mirror = SameVolume(out var root);
        mirror.Prepare(Game);
        _disk.Put(In(Game, TonelabGameMirror.ExeName), "updated exe");   // Steam writes a NEW file
        Assert.NotEqual(_disk.FileId(In(Game, TonelabGameMirror.ExeName)), _disk.FileId(In(root, TonelabGameMirror.ExeName)));
        _disk.Log.Clear();

        Assert.Equal(GameMirrorOutcome.Ready, mirror.Prepare(Game).Outcome);

        Assert.Equal(_disk.FileId(In(Game, TonelabGameMirror.ExeName)), _disk.FileId(In(root, TonelabGameMirror.ExeName)));
        Assert.Equal(["delete " + In(root, TonelabGameMirror.ExeName), "link " + In(root, TonelabGameMirror.ExeName)],
            _disk.Log.Where(l => l.EndsWith(TonelabGameMirror.ExeName, StringComparison.Ordinal)));
        Assert.DoesNotContain(_disk.Log, l => l.StartsWith("link ", StringComparison.Ordinal) && !l.EndsWith(TonelabGameMirror.ExeName, StringComparison.Ordinal));
    }

    [Fact]
    public void ANewPsarc_IsLinked_AndAVanishedOne_LeavesTheMirror()
    {
        var mirror = SameVolume(out var root);
        mirror.Prepare(Game);
        _disk.Put(In(Game, "new_song.psarc"));
        _disk.DeleteFile(In(Game, "cache.psarc"));

        mirror.Prepare(Game);

        Assert.Equal(_disk.FileId(In(Game, "new_song.psarc")), _disk.FileId(In(root, "new_song.psarc")));
        Assert.False(_disk.FileExists(In(root, "cache.psarc")));
        Assert.True(_disk.FileExists(In(root, "pack.psarc")));
    }

    [Fact]
    public void AJunctionInPlace_IsKept_AndAWrongOneIsReplaced_NeverRecursively()
    {
        var mirror = SameVolume(out var root);
        mirror.Prepare(Game);
        _disk.RemoveJunction(In(root, "venues"));
        _disk.CreateJunction(In(root, "venues"), @"D:\elsewhere\venues");
        _disk.Log.Clear();

        mirror.Prepare(Game);

        Assert.Equal(["unjunction " + In(root, "venues"), "junction " + In(root, "venues")], _disk.Log.Where(l => l.Contains("junction", StringComparison.Ordinal)));
        Assert.Equal(In(Game, "venues"), _disk.JunctionTarget(In(root, "venues")));
    }

    [Fact]
    public void ARealFolderWhereAJunctionBelongs_FailsTheUpkeep_AndIsLeftAlone()
    {
        var mirror = SameVolume(out var root);
        _disk.PutDirectory(In(root, "base"));
        _disk.Put(In(In(root, "base"), "keep.bin"));

        var result = mirror.Prepare(Game);

        Assert.Equal(GameMirrorOutcome.Failed, result.Outcome);
        Assert.True(_disk.FileExists(In(In(root, "base"), "keep.bin")));
    }

    [Fact]
    public void TheMirrorsOwnFiles_AreCreatedNew_NeverWrittenThroughALinkedName()
    {
        var mirror = SameVolume(out var root);
        _disk.CreateDirectory(root);
        // A broken earlier mirror: its Rocksmith.ini and RS_ASIO.ini are hardlinks of the game's.
        _disk.CreateHardLink(In(root, "Rocksmith.ini"), In(Game, "Rocksmith.ini"));
        _disk.CreateHardLink(In(root, "RS_ASIO.ini"), In(Game, "RS_ASIO.ini"));
        var gameIni = _disk.Node(In(Game, "RS_ASIO.ini")).Bytes.ToArray();

        Assert.Equal(GameMirrorOutcome.Ready, mirror.Prepare(Game).Outcome);

        Assert.Equal(gameIni, _disk.Node(In(Game, "RS_ASIO.ini")).Bytes);
        Assert.NotEqual(_disk.FileId(In(Game, "RS_ASIO.ini")), _disk.FileId(In(root, "RS_ASIO.ini")));
        Assert.NotEqual(_disk.FileId(In(Game, "Rocksmith.ini")), _disk.FileId(In(root, "Rocksmith.ini")));
    }

    [Fact]
    public void AGameOnAnotherVolume_MirrorsOnTheGamesVolume_WhenItIsWritable()
    {
        var result = new TonelabGameMirror(_disk, Mirror).Prepare(Game);

        Assert.Equal(GameMirrorOutcome.Ready, result.Outcome);
        Assert.Equal(@"D:\tonelab-game", result.Root);
        Assert.Equal(_disk.FileId(In(Game, TonelabGameMirror.ExeName)), _disk.FileId(@"D:\tonelab-game\Rocksmith2014.exe"));
        Assert.DoesNotContain(_disk.Log, l => l.StartsWith("link C:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AGameOnAnotherVolume_ThatIsNotWritable_MeansNoMirror_TheFallbackDecision()
    {
        _disk.Unwritable.Add(@"D:\tonelab-game");

        var result = new TonelabGameMirror(_disk, Mirror).Prepare(Game);

        Assert.Equal(GameMirrorOutcome.CrossVolume, result.Outcome);
        Assert.Null(result.Root);
        Assert.DoesNotContain(_disk.Log, l => l.StartsWith("link ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(@"D:\NotTheGame")]
    public void NoGameFolder_IsNoGame(string? folder) =>
        Assert.Equal(GameMirrorOutcome.NoGame, new TonelabGameMirror(_disk, Mirror).Prepare(folder).Outcome);

    [Fact]
    public void TheDefaultRoot_FollowsTheDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dc-tonelab-mirror-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (DotCharter.Tone.Infrastructure.Storage.UserDataFolders.UseDataRootForTesting(root))
                Assert.Equal(Path.Combine(root, "Local", "tonelab", "game"), TonelabGameMirror.DefaultRoot);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
