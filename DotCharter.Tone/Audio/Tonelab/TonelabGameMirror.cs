using System.Text;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>A file's identity on disk: the volume serial and the file index (what <c>GetFileInformationByHandle</c> reports). Two names with one identity are hardlinks of one file.</summary>
public readonly record struct GameFileId(uint VolumeSerial, ulong FileIndex);

/// <summary><c>CreateHardLinkW</c> failed with <c>ERROR_NOT_SAME_DEVICE</c>: the mirror and the game are on different volumes.</summary>
public sealed class GameMirrorCrossVolumeException(string message) : IOException(message);

/// <summary>
///     The file operations the mirror needs, so its upkeep runs over memory in tests and over Win32
///     in the app (<see cref="Win32GameMirrorFileSystem" />). No member opens an existing file for
///     writing: a mirror name may be a hardlink, and writing through it writes the game's original.
/// </summary>
public interface IGameMirrorFileSystem
{
    bool FileExists(string path);

    /// <summary>True for a real folder or a junction.</summary>
    bool DirectoryExists(string path);

    /// <summary>The target of the junction at <paramref name="path" />, or null when it is none.</summary>
    string? JunctionTarget(string path);

    /// <summary>The identity of the file at <paramref name="path" />, or null when there is none. Opens it with no read or write access.</summary>
    GameFileId? FileId(string path);

    /// <summary>The names (not paths) of the files directly in <paramref name="directory" /> matching <paramref name="pattern" />.</summary>
    IReadOnlyList<string> FileNames(string directory, string pattern);

    void CreateDirectory(string path);

    /// <summary>Adds the name <paramref name="link" /> to <paramref name="existing" />; throws <see cref="GameMirrorCrossVolumeException" /> across volumes.</summary>
    void CreateHardLink(string link, string existing);

    void CreateJunction(string link, string target);

    /// <summary>Removes the junction itself (<c>RemoveDirectory</c>), never recursively: its target's contents are untouched.</summary>
    void RemoveJunction(string path);

    /// <summary>Removes a NAME; a linked original keeps its data.</summary>
    void DeleteFile(string path);

    /// <summary>Copies into a NEW file; the destination must not exist.</summary>
    void CopyToNewFile(string source, string destination);

    /// <summary>Writes a NEW file; the destination must not exist.</summary>
    void WriteNewFile(string path, byte[] bytes);
}

/// <summary>What one <see cref="TonelabGameMirror.Prepare" /> did.</summary>
public enum GameMirrorOutcome
{
    /// <summary>The mirror is up to date at <see cref="GameMirrorResult.Root" />.</summary>
    Ready,

    /// <summary>No mirror is possible (the game is on another volume and the game's volume is not writable): the live folder is the fallback.</summary>
    CrossVolume,

    /// <summary>The game folder is unknown or holds no <c>Rocksmith2014.exe</c>.</summary>
    NoGame,

    /// <summary>A file operation failed, or the mirror holds a real folder where a junction belongs; nothing recursive is ever deleted.</summary>
    Failed
}

public sealed record GameMirrorResult(GameMirrorOutcome Outcome, string? Root, string Detail);

/// <summary>
///     The tone-server folder (tonelab's HANDOVER, "The tone-server folder"): a mirror of the game
///     folder without copies, so the live game folder is never changed. The upkeep runs at every
///     start, while the game is closed: the read-only game files are hardlinks, checked by identity
///     and relinked when stale (a game update writes new files); new root <c>*.psarc</c> are linked
///     and vanished ones dropped; <c>base</c>, <c>guitarcade</c>, <c>venues</c>, <c>UPlay</c> are
///     junctions; <c>Rocksmith.ini</c> is copied fresh (the user's settings); <c>RS_ASIO.ini</c> is the
///     mirror's own, driverless; <c>Tmp</c> exists. rs7string, RSMods, the CDLC enabler and <c>dlc</c>
///     stay out. Real files are only ever created NEW (the old name deleted first), so nothing
///     writes through a hardlink. Pure over <see cref="IGameMirrorFileSystem" />.
/// </summary>
public sealed class TonelabGameMirror
{
    public const string ExeName = "Rocksmith2014.exe";
    public const string SettingsIniName = "Rocksmith.ini";
    public const string TmpFolderName = "Tmp";
    public const string GameVolumeFolderName = "tonelab-game";

    /// <summary>The read-only game files the mirror links (plus every root <c>*.psarc</c>).</summary>
    public static IReadOnlyList<string> LinkedFiles { get; } =
        [ExeName, "steam_api.dll", "bink2w32.dll", "systemdetection.dll", "uplay_r1_loader.dll", "metadata", "avrt.dll", "RS_ASIO.dll"];

    public static IReadOnlyList<string> Junctions { get; } = ["base", "guitarcade", "venues", "UPlay"];

    /// <summary>Kept out on purpose: rs7string, RSMods, the CDLC enabler (the system DLLs load instead) and the DLC folder.</summary>
    public static IReadOnlyList<string> LeftOut { get; } = ["dinput8.dll", "xinput1_3.dll", "D3DX9_42.dll", "dlc"];

