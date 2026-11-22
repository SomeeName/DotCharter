using System.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Perf;

/// <summary>
///     One launch of the app by <see cref="StartupPerfGateTests" />, isolated from the
///     user's data (#606, #439).
///     <para>
///         The gate used to start the app with no environment of its own, so the
///         child read and wrote the real <c>%APPDATA%\DotCharter</c> and
///         <c>%LOCALAPPDATA%\DotCharter</c>, and then looked for the newest log in
///         the SHARED log folder with only a timestamp to tell its own line from
///         another instance's. Here the child gets a fresh
///         <see cref="UserDataFolders.DataRootVariable" /> folder, and its startup
///         line is read from the log folder the product resolves under that root
///         and from nowhere else: no other instance writes there.
///     </para>
/// </summary>
internal sealed class StartupGateLaunch : IDisposable
{
    public const string StartupPhasesMarker = "StartupPhases:";

    public StartupGateLaunch()
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "dc-startup-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataRoot);
        // The product's own mapping from a data root to its log folder, so the gate
        // cannot drift from where the child really writes.
        using (UserDataFolders.UseDataRootForTesting(DataRoot))
        {
            LogsDirectory = WritablePaths.RedirectedLogRoot()
                            ?? throw new InvalidOperationException("the product resolved no log folder under a data root.");
        }
    }

    /// <summary>The child's own data root, a fresh folder under the temp folder.</summary>
    public string DataRoot { get; }

    /// <summary>Where the child writes its log, under <see cref="DataRoot" />.</summary>
    public string LogsDirectory { get; }

    /// <summary>How the child is started: in its own folder, with the data root set.</summary>
    public ProcessStartInfo StartInfo(string exe)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
        };
        info.Environment[UserDataFolders.DataRootVariable] = DataRoot;
        return info;
    }

    /// <summary>
    ///     The child's startup line from <see cref="StartupPhasesMarker" /> on, or
    ///     null while it has not written one. The log is open in the app, so it is
    ///     read with full sharing; a file that cannot be read yet reads as "not yet".
    /// </summary>
    public string? FindStartupPhasesLine()
    {
        if (!Directory.Exists(LogsDirectory)) return null;
        foreach (var log in Directory.EnumerateFiles(LogsDirectory, "*.log"))
        {
            try
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    var index = line.IndexOf(StartupPhasesMarker, StringComparison.Ordinal);
                    if (index >= 0) return line[index..];
                }
            }
            catch (IOException)
            {
                // Being rolled or created this instant: the next poll reads it.
            }
        }

        return null;
    }

    public void Dispose() => BestEffortDelete.Directory(DataRoot);
}
