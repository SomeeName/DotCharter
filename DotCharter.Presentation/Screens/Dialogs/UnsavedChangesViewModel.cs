using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The close gate and the replace-project guard. Esc and a close from
///     outside complete with the default, <see cref="UnsavedChangesChoice.Cancel" />.
/// </summary>
public sealed class UnsavedChangesViewModel : DialogViewModel<UnsavedChangesChoice>, IUnsavedChangesScreen
{
    public UnsavedChangesViewModel()
    {
        SaveCommand = new RelayCommand(() => Close(UnsavedChangesChoice.Save));
        DiscardCommand = new RelayCommand(() => Close(UnsavedChangesChoice.Discard));
        CancelCommand = new RelayCommand(() => Close(UnsavedChangesChoice.Cancel));
    }

    public ICommand SaveCommand { get; }

    public ICommand DiscardCommand { get; }

    public ICommand CancelCommand { get; }
}
