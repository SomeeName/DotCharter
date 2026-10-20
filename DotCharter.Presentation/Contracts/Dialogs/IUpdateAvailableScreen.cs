using System.Windows.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Update;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>Opens <see cref="IUpdateAvailableScreen" /> for the release a check found.</summary>
public sealed record UpdateAvailableRequest(UpdateInfo Update)
    : IDialogRequest<UpdateAvailableRequest, IUpdateAvailableScreen, bool>;

/// <summary>
///     "Update available": the new version and its release notes, and the
///     download → verify → stage → restart verb. <see cref="IDialogScreen{TResult}.Result" />
///     is false for Later; a successful install closes the app instead.
/// </summary>
public interface IUpdateAvailableScreen : IDialogScreen<bool>
{
    /// <summary>"v0.63.0 — you have v0.62.1", already formatted.</summary>
    string VersionText { get; }

    /// <summary>Markdown, trimmed.</summary>
    string ReleaseNotes { get; }

    bool HasReleaseNotes { get; }

    /// <summary>Why the install is blocked, the download progress line, or a failure. Empty when there is nothing to say.</summary>
    string StatusText { get; }

    bool IsDownloading { get; }

    /// <summary>0–100.</summary>
    double DownloadProgress { get; }

    /// <summary>False when this install cannot apply an update (not a release install, read-only folder).</summary>
    bool CanInstall { get; }

    ICommand LaterCommand { get; }

    ICommand DownloadAndInstallCommand { get; }
}
