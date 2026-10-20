using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     The Settings page's chrome: the rail of tabs, the cross-tab search and
///     the footer — the unsaved count, Reset section, Cancel and Save. Each
///     tab's body is a screen of its own; the view's tab host realizes the
///     contracts in <see cref="TabScreens" /> and shows <see cref="CurrentScreen" />.
/// </summary>
public interface ISettingsShellScreen : INotifyPropertyChanged
{
    /// <summary>The rail, in order: an eyebrow caption over each run of tabs.</summary>
    IReadOnlyList<ISettingsRailSection> Sections { get; }

    /// <summary>The screen contracts of the tabs that are screens, in rail order.</summary>
    IReadOnlyList<Type> TabScreens { get; }

    /// <summary>The selected tab's screen contract, or null while the selected tab is hosted elsewhere.</summary>
    Type? CurrentScreen { get; }

    string SearchQuery { get; set; }

    bool HasSearchQuery { get; }

    /// <summary>The results dropdown is open. Two-way: a light dismiss closes it from the view.</summary>
    bool IsSearchOpen { get; set; }

    /// <summary>At most the first forty hits; <see cref="SearchResultCountText" /> reports the real total.</summary>
    IReadOnlyList<ISettingsSearchHit> SearchResults { get; }

    bool HasSearchResults { get; }

    string SearchResultCountText { get; }

    ICommand ClearSearchCommand { get; }

    ICommand HighlightNextResultCommand { get; }

    ICommand HighlightPreviousResultCommand { get; }

    /// <summary>Jumps to the highlighted result, or to the first one when none is highlighted.</summary>
    ICommand ActivateResultCommand { get; }

    /// <summary>"2 unsaved changes" / "No unsaved changes".</summary>
    string UnsavedChangesText { get; }

    bool HasUnsavedChanges { get; }

    /// <summary>Asks, then puts the SELECTED tab back to its saved values; every other tab keeps its edits. Enabled while that tab has unsaved edits.</summary>
    ICommand ResetSectionCommand { get; }

    ICommand CancelCommand { get; }

    ICommand SaveCommand { get; }
}

public interface ISettingsRailSection : INotifyPropertyChanged
{
    string Title { get; }

    IReadOnlyList<ISettingsRailItem> Items { get; }
}

public interface ISettingsRailItem : INotifyPropertyChanged
{
    string Title { get; }

    /// <summary>The one line under the label; wraps rather than trims.</summary>
    string Description { get; }

    /// <summary>The app resource key of the rail glyph, e.g. <c>IconKeyboard</c>.</summary>
    string IconKey { get; }

    bool IsSelected { get; }

    ICommand SelectCommand { get; }
}

/// <summary>One row of the results dropdown: a setting's label, the group it sits in and the tab it lives on.</summary>
public interface ISettingsSearchHit : INotifyPropertyChanged
{
    string Label { get; }

    /// <summary>Empty for a group reached by its own title — repeating it underneath would read "Theme / Theme".</summary>
    string GroupName { get; }

    bool HasGroupName { get; }

    string TabName { get; }

    /// <summary>The keyboard's row: Up/Down walk the list while focus stays in the search box.</summary>
    bool IsHighlighted { get; }

    ICommand GoCommand { get; }
}
