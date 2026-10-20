using System.Text.Json;
using DotCharter.Services.Library;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Services.Gameplay.Setlists;

/// <summary>What a setlist read found. The three degraded-read outcomes are kept
/// apart on purpose (DotCharter.Services/AGENTS.md, "A degraded READ is never
/// authoritative").</summary>
public enum PlaySetlistReadOutcome
{
    /// <summary>The file was read and parsed.</summary>
    Loaded,

    /// <summary>No file: a player who has made no setlist yet. Starts fresh.</summary>
    Absent,

    /// <summary>The bytes are not a setlist store. Moved aside as
    /// <c>setlists.corrupt-&lt;stamp&gt;.json</c> and started fresh; when the move
    /// fails nothing is written over the original.</summary>
    Corrupt,

    /// <summary>The file could not be opened right now (a sharing violation, an
    /// antivirus or cloud-sync hold). Presumed INTACT: the empty list is served
    /// but every save is refused until a read succeeds.</summary>
    Unreadable
}

/// <summary>A read's outcome and the setlists it produced (empty unless
/// <see cref="PlaySetlistReadOutcome.Loaded" />).</summary>
public sealed record PlaySetlistRead(PlaySetlistReadOutcome Outcome, IReadOnlyList<PlaySetlist> Setlists)
{
    /// <summary>True when a save may follow this read.</summary>
    public bool IsAuthoritative => Outcome != PlaySetlistReadOutcome.Unreadable;
}

/// <summary>A player profile's Play Mode setlists.</summary>
public interface IPlaySetlistStore
{
    /// <summary>Reads one profile's setlists. Never throws.</summary>
    PlaySetlistRead Load(Guid profileId);

    /// <summary>Replaces one profile's setlists. Refused (false) until a read of
    /// that profile has succeeded, after an UNREADABLE read, after a CORRUPT read
    /// that could not be moved aside, and for the registry's degraded stand-in
    /// profile. Never throws.</summary>
    bool Save(Guid profileId, IReadOnlyList<PlaySetlist> setlists);
}

/// <summary>
///     <see cref="IPlaySetlistStore" /> over one <c>setlists.json</c> per player
///     profile, beside the profile's stats in <c>profiles/&lt;id&gt;/</c> — so the
///     profile tree's existing rules cover it: excluded from update payloads,
///     included in whole-install backups, deleted with the profile. Written
///     through <see cref="AtomicFile" />.
///     <para>
///         The profile is an ARGUMENT, not resolved per call: the caller (the
///         setlist view model) reads and writes the profile it loaded, so a save
///         that lands after a profile switch can never write the outgoing player's
///         lists into the incoming player's file.
///     </para>
/// </summary>
public sealed class PlaySetlistStore : IPlaySetlistStore
{
    public const string StoreFileName = "setlists.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Func<Guid, string> _directoryOf;
    private readonly Func<Guid, bool> _isDegraded;
    private readonly IAppLogger? _logger;
    private readonly object _gate = new();

    /// <summary>The profiles whose last read may be written over.</summary>
    private readonly HashSet<Guid> _authoritative = [];

    /// <summary>Production: one file in each profile's data directory.</summary>
    public PlaySetlistStore(IToneRigProfileScope profiles, IAppLogger? logger = null)
        : this(profiles.GetDataDirectory, profiles.IsDegradedProfile, logger)
    {
    }

    /// <summary>Tests and tools: <paramref name="directoryOf" /> maps a profile to
    /// the folder holding its <see cref="StoreFileName" />.</summary>
    public PlaySetlistStore(Func<Guid, string> directoryOf, Func<Guid, bool>? isDegraded = null,
        IAppLogger? logger = null)
    {
        _directoryOf = directoryOf;
        _isDegraded = isDegraded ?? (static _ => false);
        _logger = logger;
    }

