using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     One column facet of the song browser's datagrid-style filtering: a set of
///     checkable options derived from the SCANNED library (never a fixed list — a
///     tuning the library does not contain must not be offerable), matched
///     against a per-row canonical VALUE.
///     <para>
///         The contract is uniform across every facet: NO selection means the
///         facet is inert (everything passes); any selection means a row must
///         match ONE of the selected values. Unknown values (no year, never
///         rated) are an EXPLICIT option with their own label rather than an
///         implicit pass-through — with the option visible, including or
///         excluding unknowns is the user's call, which is stronger than the
///         silent always-passes rule the older single-select filters used
///         (that rule predates having anywhere to SHOW an unknown bucket).
///     </para>
///     <para>
///         Selection state lives in the <see cref="Selected" /> set (what the
///         predicate reads — O(1) per row) and mirrors into each option's
///         <see cref="BrowserFacetOption.IsChecked" /> (what the popover binds).
///         The owner view model wires <see cref="SelectionChanged" /> to its
///         filter pass; a bulk clear suppresses per-option callbacks and fires
///         once.
///     </para>
/// </summary>
public sealed class BrowserFacet : ObservableObject
{
    private bool _suppressCallbacks;

    public BrowserFacet(string key, string title)
    {
        Key = key;
        Title = title;
        ClearFacetCommand = new RelayCommand(Clear);
    }

    /// <summary>The popover footer's "clear" — unchecks this one facet. (The
    /// deliberately clunky name avoids shadowing PedalboardViewModel's unbound
    /// clear wrapper: the reachability lints match bare names across view
    /// models, comments included.)</summary>
    public IRelayCommand ClearFacetCommand { get; }

    /// <summary>Stable identity ("tuning", "year", …) — chips and the removal
    /// mapping key on it, so it is never a localized string.</summary>
    public string Key { get; }

    /// <summary>Localized popover title (the column caption's word).</summary>
    public string Title { get; }

    public ObservableCollection<BrowserFacetOption> Options { get; } = [];

    /// <summary>The canonical values currently selected — what the predicate
    /// tests. Empty = facet inert.</summary>
    public HashSet<string> Selected { get; } = new(StringComparer.Ordinal);

    /// <summary>True while any option is selected — drives the funnel's lit
    /// state and the clear affordances. Change-notified from every selection
    /// mutation, because the funnel's class binding reads it live.</summary>
    public bool IsActive => Selected.Count > 0;

    /// <summary>Raised once per user gesture (a toggle, a clear) — the owner
    /// runs the filter pass off it.</summary>
    public event Action? SelectionChanged;

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(IsActive));
        SelectionChanged?.Invoke();
    }

    /// <summary>Replaces the option list (after a scan), preserving the current
    /// selection where the value still exists — the same courtesy the old
    /// dropdowns extended across rescans. Values that vanished from the library
    /// are dropped from the selection.</summary>
    public void ResetOptions(IEnumerable<(string Value, string Label)> options)
    {
        _suppressCallbacks = true;
        try
        {
            Options.Clear();
            var kept = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (value, label) in options)
            {
                var option = new BrowserFacetOption(this, value, label)
                {
                    IsChecked = Selected.Contains(value)
                };
                if (option.IsChecked) kept.Add(value);
                Options.Add(option);
            }

            Selected.IntersectWith(kept);
        }
        finally
        {
            _suppressCallbacks = false;
        }

        OnPropertyChanged(nameof(IsActive));
    }

    /// <summary>Writes each option's match count (<c>LibraryQuery.CountFacet</c>); a value with no row reads 0.</summary>
    public void ApplyCounts(IReadOnlyDictionary<string, int> counts)
    {
        foreach (var option in Options)
            option.Count = counts.GetValueOrDefault(option.Value);
    }

    /// <summary>Unchecks everything. One <see cref="SelectionChanged" /> when
    /// anything was selected; silent when already clear.</summary>
    public void Clear()
    {
        if (Selected.Count == 0) return;
        _suppressCallbacks = true;
        try
        {
            Selected.Clear();
            foreach (var option in Options) option.IsChecked = false;
        }
        finally
        {
            _suppressCallbacks = false;
        }

        NotifySelection();
    }

    /// <summary>Unchecks one value (a chip's ✕). Silent no-op when it was not
    /// selected.</summary>
    public void Deselect(string value)
    {
        if (!Selected.Remove(value)) return;
        _suppressCallbacks = true;
        try
        {
            foreach (var option in Options)
            {
                if (option.Value == value)
                    option.IsChecked = false;
            }
        }
        finally
        {
            _suppressCallbacks = false;
        }

        NotifySelection();
    }

    internal void OnOptionToggled(BrowserFacetOption option)
    {
        if (_suppressCallbacks) return;
        if (option.IsChecked) Selected.Add(option.Value);
        else Selected.Remove(option.Value);
        NotifySelection();
    }
}

/// <summary>One checkable row of a facet popover: the canonical value the
/// predicate matches, the localized label the row prints, and the match count
/// the popover shows beside it (refreshed when the popover opens — counting on
/// every filter pass would tax typing for a number nobody is looking at).</summary>
public sealed partial class BrowserFacetOption : ObservableObject
{
    private readonly BrowserFacet _owner;

    public BrowserFacetOption(BrowserFacet owner, string value, string label)
    {
        _owner = owner;
        Value = value;
        Label = label;
    }

    public string Value { get; }
    public string Label { get; }

    /// <summary>"(n)" — how many rows would show if ONLY this option were added
    /// to the current selection (every other facet + the search applied, this
    /// facet counted per option).</summary>
    [ObservableProperty] private int _count;

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        _ = value;
        _owner.OnOptionToggled(this);
    }
}
