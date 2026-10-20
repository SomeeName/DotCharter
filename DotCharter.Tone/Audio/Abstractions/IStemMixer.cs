namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
///     The multi-track stem mixer: stems loaded beside the song's own mix,
///     each with its own gain, mute and solo, plus the original mix's level and
///     mute. One of the focused seams <see cref="IAudioPlaybackService" /> is
///     made of (docs/agents/services/audio-engine.md, "Focused interfaces for
///     view models").
///     <para>
///         <see cref="SongVolume" /> is here because the stem mixer's master
///         strip writes it: the engine applies it to the original mix AND to
///         every stem (<c>UserVolume * master</c>), so it is the mixer's common
///         factor, not a transport property.
///     </para>
/// </summary>
public interface IStemMixer
{
    double SongVolume { get; set; }

    /// <summary>True while one or more stems are loaded alongside the main mix.</summary>
    bool HasStems { get; }

    /// <summary>Names of the currently loaded stems (drums, bass, vocals…). Empty when none.</summary>
    IReadOnlyList<string> LoadedStemNames { get; }

    /// <summary>
    /// Loads a set of stems alongside the currently playing main mix. All
    /// stems play back synchronized with the master transport: Play/Pause/
    /// Stop/Seek/PlaybackSpeed apply to every stem source plus the original
    /// mix. Per-stem volume, mute and solo are independent.
    /// </summary>
    Task LoadStemsAsync(IReadOnlyList<StemLoadRequest> stems);

    /// <summary>Releases all stem sources and returns to single-mix playback.</summary>
    void ClearStems();

    /// <summary>Sets the per-stem linear gain (0..4). Solo / Mute still apply on top.</summary>
    void SetStemVolume(string name, double linearGain);

    /// <summary>Per-stem hard mute.</summary>
    void SetStemMute(string name, bool muted);

    /// <summary>Per-stem solo. When any stem is soloed, all non-soloed stems are silenced.</summary>
    void SetStemSolo(string name, bool soloed);

    /// <summary>Mutes / unmutes the original master mix (so the user can listen to stems only).</summary>
    void SetMasterMixMuted(bool muted);
}

/// <summary>Single stem to load. <paramref name="Name"/> identifies the stem
/// across the mixer (drums, bass, vocals, …); <paramref name="Path"/> is an
/// absolute audio file the JUCE format manager can decode.</summary>
public readonly record struct StemLoadRequest(string Name, string Path);
