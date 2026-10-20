using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Services.Audio.Dsp;

/// <summary>
///     Makes the tone rig follow the PLAYER PROFILE: it starts every seat's
///     <see cref="ToneSessionService" /> and, whenever the active profile changes,
///     hands the outgoing player's board to their own store and brings the
///     incoming player's board up.
///     <para>
///         <b>The order of the three steps is the whole feature</b>, and getting
///         it wrong is silent:
///         <list type="number">
///             <item>
///                 <b>Flush.</b> <see cref="IPlayerProfileService.ActiveProfileChanged" />
///                 is raised AFTER the switch, and the rig is persisted by a 5 s
///                 POLL (a knob turn raises no event — see
///                 <see cref="ToneSessionService" /> for why). So up to five
///                 seconds of the outgoing player's edits are still only in the
///                 live chain at this instant. Flushing FIRST, while the store is
///                 still scoped to them, is what keeps those edits theirs.
///             </item>
///             <item>
///                 <b>Rescope.</b> Only now does the store repoint. This is why
///                 <see cref="IProfileToneRigStore" /> pins its scope instead of
///                 re-reading the active profile per call: a per-call store would
///                 already have been pointing at the new profile in step 1.
///             </item>
///             <item>
///                 <b>Reload.</b> Apply the incoming player's rigs — and CLEAR any
///                 seat they have nothing stored for, because leaving the previous
///                 board up would show the wrong pedals and then let the next poll
///                 persist them into this profile as if they had been built here.
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>This type owns <see cref="ToneSessionSet.StartAll" />, so it cannot
///         become a dead registration.</b> Nothing else starts the tone sessions:
///         if the composition root stops resolving this coordinator, the rig never
///         restores at all and the failure is loud on the first launch rather than
///         invisible until someone makes a second profile. Pinned by
///         <c>UI/ProfileToneRigReachabilityTests</c>.
///     </para>
///     Throw-free: this rides a UI-thread event, and a tone that fails to switch
///     must never take a profile switch down with it.
/// </summary>
public sealed class ProfileToneRigCoordinator : IDisposable
{
    private readonly IPlayerProfileService _profiles;
    private readonly IProfileToneRigStore _store;
    private readonly ToneSessionSet _sessions;
    private readonly IAppLogger? _logger;
    private readonly object _gate = new();

    private bool _started;
    private bool _disposed;

    public ProfileToneRigCoordinator(
        IPlayerProfileService profiles,
        IProfileToneRigStore store,
        ToneSessionSet sessions,
        IAppLogger? logger = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _logger = logger;
    }

    /// <summary>Subscribes to profile switches and starts every seat's tone
    /// session. Safe to call once; a second call is a no-op.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        // Pin the scope BEFORE the sessions start restoring: the first restore
        // must read the profile that is active now, not resolve one later.
        _store.Rescope();
        _profiles.ActiveProfileChanged += OnActiveProfileChanged;
        _sessions.StartAll();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _profiles.ActiveProfileChanged -= OnActiveProfileChanged; } catch { /* ignored */ }
    }

    private void OnActiveProfileChanged(PlayerProfile profile)
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        try
        {
            _sessions.FlushAll();
            if (!_store.Rescope()) return; // same profile: nothing moved, nothing to reload
            _sessions.ReloadAll();
            _logger?.Info(AppLogCategory.Audio,
                $"Tone rig: switched to profile '{profile?.Name}' ({_store.ScopedProfileId:N}).");
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Tone rig did not follow the profile switch. {ex.GetType().Name}: {ex.Message}");
        }
    }
}
