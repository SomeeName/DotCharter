using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Editor;

/// <summary>
///     A named-marker list of the Song tab — its Sections and its Tones — shaped like the Phrases
///     panel (<see cref="IPhrasesScreen" />): one row per name, folding open to every place it is
///     used, each place a jump to it. A row renames in place (Enter applies, Esc puts the name
///     back) and deletes; every edit is one undo step and the rows follow undo and redo.
/// </summary>
public interface IMarkerGroupsScreen : INotifyPropertyChanged
{
    /// <summary>"Sections" or "Tones": the list's accessible name.</summary>
    string Title { get; }

    IReadOnlyList<IMarkerGroupRow> Groups { get; }

    bool HasGroups { get; }

    /// <summary>Why the list is empty.</summary>
    string EmptyText { get; }

    /// <summary>What the last rename or delete did; empty before the first.</summary>
    string StatusLine { get; }

    /// <summary>Renames the name of the row passed as the parameter to its edited name.</summary>
    ICommand RenameCommand { get; }

    /// <summary>Puts the row's edited name back to its name.</summary>
    ICommand CancelRenameCommand { get; }

    /// <summary>Deletes the row's name everywhere it is used.</summary>
    ICommand DeleteCommand { get; }

    /// <summary>Moves the editor to the place passed as the parameter.</summary>
    ICommand JumpToCommand { get; }
}

/// <summary>One name: the inline rename, its count, its places and what is wrong with it.</summary>
public interface IMarkerGroupRow : INotifyPropertyChanged
{
    string Name { get; }

    /// <summary>"3 places", "2 uses".</summary>
    string CountLabel { get; }

    IReadOnlyList<IMarkerOccurrenceRow> Occurrences { get; }

    /// <summary>What Delete removes, in words: the button's tooltip and accessible name.</summary>
    string DeleteTip { get; }

    /// <summary>The rename box's text.</summary>
    string EditName { get; set; }

    /// <summary>The rename box differs from the name: Apply shows.</summary>
    bool IsRenameDirty { get; }

    /// <summary>The places show; the chevron folds them away.</summary>
    bool IsExpanded { get; set; }

    /// <summary>A standing problem with the name (a tone placed with no definition); empty when none.</summary>
    string Warning { get; }

    bool HasWarning { get; }

    /// <summary>Why the last rename was refused, printed under the row; cleared by the next keystroke.</summary>
    string Error { get; }

    bool HasError { get; }
}

/// <summary>One place a name is used.</summary>
public interface IMarkerOccurrenceRow
{
    /// <summary>"#2 · bar 17 · 0:42.0", followed by the arrangements when the song has several.</summary>
    string Label { get; }
}
