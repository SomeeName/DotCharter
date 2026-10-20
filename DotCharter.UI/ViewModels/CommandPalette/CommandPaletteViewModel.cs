using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.CommandPalette;

/// <summary>
///     One row in the palette: the display projection of an <see cref="AppCommand" />
///     (title, category, formatted shortcut and enabled state).
/// </summary>
public sealed class CommandPaletteItem
{
    public CommandPaletteItem(AppCommand command, ILocalizationService localization)
    {
        Command = command;
        var chord = command.Shortcut();
        ShortcutText = chord.IsBound ? chord.ToDisplayString(localization) : string.Empty;
        IsEnabled = SafeCanExecute(command);
    }

    public AppCommand Command { get; }
    public string Title => Command.Title;
    public string Category => Command.Category;
    public string ShortcutText { get; }
    public bool HasShortcut => ShortcutText.Length > 0;
    public bool IsEnabled { get; }

    private static bool SafeCanExecute(AppCommand command)
    {
        try { return command.CanExecute(); }
        catch { return true; }
    }
}

/// <summary>
///     The Ctrl+Shift+P command palette: a fuzzy-searchable overlay listing every
///     app command with its bound shortcut. Pure of Avalonia — the catalogue is
///     injected as a provider so it can be rebuilt (fresh shortcuts + enabled
///     state) each time the palette opens, and the whole thing is unit-testable.
/// </summary>
public partial class CommandPaletteViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<AppCommand>> _catalogProvider;
    private readonly ILocalizationService _localization;

    /// <summary>Coalesces the query box's per-keystroke re-rank + Results refill
    /// into one pass (the <c>CoalescedFilterPass</c> rule — the palette rebuilt
    /// its whole Results collection on every keystroke). Tests omit
    /// <c>searchPost</c> and the pass runs synchronously; the shell passes the
    /// UI dispatcher's background post so a typing burst drains first.</summary>
    private readonly Tone.UI.ViewModels.Shell.CoalescedFilterPass _filterPass;

    private List<CommandPaletteItem> _allItems = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private CommandPaletteItem? _selectedItem;

    public CommandPaletteViewModel(
        Func<IReadOnlyList<AppCommand>> catalogProvider, ILocalizationService localization,
        Action<Action>? searchPost = null)
    {
        _catalogProvider = catalogProvider;
        _localization = localization;
        _filterPass = new Tone.UI.ViewModels.Shell.CoalescedFilterPass(ApplyFilter, searchPost ?? (action => action()));
    }

    public ObservableCollection<CommandPaletteItem> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    /// <summary>How many commands the current query matched. Display only — the
    /// palette's footer says "{0} OF {1}", which is the one thing that tells a
    /// query that narrowed the list from one that matched everything.</summary>
    public int ResultCount => Results.Count;

    /// <summary>How many commands the catalogue holds at all.</summary>
    public int TotalCount => _allItems.Count;

    partial void OnQueryChanged(string value) => _filterPass.Request();

    /// <summary>Opens the palette, rebuilding the catalogue so shortcuts and
    /// command availability reflect the moment it is invoked.
    ///
    /// There is no "is a dialog open?" check here on purpose: this view model
    /// is deliberately pure of the shell, and the window tunnel
    /// (<c>MainWindow.GlobalKeyDownPreview</c> → <c>GlobalChordGate</c>) is the
    /// ONE caller — the palette has no menu item, toolbar button or binding.
    /// Opening it over a modal dialog left the dialog unresponsive (the
    /// tunnel's palette-navigation block claims Esc/Enter/arrows ahead of the
    /// Esc branch), so if you ever add a second way in, gate it there too.</summary>
    [RelayCommand]
    public void Open()
    {
        _allItems = _catalogProvider().Select(c => new CommandPaletteItem(c, _localization)).ToList();
        Query = string.Empty;
        // OnQueryChanged only fires when the value actually changes; run the
        // filter explicitly (and NOW — opening is a discrete gesture, not a
        // burst) so a re-open with an already-empty query still repopulates
        // the (possibly rebuilt) list.
        _filterPass.RunImmediately();
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    /// <summary>Moves the highlighted row by <paramref name="delta" /> (wrapping),
    /// driven by the Up/Down arrow keys.</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        var index = SelectedItem is null ? -1 : Results.IndexOf(SelectedItem);
        index = ((index + delta) % Results.Count + Results.Count) % Results.Count;
        SelectedItem = Results[index];
    }

    /// <summary>Runs the highlighted command (Enter). Closes first so a command
    /// that opens a dialog isn't hidden behind the palette scrim.</summary>
    public void ExecuteSelected()
    {
        if (SelectedItem is { } item) Execute(item);
    }

    public void Execute(CommandPaletteItem item)
    {
        Close();
        if (!item.IsEnabled) return;
        item.Command.Execute();
    }

    private void ApplyFilter()
    {
        var ranked = CommandFuzzyMatcher.Rank(Query, _allItems, i => i.Title, i => i.Category);

        Results.Clear();
        foreach (var item in ranked) Results.Add(item);

        SelectedItem = Results.Count > 0 ? Results[0] : null;
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(TotalCount));
    }
}
