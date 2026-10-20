using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>One rail row over one tab. The texts are read from the localization service on every get, so a language change only needs <see cref="Refresh" />.</summary>
public sealed class SettingsRailItemViewModel : ObservableObject, ISettingsRailItem
{
    private readonly ILocalizationService _localization;
    private bool _isSelected;

    public SettingsRailItemViewModel(ISettingsTab tab, SettingsRailEntry entry, ILocalizationService localization,
        Action<SettingsRailItemViewModel> select)
    {
        Tab = tab;
        Entry = entry;
        _localization = localization;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public ISettingsTab Tab { get; }

    public SettingsRailEntry Entry { get; }

    public string Title => _localization[Entry.TitleKey];

    public string Description => _localization[Entry.DescriptionKey];

    public string IconKey => Entry.IconKey;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            // Announced even when unchanged: a click on the already-selected
            // row toggles the button off locally, and only a notification puts
            // the one-way binding's true back.
            OnPropertyChanged();
        }
    }

    public ICommand SelectCommand { get; }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
    }
}

/// <summary>An eyebrow caption over a run of rail rows.</summary>
public sealed class SettingsRailSectionViewModel : ObservableObject, ISettingsRailSection
{
    private readonly ILocalizationService _localization;
    private readonly string _titleKey;

    public SettingsRailSectionViewModel(string titleKey, IReadOnlyList<SettingsRailItemViewModel> items,
        ILocalizationService localization)
    {
        _titleKey = titleKey;
        _localization = localization;
        Items = items;
    }

    public string Title => _localization[_titleKey];

    public IReadOnlyList<ISettingsRailItem> Items { get; }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        foreach (var item in Items.OfType<SettingsRailItemViewModel>()) item.Refresh();
    }
}

/// <summary>One row of the results dropdown.</summary>
public sealed class SettingsSearchHitViewModel : ObservableObject, ISettingsSearchHit
{
    private bool _isHighlighted;

    public SettingsSearchHitViewModel(SettingsSearchHit hit, Action<SettingsSearchHitViewModel> go)
    {
        Hit = hit;
        GoCommand = new RelayCommand(() => go(this));
    }

    public SettingsSearchHit Hit { get; }

    public string Label => Hit.Label;

    public string GroupName => Hit.GroupName ?? "";

    public bool HasGroupName => !string.IsNullOrEmpty(Hit.GroupName);

    public string TabName => Hit.TabName;

    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }

    public ICommand GoCommand { get; }
}
