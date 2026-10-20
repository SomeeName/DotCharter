using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     The Tone designer's AI tone creator: the charter describes a tone, the configured AI provider proposes
///     Rocksmith gear for it (the catalogue gate rejects anything the game does not have, by name), and the proposal
///     is shown, auditioned through the designer's preview target, refined and accepted.
///     <see cref="IDialogScreen{TResult}.Result" /> is the key of the tone written (ONE undo step) when closed through
///     Accept, and <c>null</c> when cancelled — then nothing changed.
/// </summary>
public interface IToneCreatorScreen : IDialogScreen<string?>
{
    /// <summary>The charter's words for the tone.</summary>
    string Description { get; set; }

    /// <summary>The longest description the dialog takes.</summary>
    int MaxLength { get; }

    /// <summary>The gear scope "Rocksmith gear only": the result can ship in a package. Always on in the chart's designer.</summary>
    bool IsRocksmithGearOnly { get; set; }

    /// <summary>The gear scope "DotCharter rig". Refused here: <see cref="DotCharterRigReason" /> says why.</summary>
    bool IsDotCharterRig { get; set; }

    bool CanUseDotCharterRig { get; }

    /// <summary>Why the DotCharter-rig scope is greyed: a rig cannot be saved as a Rocksmith tone.</summary>
    string DotCharterRigReason { get; }

    /// <summary>Also measure the song at the playhead (its cached stem); off by default.</summary>
    bool UseSongEvidence { get; set; }

    bool CanUseSongEvidence { get; }

    /// <summary>The span and stem measured, or why there is nothing to measure.</summary>
    string SongEvidenceText { get; }

    ICommand CreateCommand { get; }

    /// <summary>Cancels the call in flight.</summary>
    ICommand StopCommand { get; }

    bool IsBusy { get; }

    /// <summary>The last call's outcome: asking, stopped, refused (naming the gear) or failed. Empty when there is nothing to say.</summary>
    string Status { get; }

    bool HasStatus { get; }

    bool HasProposal { get; }

    /// <summary>The proposed chain in signal order, every knob, and the volume.</summary>
    IReadOnlyList<IToneProposalRow> ProposalRows { get; }

    /// <summary>The model's one-line reason.</summary>
    string Rationale { get; }

    /// <summary>Whether the proposal was measured from a stem or made from the words alone.</summary>
    string EvidenceNote { get; }

    /// <summary>The proposal on the designer's preview target (the DotCharter rig, or Rocksmith through tonelab).</summary>
    bool IsAuditioning { get; set; }

    bool CanAudition { get; }

    string AuditionUnavailableReason { get; }

    /// <summary>What to change ("more gain, less reverb"); Refine sends it with the current proposal.</summary>
    string RefineText { get; set; }

    ICommand RefineCommand { get; }

    /// <summary>
    ///     The audio check: several proposals, their knobs tuned against the song through the clone, the best judged
    ///     through Rocksmith itself when tonelab is ready, then revised with what was heard. Its best becomes the proposal.
    /// </summary>
    ICommand CheckCommand { get; }

    bool CanCheck { get; }

    /// <summary>Why the check is greyed (no span of the song to compare against); empty while it is live.</summary>
    string CheckUnavailableReason { get; }

    bool HasCheckResult { get; }

    /// <summary>Who judged the kept tone and its score: Rocksmith itself, or DotCharter's clone only and why.</summary>
    string CheckJudgeText { get; }

    /// <summary>One line per round: the round's best tone, the clone's score and the game's.</summary>
    IReadOnlyList<string> CheckRounds { get; }

    /// <summary>Accept into the tone being edited: its key and name stay.</summary>
    bool IsReplaceEdited { get; set; }

    /// <summary>Accept as a new tone in a free slot of the arrangement.</summary>
    bool IsSaveAsNew { get; set; }

    bool CanReplaceEdited { get; }

    /// <summary>"Replace “Clean verse”", or that no tone is being edited.</summary>
    string ReplaceEditedText { get; }

    string NewToneName { get; set; }

    /// <summary>The free slot the new tone is declared in, or that the arrangement has none.</summary>
    string NewToneSlotText { get; }

    /// <summary>Also place a tone change naming the tone at the playhead.</summary>
    bool PlaceAtPlayhead { get; set; }

    string PlaceAtPlayheadText { get; }

    /// <summary>Why Accept is greyed; empty while it is live.</summary>
    string AcceptUnavailableReason { get; }

    bool HasAcceptUnavailableReason { get; }

    ICommand AcceptCommand { get; }

    ICommand CancelCommand { get; }
}

/// <summary>One line of a proposal: the slot ("Amplifier", "Pre-pedal 1") and the gear with its knobs.</summary>
public interface IToneProposalRow
{
    string Role { get; }

    string Gear { get; }
}
