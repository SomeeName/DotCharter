using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free Appearance tab in every state the screen can be in.</summary>
public sealed partial class FakeAppearanceSettingsScreen : ObservableObject, IAppearanceSettingsScreen
{
    [ObservableProperty] private int _selectedStringPaletteIndex;
    [ObservableProperty] private int _uiScaleChoiceIndex;
    [ObservableProperty] private string _uiScaleReadout = "100 %";

    private FakeAppearanceSettingsScreen(IReadOnlyList<IAppearanceColorGroup> groups)
    {
        ColorGroups = groups;
        ResetColorsCommand = new RelayCommand(() => { });
    }

    public static IReadOnlyList<FakeVariant<IAppearanceSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    /// <summary>Two headings of a few colours each, the theme's own string colours, Auto scale.</summary>
    public static FakeAppearanceSettingsScreen Default() => new(
    [
        Group("Surface", ("App background", "#0B0C0E"), ("Panel background", "#131417"), ("Card background", "#1A1C20")),
        Group("Accent", ("Accent", "#8B5CF6"), ("Accent hover", "#A78BFA"))
    ]);

    /// <summary>No customizable colours at all.</summary>
    public static FakeAppearanceSettingsScreen Empty() => new([]);

    /// <summary>German headings and names, a Japanese one, and a translucent colour's eight-digit readout.</summary>
    public static FakeAppearanceSettingsScreen LongText() => new(
    [
        Group("Oberflächen und Hintergründe",
            ("Hintergrund der gesamten Anwendung", "#0B0C0E"), ("Hintergrund der Seitenleisten und Bedienfelder", "#80131417")),
        Group("文字とアクセントの色", ("アクセントカラー（ホバー時）", "#A78BFA"))
    ])
    {
        SelectedStringPaletteIndex = 1,
        UiScaleChoiceIndex = 2,
        UiScaleReadout = "125 %"
    };

    /// <summary>Every heading the app has, several colours each, the largest scale.</summary>
    public static FakeAppearanceSettingsScreen Full() => new(
    [
        Group("Surface", ("App background", "#0B0C0E"), ("Panel background", "#131417"), ("Card background", "#1A1C20"),
            ("Elevated surface", "#202227"), ("Hover surface", "#262930"), ("Border", "#2F3236")),
        Group("Text", ("Primary text", "#E8EAED"), ("Secondary text", "#B4B9C0"), ("Muted text", "#7A8390")),
        Group("Accent", ("Accent", "#8B5CF6"), ("Accent hover", "#A78BFA")),
        Group("Strings", ("String 1", "#E0443E"), ("String 2", "#E8C547"), ("String 3", "#4C8DF6"),
            ("String 4", "#F28C28"), ("String 5", "#3FB950"), ("String 6", "#B659E0"))
    ])
    {
        SelectedStringPaletteIndex = 2,
        UiScaleChoiceIndex = 5,
        UiScaleReadout = "200 %"
    };

    public IReadOnlyList<string> StringPaletteChoices { get; } =
        ["Default (theme)", "Okabe–Ito (color-blind safe)", "IBM (color-blind safe)"];

    public IReadOnlyList<string> UiScaleChoices { get; } = ["Auto", "100 %", "125 %", "150 %", "175 %", "200 %"];

    public IReadOnlyList<IAppearanceColorGroup> ColorGroups { get; }

    public ICommand ResetColorsCommand { get; }

    public IReadOnlySet<string> HiddenKeys { get; } = new HashSet<string>();

    private static FakeColorGroup Group(string title, params (string Name, string Hex)[] items) =>
        new(title, [.. items.Select(i => new FakeColorItem(i.Name, i.Hex))]);

    private sealed class FakeColorGroup(string title, IReadOnlyList<IAppearanceColorItem> items)
        : ObservableObject, IAppearanceColorGroup
    {
        public string Title { get; } = title;

        public IReadOnlyList<IAppearanceColorItem> Items { get; } = items;

        public bool IsVisible => true;
    }

    private sealed partial class FakeColorItem(string name, string hex) : ObservableObject, IAppearanceColorItem
    {
        [ObservableProperty] private string _hex = hex;

        public string Name { get; } = name;

        public bool IsVisible => true;
    }
}
