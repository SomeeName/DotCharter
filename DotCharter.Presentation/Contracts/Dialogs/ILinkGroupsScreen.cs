using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Linking;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>Opens the manager with suggestions to review (after an import's Review).</summary>
public sealed record LinkGroupsRequest(IReadOnlyList<LinkSuggestion> Suggestions)
    : IDialogRequest<LinkGroupsRequest, ILinkGroupsScreen, bool>;

/// <summary>
///     Tools ▸ Linked measures: every link group with its placements, Rename, Unlink all and Go to
///     a placement — each edit one undo step — and, when opened from an import's Review, the
///     suggestions with a checkbox each and Link selected. <see cref="IDialogScreen{TResult}.Result" />
///     is true when the dialog changed the chart or moved the editor.
/// </summary>
public interface ILinkGroupsScreen : IDialogScreen<bool>
{
    bool HasGroups { get; }

    IReadOnlyList<ILinkGroupRow> Groups { get; }

    /// <summary>Renames the <see cref="ILinkGroupRow" /> passed as the parameter to its edited name.</summary>
    ICommand RenameCommand { get; }

    /// <summary>Unlinks every placement of the <see cref="ILinkGroupRow" /> passed as the parameter.</summary>
    ICommand DeleteCommand { get; }

    /// <summary>Shows the <see cref="ILinkPlacementRow" /> passed as the parameter in the editor, and closes.</summary>
    ICommand GoToCommand { get; }

    bool HasSuggestions { get; }

    IReadOnlyList<ILinkSuggestionRow> Suggestions { get; }

    /// <summary>Links the ticked suggestions as one undo step.</summary>
    ICommand LinkSelectedCommand { get; }

    ICommand CloseCommand { get; }
}

public interface ILinkGroupRow : INotifyPropertyChanged
{
    /// <summary>"A", "B", …</summary>
    string Letter { get; }

    /// <summary>The name being edited; Rename applies it.</summary>
    string Name { get; set; }

    /// <summary>"Lead ×3, Rhythm ×3".</summary>
    string Summary { get; }

    IReadOnlyList<ILinkPlacementRow> Placements { get; }
}

public interface ILinkPlacementRow
{
    /// <summary>"Lead · bars 5–8".</summary>
    string Label { get; }
}

public interface ILinkSuggestionRow : INotifyPropertyChanged
{
    bool IsSelected { get; set; }

    /// <summary>"Chorus · 8 measures".</summary>
    string Title { get; }

    /// <summary>"Lead ×3, Rhythm ×3 · bars 9–16, 25–32, 41–48".</summary>
    string Detail { get; }
}
