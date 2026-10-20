using System.Diagnostics;
using Microsoft.Win32;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>The running game, as far as the launcher needs it: find it, wait for it to exit, end it.</summary>
public interface IRocksmithProcesses
{
    /// <summary>The pid of a running <c>Rocksmith2014.exe</c>, or null.</summary>
    int? FindGame();

    /// <summary>True once <paramref name="processId" /> has exited (or is gone), false when it still runs after <paramref name="timeout" />.</summary>
    Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct);

    /// <summary>Ends the process; false when it could not. Only ever called for a game DotCharter started and that ignored <c>quit</c>.</summary>
    bool Kill(int processId);
}

/// <summary>Whether a start strategy started the game, cannot run here (the next one is tried), or refused.</summary>
public enum RocksmithStartStatus
{
    Started,

    /// <summary>This strategy is not possible on this machine (no mirror across volumes): the launcher tries the next.</summary>
    Unavailable,

    Failed
}

/// <summary>One start's answer: the pid when the strategy knows it (a direct start), else null and the launcher looks for the process.</summary>
public sealed record RocksmithStart(RocksmithStartStatus Status, int? ProcessId, GameToneLaunchFailure Failure, string Detail)
{
    public static RocksmithStart Requested { get; } = new(RocksmithStartStatus.Started, null, GameToneLaunchFailure.None, "");

    public static RocksmithStart Running(int processId) => new(RocksmithStartStatus.Started, processId, GameToneLaunchFailure.None, "");

    public static RocksmithStart NotHere(string detail) => new(RocksmithStartStatus.Unavailable, null, GameToneLaunchFailure.None, detail);

    public static RocksmithStart Refused(GameToneLaunchFailure failure, string detail) => new(RocksmithStartStatus.Failed, null, failure, detail);
}

/// <summary>
///     How the game gets started. The launcher tries its strategies in order: the direct start
///     from the mirror (<see cref="MirrorRocksmithStarter" />) first, Steam's
///     <c>steam://rungameid/221680</c> on the live folder (<see cref="SteamRocksmithStarter" />) only
///     when no mirror is possible.
/// </summary>
public interface IRocksmithStarter
{
    /// <summary>True when the game runs from its live folder, so the launcher swaps RS_ASIO.ini around the session (<see cref="RsAsioIniGuard" />).</summary>
    bool UsesLiveGameFolder { get; }

    Task<RocksmithStart> StartAsync(CancellationToken ct);
}

/// <summary>Starts the game through Steam on its live folder (the fallback: Steam's overlay and DRM as usual, tonelab hides the window).</summary>
public sealed class SteamRocksmithStarter(IAppLogger logger) : IRocksmithStarter
{
    public const string SteamUrl = "steam://rungameid/" + RocksmithGame.SteamAppId;

    public bool UsesLiveGameFolder => true;

    public Task<RocksmithStart> StartAsync(CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(SteamUrl) { UseShellExecute = true });
            return Task.FromResult(RocksmithStart.Requested);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.Warning(AppLogCategory.Audio, $"tonelab: Steam did not take {SteamUrl}.", ex);
            return Task.FromResult(RocksmithStart.Refused(GameToneLaunchFailure.StartFailed, ex.Message));
        }
    }
}

/// <summary>The installed game: its exe name, its Steam app id and its folder.</summary>
public static class RocksmithGame
{
    public const string SteamAppId = "221680";
    public const string ProcessName = "Rocksmith2014";

    /// <summary>The Steam client's process (<c>steam.exe</c>); a direct start needs it running.</summary>
    public const string SteamProcessName = "steam";

    /// <summary>Whether the Steam client runs: without it a direct start of the game cannot pass Steam's check.</summary>
    public static bool IsSteamRunning()
    {
        var processes = Process.GetProcessesByName(SteamProcessName);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    /// <summary>
    ///     The game folder from Steam's uninstall key (<c>Steam App 221680</c>, 32-bit registry view,
    ///     <c>InstallLocation</c>), as tonelab's launcher reads it; null when there is none. Read only.
    /// </summary>
    public static string? FindGameDirectory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + SteamAppId);
            return key?.GetValue("InstallLocation") is string { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The real processes.</summary>
public sealed class RocksmithProcesses : IRocksmithProcesses
{
    public static RocksmithProcesses Instance { get; } = new();

    public int? FindGame()
    {
        var processes = Process.GetProcessesByName(RocksmithGame.ProcessName);
        try
        {
            return processes.Length == 0 ? null : processes[0].Id;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout != System.Threading.Timeout.InfiniteTimeSpan) wait.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    public bool Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
