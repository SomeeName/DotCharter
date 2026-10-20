using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     The latency / A-V calibration wizard: intro, the judging round trip measured
///     by looping a click back through the input, then two tap steps (a visual beat
///     and an audio beat) whose difference is the display latency, then the result.
///     One step shows at a time and each carries its own footer.
///     <see cref="IDialogScreen{TResult}.Result" /> is the visual lead the player
///     applied, in milliseconds; null when they left without applying one.
/// </summary>
public interface ILatencyWizardScreen : IDialogScreen<double?>
{
    bool IsIntro { get; }

    bool IsJudging { get; }

    bool IsVisualTap { get; }

    bool IsAudioTap { get; }

    bool IsTapStep { get; }

    bool IsResults { get; }

    /// <summary>The open input channels, labelled by jack ("Input 2"); the guitar's is picked first.</summary>
    IReadOnlyList<string> ListeningChannels { get; }

    bool HasListeningChannels { get; }

    /// <summary>Locked while a run measures.</summary>
    int SelectedListeningChannelIndex { get; set; }

    bool IsMeasuringJudging { get; }

    /// <summary>Measure, or Stop while measuring.</summary>
    string JudgingButtonLabel { get; }

    /// <summary>The current offset, what the run is doing, or why it failed.</summary>
    string JudgingStatus { get; }

    bool JudgingMeasured { get; }

    string JudgingResult { get; }

    /// <summary>0..1: the visual beat's flash, peaking on the beat.</summary>
    double VisualPulse { get; }

    /// <summary>"7 / 12 taps   mean +18.2 ms ± 11.0".</summary>
    string TapReadout { get; }

    /// <summary>Enough steady taps to move on.</summary>
    bool CanContinueTaps { get; }

    string ResultsSummary { get; }

    string ResultsDetail { get; }

    /// <summary>The taps were steady enough to act on; Apply shows only then.</summary>
    bool ResultsTrusted { get; }

    ICommand CancelCommand { get; }

    ICommand GoToJudgingCommand { get; }

    /// <summary>Starts the loopback, or stops a run in progress; greyed with no input open, and the status says so.</summary>
    ICommand MeasureJudgingCommand { get; }

    ICommand GoToVisualTapCommand { get; }

    ICommand GoToAudioTapCommand { get; }

    /// <summary>One tap; the view also sends Space and Enter here during a tap step.</summary>
    ICommand TapCommand { get; }

    ICommand RedoTapsCommand { get; }

    ICommand BackCommand { get; }

    ICommand ComputeResultsCommand { get; }

    /// <summary>Writes the visual lead, applies it to the renderers and closes with it.</summary>
    ICommand ApplyResultsCommand { get; }
}
