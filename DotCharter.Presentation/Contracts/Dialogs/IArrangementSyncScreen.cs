using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Arrangement ▸ Sync sounds: copies the sounds a SOURCE arrangement shares
///     with a TARGET onto the target, and says what it would change BEFORE doing
///     it — the operation overwrites, so the preview is the point.
///     <see cref="IDialogScreen{TResult}.Result" /> is true once applied.
/// </summary>
public interface IArrangementSyncScreen : IDialogScreen<bool>
{
    /// <summary>The open project's arrangement names, in project order.</summary>
    IReadOnlyList<string> Arrangements { get; }

    /// <summary>Index into <see cref="Arrangements" />; −1 for none.</summary>
    int SourceIndex { get; set; }

    /// <summary>Index into <see cref="Arrangements" />; −1 for none.</summary>
    int TargetIndex { get; set; }

    /// <summary>What Apply would do for this pair, or why it cannot.</summary>
    string Preview { get; }

    /// <summary>False while the pair is incomplete, the same arrangement twice, or would change nothing.</summary>
    bool CanApply { get; }

    ICommand ApplyCommand { get; }

    ICommand CancelCommand { get; }

    /// <summary>Swaps source and target — the one thing anybody does twice here.</summary>
    ICommand SwapCommand { get; }
}