    public PlaySetlistRead Load(Guid profileId)
    {
        lock (_gate)
        {
            _authoritative.Remove(profileId);
            string path;
            try
            {
                path = PathFor(profileId);
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Application,
                    $"Setlists: no data directory for the profile ({ex.GetType().Name}: {ex.Message}).");
                return new PlaySetlistRead(PlaySetlistReadOutcome.Unreadable, []);
            }

            try
            {
                if (!File.Exists(path))
                {
                    _authoritative.Add(profileId);
                    return new PlaySetlistRead(PlaySetlistReadOutcome.Absent, []);
                }

                var file = JsonSerializer.Deserialize<StoreFile>(AtomicFile.ReadAllText(path), JsonOptions)
                           ?? throw new JsonException("The setlist store is null.");
                _authoritative.Add(profileId);
                return new PlaySetlistRead(PlaySetlistReadOutcome.Loaded, Normalize(file.Setlists));
            }
            catch (JsonException ex)
            {
                // CORRUPT: readable bytes that are not a store. Moved aside so the
                // player keeps them; if they cannot be moved, nothing writes over them.
                if (TryQuarantine(path, ex)) _authoritative.Add(profileId);
                return new PlaySetlistRead(PlaySetlistReadOutcome.Corrupt, []);
            }
            catch (Exception ex)
            {
                // UNREADABLE: presumed intact. Not authoritative, so no save follows.
                _logger?.Warning(AppLogCategory.Application,
                    $"Setlists could not be read ({ex.GetType().Name}: {ex.Message}); not writing over them.");
                return new PlaySetlistRead(PlaySetlistReadOutcome.Unreadable, []);
            }
        }
    }

    public bool Save(Guid profileId, IReadOnlyList<PlaySetlist> setlists)
    {
        lock (_gate)
        {
            if (!_authoritative.Contains(profileId) || _isDegraded(profileId)) return false;
            try
            {
                var file = new StoreFile { Setlists = setlists.Select(s => (PlaySetlist?)s.Clone()).ToList() };
                AtomicFile.WriteAllText(PathFor(profileId), JsonSerializer.Serialize(file, JsonOptions));
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Application,
                    $"Setlists could not be saved ({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }
    }

    private string PathFor(Guid profileId) => Path.Combine(_directoryOf(profileId), StoreFileName);

    /// <summary>Drops null lists and entries a hand edit could leave, and gives
    /// every list an id, so the view model never meets a hole. A library song saved
    /// under the old <c>psarc:&lt;key&gt;</c> identity is read as <c>lib:&lt;key&gt;</c>; the next
    /// save writes it so.</summary>
    private static List<PlaySetlist> Normalize(List<PlaySetlist?>? lists)
    {
        var result = new List<PlaySetlist>();
        foreach (var list in lists ?? [])
        {
            if (list is null) continue;
            list.Entries = (list.Entries ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.SongKey))
                .ToList();
            foreach (var entry in list.Entries) entry.SongKey = LibrarySongIdentity.Normalize(entry.SongKey);
            if (list.Id == Guid.Empty) list.Id = Guid.NewGuid();
            result.Add(list);
        }

        return result;
    }

    private bool TryQuarantine(string path, Exception cause)
    {
        var aside = Path.Combine(Path.GetDirectoryName(path) ?? "",
            $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}{Path.GetExtension(path)}");
        try
        {
            File.Move(path, aside, true);
            _logger?.Warning(AppLogCategory.Application,
                $"Setlists were unparseable ({cause.Message}); moved to '{Path.GetFileName(aside)}' and starting fresh.");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Application,
                $"Setlists are unparseable ({cause.Message}) and could not be moved aside; not writing over them.", ex);
            return false;
        }
    }

    /// <summary>On-disk shape. An object, not a bare array, so a later field has
    /// somewhere to go.</summary>
    internal sealed class StoreFile
    {
        public List<PlaySetlist?>? Setlists { get; set; } = [];
    }
}
