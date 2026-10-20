using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="ISetBpmScreen" />; Save answers true, Cancel false.</summary>
public sealed class FakeSetBpmScreen : FakeDialog<bool>, ISetBpmScreen
{
    private FakeSetBpmScreen(double bpm)
    {
        Bpm = bpm;
        SaveCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<ISetBpmScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText)
    ];

    public static FakeSetBpmScreen Default() => new(120);

    public static FakeSetBpmScreen Empty() => new(1);

    /// <summary>The widest value the input takes.</summary>
    public static FakeSetBpmScreen LongText() => new(987.654);

    public double Bpm { get; set; }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }
}
