namespace DotCharter.Services.Update;

/// <summary>
///     The "update available" dialog as the update flow and the settings
///     screens see it. It REMEMBERS the update the last check found, so a
///     screen can re-open it without a second network call.
/// </summary>
public interface IUpdatePrompt
{
    /// <summary>The update found by the last check, or null after "up to date" or before any check.</summary>
    UpdateInfo? PendingUpdate { get; }

    /// <summary>Remember <paramref name="update" /> without opening the dialog — the startup check is non-modal.</summary>
    void Prime(UpdateInfo update);

    void Open(UpdateInfo update);

    /// <summary>Forget the remembered update — the only correct answer to an up-to-date check.</summary>
    void ClearPending();
}
