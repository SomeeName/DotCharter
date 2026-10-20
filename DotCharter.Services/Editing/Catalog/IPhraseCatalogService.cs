namespace DotCharter.Services.Editing.Catalog;

/// <summary>
///     The per-user library of named, reusable chart phrases (Editor-on-Fire's
///     "fret catalog"). Songs repeat; this is what turns the second chorus into
///     two keystrokes.
/// </summary>
public interface IPhraseCatalogService
{
    /// <summary>Raised after any add / rename / remove / reload, on the calling
    /// thread. The catalog is edited from the UI thread only.</summary>
    event Action? CatalogChanged;

    /// <summary>Where the entries live on disk. Shown in the pane so the user can
    /// back the folder up or share a phrase file.</summary>
    string StorageDirectory { get; }

    /// <summary>Every readable entry, newest first. Loaded on first access and
    /// cached; unreadable files are skipped, never thrown on.</summary>
    IReadOnlyList<PhraseCatalogEntry> Entries { get; }

    /// <summary>
    ///     Captures the given chart content under <paramref name="name" />.
    ///     Returns the stored entry, or <c>null</c> when there was nothing to
    ///     store or the write failed (a failure is logged, never thrown).
    /// </summary>
    PhraseCatalogEntry? Add(
        string name,
        Arrangement source,
        IEnumerable<GuitarNote> notes,
        IEnumerable<Chord> chords,
        IEnumerable<HandShape> handShapes,
        IEnumerable<FretAnchor> anchors);

    /// <summary>Exact, case-insensitive name lookup — the paste-by-typed-name
    /// path. Null when nothing matches.</summary>
    PhraseCatalogEntry? FindByName(string name);

    /// <summary>Renames an entry in place. The entry's id (and file name) is
    /// deliberately NOT changed, so nothing that referenced it breaks.</summary>
    bool Rename(PhraseCatalogEntry entry, string newName);

    bool Remove(PhraseCatalogEntry entry);

    /// <summary>Drops the in-memory cache so the next read re-scans the folder.
    /// Needed after a whole-install backup import replaced the files underneath.</summary>
    void InvalidateCache();
}
