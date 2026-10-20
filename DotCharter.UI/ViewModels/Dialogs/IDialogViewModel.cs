namespace DotCharter.UI.ViewModels.Dialogs;

public interface IDialogViewModel
{
    bool IsOpen { get; set; }
    void RequestClose();
}
