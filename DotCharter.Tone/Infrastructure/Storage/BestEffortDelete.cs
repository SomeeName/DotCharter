namespace DotCharter.Tone.Infrastructure.Storage;

/// <summary>
///     Deletes that must never fail the operation around them: temp and scratch
///     files, superseded caches and snapshots, half-written downloads. A missing
///     path is a no-op, and a file the OS refuses (locked by another process,
///     read-only, denied by an ACL) is left behind — every caller treats a stale
///     leftover as harmless, and the next run overwrites or sweeps it.
///     <para>
///         Only <see cref="IOException" /> and <see cref="UnauthorizedAccessException" />
///         are swallowed. Anything else is a bug in the caller (a null or
///         malformed path) and is allowed to surface.
///     </para>
/// </summary>
public static class BestEffortDelete
{
    public static void File(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void Directory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
