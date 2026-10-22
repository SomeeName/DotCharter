using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
/// One channel strip in the <see cref="StemMixerViewModel"/>. Mirrors the
/// underlying <see cref="Stem"/> domain object and pushes user actions
/// (mute, solo, volume) into <see cref="IStemMixer"/> in real-time.
/// </summary>
public partial class StemTrackViewModel : ObservableObject
{
    private readonly IStemMixer _audio;
    private readonly Stem? _stem;

    private readonly ILocalizationService _localization;
    private StemMixerViewModel? _owner;
    private bool _syncing;

    /// <summary>While set, a mute change does NOT write through to the domain
    /// <c>Stem.Muted</c> flag. See <see cref="SetMutedForRun" />.</summary>
    private bool _runScopedMute;

    public StemTrackViewModel(IStemMixer audio, Stem stem, ILocalizationService localization)
    {
        _audio = audio;
        _localization = localization;
        _stem = stem;
        Name = string.IsNullOrWhiteSpace(stem.Name) ? "stem" : stem.Name;
        _volumeLinear = Math.Clamp(stem.Volume <= 0 ? 1.0 : stem.Volume, 0.0, 4.0);
        _isMuted = stem.Muted;
        _isSolo = stem.Solo;
        IsMasterMix = false;
    }

    /// <summary>Special-purpose constructor for the "Original Mix" channel strip.</summary>
    /// <param name="audio">The engine this strip pushes its state into.</param>
    /// <param name="displayName">The channel's label, e.g. "Original".</param>
    /// <param name="muted">
    ///     The project's <c>OriginalMixMuted</c>. It is a PARAMETER because this
    ///     channel has no backing <see cref="Stem" /> to read it from, and a
    ///     hard-coded <c>true</c> here would make a project whose stems were all muted
    ///     (what the mixer's own cross-track exclusion produces when you un-mute the
    ///     original) reopen with every channel silent.
    /// </param>
    /// <param name="localization">Names the strip on screen; see <see cref="DisplayName" />.</param>
    public StemTrackViewModel(IStemMixer audio, string displayName, bool muted, ILocalizationService localization)
    {
        _audio = audio;
        _localization = localization;
        _stem = null;
        Name = displayName;
        IsMasterMix = true;
        // SEEDED FROM THE ENGINE, not hard-coded to unity. This strip's fader IS
        // `IAudioPlaybackService.SongVolume` — the same value the transport's Song
        // fader, the Mix popover and Play Mode's game settings all write, and the
        // same one `ApplyStemGainsLocked` multiplies every stem by. It was 1.0
        // here, and `ApplyToEngine` pushes it back on EVERY rebuild, so a project
        // load, a project-path change or any coalesced second pass silently reset
        // the user's song level to unity while `TransportViewModel._songVolume`
        // (which is read once, at construction) went on showing the old number.
        // The engine's own clamp is the range the value has to survive; the two
        // fader controls are narrower (0..2 in the mixers, 0..1 in the popover)
        // and must not be what defines it.
        _volumeLinear = Math.Clamp(audio.SongVolume, 0.0, 4.0);
        _isMuted = muted;
    }

    internal void AttachOwner(StemMixerViewModel owner) => _owner = owner;

    /// <summary>Updates the muted/soloed state without re-firing the audio callbacks
    /// or the cross-track exclusion logic. Used by <see cref="StemMixerViewModel"/>
    /// to mirror engine-driven changes back into the UI.</summary>
    internal void SetMutedSilently(bool value)
    {
        if (IsMuted == value) return;
        _syncing = true;
        try { IsMuted = value; }
        finally { _syncing = false; }
        _stem?.Muted = value;
        if (IsMasterMix)
        {
            // Cross-track exclusion arrives here, and it is a real state change the
            // project has to keep — the stems it silences persist theirs.
            _owner?.PersistOriginalMixMuted(value);
            _audio.SetMasterMixMuted(value);
        }
        else
        {
            _audio.SetStemMute(Name, value);
        }
    }

