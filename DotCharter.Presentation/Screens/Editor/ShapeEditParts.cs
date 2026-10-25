using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>One scope of the shape editor: its words, and whether it is the one Apply will carry out.</summary>
public sealed partial class ShapeEditScopeViewModel : ObservableObject, IShapeEditScope
{
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isAvailable;

    public ShapeEditScopeViewModel(string label, Action<ShapeEditScopeViewModel> select)
    {
        Label = label;
        SelectCommand = new RelayCommand(() => select(this), () => IsAvailable);
    }

    public string Label { get; }

    public ICommand SelectCommand { get; }

    partial void OnIsAvailableChanged(bool value) => ((RelayCommand)SelectCommand).NotifyCanExecuteChanged();
}

/// <summary>One string of the grip, typed as text: a fret (<c>x</c> unplayed, <c>0</c> open) and a finger (1-4, T).</summary>
public sealed partial class ShapeEditStringViewModel : ObservableObject, IShapeEditString
{
    private readonly string _startFret;
    private readonly string _startFinger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private string _fret;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private string _finger;

    public ShapeEditStringViewModel(int stringIndex, string stringName, string fret, string finger)
    {
        StringIndex = stringIndex;
        StringName = stringName;
        _startFret = _fret = fret;
        _startFinger = _finger = finger;
    }

    public int StringIndex { get; }

    public string StringName { get; }

    public bool IsChanged => !string.Equals(Normal(Fret), Normal(_startFret), StringComparison.Ordinal)
                             || !string.Equals(Finger.Trim(), _startFinger, StringComparison.OrdinalIgnoreCase);

    private static string Normal(string fret) => fret.Trim().Length == 0 ? "x" : fret.Trim().ToLowerInvariant();
}

/// <summary>One bar of the song strip.</summary>
public sealed record ShapeEditUseBar(bool IsUse, bool IsReached, bool IsOrigin) : IShapeUseBar;
