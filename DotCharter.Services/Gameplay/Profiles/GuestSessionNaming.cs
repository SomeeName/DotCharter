namespace DotCharter.Services.Gameplay.Profiles;

/// <summary>
///     Pure naming rules for the guest-profile fast path (v0.31
///     "friends-onboarding-lite"): a LAN guest types a name and is playing in
///     ~2 minutes — the profile itself is an ordinary
///     <see cref="IPlayerProfileService" /> profile (deleted after the night
///     from the profile switcher like any other), so all this has to decide is
///     what the profile is CALLED.
/// </summary>
public static class GuestSessionNaming
{
    /// <summary>Display caps elsewhere trim at ~160 px — 32 chars keeps chips
    /// and leaderboard rows readable.</summary>
    public const int MaxLength = 32;

    /// <summary>Used when the guest types nothing. Deliberately invariant —
    /// a profile name is data, not UI chrome.</summary>
    public const string Fallback = "Guest";

    /// <summary>Trims, caps, falls back, and de-duplicates against the
    /// machine's existing profile names (case-insensitive, " 2"/" 3" suffixes
    /// — the same convention profile import uses).</summary>
    public static string Resolve(string? requested, IEnumerable<string>? existingNames = null)
    {
        var name = (requested ?? "").Trim();
        if (name.Length == 0) name = Fallback;
        if (name.Length > MaxLength) name = name[..MaxLength].TrimEnd();

        if (existingNames == null) return name;
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} {i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
