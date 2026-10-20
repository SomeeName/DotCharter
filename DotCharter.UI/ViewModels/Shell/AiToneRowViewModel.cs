// The AI chat's Tones review: the tone half of a dense review-card row — the proposed gear chain (short
// in the row, whole beside the current one in its tooltip) and Audition.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Ai.Chart;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
/// The tone half of a review row: what the proposal would put on the rig and what is there now, in
///     the same words (<see cref="AiToneText.Chain" />: every gear item in signal order with its knobs), and the
///     Audition verb with its reason when it cannot play.
/// </summary>
public sealed partial class AiToneRowViewModel : ObservableObject
{
    private readonly AiChartReviewViewModel _owner;

    internal AiToneRowViewModel(AiChartReviewViewModel owner, AiToneReviewItem item, ILocalizationService loc)
    {
        _owner = owner;
        Item = item;
        ProposedText = AiToneText.Chain(item.Proposed);
        ShortText = AiToneText.Summary(item.Proposed);
        Evidence = AiToneText.Evidence(item, loc);
        CurrentText = item.Current is { } current ? AiToneText.Chain(current) : loc["AiChartReview_ToneNone"];
        var blocker = owner.AuditionBlocker(item);
        CanAudition = blocker is null;
        AuditionTip = blocker ?? loc["AiChartReview_Audition_Tip"];
    }

    /// <summary>The proposal.</summary>
    internal AiToneReviewItem Item { get; }

    /// <summary>"Pedal_Chorus (Rate 4) → Amp_TW22 (Gain 35, Bass 50) → Cab_TW112C_57_Cone".</summary>
    public string ProposedText { get; }

    /// <summary>The proposed chain by gear name only, for the dense row: "TW22 → AT-0112C · 57 Cone".</summary>
    public string ShortText { get; }

    /// <summary>Where the proposal's evidence came from: the stem and confidence, or the charter's words alone.</summary>
    public string Evidence { get; }

    /// <summary>The tone the definition holds now, in the same words, or "No tone body yet".</summary>
    public string CurrentText { get; }

    /// <summary>False when the rig cannot play this proposal (no tone body in the project yet, no rig);
    /// <see cref="AuditionTip" /> then says why.</summary>
    public bool CanAudition { get; }

    /// <summary>What Audition does, or why it cannot.</summary>
    public string AuditionTip { get; }

    /// <summary>Plays the tone's section once with the proposed tone on the rig.</summary>
    [RelayCommand(CanExecute = nameof(CanAudition))]
    private void Audition() => _owner.Audition(this);

    /// <summary>The block's accessible name.</summary>
    public override string ToString() => $"{Item.Name}: {ProposedText}";
}
