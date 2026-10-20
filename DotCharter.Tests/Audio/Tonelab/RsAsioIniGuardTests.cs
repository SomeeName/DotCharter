using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>
///     The RS_ASIO.ini state machine, tonelab's rules: back up first, then the driverless ini; back
///     after the exit and at every start while a backup is left; never while the game runs; the
///     restore byte-identical. On a temp folder over the real disk, never a game folder.
/// </summary>
public sealed class RsAsioIniGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dotcharter-rsasio-" + Guid.NewGuid().ToString("N"));
    private bool _gameRunning;

    public RsAsioIniGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Ini => Path.Combine(_dir, RsAsioIniGuard.IniName);

    private string Backup => Path.Combine(_dir, RsAsioIniGuard.BackupName);

    private RsAsioIniGuard Guard() => new(PhysicalTonelabFileSystem.Instance, () => _gameRunning, _dir);

    /// <summary>A BOM, CRLF and LF mixed, a trailing byte without a newline: anything a text round trip would change.</summary>
    private static byte[] Original()
    {
        var bytes = new byte[512];
        new Random(221680).NextBytes(bytes);
        return [0xEF, 0xBB, 0xBF, .. "[Config]\r\nEnableAsio=1\n"u8.ToArray(), .. bytes];
    }

    [Fact]
    public void Apply_BacksUpFirst_ThenWritesTheDriverlessIni_AndRestore_IsByteIdentical()
    {
        var original = Original();
        File.WriteAllBytes(Ini, original);
        var guard = Guard();

        Assert.Equal(RsAsioIniOutcome.Applied, guard.Apply());
        Assert.Equal(original, File.ReadAllBytes(Backup));
        Assert.Equal(RsAsioIniGuard.DriverlessIni, File.ReadAllBytes(Ini));
        Assert.True(guard.HasBackup);

        Assert.Equal(RsAsioIniOutcome.Restored, guard.Restore());
        Assert.Equal(original, File.ReadAllBytes(Ini));
        Assert.False(File.Exists(Backup));
        Assert.Equal(RsAsioIniOutcome.NothingToRestore, guard.Restore());
    }

    [Fact]
    public void ABackupACrashLeft_IsRestoredAtTheNextStart_ByteIdentical()
    {
        var original = Original();
        File.WriteAllBytes(Ini, original);
        Assert.Equal(RsAsioIniOutcome.Applied, Guard().Apply());   // the session that then crashed

        Assert.Equal(RsAsioIniOutcome.Restored, Guard().Restore());   // a fresh app start
        Assert.Equal(original, File.ReadAllBytes(Ini));
        Assert.False(File.Exists(Backup));
    }

    [Fact]
    public void ApplyWithACrashLeftBackup_PutsItBackFirst_SoTheBackupStaysTheUsersOriginal()
    {
        var original = Original();
        File.WriteAllBytes(Ini, original);
        Assert.Equal(RsAsioIniOutcome.Applied, Guard().Apply());

        Assert.Equal(RsAsioIniOutcome.Applied, Guard().Apply());   // never backs up the driverless ini
        Assert.Equal(original, File.ReadAllBytes(Backup));
        Assert.Equal(RsAsioIniOutcome.Restored, Guard().Restore());
        Assert.Equal(original, File.ReadAllBytes(Ini));
    }

    [Fact]
    public void WhileTheGameRuns_NothingIsChanged_EitherWay()
    {
        var original = Original();
        File.WriteAllBytes(Ini, original);
        _gameRunning = true;
        Assert.Equal(RsAsioIniOutcome.RefusedGameRunning, Guard().Apply());
        Assert.Equal(original, File.ReadAllBytes(Ini));
        Assert.False(File.Exists(Backup));

        _gameRunning = false;
        Assert.Equal(RsAsioIniOutcome.Applied, Guard().Apply());
        _gameRunning = true;
        Assert.Equal(RsAsioIniOutcome.RefusedGameRunning, Guard().Restore());
        Assert.Equal(RsAsioIniGuard.DriverlessIni, File.ReadAllBytes(Ini));
        Assert.True(File.Exists(Backup));   // kept for after the exit, or the next start

        _gameRunning = false;
        Assert.Equal(RsAsioIniOutcome.Restored, Guard().Restore());
        Assert.Equal(original, File.ReadAllBytes(Ini));
    }

    [Fact]
    public void WithoutRsAsio_OrWithoutAGameFolder_NothingIsWritten()
    {
        Assert.Equal(RsAsioIniOutcome.NoIni, Guard().Apply());
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Equal(RsAsioIniOutcome.NoGameFolder, new RsAsioIniGuard(PhysicalTonelabFileSystem.Instance, () => false, null).Apply());
        Assert.Equal(RsAsioIniOutcome.NoGameFolder, new RsAsioIniGuard(PhysicalTonelabFileSystem.Instance, () => false, " ").Restore());
    }

    [Fact]
    public void AFailedDriverlessWrite_PutsTheOriginalBackAtOnce()
    {
        var files = new MemoryTonelabFileSystem();
        var guard = new RsAsioIniGuard(files, () => false, @"C:\Game");
        var original = Original();
        files.WriteAllBytes(guard.IniPath!, original);
        files.FailWrite = (path, bytes) => path == guard.IniPath && bytes.SequenceEqual(RsAsioIniGuard.DriverlessIni);

        Assert.Equal(RsAsioIniOutcome.Failed, guard.Apply());
        Assert.Equal(original, files.ReadAllBytes(guard.IniPath!));
        Assert.False(files.Exists(guard.BackupPath!));
    }

    [Fact]
    public void AFailedBackup_LeavesTheIniAlone()
    {
        var files = new MemoryTonelabFileSystem();
        var guard = new RsAsioIniGuard(files, () => false, @"C:\Game");
        var original = Original();
        files.WriteAllBytes(guard.IniPath!, original);
        files.FailWrite = (path, _) => path == guard.BackupPath;

        Assert.Equal(RsAsioIniOutcome.Failed, guard.Apply());
        Assert.Equal(original, files.ReadAllBytes(guard.IniPath!));
    }

    [Fact]
    public void AFailedRestore_KeepsTheBackup_ForTheNextTry()
    {
        var files = new MemoryTonelabFileSystem();
        var guard = new RsAsioIniGuard(files, () => false, @"C:\Game");
        var original = Original();
        files.WriteAllBytes(guard.IniPath!, original);
        Assert.Equal(RsAsioIniOutcome.Applied, guard.Apply());
        files.FailWrite = (path, _) => path == guard.IniPath;

        Assert.Equal(RsAsioIniOutcome.Failed, guard.Restore());
        Assert.True(files.Exists(guard.BackupPath!));
        files.FailWrite = null;
        Assert.Equal(RsAsioIniOutcome.Restored, guard.Restore());
        Assert.Equal(original, files.ReadAllBytes(guard.IniPath!));
    }

    [Fact]
    public void TheDriverlessIni_TurnsEveryDriverOff()
    {
        var text = System.Text.Encoding.ASCII.GetString(RsAsioIniGuard.DriverlessIni);
        Assert.Contains("EnableAsio=0\r\n", text, StringComparison.Ordinal);
        Assert.Contains("EnableWasapiOutputs=0\r\n", text, StringComparison.Ordinal);
        Assert.Contains("EnableWasapiInputs=0\r\n", text, StringComparison.Ordinal);
        Assert.Equal(4, text.Split("Driver=\r\n").Length - 1);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
