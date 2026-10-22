using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Setlists;

namespace DotCharter.Services.Session;

/// <summary>
///     <see cref="IProjectPathRekey" /> over the stores an own chart is keyed in by its project
///     path. Every player profile is covered, not just the active one: each has its own
///     practice store and setlists, and the leaderboards read them all.
///     <para>
///         Not moved, with the reason, in <c>docs/agents/services/gameplay.md</c> (path-keyed
///         stores): checkpoints, AI chats and tone edits follow through the Save As tail the
///         migration shares; the rest are keyed by audio content or identity, not by project path.
///     </para>
/// </summary>
public sealed class ProjectPathRekey : IProjectPathRekey
{
    private readonly IPracticeProfileService _practice;
    private readonly IPlaySetlistStore _setlists;
    private readonly IPlayerProfileService _profiles;
    private readonly IConfigurationService _config;
    private readonly IAppLogger _logger;

    public ProjectPathRekey(IPracticeProfileService practice, IPlaySetlistStore setlists, IPlayerProfileService profiles,
        IConfigurationService config, IAppLogger logger)
    {
        _practice = practice;
        _setlists = setlists;
        _profiles = profiles;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProjectRekeyResult Rekey(string fromPath, string toPath)
    {
        if (string.IsNullOrWhiteSpace(fromPath) || string.IsNullOrWhiteSpace(toPath) ||
            ProjectStoreKeys.SamePath(fromPath, toPath))
        {
            return new ProjectRekeyResult(0, 0, 0);
        }

        var result = new ProjectRekeyResult(
            Guarded("practice stats", () => _practice.RekeySong(fromPath, toPath)),
            Guarded("setlists", () => RekeySetlists(fromPath, toPath)),
            Guarded("per-song settings", () => RekeyConfig(fromPath, toPath)));
        if (result.Total == 0) return result;

        _logger.Info(AppLogCategory.Project,
            $"Project '{fromPath}' moved to '{toPath}': re-keyed {result.PracticeEntries} practice entr(ies), " +
            $"{result.SetlistEntries} setlist song(s) and {result.ConfigEntries} per-song setting(s).");
        return result;
    }

    /// <summary>One store failing must not stop the others: each is atomic and idempotent, so a
    /// failed one is simply left as it was.</summary>
    private int Guarded(string store, Func<int> move)
    {
        try
        {
            return move();
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project, $"The {store} were not re-keyed for the moved project; they are left as they are.", ex);
            return 0;
        }
    }

    private int RekeySetlists(string fromPath, string toPath)
    {
        var moved = 0;
        foreach (var profile in _profiles.Profiles)
        {
            var read = _setlists.Load(profile.Id);
            if (read.Outcome != PlaySetlistReadOutcome.Loaded) continue;

            var changed = 0;
            foreach (var entry in read.Setlists.SelectMany(list => list.Entries))
            {
                if (!ProjectStoreKeys.SamePath(entry.SongKey, fromPath)) continue;
                entry.SongKey = toPath;
                changed++;
            }

            // A refused save (a degraded profile) leaves the file as it was.
            if (changed > 0 && _setlists.Save(profile.Id, read.Setlists)) moved += changed;
        }

        return moved;
    }

    private int RekeyConfig(string fromPath, string toPath)
    {
        // Looked at first, so a project with no per-song settings does not rewrite appconfig.
        var current = _config.LoadAppConfig();
        if (!current.SongTonePreferences.Keys.Any(k => ProjectStoreKeys.SamePath(k, fromPath)) &&
            !current.DropTunerSongs.Any(s => ProjectStoreKeys.SamePath(s, fromPath)))
        {
            return 0;
        }

        var moved = 0;
        _config.Update(config =>
        {
            moved = 0;
            foreach (var key in config.SongTonePreferences.Keys.Where(k => ProjectStoreKeys.SamePath(k, fromPath)).ToList())
            {
                // The new path's own setting wins; the old one goes either way.
                if (!config.SongTonePreferences.ContainsKey(toPath))
                    config.SongTonePreferences[toPath] = config.SongTonePreferences[key];
                config.SongTonePreferences.Remove(key);
                moved++;
            }

            var removed = config.DropTunerSongs.RemoveAll(s => ProjectStoreKeys.SamePath(s, fromPath));
            if (removed == 0) return;
            if (!config.DropTunerSongs.Contains(toPath, StringComparer.OrdinalIgnoreCase)) config.DropTunerSongs.Add(toPath);
            moved += removed;
        });
        return moved;
    }
}
