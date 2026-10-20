using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>One colour row. A write from the picker goes to <paramref name="preview" />; a refresh from the theme does not.</summary>
public sealed class AppearanceColorItemViewModel(string tokenKey, string searchKey, Action<string, string> preview)
    : ObservableObject, IAppearanceColorItem
{
    private string _name = "";
    private string _hex = "";
    private bool _isVisible = true;

    public string TokenKey { get; } = tokenKey;

    public string SearchKey { get; } = searchKey;

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public string Hex
    {
        get => _hex;
        set
        {
            if (string.Equals(_hex, value, StringComparison.OrdinalIgnoreCase)) return;
            preview(TokenKey, value);
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>Takes the theme's current value and the localized name without previewing anything.</summary>
    public void Refresh(string name, string hex)
    {
        Name = name;
        SetProperty(ref _hex, hex, nameof(Hex));
    }
}

/// <summary>One heading of the colour list.</summary>
public sealed class AppearanceColorGroupViewModel(string titleKey, IReadOnlyList<AppearanceColorItemViewModel> items)
    : ObservableObject, IAppearanceColorGroup
{
    private string _title = "";
    private bool _isVisible = true;

    public string TitleKey { get; } = titleKey;

    public IReadOnlyList<AppearanceColorItemViewModel> Rows { get; } = items;

    public IReadOnlyList<IAppearanceColorItem> Items => Rows;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    public void RefreshVisibility() => IsVisible = Rows.Any(r => r.IsVisible);
}
