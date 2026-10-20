using DotCharter.Presentation.Contracts.Shell;

namespace DotCharter.UI.ViewModels.CommandPalette;

/// <summary>
///     The palette catalog as Help ▸ Keyboard shortcuts reads it: only commands
///     with a LIVE chord (an unbound action is Settings' business, not a
///     shortcut), in catalog order, the chord printed in the user's language.
///     The chord is a lookup into the current keybinds, so a rebind shows on the
///     next open without a restart.
/// </summary>
internal static class ShortcutEntries
{
    public static IReadOnlyList<ShortcutEntry> From(IEnumerable<AppCommand> catalog, ILocalizationService localization)
    {
        var entries = new List<ShortcutEntry>();
        foreach (var command in catalog)
        {
            var chord = command.Shortcut();
            if (!chord.IsBound) continue;
            entries.Add(new ShortcutEntry(command.Category, command.Title,
                chord.ToDisplayString(localization)));
        }

        return entries;
    }
}
