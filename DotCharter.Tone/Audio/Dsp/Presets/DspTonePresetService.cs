using System.Text;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
/// Default <see cref="IDspTonePresetService"/>: exposes the immutable
/// <see cref="DspTonePresetLibrary"/> factory catalogue plus the persisted user
/// profiles (<see cref="UserTonePresetStore"/> over
/// <c>RigConfig.UserTonePresets</c>), and applies presets through a
/// <see cref="DcRigApplier"/> onto the injected <see cref="IDspChainService"/>.
/// Throw-free: a bad store read yields an empty user list, an apply against the
/// null chain is a no-op, and capture/save never throw.
/// </summary>
public sealed class DspTonePresetService : IDspTonePresetService
{
    private readonly IDspChainService _chain;
    private readonly UserTonePresetStore _store;
    private readonly DcRigApplier _applier;
    private readonly IAppLogger? _logger;
    private readonly HashSet<string> _factoryIds;
    private readonly Func<string?>? _namCaptureDirectory;

    // ABI v28: one applier per SEAT, built lazily. Null when the app is
    // running without the per-player registry (headless tests), in which case
    // every seat resolves to the single injected chain — i.e. exactly the
    // pre-v28 behaviour.
    private readonly IPlayerDspChains? _chains;
    private readonly Dictionary<int, DcRigApplier> _playerAppliers = [];
    private readonly object _playerApplierGate = new();

