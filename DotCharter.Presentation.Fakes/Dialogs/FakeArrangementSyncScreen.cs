using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IArrangementSyncScreen" />; Apply answers true, Cancel false, Swap swaps.</summary>
public sealed class FakeArrangementSyncScreen : FakeDialog<bool>, IArrangementSyncScreen
{
    private int _sourceIndex;
    private int _targetIndex;

    private FakeArrangementSyncScreen(IReadOnlyList<string> arrangements, int source, int target, string preview,
        bool canApply)
    {
        Arrangements = arrangements;
        _sourceIndex = source;
        _targetIndex = target;
        Preview = preview;
        CanApply = canApply;
        ApplyCommand = canApply ? Answer(true) : Disabled();
        CancelCommand = Answer(false);
        SwapCommand = new RelayCommand(() =>
        {
            (_sourceIndex, _targetIndex) = (_targetIndex, _sourceIndex);
            OnPropertyChanged(nameof(SourceIndex));
            OnPropertyChanged(nameof(TargetIndex));
        });
    }

    public static IReadOnlyList<FakeVariant<IArrangementSyncScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    public static FakeArrangementSyncScreen Default() =>
        new(["Lead", "Rhythm", "Bass"], 0, 1,
            "412 shared sounds — 37 would change, plus 12 hand shapes and 8 fret-hand positions.", true);

    /// <summary>A project with one arrangement: there is no pair to sync.</summary>
    public static FakeArrangementSyncScreen Empty() =>
        new(["Lead"], 0, -1, "Pick two different arrangements.", false);

    public static FakeArrangementSyncScreen LongText() =>
        new(["Leadgitarre (alternative Stimmung, zweites Arrangement)", "リズムギター（ドロップD・カポ2）"], 0, 1,
            "412 gemeinsame Klänge — 37 würden sich ändern, dazu 12 Griffbilder und 8 Greifhandpositionen; " +
            "同期すると対象のアレンジメントが上書きされます。", true);

    /// <summary>A project with every arrangement slot used, for the dropdowns.</summary>
    public static FakeArrangementSyncScreen Full() =>
        new(["Lead", "Rhythm", "Bass", "Alt. Lead", "Alt. Rhythm", "Alt. Bass", "Bonus Lead", "Bonus Rhythm"], 3, 4,
            "96 shared sounds — 12 would change, plus 3 hand shapes and 2 fret-hand positions.", true);

    public IReadOnlyList<string> Arrangements { get; }

    public int SourceIndex
    {
        get => _sourceIndex;
        set => SetProperty(ref _sourceIndex, value);
    }

    public int TargetIndex
    {
        get => _targetIndex;
        set => SetProperty(ref _targetIndex, value);
    }

    public string Preview { get; }

    public bool CanApply { get; }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand SwapCommand { get; }
}
