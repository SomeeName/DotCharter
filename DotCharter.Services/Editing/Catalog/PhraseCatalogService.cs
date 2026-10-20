using System.Globalization;
using System.Text;
using DotCharter.Services.Export;
using DotCharter.Services.Parsing;

namespace DotCharter.Services.Editing.Catalog;

/// <summary>
///     Folder-backed implementation of <see cref="IPhraseCatalogService" />: one
///     <c>.rscp</c> fragment per phrase under
///     <c>&lt;ConfigRoot&gt;/phrases/</c>.
///     <para>
///         <b>Why per-user and not per-project.</b> A phrase library that cannot
///         follow the charter to the next song is most of its value gone — the
///         whole point is that the riff you built in one project is two keystrokes
///         away in the next. Storing it in the project would also mean a new
///         <c>.rscp</c> node (reader + writer + round-trip pins) for data that is
///         not part of the chart.
///     </para>
///     <para>
///         <b>Why a folder and not one JSON blob.</b> Entries are independent, and
///         a single file means one corrupt byte costs the whole library. It also
///         lets a phrase be shared by handing someone the file, and it lets the
///         entry BE a project fragment (see <see cref="PhraseCatalogFragment" />)
///         rather than a hand-written DTO that would drift from the model.
///     </para>
///     <para>
///         <b>The <c>phrases</c> folder is on two lists and must stay on both.</b>
///         On a portable install <c>WritablePaths.ConfigRoot</c> IS the install
///         directory, so anything under it that
///         <c>PayloadReceiptFilter.IsLocalState</c> misses is overwritten by an
///         applied update payload — hence
///         <c>PayloadReceiptFilter.ExcludedTopDirs</c>. And user data belongs in
///         the whole-install backup — hence <c>ConfigBackupRules.IncludedTopDirs</c>.
///         The two are pinned against each other by
///         <c>PayloadReceiptFilterTests</c>.
///     </para>
/// </summary>
public sealed class PhraseCatalogService : IPhraseCatalogService
{
    /// <summary>Top-level folder name under <c>ConfigRoot</c>. Referenced by
    /// <c>PayloadReceiptFilter</c> and <c>ConfigBackupRules</c> so the three
    /// cannot drift apart.</summary>
    public const string FolderName = "phrases";

    public const string FileExtension = ".rscp";

    private const int MaxIdLength = 60;

    private readonly string _directory;
    private readonly IAppLogger? _logger;
    private readonly IChartSerializer _serializer;
    private readonly object _sync = new();

    private List<PhraseCatalogEntry>? _entries;

    /// <summary>Test/tool constructor: a fixed directory, no
    /// <see cref="WritablePaths" /> probe.</summary>
    public PhraseCatalogService(string directory, IChartSerializer serializer, IAppLogger? logger = null)
    {
        _directory = directory;
        _serializer = serializer;
        _logger = logger;
    }

    public PhraseCatalogService(IChartSerializer serializer, IAppLogger? logger = null)
        : this(Path.Combine(WritablePaths.ConfigRoot, FolderName), serializer, logger)
    {
    }

    public event Action? CatalogChanged;

    public string StorageDirectory => _directory;

