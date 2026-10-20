using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Session;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The services caching a file a backup import replaces. Each cache is dropped
///     before anything reads again, or the next config save writes the stale copy back.
/// </summary>
public sealed class ImportedConfigStores(
    IConfigurationService config,
    IPlayerProfileService players,
    IPracticeProfileService practice,
    IPhraseCatalogService phrases,
    IProjectSessionService session)
{
    /// <summary>Idempotent, so it runs again after a failed import that may still have replaced files.</summary>
    public void DropCaches()
    {
        config.InvalidateCache();
        players.InvalidateCache();
        practice.InvalidateCache();
        phrases.InvalidateCache();
    }

    /// <summary>Drops every cache, then has the project session re-read the imported config.</summary>
    public void Reload()
    {
        DropCaches();
        session.LoadAppConfig();
    }
}
