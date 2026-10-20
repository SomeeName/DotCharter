using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Edit ▸ History: every retained state of the branching undo tree — the
///     current path and the preserved branches — and a click walks undo/redo
///     to the chosen state. The list follows the history while it is open.
///     <see cref="IDialogScreen{TResult}.Result" /> is true when closed with Close.
/// </summary>
public interface IHistoryScreen : IDialogScreen<bool>
{
    /// <summary>Depth-first from the initial state, each branch grouped under its fork.</summary>
    IReadOnlyList<IHistoryNode> Nodes { get; }

    bool HasNodes { get; }

    /// <summary>"4 states · no branches" — counted from the rows, so it never disagrees with them.</summary>
    string Summary { get; }

    /// <summary>Walks to the <see cref="IHistoryNode" /> passed as the parameter; the current row does nothing.</summary>
    ICommand JumpToCommand { get; }

    ICommand CloseCommand { get; }
}

public interface IHistoryNode
{
    string Description { get; }

    /// <summary>When the state was reached, local wall clock.</summary>
    string Time { get; }

    /// <summary>Left inset in pixels: it grows at branch points only.</summary>
    double Indent { get; }

    bool IsCurrent { get; }

    /// <summary>A state the current path has left behind.</summary>
    bool IsBranch { get; }
}
