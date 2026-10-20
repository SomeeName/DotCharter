// Tools ▸ Fix & clean up: the AI chart pass verbs — FHP + fingering, FHP only, fingering only,
// chord names in the EOF or the musical style — and the door to Settings ▸ AI. One line each over AiChartPassRun.
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Ai.Chart;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     The AI chart pass, as menu and palette verbs. Each verb is an async command, so its
///     row is disabled while its run is in flight; <see cref="AiChartPassRun" /> refuses a
///     second run of ANY verb meanwhile, reports progress on the status rail, and holds the
///     proposals for <see cref="Review" />, which writes the accepted ones as one undo step.
/// </summary>
public sealed partial class AiChartViewModel : ViewModelBase
{
    private readonly AiChartPassRun _run;

    public AiChartViewModel(AiChartPassRun run, AiChartReviewViewModel review)
    {
        _run = run;
        Review = review;
    }

    /// <summary>The review card the run's proposals are held in until the charter applies
    /// the accepted sections.</summary>
    public AiChartReviewViewModel Review { get; }

    /// <summary>Tools ▸ Fix &amp; clean up ▸ AI settings…: the shell swaps to its Settings tab.</summary>
    public event Action? OpenSettingsRequested;

    [RelayCommand]
    private Task FixFhpAndFingeringAsync() => _run.RunAsync(AiChartJob.FhpAndFingering);

    /// <summary>Anchors from the model; free fingerings follow them deterministically.</summary>
    [RelayCommand]
    private Task FixFhpOnlyAsync() => _run.RunAsync(AiChartJob.FhpOnly);

    /// <summary>Fingers from the model; the anchors stay as they are.</summary>
    [RelayCommand]
    private Task FixFingeringOnlyAsync() => _run.RunAsync(AiChartJob.FingeringOnly);

    [RelayCommand]
    private Task FixChordNamesAsync() => _run.RunAsync(AiChartJob.ChordNames, ChordNamingStyle.Eof);

    [RelayCommand]
    private Task FixChordNamesMusicalAsync() => _run.RunAsync(AiChartJob.ChordNames, ChordNamingStyle.Musical);

    [RelayCommand]
    private void OpenAiSettings() => OpenSettingsRequested?.Invoke();
}
