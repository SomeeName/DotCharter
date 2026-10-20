using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
/// What <see cref="PlaySetlistViewModel" /> needs from Play Mode: the
///     screen it is on (its <c>PropertyChanged</c> carries <c>CurrentScreen</c>),
///     Song Select's pick, the practice speed, the song loader Home already uses,
///     and the two run exits. <see cref="PlayModeViewModel" /> implements every
///     member as it already was, so the setlist logic lives outside the
///     size-capped Play Mode partials (<c>SplitTypePartialRatchetTests</c>).
/// </summary>
public interface IPlaySetlistHost : INotifyPropertyChanged
{
    PlayModeScreen CurrentScreen { get; }

    PlaySongItem? SelectedSong { get; }

    SongArrangementFact? SelectedArrangement { get; }

    int PracticeSpeedPercent { get; set; }

    /// <summary>Loads a song and opens Get Ready on the named path. False when the
    /// song is not in the library or would not load — Play Mode is then on Song
    /// Select.</summary>
    Task<bool> OpenSongAsync(string songKey, string arrangementName);

    /// <summary>Results → Song Select: stops the transport and unwinds the run.</summary>
    IRelayCommand BackToSongSelectCommand { get; }

    /// <summary>Pause → Song Select: abandons the run without results.</summary>
    IRelayCommand AbortToSongSelectCommand { get; }
}
