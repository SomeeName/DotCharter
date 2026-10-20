using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.PlayMode;

/// <summary>
/// Song Select's SETLIST panel: the player's named, ordered lists of
///     songs (each with its path and speed), the picked one's songs, and the verbs
///     that build and play it. Solo only. One view (<c>SetlistPanelView</c>) binds it
///     in the app's rail and in the redesigned one, so the gallery renders the app's markup.
/// </summary>
public interface ISetlistPanel : INotifyPropertyChanged
{
    /// <summary>Every setlist of the active profile, in the order they were made.</summary>
    IReadOnlyList<ISetlistOption> Lists { get; }

    /// <summary>The picked setlist; null when there are none.</summary>
    ISetlistOption? SelectedList { get; set; }

    /// <summary>The picked setlist's name, edited in place (committed when the field loses focus).</summary>
    string SelectedListName { get; set; }

    /// <summary>"5 songs" — empty with no setlist picked.</summary>
    string SelectedListSummary { get; }

    /// <summary>The picked setlist's songs, in play order.</summary>
    IReadOnlyList<ISetlistEntryRow> Entries { get; }

    bool HasLists { get; }

    bool HasSelectedList { get; }

    bool HasEntries { get; }

    /// <summary>A setlist is picked and has no songs: the panel says how to add one.</summary>
    bool ShowsEmptyHint { get; }

    /// <summary>Song Select has a playable song to add.</summary>
    bool CanAddSong { get; }

    /// <summary>A setlist with songs is picked and none is running.</summary>
    bool CanPlaySelected { get; }

    /// <summary>A line for the panel: songs skipped when nothing after them opened, or a
    /// store that could not be read. Empty when there is nothing to say.</summary>
    string Notice { get; }

    /// <summary>Makes an empty setlist with the next free default name and picks it.</summary>
    ICommand NewSetlistCommand { get; }

    ICommand DeleteSetlistCommand { get; }

    /// <summary>Adds Song Select's song on the rail's path at the current practice speed.</summary>
    ICommand AddSongToSetlistCommand { get; }

    /// <summary>Removes the <see cref="ISetlistEntryRow" /> passed as the parameter.</summary>
    ICommand RemoveSetlistEntryCommand { get; }

    /// <summary>Plays the picked setlist from its first song.</summary>
    ICommand StartSetlistCommand { get; }
}

/// <summary>One setlist in the panel's picker.</summary>
public interface ISetlistOption : INotifyPropertyChanged
{
    /// <summary>The setlist's name; raises a change when it is renamed.</summary>
    string Name { get; }
}

/// <summary>One song of the picked setlist, already formatted.</summary>
public interface ISetlistEntryRow : INotifyPropertyChanged
{
    /// <summary>Its place in the list, from 1.</summary>
    string Number { get; }

    string Title { get; }

    /// <summary>"Artist · Path · 80 %" — the speed only when it is slowed down; may be empty.</summary>
    string Detail { get; }

    bool HasDetail { get; }
}

/// <summary>Where a running setlist stands — what every flow screen shows of it.</summary>
public interface ISetlistStatus : INotifyPropertyChanged
{
    /// <summary>A setlist is being played.</summary>
    bool IsRunning { get; }

    /// <summary>"Rock night · song 2 of 5" — empty while none runs.</summary>
    string ProgressLabel { get; }
}

/// <summary>Get Ready's part of a running setlist.</summary>
public interface IGetReadySetlist : ISetlistStatus
{
    /// <summary>Names the songs skipped on the way to this one because they would not
    /// open; empty when none was.</summary>
    string SkippedNote { get; }

    bool HasSkippedNote { get; }
}

/// <summary>Pause's part of a running setlist.</summary>
public interface IPausedSetlist : ISetlistStatus
{
    /// <summary>Abandons this run and opens the next song's Get Ready.</summary>
    ICommand SkipSetlistSongCommand { get; }

    /// <summary>Leaves to Song Select; the setlist is over.</summary>
    ICommand EndSetlistCommand { get; }
}

/// <summary>Results' part of a running setlist.</summary>
public interface IResultsSetlist : ISetlistStatus
{
    /// <summary>A song follows this one: "Next" is the primary action.</summary>
    bool HasNext { get; }

    /// <summary>"Next: Hysteria".</summary>
    string NextLabel { get; }

    /// <summary>The last song's results are up: the setlist is done.</summary>
    bool IsFinished { get; }

    /// <summary>"Setlist complete · 5 of 5 played".</summary>
    string FinishedLabel { get; }

    /// <summary>Opens the next song's Get Ready.</summary>
    ICommand NextSetlistSongCommand { get; }

    /// <summary>Leaves to Song Select; the setlist is over.</summary>
    ICommand EndSetlistCommand { get; }
}
