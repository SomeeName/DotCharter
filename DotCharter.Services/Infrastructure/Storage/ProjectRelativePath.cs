namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>
///     The ONE check for a file path that came out of a file: an <c>.rscp</c>'s
///     <c>musicFileName</c> / <c>dcCoverArt</c> / stem paths / <c>arrangementFiles</c>,
///     an <c>.eof</c>'s companion audio, an imported profile's avatar. Such a path is
///     DATA written by whoever shared the file, never a trusted location.
///     <para>
///         Nothing here touches the file system. That is the point: on Windows a
///         <see cref="File.Exists(string)" /> on a UNC path already sends the user's
///         NTLM credentials to that host, so a refused path must be refused BEFORE
///         anything probes it.
///     </para>
///     <para>
///         Two strengths. <see cref="TryResolve" /> is the strict one: a relative
///         path with no <c>..</c>, no drive, no device name, that stays under the
///         base directory — used for everything that is copied, merged, exported or
///         deleted (cover art, arrangement files, EOF companion audio, avatars).
///         <see cref="IsReadableMediaReference" /> is the media one, for the audio and
///         stem references the app ITSELF writes as absolute or <c>..</c>-relative paths
///         (Save As out of the audio's folder, attaching audio to an unsaved session,
///         crash snapshots, stems beside a parent-folder audio): a local drive path is
///         read as audio, a remote, device or drive-relative one never is. Neither is
///         ever a write location.
///     </para>
/// </summary>
public static class ProjectRelativePath
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3"
    };

    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    ///     The declared path made absolute under <paramref name="baseDirectory" />, or
    ///     null when it is not a plain project-relative path: rooted, UNC, device,
    ///     drive-relative (<c>C:x</c>), a <c>..</c> segment, a reserved device name,
    ///     a stream (<c>:</c>) or a wildcard. Never touches the disk.
    /// </summary>
    public static string? TryResolve(string? baseDirectory, string? declared)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) || !IsSafeRelative(declared)) return null;

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
            var full = Path.GetFullPath(Path.Combine(root, declared!));
            return IsUnder(root, full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    ///     True for a relative path whose every segment is a plain name: not rooted,
    ///     no drive, no <c>..</c>, no device name, no stream or wildcard character.
    /// </summary>
    public static bool IsSafeRelative(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared)) return false;
        if (Path.IsPathRooted(declared) || declared.IndexOfAny(Separators) == 0) return false;
        if (declared.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        foreach (var segment in declared.Split(Separators))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (!IsPlainSegment(segment)) return false;
        }

        return true;
    }

    /// <summary>
    ///     True when an audio or stem reference may be READ: a safe relative path, a
    ///     relative path that climbs with <c>..</c> (the app writes those for stems
    ///     beside a parent-folder audio), or a fully qualified path on a LOCAL drive
    ///     letter (what Save As and the unsaved-session attach write). False — and
    ///     the caller must not probe it — for UNC (<c>\\</c>, <c>//</c>), device
    ///     (<c>\\?\</c>, <c>\\.\</c>), drive-relative (<c>C:x</c>), root-relative
    ///     (<c>\x</c>) paths, reserved device names, and a drive mapped to a network
    ///     share.
    /// </summary>
    public static bool IsReadableMediaReference(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared)) return false;
        if (declared.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        if (!Path.IsPathRooted(declared))
        {
            foreach (var segment in declared.Split(Separators))
            {
                if (segment.Length == 0 || segment is "." or "..") continue;
                if (!IsPlainSegment(segment)) return false;
            }

            return true;
        }

        return IsLocalAbsolute(declared);
    }

    /// <summary>
    ///     True for a fully qualified path on a local drive letter (<c>C:\…</c> on
    ///     Windows, <c>/…</c> elsewhere) with plain segments. Never a UNC, device or
    ///     drive-relative path, and never a drive letter mapped to a network share.
    /// </summary>
    public static bool IsLocalAbsolute(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared) || !Path.IsPathFullyQualified(declared)) return false;
        if (declared.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        string rest;
        if (OperatingSystem.IsWindows())
        {
            // Exactly "X:\" or "X:/" — anything starting with two separators is UNC or a device.
            if (declared.Length < 3 || !char.IsAsciiLetter(declared[0]) || declared[1] != ':' ||
                Array.IndexOf(Separators, declared[2]) < 0)
            {
                return false;
            }

            if (IsNetworkDrive(declared[0])) return false;
            rest = declared[3..];
        }
        else
        {
            if (declared.StartsWith("//", StringComparison.Ordinal)) return false;
            rest = declared[1..];
        }

        foreach (var segment in rest.Split(Separators))
        {
            if (segment.Length == 0 || segment is "." or "..") continue;
            if (!IsPlainSegment(segment)) return false;
        }

        return true;
    }

    /// <summary>True when <paramref name="path" /> is <paramref name="root" /> itself or lies below it.</summary>
    public static bool IsUnder(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        if (string.Equals(trimmedRoot, Path.TrimEndingDirectorySeparator(path), comparison)) return true;
        return path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, comparison)
               || path.StartsWith(trimmedRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool IsPlainSegment(string segment)
    {
        if (segment == "..") return false;
        // ':' is a drive ("C:x") or an alternate data stream; '*' and '?' are wildcards.
        if (segment.IndexOfAny([':', '*', '?']) >= 0) return false;
        // Windows strips trailing dots and spaces, so "..." and "x. " name something else.
        if (segment[^1] is '.' or ' ') return false;

        var stem = segment;
        var dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        return !ReservedDeviceNames.Contains(stem.TrimEnd(' '));
    }

    private static bool IsNetworkDrive(char letter)
    {
        try
        {
            // GetDriveType reads the local drive table; it does not contact the share.
            return new DriveInfo(letter.ToString()).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
