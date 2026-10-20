using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Ai.Match;

namespace DotCharter.UI.ViewModels.PlayMode.ToneSections;

/// <summary>
///     One suggested section, waiting to be accepted or rejected.
///
///     <para>
///         <b>A proposal is never silently applied.</b> The chart's own tone
///         changes are a charter's decision and the audio's are a guess; both
///         arrive here as an offer with its ORIGIN and its confidence on the row,
///         because a list that had already rewritten the player's sections would
///         make "Propose" a destructive button.
///     </para>
/// </summary>
public sealed class ToneSectionProposalViewModel
{
    /// <summary>Builds a proposal row.</summary>
    /// <param name="proposal">What the proposer answered.</param>
    /// <param name="accept">Turns this proposal into a section row.</param>
    /// <param name="reject">Drops it from the offer list.</param>
    /// <param name="loc">Localization.</param>
    public ToneSectionProposalViewModel(
        ToneSectionProposal proposal,
        Action<ToneSectionProposalViewModel> accept,
        Action<ToneSectionProposalViewModel> reject,
        ILocalizationService loc)
    {
        Proposal = proposal;
        Title = proposal.Section.Label;
        RangeText = loc.Format(
            "ToneSections_Range",
            ToneSectionTime.Format(proposal.Section.From),
            ToneSectionTime.Format(proposal.Section.To));

        // An origin the build does not know prints ITSELF rather than a blank:
        // the vocabulary is additive by contract, and a proposer from a later
        // build must not produce an unlabelled row.
        OriginText = proposal.Origin switch
        {
            ToneSectionOrigins.Chart => loc["ToneSections_Origin_Chart"],
            ToneSectionOrigins.Audio => loc["ToneSections_Origin_Audio"],
            ToneSectionOrigins.Both => loc["ToneSections_Origin_Both"],
            _ => proposal.Origin
        };

        MetaText = loc.Format(
            "ToneSections_ProposalMeta",
            OriginText,
            (int)Math.Round(Math.Clamp(proposal.Confidence, 0, 1) * 100));

        AcceptCommand = new RelayCommand(() => accept(this));
        RejectCommand = new RelayCommand(() => reject(this));
    }

    /// <summary>The contract record this row stands for.</summary>
    public ToneSectionProposal Proposal { get; }

    /// <summary>The proposer's word for the section — it becomes the row's label
    /// and then the tone's name.</summary>
    public string Title { get; }

    /// <summary>"0:10 – 0:30".</summary>
    public string RangeText { get; }

    /// <summary>Where the boundary came from, in the player's language.</summary>
    public string OriginText { get; }

    /// <summary>Origin and confidence on one line.</summary>
    public string MetaText { get; }

    /// <summary>Adds this proposal to the section list.</summary>
    public RelayCommand AcceptCommand { get; }

    /// <summary>Dismisses it.</summary>
    public RelayCommand RejectCommand { get; }
}
