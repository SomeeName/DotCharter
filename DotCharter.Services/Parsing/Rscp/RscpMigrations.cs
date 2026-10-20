using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Services.Parsing.Xml.Vocals;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>The outcome of a load-time upgrade, for logging and for tests.</summary>
/// <param name="Migrated">True when the file on disk was an older format than the one in memory.</param>
/// <param name="FromVersion">The version the file effectively was.</param>
/// <param name="Notes">One line per thing that was recovered or could not be.</param>
public sealed record RscpMigrationResult(bool Migrated, int FromVersion, IReadOnlyList<string> Notes)
{
    public static readonly RscpMigrationResult None = new(false, 3, []);
}

/// <summary>
///     Upgrades older <c>.rscp</c> projects to the current schema on load.
/// </summary>
/// <remarks>
///     The format is inherited from RsCharter (Java), which shipped three versions and
///     its own upgraders. Ported faithfully:
///     <list type="bullet">
///         <item>
///             <b>v1 → v2</b> set editor UI state (edit mode, selected arrangement /
///             level / playhead). DotCharter does not persist any of that, so it is a
///             genuine NO-OP here — recorded rather than silently skipped, so the next
///             reader knows it was considered.
///         </item>
///         <item>
///             <b>v2 → v3</b> is the one that matters. In v2 the arrangements were NOT
///             embedded in the project: it carried an <c>arrangementFiles</c> list naming
///             sibling Rocksmith XML files, and vocals lived in their own
///             <c>vocals.xml</c>. v3 embeds both.
///         </item>
///     </list>
///     <para>
///         <b>Why this was a data-loss bug.</b> Without the upgrade, a v2 file loaded
///         through the v3 parser finds no embedded <c>&lt;arrangements&gt;</c> and yields
///         an EMPTY chart. The user opens their old project, sees nothing, and the moment
///         they save, the empty project overwrites it. Worse, the version number was not
///         even consulted: a missing or unparseable <c>chartFormatVersion</c> defaulted to
///         3, so the file was confidently misread.
///     </para>
///     <para>
///         <b>Detection is by STRUCTURE, not by the declared number.</b> The number cannot
///         be trusted — RsCharter's own field default is 3, so a file written before the
///         element existed still deserializes as "3". A project that names arrangement
///         files but embeds no arrangements IS a v2 project, whatever it claims.
///     </para>
/// </remarks>
public sealed class RscpMigrations
{
    private readonly IAppLogger? _logger;

    public RscpMigrations(IAppLogger? logger = null) => _logger = logger;

    /// <summary>Current schema version. Bump WITH a migration, never without one.</summary>
    public const int CurrentVersion = 3;

    /// <summary>
    ///     True when <paramref name="doc" /> is a pre-v3 project regardless of the version
    ///     it declares: it names external arrangement files but embeds no arrangements.
    /// </summary>
    public static bool NeedsArrangementRecovery(XDocument doc)
    {
        var named = doc.Descendants("arrangementFiles").Elements("string")
            .Any(e => !string.IsNullOrWhiteSpace(e.Value));
        if (!named) return false;

        // An embedded arrangements node with at least one child means v3 already.
        var embedded = doc.Descendants("arrangements").Elements().Any();
        return !embedded;
    }

    /// <summary>
    ///     Brings <paramref name="project" /> up to <see cref="CurrentVersion" />, pulling
    ///     in whatever the older layout kept outside the file. Never throws: a sibling file
    ///     that is missing or unreadable becomes a NOTE, because a partially recovered
    ///     project the user can see is strictly better than an exception or an empty one.
    /// </summary>
    /// <param name="doc">The project document as loaded.</param>
    /// <param name="filePath">The .rscp path — sibling files resolve against its folder.</param>
    /// <param name="project">The project parsed so far (header + beats + any arrangements).</param>
    public RscpMigrationResult Upgrade(XDocument doc, string filePath, ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(project);

        if (!NeedsArrangementRecovery(doc)) return RscpMigrationResult.None;

        var declared = project.Chart.ChartFormatVersion;
        var notes = new List<string>();
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";

        var recovered = LoadArrangements(directory, project.ArrangementFiles, notes);

        if (recovered.Count > 0)
        {
            // RscpArrangementParser fabricates an empty "Lead Guitar" arrangement when a
            // document has no embedded ones — which is what made this bug so deceptive:
            // a v2 project did not open as visibly broken, it opened as a plausible FRESH
            // chart, giving the user no reason to suspect anything was lost before they
            // saved over it. This document is by definition one of those (that is what
            // NeedsArrangementRecovery means), so whatever is in the list is that
            // placeholder. Drop it now that we have the real thing.
            project.Arrangements.Clear();
            foreach (var arrangement in recovered) project.Arrangements.Add(arrangement);
        }

        RecoverVocals(directory, project, notes);

        project.Chart.ChartFormatVersion = CurrentVersion;

        _logger?.Warning(AppLogCategory.Project,
            $"RSCP '{filePath}' is a pre-v3 project (declared {declared}); upgraded on load. " +
            string.Join(" ", notes));

        return new RscpMigrationResult(true, declared, notes);
    }

    /// <summary>v2 kept each arrangement in its own Rocksmith XML next to the project.</summary>
    private List<Arrangement> LoadArrangements(
        string directory, IEnumerable<string> arrangementFiles, List<string> notes)
    {
        var reader = new RsXmlSongReader();
        var recovered = new List<Arrangement>();

        foreach (var fileName in arrangementFiles.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                notes.Add($"Arrangement file '{fileName}' is missing — that arrangement could not be recovered.");
                continue;
            }

            try
            {
                var loaded = reader.Read(path);
                recovered.AddRange(loaded.Arrangements);
            }
            catch (Exception ex)
            {
                // One unreadable sibling must not cost the user the other arrangements.
                notes.Add($"Arrangement file '{fileName}' could not be read ({ex.Message}).");
                _logger?.Warning(AppLogCategory.Project,
                    $"Pre-v3 upgrade: failed to read arrangement '{path}': {ex.Message}");
            }
        }

        notes.Add($"Recovered {recovered.Count} arrangement(s) from external files.");
        return recovered;
    }

    /// <summary>v2 kept vocals in a sibling vocals.xml.</summary>
    private void RecoverVocals(string directory, ChartProject project, List<string> notes)
    {
        if (project.VocalTracks.Any(t => t.Phrases.Count > 0)) return;

        var path = Path.Combine(directory, "vocals.xml");
        if (!File.Exists(path)) return;

        try
        {
            var loaded = new RsXmlVocalsReader().Read(path);
            foreach (var track in loaded.VocalTracks.Where(t => t.Phrases.Count > 0))
            {
                project.VocalTracks.Add(track);
                notes.Add($"Recovered {track.Phrases.Count} vocal phrase(s) from vocals.xml.");
            }
        }
        catch (Exception ex)
        {
            notes.Add($"vocals.xml could not be read ({ex.Message}).");
            _logger?.Warning(AppLogCategory.Project,
                $"Pre-v3 upgrade: failed to read vocals '{path}': {ex.Message}");
        }
    }
}
