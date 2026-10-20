using System.ComponentModel;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ Play: how a Play Mode run sounds and whether misses can end
///     it. Play Mode's own settings modal writes the same four values, so the
///     rows read the saved state each time Settings opens.
/// </summary>
public interface IPlaySettingsScreen : INotifyPropertyChanged
{
    /// <summary>0 off, then 2, 4 and 8 beats.</summary>
    int GameplayCountInIndex { get; set; }

    bool GameplayMetronomeEnabled { get; set; }

    bool GameplayClapsEnabled { get; set; }

    bool GameplayRockMeterEnabled { get; set; }

    /// <summary>Row and group keys the current search filters OUT. Empty when there is no query.</summary>
    IReadOnlySet<string> HiddenKeys { get; }
}
