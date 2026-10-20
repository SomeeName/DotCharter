using System.Text;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>What one <see cref="RsAsioIniGuard" /> step did.</summary>
public enum RsAsioIniOutcome
{
    /// <summary>The original is backed up and the driverless ini is in place.</summary>
    Applied,

    /// <summary>The backup is copied back byte for byte and deleted.</summary>
    Restored,

    /// <summary>No backup: nothing to put back.</summary>
    NothingToRestore,

    /// <summary>No RS_ASIO.ini in the game folder: RS ASIO is not installed, so the game takes no ASIO device anyway.</summary>
    NoIni,

    /// <summary>The game folder is unknown (Rocksmith is not installed through Steam).</summary>
    NoGameFolder,

    /// <summary>The game is running: RS_ASIO.ini is only ever changed while it is closed.</summary>
    RefusedGameRunning,

    /// <summary>A file operation failed; the backup (if any) is kept for the next try.</summary>
    Failed
}

/// <summary>The few file operations the guard needs, so tests run it over a temp folder or memory.</summary>
public interface ITonelabFileSystem
{
    bool Exists(string path);

    byte[] ReadAllBytes(string path);

    void WriteAllBytes(string path, byte[] bytes);

    void Delete(string path);
}

/// <summary>The real disk.</summary>
public sealed class PhysicalTonelabFileSystem : ITonelabFileSystem
{
    public static PhysicalTonelabFileSystem Instance { get; } = new();

    public bool Exists(string path) => File.Exists(path);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

    public void Delete(string path) => File.Delete(path);
}

/// <summary>
///     RS_ASIO.ini for a tone-server session, by tonelab's rules (its HANDOVER, "Without RS ASIO
///     drivers"): back the file up byte for byte FIRST (<c>RS_ASIO.ini.tonelab-backup</c>, the name
///     tonelab's own launcher uses, so either one cleans up after the other), then write the
///     driverless ini; put the backup back after the game exits, and at every start while one is
///     left (a crash); NEVER change the file while the game runs. Pure over
///     <see cref="ITonelabFileSystem" /> and the "is the game running" probe; thread-safe.
/// </summary>
public sealed class RsAsioIniGuard
{
    public const string IniName = "RS_ASIO.ini";
    public const string BackupName = "RS_ASIO.ini.tonelab-backup";

    /// <summary>
    ///     The driverless ini (tonelab's): no WASAPI endpoints, ASIO off, every driver empty. CRLF, as RS
    ///     ASIO writes its own.
    /// </summary>
    public static readonly byte[] DriverlessIni = Encoding.ASCII.GetBytes(
        "; written by DotCharter for a tonelab tone-server session; the original is RS_ASIO.ini.tonelab-backup\r\n" +
        "; and is restored when the game exits (or at the next DotCharter start)\r\n" +
        "[Config]\r\n" +
        "EnableWasapiOutputs=0\r\n" +
        "EnableWasapiInputs=0\r\n" +
        "EnableAsio=0\r\n" +
        "\r\n" +
        "[Asio.Output]\r\n" +
        "Driver=\r\n" +
        "\r\n" +
        "[Asio.Input.0]\r\n" +
        "Driver=\r\n" +
        "\r\n" +
        "[Asio.Input.1]\r\n" +
        "Driver=\r\n" +
        "\r\n" +
        "[Asio.Input.Mic]\r\n" +
        "Driver=\r\n");

    private readonly ITonelabFileSystem _files;
    private readonly Func<bool> _gameRunning;
    private readonly string? _gameDirectory;
    private readonly object _sync = new();

    public RsAsioIniGuard(ITonelabFileSystem files, Func<bool> gameRunning, string? gameDirectory)
    {
        _files = files;
        _gameRunning = gameRunning;
        _gameDirectory = string.IsNullOrWhiteSpace(gameDirectory) ? null : gameDirectory;
    }

    public string? IniPath => _gameDirectory is null ? null : Path.Combine(_gameDirectory, IniName);

    public string? BackupPath => _gameDirectory is null ? null : Path.Combine(_gameDirectory, BackupName);

    /// <summary>Whether a backup is waiting to be put back.</summary>
    public bool HasBackup => BackupPath is { } backup && _files.Exists(backup);

    /// <summary>
    ///     Before a start: puts back a backup an earlier session left, backs the ini up, then writes
    ///     the driverless one. A failed write puts the original back at once.
    /// </summary>
    public RsAsioIniOutcome Apply()
    {
        lock (_sync)
        {
            if (IniPath is not { } ini || BackupPath is not { } backup) return RsAsioIniOutcome.NoGameFolder;
            if (_gameRunning()) return RsAsioIniOutcome.RefusedGameRunning;
            if (_files.Exists(backup) && RestoreLocked(ini, backup) != RsAsioIniOutcome.Restored) return RsAsioIniOutcome.Failed;
            if (!_files.Exists(ini)) return RsAsioIniOutcome.NoIni;
            try
            {
                _files.WriteAllBytes(backup, _files.ReadAllBytes(ini));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return RsAsioIniOutcome.Failed;
            }

            try
            {
                _files.WriteAllBytes(ini, DriverlessIni);
                return RsAsioIniOutcome.Applied;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RestoreLocked(ini, backup);
                return RsAsioIniOutcome.Failed;
            }
        }
    }

    /// <summary>Puts the backup back byte for byte and deletes it, unless the game is running. The same call serves "after the game exited" and "at startup, a crash left one".</summary>
    public RsAsioIniOutcome Restore()
    {
        lock (_sync)
        {
            if (IniPath is not { } ini || BackupPath is not { } backup) return RsAsioIniOutcome.NoGameFolder;
            if (!_files.Exists(backup)) return RsAsioIniOutcome.NothingToRestore;
            if (_gameRunning()) return RsAsioIniOutcome.RefusedGameRunning;
            return RestoreLocked(ini, backup);
        }
    }

    private RsAsioIniOutcome RestoreLocked(string ini, string backup)
    {
        try
        {
            _files.WriteAllBytes(ini, _files.ReadAllBytes(backup));
            // The backup goes only after the copy succeeded.
            _files.Delete(backup);
            return RsAsioIniOutcome.Restored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RsAsioIniOutcome.Failed;
        }
    }
}
