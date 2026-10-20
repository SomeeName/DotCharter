using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IChartFromAudioScreen" />; Apply answers true, Cancel false.</summary>
public sealed class FakeChartFromAudioScreen : FakeDialog<bool>, IChartFromAudioScreen
{
    private const string FullMixHint = "Analyzing the full mix. Separate the stems first for a better draft.";

    private const string SeparatingHint = "Separating stems… The next analysis uses the stem once it is done.";

    private const string KeepsRunningNote = "Closing this dialog does not stop the separation. It keeps running in the "
                                            + "status bar, and the stems are saved to the cache for this song.";

    private string _audioPath;
    private double _sensitivity = 50;
    private bool _mergeIntoCurrent;
    private bool _replaceExisting;

    private FakeChartFromAudioScreen(string path, bool busy, string status, string summary, FakeStemStrip? stem = null)
    {
        _audioPath = path;
        IsBusy = busy;
        Status = status;
        DraftSummary = summary;
        var strip = stem ?? FakeStemStrip.None;
        StemHint = strip.Hint;
        CanSeparateStems = strip.CanSeparate;
        SeparateStemsBlockedReason = strip.BlockedReason;
        IsSeparating = strip.Separating;
        SeparationProgress = strip.Progress;
        BrowseAudioCommand = new RelayCommand(() => { });
        AnalyzeCommand = busy ? Disabled() : new RelayCommand(() => { });
        ApplyCommand = HasDraft ? Answer(true) : Disabled();
        CancelCommand = Answer(false);
        SeparateStemsCommand = CanSeparateStems ? new RelayCommand(() => { }) : Disabled();
        CancelSeparationCommand = IsSeparating ? new RelayCommand(() => { }) : Disabled();
    }

    public static IReadOnlyList<FakeVariant<IChartFromAudioScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Busy, Busy),
        new(FakeVariantNames.Error, Error),
        new("FullMix", FullMix),
        new("Separating", Separating),
        new("SeparatingReopened", SeparatingReopened),
        new("SeparationBlocked", SeparationBlocked),
        new("Merge", Merge),
        new("MergeReplace", MergeReplace)
    ];

    /// <summary>An analysed draft in hand: Apply is live.</summary>
    public static FakeChartFromAudioScreen Default() =>
        new(@"C:\Charts\Song\guitar-stem.wav", false, "", "143 notes drafted, 71 % average confidence");

    /// <summary>The state it opens in: no file, no draft, and Apply's greyed reason.</summary>
    public static FakeChartFromAudioScreen Empty() => new("", false, "", "");

    public static FakeChartFromAudioScreen LongText() =>
        new(@"D:\Musik\Sehr lange Ordnernamen\Gitarrenspur (isoliert, zweite Aufnahme)\ギター・ステム_最終版.wav",
            false, "", "143 Noten entworfen, durchschnittliche Sicherheit 71 % — 下書きを新しいアレンジメントとして追加します",
            new FakeStemStrip("Analysiert wird der komplette Mix. Trenne zuerst die Stems für einen besseren Entwurf — "
                              + "ミックス全体を解析します。先にステムを分離すると、より良いドラフトになります。", true, "", false, 0));

    public static FakeChartFromAudioScreen Busy() =>
        new(@"C:\Charts\Song\guitar-stem.wav", true, "Analyzing audio…", "");

    /// <summary>The analysis found nothing it would chart: the notice strip says why.</summary>
    public static FakeChartFromAudioScreen Error() =>
        new(@"C:\Charts\Song\silence.wav", false, "No notes were confident enough — raise the sensitivity.", "");

    /// <summary>The song's own mix with no stem separated yet: the strip offers Separate stems.</summary>
    public static FakeChartFromAudioScreen FullMix() =>
        new(@"C:\Charts\Song\song.wav", false, "", "", new FakeStemStrip(FullMixHint, true, "", false, 0));

    /// <summary>A separation started here, 40 % in: progress, Cancel separation, and the note that closing leaves it running.</summary>
    public static FakeChartFromAudioScreen Separating() =>
        new(@"C:\Charts\Song\song.wav", false, "", "", new FakeStemStrip(SeparatingHint, false, "", true, 0.4));

    /// <summary>Reopened while the run it started goes on, 75 % in: the dialog follows that run instead of starting another.</summary>
    public static FakeChartFromAudioScreen SeparatingReopened() =>
        new(@"C:\Charts\Song\song.wav", false, "", "", new FakeStemStrip(SeparatingHint, false, "", true, 0.75));

    /// <summary>No model installed: the button is greyed and says why.</summary>
    public static FakeChartFromAudioScreen SeparationBlocked() =>
        new(@"C:\Charts\Song\song.wav", false, "", "",
            new FakeStemStrip(FullMixHint, false,
                "Stem separation needs a model. Download and activate one under Settings, Stem Models, then run Separate Stems again.",
                false, 0));

    /// <summary>A draft in hand with the merge target chosen: Apply reviews the merge.</summary>
    public static FakeChartFromAudioScreen Merge()
    {
        var screen = Default();
        screen.MergeIntoCurrent = true;
        return screen;
    }

    /// <summary>The merge target with "replace existing notes" ticked.</summary>
    public static FakeChartFromAudioScreen MergeReplace()
    {
        var screen = Merge();
        screen.ReplaceExisting = true;
        return screen;
    }

    public string AudioPath
    {
        get => _audioPath;
        set => SetProperty(ref _audioPath, value);
    }

    public double Sensitivity
    {
        get => _sensitivity;
        set => SetProperty(ref _sensitivity, value);
    }

    public bool IsBusy { get; }

    public string Status { get; }

    public bool HasDraft => DraftSummary.Length > 0;

    public string DraftSummary { get; }

    public bool NothingToApply => !HasDraft && !IsBusy && Status.Length == 0;

    public bool CanMerge => true;

    public string MergeTargetText => "Merge into “Lead”";

    public bool MergeIntoCurrent
    {
        get => _mergeIntoCurrent;
        set
        {
            if (!SetProperty(ref _mergeIntoCurrent, value)) return;
            OnPropertyChanged(nameof(AddAsNewArrangement));
            OnPropertyChanged(nameof(ApplyText));
        }
    }

    public bool AddAsNewArrangement
    {
        get => !MergeIntoCurrent;
        set => MergeIntoCurrent = !value;
    }

    public bool ReplaceExisting
    {
        get => _replaceExisting;
        set => SetProperty(ref _replaceExisting, value);
    }

    public string ApplyText => MergeIntoCurrent ? "Review merge" : "Add draft arrangement";

    public bool HasStemHint => StemHint.Length > 0;

    public string StemHint { get; }

    public bool CanSeparateStems { get; }

    public string SeparateStemsBlockedReason { get; }

    public bool IsSeparating { get; }

    public double SeparationProgress { get; }

    public string SeparationKeepsRunningNote => IsSeparating ? KeepsRunningNote : "";

    public ICommand BrowseAudioCommand { get; }

    public ICommand AnalyzeCommand { get; }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand SeparateStemsCommand { get; }

    public ICommand CancelSeparationCommand { get; }

    private sealed record FakeStemStrip(string Hint, bool CanSeparate, string BlockedReason, bool Separating, double Progress)
    {
        public static FakeStemStrip None { get; } = new("", false, "", false, 0);
    }
}
