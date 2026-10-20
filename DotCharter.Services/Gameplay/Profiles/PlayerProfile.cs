namespace DotCharter.Services.Gameplay.Profiles;

/// <summary>
///     One local player profile. All gameplay data (scores, grades, statistics,
///     progress, recent activity) is namespaced by the owning profile's
///     <see cref="Id"/>; the editor side of the app is profile-agnostic.
/// </summary>
public sealed class PlayerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The name the player gave; EMPTY for the default profile, which has none.</summary>
    public string Name { get; set; } = "";

    /// <summary>False for the default profile: its name follows the UI language at display time.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCustomName => !string.IsNullOrWhiteSpace(Name);

    /// <summary>What the profile is called on screen: its own name, or the localized "Player 1".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => HasCustomName ? Name : DefaultName?.Invoke() ?? FallbackDefaultName;

    /// <summary>The name a profile without one shows when no service has said how to say "Player 1".</summary>
    internal const string FallbackDefaultName = "Player 1";

    /// <summary>
    ///     The localized "Player 1", read at DISPLAY time so a language switch renames the default
    ///     profile. Set by the <see cref="PlayerProfileService" /> that hands the profile out, from its
    ///     injected localization.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal Func<string>? DefaultName { get; set; }

    /// <summary>Accent color as #RRGGBB, shown wherever the profile appears (status bar, leaderboards).</summary>
    public string AccentColor { get; set; } = PlayerProfileService.AccentPalette[0];

    /// <summary>
    ///     File name of the avatar image inside the profile's data directory
    ///     (e.g. <c>avatar.png</c>). Empty = no avatar; the UI falls back to
    ///     the accent-color dot.
    /// </summary>
    public string AvatarFileName { get; set; } = "";

    /// <summary>
    ///     Left-handed mode (v0.34): mirrors the 3D highway (fret axis) and
    ///     the 2D lanes for this profile on every machine it plays on — the
    ///     flag lives in profile.json, so the portable export / LAN roaming
    ///     bundle carries it for free. Default false keeps old registries
    ///     byte-stable.
    /// </summary>
    public bool LeftyFlip { get; set; }

    /// <summary>
    /// The tuning this player's guitar is normally in, as a
    ///     <c>TunerTunings</c> id (<c>guitar:-1,-1,-1,-1,-1,-1</c> — stable across a
    ///     rename of the list). Empty means E standard, so old registries read as that.
    /// </summary>
    public string MyTuningId { get; set; } = "";

    /// <summary>
    /// Tuning-match transpose, opt-in: a path tuned uniformly away from
    ///     <see cref="MyTuningId" /> plays with the SONG shifted to the player's tuning
    ///     instead of asking them to retune. Per profile, like <see cref="LeftyFlip" />.
    /// </summary>
    public bool PlayInMyTuning { get; set; }

    /// <summary>
    ///     True for the profile created on first 0.11 launch. Pre-profile
    ///     (0.10) score data is migrated into this profile so nothing is lost.
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime LastUsedUtc { get; set; }
}
