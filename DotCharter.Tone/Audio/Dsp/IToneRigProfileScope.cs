namespace DotCharter.Tone.Audio.Dsp;

/// <summary>
///     The slice of the player-profile registry that <see cref="ProfileToneRigStore" />
///     needs: whose rig to read and where that player's files live. DotCharter's
///     <c>IPlayerProfileService</c> extends this (DotCharter.Services); the
///     standalone rig has no profiles and uses the store's fixed-directory
///     constructor instead.
/// </summary>
public interface IToneRigProfileScope
{
    /// <summary>The active profile's id. May throw when the registry is broken;
    /// the store reports that and falls back to an empty scope.</summary>
    Guid ActiveProfileId { get; }

    /// <summary>The directory holding one profile's per-player files.</summary>
    string GetDataDirectory(Guid id);

    /// <summary>True for the in-memory stand-in profile the registry hands out
    /// when it could not load: nothing may be written for it.</summary>
    bool IsDegradedProfile(Guid id);
}
