using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>The result half every dialog fake shares: a verb completes it once.</summary>
public abstract class FakeDialog<TResult> : ObservableObject
{
    private readonly TaskCompletionSource<TResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<TResult> Result => _result.Task;

    protected ICommand Answer(TResult result) => new RelayCommand(() => _result.TrySetResult(result));

    protected ICommand Disabled() => new RelayCommand(() => { }, () => false);
}