    public IReadOnlyList<PhraseCatalogEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries ??= LoadLocked();
            }
        }
    }

    public PhraseCatalogEntry? Add(
        string name,
        Arrangement source,
        IEnumerable<GuitarNote> notes,
        IEnumerable<Chord> chords,
        IEnumerable<HandShape> handShapes,
        IEnumerable<FretAnchor> anchors)
    {
        ArgumentNullException.ThrowIfNull(source);

        // The catalog writes note data to disk: play-only content is never exported.
        if (PlayOnlyExportGuard.IsPlayOnly(source))
        {
            _logger?.Info(AppLogCategory.Editing, "Phrase catalog: refused to store play-only content.");
            return null;
        }

        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            _logger?.Info(AppLogCategory.Editing, "Phrase catalog: refused to store an entry with no name.");
            return null;
        }

        var fragment = PhraseCatalogFragment.Build(trimmed, source, notes, chords, handShapes, anchors);
        if (fragment == null)
        {
            _logger?.Info(AppLogCategory.Editing,
                $"Phrase catalog: nothing to capture for '{trimmed}' (empty selection).");
            return null;
        }

        lock (_sync)
        {
            _entries ??= LoadLocked();

            var id = UniqueIdLocked(trimmed);
            var path = Path.Combine(_directory, id + FileExtension);
            try
            {
                Directory.CreateDirectory(_directory);
                // The serializer is path-based and flushes to the platter, but it
                // truncates in place — write beside the target and swap, so a
                // failed write can never leave a half file where a good one was.
                var temp = path + ".tmp";
                _serializer.Save(temp, fragment);
                AtomicFile.ReplaceWithRetry(temp, path);
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Editing,
                    $"Phrase catalog: could not write '{path}': {ex.Message}");
                return null;
            }

            var entry = PhraseCatalogFragment.Read(id, fragment, DateTime.UtcNow);
            if (entry == null) return null;

            _entries.Insert(0, entry);
            _logger?.Info(AppLogCategory.Editing,
                $"Phrase catalog: stored '{entry.Name}' (id={entry.Id}) — notes={entry.NoteCount}, " +
                $"chords={entry.ChordCount}, handShapes={entry.HandShapeCount}, anchors={entry.AnchorCount}, " +
                $"span={entry.LengthInBeats:0.###} beats.");
            RaiseChanged();
            return entry;
        }
    }

    public PhraseCatalogEntry? FindByName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                return entry;
        }

        return null;
    }

    public bool Rename(PhraseCatalogEntry entry, string newName)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length == 0) return false;

        lock (_sync)
        {
            _entries ??= LoadLocked();
            var index = _entries.FindIndex(e => e.Id == entry.Id);
            if (index < 0) return false;

            var path = Path.Combine(_directory, entry.Id + FileExtension);
            try
            {
                var project = _serializer.Load(path);
                project.Chart.SongName = trimmed;
                var temp = path + ".tmp";
                _serializer.Save(temp, project);
                AtomicFile.ReplaceWithRetry(temp, path);
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Editing,
                    $"Phrase catalog: could not rename '{entry.Id}': {ex.Message}");
                return false;
            }

            // The id (and therefore the file name) deliberately does not follow
            // the display name: a stable id is what keeps the store simple and
            // makes a rename a single in-place write.
            _entries[index] = new PhraseCatalogEntry
            {
                Id = entry.Id,
                Name = trimmed,
                Source = entry.Source,
                Payload = entry.Payload,
                CreatedUtc = entry.CreatedUtc
            };
            RaiseChanged();
            return true;
        }
    }

    public bool Remove(PhraseCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_sync)
        {
            _entries ??= LoadLocked();
            var path = Path.Combine(_directory, entry.Id + FileExtension);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Editing,
                    $"Phrase catalog: could not delete '{path}': {ex.Message}");
                return false;
            }

            var removed = _entries.RemoveAll(e => e.Id == entry.Id) > 0;
            if (removed) RaiseChanged();
            return removed;
        }
    }

    public void InvalidateCache()
    {
        lock (_sync)
        {
            _entries = null;
        }

        RaiseChanged();
    }

    private List<PhraseCatalogEntry> LoadLocked()
    {
        var entries = new List<PhraseCatalogEntry>();
        if (!Directory.Exists(_directory)) return entries;

        string[] files;
        try
        {
            files = Directory.GetFiles(_directory, "*" + FileExtension, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Editing,
                $"Phrase catalog: could not read '{_directory}': {ex.Message}");
            return entries;
        }

        foreach (var file in files)
        {
            // One unreadable entry must never cost the rest of the library, so
            // every file is parsed inside its own guard and simply skipped.
            try
            {
                var project = _serializer.Load(file);
                var entry = PhraseCatalogFragment.Read(
                    Path.GetFileNameWithoutExtension(file), project, File.GetLastWriteTimeUtc(file));
                if (entry != null)
                {
                    entries.Add(entry);
                }
                else
                {
                    _logger?.Warning(AppLogCategory.Editing,
                        $"Phrase catalog: '{file}' holds no phrase content — skipped.");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Editing,
                    $"Phrase catalog: '{file}' is unreadable and was skipped: {ex.Message}");
            }
        }

        entries.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return entries;
    }

    private string UniqueIdLocked(string name)
    {
        var baseId = Sanitize(name);
        var taken = new HashSet<string>(_entries!.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseId) && !File.Exists(Path.Combine(_directory, baseId + FileExtension)))
            return baseId;

        for (var i = 2; i < 10000; i++)
        {
            var candidate = $"{baseId}-{i.ToString(CultureInfo.InvariantCulture)}";
            if (!taken.Contains(candidate) && !File.Exists(Path.Combine(_directory, candidate + FileExtension)))
                return candidate;
        }

        return baseId + "-" + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    ///     Folds a free-text phrase name into a filesystem-safe id. The DISPLAY
    ///     name is stored inside the fragment, so this may be as lossy as it likes
    ///     — it only has to be unique and legal on every platform.
    /// </summary>
    internal static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        var id = builder.ToString().Trim('-');
        if (id.Length > MaxIdLength) id = id[..MaxIdLength].TrimEnd('-');
        return id.Length == 0 ? "phrase" : id;
    }

    private void RaiseChanged() => CatalogChanged?.Invoke();
}
