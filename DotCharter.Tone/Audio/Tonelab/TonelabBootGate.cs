using System.Globalization;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>How far tonelab's own log says one game process is.</summary>
public enum TonelabBootStage
{
    /// <summary>No <c>loaded into pid</c> line for this pid (yet): the log is missing, empty, or another game's.</summary>
    NotLoaded,

    /// <summary>tonelab is injected and booting; the game must not be talked to yet.</summary>
    Booting,

    /// <summary>The log says <c>ready: profile loaded</c>: the pipe may be used.</summary>
    Ready
}

/// <summary>
///     Reads tonelab's <c>tonelab.log</c> (next to <c>tonelab.dll</c>) to learn when a game DotCharter started is past its boot.
///     Any connection to <c>\.\pipe\tonelab-&lt;pid&gt;</c> during the boot hangs the game before its title screen, so a launch waits
///     for this file instead of polling the pipe. Pure over the text; the file read and the clock are injected.
/// </summary>
public static class TonelabBootGate
{
    public const string LogFileName = "tonelab.log";
    public const string ReadyMarker = "ready: profile loaded";

    private const string LoadedMarker = "tonelab M2 loaded into pid ";

    /// <summary>How often the log is read.</summary>
    public static TimeSpan Poll { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long one boot may take before that attempt counts as hung (a good boot takes 27-31 s).</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(45);

    /// <summary>The log that sits beside <paramref name="tonelabDllPath" />.</summary>
    public static string LogPathFor(string tonelabDllPath) => Path.Combine(Path.GetDirectoryName(tonelabDllPath) ?? "", LogFileName);

    /// <summary>The stage of <paramref name="pid" /> in <paramref name="logText" />: its LAST load line counts, a later load line of another pid ends its section.</summary>
    public static TonelabBootStage Evaluate(string? logText, int pid)
    {
        if (string.IsNullOrEmpty(logText)) return TonelabBootStage.NotLoaded;
        var stage = TonelabBootStage.NotLoaded;
        foreach (var line in logText.Split('\n'))
        {
            var at = line.IndexOf(LoadedMarker, StringComparison.Ordinal);
            if (at >= 0)
            {
                var digits = line.AsSpan(at + LoadedMarker.Length).Trim();
                var end = 0;
                while (end < digits.Length && char.IsAsciiDigit(digits[end])) end++;
                stage = int.TryParse(digits[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var loaded) && loaded == pid
                    ? TonelabBootStage.Booting
                    : TonelabBootStage.NotLoaded;

                continue;
            }

            if (stage == TonelabBootStage.Booting && line.Contains(ReadyMarker, StringComparison.Ordinal)) stage = TonelabBootStage.Ready;
        }

        return stage;
    }

    /// <summary>The log's text, opened shared (the game keeps writing it); null when it is not there or cannot be read.</summary>
    public static string? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Waits until the log says <paramref name="pid" /> is ready (true) or <paramref name="timeout" /> passes on <paramref name="clock" /> (false). Touches no pipe.</summary>
    public static async Task<bool> WaitForReadyAsync(int pid, string logPath, Func<string, string?> read, Func<TimeSpan, CancellationToken, Task> delay,
        Func<DateTime> clock, TimeSpan timeout, IAppLogger? logger, CancellationToken ct)
    {
        var started = clock();
        var last = (TonelabBootStage?)null;
        while (true)
        {
            var stage = Evaluate(read(logPath), pid);
            if (stage != last) logger?.Info(AppLogCategory.Audio, $"tonelab: boot log of pid {pid}: {stage} ({logPath}).");
            last = stage;
            if (stage == TonelabBootStage.Ready) return true;
            if (clock() - started >= timeout) return false;
            await delay(Poll, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }
}
