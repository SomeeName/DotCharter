using DotCharter.Tone.Audio.Dsp.Presets;

namespace DotCharter.Tone.Audio.Dsp;

/// <summary>
///     One <see cref="ToneSessionService" /> per seat (ABI v28), so every
///     player's board survives an engine rebuild and the app being closed — not
///     just player 1's.
///     <para>
///         <b>Why one service per seat rather than one service that loops.</b>
///         The restore is driven by <see cref="IDspChainService.EngineAttached" />,
///         which each seat's chain raises for itself, and the persist decision is
///         a per-chain signature compare. A single looping service would have to
///         re-derive both, and would lose the property that matters most here:
///         <b>an EMPTY chain is never persisted</b>, because it is
///         indistinguishable from "the engine is down". With one service per seat
///         that rule is enforced per seat, so player 2 never having plugged in
///         cannot wipe player 2's saved rig — and, just as importantly, cannot
///         wipe player 1's either.
///     </para>
/// </summary>
public sealed class ToneSessionSet : IDisposable
{
    private readonly ToneSessionService[] _sessions;

    public ToneSessionSet(IReadOnlyList<ToneSessionService> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0) throw new ArgumentException("At least one session is required.", nameof(sessions));
        _sessions = sessions.ToArray();
    }

    /// <summary>Seat 0's session — the one every single-seat caller resolves.</summary>
    public ToneSessionService Primary => _sessions[0];

    public IReadOnlyList<ToneSessionService> All => _sessions;

    /// <summary>Starts listening on every seat. Safe to call once.</summary>
    public void StartAll()
    {
        foreach (var s in _sessions) s.Start();
    }

    /// <summary>Writes every seat's live rig into the ACTIVE profile's store now.
    /// Called on shutdown so the last few seconds of knob-twiddling are not lost,
    /// and by <c>ProfileToneRigCoordinator</c> BEFORE a profile switch
    /// repoints the store.</summary>
    public void FlushAll()
    {
        foreach (var s in _sessions) s.Flush();
    }

    /// <summary>Re-applies every seat's rig for the profile the store is scoped to
    /// now. Called by <c>ProfileToneRigCoordinator</c> AFTER a profile
    /// switch has repointed the store — never before.</summary>
    public void ReloadAll()
    {
        foreach (var s in _sessions) s.ReloadForActiveProfile();
    }

    public void Dispose()
    {
        foreach (var s in _sessions)
            try { s.Dispose(); } catch { /* ignored */ }
    }

    /// <summary>Builds one session per seat over <paramref name="chains" />.</summary>
    public static ToneSessionSet Create(
        IPlayerDspChains chains, IRigConfigService config, IDspTonePresetService presets,
        IProfileToneRigStore rigStore, IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(chains);
        // ONE store across every seat, deliberately: the profile is the outer
        // axis, so all seats must repoint together on a switch. A store per seat
        // could be scoped to two different profiles at once.
        ArgumentNullException.ThrowIfNull(rigStore);
        // SPILLOVER (ABI v37) is the player's setting, not the engine's: the
        // engine starts with it off, every seat's chain records the setting and
        // replays it onto each engine it is attached to. Seeded HERE because this
        // is where both hosts bring the chains and the config together at
        // startup — before the rig UI (which writes it on a toggle) may ever open.
        var spillover = SafeSpilloverSetting(config, logger);
        foreach (var chain in chains.All) chain.Spillover = spillover;
        var list = new List<ToneSessionService>(chains.MaxPlayers);
        foreach (var chain in chains.All)
            list.Add(new ToneSessionService(chain, config, presets, rigStore, logger));
        return new ToneSessionSet(list);
    }

    private static bool SafeSpilloverSetting(IRigConfigService config, IAppLogger? logger)
    {
        try { return config.LoadRigConfig().RigSpillover; }
        catch (Exception ex)
        {
            logger?.Warning(AppLogCategory.Audio, $"Reading the spillover setting failed; using on. {ex.Message}");
            return true;
        }
    }
}
