using System.Collections.Concurrent;

namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>Recycle-only removal. There is deliberately no permanent-delete fallback.</summary>
internal static class RecycleBin
{
    // Windows SDK: FOFX_RECYCLEONDELETE | FOFX_EARLYFAILURE | FOFX_NOCOPYHOOKS | FOF_NOERRORUI | FOF_NOCONFIRMATION | FOF_NOCONFIRMMKDIR | FOF_SILENT.
    private static readonly ConcurrentDictionary<string, byte> LoggedRefusals = new(StringComparer.OrdinalIgnoreCase);
    private const uint Flags = 0x00080000 | 0x00100000 | 0x00800000 | 0x0400 | 0x0010 | 0x0200 | 0x0004;

    internal static void Remove(string path) => Remove(path,
        full => OperatingSystem.IsWindows() ? WindowsRecycleBinProbe.Read(full) : new(false, false, null, null, null, null),
        Execute, Console.Error.WriteLine);

    internal static void Remove(string path, Func<string, RecycleBinStatus> probe,
        Action<string, uint> execute, Action<string> log)
    {
        RecycleBinStatus status;
        string full;
        var identity = path;
        try
        {
            full = Path.GetFullPath(path);
            identity = full;
            // Probe before Exists: a UNC/network/reparse path must not reach filesystem or shell removal.
            status = probe(full);
        }
        catch (Exception ex)
        {
            LogOnce(identity, log, $"Recycle Bin left '{Path.GetFileName(path)}' in place: eligibility could not be verified ({ex.Message}).");
            return;
        }
        if (!status.IsFixedLocalVolume || !status.IsPlainLocalPath || status.IsEnabled != true ||
            status.QuotaBytes is not > 0 || status.UsedBytes is not >= 0 || status.CandidateBytes is not >= 0 ||
            status.UsedBytes > status.QuotaBytes || status.CandidateBytes > status.QuotaBytes - status.UsedBytes)
        {
            LogOnce(identity, log, $"Recycle Bin left '{Path.GetFileName(full)}' in place: a safe local bin with sufficient configured capacity could not be verified.");
            return;
        }
        if (!File.Exists(full) && !Directory.Exists(full)) return;
        try
        {
            execute(full, Flags);
        }
        // Another caller (a second tidy of the same folder) took the item between the
        // check above and the shell: it is gone, so the removal is done. Anything still on
        // disk is a real refusal and keeps throwing.
        catch (IOException ex) when (ex.InnerException is FileNotFoundException or DirectoryNotFoundException
                                     && !File.Exists(full) && !Directory.Exists(full))
        {
        }
    }

    private static void LogOnce(string path, Action<string> log, string message)
    {
        if (LoggedRefusals.TryAdd(path, 0)) log(message);
    }

    // Explicit backend seam retained for the original operation-flags contract test.
    internal static void Remove(string path, Action<string, uint>? execute)
    {
        if (execute == null)
        {
            Remove(path);
            return;
        }
        if (File.Exists(path) || Directory.Exists(path)) execute(Path.GetFullPath(path), Flags);
    }

    private static void Execute(string full, uint flags)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Recycle Bin is unavailable.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows()) WindowsRecycleOperation.Execute(full, flags);
                else throw new PlatformNotSupportedException("Recycle Bin is unavailable.");
            }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true, Name = "Project file recycling"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new IOException("The Recycle Bin could not accept the item; it was left in place.", failure);
    }
}