    /// <summary>The mirror's own RS_ASIO.ini: no ASIO driver, no WASAPI pass-through, so the game opens no audio device.</summary>
    public static readonly byte[] DriverlessIni = Encoding.ASCII.GetBytes(
        "; tonelab mirror (DotCharter): driverless (no ASIO driver, no WASAPI pass-through)\r\n" +
        "[Config]\r\nEnableWasapiOutputs=0\r\nEnableWasapiInputs=0\r\nEnableAsio=0\r\n\r\n" +
        "[Asio.Output]\r\nDriver=\r\n\r\n[Asio.Input.0]\r\nDriver=\r\n\r\n[Asio.Input.1]\r\nDriver=\r\n\r\n[Asio.Input.Mic]\r\nDriver=\r\n");

    private const string PsarcPattern = "*.psarc";

    private readonly IGameMirrorFileSystem _files;
    private readonly string _primaryRoot;

    public TonelabGameMirror(IGameMirrorFileSystem files, string primaryRoot)
    {
        _files = files;
        _primaryRoot = primaryRoot;
    }

    /// <summary>tonelab's own default: <c>%LOCALAPPDATA%\tonelab\game</c>, through DotCharter's data root.</summary>
    public static string DefaultRoot => Path.Combine(UserDataFolders.LocalAppData, "tonelab", "game");

    /// <summary>The mirror on the game's own volume (<c>&lt;drive&gt;:\tonelab-game</c>), for a game on another volume than the primary root.</summary>
    public static string GameVolumeRoot(string gameDirectory) =>
        Path.Combine(Path.GetPathRoot(Path.GetFullPath(gameDirectory)) ?? "", GameVolumeFolderName);

    /// <summary>
    ///     Brings the mirror up to date: at the primary root, else (hardlinks across volumes) on the
    ///     game's volume, else <see cref="GameMirrorOutcome.CrossVolume" />. Call only while the game is closed.
    /// </summary>
    public GameMirrorResult Prepare(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !_files.FileExists(Path.Combine(gameDirectory, ExeName)))
        {
            return new GameMirrorResult(GameMirrorOutcome.NoGame, null, "no Rocksmith2014.exe in the game folder");
        }

        var crossVolume = "";
        foreach (var root in new[] { _primaryRoot, GameVolumeRoot(gameDirectory) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                return Upkeep(root, gameDirectory);
            }
            catch (GameMirrorCrossVolumeException ex)
            {
                crossVolume = ex.Message;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && root != _primaryRoot && !_files.DirectoryExists(root))
            {
                // The game's volume is not writable here: no mirror at all.
                crossVolume = ex.Message;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new GameMirrorResult(GameMirrorOutcome.Failed, root, ex.Message);
            }
        }

        return new GameMirrorResult(GameMirrorOutcome.CrossVolume, null, crossVolume);
    }

    private GameMirrorResult Upkeep(string root, string game)
    {
        _files.CreateDirectory(root);
        var gamePsarcs = _files.FileNames(game, PsarcPattern);
        // The exe first: across volumes the first link fails before anything else is written.
        foreach (var name in LinkedFiles.Concat(gamePsarcs)) LinkFile(root, game, name);
        var keep = new HashSet<string>(gamePsarcs, StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _files.FileNames(root, PsarcPattern).Where(n => !keep.Contains(n))) _files.DeleteFile(Path.Combine(root, stale));

        foreach (var name in Junctions)
        {
            if (Junction(root, game, name) is { } fault) return new GameMirrorResult(GameMirrorOutcome.Failed, root, fault);
        }

        foreach (var name in LeftOut)
        {
            if (RemoveLeftOut(Path.Combine(root, name)) is { } fault) return new GameMirrorResult(GameMirrorOutcome.Failed, root, fault);
        }

        var settings = Path.Combine(root, SettingsIniName);
        RemoveFile(settings);
        if (_files.FileExists(Path.Combine(game, SettingsIniName))) _files.CopyToNewFile(Path.Combine(game, SettingsIniName), settings);
        var rsAsio = Path.Combine(root, RsAsioIniGuard.IniName);
        RemoveFile(rsAsio);
        _files.WriteNewFile(rsAsio, DriverlessIni);
        _files.CreateDirectory(Path.Combine(root, TmpFolderName));
        return new GameMirrorResult(GameMirrorOutcome.Ready, root, "");
    }

    /// <summary>A missing or stale name is dropped and linked again; a file the game lacks leaves the mirror too.</summary>
    private void LinkFile(string root, string game, string name)
    {
        var source = Path.Combine(game, name);
        var link = Path.Combine(root, name);
        if (_files.FileId(source) is not { } original)
        {
            RemoveFile(link);
            return;
        }

        if (_files.FileId(link) == original) return;
        RemoveFile(link);
        _files.CreateHardLink(link, source);
    }

    /// <summary>Null when the junction is in place; otherwise the reason it cannot be.</summary>
    private string? Junction(string root, string game, string name)
    {
        var link = Path.Combine(root, name);
        var target = Path.Combine(game, name);
        var current = _files.JunctionTarget(link);
        if (current is not null && SamePath(current, target) && _files.DirectoryExists(target)) return null;
        if (current is not null) _files.RemoveJunction(link);
        else if (_files.DirectoryExists(link)) return $"{link} is a real folder, not a junction; it is left alone";
        if (_files.DirectoryExists(target)) _files.CreateJunction(link, target);
        return null;
    }

    private string? RemoveLeftOut(string path)
    {
        if (_files.JunctionTarget(path) is not null) _files.RemoveJunction(path);
        else if (_files.DirectoryExists(path)) return $"{path} is a real folder; the mirror must not carry it";
        else RemoveFile(path);
        return null;
    }

    private void RemoveFile(string path)
    {
        if (_files.FileExists(path)) _files.DeleteFile(path);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
