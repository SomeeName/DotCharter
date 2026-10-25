using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>One scope card; selecting it tells the editor, which relights the strip.</summary>
public sealed class FakeShapeEditScope : ObservableObject, IShapeEditScope
{
    private bool _isSelected;

    internal FakeShapeEditScope(string label, string detail, int reach, int handShapes, bool isAvailable, Action<FakeShapeEditScope> select)
    {
        Label = label;
        Detail = detail;
        Reach = reach;
        HandShapes = handShapes;
        IsAvailable = isAvailable;
        SelectCommand = new RelayCommand(() => select(this), () => IsAvailable);
    }

    public string Label { get; }

    public string Detail { get; }

    /// <summary>How many uses the scope reaches; the strip lights that many.</summary>
    internal int Reach { get; }

    /// <summary>How many hand shapes the scope reaches when the fixture says so; -1 to scale with <see cref="Reach" />.</summary>
    internal int HandShapes { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public bool IsAvailable { get; }

    public ICommand SelectCommand { get; }
}

/// <summary>One string row; typing marks it changed against its starting values.</summary>
public sealed class FakeShapeEditString : ObservableObject, IShapeEditString
{
    private readonly string _startFret;
    private readonly string _startFinger;
    private string _fret;
    private string _finger;

    internal FakeShapeEditString(string stringName, string startFret, string startFinger, string fret, string finger)
    {
        StringName = stringName;
        _startFret = startFret;
        _startFinger = startFinger;
        _fret = fret;
        _finger = finger;
    }

    public string StringName { get; }

    public string Fret
    {
        get => _fret;
        set
        {
            if (SetProperty(ref _fret, value)) OnPropertyChanged(nameof(IsChanged));
        }
    }

    public string Finger
    {
        get => _finger;
        set
        {
            if (SetProperty(ref _finger, value)) OnPropertyChanged(nameof(IsChanged));
        }
    }

    public bool IsChanged => _fret != _startFret || _finger != _startFinger;
}

/// <summary>One bar of the song strip.</summary>
public sealed record FakeShapeUseBar(bool IsUse, bool IsReached, bool IsOrigin) : IShapeUseBar;
