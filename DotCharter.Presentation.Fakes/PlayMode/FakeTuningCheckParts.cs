using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>One arrangement as a path tile.</summary>
public sealed class FakePathTile : ObservableObject, IPathTile
{
    private FakeTuningCheckScreen? _owner;
    private bool _isSelected;

    public FakePathTile(string name, string typeLabel, string noteCountLabel, string recordLabel)
    {
        Name = name;
        TypeLabel = typeLabel;
        NoteCountLabel = noteCountLabel;
        RecordLabel = recordLabel;
        SelectCommand = new RelayCommand(() => _owner?.Pick(this));
    }

    public string Name { get; }

    public string TypeLabel { get; }

    public string NoteCountLabel { get; }

    public string RecordLabel { get; }

    public bool HasRecord => RecordLabel.Length > 0;

    public bool IsSelected => _isSelected;

    public ICommand SelectCommand { get; }

    internal void Attach(FakeTuningCheckScreen owner) => _owner = owner;

    internal void SetSelected(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));
}

/// <summary>One string of the expected tuning.</summary>
public sealed class FakeTunerString : ObservableObject, ITunerString
{
    private bool _isVerified;
    private bool _isAwaiting;
    private double _cents;
    private string _stateText = "";

    private FakeTunerString(string name) => Name = name;

    /// <summary>A string already heard in tune.</summary>
    public static FakeTunerString Verified(string name) =>
        new(name) { _isVerified = true, _stateText = "IN TUNE", _cents = 0 };

    /// <summary>The string the check is currently listening for.</summary>
    public static FakeTunerString Awaiting(string name) =>
        new(name) { _isAwaiting = true, _stateText = "PLUCK" };

    /// <summary>A string that is measurably off.</summary>
    public static FakeTunerString Off(string name, double cents, string stateText) =>
        new(name) { _cents = cents, _stateText = stateText };

    public string Name { get; }

    public string CentsLabel =>
        _isAwaiting ? "" : (_cents > 0 ? "+" : "") + _cents.ToString("0", CultureInfo.CurrentCulture);

    public string StateText => _stateText;

    /// <summary>
    ///     The needle's position along its track, 0..100. Centre is 50, and the
    ///     range is ±50 cents — past that the string is not the one being
    ///     plucked, which is the check's own rule.
    /// </summary>
    public double NeedlePercent => _isAwaiting ? 50 : Math.Clamp(50 + _cents, 0, 100);

    public bool IsVerified => _isVerified;

    public bool IsAwaiting => _isAwaiting;

    public bool IsOff => !_isVerified && !_isAwaiting;

    internal void Verify()
    {
        _isVerified = true;
        _isAwaiting = false;
        _cents = 0;
        _stateText = "IN TUNE";
        Republish();
    }

    internal void Reset()
    {
        _isVerified = false;
        _isAwaiting = false;
        Republish();
    }

    private void Republish()
    {
        OnPropertyChanged(nameof(IsVerified));
        OnPropertyChanged(nameof(IsAwaiting));
        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(CentsLabel));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(NeedlePercent));
    }
}
