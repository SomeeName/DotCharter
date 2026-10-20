namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
/// Per-song tone-source preference for Play Mode runs, persisted in
/// <see cref="AppConfig.SongTonePreferences"/> keyed by the SAME song identity
/// Live Play's practice profiles use (<c>"lib:&lt;SongKey&gt;"</c> for library
/// songs, the project path for own charts). Consulted by
/// <c>RsTonePlaybackCoordinator.BeginRun</c>:
/// <list type="bullet">
/// <item><see cref="InGameMode"/> — force driving the song's converted RS tones
/// even when the global <see cref="AppConfig.UseInGameTones"/> toggle is off.</item>
/// <item><see cref="MyToneMode"/> — apply the user tone profile in
/// <see cref="PresetId"/> once at run start and never drive switches.</item>
/// <item><see cref="GlobalMode"/> — follow the global toggle. This is the
/// default; entries in this mode are removed from the dictionary rather than
/// stored, so the config only carries real overrides.</item>
/// </list>
/// Unknown <see cref="Mode"/> values read as <see cref="GlobalMode"/> (schema
/// tolerant, like the rest of the config).
/// </summary>
public class SongTonePreference
{
    public const string GlobalMode = "global";
    public const string InGameMode = "ingame";
    public const string MyToneMode = "mytone";

    /// <summary>One of <see cref="GlobalMode"/>/<see cref="InGameMode"/>/
    /// <see cref="MyToneMode"/>.</summary>
    public string Mode { get; set; } = GlobalMode;

    /// <summary>The user tone-preset id applied when <see cref="Mode"/> is
    /// <see cref="MyToneMode"/> (see <c>AppConfig.UserTonePresets</c>).</summary>
    public string PresetId { get; set; } = string.Empty;
}
