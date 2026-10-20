using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IMessagePromptScreen" />; OK and Cancel complete its result.</summary>
public sealed class FakeMessagePromptScreen : ObservableObject, IMessagePromptScreen
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FakeMessagePromptScreen(string title, string message, bool isConfirmation)
    {
        Title = title;
        Message = message;
        IsConfirmation = isConfirmation;
        OkCommand = new RelayCommand(() => _result.TrySetResult(true));
        CancelCommand = new RelayCommand(() => _result.TrySetResult(false));
    }

    public static IReadOnlyList<FakeVariant<IMessagePromptScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Info),
        new(FakeVariantNames.Empty, TitleOnly),
        new(FakeVariantNames.LongText, LongText),
        new("Confirm", Confirm)
    ];

    public static FakeMessagePromptScreen Info() =>
        new("Chart saved", "Master of Puppets.dcproj was written to your projects folder.", isConfirmation: false);

    public static FakeMessagePromptScreen TitleOnly() => new("Nothing to export", "", isConfirmation: false);

    public static FakeMessagePromptScreen Confirm() =>
        new("Discard the imported tones?", "The eleven tones from this import are not saved anywhere else.",
            isConfirmation: true);

    public static FakeMessagePromptScreen LongText() =>
        new("Die Arrangement-Datei konnte nicht vollständig gelesen werden",
            "Donaudampfschifffahrtsgesellschaftskapitänsmütze: 1.204 Noten wurden übernommen, 37 Akkorde " +
            "ohne Griffbild wurden verworfen. 非常に長いメッセージ本文のテストケースです。インポートされたトーンは" +
            "保存されていません。続行すると元に戻せません。",
            isConfirmation: true);

    public Task<bool> Result => _result.Task;

    public string Title { get; }

    public string Message { get; }

    public bool HasMessage => Message.Length > 0;

    public bool IsConfirmation { get; }

    public ICommand OkCommand { get; }

    public ICommand CancelCommand { get; }
}
