using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IEstimateBpmScreen" />; Apply answers true, Cancel false.</summary>
public sealed class FakeEstimateBpmScreen : FakeDialog<bool>, IEstimateBpmScreen
{
    private FakeEstimateBpmScreen(string bpm, string half, string twice, bool hasResult, bool marked, string status, bool problem, bool busy = false)
    {
        IsBusy = busy;
        BpmText = bpm;
        HalfText = half;
        DoubleText = twice;
        HasResult = hasResult;
        HasHalf = half.Length > 0;
        HasDouble = twice.Length > 0;
        HasMarkedPart = marked;
        MarkedPartText = marked ? "Between the start and end markers, 12.0 s to 48.5 s" : "Set the start and end markers to listen to a part";
        StatusText = status;
        StatusIsProblem = problem;
        TargetText = hasResult ? "Applied from beat 1 up to the next anchor" : "";
        EstimateCommand = Answer(false);
        ApplyCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IEstimateBpmScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Busy, Busy)
    ];

    /// <summary>An estimate with both alternatives and a marked part.</summary>
    public static FakeEstimateBpmScreen Default() => new("128 BPM", "64 BPM", "256 BPM", true, true, "", false);

    /// <summary>No audio loaded: nothing to estimate.</summary>
    public static FakeEstimateBpmScreen Empty() =>
        new("", "", "", false, false, "Load the song audio first; there is nothing to listen to", true);

    /// <summary>A run in progress.</summary>
    public static FakeEstimateBpmScreen Busy() => new("", "", "", false, true, "Listening…", false, true);

    /// <summary>The longest strings: German and Japanese notes, a tempo with decimals.</summary>
    public static FakeEstimateBpmScreen LongText() =>
        new("1234,56 BPM", "617,28 BPM", "", true, true,
            "Kein gleichmäßiges Tempo gefunden; versuche einen Teil mit klarem Schlagzeug oder Klick / 安定したテンポが見つかりません", true);

    public string SourceText => "a-very-long-song-file-name-for-the-audio-row.ogg";

    public bool IsWholeSong { get; set; } = true;

    public bool IsMarkedPart { get; set; }

    public bool HasMarkedPart { get; }

    public string MarkedPartText { get; }

    public bool IsBusy { get; }

    public string StatusText { get; }

    public bool StatusIsProblem { get; }

    public bool HasResult { get; }

    public string BpmText { get; }

    public string HalfText { get; }

    public string DoubleText { get; }

    public bool HasHalf { get; }

    public bool HasDouble { get; }

    public bool UseEstimate { get; set; } = true;

    public bool UseHalf { get; set; }

    public bool UseDouble { get; set; }

    public string TargetText { get; }

    public ICommand EstimateCommand { get; }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }
}
