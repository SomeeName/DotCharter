using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="ILatencyWizardScreen" /> held on one step; Apply answers a 12.5 ms lead.</summary>
public sealed class FakeLatencyWizardScreen : FakeDialog<double?>, ILatencyWizardScreen
{
    private enum Step { Intro, Judging, VisualTap, AudioTap, Results }

    private readonly Step _step;

    private FakeLatencyWizardScreen(Step step, IReadOnlyList<string> channels, string judgingStatus = "",
        string judgingResult = "", string tapReadout = "", bool canContinue = false, double pulse = 0,
        string resultsSummary = "", string resultsDetail = "", bool trusted = false, bool measuring = false)
    {
        _step = step;
        ListeningChannels = channels;
        SelectedListeningChannelIndex = channels.Count > 1 ? 1 : channels.Count - 1;
        JudgingStatus = judgingStatus;
        JudgingResult = judgingResult;
        IsMeasuringJudging = measuring;
        TapReadout = tapReadout;
        CanContinueTaps = canContinue;
        VisualPulse = pulse;
        ResultsSummary = resultsSummary;
        ResultsDetail = resultsDetail;
        ResultsTrusted = trusted;
        static ICommand Idle() => new RelayCommand(() => { });
        CancelCommand = Answer(null);
        GoToJudgingCommand = Idle();
        MeasureJudgingCommand = channels.Count > 0 ? Idle() : Disabled();
        GoToVisualTapCommand = Idle();
        GoToAudioTapCommand = Idle();
        TapCommand = Idle();
        RedoTapsCommand = Idle();
        BackCommand = Idle();
        ComputeResultsCommand = Idle();
        ApplyResultsCommand = Answer(12.5);
    }

    public static IReadOnlyList<FakeVariant<ILatencyWizardScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("Intro", Intro),
        new("AudioTap", AudioTap)
    ];

    /// <summary>A trusted result: the picture lags, and Apply would add a visual lead.</summary>
    public static FakeLatencyWizardScreen Default() => new(Step.Results, ["Input 1", "Input 2"],
        resultsSummary: "Audio/Video offset: +12.5 ms",
        resultsDetail: "The picture lags the sound by 12.5 ms. Apply adds a 12.5 ms visual lead, so notes reach the line when you hear them.",
        trusted: true);

    /// <summary>The judging step with no input open: no channel picker, and the status says why.</summary>
    public static FakeLatencyWizardScreen Empty() => new(Step.Judging, [],
        judgingStatus: "Audio engine not running - configure an input device first");

    /// <summary>German status text and a failed search across long jack names.</summary>
    public static FakeLatencyWizardScreen LongText() => new(Step.Judging,
        ["Eingang 1 (Instrument, hochohmig)", "Eingang 2 (Mikrofon/Line, symmetrisch)"],
        judgingStatus: "Auf „Eingang 1 (Instrument, hochohmig)“ und „Eingang 2 (Mikrofon/Line, symmetrisch)“ kam kein Klick zurück. " +
                       "Verbinde den Ausgang mit dem Eingang oder halte das Mikrofon an den Lautsprecher und versuche es erneut.");

    /// <summary>A measured judging offset on the second input.</summary>
    public static FakeLatencyWizardScreen Full() => new(Step.Judging, ["Input 1", "Input 2", "Input 3", "Input 4"],
        judgingResult: "Judging offset: 23.4 ms (± 0.8 ms, 7 clicks, Input 2)");

    public static FakeLatencyWizardScreen Intro() => new(Step.Intro, ["Input 1"]);

    /// <summary>The audio beat mid-way: enough taps to go on.</summary>
    public static FakeLatencyWizardScreen AudioTap() => new(Step.AudioTap, ["Input 1"],
        tapReadout: "9 / 12 taps   mean +18.2 ms  ± 11.0 ms", canContinue: true);

    public bool IsIntro => _step == Step.Intro;
    public bool IsJudging => _step == Step.Judging;
    public bool IsVisualTap => _step == Step.VisualTap;
    public bool IsAudioTap => _step == Step.AudioTap;
    public bool IsTapStep => IsVisualTap || IsAudioTap;
    public bool IsResults => _step == Step.Results;
    public IReadOnlyList<string> ListeningChannels { get; }
    public bool HasListeningChannels => ListeningChannels.Count > 0;
    public int SelectedListeningChannelIndex { get; set; }
    public bool IsMeasuringJudging { get; }
    public string JudgingButtonLabel => IsMeasuringJudging ? "Stop" : "Measure";
    public string JudgingStatus { get; }
    public bool JudgingMeasured => JudgingResult.Length > 0;
    public string JudgingResult { get; }
    public double VisualPulse { get; }
    public string TapReadout { get; }
    public bool CanContinueTaps { get; }
    public string ResultsSummary { get; }
    public string ResultsDetail { get; }
    public bool ResultsTrusted { get; }
    public ICommand CancelCommand { get; }
    public ICommand GoToJudgingCommand { get; }
    public ICommand MeasureJudgingCommand { get; }
    public ICommand GoToVisualTapCommand { get; }
    public ICommand GoToAudioTapCommand { get; }
    public ICommand TapCommand { get; }
    public ICommand RedoTapsCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ComputeResultsCommand { get; }
    public ICommand ApplyResultsCommand { get; }
}
