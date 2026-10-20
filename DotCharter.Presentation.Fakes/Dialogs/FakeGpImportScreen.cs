using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IGpImportScreen" />; Import answers true, Cancel false.</summary>
public sealed class FakeGpImportScreen : FakeDialog<bool>, IGpImportScreen
{
    private int _mode;

    private FakeGpImportScreen(IReadOnlyList<IGpTrackRow> tracks, int mode = 0, string oldFile = "", bool isBusy = false,
        string error = "")
    {
        Tracks = tracks;
        _mode = mode;
        OldFilePath = oldFile;
        IsBusy = isBusy;
        ErrorMessage = error;
        PickOldFileCommand = Disabled();
        ImportCommand = isBusy || tracks.Count == 0 ? Disabled() : Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IGpImportScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy),
        new(FakeVariantNames.Error, Error)
    ];

    private static readonly IReadOnlyList<string> Arrangements = ["Lead", "Rhythm", "Bass"];

    public static FakeGpImportScreen Default() => new(
    [
        new Track("Lead", true, Arrangements, 0),
        new Track("Rhythm", true, Arrangements, 1),
        new Track("Bass", false, Arrangements, 2)
    ]);

    /// <summary>A score with no playable track.</summary>
    public static FakeGpImportScreen Empty() => new([]);

    /// <summary>Diff-compare with its older file and per-track targets showing.</summary>
    public static FakeGpImportScreen LongText() => new(
    [
        new Track("Leadgitarre (alternative Stimmung, zweites Solo mit Whammy-Effekten)", true,
            ["Leadgitarre mit alternativer Stimmung", "リズムギター（ドロップD・カポ2）"], 0),
        new Track("リズムギター（ドロップD・カポ2）", true,
            ["Leadgitarre mit alternativer Stimmung", "リズムギター（ドロップD・カポ2）"], 1)
    ], 2, @"D:\Tabs\Die Ärzte – Schrei nach Liebe (Liveaufnahme, frühere Fassung von 2019).gp");

    /// <summary>A full band score in technique-only mode: the list scrolls, every row has a target.</summary>
    public static FakeGpImportScreen Full() => new(
    [
        .. Enumerable.Range(1, 16).Select(IGpTrackRow (i) => new Track($"Track {i}", i % 3 != 0, Arrangements, i % 3))
    ], 1);

    /// <summary>The score being parsed: every choice and Import are off.</summary>
    public static FakeGpImportScreen Busy() => new([new Track("Lead", true, Arrangements, 0)], isBusy: true);

    /// <summary>A parse that failed: the reason, inside the dialog.</summary>
    public static FakeGpImportScreen Error() => new([new Track("Lead", true, Arrangements, 0)],
        error: "Guitar Pro import failed: the file is not a Guitar Pro 3–7 score.");

    public bool IsAddNewMode
    {
        get => _mode == 0;
        set => SetMode(0, value);
    }

    public bool IsTechniqueOnlyMode
    {
        get => _mode == 1;
        set => SetMode(1, value);
    }

    public bool IsDiffCompareMode
    {
        get => _mode == 2;
        set => SetMode(2, value);
    }

    public bool RequiresOldFile => _mode == 2;

    public string OldFilePath { get; }

    public ICommand PickOldFileCommand { get; }

    public bool RequiresTargetSelection => _mode != 0;

    public bool HasTracks => Tracks.Count > 0;

    public IReadOnlyList<IGpTrackRow> Tracks { get; }

    public bool IsBusy { get; }

    public string ErrorMessage { get; }

    public ICommand ImportCommand { get; }

    public ICommand CancelCommand { get; }

    private void SetMode(int mode, bool on)
    {
        if (!on || _mode == mode) return;
        _mode = mode;
        OnPropertyChanged(nameof(IsAddNewMode));
        OnPropertyChanged(nameof(IsTechniqueOnlyMode));
        OnPropertyChanged(nameof(IsDiffCompareMode));
        OnPropertyChanged(nameof(RequiresOldFile));
        OnPropertyChanged(nameof(RequiresTargetSelection));
    }

    private sealed class Track(string name, bool selected, IReadOnlyList<string> targets, int target)
        : ObservableObject, IGpTrackRow
    {
        private bool _isSelected = selected;
        private int _targetIndex = target;

        public string Name { get; } = name;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public IReadOnlyList<string> Targets { get; } = targets;

        public int TargetIndex
        {
            get => _targetIndex;
            set => SetProperty(ref _targetIndex, value);
        }
    }
}
