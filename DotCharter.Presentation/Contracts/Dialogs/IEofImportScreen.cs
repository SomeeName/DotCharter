using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Import.Eof;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>Opens <see cref="IEofImportScreen" /> on an .eof whose track list was already read off the UI thread.</summary>
public sealed record EofImportRequest(string FilePath, IReadOnlyList<EofTrackInfo> Tracks)
    : IDialogRequest<EofImportRequest, IEofImportScreen, EofImportSelection?>;

/// <summary>
///     File ▸ Import ▸ Editor on Fire: pick the Pro Guitar tracks that become
///     arrangements. Import parses them off the UI thread and finds the song's
///     audio beside the file; a failure of either stays IN the dialog. The
///     project is created after it closes (<c>EofProjectCreator</c>).
///     <see cref="IDialogScreen{TResult}.Result" /> is what was parsed, or null.
/// </summary>
public interface IEofImportScreen : IDialogScreen<EofImportSelection?>
{
    /// <summary>The file's Pro Guitar tracks that have notes, all ticked on open.</summary>
    IReadOnlyList<IEofTrackOption> AvailableTracks { get; }

    bool HasNoTracks { get; }

    /// <summary>True while the tracks are parsed. Cancel then only asks it to stop; nothing is used after.</summary>
    bool IsBusy { get; }

    /// <summary>Why the parse failed or the audio is missing; empty otherwise.</summary>
    string ErrorMessage { get; }

    ICommand ImportCommand { get; }

    ICommand CancelCommand { get; }
}

public interface IEofTrackOption : INotifyPropertyChanged
{
    /// <summary>"Name (N notes)".</summary>
    string Name { get; }

    bool IsSelected { get; set; }
}
