using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IVocalTrackSettingsScreen" />; Save answers true, Cancel false.</summary>
public sealed class FakeVocalTrackSettingsScreen : FakeDialog<bool>, IVocalTrackSettingsScreen
{
    private string _name;
    private string _trackColor = "";

    private FakeVocalTrackSettingsScreen(string name, string color)
    {
        _name = name;
        ColorSwatches =
        [
            new Swatch(this, "#FFFFFF", "White"),
            new Swatch(this, "#A78BFA", "Purple"),
            new Swatch(this, "#4ADE80", "Green"),
            new Swatch(this, "#F0A54A", "Amber")
        ];
        TrackColor = color;
        SaveCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IVocalTrackSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    public static FakeVocalTrackSettingsScreen Default() => new("Vocals", "#A78BFA");

    /// <summary>No name yet, and a picked colour none of the swatches offers.</summary>
    public static FakeVocalTrackSettingsScreen Empty() => new("", "#3366CC");

    public static FakeVocalTrackSettingsScreen LongText() =>
        new("Harmoniestimme (zweite Strophe, doppelt eingesungen) — ハーモニー", "#FFFFFF");

    /// <summary>The swatch list is fixed at four; this is its last swatch picked.</summary>
    public static FakeVocalTrackSettingsScreen Full() => new("Backing vocals", "#F0A54A");

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string TrackColor
    {
        get => _trackColor;
        set
        {
            if (!SetProperty(ref _trackColor, value)) return;
            foreach (var swatch in ColorSwatches.Cast<Swatch>())
                swatch.IsSelected = string.Equals(swatch.Color, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<ILyricColorSwatch> ColorSwatches { get; }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    private sealed class Swatch(FakeVocalTrackSettingsScreen owner, string color, string name)
        : ObservableObject, ILyricColorSwatch
    {
        private bool _isSelected;

        public string Name { get; } = name;

        public string Color { get; } = color;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public ICommand PickCommand { get; } = new RelayCommand(() => owner.TrackColor = color);
    }
}
