using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Help ▸ Keyboard shortcuts: a READ-ONLY, searchable list of every bound
///     command, generated from the live keybinds when it opens — never a
///     hand-written table. Rebinding lives in Settings ▸ Keybinds.
///     <see cref="IDialogScreen{TResult}.Result" /> is true when closed with Close.
/// </summary>
public interface IKeyboardShortcutsScreen : IDialogScreen<bool>
{
    /// <summary>Matches a row's title or its chord, case-insensitively; empty shows every row.</summary>
    string FilterText { get; set; }

    /// <summary>The matching rows by category, in the catalog's order; a category with no match is left out.</summary>
    IReadOnlyList<IShortcutGroup> Groups { get; }

    bool HasNoMatches { get; }

    ICommand CloseCommand { get; }
}

public interface IShortcutGroup
{
    string Name { get; }

    IReadOnlyList<IShortcutRow> Rows { get; }
}

public interface IShortcutRow
{
    string Title { get; }

    /// <summary>The chord as the user reads it, in their language.</summary>
    string Chord { get; }
}
