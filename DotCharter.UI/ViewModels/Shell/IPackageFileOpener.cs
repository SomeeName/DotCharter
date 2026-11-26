namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     Opens a game package file handed to the app (window drop, File ▸ Open
///     Recent) as a project, through the same load path a Library row uses — the
///     <see cref="DroppedFileRoute.RocksmithPackage" /> arm of
///     <see cref="ProjectMenuViewModel.LoadFile" />. Implemented by the song browser.
/// </summary>
public interface IPackageFileOpener
{
    /// <summary>True when the package's song replaced the open project; false after a
    /// cancel or a failure, which has already been shown on the status rail.</summary>
    Task<bool> OpenPackageFileAsync(string path);
}
