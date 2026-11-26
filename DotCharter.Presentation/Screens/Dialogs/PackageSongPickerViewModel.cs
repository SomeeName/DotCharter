using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The song picker for a multi-song package. One instance per open.</summary>
public sealed class PackageSongPickerViewModel : DialogViewModel<int?>, IPackageSongPickerScreen,
    IDialogSetup<PackageSongPickerRequest>, ICancelableDialog
{
    private readonly RelayCommand _open;
    private IPackageSongRow? _selected;

    public PackageSongPickerViewModel()
    {
        _open = new RelayCommand(Open, () => Selected is not null);
        CancelCommand = new RelayCommand(() => Close(null));
    }

    public string PackageName { get; private set; } = "";

    public IReadOnlyList<IPackageSongRow> Songs { get; private set; } = [];

    public IPackageSongRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) _open.NotifyCanExecuteChanged();
        }
    }

    public ICommand OpenCommand => _open;

    public ICommand CancelCommand { get; }

    public void Configure(PackageSongPickerRequest request)
    {
        PackageName = request.PackageName;
        Songs = [.. request.Songs.Select(song => new Row(song))];
        OnPropertyChanged(nameof(PackageName));
        OnPropertyChanged(nameof(Songs));
        Selected = Songs.FirstOrDefault();
    }

    /// <summary>Esc answers nothing chosen.</summary>
    public bool TryCancel()
    {
        Close(null);
        return true;
    }

    private void Open()
    {
        var index = Selected is null ? -1 : Songs.ToList().IndexOf(Selected);
        if (index >= 0) Close(index);
    }

    private sealed class Row(PackageSongChoice song) : IPackageSongRow
    {
        public string Title { get; } = song.Title;

        public string Detail { get; } = song.Detail;

        public override string ToString() => Title;
    }
}
