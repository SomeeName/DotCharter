namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
/// CRUD store for player-saved tone profiles, persisted as
/// <see cref="RigConfig.UserTonePresets"/> via the injected
/// <see cref="IRigConfigService"/> — the SAME load → transform → save shape
/// as <c>MatchPatternStore</c>. Every read is throw-free
/// (a corrupt/absent list logs and yields an empty result), and returned presets
/// are always fresh <see cref="DspToneOrigin.User"/> materializations so a caller
/// can never mutate the stored DTOs in place.
/// </summary>
public sealed class UserTonePresetStore
{
    private readonly IRigConfigService _config;
    private readonly IAppLogger? _logger;

    public UserTonePresetStore(IRigConfigService config, IAppLogger? logger = null)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>All saved user presets in stored (insertion) order. Never throws —
    /// on any failure logs and returns an empty list.</summary>
    public IReadOnlyList<DspTonePreset> List()
    {
        try
        {
            var config = _config.LoadRigConfig();
            var dtos = config.UserTonePresets ?? [];
            var result = new List<DspTonePreset>(dtos.Count);
            foreach (var dto in dtos)
            {
                if (dto is null) continue;
                result.Add(dto.ToPreset());
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                "UserTonePresetStore: failed to read user tone presets; treating as empty.", ex);
            return [];
        }
    }

    /// <summary>Upserts a user preset by id (replace in place when the id already
    /// exists, else append). Returns the stored preset (User origin) or null on a
    /// persistence failure.</summary>
    public DspTonePreset? Upsert(DspTonePreset preset)
    {
        try
        {
            var dto = UserTonePresetDto.FromPreset(preset);
            // Update: the list read-modify-write + serialize is atomic under the
            // config lock, so a concurrent SaveAppConfig can't serialize the list
            // mid-mutation ("Collection was modified" → silently dropped save).
            _config.UpdateRigConfig(config =>
            {
                config.UserTonePresets ??= [];

                var index = config.UserTonePresets.FindIndex(p => p is not null && p.Id == dto.Id);
                if (index >= 0)
                    config.UserTonePresets[index] = dto;
                else
                    config.UserTonePresets.Add(dto);
            });
            return dto.ToPreset();
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"UserTonePresetStore: failed to save user tone preset '{preset.Id}'.", ex);
            return null;
        }
    }

    /// <summary>Removes the user preset with <paramref name="id"/>. Returns whether
    /// anything was removed; false (never throws) on unknown id or a failure.</summary>
    public bool Remove(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        try
        {
            var removed = false;
            _config.UpdateRigConfig(config =>
            {
                if (config.UserTonePresets is null) return;
                removed = config.UserTonePresets.RemoveAll(p => p is not null && p.Id == id) > 0;
            });
            return removed;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"UserTonePresetStore: failed to delete user tone preset '{id}'.", ex);
            return false;
        }
    }
}
