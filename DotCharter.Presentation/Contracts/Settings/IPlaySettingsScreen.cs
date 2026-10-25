using System.ComponentModel;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ Play: how a Play Mode run sounds, whether misses can end it and
///     how its highway stacks the strings. Play Mode's own settings modal writes
///     the sound values too, so the rows read the saved state each time Settings opens.
/// </summary>
public interface IPlaySettingsScreen : INotifyPropertyChanged
{
    /// <summary>0 off, then 2, 4 and 8 beats.</summary>
    int GameplayCountInIndex { get; set; }

    bool GameplayMetronomeEnabled { get; set; }

    bool GameplayClapsEnabled { get; set; }

    bool GameplayRockMeterEnabled { get; set; }

    /// <summary>String order: 0 Rocksmith (low string on top), 1 Tab (low string at the bottom). The run's own, separate
    /// from the editor's.</summary>
    int StringOrderIndex { get; set; }

    /// <summary>Row and group keys the current search filters OUT. Empty when there is no query.</summary>
    IReadOnlySet<string> HiddenKeys { get; }
}
