using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="IDspTonePresetService" /> for tone-browser tests: a small
/// seeded factory catalogue plus a mutable user list, with per-op call recording
/// so tests can assert the browser VM drives the service. Mirrors the real
/// service's semantics — factory presets are immutable (overwrite/delete refused),
/// <see cref="AllPresets"/> is factory-then-user, and a user preset with a new
/// name gets a fresh id while an overwrite replaces in place.
/// </summary>
public sealed class FakeDspTonePresetService : IDspTonePresetService
{
    private readonly List<DspTonePreset> _factory;
    private readonly List<DspTonePreset> _user = [];
    private int _idSeq = 1;

    /// <summary>Ids passed to <see cref="Apply"/> in call order.</summary>
    public List<string> Applied { get; } = [];

    /// <summary>Rigs saved via <see cref="SaveUser"/> with the overwrite id used
    /// (null = create).</summary>
    public List<(string Name, DspToneCategory Category, DcRigPreset Rig, string? OverwriteId)> Saves { get; } = [];

    /// <summary>Ids passed to <see cref="DeleteUser"/>.</summary>
    public List<string> Deletes { get; } = [];

    public FakeDspTonePresetService(IReadOnlyList<DspTonePreset>? factory = null) => _factory = (factory ?? DefaultFactory()).ToList();

    public IReadOnlyList<DspTonePreset> Presets => _factory;
    public IReadOnlyList<DspTonePreset> UserPresets => _user;

    public IReadOnlyList<DspTonePreset> AllPresets => _factory.Concat(_user).ToList();

    public DspTonePreset? FindById(string id)
        => AllPresets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public DcRigApplyResult Apply(DspTonePreset preset)
    {
        Applied.Add(preset.Id);
        return new DcRigApplyResult(0, 0, 0, 0, 0, true);
    }

    public DcRigApplyResult? ApplyById(string id)
    {
        var p = FindById(id);
        return p is null ? null : Apply(p);
    }

    /// <summary>Per-seat applies are recorded separately (ABI v28) so a test can
    /// assert WHICH player got WHICH tone — the whole point of the feature.</summary>
    public List<(int Player, string PresetId)> AppliedPerPlayer { get; } = [];

    /// <summary>The whole PRESET each per-seat apply carried, not just its id —
    /// a test that has to prove one seat's RIG did not land on another needs the
    /// modules, and an id ("compare") is the same string either way.</summary>
    public List<(int Player, DspTonePreset Preset)> AppliedPresetsPerPlayer { get; } = [];

    public DcRigApplyResult ApplyToPlayer(int player, DspTonePreset preset)
    {
        AppliedPerPlayer.Add((player, preset.Id));
        AppliedPresetsPerPlayer.Add((player, preset));
        Applied.Add(preset.Id);
        return new DcRigApplyResult(0, 0, 0, 0, 0, true);
    }

    public DcRigApplyResult? ApplyToPlayerById(int player, string id)
    {
        var p = FindById(id);
        return p is null ? null : ApplyToPlayer(player, p);
    }

    public DspTonePreset SaveUser(
        string name, DspToneCategory category, string description, DcRigPreset rig, string? overwriteId = null)
    {
        Saves.Add((name, category, rig, overwriteId));

        // Refuse to overwrite a factory preset — return it unchanged.
        if (overwriteId != null && _factory.Any(p => p.Id == overwriteId))
            return _factory.First(p => p.Id == overwriteId);

        if (overwriteId != null)
        {
            var idx = _user.FindIndex(p => p.Id == overwriteId);
            if (idx >= 0)
            {
                var replaced = new DspTonePreset(overwriteId, name, category, description, rig, DspToneOrigin.User);
                _user[idx] = replaced;
                return replaced;
            }
        }

        var created = new DspTonePreset($"user-{_idSeq++}", name, category, description, rig, DspToneOrigin.User);
        _user.Add(created);
        return created;
    }

    public bool DeleteUser(string id)
    {
        Deletes.Add(id);
        if (_factory.Any(p => p.Id == id)) return false;
        return _user.RemoveAll(p => p.Id == id) > 0;
    }

    private static IReadOnlyList<DspTonePreset> DefaultFactory() =>
    [
        Factory("fac-clean-1", "Glass Clean", DspToneCategory.Clean),
        Factory("fac-clean-2", "Chime", DspToneCategory.Clean),
        Factory("fac-crunch-1", "Edge Crunch", DspToneCategory.Crunch),
        Factory("fac-highgain-1", "Modern Metal", DspToneCategory.HighGain),
        Factory("fac-lead-1", "Singing Lead", DspToneCategory.Lead),
        Factory("fac-ambient-1", "Cathedral", DspToneCategory.Ambient),
    ];

    private static DspTonePreset Factory(string id, string name, DspToneCategory cat)
        => new(id, name, cat, $"{name} description", new DcRigPreset([]));
}
