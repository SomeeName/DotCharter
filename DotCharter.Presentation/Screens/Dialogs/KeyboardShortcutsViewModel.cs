using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Contracts.Shell;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     Help ▸ Keyboard shortcuts. The catalog is read once, when it opens; the
///     filter is synchronous because the source is ~100 in-memory rows.
/// </summary>
public sealed class KeyboardShortcutsViewModel : DialogViewModel<bool>, IKeyboardShortcutsScreen
{
    private readonly IReadOnlyList<ShortcutEntry> _snapshot;
    private string _filterText = "";
    private IReadOnlyList<IShortcutGroup> _groups;

    public KeyboardShortcutsViewModel(IShortcutCatalog catalog)
    {
        _snapshot = catalog.CurrentShortcuts();
        _groups = BuildGroups(_snapshot, "");
        CloseCommand = new RelayCommand(() => Close(true));
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetProperty(ref _filterText, value ?? "")) return;
            _groups = BuildGroups(_snapshot, _filterText);
            OnPropertyChanged(nameof(Groups));
            OnPropertyChanged(nameof(HasNoMatches));
        }
    }

    public IReadOnlyList<IShortcutGroup> Groups => _groups;

    public bool HasNoMatches => _groups.Count == 0;

    public ICommand CloseCommand { get; }

    /// <summary>
    ///     The pure half: entries → grouped, filtered rows. Categories keep the
    ///     catalog's order, and the filter matches a title OR a chord.
    /// </summary>
    public static IReadOnlyList<IShortcutGroup> BuildGroups(IEnumerable<ShortcutEntry> entries, string filter)
    {
        var rowsByCategory = new Dictionary<string, List<IShortcutRow>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var entry in entries)
        {
            if (filter.Length > 0
                && !entry.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !entry.Chord.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!rowsByCategory.TryGetValue(entry.Category, out var rows))
            {
                rowsByCategory[entry.Category] = rows = [];
                order.Add(entry.Category);
            }

            rows.Add(new Row(entry.Title, entry.Chord));
        }

        return order.Select(IShortcutGroup (category) => new Group(category, rowsByCategory[category])).ToList();
    }

    private sealed record Group(string Name, IReadOnlyList<IShortcutRow> Rows) : IShortcutGroup;

    private sealed record Row(string Title, string Chord) : IShortcutRow;
}
