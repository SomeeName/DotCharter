using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Services.Editing.Tracks;

/// <summary>
/// Composable top-ribbon row visibility preferences for one track. Tones,
/// sections and phrases have per-track toggles; lyrics belong to the active
/// timeline. The renderer reflows the ribbon height around the visible rows
/// so the lane area expands to fill any reclaimed space.
///
/// These are the USER toggles only: `ChartRenderer.TopRibbon` additionally
/// collapses rows that have no content (Sections
/// stays as the event-dialog entry point when no events exist), so an "on" toggle
/// does not guarantee the row occupies height.
///
/// Lyrics are song-wide: stacked renderers exclude that row regardless of
/// ShowLyrics, including a saved layout that enables it. The active timeline
/// keeps the lyrics row and its toggle.
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

    /// <summary>The LINKS row (linked measures). It only ever takes height while the
    /// track has link groups.</summary>
    [ObservableProperty] private bool _showLinks = true;

    /// <summary>
    /// "Hide empty lanes automatically" (on by default): a lane whose
    /// toggle is on still gives its height back while the track has nothing
    /// for it — no phrases, only ONE tone. Off, every toggled lane
    /// keeps its row. Sections stays as the event dialog entry point; a lane switched on stays until switched off.
    /// </summary>
    [ObservableProperty] private bool _autoHideEmpty = true;

    /// <summary>"Compact rows": every ribbon row drawn shorter, the
    /// difference handed to the note lanes.</summary>
    [ObservableProperty] private bool _compactRows;

    // Switching a lane's toggle ON (the menu, "Show all lanes") keeps its row even while
    // empty, until the toggle goes off again. Session state, never saved.
    [property: JsonIgnore]
    [ObservableProperty]
    private bool _forceTones;
    [property: JsonIgnore]
    [ObservableProperty]
    private bool _forceSections;
    [property: JsonIgnore]
    [ObservableProperty]
    private bool _forcePhrases;
    [property: JsonIgnore]
    [ObservableProperty]
    private bool _forceLyrics;

    partial void OnShowTonesChanged(bool value) => ForceTones = value;
    partial void OnShowSectionsChanged(bool value) => ForceSections = value;
    partial void OnShowPhrasesChanged(bool value) => ForcePhrases = value;
    partial void OnShowLyricsChanged(bool value) => ForceLyrics = value;

    /// <summary>"Show all lanes": every lane toggle back on.</summary>
    public void ShowAllLanes()
    {
        ShowTones = true;
        ShowSections = true;
        ShowPhrases = true;
        ShowLyrics = true;
        ShowMeasureMetadata = true;
        ShowLinks = true;
    }
}
