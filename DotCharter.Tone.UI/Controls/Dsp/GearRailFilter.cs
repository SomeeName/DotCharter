namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
/// Pure filter behind the pedalboard's gear rail (the browsable module
/// catalogue that replaced the add-dropdown): a category tab (null = All)
/// plus a case-insensitive substring search over the module's display name
/// and its catalogue id. No Avalonia types — unit-tested directly
/// (<c>GearRailTests</c>), mirroring <see cref="PresetBrowserFilter"/>.
/// </summary>
public static class GearRailFilter
{
    /// <summary>
    /// True when a module with the given display name / catalogue id /
    /// visual category passes the rail's current tab + search query.
    /// A null <paramref name="tab"/> means the All tab; a null/blank
    /// <paramref name="query"/> matches everything on the tab.
    /// </summary>
    public static bool Matches(
        string? displayName,
        string? moduleTypeId,
        DspModuleCategory moduleCategory,
        DspModuleCategory? tab,
        string? query)
    {
        if (tab.HasValue && moduleCategory != tab.Value) return false;
        if (string.IsNullOrWhiteSpace(query)) return true;

        var q = query.Trim();
        return Contains(displayName, q) || Contains(moduleTypeId, q);
    }

    /// <summary>True when the module passes the <paramref name="tab"/> AND any of
    /// its named model <paramref name="labels"/> (ABI v24) contains the query —
    /// so "Plexi" surfaces the Amp card even though the module id is just "amp".
    /// A null/blank query never matches here (the plain <see cref="Matches"/>
    /// already returns everything on the tab).</summary>
    public static bool MatchesModelLabel(
        DspModuleCategory moduleCategory,
        DspModuleCategory? tab,
        IReadOnlyList<string>? labels,
        string? query)
    {
        if (tab.HasValue && moduleCategory != tab.Value) return false;
        if (string.IsNullOrWhiteSpace(query) || labels is null) return false;

        var q = query.Trim();
        foreach (var l in labels)
            if (Contains(l, q)) return true;
        return false;
    }

    /// <summary>The gear browser's top-level KIND tab for a module — what SHAPE
    /// of thing it is, as opposed to what it does to the sound (that is the
    /// character chips / <see cref="DspModuleCategory"/>). Derived from the board
    /// skin family via <see cref="GearThumbnailMath.KindFor"/> so the tab a card
    /// sits under and the chassis it is drawn on can never disagree.</summary>
    public static GearRailKind KindOf(string moduleTypeId)
        => GearThumbnailMath.KindFor(moduleTypeId) switch
        {
            GearThumbKind.AmpHead => GearRailKind.Amp,
            GearThumbKind.Cabinet => GearRailKind.Cab,
            // A routing junction really is a rack blank (see the visual language's
            // §2.4) — that is exactly the RACK drawer, not a stompbox.
            GearThumbKind.Junction => GearRailKind.Rack,
            _ => GearRailKind.Stomp
        };

    /// <summary>True when a module belongs on the given kind tab; a null tab
    /// (no kind selected) matches everything.</summary>
    public static bool MatchesKind(GearRailKind? kind, string moduleTypeId)
        => !kind.HasValue || KindOf(moduleTypeId) == kind.Value;

    /// <summary>Whether <paramref name="moduleTypeId"/> is one of the player's
    /// starred units. Ordinal (a catalogue id is an identifier, not prose) and
    /// throw-free on a null/empty set or id, which is the resting state of a
    /// fresh install.</summary>
    public static bool IsFavorite(IReadOnlySet<string>? favorites, string? moduleTypeId)
        => favorites is { Count: > 0 } &&
           !string.IsNullOrEmpty(moduleTypeId) &&
           favorites.Contains(moduleTypeId);

    /// <summary>
    ///     The FAVOURITES gate, over the tab/kind/search filters: with the
    ///     library's Favourites tab off this matches everything, and with it on
    ///     only the starred units pass.
    ///     <para>
    ///         Deliberately its own predicate rather than a branch inside
    ///         <see cref="Matches" />: the star is a property of the PLAYER, not
    ///         of the module, so it is the one filter input the catalogue cannot
    ///         supply — and keeping it separate is what lets the rail combine it
    ///         with the search unchanged.
    ///     </para>
    /// </summary>
    public static bool MatchesFavorites(
        bool favoritesOnly, IReadOnlySet<string>? favorites, string? moduleTypeId)
        => !favoritesOnly || IsFavorite(favorites, moduleTypeId);

    private static bool Contains(string? haystack, string needle)
        => !string.IsNullOrEmpty(haystack) &&
           haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The four physical gear families the browser's top tab row offers.
/// Deliberately about FORM, not function — a fuzz and a delay are both stomps.</summary>
public enum GearRailKind
{
    Stomp,
    Amp,
    Cab,
    Rack
}
