namespace DotCharter.UI.Editing.Tracks;

/// <summary>What one track HAS for each ribbon lane — the content half of the
/// lane rules. <see cref="DistinctTones" /> is 0, 1, or 2 for "two or more".</summary>
public readonly record struct RibbonContent(int DistinctTones, bool HasSections, bool HasPhrases, bool HasLyrics, bool HasLinks = false);

/// <summary>Why a lane whose row the user may expect is not on screen.</summary>
public enum RibbonLaneHiddenReason
{
    /// <summary>The lane has its row.</summary>
    Shown,

    /// <summary>The user switched it off.</summary>
    Off,

    /// <summary>Auto-hidden: the track has nothing for it.</summary>
    Empty,

    /// <summary>Auto-hidden: the chart only ever uses one tone, so a TONES row would
    /// repeat that one name for the whole song.</summary>
    OneTone
}

/// <summary>
///     Which ribbon lanes get a row: the user's toggles, minus the empty ones while
///     "Hide empty lanes automatically" is on. Pure, so the timeline's
///     <c>ChartRenderer.TopRibbon</c>, its hit tester and the lane menu's
///     "auto-hidden" hints all read the same answer (<c>UI/RibbonLaneRulesTests</c>).
///     <para>
///         <b>SECTIONS is the entry point.</b> The event dialog edits a section, a
///         phrase and a tone together and opens from a right-click on an event row,
///         so with no section, no phrase and no tone CHANGE the Sections row stays,
///         empty, or the first event could never be added from the timeline.
///     </para>
/// </summary>
public static class RibbonLaneRules
{
    /// <summary>The lanes that get a row.</summary>
    public static RibbonLanes Visible(RibbonLanes toggles, bool autoHideEmpty, RibbonContent content)
    {
        // LINKS has nothing to click while there are no groups (groups are made from a
        // selection), so it never keeps an empty row, whatever the auto-hide setting.
        if (!content.HasLinks) toggles &= ~RibbonLanes.Links;
        if (!autoHideEmpty) return toggles;

        var hasTones = content.DistinctTones > 1;
        var hasEventContent = hasTones || content.HasSections || content.HasPhrases;
        var lanes = toggles;
        if (!hasTones) lanes &= ~RibbonLanes.Tones;
        if (!content.HasSections && hasEventContent) lanes &= ~RibbonLanes.Sections;
        if (!content.HasPhrases) lanes &= ~RibbonLanes.Phrases;
        if (!content.HasLyrics) lanes &= ~RibbonLanes.Lyrics;
        return lanes;
    }

    /// <summary>The layout a track with <paramref name="visibility" />'s settings draws.</summary>
    public static RibbonLayout Compose(RibbonVisibility visibility, RibbonContent content) =>
        new(Visible(visibility.Lanes(), visibility.AutoHideEmpty, content), visibility.Density());

    /// <summary>Why <paramref name="lane" /> has no row (or <see cref="RibbonLaneHiddenReason.Shown" />).</summary>
    public static RibbonLaneHiddenReason HiddenReason(
        RibbonLanes lane, RibbonLanes toggles, bool autoHideEmpty, RibbonContent content)
    {
        if ((toggles & lane) == 0) return RibbonLaneHiddenReason.Off;
        if ((Visible(toggles, autoHideEmpty, content) & lane) != 0) return RibbonLaneHiddenReason.Shown;
        return lane == RibbonLanes.Tones && content.DistinctTones == 1
            ? RibbonLaneHiddenReason.OneTone
            : RibbonLaneHiddenReason.Empty;
    }

    /// <summary>
    ///     How many different tones the chart uses: the base tone and every tone
    ///     change's name, compared by name — 0, 1, or 2 for "two or more". A chart
    ///     that switches to the tone it already had has ONE tone. No allocation:
    ///     the renderer asks this on every <c>TopRibbon</c> read.
    /// </summary>
    public static int DistinctTones(string? baseTone, IList<ToneChange> changes)
    {
        var first = string.IsNullOrEmpty(baseTone) ? null : baseTone;
        for (var i = 0; i < changes.Count; i++)
        {
            var name = changes[i].ToneName;
            if (string.IsNullOrEmpty(name)) continue;
            if (first == null) first = name;
            else if (!string.Equals(first, name, StringComparison.Ordinal)) return 2;
        }

        return first == null ? 0 : 1;
    }
}
