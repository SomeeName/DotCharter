using System.Windows.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Library;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>Opens <see cref="ILibraryDoctorScreen" /> over the song browser's current scan.</summary>
public sealed record LibraryDoctorRequest(IReadOnlyList<LibrarySongEntry> Songs)
    : IDialogRequest<LibraryDoctorRequest, ILibraryDoctorScreen, bool>;

/// <summary>
///     The song browser's Library Doctor: a health check over the scanned
///     library that starts as the dialog opens, with the findings grouped by
///     kind. Read-only — it never changes the library.
///     <see cref="IDialogScreen{TResult}.Result" /> is true when closed with Close.
/// </summary>
public interface ILibraryDoctorScreen : IDialogScreen<bool>
{
    IReadOnlyList<ILibraryDoctorGroup> Groups { get; }

    bool HasIssues { get; }

    bool IsRunning { get; }

    /// <summary>0..1 across the packages being probed.</summary>
    double Progress { get; }

    /// <summary>"Scanning…", the verdict, or why the scan stopped.</summary>
    string Summary { get; }

    /// <summary>Runs the check again; disabled while one runs.</summary>
    ICommand RunCommand { get; }

    /// <summary>Stops a running check and closes.</summary>
    ICommand CloseCommand { get; }
}

public interface ILibraryDoctorGroup
{
    string Title { get; }

    int Count { get; }

    bool IsError { get; }

    bool IsWarning { get; }

    IReadOnlyList<ILibraryDoctorIssue> Issues { get; }
}

public interface ILibraryDoctorIssue
{
    string SongDisplay { get; }

    string FileName { get; }

    string Detail { get; }
}
