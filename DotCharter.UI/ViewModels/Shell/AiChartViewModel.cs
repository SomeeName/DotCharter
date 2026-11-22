// The AI chart verbs of the old Tools ▸ AI rows, kept as palette ids: each starts its job in the AI chat panel.
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Services.Ai.Chart;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     The AI chart pass as palette verbs. The Tools menu has one "AI chat" row; the five fix verbs are
///     jobs in the chat panel, and each verb here shows the panel, picks its job and runs it on the
///     panel's scope through the same pass (<see cref="AiChartPassRun" />), so the proposals are held in
///     <see cref="Review" /> and written as one undo step. The palette calls the methods directly (no view binds
///     them); the panel refuses a second run while one is in flight.
/// </summary>
public sealed class AiChartViewModel : ViewModelBase
{
    private readonly AiChatViewModel _chat;

    public AiChartViewModel(AiChatViewModel chat, AiChartReviewViewModel review)
    {
        _chat = chat;
        Review = review;
    }

    /// <summary>The review card the run's proposals are held in until the charter applies
    /// the accepted sections.</summary>
    public AiChartReviewViewModel Review { get; }

    /// <summary>The chat panel's AI settings door, as the palette's "AI settings" id.</summary>
    public System.Windows.Input.ICommand OpenAiSettingsCommand => _chat.OpenAiSettingsCommand;

    public Task FixFhpAndFingeringAsync() => _chat.StartJobAsync(AiChartJob.FhpAndFingering);

    /// <summary>Anchors from the model; free fingerings follow them deterministically.</summary>
    public Task FixFhpOnlyAsync() => _chat.StartJobAsync(AiChartJob.FhpOnly);

    /// <summary>Fingers from the model; the anchors stay as they are.</summary>
    public Task FixFingeringOnlyAsync() => _chat.StartJobAsync(AiChartJob.FingeringOnly);

    public Task FixChordNamesAsync() => _chat.StartJobAsync(AiChartJob.ChordNames, ChordNamingStyle.Eof);

    public Task FixChordNamesMusicalAsync() => _chat.StartJobAsync(AiChartJob.ChordNames, ChordNamingStyle.Musical);
}