    /// <summary>
    ///     Silences (or restores) the master "Original" strip for the duration of a
    ///     SOLO, without writing the change into the project.
    /// </summary>
    /// <remarks>
    ///     A solo is transient: the engine already drops the original mix while any
    ///     stem is soloed (<c>ApplyGain</c>'s <c>_masterMixMuted || anySolo</c>) and
    ///     brings it back the moment the solo is released. The mixer nonetheless mutes
    ///     the strip so the UI tells the truth about what is audible — but doing that
    ///     through <see cref="SetMutedSilently" /> ALSO wrote
    ///     <c>ChartProject.OriginalMixMuted = true</c>, which is permanent. Soloing a
    ///     stem once and releasing it therefore left the original mix off for good,
    ///     saved into the <c>.dcproj</c>. Same rule, and the same reason, as
    ///     <see cref="SetMutedForRun" />.
    /// </remarks>
    internal void SetMutedForSolo(bool value)
    {
        if (!IsMasterMix) return;
        if (IsMuted == value) return;
        _syncing = true;
        try { IsMuted = value; }
        finally { _syncing = false; }
        _audio.SetMasterMixMuted(value);
    }

    internal void SetSoloSilently(bool value)
    {
        if (IsSolo == value) return;
        _syncing = true;
        try { IsSolo = value; }
        finally { _syncing = false; }
        if (IsMasterMix) return;
        _stem?.Solo = value;
        _audio.SetStemSolo(Name, value);
    }

    public string Name { get; }

    /// <summary>What the strip is called on screen; <see cref="Name" /> stays the engine's key.</summary>
    public string DisplayName => IsMasterMix ? Text("Stem_Original") : LabelFor(Name);

    /// <summary>The separator's stems by their word ("drums" is Drums / Schlagzeug); any other
    /// name as the user gave it, with a capital.</summary>
    private string LabelFor(string name)
    {
        var key = name.Trim().ToLowerInvariant() switch
        {
            "drums" => "Stem_Drums",
            "bass" => "Stem_Bass",
            "vocals" => "Stem_Vocals",
            "other" => "Stem_Other",
            "guitar" => "Stem_Guitar",
            "guitar-lead" => "Stem_GuitarLead",
            "guitar-rhythm" => "Stem_GuitarRhythm",
            "piano" => "Stem_Piano",
            _ => null
        };
        if (key != null) return Text(key);
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? name : char.ToUpper(trimmed[0], CultureInfo.CurrentCulture) + trimmed[1..];
    }

    private string Text(string key) => _localization[key];

    public bool IsMasterMix { get; }

    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isSolo;
    [ObservableProperty] private double _volumeLinear;

    /// <summary>Unity — what a double-click on this strip's fader restores. A
    /// property rather than a literal in the markup because it is the same
    /// number the constructors seed a stem with when it carries no stored
    /// volume, and the fader and the seed may not disagree.</summary>
    public double DefaultVolumeLinear => 1.0;

    public string VolumeDbLabel
    {
        get
        {
            if (VolumeLinear <= 0.0001) return "-∞ dB";
            var db = 20.0 * Math.Log10(VolumeLinear);
            return db >= 0 ? $"+{db:0.#} dB" : $"{db:0.#} dB";
        }
    }

    /// <summary>
    ///     Mutes/unmutes for the duration of a gameplay run WITHOUT writing through to
    ///     the domain <c>Stem.Muted</c> flag. That field is serialized into the
    ///     <c>.dcproj</c> (the additive <c>stem/@muted</c> attribute,
    ///     <c>RscpWriter.BuildStemsNode</c>), so a run-scoped mute that reached it
    ///     would survive a crash or hard exit mid-run and leave the stem permanently
    ///     muted in the saved project. Audio and the UI mirror still apply; the
    ///     master-mix CROSS-TRACK EXCLUSION deliberately does NOT — see
    ///     <c>OnIsMutedChanged</c>, because that path persists both sides
    ///     and a run-scoped restore is putting the strip back where it already was.
    ///     <para>
    ///         <c>Stem.Muted</c> is persisted to the file by the writer/parser, because
    ///         a mixer mute the user set deliberately has to survive a
    ///         reload.
    ///     </para>
    /// </summary>
    internal void SetMutedForRun(bool value)
    {
        if (IsMuted == value) return;
        _runScopedMute = true;
        try { IsMuted = value; }
        finally { _runScopedMute = false; }
    }

