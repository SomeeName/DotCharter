using System.Windows.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Infrastructure.Recovery;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>The sessions a startup crash check found, newest first; never empty.</summary>
public sealed record RecoveryRequest(IReadOnlyList<RecoverableSession> Sessions)
    : IDialogRequest<RecoveryRequest, IRecoveryScreen, bool>;

/// <summary>
///     The startup crash-recovery prompt: each leftover autosave can be restored
///     (Save stays pointed at the ORIGINAL project file) or discarded, and "Ask
///     later" leaves every file in place so the next launch asks again.
///     <see cref="IDialogScreen{TResult}.Result" /> is true when every session was
///     restored or discarded, false when some were kept for later.
/// </summary>
public interface IRecoveryScreen : IDialogScreen<bool>
{
    /// <summary>"3 snapshots were found" — counted, in both grammatical forms.</summary>
    string Subtitle { get; }

    IReadOnlyList<IRecoverySession> Sessions { get; }

    /// <summary>The row the footer's primary restores; the newest is picked on open.</summary>
    IRecoverySession? SelectedSession { get; set; }

    bool IsBusy { get; }

    ICommand RecoverSelectedCommand { get; }

    /// <summary>Deletes the files of the <see cref="IRecoverySession" /> passed as the parameter.</summary>
    ICommand DiscardCommand { get; }

    ICommand DiscardAllCommand { get; }

    ICommand AskLaterCommand { get; }
}

public interface IRecoverySession
{
    /// <summary>The project's file name (and song, when it differs), or "Unsaved project".</summary>
    string DisplayName { get; }

    /// <summary>True when the crashed session had never been saved to a file.</summary>
    bool IsUntitled { get; }

    /// <summary>"Project" / "Never saved" — the row's tag.</summary>
    string KindLabel { get; }

    /// <summary>The original project file, or a sentence saying there is none.</summary>
    string PathLine { get; }

    /// <summary>When the snapshot was written, local date and time; empty when it carried no stamp.</summary>
    string SavedAt { get; }
}
