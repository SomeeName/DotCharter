using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Audio.ChartSynth;

namespace DotCharter.Services.Audio.ChartSynth;

/// <summary>
/// Decides WHICH instrument the chart synth plays and is the only thing
///     that sets it on the audio service: the PLAYER PROFILE's own guitar and bass
///     (<see cref="ProfileChartSynthInstrumentStore" />), unless the SONG TONE that
///     is playing carries an override (<see cref="ChartSynthToneOverride" />, saved
///     beside the song's tone edits in <see cref="SongToneEditStore" />). The
///     override wins family by family while its tone plays; the profile's choice
///     comes back when the tone changes, the run ends or the tone edit closes.
///     <para>
///         <b>The service keeps only the EFFECTIVE choice</b> and replays it onto a
///         rebuilt engine itself (<c>ChartSynthEngineDriver.PushTo</c>),
///         so a device change or a watchdog rebuild needs nothing from here. The
///         config apply reads no instrument (it would put the app-wide
///         value back on every settings apply); the legacy app-wide config fields are
///         MOVED into the active profile once by <see cref="Start" />.
///     </para>
///     <para>
///         Thread-safe: profile switches arrive on the UI thread, tone changes on
///         the tone coordinator's apply thread, <see cref="Start" /> on a startup
///         task. File writes go to one serial background chain so a picker never
///         waits on an fsync. Throw-free on every event path.
///     </para>
/// </summary>
public sealed class ChartSynthInstrumentCoordinator : IDisposable
{
    private readonly IChartSynthEngine? _synth;
    private readonly IPlayerProfileService _profiles;
    private readonly ProfileChartSynthInstrumentStore _store;
    private readonly IRigConfigService _config;
    private readonly SongToneEditStore _songTones;
    private readonly Func<RsTonePlaybackCoordinator?>? _tonesFactory;
    private RsTonePlaybackCoordinator? _tones;
    private readonly IAppLogger? _logger;
    private readonly object _gate = new();

    // The active profile's choice. Null = not read yet, or the last read was
    // UNREADABLE (served the defaults, never cached — the next apply re-reads).
    private Guid _profileId;
    private ChartSynthInstrument? _profileGuitar, _profileBass;

    // The song tone that is playing (null when the player's own board is up) and
    // that song's overrides, read once per song.
    private string? _song, _toneKey, _toneName;
    private string? _overridesSong;
    private Dictionary<string, ChartSynthToneOverride> _overrides = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _writeGate = new();
    private Task _writeTail = Task.CompletedTask;

    // Saves that are queued but have not landed yet (under _gate). A re-read prefers
    // them over the file: switching to another player and back, or ending a run and
    // replaying the song, before the save lands must play the pick, not the stale file.
    private readonly Dictionary<Guid, (long Seq, ChartSynthInstrument Guitar, ChartSynthInstrument Bass)> _pendingProfiles = [];
    private readonly Dictionary<string, (long Seq, string Song, string ToneKey, ChartSynthToneOverride? Value)> _pendingTones =
        new(StringComparer.OrdinalIgnoreCase);
    private long _writeSeq;

    /// <summary>Where queued saves run. A test holds them in flight through this.</summary>
    internal TaskScheduler WriteScheduler { get; init; } = TaskScheduler.Default;
    private bool _started, _disposed;

    public ChartSynthInstrumentCoordinator(
        IAudioPlaybackService audio,
        IPlayerProfileService profiles,
        ProfileChartSynthInstrumentStore store,
        IRigConfigService config,
        SongToneEditStore songTones,
        Func<RsTonePlaybackCoordinator?>? tones = null,
        IAppLogger? logger = null)
    {
        _synth = audio as IChartSynthEngine;
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _songTones = songTones ?? throw new ArgumentNullException(nameof(songTones));
        // Resolved in Start, not here: the tone coordinator is built lazily (it
        // pulls in the rig and the renderer's HUD), and the transport that takes
        // this coordinator is built early.
        _tonesFactory = tones;
        _logger = logger;
    }

    /// <summary>Raised after the effective choice, the profile's choice or the
    /// override state changed — on whichever thread changed it.</summary>
    public event Action? Changed;

    /// <summary>The audio service has a chart synth.</summary>
    public bool IsAvailable => _synth != null;

