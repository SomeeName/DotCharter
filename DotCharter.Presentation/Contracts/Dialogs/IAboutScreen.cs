using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Help ▸ About: the build lockup (mark, wordmark, version) over the four
///     fact cards. The cards are fixed copy, so the view reads them from the
///     string table; the contract carries only what differs per build.
///     <see cref="IDialogScreen{TResult}.Result" /> is true when closed with Close.
/// </summary>
public interface IAboutScreen : IDialogScreen<bool>
{
    /// <summary>"v0.62.0.0" — the entry assembly's version, or "vdev" when it has none.</summary>
    string Version { get; }

    /// <summary>Shows the bundled THIRD-PARTY-NOTICES.md and LICENSE in the app, over About (<see cref="ITextDocumentScreen" />).</summary>
    ICommand OpenLicenseCommand { get; }

    ICommand CloseCommand { get; }
}
