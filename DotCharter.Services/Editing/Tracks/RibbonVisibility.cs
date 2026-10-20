using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Services.Editing.Tracks;

/// <summary>
/// Composable top-ribbon row visibility for one track. Each track decides
/// whether to show tones, sections, phrases and lyrics independently — the
/// renderer reflows the ribbon height around the visible rows so the lane
/// area expands to fill any reclaimed space.
///
/// These are the USER toggles only: `ChartRenderer.TopRibbon` additionally
/// collapses rows that have no content (keeping the Sections row as the
/// event-dialog entry point when no events exist at all), so an "on" toggle
/// does not guarantee the row occupies height.
///
/// Stacked panels default to hiding the lyrics row (lyrics are a song-wide
/// concept, redundant per-track), which trims ~24 px of dead space from
/// each row in the multi-track view.
/// </summary>
public partial class RibbonVisibility : ObservableObject
{
    [ObservableProperty] private bool _showTones = true;
    [ObservableProperty] private bool _showSections = true;
    [ObservableProperty] private bool _showPhrases = true;
    [ObservableProperty] private bool _showLyrics = true;
    /// <summary>
    /// Whether the per-measure metadata text (M{n} · BPM · sig) is drawn
    /// just below the ribbon. Defaults to true on the main timeline; the
    /// stacked panels turn it off because the active timeline already shows
    /// the same information at the same scroll position.
    /// </summary>
    [ObservableProperty] private bool _showMeasureMetadata = true;
}