    /// <summary>
    ///     Moves the app-wide choice into the active profile (once), follows profile
    ///     switches and song tones from now on, and puts the active profile's choice
    ///     on the synth. A second call is a no-op.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
        }

        MoveLegacyChoiceIntoActiveProfile();
        _profiles.ActiveProfileChanged += OnActiveProfileChanged;
        try { _tones = _tonesFactory?.Invoke(); }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Chart synth: song tones cannot override the instrument (no tone coordinator). {ex.GetType().Name}: {ex.Message}");
        }

        if (_tones != null)
        {
            _tones.ActiveToneChanged += OnActiveToneChanged;
            _tones.DrivingStopped += OnDrivingStopped;
        }

        Reapply();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _profiles.ActiveProfileChanged -= OnActiveProfileChanged;
        if (_tones != null)
        {
            _tones.ActiveToneChanged -= OnActiveToneChanged;
            _tones.DrivingStopped -= OnDrivingStopped;
        }
    }

    // ---- What the pickers read ---------------------------------------------------

    /// <summary>What the synth plays for the family now: the playing tone's override, else the profile's.</summary>
    public ChartSynthInstrument Effective(bool bass)
    {
        lock (_gate) return EffectiveLocked(bass);
    }

    /// <summary>The active profile's own instrument for the family.</summary>
    public ChartSynthInstrument ProfileInstrument(bool bass)
    {
        lock (_gate) return ProfileLocked(bass);
    }

    /// <summary>The name of the song tone that is playing, or null when none is.</summary>
    public string? ActiveToneName
    {
        get { lock (_gate) return _toneKey == null ? null : _toneName; }
    }

    /// <summary>A song tone is playing, so an override can be made for it.</summary>
    public bool CanOverrideTone
    {
        get { lock (_gate) return _song != null && _toneKey != null; }
    }

    /// <summary>The playing song tone has its own instrument: the pickers edit IT.</summary>
    public bool HasToneOverride
    {
        get { lock (_gate) return ActiveOverrideLocked() != null; }
    }

    /// <summary>The family plays the tone's instrument rather than the profile's.</summary>
    public bool IsOverridden(bool bass)
    {
        lock (_gate) return ActiveOverrideLocked()?.For(bass) != null;
    }

    // ---- What the pickers write ----------------------------------------------------

    /// <summary>
    ///     The pickers' write: while the playing tone has an override this edits
    ///     the override (that tone only), otherwise the player's own instrument.
    ///     False — and no change — when the synth does not offer the selection.
    /// </summary>
    public bool SetInstrument(ChartSynthInstrument instrument)
    {
        var synth = _synth;
        if (synth == null) return false;
        var info = synth.GetChartSynthModelInfo(instrument.Model);
        if (info == null || !info.Pickups.Contains(instrument.Pickup)) return false;
        var normalized = ChartSynthInstrumentSelection.Normalize(instrument, instrument.IsBass, info);

        lock (_gate)
        {
            if (ActiveOverrideLocked() is { } current)
            {
                var updated = current.With(normalized);
                _overrides[_toneKey!] = updated;
                QueueToneWrite(_song!, _toneKey!, updated);
            }
            else
            {
                if (normalized.IsBass) _profileBass = normalized;
                else _profileGuitar = normalized;
                // An unread profile (unreadable file) keeps the other family's default
                // in memory; the store refuses the write while the file is unreadable.
                QueueProfileWrite(_profileId, ProfileLocked(false), ProfileLocked(true));
            }
        }

        Reapply();
        return true;
    }

    /// <summary>
    /// Gives the playing song tone its own instrument: from now on the
    ///     pickers edit that tone's choice. It starts empty — both families follow
    ///     the profile until one is changed. False when no song tone is playing.
    /// </summary>
    public bool OverrideTone()
    {
        lock (_gate)
        {
            if (_song == null || _toneKey == null) return false;
            if (ActiveOverrideLocked() != null) return true;
            _overrides[_toneKey] = ChartSynthToneOverride.Empty;
            QueueToneWrite(_song, _toneKey, ChartSynthToneOverride.Empty);
        }

        Reapply();
        return true;
    }

    /// <summary>"Use my instrument": drops the playing tone's override, so the
    /// profile's choice plays again. False when it had none.</summary>
    public bool UseProfileInstrument()
    {
        lock (_gate)
        {
            if (_song == null || _toneKey == null || !_overrides.Remove(_toneKey)) return false;
            QueueToneWrite(_song, _toneKey, null);
        }

        Reapply();
        return true;
    }

    // ---- What moves it -----------------------------------------------------------------

    /// <summary>
    ///     A song tone started playing (<paramref name="song" /> and
    ///     <paramref name="toneKey" /> non-null), or the player's own board came
    ///     back (both null). The tone coordinator's events land here; tests drive it directly.
    /// </summary>
    internal void OnSongTone(string? song, string? toneKey, string? toneName)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                _song = string.IsNullOrWhiteSpace(song) || string.IsNullOrWhiteSpace(toneKey) ? null : song;
                _toneKey = _song == null ? null : toneKey!.Trim();
                _toneName = _song == null ? null : (string.IsNullOrWhiteSpace(toneName) ? _toneKey : toneName);
                if (_song == null)
                {
                    // Re-read on the next song (or the next run of this one): the
                    // tone-edit screen may have changed the file in between.
                    _overridesSong = null;
                    _overrides = new Dictionary<string, ChartSynthToneOverride>(StringComparer.OrdinalIgnoreCase);
                }
                else if (!string.Equals(_overridesSong, _song, StringComparison.Ordinal))
                {
                    _overrides = new Dictionary<string, ChartSynthToneOverride>(
                        _songTones.ReadInstruments(_song), StringComparer.OrdinalIgnoreCase);
                    foreach (var pending in _pendingTones.Values)
                    {
                        if (!string.Equals(pending.Song, _song, StringComparison.Ordinal)) continue;
                        if (pending.Value == null) _overrides.Remove(pending.ToneKey);
                        else _overrides[pending.ToneKey] = pending.Value;
                    }

                    _overridesSong = _song;
                }
            }

            Reapply();
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Chart synth: following the song tone failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnActiveToneChanged(ActiveToneInfo info)
        => OnSongTone(info.SongIdentity, info.PackageToneKey ?? info.ToneName, info.ToneName);

    private void OnDrivingStopped() => OnSongTone(null, null, null);

    private void OnActiveProfileChanged(PlayerProfile profile)
    {
        try
        {
            lock (_gate) _profileGuitar = _profileBass = null; // re-read whoever is active now
            Reapply();
            _logger?.Info(AppLogCategory.Audio,
                $"Chart synth: instrument follows profile '{profile?.Name}' — guitar {Effective(false)}, bass {Effective(true)}.");
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Chart synth: the instrument did not follow the profile switch. {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Re-reads the profile if needed and puts the effective choice on the synth.</summary>
    private void Reapply()
    {
        ChartSynthInstrument guitar, bass;
        lock (_gate)
        {
            if (_disposed) return;
            EnsureProfileLocked();
            guitar = EffectiveLocked(false);
            bass = EffectiveLocked(true);
        }

        if (_synth != null)
        {
            Put(guitar);
            Put(bass);
        }

        Changed?.Invoke();
    }

    private void Put(ChartSynthInstrument wanted)
    {
        var synth = _synth!;
        var current = wanted.IsBass ? synth.BassInstrument : synth.GuitarInstrument;
        if (current == wanted) return;
        if (synth.TrySetChartSynthInstrument(wanted)) return;
        // A saved choice this engine build does not offer (an older pack set, a
        // pickup another model has): its model's default position, else the family default.
        var normalized = ChartSynthInstrumentSelection.Normalize(wanted, wanted.IsBass, synth.GetChartSynthModelInfo(wanted.Model));
        if (normalized != wanted && synth.TrySetChartSynthInstrument(normalized)) return;
        var fallback = ChartSynthInstrumentSelection.Default(wanted.IsBass);
        if (current != fallback) synth.TrySetChartSynthInstrument(fallback);
        _logger?.Warning(AppLogCategory.Audio, $"Chart synth: the engine does not offer {wanted}; playing a default instead.");
    }

    private ChartSynthToneOverride? ActiveOverrideLocked()
        => _toneKey != null && _overrides.TryGetValue(_toneKey, out var value) ? value : null;

    private ChartSynthInstrument EffectiveLocked(bool bass)
        => ActiveOverrideLocked()?.For(bass) ?? ProfileLocked(bass);

    private ChartSynthInstrument ProfileLocked(bool bass)
        => (bass ? _profileBass : _profileGuitar) ?? ChartSynthInstrumentSelection.Default(bass);

    private void EnsureProfileLocked()
    {
        Guid id;
        try { id = _profiles.ActiveProfile.Id; }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Chart synth: the active profile could not be read: {ex.Message}");
            return;
        }

        if (id == _profileId && _profileGuitar != null && _profileBass != null) return;
        _profileId = id;
        if (_pendingProfiles.TryGetValue(id, out var pending))
        {
            _profileGuitar = pending.Guitar;
            _profileBass = pending.Bass;
            return;
        }

        var read = _store.Read(id);
        if (read.State == ProfileInstrumentReadState.Unreadable)
        {
            // Presumed intact: serve the defaults, cache nothing, retry next time.
            _profileGuitar = _profileBass = null;
            _logger?.Warning(AppLogCategory.Audio,
                $"Chart synth: the profile's instrument file could not be read; playing the defaults until it can.");
            return;
        }

        _profileGuitar = read.Guitar;
        _profileBass = read.Bass;
    }

    /// <summary>
    ///     The upgrade from the legacy app-wide choice: MOVED into the profile that is
    ///     active now (whoever was last playing, like the tone rig's own move), then
    ///     cleared from the config — which is what makes it one-shot. A profile that
    ///     already has a file keeps it. Nothing is cleared unless the profile was
    ///     written (or already had its own), so an unreadable file or a degraded
    ///     registry retries on the next start.
    /// </summary>
    private void MoveLegacyChoiceIntoActiveProfile()
    {
        try
        {
            var config = _config.LoadRigConfig();
            if (!ChartSynthInstrumentSelection.HasLegacyConfig(config)) return;
            var id = _profiles.ActiveProfile.Id;
            var read = _store.Read(id);
            switch (read.State)
            {
                case ProfileInstrumentReadState.Unreadable:
                    return;
                case ProfileInstrumentReadState.Absent or ProfileInstrumentReadState.Corrupt:
                    var guitar = ChartSynthInstrumentSelection.FromConfig(config, bass: false);
                    var bass = ChartSynthInstrumentSelection.FromConfig(config, bass: true);
                    if (!_store.Save(id, guitar, bass, out var error))
                    {
                        _logger?.Warning(AppLogCategory.Audio, $"Chart synth: moving the instrument into the profile failed: {error}");
                        return;
                    }

                    _logger?.Info(AppLogCategory.Audio, $"Chart synth: moved the app-wide instrument into the active profile (guitar {guitar}, bass {bass}).");
                    break;
            }

            _config.UpdateRigConfig(ChartSynthInstrumentSelection.ClearConfig);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Chart synth: moving the app-wide instrument into the profile failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Both Queue*Write run under _gate, which is what keeps a pending entry and its
    // queued save in step.
    private void QueueProfileWrite(Guid id, ChartSynthInstrument guitar, ChartSynthInstrument bass)
    {
        var seq = ++_writeSeq;
        _pendingProfiles[id] = (seq, guitar, bass);
        QueueWrite(() =>
            {
                if (!_store.Save(id, guitar, bass, out var error))
                    _logger?.Warning(AppLogCategory.Audio, $"Chart synth: the profile's instrument was not saved: {error}");
            },
            () =>
            {
                if (_pendingProfiles.TryGetValue(id, out var p) && p.Seq == seq) _pendingProfiles.Remove(id);
            });
    }

    private void QueueToneWrite(string song, string toneKey, ChartSynthToneOverride? value)
    {
        var seq = ++_writeSeq;
        var key = song + "\0" + toneKey;
        _pendingTones[key] = (seq, song, toneKey, value);
        QueueWrite(() =>
            {
                var result = _songTones.SaveInstrument(song, toneKey, value);
                if (!result.Ok)
                    _logger?.Warning(AppLogCategory.Audio, $"Chart synth: the tone's instrument was not saved: {result.Detail}");
            },
            () =>
            {
                if (_pendingTones.TryGetValue(key, out var p) && p.Seq == seq) _pendingTones.Remove(key);
            });
    }

    /// <summary>Queues <paramref name="write" />; <paramref name="settled" /> runs under
    /// <c>_gate</c> once it has landed or failed, so a re-read goes back to the file.</summary>
    private void QueueWrite(Action write, Action settled)
    {
        lock (_writeGate)
        {
            _writeTail = _writeTail.ContinueWith(_ =>
            {
                try { write(); }
                catch (Exception ex)
                {
                    _logger?.Warning(AppLogCategory.Audio, $"Chart synth: saving the instrument failed. {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    lock (_gate) settled();
                }
            }, CancellationToken.None, TaskContinuationOptions.None, WriteScheduler);
        }
    }

    /// <summary>Blocks until every queued save has landed (60 s cap — a deadlock
    /// detector). For tests, and for a shutdown that must not lose the last pick.</summary>
    public void Flush() => Flush(TimeSpan.FromSeconds(60));

    /// <summary>
    ///     Waits at most <paramref name="cap" /> for every queued save to land; true
    ///     when they all did. The app's exit paths call this with a short cap: they
    ///     terminate the process right after, so a pick made just before closing
    /// would otherwise die with its queued write, and a stuck disk must not
    ///     hold the window open.
    /// </summary>
    public bool Flush(TimeSpan cap)
    {
        Task tail;
        lock (_writeGate) tail = _writeTail;
        return tail.Wait(cap);
    }
}
