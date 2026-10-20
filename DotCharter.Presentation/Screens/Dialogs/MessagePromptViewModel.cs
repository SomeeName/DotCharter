using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The message prompt behind <c>IDialogs.MessageAsync</c>. One instance per open.</summary>
public sealed class MessagePromptViewModel : ObservableObject, IMessagePromptScreen, IMessagePromptSetup
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _title = "";
    private string _message = "";
    private bool _isConfirmation;

    public MessagePromptViewModel()
    {
        OkCommand = new RelayCommand(() => _result.TrySetResult(true));
        CancelCommand = new RelayCommand(() => _result.TrySetResult(false));
    }

    public Task<bool> Result => _result.Task;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message.Length > 0;

    public bool IsConfirmation
    {
        get => _isConfirmation;
        private set => SetProperty(ref _isConfirmation, value);
    }

    public ICommand OkCommand { get; }

    public ICommand CancelCommand { get; }

    public void Configure(string title, string message, bool isConfirmation)
    {
        Title = title ?? "";
        Message = message ?? "";
        IsConfirmation = isConfirmation;
    }
}
