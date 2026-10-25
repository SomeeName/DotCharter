using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free Play tab in every state the screen can be in.</summary>
public sealed partial class FakePlaySettingsScreen : ObservableObject, IPlaySettingsScreen
{
    [ObservableProperty] private int _gameplayCountInIndex = 2;
    [ObservableProperty] private bool _gameplayMetronomeEnabled;
    [ObservableProperty] private bool _gameplayClapsEnabled;
    [ObservableProperty] private bool _gameplayRockMeterEnabled;
    [ObservableProperty] private int _stringOrderIndex;

    private FakePlaySettingsScreen()
    {
    }

    public static IReadOnlyList<FakeVariant<IPlaySettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    public static FakePlaySettingsScreen Default() => new();

    /// <summary>No count-in and nothing sounding.</summary>
    public static FakePlaySettingsScreen Empty() => new() { GameplayCountInIndex = 0 };

    /// <summary>The longest count-in; the labels are the view's, so length comes from the culture under test.</summary>
    public static FakePlaySettingsScreen LongText() => new() { GameplayCountInIndex = 3, GameplayRockMeterEnabled = true };

    /// <summary>The longest count-in and every sound on.</summary>
    public static FakePlaySettingsScreen Full() => new()
    {
        GameplayCountInIndex = 3,
        GameplayMetronomeEnabled = true,
        GameplayClapsEnabled = true,
        GameplayRockMeterEnabled = true,
        StringOrderIndex = 1
    };

    public IReadOnlySet<string> HiddenKeys { get; } = new HashSet<string>();
}
