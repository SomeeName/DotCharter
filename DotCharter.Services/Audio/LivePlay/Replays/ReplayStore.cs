using System.Text.Json;
using DotCharter.Services.Gameplay.Profiles;

namespace DotCharter.Services.Audio.LivePlay.Replays;

/// <summary>
///     Owns the ACTIVE player profile's run-replay files (`replays/` data dir
///     — inside the `profiles` tree, so LAN-package exclusion and backup
///     inclusion are inherited from the existing directory rules). Replays
///     are kilobytes, so there is deliberately NO size-cap sweep (contrast
///     the take store); a dangling <c>PracticeSessionRecord.ReplayFile</c>
///     name must read as "no replay", never an error.
/// </summary>
public interface IReplayStore
{
    /// <summary>The active profile's replays directory (created on demand).</summary>
    string ReplaysDirectory { get; }

    /// <summary>Persists a replay; returns its file name (relative to
    /// <see cref="ReplaysDirectory" />) or null on failure. Never throws.</summary>
    string? Save(RunReplay replay);

    /// <summary>Loads a replay by stored name; null when missing, unreadable
    /// or not a replay. Never throws.</summary>
    RunReplay? TryLoad(string fileName);
}

public sealed class ReplayStore : IReplayStore
{
    public const string ReplaysDirectoryName = "replays";

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string? _fixedDirectory;
    private readonly IPlayerProfileService? _playerProfiles;
    private readonly IAppLogger? _logger;

    /// <summary>Profile-scoped store (production wiring).</summary>
    public ReplayStore(IPlayerProfileService playerProfiles, IAppLogger? logger = null)
    {
        _playerProfiles = playerProfiles;
        _logger = logger;
    }

    /// <summary>Fixed-directory store (tests, tools).</summary>
    public ReplayStore(string directory, IAppLogger? logger = null)
    {
        _fixedDirectory = directory;
        _logger = logger;
    }

    public string ReplaysDirectory
    {
        get
        {
            if (_fixedDirectory != null) return _fixedDirectory;
            var profiles = _playerProfiles!;
            return Path.Combine(profiles.GetDataDirectory(profiles.ActiveProfile.Id), ReplaysDirectoryName);
        }
    }

    public string? Save(RunReplay replay)
    {
        try
        {
            var name = $"replay-{replay.RecordedUtc:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..40] + ".json";
            Directory.CreateDirectory(ReplaysDirectory);
            // Atomic-enough for a new unique name; tolerant readers treat a
            // torn file as "no replay".
            File.WriteAllText(Path.Combine(ReplaysDirectory, name),
                JsonSerializer.Serialize(replay, JsonOptions));
            return name;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Replay not writable: {ex.Message}");
            return null;
        }
    }

    public RunReplay? TryLoad(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        try
        {
            var path = Path.Combine(ReplaysDirectory, fileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<RunReplay>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger?.Debug(AppLogCategory.Audio, $"Replay unreadable ('{fileName}'): {ex.Message}");
            return null;
        }
    }
}
