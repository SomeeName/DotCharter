using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IFhpScreen" />; Save answers true, Cancel false.</summary>
public sealed class FakeFhpScreen : FakeDialog<bool>, IFhpScreen
{
    private FakeFhpScreen(int fret, int width)
    {
        Fret = fret;
        Width = width;
        SaveCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IFhpScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText)
    ];

    public static FakeFhpScreen Default() => new(5, 4);

    public static FakeFhpScreen Empty() => new(1, 1);

    /// <summary>The widest values the inputs take.</summary>
    public static FakeFhpScreen LongText() => new(24, 24);

    public int Fret { get; set; }

    public int Width { get; set; }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }
}
