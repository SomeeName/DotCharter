using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>
///     A services-free <see cref="IUnsavedChangesScreen" />. Every word on the card
///     is a resource string, so the variants differ only by name; LongText is what
///     the gallery shows under de-DE and ja-JP.
/// </summary>
public sealed class FakeUnsavedChangesScreen : FakeDialog<UnsavedChangesChoice>, IUnsavedChangesScreen
{
    private FakeUnsavedChangesScreen()
    {
        SaveCommand = Answer(UnsavedChangesChoice.Save);
        DiscardCommand = Answer(UnsavedChangesChoice.Discard);
        CancelCommand = Answer(UnsavedChangesChoice.Cancel);
    }

    public static IReadOnlyList<FakeVariant<IUnsavedChangesScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText)
    ];

    public static FakeUnsavedChangesScreen Default() => new();

    public static FakeUnsavedChangesScreen Empty() => new();

    public static FakeUnsavedChangesScreen LongText() => new();

    public ICommand SaveCommand { get; }

    public ICommand DiscardCommand { get; }

    public ICommand CancelCommand { get; }
}
