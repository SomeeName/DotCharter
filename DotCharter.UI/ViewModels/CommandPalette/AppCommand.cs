using DotCharter.Services.Diagnostics;

namespace DotCharter.UI.ViewModels.CommandPalette;

/// <summary>
///     A single entry in the command palette read-model: a stable id, a
///     display title + category, an optional keyboard shortcut and the delegate
///     that runs it. This is a pure data holder — <see cref="CommandCatalog" />
///     builds the list, <see cref="CommandPaletteViewModel" /> filters + runs it.
///     The shortcut and enabled state are lookups (not snapshots) so a palette
///     rebuilt on open always reflects the user's current keybinds + command
///     availability.
/// </summary>
public sealed class AppCommand
{
    public AppCommand(
        string id,
        string title,
        string category,
        Action execute,
        Func<KeyChord>? shortcut = null,
        Func<bool>? canExecute = null,
        string? usageId = null)
    {
        Id = id;
        Title = title;
        Category = category;
        UsageId = string.IsNullOrWhiteSpace(usageId) ? id : usageId;
        Shortcut = shortcut ?? (static () => KeyChord.None);
        CanExecute = canExecute ?? (static () => true);

        // Counting HERE rather than at each call site is what makes the usage
        // statistics complete by construction: a command that runs without being
        // counted would read as dead and be a candidate for removal.
        var counted = UsageId;
        Execute = Tracker == null
            ? execute
            : () =>
            {
                Tracker?.Record(UsageKind.Command, counted);
                execute();
            };
    }

    /// <summary>
    ///     Where command usage is counted. A static because <see cref="AppCommand" />
    ///     is a plain data holder built in a dozen places from a catalogue, none of
    ///     which is DI-constructed; the composition root sets it once at startup and
    ///     it stays null in tests, where the wrapper is not even allocated.
    /// </summary>
    public static IUsageTracker? Tracker { get; set; }

    public string Id { get; }

    /// <summary>
    ///     The id this command's uses are COUNTED under. For a palette entry that
    ///     wraps a view-model <c>[RelayCommand]</c> it is the command's method
    ///     name (<c>SaveProject</c>), because that is what the menu click hook
    ///     records for the same command — one verb, one bucket, whichever route
    ///     ran it. Otherwise it is <see cref="Id" />.
    /// </summary>
    public string UsageId { get; }

    public string Title { get; }
    public string Category { get; }
    public Action Execute { get; }
    public Func<KeyChord> Shortcut { get; }
    public Func<bool> CanExecute { get; }
}