    /// <param name="chain">The DSP tone chain the presets are applied onto (through
    /// the internal rig applier).</param>
    /// <param name="config">Configuration store backing user-preset persistence
    /// (wrapped by the internal user preset store).</param>
    /// <param name="logger">Optional diagnostics logger; null disables logging.</param>
    /// <param name="namCaptureDirectory">Optional provider of the local NAM
    /// capture directory (the library root). When set, a <c>namamp</c> preset's
    /// <c>modelPath</c> that is a BARE bundled file name (no directory) is
    /// resolved to an absolute path under that directory at apply time — this is
    /// how the capture-backed factory presets find their bundled <c>.nam</c>
    /// files. Null (tests / no NAM library) leaves modelPath values untouched.</param>
    /// <param name="chains">Optional per-player chain registry (ABI v28). When
    /// supplied, <see cref="ApplyToPlayer" /> can put a DIFFERENT tone on each
    /// seat; without it every seat resolves to
    /// <paramref name="chain" />.</param>
    public DspTonePresetService(
        IDspChainService chain, IRigConfigService config, IAppLogger? logger = null,
        Func<string?>? namCaptureDirectory = null, IPlayerDspChains? chains = null)
    {
        _chain = chain;
        _chains = chains;
        _logger = logger;
        _store = new UserTonePresetStore(config, logger);
        _applier = new DcRigApplier(chain, logger);
        _namCaptureDirectory = namCaptureDirectory;
        Presets = DspTonePresetLibrary.All;
        _factoryIds = new HashSet<string>(Presets.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<DspTonePreset> Presets { get; }

    public IReadOnlyList<DspTonePreset> UserPresets => _store.List();

    public IReadOnlyList<DspTonePreset> AllPresets
    {
        get
        {
            var user = _store.List();
            var all = new List<DspTonePreset>(Presets.Count + user.Count);
            all.AddRange(Presets);
            all.AddRange(user);
            return all;
        }
    }

    public DspTonePreset? FindById(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var p in Presets)
        {
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                return p;
        }

        foreach (var p in _store.List())
        {
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                return p;
        }

        return null;
    }

    // The default-filling / factory-loudness / NAM-path rules live in the
    // shared DcRigPresetPreparation pipeline so the offline take re-amp
    // prepares presets IDENTICALLY to this live apply.
    public DcRigApplyResult Apply(DspTonePreset preset)
        => _applier.Apply(DcRigPresetPreparation.Prepare(preset, _namCaptureDirectory?.Invoke()));

    public DcRigApplyResult? ApplyById(string id)
    {
        var preset = FindById(id);
        return preset is null ? null : Apply(preset);
    }

    /// <inheritdoc />
    public DcRigApplyResult ApplyToPlayer(int player, DspTonePreset preset)
    {
        var applier = ApplierFor(player);
        if (applier is null)
        {
            // No registry, so seat p > 0 has no chain of its own. Applying to
            // the single chain instead would be WORSE than doing nothing: it is
            // how player 2's restore silently overwrites player 1's board (and
            // how player 1's next persist then saves player 2's rig as if the
            // player had built it).
            _logger?.Warning(AppLogCategory.Audio,
                $"Tone preset for player {player + 1} not applied: this build has no per-player DSP chains.");
            return new DcRigApplyResult(0, 0, 0, 0, 0, NoOp: true);
        }

        return applier.Apply(DcRigPresetPreparation.Prepare(preset, _namCaptureDirectory?.Invoke()));
    }

    /// <inheritdoc />
    public DcRigApplyResult? ApplyToPlayerById(int player, string id)
    {
        var preset = FindById(id);
        return preset is null ? null : ApplyToPlayer(player, preset);
    }

    /// <summary>
    ///     The rig applier for one seat, created once and kept.
    ///     <para>
    ///         <b>An applier must be per-CHAIN and long-lived</b>: it diffs the
    ///         incoming preset against that chain's live node list, which is how a
    ///         mid-song tone switch stays a minimal edit instead of a teardown.
    ///         Seat 0 reuses the applier built in the constructor so the shipped
    ///         single-player path is bit-for-bit what it was.
    ///     </para>
    /// </summary>
    private DcRigApplier? ApplierFor(int player)
    {
        if (player <= 0) return _applier;
        // Seat p > 0 with no registry has no chain. Null — never seat 0's applier.
        if (_chains is null) return null;
        lock (_playerApplierGate)
        {
            if (_playerAppliers.TryGetValue(player, out var existing)) return existing;
            var chain = _chains.For(player);
            // The registry clamps, so a stray index can land back on seat 0 —
            // in which case reuse seat 0's applier rather than minting a second
            // one for the same chain (two appliers over one chain each believe
            // they know its composition, and one of them is wrong).
            var applier = ReferenceEquals(chain, _chain) ? _applier : new DcRigApplier(chain, _logger);
            _playerAppliers[player] = applier;
            return applier;
        }
    }

    // There is no CaptureCurrent: nothing called it. See the
    // note on IDspTonePresetService: the live "save my pedalboard as a tone" path
    // is SaveUser(…, PedalboardViewModel.BuildCurrentRig()), and one snapshot
    // implementation is the point.

    public DspTonePreset SaveUser(
        string name, DspToneCategory category, string description, DcRigPreset rig, string? overwriteId = null)
    {
        rig ??= new DcRigPreset([]);

        // Refuse to overwrite a factory preset — return it unchanged, persist nothing.
        if (!string.IsNullOrEmpty(overwriteId) && _factoryIds.Contains(overwriteId))
        {
            _logger?.Info(AppLogCategory.Audio,
                $"DspTonePresetService.SaveUser: refusing to overwrite built-in preset '{overwriteId}'.");
            return FindById(overwriteId)!; // guaranteed non-null: it is a known factory id
        }

        string id;
        if (!string.IsNullOrEmpty(overwriteId) &&
            _store.List().Any(p => string.Equals(p.Id, overwriteId, StringComparison.OrdinalIgnoreCase)))
        {
            id = overwriteId; // replace an existing user preset in place
        }
        else
        {
            id = GenerateId(name);
        }

        var preset = new DspTonePreset(id, CleanName(name), category, CleanName(description), rig, DspToneOrigin.User);
        return _store.Upsert(preset) ?? preset;
    }

    public bool DeleteUser(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (_factoryIds.Contains(id)) return false; // factory presets are immutable
        return _store.Remove(id);
    }

    // ---- id / text helpers ---------------------------------------------------

    private string GenerateId(string name)
    {
        var slug = Kebab(name);
        if (slug.Length == 0) slug = "tone";

        // Collision-safe against factory ids AND currently-saved user ids.
        var taken = new HashSet<string>(_factoryIds, StringComparer.OrdinalIgnoreCase);
        foreach (var p in _store.List()) taken.Add(p.Id);

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var suffix = Guid.NewGuid().ToString("N")[..6];
            var candidate = $"user-{slug}-{suffix}";
            if (!taken.Contains(candidate)) return candidate;
        }

        return $"user-{slug}-{Guid.NewGuid():N}"; // extremely unlikely fallthrough
    }

    private static string CleanName(string? s) => (s ?? string.Empty).Trim();

    private static string Kebab(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        var lastDash = false;
        foreach (var ch in s.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        var result = sb.ToString().Trim('-');
        return result.Length > 24 ? result[..24].Trim('-') : result;
    }
}
