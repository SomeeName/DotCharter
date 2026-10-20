using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ Appearance: the colour-blind string palette, the interface
///     scale and the per-colour customiser of the app's one theme. Every row
///     previews LIVE; Save persists it and leaving without saving puts the
///     persisted look back.
/// </summary>
public interface IAppearanceSettingsScreen : INotifyPropertyChanged
{
    /// <summary>The string palettes' display names; the first is the theme's own colours.</summary>
    IReadOnlyList<string> StringPaletteChoices { get; }

    int SelectedStringPaletteIndex { get; set; }

    /// <summary>Auto first, then the fixed percentages.</summary>
    IReadOnlyList<string> UiScaleChoices { get; }

    int UiScaleChoiceIndex { get; set; }

    /// <summary>The factor being applied now, formatted ("125 %"); under Auto, the one Auto chose.</summary>
    string UiScaleReadout { get; }

    /// <summary>The customizable colours under their headings, in catalogue order.</summary>
    IReadOnlyList<IAppearanceColorGroup> ColorGroups { get; }

    /// <summary>Every colour back to the theme's, keeping the selected string palette.</summary>
    ICommand ResetColorsCommand { get; }

    /// <summary>Row and group keys the current search filters OUT. Empty when there is no query.</summary>
    IReadOnlySet<string> HiddenKeys { get; }
}

/// <summary>One heading of the colour list and the colours under it.</summary>
public interface IAppearanceColorGroup : INotifyPropertyChanged
{
    string Title { get; }

    IReadOnlyList<IAppearanceColorItem> Items { get; }

    /// <summary>False while a search query matches none of its colours.</summary>
    bool IsVisible { get; }
}

/// <summary>One customizable colour.</summary>
public interface IAppearanceColorItem : INotifyPropertyChanged
{
    string Name { get; }

    /// <summary><c>#RRGGBB</c>, or <c>#AARRGGBB</c> when not opaque. Writing it previews the colour live.</summary>
    string Hex { get; set; }

    /// <summary>False while a search query matches neither this colour nor its group.</summary>
    bool IsVisible { get; }
}
