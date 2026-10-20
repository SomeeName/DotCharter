using DotCharter.Tone.Audio.Dsp.Presets;

namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>Which slice of the tone catalogue the preset browser's category rail
/// is showing: everything, one <see cref="DspToneCategory"/>, or the player's
/// saved profiles.</summary>
public enum PresetFilterKind
{
    All,
    Category,
    User,

    /// <summary>
    ///     The algorithmic originals of every tone that has a capture-backed
    ///     twin — a rail entry of their own rather than a filter checkbox.
    ///     <para>
    ///         They are NOT removed and NOT hidden: each one keeps its id, its
    ///         rig and its musical category, and lives here so the eight category
    ///         tabs show one entry per tone instead of two near-identically named
    ///         ones. Everything in this slice is reachable, applyable and
    ///         savable exactly as before.
    ///     </para>
    /// </summary>
    Legacy,

    /// <summary>The TONE3000 NAM-capture section — not a preset slice at all;
    /// the browser swaps the card grid for the capture panel, so
    /// <see cref="PresetBrowserFilter.Filter"/> yields no cards for it.</summary>
    NamCaptures,

    /// <summary>The rig's setlists — not a preset slice either; the browser swaps
    /// the card grid for the setlist panel (<c>RigSetlistPanel</c>).</summary>
    Setlists
}

/// <summary>
/// Pure, Avalonia-free mapping/filtering for the tone-preset browser: given the
/// full catalogue (factory + user) and the selected rail entry, it returns the
/// presets that belong in the card grid, honouring an optional free-text search
/// over name + description. No UI types, so it is unit-tested directly
/// (<c>PresetBrowserFilterTests</c>). Factory-before-user order is inherited from
/// the incoming list (<see cref="IDspTonePresetService.AllPresets"/> already
/// orders that way) — the filter never reorders.
/// </summary>
public static class PresetBrowserFilter
{
    /// <summary>The <c>nam-</c> prefix a capture-backed twin's id carries.</summary>
    private const string CaptureTwinPrefix = "nam-";

    /// <summary>
    ///     True when this preset has been SUPERSEDED by a capture-backed twin —
    ///     i.e. a preset whose id is this one's prefixed <c>nam-</c> exists in the
    ///     catalogue.
    ///     <para>
    ///         Derived, never a flag on the preset, and never "the name ends in
    ///         (Legacy)". The catalogue holds both halves of 70 pairs, and a
    ///         browser that lists both shows each tone twice — but only the ones
    ///         that genuinely have a twin may be hidden. The acoustic rigs, the
    ///         vocal-synth rigs and every user preset have no capture version and
    ///         must always be visible; keying off the id relationship is what
    ///         makes that fall out automatically instead of needing a list.
    ///     </para>
    /// </summary>
    public static bool IsSupersededByACapture(DspTonePreset preset, IReadOnlySet<string> allIds)
        => preset is { Id.Length: > 0 }
           && !preset.Id.StartsWith(CaptureTwinPrefix, StringComparison.Ordinal)
           && allIds.Contains(CaptureTwinPrefix + preset.Id);

    /// <summary>True when the preset plays through a real neural capture — i.e.
    /// its rig carries a <c>namamp</c>. Read from the RIG, not the id, so a user
    /// preset built on a capture is badged like one.</summary>
    public static bool IsCaptureBacked(DspTonePreset preset)
        => preset?.Rig?.Modules?.Any(m => m.ModuleTypeId == "namamp") == true;

    /// <summary>The presets to show for a rail selection. <paramref name="kind"/>
    /// picks the slice (<see cref="PresetFilterKind.Category"/> uses
    /// <paramref name="category"/>); a non-empty <paramref name="search"/> further
    /// keeps only presets whose name or description contains it
    /// (case-insensitive).
    /// <para>
    ///     A tone whose algorithmic original has been superseded by a
    ///     capture-backed twin appears under <see cref="PresetFilterKind.Legacy"/>
    ///     and NOWHERE ELSE — not in All, not in its category. Nothing is
    ///     removed or hidden; the split exists so the eight category tabs list
    ///     one entry per tone instead of two near-identically named ones.
    /// </para></summary>
    public static IReadOnlyList<DspTonePreset> Filter(
        IReadOnlyList<DspTonePreset> all,
        PresetFilterKind kind,
        DspToneCategory category,
        string? search = null)
    {
        if (all is null || all.Count == 0) return [];

        var ids = all.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        bool Superseded(DspTonePreset p) => IsSupersededByACapture(p, ids);

        var query = kind switch
        {
            PresetFilterKind.User => all.Where(p => p.IsUser),
            PresetFilterKind.Legacy => all.Where(Superseded),
            PresetFilterKind.Category => all.Where(p => p.Category == category && !Superseded(p)),
            PresetFilterKind.NamCaptures or PresetFilterKind.Setlists => [],
            _ => all.Where(p => !Superseded(p))
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            query = query.Where(p =>
                (p.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return query.ToList();
    }
}
