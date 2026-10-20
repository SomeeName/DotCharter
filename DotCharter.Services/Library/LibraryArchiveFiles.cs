using DotCharter.Services.Package.DcSong;

namespace DotCharter.Services.Library;

/// <summary>What a library entry was read from.</summary>
public enum LibrarySourceKind
{
    /// <summary>A Rocksmith 2014 <c>.psarc</c>.</summary>
    Psarc,

    /// <summary>A DotCharter song package, <c>.dcsong</c> (<c>docs/agents/services/dcsong-format.md</c>).</summary>
    DcSong
}

/// <summary>
///     The files a library scan reads under its root, and how it walks there: every
///     file a registered <see cref="ILibraryPackageSource" /> reads. An entry's
///     <see cref="LibrarySourceKind" /> is its file's extension — the one thing the scan
///     admitted it by — so the kind needs no column in the library index and no schema
///     bump: no earlier index holds a <c>.dcsong</c> row.
/// </summary>
public static class LibraryArchiveFiles
{
    /// <summary>The kind of the library file at <paramref name="path" />.</summary>
    public static LibrarySourceKind KindOf(string? path) =>
        path != null && path.EndsWith(DcSongFormat.Extension, StringComparison.OrdinalIgnoreCase)
            ? LibrarySourceKind.DcSong
            : LibrarySourceKind.Psarc;

    /// <summary>Every library file under <paramref name="rootPath" />, one extension after the
    /// other in <paramref name="extensions" />' order (each package source's, in
    /// registration order — <c>.psarc</c>, then <c>.dcsong</c>).</summary>
    public static string[] Enumerate(string rootPath, IEnumerable<string> extensions) =>
    [
        .. extensions.SelectMany(extension => Directory.EnumerateFiles(rootPath, "*" + extension, RecursiveScanOptions))
    ];

    /// <summary>
    ///     How the library root is walked.
    ///     <para>
    ///         <b><c>IgnoreInaccessible</c> is the load-bearing flag.</b> The scan
    ///         used <c>SearchOption.AllDirectories</c>, which is
    ///         <c>EnumerationOptions.Compatible</c> — and that sets
    ///         <c>IgnoreInaccessible = false</c>, so the FIRST denied directory
    ///         throws <see cref="UnauthorizedAccessException" /> out of the whole
    ///         enumeration. Not the folder: the enumeration, before a single
    ///         archive is opened. The caller then reports a failed scan and both
    ///         song lists come up EMPTY, with no partial result and no use of the
    ///         index cache.
    ///     </para>
    ///     <para>
    ///         A library pointed at a whole DRIVE is a supported configuration
    ///         (the library index's own <c>PurgeMissing</c> has a drive-root case for it), and
    ///         every NTFS volume root carries <c>System Volume Information</c>,
    ///         which is denied to a non-elevated user. So the supported case was
    ///         the broken one.
    ///     </para>
    ///     <para>
    ///         <c>AttributesToSkip</c> is cleared back to zero on purpose:
    ///         <see cref="EnumerationOptions" />'s own default skips hidden and
    ///         system entries, and a user is entitled to keep a library in a
    ///         hidden folder. This changes exactly one behaviour — the throw.
    ///     </para>
    /// </summary>
    internal static EnumerationOptions RecursiveScanOptions => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0
    };
}