    /// <summary>Puts the UI/audio solo flag back in step with the project after a
    /// run-scoped mute cleared it (the domain flag was deliberately left alone).</summary>
    internal void RestoreSoloFromProject()
    {
        if (_stem == null || IsSolo == _stem.Solo) return;
        SetSoloSilently(_stem.Solo);
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_syncing) return;
        if (_stem != null && !_runScopedMute) _stem.Muted = value;
        if (IsMasterMix)
        {
            // Same rule as _stem.Muted above: a RUN-scoped mute must not reach the
            // saved project, or a crash mid-run leaves the original permanently off.
            if (!_runScopedMute) _owner?.PersistOriginalMixMuted(value);
            _audio.SetMasterMixMuted(value);
        }
        else _audio.SetStemMute(Name, value);

        // Mute and Solo are mutually exclusive — muting a track clears its solo.
        if (value && IsSolo)
        {
            _syncing = true;
            try { IsSolo = false; }
            finally { _syncing = false; }
            if (!IsMasterMix) _audio.SetStemSolo(Name, false);
            // Same reason as the mute above: a run-scoped mute must not persist a
            // cleared solo into the project. RestoreSoloFromProject puts the UI back.
            if (_stem != null && !_runScopedMute) _stem.Solo = false;
        }

        // Cross-track exclusion: the master mix and the stem group should not
        // both be audible at once (they overlap → audible doubling / phasing).
        // NOT under a RUN-scoped mute, and that exemption is the third sibling of
        // the solo bug. The exclusion silences the other side through
        // SetMutedSilently, which writes `Stem.Muted` and `PersistOriginalMixMuted`
        // — both PERMANENT — so `RestoreBackingTrackMutes` putting a stem back at
        // the end of a Play Mode run wrote `OriginalMixMuted = true` into the
        // project. A project loaded with the original audible AND a stem audible
        // (a perfectly saveable state: nothing re-applies the exclusion at load)
        // therefore lost its original mix to one run of "mute my instrument", and
        // it survived the reload. There is nothing for the exclusion to do here
        // anyway: a run-scoped restore puts the strip back exactly where it was
        // BEFORE the run, a state the exclusion had already had its say about.
        if (!_runScopedMute) _owner?.OnTrackMuteChanged(this, value);
        // Audibility DID change either way — the drawn waveform follows what is
        // audible, run-scoped or not.
        _owner?.OnTrackAudibilityChanged();
    }

    partial void OnIsSoloChanged(bool value)
    {
        if (_syncing) return;
        if (IsMasterMix) return;
        if (_stem != null) _stem.Solo = value;
        _audio.SetStemSolo(Name, value);

        // Soloing implicitly un-mutes the track — a soloed muted track is a
        // contradiction.
        if (value && IsMuted)
        {
            _syncing = true;
            try { IsMuted = false; }
            finally { _syncing = false; }
            _audio.SetStemMute(Name, false);
            if (_stem != null) _stem.Muted = false;
        }

        _owner?.OnTrackSoloChanged(this, value);
    }

    partial void OnVolumeLinearChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeDbLabel));
        if (_syncing) return;
        var clamped = Math.Clamp(value, 0.0, 4.0);
        if (_stem != null) _stem.Volume = clamped;
        if (IsMasterMix) _audio.SongVolume = clamped;
        else _audio.SetStemVolume(Name, clamped);
        _owner?.OnTrackAudibilityChanged();
    }

    /// <summary>Pushes the current UI state into the audio engine without re-firing setters.</summary>
    internal void ApplyToEngine()
    {
        if (IsMasterMix)
        {
            _audio.SetMasterMixMuted(IsMuted);
            _audio.SongVolume = VolumeLinear;
        }
        else
        {
            _audio.SetStemVolume(Name, VolumeLinear);
            _audio.SetStemMute(Name, IsMuted);
            _audio.SetStemSolo(Name, IsSolo);
        }
    }
}
