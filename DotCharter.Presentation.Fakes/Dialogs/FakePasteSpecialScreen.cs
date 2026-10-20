using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IPasteSpecialScreen" />; Apply answers true, Cancel false.</summary>
public sealed class FakePasteSpecialScreen : FakeDialog<bool>, IPasteSpecialScreen
{
    private int _mode;
    private bool _useMousePosition;

    private FakePasteSpecialScreen(int mode, bool atMouse)
    {
        _mode = mode;
        _useMousePosition = atMouse;
        ApplyCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IPasteSpecialScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText)
    ];

    public static FakePasteSpecialScreen Default() => new(0, false);

    /// <summary>Nothing to fill: the dialog has no free text, so this is the other end of the choices.</summary>
    public static FakePasteSpecialScreen Empty() => new(2, true);

    /// <summary>The dialog's copy is all resx; the middle mode is the longest line in every language.</summary>
    public static FakePasteSpecialScreen LongText() => new(1, true);

    public bool IsPreserve
    {
        get => _mode == 0;
        set => Pick(0, value);
    }

    public bool IsOverwriteSameType
    {
        get => _mode == 1;
        set => Pick(1, value);
    }

    public bool IsOverwriteAll
    {
        get => _mode == 2;
        set => Pick(2, value);
    }

    public bool UseMousePosition
    {
        get => _useMousePosition;
        set => SetProperty(ref _useMousePosition, value);
    }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    private void Pick(int mode, bool on)
    {
        if (!on || _mode == mode) return;
        _mode = mode;
        OnPropertyChanged(nameof(IsPreserve));
        OnPropertyChanged(nameof(IsOverwriteSameType));
        OnPropertyChanged(nameof(IsOverwriteAll));
    }
}
