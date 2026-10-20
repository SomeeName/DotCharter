namespace DotCharter.Presentation.Contracts.Shell;

/// <summary>One command with a live chord, as the user reads it: its category, its title and the chord's display text.</summary>
public sealed record ShortcutEntry(string Category, string Title, string Chord);

/// <summary>
///     "What can I press?" — every command that is bound RIGHT NOW, in the
///     command catalog's order.
///     <para>
///         The source is the command palette's catalog, which is the one
///         complete list of commands with localized titles and categories, and
///         whose chords are lookups into the user's current keybinds — so a
///         rebind shows up at the next ask, and an unbound command is never
///         listed. The catalog needs the window's view model, which this project
///         may not reference, so the shell implements this seam
///         (<c>MainWindowViewModel.Dialogs.cs</c>), the same shape as
///         <see cref="IShellDestinations" />.
///     </para>
/// </summary>
public interface IShortcutCatalog
{
    /// <summary>A fresh snapshot on every call; never null. UI thread.</summary>
    IReadOnlyList<ShortcutEntry> CurrentShortcuts();
}
