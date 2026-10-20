namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
///     One entry of the persisted "Open Recent" list (<see cref="AppConfig.RecentProjects" />).
///     The path is user data — never log it at Info level.
/// </summary>
public sealed class RecentProjectEntry
{
    /// <summary>Absolute path to the <c>.rscp</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>What the menu shows — the song name at the time it was opened,
    /// falling back to the file name. Cached here so the menu never has to open
    /// ten project files to render itself.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public DateTime LastOpenedUtc { get; set; }
}

/// <summary>
///     The pure MRU rules over <see cref="AppConfig.RecentProjects" />, kept
///     static + side-effect-free so they are testable without a session.
///     Maintained by the ONE open/save funnel (<c>ProjectSessionService</c>):
///     a successful project open or save-as touches the list; nothing else
///     writes it.
/// </summary>
public static class RecentProjectsList
{
    /// <summary>List cap — the menu stays scannable and the config stays small.</summary>
    public const int Cap = 10;

    /// <summary>
    ///     Records <paramref name="path" /> as most-recently-opened: normalizes to a
    ///     full path, de-duplicates case-insensitively (Windows paths), inserts at
    ///     the front and trims to <see cref="Cap" />. A null/blank or unresolvable
    ///     path is ignored.
    /// </summary>
    public static void Touch(List<RecentProjectEntry> list, string path, string? displayName, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(path);
        }
        catch
        {
            return; // invalid path characters etc. — not worth an exception on a save
        }

        var name = string.IsNullOrWhiteSpace(displayName)
            ? System.IO.Path.GetFileNameWithoutExtension(fullPath)
            : displayName.Trim();

        list.RemoveAll(e => string.Equals(e.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, new RecentProjectEntry { Path = fullPath, DisplayName = name, LastOpenedUtc = nowUtc });
        if (list.Count > Cap) list.RemoveRange(Cap, list.Count - Cap);
    }

    /// <summary>
    ///     <see cref="Touch" />, with entries whose files are GONE dropped first.
    ///     <para>
    ///         <see cref="Prune" /> was applied on the READ only, so a dead entry
    ///         went on occupying one of the <see cref="Cap" /> slots forever: the
    ///         menu rendered fewer and fewer rows as projects were deleted or
    ///         moved, and — the part that loses data — the next open then trimmed a
    ///         LIVE entry off the end to make room, because the trim counted the
    ///         dead ones. A user who had deleted five old drafts kept five recent
    ///         projects, not ten, and never saw why.
    ///     </para>
    ///     <para>
    ///         The prune runs BEFORE the insert, so the path being touched is added
    ///         whatever <paramref name="fileExists" /> says about it — the callers
    ///         record only after the file is safely open or on disk, and a probe
    ///         that answers "no" for a file we just wrote must not lose the entry.
    ///     </para>
    /// </summary>
    public static void TouchLive(
        List<RecentProjectEntry> list, string path, string? displayName, DateTime nowUtc,
        Func<string, bool>? fileExists = null)
    {
        var live = Prune(list, fileExists);
        list.Clear();
        list.AddRange(live);
        Touch(list, path, displayName, nowUtc);
    }

    /// <summary>
    ///     Returns the entries whose files still exist (a deleted/renamed project
    ///     must not render a dead menu row). <paramref name="fileExists" /> is
    ///     injectable so the rule tests without touching a disk.
    /// </summary>
    public static List<RecentProjectEntry> Prune(IEnumerable<RecentProjectEntry> list, Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        return list.Where(e => !string.IsNullOrWhiteSpace(e.Path) && exists(e.Path)).ToList();
    }
}
