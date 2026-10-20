using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IBeatScreen" />; Save answers true, Cancel false.</summary>
public sealed class FakeBeatScreen : FakeDialog<bool>, IBeatScreen
{
    private FakeBeatScreen(double bpm, int beatsInMeasure, int denominator, bool isAnchor)
    {
        Bpm = bpm;
        BeatsInMeasure = beatsInMeasure;
        NoteDenominator = denominator;
        IsAnchor = isAnchor;
        SaveCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IBeatScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText)
    ];

    public static FakeBeatScreen Default() => new(120, 4, 4, false);

    public static FakeBeatScreen Empty() => new(1, 1, 1, false);

    /// <summary>The widest values the inputs take.</summary>
    public static FakeBeatScreen LongText() => new(987.654, 32, 64, true);

    public double Bpm { get; set; }

    public int BeatsInMeasure { get; set; }

    public int NoteDenominator { get; set; }

    public bool IsAnchor { get; set; }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }
}
